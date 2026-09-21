using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Hci.Api;
using Mpai.Mas.PortData;

namespace Mpai.RcaWeb.Mas;

// THE NORTH API, WITHOUT WAITING. A browser runs WebAssembly on one thread and
// never lets it block on the network, so the seam the interpreter calls is
// asynchronous here. The desktop's INorthApi is untouched; this is its twin for
// the browser.
public interface IAsyncNorthApi
{
    Task<AifError>        StartFlowAsync(string moduleName);
    Task<NorthApi.Result> AdvanceAsync(string moduleName, IEnumerable<NorthApi.Datum> inputs);
    Task                  StopFlowAsync(string moduleName);
}

// MPAI-MAS FROM A BROWSER. The same requests as Mpai.Mas.Client.RemoteNorthApi,
// awaited instead of blocked on. The HttpClient is the page's own: its base
// address is the origin that served the client, which forwards /MPAI/AIFU to
// the Service, so the browser never makes a cross-origin call.
public sealed class RemoteNorthApiAsync : IAsyncNorthApi
{
    private readonly HttpClient     http;
    private readonly PortDataCodecs codecs = PortDataCodecs.Default();
    private string  prefix = "/MPAI/AIFU";
    private string? controllerId;
    private readonly Dictionary<string, string> modules = new(StringComparer.Ordinal);

    public RemoteNorthApiAsync(HttpClient http, string? bearerToken = null)
    {
        this.http = http;
        if (!string.IsNullOrWhiteSpace(bearerToken))
            this.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    private async Task<string> ControllerAsync()
    {
        if (controllerId is not null) return controllerId;
        var response = await http.PostAsync($"{prefix.TrimStart('/')}/Controller", null);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject
                   ?? throw new FormatException("Controller response is not JSON.");
        var alternative = (string?)json["prefix"];
        if (!string.IsNullOrWhiteSpace(alternative)) prefix = alternative!.TrimEnd('/');
        controllerId = (string?)json["id"] ?? throw new FormatException("Controller response has no id.");
        return controllerId;
    }

    private async Task<string> RootAsync() => $"{prefix.TrimStart('/')}/{await ControllerAsync()}/MODULE";

    public async Task<AifError> StartFlowAsync(string moduleName)
    {
        if (modules.ContainsKey(moduleName)) return AifError.OK;
        var body = new StringContent(new JsonObject { ["module"] = moduleName }.ToJsonString(),
                                     Encoding.UTF8, "application/json");
        var response = await http.PostAsync($"{await RootAsync()}/Start", body);
        if (!response.IsSuccessStatusCode) return AifError.Failed;
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
        var id = (string?)json?["id"];
        if (string.IsNullOrEmpty(id)) return AifError.Failed;
        modules[moduleName] = id!;
        return AifError.OK;
    }

    public async Task StopFlowAsync(string moduleName)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return;
        try { await http.GetAsync($"{await RootAsync()}/{mid}/Stop"); }
        catch { /* a Module already gone is stopped */ }
        modules.Remove(moduleName);
    }

    // ONE EXCHANGE. The inputs are written, then every Output Port the client can
    // read is asked for; the first read closes the exchange on the Service and
    // runs the Module on what was written.
    public async Task<NorthApi.Result> AdvanceAsync(string moduleName, IEnumerable<NorthApi.Datum> inputs)
    {
        if (!modules.ContainsKey(moduleName))
        {
            var started = await StartFlowAsync(moduleName);
            if (started != AifError.OK)
                return new NorthApi.Result(started, Array.Empty<NorthApi.Datum>(), false);
        }
        var mid  = modules[moduleName];
        var root = await RootAsync();

        foreach (var datum in inputs)
        {
            if (!codecs.Knows(datum.DataType))
                return new NorthApi.Result(AifError.Failed, Array.Empty<NorthApi.Datum>(), false);
            var content = new ByteArrayContent(codecs.ToWire(datum.DataType, datum.Json));
            content.Headers.ContentType = new MediaTypeHeaderValue("MPAI/port-data");
            var posted = await http.PostAsync($"{root}/{mid}/Input/{Segment(datum.DataType, datum.PortNumber)}", content);
            if (!posted.IsSuccessStatusCode)
                return new NorthApi.Result(AifError.Failed, Array.Empty<NorthApi.Datum>(), false);
        }

        var outputs = new List<NorthApi.Datum>();
        foreach (var dataType in codecs.KnownDataTypes)
        {
            for (int portNumber = 1; portNumber <= 4; portNumber++)
            {
                var response = await http.GetAsync($"{root}/{mid}/Output/{Segment(dataType, portNumber)}");
                if (!response.IsSuccessStatusCode) continue;
                var wire = await response.Content.ReadAsByteArrayAsync();
                outputs.Add(new NorthApi.Datum(dataType, portNumber, codecs.ToInternal(dataType, wire)));
            }
        }
        return new NorthApi.Result(AifError.OK, outputs, false);
    }

    private static string Segment(string dataType, int portNumber) =>
        portNumber == 1 ? dataType : dataType + ":" + portNumber;
}
