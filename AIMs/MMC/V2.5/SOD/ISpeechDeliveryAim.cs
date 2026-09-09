using System.Threading.Tasks;
using Mpai.Core;

namespace Mpai.Aims.Speech;

// Speech Object Delivery device abstraction. The speech counterpart of
// IAudioDeliveryAim: it delivers a Speech Object to a device, keeping the object
// typed as speech to the device edge (no demotion to audio). Speech Object
// Delivery (MMC-SOD) and Audio Object Delivery (CAE-AOD) are independent siblings;
// each has its own delivery device abstraction and implementations.
public interface ISpeechDeliveryAim
{
    Task DeliverAsync(BasicSpeechObject speech);
}