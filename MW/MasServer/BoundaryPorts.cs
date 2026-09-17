using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Mpai.Mas.Server;

// Reads a Module''s boundary Ports out of its AMD.
//
// SHARED, BECAUSE EVERY HOST NEEDS IT AND NONE OF IT IS APPLICATION-SPECIFIC.
// It takes a JsonElement rather than an AmdStore, so this library stays free of
// any particular store implementation - the host fetches the AMD however it
// likes and hands over the root.
public static class BoundaryPorts
{
    public static IReadOnlyList<BoundaryPort> FromAmd(
        JsonElement amdRoot)
    {
        var ports = new List<BoundaryPort>();

        if (!amdRoot.TryGetProperty("ExternalPorts", out var external) ||
            external.ValueKind != JsonValueKind.Array)
            return ports;

        foreach (var port in external.EnumerateArray())
        {
            var direction = port.TryGetProperty("Direction", out var d)
                ? d.GetString() ?? string.Empty
                : string.Empty;

            // DataType is either one identifier or a set of them. The FIRST is
            // the one the Controller keys the boundary by; the rest are also
            // accepted on the wire.
            var types = new List<string>();
            if (port.TryGetProperty("DataType", out var dt))
            {
                if (dt.ValueKind == JsonValueKind.String)
                {
                    var one = dt.GetString();
                    if (!string.IsNullOrEmpty(one)) types.Add(one!);
                }
                else if (dt.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in dt.EnumerateArray())
                        if (entry.ValueKind == JsonValueKind.String)
                        {
                            var one = entry.GetString();
                            if (!string.IsNullOrEmpty(one)) types.Add(one!);
                        }
                }
            }

            if (types.Count == 0) continue;

            // Absent PortNumber means 1 - matching Controller.ResolveEndpoint,
            // which defaults the same way. The two must agree or a URL would
            // address a Port the boundary key does not.
            var number = 1;
            if (port.TryGetProperty("PortNumber", out var pn) &&
                pn.ValueKind == JsonValueKind.Number)
                number = pn.GetInt32();

            var optional = port.TryGetProperty("IsOptional", out var io) &&
                           io.ValueKind == JsonValueKind.True;

            ports.Add(new BoundaryPort
            {
                Direction  = direction,
                DataType   = types[0],
                DataTypes  = types,
                PortNumber = number,
                IsOptional = optional
            });
        }

        return ports;
    }
}