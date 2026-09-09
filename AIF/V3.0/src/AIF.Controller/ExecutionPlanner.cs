namespace AIF.Controller;

// Derives the execution order of a composite AIM's SubAIMs from its Topology,
// so the AIM Metadata — not hand-written code — decides what runs when.
//
// A connection is "AIMName.PortName" at each end, or a bare "PortName" when the
// end is the composite's own boundary. Only AIM-to-AIM connections constrain
// the order; boundary connections are inputs and outputs of the composite.
public sealed class ExecutionPlanner
{
    public IReadOnlyList<string> BuildPlan(
        DescriptorGraph graph)
    {
        return BuildPlan(graph.Root);
    }

    public IReadOnlyList<string> BuildPlan(
        DescriptorNode node)
    {
        var order =
            node.Children
                .Select(child => child.AIMName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct()
                .ToList();

        var known =
            new HashSet<string>(order);

        var successors =
            order.ToDictionary(
                name => name,
                _ => new List<string>());

        var indegree =
            order.ToDictionary(
                name => name,
                _ => 0);

        foreach (var connection in node.Connections)
        {
            var source =
                AimOf(connection.Source);

            var destination =
                AimOf(connection.Destination);

            if (source is null ||
                destination is null ||
                !known.Contains(source) ||
                !known.Contains(destination) ||
                source == destination)
            {
                continue;   // a boundary connection, or outside this composite
            }

            successors[source].Add(destination);
            indegree[destination]++;
        }

        var plan =
            new List<string>();

        var ready =
            order.Where(name => indegree[name] == 0)
                 .ToList();

        while (ready.Count > 0)
        {
            var next = ready[0];
            ready.RemoveAt(0);
            plan.Add(next);

            foreach (var successor in successors[next])
            {
                indegree[successor]--;

                if (indegree[successor] == 0)
                {
                    var position =
                        ready.FindIndex(
                            name => order.IndexOf(name) > order.IndexOf(successor));

                    if (position < 0)
                    {
                        ready.Add(successor);
                    }
                    else
                    {
                        ready.Insert(position, successor);
                    }
                }
            }
        }

        if (plan.Count != order.Count)
        {
            throw new InvalidOperationException(
                $"The Topology of {node.AIMName} contains a cycle; no execution order exists.");
        }

        return plan;
    }

    private static string? AimOf(
        string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var separator =
            endpoint.LastIndexOf('.');

        return separator <= 0
            ? null
            : endpoint[..separator];
    }
}
