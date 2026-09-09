using System.Collections.Generic;
using AIF.Controller;
using Mpai.Core;
using Mpai.Osd.VisualScene;   // ScrfdFaceDetector
using Mpai.Core.OSD;          // SubjectGallery

namespace Mpai.Paf.Fir;

// Plug-in for Face Instance Recognition. Builds and caches its own SCRFD detector,
// ArcFace recogniser and the SubjectGallery it matches against - each path from
// settings, defaulting to the current location. Discovered dynamically (IAimPlugin).
public sealed class FirPlugin : IAimPlugin
{
    private ScrfdFaceDetector? _scrfd;
    private ArcFaceRecogniser? _arcFace;
    private SubjectGallery?    _gallery;

    public string AimName => "PAF-FIR-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new FirAimProcessor(
            AimName,
            _scrfd   ??= new ScrfdFaceDetector(Get(settings, "ScrfdModel",  Mpai.Core.MpaiPaths.Model("scrfd_10g_bnkps.onnx"))),
            _arcFace ??= new ArcFaceRecogniser(Get(settings, "ArcFaceModel", Mpai.Core.MpaiPaths.Model("glintr100.onnx"))),
            _gallery ??= SubjectGallery.Load(new AIF.SharedStorage.FileSharedStorage(Mpai.Core.MpaiPaths.SharedStorage, AimName, "local")),
            ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
