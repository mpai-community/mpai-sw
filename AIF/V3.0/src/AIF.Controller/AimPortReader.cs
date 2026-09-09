using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using AIF.Store;

namespace AIF.Controller;

// Reads ExternalPort names from an AMD by DataType.
//
// Each AIM reads its own port names from its own instance JSON at startup, so
// nothing is hardcoded. The AIM knows its own DataTypes; the AMD declares which
// port name carries each DataType. This class bridges the two.
//
// An AIM may declare SEVERAL ports of the same Direction and DataType - MMC-TTT
// has two OSD-TXO-V1.5 inputs, Input Text and Recognised Text. DataType alone
// cannot tell them apart, so ports are held in PortNumber order and the ordinal
// selects one. Ordinal 1 is the default, which is what every single-port AIM
// asks for implicitly.
public sealed class AimPortReader
{
    private readonly Dictionary<string, List<Entry>> _inputPorts  = new();
    private readonly Dictionary<string, List<Entry>> _outputPorts = new();

    private sealed record Entry(string Name, int Ordinal, int Declared);

    private AimPortReader() { }

    // Load the port map for the given AIMName from the store.
    public static AimPortReader Load(AmdStore store, string aimName)
    {
        var reader   = new AimPortReader();
        var resolved = store.FindByAimName(aimName);
        if (resolved is null)
            return reader;

        var amd = store.GetAMD(resolved).RootElement;
        if (!amd.TryGetProperty("ExternalPorts", out var ports))
            return reader;

        var declared = 0;
        foreach (var port in ports.EnumerateArray())
        {
            var name      = port.GetProperty("Name").GetString()      ?? string.Empty;
            var direction = port.GetProperty("Direction").GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var ordinal =
                port.TryGetProperty("PortNumber", out var portNumber) &&
                portNumber.TryGetInt32(out var parsed) && parsed >= 1
                    ? parsed
                    : 0;   // 0 = not declared; declaration order decides

            var target = direction == "Input"  ? reader._inputPorts
                       : direction == "Output" ? reader._outputPorts
                       : null;
            if (target is null) continue;

            // A Port may accept SEVERAL Data Types - a Port taking either a
            // Basic or a full Audio Object declares both - so it is indexed
            // under each of them. Asking for OSD-BAO-V1.5 or for OSD-AUO-V1.5
            // then finds the one Port that takes either.
            //
            // One position in declaration order per PORT, not per Data Type, so
            // that a multi-typed Port does not consume several ordinals.
            var position = declared++;

            foreach (var dataType in DataTypesOf(port))
            {
                if (!target.TryGetValue(dataType, out var list))
                {
                    list = new List<Entry>();
                    target[dataType] = list;
                }

                list.Add(new Entry(name, ordinal, position));
            }
        }

        return reader;
    }

    // A Port's DataType is a string, or an ARRAY of strings when the Port
    // accepts more than one. Reading it with GetString() throws on the array, so
    // every reader of an AMD goes through here.
    private static IReadOnlyList<string> DataTypesOf(JsonElement port)
    {
        if (!port.TryGetProperty("DataType", out var dt))
            return Array.Empty<string>();

        if (dt.ValueKind == JsonValueKind.String)
        {
            var one = dt.GetString();
            return string.IsNullOrWhiteSpace(one) ? Array.Empty<string>() : new[] { one };
        }

        if (dt.ValueKind == JsonValueKind.Array)
            return dt.EnumerateArray()
                     .Select(e => e.GetString())
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Select(s => s!)
                     .ToArray();

        return Array.Empty<string>();
    }

    // Return the Input port name for the given DataType and ordinal.
    // Throws if not found â€” a misconfigured AMD is a hard error.
    public string Input(string dataType, int ordinal = 1) =>
        Resolve(_inputPorts, dataType, ordinal)
        ?? throw new InvalidOperationException(
               $"No Input port {ordinal} found for DataType '{dataType}'.");

    // Return the Output port name for the given DataType and ordinal.
    public string Output(string dataType, int ordinal = 1) =>
        Resolve(_outputPorts, dataType, ordinal)
        ?? throw new InvalidOperationException(
               $"No Output port {ordinal} found for DataType '{dataType}'.");

    // Return the Input port name, or a fallback if not found.
    public string InputOrDefault(string dataType, string fallback) =>
        Resolve(_inputPorts, dataType, 1) ?? fallback;

    public string InputOrDefault(string dataType, int ordinal, string fallback) =>
        Resolve(_inputPorts, dataType, ordinal) ?? fallback;

    // Return the Output port name, or a fallback if not found.
    public string OutputOrDefault(string dataType, string fallback) =>
        Resolve(_outputPorts, dataType, 1) ?? fallback;

    public string OutputOrDefault(string dataType, int ordinal, string fallback) =>
        Resolve(_outputPorts, dataType, ordinal) ?? fallback;

    // A declared PortNumber wins; otherwise declaration order stands in for it.
    private static string? Resolve(
        Dictionary<string, List<Entry>> ports,
        string dataType,
        int ordinal)
    {
        if (!ports.TryGetValue(dataType, out var list) || list.Count == 0)
            return null;

        if (list.Count == 1)
            return list[0].Name;

        var declared = list.FirstOrDefault(e => e.Ordinal == ordinal);
        if (declared is not null)
            return declared.Name;

        var ordered = list.OrderBy(e => e.Declared).ToList();
        return ordinal >= 1 && ordinal <= ordered.Count
            ? ordered[ordinal - 1].Name
            : null;
    }
}