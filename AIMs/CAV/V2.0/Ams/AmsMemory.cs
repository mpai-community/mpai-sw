using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.SharedStorage;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Ams;

// AMS MEMORY, STAGE 1 (CAV-AMM; M3233 3.8). Every decision the AMS takes - Route,
// Path, Trajectory, Alert, AMS-MAS Message - kept in the Private Storage of the
// Module, category AMSData, with the Full Environment Descriptors it was taken on
// (the latest before it); for each AMS-MAS Message, the AMS Data of that instant
// given out: the Route in force, the Path, the Trajectory commanded, the latest
// Alert since the last, and the Full Environment Descriptors.
public sealed class AmsMemory(string instanceId, IRuledStorage? storage) : IAimProcessor, IAimRunner
{
    private string? routeId;
    private JsonNode? path;
    private JsonNode? fed;
    private readonly List<JsonNode> alerts = [];
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-AMM runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Route, 1), (AmsTypes.Path, 1), (AmsTypes.Trajectory, 1), (AmsTypes.Alert, 1), (AmsTypes.Message, 1), (AmsTypes.Fed, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == AmsTypes.Fed) { fed = json; continue; }   // what decisions are taken on, not a decision
            Keep(port.DataType, json);
            switch (port.DataType)
            {
                case AmsTypes.Route: routeId = json["RouteID"]?.GetValue<string>(); continue;
                case AmsTypes.Path: path = json; continue;
                case AmsTypes.Alert: alerts.Add(json); continue;
                case AmsTypes.Trajectory: continue;
            }
            // An AMS-MAS Message: the AMS Data of its instant.
            var ms = AmsTypes.Ms(json["AMSMASMessageTime"]);
            var id = $"AMD{++count:D6}";
            var data = new JsonObject
            {
                ["Header"] = AmsTypes.Data, ["AMSDataID"] = id, ["AMSDataTime"] = EssJson.SimpleTime(id + "-T", ms)
            };
            if (routeId is not null) data["RouteID"] = routeId;
            if (path is not null) data["Path"] = path.DeepClone();
            if (json["AMSMessage"]?["Trajectory"] is { } trajectory) data["Trajectory"] = trajectory.DeepClone();
            if (alerts.Count > 0) data["Alert"] = alerts[^1].DeepClone();
            alerts.Clear();
            if (fed is not null) data["FED"] = fed.DeepClone();
            data["AMSMASMessage"] = json.DeepClone();
            await ports.WriteAsync(AmsTypes.Data, 1, data.ToJsonString());
        }
    }

    private long kept;
    // A decision and the Full Environment Descriptors it was taken on.
    private void Keep(string dataType, JsonNode json)
    {
        var record = new JsonObject { ["Decision"] = json.DeepClone(), ["FED"] = fed?.DeepClone() };
        // Read back by the User Agent: what the AMS decided can always be read (M3233 3.8).
        storage?.MPAI_AIFM_RuledStorage_Put($"decisions/{++kept:D8}-{dataType}", Encoding.UTF8.GetBytes(record.ToJsonString()), "AMSData",
                                            readers: [AIF.SharedStorage.RuledStore.UserAgent]);
    }
}
