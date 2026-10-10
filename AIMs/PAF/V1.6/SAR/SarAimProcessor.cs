using System.Text;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Sar;

// PAF-SAR-V1.6 - Scene and Avatar Rendering, as an AIF IAimProcessor. It places the
// Speaking Avatar (XRV-SAV), made by Speaking Avatar Synthesis, in a Scene seen and
// heard from a Point of View, and produces the Scene as data (the author, 2026/10/03:
// RSR itself never draws). It has two alternatives, chosen by its setting SceneOutputs
// - "3D" (the default), "2D" or "Both". The 3D one:
//   - 3D Model Scene (OSD-B3S): the input 3D Model Scene with the Avatar placed - a
//     member naming the Avatar's 3D Model, at the Avatar's Space-Time - for a 3D
//     Model Object Delivery (the User Agent's renderer) to draw;
//   - Multimodal Scene (OSD-BMS): the Basic Scenes heard and seen - the input Audio
//     Scene, the Speech Scene (the Avatar's speech at its mouth) and the 3D Model
//     Scene - in one frame.
// The 2D one (the author, 2026/10/03):
//   - Audio Scene (OSD-BAS): the input Audio Scene with the Avatar's speech, as a
//     Basic Audio Object, at its mouth;
//   - Visual Scene (OSD-BVS): one Basic Visual Object, a video (MP4) as long as the
//     speech, of the whole view - the 3D Model Scene with the Avatar animated by its
//     Face and Body Descriptors, seen from the Point of View (SceneVideo: the User
//     Agent's page, drawn off screen; settings Browser and FFmpeg, else found).
//
// WHERE THE AVATAR IS. Its own Space-Time, when the Avatar has one; else the Avatar
// PoV its requester gives (the author, 2026/10/04: the User Agent that asks for the
// Speaking Avatar knows where to place it); else where the 3D Model Scene already has
// a member naming it; else 1.5 m in front of the Point of View, turned to face it;
// else at the origin. Positions are in metres -
// X ahead, Y left, Z up - and Orientation is (roll, pitch, yaw) in degrees. A 3D
// Model faces -X at yaw 0, so an Avatar given the Point of View's yaw faces the
// viewer.
//
// THE SCENE. One Basic Multimodal Scene (the author, 2026/10/04: "I would keep BMS as
// input"): its Audio Scene and its 3D Model Scene are what the Avatar is placed in.
//
// THE SCENE IS KEPT. The Scene, the Point of View and the Avatar PoV are given when they change -
// the room once, the viewpoint when the user moves - and used for every utterance
// until replaced, as Speaking Avatar Synthesis keeps the Avatar. Only the Speaking
// Avatar is required. With no scene given at all there is nothing to place the
// Avatar in, and nothing is produced.
//
// SO ARE THE AVATARS. Every Avatar placed stays in the Scene, by its ID, where it
// was last placed - in a videoconference each participant's (the author, 2026/10/03:
// RSR in the Avatar-Based Videoconference, its Client Receiver PDX -> SAS -> SAR).
// The one speaking is placed anew by the rules above, and its speech alone is in
// the Speech Scene.
public sealed class SarAimProcessor : IAimProcessor, IAsyncDisposable
{
    public const double Ahead = 1.5;      // metres between the Point of View and the Avatar
    public const double MouthHeight = 1.6;

    private readonly string _avatarPort;  // XRV-SAV
    private readonly string _scenePort;   // OSD-BMS
    private readonly string _povPort;     // OSD-OPV #1, the Point of View
    private readonly string _avatarPovPort; // OSD-OPV #2, where the Avatar is placed
    private readonly string _b3sPort;     // OSD-B3S out
    private readonly string _bmsPort;     // OSD-BMS out
    private readonly string _basPort;     // OSD-BAS out
    private readonly string _bvsPort;     // OSD-BVS out
    private readonly bool _threeD, _twoD;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private Task<SceneVideo>? _video;

    private BasicAudioSceneDescriptors? _audio;
    private Basic3DModelSceneDescriptors? _model;
    private PointOfView? _pov;
    private PointOfView? _avatarPov;
    private readonly List<PlacedAvatar> _avatars = new();   // in the order first placed

    // An Avatar in the Scene: its ID, the 3D Model that shows it, where it is.
    public sealed record PlacedAvatar(string Id, string Model, SpaceTime At);

    public string InstanceId { get; }

    public SarAimProcessor(string instanceId, AimPortReader ports, IReadOnlyDictionary<string, string>? settings = null)
    {
        _settings = settings ?? new Dictionary<string, string>();
        var outputs = _settings.GetValueOrDefault("SceneOutputs", "3D");
        _twoD = outputs.Equals("2D", StringComparison.OrdinalIgnoreCase) || outputs.Equals("Both", StringComparison.OrdinalIgnoreCase);
        _threeD = !outputs.Equals("2D", StringComparison.OrdinalIgnoreCase);
        InstanceId  = instanceId;
        _avatarPort = ports.Input("XRV-SAV-V1.0");
        _scenePort  = ports.Input("OSD-BMS-V1.5");
        _povPort    = ports.Input("OSD-OPV-V1.5", 1);
        _avatarPovPort = ports.InputOrDefault("OSD-OPV-V1.5", 2, "");
        _b3sPort    = ports.Output("OSD-B3S-V1.5");
        _bmsPort    = ports.Output("OSD-BMS-V1.5");
        _basPort    = ports.OutputOrDefault("OSD-BAS-V1.5", "");
        _bvsPort    = ports.OutputOrDefault("OSD-BVS-V1.5", "");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        string? Json(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

        if (Json(_povPort) is { } pov) _pov = MpaiJson.FromJson<PointOfView>(pov);
        if (Json(_avatarPovPort) is { } avatarPov) _avatarPov = MpaiJson.FromJson<PointOfView>(avatarPov);
        if (Json(_scenePort) is { } scene)
        {
            // Its members: the Audio Scene and the 3D Model Scene the Avatar is placed in.
            _audio = null; _model = null;
            foreach (var entry in JsonNode.Parse(scene)?["BasicAVSceneDescriptorsData"]?.AsArray() ?? [])
            {
                if (entry?["BXSOrBXSID"] is not JsonObject member) continue;
                var json = member.ToJsonString();
                switch ((string?)member["Header"])
                {
                    case "OSD-BAS-V1.5": _audio = MpaiJson.FromJson<BasicAudioSceneDescriptors>(json); break;
                    case "OSD-B3S-V1.5": _model = MpaiJson.FromJson<Basic3DModelSceneDescriptors>(json); break;
                    default: message.Context.Report($"Scene member {(string?)member["Header"]} is not rendered: this Implementation takes a Basic Audio Scene (OSD-BAS) and a Basic 3D Model Scene (OSD-B3S)."); break;
                }
            }
            _pov ??= JsonNode.Parse(scene)?["UserPoV"] is JsonObject seen ? MpaiJson.FromJson<PointOfView>(seen.ToJsonString()) : null;
        }

        var speaking = Json(_avatarPort) is { } sav ? MpaiJson.FromJson<SpeakingAvatar>(sav) : null;
        if (speaking is null)                                   // scene alone: kept for the next utterance
            return new Message { MessageId = message.MessageId, MessageType = "SceneKept", Ports = new() };
        if (_audio is null && _model is null && _pov is null)   // no scene: nothing to place the Avatar in
            return new Message { MessageId = message.MessageId, MessageType = "NoScene", Ports = new() };

        var (b3s, bms, mouth) = Render(speaking, _audio, _model, _pov, _avatars, _avatarPov);
        var ports = new Dictionary<string, string>();
        if (_threeD && _b3sPort.Length > 0) ports[_b3sPort] = MpaiJson.ToJson(b3s);
        if (_threeD && _bmsPort.Length > 0) ports[_bmsPort] = MpaiJson.ToJson(bms);
        if (_twoD && speaking.Speech() is { } speech)
        {
            if (_basPort.Length > 0) ports[_basPort] = MpaiJson.ToJson(Heard(_audio, speech, mouth, b3s));
            if (_bvsPort.Length > 0)
            {
                var video = await (_video ??= StartVideo()).WaitAsync(message.Context.StopToken);
                var mp4 = await video.RenderAsync(MpaiJson.ToJson(b3s), MpaiJson.ToJson(b3s.UserPoV),
                    speaking.Face() is { } f ? MpaiJson.ToJson(f) : null, speaking.Body() is { } b ? MpaiJson.ToJson(b) : null,
                    WavSeconds(speech.Data), message.Context.StopToken);
                ports[_bvsPort] = MpaiJson.ToJson(Seen(mp4, b3s));
            }
        }
        return new Message
        {
            MessageId = message.MessageId, MessageType = "Scene",
            DataType = _threeD ? "OSD-B3S-V1.5" : "OSD-BVS-V1.5",
            Payload = ports.GetValueOrDefault(_threeD ? _b3sPort : _bvsPort, ""), Ports = ports
        };
    }

    // The browser that draws the 2D alternative, ended.
    public async ValueTask DisposeAsync()
    {
        if (_video is { IsCompletedSuccessfully: true } started) await started.Result.DisposeAsync();
        _video = null;
    }

    private Task<SceneVideo> StartVideo()
    {
        var browser = SceneVideo.FindBrowser(_settings.GetValueOrDefault("Browser"))
                      ?? throw new InvalidOperationException("no browser to draw the scene with (Edge or Chromium; setting Browser).");
        var ffmpeg = SceneVideo.FindFfmpeg(_settings.GetValueOrDefault("FFmpeg"))
                     ?? throw new InvalidOperationException("no ffmpeg to make the video with (setting FFmpeg).");
        return SceneVideo.StartAsync(MpaiPaths.Assets, browser, ffmpeg);
    }

    // The Audio Scene heard: the input Audio Scene and the Avatar's speech at its mouth.
    public static BasicAudioSceneDescriptors Heard(BasicAudioSceneDescriptors? audio, BasicSpeechObject speech, SpaceTime mouth,
                                                   Basic3DModelSceneDescriptors seen)
    {
        var (rate, bits) = WavFormat(speech.Data);
        var spoken = new BasicAudioObject
        {
            MInstanceID = speech.MInstanceID, BasicAudioObjectID = speech.BasicSpeechObjectID,
            BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(speech.Data))],
            AudioQualifier = new AudioQualifier
            {
                AudioQualifierID = Guid.NewGuid().ToString(),
                SubTypes = "Speech",
                Formats = new AudioFormats
                {
                    ContentFormat = new AudioContentFormat { RawData = new AudioRawData { SampleSpace = new Pcm { SamplingFrequency = rate, Precision = bits } } },
                    TransportFormat = new AudioTransportFormat { FileFormats = AudioFileFormat.Wav }
                }
            }
        };
        var entries = new List<BasicAudioSceneEntry>(audio?.BasicAudioSceneDescriptorsEntries ?? []);
        entries.Add(new BasicAudioSceneEntry { AudioObjectSpaceTime = mouth, AudioObjectIDOrAudioObject = spoken });
        return new BasicAudioSceneDescriptors
        {
            MInstanceID = seen.MInstanceID, BasicAudioSceneDescriptorsID = Guid.NewGuid().ToString(),
            BASSpaceTime = audio?.BASSpaceTime ?? seen.Basic3DModelSceneDescriptorsSpaceTime,
            UserPoV = seen.UserPoV, ClosedSpace = audio?.ClosedSpace, AcousticProfile = audio?.AcousticProfile,
            AudioObjectCount = entries.Count, BasicAudioSceneDescriptorsEntries = entries
        };
    }

    // The Visual Scene seen: one Visual Object, the video of the whole view - AVC (H.264)
    // in an MP4, at SceneVideo.Fps frames a second.
    public static BasicVisualSceneDescriptors Seen(byte[] mp4, Basic3DModelSceneDescriptors seen) => new()
    {
        MInstanceID = seen.MInstanceID, BasicVisualSceneDescriptorsID = Guid.NewGuid().ToString(),
        BVSDescriptorsSpaceTime = seen.Basic3DModelSceneDescriptorsSpaceTime, UserPoV = seen.UserPoV,
        VisualObjectCount = 1,
        BasicVisualSceneDescriptorsEntries = [new BasicVisualSceneEntry
        {
            VObjectIDOrVObject = new BasicVisualObject
            {
                BasicVisualObjectID = Guid.NewGuid().ToString(), FileName = "view.mp4", Data = mp4,
                VisualQualifier = new VisualQualifier
                {
                    VisualQualifierID = Guid.NewGuid().ToString(),
                    Format = new VisualFormat
                    {
                        Content = new VisualContentFormat
                        {
                            TimeSampling = new VisualTimeSampling { Time = SceneVideo.Fps },
                            TwoD = new Visual2D { Dynamic = new Visual2DDynamic { OtherContentFormat = "AVC" } }   // H.264
                        },
                        Transport = new VisualTransport { FileFormat = VisualFileFormat.MP4 }
                    }
                }
            }
        }]
    };

    // A WAV's sampling frequency and bits, and its length in seconds.
    private static (int Rate, int Bits) WavFormat(byte[] wav) =>
        wav.Length >= 36 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF"
            ? (BitConverter.ToInt32(wav, 24), BitConverter.ToInt16(wav, 34)) : (16000, 16);

    public static double WavSeconds(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return 1;
        int channels = BitConverter.ToInt16(wav, 22), rate = BitConverter.ToInt32(wav, 24), bits = BitConverter.ToInt16(wav, 34);
        for (var i = 12; i + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, i, 4);
            var size = BitConverter.ToInt32(wav, i + 4);
            if (id == "data") return Math.Min(size, wav.Length - i - 8) / (double)(rate * channels * Math.Max(1, bits / 8));
            i += 8 + size + (size & 1);
        }
        return 1;
    }

    private static string? Header(string json) => (string?)JsonNode.Parse(json)?["Header"];

    // The Avatar placed in the Scene: the 3D Model Scene with it, and the Multimodal
    // Scene of what is heard and seen.
    // avatars: those already placed, updated with the one speaking (kept by the caller).
    public static (Basic3DModelSceneDescriptors Model, BasicAudioVisualSceneDescriptors Multimodal, SpaceTime Mouth) Render(
        SpeakingAvatar speaking, BasicAudioSceneDescriptors? audio, Basic3DModelSceneDescriptors? model, PointOfView? pov,
        List<PlacedAvatar>? avatars = null, PointOfView? avatarPov = null)
    {
        avatars ??= new();
        var avatar = speaking.Avatar();
        var name = avatar?.ModelId() ?? avatar?.AvatarID ?? speaking.SpeakingAvatarID;
        var id = avatar?.AvatarID is { Length: > 0 } a ? a : name;
        var mInstance = speaking.MInstanceID ?? model?.MInstanceID ?? "";
        var viewer = pov ?? model?.UserPoV ?? new PointOfView { PointOfViewID = "eyes", CartPosition = [0, 0, MouthHeight] };
        var placed = avatar?.AvatarSpaceTime
                     ?? (avatarPov is null ? null : At(avatarPov.CartPosition, avatarPov.Orientation.Length > 2 ? avatarPov.Orientation[2] : 0))
                     ?? model?.Basic3DModelSceneItems.FirstOrDefault(i => i.Id == id || i.Id == name)?.ModelObjectSpaceTime
                     ?? (pov is null ? At([0, 0, 0], 0) : InFrontOf(pov));
        var frame = model?.Basic3DModelSceneDescriptorsSpaceTime ?? At([0, 0, 0], 0);

        var index = avatars.FindIndex(p => p.Id == id);
        if (index < 0) avatars.Add(new PlacedAvatar(id, name, placed)); else avatars[index] = new PlacedAvatar(id, name, placed);
        bool IsAvatar(Basic3DModelSceneItem i) => avatars.Any(p => i.Id == p.Id || i.Id == p.Model);

        var items = (model?.Basic3DModelSceneItems ?? []).Where(i => !IsAvatar(i)).ToList();
        foreach (var p in avatars)
            items.Add(new Basic3DModelSceneItem { ModelObjectSpaceTime = p.At, ObjectIDOrObject = [JsonValue.Create(p.Model)] });
        var b3s = new Basic3DModelSceneDescriptors
        {
            MInstanceID = mInstance, UEnvironmentID = model?.UEnvironmentID,
            Basic3DModelSceneDescriptorsID = Guid.NewGuid().ToString(),
            UserPoV = viewer, GravityValue = model?.GravityValue,
            Basic3DModelSceneDescriptorsSpaceTime = frame,
            ModelObjectCount = items.Count, Basic3DModelSceneItems = items
        };

        var mouth = Raised(placed, MouthHeight);
        var entries = new List<BasicAVSceneEntry>();
        if (audio is not null) entries.Add(new BasicAVSceneEntry { BXSSpaceTime = audio.BASSpaceTime ?? frame, BXSOrBXSID = audio });
        if (speaking.Speech() is { } speech)
        {
            entries.Add(new BasicAVSceneEntry
            {
                BXSSpaceTime = frame,
                BXSOrBXSID = new BasicSpeechSceneDescriptors
                {
                    MInstanceID = mInstance, BasicSpeechSceneDescriptorsID = Guid.NewGuid().ToString(),
                    BasicSpeechSceneDescriptorsSpaceTime = frame, UserPoV = viewer, ObjectCount = 1,
                    BasicSpeechSceneItems = [new BasicSpeechSceneItem { ObjectSpaceTime = mouth, SpeechObject = speech }]
                }
            });
        }
        entries.Add(new BasicAVSceneEntry { BXSSpaceTime = frame, BXSOrBXSID = b3s });
        var bms = new BasicAudioVisualSceneDescriptors
        {
            MInstanceID = mInstance, BasicAVSceneDescriptorsID = Guid.NewGuid().ToString(),
            BAVSDescriptorsSpaceTime = frame, UserPoV = viewer,
            AVObjectCount = entries.Count, BasicAVSceneDescriptorsData = entries
        };
        return (b3s, bms, mouth);
    }

    // 1.5 m along the Point of View's heading, on the ground, facing back at it.
    public static SpaceTime InFrontOf(PointOfView pov)
    {
        var yaw = pov.Orientation.Length > 2 ? pov.Orientation[2] : 0;
        var rad = yaw * Math.PI / 180;
        var p = pov.CartPosition;
        return At([p[0] + Ahead * Math.Cos(rad), p[1] + Ahead * Math.Sin(rad), 0], yaw);
    }

    public static SpaceTime At(double[] position, double yaw) => new()
    {
        SpaceTimeID = Guid.NewGuid().ToString(),
        SpatialAttitude1 = new SpatialAttitude
        {
            ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
            Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = position },
            Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, yaw] }
        }
    };

    private static SpaceTime Raised(SpaceTime at, double height)
    {
        var a = at.SpatialAttitude1;
        var p = a?.Position.CartPosition ?? [0, 0, 0];
        return At([p[0], p[1], p[2] + height], a?.Orientation.EulerAngles is { Length: > 2 } e ? e[2] : 0);
    }
}
