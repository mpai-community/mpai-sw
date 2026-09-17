using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mpai.Rca;

// WHICH DEVICE A DATA TYPE COMES FROM, AND WHICH ONE IT GOES TO.
//
// A workflow says "acquire UserFace (OSD-BVO-V1.5)" and nothing about a camera;
// it says "present MachineSpeech, MachineFace" and nothing about a loudspeaker or
// an avatar. Something must know that an OSD-BVO is got by looking, an OSD-BSO by
// listening, an OSD-STM by asking the clock - and this is that something.
//
// It is the parallel of the Port-data serialiser registry, and for the same
// reason: one entry per Data Type, registered once, and no application anywhere
// in it. A Remote Client Application that knew which application it was running
// would not be one.
//
// ACQUISITION RETURNS AN OBJECT, NOT BYTES. Whatever a device produces arrives
// with the Qualifier the device determined - the sampling frequency it captured
// at, the format it wrote - because a consumer that must guess will guess wrong
// and say nothing about it.
public sealed class DeviceRegistry
{
    // Gets a datum of this Data Type from the real world. The flag is the
    // workflow's "via VAD": wait for the speaker to stop, rather than for a fixed
    // interval or a button.
    public delegate Task<string?> Acquire(bool viaVad);

    // Renders a datum of this Data Type. Several may be presented together - an
    // OSD-BSO and a PAF-FDO are one utterance, not two - so a presenter receives
    // everything being presented at once and takes what it recognises.
    public delegate Task Present(IReadOnlyDictionary<string, string> byDataType);

    private readonly Dictionary<string, Acquire> acquirers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(string Name, Present Render)> presenters = new();

    public DeviceRegistry RegisterAcquire(string dataType, Acquire how)
    {
        acquirers[dataType] = how;
        return this;
    }

    // A presenter is not keyed by Data Type, because rendering is not one datum at
    // a time. The avatar wants the speech and the face descriptors together; a
    // screen wants the text. Each is offered everything and takes what it knows.
    public DeviceRegistry RegisterPresent(string name, Present render)
    {
        presenters.Add((name, render));
        return this;
    }

    public bool CanAcquire(string dataType) => acquirers.ContainsKey(dataType);

    public Task<string?> AcquireAsync(string dataType, bool viaVad) =>
        acquirers.TryGetValue(dataType, out var how)
            ? how(viaVad)
            : throw new NotSupportedException(
                  $"Nothing in this Remote Client Application acquires a {dataType}. " +
                  "A workflow may only ask for what the client can get.");

    public async Task PresentAsync(IReadOnlyDictionary<string, string> byDataType)
    {
        foreach (var (_, render) in presenters)
            await render(byDataType);
    }

    public IReadOnlyCollection<string> KnownAcquisitions => acquirers.Keys;
}