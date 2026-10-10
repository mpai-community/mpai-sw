using System;
using System.Collections.Generic;

namespace Mpai.Mas.Server;

// A boundary Port of a Module, as the MAS server needs to see it.
//
// NO NAME. A Port is (DataType, PortNumber) and nothing else here - not even for
// error messages, because a name available is a name that eventually gets routed
// on. That is how the previous MAS server came to switch on "InputVisual" and
// carry cases for Modules it was not running.
public sealed class BoundaryPort
{
    public string Direction { get; init; } = string.Empty;   // "Input" | "Output"

    // The Data Type this Port is KEYED by - the first of its declared set. The
    // Controller builds its boundary key from this one, whatever the RCA sends.
    public string DataType { get; init; } = string.Empty;

    // Every Data Type this Port accepts. A Port taking either a Basic or a full
    // Text Object has two; most have one.
    public IReadOnlyList<string> DataTypes { get; init; } = Array.Empty<string>();

    public int PortNumber { get; init; } = 1;

    public bool IsOptional { get; init; }

    public bool Accepts(string dataType)
    {
        if (string.Equals(DataType, dataType, StringComparison.Ordinal)) return true;

        foreach (var candidate in DataTypes)
            if (string.Equals(candidate, dataType, StringComparison.Ordinal)) return true;

        return false;
    }

    // The boundary key the executor routes on.
    public string Key => DataType + "#" + PortNumber;

    // The URL segment for this Port, naming the Data Type actually being sent.
    public string SegmentFor(string dataType) =>
        PortNumber == 1 ? dataType : dataType + ":" + PortNumber;
}

// What the MAS server needs from whatever runs Modules.
//
// DELIBERATELY NOT THE CONTROLLER API ITSELF. ControllerApi lives in Mpai.Aif.Api, and a
// server that referenced it would drag an application-flavoured assembly into
// the one layer that must know no application. The host adapts its ControllerApi to
// this interface in a handful of lines, and this library depends on Data Types
// and nothing else.
public interface IModuleRunner
{
    // The Module''s boundary Ports, from its AMD. The server needs these to turn
    // a URL segment into a boundary key: the RCA names the Data Type it is
    // sending, which may be any of the Port''s accepted types, while the
    // Controller keys the boundary by the Port''s own DataType.
    IReadOnlyList<BoundaryPort> PortsOf(string moduleName);

    // Start a Module by name. Returns null on success, or a reason to report.
    string? Start(string moduleName);

    // Supply boundary inputs and run. Keys are "DataType#PortNumber".
    RunResult Run(string moduleName, IReadOnlyDictionary<string, string> inputs);

    // The same, for a client's session: what the run keeps for the session is that
    // client's, and deleted when it ends (M3245 3.3).
    RunResult Run(string moduleName, IReadOnlyDictionary<string, string> inputs, string session) => Run(moduleName, inputs);

    // A client's session ended.
    void SessionEnded(string session) { }

    void Stop(string moduleName);
}

public sealed class RunResult
{
    // Present when the run completed. Keyed "DataType#PortNumber".
    public IReadOnlyDictionary<string, string> Outputs { get; init; } =
        new Dictionary<string, string>();

    // Present when the run failed, for the status the RCA is given.
    public string? Error { get; init; }
}

// Turns a {pid} URL segment into the Port it addresses.
//
// The MAS specification fixes every route but leaves {pid} undefined, saying
// only that the body matches "the declared type of the involved ports". So {pid}
// carries (DataType, PortNumber), separated by a COLON: "#" cannot appear in a
// URL path at all - it begins a fragment and never reaches a server - while a
// colon is legal in a path segment. An omitted PortNumber means 1, literally,
// not "the only Port of that type".
//
//     Input/OSD-BTO-V1.5        -> PortNumber 1
//     Input/OSD-BTO-V1.5:2      -> PortNumber 2
//
// MMC-AMQ-V2.5 is why this is needed on the first Module an RCA touches rather
// than someday: it declares two Speech inputs, PortNumber 1 for the question and
// 2 for the reply, and two Text inputs the same way.
public static class PortSegment
{
    public static bool TryParse(
        string segment,
        out string dataType,
        out int portNumber)
    {
        dataType   = string.Empty;
        portNumber = 1;

        if (string.IsNullOrWhiteSpace(segment)) return false;

        // A Data Type identifier contains no colon of its own, so the LAST colon
        // separates the PortNumber when one is present.
        var colon = segment.LastIndexOf(':');

        if (colon < 0)
        {
            dataType = segment;
            return true;
        }

        var type   = segment.Substring(0, colon);
        var number = segment.Substring(colon + 1);

        if (type.Length == 0) return false;
        if (!int.TryParse(number, out var n) || n < 1) return false;

        dataType   = type;
        portNumber = n;
        return true;
    }

    // The Port a segment addresses, or null if the Module has no such Port.
    //
    // Matching is on DIRECTION, PORT NUMBER and ACCEPTANCE of the named type -
    // so an RCA sending a full Text Object to a Port declared
    // ["OSD-BTO-V1.5", "OSD-TXO-V1.5"] names OSD-TXO-V1.5 in the URL, honestly,
    // and the Port is still found. The boundary key then comes from the Port,
    // not from the URL, which is the whole reason this lookup exists.
    public static BoundaryPort? Resolve(
        IReadOnlyList<BoundaryPort> ports,
        string direction,
        string dataType,
        int portNumber)
    {
        foreach (var port in ports)
            if (string.Equals(port.Direction, direction, StringComparison.Ordinal) &&
                port.PortNumber == portNumber &&
                port.Accepts(dataType))
                return port;

        return null;
    }
}