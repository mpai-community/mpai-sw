using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using AIF.GlobalStorage;
using Mpai.Core.OSD;   // FaceDescriptorsObject, SpeechDescriptorsObject

namespace Mpai.Gallery;

// The subject gallery, as a Repository over AIF Global Storage (M3124). Every
// stored value is a serialized standard MPAI data type - a Face Descriptors
// Object (PAF-FDO) or a Speech Descriptors Object (MMC-SDO) - so any AIM can read
// an enrolled subject's descriptors without knowing this repository's internals.
//
// Keys follow the typed-instance convention of M3124 Section 4 (type encoded in
// the key, enumerated with List by prefix):
//     SubjectFaceDescriptors:<id>    -> serialized FaceDescriptorsObject
//     SubjectSpeechDescriptors:<id>  -> serialized SpeechDescriptorsObject
//
// Provenance (who enrolled, when) is framework-stamped by Global Storage and read
// back via GetKeyInfo - no field here for a caller to forge.
public sealed class SubjectRepository
{
    public const string FaceKeyPrefix   = "SubjectFaceDescriptors:";
    public const string SpeechKeyPrefix = "SubjectSpeechDescriptors:";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IGlobalStorage _storage;

    public SubjectRepository(IGlobalStorage storage) => _storage = storage;

    // ---- enrolment (Put) --------------------------------------------------

    public void EnrolFace(string subjectId, FaceDescriptorsObject face)
        => _storage.Put(FaceKeyPrefix + subjectId, Serialize(face));

    public void EnrolSpeech(string subjectId, SpeechDescriptorsObject speech)
        => _storage.Put(SpeechKeyPrefix + subjectId, Serialize(speech));

    // ---- retrieval (Get) --------------------------------------------------

    public FaceDescriptorsObject? GetFace(string subjectId)
        => TryGet(FaceKeyPrefix + subjectId, out var bytes)
            ? Deserialize<FaceDescriptorsObject>(bytes) : null;

    public SpeechDescriptorsObject? GetSpeech(string subjectId)
        => TryGet(SpeechKeyPrefix + subjectId, out var bytes)
            ? Deserialize<SpeechDescriptorsObject>(bytes) : null;

    // ---- enumeration (List) ----------------------------------------------

    // The ids of every subject with an enrolled face descriptor.
    public IReadOnlyList<string> FaceSubjectIds()
        => _storage.List(FaceKeyPrefix).Select(k => k[FaceKeyPrefix.Length..]).ToList();

    public IReadOnlyList<string> SpeechSubjectIds()
        => _storage.List(SpeechKeyPrefix).Select(k => k[SpeechKeyPrefix.Length..]).ToList();

    // ---- matching ---------------------------------------------------------
    // The repository serves descriptors; a caller compares. These helpers do the
    // common cosine match against every enrolled subject and return the best over
    // threshold, so recognition AIMs need not re-implement enumeration.

    public (string SubjectId, float Similarity)? MatchFace(float[] probe, float threshold)
        => BestMatch(FaceSubjectIds(), id => GetFace(id)?.Embedding(), probe, threshold);

    public (string SubjectId, float Similarity)? MatchSpeech(float[] probe, float threshold)
        => BestMatch(SpeechSubjectIds(), id => GetSpeech(id)?.Embedding(), probe, threshold);

    private static (string, float)? BestMatch(
        IReadOnlyList<string> ids, Func<string, float[]?> load, float[] probe, float threshold)
    {
        string? bestId = null; float best = float.NegativeInfinity;
        foreach (var id in ids)
        {
            var emb = load(id);
            if (emb is null) continue;
            var sim = Cosine(probe, emb);
            if (sim > best) { best = sim; bestId = id; }
        }
        return (bestId is not null && best >= threshold) ? (bestId, best) : null;
    }

    // ---- provenance -------------------------------------------------------

    public KeyInfo FaceProvenance(string subjectId)   => _storage.GetKeyInfo(FaceKeyPrefix + subjectId);
    public KeyInfo SpeechProvenance(string subjectId) => _storage.GetKeyInfo(SpeechKeyPrefix + subjectId);

    // ---- internals --------------------------------------------------------

    private bool TryGet(string key, out byte[] bytes)
    {
        if (_storage.Exists(key)) { bytes = _storage.Get(key); return true; }
        bytes = Array.Empty<byte>(); return false;
    }

    private static byte[] Serialize<T>(T value)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json));

    private static T Deserialize<T>(byte[] bytes)
        => JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(bytes))!;

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1f;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return -1f;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
