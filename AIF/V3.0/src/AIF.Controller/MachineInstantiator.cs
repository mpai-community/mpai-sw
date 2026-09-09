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
                        connection.Source,
                    Destination =
                        connection.Destination
                });

            channelNumber++;
        }

        return machine;
    }

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