using System.Collections.Concurrent;
using Maple2.Database.Extensions;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Game.Dungeon;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Packets;

namespace Maple2.Server.Game.Manager.Field;

public class DungeonFieldManager : FieldManager {
    public readonly DungeonRoomMetadata DungeonMetadata;
    public DungeonFieldManager? Lobby { get; init; }
    public readonly ConcurrentDictionary<int, DungeonFieldManager> RoomFields = [];
    public required DungeonRoomRecord DungeonRoomRecord { get; init; }
    public int PartyId { get; init; }

    public DungeonFieldManager(DungeonRoomMetadata dungeonMetadata, MapMetadata mapMetadata, UgcMapMetadata ugcMetadata, MapEntityMetadata entities, NpcMetadataStorage npcMetadata, long ownerId = 0, int size = 1, int partyId = 0)
        : base(mapMetadata, ugcMetadata, entities, npcMetadata, ownerId) {
        DungeonMetadata = dungeonMetadata;
        if (dungeonMetadata.LobbyFieldId == mapMetadata.Id) {
            Lobby = this;
        }
        DungeonId = dungeonMetadata.Id;
        Size = size;
        PartyId = partyId;
    }

    public void ChangeState(DungeonState state) {
        DungeonRoomRecord.State = state;
        DungeonRoomRecord.EndTick = FieldTick;

        var compiledResults = new Dictionary<long, DungeonUserResult>();

        // Party meter should compare the same metric across all party members.
        // Use TotalDamage for everyone instead of assigning each character a different “best” category.
        foreach ((long characterId, DungeonUserRecord userRecord) in DungeonRoomRecord.UserResults) {
            if (!TryGetPlayerById(characterId, out FieldPlayer? _)) {
                continue;
            }

            userRecord.AccumulationRecords.TryGetValue(DungeonAccumulationRecordType.TotalDamage, out int totalDamage);
            compiledResults[characterId] = new DungeonUserResult(characterId, DungeonAccumulationRecordType.TotalDamage, totalDamage);
        }

        Broadcast(DungeonRoomPacket.DungeonResult(DungeonRoomRecord.State, compiledResults));

        if (DungeonRoomRecord.State == DungeonState.Clear) {
            long clearTimestamp = DateTime.Now.ToEpochSeconds();
            foreach (long characterId in compiledResults.Keys) {
                if (!TryGetPlayerById(characterId, out FieldPlayer? player)) {
                    continue;
                }

                player.Session.Dungeon.CompleteDungeon(clearTimestamp);

            }
        }
    }
}
