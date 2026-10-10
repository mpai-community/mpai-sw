using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.SharedStorage;

namespace Mpai.Rca;

// A MESSAGE OF A RECORD, as the User Agent plays it: when it crossed the
// boundary, from the start of the record; where; the Object; and the payloads
// the Object refers to as record:payload/<n>.
public sealed record RecordedMessage(TimeSpan At, string DataType, int PortNumber, string Json,
                                     IReadOnlyDictionary<string, byte[]> Payloads);

// A SOURCE OF RECORDED DATA (M3219 3.5): a device of the Physical Layer, as a
// microphone is. Playing a record is the User Agent's act, not the Controller's.
public interface IRecordSource
{
    // The Messages written to the boundary Input Ports, in the order they
    // crossed it; a refusal where the User Agent is not a reader of the record.
    Task<IReadOnlyList<RecordedMessage>> InputsAsync();
}

// A record in the Private Storage of a Module, read under its rules: the record
// the Controller made (BoundaryRecord), the User Agent its reader.
public sealed class StoredRecord(IRuledStorage storage, string recordId) : IRecordSource
{
    public Task<IReadOnlyList<RecordedMessage>> InputsAsync()
    {
        var header = storage.MPAI_AIFM_RuledStorage_Get(recordId + "/header", out var h);
        if (header == StorageOutcome.NotAuthorised)
            throw new UnauthorizedAccessException($"The User Agent is not a reader of the record {recordId}.");

        var records = storage.MPAI_AIFM_RuledStorage_List(BoundaryRecord.Category, recordId + "/")
            .Where(k => !k.EndsWith("/header") && !k.Contains("/payload/"))
            .Select(k => storage.MPAI_AIFM_RuledStorage_Get(k, out var d) == StorageOutcome.OK ? JsonNode.Parse(d)!.AsObject() : null)
            .OfType<JsonObject>()
            .OrderBy(r => (long)r["Sequence"]!)
            .ToList();
        if (records.Count == 0 && header != StorageOutcome.OK)
            throw new InvalidOperationException($"There is no record {recordId}.");

        // PACED BY THE CONTROLLER'S STAMPS: the time of each Message from the start
        // of the record, as the header says it, or from its first Message.
        var start = header == StorageOutcome.OK && JsonNode.Parse(h)!["Started"] is { } s
            ? DateTimeOffset.Parse((string)s!)
            : records.Select(r => DateTimeOffset.Parse((string)r["Stamp"]!)).DefaultIfEmpty(DateTimeOffset.MinValue).Min();

        var inputs = new List<RecordedMessage>();
        foreach (var r in records.Where(r => (string)r["Direction"]! == "In"))
        {
            var json = (string)r["Json"]!;
            var payloads = new Dictionary<string, byte[]>();
            foreach (var reference in References(json))
                if (storage.MPAI_AIFM_RuledStorage_Get($"{recordId}/payload/{reference["record:payload/".Length..]}", out var bytes) == StorageOutcome.OK)
                    payloads[reference] = bytes;
            inputs.Add(new RecordedMessage(DateTimeOffset.Parse((string)r["Stamp"]!) - start,
                                           (string)r["DataType"]!, (int)r["PortNumber"]!, json, payloads));
        }
        return Task.FromResult<IReadOnlyList<RecordedMessage>>(inputs);
    }

    private static IEnumerable<string> References(string json) =>
        System.Text.RegularExpressions.Regex.Matches(json, "\"(record:payload/[0-9]+)\"").Select(m => m.Groups[1].Value).Distinct();
}
