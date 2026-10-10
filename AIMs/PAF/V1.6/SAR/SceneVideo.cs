using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Paf.Sar;

// WHAT THE VIEWER SEES, AS A VIDEO: Scene and Avatar Rendering's 2D alternative (the
// author, 2026/10/03: one video Visual Object per utterance, of the whole view). The
// scene is drawn by the same page the User Agent draws it with (UserAgent/Assets/
// cav-webview.html), here in a browser without a window - Microsoft Edge or Chromium,
// driven through its DevTools protocol - frame by frame on SAR's own clock: the
// page is given the 3D Model Scene, the Point of View and the utterance's Face and
// Body Descriptors, and asked for the frame at each time. ffmpeg makes the frames an
// MP4 (H.264). No sound: the speech is in the Audio Scene beside it.
//
// The browser and the page stay loaded between utterances; the browser is ended
// with the process. The page's three.js comes from its CDN, as in the client.
public sealed class SceneVideo : IAsyncDisposable
{
    public const int Fps = 25;

    private readonly Process browser;
    private readonly HttpListener http;
    private readonly ClientWebSocket socket;
    private readonly string profile;
    private readonly string ffmpeg;
    private readonly int width, height;
    private int ids;

    private SceneVideo(Process browser, HttpListener http, ClientWebSocket socket, string profile, string ffmpeg, int width, int height) =>
        (this.browser, this.http, this.socket, this.profile, this.ffmpeg, this.width, this.height) = (browser, http, socket, profile, ffmpeg, width, height);

    // The browser and ffmpeg this machine has, or null.
    public static string? FindBrowser(string? given = null) =>
        new[]
        {
            given,
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "/usr/bin/microsoft-edge", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome"
        }.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

    public static string? FindFfmpeg(string? given = null)
    {
        if (!string.IsNullOrWhiteSpace(given) && File.Exists(given)) return given;
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d.Trim('"'), name)).FirstOrDefault(File.Exists);
    }

    public static async Task<SceneVideo> StartAsync(string assets, string browserPath, string ffmpegPath,
                                                    int width = 640, int height = 480, CancellationToken cancel = default)
    {
        // The page and its assets, as the client's virtual host serves them.
        var port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://localhost:{port}/");
        http.Start();
        _ = Task.Run(() => Serve(http, assets));

        var profile = Path.Combine(Path.GetTempPath(), "mpai-sar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        // THE BROWSER INHERITS NOTHING. On Windows a child started directly inherits the
        // parent's inheritable handles - among them the pipe a test runner reads the
        // output through - and a browser that outlives its parent then holds that pipe
        // open: the runner waits for ever. Started through the shell it inherits none.
        // On Linux the standard streams are the only ones inherited: they are redirected.
        var start = new ProcessStartInfo(browserPath)
        {
            ArgumentList =
            {
                "--headless=new", "--remote-debugging-port=0", $"--user-data-dir={profile}",
                "--no-first-run", "--no-default-browser-check", "--mute-audio",
                "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist",
                $"--window-size={width},{height}", "about:blank"
            }
        };
        if (OperatingSystem.IsWindows()) { start.UseShellExecute = true; start.WindowStyle = ProcessWindowStyle.Hidden; }
        else { start.UseShellExecute = false; start.RedirectStandardError = true; start.RedirectStandardOutput = true; start.RedirectStandardInput = true; }
        var browser = Process.Start(start) ?? throw new InvalidOperationException("the browser did not start.");
        if (!OperatingSystem.IsWindows())
        {
            browser.ErrorDataReceived += (_, _) => { }; browser.BeginErrorReadLine();
            browser.OutputDataReceived += (_, _) => { }; browser.BeginOutputReadLine();
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { browser.Kill(true); } catch { } };

        // The DevTools port the browser chose, then its page.
        var activePort = Path.Combine(profile, "DevToolsActivePort");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(activePort) || new FileInfo(activePort).Length == 0)
        {
            if (DateTime.UtcNow > until || browser.HasExited) throw new InvalidOperationException("the browser did not start its DevTools.");
            await Task.Delay(100, cancel);
        }
        var devtools = int.Parse((await File.ReadAllLinesAsync(activePort, cancel))[0]);
        using var client = new HttpClient();
        string? page = null;
        while (page is null)
        {
            var list = JsonNode.Parse(await client.GetStringAsync($"http://127.0.0.1:{devtools}/json/list", cancel))!.AsArray();
            page = list.Select(t => t!).FirstOrDefault(t => (string?)t["type"] == "page")?["webSocketDebuggerUrl"]?.GetValue<string>();
            if (page is null) await Task.Delay(100, cancel);
        }
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(page), cancel);
        var video = new SceneVideo(browser, http, socket, profile, ffmpegPath, width, height);
        await video.SendAsync("Page.navigate", new JsonObject { ["url"] = $"http://localhost:{port}/cav-webview.html" }, cancel);
        await video.EvaluateAsync("(async () => { while (!window.cavFrames) await new Promise(r => setTimeout(r, 100)); return await window.cavFrames.ready; })()", cancel);
        return video;
    }

    // The video of an utterance: seconds long, the avatar animated by its Face and Body
    // Descriptors in the 3D Model Scene seen from the Point of View.
    public async Task<byte[]> RenderAsync(string modelScene, string pointOfView, string? face, string? body, double seconds,
                                          CancellationToken cancel = default)
    {
        string Literal(string? s) => s is null ? "null" : JsonSerializer.Serialize(s);
        await EvaluateAsync($"window.cavFrames.load({Literal(modelScene)}, {Literal(pointOfView)}, {Literal(face)}, {Literal(body)}, {width}, {height})", cancel);

        var dir = Path.Combine(Path.GetTempPath(), "mpai-sar-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var frames = Math.Max(1, (int)Math.Ceiling(seconds * Fps));
            for (var i = 0; i < frames; i++)
            {
                var t = (i / (double)Fps).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var url = (string?)(await EvaluateAsync($"window.cavFrames.at({t})", cancel)) ?? "";
                await File.WriteAllBytesAsync(Path.Combine(dir, $"f{i:D5}.jpg"), Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]), cancel);
            }
            var mp4 = Path.Combine(dir, "view.mp4");
            using var encode = Process.Start(new ProcessStartInfo(ffmpeg)
            {
                ArgumentList = { "-y", "-loglevel", "error", "-framerate", Fps.ToString(), "-i", Path.Combine(dir, "f%05d.jpg"),
                                 "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", mp4 },
                UseShellExecute = false, RedirectStandardError = true
            })!;
            var error = await encode.StandardError.ReadToEndAsync(cancel);
            await encode.WaitForExitAsync(cancel);
            if (encode.ExitCode != 0) throw new InvalidOperationException($"ffmpeg: {error.Trim()}");
            return await File.ReadAllBytesAsync(mp4, cancel);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- the DevTools protocol: a request, its reply ------------------------------

    private async Task<JsonNode?> EvaluateAsync(string expression, CancellationToken cancel)
    {
        var reply = await SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true
        }, cancel);
        if (reply?["exceptionDetails"] is { } failed)
            throw new InvalidOperationException($"the page: {failed["exception"]?["description"] ?? failed["text"]}");
        return reply?["result"]?["value"]?.DeepClone();
    }

    private async Task<JsonNode?> SendAsync(string method, JsonObject parameters, CancellationToken cancel)
    {
        var id = Interlocked.Increment(ref ids);
        var request = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString();
        await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, cancel);
        var buffer = new byte[1 << 16];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer, timeout.Token);
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            var frame = JsonNode.Parse(message.ToArray());
            if ((int?)frame?["id"] != id) continue;                    // an event, or another reply
            if (frame?["error"] is { } error) throw new InvalidOperationException($"{method}: {error["message"]}");
            return frame?["result"];
        }
    }

    // ---- the page's folder, served -------------------------------------------------

    private static async Task Serve(HttpListener http, string assets)
    {
        while (http.IsListening)
        {
            HttpListenerContext context;
            try { context = await http.GetContextAsync(); } catch { return; }
            try
            {
                var name = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'));
                var path = Path.GetFullPath(Path.Combine(assets, name));
                if (!path.StartsWith(Path.GetFullPath(assets), StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(path);
                if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("https://cavapp.local/", "/"));
                context.Response.ContentType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".html" => "text/html; charset=utf-8", ".glb" => "model/gltf-binary", ".js" => "text/javascript",
                    _ => "application/octet-stream"
                };
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch { try { context.Response.Abort(); } catch { } }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        socket.Dispose();
        try { browser.Kill(true); } catch { }
        http.Close();
        try { Directory.Delete(profile, true); } catch { }
    }
}
