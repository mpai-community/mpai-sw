using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Mas.Server;

namespace Mpai.Mas.Service;

// THE FIRST ANSWER AS FAST AS THE NEXT (the author, 2026/10/06: on a server the
// first question waits far longer than the later ones). Building a Module loads
// its AIMs, but several engines still do their slowest work only on the first
// request: Ollama loads the language model and reads the whole system prompt,
// a resident Piper starts and loads its voice the first time that voice speaks,
// whisper-server and ONNX Runtime allocate and initialise on their first run.
//
// So, before the Service accepts anyone, each Module answers a few canned turns -
// the turns a person would make - in a session of its own that is then ended, so
// nothing of them is kept. A turn that fails is reported and the Service goes on:
// a warm-up never stops the Service from starting.
//
// MAC and ACR are not warmed: ACR registers a person into the gallery, and a warm-up
// writes nothing a person could later find.
//
// Off with "WarmUp": false in mas-server.json. The spoken turn and the picture
// come from WarmUpDirectory (question.wav, picture.jpg); without them the turns
// that need them are skipped, and the typed ones still run.
internal static class WarmUp
{
    private const string Session = "warm-up";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    public static void Run(
        IModuleRunner runner,
        string settingsPath,
        string? directory,
        Action<string> say)
    {
        var clock = Stopwatch.StartNew();
        say("Warming up: the first answer of each App, before anyone asks.");

        var avatar  = AvatarUtterance.AvatarDatum("cav-avatar.glb");
        var speech  = Speech(Find(directory, "question.wav"));
        var picture = Picture(Find(directory, "picture.jpg") ?? Find(directory, "red.jpg"));

        if (speech is null)  say($"  warm-up: no question.wav in {directory ?? "(no WarmUpDirectory)"} - spoken turns skipped");
        if (picture is null) say($"  warm-up: no picture.jpg in {directory ?? "(no WarmUpDirectory)"} - picture turns skipped");

        var avt = ("PAF-AVT-V1.6#1", avatar);

        // MAS-App: the Welcome every client hears first, before choosing an App.
        Turn(runner, say, "MAS-App welcome", Program.MasModule, Text(1, "Welcome."), avt);

        // MAD: typed - Ollama loaded, the system prompt read; RSR's English voice.
        Turn(runner, say, "MAD typed", Program.MadModule, Text(2, "Hello."), avt);
        if (speech is not null)
            Turn(runner, say, "MAD spoken", Program.MadModule, ("OSD-BSO-V1.5#1", speech), avt);

        // AMQ: the picture model.
        if (picture is not null)
            Turn(runner, say, "AMQ", Program.AmqModule, ("OSD-BVO-V1.5#1", picture), Text(2, "What color is the picture?"), avt);

        // MAT: the translation model, and every other voice a translation can speak.
        foreach (var language in Languages(settingsPath).Where(l => l != "en"))
            Turn(runner, say, $"MAT en->{language}", Program.MatModule,
                 ("OSD-SEL-V1.5#1", MpaiJson.ToJson(BasicSelectorObject.Languages("en", language))), Text(2, "Hello."), avt);

        // MPD: the voice and face emotion models with the dialogue.
        if (speech is not null)
        {
            var mpd = new List<(string, string)> { ("OSD-BSO-V1.5#1", speech), avt };
            if (picture is not null) mpd.Add(("OSD-BVO-V1.5#1", picture));
            Turn(runner, say, "MPD", Program.MpdModule, mpd.ToArray());
        }
        else Turn(runner, say, "MPD typed", Program.MpdModule, Text(2, "Hello."), avt);

        runner.SessionEnded(Session);
        say($"Warm-up done in {clock.Elapsed.TotalSeconds:F1} s.");
    }

    private static void Turn(IModuleRunner runner, Action<string> say, string what, string module,
                             params (string Key, string Json)[] inputs)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var run = Task.Run(() => runner.Run(module, inputs.ToDictionary(i => i.Key, i => i.Json), Session));
            if (!run.Wait(Limit)) { say($"  warm-up {what}: no answer within {Limit.TotalMinutes:F0} min - left to finish"); return; }
            var result = run.Result;
            say(result.Error is null
                ? $"  warm-up {what}: {clock.Elapsed.TotalSeconds:F1} s"
                : $"  warm-up {what}: {result.Error} after {clock.Elapsed.TotalSeconds:F1} s");
        }
        catch (Exception failure)
        {
            say($"  warm-up {what}: failed after {clock.Elapsed.TotalSeconds:F1} s - {failure.GetBaseException().Message}");
        }
    }

    // The languages the Text-to-Speech settings have a voice for ("Voice:it", ...).
    private static IEnumerable<string> Languages(string settingsPath)
    {
        try
        {
            var tts = JsonNode.Parse(File.ReadAllText(settingsPath))?["1MMC-TTS-V2.5-I01"]?.AsObject();
            if (tts is null) return [];
            return tts.Select(p => p.Key).Where(k => k.StartsWith("Voice:", StringComparison.Ordinal))
                      .Select(k => k["Voice:".Length..]).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();
        }
        catch { return []; }
    }

    private static string? Find(string? directory, string file)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var path = Path.Combine(directory, file);
        return File.Exists(path) ? path : null;
    }

    private static (string, string) Text(int port, string text) =>
        ("OSD-BTO-V1.5#" + port, MpaiJson.ToJson(BasicTextObject.FromText(text)));

    private static string? Picture(string? path) =>
        path is null ? null : MpaiJson.ToJson(BasicVisualObject.FromFile(Path.GetFileName(path), File.ReadAllBytes(path), "Picture"));

    private static string? Speech(string? path) =>
        path is null ? null : MpaiJson.ToJson(BasicSpeechObject.FromData(File.ReadAllBytes(path), new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            Format = new SpeechFormat
            {
                ContentFormats   = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } },
                TransportFormats = new SpeechTransportFormats { FileFormat = SpeechFileFormat.Wav }
            },
            Attributes = new SpeechAttributes
            {
                Metadata = new SpeechMetadata { Language = new Language { LanguageCode = "en", LanguageFormat = LanguageFormat.Iso639_1 } }
            }
        }));
}
