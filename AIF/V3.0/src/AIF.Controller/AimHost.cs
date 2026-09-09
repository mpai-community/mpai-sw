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

    public void RegisterRuntime(IAimProcessor processor)
    {
        _processors[processor.InstanceId] = processor;
        _lifecycles[processor.InstanceId] = new AimLifecycle();
    }

    public bool Contains(string instanceId) =>
        _processors.ContainsKey(instanceId);

    public AimState GetState(string instanceId) =>
        _lifecycles.TryGetValue(instanceId, out var lc)
            ? lc.State
            : AimState.Idle;

    // ── Lifecycle API (MPAI-AIF Basic API section 4.4) ───────────────────────

    // MPAI_AIFM_AIM_Start: prepare the AIM for a new run and return the
    // AimContext the caller should embed in the Message for this invocation.
    public AimContext StartAim(string instanceId)
    {
        if (!_lifecycles.TryGetValue(instanceId, out var lc))
            throw new InvalidOperationException(
                $"No AIM registered for {instanceId}.");
        return lc.Start();
    }

    // MPAI_AIFM_AIM_Stop: signal the AIM to stop at its next yield point.
    public void StopAim(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Stop();
    }

    // MPAI_AIFM_AIM_Pause: close the AIM's pause gate so it blocks.
    public void PauseAim(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Pause();
    }

    // MPAI_AIFM_AIM_Resume: open the AIM's pause gate so it continues.
    public void ResumeAim(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Resume();
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    // Called by MachineExecutor for normal (non-interactive) AIMs.
    // Automatically starts and the AIM runs to completion.
    public Task<Message> ProcessAsync(string instanceId, Message message)
    {
        if (!_processors.TryGetValue(instanceId, out var processor))
            throw new InvalidOperationException(
                $"No implementation is registered for {instanceId}.");

        // Embed an AimContext in the message so the processor can honour
        // lifecycle signals without holding a reference to AimLifecycle.
        var context = StartAim(instanceId);
        var msg     = message with { Context = context };

        return processor.ProcessAsync(msg);
    }

    // Called by AifAmqSession for interactive AIMs (e.g. CAE-AOA).
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
        foreach (var lc in _lifecycles.Values)
            lc.Dispose();
    }
}
