using System;
using System.Collections.Generic;
using System.Linq;
namespace AIF.Controller;

// Runtime representation of an AIM ExternalPort.
public sealed class RuntimePort
{
    public string Name { get; init; } =
        string.Empty;

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

    // 1-based ordinal among this AIM's ports of the SAME Direction and
    // DataType, as declared in the AMD. Null when the AMD omitted it, which
    // per AIMMetadata V3.0 means 1.
    //
    // Routing is by DataType. This is the only tie-breaker when one AIM
    // declares two ports of the same Direction and DataType - port NAMES are
    // advisory and cannot be used for it.
    public int? PortNumber { get; init; }

    // Composite boundary INPUT ports only.
    //
    // False (the default) is the AMQ behaviour: if the User Agent has not
    // supplied this port, the run SUSPENDS and asks for it.
    //
    // True means the input may legitimately never arrive: the AIMs fed solely
    // by it are SKIPPED and the run continues. That is what a workflow with
    // alternative inputs needs - speech OR text - where suspending on the
    // unused one would hang forever, since nobody is ever going to supply it.
    //
    // Declared in the AMD as "IsOptional": true. Absent means false, so no
    // existing AMD changes behaviour.
    public bool IsOptional { get; init; }
}