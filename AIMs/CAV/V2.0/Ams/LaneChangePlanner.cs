using System.Text.Json.Nodes;

using Mpai.Cav.Ess;
using Mpai.Cav.Map;

namespace Mpai.Cav.Ams;

// A LANE CHANGE: the Path leaves the lane it was in (FromOffset, metres to the left of the line of the Route) for
// another (ToOffset), beginning at StartS metres along the Route and taking Length metres to do it.
public sealed record LaneChange(double FromOffset, double ToOffset, double StartS, double Length)
{
    // Where the Path is at s, to the left of the line of the Route - a smooth step, a lane in about four seconds.
    public double OffsetAt(double s)
    {
        var u = Math.Clamp((s - StartS) / Length, 0, 1);
        return FromOffset + (ToOffset - FromOffset) * u * u * (3 - 2 * u);
    }

    // How fast the offset changes with s: the Path turns by its arctangent.
    public double SlopeAt(double s)
    {
        var u = (s - StartS) / Length;
        return u <= 0 || u >= 1 ? 0 : (ToOffset - FromOffset) * 6 * u * (1 - u) / Length;
    }

    public bool Moving(double s) => s >= StartS && s < StartS + Length;
}

// OVERTAKING, THE LANE CHANGE OF PATH SELECTION PLANNING, STAGE 2 (M3253 4). The CAV keeps the lane of its Path
// at its planned speed. When a slower vehicle comes within DecisionDistance ahead in it, it looks at the lane to
// its left - what the Full Environment Descriptors say is ahead there, and what the rear cameras of the ESS have
// seen behind - and:
//  - waits, following the vehicle ahead, while a vehicle is ahead in the left lane within LeftAheadClear metres, or
//    one behind it there is nearer than BehindDistance or would arrive within BehindSeconds (BehindMargin
//    metres short of it), or has not been followed long enough for its speed to be known;
//  - otherwise pulls out, to the lane to its left, taking ChangeSeconds;
//  - passes - the Motion Selection Planning speeds up to the limit, behind whatever is ahead in the new lane - and
//    returns to the right when the vehicle it passed is ReturnBehind metres behind it and the right lane is free
//    for RightAheadClear metres ahead.
// What the cameras no longer see is remembered. A vehicle behind that was closing is taken to be coming still, at the
// distance its closing speed gives, for Memory seconds or until it has gone by; the vehicle being passed, once the
// ego is beside it, to go on at the speed it had. The CAV's Stage 1 planner follows the lane of its Path
// and is not changed by this: the planner gives the Path a lane change, and the planner follows the Path.
// Settings: LaneChange (1 switches the planner on), DecisionDistance, LeftAheadClear, BehindDistance,
// BehindSeconds, BehindMargin, ChangeSeconds, ReturnBehind, RightAheadClear, SlowerBy, EgoLength, Memory, MaxLeftSeconds.
//
// A TWO-WAY ROAD (the setting OpposingLane = 1): the lane to the left carries the traffic that comes the other way, so
// that passing is meeting it head on. The CAV pulls out only if the vehicle that would meet it first cannot do so before
// the pass is over - the time it takes at PassAcceleration, PullOutSeconds to get out and ChangeSeconds to get back,
// at the planned speed and the speed of the vehicle coming, and SafetyDistance to spare. What it knows of that lane:
// what its own sensors see - the truck it follows hides what is beyond it: the line of sight past its side reaches
// only so far (OwnRange at most) - and what the CAVs coming the other way report, each as far as RemoteReach metres
// from where it is (what a CAV perceives and what the CAVs it is in touch with have told it). The lane is known as far as the sight and the reports, together, go without a break; beyond, a vehicle
// is taken to be there, coming at OncomingSpeed. A vehicle that appears while it is out and cannot be got past in time
// makes it go back behind the truck if it has not drawn level, else get past as soon as it can. The Motion Selection
// Planning and the Traffic Obstacle Avoidance leave what comes the other way to this planner.
// Settings: OpposingLane, OncomingSpeed, SafetyDistance, OwnRange, RemoteReach, PassAcceleration, PullOutSeconds.
public sealed class LaneChangePlanner
{
    public enum Phase { Following, PullingOut, Passing, Returning }

    private sealed class Seen
    {
        public required string Id;
        public string Class = "car";
        public double X, Y;                              // metres ahead of the ego's front, and to its left, as of Time
        public double? Speed;                            // its own, m/s, where it is known
        public bool RearFace = true;                     // X is its rear (seen from behind it), else its front
        public double Time, LastSeen, FirstSeen;
        public List<(double T, double X)> History = [];
        public double Length => Class is "truck" or "bus" ? 12 : 4.5;
    }

    private readonly bool enabled;
    private readonly double decisionDistance, leftAheadClear, behindDistance, behindSeconds, behindMargin, changeSeconds, returnBehind,
                            rightAheadClear, slowerBy, egoLength, memory, maxLeft;
    private readonly bool opposing;
    private readonly double oncomingSpeed, safetyDistance, ownRange, remoteReach, passAcceleration, pullOutSeconds;
    private readonly Dictionary<string, Seen> seen = new(StringComparer.Ordinal);
    private readonly List<(double X, double Speed, string Id)> oncoming = [];     // what comes the other way: metres ahead, m/s
    private readonly List<double> remoteCavs = [];                                // where the CAVs coming the other way are
    private bool hurry;
    private double now, egoSpeed;
    private string? passing;                             // the vehicle being passed
    private double leftSince;
    private LaneChange? current;

    public LaneChangePlanner(IReadOnlyDictionary<string, string> settings)
    {
        enabled = EssJson.Setting(settings, "LaneChange", 0) > 0;
        decisionDistance = EssJson.Setting(settings, "DecisionDistance", 60);
        leftAheadClear = EssJson.Setting(settings, "LeftAheadClear", 80);
        behindDistance = EssJson.Setting(settings, "BehindDistance", 30);
        behindSeconds = EssJson.Setting(settings, "BehindSeconds", 16);
        behindMargin = EssJson.Setting(settings, "BehindMargin", 10);
        changeSeconds = EssJson.Setting(settings, "ChangeSeconds", 4);
        returnBehind = EssJson.Setting(settings, "ReturnBehind", 30);
        rightAheadClear = EssJson.Setting(settings, "RightAheadClear", 60);
        slowerBy = EssJson.Setting(settings, "SlowerBy", 3);
        egoLength = EssJson.Setting(settings, "EgoLength", 4.8);
        memory = EssJson.Setting(settings, "Memory", 8);
        maxLeft = EssJson.Setting(settings, "MaxLeftSeconds", 40);
        opposing = EssJson.Setting(settings, "OpposingLane", 0) > 0;
        oncomingSpeed = EssJson.Setting(settings, "OncomingSpeed", 28);
        safetyDistance = EssJson.Setting(settings, "SafetyDistance", 60);
        ownRange = EssJson.Setting(settings, "OwnRange", 150);
        remoteReach = EssJson.Setting(settings, "RemoteReach", 1500);
        passAcceleration = EssJson.Setting(settings, "PassAcceleration", 1.5);
        pullOutSeconds = EssJson.Setting(settings, "PullOutSeconds", 2.5);
    }

    public bool Enabled => enabled;
    public Phase Now { get; private set; } = Phase.Following;
    public string Status { get; private set; } = "following";

    // The offset of the Path from the line of the Route, now and after the lane change in hand.
    public double Offset => current?.ToOffset ?? 0;
    public LaneChange? Current => current;

    // What the Full Environment Descriptors say at this instant, with s the ego's distance along the Route and
    // speed the planned speed: the lane change to begin, or none.
    public LaneChange? Update(JsonNode fed, double s, double planned, int lanes)
    {
        if (!enabled) return null;
        now = AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]) / 1000.0;
        egoSpeed = AmsTypes.Ego(fed["EgoSpatialAttitude"]!).Speed;
        Observe(fed);
        if (opposing) ObserveOncoming(fed);

        switch (Now)
        {
            case Phase.Following: return Follow(s, planned, lanes);
            case Phase.PullingOut:
                if (opposing && Verify(s, planned) is { } abort1) return abort1;
                if (current is { } p && s >= p.StartS + p.Length) { Now = Phase.Passing; Status = "passing"; leftSince = now; }
                return null;
            case Phase.Passing:
                if (opposing && Verify(s, planned) is { } abort2) return abort2;
                return Pass(s);
            default:
                if (current is { } r && s >= r.StartS + r.Length)
                {
                    Now = Phase.Following; Status = "following"; passing = null; hurry = false;
                }
                return null;
        }
    }

    // ---- what is around, and what is remembered -------------------------------------------------

    private void Observe(JsonNode fed)
    {
        var seenNow = new HashSet<string>();
        foreach (var o in fed["FullEnvironmentObjects"]?.AsArray() ?? [])
        {
            if ((string?)o?["Source"] is { } source && source != "Ego") continue;
            var b = o?["BasicEnvironmentObject"];
            if (b is null || (double?)b["ExistenceConfidence"] < 0.5) continue;
            var p = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartPosition"]);
            if (p is null) continue;
            var id = (string?)b["BasicEnvironmentObjectID"] ?? "";
            var v = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartVelocity"]);
            if (!seen.TryGetValue(id, out var t)) seen[id] = t = new Seen { Id = id, FirstSeen = now };
            t.Class = (string?)b["InstanceIdentifier"]?["InstanceIdentifier"] ?? t.Class;
            t.X = p.Value.X; t.Y = p.Value.Y; t.Time = now; t.LastSeen = now; t.RearFace = p.Value.X > -egoLength / 2;
            t.History.Add((now, t.X));
            t.History.RemoveAll(h => now - h.T > 3.0);
            // Its speed: the slope of its distance over the last seconds - the filter of the ESS follows a
            // vehicle seen a few times by lagging - and, until there are the looks for it, not known.
            t.Speed = Slope(t) is { } slope ? egoSpeed + slope : null;
            seenNow.Add(id);
        }
        foreach (var t in seen.Values.ToList())
        {
            if (seenNow.Contains(t.Id)) continue;
            // Not seen now: it is where it was going to be.
            var dt = now - t.Time;
            if (dt > 0) { t.X += ((t.Speed ?? egoSpeed + 12) - egoSpeed) * dt; t.Time = now; }
            var isPassing = t.Id == passing;
            if (!isPassing && (now - t.LastSeen > memory || t.X > 5)) seen.Remove(t.Id);
        }
    }

    private static double? Slope(Seen t)
    {
        var p = t.History;
        if (p.Count < 4 || p[^1].T - p[0].T < 1.0) return null;
        double mt = p.Average(x => x.T), mx = p.Average(x => x.X);
        var den = p.Sum(x => (x.T - mt) * (x.T - mt));
        return den <= 0 ? null : p.Sum(x => (x.T - mt) * (x.X - mx)) / den;
    }

    // How far behind the ego's rear bumper the vehicle's front is (negative: its front is beside or ahead of it).
    private double Behind(Seen t) => -t.X - egoLength - (t.RearFace ? t.Length : 0);

    // ---- the decisions --------------------------------------------------------------------------

    private LaneChange? Follow(double s, double planned, int lanes)
    {
        Status = "following";
        // The vehicle ahead in the lane, and whether it holds the CAV back.
        Seen? lead = null;
        foreach (var t in seen.Values)
            if (t.LastSeen == now && t.X > 0 && Math.Abs(t.Y) < 1.75 && (lead is null || t.X < lead.X)) lead = t;
        if (lead is null || lead.X > decisionDistance || lead.Speed is not { } ls || ls > planned - slowerBy) return null;
        if (lanes < 2) { Status = "following: no lane to the left"; return null; }

        if (opposing)
        {
            if (!OpposingClear(lead, planned, out var why)) { Status = "wants to pass: " + why; return null; }
            passing = lead.Id; hurry = false;
            Now = Phase.PullingOut; Status = "pulling out to pass: " + why;
            return current = new LaneChange(current?.ToOffset ?? 0, RoadMap.LaneWidth, s + 2, Math.Max(40, egoSpeed * changeSeconds));
        }

        // The left lane ahead; and behind.
        if (seen.Values.Any(t => t.LastSeen == now && t.X > 0 && t.Y is > 1.75 and < 5.25 && t.X < leftAheadClear))
        { Status = "wants to pass: a vehicle ahead in the left lane"; return null; }
        foreach (var t in seen.Values)
        {
            if (t.Y is < 1.5 or > 5.5 || t.X > 5 || t.Id == lead.Id) continue;
            var behind = Behind(t);
            var closing = t.Speed is { } sp ? sp - egoSpeed : (double?)null;
            if (closing is null || behind < behindDistance || (closing > 0 && (behind - behindMargin) / closing < behindSeconds))
            { Status = "wants to pass: waiting, a vehicle is coming up in the left lane"; return null; }
        }

        passing = lead.Id;
        Now = Phase.PullingOut; Status = "pulling out to pass";
        return current = new LaneChange(current?.ToOffset ?? 0, RoadMap.LaneWidth, s + 2, Math.Max(40, egoSpeed * changeSeconds));
    }

    // What comes the other way: the vehicles moving against the road, from the ESS and from the CAVs that report.
    private void ObserveOncoming(JsonNode fed)
    {
        oncoming.Clear(); remoteCavs.Clear();
        foreach (var o in fed["FullEnvironmentObjects"]?.AsArray() ?? [])
        {
            var b = o?["BasicEnvironmentObject"];
            if (b is null || (double?)b["ExistenceConfidence"] < 0.5) continue;
            var p = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartPosition"]);
            if (p is null) continue;
            var along = egoSpeed + (EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartVelocity"])?.X ?? 0);    // its speed along the road
            if (along > -1) continue;
            var id = (string?)b["BasicEnvironmentObjectID"] ?? "";
            oncoming.Add((p.Value.X, -along, id));
            var parts = id.Split('/');
            if ((string?)o!["Source"] == "Remote" && parts.Length == 2 && parts[0] == parts[1]) remoteCavs.Add(p.Value.X);   // the CAV itself
        }
    }

    // How far ahead the lane to the left is seen from the lane the CAV is in, past the vehicle ahead: the line of sight
    // that clears its left side at its rear reaches the lane at this many metres.
    private double OwnHorizon(Seen? ahead)
    {
        if (ahead is null || ahead.X <= 0) return ownRange;
        var edge = ahead.Y + (ahead.Class is "truck" or "bus" ? 1.25 : 0.9);          // its left side, to the left of the CAV
        var lane = RoadMap.LaneWidth + ahead.Y;                                          // the lane that comes, to the left of the CAV
        return edge <= 0.1 ? ownRange : Math.Min(ownRange, ahead.X * lane / edge);
    }

    // The lane is known as far as the sight and the reports go without a break; the first to meet the CAV there, and its speed.
    private (double Distance, double Speed, string What) FirstToMeet(double ownHorizon)
    {
        var cover = new List<(double From, double To)> { (0, ownHorizon) };
        foreach (var c in remoteCavs) if (c > 0) cover.Add((c - remoteReach, c));
        var known = ownHorizon;
        foreach (var (from, to) in cover.OrderBy(c => c.From)) if (from <= known + 10) known = Math.Max(known, to);
        var nearest = oncoming.Where(o => o.X > 0).OrderBy(o => o.X).Select(o => ((double X, double Speed, string Id)?)o).FirstOrDefault();
        if (nearest is { } n && n.X <= known + 1) return (n.X, n.Speed, $"{n.Id} at {n.X:0} m");
        return (known, oncomingSpeed, $"the lane is known only to {known:0} m");
    }

    // How long the pass takes from here: what is to be gained on the vehicle passed - until it is returnBehind metres
    // behind - at the planned speed, reached by PassAcceleration; and the lane changes.
    private double PassTime(double distance, double egoSpeedNow, double leadSpeed, double planned, bool pullOut, bool goBack)
    {
        var vp = Math.Max(planned, leadSpeed + 1);
        var a = passAcceleration;
        var t1 = Math.Max(0, (vp - egoSpeedNow) / a);
        var gained = (egoSpeedNow - leadSpeed) * t1 + 0.5 * a * t1 * t1;
        double over;
        if (distance <= 0) over = 0;
        else if (gained >= distance)
        {
            var b = egoSpeedNow - leadSpeed;
            over = (-b + Math.Sqrt(b * b + 2 * a * distance)) / a;
        }
        else over = t1 + (distance - gained) / Math.Max(0.5, vp - leadSpeed);
        return (pullOut ? pullOutSeconds : 0) + over + (goBack ? changeSeconds : 0);
    }

    // May the CAV pull out to pass the vehicle ahead? why: what it knows.
    private bool OpposingClear(Seen lead, double planned, out string why)
    {
        var time = PassTime(Math.Max(0, returnBehind - Behind(lead)), egoSpeed, lead.Speed ?? egoSpeed, planned, pullOut: true, goBack: true);
        var vp = Math.Max(planned, (lead.Speed ?? egoSpeed) + 1);
        var (d, vo, what) = FirstToMeet(OwnHorizon(lead));
        var needed = time * (vp + vo) + safetyDistance;
        why = $"{what}; the pass takes {time:0.0} s and needs {needed:0} m";
        if (d > needed) return true;
        why = (what.StartsWith("the lane") ? "oncoming traffic cannot be ruled out: " : "oncoming traffic: ") + why;
        return false;
    }

    // While out: can it still be done, with what is known now? Null where it can; else the lane change back, where it has not drawn
    // level; or - hurrying - null, to get past as soon as it can.
    private LaneChange? Verify(double s, double planned)
    {
        if (passing is null || !seen.TryGetValue(passing, out var p)) return null;
        var time = PassTime(Math.Max(0, returnBehind - Behind(p)), egoSpeed, p.Speed ?? egoSpeed, planned, pullOut: false, goBack: true);
        var vp = Math.Max(planned, (p.Speed ?? egoSpeed) + 1);
        var (d, vo, what) = FirstToMeet(OwnHorizon(p));
        if (d > time * (vp + vo) + safetyDistance / 2) return null;
        if (p.X > 0 && !hurry)
        {
            Now = Phase.Returning; Status = $"abandons the pass: {what}"; passing = null;
            var from = current?.OffsetAt(s) ?? RoadMap.LaneWidth;
            return current = new LaneChange(from, 0, s + 2, Math.Max(40, egoSpeed * changeSeconds));
        }
        hurry = true; Status = $"hurrying back: {what}";
        return null;
    }

    private LaneChange? Pass(double s)
    {
        Status = "passing";
        // The right lane: free ahead, and nothing close behind - the vehicle passed above all.
        if (seen.Values.Any(t => t.LastSeen == now && t.X > 0 && t.Y is < -1.5 and > -5.5 && t.X < rightAheadClear)) return null;
        foreach (var t in seen.Values)
        {
            if (t.Y is > -1.5 or < -5.5) continue;
            if (t.X > 0 && t.LastSeen == now) continue;
            var behind = Behind(t);
            var closing = t.Speed is { } sp ? sp - egoSpeed : 0;
            if (behind < (hurry ? returnBehind / 2 : returnBehind) || (closing > 0 && (behind - behindMargin) / closing < behindSeconds / 2)) return null;
        }
        // The vehicle that was passed must have been left behind by the distance: if it was not seen
        // at all after the pull-out it is not known to be, and the ego waits - up to MaxLeftSeconds.
        if (passing is not null && seen.TryGetValue(passing, out var p) && Behind(p) < (hurry ? returnBehind / 2 : returnBehind) && now - leftSince < maxLeft) return null;

        Now = Phase.Returning; Status = "passed: back to the right lane";
        return current = new LaneChange(RoadMap.LaneWidth, 0, s + 2, Math.Max(40, egoSpeed * changeSeconds));
    }
}
