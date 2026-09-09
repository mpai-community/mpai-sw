using System;
using System.Collections.Generic;

namespace Mpai.Paf.Fir;

// ---------------------------------------------------------------------------
//  Face recognition database - the enrolled identities FIR matches against.
//
//  This is the "face recognition data base" the OSD-TMA FIR compared embeddings
//  against. Enrol a person's face embedding under an ID; identify a new face by
//  finding the nearest enrolled embedding above a similarity threshold.
//
//  Pure C#, no dependencies. In-memory here; persistence (to AIF Shared Storage)
//  is an EXTENSION POINT.
// ---------------------------------------------------------------------------
public sealed class FaceDatabase
{
    private readonly List<FaceRecord> _records = new();

    // Cosine-similarity threshold for a positive match. ArcFace-family models
    // typically use ~0.28-0.40 for verification; tune against real data.
    private readonly float _threshold;

    public FaceDatabase(float threshold = 0.35f) => _threshold = threshold;

    // Enrol (register) a face embedding under a known identity.
    public void Enrol(string personId, float[] embedding)
    {
        _records.Add(new FaceRecord { PersonId = personId, Embedding = embedding });
    }

    // Identify a face embedding: returns the best match above threshold, or null.
    public FaceMatch? Identify(float[] embedding)
    {
        FaceRecord? best = null;
        float bestSim = float.NegativeInfinity;

        foreach (var r in _records)
        {
            float sim = ArcFaceRecogniser.CosineSimilarity(embedding, r.Embedding);
            if (sim > bestSim) { bestSim = sim; best = r; }
        }

        if (best is null || bestSim < _threshold) return null;
        return new FaceMatch { PersonId = best.PersonId, Similarity = bestSim };
    }

    public int Count => _records.Count;
}

public sealed class FaceRecord
{
    public string PersonId { get; init; } = "";
    public float[] Embedding { get; init; } = Array.Empty<float>();
}

public sealed class FaceMatch
{
    public string PersonId { get; init; } = "";
    public float Similarity { get; init; }
}
