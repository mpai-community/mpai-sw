using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;

using Mpai.Mas.PortData;

namespace Mpai.Mas.Server;

// The MPAI-MAS V1.0 API over a Module runner.
//
// KNOWS NO APPLICATION. No Module name, no AIM name, no Data Type literal
// appears below. The Module name arrives in a request body, the Data Types
// arrive in URL segments, and the codecs come from a registry. Adding an
// application means writing a host; adding a Data Type means writing a codec.
// Neither means editing this file.
//
// ROUTES, from the specification:
//   POST   /MPAI/AIFU/Controller                                -> 201 {id}
//   POST   /MPAI/AIFU/{cid}/MODULE/Start        {"module": "x"} -> 200 {state}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}                        -> 200 {state}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Pause                  -> 200 {state}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Resume                 -> 200 {state}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Stop                   -> 200
//   POST   /MPAI/AIFU/{cid}/MODULE/{mid}/Input/{pid}            -> 200
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Output/{pid}           -> 200 port-data
//   DELETE /MPAI/AIFU/Controller/{cid}                          -> 200
//
// MODULE, not AIW. The old server used /AIW/ throughout and was non-conformant
// on every route regardless of how its ports were keyed.
public sealed class MasServer
{
    private const string Prefix = "/MPAI/AIFU";

    // WHAT THIS SERVICE OFFERS, if anything. A Service with no catalogue serves
    // Modules to clients that already know which one they want; a Service with
    // one also tells a client what Apps it has, so a client holding no
    // application can be handed one.
    public AppCatalogue Catalogue { get; init; } = AppCatalogue.Scan(null);

    private readonly IModuleRunner runner;
    private readonly PortDataCodecs codecs;
    private readonly string listenUrl;
    private readonly string? bearerToken;

    // The certificate to present, or null to let Kestrel choose - which on a
    // loopback address means the development certificate, and anywhere else
    // means the caller should not have got this far. The server package neither
    // creates nor renews: it presents what the platform placed on disk.
    private readonly X509Certificate2? certificate;

    // The intermediates presented WITH the certificate, so a client can build a
    // path to a root it already trusts. Not a store of what this server trusts.
    private readonly X509Certificate2Collection? authority;

    // One SCI per Controller Instance created by the RCA.
    private readonly ConcurrentDictionary<string, Sci> instances = new();

    public MasServer(
        IModuleRunner runner,
        PortDataCodecs codecs,
        string listenUrl,
        string? bearerToken = null,
        X509Certificate2? certificate = null,
        X509Certificate2Collection? authority = null)
    {
        this.runner      = runner;
        this.codecs      = codecs;
        this.listenUrl   = listenUrl;
        this.bearerToken = bearerToken;
        this.certificate = certificate;
        this.authority   = authority;
    }

    private sealed class Sci
    {
        public string Id { get; init; } = string.Empty;

        // Module Instance id -> running Module.
        public ConcurrentDictionary<string, ModuleInstance> Modules { get; } = new();
    }

    private sealed class ModuleInstance
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string State { get; set; } = "ACTIVE";
        public DateTimeOffset Changed { get; set; } = DateTimeOffset.UtcNow;

        // Boundary inputs buffered until the run. Keyed "DataType#PortNumber".
        public Dictionary<string, string> Inputs { get; } = new();

        // Outputs of the last completed run, same keying. Null before a run.
        public Dictionary<string, string>? Outputs { get; set; }

        // One run at a time per Module. A run holds models and takes seconds;
        // two overlapping runs would interleave their boundaries.
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    public async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(listenUrl);
        builder.Logging.ClearProviders();

        // ONE LINE, AND IT IS THE WHOLE OF TLS. Kestrel negotiates from a
        // certificate held in memory, identically on Windows and Linux. This is
        // why the listener had to be Kestrel: HttpListener binds a certificate
        // to a port administratively on Windows and cannot serve HTTPS on Linux
        // at any price, so with it there was no OS-independent HTTPS layer to
        // write. Here the platform difference is reduced to a file path.
        if (certificate is not null)
            builder.WebHost.ConfigureKestrel(
                options => options.ConfigureHttpsDefaults(https =>
                {
                    https.ServerCertificate = certificate;

                    // A CLIENT THAT CANNOT BUILD THE CHAIN REFUSES BEFORE ASKING.
                    // It sends no request, so the server sees nothing and reports
                    // nothing - the failure is visible only at the client, as a
                    // trust error. Presenting the intermediates is what prevents it.
                    if (authority is not null && authority.Count > 0)
                        https.ServerCertificateChain = authority;
                }));

        var app = builder.Build();
        app.Run(HandleAsync);

        Console.WriteLine($"[MAS] Listening on {listenUrl}");
        Console.WriteLine(certificate is null
            ? "[MAS] No certificate supplied - Kestrel default."
            : $"[MAS] Certificate: {certificate.Subject} (expires {certificate.NotAfter:u})");
        Console.WriteLine($"[MAS] Data Types: {string.Join(", ", codecs.KnownDataTypes)}");
        Console.WriteLine(bearerToken is null
            ? "[MAS] No bearer token configured - every caller is accepted."
            : "[MAS] Bearer token required.");

        await app.RunAsync();
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        try
        {
            if (!Authorised(ctx))
            {
                await Write(ctx, 401, "text/plain", "Unauthorised.");
                return;
            }

            var path   = (ctx.Request.Path.Value ?? string.Empty).TrimEnd('/');
            var method = ctx.Request.Method;

            if (!path.StartsWith(Prefix, StringComparison.Ordinal))
            {
                await Write(ctx, 404, "text/plain", "Not an MPAI AIFU route.");
                return;
            }

            // Segments after the prefix.
            var rest = path.Substring(Prefix.Length).Trim('/');
            var segs = rest.Length == 0
                ? Array.Empty<string>()
                : rest.Split('/');

            // GET /Apps - the catalogue
            if (method == "GET" && segs.Length == 1 && segs[0] == "Apps")
            {
                await Write(ctx, 200, "application/json", Catalogue.ToJson());
                return;
            }

            // GET /Apps/{id} - the Workflow Description itself
            if (method == "GET" && segs.Length == 2 && segs[0] == "Apps")
            {
                var app = Catalogue.Find(segs[1]);
                if (app is null) { await Write(ctx, 404, "text/plain", "No such App."); return; }
                await Write(ctx, 200, "text/plain; charset=utf-8",
                            await System.IO.File.ReadAllTextAsync(app.WorkflowPath));
                return;
            }

            // GET /Apps/{id}/Icon
            if (method == "GET" && segs.Length == 3 && segs[0] == "Apps" && segs[2] == "Icon")
            {
                var app = Catalogue.Find(segs[1]);
                if (app?.IconFile is null) { await Write(ctx, 404, "text/plain", "No icon."); return; }
                var bytes = await System.IO.File.ReadAllBytesAsync(
                    System.IO.Path.Combine(app.Folder, app.IconFile));
                ctx.Response.StatusCode  = 200;
                ctx.Response.ContentType = IconType(app.IconFile);
                await ctx.Response.Body.WriteAsync(bytes);
                return;
            }

            // POST /Controller
            if (method == "POST" && segs.Length == 1 && segs[0] == "Controller")
            {
                await CreateController(ctx);
                return;
            }

            // DELETE /Controller/{cid}
            if (method == "DELETE" && segs.Length == 2 && segs[0] == "Controller")
            {
                await DeleteController(ctx, segs[1]);
                return;
            }

            // Everything else is {cid}/MODULE/...
            if (segs.Length < 2 || segs[1] != "MODULE")
            {
                await Write(ctx, 404, "text/plain", "Unknown route.");
                return;
            }

            if (!instances.TryGetValue(segs[0], out var sci))
            {
                await Write(ctx, 404, "text/plain", "No such Controller Instance.");
                return;
            }

            // POST {cid}/MODULE/Start
            if (method == "POST" && segs.Length == 3 && segs[2] == "Start")
            {
                await StartModule(ctx, sci);
                return;
            }

            if (segs.Length < 3)
            {
                await Write(ctx, 404, "text/plain", "Unknown route.");
                return;
            }

            if (!sci.Modules.TryGetValue(segs[2], out var module))
            {
                await Write(ctx, 404, "text/plain", "No such Module Instance.");
                return;
            }

            // GET {cid}/MODULE/{mid}
            if (method == "GET" && segs.Length == 3)
            {
                await WriteState(ctx, sci, module);
                return;
            }

            // GET {cid}/MODULE/{mid}/{Pause|Resume|Stop}
            if (method == "GET" && segs.Length == 4)
            {
                switch (segs[3])
                {
                    case "Pause":
                        module.State   = "PAUSED";
                        module.Changed = DateTimeOffset.UtcNow;
                        await WriteState(ctx, sci, module);
                        return;

                    case "Resume":
                        module.State   = "ACTIVE";
                        module.Changed = DateTimeOffset.UtcNow;
                        await WriteState(ctx, sci, module);
                        return;

                    case "Stop":
                        runner.Stop(module.Name);
                        sci.Modules.TryRemove(module.Id, out _);
                        await Write(ctx, 200, "text/plain", "OK");
                        return;
                }
            }

            // POST {cid}/MODULE/{mid}/Input/{pid}
            if (method == "POST" && segs.Length == 5 && segs[3] == "Input")
            {
                await ReceiveInput(ctx, module, segs[4]);
                return;
            }

            // GET {cid}/MODULE/{mid}/Output/{pid}
            if (method == "GET" && segs.Length == 5 && segs[3] == "Output")
            {
                await SendOutput(ctx, module, segs[4]);
                return;
            }

            await Write(ctx, 404, "text/plain", "Unknown route.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MAS] EXCEPTION: {ex}");
            await Write(ctx, 500, "text/plain", ex.Message);
        }
    }

    // The SLA chooses among BASIC, DIGEST and BEARER; this implements BEARER,
    // and no token configured means no check - which is correct on a loopback
    // demonstration and wrong the moment the port is reachable.
    private bool Authorised(HttpContext ctx)
    {
        if (bearerToken is null) return true;

        var header = ctx.Request.Headers["Authorization"].ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) &&
               string.Equals(header.Substring(7).Trim(), bearerToken, StringComparison.Ordinal);
    }

    private async Task CreateController(HttpContext ctx)
    {
        var id = Guid.NewGuid().ToString();
        instances[id] = new Sci { Id = id };

        Console.WriteLine($"[MAS] Controller Instance {id}");

        // The optional "prefix" field is not returned: this server serves the
        // standard /MPAI/AIFU prefix, and an RCA must cope with either.
        await Write(ctx, 201, "application/json",
            new JsonObject { ["id"] = id }.ToJsonString());
    }

    private async Task DeleteController(HttpContext ctx, string cid)
    {
        if (instances.TryRemove(cid, out var sci))
            foreach (var module in sci.Modules.Values)
                runner.Stop(module.Name);

        await Write(ctx, 200, "text/plain", "OK");
    }

    private async Task StartModule(HttpContext ctx, Sci sci)
    {
        var body = await ReadBody(ctx);
        var name = (JsonNode.Parse(body) as JsonObject)?["module"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            await Write(ctx, 400, "text/plain", "Body must be {\"module\": \"...\"}.");
            return;
        }

        var failure = runner.Start(name!);
        if (failure is not null)
        {
            await Write(ctx, 500, "text/plain", failure);
            return;
        }

        var module = new ModuleInstance { Id = Guid.NewGuid().ToString(), Name = name! };
        sci.Modules[module.Id] = module;

        Console.WriteLine($"[MAS] Module {name} started as {module.Id}");

        await WriteState(ctx, sci, module);
    }

    private async Task ReceiveInput(HttpContext ctx, ModuleInstance module, string pid)
    {
        if (!PortSegment.TryParse(pid, out var dataType, out var portNumber))
        {
            await Write(ctx, 400, "text/plain", $"Malformed port identifier '{pid}'.");
            return;
        }

        var port = PortSegment.Resolve(
            runner.PortsOf(module.Name), "Input", dataType, portNumber);

        if (port is null)
        {
            await Write(ctx, 404, "text/plain",
                $"No input Port {portNumber} accepting '{dataType}'.");
            return;
        }

        if (!codecs.Knows(dataType))
        {
            await Write(ctx, 415, "text/plain",
                $"No port-data codec for '{dataType}'.");
            return;
        }

        var wire = await ReadBodyBytes(ctx);

        // The RCA names the Data Type it is SENDING, which may be any type the
        // Port accepts. The boundary key comes from the PORT, because that is
        // what the Controller routes on - the Port''s own DataType, the first of
        // its declared set. This is the one place the two conventions meet.
        module.Inputs[port.Key] = codecs.ToInternal(dataType, wire);

        // The first input after a completed run begins a new round. Over MAS the
        // inputs arrive one request at a time and nothing in the API says when a
        // round has ended; this is the only moment that can mean it.
        if (module.Outputs is not null)
        {
            var latest = module.Inputs[port.Key];
            module.Inputs.Clear();
            module.Inputs[port.Key] = latest;
            module.Outputs = null;
        }

        Console.WriteLine($"[MAS] input {port.Key} ({wire.Length:N0} bytes)");

        await Write(ctx, 200, "text/plain", "OK");
    }

    private async Task SendOutput(HttpContext ctx, ModuleInstance module, string pid)
    {
        if (!PortSegment.TryParse(pid, out var dataType, out var portNumber))
        {
            await Write(ctx, 400, "text/plain", $"Malformed port identifier '{pid}'.");
            return;
        }

        var port = PortSegment.Resolve(
            runner.PortsOf(module.Name), "Output", dataType, portNumber);

        if (port is null)
        {
            await Write(ctx, 404, "text/plain",
                $"No output Port {portNumber} carrying '{dataType}'.");
            return;
        }

        if (!codecs.Knows(dataType))
        {
            await Write(ctx, 415, "text/plain",
                $"No port-data codec for '{dataType}'.");
            return;
        }

        // The run happens on the first Output request after inputs were written.
        // Reading a second output port then serves the same run rather than
        // running again.
        await module.Gate.WaitAsync();
        try
        {
            if (module.Outputs is null)
            {
                var result = runner.Run(module.Name, module.Inputs);

                if (result.Error is not null)
                {
                    await Write(ctx, 500, "text/plain", result.Error);
                    return;
                }

                if (result.Suspended)
                {
                    // The Module wants boundary input it has not been given.
                    await Write(ctx, 404, "text/plain",
                        "The Module is waiting for further input.");
                    return;
                }

                module.Outputs = new Dictionary<string, string>(result.Outputs);
            }
        }
        finally
        {
            module.Gate.Release();
        }

        if (!module.Outputs.TryGetValue(port.Key, out var internalJson))
        {
            await Write(ctx, 404, "text/plain", $"No output for Port {port.Key}.");
            return;
        }

        var wire = codecs.ToWire(dataType, internalJson);

        ctx.Response.StatusCode    = 200;
        ctx.Response.ContentType   = "MPAI/port-data";
        ctx.Response.ContentLength = wire.Length;
        await ctx.Response.Body.WriteAsync(wire);
    }

    private async Task WriteState(HttpContext ctx, Sci sci, ModuleInstance module)
    {
        var state = new JsonObject
        {
            ["controller"] = sci.Id,
            ["id"]         = module.Id,
            ["name"]       = module.Name,
            ["state"]      = module.State,
            ["change"]     = module.Changed.ToString("o")
        };

        await Write(ctx, 200, "application/json", state.ToJsonString());
    }

    private static async Task<string> ReadBody(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<byte[]> ReadBodyBytes(HttpContext ctx)
    {
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static string IconType(string file) =>
        System.IO.Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".png"  => "image/png",
            ".jpg"  => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".svg"  => "image/svg+xml",
            _       => "application/octet-stream"
        };

    private static async Task Write(
        HttpContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode    = status;
        ctx.Response.ContentType   = contentType;
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
    }
}