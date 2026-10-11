using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Mas;

// MAS RESPONSE ANALYSIS, STAGE 1 (CAV-MRA; M3237 3.7). The answer to each AMS-MAS
// Message, to the AMS: a MAS Message with the latest Spatial Attitude of the MAS - on
// its own frame, so that the AMS can give its Trajectories on it - and the latest
// Road State of ICA - so that the AMS knows what the CAV can do: a CAV on ice brakes
// less. The Responses of the devices are kept, the latest of each, for a later stage
// to judge what was executed against what was asked.
public sealed class MasResponseAnalysis(string instanceId) : IAimProcessor, IAimRunner
{
    private JsonNode? attitude, road;
    private readonly Dictionary<string, JsonNode> responses = new(StringComparer.Ordinal);
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-MRA runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (MasTypes.Message, 1), (MasTypes.Attitude, 1), (MasTypes.RoadState, 1),
                                       (MasTypes.BrakeResponse, 1), (MasTypes.MotorResponse, 1), (MasTypes.WheelResponse, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            switch (port.DataType)
            {
                case MasTypes.Message:
                    if (Answer(json) is { } answer) await ports.WriteAsync(MasTypes.Message, 1, answer.ToJsonString());
                    continue;
                case MasTypes.Attitude: Observe(json); continue;
                case MasTypes.RoadState: Road(json); continue;
                default: responses[port.DataType] = json; continue;
            }
        }
    }

    public void Observe(JsonNode attitude) => this.attitude = attitude;
    public void Road(JsonNode state) => road = state;

    // THE ANSWER TO AN AMS-MAS MESSAGE; none to one that is itself an answer.
    public JsonObject? Answer(JsonNode message)
    {
        if (message["AMSMessage"] is null) return null;
        var ms = MasTypes.Ms(attitude?["SpatialAttitudeTime"]);
        if (ms == 0) ms = MasTypes.Ms(message["AMSMASMessageTime"]);
        var id = $"MAS{++count:D6}";
        var mas = new JsonObject { ["CurrentTime"] = EssJson.SimpleTime(id + "-C", ms) };
        if (attitude is not null) mas["SpatialAttitude"] = attitude.DeepClone();
        if (road is not null) mas["RoadState"] = road.DeepClone();
        return new JsonObject
        {
            ["Header"] = MasTypes.Message, ["AMSMASMessageID"] = id, ["AMSMASMessageTime"] = EssJson.SimpleTime(id + "-T", ms),
            ["MASMessage"] = mas,
            ["DescrMetadata"] = $"The answer to {(string?)message["AMSMASMessageID"]}."
        };
    }
}
