using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Hci.Api;
using Mpai.Mas.PortData;

namespace Mpai.Mas.Client;

// The North API over MPAI-MAS.
//
// SAME THREE METHODS, ACROSS A NETWORK. The User Agent holds an INorthApi and
// cannot tell which implementation it has. Everything it sends is typed data, so
// there is nothing else to carry.
//
// SYNCHRONOUS ON PURPOSE. INorthApi is synchronous because NorthApi is, and
// every call site in the UA already goes through Task.Run - so the blocking
// happens on a pool thread with no synchronisation context and cannot deadlock.
// The exception is a UA closing down, which calls StopFlow on its own thread;
// the timeout below keeps that from hanging a window.
public sealed class RemoteNorthApi : INorthApi, IDisposable
{
    private readonly HttpClient http;
    private readonly PortDataCodecs codecs;

    private string prefix = "/MPAI/AIFU";
    private string? controllerId;

    // Module name -> Module Instance id, for the Modules started so far.
    private readonly Dictionary<string, string> modules =
        new(StringComparer.Ordinal);

    public RemoteNorthApi(
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

    public NorthApi.Result Advance(
        string moduleName,
        IEnumerable<NorthApi.Datum> inputs)
    {
        if (!modules.ContainsKey(moduleName))
        {
            var started = StartFlow(moduleName);
            if (started != AifError.OK)
                return new NorthApi.Result(started, Array.Empty<NorthApi.Datum>(), false);
        }

        var mid = modules[moduleName];

        // POST every input Port, then read the outputs. The run happens on the
        // first Output request: the specification has no "run now" call, so
        // something has to be the trigger.
        foreach (var datum in inputs)
        {
            if (!codecsKnow(datum.DataType))
                return new NorthApi.Result(AifError.Failed, Array.Empty<NorthApi.Datum>(), false);

            var wire = Codecs.ToWire(datum.DataType, datum.Json);

            var content = new ByteArrayContent(wire);
            content.Headers.ContentType = new MediaTypeHeaderValue("MPAI/port-data");

            var posted = http.PostAsync(
                $"{Root}/{mid}/Input/{Segment(datum.DataType, datum.PortNumber)}",
                content).GetAwaiter().GetResult();

            if (!posted.IsSuccessStatusCode)
                return new NorthApi.Result(AifError.Failed, Array.Empty<NorthApi.Datum>(), false);
        }

        // WHICH OUTPUTS TO ASK FOR. MAS delivers one Port at a time, so the RCA
        // asks for the Data Types it can read - every type it has a codec for.
        // A Port that produced nothing answers 404, which is not an error here:
        // it simply did not fire this run.
        var outputs = new List<NorthApi.Datum>();

        foreach (var dataType in Codecs.KnownDataTypes)
        {
            // AND AT WHICH PORT NUMBER. A Module may declare two outputs of one
            // Data Type - MMC-HCI emits OSD-BTO at #1 and #2, the machine's
            // response and the user's recognised text - and asking only at #1
            // would deliver the first and lose the second, silently. That is the
            // defect this client was written to avoid on the input side, and had
            // on the output side.
            //
            // MAS offers no way to ask which Ports a Module declares, so the
            // client probes: #1, then upwards while each answers. A Port that
            // produced nothing this run answers 404, which is not an error - it
            // simply did not fire - and ends the probe for that type.
            for (int portNumber = 1; ; portNumber++)
            {
                var response = http.GetAsync(
                    $"{Root}/{mid}/Output/{Segment(dataType, portNumber)}")
                    .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode) break;

                var wire = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                outputs.Add(new NorthApi.Datum(
                    dataType, portNumber, Codecs.ToInternal(dataType, wire)));
            }
        }

        return new NorthApi.Result(AifError.OK, outputs, false);
    }

    private static string Segment(string dataType, int portNumber) =>
        portNumber == 1 ? dataType : dataType + ":" + portNumber;

    private PortDataCodecs Codecs => codecs;

    private bool codecsKnow(string dataType) => codecs.Knows(dataType);

    public void Dispose() => http.Dispose();
}