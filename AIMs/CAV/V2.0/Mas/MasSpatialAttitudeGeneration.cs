using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Mas;

// MAS SPATIAL ATTITUDE GENERATION, STAGE 1 (CAV-MSA; M3237 3.5). The Spatial
// Attitude of the CAV from its Spatial Data, by dead reckoning: at each Spatial
// Data, the distance run since the last from the odometer; the heading turned over
// it by the gyroscope, at the mean of its last two yaw rates; the position moved
// along the mean heading. Velocity from the speedometer, acceleration from the
// accelerometer, pitch and roll from the inclinometers. The motion sensors only (the
// author): what the steered wheels report is not how the CAV moved.
//
// The frame is the MAS's own: east and north metres from where the CAV was at
// Start, its heading then the setting InitialHeading (degrees anticlockwise from
// east) - Spatial Data has no sensor of absolute heading. What the MAS cannot know -
// an odometer that reads long, the bias of the gyroscope - is
// in the accuracy it states: BaseAccuracy metres, grown by DistanceAccuracy of the
// distance run. The ESS's Spatial Attitude Generation anchors it with GNSS.
public sealed class MasSpatialAttitudeGeneration(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    private readonly double baseAccuracy = EssJson.Setting(settings, "BaseAccuracy", 0.1),
                            distanceAccuracy = EssJson.Setting(settings, "DistanceAccuracy", 0.03);
    private double heading = EssJson.Setting(settings, "InitialHeading", 0) * Math.PI / 180;
    private double east, north, run;
    private double? odometer;
    private (double Rate, long Ms)? lastYaw;                  // degrees/second
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-MSA runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (MasTypes.SpatialData, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (Attitude(json) is { } attitude) await ports.WriteAsync(MasTypes.Attitude, 1, attitude.ToJsonString());
        }
    }

    // THE SPATIAL ATTITUDE AT A SPATIAL DATA; null where it has no odometer reading.
    public JsonObject? Attitude(JsonNode spatialData)
    {
        var data = spatialData["SpatialData"];
        if (data?["OdometerData"] is not JsonValue o || !o.TryGetValue<double>(out var reading)) return null;
        var ms = MasTypes.Ms(spatialData["SpaceTime"]);
        var ds = odometer is { } last ? Math.Max(0, reading - last) : 0;
        odometer = reading;

        var rate = (double?)data["GyroscopeData"]?["YawRate"] ?? 0;
        var turned = lastYaw is { } y && ms > y.Ms ? (rate + y.Rate) / 2 * Math.PI / 180 * ((ms - y.Ms) / 1000.0) : 0;
        lastYaw = (rate, ms);
        var mid = heading + turned / 2;
        (east, north) = (east + ds * Math.Cos(mid), north + ds * Math.Sin(mid));
        heading = Math.IEEERemainder(heading + turned, 2 * Math.PI);
        run += ds;

        var speed = (double?)data["SpeedometerData"] ?? 0;
        var acceleration = (double?)data["AccelerometerData"] ?? 0;
        var pitch = (double?)data["InclinometerData"]?["LongitudinalInclination"] ?? 0;
        var roll = (double?)data["InclinometerData"]?["LateralInclination"] ?? 0;
        var accuracy = baseAccuracy + distanceAccuracy * run;
        var id = $"MSA{++count:D6}";
        var attitude = EssJson.Attitude(id, ms, (east, north, 0), (accuracy, accuracy, accuracy),
            (speed * Math.Cos(heading), speed * Math.Sin(heading), 0),
            new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = id + "-O", ["Orientation"] = new JsonArray(Math.Round(roll, 3), Math.Round(pitch, 3), Math.Round(heading * 180 / Math.PI, 3)) });
        attitude["Position"]!["CartAccel"] = EssJson.Triple((acceleration * Math.Cos(heading), acceleration * Math.Sin(heading), 0));
        return attitude;
    }
}
