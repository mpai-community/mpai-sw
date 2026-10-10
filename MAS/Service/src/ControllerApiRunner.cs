using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

using AIF.Controller;
using AIF.Store;

using Mpai.Aif.Api;
using Mpai.Mas.Server;

namespace Mpai.Mas.Service;

// Adapts the Controller API to what the MAS server needs.
//
// THIS IS THE ONLY PLACE THE TWO MEET. ControllerApi speaks Datum(DataType,
// PortNumber, Json); the MAS server speaks the boundary key "DataType#Number".
// Neither had to change: the translation is here, in one class, and it is the
// only code in the server half that knows an application exists.
internal sealed class ControllerApiRunner : IModuleRunner
{
    private readonly ControllerApi north;
    private readonly AmdStore store;

    // The Ports of each Module, read once from its AMD.
    private readonly ConcurrentDictionary<string, IReadOnlyList<BoundaryPort>> ports =
        new(StringComparer.Ordinal);

    // ONE MODULE, MANY PEOPLE. The Controller API runs one instance of each Module,
    // and every client that starts it shares that instance. So a Module is
    // stopped only when the last of those who started it has stopped it - the
    // Service's own start at load time counts as one, and is never undone, so a
    // person pressing Stop ends their own App and nobody else's.
    private readonly Dictionary<string, int> holders = new(StringComparer.Ordinal);

    // And the shared instance runs one exchange at a time: its AIMs' lifecycles
    // are reset at each run, so two runs of one Module overlapping would cancel
    // each other. Different Modules still run side by side.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> turns =
        new(StringComparer.Ordinal);

    public ControllerApiRunner(
        ControllerApi north,
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

        // Not disposed: the document belongs to the store, which keeps it.
        var amd = store.GetAMD(identifier);

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
            if (error != AifError.OK) return error.ToString();

            lock (holders)
                holders[moduleName] = holders.GetValueOrDefault(moduleName) + 1;
            return null;
        }
        catch (Exception failure)
        {
            return failure.Message;
        }
    }

    public void Stop(
        string moduleName)
    {
        lock (holders)
        {
            if (!holders.TryGetValue(moduleName, out var count)) return;
            if (count > 1) { holders[moduleName] = count - 1; return; }
            holders.Remove(moduleName);
        }

        // The last holder: no run of it can be in progress for anyone else.
        var turn = turns.GetOrAdd(moduleName, _ => new SemaphoreSlim(1, 1));
        turn.Wait();
        try { north.StopFlow(moduleName); }
        finally { turn.Release(); }
    }

    public RunResult Run(
        string moduleName,
        IReadOnlyDictionary<string, string> inputs) => Run(moduleName, inputs, null);

    public void SessionEnded(string session) => north.SessionEnded(session);

    public RunResult Run(
        string moduleName,
        IReadOnlyDictionary<string, string> inputs,
        string? session)
    {
        var data = new List<ControllerApi.Datum>();
        foreach (var pair in inputs)
        {
            var hash = pair.Key.LastIndexOf('#');
            var type = hash > 0 ? pair.Key.Substring(0, hash) : pair.Key;
            var num  = hash > 0 && int.TryParse(pair.Key.Substring(hash + 1), out var n) ? n : 1;
            data.Add(new ControllerApi.Datum(type, num, pair.Value));
        }

        var turn = turns.GetOrAdd(moduleName, _ => new SemaphoreSlim(1, 1));
        ControllerApi.Result result;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        turn.Wait();
        var waited = clock.ElapsedMilliseconds;
        try { result = session is null ? north.Advance(moduleName, data) : north.Advance(moduleName, data, session); }
        finally
        {
            turn.Release();
            AIF.Controller.TurnTrace.Line($"turn {moduleName} ({string.Join(", ", inputs.Keys)}): waited {waited} ms for the Module, ran {clock.ElapsedMilliseconds - waited} ms");
        }

        if (result.Error != AifError.OK)
            return new RunResult { Error = result.Error.ToString() };

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var datum in result.Outputs)
            outputs[datum.DataType + "#" + datum.PortNumber] = datum.Json;

        return new RunResult { Outputs = outputs };
    }
}