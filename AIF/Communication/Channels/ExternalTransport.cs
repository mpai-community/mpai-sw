using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AIF.Channels;

// THE EXTERNAL TRANSPORT (M3205 3.3, Between Controllers; M3241 3.2). A Controller
// discovers the Controllers in range running the same type of Module and exchanges
// Messages with their External Ports: what an External Output Port of its Module
// gives is sent to every Controller linked, and what they send is written to the
// External Input Port of the same Data Type and Port Number, with the writer's
// controllerID and stamp. A controllerID is valid from the discovery that returned
// it until that Controller leaves range - it is no longer in range, or no longer
// announces itself - and the link is then closed.

// What a Controller says of itself to those in range.
public sealed record Announcement(string ControllerId, string ModuleType, string Host, int Port);

// WHERE ANNOUNCEMENTS TRAVEL: the radio of a vehicle; on one machine, its network.
public interface IDiscoveryMedium : IAsyncDisposable
{
    Task AnnounceAsync(Announcement announcement, CancellationToken cancel);
    event Action<Announcement>? Heard;
}

// ANNOUNCEMENTS BY UDP MULTICAST on the machine's network: every Controller on it
// hears every other; which of them are in range is the hub's to decide.
public sealed class UdpDiscovery : IDiscoveryMedium
{
    public const string DefaultGroup = "239.255.41.10";
    private readonly UdpClient receiver;
    private readonly UdpClient sender;
    private readonly IPEndPoint group;
    private readonly CancellationTokenSource stop = new();
    private readonly Task listening;
    public event Action<Announcement>? Heard;

    public UdpDiscovery(int port, string groupAddress = DefaultGroup)
    {
        group = new IPEndPoint(IPAddress.Parse(groupAddress), port);
        receiver = new UdpClient(AddressFamily.InterNetwork);
        receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        receiver.JoinMulticastGroup(group.Address, IPAddress.Loopback);
        receiver.MulticastLoopback = true;
        sender = new UdpClient(AddressFamily.InterNetwork) { MulticastLoopback = true };
        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
        listening = Task.Run(ListenAsync);
    }

    public async Task AnnounceAsync(Announcement a, CancellationToken cancel)
    {
        var bytes = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["Kind"] = "MPAI-AIF-Announcement", ["ControllerId"] = a.ControllerId, ["ModuleType"] = a.ModuleType, ["Host"] = a.Host, ["Port"] = a.Port
        }.ToJsonString());
        await sender.SendAsync(bytes, group, cancel);
    }

    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var got = await receiver.ReceiveAsync(stop.Token);
                var o = JsonNode.Parse(Encoding.UTF8.GetString(got.Buffer)) as JsonObject;
                if ((string?)o?["Kind"] != "MPAI-AIF-Announcement") continue;
                Heard?.Invoke(new Announcement((string)o!["ControllerId"]!, (string)o["ModuleType"]!, (string)o["Host"]!, (int)o["Port"]!));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is SocketException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        receiver.Dispose();
        sender.Dispose();
        try { await listening; } catch { }
    }
}

// A Message received on an External Input Port: its writer and the writer's stamp.
public sealed record ExternalMessage(string DataType, int PortNumber, string Json, string From, DateTimeOffset Stamp);

// THE HUB OF ONE MODULE: its announcement, its links, its External Ports.
public sealed class ExternalHub : IAsyncDisposable
{
    public sealed record Options
    {
        public required string ControllerId { get; init; }
        public required string ModuleType { get; init; }
        public required IDiscoveryMedium Discovery { get; init; }
        public required ILinkAdmission Admission { get; init; }

        // Whether a Controller is in range: the radio's; in a simulation, its distance.
        public Func<string, bool> InRange { get; init; } = _ => true;
        public TimeSpan AnnounceEvery { get; init; } = TimeSpan.FromMilliseconds(250);
        public TimeSpan LeaveAfter { get; init; } = TimeSpan.FromSeconds(3);
        public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    }

    // What the hub gives its Module (writes to an External Input Port), and the
    // External Output Ports it reads: (Data Type, Port Number) pairs, and a reader
    // that waits up to the timeout for the next Message of one of them.
    public required Func<ExternalMessage, Task> Deliver { get; init; }
    public IReadOnlyList<(string DataType, int PortNumber)> Outputs { get; init; } = [];

    // THE CONTROLLER'S TRUST (M3241 3.1, 3.3): what its Module sends signed; what it
    // receives verified against the Controller the link admitted - null where it is
    // accepted, else why not; refused, it is said and given to no one.
    public Func<string, string>? Sign { get; init; }
    public Func<string, string, string?>? Verify { get; init; }
    public long Refused => Interlocked.Read(ref refused);
    private long refused;
    public Func<string, int, int, Task<(string Json, DateTimeOffset Stamp)?>>? ReadOutput { get; init; }

    private readonly Options options;
    private readonly RemoteLink.Listener listener;
    private readonly ConcurrentDictionary<string, RemoteLink> links = new();
    private readonly ConcurrentDictionary<string, (Announcement Said, DateTimeOffset Heard)> heard = new();
    private readonly ConcurrentDictionary<string, byte> opening = new();
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> running = new();

    // What happened, for whoever watches: a Controller linked, left, refused.
    public event Action<string>? Said;
    public IReadOnlyCollection<string> Linked => links.Keys.ToList();
    public long Sent => Interlocked.Read(ref sent);
    public long Received => Interlocked.Read(ref received);
    private long sent, received;

    public ExternalHub(Options options)
    {
        this.options = options;
        listener = new RemoteLink.Listener(0, options.Admission, Accepted);
        listener.Failed += why => Said?.Invoke($"a Controller not linked: {why}");
        options.Discovery.Heard += Heard;
    }

    public void Start()
    {
        running.Add(Task.Run(AnnounceAsync));
        running.Add(Task.Run(WatchAsync));
        foreach (var (dataType, portNumber) in Outputs) running.Add(Task.Run(() => PumpAsync(dataType, portNumber)));
    }

    private async Task AnnounceAsync()
    {
        var me = new Announcement(options.ControllerId, options.ModuleType, "127.0.0.1", listener.Port);
        while (!stop.IsCancellationRequested)
        {
            try { await options.Discovery.AnnounceAsync(me, stop.Token); } catch (Exception) when (!stop.IsCancellationRequested) { }
            try { await Task.Delay(options.AnnounceEvery, stop.Token); } catch (OperationCanceledException) { }
        }
    }

    private void Heard(Announcement a)
    {
        if (a.ControllerId == options.ControllerId || a.ModuleType != options.ModuleType) return;
        heard[a.ControllerId] = (a, options.Clock());
    }

    // WHO IS IN RANGE, NOW: a Controller heard lately and in range is linked - by the
    // one of the two whose controllerID is lower, so that there is one link; one no
    // longer heard, or no longer in range, is left.
    private async Task WatchAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var now = options.Clock();
            foreach (var (id, (said, at)) in heard)
            {
                var present = now - at <= options.LeaveAfter && options.InRange(id);
                if (!present && links.TryRemove(id, out var gone))
                {
                    Said?.Invoke($"{id} left");
                    await gone.DisposeAsync();
                }
                else if (present && !links.ContainsKey(id) && string.CompareOrdinal(options.ControllerId, id) < 0 && opening.TryAdd(id, 0))
                    _ = ConnectAsync(said);
            }
            try { await Task.Delay(100, stop.Token); } catch (OperationCanceledException) { }
        }
    }

    private async Task ConnectAsync(Announcement to)
    {
        try
        {
            var link = await RemoteLink.ConnectAsync(to.Host, to.Port, options.Admission, stop.Token);
            await link.NoticeAsync(new JsonObject { ["Kind"] = "Hello", ["ControllerId"] = options.ControllerId });
            Join(to.ControllerId, link);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SocketException)
        {
            Said?.Invoke($"{to.ControllerId} not linked: {e.Message}");
        }
        catch (OperationCanceledException) { }
        finally { opening.TryRemove(to.ControllerId, out _); }
    }

    // A link another Controller opened: it names itself first.
    private void Accepted(RemoteLink link)
    {
        link.OnNotice = frame =>
        {
            if ((string?)frame["Kind"] == "Hello" && (string?)frame["ControllerId"] is { } id)
            {
                if (options.InRange(id)) Join(id, link);
                else { Said?.Invoke($"{id} refused: not in range"); _ = link.DisposeAsync().AsTask(); }
            }
            return Task.CompletedTask;
        };
    }

    private void Join(string id, RemoteLink link)
    {
        if (!links.TryAdd(id, link)) { _ = link.DisposeAsync().AsTask(); return; }
        link.OnNotice = Receive(id);
        link.Lost += _ => { if (links.TryRemove(new KeyValuePair<string, RemoteLink>(id, link))) Said?.Invoke($"{id} lost"); };
        Said?.Invoke($"{id} linked");
    }

    private Func<JsonObject, Task> Receive(string from) => async frame =>
    {
        if ((string?)frame["Kind"] != "External") return;
        Interlocked.Increment(ref received);
        if (Verify?.Invoke(from, (string)frame["Json"]!) is { } why)
        {
            Interlocked.Increment(ref refused);
            Said?.Invoke($"from {from}, refused: {why}");
            return;
        }
        await Deliver(new ExternalMessage((string)frame["DataType"]!, (int)frame["PortNumber"]!, (string)frame["Json"]!, from,
                                          DateTimeOffset.Parse((string)frame["Stamp"]!, System.Globalization.CultureInfo.InvariantCulture)));
    };

    // WHAT AN EXTERNAL OUTPUT PORT GIVES, to every Controller linked.
    private async Task PumpAsync(string dataType, int portNumber)
    {
        while (!stop.IsCancellationRequested)
        {
            (string Json, DateTimeOffset Stamp)? next;
            try { next = await ReadOutput!(dataType, portNumber, 200); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            if (next is not { } m) continue;
            await SendAsync(dataType, portNumber, Sign?.Invoke(m.Json) ?? m.Json, m.Stamp);
        }
    }

    public async Task SendAsync(string dataType, int portNumber, string json, DateTimeOffset stamp)
    {
        var frame = new JsonObject
        {
            ["Kind"] = "External", ["DataType"] = dataType, ["PortNumber"] = portNumber, ["Json"] = json,
            ["Stamp"] = stamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        foreach (var (id, link) in links)
        {
            try { await link.NoticeAsync(frame.DeepClone().AsObject()); Interlocked.Increment(ref sent); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { Said?.Invoke($"{id}: not sent ({e.Message})"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        options.Discovery.Heard -= Heard;
        try { await Task.WhenAll(running); } catch { }
        foreach (var link in links.Values) await link.DisposeAsync();
        links.Clear();
        await listener.DisposeAsync();
    }
}
