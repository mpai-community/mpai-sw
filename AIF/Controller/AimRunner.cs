using AIF.Channels;

namespace AIF.Controller;

// AN AIM THAT RUNS FROM START TO STOP (M3205 5.2, M3215 3.3). The contract of an
// AIM written for the continuous executor, beside IAimProcessor: it is given its
// Ports and runs until stopped, reading its Input Ports when Messages are pending
// and writing its Output Ports when it has something to write. An AIM that is an
// IAimProcessor runs on the continuous executor too, through the adapter.
public interface IAimRunner
{
    string InstanceId { get; }

    // Returns at Stop. The context carries Pause, Stop, Report and StopAim, as
    // for an IAimProcessor.
    Task RunAsync(IAimPorts ports, AimContext context);
}

// An AIM's own Ports, addressed by Data Type and Port Number, the Port Number
// being 1 where the Metadata omits it; a Port name never reaches the code. The
// implementation's form of M3203 4.6, 4.12 and 4.13. Behind it are the Channel
// ends the Controller opened for the AIM, whatever their transport.
public interface IAimPorts
{
    // The oldest Message pending on an Input Port, within timeoutMs (0: do not
    // wait; negative: without limit); null when none came.
    ValueTask<PortMessage?> ReadAsync(string dataType, int portNumber = 1, int timeoutMs = -1);

    // Writes a datum on an Output Port; false when it could not be written within
    // timeoutMs. The Controller stamps it.
    ValueTask<bool> WriteAsync(string dataType, int portNumber, string json, int timeoutMs = -1);

    // The first of these Input Ports with a Message pending, within timeoutMs.
    ValueTask<(string DataType, int PortNumber)?> SelectAsync(int timeoutMs, params (string DataType, int PortNumber)[] inputs);

    int  Pending(string dataType, int portNumber = 1);
    long Dropped(string dataType, int portNumber = 1);

    // The Controller's time, on the time base of the clock plugged in (M3215 3.5).
    DateTimeOffset Now { get; }

    // Completes at the AIM's next Period; at once where it has none.
    Task NextPeriodAsync();

    // PAYLOADS BY REFERENCE (M3215 3.6): placed for the Channel of an Output Port,
    // the reference returned going in the Object's DataURI; fetched and released
    // by a reader at the far end of that Channel.
    string PutPayload(string dataType, int portNumber, ReadOnlyMemory<byte> data);
    ReadOnlyMemory<byte> GetPayload(string reference);
    void ReleasePayload(string reference);
}
