using System.Text.Json;
using AIF.Store;

namespace AIF.Controller;

public sealed class Controller
{
    private readonly AmdStore store;

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
        // Look up by AIMName only 鈥?ImplementerID and ImplementationID may be
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

                    // Omitted means false: absence of this input suspends.
                    IsOptional =
                        port.TryGetProperty("IsOptional", out var optional) &&
                        optional.ValueKind == JsonValueKind.True
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

        // InternalTypes  (InternalType name -> DataType)
        if (root.TryGetProperty("InternalTypes", out var internalTypes))
        {
            foreach (var it in internalTypes.EnumerateArray())
            {
                var name = it.GetProperty("Name").GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // An InternalType names a Data Type, or a SET of them - it is
                // the same kind of declaration as a Port's, so it takes the same
                // form. This loop read it with GetString() and threw on the
                // array: the schema was extended and this reader was not.
                //
                // InternalTypes maps a name to ONE Data Type, so the first of a
                // set stands for it. That is enough for what the map is for -
                // resolving an InternalType name in a Topology - and a set here
                // says the flow may be of either kind, not that it is two flows.
                var declared = DataTypesOf(it);
                if (declared.Count > 0)
                    node.InternalTypes[name] = declared[0];
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
                if (!string.IsNullOrWhiteSpace(subId.AIMName))
                    node.Children.Add(BuildNode(subId, expanding));
            }
        }

        // Topology
        // Convention in the instance JSON:
        //   "Output" = the PRODUCING side (data leaves that AIM)
        //   "Input"  = the RECEIVING side (data enters that AIM)
        if (root.TryGetProperty("Topology", out var topology))
        {
            foreach (var connection in topology.EnumerateArray())
            {
                var producer = connection.GetProperty("Output");  // producing AIM
                var consumer = connection.GetProperty("Input");   // receiving AIM

                node.Connections.Add(new TopologyConnection
                {
                    Source      = Endpoint(producer),  // who produces
                    Destination = Endpoint(consumer)   // who consumes
                });
            }
        }

        expanding.Remove(identifier);
        return node;
    }

    private static string Endpoint(JsonElement port)
    {
        var aimName  = port.GetProperty("AIMName").GetString()  ?? string.Empty;
        var portName = port.GetProperty("PortName").GetString() ?? string.Empty;

        // A Topology PortID may carry a PortNumber, which selects WHICH port of
        // that Direction and DataType is meant when the endpoint AIM declares
        // more than one. It rides along in the endpoint string as "#n" and is
        // read back by Endpoint.Parse. Omitted means 1.
        var ordinal =
            port.TryGetProperty("PortNumber", out var portNumber) &&
            portNumber.TryGetInt32(out var parsedOrdinal)
                ? $"#{parsedOrdinal}"
                : string.Empty;

        return string.IsNullOrWhiteSpace(aimName)
            ? $"{portName}{ordinal}"
            : $"{aimName}.{portName}{ordinal}";
    }

    public IReadOnlyList<string> Instantiate(
        DescriptorGraph graph,
        IAimProvider provider,
        AimSettings settings,
        AimHost host)
    {
        var instantiated = new List<string>();
        InstantiateNode(graph.Root, provider, settings, host, instantiated);
        return instantiated;
    }

    private void InstantiateNode(
        DescriptorNode node,
        IAimProvider provider,
        AimSettings settings,
        AimHost host,
        List<string> instantiated)
    {
        foreach (var child in node.Children)
        {
            if (child.IsComposite)
            {
                InstantiateNode(child, provider, settings, host, instantiated);
                continue;
            }

            var aimName = child.AIMName;
            if (string.IsNullOrWhiteSpace(aimName) || instantiated.Contains(aimName))
                continue;

            CheckResources(child);
            host.RegisterRuntime(provider.Create(aimName, settings.For(aimName)));
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
