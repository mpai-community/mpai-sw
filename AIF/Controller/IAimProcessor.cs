namespace AIF.Controller;

// What the framework needs of an AIM in order to run it: an identity, and the
// ability to turn one Message into another.
//
// Asynchronous, because an AIM may wait on a device, a subprocess or a network
// without blocking the thread that runs the workflow.
//
// Named IAimProcessor, not IAimRuntime, because AIF.Abstractions.IAimRuntime is
// a different contract â€” the AIM lifecycle (Start, Pause, Resume, Stop).
public interface IAimProcessor
{
    string InstanceId
    {
        get;
    }

    Task<Message> ProcessAsync(
        Message message);
}

