namespace AIF.Controller;

// The status of an AIM of a running Module (M3203 3.7, M3213 3.5): ALIVE;
// DEGRADED, after it has failed or reported in a run; DEAD, when it or its
// Module has been stopped.
public enum AimStatus
{
    Alive,
    Degraded,
    Dead
}

// What the User Agent learns of one AIM: its status, why, and what it reported
// in the Module's last run.
public sealed record AimReport(string Aim, AimStatus Status, string Reason, IReadOnlyList<string> Reports);

// The four states of an AIM's lifecycle, as its host drives it.
public enum AimState
{
    Idle,
    Running,
    Paused,
    Stopped
}

// Per-AIM lifecycle controller held by AimHost.
// Provides the CancellationToken (for Stop) and pause gate (for Pause/Resume)
// that an AIM's ProcessAsync uses to honour lifecycle signals from the Controller.
// The AIM never holds this directly â€” it is given a snapshot (AimContext) per call.
internal sealed class AimLifecycle : IDisposable
{
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource    _pauseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AimState                _state = AimState.Idle;
    private int                     _pauseRequests;
    private readonly object         _lock = new();

    public AimState State
    {
        get { lock (_lock) return _state; }
    }

    // Called by AimHost.StartAimAsync â€” resets the lifecycle for a new run.
    public AimContext Start()
    {
        lock (_lock)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            _pauseGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pauseGate.TrySetResult();   // not paused â€” gate is open

            _pauseRequests = 0;
            _state = AimState.Running;
        }

        // The AIM is given ACCESSORS, not a snapshot.
        //
        // It used to receive _pauseGate.Task as it stood at Start - already
        // completed. Pause() then replaced the field with a fresh uncompleted
        // source, which the AIM never saw, so awaiting its copy returned at once
        // and a Pause was invisible to a running AIM. Stop was the only signal
        // that reached one, which is why press-to-stop capture had to abuse it.
        return new AimContext(
            _cts.Token,
            () => { lock (_lock) return _pauseGate.Task; },
            () => { lock (_lock) return _pauseRequests; });
    }

    // Called by AimHost.StopAimAsync.
    public void Stop()
    {
        lock (_lock)
        {
            _state = AimState.Stopped;
            _cts.Cancel();
            _pauseGate.TrySetResult();   // unblock any pause wait
        }
    }

    // Called by AimHost.PauseAimAsync.
    public void Pause()
    {
        lock (_lock)
        {
            if (_state != AimState.Running) return;
            _state = AimState.Paused;

            // The counter, not the gate, is what an AIM should test to learn that
            // a Pause was ASKED FOR. The gate says whether it is paused right now,
            // which a quick Pause-then-Resume can pass through unseen; the counter
            // cannot be missed however briefly the pause lasts.
            _pauseRequests++;

            _pauseGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // Called by AimHost.ResumeAimAsync.
    public void Resume()
    {
        lock (_lock)
        {
            if (_state != AimState.Paused) return;
            _state = AimState.Running;
            _pauseGate.TrySetResult();   // open the gate
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}

// Snapshot of lifecycle signals given to an AIM for one ProcessAsync call.
// The AIM uses StopToken to detect a Stop request and awaits PauseGate to
// honour a Pause. Neither field exposes the AimLifecycle itself.
public readonly struct AimContext
{
    private readonly Func<Task>? _pauseGate;
    private readonly Func<int>?  _pauseRequests;
    private readonly Action<string>?     _report;
    private readonly Func<string, bool>? _stopAim;
    private readonly IAccessReader?      _access;

    public static readonly AimContext None = new(CancellationToken.None, Task.CompletedTask);

    public CancellationToken StopToken { get; }

    // Read afresh each time: a Pause after this context was handed out must be
    // visible to the AIM holding it.
    public Task PauseGate => _pauseGate?.Invoke() ?? Task.CompletedTask;

    // How many times a Pause has been asked for during this run. An AIM that
    // wants to be interrupted - a microphone waiting to be told "that is enough"
    // - watches this for a change rather than watching the gate.
    public int PauseRequests => _pauseRequests?.Invoke() ?? 0;

    // True when this context can carry a Pause at all. AimContext.None cannot,
    // and an AIM should not wait for a signal that will never come.
    public bool CanBePaused => _pauseRequests is not null;

    public AimContext(CancellationToken stopToken, Task pauseGate)
    {
        StopToken      = stopToken;
        _pauseGate     = () => pauseGate;
        _pauseRequests = null;
    }

    public AimContext(
        CancellationToken stopToken,
        Func<Task> pauseGate,
        Func<int> pauseRequests)
    {
        StopToken      = stopToken;
        _pauseGate     = pauseGate;
        _pauseRequests = pauseRequests;
    }

    private AimContext(AimContext context, Action<string> report, Func<string, bool> stopAim, IAccessReader? access)
    {
        StopToken      = context.StopToken;
        _pauseGate     = context._pauseGate;
        _pauseRequests = context._pauseRequests;
        _report        = report;
        _stopAim       = stopAim;
        _access        = access;
    }

    // The same context, with the calls an AIM makes of its host: and Access, where
    // the Controller has one.
    public AimContext WithHost(Action<string> report, Func<string, bool> stopAim, IAccessReader? access = null) =>
        new(this, report, stopAim, access);

    // MPAI_AIFM_AIM_Report (M3203 4.11): something the AIM did or could not do.
    // The Controller conveys it, does not interpret it and takes no action on it;
    // the AIM is DEGRADED for the run. Without a host the report is discarded.
    public void Report(string text) => _report?.Invoke(text);

    // MPAI_AIFM_AIM_Stop, as the published V3.0 provides it: this AIM asks the
    // Controller to stop an AIM of its Module, which is then DEAD (StopAIM).
    public bool StopAim(string aimName) => _stopAim?.Invoke(aimName) ?? false;

    // ACCESS (AIF V3.0, Basic API 4.11.3): the static or slowly changing data of the
    // Controller, which an AIM reads and never writes. Without an Access nothing is
    // found, and a Source's Version is -1.
    public AccessOutcome MPAI_AIFM_Access_Get(string source, string key, out byte[] data)
    {
        data = [];
        return _access?.Get(source, key, out data) ?? AccessOutcome.NotFound;
    }

    public IReadOnlyList<string> MPAI_AIFM_Access_List(string source, string prefix = "") =>
        _access?.List(source, prefix) ?? [];

    public long MPAI_AIFM_Access_Version(string source) => _access?.Version(source) ?? -1;

    // Convenience: await this to honour both Pause and Stop.
    // Call repeatedly at natural yield points inside ProcessAsync.
    public async Task CheckAsync()
    {
        StopToken.ThrowIfCancellationRequested();
        await PauseGate;
        StopToken.ThrowIfCancellationRequested();
    }
}