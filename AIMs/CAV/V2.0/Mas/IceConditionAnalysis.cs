using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Mas;

// ICE CONDITION ANALYSIS, STAGE 1 (CAV-ICA; M3237 3.6). The state of the road under
// the CAV, at each Spatial Attitude of the MAS: the friction it offers.
//
// From the tyres: what the CAV asks of them - its acceleration along its heading and
// across it, v^2 tan(steer) / Wheelbase - against what they give. When the brakes'
// ABS acts, a braked wheel slips as far as ABS lets it, or the motor loses traction,
// the tyres are at their limit: the friction is what was asked, over g and the share
// ABS keeps (AbsShare). When more is asked and given than the friction estimated,
// the friction is at least that - of the road just run, so forgotten GripMemory
// metres on (M3237, Step 7: grip proven before an icy stretch is not grip on it). A
// limit reached is forgotten Memory metres after the last. Until the tyres say otherwise, the Weather Data: below 0 degrees C
// with precipitation, or ice reported, the road may be icy (IcyFriction); with rain,
// wet (WetFriction); without either, the road is taken as dry (DryFriction), with
// little confidence.
//
// The Road State: the Surface Condition - the friction, ice present when it is below
// IceBelow, and the confidence - of the road under the CAV. The MAS has no map: the
// Segment is the road under the CAV (RoadID "UnderCAV"), for the AMS to place.
public sealed class IceConditionAnalysis(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    private const double G = 9.81;
    private readonly double wheelbase = EssJson.Setting(settings, "Wheelbase", 2.8), absShare = EssJson.Setting(settings, "AbsShare", 0.95),
                            memory = EssJson.Setting(settings, "Memory", 100), gripMemory = EssJson.Setting(settings, "GripMemory", 10), dry = EssJson.Setting(settings, "DryFriction", 0.9),
                            wet = EssJson.Setting(settings, "WetFriction", 0.6), icy = EssJson.Setting(settings, "IcyFriction", 0.15),
                            iceBelow = EssJson.Setting(settings, "IceBelow", 0.3), slipLimit = EssJson.Setting(settings, "SlipLimit", 0.1);
    private readonly string cav = settings.TryGetValue("CAVID", out var id) ? id : "CAV-1";

    private double? friction;                                 // from the tyres
    private double confidence, run, lastLimit, angle;
    private bool proven;                                      // the estimate is a lower bound, not a limit reached
    private (double East, double North)? last;
    private bool atLimit;                                     // since the last Spatial Attitude
    private bool wasAtLimit;                                  // at the last one
    private string? weather;                                  // "icy", "wet" or null
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-ICA runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (MasTypes.Weather, 1), (MasTypes.Attitude, 1), (MasTypes.Message, 1),
                                       (MasTypes.BrakeResponse, 1), (MasTypes.MotorResponse, 1), (MasTypes.WheelResponse, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            switch (port.DataType)
            {
                case MasTypes.Weather:
                    Weather(json);
                    await ports.WriteAsync(MasTypes.Weather, 1, m.Json);
                    continue;
                case MasTypes.Attitude:
                    await ports.WriteAsync(MasTypes.RoadState, 1, State(json).ToJsonString());
                    continue;
                case MasTypes.Message:
                    continue;                                 // what was asked: the tyres say what was given
                default:
                    Responded(port.DataType, json);
                    continue;
            }
        }
    }

    // What a Response says of the tyres.
    public void Responded(string dataType, JsonNode response)
    {
        switch (dataType)
        {
            case MasTypes.BrakeResponse:
                atLimit |= (bool?)response["ABSActivation"] == true || ((double?)response["WheelSlipRatio"] ?? 0) >= slipLimit;
                break;
            case MasTypes.MotorResponse:
                atLimit |= (string?)response["MotorState"] == "LossOfTraction";
                break;
            case MasTypes.WheelResponse:
                if ((double?)response["WheelResponse"]?["WheelAngle"] is { } degrees) angle = degrees * Math.PI / 180;
                break;
        }
    }

    // What the Weather Data says of the road.
    public void Weather(JsonNode data)
    {
        var w = data["WeatherData"];
        var t = (double?)w?["Temperature"]?["Value"];
        if (t is { } v && (string?)w?["Temperature"]?["Unit"] is "K") t = v - 273.15;
        var precipitation = new[] { "Rain", "Snow", "Sleet", "Hail" }.Any(p => ((double?)w?[p]?["Value"] ?? 0) > 0);
        weather = (bool?)w?["Ice"]?["Value"] == true || (t < 0 && precipitation) ? "icy"
                : ((double?)w?["Rain"]?["Value"] ?? 0) > 0 ? "wet"
                : null;
    }

    // THE ROAD STATE AT A SPATIAL ATTITUDE.
    public JsonObject State(JsonNode attitude)
    {
        var (east, north, heading, speed, ms) = MasTypes.Pose(attitude);
        if (last is { } l) run += Math.Sqrt(Math.Pow(east - l.East, 2) + Math.Pow(north - l.North, 2));
        last = (east, north);
        var a = EssJson.Vector(attitude["Position"]?["CartAccel"]) ?? (0, 0, 0);
        var along = a.X * Math.Cos(heading) + a.Y * Math.Sin(heading);
        var across = speed * speed * Math.Tan(angle) / wheelbase;
        var asked = Math.Sqrt(along * along + across * across) / G;

        // What the weather suggests, until the tyres say otherwise.
        var prior = weather switch { "icy" => icy, "wet" => wet, _ => dry };
        // At the limit: while the braking builds up (the brakes' lag) less is achieved
        // than the tyres give, so over consecutive Spatial Attitudes at the limit, the
        // most they gave.
        if (atLimit && asked > 0.03)
        {
            var limit = Math.Clamp(asked / absShare, 0.05, 1);
            if (wasAtLimit && friction is { } previous) limit = Math.Max(limit, previous);
            (friction, confidence, lastLimit, proven) = (limit, 0.9, run, false);
        }
        else if (asked > (friction ?? prior) && asked > 0.05) (friction, confidence, lastLimit, proven) = (Math.Min(1, asked), 0.7, run, true);
        else if (friction is not null && run - lastLimit > (proven ? gripMemory : memory)) friction = null;
        wasAtLimit = atLimit && asked > 0.03;
        atLimit = false;

        var estimate = friction ?? prior;
        var sure = friction is not null ? confidence : weather is not null ? 0.6 : 0.5;

        var rid = $"RDS{++count:D6}";
        return new JsonObject
        {
            ["Header"] = MasTypes.RoadState, ["RoadStateID"] = rid, ["RoadStateTime"] = EssJson.SimpleTime(rid + "-T", ms),
            ["CAVID"] = cav, ["Segment"] = new JsonObject { ["RoadID"] = "UnderCAV", ["SegmentNumber"] = 0 },
            ["RoadAttributes"] = new JsonObject(),
            ["SurfaceCondition"] = new JsonObject
            {
                ["IcePresence"] = estimate < iceBelow, ["FrictionCoefficientEstimate"] = Math.Round(estimate, 3), ["Confidence"] = sure
            },
            ["DescrMetadata"] = friction is not null ? "friction from the tyres at their limit or beyond the estimate"
                              : weather is not null ? $"the road may be {weather}: the Weather Data"
                              : "no evidence: the road taken as dry"
        };
    }
}
