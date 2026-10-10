using System.Text.Json.Nodes;

using AIF.Channels;

namespace AIF.Controller;

// THE CONTROLLER'S SIDE OF THE CONTROL PATH (M3217 3.3): one link to one AIM
// host, and what the Controller asks of it - place an AIM of a Module instance,
// run it, pause, resume, stop, status, release.
public sealed class AimHostClient : IAsyncDisposable
{
    public RemoteLink Link { get; }
    public string Address { get; }

    private AimHostClient(RemoteLink link, string address)
    {
        Link = link;
        Address = address;
    }

    // address: host:port, or tcp+tls://host:port, as the Controller's
    // configuration names it (M3217 3.4).
    public static Task<AimHostClient> ConnectAsync(string address, string key, CancellationToken cancel = default) =>
        ConnectAsync(address, new KeyAdmission(key), cancel);

    public static async Task<AimHostClient> ConnectAsync(string address, ILinkAdmission admission, CancellationToken cancel = default)
    {
        var at = address.StartsWith("tcp+tls://", StringComparison.OrdinalIgnoreCase) ? address["tcp+tls://".Length..] : address;
        at = at.TrimEnd('/');
        var colon = at.LastIndexOf(':');
        var link = await RemoteLink.ConnectAsync(at[..colon], int.Parse(at[(colon + 1)..]), admission, cancel);
        return new AimHostClient(link, address);
    }

    // The host's answer: with the CII of the AIM Instance it placed, whose key it
    // made and holds (M3223 3.1).
    public async Task<JsonObject> PlaceAsync(string module, string aim)
    {
        var reply = await Link.RequestAsync(new JsonObject { ["Kind"] = "Place", ["Module"] = module, ["Aim"] = aim });
        if (reply["Ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"The AIM host at {Address} did not place {aim}: {reply["Error"]}.");
        return reply;
    }

    public Task<JsonObject> AskAsync(string kind, string module, JsonObject? more = null)
    {
        var frame = more ?? new JsonObject();
        frame["Kind"] = kind;
        frame["Module"] = module;
        return Link.RequestAsync(frame);
    }

    public ValueTask DisposeAsync() => Link.DisposeAsync();
}

// AN AIM ON A HOST, FIRED BY ITS CONTROLLER IN AN EXCHANGE. To the executor it is
// an AIM like any other: given its inputs, it answers with its outputs. The AIM
// runs on the host, under the host's lifecycle, which the Controller drives; what
// it reported comes back with its answer. A link lost is an error of the AIM.
public sealed class RemoteProcessor : IAimProcessor
{
    private readonly AimHostClient host;
    private readonly string module;
    private readonly Action<string, string>? report;

    public RemoteProcessor(string instanceId, AimHostClient host, string module, Action<string, string>? report = null)
    {
        InstanceId = instanceId;
        this.host = host;
        this.module = module;
        this.report = report;
    }

    public string InstanceId { get; }

    public async Task<Message> ProcessAsync(Message message)
    {
        JsonObject reply;
        try
        {
            reply = await host.AskAsync("Process", module, new JsonObject
            {
                ["Aim"] = InstanceId,
                ["MessageId"] = message.MessageId,
                ["Ports"] = new JsonObject(message.Ports.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)))
            });
        }
        catch (Exception lost) when (lost is IOException || host.Link.IsClosed)
        {
            throw new InvalidOperationException($"the link to its host {host.Address} was lost: {lost.Message}", lost);
        }

        foreach (var text in reply["Reports"]?.AsArray() ?? new JsonArray()) report?.Invoke(InstanceId, text!.GetValue<string>());
        if (reply["Ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"its host refused: {reply["Error"]}");
        if (reply["Cancelled"] is { } cancelled) throw new OperationCanceledException(cancelled.GetValue<string>());
        if (reply["Threw"] is { } threw) throw new InvalidOperationException(threw.GetValue<string>());

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = reply["MessageType"]?.GetValue<string>() ?? "",
            Payload     = reply["Payload"]?.GetValue<string>() ?? "",
            Ports       = (reply["Ports"]?.AsObject() ?? new JsonObject()).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>())
        };
    }
}
