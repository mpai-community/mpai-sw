namespace AIF.Controller;

public sealed class MachineInstantiator
{
    public MachineInstance Instantiate(
        DescriptorGraph graph)
    {
        var machine =
            new MachineInstance
            {
                MachineId =
                    BuildMachineId(
                        graph.Root),
                AIMName =
                    graph.Root.AIMName,

                DescriptorGraph =
                    graph,

                ConnectionCount =
                    graph.Connections.Count
            };

        machine.AimInstances.Add(
            new AimInstance
            {
                AIMName =
                    graph.Root.AIMName,
                InstanceId =
                    BuildInstanceId(
                        graph.Root)
            });

        AddAimInstances(
            graph.Root,
            machine);

        int channelNumber = 1;

        foreach (var connection
                 in graph.Connections)
        {
            machine.Channels.Add(
                new ChannelInstance
                {
                    ChannelId =
                        $"CH#{channelNumber}",
                    Source =
                        Describe(connection.Output),
                    Destination =
                        Describe(connection.Input)
                });

            channelNumber++;
        }

        return machine;
    }

    // Render a typed Endpoint as a readable channel label (diagnostic only;
    // routing is by DataType+PortNumber, not by this string). Boundary side has
    // no AIM. Shows "AIM:DataType#n" or ":DataType#n" for the boundary.
    private static string Describe(Endpoint e) =>
        (string.IsNullOrEmpty(e.AimName) ? "" : e.AimName) + ":" + e.DataType + "#" + e.PortNumber;
    private static void AddAimInstances(
        DescriptorNode parent,
        MachineInstance machine)
    {
        foreach (var child in parent.Children)
        {
            machine.AimInstances.Add(
                new AimInstance
                {
                    AIMName =
                        child.AIMName,
                    InstanceId =
                        BuildInstanceId(
                            child)
                });

            AddAimInstances(
                child,
                machine);
        }
    }

    private static string BuildMachineId(
        DescriptorNode node)
    {
        return BuildIdentifier(node);
    }

    private static string BuildInstanceId(
        DescriptorNode node)
    {
        return BuildIdentifier(node);
    }

    private static string BuildIdentifier(
        DescriptorNode node)
    {
        if (!string.IsNullOrWhiteSpace(
                node.ImplementerID) &&
            !string.IsNullOrWhiteSpace(
                node.ImplementationID))
        {
            return
                $"{node.ImplementerID}" +
                $"{node.ImplementationID}";
        }

        return node.AIMName;
    }
}