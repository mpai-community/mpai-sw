using System;
using System.Threading.Tasks;

namespace Mpai.Core;

// ---------------------------------------------------------------------------
//  Every AIM Ã¢â‚¬â€ at any level, standalone or a SubAIM of a composite Ã¢â‚¬â€ carries
//  its STANDARD NAME. The name belongs to the role (the interface), not to the
//  engine that implements it, so it is declared once per role as a default.
//
//  Three forms, per the naming rule:
//    AimName        full name, spaces, natural casing   "Text and Image Query"
//    AimNameCompact no spaces, every word capitalised    "TextAndImageQuery"
//                   (including And, To, ... )
//    AimIdentifier  domain-prefixed identifier           "MMC-TIQ-V2.5"
// ---------------------------------------------------------------------------
public interface IAim
{
    string AimName { get; }
    string AimNameCompact { get; }
    string AimIdentifier { get; }
}

// ---- Audio Object Acquisition (CAE-AOA) Ã¢â‚¬â€ device edge, environment-dependent.
//      Acquires a Basic Audio Object from a device (microphone, network, disk).
public interface IAudioAcquisitionAim : IAim
{
    string IAim.AimName        => "Audio Object Acquisition";
    string IAim.AimNameCompact => "AudioObjectAcquisition";
    string IAim.AimIdentifier  => "CAE-AOA-V1.0";

    Task<BasicAudioObject> AcquireAsync(AcquisitionRequest request);
}

// Optional capability for an Audio Object Acquisition AIM: manual start/stop
// (press-to-stop) instead of a fixed duration.
public interface IStartStopAcquisition
{
    void StartAcquire();
    Task<BasicAudioObject> StopAcquireAsync();
}

// ---- Automatic Speech Recognition (MMC-ASR) Ã¢â‚¬â€ environment-independent
public interface IAsrAim : IAim
{
    string IAim.AimName        => "Automatic Speech Recognition";
    string IAim.AimNameCompact => "AutomaticSpeechRecognition";
    string IAim.AimIdentifier  => "MMC-ASR-V2.5";

    Task<BasicTextObject> ProcessAsync(BasicSpeechObject speech);
}

// ---- Text and Image Query (MMC-TIQ) Ã¢â‚¬â€ environment-independent
public interface ITiqAim : IAim
{
    string IAim.AimName        => "Text and Image Query";
    string IAim.AimNameCompact => "TextAndImageQuery";
    string IAim.AimIdentifier  => "MMC-TIQ-V2.5";

    Task<BasicTextObject> ProcessAsync(BasicTextObject question, BasicVisualObject image);
}

// ---- Text to Speech (MMC-TTS) Ã¢â‚¬â€ environment-independent
// ---- Text-to-Text Translation (MMC-TTT) Ã¢â‚¬â€ environment-independent
//
// Declared HERE, with the other AIM interfaces, rather than beside its engine.
// ITtsAim, IAsrAim and IAudioAcquisitionAim all live in Mpai.Core; ITttAim was
// put in Mmc.Ttt when it was written, which made it the odd one out and meant
// anyone declaring a field of this type needed a using nobody expected.
public interface ITttAim : IAim
{
    string IAim.AimName        => "Text-to-Text Translation";
    string IAim.AimNameCompact => "TextToTextTranslation";
    string IAim.AimIdentifier  => "MMC-TTT-V2.5";

    // CancellationToken is qualified: Aims.cs imports System and
    // System.Threading.Tasks, not System.Threading, and one interface is a poor
    // reason to widen what the whole file imports.
    Task<BasicTextObject> ProcessAsync(
        BasicTextObject     text,
        BasicSelectorObject languages,
        System.Threading.CancellationToken token = default);
}

public interface ITtsAim : IAim
{
    string IAim.AimName        => "Text to Speech";
    string IAim.AimNameCompact => "TextToSpeech";
    string IAim.AimIdentifier  => "MMC-TTS-V2.5";

    Task<BasicSpeechObject> ProcessAsync(BasicTextObject text);
}

// ---- Audio Object Delivery (CAE-AOD) Ã¢â‚¬â€ device edge, environment-dependent.
//      Delivers a Basic Audio Object to a device (loudspeaker, network, disk).
public interface IAudioDeliveryAim : IAim
{
    string IAim.AimName        => "Audio Object Delivery";
    string IAim.AimNameCompact => "AudioObjectDelivery";
    string IAim.AimIdentifier  => "CAE-AOD-V1.0";

    Task DeliverAsync(BasicAudioObject audio);
}

// ---- Visual Object Acquisition (CVE-VOA) Ã¢â‚¬â€ device edge, environment-dependent.
//      Acquires a Basic Visual Object from a source (file now; camera later).
public interface IVisualAcquisitionAim : IAim
{
    string IAim.AimName        => "Visual Object Acquisition";
    string IAim.AimNameCompact => "VisualObjectAcquisition";
    string IAim.AimIdentifier  => "CVE-VOA-V1.0";

    Task<BasicVisualObject> AcquireAsync(VisualAcquisitionRequest request);
}

// ---- Visual Object Delivery (CVE-VOD) Ã¢â‚¬â€ device edge, environment-dependent.
//      Delivers a Basic Visual Object to a destination (a window, a display,
//      a file). The mirror of CVE-VOA, and the visual counterpart of CAE-AOD.
public interface IVisualDeliveryAim : IAim
{
    string IAim.AimName        => "Visual Object Delivery";
    string IAim.AimNameCompact => "VisualObjectDelivery";
    string IAim.AimIdentifier  => "CVE-VOD-V1.0";

    Task DeliverAsync(BasicVisualObject visual);
}

// ---- Answer to Multimodal Question (MMC-AMQ) Ã¢â‚¬â€ the composite AIM.
//      A composite is itself an AIM, so it carries a standard name too.
public interface IAmqAim : IAim
{
    string IAim.AimName        => "Answer to Multimodal Question";
    string IAim.AimNameCompact => "AnswerToMultimodalQuestion";
    string IAim.AimIdentifier  => "MMC-AMQ-V2.5";
}

// Request to an Audio Object Acquisition AIM (how much to acquire).
public sealed class AcquisitionRequest
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);
}

// Request to a Visual Object Acquisition AIM (which source to acquire from).
public sealed class VisualAcquisitionRequest
{
    public string? SourcePath { get; init; }   // file path for the file source
    public string? VisualObjectType { get; init; }
}

// Output of the MMC-AMQ composite: the recognised question and the answer
// (as Text and as Speech).
public sealed class AmqResult
{
    public BasicVisualObject Image { get; init; } = new();
    public BasicTextObject Question { get; init; } = new();
    public BasicTextObject Answer { get; init; } = new();
    public BasicSpeechObject SpeechAnswer { get; init; } = new();
}
