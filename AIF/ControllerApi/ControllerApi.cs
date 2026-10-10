using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;

namespace Mpai.Aif.Api;

// ControllerApi - the MPAI-AIF Controller API. The UA identifies data ONLY by
// (DataType, PortNumber). The boundary contract with the Controller is the
// typed key "DataType#PortNumber": ControllerApi neither knows nor uses any port
// NAME. Outcomes are the standard AifError, surfaced faithfully; no application
// semantics, no content, no state (memory lives in the Module).
public sealed class ControllerApi : IControllerApi, IDisposable
{
    // THE FRAMEWORK OFFERS THE PLACE; THIS KNOWS WHAT TO LOOK FOR. AIF.Controller
    // routes Data Types and payloads and does not know what an MPAI Object is.
    // The Controller API knows both, so the inspector is installed here - once, for every
    // application and both servers, since all of them construct a ControllerApi.
    static ControllerApi()
    {
        AIF.Controller.ContinuousExecutor.ObjectInspector = Mpai.Core.QualifierCheck.Inspect;
    }

    // THE CODECS RESOLVE A REFERENCE THIS CONTROLLER ISSUED (M3215 3.6), through the
    // User Agents of the Controller APIs in this process.
    private static readonly List<WeakReference<UserAgent>> Resolvers = new();

    private static byte[]? ResolvePayload(string reference)
    {
        lock (Resolvers)
            foreach (var weak in Resolvers)
                if (weak.TryGetTarget(out var ua) && ua.ResolvePayload(reference) is { } data) return data;
        return null;
    }

    private void OfferPayloads()
    {
        lock (Resolvers) Resolvers.Add(new WeakReference<UserAgent>(_ua));
        Mpai.Aif.PortData.PayloadReferences.Resolve = ResolvePayload;
    }

    private readonly UserAgent    _ua;
    private readonly IAimProvider _provider;
    private readonly AimSettings  _settings;

    // A Service calls this from many requests at once, for different Modules.
    // The table is touched only under _tables; a run itself happens outside it,
    // so one Module's run never waits for another's.
    private readonly Dictionary<string, Started> _running = new();
    private readonly object _tables = new();

    // A started Module: its id, what has been written to it since its last run,
    // and that run.
    private sealed class Started
    {
        public required int Id { get; init; }

        // Its policy stopped it (OnDegraded StopModule): nothing more is written
        // to it, and its status can still be asked for, until it is started again.
        public bool Stopped { get; set; }
        public Dictionary<string, string> Written { get; } = new();
        public Task<Result>? Run { get; set; }

        // The exchange on the Module's Channels, when it runs there: a boundary
        // Output Port settles before the whole run completes (M3215 3.4).
        public ContinuousExecutor.ExchangeRun? Exchange { get; set; }
    }

    public ControllerApi(string amdDir, string settingsPath, IAimProvider provider)
    {
        _settings = AimSettings.Load(settingsPath);
        _provider = provider;
        var store = new AmdStore(amdDir); store.Scan();
        _ua = new UserAgent(store, Mpai.Core.MpaiPaths.SharedStorage);
        _ua.MPAI_AIFU_Controller_Initialize();
        _ua.Access = new AccessStore(Mpai.Core.MpaiPaths.Access);
        OfferPayloads();
    }

    // Overload: caller supplies a provider FACTORY, so ControllerApi builds ONE AmdStore
    // and hands it to the factory (e.g. store => new MacProvider(store, galleryJson)).
    public ControllerApi(string amdDir, string settingsPath, Func<AmdStore, IAimProvider> providerFactory)
    {
        _settings = AimSettings.Load(settingsPath);
        var store = new AmdStore(amdDir); store.Scan();
        _provider = providerFactory(store);
        _ua = new UserAgent(store, Mpai.Core.MpaiPaths.SharedStorage);
        _ua.MPAI_AIFU_Controller_Initialize();
        _ua.Access = new AccessStore(Mpai.Core.MpaiPaths.Access);
        OfferPayloads();
    }

    // A typed datum: DataType (+ PortNumber where a type repeats) + JSON payload.
    public readonly record struct Datum(string DataType, int PortNumber, string Json)
    {
        public Datum(string dataType, string json) : this(dataType, 1, json) { }
    }

    // What one exchange returned.
    public readonly record struct Result(AifError Error, IReadOnlyList<Datum> Outputs)
    {
        public bool Ok => Error == AifError.OK;
        public string? ByType(string dataType, int portNumber = 1) =>
            Outputs.FirstOrDefault(o => o.DataType == dataType && o.PortNumber == portNumber).Json;
    }

    // What Status returned: an outcome, and each AIM's status and reports.
    public readonly record struct ModuleStatus(AifError Error, IReadOnlyList<AimReport> Aims)
    {
        public bool Ok => Error == AifError.OK;

        // While the Module is recorded: per Port, what is recorded and what is not
        // (M3219 3.4).
        public IReadOnlyDictionary<BoundaryRecord.PortKey, (long Recorded, long NotRecorded)>? Record { get; init; }
    }

    // What one OutputRead returned: an outcome, and the datum when it is OK.
    public readonly record struct Read(AifError Error, string? Json)
    {
        public bool Ok => Error == AifError.OK;
    }

    public AifError StartFlow(string moduleName)
    {
        lock (_tables)
        {
            if (_running.TryGetValue(moduleName, out var already))
            {
                if (!already.Stopped) return AifError.OK;
                _ua.MPAI_AIFU_MODULE_Stop(already.Id);
                _running.Remove(moduleName);
            }
            var err = _ua.MPAI_AIFU_MODULE_Start(moduleName, _provider, _settings, out var id);
            if (err == AifError.OK) _running[moduleName] = new Started { Id = id };
            return err;
        }
    }

    public void StopFlow(string moduleName)
    {
        List<AIF.Channels.ExternalHub>? hubs;
        lock (_tables)
        {
            if (_running.Remove(moduleName, out var started))
                _ua.MPAI_AIFU_MODULE_Stop(started.Id);
            _external.Remove(moduleName, out hubs);
        }
        foreach (var hub in hubs ?? []) hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // THE EXTERNAL PORTS OF A MODULE JOINED TO OTHER CONTROLLERS (M3205 3.3; M3241 3.2):
    // its boundary Ports marked IsRemote. What its External Output Ports give is sent,
    // with its stamp, to the Controllers in range the hub links; what they send is
    // written to its External Input Ports with their controllerID and stamp. sign and
    // verify: the Controller's trust (CityTrust). The hub ends when the Module stops.
    private readonly Dictionary<string, List<AIF.Channels.ExternalHub>> _external = new();

    public AIF.Channels.ExternalHub StartExternal(string moduleName, AIF.Channels.ExternalHub.Options options,
                                                  Func<string, string>? sign = null, Func<string, string, string?>? verify = null)
    {
        int id;
        IReadOnlyList<RuntimePort> ports;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started) || !_ua.IsContinuous(started.Id))
                throw new InvalidOperationException($"{moduleName} is not a continuous Module started here.");
            id = started.Id;
            ports = (_ua.BoundaryPorts(id) ?? Array.Empty<RuntimePort>()).Where(p => p.IsRemote).ToList();
        }
        var hub = new AIF.Channels.ExternalHub(options)
        {
            Outputs = ports.Where(p => p.Direction == "Output").Select(p => (p.DataType, p.PortNumber ?? 1)).ToList(),
            ReadOutput = async (dataType, portNumber, timeoutMs) =>
            {
                var (error, json, stamp) = await _ua.ContinuousReadStampedAsync(id, dataType, portNumber, timeoutMs);
                return error == AifError.OK && json is not null ? (json, stamp) : null;
            },
            Deliver = async m =>
            {
                if (!ports.Any(p => p.Direction == "Input" && p.Accepts(m.DataType))) return;
                await _ua.ContinuousWriteAsync(id, m.DataType, m.PortNumber, m.Json, 1000, m.From, m.Stamp);
            },
            Sign = sign, Verify = verify
        };
        lock (_tables)
        {
            if (!_external.TryGetValue(moduleName, out var list)) _external[moduleName] = list = new();
            list.Add(hub);
        }
        hub.Start();
        return hub;
    }

    // The boundary key the Controller routes on: DataType + PortNumber. Ports of
    // the same type are told apart only by number; a single occurrence is #1.
    private static string Key(string dataType, int portNumber) => dataType + "#" + portNumber;

    // ---- The User Agent's data path (M3203 3.3, M3213 3.2) -------------------

    // MPAI_AIFU_MODULE_Input_Write. Supplies a datum at a boundary input Port,
    // for the Module's next run. Writing never waits here, so timeoutMs is
    // honoured trivially; it is in the signature because a Port with a Depth
    // (Phase 4) may have to.
    public AifError InputWrite(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1)
    {
        Started? continuous = null;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started) || started.Stopped) return AifError.NotStarted;

            var port = PortOf(started, "Input", dataType, portNumber);
            if (port is null) return AifError.NoSuchPort;
            if (HeaderOf(json) is { } header && !port.Accepts(header)) return AifError.TypeNotAccepted;

            if (_ua.IsContinuous(started.Id)) continuous = started;
            else
            {
                started.Written[Key(dataType, portNumber)] = json;
                return AifError.OK;
            }
        }

        // A Continuous Module: onto its boundary Channel, under its Port's
        // behaviour, within the timeout - outside the table's lock.
        return _ua.ContinuousWriteAsync(continuous.Id, dataType, portNumber, json, timeoutMs).GetAwaiter().GetResult();
    }

    // MPAI_AIFU_MODULE_Output_Read. Collects the datum of a boundary output Port.
    // The first read after one or more writes runs the Module on what was
    // written; later reads return what that run produced. timeoutMs: 0, do not
    // wait; negative, wait without limit. A run a TIMEOUT leaves behind
    // continues, and a later read returns its result.
    public Read OutputRead(string moduleName, string dataType, int portNumber, int timeoutMs = -1)
    {
        Task<Result>? run;
        int? continuous = null;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started)) return new Read(AifError.NotStarted, null);
            if (PortOf(started, "Output", dataType, portNumber) is null) return new Read(AifError.NoSuchPort, null);
            if (_ua.IsContinuous(started.Id)) { continuous = started.Id; run = null; }
            else run = RunIfWritten(moduleName, started);
        }

        // A Continuous Module: the oldest Message pending, within the timeout;
        // NOT_PRODUCED when none was (M3203 3.3).
        if (continuous is int id)
        {
            var (error, pending) = _ua.ContinuousReadAsync(id, dataType, portNumber, timeoutMs).GetAwaiter().GetResult();
            return new Read(error, pending);
        }

        if (run is null) return new Read(AifError.NotProduced, null);

        // THE EXCHANGE IS FINISHED FOR WHAT THE USER AGENT ASKS (OI-12): on the
        // Module's Channels, this Port settles as soon as it has its Message or can
        // no longer get one, whatever the rest of the run is doing.
        ContinuousExecutor.ExchangeRun? exchange;
        lock (_tables) exchange = _running.TryGetValue(moduleName, out var s) ? s.Exchange : null;
        if (exchange is not null)
        {
            var port = exchange.Port(dataType, portNumber);
            if (!Completes(port, timeoutMs)) return new Read(AifError.Timeout, null);
            if (port.Result is { } settled) return new Read(AifError.OK, settled);
            if (!Completes(run, 0) || run.Result.Error == AifError.OK) return new Read(AifError.NotProduced, null);
            return new Read(run.Result.Error, null);
        }

        if (!Completes(run, timeoutMs)) return new Read(AifError.Timeout, null);

        var result = run.Result;
        if (result.Error != AifError.OK) return new Read(result.Error, null);
        return result.ByType(dataType, portNumber) is { } json
            ? new Read(AifError.OK, json)
            : new Read(AifError.NotProduced, null);
    }

    // MPAI_AIFU_MODULE_Pause and _Resume. A run started while the Module is
    // paused waits, and a read observes its timeout.
    public AifError Pause(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started)
                ? _ua.MPAI_AIFU_MODULE_Pause(started.Id)
                : AifError.NotStarted;
    }

    public AifError Resume(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started)
                ? _ua.MPAI_AIFU_MODULE_Resume(started.Id)
                : AifError.NotStarted;
    }

    public ModuleStatus Status(string moduleName)
    {
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started)) return new ModuleStatus(AifError.NotStarted, Array.Empty<AimReport>());
            var err = _ua.MPAI_AIFU_MODULE_GetStatus(started.Id, out var aims);
            return new ModuleStatus(err, aims) { Record = _ua.RecordStatus(started.Id)?.Totals };
        }
    }

    public AifError StopAim(string moduleName, string aimName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) && !started.Stopped
                ? _ua.MPAI_AIFU_AIM_Stop(started.Id, aimName)
                : AifError.NotStarted;
    }

    // MPAI_AIFU_Payload_Put (M3215 3.6): a payload placed for a boundary Input
    // Port of a Continuous Module; its Object is then written with the reference.
    public (AifError Error, string? Reference) PayloadPut(string moduleName, string dataType, int portNumber, ReadOnlyMemory<byte> data)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started)
                ? _ua.PayloadPut(started.Id, dataType, portNumber, data)
                : (AifError.NotStarted, null);
    }

    public int PayloadsHeld(string moduleName)
    {
        lock (_tables) return _running.TryGetValue(moduleName, out var started) ? _ua.PayloadsHeld(started.Id) : 0;
    }

    // What each Channel of a Continuous Module carried (M3215 3.8).
    public IReadOnlyList<string> ChannelAccounts(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.ChannelAccounts(started.Id) : Array.Empty<string>();
    }

    // The Controller's transports and the one a Channel uses when its Output Port
    // declares none (M3215 3.1).
    public UserAgent Controller => _ua;

    // THE RECORD OF THE BOUNDARY (M3219 3.4).
    public AifError RecordStart(string moduleName, out string? recordId)
    {
        recordId = null;
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.MPAI_AIFU_Record_Start(started.Id, out recordId) : AifError.NotStarted;
    }

    public AifError RecordStop(string moduleName, out IReadOnlyDictionary<BoundaryRecord.PortKey, (long Recorded, long NotRecorded)>? totals)
    {
        totals = null;
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.MPAI_AIFU_Record_Stop(started.Id, out totals) : AifError.NotStarted;
    }

    public AIF.SharedStorage.IRuledStorage ModuleStorageAt(string moduleName, string location) =>
        _ua.ModuleStorageAt(moduleName, location);

    public string? RecordId(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.RecordStatus(started.Id)?.Id : null;
    }

    // The Private Storage of a running Module, as the User Agent may reach it:
    // under the rules of its writers or its central control (M3219 3.1).
    public AIF.SharedStorage.IRuledStorage? ModuleStorage(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.ModuleStorage(started.Id) : null;
    }

    // ACCESS (AIF V3.0, Basic API 3.9): the User's Sources, written through the User
    // Agent; a Module's AIMs read them.
    public AifError AccessCreate(string source) => _ua.MPAI_AIFU_Access_Create(source);
    public AifError AccessPut(string source, string key, byte[] data) => _ua.MPAI_AIFU_Access_Put(source, key, data);
    public AifError AccessDelete(string source, string key) => _ua.MPAI_AIFU_Access_Delete(source, key);

    public AifError SharedStorageInit(string moduleName, string location) => SharedStorageInit(moduleName, location, false);

    // WHOSE SESSION AN EXCHANGE SERVES (M3245 3.3): a Service's Controller serves
    // many clients; the data an exchange keeps for its session are that client's,
    // and deleted when the client's session ends.
    public Result Advance(string moduleName, IEnumerable<Datum> inputs, string session)
    {
        _ua.MPAI_AIFU_Session_Serve(moduleName, session);
        try { return Advance(moduleName, inputs); }
        finally { _ua.MPAI_AIFU_Session_Serve(moduleName, null); }
    }

    public void SessionEnded(string session) => _ua.MPAI_AIFU_Session_End(session);

    // How long data of a category is kept at a location, where the writer does not say.
    public AifError SharedStorageKeep(string location, string category, AIF.SharedStorage.StorageTime time) =>
        _ua.MPAI_AIFU_SharedStorage_Keep(location, category, time);

    public AifError SharedStorageInit(string moduleName, string location, bool governs)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started)
                ? _ua.MPAI_AIFU_SharedStorage_Init(started.Id, location, governs)
                : AifError.NotStarted;
    }

    // The Shared Storage at a running Module's location, as the User Agent may
    // reach it (M3219 3.3).
    public AIF.SharedStorage.IRuledStorage? SharedStorage(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.SharedStorage(started.Id) : null;
    }

    // Write every input, read every output: one exchange, built on the data path.
    // It is lenient where InputWrite is not - a datum for a Port the Module does
    // not declare is passed on, and meets no connection - because every client
    // and workflow was written against it.
    public Result Advance(string moduleName, IEnumerable<Datum> inputs)
    {
        bool ephemeral;
        lock (_tables) ephemeral = !_running.ContainsKey(moduleName);
        if (ephemeral)
        {
            var e = StartFlow(moduleName);
            if (e != AifError.OK) return new Result(e, Array.Empty<Datum>());
        }

        // A Continuous Module: the inputs written, and what is pending at its
        // boundary outputs now, one Message of each; nothing waits for more.
        int? continuousId = null;
        lock (_tables)
            if (_running.TryGetValue(moduleName, out var s) && _ua.IsContinuous(s.Id)) continuousId = s.Id;
        if (continuousId is int cid)
        {
            foreach (var d in inputs)
                _ua.ContinuousWriteAsync(cid, d.DataType, d.PortNumber, d.Json, -1).GetAwaiter().GetResult();
            var pending = new List<Datum>();
            foreach (var port in (_ua.BoundaryPorts(cid) ?? Array.Empty<RuntimePort>()).Where(p => p.Direction == "Output"))
            {
                var number = port.PortNumber ?? 1;
                var (error, json) = _ua.ContinuousReadAsync(cid, port.DataType, number, 0).GetAwaiter().GetResult();
                if (error == AifError.OK && json is not null) pending.Add(new Datum(port.DataType, number, json));
            }
            if (ephemeral) StopFlow(moduleName);
            return new Result(AifError.OK, pending);
        }

        Task<Result>? run;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started) || started.Stopped)
                return new Result(AifError.NotStarted, Array.Empty<Datum>());
            foreach (var d in inputs)
                started.Written[Key(d.DataType, d.PortNumber)] = d.Json;
            run = RunIfWritten(moduleName, started, always: true)!;
        }

        var result = run.GetAwaiter().GetResult();
        if (ephemeral) StopFlow(moduleName);
        return result;
    }

    // Starts a run on what was written, if anything was written since the last
    // one; returns the Module's latest run. Called under _tables.
    private Task<Result>? RunIfWritten(string moduleName, Started started, bool always = false)
    {
        if (started.Written.Count > 0 || always)
        {
            var boundary = new Dictionary<string, string>(started.Written);
            started.Written.Clear();
            var exchange = _ua.StartExchange(started.Id, boundary);
            started.Exchange = exchange;
            started.Run = exchange is null
                ? Task.FromResult(new Result(AifError.NotStarted, Array.Empty<Datum>()))
                : Completed(started, exchange);
        }
        return started.Run;
    }

    private async Task<Result> Completed(Started started, ContinuousExecutor.ExchangeRun exchange)
    {
        Message message;
        try { message = await exchange.Completed; }
        catch { return new Result(AifError.Failed, Array.Empty<Datum>()); }
        return ToResult(started, AifError.OK, message);
    }

    private Result ToResult(Started started, AifError err, Message? completed)
    {
        if (_ua.ModuleStopped(started.Id))
            lock (_tables) started.Stopped = true;
        if (err != AifError.OK) return new Result(err, Array.Empty<Datum>());

        // An AIM's error does not end the run (every Module continues, until
        // Step 5 gives it its policy): the run's message is marked an error when
        // the last AIM to run failed, and still carries what the others produced.
        // A cancelled run - its Module stopped - produced nothing.
        var message = completed;
        if (message is null || message.IsCancelled)
            return new Result(AifError.Failed, Array.Empty<Datum>());

        // Outputs come back keyed by (DataType, PortNumber) too - parse the key.
        var outs = new List<Datum>();
        foreach (var kv in message.Ports)
        {
            var hash = kv.Key.LastIndexOf('#');
            if (hash <= 0) continue;
            var dt = kv.Key.Substring(0, hash);
            var pn = int.TryParse(kv.Key.Substring(hash + 1), out var n) ? n : 1;
            outs.Add(new Datum(dt, pn, kv.Value));
        }
        return new Result(AifError.OK, outs);
    }

    private static bool Completes(Task task, int timeoutMs) =>
        timeoutMs < 0 ? WaitedFor(task) : task.Wait(timeoutMs);

    private static bool WaitedFor(Task task) { task.Wait(); return true; }

    // The boundary Port of that Direction, Data Type and Port Number: the one
    // declaring that number, else the n-th of its type (1 where it occurs once).
    private RuntimePort? PortOf(Started started, string direction, string dataType, int portNumber)
    {
        var ports = (_ua.BoundaryPorts(started.Id) ?? Array.Empty<RuntimePort>())
            .Where(p => p.Direction == direction && p.Accepts(dataType))
            .ToList();
        return ports.FirstOrDefault(p => p.PortNumber == portNumber)
            ?? (ports.All(p => p.PortNumber is null) && portNumber >= 1 && portNumber <= ports.Count
                ? ports[portNumber - 1]
                : null);
    }

    // The Data Type an MPAI Object says it is: its Header. Null for a datum that
    // is not an Object, which says nothing about its type.
    private static string? HeaderOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("Header", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        List<AIF.Channels.ExternalHub> hubs;
        lock (_tables)
        {
            foreach (var started in _running.Values) _ua.MPAI_AIFU_MODULE_Stop(started.Id);
            _running.Clear();
            hubs = _external.Values.SelectMany(h => h).ToList();
            _external.Clear();
        }
        foreach (var hub in hubs) hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
        (_provider as IDisposable)?.Dispose();
    }
}
