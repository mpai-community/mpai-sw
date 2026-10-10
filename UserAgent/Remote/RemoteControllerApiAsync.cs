using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aif.PortData;

namespace Mpai.Mas.Client;

// MPAI-MAS FROM A BROWSER. The same requests as RemoteControllerApi,
// awaited instead of blocked on. The HttpClient is the page's own: its base
// address is the origin that served the client, which forwards /MPAI/AIFU to
// the Service, so the browser never makes a cross-origin call.
public sealed class RemoteControllerApiAsync : IAsyncControllerApi
{
    private readonly HttpClient     http;
    private readonly PortDataCodecs codecs = PortDataCodecs.Default();
    private string  prefix = "/MPAI/AIFU";
    private string? controllerId;
    private readonly Dictionary<string, string> modules = new(StringComparer.Ordinal);

    public RemoteControllerApiAsync(HttpClient http, string? bearerToken = null)
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
    public Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs) =>
        AdvanceAsync(moduleName, inputs, Array.Empty<(string, int)>());

    // ONLY WHAT IS WANTED, when the caller says: asking for every Data Type the
    // client knows on four Ports each is thirty requests a step, nearly all of them
    // "no such output". The Service discards a run's outputs at the next input, so
    // one not asked for is never served to a later step. Nothing said, all asked.
    public async Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs,
                                                         IReadOnlyCollection<(string DataType, int PortNumber)> wanted)
    {
        if (!modules.ContainsKey(moduleName))
        {
            var started = await StartFlowAsync(moduleName);
            if (started != AifError.OK)
                return new ControllerApi.Result(started, Array.Empty<ControllerApi.Datum>());
        }
        var mid  = modules[moduleName];
        var root = await RootAsync();

        foreach (var datum in inputs)
        {
            if (!codecs.Knows(datum.DataType))
                return new ControllerApi.Result(AifError.Failed, Array.Empty<ControllerApi.Datum>());
            var content = new ByteArrayContent(codecs.ToWire(datum.DataType, datum.Json));
            content.Headers.ContentType = new MediaTypeHeaderValue("MPAI/port-data");
            var posted = await http.PostAsync($"{root}/{mid}/Input/{Segment(datum.DataType, datum.PortNumber)}", content);
            if (!posted.IsSuccessStatusCode)
                return new ControllerApi.Result(AifError.Failed, Array.Empty<ControllerApi.Datum>());
        }

        var outputs = new List<ControllerApi.Datum>();
        var asked = wanted.Count > 0
            ? wanted.Where(w => codecs.Knows(w.DataType)).Distinct().ToList()
            : codecs.KnownDataTypes.SelectMany(t => Enumerable.Range(1, 4).Select(n => (t, n))).ToList();
        foreach (var (dataType, portNumber) in asked)
        {
            {
                var response = await http.GetAsync($"{root}/{mid}/Output/{Segment(dataType, portNumber)}");
                if (!response.IsSuccessStatusCode) continue;
                var wire = await response.Content.ReadAsByteArrayAsync();
                outputs.Add(new ControllerApi.Datum(dataType, portNumber, codecs.ToInternal(dataType, wire)));
            }
        }
        return new ControllerApi.Result(AifError.OK, outputs);
    }

    // The data path over MPAI-MAS's routes: see RemoteControllerApi.
    public async Task<AifError> InputWriteAsync(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return AifError.NotStarted;
        if (!codecs.Knows(dataType)) return AifError.Failed;
        var content = new ByteArrayContent(codecs.ToWire(dataType, json));
        content.Headers.ContentType = new MediaTypeHeaderValue("MPAI/port-data");
        try
        {
            using var limit = Limit(timeoutMs);
            var posted = await http.PostAsync($"{await RootAsync()}/{mid}/Input/{Segment(dataType, portNumber)}", content, limit.Token);
            return posted.IsSuccessStatusCode ? AifError.OK : AifError.Failed;
        }
        catch (OperationCanceledException) { return AifError.Timeout; }
    }

    public async Task<ControllerApi.Read> OutputReadAsync(string moduleName, string dataType, int portNumber, int timeoutMs = -1)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return new ControllerApi.Read(AifError.NotStarted, null);
        if (!codecs.Knows(dataType)) return new ControllerApi.Read(AifError.Failed, null);
        try
        {
            using var limit = Limit(timeoutMs);
            var response = await http.GetAsync($"{await RootAsync()}/{mid}/Output/{Segment(dataType, portNumber)}", limit.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new ControllerApi.Read(AifError.NotProduced, null);
            if (!response.IsSuccessStatusCode) return new ControllerApi.Read(AifError.Failed, null);
            return new ControllerApi.Read(AifError.OK, codecs.ToInternal(dataType, await response.Content.ReadAsByteArrayAsync()));
        }
        catch (OperationCanceledException) { return new ControllerApi.Read(AifError.Timeout, null); }
    }

    // Refused over MPAI-MAS until Phase 15: see RemoteControllerApi.
    public Task<AifError> PauseAsync(string moduleName) => Task.FromResult(AifError.Failed);

    public Task<AifError> ResumeAsync(string moduleName) => Task.FromResult(AifError.Failed);

    public Task<ControllerApi.ModuleStatus> StatusAsync(string moduleName) =>
        Task.FromResult(new ControllerApi.ModuleStatus(AifError.Failed, Array.Empty<AimReport>()));

    public Task<AifError> StopAimAsync(string moduleName, string aimName) => Task.FromResult(AifError.Failed);

    public Task<AifError> SharedStorageInitAsync(string moduleName, string location) => Task.FromResult(AifError.Failed);

    private static System.Threading.CancellationTokenSource Limit(int timeoutMs) =>
        timeoutMs < 0 ? new System.Threading.CancellationTokenSource() : new System.Threading.CancellationTokenSource(Math.Max(timeoutMs, 1));

    private static string Segment(string dataType, int portNumber) =>
        portNumber == 1 ? dataType : dataType + ":" + portNumber;
}
