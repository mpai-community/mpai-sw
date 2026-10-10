namespace AIF.Controller;

// Holds the instantiated AIMs and is the SOLE gateway through which the
// application interacts with them — consistent with the zero-trust principle
// that no entity communicates with another except via the Controller.
//
// Implements the four lifecycle operations from the MPAI-AIF Basic API
// (section 4.4): Start, Pause, Resume, Stop — applied per AIM.
// The AIM itself never holds a lifecycle object; it receives an AimContext
// snapshot for each ProcessAsync call, containing only the signals it needs.
public sealed class AimHost : IDisposable
{
    private readonly Dictionary<string, IAimProcessor> _processors = new();
    private readonly Dictionary<string, AimLifecycle>  _lifecycles = new();

    // THE MODULE'S OWN PAUSE AND STOP (M3213 3.4). This host holds every AIM of
    // one Module, at every depth, so what is done here reaches them all. The gate
    // is held until Resume, across runs: an AIM about to run waits at it, and an
    // AIM already running is paused through its context, if it honours one.
    private readonly object _module = new();
    private TaskCompletionSource _running = Open();
    private readonly CancellationTokenSource _stopped = new();

    private static TaskCompletionSource Open()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.TrySetResult();
        return gate;
    }

    // Each AIM's status, why, and what it reported in the last run (M3213 3.5).
    private readonly Dictionary<string, AimStatus>    _status  = new();
    private readonly Dictionary<string, string>       _reason  = new();
    private readonly Dictionary<string, List<string>> _reports = new();

    public void RegisterRuntime(IAimProcessor processor)
    {
        _processors[processor.InstanceId] = processor;
        _lifecycles[processor.InstanceId] = new AimLifecycle();
        _status[processor.InstanceId]     = AimStatus.Alive;
        _reason[processor.InstanceId]     = string.Empty;
        _reports[processor.InstanceId]    = new List<string>();
    }

    public bool Contains(string instanceId) =>
        _processors.ContainsKey(instanceId);

    // The implementation that runs an AIM here.
    public IAimProcessor? ProcessorOf(string instanceId) =>
        _processors.GetValueOrDefault(instanceId);

    public AimState GetState(string instanceId) =>
        _lifecycles.TryGetValue(instanceId, out var lc)
            ? lc.State
            : AimState.Idle;

    // ── Lifecycle API (MPAI-AIF Basic API section 4.4) ───────────────────────

    // MPAI_AIFM_AIM_Start: prepare the AIM for a new run and return the
    // AimContext the caller should embed in the Message for this invocation.
    public AimContext MPAI_AIFM_AIM_Start(string instanceId)
    {
        if (!_lifecycles.TryGetValue(instanceId, out var lc))
            throw new InvalidOperationException(
                $"No AIM registered for {instanceId}.");
        return lc.Start();
    }

    // MPAI_AIFM_AIM_Stop: signal the AIM to stop at its next yield point.
    public void MPAI_AIFM_AIM_Stop(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Stop();
    }

    // MPAI_AIFM_AIM_Pause: close the AIM's pause gate so it blocks.
    public void MPAI_AIFM_AIM_Pause(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Pause();
    }

    // MPAI_AIFM_AIM_Resume: open the AIM's pause gate so it continues.
    public void MPAI_AIFM_AIM_Resume(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Resume();
    }

    // MPAI_AIFU_MODULE_Pause: every AIM completes what it is doing and waits.
    public void PauseModule()
    {
        lock (_module)
        {
            if (_running.Task.IsCompleted)
                _running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var lc in _lifecycles.Values) lc.Pause();
        }
    }

    // MPAI_AIFU_MODULE_Resume.
    public void ResumeModule()
    {
        lock (_module)
        {
            _running.TrySetResult();
            foreach (var lc in _lifecycles.Values) lc.Resume();
        }
    }

    // MPAI_AIFU_MODULE_Stop: every AIM that is running is signalled, and none
    // runs again.
    public void StopModule()
    {
        lock (_module)
        {
            _stopped.Cancel();
            _running.TrySetResult();
            foreach (var lc in _lifecycles.Values) lc.Stop();
            foreach (var aim in _status.Keys.ToList())
                if (_status[aim] != AimStatus.Dead) { _status[aim] = AimStatus.Dead; _reason[aim] = "its Module was stopped"; }
        }
    }

    public bool IsStopped => _stopped.IsCancellationRequested;

    // ── Status, degradation and reporting (M3213 3.5) ────────────────────────

    // A run begins: the reports of the last one are discarded.
    public void BeginRun()
    {
        lock (_module)
            foreach (var reports in _reports.Values) reports.Clear();
    }

    // MPAI_AIFM_AIM_Report, conveyed: logged, kept for Status, and the AIM is
    // DEGRADED for the run. Nothing else is done with it.
    public void Report(string aim, string text)
    {
        Console.WriteLine($"[AIF] {aim} reports: {text}");
        lock (_module)
        {
            if (!_reports.TryGetValue(aim, out var reports)) return;
            reports.Add(text);
            if (_status[aim] != AimStatus.Dead) { _status[aim] = AimStatus.Degraded; _reason[aim] = "it reported"; }
        }
    }

    // The AIM failed in this run: it threw, or returned an error.
    public void Degrade(string aim, string reason)
    {
        lock (_module)
            if (_status.TryGetValue(aim, out var status) && status != AimStatus.Dead)
            { _status[aim] = AimStatus.Degraded; _reason[aim] = reason; }
    }

    // The AIM completed a run without failing or reporting: ALIVE again.
    public void Succeeded(string aim)
    {
        lock (_module)
            if (_status.TryGetValue(aim, out var status) && status == AimStatus.Degraded && _reports[aim].Count == 0)
            { _status[aim] = AimStatus.Alive; _reason[aim] = string.Empty; }
    }

    // StopAIM, MPAI_AIFM_AIM_Stop and MPAI_AIFU_AIM_Stop: the AIM is signalled,
    // is DEAD, and does not run again in this Module. False if there is no such AIM.
    public bool StopAim(string aim, string reason = "stopped")
    {
        lock (_module)
        {
            if (!_lifecycles.TryGetValue(aim, out var lc)) return false;
            if (_status[aim] != AimStatus.Dead) { _status[aim] = AimStatus.Dead; _reason[aim] = reason; }
            lc.Stop();
        }
        AimStopped?.Invoke(aim, reason);
        return true;
    }

    // AN AIM ON ANOTHER MACHINE (M3217 3.3). Told of every AIM stopped here: the
    // Controller passes it on to the host the AIM runs on. And where an AIM here
    // stops another, who does it: on a host, the Controller, which holds the Module.
    public Action<string, string>? AimStopped { get; set; }
    public Func<string, string, bool>? StopAimBy { get; set; }

    // The Controller's Access, which the AIMs held here read (AIF V3.0, Storage 6):
    // its store, or - on an AIM host - the Controller's, read over the link.
    public IAccessReader? Access { get; set; }

    public bool IsDead(string aim)
    {
        lock (_module)
            return _status.TryGetValue(aim, out var status) && status == AimStatus.Dead;
    }

    public AimStatus StatusOf(string aim)
    {
        lock (_module)
            return _status.TryGetValue(aim, out var status) ? status : AimStatus.Dead;
    }

    public IReadOnlyList<AimReport> Status()
    {
        lock (_module)
            return _status.Keys.OrderBy(a => a, StringComparer.Ordinal)
                .Select(a => new AimReport(a, _status[a], _reason[a], _reports[a].ToList()))
                .ToList();
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    // Called by the executor for normal (non-interactive) AIMs.
    // Waits while the Module is paused, refuses once it is stopped, then starts
    // the AIM, which runs to completion.
    public async Task<Message> ProcessAsync(string instanceId, Message message)
    {
        if (!_processors.TryGetValue(instanceId, out var processor))
            throw new InvalidOperationException(
                $"No implementation is registered for {instanceId}.");

        // Embed an AimContext in the message so the processor can honour
        // lifecycle signals without holding a reference to AimLifecycle.
        var context = await ContextAsync(instanceId);
        if (!TurnTrace.Enabled) return await processor.ProcessAsync(message with { Context = context });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { return await processor.ProcessAsync(message with { Context = context }); }
        finally { TurnTrace.Line($"aim {instanceId} {clock.ElapsedMilliseconds} ms"); }
    }

    // The implementation registered for an AIM.
    public IAimProcessor? Processor(string instanceId) =>
        _processors.TryGetValue(instanceId, out var processor) ? processor : null;

    // An AIM about to run: waits while the Module is paused, refuses once the
    // Module or the AIM is stopped, then starts the AIM's lifecycle and gives the
    // context it runs with - Pause, Stop, Report, StopAim.
    public async Task<AimContext> ContextAsync(string instanceId)
    {
        await WhilePausedAsync();
        _stopped.Token.ThrowIfCancellationRequested();
        if (IsDead(instanceId))
            throw new OperationCanceledException($"{instanceId} is stopped.");

        var context = MPAI_AIFM_AIM_Start(instanceId);
        lock (_module)
            if (!_running.Task.IsCompleted) _lifecycles[instanceId].Pause();   // paused as it started
        return context.WithHost(text => Report(instanceId, text),
            aim => StopAimBy?.Invoke(aim, instanceId) ?? StopAim(aim, $"stopped by {instanceId}"), Access);
    }

    // Completes when the Module is not paused.
    public Task WhilePausedAsync()
    {
        lock (_module) return _running.Task;
    }

    // Cancelled when the Module is stopped.
    public CancellationToken Stopping => _stopped.Token;

    // Called by AifAmqSession for interactive AIMs.
    // The caller starts the AIM, lets it run, then calls StopAim when ready.
    public Task<Message> ProcessWithContextAsync(
        string     instanceId,
        Message    message,
        AimContext context)
    {
        if (!_processors.TryGetValue(instanceId, out var processor))
            throw new InvalidOperationException(
                $"No implementation is registered for {instanceId}.");

        var msg = message with { Context = context };
        return processor.ProcessAsync(msg);
    }

    public void Dispose()
    {
        StopModule();
        foreach (var lc in _lifecycles.Values)
            lc.Dispose();
    }
}
