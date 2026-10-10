using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Api;

// One utterance of the avatar, as the UA presents it: the machine speech (WAV bytes),
// the machine Face Descriptors and Body Descriptors that drive the 3-D avatar;
// TranslatedText carries a spoken translation's text when relevant. Defined here in
// AIF/ControllerApi (alongside ControllerApi) so every User Agent and UaKit shares one
// type: a first-class Controller API type - not an MPAI data type. (Named
// SpeakingAvatar until 2026/10/03; the name is the MPAI Speaking Avatar's, XRV-SAV-V1.0,
// Mpai.Core.OSD.SpeakingAvatar.)
public sealed record AvatarUtterance(
    byte[] MachineSpeechWav,
    FaceDescriptorsObject? FaceDescriptors,
    string? TranslatedText = null,
    BodyDescriptorsObject? BodyDescriptors = null)
{
    // THE AVATAR THE CLIENT HOLDS, as the datum (PAF-AVT-V1.6) a Module that renders
    // with Response and Scene Rendering takes: the Model by its identifier. The client
    // holds the model itself and draws it; the Module animates it and gives it a voice.
    public static string AvatarDatum(string modelId, string? avatarId = null) =>
        MpaiJson.ToJson(Avatar.OfModel(modelId, avatarId));

    // A SPEAKING AVATAR (XRV-SAV-V1.0), as Response and Scene Rendering outputs it,
    // unpacked for presentation: its Speech, and the Face and Body Descriptors its
    // Avatar carries. Null when there is none, or it carries no speech.
    public static AvatarUtterance? FromSpeakingAvatar(string? json, string? translatedText = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var speaking = MpaiJson.FromJson<SpeakingAvatar>(json);
        var wav = speaking?.Speech()?.Data;
        if (speaking is null || wav is not { Length: > 0 }) return null;
        return new AvatarUtterance(wav, speaking.Face(), translatedText, speaking.Body());
    }
}
