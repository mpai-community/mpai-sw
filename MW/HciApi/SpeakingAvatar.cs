using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Hci.Api;

// The Speaking Avatar payload the UA renders: the machine speech (WAV bytes) and
// the machine Face Descriptors that drive the 3-D avatar; TranslatedText carries a
// spoken translation's text when relevant. Defined here in MW/HciApi (alongside
// NorthApi) so every User Agent and UaKit shares one type. (Previously it lived in
// the retired HciApi facade; it belongs here as a first-class North-API type.)
public sealed record SpeakingAvatar(
    byte[] MachineSpeechWav,
    FaceDescriptorsObject? FaceDescriptors,
    string? TranslatedText = null);
