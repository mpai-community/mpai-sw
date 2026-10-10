using System;
using System.Collections.Generic;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Osd.VisualScene;

// ---------------------------------------------------------------------------
//  AVS - Visual pipeline (Slice 3), the visual counterpart of AvsAudioPipeline.
//
//  Consumes a BasicVisualObject (from VOA), runs SCRFD face DETECTION, and emits
//  BasicVisualSceneDescriptors (OSD-BVS): one entry per detected face, each an
//  object + its Spatial Attitude (PointOfView).
//
//  DESCRIBE, DO NOT IDENTIFY. Each entry says "a face is here, at this bearing".
//  It never says whose face. Recognition is FIR's job, downstream, consuming
//  these descriptors.
//
//  Spatial Attitude note: from a single 2D image we can give a BEARING (the
//  direction to the face, from image geometry) but not true 3D position - that
//  needs depth (LiDAR fusion, later). So PointOfView.SpherPosition carries the
//  visual bearing (azimuth/elevation); CartPosition is left at origin until
//  fusion supplies depth. This is honest: we describe direction, not distance.
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
//  FUTURE INPUT (reference model, not yet in the system): AVS is also to
//  receive the PREVIOUS cycle's Full Environment Descriptors (CAV-FED-V2.0)
//  as prior context - FED(t-1) -> AVS(t) - to inform the current scene
//  description (temporal continuity, tracking, disambiguation).
//
//  TRUST: HCI is a separate trust domain from the other Ego-CAV subsystems
//  (ESS/AMS/MAS). So FED arriving here crosses a trust boundary and carries
//  MPAI-PTF Data Exchange Metadata (DataXMData -> PTF/V1.0): it is external,
//  provenance/authorisation/confidence-checked data, to be trust-evaluated,
//  NOT treated as HCI-internal state. (FED from a REMOTE CAV crosses the same
//  way, arriving in the Ego-Remote HCI Message (CAV-ERH-V2.0), also PTF-borne.)
//
//  Depends on: fused AudioVisualSceneDescriptors (OSD-BMS) and the FED type,
//  neither of which exists yet. Add when those are built.
// ---------------------------------------------------------------------------
public sealed class AvsVisualPipeline
{
    private readonly ScrfdFaceDetector _detector;

    // Horizontal / vertical field of view of the camera, degrees. Used only to
    // turn a face's image position into a bearing. Configure per camera.
    private readonly double _hFovDeg;
    private readonly double _vFovDeg;

    public AvsVisualPipeline(ScrfdFaceDetector detector, double hFovDeg = 70.0, double vFovDeg = 40.0)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _hFovDeg = hFovDeg;
        _vFovDeg = vFovDeg;
    }

    public BasicVisualSceneDescriptors Process(BasicVisualObject visual)
    {
        if (visual is null) throw new ArgumentNullException(nameof(visual));

        // Detect faces (describe where they are).
        var faces = _detector.Detect(visual.Data);

        // We need image dimensions to map pixel positions to bearings. Decode
        // once via ImageSharp (the detector already loaded it; cheap for now,
        // an EXTENSION POINT is to pass dimensions out of Detect()).
        int imgW, imgH;
        using (var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(visual.Data))
        {
            imgW = img.Width; imgH = img.Height;
        }

        var entries = new List<BasicVisualSceneEntry>();
        foreach (var f in faces)
        {
            // Bearing from image position: centre of image = 0 deg. Right/up positive.
            double azimuth  = ((f.CentreX / imgW) - 0.5) * _hFovDeg;   // degrees
            double elevation = -(((f.CentreY / imgH) - 0.5) * _vFovDeg); // degrees (up positive)

            entries.Add(new BasicVisualSceneEntry
            {
                // The described object: the face as a Visual Object. For now the
                // whole source image is carried; EXTENSION POINT: crop to the
                // face box so FIR receives just the face region.
                VObjectIDOrVObject = visual,
                // Where the face is: bearing only (r unknown without depth),
                // (r=0, phi=azimuth, theta=elevation), until LiDAR fusion
                // supplies depth.
                VisualObjectSpaceTime = new SpaceTime
                {
                    SpaceTimeID = Guid.NewGuid().ToString(),
                    SpatialAttitude1 = new SpatialAttitude
                    {
                        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
                        Position = new Position { PositionID = Guid.NewGuid().ToString(), SpherPosition = new double[] { 0.0, azimuth, elevation } }
                    }
                }
            });
        }

        return new BasicVisualSceneDescriptors
        {
            BasicVisualSceneDescriptorsID = Guid.NewGuid().ToString(),
            VisualObjectCount = entries.Count,
            // The camera's place is not known here: at the origin, looking ahead.
            UserPoV = new PointOfView { PointOfViewID = "camera" },
            BasicVisualSceneDescriptorsEntries = entries
            // EXTENSION POINTs: crop each face to its own BasicVisualObject;
            // add body detection (also valid OSD-BVS objects); fuse with the
            // audio scene -> BasicAudioVisualSceneDescriptors (OSD-BMS).
        };
    }
}
