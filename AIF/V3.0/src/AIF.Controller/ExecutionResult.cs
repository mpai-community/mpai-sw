namespace AIF.Controller;

// The outcome of running a composite: either it completed, or it suspended
// because a required boundary input is not yet available (typically because a
// human, via the User Agent, must supply it before the run can continue).
//
// This is general AIF infrastructure - not specific to AMQ. Any composite whose
// Topology draws a boundary input that is absent at the point it is needed will
// suspend here, letting the User Agent service the interaction and resume.
public sealed class ExecutionResult
{
    // Set when the run finished.
    public Message? Completed { get; init; }

    // Set when the run paused waiting for a boundary input.
    public SuspendedExecution? Suspended { get; init; }

    public bool IsSuspended => Suspended is not null;

    public static ExecutionResult Complete(Message message) =>
        new() { Completed = message };

    public static ExecutionResult Suspend(SuspendedExecution state) =>
        new() { Suspended = state };
}

// Opaque, resumable snapshot of a suspended composite run. Holds everything
// needed to continue the top-level plan from where it stopped.
public sealed class SuspendedExecution
{
    // The composite being run.
    public required DescriptorNode Node { get; init; }

    // The topological plan and the index of the AIM that could not run yet.
    public required IReadOnlyList<string> Plan { get; init; }
    public required int Position { get; init; }

    // Outputs already produced by earlier AIMs (aimName -> portName -> object).
    public required Dictionary<string, Dictionary<string, RoutedObject>> Outputs { get; init; }

    // The composite's boundary inputs accumulated so far.
    public required Dictionary<string, string> Boundary { get; init; }

    // The original message envelope (id, type).
    public required Message Envelope { get; init; }

    // Why it suspended: the AIM waiting, and the boundary port/DataType it needs.
    public required string WaitingAim { get; init; }
    public required string WaitingPort { get; init; }
    public required string WaitingDataType { get; init; }

    // Partial outputs the composite can already expose (e.g. OCR's Recognised
    // Text), so the User Agent can show them to the user before resuming.
    public required Dictionary<string, string> PartialOutputs { get; init; }
}
