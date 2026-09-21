// THE HOST OF THE BROWSER RCA.
//
//   /                   the client (Blazor WebAssembly) and its files
//   /avatar/...         the avatar page and its model, from UAs\Assets, adapted
//                       as they are served: the page was written for WebView2,
//                       and the file itself is left as it is
//   /mas/MPAI-MAS.orch  the client's own workflow, from UAs\Orchestration
//   /MPAI/AIFU/...      passed on to the MPAI-MAS Service
//
//   dotnet run --project MPAIApps\RcaWeb\Host -- --Service https://localhost:5005/ --Urls https://localhost:5010

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // The client's files are served from its build output as static web assets,
    // which ASP.NET does in Development; that is this program's default.
    EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Development
});
builder.WebHost.UseStaticWebAssets();
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "https://localhost:5010");

var service = new Uri(builder.Configuration["Service"] ?? "https://localhost:5005/");
var root    = RepositoryRoot(AppContext.BaseDirectory);
var assets  = Path.Combine(root, "UAs", "Assets");
var masOrch = Path.Combine(root, "UAs", "Orchestration", "MPAI-MAS.orch");

var app = builder.Build();
// _framework and the client's files are served by MapStaticAssets, the .NET 10 way,
// compression included; the older UseBlazorFrameworkFiles would compete with it.
app.UseStaticFiles();
if (File.Exists(Path.Combine(AppContext.BaseDirectory, $"{builder.Environment.ApplicationName}.staticwebassets.endpoints.json")))
    app.MapStaticAssets();

// THE AVATAR PAGE, AS A BROWSER NEEDS IT. Its model and lighting are fetched from
// here instead of WebView2's virtual host, and a few lines give it the
// chrome.webview messaging it listens to, carried by the browser's own
// postMessage from the page around it.
const string WebViewShim = """
<script>
  window.chrome = window.chrome || {};
  if (!window.chrome.webview) {
    window.chrome.webview = {
      addEventListener: (type, handler) => {
        if (type === 'message')
          window.addEventListener('message', e => {
            if (e.origin === window.location.origin) handler({ data: e.data });
          });
      },
      postMessage: m => window.parent.postMessage(m, window.location.origin)
    };
  }
</script>
""";

app.MapGet("/avatar/cav-webview.html", async () =>
{
    var html = await File.ReadAllTextAsync(Path.Combine(assets, "cav-webview.html"));
    html = html.Replace("https://cavapp.local/", "/avatar/");
    var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
    html = head >= 0 ? html.Insert(head + "<head>".Length, "\n" + WebViewShim) : WebViewShim + html;
    return Results.Content(html, "text/html; charset=utf-8");
});

var avatarFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["cav-avatar.glb"] = "model/gltf-binary",
    ["studio.hdr"]     = "application/octet-stream"
};
app.MapGet("/avatar/{file}", (string file) =>
    avatarFiles.TryGetValue(file, out var type) && File.Exists(Path.Combine(assets, file))
        ? Results.File(Path.Combine(assets, file), type)
        : Results.NotFound());

app.MapGet("/mas/MPAI-MAS.orch", () =>
    File.Exists(masOrch) ? Results.File(masOrch, "text/plain; charset=utf-8") : Results.NotFound());

// THE SERVICE, THROUGH THIS ORIGIN. A development convenience accepts the
// Service's development certificate; a Service reached over a network presents
// one this machine trusts.
var forward = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
})
{ BaseAddress = service, Timeout = TimeSpan.FromMinutes(3) };

app.Map("/MPAI/AIFU/{**rest}", async (HttpContext http) =>
{
    var target = new Uri(service, http.Request.Path.Value!.TrimStart('/') + http.Request.QueryString);
    using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);

    if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body);
        body.Position = 0;
        request.Content = new StreamContent(body);
        if (http.Request.ContentType is { } contentType)
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
    }
    else if (http.Request.Method is "POST" or "PUT")
    {
        request.Content = new ByteArrayContent(Array.Empty<byte>());
    }
    if (http.Request.Headers.Authorization.Count > 0)
        request.Headers.TryAddWithoutValidation("Authorization", http.Request.Headers.Authorization.ToString());

    using var response = await forward.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
    http.Response.StatusCode = (int)response.StatusCode;
    if (response.Content.Headers.ContentType is { } type)
        http.Response.ContentType = type.ToString();
    await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
});

app.MapFallbackToFile("index.html");

Console.WriteLine($"=== MPAI-MAS browser client ===");
Console.WriteLine($"  Open:     {builder.Configuration["Urls"] ?? "https://localhost:5010"}");
Console.WriteLine($"  Service:  {service}");
Console.WriteLine($"  Assets:   {assets}");
app.Run();

// The repository root is the first folder above this program holding UAs\Assets.
static string RepositoryRoot(string from)
{
    for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "UAs", "Assets", "cav-webview.html")))
            return dir.FullName;
    throw new DirectoryNotFoundException($"No UAs\\Assets\\cav-webview.html above {from}.");
}
