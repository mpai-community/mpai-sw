using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mpai.Rca;

// A DEVICE THAT ACTS ON THE WORLD AND ANSWERS (M3205 3.1.1, M3237 3.2): the brakes,
// the motor and the steering of a CAV - real or simulated. It takes what the User
// Agent delivers, gives what it produces - its responses, what its sensors read -
// and knows its own safe state: every act on the world has one owner, the User
// Agent, which can interlock it.
public interface IDevice
{
    // A datum a Module gave, delivered: the device acts on it.
    Task DeliverAsync(string dataType, string json, CancellationToken cancel);

    // What the device produces, as it produces it, until cancelled.
    IAsyncEnumerable<(string DataType, string Json)> ReadAsync(CancellationToken cancel);

    // Bring the device to its safe state - a vehicle, stopped - whatever it was
    // last told. The User Agent asks it before it stops the Module that drives it.
    Task SafeStopAsync();
}
