namespace AIF.Controller;

// A Message carries MPAI Data Objects between AIMs.
// It also carries an AimContext — the lifecycle signals (StopToken, PauseGate)
// the AIM uses to honour Start/Pause/Resume/Stop from the Controller.
// The AIM never holds these signals beyond the duration of one ProcessAsync call.
public sealed record Message
{
    // Reserved MessageTypes understood by the framework itself.
    public const string ErrorType     = "Error";
    public const string CancelledType = "Cancelled";

    public string MessageId   { get; init; } = string.Empty;
    public string MessageType { get; init; } = string.Empty;

    // Primary output (legacy single-object transport).
    public string DataType { get; init; } = string.Empty;
    public string Payload  { get; init; } = string.Empty;

    // Future transport model.
    public List<DataObjectMessage> Inputs { get; init; } = new();

    // Port-keyed routing (current routing model).
    public Dictionary<string, string> Ports { get; init; } = new();

    // Lifecycle context — injected by AimHost, consumed by the AIM processor.
    // AimContext.None when no lifecycle management is needed (e.g. unit tests).
    public AimContext Context { get; init; } = AimContext.None;

    // The AIM that failed, when this Message reports a failure.
    public string FailedAim { get; init; } = string.Empty;

    public bool IsError     => MessageType == ErrorType;
    public bool IsCancelled => MessageType == CancelledType;

    public static Message Error(string messageId, string failedAim, string reason) =>
        new() { MessageId = messageId, MessageType = ErrorType,
                FailedAim = failedAim, Payload = reason };

    public static Message Cancelled(string messageId, string aimName, string reason) =>
        new() { MessageId = messageId, MessageType = CancelledType,
                FailedAim = aimName, Payload = reason };
}
