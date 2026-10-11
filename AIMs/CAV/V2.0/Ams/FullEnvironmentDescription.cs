using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Cav.Map;

namespace Mpai.Cav.Ams;

// FULL ENVIRONMENT DESCRIPTION, STAGE 1 (CAV-FED; M3233 3.3). The Basic Environment
// Objects of the ESS placed on the Offline Map: the CAV by map matching - its
// position and heading on the map's frame, the segment it is on, how far along it -
// and each object where it is, on a segment and in a lane, 0 the CAV's lane, +1 the
// lane to its left. The Full Environment Descriptors V2.0, at the instant of the
// Basic Environment Descriptors they were made from, one for each of them.
//
// THE REMOTE CAVs (M3241 3.3). A CAV given its identity (the setting CAVID) sends its
// Full Environment Descriptors - what it perceives itself - to the CAVs in range, as
// an Ego-Remote AMS Message Response, at most one each SendEvery ms. What another CAV
// sends is placed on the map as this CAV's own are: each object where the other CAV
// says it is - that CAV's Spatial Attitude and the object's place relative to it,
// on the map's frame both share - predicted by its velocity to this instant; marked
// Remote; left out where this CAV perceives it itself, or where it is this CAV; not
// used when older than MaxAge ms. The Controller verified who sent it.
public sealed class FullEnvironmentDescription(string instanceId, IReadOnlyDictionary<string, string>? settings = null) : IAimProcessor, IAimRunner
{
    public const string Remote = "CAV-ERA-V2.0";
    private readonly string? cav = settings?.TryGetValue("CAVID", out var id) == true ? id : null;
    private readonly double sendEvery = EssJson.Setting(settings ?? new Dictionary<string, string>(), "SendEvery", 200),
                            maxAge = EssJson.Setting(settings ?? new Dictionary<string, string>(), "MaxAge", 1000);
    private RoadMap? map;
    private long count, sent;
    private long? lastSent;
    private readonly Dictionary<string, JsonNode> reported = new(StringComparer.Ordinal);    // the latest of each Remote CAV

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-FED runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Map, 1), (Remote, 1), (AmsTypes.Bed, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == AmsTypes.Map) { map = RoadMap.FromOfflineMapObject(json); continue; }
            if (port.DataType == Remote) { if (Heard(json) is { } why) context.Report(why); continue; }
            if (map is null) { context.Report("Basic Environment Descriptors before the Offline Map: not placed."); continue; }
            var fed = Describe(json);
            await ports.WriteAsync(AmsTypes.Fed, 1, fed.ToJsonString());
            if (ToSend(fed) is { } message) await ports.WriteAsync(Remote, 1, message.ToJsonString());
        }
    }

    // The Offline Map, as a Basic Offline Map Object.
    public void Know(JsonNode offlineMapObject) => map = RoadMap.FromOfflineMapObject(offlineMapObject);

    // AN EGO-REMOTE AMS MESSAGE: a Response carrying another CAV's Full Environment
    // Descriptors is kept, the latest of each CAV. Null where it is; else why not.
    public string? Heard(JsonNode message)
    {
        if (message["Message"]?["RemoteFED"] is not JsonObject fed || (string?)message["Message"]?["RemoteCAVID"] is not { } from)
            return $"{(string?)message["EgoRemoteAMSMessageID"]}: not a Response with Full Environment Descriptors - not used in Stage 1.";
        if (from == cav) return null;
        if (!reported.TryGetValue(from, out var known) || AmsTypes.Ms(known["FullEnvironmentDescriptorsTime"]) <= AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]))
            reported[from] = fed;
        return null;
    }

    // What this CAV sends, where it has an identity and it is time: its own perception.
    public JsonObject? ToSend(JsonObject fed)
    {
        var ms = AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]);
        if (cav is null || ms - lastSent < sendEvery) return null;          // lastSent null: not yet
        lastSent = ms;
        var own = fed.DeepClone().AsObject();
        var objects = new JsonArray(own["FullEnvironmentObjects"]!.AsArray().Where(o => (string?)o?["Source"] == "Ego").Select(o => o!.DeepClone()).ToArray());
        own["FullEnvironmentObjects"] = objects;
        own["FullEnvironmentObjectCount"] = objects.Count;
        var id = $"ERA{++sent:D6}";
        return new JsonObject
        {
            ["Header"] = Remote, ["EgoRemoteAMSMessageID"] = $"{cav}-{id}",
            ["EgoRemoteAMSMessageTime"] = EssJson.SimpleTime($"{cav}-{id}-T", ms), ["CAVIdentifier"] = cav,
            ["Message"] = new JsonObject { ["RemoteCAVID"] = cav, ["RemoteFED"] = own }
        };
    }

    public JsonObject Describe(JsonNode bed)
    {
        var id = $"FED{++count:D6}";
        var attitude = bed["EgoSpatialAttitude"]!;
        var (east, north, heading, _) = AmsTypes.Ego(attitude);
        var ego = AmsTypes.Match(map!, east, north, heading);
        var ms = AmsTypes.Ms(bed["BasicEnvironmentDescriptorsTime"]);

        var objects = new JsonArray();
        var seen = new List<(double X, double Y)>();
        foreach (var o in bed["BasicEnvironmentObjects"]?.AsArray() ?? [])
        {
            var p = EssJson.Vector(o?["SpatialAttitude"]?["Position"]?["CartPosition"]);
            if (p is null) continue;
            seen.Add((p.Value.X, p.Value.Y));
            // Ahead X and left Y of the CAV, turned to the map's frame.
            var (x, y) = (east + p.Value.X * Math.Cos(heading) - p.Value.Y * Math.Sin(heading), north + p.Value.X * Math.Sin(heading) + p.Value.Y * Math.Cos(heading));
            var placed = AmsTypes.Match(map!, x, y, heading);
            var entry = new JsonObject { ["BasicEnvironmentObject"] = o!.DeepClone(), ["Source"] = "Ego" };
            if (placed is { } q) entry["Placement"] = AmsTypes.Placement(q.Segment, q.Along, AmsTypes.LaneOf(q.Left - (ego?.Left ?? 0)));
            objects.Add(entry);
        }
        foreach (var entry in FromRemote(ms, east, north, heading, attitude, ego?.Left ?? 0, seen)) objects.Add(entry);

        var fed = new JsonObject
        {
            ["Header"] = AmsTypes.Fed, ["FullEnvironmentDescriptorsID"] = id,
            ["FullEnvironmentDescriptorsTime"] = bed["BasicEnvironmentDescriptorsTime"]!.DeepClone(),
            ["OfflineMapID"] = map!.Id,
            ["EgoSpatialAttitude"] = attitude.DeepClone()
        };
        if (ego is { } e)
        {
            fed["EgoPlacement"] = AmsTypes.Placement(e.Segment, e.Along, 0);
            fed["RoadAhead"] = new JsonArray(new JsonObject
            {
                ["SegmentID"] = e.Segment.Id, ["Length"] = Math.Round(map.Length(e.Segment), 3), ["SpeedLimit"] = e.Segment.SpeedLimit, ["Lanes"] = e.Segment.Lanes
            });
        }
        fed["FullEnvironmentObjectCount"] = objects.Count;
        fed["FullEnvironmentObjects"] = objects;
        if (bed["WeatherData"] is { } w) fed["WeatherData"] = w.DeepClone();
        return fed;
    }

    // WHAT OTHER CAVs PERCEIVE, placed as this CAV's own: the objects each reports -
    // and itself - where this CAV does not perceive something already, nor is.
    private IEnumerable<JsonObject> FromRemote(long ms, double east, double north, double heading, JsonNode attitude, double egoLeft, List<(double X, double Y)> seen)
    {
        var own = EssJson.Vector(attitude["Position"]?["CartVelocity"]) ?? (0, 0, 0);
        foreach (var (from, fed) in reported)
        {
            var age = ms - AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]);
            if (age > maxAge || age < -maxAge) continue;
            var dt = age / 1000.0;
            var remote = fed["EgoSpatialAttitude"]!;
            var (re, rn, rh, _) = AmsTypes.Ego(remote);
            var rv = EssJson.Vector(remote["Position"]?["CartVelocity"]) ?? (0, 0, 0);
            var reports = new List<(string Id, JsonNode Object, (double X, double Y) World, (double X, double Y) Velocity)>
            {
                (from, remote, (re, rn), (rv.X, rv.Y))                                          // the Remote CAV itself
            };
            foreach (var o in fed["FullEnvironmentObjects"]?.AsArray() ?? [])
            {
                var b = o?["BasicEnvironmentObject"];
                var p = EssJson.Vector(b?["SpatialAttitude"]?["Position"]?["CartPosition"]);
                if (b is null || p is null || (string?)o!["Source"] != "Ego") continue;
                var v = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartVelocity"]) ?? (0, 0, 0);
                reports.Add(((string?)b["BasicEnvironmentObjectID"] ?? "object", b,
                    (re + p.Value.X * Math.Cos(rh) - p.Value.Y * Math.Sin(rh), rn + p.Value.X * Math.Sin(rh) + p.Value.Y * Math.Cos(rh)),
                    (rv.X + v.X * Math.Cos(rh) - v.Y * Math.Sin(rh), rv.Y + v.X * Math.Sin(rh) + v.Y * Math.Cos(rh))));
            }
            foreach (var (objectId, source, world, velocity) in reports)
            {
                // Predicted to this instant; relative to this CAV.
                var (wx, wy) = (world.X + velocity.X * dt, world.Y + velocity.Y * dt);
                var (dx, dy) = (wx - east, wy - north);
                var (x, y) = (dx * Math.Cos(heading) + dy * Math.Sin(heading), -dx * Math.Sin(heading) + dy * Math.Cos(heading));
                if (Math.Abs(x) < 3 && Math.Abs(y) < 2) continue;                                // this CAV
                if (seen.Any(s => Math.Abs(s.X - x) < 3 && Math.Abs(s.Y - y) < 2)) continue;     // perceived here too
                var (rvx, rvy) = (velocity.X - own.X, velocity.Y - own.Y);
                var relative = (rvx * Math.Cos(heading) + rvy * Math.Sin(heading), -rvx * Math.Sin(heading) + rvy * Math.Cos(heading), 0.0);
                var reportedId = $"{from}/{objectId}";
                var entry = new JsonObject
                {
                    ["BasicEnvironmentObject"] = new JsonObject
                    {
                        ["BasicEnvironmentObjectID"] = reportedId,
                        ["InstanceIdentifier"] = source["InstanceIdentifier"]?.DeepClone() ?? EssJson.Identifier("car", 1),
                        ["SpatialAttitude"] = EssJson.Attitude($"{reportedId}-{ms}", ms, (x, y, 0), (1, 1, 1), relative),
                        ["ExistenceConfidence"] = (double?)source["ExistenceConfidence"] ?? 1.0, ["Motion"] = "Dynamic",
                        ["Contributions"] = new JsonArray(new JsonObject { ["Technology"] = "Visual" })
                    },
                    ["Source"] = "Remote"
                };
                if (AmsTypes.Match(map!, wx, wy, heading) is { } q) entry["Placement"] = AmsTypes.Placement(q.Segment, q.Along, AmsTypes.LaneOf(q.Left - egoLeft));
                yield return entry;
            }
        }
    }
}
