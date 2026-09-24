using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Rca;
using Mpai.RcaWeb.Mas;
using Mpai.RcaWeb.Media;
using Mpai.RcaWeb.Wdl;
using Mpai.Wdl;

namespace Mpai.RcaWeb;

// THE REMOTE CLIENT APPLICATION, IN A BROWSER. It holds no App: it runs
// MPAI-MAS, the client's own workflow, and MPAI-MAS runs the App the person
// chooses. Its sources and presenters are the desktop RCA's, with the browser
// doing the capturing and the rendering.
public partial class RcaShell : ComponentBase
{
    [Inject] private HttpClient Http { get; set; } = default!;
    [Inject] private IJSRuntime Js   { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    // WHICH COLLECTION, AND WHICH APP, THE ADDRESS NAMES: .../c/Language uses the
    // Language collection; .../app/MAT opens MAT directly, without MPAI-MAS.
    private string? collection, directApp;
    private WebAppDirectory Directory() => new(Http, collection);

    protected override void OnInitialized()
    {
        var parts = new Uri(Nav.Uri).AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i] == "c")   collection = Uri.UnescapeDataString(parts[i + 1]);
            if (parts[i] == "app") directApp  = Uri.UnescapeDataString(parts[i + 1]);
        }
    }

    // FINDING AN APP, and its page before it starts.
    private string searchWords = "", searchCategory = "";
    private IReadOnlyList<string> categories = Array.Empty<string>();
    private WebAppDirectory.Descriptor? appPage;

    private string AppTitle    = "";
    private string Instruction = "Press Start. The browser will ask to use the microphone.";
    private string StatusLine  = "";

    private bool started, stopEnabled;
    private CancellationTokenSource? _stopping, _appStopping;

    // TYPING CLAIMS THE TURN. While the person may speak or type, the microphone
    // listens - and hears the keys. The first character typed ends the listening,
    // so the turn is the typed one.
    private CancellationTokenSource? _typingClaims;
    private readonly Dictionary<string, IAsyncNorthApi> _controllers = new();

    private IReadOnlyList<WebAppDirectory.App> apps = Array.Empty<WebAppDirectory.App>();
    private bool showApps;
    private TaskCompletionSource<string?>? choosing;

    private bool   typing;
    private string typedText = "";
    private TaskCompletionSource<string>? typed;

    private bool   choosingLanguages;
    private string langFrom = "en", langTo = "it";
    private TaskCompletionSource<(string, string)?>? languages;
    private string? _sourceLanguage;

    private bool choosingPicture;
    private TaskCompletionSource<(string Name, byte[] Data)?>? picture;
    private string? stageImage, stageTitle;

    private static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),  ("it", "Italiano"), ("es", "Espanol"), ("pt", "Portugues"),
        ("fr", "Francais"), ("de", "Deutsch"),  ("ja", "Nihongo"), ("zh", "Zhongwen")
    };

    // ---- running ------------------------------------------------------------

    // THE CLICK THAT OPENS THE WAY. A browser plays sound and opens the
    // microphone only after the person has done something; Start is that.
    private async Task StartAsync()
    {
        started = true;
        try { await Js.InvokeVoidAsync("rca.unlock"); }
        catch (Exception ex) { Status("microphone: " + ex.Message); }

        // PRESENT WHILE OPEN, GONE WHEN CLOSED: see rca.presence.
        if (Http.DefaultRequestHeaders.TryGetValues("MPAI-Client", out var ids))
            try { await Js.InvokeVoidAsync("rca.presence", ids.First()); } catch { }
        _stopping = new CancellationTokenSource();
        await RunAppAsync(directApp ?? "MAS");
        started = false;
        Refresh();
    }

    private async Task RunAppAsync(string appId)
    {
        try
        {
            Status($"obtaining {appId}...");
            var directory = Directory();
            var offered   = await directory.ListAsync();
            var app       = offered.FirstOrDefault(a => a.Id == appId);

            // MPAI-MAS is the client's own; every other App comes from the Service.
            var text = appId == "MAS"
                ? await Http.GetStringAsync("mas/MPAI-MAS.orch")
                : await directory.WorkflowAsync(appId);
            var workflow = new WorkflowReader().Read(text);
            if (appId != "MAS") _sourceLanguage = null;

            // AN APP RUNS UNDER A CONTROLLER OF ITS OWN, kept for when it is chosen again.
            if (!_controllers.TryGetValue(appId, out var north))
                _controllers[appId] = north = new RemoteNorthApiAsync(Http);

            AppTitle = app?.Name ?? (appId == "MAS" ? "" : appId);
            stopEnabled = true;
            Refresh();

            var interpreter = new AsyncWorkflowInterpreter(north, Devices(), Status);

            // STOP ENDS THE APP THAT IS RUNNING, NOT MPAI-MAS.
            using var appStop = appId == "MAS" ? null : new CancellationTokenSource();
            _appStopping = appStop;
            try     { await interpreter.RunAsync(workflow, appStop?.Token ?? _stopping?.Token ?? default); }
            finally { _appStopping = null; }

            Status($"{app?.Name ?? appId} finished");
        }
        catch (Exception ex)
        {
            Status($"{appId}: {ex.Message}");
            Console.Error.WriteLine(ex);
        }
        finally
        {
            AppTitle = "";
            Instruction = "";
            typing = false;
            stageImage = null;
            Refresh();
        }
    }

    private void Stop() => (_appStopping ?? _stopping)?.Cancel();

    // ---- sources and presenters --------------------------------------------

    private DeviceRegistry Devices()
    {
        var devices = new DeviceRegistry();

        // SPEECH, from the microphone, until the person stops speaking. Stop, or
        // text typed first, ends the capture at once.
        devices.RegisterAcquire("OSD-BSO-V1.5", async (viaVad, wanted, abandon) =>
        {
            Instruct("Speak when you are ready.");
            var stop = (_appStopping ?? _stopping)?.Token ?? CancellationToken.None;
            using var claims = new CancellationTokenSource();
            _typingClaims = claims;
            using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, abandon, claims.Token);
            using var onEnd  = either.Token.Register(() => _ = Js.InvokeVoidAsync("rca.abandonCapture"));
            var b64 = await Js.InvokeAsync<string?>("rca.captureSpeech");
            if (either.IsCancellationRequested || string.IsNullOrEmpty(b64)) return null;
            var pcm = Convert.FromBase64String(b64);
            return pcm.Length == 0 ? null : MpaiJson.ToJson(SpeechPackaging.FromPcm16k(pcm, _sourceLanguage));
        });

        // TEXT: typed by the person, or the name of an App chosen from the list.
        devices.RegisterAcquire("OSD-BTO-V1.5", async (viaVad, wanted, abandon) =>
        {
            var stop = (_appStopping ?? _stopping)?.Token ?? CancellationToken.None;

            // HOW MANY ARE HERE: a sentence when others use the Service now; else nothing.
            if ((wanted ?? "").Contains("Concurrency", StringComparison.OrdinalIgnoreCase))
            {
                var count = await Directory().ActiveClientsAsync();
                return count is int n && n > 1
                    ? MpaiJson.ToJson(BasicTextObject.FromText($"You are the {Ordinal(n)} concurrent user of the MPAI as a Service App."))
                    : null;
            }

            if (!(wanted ?? "").Contains("AppName", StringComparison.OrdinalIgnoreCase))
            {
                Instruct("Speak, or type and press Enter.");
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                typed = tcs; typedText = ""; typing = true;
                Refresh();
                _ = Js.InvokeVoidAsync("rca.focus", "typed");
                using var r1 = stop.Register(CancelTyped);
                using var r2 = abandon.Register(CancelTyped);
                var words = await tcs.Task;
                return string.IsNullOrWhiteSpace(words) ? null : MpaiJson.ToJson(BasicTextObject.FromText(words.Trim()));
            }

            // MPAI-MAS IS NOT AN APP, so it is not offered.
            apps = (await Directory().ListAsync())
                   .Where(a => !a.Id.Equals("MAS", StringComparison.OrdinalIgnoreCase)).ToList();
            categories = await Directory().CategoriesAsync();
            searchWords = searchCategory = ""; appPage = null;
            var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            choosing = picked; showApps = true;
            Instruct("Choose an App.");
            using var r3 = stop.Register(() => picked.TrySetResult(null));
            var name = await picked.Task;
            showApps = false; Refresh();
            return name is null ? null : MpaiJson.ToJson(BasicTextObject.FromText(name));
        });

        // THE LANGUAGES, input and output together, as one Language Selector.
        devices.RegisterAcquire("OSD-SEL-V1.5", async (_, _) =>
        {
            Instruct("Choose the input and output languages.");
            var tcs = new TaskCompletionSource<(string, string)?>(TaskCreationOptions.RunContinuationsAsynchronously);
            languages = tcs; choosingLanguages = true;
            Refresh();
            var pair = await tcs.Task;
            choosingLanguages = false; Refresh();
            if (pair is not { } p) return null;
            _sourceLanguage = p.Item1;
            return MpaiJson.ToJson(BasicSelectorObject.Languages(p.Item1, p.Item2));
        });

        // A VISUAL OBJECT: a Face from the camera; anything else, a picture the
        // person chooses (a browser opens a file only on the person's click).
        devices.RegisterAcquire("OSD-BVO-V1.5", async (_, wanted) =>
        {
            if ((wanted ?? "").Contains("\"Face\"", StringComparison.Ordinal))
            {
                var frame = await Js.InvokeAsync<string?>("rca.captureFrame");
                return string.IsNullOrEmpty(frame) ? null
                     : MpaiJson.ToJson(BasicVisualObject.FromFile("webcam.jpg", Convert.FromBase64String(frame), "Face"));
            }
            Instruct("Choose a picture.");
            var tcs = new TaskCompletionSource<(string Name, byte[] Data)?>(TaskCreationOptions.RunContinuationsAsynchronously);
            picture = tcs; choosingPicture = true;
            Refresh();
            var chosen = await tcs.Task;
            choosingPicture = false; Refresh();
            return chosen is not { } c ? null : MpaiJson.ToJson(BasicVisualObject.FromFile(c.Name, c.Data, "Picture"));
        });

        devices.Run = async app => await RunAppAsync(app);

        // THE AVATAR: speech and face together, and the time it takes to say it.
        devices.RegisterPresent("avatar", async data =>
        {
            string? wavB64 = null, fdoJson = null;
            double seconds = 0;
            if (data.TryGetValue("OSD-BSO-V1.5", out var sj) && !string.IsNullOrWhiteSpace(sj))
            {
                var wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data;
                if (wav is { Length: > 0 }) { wavB64 = Convert.ToBase64String(wav); seconds = SpeechPackaging.WavSeconds(wav); }
            }
            if (data.TryGetValue("PAF-FDO-V1.6", out var fj) && !string.IsNullOrWhiteSpace(fj)) fdoJson = fj;
            if (wavB64 is null && fdoJson is null) return;
            await Js.InvokeVoidAsync("rca.present", fdoJson, wavB64);
            await Task.Delay(TimeSpan.FromSeconds(seconds + 0.8));
        });

        // THE STAGE: a picture the person handed over.
        devices.RegisterPresent("stage", data =>
        {
            foreach (var kv in data)
            {
                if (!kv.Key.Contains("OSD-BVO", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var visual = MpaiJson.FromJson<BasicVisualObject>(kv.Value);
                    if (visual?.Data is not { Length: > 0 }) continue;
                    stageImage = "data:image/*;base64," + Convert.ToBase64String(visual.Data);
                    stageTitle = visual.FileName ?? "Picture";
                    Refresh();
                }
                catch { /* what cannot be shown is still what was given */ }
            }
            return Task.CompletedTask;
        });

        // THE SCREEN: what is displayed or prompted.
        devices.RegisterPresent("screen", data =>
        {
            foreach (var kv in data)
            {
                if (kv.Key == "Prompt") { Instruct(kv.Value); continue; }
                if (kv.Key.StartsWith("Display:OSD-BTO", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("OSD-BTO-V1.5", StringComparison.OrdinalIgnoreCase))
                { Instruct(Words(kv.Value)); continue; }
                if (kv.Key.StartsWith("Display", StringComparison.OrdinalIgnoreCase)) Instruct(kv.Value);
            }
            return Task.CompletedTask;
        });

        return devices;
    }

    // ---- the page -----------------------------------------------------------

    // CHOOSING OPENS THE APP'S PAGE - what it asks for, what it keeps - and Open
    // starts it: a person consents before the App begins.
    // CHOOSING STARTS THE APP DIRECTLY, as it always did before its page was
    // added; the page itself (ShowDetail's descriptor lookup, Open, Back) stays
    // for when it is wanted again, just not on the path a click takes today.
    private void ChooseApp(string id)
    {
        var waiting = choosing; choosing = null;
        waiting?.TrySetResult(id);
    }

    private void OpenPage()
    {
        if (appPage is null) return;
        var id = appPage.Id; appPage = null;
        var waiting = choosing; choosing = null;
        waiting?.TrySetResult(id);
    }

    private void ClosePage() { appPage = null; Refresh(); }

    private static string PageFacts(WebAppDirectory.Descriptor d) =>
        string.Join("  -  ", new[]
        {
            d.Standard,
            d.Version.Length > 0 ? "version " + d.Version : "",
            d.Languages.Count > 0 ? "languages: " + string.Join(", ", d.Languages) : ""
        }.Where(x => x.Length > 0));

    private async Task SearchAsync()
    {
        try
        {
            var found = searchWords.Trim().Length == 0 && searchCategory.Length == 0
                ? await Directory().ListAsync()
                : await Directory().SearchAsync(searchWords.Trim(), searchCategory);
            apps = found.Where(a => !a.Id.Equals("MAS", StringComparison.OrdinalIgnoreCase)).ToList();
            Status(apps.Count == 0 ? "no App matches" : $"{apps.Count} App(s) found");
        }
        catch (Exception ex) { Status("the search failed: " + ex.Message); }
    }

    private async Task OnSearchKey(KeyboardEventArgs e) { if (e.Key == "Enter") await SearchAsync(); }

    private async Task OnCategory(ChangeEventArgs e) { searchCategory = e.Value?.ToString() ?? ""; await SearchAsync(); }

    private void LanguagesChosen()
    {
        var waiting = languages; languages = null;
        waiting?.TrySetResult((langFrom, langTo));
    }

    private async Task PictureChosen(InputFileChangeEventArgs e)
    {
        var file = e.File;
        using var stream = file.OpenReadStream(maxAllowedSize: 20 * 1024 * 1024);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var waiting = picture; picture = null;
        waiting?.TrySetResult((file.Name, memory.ToArray()));
    }

    private void OnTypedInput()
    {
        if (typed is not null && !string.IsNullOrEmpty(typedText))
            try { _typingClaims?.Cancel(); } catch { }
    }

    private void OnTypedKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey) SendTyped();
    }

    private void SendTyped()
    {
        if (typed is null || string.IsNullOrWhiteSpace(typedText)) return;
        var words = typedText;
        typing = false;
        var waiting = typed; typed = null;
        waiting.TrySetResult(words);
        Refresh();
    }

    // The text box closes without giving anything: the person spoke instead, or pressed Stop.
    private void CancelTyped()
    {
        if (typed is null) return;
        typing = false; typedText = "";
        var waiting = typed; typed = null;
        waiting.TrySetResult("");
        Refresh();
    }

    // "You are the 2nd concurrent user ..." - the ordinal of a count.
    private static string Ordinal(int n) =>
        (n % 100) is 11 or 12 or 13 ? n + "th"
        : (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };

    private static string Words(string json)
    {
        try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? ""; }
        catch { return ""; }
    }

    private void Instruct(string text) { Instruction = text; Refresh(); }
    private void Status(string text)   { StatusLine  = text; Refresh(); Console.WriteLine(text); }
    private void Refresh() => _ = InvokeAsync(StateHasChanged);
}
