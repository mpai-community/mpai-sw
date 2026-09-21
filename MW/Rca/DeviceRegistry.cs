using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    // THE REQUEST IS A QUALIFIER, AND THE SOURCE ANSWERS IT. The User Agent says
    // what it wants by writing the Qualifier fields it cares about; the source
    // returns the Object it made, or - when it cannot make that - an Object with
    // no data and a Qualifier saying what it does have. The User Agent then
    // decides: abandon, or ask again naming what was offered.
    //
    // A Qualifier describes. It never chooses: the source reads the request and
    // answers it, and nothing here matches one against another.
    public delegate Task<string?> Acquire(bool viaVad, string? wanted);

    // THE SAME, FOR A SOURCE THAT CAN BE ABANDONED. When a workflow waits for
    // speech or typed text, whichever comes first, the one that did not come is
    // told so and returns nothing; a source that cannot be told simply finishes
    // later, and what it brings is dropped.
    public delegate Task<string?> AcquireUntil(bool viaVad, string? wanted, CancellationToken abandon);

    // Renders a datum of this Data Type. Several may be presented together - an
    // OSD-BSO and a PAF-FDO are one utterance, not two - so a presenter receives
    // everything being presented at once and takes what it recognises.
    public delegate Task Present(IReadOnlyDictionary<string, string> byDataType);

    private readonly Dictionary<string, AcquireUntil> sources =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(string Name, Present Render)> presenters = new();

    // ONE SOURCE PER DATA TYPE. Which device serves a Data Type is the User
    // Agent's own business; the request that reaches it says what is wanted.
    public DeviceRegistry RegisterAcquire(string dataType, Acquire how)
    {
        sources[dataType] = (viaVad, wanted, _) => how(viaVad, wanted);
        return this;
    }

    public DeviceRegistry RegisterAcquire(string dataType, AcquireUntil how)
    {
        sources[dataType] = how;
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

    public bool CanAcquire(string dataType) => sources.ContainsKey(dataType);

    // ASKED WHEN THE WORKFLOW DID NOT SAY AND MORE THAN ONE IS POSSIBLE. The
    // client puts the choice to the person; a client that cannot ask uses the
    // first it has.
    public Func<string, IReadOnlyList<string>, Task<string?>>? Ask { get; set; }

    // WAITING FOR THE PERSON. The word is the App's and the client shows it on a
    // button; a client that cannot wait proceeds, which is what a console does.
    public Func<string, Task>? Await { get; set; }

    // RUNNING AN APP. The User Agent obtains the Workflow Description of the
    // Application named, gives it a Controller of its own, interprets it, and
    // returns when it ends. Nothing of that is the interpreter's business, which
    // is why it is asked for rather than done here.
    public Func<string, Task>? Run { get; set; }

    public Task<string?> AcquireAsync(string dataType, bool viaVad, string? wanted = null,
                                      CancellationToken abandon = default) =>
        sources.TryGetValue(dataType, out var how)
            ? how(viaVad, wanted, abandon)
            : throw new NotSupportedException($"This client acquires no {dataType}.");

    public async Task PresentAsync(IReadOnlyDictionary<string, string> byDataType)
    {
        foreach (var (_, render) in presenters)
            await render(byDataType);
    }

    public IReadOnlyCollection<string> KnownAcquisitions => sources.Keys;
}