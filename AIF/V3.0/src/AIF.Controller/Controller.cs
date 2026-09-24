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
    private ISharedStorage? StorageFor(string moduleName, string aimName) =>
        storageRoot is null
            ? null
            : new FileSharedStorage(storageRoot, $"{moduleName}/{aimName}", "local");

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
                    Name      = port.GetProperty("Name").GetString()      ?? string.Empty,
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
                    OutputGroup = IntOf(port, "Output")
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

        // InternalTypes  (InternalType name -> DataType)
        if (root.TryGetProperty("InternalTypes", out var internalTypes))
        {
            foreach (var it in internalTypes.EnumerateArray())
            {
                var name = it.GetProperty("Name").GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var declared = DataTypesOf(it);
                if (declared.Count > 0)
                    node.InternalTypes[name] = declared[0];

                if (IntOf(it, "Output") is int output)
                    node.InternalTypeOutputs[name] = output;
            }
        }

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

                var child = BuildNode(subId, expanding);
                child.Relation =
                    subAim.GetProperty("Identifier").TryGetProperty("Relation", out var relation)
                        ? relation.GetString() ?? string.Empty
                        : string.Empty;
                node.Children.Add(child);
            }
        }

        // Topology
        //   "Output" = the PRODUCING side (data leaves that AIM)
        //   "Input"  = the RECEIVING side (data enters that AIM)
        // Each side names an AIM and a PORT NAME in the JSON; the PORT NAME is a
        // human label. Here it is resolved ONCE, against this composite's own
        // ExternalPorts and InternalTypes, and the connections stored are purely
        // typed. Nothing downstream ever sees the name. See ResolveConnection.
        if (root.TryGetProperty("Topology", out var topology))
        {
            foreach (var connection in topology.EnumerateArray())
                node.Connections.AddRange(ResolveConnection(node, connection));
        }

        expanding.Remove(identifier);
        return node;
    }

    // ONE TOPOLOGY LINE BECOMES ONE OR MORE TYPED CONNECTIONS.
    //
    // A Port name is a label for the person reading the L3. It is used here once,
    // to find the Data Type the composite ITSELF declares for the flow - in its own
    // ExternalPorts or InternalTypes - and is then dropped. A Sub-AIM's own names
    // are never consulted: the composite's author need not know them, and the
    // Sub-AIM's Port is found by Data Type, Direction and Port Number alone.
    //
    // A connection carries one Data Type. It is taken from whichever end the
    // composite declares; the other end's name is a label only.
    //
    // M3194 OUTPUT / INPUT. Where the receiving Sub-AIM is a composite that
    // declares Input groups for that Data Type, the flow must state an Output
    // number (on its InternalType, or on the boundary ExternalPort it enters
    // through), and the line is expanded into one connection per Port of the
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

        var dataType = fromDecl?.DataType ?? toDecl?.DataType;
        var legacy   = false;

        // LEGACY. An L3 written before this rule names a Sub-AIM Port by that
        // Sub-AIM's own name. Still read, so that Modules not yet aligned keep
        // loading - but said, because it is what this design removes.
        if (dataType is null)
        {
            var legacyType = LegacyDataType(node, from, "Output") ?? LegacyDataType(node, to, "Input");
            if (legacyType is not null)
            {
                Console.WriteLine(
                    $"[AIF] {node.AIMName}: Topology '{from}' -> '{to}' names no Port or InternalType " +
                    $"of {node.AIMName}; resolved to {legacyType} through a Sub-AIM's own Port name (legacy).");
                dataType = legacyType;
                legacy   = true;
            }
        }

        if (dataType is null)
            throw new InvalidOperationException(
                $"{node.AIMName}: Topology '{from}' -> '{to}' resolves to no Data Type. At least one " +
                $"end must be a Port or InternalType that {node.AIMName} itself declares.");

        if (fromDecl is not null && toDecl is not null &&
            !fromDecl.Accepts(toDecl.DataType) && !toDecl.Accepts(fromDecl.DataType))
            throw new InvalidOperationException(
                $"{node.AIMName}: Topology '{from}' -> '{to}' joins {fromDecl.DataType} to {toDecl.DataType}.");

        if (fromDecl?.Output is int a && toDecl?.Output is int b && a != b)
            throw new InvalidOperationException(
                $"{node.AIMName}: Topology '{from}' -> '{to}' is declared with Output {a} at one end " +
                $"and Output {b} at the other.");

        var output = fromDecl?.Output ?? toDecl?.Output;

        var fromEndpoint = EndpointOf(node, from, fromDecl, dataType, "Output", legacy);
        var toEndpoint   = EndpointOf(node, to,   toDecl,   dataType, "Input",  legacy);

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

    // One end of a Topology line as written: an AIM (empty for the composite's
    // own boundary), a label, and the Port Number cited, if any.
    private sealed record Side(string Aim, string Name, int? Cited)
    {
        public bool IsBoundary => string.IsNullOrEmpty(Aim);

        public static Side Read(JsonElement side) => new(
            side.TryGetProperty("AIMName", out var a) ? (a.GetString() ?? string.Empty) : string.Empty,
            side.TryGetProperty("PortName", out var p) ? (p.GetString() ?? string.Empty) : string.Empty,
            side.TryGetProperty("PortNumber", out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt32()
                : null);

        public override string ToString() =>
            (IsBoundary ? "" : Aim + ".") + Name + (Cited is int c ? ":" + c : "");
    }

    // What the composite itself declares for a label: a Data Type, the declared
    // PortNumber when the label is one of its own ExternalPorts, and the Output
    // number declared on that flow.
    private sealed record Declared(
        string DataType, IReadOnlyList<string> DataTypes, int? PortNumber, int? Output)
    {
        public bool Accepts(string dataType) =>
            DataTypes.Count > 0 ? DataTypes.Contains(dataType) : DataType == dataType;
    }

    private static Declared? Declaration(DescriptorNode node, Side side, string boundaryDirection)
    {
        if (side.IsBoundary)
        {
            // The composite's own boundary: one of its ExternalPorts, of the
            // direction this end implies. A label may repeat - PortNumber decides.
            var named = node.Ports
                .Where(p => p.Direction == boundaryDirection && p.Name == side.Name)
                .ToList();

            if (named.Count > 1 && side.Cited is null)
                throw new InvalidOperationException(
                    $"{node.AIMName}: Topology end '{side}' names {named.Count} {boundaryDirection} Ports; " +
                    "the line must state which by PortNumber.");

            var port = side.Cited is int n
                ? named.FirstOrDefault(p => (p.PortNumber ?? 1) == n) ?? (named.Count == 1 ? named[0] : null)
                : named.FirstOrDefault();

            if (port is null)
                throw new InvalidOperationException(
                    $"{node.AIMName}: Topology end '{side}' is on the boundary, and {node.AIMName} " +
                    $"declares no {boundaryDirection} ExternalPort of that name and number.");

            return new Declared(port.DataType, port.DataTypes, port.PortNumber, port.OutputGroup);
        }

        // A Sub-AIM end: the label is the COMPOSITE's name for the flow.
        if (node.InternalTypes.TryGetValue(side.Name, out var internalType))
            return new Declared(
                internalType, new[] { internalType }, null,
                node.InternalTypeOutputs.TryGetValue(side.Name, out var o) ? o : null);

        var external = node.Ports.FirstOrDefault(p => p.Name == side.Name);
        if (external is not null)
            return new Declared(external.DataType, external.DataTypes, null, external.OutputGroup);

        return null;   // a label the composite does not declare: for the reader only
    }

    // The typed endpoint. The boundary is identified as the User Agent addresses
    // it: by the ExternalPort's own declared PortNumber. A Sub-AIM Port by the
    // number cited on the line, absent meaning 1.
    private static Endpoint EndpointOf(
        DescriptorNode node, Side side, Declared? declared, string dataType, string childDirection,
        bool legacy)
    {
        if (side.IsBoundary)
            return new Endpoint(null, dataType, declared?.PortNumber ?? 1);

        var child = node.Children.FirstOrDefault(c => c.AIMName == side.Aim)
            ?? throw new InvalidOperationException(
                $"{node.AIMName}: Topology end '{side}' names an AIM that is not one of its SubAIMs.");

        // A child whose L3 is loaded must have a Port for this Data Type.
        if (child.Ports.Count > 0 &&
            !child.Ports.Any(p => p.Direction == childDirection && p.Accepts(dataType)))
            throw new InvalidOperationException(
                $"{node.AIMName}: Topology end '{side}' - {child.AIMName} declares no " +
                $"{childDirection} Port of {dataType}.");

        var number = side.Cited ?? (legacy ? LegacyPortNumber(child, side, childDirection) : null) ?? 1;
        return new Endpoint(child.AIMName, dataType, number);
    }

    // LEGACY ONLY: a Sub-AIM Port found by that Sub-AIM's own name.
    private static string? LegacyDataType(DescriptorNode node, Side side, string childDirection)
    {
        if (side.IsBoundary) return null;
        var child = node.Children.FirstOrDefault(c => c.AIMName == side.Aim);
        return child?.Ports
            .FirstOrDefault(p => p.Direction == childDirection && p.Name == side.Name &&
                                 (side.Cited is null || (p.PortNumber ?? 1) == side.Cited))
            ?.DataType;
    }

    private static int? LegacyPortNumber(DescriptorNode child, Side side, string childDirection) =>
        child.Ports
            .FirstOrDefault(p => p.Direction == childDirection && p.Name == side.Name)
            ?.PortNumber;

    public IReadOnlyList<string> Instantiate(
        DescriptorGraph graph,
        IAimProvider provider,
        AimSettings settings,
        AimHost host)
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
            var elsewhere = RemoteAims?.Invoke(aimName, graph.Root.Relation);
            host.RegisterRuntime(elsewhere ??
                provider.Create(aimName, settings.For(aimName), StorageFor(moduleName, aimName)));
            instantiated.Add(aimName);
            return instantiated;
        }

        InstantiateNode(graph.Root, provider, settings, host, instantiated, moduleName);
        return instantiated;
    }

    // WHERE A SUB-AIM THAT RUNS ELSEWHERE IS REACHED. A Service sets this; it is
    // asked for every AIM, with what the L3 says about where that AIM runs, and
    // answers null for the AIMs this machine builds itself. The Controller stays
    // free of any knowledge of MPAI-MAS: what comes back is an IAimProcessor.
    public static Func<string, string, IAimProcessor?>? RemoteAims { get; set; }

    private void InstantiateNode(
        DescriptorNode node,
        IAimProvider provider,
        AimSettings settings,
        AimHost host,
        List<string> instantiated,
        string moduleName)
    {
        foreach (var child in node.Children)
        {
            if (child.IsComposite)
            {
                InstantiateNode(child, provider, settings, host, instantiated, moduleName);
                continue;
            }

            var aimName = child.AIMName;
            if (string.IsNullOrWhiteSpace(aimName) || instantiated.Contains(aimName))
                continue;

            CheckResources(child);

            // A SUB-AIM MAY RUN ON ANOTHER MACHINE (MPAI-MAS: Relation other than
            // Internal). The Service says where, and supplies something that looks
            // to this Controller like any AIM and carries its Ports there and back.
            // Unanswered - no such arrangement, or none for this AIM - it is built
            // here, as every AIM is today.
            var elsewhere = RemoteAims?.Invoke(aimName, child.Relation);
            host.RegisterRuntime(elsewhere ??
                provider.Create(aimName, settings.For(aimName), StorageFor(moduleName, aimName)));
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
