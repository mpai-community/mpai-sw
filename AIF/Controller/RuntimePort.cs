using System;
using System.Collections.Generic;
using System.Linq;
namespace AIF.Controller;

// Runtime representation of an AIM ExternalPort.
public sealed class RuntimePort
{
    public string Direction { get; init; } =
        string.Empty;

    // The Data Type this Port carries. When a Port accepts more than one - a
    // Port taking either a Basic or a full Audio Object - this is the FIRST of
    // the set, kept so that everything reading a single Data Type still reads
    // something sensible. Matching should use Accepts.
    public string DataType { get; init; } =
        string.Empty;

    // Every Data Type this Port accepts. A single-typed Port has one entry, so
    // there is no separate case to handle: the set is always the truth and
    // DataType is a convenience over it.
    public IReadOnlyList<string> DataTypes { get; init; } =
        Array.Empty<string>();

    // Does this Port accept values of that Data Type?
    //
    // This is the rule the AIM Metadata states: a Controller routes a value to a
    // Port whose Data Type set CONTAINS the value's Data Type. Equality was the
    // rule while a Port could carry only one.
    public bool Accepts(string dataType) =>
        DataTypes.Count > 0
            ? DataTypes.Contains(dataType)
            : DataType == dataType;

    public string Technology { get; init; } =
        string.Empty;

    public string Protocol { get; init; } =
        string.Empty;

    public bool IsRemote { get; init; }

    // M3205 3.6.2, 3.6.3 (Phase 2 fields, acted upon from Phase 4). On an Input
    // Port: Depth, Overflow, MaxAge (ms), the transports it accepts; on an Output
    // Port: the transport of the Channel it writes. Null where not declared.
    public int?    Depth    { get; init; }
    public string? Overflow { get; init; }
    public double? MaxAge   { get; init; }
    public string? Transport { get; init; }
    public IReadOnlyList<string>? AcceptedTransports { get; init; }

    // 1-based ordinal among this AIM's ports of the SAME Direction and
    // DataType, as declared in the AMD. Null when the AMD omitted it, which
    // per AIMMetadata V3.0 means 1.
    //
    // Routing is by DataType. This is the only tie-breaker when one AIM
    // declares two ports of the same Direction and DataType - port NAMES are
    // advisory and cannot be used for it.
    public int? PortNumber { get; init; }

    // An input the AIM can do without. In an exchange an AIM runs on what it has
    // received and the flag is documentation; in continuous execution it decides
    // whether an AIM waits for the input (M3205 5.3). Nothing waits for a User
    // Agent: an AIM fed by nothing at all is skipped (M3205 5.1).
    //
    // Declared in the AMD as "IsOptional": true. Absent means false.
    public bool IsOptional { get; init; }

    // M3194 NUMBER 4 - INPUT. Declared by a composite on its own Input
    // ExternalPorts: every Port of one Data Type meant to receive ONE shared
    // external supply carries the same "Input" value: e.g. two Text Ports
    // (PortNumber 1 to Text-To-Speech, 2 to Generative Face Description) both
    // declaring Input 1. Null when the AMD states none. Independent of PortNumber:
    // PortNumber says which internal recipient a Port is; Input says which
    // external supply feeds it.
    public int? InputGroup { get; init; }

    // M3194 NUMBER 3 - OUTPUT, declared on a boundary INPUT flow. A composite
    // states it on the ExternalPort through which a datum enters it, when that
    // datum is sent on to a child composite's Input group of the same number.
    // Null when the AMD states none.
    public int? OutputGroup { get; init; }
}