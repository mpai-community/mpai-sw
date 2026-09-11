namespace AIF.Controller;

// A TYPED endpoint of a Topology connection.
//
//   AimName    - the sub-AIM's canonical acronym, or null/empty for the
//                composite's own boundary.
//   DataType   - the canonical Data Type acronym the port carries.
//   PortNumber - disambiguates when that Data Type occurs more than once on the
//                same AIM/boundary; 1 when a type occurs once.
//
// There is NO port name here. A human name lives only in ExternalPorts /
// InternalTypes as a label; the loader resolves it to (DataType, PortNumber)
// once and this typed endpoint is all the running software ever sees. Nothing
// downstream addresses a port by its name.
public readonly record struct Endpoint(string? AimName, string DataType, int PortNumber = 1)
{
    public bool IsBoundary => string.IsNullOrEmpty(AimName);

    // The routing key for a boundary datum: DataType + PortNumber. Ports of the
    // same type are told apart only by number; a single occurrence is #1.
    public string Key => DataType + "#" + PortNumber;
}

// One Topology edge. "Output" is the producing side, "Input" the receiving side
// (the vocabulary of the AMD). Both are typed endpoints - no strings, no names.
public sealed class TopologyConnection
{
    public Endpoint Output { get; init; }
    public Endpoint Input  { get; init; }
}
