using System.Text.Json.Nodes;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Mas;

// WHAT THE AIMs OF THE MAS SHARE: the Data Types they exchange, the commands of the
// mechanical subsystems in the form of their schemas, and a Spatial Attitude read.
public static class MasTypes
{
    public const string Message = "CAV-AMM-V2.0", Attitude = "OSD-OSA-V1.5", SpatialData = "CAV-SPD-V2.0",
                        Weather = "CAV-WDT-V2.0", RoadState = "CAV-RDS-V2.0",
                        BrakeCommand = "CAV-BRC-V2.0", MotorCommand = "CAV-MRC-V2.0", WheelCommand = "CAV-WHC-V2.0",
                        BrakeResponse = "CAV-BRR-V2.0", MotorResponse = "CAV-MRP-V2.0", WheelResponse = "CAV-WHR-V2.0";

    public static long Ms(JsonNode? simpleTime) => EssJson.Milliseconds(simpleTime) ?? 0;

    // A Brake Command of one set point: a deceleration (m/s2), released at releaseAt
    // m/s (0: held to a standstill); an emergency brakes as hard as the brakes can.
    public static string Brake(string id, long ms, double deceleration, bool emergency = false, bool abs = true, double releaseAt = 0) => new JsonObject
    {
        ["Header"] = BrakeCommand, ["BrakeCommandID"] = id, ["BrakeID"] = "B1",
        ["BrakeCommand"] = new JsonArray(new JsonObject
        {
            ["TargetVelocity"] = releaseAt, ["BrakeCommandTime"] = EssJson.SimpleTime(id + "-T", ms),
            ["DecelerationTarget"] = Math.Round(deceleration, 3), ["ABSAllow"] = abs, ["EmergencyBrakeFlag"] = emergency
        })
    }.ToJsonString();

    // A Motor Command: a target in torque (N m), acceleration (m/s2) or velocity (m/s) mode.
    public static string Motor(string id, long ms, string mode, double target)
    {
        var command = new JsonObject { ["ControlMode"] = mode, ["MotorCommandTime"] = EssJson.SimpleTime(id + "-T", ms) };
        command[mode switch { "torque" => "TargetTorque", "acceleration" => "TargetAcceleration", _ => "TargetVelocity" }] = Math.Round(target, 3);
        return new JsonObject { ["Header"] = MotorCommand, ["MotorCommandID"] = id, ["MotorID"] = "M1", ["MotorCommand"] = command }.ToJsonString();
    }

    // A Wheel Command: the angle of the road wheels, degrees, left positive.
    public static string Wheel(string id, long ms, double degrees) => new JsonObject
    {
        ["Header"] = WheelCommand, ["WheelCommandID"] = id, ["WheelID"] = "W1", ["WheelCommandTime"] = EssJson.SimpleTime(id + "-T", ms),
        ["WheelCommand"] = new JsonObject { ["Angle"] = Math.Round(degrees, 3), ["SteeringMode"] = "SteerByWire" }
    }.ToJsonString();

    // Where a Spatial Attitude says the CAV is: position, heading (radians,
    // anticlockwise from east), speed, and its time.
    public static (double East, double North, double Heading, double Speed, long Ms) Pose(JsonNode attitude)
    {
        var p = EssJson.Vector(attitude["Position"]?["CartPosition"]) ?? (0, 0, 0);
        var v = EssJson.Vector(attitude["Position"]?["CartVelocity"]) ?? (0, 0, 0);
        var yaw = EssJson.Vector(attitude["Orientation"]?["Orientation"])?.Z ?? 0;
        return (p.X, p.Y, yaw * Math.PI / 180, Math.Sqrt(v.X * v.X + v.Y * v.Y), Ms(attitude["SpatialAttitudeTime"]));
    }
}
