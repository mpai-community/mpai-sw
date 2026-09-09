namespace AIF.Controller;

// A node in an AIM hierarchy.
//
// An AIM may have hierarchical structure: a node with no children is a Basic
// AIM; a node with children is a Composite AIM, and its Connections are that
// composite's own Topology. The structure nests to any depth.
//
// InternalTypes maps an InternalType name (as used in Topology PortName fields)
// to its DataType identifier (e.g. "OSD-AUO-V1.5"). This lets the executor
// route data between AIMs by DataType rather than by port name, so each AIM
// can use its own port names without the Controller needing to know them.
public sealed class DescriptorNode
{
    public string AIMName { get; init; } =
        string.Empty;

    public string ImplementerID { get; init; } =
        string.Empty;

    public string ImplementationID { get; init; } =
        string.Empty;

    public List<RuntimePort> Ports { get; } =
        new();

    // InternalType name -> DataType identifier.
    // Populated from the "InternalTypes" array in the composite's AMD.
    public Dictionary<string, string> InternalTypes { get; } =
        new();

    public List<DescriptorNode> Children { get; } =
        new();

    public List<TopologyConnection> Connections { get; } =
        new();

    public bool IsComposite =>
        Children.Count > 0;

    // Resolve an InternalType name to a DataType.
    // Returns null if the name is not found in InternalTypes.
    public string? ResolveInternalType(string internalTypeName)
    {
        return InternalTypes.TryGetValue(internalTypeName, out var dataType)
            ? dataType
            : null;
    }
}
