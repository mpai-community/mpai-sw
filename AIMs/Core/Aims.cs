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

// ---- Audio acquisition: a device of the User Agent's Physical Layer (the Microphone
//      Unit), not an AIM. Acquires a Basic Audio Object from a microphone, a microphone
//      array or a file.
public interface IAudioAcquisition
{
    Task<BasicAudioObject> AcquireAsync(AcquisitionRequest request);
}

// Optional capability of an audio acquisition device: manual start/stop
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
// ITtsAim and IAsrAim live in Mpai.Core; ITttAim was
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

// ---- Visual acquisition: a device of the User Agent's Physical Layer (the Camera
//      Unit), not an AIM. Acquires a Basic Visual Object from a camera or a file.
public interface IVisualAcquisition
{
    Task<BasicVisualObject> AcquireAsync(VisualAcquisitionRequest request);
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
