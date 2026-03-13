using System.Collections.Concurrent;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.PacketLib.Tools;
using Maple2.Server.Core.Constants;
using Maple2.Server.Game.PacketHandlers.Field;
using Maple2.Server.Game.Manager;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Maple2.Database.Storage;
using Maple2.Model.Metadata;

namespace Maple2.Server.Game.PacketHandlers;

public class PetHandler : FieldPacketHandler {
    private sealed class DailyFusionBonusState {
        public DateOnly Day;
        public int UsedCount;
    }

    private const double StrengthenedBattlePetExpScale = 1.5d;
    private const double DailyFusionBonusRate = 0.125d;
    private static readonly ConcurrentDictionary<int, DailyFusionBonusState> DailyFusionBonusByCharacter = new();

    public override RecvOp OpCode => RecvOp.RequestPet;

    private enum Command : byte {
        Summon = 0,
        UnSummon = 1,
        Switch = 3,
        Rename = 4,
        UpdatePotionConfig = 5,
        UpdateLootConfig = 6,
        Fusion = 12, // OnPetEnchant, s_msg_transfer_bind_pet_compose
        Attack = 15, // OnPetAttack, setPetAttackState
        Unknown16 = 16,
        Evolve = 17,
        EvolvePoints = 18,
    }

    public override void Handle(GameSession session, IByteReader packet) {
        var command = packet.Read<Command>();

        switch (command) {
            case Command.Summon:
                HandleSummon(session, packet);
                return;
            case Command.UnSummon:
                HandleUnSummon(session, packet);
                return;
            case Command.Switch:
                HandleSwitch(session, packet);
                return;
            case Command.Rename:
                HandleRename(session, packet);
                return;
            case Command.UpdatePotionConfig:
                HandleUpdatePotionConfig(session, packet);
                return;
            case Command.UpdateLootConfig:
                HandleUpdateLootConfig(session, packet);
                return;
            case Command.Fusion:
                HandleFusion(session, packet);
                return;
            case Command.Attack:
                HandleAttack(session, packet);
                return;
            case Command.Unknown16:
                HandleUnknown16(session, packet);
                return;
            case Command.Evolve:
                HandleEvolve(session, packet);
                return;
            case Command.EvolvePoints:
                HandleEvolvePoints(session, packet);
                return;
        }
    }

    private void HandleSummon(GameSession session, IByteReader packet) {
        long petUid = packet.ReadLong();
        SummonPet(session, petUid);
    }

    private void HandleUnSummon(GameSession session, IByteReader packet) {
        long petUid = packet.ReadLong();
        if (session.Pet?.Pet.Uid != petUid) {
            return;
        }

        session.Pet.Dispose();
    }

    private void HandleSwitch(GameSession session, IByteReader packet) {
        if (session.Pet == null) {
            return;
        }

        long petUid = packet.ReadLong();
        session.Pet.Dispose();
        SummonPet(session, petUid);
    }

    private void HandleRename(GameSession session, IByteReader packet) {
        if (session.Pet == null) {
            return;
        }

        string name = packet.ReadUnicodeString();
        session.Pet.Rename(name);
    }

    private void HandleUpdatePotionConfig(GameSession session, IByteReader packet) {
        if (session.Pet == null) {
            return;
        }

        byte count = packet.ReadByte();

        var config = new PetPotionConfig[count];
        for (int i = 0; i < count; i++) {
            config[i] = packet.Read<PetPotionConfig>();
        }

        session.Pet.UpdatePotionConfig(config);
    }

    private void HandleUpdateLootConfig(GameSession session, IByteReader packet) {
        if (session.Pet == null) {
            return;
        }

        var config = packet.Read<PetLootConfig>();
        session.Pet.UpdateLootConfig(config);
    }

    private void HandleFusion(GameSession session, IByteReader packet) {
        long petUid = packet.ReadLong();
        short count = packet.ReadShort();

        var fodderUids = new List<long>(count);
        for (int i = 0; i < count; i++) {
            fodderUids.Add(packet.ReadLong());
            packet.ReadInt(); // count
        }

        Item? pet = session.Item.Inventory.Get(petUid, InventoryType.Pets);
        if (pet?.Pet == null) {
            session.Send(PetPacket.Error(PetError.s_common_error_unknown));
            return;
        }

        long gainedExp = 0;
        int remainingFusionBonuses = GetRemainingFusionBonuses(session);
        lock (session.Item) {
            foreach (long fodderUid in fodderUids) {
                if (fodderUid == petUid) {
                    continue;
                }

                Item? fodder = session.Item.Inventory.Get(fodderUid, InventoryType.Pets);
                if (fodder?.Pet == null) {
                    continue;
                }

                long materialExp = GetFusionMaterialExp(fodder);
                if (remainingFusionBonuses > 0) {
                    long bonusExp = (long) Math.Floor(materialExp * DailyFusionBonusRate);
                    materialExp += bonusExp;
                    ConsumeFusionBonus(session);
                    remainingFusionBonuses--;
                }

                gainedExp += materialExp;
                if (session.Item.Inventory.Remove(fodderUid, out Item? removed)) {
                    session.Item.Inventory.Discard(removed, commit: true);
                }
            }
        }

        if (gainedExp <= 0) {
            session.Send(PetPacket.Error(PetError.s_common_error_unknown));
            return;
        }

        pet.Pet.Exp += gainedExp;

        bool leveled = false;
        while (pet.Pet.Level < Constant.PetMaxLevel) {
            long requiredExp = GetRequiredPetExp(pet.Pet.Level, pet);
            if (pet.Pet.Exp < requiredExp) {
                break;
            }

            pet.Pet.Exp -= requiredExp;
            pet.Pet.Level++;
            leveled = true;
        }

        using (GameStorage.Request db = session.GameStorage.Context()) {
            db.UpdateItem(pet);
        }

        session.Send(ItemInventoryPacket.UpdateItem(pet));
        session.Send(PetPacket.PetInfo(session.Player.ObjectId, pet));
        FieldPet? summonedPet = GetSummonedFieldPet(session, pet.Uid);
        if (summonedPet != null) {
            session.Send(PetPacket.Fusion(summonedPet));
            if (leveled) {
                session.Send(PetPacket.LevelUp(summonedPet));
            }
        } else {
            session.Send(PetPacket.Fusion(session.Player.ObjectId, pet));
            if (leveled) {
                session.Send(PetPacket.LevelUp(session.Player.ObjectId, pet));
            }
            session.Send(PetPacket.FusionCount(GetRemainingFusionBonuses(session)));
        }
    }

    private static FieldPet? GetSummonedFieldPet(GameSession session, long petUid) {
        if (session.Field == null) {
            return null;
        }

        foreach (FieldPet fieldPet in session.Field.Pets.Values) {
            if (fieldPet.OwnerId == session.Player.ObjectId && fieldPet.Pet.Uid == petUid) {
                return fieldPet;
            }
        }

        return null;
    }

    private void HandleAttack(GameSession session, IByteReader packet) {
        packet.ReadBool();
    }

    private void HandleUnknown16(GameSession session, IByteReader packet) {
        packet.ReadLong();
        packet.ReadLong();
    }

    private void HandleEvolve(GameSession session, IByteReader packet) {
        long petUid = packet.ReadLong();

        Item? pet = session.Item.Inventory.Get(petUid, InventoryType.Pets);
        if (pet?.Pet == null) {
            session.Send(PetPacket.Error(PetError.s_common_error_unknown));
            return;
        }

        int requiredPoints = GetRequiredEvolvePoints(pet.Rarity);
        if (pet.Pet.EvolvePoints < requiredPoints) {
            session.Send(PetPacket.Error(PetError.s_common_error_unknown));
            return;
        }

        pet.Pet.EvolvePoints -= requiredPoints;
        using (GameStorage.Request db = session.GameStorage.Context()) {
            db.UpdateItem(pet);
        }
        session.Send(ItemInventoryPacket.UpdateItem(pet));
        session.Send(PetPacket.EvolvePoints(session.Player.ObjectId, pet));
    }

    private void HandleEvolvePoints(GameSession session, IByteReader packet) {
        long petUid = packet.ReadLong();
        short count = packet.ReadShort();

        var fodderUids = new List<long>(count);
        for (int i = 0; i < count; i++) {
            fodderUids.Add(packet.ReadLong());
        }

        Item? pet = session.Item.Inventory.Get(petUid, InventoryType.Pets);
        if (pet?.Pet == null) {
            session.Send(PetPacket.Error(PetError.s_common_error_unknown));
            return;
        }

        int gainedPoints = 0;
        lock (session.Item) {
            foreach (long fodderUid in fodderUids) {
                if (fodderUid == petUid) {
                    continue;
                }

                Item? fodder = session.Item.Inventory.Get(fodderUid, InventoryType.Pets);
                if (fodder?.Pet == null) {
                    continue;
                }

                gainedPoints += Math.Max(1, fodder.Rarity);
                if (session.Item.Inventory.Remove(fodderUid, out Item? removed)) {
                    session.Item.Inventory.Discard(removed, commit: true);
                }
            }
        }

        if (gainedPoints <= 0) {
            session.Send(PetPacket.Error(PetError.s_common_error_unknown));
            return;
        }

        pet.Pet.EvolvePoints += gainedPoints;
        using (GameStorage.Request db = session.GameStorage.Context()) {
            db.UpdateItem(pet);
        }
        session.Send(ItemInventoryPacket.UpdateItem(pet));
        session.Send(PetPacket.EvolvePoints(session.Player.ObjectId, pet));
    }

    private static long GetRequiredPetExp(Item pet) {
        int levelRequirement = pet.Metadata.Limit.Level;
        int rarity = pet.Rarity;

        long baseExp = (rarity, levelRequirement) switch {
            (1, 50) => 22000L,
            (2, 50) => 55000L,
            (3, 50) => 90000L,
            (3, 60) => 135000L,
            (4, 50) => 90000L,
            (4, 60) => 135000L,
            (_, >= 60) => 135000L,
            _ => rarity switch {
                <= 1 => 22000L,
                2 => 55000L,
                3 => 90000L,
                _ => 90000L,
            }
        };

        return (long) Math.Round(baseExp * GetBattlePetExpScale(pet));
    }

    private static long GetFusionBaseExp(Item pet) {
        int levelRequirement = pet.Metadata.Limit.Level;
        int rarity = pet.Rarity;

        long baseExp = (rarity, levelRequirement) switch {
            (1, 50) => 1500L,
            (2, 50) => 3000L,
            (3, 50) => 12000L,
            (3, 60) => 18000L,
            (4, 50) => 24000L,
            (4, 60) => 36000L,
            (_, >= 60) => 18000L,
            _ => rarity switch {
                <= 1 => 1500L,
                2 => 3000L,
                3 => 12000L,
                _ => 24000L,
            }
        };

        return (long) Math.Round(baseExp * GetBattlePetExpScale(pet));
    }

    private static long GetFusionMaterialExp(Item pet) {
        if (pet.Pet == null) {
            return 0L;
        }

        long perLevelExp = GetRequiredPetExp(pet);
        long investedExp = (Math.Max(1, (int) pet.Pet.Level) - 1L) * perLevelExp + Math.Max(0L, pet.Pet.Exp);
        long materialExp = GetFusionBaseExp(pet) + (long) Math.Floor(investedExp * 0.8d);
        return Math.Max(0L, materialExp);
    }

    private static long GetRequiredPetExp(short level, Item pet) {
        return GetRequiredPetExp(pet);
    }

    private static int GetRequiredEvolvePoints(int rarity) {
        int safeRarity = Math.Max(1, rarity);
        return safeRarity * 10;
    }

    private static double GetBattlePetExpScale(Item pet) {
        if (pet.Id >= 61100000 || pet.Metadata.Limit.Level >= 60) {
            return StrengthenedBattlePetExpScale;
        }

        return 1d;
    }

    private static int GetRemainingFusionBonuses(GameSession session) {
        int characterId = (int) session.CharacterId;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        DailyFusionBonusState state = DailyFusionBonusByCharacter.AddOrUpdate(characterId,
            _ => new DailyFusionBonusState { Day = today, UsedCount = 0 },
            (_, existing) => {
                if (existing.Day != today) {
                    existing.Day = today;
                    existing.UsedCount = 0;
                }

                return existing;
            });

        return Math.Max(0, Constant.DailyPetEnchantMaxCount - state.UsedCount);
    }

    private static void ConsumeFusionBonus(GameSession session) {
        int characterId = (int) session.CharacterId;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        DailyFusionBonusByCharacter.AddOrUpdate(characterId,
            _ => new DailyFusionBonusState { Day = today, UsedCount = 1 },
            (_, existing) => {
                if (existing.Day != today) {
                    existing.Day = today;
                    existing.UsedCount = 0;
                }

                existing.UsedCount = Math.Min(Constant.DailyPetEnchantMaxCount, existing.UsedCount + 1);
                return existing;
            });
    }

    private static void SummonPet(GameSession session, long petUid) {
        if (session.Field == null || session.Pet != null) {
            return;
        }

        lock (session.Item) {
            Item? pet = session.Item.Inventory.Get(petUid, InventoryType.Pets);
            if (pet == null) {
                return;
            }

            FieldPet? fieldPet = session.Field.SpawnPet(pet, session.Player.Position, session.Player.Rotation, player: session.Player);
            if (fieldPet == null) {
                return;
            }

            session.Field.Broadcast(FieldPacket.AddPet(fieldPet));
            session.Field.Broadcast(ProxyObjectPacket.AddPet(fieldPet));
            session.Pet = new PetManager(session, fieldPet);
            session.Pet.Load();
            session.Stats.Refresh();
        }
    }
}
