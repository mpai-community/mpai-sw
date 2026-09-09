using System;
using System.IO;
using System.Linq;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Mpai.Core;
using Mpai.Paf.Fir;
using Mpai.Mmc.Sir;
using Mpai.Osd.VisualScene;   // ScrfdFaceDetector

namespace Mpai.Hci.Idr;

// Media-taking enrolment for the SubjectGallery. The gallery itself is model-
// agnostic (Mpai.Core, stores embeddings + cosine), so the convenience of
// enrolling FROM a photo / wav lives here, where the FIR (ArcFace) and SIR
// (ECAPA) embedders are available. This is the "add a user entry" entry point:
// give a face image and/or a voice clip + a subject id, and the person becomes a
// single entry that FIR, SIR and IDR all recognise.
//
// CRITICAL - enrol and recognise must embed IDENTICALLY. FIR runs SCRFD to
// detect the face, crops it, and embeds the CROP with ArcFace. Enrolment must do
// the SAME, or the gallery holds a whole-image embedding that never matches the
// cropped-face embedding FIR produces. So enrolment takes a SCRFD detector too
// and mirrors FIR's detect -> crop -> embed path exactly.
public static class SubjectEnrolment
{
    public static void EnrolSubject(
        SubjectGallery gallery,
        string subjectId,
        ArcFaceRecogniser? faceRecogniser = null,
        string? faceImagePath = null,
        SpeakerEmbedder? speakerEmbedder = null,
        string? voiceClipPath = null,
        ScrfdFaceDetector? faceDetector = null)
    {
        float[]? face = null, voice = null;

        if (faceImagePath is not null)
        {
            if (faceRecogniser is null) throw new ArgumentNullException(nameof(faceRecogniser),
                "a face image was given but no ArcFaceRecogniser to embed it");

            var imageBytes = File.ReadAllBytes(faceImagePath);

            if (faceDetector is not null)
            {
                // Mirror FIR: detect, take the most prominent face, crop, embed the crop.
                var faces = faceDetector.Detect(imageBytes);
                if (faces.Count == 0)
                    throw new InvalidOperationException(
                        $"No face detected in '{faceImagePath}' - cannot enrol a face template.");
                var f = faces.OrderByDescending(d => d.Width * d.Height).First();
                using Image<Rgb24> crop = FaceCrop.Crop(imageBytes, f.X1, f.Y1, f.X2, f.Y2);
                face = faceRecogniser.Embed(crop);
            }
            else
            {
                // Fallback (legacy): embed the whole image. Kept only so callers
                // without a detector still work; NOT comparable with FIR's crops.
                face = faceRecogniser.Embed(imageBytes);
            }
        }

        if (voiceClipPath is not null)
        {
            if (speakerEmbedder is null) throw new ArgumentNullException(nameof(speakerEmbedder),
                "a voice clip was given but no SpeakerEmbedder to embed it");
            voice = speakerEmbedder.Embed(WavReader.ReadMono16k(voiceClipPath));
        }

        gallery.EnrolEmbeddings(subjectId, face, voice);
    }
}
