using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Cav.Ess;

// SPATIAL ATTITUDE GENERATION (CAV-SAG-V2.0; M3229 11, M3221 3.2). From the Spatial
// Attitude the Motion Actuation Subsystem gives at its rate - odometry and inertial
// sensors: smooth, frequent, drifting - and GNSS at a lower rate - absolute, noisy,
// lost in tunnels - the ego Spatial Attitude on one frame: east, north and up in
// metres from the origin of the Offline Map, when the ESS has it - the frame of the
// AMS - and from the first GNSS fix otherwise (M3237, Step 7, the author: a frame
// anchored at a fix carries that fix's error for ever).
//
// A complementary filter: the ego position is a dead-reckoned track plus an offset,
// and each GNSS fix moves the offset towards what it says by a weight (the setting
// GnssWeight). The track is the odometry's steps, turned by a heading correction:
// the direction the GNSS fixes travelled against the direction the odometry did,
// over the last HeadingWindow fixes - the mean of HeadingEnds fixes at each end, at
// least HeadingBaseline metres apart - approached by HeadingWeight at each fix (M3237,
// Step 7: an odometer that reads long turns MSA's heading too far at a corner, and
// positions alone do not correct it). The ego's velocity and heading are turned by
// it too. The accuracy stated is the filter's, for the GNSS noise the Qualifier
// implies, grown by 2% of the distance run since the last fix. Before any fix, the
// frame is the odometry's, and the accuracy says it is not anchored.
public sealed class SpatialAttitudeGeneration(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    public const string Attitude = "OSD-OSA-V1.5", Gnss = "CAV-GNO-V2.0", Map = "OSD-BOO-V1.5";
    private const double Earth = 6_371_000, GnssSigma = 1.5, Unanchored = 1000;

    private readonly double weight = EssJson.Setting(settings, "GnssWeight", 0.1),
                            headingBaseline = EssJson.Setting(settings, "HeadingBaseline", 30), headingWeight = EssJson.Setting(settings, "HeadingWeight", 0.05);
    private readonly int headingWindow = (int)EssJson.Setting(settings, "HeadingWindow", 100), headingEnds = (int)EssJson.Setting(settings, "HeadingEnds", 10);
    private (double Lat, double Lon)? anchor;
    private bool placed;                                      // a fix has placed the CAV on the frame
    private int fixes;                                        // since it was placed
    private (double East, double North) offset;
    private (double X, double Y, double Z)? lastOdometry;
    private (double X, double Y) track;                       // the odometry's steps, turned
    private double turn, sinceFix;                            // the heading correction (radians); run since the last fix
    private readonly Queue<(double OdometryX, double OdometryY, double East, double North)> window = new();
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-SAG runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Attitude, 1), (Gnss, 1), (Map, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == Gnss) { Fix(json); continue; }
            if (port.DataType == Map) { Know(json); continue; }

            var odometry = EssJson.Vector(json["Position"]?["CartPosition"]) ?? (0, 0, 0);
            var velocity = EssJson.Vector(json["Position"]?["CartVelocity"]) ?? (0, 0, 0);
            var ms = EssJson.Milliseconds(json["SpatialAttitudeTime"]) ?? ports.Now.ToUnixTimeMilliseconds();
            var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
            if (lastOdometry is { } last)
            {
                var (dx, dy) = (odometry.X - last.X, odometry.Y - last.Y);
                track = (track.X + dx * cos - dy * sin, track.Y + dx * sin + dy * cos);
                sinceFix += Math.Sqrt(dx * dx + dy * dy);
            }
            else track = (odometry.X, odometry.Y);
            lastOdometry = odometry;

            var accuracy = !placed ? Unanchored : GnssSigma * Math.Sqrt(weight / (2 - weight)) + 0.02 * sinceFix;
            var orientation = json["Orientation"]?.DeepClone();
            if (orientation?["Orientation"] is JsonArray { Count: 3 } o)
                o[2] = Math.Round(Math.IEEERemainder((double)o[2]! + turn * 180 / Math.PI, 360), 3);
            var ego = EssJson.Attitude($"EGO{++count:D6}", ms,
                (track.X + offset.East, track.Y + offset.North, odometry.Z), (accuracy, accuracy, accuracy),
                (velocity.X * cos - velocity.Y * sin, velocity.X * sin + velocity.Y * cos, velocity.Z), orientation);
            await ports.WriteAsync(Attitude, 1, ego.ToJsonString());
        }
    }

    // The Offline Map: its origin anchors the frame; the next fix places the CAV on it.
    public void Know(JsonNode offlineMapObject)
    {
        try
        {
            var map = JsonNode.Parse((string)offlineMapObject["BasicOfflineMapData"]![0]!["Data"]!)!;
            anchor = ((double)map["Origin"]!["Lat"]!, (double)map["Origin"]!["Lon"]!);
            placed = false;
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or FormatException or System.Text.Json.JsonException) { }
    }

    // A GNSS fix: the first places the CAV, and anchors the frame if no map has;
    // each moves the offset - the first ones by their mean (1/n), so that the
    // error of one fix is not kept, then by the weight.
    private void Fix(JsonNode gnss)
    {
        if (Position(gnss) is not { } fix) return;
        if (!placed) fixes = 0;
        placed = true;
        fixes++;
        anchor ??= fix;
        var east = (fix.Lon - anchor.Value.Lon) * Math.PI / 180 * Earth * Math.Cos(anchor.Value.Lat * Math.PI / 180);
        var north = (fix.Lat - anchor.Value.Lat) * Math.PI / 180 * Earth;
        var errorEast = east - (track.X + offset.East);
        var errorNorth = north - (track.Y + offset.North);
        var k = Math.Max(weight, 1.0 / fixes);                     // the first fix places the CAV
        offset = (offset.East + k * errorEast, offset.North + k * errorNorth);
        sinceFix = 0;
        if (lastOdometry is { } o) Heading(o.X, o.Y, east, north);
    }

    // The heading correction: the direction the fixes travelled over the window against
    // the direction the odometry did, each between the means of its ends.
    private void Heading(double odometryX, double odometryY, double east, double north)
    {
        window.Enqueue((odometryX, odometryY, east, north));
        while (window.Count > headingWindow) window.Dequeue();
        if (window.Count < 2 * headingEnds) return;
        var first = window.Take(headingEnds).ToList();
        var last = window.Skip(window.Count - headingEnds).ToList();
        var (ax, ay) = (last.Average(w => w.OdometryX) - first.Average(w => w.OdometryX), last.Average(w => w.OdometryY) - first.Average(w => w.OdometryY));
        var (bx, by) = (last.Average(w => w.East) - first.Average(w => w.East), last.Average(w => w.North) - first.Average(w => w.North));
        if (Math.Sqrt(ax * ax + ay * ay) < headingBaseline || Math.Sqrt(bx * bx + by * by) < headingBaseline) return;
        var estimate = Math.Atan2(by, bx) - Math.Atan2(ay, ax);
        turn += headingWeight * Math.IEEERemainder(estimate - turn, 2 * Math.PI);
    }

    // Latitude and longitude from the GGA sentence of NMEA 0183 in the GNSS Data.
    public static (double Lat, double Lon)? Position(JsonNode gnss)
    {
        foreach (var entry in gnss["GNSSData"]?.AsArray() ?? [])
        {
            if (entry?["Data"]?.GetValue<string>() is not { } b64) continue;
            var text = Encoding.ASCII.GetString(Convert.FromBase64String(b64));
            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('*')[0].Split(',');
                if (!f[0].EndsWith("GGA") || f.Length < 6 || f[2] == "" || f[4] == "") continue;
                double Angle(string s, int degrees) =>
                    double.Parse(s[..degrees], CultureInfo.InvariantCulture) + double.Parse(s[degrees..], CultureInfo.InvariantCulture) / 60;
                var lat = Angle(f[2], 2) * (f[3] == "S" ? -1 : 1);
                var lon = Angle(f[4], 3) * (f[5] == "W" ? -1 : 1);
                return (lat, lon);
            }
        }
        return null;
    }
}
