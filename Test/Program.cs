using AIF.Controller;
using AIF.Store;

var amdDir = args.Length > 0 ? args[0] : "../AIMs/AMDs";
var store  = new AmdStore(amdDir); store.Scan();
var controller = new Controller(store);

DescriptorGraph Load(string aim) =>
    controller.RegisterAim(store.FindByAimName(aim) ?? throw new Exception("no " + aim));

void Show(DescriptorNode n, string indent = "")
{
    if (!n.IsComposite) return;
    Console.WriteLine($"{indent}{n.AIMName}");
    foreach (var c in n.Connections)
        Console.WriteLine($"{indent}  {E(c.Output)}  ->  {E(c.Input)}");
    foreach (var ch in n.Children) Show(ch, indent + "    ");
}
static string E(Endpoint e) => $"{(e.IsBoundary ? "[boundary]" : e.AimName)} {e.DataType}#{e.PortNumber}";

if (args.Length > 1 && args[1] == "all")
{
    foreach (var name in store.GetAimNames().OrderBy(x => x))
    {
        try { var g = Load(name); if (g.Root.IsComposite) Console.WriteLine($"LOADS   {name}"); }
        catch (Exception ex) { Console.WriteLine($"FAILS   {name}: {ex.Message}"); }
    }
    return;
}

var graph = Load("1MMC-MAD-V2.5-I01");
Console.WriteLine("=== Typed connections ===");
Show(graph.Root);

// Stand-in leaves: each reports, on every Output Port it declares, what reached it.
var host = new AimHost();
void Register(DescriptorNode n)
{
    if (n.IsComposite) { foreach (var c in n.Children) Register(c); return; }
    host.RegisterRuntime(new Echo(n));
}
Register(graph.Root);
var executor = new MachineExecutor(host);

async Task Exchange(string title, Dictionary<string, string> boundary)
{
    Console.WriteLine($"\n=== {title}: offered {string.Join(", ", boundary.Keys)} ===");
    var result = await executor.ExecuteResumableAsync(graph,
        new Message { MessageId = "t", MessageType = "test", Ports = boundary });
    if (result.IsSuspended) { Console.WriteLine("SUSPENDED on " + result.Suspended!.WaitingPort); return; }
    foreach (var kv in result.Completed!.Ports) Console.WriteLine($"RESULT {kv.Key} = {kv.Value}");
}

await Exchange("Welcome", new() { ["OSD-BTO-V1.5#1"] = "'Welcome'" });
await Exchange("Dialogue turn", new() { ["OSD-BSO-V1.5#1"] = "<speech>", ["OSD-STM-V1.5#1"] = "<time>" });
await Exchange("Dialogue turn with EPS", new() { ["OSD-BSO-V1.5#1"] = "<speech>", ["MMC-EPS-V2.5#1"] = "<eps>" });

sealed class Echo(DescriptorNode node) : IAimProcessor
{
    public string InstanceId => node.AIMName;
    public Task<Message> ProcessAsync(Message m)
    {
        var got = string.Join(" ", m.Ports.Select(kv => $"{kv.Key}={kv.Value}"));
        var short_ = node.AIMName.Substring(1, 7);
        var outs = node.Ports.Where(p => p.Direction == "Output")
                             .ToDictionary(p => p.Name, p => $"{short_}({got})");
        Console.WriteLine($"   ran {node.AIMName}: {got}");
        return Task.FromResult(new Message { MessageId = m.MessageId, MessageType = m.MessageType, Ports = outs });
    }
}
