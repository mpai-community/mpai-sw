using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Sas;

// PAF-SAS-V1.6 - Speaking Avatar Synthesis, as an AIF IAimProcessor. It makes the
// Speaking Avatar (XRV-SAV-V1.0) that Response and Scene Rendering outputs (the
// author, 2026/10/03) from:
//   - Avatar           : the Model to animate (for instance, chosen from a gallery);
//   - Speech Object    : the Machine Speech, from Text-To-Speech;
//   - Face Descriptors : the face's motion, from Generative Face Description;
//   - Body Descriptors : the body's motion, from Generative Body Description.
// Speech, Face and Body Descriptors are required: in continuous execution SAS then
// waits for all three of an utterance instead of packaging the speech alone.
// The Speaking Avatar is ready to play: the Avatar it holds carries the Model and the
// Face and Body Descriptors, and the Speech is carried once, beside it. It synthesises
// the package; it draws nothing.
//
// THE AVATAR IS KEPT. It is given once - when the Module starts, or when the User
// Agent changes avatar - and used for every utterance until another arrives: in
// continuous execution an AIM runs on a new datum at each required Port, and nothing
// upstream knows when the machine will speak. So the Avatar Port is optional here,
// and an utterance before any Avatar is an error.
public sealed class SasAimProcessor : IAimProcessor
{
    private readonly string _avatarPort;   // PAF-AVT
    private readonly string _speechPort;   // OSD-BSO
    private readonly string _facePort;     // PAF-FDO
    private readonly string _bodyPort;     // PAF-BDO
    private readonly string _outPort;      // XRV-SAV

    private Avatar? _avatar;               // the last Avatar received

    public string InstanceId { get; }

    public SasAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId  = instanceId;
        _avatarPort = ports.Input("PAF-AVT-V1.6");
        _speechPort = ports.Input("OSD-BSO-V1.5");
        _facePort   = ports.Input("PAF-FDO-V1.6");
        _bodyPort   = ports.Input("PAF-BDO-V1.6");
        _outPort    = ports.Output("XRV-SAV-V1.0");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        T? Read<T>(string port) where T : class =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json)
                ? MpaiJson.FromJson<T>(json) : null;

        if (Read<Avatar>(_avatarPort) is { } given) _avatar = given;
        var avatar = _avatar;
        var speech = Read<BasicSpeechObject>(_speechPort);
        if (avatar is not null && speech is null)   // an Avatar alone: kept, nothing to say
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "AvatarKept", Ports = new() });
        if (avatar is null || speech is null)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId,
                avatar is null ? "no Avatar received yet" : "no Speech Object on its input port"));

        var face = Read<FaceDescriptorsObject>(_facePort);
        var body = Read<BodyDescriptorsObject>(_bodyPort);
        var speaking = new SpeakingAvatar
        {
            MInstanceID = avatar.MInstanceID,
            SpeakingAvatarID = Guid.NewGuid().ToString(),
            SpeakingAvatarData = new SpeakingAvatarData { Avatar = avatar.Speaking(null, face, body), SpeechObject = speech }
        };
        MpaiDiag.Emotion("SAS", $"{avatar.ModelId() ?? "no model"}: {(face is null ? "no face" : "face")}, {(body is null ? "no body" : "body")}");

        var json = MpaiJson.ToJson(speaking);
        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = "SpeakingAvatar",
            DataType    = "XRV-SAV-V1.0",
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outPort] = json }
        });
    }
}
