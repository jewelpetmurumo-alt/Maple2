using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Maple2.Model.Metadata;

public record ConstantsTable(IReadOnlyDictionary<string, Constants> Constants) : ServerTable;

public record Constants (
    string Key,
    string Value
    );
