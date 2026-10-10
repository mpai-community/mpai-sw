using System.Text.Json;
using AIF.SharedStorage;
using AIF.Store;

namespace AIF.Controller;

public sealed class Controller
{
    private readonly AmdStore store;

    // WHERE SHARED STORAGE LIVES, AND NOTHING ABOUT WHAT GOES IN IT. Supplied by
    // the User Agent through MPAI_AIFU_SharedStorage_Init; null when no scope is
    // configured, in which case AIMs are handed no storage at all.
    private string? storageRoot;

    public void SetSharedStorageRoot(string? root) => storageRoot = root;

    // THE HANDLE AN AIM IS GIVEN IS STAMPED WITH WHO IT IS. Accountability is the
    // point of the provenance record: if data is written it must be possible to
    // know who wrote it. A handle an AIM or its provider constructed would carry
    // whatever identity they chose, which proves nothing. The Module names the
    // context and the AIM names the writer within it, because an AIM name without
    // its Module identifies nothing.
    //
    // WHERE, THE MODULE SAYS. A Module's scope is where the User Agent initialised
    // it (MPAI_AIFU_SharedStorage_Init with its MODULE_ID), else the Controller's
    // root; the handle asks at each call, so an Init after Start takes effect.
    //
    // UNDER RULES (M3219 3.3), where the User Agent says which instance of a
    // Module and which session a handle serves: the Shared Storage at the
    // location, shared by the Modules given it, each datum under its rules.
    private ISharedStorage? StorageFor(string moduleName, string aimName, Func<string?>? location) =>
        location is not null && InstanceOf is { } instance && SessionOf is { } session
            ? new SharedStorageHandle(() => location() ?? storageRoot, new StorageHolder(moduleName, aimName),
                                      () => instance(moduleName), () => session(moduleName), "local", Now ?? (() => DateTimeOffset.UtcNow))
            : location is not null
            ? new ModuleSharedStorage(() => location() ?? storageRoot, $"{moduleName}/{aimName}", "local")
            : storageRoot is null
                ? null
                : new FileSharedStorage(storageRoot, $"{moduleName}/{aimName}", "local");

    // The instance of a Module that runs now, the session, and the time base: set
    // by the User Agent, for the handles under rules.
    public Func<string, string>? InstanceOf { get; set; }
    // The session a Module's exchange serves, by Module: one User Agent's own, or -
    // a Service's Controller serving many clients - the client's (M3245 3.3).
    public Func<string, string>? SessionOf { get; set; }
    public Func<DateTimeOffset>? Now { get; set; }

    // PRIVATE STORAGE (M3215 3.7): reachable only by its AIM Instance, held below
    // its Module's scope at private/<AIM Instance>, stamped as Shared Storage is.
    // The AIM is given no handle to any other AIM's.
    private ISharedStorage? PrivateStorageFor(string moduleName, string aimName, Func<string?>? location)
    {
        string? Scope() => (location?.Invoke() ?? storageRoot) is { } root
            ? Path.Combine(root, "private", Uri.EscapeDataString(aimName))
            : null;
        return Scope() is null ? null : new ModuleSharedStorage(Scope, $"{moduleName}/{aimName}", "local");
    }

    public Controller(AmdStore store)
    {
        this.store = store;
    }

    public DescriptorGraph RegisterAim(Identifier identifier)
    {
        return new DescriptorGraph
        {
            Root = BuildNode(identifier, new HashSet<Identifier>())
        };
    }

    private DescriptorNode BuildNode(
        Identifier identifier,
        ISet<Identifier> expanding)
    {
        // Look up by AIMName only - ImplementerID and ImplementationID may be
        // placeholder strings in SubAIM references that differ from the actual
        // AMD file's Identifier. AIMName is always the stable, canonical key.
        var resolved = store.FindByAimName(identifier.AIMName);
        if (resolved is null)
        {
            return new DescriptorNode
            {
                AIMName          = identifier.AIMName,
                ImplementerID    = identifier.ImplementerID,
                ImplementationID = identifier.ImplementationID
            };
        }
        identifier = resolved;

        if (!store.Exists(identifier))
        {
            return new DescriptorNode
            {
                AIMName          = identifier.AIMName,
                ImplementerID    = identifier.ImplementerID,
                ImplementationID = identifier.ImplementationID
            };
        }

        if (!expanding.Add(identifier))
        {
            throw new InvalidOperationException(
                $"{identifier} contains itself; the AIM hierarchy is not finite.");
        }

        var root           = store.GetAMD(identifier).RootElement;
        var identifierJson = root.GetProperty("Identifier");

        var node = new DescriptorNode
        {
            AIMName          = identifierJson.GetProperty("AIMName").GetString()          ?? string.Empty,
            ImplementerID    = identifierJson.GetProperty("ImplementerID").GetString()    ?? string.Empty,
            ImplementationID = identifierJson.GetProperty("ImplementationID").GetString() ?? string.Empty
        };

        // ExternalPorts
        if (root.TryGetProperty("ExternalPorts", out var externalPorts))
        {
            foreach (var port in externalPorts.EnumerateArray())
            {
                var declared = DataTypesOf(port);

                node.Ports.Add(new RuntimePort
                {
                    Direction = port.GetProperty("Direction").GetString()  ?? string.Empty,
                    DataType  = declared.Count > 0 ? declared[0] : string.Empty,
                    DataTypes = declared,
                    Technology= port.GetProperty("Technology").GetString() ?? string.Empty,
                    Protocol  = port.GetProperty("Protocol").GetString()   ?? string.Empty,
                    IsRemote  = port.GetProperty("IsRemote").GetBoolean(),

                    // Optional in the AMD; omitted means 1.
                    PortNumber =
                        port.TryGetProperty("PortNumber", out var declaredOrdinal) &&
                        declaredOrdinal.TryGetInt32(out var portOrdinal)
                            ? portOrdinal
                            : null,

                    // Omitted means false.
                    IsOptional =
                        port.TryGetProperty("IsOptional", out var optional) &&
                        optional.ValueKind == JsonValueKind.True,

                    // M3194 Number 4 (Input) and Number 3 (Output). Both optional
                    // in the AMD; absent means the Port takes part in neither.
                    InputGroup  = IntOf(port, "Input"),
                    OutputGroup = IntOf(port, "Output"),

                    Depth     = IntOf(port, "Depth"),
                    Overflow  = TextOf(port, "Overflow"),
                    MaxAge    = NumberOf(port, "MaxAge"),
                    Transport = TextOf(port, "Transport"),
                    AcceptedTransports =
                        port.TryGetProperty("AcceptedTransports", out var accepted) && accepted.ValueKind == JsonValueKind.Array
                            ? accepted.EnumerateArray().Select(a => a.GetString() ?? "").ToList()
                            : null
                });
            }
        }

        // A Port's DataType is a string, or an ARRAY of strings when the Port
        // accepts more than one - a Port taking either a Basic or a full Audio
        // Object declares both. Reading it with GetString() throws on the array,
        // so every reader of an AMD has to go through here.
        static IReadOnlyList<string> DataTypesOf(JsonElement port)
        {
            if (!port.TryGetProperty("DataType", out var dt))
                return Array.Empty<string>();

            if (dt.ValueKind == JsonValueKind.String)
            {
                var one = dt.GetString();
                return string.IsNullOrWhiteSpace(one) ? Array.Empty<string>() : new[] { one };
            }

            if (dt.ValueKind == JsonValueKind.Array)
                return dt.EnumerateArray()
                         .Select(e => e.GetString())
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .Select(s => s!)
                         .ToArray();

            return Array.Empty<string>();
        }

        static int? IntOf(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var n)
                ? n
                : null;

        if (root.TryGetProperty("OnDegraded", out var onDegraded) && onDegraded.ValueKind == JsonValueKind.String)
            node.OnDegraded = onDegraded.GetString() ?? node.OnDegraded;
        node.Execution      = TextOf(root, "Execution") ?? node.Execution;
        node.StorageControl = TextOf(root, "StorageControl");
        node.Record         = TextOf(root, "Record");
        node.RestartLimit = IntOf(root, "RestartLimit") ?? 0;
        node.Period       = NumberOf(root, "Period");
        node.Deadline     = NumberOf(root, "Deadline");

        static string? TextOf(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static double? NumberOf(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;

        // SubAIMs
        if (root.TryGetProperty("SubAIMs", out var subAims))
        {
            foreach (var subAim in subAims.EnumerateArray())
            {
                var subId = new Identifier
                {
                    AIMName          = subAim.GetProperty("Identifier").GetProperty("AIMName").GetString()          ?? string.Empty,
                    ImplementerID    = subAim.GetProperty("Identifier").GetProperty("ImplementerID").GetString()    ?? string.Empty,
                    ImplementationID = subAim.GetProperty("Identifier").GetProperty("ImplementationID").GetString() ?? string.Empty
                };
                if (string.IsNullOrWhiteSpace(subId.AIMName)) continue;

                var subRelation =
                    subAim.GetProperty("Identifier").TryGetProperty("Relation", out var relation)
                        ? relation.GetString() ?? string.Empty
                        : string.Empty;

                // An AIM inside a package is not built: the package runs it.
                if (subRelation == "Packaged") { node.Packaged.Add(subId.AIMName); continue; }

                var child = BuildNode(subId, expanding);
                child.Relation = subRelation;
                node.Children.Add(child);
            }
            if (node.Packaged.Count > 0 && node.Children.Count > 0)
                throw new InvalidOperationException(
                    $"{identifier.AIMName}: its SubAIMs are Packaged and built; a package contains all its AIMs, or none.");
        }

        // Topology
        //   "Output" = the PRODUCING side (data leaves that AIM)
        //   "Input"  = the RECEIVING side (data enters that AIM)
        // Each side is an AIM and a Data Type (with a Port Number): the names of
        // the L3 were resolved to Data Types when it was imported
        // (AIF.Store.TopologyNormaliser), and the Controller never sees one.
        if (node.Packaged.Count == 0 && root.TryGetProperty("Topology", out var topology))
        {
            foreach (var connection in topology.EnumerateArray())
                node.Connections.AddRange(ResolveConnection(node, connection));
        }

        expanding.Remove(identifier);
        return node;
    }

    // ONE TOPOLOGY LINE BECOMES ONE OR MORE TYPED CONNECTIONS.
    //
    // Each end is a Data Type, as normalised at import: a boundary end is the
    // composite's ExternalPort of that Data Type and Port Number; a Sub-AIM end
    // is found by Data Type, Direction and Port Number alone.
    //
    // M3194 OUTPUT / INPUT. Where the receiving Sub-AIM is a composite that
    // declares Input groups for that Data Type, the flow must state an Output
    // number (carried on the line's producing end, or on the boundary
    // ExternalPort it enters through), and the line is expanded into one connection per Port of the
    // matching group. The parent never learns how many Ports the group has.
    private static IEnumerable<TopologyConnection> ResolveConnection(
        DescriptorNode node,
        JsonElement    connection)
    {
        var from = Side.Read(connection.GetProperty("Output"));
        var to   = Side.Read(connection.GetProperty("Input"));

        // A boundary end on the producing side is where data ENTERS the
        // composite, i.e. one of its Input ExternalPorts; on the receiving side
        // it is where data LEAVES, i.e. an Output ExternalPort.
        var fromDecl = Declaration(node, from, boundaryDirection: "Input");
        var toDecl   = Declaration(node, to,   boundaryDirection: "Output");

        var dataType = fromDecl.DataType;

        if (!fromDecl.Accepts(toDecl.DataType) && !toDecl.Accepts(fromDecl.DataType))
            throw new InvalidOperationException(
                $"{node.AIMName}: Topology '{from}' -> '{to}' joins {fromDecl.DataType} to {toDecl.DataType}.");

        if (fromDecl.Output is int a && toDecl.Output is int b && a != b)
            throw new InvalidOperationException(
                $"{node.AIMName}: Topology '{from}' -> '{to}' is declared with Output {a} at one end " +
                $"and Output {b} at the other.");

        var output = fromDecl.Output ?? toDecl.Output;

        var fromEndpoint = EndpointOf(node, from, fromDecl, dataType, "Output");
        var toEndpoint   = EndpointOf(node, to,   toDecl,   dataType, "Input");

        if (to.IsBoundary)
            return new[] { new TopologyConnection { Output = fromEndpoint, Input = toEndpoint } };

        var child  = node.Children.First(c => c.AIMName == to.Aim);
        var groups = child.Ports
            .Where(p => p.Direction == "Input" && p.Accepts(dataType) && p.InputGroup is not null)
            .ToList();

        // The receiver declares no Input group for this Data Type: an ordinary
        // connection. An Output number on the flow does not concern this edge.
        if (groups.Count == 0)
        {
            if (output is not null)
                Console.WriteLine(
                    $"[AIF] {node.AIMName}: flow '{from}' declares Output {output}, but {child.AIMName} " +
                    $"declares no Input group for {dataType}; not used on this edge.");

            return new[] { new TopologyConnection { Output = fromEndpoint, Input = toEndpoint } };
        }

        if (output is null)
            throw new InvalidOperationException(
                $"{node.AIMName}: {child.AIMName} declares Input groups for {dataType}, and flow " +
                $"'{from}' into it states no Output. Declare \"Output\" on the flow (its InternalType, " +
                "or the ExternalPort it enters through).");

        var members = groups.Where(p => p.InputGroup == output).ToList();
        if (members.Count == 0)
            throw new InvalidOperationException(
                $"{node.AIMName}: flow '{from}' declares Output {output}; {child.AIMName} declares no " +
                $"Input {output} for {dataType} (it declares " +
                $"{string.Join(", ", groups.Select(g => g.InputGroup).Distinct())}).");

        // One supply, every Port of the group. The PortNumber cited on the line
        // (the parent's own numbering) is not matched against the child: the
        // child's Ports are identified by the child's own PortNumbers.
        return members
            .Select(m => new TopologyConnection
            {
                Output = fromEndpoint,
                Input  = new Endpoint(child.AIMName, dataType, m.PortNumber ?? 1)
            })
            .ToArray();
    }

    // One end of a Topology line, normalised at import (TopologyNormaliser): an AIM
    // (empty for the composite's own boundary), the Data Type, the Port Number, and
    // the Output group of the flow on its producing end. There is no name in it.
    private sealed record Side(string Aim, string DataType, int? Cited, int? Output)
    {
        public bool IsBoundary => string.IsNullOrEmpty(Aim);

        public static Side Read(JsonElement side) => new(
            side.TryGetProperty("AIMName", out var a) ? (a.GetString() ?? string.Empty) : string.Empty,
            side.TryGetProperty("DataType", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : throw new InvalidOperationException("A Topology end states no DataType: the L3 was not normalised at import."),
            side.TryGetProperty("PortNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null,
            side.TryGetProperty("Output", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : null);

        public override string ToString() =>
            (IsBoundary ? "" : Aim + ".") + DataType + (Cited is int c ? ":" + c : "");
    }

    // What an end stands for: a Data Type, the boundary Port's own PortNumber,
    // and the Output number of the flow.
    private sealed record Declared(
        string DataType, IReadOnlyList<string> DataTypes, int? PortNumber, int? Output)
    {
        public bool Accepts(string dataType) =>
            DataTypes.Count > 0 ? DataTypes.Contains(dataType) : DataType == dataType;
    }

    private static Declared Declaration(DescriptorNode node, Side side, string boundaryDirection)
    {
        if (side.IsBoundary)
        {
            // The composite's own boundary: its ExternalPort of the direction this
            // end implies, of the end's Data Type and Port Number.
            var typed = node.Ports
                .Where(p => p.Direction == boundaryDirection && p.Accepts(side.DataType))
                .ToList();

            if (typed.Count > 1 && side.Cited is null)
                throw new InvalidOperationException(
                    $"{node.AIMName}: Topology end '{side}' is one of {typed.Count} {boundaryDirection} Ports; " +
                    "the line must state which by PortNumber.");

            var port = side.Cited is int n
                ? typed.FirstOrDefault(p => (p.PortNumber ?? 1) == n) ?? (typed.Count == 1 ? typed[0] : null)
                : typed.FirstOrDefault();

            if (port is null)
                throw new InvalidOperationException(
                    $"{node.AIMName}: Topology end '{side}' is on the boundary, and {node.AIMName} " +
                    $"declares no {boundaryDirection} ExternalPort of that Data Type and number.");

            return new Declared(port.DataType, port.DataTypes, port.PortNumber, port.OutputGroup ?? side.Output);
        }

        // A Sub-AIM end: its Data Type; the Sub-AIM's Port is found by it, by
        // Direction and by Port Number alone.
        return new Declared(side.DataType, new[] { side.DataType }, null, side.Output);
    }

    // The typed endpoint. The boundary is identified as the User Agent addresses
    // it: by the ExternalPort's own declared PortNumber. A Sub-AIM Port by the
    // number cited on the line, absent meaning 1.
    private static Endpoint EndpointOf(
        DescriptorNode node, Side side, Declared declared, string dataType, string childDirection)
    {
        if (side.IsBoundary)
            return new Endpoint(null, dataType, declared.PortNumber ?? 1);

        var child = node.Children.FirstOrDefault(c => c.AIMName == side.Aim)
            ?? throw new InvalidOperationException(
                $"{node.AIMName}: Topology end '{side}' names an AIM that is not one of its SubAIMs.");

        // A child whose L3 is loaded must have a Port for this Data Type.
        if (child.Ports.Count > 0 &&
            !child.Ports.Any(p => p.Direction == childDirection && p.Accepts(dataType)))
            throw new InvalidOperationException(
                $"{node.AIMName}: Topology end '{side}' - {child.AIMName} declares no " +
                $"{childDirection} Port of {dataType}.");

        var number = side.Cited ?? 1;
        return new Endpoint(child.AIMName, dataType, number);
    }

    public IReadOnlyList<string> Instantiate(
        DescriptorGraph graph,
        IAimProvider provider,
        AimSettings settings,
        AimHost host,
        Func<string?>? storageLocation = null,
        Func<DescriptorNode, IAimProcessor?>? placed = null,
        Func<string, IRuledStorage?>? moduleStorage = null)
    {
        var instantiated = new List<string>();

        // The Module names the context for every provenance stamp made inside it.
        var moduleName = graph.Root?.AIMName ?? "";

        // WHAT A REMOTE CLIENT STARTS MAY BE A BASIC AIM. MPAI-MAS action 6 starts
        // "the AIM selected": a Service that offers one AIM - as the machine running
        // a Sub-AIM of another machine's composite does - runs that AIM, and it stays
        // an AIM. Nothing contains it, so there is nothing to walk: it is built here.
        if (!graph.Root.IsComposite)
        {
            var aimName = graph.Root.AIMName;
            CheckResources(graph.Root);
            host.RegisterRuntime(
                provider.Create(aimName, settings.For(aimName), StorageFor(moduleName, aimName, storageLocation),
                                PrivateStorageFor(moduleName, aimName, storageLocation), moduleStorage?.Invoke(aimName)));
            instantiated.Add(aimName);
            return instantiated;
        }

        InstantiateNode(graph.Root, provider, settings, host, instantiated, moduleName, storageLocation, placed, moduleStorage);
        return instantiated;
    }

    private void InstantiateNode(
        DescriptorNode node,
        IAimProvider provider,
        AimSettings settings,
        AimHost host,
        List<string> instantiated,
        string moduleName,
        Func<string?>? storageLocation,
        Func<DescriptorNode, IAimProcessor?>? placed,
        Func<string, IRuledStorage?>? moduleStorage)
    {
        foreach (var child in node.Children)
        {
            if (child.IsComposite)
            {
                InstantiateNode(child, provider, settings, host, instantiated, moduleName, storageLocation, placed, moduleStorage);
                continue;
            }

            var aimName = child.AIMName;
            if (string.IsNullOrWhiteSpace(aimName) || instantiated.Contains(aimName))
                continue;

            // A SUB-AIM PLACED ON ANOTHER MACHINE (Relation other than Internal)
            // is instantiated there by an AIM host (M3217 3.4); what comes back
            // stands in for it here. Its resources are the host's.
            if (placed?.Invoke(child) is { } onItsHost)
            {
                host.RegisterRuntime(onItsHost);
                instantiated.Add(aimName);
                continue;
            }

            CheckResources(child);
            host.RegisterRuntime(
                provider.Create(aimName, settings.For(aimName), StorageFor(moduleName, aimName, storageLocation),
                                PrivateStorageFor(moduleName, aimName, storageLocation), moduleStorage?.Invoke(aimName)));
            instantiated.Add(aimName);
        }
    }

    private void CheckResources(DescriptorNode node)
    {
        var identifier = new Identifier
        {
            AIMName          = node.AIMName,
            ImplementerID    = node.ImplementerID,
            ImplementationID = node.ImplementationID
        };

        if (!store.Exists(identifier)) return;

        var policies = ResourcePolicy.ReadFrom(store.GetAMD(identifier).RootElement);
        foreach (var policy in policies)
        {
            if (policy.Name != "Memory") continue;
            var minimum   = ResourcePolicy.MemoryBytes(policy.Minimum);
            if (minimum is null) continue;
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available > 0 && available < minimum)
                Console.WriteLine(
                    $"[AIF] {node.AIMName} requests at least {policy.Minimum}; " +
                    $"machine reports {available / (1024.0 * 1024 * 1024):0.0}_GB.");
        }
    }
}
