using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mpai.Core;

// ---------------------------------------------------------------------------
//  SubjectGallery - the single "common name DB" that FIR and SIR both search,
//  so their outputs live in ONE subject-ID space (the prerequisite for IDR's
//  cross-modal fusion). Model 1: this gallery IS the database for FIR, SIR AND
//  IDR - enrol a subject once and every consumer finds them.
//
//  Each SUBJECT holds up to one face template and one voice template under a
//  single SubjectId. Enrolling a person's face AND voice under the same id is
//  the single act that creates the label alignment across modalities.
//
//  MODEL-AGNOSTIC BY DESIGN: the gallery stores float[] embeddings and computes
//  cosine ITSELF (both FIR's ArcFace and SIR's ECAPA emit L2-normalised
//  embeddings, so cosine == dot product). It does NOT reference the embedders,
//  so it lives in Mpai.Core with no dependency on FIR/SIR - which is what breaks
//  the dependency cycle (FIR/SIR/IDR all reference Core; Core references none of
//  them). Callers embed (FIR/SIR own the models) and pass the float[] in.
//
//  Persistence: JSON on disk. Proper eventual home is AIF Shared Storage
//  (M3124, ISharedStorage) - flagged as the extension point.
// ---------------------------------------------------------------------------
public sealed class SubjectGallery
{
    private readonly Dictionary<string, Subject> _subjects = new();

    // Per-modality match thresholds (the values FIR/SIR used with their own DBs):
    // ArcFace face verification ~0.35, ECAPA speaker ~0.45.
    private readonly float _faceThreshold;
    private readonly float _voiceThreshold;

    public SubjectGallery(float faceThreshold = 0.35f, float voiceThreshold = 0.45f)
    {
        _faceThreshold = faceThreshold;
        _voiceThreshold = voiceThreshold;
    }

    public IReadOnlyCollection<string> SubjectIds => _subjects.Keys.ToList();
    public int Count => _subjects.Count;

    // ---- Enrolment (embedding-level; media-taking helper is in IDR) --------

    // Enrol / update a subject from already-computed embeddings. Either may be
    // null (partial enrolment: face-only or voice-only).
    public void EnrolEmbeddings(string subjectId, float[]? face = null, float[]? voice = null,
                                string? faceTime = null, string? speechTime = null)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("subjectId required", nameof(subjectId));
        if (!_subjects.TryGetValue(subjectId, out var s))
        {
            s = new Subject { SubjectId = subjectId };
            _subjects[subjectId] = s;
        }
        if (face is not null) s.FaceEmbedding = face;
        if (voice is not null) s.VoiceEmbedding = voice;
        if (faceTime is not null) s.FaceTime = faceTime;
        if (speechTime is not null) s.SpeechTime = speechTime;
    }

    public bool Remove(string subjectId) => _subjects.Remove(subjectId);
    public bool Contains(string subjectId) => _subjects.ContainsKey(subjectId);

    // ---- Top-1 identification (Model 1: the gallery IS FIR's and SIR's DB) --

    public GalleryMatch? IdentifyFace(float[] probeFace)
    {
        string? bestId = null; float bestSim = float.NegativeInfinity;
        foreach (var s in _subjects.Values)
            if (s.FaceEmbedding is not null)
            {
                float sim = Cosine(probeFace, s.FaceEmbedding);
                if (sim > bestSim) { bestSim = sim; bestId = s.SubjectId; }
            }
        if (bestId is null || bestSim < _faceThreshold) return null;
        return new GalleryMatch(bestId, bestSim);
    }

    public GalleryMatch? IdentifyVoice(float[] probeVoice)
    {
        string? bestId = null; float bestSim = float.NegativeInfinity;
        foreach (var s in _subjects.Values)
            if (s.VoiceEmbedding is not null)
            {
                float sim = Cosine(probeVoice, s.VoiceEmbedding);
                if (sim > bestSim) { bestSim = sim; bestId = s.SubjectId; }
            }
        if (bestId is null || bestSim < _voiceThreshold) return null;
        return new GalleryMatch(bestId, bestSim);
    }

    // ---- Scoring (full vectors over the common subject space, for IDR) ------

    public List<SubjectScore> ScoreFace(float[] probeFace)
    {
        var scores = new List<SubjectScore>();
        foreach (var s in _subjects.Values)
            if (s.FaceEmbedding is not null)
                scores.Add(new SubjectScore(s.SubjectId, Cosine(probeFace, s.FaceEmbedding)));
        return scores;
    }

    public List<SubjectScore> ScoreVoice(float[] probeVoice)
    {
        var scores = new List<SubjectScore>();
        foreach (var s in _subjects.Values)
            if (s.VoiceEmbedding is not null)
                scores.Add(new SubjectScore(s.SubjectId, Cosine(probeVoice, s.VoiceEmbedding)));
        return scores;
    }

    // Cosine of two vectors. FIR/SIR embeddings are L2-normalised, so this is the
    // dot product; the explicit norm keeps it correct for any input.
    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) { dot += a[i] * (double)b[i]; na += a[i] * (double)a[i]; nb += b[i] * (double)b[i]; }
        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom < 1e-12 ? 0f : (float)(dot / denom);
    }

    // ---- Persistence (JSON on disk; AIF Shared Storage is the eventual home) --

    public void Save(string path)
    {
        var dto = new GalleryDto
        {
            Subjects = _subjects.Values.Select(s => new SubjectDto
            {
                SubjectId = s.SubjectId, FaceEmbedding = s.FaceEmbedding, VoiceEmbedding = s.VoiceEmbedding, FaceTime = s.FaceTime, SpeechTime = s.SpeechTime
            }).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
    }

    public static SubjectGallery Load(string path, float faceThreshold = 0.35f, float voiceThreshold = 0.45f)
    {
        var g = new SubjectGallery(faceThreshold, voiceThreshold);
        if (!File.Exists(path)) return g;
        var dto = JsonSerializer.Deserialize<GalleryDto>(File.ReadAllText(path), JsonOpts);
        if (dto?.Subjects is null) return g;
        foreach (var s in dto.Subjects)
            g._subjects[s.SubjectId] = new Subject { SubjectId = s.SubjectId, FaceEmbedding = s.FaceEmbedding, VoiceEmbedding = s.VoiceEmbedding, FaceTime = s.FaceTime, SpeechTime = s.SpeechTime };
        return g;
    }

    // ---- Persistence via AIF Shared Storage (the governed, per-subject store) --
    //  The proper home flagged above. Each subject is one key "subject:<id>" whose
    //  value is that subject's embeddings; FIR and SIR (AIMs of the same Module)
    //  share this store, and the framework stamps provenance on every Put. A clone
    //  ships this store EMPTY - no biometrics leave the enrolling machine.
    public const string SubjectKeyPrefix = "subject:";

    public void Save(AIF.SharedStorage.ISharedStorage store)
    {
        foreach (var s in _subjects.Values)
        {
            var dto = new SubjectDto { SubjectId = s.SubjectId, FaceEmbedding = s.FaceEmbedding, VoiceEmbedding = s.VoiceEmbedding, FaceTime = s.FaceTime, SpeechTime = s.SpeechTime };
            store.Put(SubjectKeyPrefix + s.SubjectId,
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto, JsonOpts)));
        }
    }

    // Persist ONLY the named subject (one key), merging with what is already
    // stored so a face-only write and a later voice-only write accumulate rather
    // than overwrite. Never throws: a persistence hiccup must not abort enrolment
    // inside the Module. Enrolment uses this - one Put per enrolment, not a full
    // gallery rewrite.
    public void SaveSubject(AIF.SharedStorage.ISharedStorage store, string subjectId)
    {
        if (!_subjects.TryGetValue(subjectId, out var s)) return;
        var key = SubjectKeyPrefix + subjectId;

        SubjectDto dto = new SubjectDto { SubjectId = subjectId };
        try
        {
            if (store.Exists(key))
            {
                var existing = JsonSerializer.Deserialize<SubjectDto>(
                    System.Text.Encoding.UTF8.GetString(store.Get(key)), JsonOpts);
                if (existing is not null) dto = existing;
            }
        }
        catch { dto = new SubjectDto { SubjectId = subjectId }; }

        if (s.FaceEmbedding  is not null) { dto.FaceEmbedding  = s.FaceEmbedding;  dto.FaceTime   = s.FaceTime; }
        if (s.VoiceEmbedding is not null) { dto.VoiceEmbedding = s.VoiceEmbedding; dto.SpeechTime = s.SpeechTime; }
        dto.SubjectId = subjectId;

        store.Put(key, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto, JsonOpts)));
    }
    public static SubjectGallery Load(AIF.SharedStorage.ISharedStorage store,
                                      float faceThreshold = 0.35f, float voiceThreshold = 0.45f)
    {
        var g = new SubjectGallery(faceThreshold, voiceThreshold);
        foreach (var key in store.List(SubjectKeyPrefix))
        {
            var dto = JsonSerializer.Deserialize<SubjectDto>(
                System.Text.Encoding.UTF8.GetString(store.Get(key)), JsonOpts);
            if (dto is null) continue;
            g._subjects[dto.SubjectId] = new Subject
            {
                SubjectId = dto.SubjectId, FaceEmbedding = dto.FaceEmbedding, VoiceEmbedding = dto.VoiceEmbedding,
                FaceTime = dto.FaceTime, SpeechTime = dto.SpeechTime
            };
        }
        return g;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private sealed class Subject
    {
        public string SubjectId { get; init; } = "";
        public float[]? FaceEmbedding { get; set; }
        public float[]? VoiceEmbedding { get; set; }
        public string? FaceTime { get; set; }     // OSD-STM (JSON), acquisition time - recorded by ACR, ignored by MAC
        public string? SpeechTime { get; set; }
    }
    private sealed class GalleryDto { public List<SubjectDto> Subjects { get; set; } = new(); }
    private sealed class SubjectDto
    {
        public string SubjectId { get; set; } = "";
        public float[]? FaceEmbedding { get; set; }
        public float[]? VoiceEmbedding { get; set; }
        public string? FaceTime { get; set; }
        public string? SpeechTime { get; set; }
    }
}

// A top-1 match from the gallery, in the common subject space (Core-level type,
// so FIR/SIR/IDR all share it without depending on each other).
public readonly record struct GalleryMatch(string SubjectId, float Similarity);

// One subject's similarity score for a probe, in the common subject space.
public readonly record struct SubjectScore(string SubjectId, float Score);