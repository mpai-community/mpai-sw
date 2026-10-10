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

// WHAT WOULD OTHERWISE GO UNSEEN: an exception no code awaits is written to the
// console with its stack, so that "Something went wrong" always says what did.
AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine("UNHANDLED: " + e.ExceptionObject);
TaskScheduler.UnobservedTaskException += (_, e) => { Console.Error.WriteLine("UNOBSERVED: " + e.Exception); e.SetObserved(); };

await builder.Build().RunAsync();
