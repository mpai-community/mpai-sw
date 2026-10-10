using System.Text.Json.Nodes;

using AIF.Channels;

namespace AIF.Controller;

// WHAT AN AIM HAS OF ACCESS (AIF V3.0, Basic API 4.11.3): reading, and nothing else -
// MPAI_AIFM_Access_Get, _List, _Version. An AIM is given this, never the store.
public interface IAccessReader
{
    AccessOutcome Get(string source, string key, out byte[] data);
    IReadOnlyList<string> List(string source, string prefix = "");
    long Version(string source);
}

// ACCESS FOR AN AIM ON AN AIM HOST: Access is the Controller's, so an AIM placed on
// a host reads it through the Controller, each call a request on the link the
// Controller opened to the host - admitted as that link is. The Controller answers
// only for a Module it placed there, and only reads.
public sealed class RemoteAccess(RemoteLink link, string module) : IAccessReader
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private JsonObject? Ask(JsonObject request)
    {
        request["Kind"] = "Access";
        request["Module"] = module;
        try { return link.RequestAsync(request).WaitAsync(Wait).GetAwaiter().GetResult(); }
        catch { return null; }                                          // the link gone, or too slow
    }

    public AccessOutcome Get(string source, string key, out byte[] data)
    {
        data = [];
        var reply = Ask(new JsonObject { ["Function"] = "Get", ["Source"] = source, ["Key"] = key });
        if (reply is null || !Enum.TryParse<AccessOutcome>((string?)reply["Outcome"], out var outcome)) return AccessOutcome.NotFound;
        if (outcome == AccessOutcome.OK) data = Convert.FromBase64String((string?)reply["Data"] ?? "");
        return outcome;
    }

    public IReadOnlyList<string> List(string source, string prefix = "") =>
        Ask(new JsonObject { ["Function"] = "List", ["Source"] = source, ["Prefix"] = prefix })?["Keys"] is JsonArray keys
            ? keys.Select(k => (string)k!).ToList()
            : [];

    public long Version(string source) =>
        Ask(new JsonObject { ["Function"] = "Version", ["Source"] = source })?["Version"]?.GetValue<long>() ?? -1;

    // The Controller's answer to such a request, from its Access (null: it has none).
    public static JsonObject Answer(IAccessReader? access, JsonObject request)
    {
        var source = (string?)request["Source"] ?? "";
        switch ((string?)request["Function"])
        {
            case "Get":
                if (access is null) return new JsonObject { ["Ok"] = false, ["Outcome"] = nameof(AccessOutcome.NotFound) };
                var outcome = access.Get(source, (string?)request["Key"] ?? "", out var data);
                var reply = new JsonObject { ["Ok"] = outcome == AccessOutcome.OK, ["Outcome"] = outcome.ToString() };
                if (outcome == AccessOutcome.OK) reply["Data"] = Convert.ToBase64String(data);
                return reply;
            case "List":
                return new JsonObject
                {
                    ["Ok"] = true,
                    ["Keys"] = new JsonArray((access?.List(source, (string?)request["Prefix"] ?? "") ?? []).Select(k => (JsonNode?)k).ToArray())
                };
            case "Version":
                return new JsonObject { ["Ok"] = true, ["Version"] = access?.Version(source) ?? -1 };
            default:
                return new JsonObject { ["Ok"] = false, ["Outcome"] = nameof(AccessOutcome.Refused), ["Error"] = "Access is only read" };
        }
    }
}
