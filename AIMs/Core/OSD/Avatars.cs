using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Mpai.Core;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  PAF-AVT-V1.6 - Avatar. Mirrors schemas/PAF/V1.6/data/Avatar.json.
//
//  An Avatar is a 3D Model and, once it speaks, the Speech it utters and the
//  Face and Body Descriptors that animate it. As an input of Response and Scene
//  Rendering it is only the Model to animate (for instance, chosen from a
//  gallery); in the Speaking Avatar that RSR outputs it carries all four,
//  time-aligned: ready to play.
//
//  The Model is usually referenced by ID (3DModelID - for instance a glTF file
//  the User Agent holds) rather than carried as a 3D Model Object.
// ---------------------------------------------------------------------------
public sealed class Avatar
{
    public string Header { get; init; } = "PAF-AVT-V1.6";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string AvatarID { get; init; } = "";
    public SpaceTime? AvatarSpaceTime { get; init; }
    public AvatarData? AvatarData { get; init; }
    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    // An Avatar that is only a Model, referenced by ID.
    public static Avatar OfModel(string modelId, string? avatarId = null) => new()
    {
        AvatarID = avatarId ?? Guid.NewGuid().ToString(),
        AvatarData = new AvatarData { ModelOrModelID = [new AvatarModel { ModelID = modelId }] }
    };

    // The same Avatar, speaking: its Model, the Face and Body Descriptors that
    // animate it and, when given, the Speech (a Speaking Avatar carries the Speech
    // itself, once).
    public Avatar Speaking(BasicSpeechObject? speech, FaceDescriptorsObject? face, BodyDescriptorsObject? body) => new()
    {
        MInstanceID = MInstanceID, UEnvironmentID = UEnvironmentID,
        AvatarID = AvatarID, AvatarSpaceTime = AvatarSpaceTime,
        AvatarData = new AvatarData
        {
            ModelOrModelID = AvatarData?.ModelOrModelID,
            SpeechObjectOrSpeechObjectID = speech is null ? null : [new AvatarSpeech { SpeechObject = speech }],
            FaceDescriptorsObject = face,
            BodyDescriptorsObject = body,
            Accessories = AvatarData?.Accessories
        },
        DataXMData = DataXMData, DescrMetadata = DescrMetadata
    };

    public string? ModelId() => AvatarData?.ModelOrModelID?.FirstOrDefault(m => m.ModelID is not null)?.ModelID;
    public BasicSpeechObject? Speech() => AvatarData?.SpeechObjectOrSpeechObjectID?.FirstOrDefault(s => s.SpeechObject is not null)?.SpeechObject;
}

public sealed class AvatarData
{
    public List<AvatarModel>? ModelOrModelID { get; init; }
    public List<AvatarSpeech>? SpeechObjectOrSpeechObjectID { get; init; }
    public BodyDescriptorsObject? BodyDescriptorsObject { get; init; }
    public FaceDescriptorsObject? FaceDescriptorsObject { get; init; }
    public JsonArray? Accessories { get; init; }
    public string? ProcessID { get; init; }
}

// One of: the 3D Model Object itself, or its ID.
public sealed class AvatarModel
{
    [JsonPropertyName("3DModel")]
    public JsonObject? Model { get; init; }
    [JsonPropertyName("3DModelID")]
    public string? ModelID { get; init; }
}

// One of: the Speech Object itself (here a Basic Speech Object), or its ID.
public sealed class AvatarSpeech
{
    public BasicSpeechObject? SpeechObject { get; init; }
    public string? SpeechObjectID { get; init; }
}

// ---------------------------------------------------------------------------
//  XRV-SAV-V1.0 - Speaking Avatar. Mirrors schemas/XRV1/V1.0/data/SpeakingAvatar.json.
//
//  Output 1 of Response and Scene Rendering (the author, 2026/10/03), made by
//  Speaking Avatar Synthesis: an Avatar - Model, Speech, Face and Body
//  Descriptors - and the Speech it utters. Ready to play: whoever receives it
//  plays the Speech and animates the Model with the Descriptors.
// ---------------------------------------------------------------------------
public sealed class SpeakingAvatar
{
    public string Header { get; init; } = "XRV-SAV-V1.0";
    public string? MInstanceID { get; init; }
    public string SpeakingAvatarID { get; init; } = "";
    public SimpleTime? SpeakingAvatarTime { get; init; }
    public SpeakingAvatarData? SpeakingAvatarData { get; init; }
    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    public Avatar? Avatar() => SpeakingAvatarData?.Avatar;
    public BasicSpeechObject? Speech() => SpeakingAvatarData?.SpeechObject ?? SpeakingAvatarData?.Avatar?.Speech();
    public FaceDescriptorsObject? Face() => SpeakingAvatarData?.Avatar?.AvatarData?.FaceDescriptorsObject;
    public BodyDescriptorsObject? Body() => SpeakingAvatarData?.Avatar?.AvatarData?.BodyDescriptorsObject;
}

public sealed class SpeakingAvatarData
{
    public Avatar? Avatar { get; init; }
    public BasicSpeechObject? SpeechObject { get; init; }
    public JsonObject? AudioBehaviour { get; init; }
    public JsonObject? VisualBehaviour { get; init; }
    public JsonObject? BiometricData { get; init; }
}
