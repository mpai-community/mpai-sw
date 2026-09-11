using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;

namespace Mpai.Hci.Api;

// NorthApi - the MPAI-AIF North API. The UA identifies data by DATA TYPE (+ Port
// Number where a type repeats); (DataType,PortNumber) <-> boundary port name is
// resolved from the Module's L3; outcomes are the standard AifError, surfaced
// faithfully; no application semantics, no content, no state (memory lives in the
// Module). The caller supplies the IAimProvider (or a factory over the AmdStore).
public sealed class NorthApi : IDisposable
{
    private readonly UserAgent    _ua;
    private readonly IAimProvider _provider;
    private readonly AimSettings  _settings;
    private readonly string       _amdDir;

    private readonly Dictionary<string, int>     _running = new();
    private readonly Dictionary<string, bool>    _suspended = new();
    private readonly Dictionary<string, PortMap> _maps = new();

    public NorthApi(string amdDir, string settingsPath, IAimProvider provider)
    {
        _amdDir = amdDir;
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
        _amdDir = amdDir;
        _settings = AimSettings.Load(settingsPath);
        var store = new AmdStore(amdDir); store.Scan();
        _provider = providerFactory(store);
        _ua = new UserAgent(store);
        _ua.MPAI_AIFU_Controller_Initialize();
    }

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

    public Result Advance(string moduleName, IEnumerable<Datum> inputs)
    {
        bool ephemeral = !_running.ContainsKey(moduleName);
        if (ephemeral)
        {
            var e = StartFlow(moduleName);
            if (e != AifError.OK) return new Result(e, Array.Empty<Datum>(), false);
        }
        int id = _running[moduleName];
        var map = MapFor(moduleName);

        var boundary = new Dictionary<string, string>();
        foreach (var d in inputs)
        {
            var name = map.InputName(d.DataType, d.PortNumber)
                ?? throw new InvalidOperationException(
                    $"{moduleName}: no boundary input of type {d.DataType} #{d.PortNumber} in its L3.");
            boundary[name] = d.Json;
        }

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

        var outs = new List<Datum>();
        if (outcome?.Completed is { IsError: false } msg)
            foreach (var kv in msg.Ports)
                foreach (var (dt, pn) in map.OutputTypes(kv.Key))
                    outs.Add(new Datum(dt, pn, kv.Value));

        if (ephemeral) StopFlow(moduleName);
        return new Result(AifError.OK, outs, false);
    }

    private PortMap MapFor(string moduleName)
    {
        if (_maps.TryGetValue(moduleName, out var m)) return m;
        m = PortMap.FromAmd(_amdDir, moduleName);
        _maps[moduleName] = m;
        return m;
    }

    public void Dispose()
    {
        foreach (var id in _running.Values) _ua.MPAI_AIFU_MODULE_Stop(id);
        _running.Clear(); _suspended.Clear();
        (_provider as IDisposable)?.Dispose();
    }

    private sealed class PortMap
    {
        private readonly Dictionary<string, string> _in = new();                       // dt|pn -> name
        private readonly Dictionary<string, List<(string dt, int pn)>> _outByName = new(); // name -> [(dt,pn)]

        public string? InputName(string dt, int pn) => _in.TryGetValue(dt + "|" + pn, out var n) ? n : null;
        public IEnumerable<(string dt, int pn)> OutputTypes(string name) =>
            _outByName.TryGetValue(name, out var v) ? v : Enumerable.Empty<(string, int)>();

        public static PortMap FromAmd(string amdDir, string moduleName)
        {
            var pm = new PortMap();
            string path = System.IO.Path.Combine(amdDir, "1" + moduleName + "-I01.json");
            if (!System.IO.File.Exists(path))
                foreach (var f in System.IO.Directory.EnumerateFiles(amdDir, "1*-I01.json"))
                    if (System.IO.File.ReadAllText(f).Contains("\"AIMName\": \"" + moduleName + "\"")) { path = f; break; }
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("ExternalPorts", out var ports)) return pm;
            foreach (var p in ports.EnumerateArray())
            {
                var name = p.GetProperty("Name").GetString() ?? "";
                var dir  = p.GetProperty("Direction").GetString() ?? "";
                int pn   = p.TryGetProperty("PortNumber", out var pne) ? pne.GetInt32() : 1;
                foreach (var dt in DataTypes(p))
                {
                    if (dir == "Input") { _ = pm._in.TryAdd(dt + "|" + pn, name); }
                    else
                    {
                        if (!pm._outByName.TryGetValue(name, out var list)) { list = new(); pm._outByName[name] = list; }
                        list.Add((dt, pn));
                    }
                }
            }
            return pm;
        }

        private static IEnumerable<string> DataTypes(JsonElement port)
        {
            if (!port.TryGetProperty("DataType", out var d)) yield break;
            if (d.ValueKind == JsonValueKind.String) yield return d.GetString()!;
            else if (d.ValueKind == JsonValueKind.Array)
                foreach (var e in d.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) yield return e.GetString()!;
        }
    }
}