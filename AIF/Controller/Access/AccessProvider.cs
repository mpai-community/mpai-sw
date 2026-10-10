using System.Security.Cryptography;
using System.Text.Json.Nodes;

using AIF.Channels;
using AIF.Trust;

namespace AIF.Controller;

// THE CONTROLLER API CALLED BY A PROVIDER (AIF V3.0, Basic API 6): a third party
// holding the rights to data writes a Source of Access directly, not through the User
// Agent. Its calls reach the Controller only on a link the Controller admitted with the
// Trust Protocol over TLS (TrustedLink), and each call is a request the Provider signs
// with MPAI-PTF Data Exchange Metadata (ExternalSigning, as between Controllers). The
// Controller acts on a request only if its signature verifies and its signer is the
// Provider the link admitted; the store then lets only the Writer of the Source write
// it, and refuses a Version not higher than the current one (AccessStore).

// The Controller's end: listens for Providers.
public sealed class AccessProviderServer : IAsyncDisposable
{
    private readonly RemoteLink.Listener listener;
    private readonly AccessStore store;
    private readonly TrustProtocol protocol;

    public int Port => listener.Port;

    // Each request refused before it reached the store, and why.
    public event Action<string>? Refused;

    public AccessProviderServer(AccessStore store, TrustProtocol protocol, int port = 0)
    {
        this.store = store;
        this.protocol = protocol;
        listener = new RemoteLink.Listener(port, new TrustedLink(protocol), link =>
            link.OnRequest = frame => Task.FromResult<JsonObject?>(Handle(link, frame)));
    }

    private JsonObject Handle(RemoteLink link, JsonObject frame)
    {
        if (link.Peer is not { } provider) return Refuse("the link admitted no Provider");
        if (frame["Request"] is not JsonObject request) return Refuse("no request");

        // Signed by the Provider this link admitted, under an anchor this Controller
        // trusts, over the request as it came.
        var check = ExternalSigning.Verify(request.ToJsonString(), provider, protocol.AnchorOf);
        if (check != ExternalSigning.Outcome.Valid) return Refuse($"the request is {check}");

        var source = (string?)request["Source"] ?? "";
        var key = (string?)request["Key"] ?? "";
        var version = request["Version"]?.GetValue<long>();
        var outcome = (string?)request["Function"] switch
        {
            "Create" => store.Create(source, provider),
            "Put"    => version is null ? AccessOutcome.Refused
                      : store.Put(source, provider, key, Convert.FromBase64String((string?)request["Data"] ?? ""), version),
            "Delete" => version is null ? AccessOutcome.Refused : store.Delete(source, provider, key, version),
            _        => AccessOutcome.Refused
        };
        return new JsonObject { ["Ok"] = outcome == AccessOutcome.OK, ["Outcome"] = outcome.ToString() };
    }

    private JsonObject Refuse(string why)
    {
        Refused?.Invoke(why);
        return new JsonObject { ["Ok"] = false, ["Outcome"] = AccessOutcome.NotAuthorised.ToString(), ["Why"] = why };
    }

    public ValueTask DisposeAsync() => listener.DisposeAsync();
}

// The Provider's end: the functions a Provider calls, each a signed request on its link.
public sealed class AccessProvider : IAsyncDisposable
{
    private readonly RemoteLink link;
    private readonly TrustAnchorKey anchor;
    private readonly ECDsa key;
    private readonly string authority;
    private readonly Func<DateTimeOffset> now;

    private AccessProvider(RemoteLink link, TrustAnchorKey anchor, ECDsa key, string authority, Func<DateTimeOffset> now) =>
        (this.link, this.anchor, this.key, this.authority, this.now) = (link, anchor, key, authority, now);

    // protocol: the Provider's own, trusting the Controller's anchor; anchor and key: the
    // Provider's, with which it signs; authority: the issuer its credential names.
    public static async Task<AccessProvider> ConnectAsync(string host, int port, TrustProtocol protocol,
                                                         TrustAnchorKey anchor, ECDsa key, string authority = "provider",
                                                         Func<DateTimeOffset>? now = null) =>
        new(await RemoteLink.ConnectAsync(host, port, new TrustedLink(protocol)), anchor, key, authority, now ?? (() => DateTimeOffset.UtcNow));

    public Task<AccessOutcome> MPAI_AIFP_Access_Create(string source) =>
        Send(new JsonObject { ["Function"] = "Create", ["Source"] = source });

    public Task<AccessOutcome> MPAI_AIFP_Access_Put(string source, long version, string key, byte[] data) =>
        Send(new JsonObject { ["Function"] = "Put", ["Source"] = source, ["Version"] = version, ["Key"] = key, ["Data"] = Convert.ToBase64String(data) });

    public Task<AccessOutcome> MPAI_AIFP_Access_Delete(string source, long version, string key) =>
        Send(new JsonObject { ["Function"] = "Delete", ["Source"] = source, ["Version"] = version, ["Key"] = key });

    // The request as it will travel, signed: for a caller that wants to see it, or alter it.
    public string Signed(JsonObject request)
    {
        request["RequestID"] ??= Guid.NewGuid().ToString("N");
        return ExternalSigning.Sign(request.ToJsonString(), anchor, key, authority, now());
    }

    // Sends a request as given - signed by Signed, or otherwise.
    public async Task<AccessOutcome> SendAsIs(string request)
    {
        var answer = await link.RequestAsync(new JsonObject { ["Request"] = JsonNode.Parse(request) });
        return Enum.TryParse<AccessOutcome>((string?)answer["Outcome"], out var outcome) ? outcome : AccessOutcome.Refused;
    }

    private Task<AccessOutcome> Send(JsonObject request) => SendAsIs(Signed(request));

    public ValueTask DisposeAsync() => link.DisposeAsync();
}
