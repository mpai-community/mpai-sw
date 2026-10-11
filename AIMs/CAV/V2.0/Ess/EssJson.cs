using System.Globalization;
using System.Text.Json.Nodes;

namespace Mpai.Cav.Ess;

// THE DATA TYPES OF THE ESS AS JSON, in the form their schemas give: Simple Time,
// Space-Time, Spatial Attitude. One place, so that every AIM of the Subsystem
// writes them alike.
public static class EssJson
{
    // A time in milliseconds since the Unix epoch, as Simple Time: absolute (bit 0),
    // in milliseconds (bits 1-2 = 01), accurate to one.
    public static JsonObject SimpleTime(string id, long ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject
        {
            ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1
        })
    };

    // The milliseconds of a Simple Time - the CAV's only time; null where there is none.
    // A Simple Time read from text holds a number; one built in this process, the long
    // it was built with.
    public static long? Milliseconds(JsonNode? simpleTime) =>
        simpleTime?["SimpleTimeData"]?[0]?["StartTime"] is not JsonValue v ? null
        : v.TryGetValue<long>(out var l) ? l
        : v.TryGetValue<double>(out var d) ? (long)d
        : v.TryGetValue<int>(out var i) ? i
        : null;

    public static JsonObject SpaceTime(string id, long ms, JsonObject? attitude = null)
    {
        var o = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = id };
        if (attitude is not null) o["SpatialAttitude1"] = attitude;
        o["Time"] = SimpleTime(id + "-T", ms);
        return o;
    }

    // A Spatial Attitude on a Cartesian frame: position, its accuracy, velocity.
    public static JsonObject Attitude(string id, long ms, (double X, double Y, double Z) position, (double X, double Y, double Z) accuracy,
                                      (double X, double Y, double Z) velocity, JsonNode? orientation = null) => new()
    {
        ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = id,
        ["SpatialAttitudeTime"] = SimpleTime(id + "-T", ms),
        ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
        ["Position"] = new JsonObject
        {
            ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = id + "-P",
            ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
            ["CartPosition"] = Triple(position), ["CartPositionAccuracy"] = Triple(accuracy), ["CartVelocity"] = Triple(velocity)
        },
        ["Orientation"] = orientation?.DeepClone() ?? new JsonObject
        {
            ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = id + "-O", ["Orientation"] = new JsonArray(0.0, 0.0, 0.0)
        }
    };

    // What an object is: its class against the taxonomy of the detector's classes
    // (COCO), with its confidence.
    public static JsonObject Identifier(string label, double confidence) => new()
    {
        ["Header"] = "OSD-IID-V1.5", ["MInstanceID"] = "", ["InstanceIdentifier"] = label,
        ["InstanceIdentifierData"] = new JsonArray(new JsonObject
        {
            ["InstanceLabel"] = label, ["LabelConfidenceLevel"] = Math.Round(confidence, 3),
            ["Taxonomy"] = new JsonObject { ["TaxonomyLevelIDs"] = new JsonArray("COCO", label), ["TaxonomyDataURI"] = "https://cocodataset.org/#explore" }
        })
    };

    public static JsonArray Triple((double X, double Y, double Z) v) => new(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));

    public static (double X, double Y, double Z)? Vector(JsonNode? array) =>
        array is JsonArray { Count: 3 } a ? ((double)a[0]!, (double)a[1]!, (double)a[2]!) : null;

    public static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static double Setting(IReadOnlyDictionary<string, string> settings, string name, double fallback) =>
        settings.TryGetValue(name, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
