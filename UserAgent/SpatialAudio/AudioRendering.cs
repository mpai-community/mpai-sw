using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.SpatialAudio;

// WHAT THE USER HEARS (the User Agent's, the author, 2026/10/04: "AOD is UA's
// business"; formerly the AIM CAE-AOD). It renders, and does not play: the
// Loudspeaker Unit plays what it gives.
//
//   - A Basic Audio Object is heard as it is, unless it states where it is heard from
//     (its UserPoV): then it is heard from there, its directional pattern counting.
//   - An Audio Object is heard as a scene of its Basic Audio Objects, from its UserPoV.
//   - A Basic Audio Scene or an Audio Scene is rendered from its UserPoV - each member
//     from its own, if it has one - by the spatial renderer (Steam Audio): distance,
//     air, directional patterns, binaural or onto a speaker layout.
//
// The result is a Basic Audio Object: the rendered audio as a 16-bit PCM WAV at 48 kHz.
// The Header of what is given says which kind it is.
public sealed class AudioRendering(string steamAudioFolder, SpatialRenderer.Layout layout = SpatialRenderer.Layout.Binaural)
{
    public bool Available => SpatialRenderer.Available(steamAudioFolder);

    public BasicAudioObject Render(string json)
    {
        switch (HeaderOf(json))
        {
            case "OSD-BAS-V1.5": return Render(SceneAudio.Members(MpaiJson.FromJson<BasicAudioSceneDescriptors>(json)).ToList(), "scene");
            case "OSD-ASD-V1.5": return Render(SceneAudio.Members(MpaiJson.FromJson<AudioSceneDescriptors>(json)).ToList(), "scene");
            case "OSD-AUO-V1.5":
                var auo = MpaiJson.FromJson<AudioObject>(json);
                return Render(SceneAudio.Members(auo).ToList(), auo.AudioObjectID);
            case "OSD-BAO-V1.5":
                var bao = MpaiJson.FromJson<BasicAudioObject>(json);
                return bao.UserPoV is null ? bao
                     : Render([new SceneAudio.Member(bao, new double[3], 0, 0, bao.UserPoV)], bao.BasicAudioObjectID);
            default: throw new ArgumentException($"nothing to hear in {HeaderOf(json) ?? "data without a Header"}: an Audio Object or an Audio Scene is heard.");
        }
    }

    // The members as the user hears them: each group of members heard from the same
    // place is rendered, and the groups are mixed.
    private BasicAudioObject Render(IReadOnlyList<SceneAudio.Member> members, string what)
    {
        if (!Available)
            throw new InvalidOperationException($"no spatial renderer - Steam Audio is not in {steamAudioFolder}.");

        using var renderer = new SpatialRenderer(steamAudioFolder, layout);
        float[] mix = [];
        foreach (var group in members.GroupBy(m => m.User))
        {
            var sources = group.Select(m => new SpatialRenderer.Source(
                SceneAudio.Mono(m.Object.Data, SpatialRenderer.Rate), m.Position, m.YawDeg, m.PitchDeg, 1,
                DirectionalPattern.FromPlot(m.Object.BasicAudioObjectProperties?.AcousticProfile?.DirectionalPatterns))).ToList();
            var part = renderer.Render(sources, SceneAudio.UserOf(group.Key));
            if (part.Length > mix.Length) Array.Resize(ref mix, part.Length);
            for (var i = 0; i < part.Length; i++) mix[i] += part[i];
        }

        var qualifier = new AudioQualifier
        {
            AudioQualifierID = Guid.NewGuid().ToString(),
            SubTypes = "Mixed",   // a rendering is a mix of its sources
            Formats = new AudioFormats
            {
                ContentFormat = new AudioContentFormat { RawData = new AudioRawData { SampleSpace = new Pcm { SamplingFrequency = SpatialRenderer.Rate, Precision = 16 } } },
                TransportFormat = new AudioTransportFormat { FileFormats = AudioFileFormat.Wav }
            },
            Attributes = new AudioAttributes
            {
                Device = new AudioDevice
                {
                    DeviceRole = "Render",
                    CaptureConfiguration = new CaptureConfiguration { ChannelCount = renderer.Channels, SamplingMode = renderer.Channels switch { 1 => "Mono", 2 => "Stereo", _ => "MultiChannel" } }
                }
            }
        };
        var played = BasicAudioObject.FromData(SceneAudio.Wav(mix, renderer.Channels, SpatialRenderer.Rate), qualifier);
        return new BasicAudioObject
        {
            BasicAudioObjectID = played.BasicAudioObjectID,
            BasicAudioObjectData = played.BasicAudioObjectData,
            AudioQualifier = played.AudioQualifier,
            DescrMetadata = $"{what} rendered for the user ({layout})"
        };
    }

    private static string? HeaderOf(string json)
    {
        try { return JsonNode.Parse(json)?["Header"]?.GetValue<string>(); }
        catch { return null; }
    }
}
