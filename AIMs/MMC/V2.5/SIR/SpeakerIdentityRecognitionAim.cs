using System;
using System.Collections.Generic;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Sir;

// ---------------------------------------------------------------------------
//  Speaker Identity Recognition AIM (MMC-SIR-V2.5).
//  "Identifies a speaker based on their speech."
//
//  Input:  a SpeechObject's audio samples (16 kHz mono).
//  Output: an InstanceIdentifier (OSD-IID) - the shared identity type, also
//          produced by FIR. LAYERED: a recognised speaker is a candidate at
//          ["sound","speech","speaker"]; an unrecognised voice is still
//          identified as speech at ["sound","speech"] (schema requires >=1
//          candidate, and "it is speech, speaker unknown" is the honest result).
//
//  Twin of PAF-FIR: embed (ECAPA) -> match the enrolled SpeakerDatabase -> emit
//  the identity. Identity-only: WHERE the speaker is is the audio scene's job;
//  ALIGNING sight and sound is OSD-AVA's; SIR says WHO (or, failing that, WHAT
//  layer it could reach).
// ---------------------------------------------------------------------------
public sealed class SpeakerIdentityRecognitionAim : IDisposable
{
    private readonly SpeakerEmbedder _embedder;
    private readonly SubjectGallery _gallery;
    private const string TaxonomyUri = "https://schemas.mpai.community/taxonomies/sound.json";

    public SpeakerIdentityRecognitionAim(SpeakerEmbedder embedder, SubjectGallery gallery)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _gallery = gallery ?? throw new ArgumentNullException(nameof(gallery));
    }

    public InstanceIdentifier Identify(float[] speechSamples, string? mInstanceID = null)
    {
        var embedding = _embedder.Embed(speechSamples);
        var match = _gallery.IdentifyVoice(embedding);

        InstanceCandidate primary;
        if (match is GalleryMatch m)
        {
            // Recognised: speaker layer.
            primary = new InstanceCandidate
            {
                InstanceLabel = m.SubjectId,
                LabelConfidenceLevel = m.Similarity,
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = new List<string> { "sound", "speech", "speaker" },
                    TaxonomyDataURI = TaxonomyUri
                },
                TaxonomyConfidenceLevel = 1.0
            };
        }
        else
        {
            // Not recognised, but still identified as speech - a coarser layer.
            primary = new InstanceCandidate
            {
                InstanceLabel = "speech",
                LabelConfidenceLevel = 1.0,   // it IS speech; the speaker is what's unknown
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = new List<string> { "sound", "speech" },
                    TaxonomyDataURI = TaxonomyUri
                },
                TaxonomyConfidenceLevel = 1.0
            };
        }

        return new InstanceIdentifier
        {
            MInstanceID = mInstanceID ?? "",
            InstanceIdentifier_ = Guid.NewGuid().ToString(),
            InstanceIdentifierData = new List<InstanceCandidate> { primary }
        };
    }

    public void Dispose() => _embedder.Dispose();
}
