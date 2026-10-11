using System.Text.Json.Nodes;

namespace Mpai.Cav.Map;

// THE OFFLINE MAP OF THE CAV (M3233 3.1), as the AMS reads it and the simulation
// makes it: way points at junctions, and
// road segments between them, each with its direction of travel, its lanes and its
// speed limit; on a local frame of east and north metres from an origin, whose
// geodetic coordinates the map states. Made from a seed, as the drives are.
// A way point; some are places with a name, which the passenger may ask for (M3243).
public sealed record WayPoint(string Id, double East, double North, string? Name = null);
public sealed record RoadSegment(string Id, string From, string To, double SpeedLimit, int Lanes);

public sealed class RoadMap
{
    public const double LaneWidth = 3.5;

    public string Id { get; }
    public double OriginLat { get; }
    public double OriginLon { get; }
    public IReadOnlyList<WayPoint> WayPoints { get; }
    public IReadOnlyList<RoadSegment> Segments { get; }
    private readonly Dictionary<string, WayPoint> points;

    public RoadMap(string id, double originLat, double originLon, IReadOnlyList<WayPoint> wayPoints, IReadOnlyList<RoadSegment> segments)
    {
        (Id, OriginLat, OriginLon, WayPoints, Segments) = (id, originLat, originLon, wayPoints, segments);
        points = wayPoints.ToDictionary(w => w.Id);
    }

    public WayPoint Point(string id) => points[id];

    // The same map, some of its way points named.
    public RoadMap Named(IReadOnlyDictionary<string, string> names) =>
        new(Id, OriginLat, OriginLon, WayPoints.Select(w => names.TryGetValue(w.Id, out var n) ? w with { Name = n } : w).ToList(), Segments);

    public double Length(RoadSegment s)
    {
        var (a, b) = (points[s.From], points[s.To]);
        return Math.Sqrt(Math.Pow(b.East - a.East, 2) + Math.Pow(b.North - a.North, 2));
    }

    // A grid of columns x rows way points, spacing metres apart, from the origin of
    // the drives (Turin); a segment each way between neighbours, three lanes each
    // way, its speed limit 50 or 70 km/h as the seed decides.
    public static RoadMap Grid(int seed, int columns = 3, int rows = 3, double spacing = 250)
    {
        var random = new Random(seed);
        var wayPoints = new List<WayPoint>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                wayPoints.Add(new WayPoint($"W{c}{r}", c * spacing, r * spacing));
        var segments = new List<RoadSegment>();
        void Road(string a, string b)
        {
            var limit = random.NextDouble() < 0.5 ? 50 / 3.6 : 70 / 3.6;
            segments.Add(new RoadSegment($"S{a}{b}", a, b, Math.Round(limit, 3), 3));
            segments.Add(new RoadSegment($"S{b}{a}", b, a, Math.Round(limit, 3), 3));
        }
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                if (c + 1 < columns) Road($"W{c}{r}", $"W{c + 1}{r}");
                if (r + 1 < rows) Road($"W{c}{r}", $"W{c}{r + 1}");
            }
        return new RoadMap($"MAP-seed{seed}", 45.0703, 7.6869, wayPoints, segments);
    }

    // THE ROUTE OF LEAST TIME between two way points: A* on the segments, each
    // costing its length over its speed limit; the heuristic the straight line at the
    // highest limit. Null where there is none.
    public IReadOnlyList<RoadSegment>? FastestRoute(string from, string to)
    {
        var fastest = Segments.Max(s => s.SpeedLimit);
        double H(string w) => Math.Sqrt(Math.Pow(points[w].East - points[to].East, 2) + Math.Pow(points[w].North - points[to].North, 2)) / fastest;
        var cost = new Dictionary<string, double> { [from] = 0 };
        var via = new Dictionary<string, RoadSegment>();
        var open = new PriorityQueue<string, double>();
        open.Enqueue(from, H(from));
        var closed = new HashSet<string>();
        while (open.TryDequeue(out var w, out _))
        {
            if (w == to) break;
            if (!closed.Add(w)) continue;
            foreach (var s in Segments.Where(s => s.From == w))
            {
                var c = cost[w] + Length(s) / s.SpeedLimit;
                if (cost.TryGetValue(s.To, out var known) && known <= c) continue;
                cost[s.To] = c; via[s.To] = s;
                open.Enqueue(s.To, c + H(s.To));
            }
        }
        if (!via.ContainsKey(to) && from != to) return null;
        var route = new List<RoadSegment>();
        for (var w = to; w != from; w = via[w].From) route.Insert(0, via[w]);
        return route;
    }

    public double TimeOf(IEnumerable<RoadSegment> route) => route.Sum(s => Length(s) / s.SpeedLimit);

    // Geodetic coordinates of a point of the map's frame.
    public (double Lat, double Lon) Geodetic(double east, double north)
    {
        const double earth = 6_371_000;
        return (OriginLat + north / earth * 180 / Math.PI, OriginLon + east / (earth * Math.Cos(OriginLat * Math.PI / 180)) * 180 / Math.PI);
    }

    // THE MAP AS GEOJSON, a format the Offline Map Qualifier admits (TFA): each way
    // point a Point, with its Name where it is a place; each segment a LineString
    // from its first way point to its second, with its speed limit and lanes; the
    // map's identifier and origin as members of the collection.
    public JsonObject ToGeoJson()
    {
        JsonArray Position(WayPoint w) { var (lat, lon) = Geodetic(w.East, w.North); return new JsonArray(Math.Round(lon, 8), Math.Round(lat, 8)); }
        var features = new JsonArray();
        foreach (var w in WayPoints)
            features.Add(new JsonObject
            {
                ["type"] = "Feature", ["geometry"] = new JsonObject { ["type"] = "Point", ["coordinates"] = Position(w) },
                ["properties"] = w.Name is null ? new JsonObject { ["WayPointID"] = w.Id } : new JsonObject { ["WayPointID"] = w.Id, ["Name"] = w.Name }
            });
        foreach (var s in Segments)
            features.Add(new JsonObject
            {
                ["type"] = "Feature",
                ["geometry"] = new JsonObject { ["type"] = "LineString", ["coordinates"] = new JsonArray(Position(points[s.From]), Position(points[s.To])) },
                ["properties"] = new JsonObject { ["SegmentID"] = s.Id, ["From"] = s.From, ["To"] = s.To, ["SpeedLimit"] = s.SpeedLimit, ["Lanes"] = s.Lanes, ["LaneWidth"] = LaneWidth }
            });
        return new JsonObject
        {
            ["type"] = "FeatureCollection", ["MapID"] = Id, ["Origin"] = new JsonObject { ["Lat"] = OriginLat, ["Lon"] = OriginLon },
            ["features"] = features
        };
    }

    public static RoadMap FromGeoJson(JsonNode map)
    {
        var (lat0, lon0) = ((double)map["Origin"]!["Lat"]!, (double)map["Origin"]!["Lon"]!);
        const double earth = 6_371_000;
        var features = map["features"]!.AsArray();
        var wayPoints = features.Where(f => (string)f!["geometry"]!["type"]! == "Point").Select(f =>
        {
            var c = f!["geometry"]!["coordinates"]!.AsArray();
            var (lon, lat) = ((double)c[0]!, (double)c[1]!);
            return new WayPoint((string)f["properties"]!["WayPointID"]!,
                Math.Round((lon - lon0) * Math.PI / 180 * earth * Math.Cos(lat0 * Math.PI / 180), 3), Math.Round((lat - lat0) * Math.PI / 180 * earth, 3),
                (string?)f["properties"]!["Name"]);
        }).ToList();
        var segments = features.Where(f => (string)f!["geometry"]!["type"]! == "LineString").Select(f =>
        {
            var p = f!["properties"]!;
            return new RoadSegment((string)p["SegmentID"]!, (string)p["From"]!, (string)p["To"]!, (double)p["SpeedLimit"]!, (int)p["Lanes"]!);
        }).ToList();
        return new RoadMap((string)map["MapID"]!, lat0, lon0, wayPoints, segments);
    }

    // The map as a Basic Offline Map Object: its GeoJSON as the data, the Qualifier
    // saying so.
    public string ToOfflineMapObject(long ms) => new JsonObject
    {
        ["Header"] = "OSD-BOO-V1.5", ["BasicOfflineMapObjectID"] = Id,
        ["BasicOfflineMapObjectSpaceTime"] = new JsonObject
        {
            ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = Id + "-ST",
            ["Time"] = new JsonObject
            {
                ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = Id + "-T",
                ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
            }
        },
        ["BasicOfflineMapData"] = new JsonArray(new JsonObject { ["Data"] = ToGeoJson().ToJsonString() }),
        ["BasicOfflineMapDataQualifier"] = new JsonObject
        {
            ["Header"] = "TFA-OMQ-V1.5", ["OLMapQualifierID"] = Id + "-Q", ["SubTypes"] = new JsonObject(),
            ["Formats"] = new JsonObject { ["ContentFormat"] = "GeoJSON" }
        }
    }.ToJsonString();

    public static RoadMap FromOfflineMapObject(JsonNode map) =>
        FromGeoJson(JsonNode.Parse((string)map["BasicOfflineMapData"]![0]!["Data"]!)!);
}

// A ROUTE AS A LINE TO FOLLOW: its way points joined, a position and a heading at
// each distance along it.
public sealed class RoutePath
{
    private readonly List<(double S, double East, double North, double Heading, RoadSegment Segment)> legs = [];
    public double Length { get; }
    public IReadOnlyList<RoadSegment> Segments { get; }

    public RoutePath(RoadMap map, IReadOnlyList<RoadSegment> route)
    {
        Segments = route;
        double s = 0;
        foreach (var seg in route)
        {
            var (a, b) = (map.Point(seg.From), map.Point(seg.To));
            legs.Add((s, a.East, a.North, Math.Atan2(b.North - a.North, b.East - a.East), seg));
            s += map.Length(seg);
        }
        Length = s;
    }

    // Where the line is at s (clamped to it), its heading (radians, anticlockwise
    // from east), and the segment it is on.
    public (double East, double North, double Heading, RoadSegment Segment) At(double s)
    {
        s = Math.Clamp(s, 0, Length);
        var leg = legs.Last(l => l.S <= s);
        var along = s - leg.S;
        return (leg.East + along * Math.Cos(leg.Heading), leg.North + along * Math.Sin(leg.Heading), leg.Heading, leg.Segment);
    }

    // A POINT PLACED ON THE LINE: the distance along it of the nearest point, and how
    // far to its left (positive) or right the point is. Near is the distance the
    // point was last placed at: of the legs within 30 m of it the nearest is taken,
    // so that where the line doubles back a point is not placed on the other side.
    public (double S, double Offset) Project(double east, double north, double near)
    {
        (double S, double Offset, double Distance)? best = null;
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var length = (i + 1 < legs.Count ? legs[i + 1].S : Length) - leg.S;
            if (leg.S > near + 30 || leg.S + length < near - 30) continue;
            var (dx, dy) = (east - leg.East, north - leg.North);
            var (cos, sin) = (Math.Cos(leg.Heading), Math.Sin(leg.Heading));
            var along = Math.Clamp(dx * cos + dy * sin, 0, length);
            var offset = -dx * sin + dy * cos;
            var (px, py) = (leg.East + along * cos, leg.North + along * sin);
            var distance = Math.Sqrt((east - px) * (east - px) + (north - py) * (north - py));
            if (best is null || distance < best.Value.Distance) best = (leg.S + along, offset, distance);
        }
        return best is { } b ? (b.S, b.Offset) : (near, 0);
    }
}
