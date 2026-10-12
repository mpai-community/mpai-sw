using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Cav.Map;

namespace Mpai.Cav.Ams;

// PATH SELECTION PLANNING, STAGE 1 (CAV-PSP; M3233 3.5). From the Route, the Path: the
// centreline of the CAV's lane - the middle one - along each segment and through
// each junction, as a Point of View every 5 m, its heading the direction of travel.
// Given to Motion Selection Planning as the Path Response of an Interaction, and as
// a Path. Stage 1 keeps the lane. WITH THE SETTING LaneChange (Stage 2: overtaking, M3253 4) the Path takes the
// lane to the left of the Route's line - and back - when the LaneChangePlanner says so: from the Full
// Environment Descriptors, a new Path from the ego's place on, the lane changed by a smooth step.
public sealed class PathSelectionPlanning(string instanceId, IReadOnlyDictionary<string, string>? settings = null) : IAimProcessor, IAimRunner
{
    public const double Spacing = 5;
    private readonly IReadOnlyDictionary<string, string> settings = settings ?? new Dictionary<string, string>();
    private readonly LaneChangePlanner planner = new(settings ?? new Dictionary<string, string>());
    private RoadMap? map;
    private RoutePath? line;
    private double lastS;
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-PSP runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Map, 1), (AmsTypes.Interaction, 1), (AmsTypes.Fed, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == AmsTypes.Map) { map = RoadMap.FromOfflineMapObject(json); continue; }
            if (port.DataType == AmsTypes.Fed)
            {
                if (planner.Enabled && line is not null && Overtake(json) is { } path) await Publish(ports, path, AmsTypes.Ms(json["FullEnvironmentDescriptorsTime"]));
                continue;                                          // Stage 1 keeps its lane: nothing else to change a Path for
            }
            if (json["RouteResponse"] is not { } route || map is null) continue;

            var segments = route["RouteSegments"]!.AsArray()
                .Select(s => map.Segments.First(g => g.From == (string)s!["WayPoint1ID"]! && g.To == (string)s["WayPoint2ID"]!)).ToList();
            line = new RoutePath(map, segments);
            lastS = 0;
            var ms = AmsTypes.Ms(route["RouteTime"]);
            await Publish(ports, MakePath(planner.Current, ms), ms);
        }
    }

    // The ego's place on the Route, and the lane change the planner decides on.
    private JsonObject? Overtake(JsonNode fed)
    {
        var (east, north, _, speed) = AmsTypes.Ego(fed["EgoSpatialAttitude"]!);
        lastS = line!.Project(east, north, lastS).S;
        var limit = (double?)fed["RoadAhead"]?[0]?["SpeedLimit"] ?? 13.9;
        var lanes = (int?)fed["RoadAhead"]?[0]?["Lanes"] ?? 1;
        var planned = EssJson.Setting(settings, "DesiredSpeed", limit);
        return planner.Update(fed, lastS, planned, lanes) is { } change ? MakePath(change, AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"])) : null;
    }

    // The Path: the Route's line, the lane change given or none, a Point of View every 5 m. Where the Path is
    // straight and the lane constant, the points that add nothing to the line are left out.
    private JsonObject MakePath(LaneChange? change, long ms)
    {
        var id = $"PAT{++count:D4}";
        var points = new JsonArray();
        var length = line!.Length;
        double? lastHeading = null; var lastKept = -1e9; var lastLegId = "";
        for (var s = 0.0; ; s += Spacing)
        {
            var at = Math.Min(s, length);
            var (east, north, heading, segment) = line.At(at);
            var moving = change is not null && at >= change.StartS - Spacing && at <= change.StartS + change.Length + Spacing;
            var last = s >= length;
            if (planner.Enabled && !moving && !last && at - lastKept < 200)
            {
                var turns = lastHeading is null || Math.Abs(heading - lastHeading.Value) > 1e-9 || segment.Id != lastLegId;
                var next = line.At(Math.Min(at + Spacing, length));
                var turnsNext = Math.Abs(next.Heading - heading) > 1e-9 || next.Segment.Id != segment.Id;
                if (!turns && !turnsNext) continue;
            }
            var offset = change?.OffsetAt(at) ?? 0;
            var h = heading + (change is null ? 0 : Math.Atan(change.SlopeAt(at)));
            points.Add(new JsonObject
            {
                ["PointOfView"] = new JsonObject
                {
                    ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = $"{id}-{points.Count}",
                    ["General"] = new JsonObject { ["CoordType"] = "Cartesian", ["ObjectType"] = "Generic", ["MediaType"] = "Visual" },
                    ["CartPosition"] = new JsonArray(Math.Round(east - Math.Sin(heading) * offset, 3), Math.Round(north + Math.Cos(heading) * offset, 3), 0.0),
                    ["Orientation"] = new JsonArray(0.0, 0.0, Math.Round(h * 180 / Math.PI, 3))
                }
            });
            (lastHeading, lastKept, lastLegId) = (heading, at, segment.Id);
            if (last) break;
        }
        return new JsonObject
        {
            ["Header"] = AmsTypes.Path, ["PathID"] = id, ["PathTime"] = EssJson.SimpleTime(id + "-T", ms),
            ["OfflineMapID"] = map!.Id, ["Path"] = points
        };
    }

    private static async Task Publish(IAimPorts ports, JsonObject path, long ms)
    {
        var id = (string)path["PathID"]!;
        var n = id[3..];
        await ports.WriteAsync(AmsTypes.Interaction, 1, new JsonObject
        {
            ["Header"] = AmsTypes.Interaction, ["InteractionID"] = $"INT-P{n}", ["InteractionTime"] = EssJson.SimpleTime($"INT-P{n}-T", ms),
            ["PathResponse"] = path.DeepClone()
        }.ToJsonString());
        await ports.WriteAsync(AmsTypes.Path, 1, path.ToJsonString());
    }
}
