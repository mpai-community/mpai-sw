using System.Collections.Generic;
using System.Threading.Tasks;
using AIF.Controller;
using Mpai.Core;
namespace Mpai.Aims.Speech;
// MMC-SOD-V2.5 - Speech Object Delivery. Self-contained IAimProcessor.
// Reads its own port names from 1MMC-SOD-V2.5-I01.json at startup.
//
// SOD takes a Speech Object (its input port accepts both OSD-BSO and OSD-SPO) and
// delivers it to a device through its own speech-delivery abstraction
// (ISpeechDeliveryAim), keeping the object typed as speech to the device edge - no
// demotion to audio. Speech Object Delivery (MMC-SOD) and Audio Object Delivery
// (CAE-AOD) do similar things but INDEPENDENTLY; SOD does not reuse AOD's device.
// The output port re-emits the Speech Object unchanged (dual-typed
// OSD-BSO/OSD-SPO), so a downstream consumer still sees speech.
public sealed class SodAimProcessor : IAimProcessor
{
    private readonly string             _inputPort;
    private readonly string             _outputPort;
    private readonly ISpeechDeliveryAim _sod;
    public string InstanceId { get; }
    public SodAimProcessor(
        string             instanceId,
        ISpeechDeliveryAim sod,
        AimPortReader           ports)
    {
        InstanceId  = instanceId;
        _sod        = sod;
        _inputPort  = ports.Input("OSD-BSO-V1.5");    // dual-typed port [OSD-BSO, OSD-SPO]
        _outputPort = ports.Output("OSD-BSO-V1.5");   // dual-typed port [OSD-BSO, OSD-SPO]
    }
    public async Task<Message> ProcessAsync(Message message)
    {
        var speech = MpaiJson.FromJson<BasicSpeechObject>(message.Ports[_inputPort]);
        // An EMPTY Speech Object means the synthesiser could not speak this text -
        // a voice whose phoneme map the installed piper cannot read, for one. The
        // translation still travelled; only the sound is missing. Handing zero
        // bytes to a sound device would turn that into a second, unrelated error.
        if (speech.Data.Length == 0)
        {
            System.Console.WriteLine("[MMC-SOD-V2.5] nothing to play - the Speech Object is empty.");
        }
        else
        {
            await _sod.DeliverAsync(speech);   // deliver the Speech Object, as speech
        }
        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicSpeechObject",
            DataType    = "OSD-SPO-V1.5",
            Payload     = message.Ports[_inputPort],
            Ports       = new Dictionary<string, string>
            {
                [_outputPort] = message.Ports[_inputPort]
            }
        };
    }
}