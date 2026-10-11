using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Cav.Map;

namespace Mpai.Cav.Ams;

// PATH SELECTION PLANNING, STAGE 1 (CAV-PSP; M3233 3.5). From the Route, the Path: the
// centreline of the CAV's lane - the middle one - along each segment and through
// each junction, as a Point of View every 5 m, its heading the direction of travel.
// Given to Motion Selection Planning as the Path Response of an Interaction, and as
// a Path. Stage 1 keeps the lane; lane changes are a later stage.
public sealed class PathSelectionPlanning(string instanceId) : IAimProcessor, IAimRunner
{
    public const double Spacing = 5;
    private RoadMap? map;
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
            if (port.DataType == AmsTypes.Fed) continue;           // Stage 1 keeps its lane: nothing to change a Path for
            if (json["RouteResponse"] is not { } route || map is null) continue;

            var segments = route["RouteSegments"]!.AsArray()
                .Select(s => map.Segments.First(g => g.From == (string)s!["WayPoint1ID"]! && g.To == (string)s["WayPoint2ID"]!)).ToList();
            var line = new RoutePath(map, segments);
            var ms = AmsTypes.Ms(route["RouteTime"]);
            var id = $"PAT{++count:D4}";
            var points = new JsonArray();
            for (var s = 0.0; ; s += Spacing)
            {
                var (east, north, heading, _) = line.At(Math.Min(s, line.Length));
                points.Add(new JsonObject
                {
                    ["PointOfView"] = new JsonObject
                    {
                        ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = $"{id}-{points.Count}",
                        ["General"] = new JsonObject { ["CoordType"] = "Cartesian", ["ObjectType"] = "Generic", ["MediaType"] = "Visual" },
                        ["CartPosition"] = new JsonArray(Math.Round(east, 3), Math.Round(north, 3), 0.0),
                        ["Orientation"] = new JsonArray(0.0, 0.0, Math.Round(heading * 180 / Math.PI, 3))
                    }
                });
                if (s >= line.Length) break;
            }
            var path = new JsonObject
            {
                ["Header"] = AmsTypes.Path, ["PathID"] = id, ["PathTime"] = EssJson.SimpleTime(id + "-T", ms),
                ["OfflineMapID"] = map.Id, ["Path"] = points
            };
            await ports.WriteAsync(AmsTypes.Interaction, 1, new JsonObject
            {
                ["Header"] = AmsTypes.Interaction, ["InteractionID"] = $"INT-P{count:D4}", ["InteractionTime"] = EssJson.SimpleTime($"INT-P{count:D4}-T", ms),
                ["PathResponse"] = path.DeepClone()
            }.ToJsonString());
            await ports.WriteAsync(AmsTypes.Path, 1, path.ToJsonString());
        }
    }
}
