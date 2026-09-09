namespace AIF.Controller;

// The hierarchy of a registered AIM. The Root may contain composite children,
// each with their own children and connections.
public sealed class DescriptorGraph
{
    public DescriptorNode Root { get; init; }
        = new();

    // The Topology of the outermost AIM.
    public List<TopologyConnection> Connections =>
        Root.Connections;
}
