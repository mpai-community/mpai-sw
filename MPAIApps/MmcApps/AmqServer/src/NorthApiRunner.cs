using System;
using System.Collections.Generic;
using System.Text.Json;

using AIF.Controller;
using AIF.Store;

using Mpai.Hci.Api;
using Mpai.Mas.Server;

namespace MmcAmq.Server;

// Adapts the North API to what the MAS server needs.
//
// THIS IS THE ONLY PLACE THE TWO MEET. NorthApi speaks Datum(DataType,
// PortNumber, Json); the MAS server speaks the boundary key "DataType#Number".
// Neither had to change: the translation is here, in one class, and it is the
// only code in the server half that knows an application exists.
internal sealed class NorthApiRunner : IModuleRunner
{
    private readonly NorthApi north;
    private readonly AmdStore store;

    // The Ports of each Module, read once from its AMD.
    private readonly Dictionary<string, IReadOnlyList<BoundaryPort>> ports =
        new(StringComparer.Ordinal);

    public NorthApiRunner(
        NorthApi north,
        AmdStore store)
    {
        this.north = north;
        this.store = store;
    }

    public IReadOnlyList<BoundaryPort> PortsOf(
        string moduleName)
    {
        if (ports.TryGetValue(moduleName, out var known)) return known;

        var identifier = store.FindByAimName(moduleName)
            ?? throw new InvalidOperationException(
                   $"No AMD for Module '{moduleName}'.");

        using var amd = store.GetAMD(identifier);

        var read = BoundaryPorts.FromAmd(amd.RootElement);
        ports[moduleName] = read;
        return read;
    }

    public string? Start(
        string moduleName)
    {
        // A MODULE'S OWN FAULT MUST NOT STOP THE OTHERS. Building an AIM can throw -
        // a missing model file, for instance - and that exception is this one
        // Module's problem alone: the Service still has four other Modules to try,
        // and a machine that hosts only some AIMs (a remote Sub-AIM's own machine,
        // say) should say which failed rather than never start at all.
        try
        {
            var error = north.StartFlow(moduleName);
            return error == AifError.OK ? null : error.ToString();
        }
        catch (Exception failure)
        {
            return failure.Message;
        }
    }

    public void Stop(
        string moduleName) =>
        north.StopFlow(moduleName);

    public RunResult Run(
        string moduleName,
        IReadOnlyDictionary<string, string> inputs)
    {
        var data = new List<NorthApi.Datum>();
        foreach (var pair in inputs)
        {
            var hash = pair.Key.LastIndexOf('#');
            var type = hash > 0 ? pair.Key.Substring(0, hash) : pair.Key;
            var num  = hash > 0 && int.TryParse(pair.Key.Substring(hash + 1), out var n) ? n : 1;
            data.Add(new NorthApi.Datum(type, num, pair.Value));
        }

        var result = north.Advance(moduleName, data);

        if (result.Error != AifError.OK)
            return new RunResult { Error = result.Error.ToString() };

        if (result.Suspended)
            return new RunResult { Suspended = true };

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var datum in result.Outputs)
            outputs[datum.DataType + "#" + datum.PortNumber] = datum.Json;

        return new RunResult { Outputs = outputs };
    }
}