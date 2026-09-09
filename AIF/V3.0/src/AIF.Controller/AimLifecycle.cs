namespace AIF.Controller;

// The four states an AIM can be in, per the MPAI-AIF Basic API.
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

    // Convenience: await this to honour both Pause and Stop.
    // Call repeatedly at natural yield points inside ProcessAsync.
    public async Task CheckAsync()
    {
        StopToken.ThrowIfCancellationRequested();
        await PauseGate;
        StopToken.ThrowIfCancellationRequested();
    }
}