using System.Collections.Concurrent;

namespace AIF.Controller;

// A Port as defined by MPAI-AIF V3.0 Basic API section 4.6.
// A FIFO of Messages with the standard Port operations:
//   Probe, Read (blocking), Write (blocking), Reset, CountPendingMessages.
//
// Ports are how data crosses AIM boundaries. The boundary Ports of the
// composite AIM are how the User Agent injects and retrieves data - the
// User Agent writes to an input Port and reads from an output Port, and
// never touches an AIM directly.
public sealed class AifPort
{
    private readonly ConcurrentQueue<Message> _fifo = new();
    private readonly SemaphoreSlim             _available = new(0);

    public string Name      { get; }
    public string Direction { get; }   // "Input" | "Output"
    public string DataType  { get; }

    public AifPort(string name, string direction, string dataType)
    {
        Name      = name;
        Direction = direction;
        DataType  = dataType;
    }

    // MPAI_AIFM_Port_Input_Write / used also to place an Output message.
    // The write is non-blocking here (unbounded FIFO) and signals waiters.
    public void Write(Message message)
    {
        _fifo.Enqueue(message);
        _available.Release();
    }

    // MPAI_AIFM_Port_Output_Read — blocking read. Returns a copy.
    public async Task<Message> ReadAsync(CancellationToken token = default)
    {
        await _available.WaitAsync(token);
        _fifo.TryDequeue(out var message);
        return message! with { };   // copy (record clone)
    }

    // MPAI_AIFM_Port_Probe — true if a read would currently succeed.
    public bool Probe() => !_fifo.IsEmpty;

    // MPAI_AIFM_Port_CountPendingMessages.
    public int CountPendingMessages() => _fifo.Count;

    // MPAI_AIFM_Port_Reset — delete all pending messages.
    public void Reset()
    {
        while (_fifo.TryDequeue(out _)) { }
        // drain the semaphore count
        while (_available.CurrentCount > 0) _available.Wait(0);
    }
}
