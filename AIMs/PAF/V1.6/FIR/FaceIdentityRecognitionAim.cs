using System;
using System.Collections.Generic;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Fir;

// ---------------------------------------------------------------------------
//  Face Identity Recognition AIM (PAF-FIR-V1.6).
//  "Identifies a person from their face."
//
//  Input:  an (already-cropped) face image.
//  Output: an InstanceIdentifier (OSD-IID) - the SAME shared identity type SIR
//          produces. LAYERED: a recognised person is a candidate at
//          ["visual","face","person"]; an unrecognised face is still identified
//          as a face at ["visual","face"] (schema requires >=1 candidate, and
//          "it is a face, identity unknown" is the honest result).
//
//  Twin of MMC-SIR: embed (ArcFace) -> match the enrolled FaceDatabase -> emit
//  the identity. Wraps the verified ArcFaceRecogniser + FaceDatabase unchanged;
//  this AIM layer only adds the OSD-IID output so FIR and SIR are uniform. VSI
//  (visual scene object identification) dispatches face objects to this AIM.
// ---------------------------------------------------------------------------
public sealed class FaceIdentityRecognitionAim : IDisposable
{
    private readonly ArcFaceRecogniser _recogniser;
    private readonly SubjectGallery _gallery;
    private const string TaxonomyUri = "https://schemas.mpai.community/taxonomies/visual.json";

    public FaceIdentityRecognitionAim(ArcFaceRecogniser recogniser, SubjectGallery gallery)
    {
        _recogniser = recogniser ?? throw new ArgumentNullException(nameof(recogniser));
        _gallery = gallery ?? throw new ArgumentNullException(nameof(gallery));
    }

    public InstanceIdentifier Identify(Image<Rgb24> faceImage, string? mInstanceID = null, string? objectId = null)
        => IdentifyEmbedding(_recogniser.Embed(faceImage), mInstanceID, objectId);

    public InstanceIdentifier Identify(byte[] faceImageData, string? mInstanceID = null, string? objectId = null)
        => IdentifyEmbedding(_recogniser.Embed(faceImageData), mInstanceID, objectId);

    private InstanceIdentifier IdentifyEmbedding(float[] embedding, string? mInstanceID, string? objectId)
    {
        var match = _gallery.IdentifyFace(embedding);

        InstanceCandidate primary;
        if (match is GalleryMatch m)
        {
            primary = new InstanceCandidate
            {
                InstanceLabel = m.SubjectId,
                LabelConfidenceLevel = m.Similarity,
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = new List<string> { "visual", "face", "person" },
                    TaxonomyDataURI = TaxonomyUri
                },
                TaxonomyConfidenceLevel = 1.0
            };
        }
        else
        {
            // Not recognised, but still identified as a face - a coarser layer.
            primary = new InstanceCandidate
            {
                InstanceLabel = "face",
                LabelConfidenceLevel = 1.0,
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = new List<string> { "visual", "face" },
                    TaxonomyDataURI = TaxonomyUri
                },
                TaxonomyConfidenceLevel = 1.0
            };
        }

        return new InstanceIdentifier
        {
            MInstanceID = mInstanceID ?? "",
            InstanceIdentifier_ = Guid.NewGuid().ToString(),
            ObjectID = objectId,
            InstanceIdentifierData = new List<InstanceCandidate> { primary }
        };
    }

    public void Dispose() => _recogniser.Dispose();
}
