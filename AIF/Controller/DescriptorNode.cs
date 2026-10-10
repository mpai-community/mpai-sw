namespace AIF.Controller;

// A node in an AIM hierarchy.
//
// An AIM may have hierarchical structure: a node with no children is a Basic
// AIM; a node with children is a Composite AIM, and its Connections are that
// composite's own Topology. The structure nests to any depth.
//
// Built from the L3 as normalised at import (AIF.Store.TopologyNormaliser):
// there is no name in it, so nothing here can route by one.
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

    // WHERE THIS SUB-AIM RUNS, as the composite that contains it declares
    // (Identifier.Relation in its SubAIMs entry): "Internal" - within the machine
    // provisioned for the Module, as every AIM does today - or "External",
    // "Private", "Public": on its own machine, reached over MPAI-MAS. Empty when
    // the L3 does not say.
    public string Relation { get; set; } = string.Empty;

    public List<DescriptorNode> Children { get; } =
        new();

    // A PACKAGE (M3223 3.3): the AIMs its L3 names with the Relation "Packaged" -
    // inside its one binary. The Controller builds none of them; the package is
    // one AIM to it, and presents them for verification.
    public List<string> Packaged { get; } =
        new();

    // What this composite does when one of its AIMs fails (M3213 3.5):
    // StopModule, the default; StopAIM; or Continue.
    public string OnDegraded { get; set; } =
        "StopModule";

    // The AIM Instance that holds the central control of this composite's Private
    // Storage (M3219 3.2); null where each writer sets the rules of its data.
    public string? StorageControl { get; set; }

    // "Always" where the Controller records the boundary from Start to Stop
    // (M3219 3.4); null where the User Agent decides.
    public string? Record { get; set; }

    // How the Controller executes this composite: Exchange, the default, or
    // Continuous (M3205 Section 5).
    public string Execution { get; set; } =
        "Exchange";

    public bool IsContinuous =>
        Execution == "Continuous";

    // How many times this AIM is started again when it throws (M3215 3.3), and its
    // Period and Deadline in milliseconds (3.5); null where not declared.
    public int     RestartLimit { get; set; }
    public double? Period       { get; set; }
    public double? Deadline     { get; set; }

    public List<TopologyConnection> Connections { get; } =
        new();

    public bool IsComposite =>
        Children.Count > 0;
}
