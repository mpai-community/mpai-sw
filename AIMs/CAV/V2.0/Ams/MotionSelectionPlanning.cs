using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Ams;

// MOTION SELECTION PLANNING, STAGE 1 (CAV-MSP; M3233 3.6). For each Full Environment
// Descriptors instance, the Trajectory of the next Horizon seconds at Step: along the
// Path, by the Intelligent Driver Model - its desired speed the speed limit of the
// segment the CAV is on, following the object ahead in the CAV's lane at a time gap,
// never nearer than a minimum gap, stopping at the end of the Path as behind a
// vehicle stopped there. The object ahead predicted at its speed. Before a Path -
// the Route not yet executed (M3243 3.1) - the Path is where the CAV stands: it holds.
// Settings: DesiredSpeed (m/s; the limit where absent), TimeGap, MinimumGap,
// Acceleration, Deceleration.
//
// THE OBJECTS BEYOND THE ONE FOLLOWED (M3241): a vehicle reported by a Remote CAV,
// hidden from the CAV by the one it follows, lowers the acceleration too - at each
// point the lowest the model gives against each object ahead in the lane.
//
// AT A CORNER OF THE PATH (M3237, found in Step 4: a CAV that moves as a vehicle does
// cannot turn at the speed limit): where the Path turns by more than 20 degrees the
// desired speed is at most what a turn of CornerRadius metres allows at
// LateralAcceleration m/s2, reached at CornerDeceleration m/s2 before it and held
// through the turn.
public sealed class MotionSelectionPlanning(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    public const double Step = 0.1, Horizon = 6;
    private readonly double timeGap = EssJson.Setting(settings, "TimeGap", 1.5), minimumGap = EssJson.Setting(settings, "MinimumGap", 4),
                            acceleration = EssJson.Setting(settings, "Acceleration", 1.5), deceleration = EssJson.Setting(settings, "Deceleration", 2);
    private readonly double cornerRadius = EssJson.Setting(settings, "CornerRadius", 10), lateralAcceleration = EssJson.Setting(settings, "LateralAcceleration", 2),
                            cornerDeceleration = EssJson.Setting(settings, "CornerDeceleration", 1);
    private List<(double S, double East, double North, double Heading)>? path;
    private List<double> corners = [];
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-MSP runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Interaction, 1), (AmsTypes.Fed, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == AmsTypes.Interaction)
            {
                if (json["PathResponse"] is { } p) Follow(p);
                continue;
            }
            await ports.WriteAsync(AmsTypes.Trajectory, 1, Plan(json).ToJsonString());
        }
    }

    // The Path to follow, from a Path Response.
    public void Follow(JsonNode path)
    {
        this.path = Line(path);
        corners = [];
        for (var i = 0; i + 1 < this.path.Count; i++)
            if (Math.Abs(Math.IEEERemainder(this.path[i + 1].Heading - this.path[i].Heading, 2 * Math.PI)) > 20 * Math.PI / 180)
                corners.Add(this.path[i + 1].S);
    }

    // The desired speed at s: no more than the corners ahead, and the one being
    // turned, allow.
    private double CornerLimit(double s, double desired)
    {
        var turning = Math.Sqrt(lateralAcceleration * cornerRadius);
        foreach (var c in corners)
        {
            if (s > c + cornerRadius) continue;
            desired = Math.Min(desired, s >= c ? turning : Math.Sqrt(turning * turning + 2 * cornerDeceleration * (c - s)));
        }
        return desired;
    }

    // The Path as a line: each point with its distance along the Path.
    private static List<(double S, double East, double North, double Heading)> Line(JsonNode path)
    {
        var line = new List<(double, double, double, double)>();
        double s = 0; (double E, double N)? last = null;
        foreach (var p in path["Path"]!.AsArray())
        {
            var c = p!["PointOfView"]!["CartPosition"]!.AsArray();
            var (e, n) = ((double)c[0]!, (double)c[1]!);
            if (last is { } l) s += Math.Sqrt(Math.Pow(e - l.E, 2) + Math.Pow(n - l.N, 2));
            line.Add((s, e, n, (double)p["PointOfView"]!["Orientation"]![2]! * Math.PI / 180));
            last = (e, n);
        }
        return line;
    }

    private (double East, double North, double Heading) At(double s)
    {
        var p = path!;
        if (s >= p[^1].S) return (p[^1].East, p[^1].North, p[^1].Heading);
        var i = Math.Max(0, p.FindLastIndex(x => x.S <= s));
        var (a, b) = (p[i], p[Math.Min(i + 1, p.Count - 1)]);
        var f = b.S > a.S ? (s - a.S) / (b.S - a.S) : 0;
        return (a.East + f * (b.East - a.East), a.North + f * (b.North - a.North), b.Heading);
    }

    // Where the CAV is along the Path: its nearest point.
    private double Along(double east, double north)
    {
        var best = double.MaxValue; var at = 0.0;
        for (var i = 0; i + 1 < path!.Count; i++)
        {
            var (a, b) = (path[i], path[i + 1]);
            var len = b.S - a.S;
            if (len <= 0) continue;
            var f = Math.Clamp(((east - a.East) * (b.East - a.East) + (north - a.North) * (b.North - a.North)) / (len * len), 0, 1);
            var (x, y) = (a.East + f * (b.East - a.East), a.North + f * (b.North - a.North));
            var d = Math.Pow(east - x, 2) + Math.Pow(north - y, 2);
            if (d < best) { best = d; at = a.S + f * len; }
        }
        return at;
    }

    // The acceleration of the Intelligent Driver Model, behind something gap metres
    // ahead approached at closing m/s (none: gap infinite).
    public double Idm(double speed, double desired, double gap, double closing)
    {
        var free = 1 - Math.Pow(speed / Math.Max(0.1, desired), 4);
        var wanted = minimumGap + Math.Max(0, speed * timeGap + speed * closing / (2 * Math.Sqrt(acceleration * deceleration)));
        return acceleration * (free - Math.Pow(wanted / Math.Max(0.1, gap), 2));
    }

    public JsonObject Plan(JsonNode fed)
    {
        var ms = AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]);
        var (east, north, heading, speed) = AmsTypes.Ego(fed["EgoSpatialAttitude"]!);
        if (path is null) (path, corners) = ([(0, east, north, heading)], []);   // no Route yet: hold where it stands
        var desired = EssJson.Setting(settings, "DesiredSpeed", (double?)fed["RoadAhead"]?[0]?["SpeedLimit"] ?? 13.9);
        var s = Along(east, north);

        // The objects ahead in the CAV's lane: their gaps, and their speeds; the nearest
        // is the one followed.
        var ahead = new List<(double Gap, double Speed)>();
        foreach (var o in fed["FullEnvironmentObjects"]?.AsArray() ?? [])
        {
            if ((int?)o?["Placement"]?["Lane"] != 0) continue;
            var b = o!["BasicEnvironmentObject"]!;
            if ((double?)b["ExistenceConfidence"] < 0.5) continue;
            var p = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartPosition"]);
            var v = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartVelocity"]);
            if (p is null || p.Value.X <= 0) continue;
            ahead.Add((p.Value.X, Math.Max(0, speed + (v?.X ?? 0))));
        }
        ahead.Sort((x, y) => x.Gap.CompareTo(y.Gap));
        double? leadGap = ahead.Count > 0 ? ahead[0].Gap : null, leadSpeed = ahead.Count > 0 ? ahead[0].Speed : null;
        // Those beyond it (M3241: a vehicle a Remote CAV reports, hidden by the one
        // followed): each may only lower the acceleration - multi-anticipation.
        var beyond = ahead.Skip(1).Select(x => (Position: s + x.Gap, x.Speed)).ToList();
        var end = path![^1].S;

        var id = $"TRJ{++count:D6}";
        var points = new JsonArray();
        double t = 0, v0 = speed, pos = s, lead = leadGap is { } g ? s + g : double.MaxValue;
        for (var k = 0; k <= Horizon / Step; k++, t += Step)
        {
            // Behind the nearer of the object ahead and the end of the Path.
            var (gap, closing) = (end + minimumGap - pos, v0);
            if (lead - pos < gap) (gap, closing) = (lead - pos, v0 - (leadSpeed ?? 0));
            var a = Idm(v0, CornerLimit(pos, desired), gap, closing);
            foreach (var (position, lspeed) in beyond) a = Math.Min(a, Idm(v0, CornerLimit(pos, desired), position - pos, v0 - lspeed));
            a = Math.Clamp(a, -9, acceleration);
            var (e, n, h) = At(pos);
            var point = EssJson.Attitude($"{id}-{k}", ms + (long)Math.Round(t * 1000), (e, n, 0), (0.5, 0.5, 0.1), (v0 * Math.Cos(h), v0 * Math.Sin(h), 0));
            point["Position"]!["CartAccel"] = EssJson.Triple((a * Math.Cos(h), a * Math.Sin(h), 0));
            points.Add(new JsonObject { ["ExpectedSpaceTime"] = EssJson.SpaceTime($"{id}-{k}-ST", ms + (long)Math.Round(t * 1000), point) });
            var next = Math.Max(0, v0 + a * Step);
            pos += (v0 + next) / 2 * Step;
            v0 = next;
            if (leadSpeed is { } ls) lead += ls * Step;
            for (var j = 0; j < beyond.Count; j++) beyond[j] = (beyond[j].Position + beyond[j].Speed * Step, beyond[j].Speed);
        }
        return new JsonObject
        {
            ["Header"] = AmsTypes.Trajectory, ["TrajectoryID"] = id, ["TrajectoryTime"] = EssJson.SimpleTime(id + "-T", ms), ["Trajectory"] = points
        };
    }
}
