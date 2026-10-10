using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using AIF.Controller;

using Mpai.Aif.Api;
using Mpai.Aif.PortData;

namespace Mpai.Mas.Client;

// THIS CLIENT, AS THE SERVICE COUNTS IT. A random identifier, made when the program
// starts and sent with every request, so that a Service can tell how many people
// are using it. It says nothing about the person.
public static class MasClientIdentity
{
    public static string Id { get; } = Guid.NewGuid().ToString("N");
}

// The Controller API over MPAI-MAS.
//
// SAME THREE METHODS, ACROSS A NETWORK. The User Agent holds an IControllerApi and
// cannot tell which implementation it has. Everything it sends is typed data, so
// there is nothing else to carry.
//
// SYNCHRONOUS ON PURPOSE. IControllerApi is synchronous because ControllerApi is, and
// every call site in the UA already goes through Task.Run - so the blocking
// happens on a pool thread with no synchronisation context and cannot deadlock.
// The exception is a UA closing down, which calls StopFlow on its own thread;
// the timeout below keeps that from hanging a window.
public sealed class RemoteControllerApi : IControllerApi, IDisposable
{
    private readonly HttpClient http;
    private readonly PortDataCodecs codecs;

    private string prefix = "/MPAI/AIFU";
    private string? controllerId;

    // Module name -> Module Instance id, for the Modules started so far.
    private readonly Dictionary<string, string> modules =
        new(StringComparer.Ordinal);

    public RemoteControllerApi(
        string baseUrl,
        string? bearerToken = null,
        TimeSpan? timeout = null)
    {
        http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/"),

            // Deliberate, not inherited. The first run loads models and BLIP
            // takes seconds; the default 100 is a number nobody chose.
            Timeout = timeout ?? TimeSpan.FromSeconds(180)
        };

        codecs = PortDataCodecs.Default();
        http.DefaultRequestHeaders.Add("MPAI-Client", MasClientIdentity.Id);

        if (!string.IsNullOrWhiteSpace(bearerToken))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    // The SCI, created once and reused. The specification allows the creation
    // response to return an alternative route prefix which MUST then replace
    // /MPAI/AIFU for this SCI - so it is honoured even though our own server
    // never returns one.
    private string Controller()
    {
        if (controllerId is not null) return controllerId;

        var response = http.PostAsync($"{prefix.TrimStart('/')}/Controller", null)
                           .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var json = JsonNode.Parse(body) as JsonObject
                   ?? throw new FormatException("Controller response is not JSON.");

        var alternative = (string?)json["prefix"];
        if (!string.IsNullOrWhiteSpace(alternative))
            prefix = alternative!.TrimEnd('/');

        controllerId = (string?)json["id"]
                       ?? throw new FormatException("Controller response has no id.");

        return controllerId;
    }

    private string Root => $"{prefix.TrimStart('/')}/{Controller()}/MODULE";

    public AifError StartFlow(
        string moduleName)
    {
        if (modules.ContainsKey(moduleName)) return AifError.OK;

        var body = new StringContent(
            new JsonObject { ["module"] = moduleName }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        var response = http.PostAsync($"{Root}/Start", body).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) return AifError.Failed;

        var json = JsonNode.Parse(
            response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JsonObject;

        var id = (string?)json?["id"];
        if (string.IsNullOrEmpty(id)) return AifError.Failed;

        modules[moduleName] = id!;
        return AifError.OK;
    }

    public void StopFlow(
        string moduleName)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return;

        try
        {
            http.GetAsync($"{Root}/{mid}/Stop").GetAwaiter().GetResult();
        }
        catch
        {
            // A UA shutting down should not be held up by a server that has
            // already gone.
        }

        modules.Remove(moduleName);
    }

    public ControllerApi.Result Advance(
        string moduleName,
        IEnumerable<ControllerApi.Datum> inputs)
    {
        if (!modules.ContainsKey(moduleName))
        {
            var started = StartFlow(moduleName);
            if (started != AifError.OK)
                return new ControllerApi.Result(started, Array.Empty<ControllerApi.Datum>());
        }

        var mid = modules[moduleName];

        // POST every input Port, then read the outputs. The run happens on the
        // first Output request: the specification has no "run now" call, so
        // something has to be the trigger.
        foreach (var datum in inputs)
        {
            if (!codecsKnow(datum.DataType))
                return new ControllerApi.Result(AifError.Failed, Array.Empty<ControllerApi.Datum>());

            var wire = Codecs.ToWire(datum.DataType, datum.Json);

            var content = new ByteArrayContent(wire);
            content.Headers.ContentType = new MediaTypeHeaderValue("MPAI/port-data");

            var posted = http.PostAsync(
                $"{Root}/{mid}/Input/{Segment(datum.DataType, datum.PortNumber)}",
                content).GetAwaiter().GetResult();

            if (!posted.IsSuccessStatusCode)
                return new ControllerApi.Result(AifError.Failed, Array.Empty<ControllerApi.Datum>());
        }

        // WHICH OUTPUTS TO ASK FOR. MAS delivers one Port at a time, so the RCA
        // asks for the Data Types it can read - every type it has a codec for.
        // A Port that produced nothing answers 404, which is not an error here:
        // it simply did not fire this run.
        var outputs = new List<ControllerApi.Datum>();

        foreach (var dataType in Codecs.KnownDataTypes)
        {
            // AND AT WHICH PORT NUMBER. A Module may declare two outputs of one
            // Data Type - CAV-HCI emits OSD-BTO at #1 and #2, the machine's
            // response and the user's recognised text - and asking only at #1
            // would deliver the first and lose the second, silently. That is the
            // defect this client was written to avoid on the input side, and had
            // on the output side.
            //
            // MAS offers no way to ask which Ports a Module declares, so the
            // client probes: #1, then upwards while each answers. A Port that
            // produced nothing this run answers 404, which is not an error - it
            // simply did not fire - and ends the probe for that type.
            // A 404 MEANS THAT PORT DID NOT FIRE, NOT THAT THERE ARE NO MORE.
            // Stopping at the first one lost every later Port whenever an earlier
            // one produced nothing: a reply recognised at Port 2 was never asked
            // for because the answer at Port 1 was absent, and the App saw silence.
            for (int portNumber = 1; portNumber <= 4; portNumber++)
            {
                var response = http.GetAsync(
                    $"{Root}/{mid}/Output/{Segment(dataType, portNumber)}")
                    .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode) continue;

                var wire = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                outputs.Add(new ControllerApi.Datum(
                    dataType, portNumber, Codecs.ToInternal(dataType, wire)));
            }
        }

        return new ControllerApi.Result(AifError.OK, outputs);
    }

    // THE DATA PATH, OVER THE ROUTES MPAI-MAS HAS. A write is one POST, a read one
    // GET; the first GET after one or more POSTs runs the Module on the Service.
    // MPAI-MAS answers a read of a Port that produced nothing, one that does not
    // exist and one whose AIM failed alike (404), so over it they are all
    // NotProduced, until MPAI-MAS carries the outcomes (M3206 Phase 15). A
    // TIMEOUT abandons the request; the run it started continues on the Service.
    public AifError InputWrite(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return AifError.NotStarted;
        if (!codecsKnow(dataType)) return AifError.Failed;

        var content = new ByteArrayContent(Codecs.ToWire(dataType, json));
        content.Headers.ContentType = new MediaTypeHeaderValue("MPAI/port-data");
        try
        {
            using var limit = Limit(timeoutMs);
            var posted = http.PostAsync($"{Root}/{mid}/Input/{Segment(dataType, portNumber)}", content, limit.Token)
                             .GetAwaiter().GetResult();
            return posted.IsSuccessStatusCode ? AifError.OK : AifError.Failed;
        }
        catch (OperationCanceledException) { return AifError.Timeout; }
    }

    public ControllerApi.Read OutputRead(string moduleName, string dataType, int portNumber, int timeoutMs = -1)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return new ControllerApi.Read(AifError.NotStarted, null);
        if (!codecsKnow(dataType)) return new ControllerApi.Read(AifError.Failed, null);
        try
        {
            using var limit = Limit(timeoutMs);
            var response = http.GetAsync($"{Root}/{mid}/Output/{Segment(dataType, portNumber)}", limit.Token)
                               .GetAwaiter().GetResult();
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new ControllerApi.Read(AifError.NotProduced, null);
            if (!response.IsSuccessStatusCode) return new ControllerApi.Read(AifError.Failed, null);
            var wire = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new ControllerApi.Read(AifError.OK, Codecs.ToInternal(dataType, wire));
        }
        catch (OperationCanceledException) { return new ControllerApi.Read(AifError.Timeout, null); }
    }

    // MPAI-MAS's Pause and Resume change a label on the Service and hold nothing,
    // so the client refuses them rather than pretend, until MPAI-MAS is aligned
    // to AIF V3.0 (M3206 Phase 15).
    public AifError Pause(string moduleName) => AifError.Failed;

    public AifError Resume(string moduleName) => AifError.Failed;

    // Status and StopAim reach a remote User Agent when MPAI-MAS carries them
    // (Phase 15); until then they are refused.
    public ControllerApi.ModuleStatus Status(string moduleName) =>
        new(AifError.Failed, Array.Empty<AimReport>());

    public AifError StopAim(string moduleName, string aimName) => AifError.Failed;

    // Where a Service holds a Module's storage is the Service's: refused.
    public AifError SharedStorageInit(string moduleName, string location) => AifError.Failed;

    // 0 - do not wait - cannot be offered over a network; it is the shortest wait.
    private static CancellationTokenSource Limit(int timeoutMs) =>
        timeoutMs < 0 ? new CancellationTokenSource() : new CancellationTokenSource(Math.Max(timeoutMs, 1));

    private static string Segment(string dataType, int portNumber) =>
        portNumber == 1 ? dataType : dataType + ":" + portNumber;

    private PortDataCodecs Codecs => codecs;

    private bool codecsKnow(string dataType) => codecs.Knows(dataType);

    public void Dispose() => http.Dispose();
}