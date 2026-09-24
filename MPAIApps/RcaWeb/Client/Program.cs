using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Mpai.RcaWeb;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<RcaShell>("#app");

// The page's own origin: the Host serves this client and forwards /MPAI/AIFU to
// the Service, so every request stays on one origin.
// This client, as the Service counts it: a random identifier, made when the page
// starts and sent with every request. It says nothing about the person.
var clientId = Guid.NewGuid().ToString("N");
builder.Services.AddScoped(_ =>
{
    var http = new HttpClient
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
        Timeout     = TimeSpan.FromSeconds(180)
    };
    http.DefaultRequestHeaders.Add("MPAI-Client", clientId);
    return http;
});

await builder.Build().RunAsync();
