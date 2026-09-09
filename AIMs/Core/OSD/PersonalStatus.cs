using System;

namespace Mpai.Core.OSD;

// Personal Status data types for MPAI-MMC V2.5.
//
// Personal Status has three Factors - Cognitive State, Emotion, Social Attitude -
// each a LABEL chosen from that Factor's standard three-level set (Category /
// General Adjectival / Specific Adjectival) plus an optional Degree in [0,1]. The
// Factors are carried per modality (Text, Speech, Face, Gesture) by a modality
// Personal Status object, and the per-modality split is conveyed by the Entity
// Personal Status. These classes mirror the data schemas:
//   MMC/V2.5/data/Emotion.json          (MMC-EEM-V2.5)
//   MMC/V2.5/data/CognitiveState.json   (MMC-ECS-V2.5)
//   MMC/V2.5/data/SocialAttitude.json   (MMC-ESA-V2.5)
//   MMC/V2.5/data/TextPersonalStatus.json   (MMC-TPS-V2.5)  [+ Speech/Face/Gesture]
//   MMC/V2.5/data/EntityPersonalStatus.json (MMC-EPS-V2.5)
//
// The three Factor objects share the same shape - a chosen label (Category +
// General + Specific adjectival) and a Degree - so they are built on one carrier,
// FactorLabel, while remaining distinct types with their own Headers.

// A chosen Factor label: the three-level label (Category / General Adjectival /
// Specific Adjectival) and an optional Degree (intensity/confidence) in [0,1].
// Any level may be null when not determined.
public sealed class FactorLabel
{
    public string? Category { get; init; }
    public string? GeneralAdjectival { get; init; }
    public string? SpecificAdjectival { get; init; }
    public double? Degree { get; init; }

    public static FactorLabel Of(string category, string? general = null, string? specific = null, double? degree = null)
        => new()
        {
            Category = category,
            GeneralAdjectival = general,
            SpecificAdjectival = specific,
            Degree = degree is null ? null : Math.Clamp(degree.Value, 0.0, 1.0)
        };
}

// MMC-EEM-V2.5 - Emotion. Carries the chosen Emotion label + Degree.
public sealed class Emotion
{
    public string Header { get; init; } = "MMC-EEM-V2.5";
    public string EntityEmotionID { get; init; } = "";
    public string? Category { get; init; }
    public string? GeneralAdjectival { get; init; }
    public string? SpecificAdjectival { get; init; }
    public double? Degree { get; init; }

    public static Emotion Of(FactorLabel label) => new()
    {
        EntityEmotionID = Guid.NewGuid().ToString(),
        Category = label.Category, GeneralAdjectival = label.GeneralAdjectival,
        SpecificAdjectival = label.SpecificAdjectival, Degree = label.Degree
    };
}

// MMC-ECS-V2.5 - Cognitive State.
public sealed class CognitiveState
{
    public string Header { get; init; } = "MMC-ECS-V2.5";
    public string EntityCognitiveStateID { get; init; } = "";
    public string? Category { get; init; }
    public string? GeneralAdjectival { get; init; }
    public string? SpecificAdjectival { get; init; }
    public double? Degree { get; init; }

    public static CognitiveState Of(FactorLabel label) => new()
    {
        EntityCognitiveStateID = Guid.NewGuid().ToString(),
        Category = label.Category, GeneralAdjectival = label.GeneralAdjectival,
        SpecificAdjectival = label.SpecificAdjectival, Degree = label.Degree
    };
}

// MMC-ESA-V2.5 - Social Attitude.
public sealed class SocialAttitude
{
    public string Header { get; init; } = "MMC-ESA-V2.5";
    public string SocialAttitudeID { get; init; } = "";
    public string? Category { get; init; }
    public string? GeneralAdjectival { get; init; }
    public string? SpecificAdjectival { get; init; }
    public double? Degree { get; init; }

    public static SocialAttitude Of(FactorLabel label) => new()
    {
        SocialAttitudeID = Guid.NewGuid().ToString(),
        Category = label.Category, GeneralAdjectival = label.GeneralAdjectival,
        SpecificAdjectival = label.SpecificAdjectival, Degree = label.Degree
    };
}

// MMC-TPS-V2.5 - Text Personal Status: the three Factors for the Text modality.
public sealed class TextPersonalStatus
{
    public string Header { get; init; } = "MMC-TPS-V2.5";
    public string TextPersonalStatusID { get; init; } = Guid.NewGuid().ToString();
    public CognitiveState? TextCognitiveState { get; init; }
    public Emotion? TextEmotion { get; init; }
    public SocialAttitude? TextSocialAttitude { get; init; }
}

// MMC-SPS-V2.5 - Speech Personal Status.
public sealed class SpeechPersonalStatus
{
    public string Header { get; init; } = "MMC-SPS-V2.5";
    public string SpeechPersonalStatusID { get; init; } = Guid.NewGuid().ToString();
    public CognitiveState? SpeechCognitiveState { get; init; }
    public Emotion? SpeechEmotion { get; init; }
    public SocialAttitude? SpeechSocialAttitude { get; init; }
}

// MMC-FPS-V2.5 - Face Personal Status.
public sealed class FacePersonalStatus
{
    public string Header { get; init; } = "MMC-FPS-V2.5";
    public string FacePersonalStatusID { get; init; } = Guid.NewGuid().ToString();
    public CognitiveState? FaceCognitiveState { get; init; }
    public Emotion? FaceEmotion { get; init; }
    public SocialAttitude? FaceSocialAttitude { get; init; }
}

// MMC-GPS-V2.5 - Gesture Personal Status.
public sealed class GesturePersonalStatus
{
    public string Header { get; init; } = "MMC-GPS-V2.5";
    public string GesturePersonalStatusID { get; init; } = Guid.NewGuid().ToString();
    public CognitiveState? GestureCognitiveState { get; init; }
    public Emotion? GestureEmotion { get; init; }
    public SocialAttitude? GestureSocialAttitude { get; init; }
}

// MMC-EPS-V2.5 - Entity Personal Status: the modality container, assembled by
// Personal Status Multiplexing from the per-modality Personal Statuses. Each is
// optional; at least one is expected.
public sealed class EntityPersonalStatus
{
    public string Header { get; init; } = "MMC-EPS-V2.5";
    public string EntityPersonalStatusID { get; init; } = Guid.NewGuid().ToString();
    public TextPersonalStatus? TextPersonalStatus { get; init; }
    public SpeechPersonalStatus? SpeechPersonalStatus { get; init; }
    public FacePersonalStatus? FacePersonalStatus { get; init; }
    public GesturePersonalStatus? GesturePersonalStatus { get; init; }
}
