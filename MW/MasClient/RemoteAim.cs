using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;
using Mpai.Hci.Api;

namespace Mpai.Mas.Client;

// A SUB-AIM THAT RUNS ON ANOTHER MACHINE. To the Controller this is an AIM like
// any other: it has the AIM's Instance identifier and answers a Message with a
// Message. What it does is carry that exchange over MPAI-MAS to the machine where
// the AIM runs - a MAS Service offering that AIM as a Module of its own - and
// bring its outputs back.
//
// MPAI-MAS V1.0 allows this: a Sub-AIM whose Relation is External, Private or
// Public runs on its own machine, and an Implementations entry names the MAS API
// port that machine embeds. What crosses is only that AIM's Ports, in the wire
// form of their Data Types - so every Data Type on its Ports must be one the two
// Services carry.
public sealed class RemoteAim : IAimProcessor, IDisposable
{
    private sealed record Port(string Name, string DataType, int Number);

    private readonly RemoteNorthApi north;
    private readonly List<Port> inputs = new();
    private readonly List<Port> outputs = new();
    private readonly Action<string> say;

    public string InstanceId { get; }
    public string Where { get; }

    public RemoteAim(string instanceId, AmdStore store, string serviceUrl, string? bearerToken = null, Action<string>? say = null)
    {
        InstanceId = instanceId;
        Where = serviceUrl;
        this.say = say ?? (_ => { });
        north = new RemoteNorthApi(serviceUrl, bearerToken);
        ReadPorts(store, instanceId);
    }

    // The AIM's own Ports, from its L3: a Port keeps its name here, because the
    // Message the Controller hands over is keyed by name; what crosses the wire is
    // its Data Type and number.
    private void ReadPorts(AmdStore store, string aimName)
    {
        var identifier = store.FindByAimName(aimName);
        if (identifier is null) { say($"  {aimName}: no L3 here, so its Ports are unknown."); return; }
        var l3 = store.GetAMD(identifier).RootElement;
        if (!l3.TryGetProperty("ExternalPorts", out var ports) || ports.ValueKind != JsonValueKind.Array) return;

        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var port in ports.EnumerateArray())
        {
            var name = port.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            var direction = port.TryGetProperty("Direction", out var d) ? d.GetString() ?? "" : "";
            if (name.Length == 0 || direction is not ("Input" or "Output")) continue;

            foreach (var dataType in DataTypes(port))
            {
                var key = direction + dataType;
                counted[key] = counted.TryGetValue(key, out var seen) ? seen + 1 : 1;
                var number = port.TryGetProperty("PortNumber", out var pn) && pn.ValueKind == JsonValueKind.Number
                    ? pn.GetInt32()
                    : counted[key];                      // not declared: its place among its own Data Type
                (direction == "Input" ? inputs : outputs).Add(new Port(name, dataType, number));
            }
        }
    }

    private static IEnumerable<string> DataTypes(JsonElement port)
    {
        if (!port.TryGetProperty("DataType", out var t)) yield break;
        if (t.ValueKind == JsonValueKind.Array)
            foreach (var one in t.EnumerateArray()) { if (one.GetString() is { Length: > 0 } s) yield return s; }
        else if (t.GetString() is { Length: > 0 } single) yield return single;
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var datums = new List<NorthApi.Datum>();
        foreach (var port in inputs)
            if (message.Ports.TryGetValue(port.Name, out var json) && !string.IsNullOrWhiteSpace(json))
                datums.Add(new NorthApi.Datum(port.DataType, port.Number, json));

        if (datums.Count == 0)
            return new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string>() };

        // One exchange with the machine that runs this AIM.
        var result = await Task.Run(() => north.Advance(InstanceId, datums));
        if (result.Error != AifError.OK)
        {
            say($"  {InstanceId} at {Where}: {result.Error}");
            return new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string>() };
        }

        var produced = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var datum in result.Outputs)
            if (outputs.FirstOrDefault(p => p.DataType == datum.DataType && p.Number == datum.PortNumber) is { } port)
                produced[port.Name] = datum.Json;

        return new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = produced };
    }

    public void Dispose()
    {
        try { north.StopFlow(InstanceId); } catch { /* a Module already gone is stopped */ }
    }
}
