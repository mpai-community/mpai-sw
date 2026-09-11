using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;

namespace Mpai.Hci.Api;

// NorthApi - the MPAI-AIF North API. The UA identifies data ONLY by
// (DataType, PortNumber). The boundary contract with the Controller is the
// typed key "DataType#PortNumber": NorthApi neither knows nor uses any port
// NAME. Outcomes are the standard AifError, surfaced faithfully; no application
// semantics, no content, no state (memory lives in the Module).
public sealed class NorthApi : IDisposable
{
    private readonly UserAgent    _ua;
    private readonly IAimProvider _provider;
    private readonly AimSettings  _settings;

    private readonly Dictionary<string, int>  _running   = new();
    private readonly Dictionary<string, bool> _suspended = new();

    public NorthApi(string amdDir, string settingsPath, IAimProvider provider)
    {
        _settings = AimSettings.Load(settingsPath);
        _provider = provider;
        var store = new AmdStore(amdDir); store.Scan();
        _ua = new UserAgent(store);
        _ua.MPAI_AIFU_Controller_Initialize();
    }

    // Overload: caller supplies a provider FACTORY, so NorthApi builds ONE AmdStore
    // and hands it to the factory (e.g. store => new MacProvider(store, galleryJson)).
    public NorthApi(string amdDir, string settingsPath, Func<AmdStore, IAimProvider> providerFactory)
    {
        _settings = AimSettings.Load(settingsPath);
        var store = new AmdStore(amdDir); store.Scan();
        _provider = providerFactory(store);
        _ua = new UserAgent(store);
        _ua.MPAI_AIFU_Controller_Initialize();
    }

    // A typed datum: DataType (+ PortNumber where a type repeats) + JSON payload.
    public readonly record struct Datum(string DataType, int PortNumber, string Json)
    {
        public Datum(string dataType, string json) : this(dataType, 1, json) { }
    }

    public readonly record struct Result(AifError Error, IReadOnlyList<Datum> Outputs, bool Suspended)
    {
        public bool Ok => Error == AifError.OK;
        public string? ByType(string dataType, int portNumber = 1) =>
            Outputs.FirstOrDefault(o => o.DataType == dataType && o.PortNumber == portNumber).Json;
    }

    public AifError StartFlow(string moduleName)
    {
        if (_running.ContainsKey(moduleName)) return AifError.OK;
        var err = _ua.MPAI_AIFU_MODULE_Start(moduleName, _provider, _settings, out var id);
        if (err == AifError.OK) { _running[moduleName] = id; _suspended[moduleName] = false; }
        return err;
    }

    public void StopFlow(string moduleName)
    {
        if (_running.TryGetValue(moduleName, out var id))
        { _ua.MPAI_AIFU_MODULE_Stop(id); _running.Remove(moduleName); _suspended.Remove(moduleName); }
    }

    // The boundary key the Controller routes on: DataType + PortNumber. Ports of
    // the same type are told apart only by number; a single occurrence is #1.
    private static string Key(string dataType, int portNumber) => dataType + "#" + portNumber;

    public Result Advance(string moduleName, IEnumerable<Datum> inputs)
    {
        bool ephemeral = !_running.ContainsKey(moduleName);
        if (ephemeral)
        {
            var e = StartFlow(moduleName);
            if (e != AifError.OK) return new Result(e, Array.Empty<Datum>(), false);
        }
        int id = _running[moduleName];

        // Typed boundary: keyed by (DataType, PortNumber). No port name anywhere.
        var boundary = new Dictionary<string, string>();
        foreach (var d in inputs)
            boundary[Key(d.DataType, d.PortNumber)] = d.Json;

        bool resuming = _suspended.TryGetValue(moduleName, out var s) && s;
        var (err, outcome) = (resuming
            ? _ua.ResumeAsync(id, boundary)
            : _ua.RunAsync(id, boundary)).GetAwaiter().GetResult();

        if (err != AifError.OK)
        { if (ephemeral) StopFlow(moduleName); return new Result(err, Array.Empty<Datum>(), false); }

        if (outcome is not null && outcome.Suspended)
        {
            _suspended[moduleName] = true;
            return new Result(AifError.OK, Array.Empty<Datum>(), true);
        }
        _suspended[moduleName] = false;

        // Outputs come back keyed by (DataType, PortNumber) too - parse the key.
        var outs = new List<Datum>();
        if (outcome?.Completed is { IsError: false } msg)
            foreach (var kv in msg.Ports)
            {
                var hash = kv.Key.LastIndexOf('#');
                if (hash <= 0) continue;
                var dt = kv.Key.Substring(0, hash);
                var pn = int.TryParse(kv.Key.Substring(hash + 1), out var n) ? n : 1;
                outs.Add(new Datum(dt, pn, kv.Value));
            }

        if (ephemeral) StopFlow(moduleName);
        return new Result(AifError.OK, outs, false);
    }

    public void Dispose()
    {
        foreach (var id in _running.Values) _ua.MPAI_AIFU_MODULE_Stop(id);
        _running.Clear(); _suspended.Clear();
        (_provider as IDisposable)?.Dispose();
    }
}