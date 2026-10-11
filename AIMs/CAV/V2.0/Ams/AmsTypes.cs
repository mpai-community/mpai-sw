using System.Text.Json.Nodes;

using Mpai.Cav.Ess;
using Mpai.Cav.Map;

namespace Mpai.Cav.Ams;

// WHAT THE AIMs OF THE AMS SHARE: the Data Types they exchange, and where a point is
// on the Offline Map.
public static class AmsTypes
{
    public const string Bed = "CAV-BED-V2.0", Fed = "CAV-FED-V2.0", Map = "OSD-BOO-V1.5", Hci = "CAV-AHM-V2.0", Interaction = "CAV-INT-V2.0",
                        Route = "CAV-RTE-V2.0", Path = "OSD-PAT-V1.5", Trajectory = "OSD-TRJ-V1.5", Alert = "CAV-ALT-V2.0",
                        Message = "CAV-AMM-V2.0", Data = "CAV-AMD-V2.0";

    public static long Ms(JsonNode? simpleTime) => EssJson.Milliseconds(simpleTime) ?? 0;

    // A point on the map: the segment it is on, how far along it, and how far to the
    // left of its centreline (the middle lane's), among segments heading within 60°
    // of heading; null where none is within 12 m.
    public static (RoadSegment Segment, double Along, double Left)? Match(RoadMap map, double east, double north, double? heading)
    {
        (RoadSegment, double, double)? best = null;
        var bestDistance = 12.0;
        foreach (var s in map.Segments)
        {
            var (a, b) = (map.Point(s.From), map.Point(s.To));
            var length = map.Length(s);
            var (ux, uy) = ((b.East - a.East) / length, (b.North - a.North) / length);
            if (heading is { } h && Math.Cos(h - Math.Atan2(uy, ux)) < 0.5) continue;
            var along = Math.Clamp((east - a.East) * ux + (north - a.North) * uy, 0, length);
            var left = -(east - a.East) * uy + (north - a.North) * ux;
            var (px, py) = (a.East + along * ux, a.North + along * uy);
            var distance = Math.Sqrt(Math.Pow(east - px, 2) + Math.Pow(north - py, 2));
            if (distance < bestDistance) { bestDistance = distance; best = (s, along, left); }
        }
        return best;
    }

    public static JsonObject Placement(RoadSegment segment, double along, int? lane) => lane is { } l
        ? new JsonObject { ["SegmentID"] = segment.Id, ["Lane"] = l, ["Along"] = Math.Round(along, 3) }
        : new JsonObject { ["SegmentID"] = segment.Id, ["Along"] = Math.Round(along, 3) };

    public static int LaneOf(double left) => (int)Math.Round(left / RoadMap.LaneWidth);

    // The ego's position and heading (radians) on the frame of its Spatial Attitude.
    public static (double East, double North, double Heading, double Speed) Ego(JsonNode attitude)
    {
        var p = EssJson.Vector(attitude["Position"]?["CartPosition"]) ?? (0, 0, 0);
        var v = EssJson.Vector(attitude["Position"]?["CartVelocity"]) ?? (0, 0, 0);
        var yaw = EssJson.Vector(attitude["Orientation"]?["Orientation"])?.Z ?? 0;
        return (p.X, p.Y, yaw * Math.PI / 180, Math.Sqrt(v.X * v.X + v.Y * v.Y));
    }
}
