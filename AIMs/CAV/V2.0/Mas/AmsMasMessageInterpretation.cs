using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Mas;

// AMS-MAS MESSAGE INTERPRETATION, STAGE 1 (CAV-AMI; M3237 3.4). The Trajectory of the
// latest AMS-MAS Message followed by the commands of the mechanical subsystems, at
// each AMS-MAS Message, from the CAV's Spatial Attitude it carries - the AMS's: GNSS
// and the MAS's own estimate combined by the ESS, on the Trajectory's frame (the
// author: the MAS executes; it does not second-guess where the AMS says the CAV is).
// A Message without one - a Suspend, a Resume - is followed from the last received.
//
// Longitudinally, the acceleration the Trajectory asks a moment ahead (Preview, the
// lag of the devices) and a correction of the speed error (SpeedGain): the motor
// in acceleration mode while that is above -Coast - the resistance of the road and
// the air slows the CAV that much without the brakes - and the brakes beyond it;
// never both acting: on a change from one to the other, the one left is released.
// A deceleration of Emergency m/s2 or more, asked or needed, is an Emergency Brake
// Command; a CAV at a standstill that is to stay there is held by the brakes (Hold).
//
// Laterally, pure pursuit: the point of the Trajectory a look-ahead distance beyond
// the one nearest the CAV - LookaheadTime seconds of its speed, at least
// MinimumLookahead metres - reached on the arc the bicycle model of the CAV's
// Wheelbase drives; beyond the end of the Trajectory, along its last heading.
//
// The Command: Execute and Change follow the Trajectory given; Suspend stops the CAV
// at once (an Emergency Brake Command) and Resume follows the Trajectory it held.
// An AMS-MAS Message with no AMS Message, or no Trajectory to follow, is reported.
public sealed class AmsMasMessageInterpretation(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    private readonly double speedGain = EssJson.Setting(settings, "SpeedGain", 0.8), preview = EssJson.Setting(settings, "Preview", 0.2),
                            lookaheadTime = EssJson.Setting(settings, "LookaheadTime", 1), minimumLookahead = EssJson.Setting(settings, "MinimumLookahead", 8),
                            wheelbase = EssJson.Setting(settings, "Wheelbase", 2.8), emergency = EssJson.Setting(settings, "Emergency", 6),
                            coast = EssJson.Setting(settings, "Coast", 0.15), hold = EssJson.Setting(settings, "Hold", 2);

    private sealed record Point(long Ms, double East, double North, double Heading, double Speed, double Acceleration);
    private List<Point>? trajectory;
    private JsonNode? attitude;                               // the CAV's, the last an AMS-MAS Message carried
    private bool suspended;
    private string? acting;                                   // "motor" or "brake"
    private double lastAngle;
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-AMI runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        // The Responses of the devices are not its concern: what was executed is MRA's
        // to judge (Step 6; the author: the MAS executes).
        while (await ports.SelectAsync(-1, (MasTypes.Message, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var (problem, commands) = Interpret(JsonNode.Parse(m.Json)!);
            if (problem is not null) context.Report(problem);
            foreach (var (dataType, json) in commands) await ports.WriteAsync(dataType, 1, json);
        }
    }

    // An AMS-MAS Message: accepted, and the commands from the Spatial Attitude it
    // carries, or the last one received.
    public (string? Problem, IReadOnlyList<(string DataType, string Json)> Commands) Interpret(JsonNode message)
    {
        var problem = Accept(message);
        if (message["AMSMessage"]?["SpatialAttitude"] is { } carried) attitude = carried.DeepClone();
        return (problem, attitude is null ? [] : Commands(attitude));
    }

    // An AMS-MAS Message: its Command, and its Trajectory. Null if accepted, else why not.
    public string? Accept(JsonNode message)
    {
        if (message["AMSMessage"] is not JsonObject ams) return $"{(string?)message["AMSMASMessageID"]}: no AMS Message to interpret.";
        var command = (string?)ams["Command"] ?? "Execute";
        switch (command)
        {
            case "Suspend": suspended = true; return null;
            case "Resume": suspended = false; return trajectory is null ? $"{(string?)message["AMSMASMessageID"]}: Resume, and no Trajectory held." : null;
        }
        if (Points(ams["Trajectory"]) is not { Count: > 0 } points)
            return $"{(string?)message["AMSMASMessageID"]}: {command}, and no Trajectory to follow.";
        trajectory = points;
        suspended = false;
        return null;
    }

    // Each point of a Trajectory: its time, where, its heading, its speed, and its
    // acceleration along its heading.
    private static List<Point>? Points(JsonNode? trajectory)
    {
        if (trajectory?["Trajectory"] is not JsonArray array) return null;
        var points = new List<Point>();
        var heading = 0.0;
        for (var k = 0; k < array.Count; k++)
        {
            var st = array[k]?["ExpectedSpaceTime"];
            var position = st?["SpatialAttitude1"]?["Position"];
            if (EssJson.Vector(position?["CartPosition"]) is not { } p) return null;
            var v = EssJson.Vector(position?["CartVelocity"]) ?? (0, 0, 0);
            var a = EssJson.Vector(position?["CartAccel"]) ?? (0, 0, 0);
            var speed = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (speed > 1e-3) heading = Math.Atan2(v.Y, v.X);
            else if (k + 1 < array.Count && EssJson.Vector(array[k + 1]?["ExpectedSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPosition"]) is { } q
                     && Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y) > 1e-3)
                heading = Math.Atan2(q.Y - p.Y, q.X - p.X);
            points.Add(new Point(MasTypes.Ms(st?["Time"]), p.X, p.Y, heading, speed, a.X * Math.Cos(heading) + a.Y * Math.Sin(heading)));
        }
        return points;
    }

    // THE COMMANDS AT A SPATIAL ATTITUDE of the MAS.
    public IReadOnlyList<(string DataType, string Json)> Commands(JsonNode attitude)
    {
        if (trajectory is null) return [];
        var (east, north, heading, speed, ms) = MasTypes.Pose(attitude);
        var commands = new List<(string, string)>();
        var id = $"{++count:D6}";

        // Longitudinally.
        var now = At(ms);
        var ahead = At(ms + (long)Math.Round(preview * 1000));
        var wanted = ahead.Acceleration + speedGain * (now.Speed - speed);
        if (suspended || -wanted >= emergency || -ahead.Acceleration >= emergency)
            Act(commands, "brake", id, ms, MasTypes.Brake("BRC" + id, ms, emergency, emergency: true));
        else if (ahead.Speed < 0.05 && speed < 0.5)
            Act(commands, "brake", id, ms, MasTypes.Brake("BRC" + id, ms, hold));
        else if (wanted > -coast)
            Act(commands, "motor", id, ms, MasTypes.Motor("MRC" + id, ms, "acceleration", wanted));
        else
            Act(commands, "brake", id, ms, MasTypes.Brake("BRC" + id, ms, -wanted));

        // Laterally: pure pursuit of the point a look-ahead beyond the nearest.
        var lookahead = Math.Max(minimumLookahead, lookaheadTime * speed);
        var (tx, ty) = Ahead(east, north, lookahead);
        var (dx, dy) = (tx - east, ty - north);
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > 0.5)
        {
            var alpha = Math.IEEERemainder(Math.Atan2(dy, dx) - heading, 2 * Math.PI);
            lastAngle = Math.Atan(2 * wheelbase * Math.Sin(alpha) / distance) * 180 / Math.PI;
        }
        commands.Add((MasTypes.WheelCommand, MasTypes.Wheel("WHC" + id, ms, lastAngle)));
        return commands;
    }

    // The command of the device that is to act; the other released if it was acting.
    private void Act(List<(string, string)> commands, string device, string id, long ms, string command)
    {
        if (acting is not null && acting != device)
            commands.Add(acting == "brake"
                ? (MasTypes.BrakeCommand, MasTypes.Brake("BRC" + id + "R", ms, 0))
                : (MasTypes.MotorCommand, MasTypes.Motor("MRC" + id + "R", ms, "torque", 0)));
        acting = device;
        commands.Add((device == "brake" ? MasTypes.BrakeCommand : MasTypes.MotorCommand, command));
    }

    // The Trajectory's speed and acceleration at a time, between its points; before
    // its first, its first; after its last, the last speed, held.
    private (double Speed, double Acceleration) At(long ms)
    {
        var t = trajectory!;
        if (ms <= t[0].Ms) return (t[0].Speed, t[0].Acceleration);
        if (ms >= t[^1].Ms) return (t[^1].Speed, 0);
        var i = t.FindLastIndex(p => p.Ms <= ms);
        var (a, b) = (t[i], t[i + 1]);
        var f = b.Ms > a.Ms ? (double)(ms - a.Ms) / (b.Ms - a.Ms) : 0;
        return (a.Speed + f * (b.Speed - a.Speed), a.Acceleration + f * (b.Acceleration - a.Acceleration));
    }

    // The point distance metres along the Trajectory beyond its point nearest (east,
    // north); beyond its end, along its last heading.
    private (double East, double North) Ahead(double east, double north, double distance)
    {
        var t = trajectory!;
        var nearest = 0;
        var best = double.MaxValue;
        for (var k = 0; k < t.Count; k++)
        {
            var d = Math.Pow(t[k].East - east, 2) + Math.Pow(t[k].North - north, 2);
            if (d < best) { best = d; nearest = k; }
        }
        var left = distance;
        for (var k = nearest; k + 1 < t.Count; k++)
        {
            var step = Math.Sqrt(Math.Pow(t[k + 1].East - t[k].East, 2) + Math.Pow(t[k + 1].North - t[k].North, 2));
            if (step >= left)
            {
                var f = left / step;
                return (t[k].East + f * (t[k + 1].East - t[k].East), t[k].North + f * (t[k + 1].North - t[k].North));
            }
            left -= step;
        }
        return (t[^1].East + left * Math.Cos(t[^1].Heading), t[^1].North + left * Math.Sin(t[^1].Heading));
    }
}
