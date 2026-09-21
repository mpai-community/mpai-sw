using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Mpai.RcaWeb;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<RcaShell>("#app");

// The page's own origin: the Host serves this client and forwards /MPAI/AIFU to
// the Service, so every request stays on one origin.
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    Timeout     = TimeSpan.FromSeconds(180)
});

await builder.Build().RunAsync();
