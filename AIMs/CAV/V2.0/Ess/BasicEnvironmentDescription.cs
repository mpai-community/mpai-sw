using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Cav.Ess;

// BASIC ENVIRONMENT DESCRIPTION, STAGE 1 (CAV-BED-V2.0; M3229 12, M3221 3.4). One
// Environment Sensing Technology contributes, the visual, so the fusion is of one;
// what it does already is what the fusion of several does per technology:
//  - each object of the Basic Visual Scene Descriptors associated with the track
//    nearest to where that track was going, within a gate (the setting Gate, m);
//  - the track's position and velocity estimated by an alpha-beta filter, from one
//    instant to the next, relative to the ego;
//  - its existence confidence raised by each association and lowered by each miss;
//    a track missed DropAfterMisses times in a row dropped - but for one in the ego's
//    lane close ahead (within KeepNear m), which is kept while unseen (for KeepFor ms
//    at most, where KeepFor is set; by default until the ego reaches it): the camera loses what is near and large before it is gone (a detector
//    trained on photographs stops recognising a vehicle a few metres ahead), and what
//    was in front of the ego does not vanish because it is no longer recognised. An
//    unseen track moves by its own speed - the ego's added to its relative velocity
//    when last seen - less the ego's speed now, its own speed falling at
//    UnseenDeceleration m/s2: what is not seen may have stopped (M3237, Step 8: the
//    estimate lags, and a stopped vehicle's track drifted away at the lag until it
//    was dropped, and the ego moved off into it). A stopped vehicle stays where it
//    is, and is dropped when the ego reaches it;
//  - the Basic Environment Descriptors V2.0 on the ego frame, given out and returned
//    to the describer as its prior;
//  - where no descriptors come for Quiet milliseconds of the ego's time (1 s: longer
//    than a frame takes to be described, and than its MaxAge) - a camera
//    stopped - the tracks predicted and a miss counted for each, and the
//    descriptors given all the same: the Subsystem goes on, and what it no longer
//    senses fades from what it describes (M3221 3.7).
// Nothing is given out before the ego Spatial Attitude is known: it is the frame.
public sealed class BasicEnvironmentDescription(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    public const string Visual = "OSD-BVS-V1.5", Attitude = "OSD-OSA-V1.5", Weather = "CAV-WDT-V2.0", Full = "CAV-FED-V2.0",
                        Descriptors = "CAV-BED-V2.0";
    private const double Alpha = 0.5, Beta = 0.3;

    private sealed class Track
    {
        public required string Id;
        public required string Class;
        public double Score;
        public double X, Y, Vx, Vy, AccuracyX, AccuracyY;
        public double Speed;                                  // its own, along the ego's heading, when last seen
        public double Existence = 0.5;
        public int Misses;
        public long First, Last;
        public string? From, Object;
    }

    private readonly double gate = EssJson.Setting(settings, "Gate", 3);
    private readonly int dropAfter = (int)EssJson.Setting(settings, "DropAfterMisses", 5);
    private readonly long quiet = (long)EssJson.Setting(settings, "Quiet", 1000);
    private readonly double keepNear = EssJson.Setting(settings, "KeepNear", 15);
    private readonly long keepFor = (long)EssJson.Setting(settings, "KeepFor", 0);   // 0: no limit
    private readonly double unseenDeceleration = EssJson.Setting(settings, "UnseenDeceleration", 3);
    private readonly double laneHalf = EssJson.Setting(settings, "LaneHalfWidth", 1.75);
    private readonly List<Track> tracks = [];
    private JsonNode? ego, weather;
    private long? lastMs, lastGiven, lastDescribed;
    private long tracksMade, instances;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-BED runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Visual, 1), (Attitude, 1), (Weather, 1), (Full, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json);
            switch (port.DataType)
            {
                case Attitude:
                    ego = json;
                    // Descriptors silent for Quiet: given every 100 ms of the ego's time.
                    if (EssJson.Milliseconds(json!["SpatialAttitudeTime"]) is { } now && lastDescribed is { } described && now - described >= quiet
                        && (lastGiven is not { } given || now - given >= 100))
                    {
                        Fuse(null, now);
                        await Give(ports, now);
                    }
                    continue;
                case Weather: weather = json; continue;
                case Full: continue;                                 // other CAVs' objects: a later stage
            }
            var ms = EssJson.Milliseconds(json!["BVSDescriptorsSpaceTime"]?["Time"]) ?? ports.Now.ToUnixTimeMilliseconds();
            lastDescribed = ms;
            Fuse(json!, ms);
            await Give(ports, ms);
        }
    }

    private async Task Give(IAimPorts ports, long ms)
    {
        if (ego is null) return;
        lastGiven = ms;
        await ports.WriteAsync(Descriptors, 1, Descriptors_(ms).ToJsonString());
    }

    // What the descriptors say at ms; with none, only the prediction and the misses.
    private void Fuse(JsonNode? descriptors, long ms)
    {
        var dt = lastMs is { } l ? Math.Max(0.001, (ms - l) / 1000.0) : 0;
        lastMs = ms;
        var egoSpeed = EgoSpeed();
        foreach (var t in tracks)
        {
            if (t.Misses == 0) { t.X += t.Vx * dt; t.Y += t.Vy * dt; }
            else
            {
                // Unseen: its own speed, falling - it may have stopped - the ego's now.
                var next = Math.Max(0, t.Speed - unseenDeceleration * dt);
                t.X += ((t.Speed + next) / 2 - egoSpeed) * dt;
                t.Speed = next;
            }
        }

        var free = new HashSet<Track>(tracks);
        var id = descriptors?["BasicVisualSceneDescriptorsID"]?.GetValue<string>();
        foreach (var entry in descriptors?["BasicVisualSceneDescriptors"]?.AsArray() ?? [])
        {
            var obj = entry?["VObjectIDOrVObject"]?[0];
            var position = EssJson.Vector(entry?["VisualObjectSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPosition"]);
            var accuracy = EssJson.Vector(entry?["VisualObjectSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPositionAccuracy"]) ?? (1, 1, 1);
            if (position is not { } p) continue;
            var cls = obj?["BasicVisualObjectProperties"]?["BasicVisualObjectIdentifier"]?["InstanceIdentifier"]?.GetValue<string>() ?? "object";
            var score = (double?)obj?["BasicVisualObjectProperties"]?["BasicVisualObjectIdentifier"]?["InstanceIdentifierData"]?[0]?["LabelConfidenceLevel"] ?? 0.5;

            var track = free.Where(t => Distance(t, p) < gate + t.AccuracyX).OrderBy(t => Distance(t, p)).FirstOrDefault();
            if (track is null)
            {
                track = new Track { Id = $"T{++tracksMade:D4}", Class = cls, X = p.X, Y = p.Y, First = ms };
                tracks.Add(track);
            }
            else
            {
                free.Remove(track);
                double rx = p.X - track.X, ry = p.Y - track.Y;
                track.X += Alpha * rx; track.Y += Alpha * ry;
                if (dt > 0) { track.Vx += Beta * rx / dt; track.Vy += Beta * ry / dt; }
                track.Existence += (1 - track.Existence) * 0.3;
            }
            track.Class = cls; track.Score = score; track.Misses = 0; track.Last = ms;
            // Its own speed: not backwards - the filter lags while the ego brakes, and a
            // vehicle ahead in the lane does not come back at it.
            track.Speed = Math.Max(0, track.Vx + egoSpeed);
            track.AccuracyX = accuracy.X; track.AccuracyY = accuracy.Y;
            track.From = id; track.Object = obj?["BasicVisualObjectID"]?.GetValue<string>();
        }
        foreach (var t in free) { t.Misses++; if (!Kept(t, ms)) t.Existence *= 0.7; }
        tracks.RemoveAll(t => t.Misses >= dropAfter && !Kept(t, ms));
    }

    // In the ego's lane, ahead and near, and not unseen for too long.
    private bool Kept(Track t, long ms) => t.X > 0 && t.X < keepNear && Math.Abs(t.Y) < laneHalf && (keepFor <= 0 || ms - t.Last <= keepFor);

    // The ego's speed, from its Spatial Attitude.
    private double EgoSpeed() => EssJson.Vector(ego?["Position"]?["CartVelocity"]) is { } v ? Math.Sqrt(v.X * v.X + v.Y * v.Y) : 0;

    private static double Distance(Track t, (double X, double Y, double Z) p) => Math.Sqrt(Math.Pow(t.X - p.X, 2) + Math.Pow(t.Y - p.Y, 2));

    private JsonObject Descriptors_(long ms)
    {
        var id = $"BED{++instances:D6}";
        var objects = new JsonArray(tracks.Select(t => (JsonNode)new JsonObject
        {
            ["BasicEnvironmentObjectID"] = t.Id,
            ["InstanceIdentifier"] = EssJson.Identifier(t.Class, t.Score),
            ["SpatialAttitude"] = EssJson.Attitude($"{id}-{t.Id}", ms, (t.X, t.Y, 0), (t.AccuracyX, t.AccuracyY, 0.1), (t.Vx, t.Vy, 0)),
            ["ExistenceConfidence"] = Math.Round(t.Existence, 3),
            ["Motion"] = "Dynamic",
            ["Contributions"] = new JsonArray(new JsonObject { ["Technology"] = "Visual", ["SceneDescriptorsID"] = t.From, ["ObjectID"] = t.Object }),
            ["FirstObserved"] = EssJson.SimpleTime($"{id}-{t.Id}-F", t.First),
            ["LastObserved"] = EssJson.SimpleTime($"{id}-{t.Id}-L", t.Last)
        }).ToArray());
        var bed = new JsonObject
        {
            ["Header"] = Descriptors, ["BasicEnvironmentDescriptorsID"] = id,
            ["BasicEnvironmentDescriptorsTime"] = EssJson.SimpleTime(id + "-T", ms),
            ["EgoSpatialAttitude"] = ego!.DeepClone(),
            ["BasicEnvironmentObjectCount"] = tracks.Count,
            ["BasicEnvironmentObjects"] = objects
        };
        if (weather is not null) bed["WeatherData"] = weather.DeepClone();
        return bed;
    }
}
