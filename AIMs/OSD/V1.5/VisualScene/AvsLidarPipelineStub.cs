using System;

using Mpai.Core;

namespace Mpai.Osd.VisualScene;

// ---------------------------------------------------------------------------
//  AVS - LiDAR pipeline (STUB).
//
//  Placeholder for the third AVS modality. Same shape as the audio and visual
//  pipelines: a LiDAR Object in, a Basic LiDAR Scene Descriptors out. Not yet
//  implemented - LiDAR is scheduled after visual (Chiariglione).
//
//  When built, LiDAR is also the enabler for:
//    - true 3D Spatial Attitude (depth), completing the visual pipeline's
//      bearing-only PointOfView into full position;
//    - the harder half of VOI (resolving which object a human points at),
//      which needs depth to be reliable.
//
//  A BasicLiDARSceneDescriptors OSD type does not exist yet; it will be created
//  from the OSD schema (mirroring BasicAudio/BasicVisual) when this is built.
// ---------------------------------------------------------------------------
public sealed class AvsLidarPipelineStub
{
    // Intentionally not implemented. Kept as a typed placeholder so the modality
    // set is visible and the build tree is ready for the LiDAR slice.
    public object Process(object lidarObject)
        => throw new NotImplementedException(
            "LiDAR AVS pipeline is a stub. Scheduled after the visual pipeline. " +
            "Will consume a LiDAR Object and emit BasicLiDARSceneDescriptors (to be created).");
}
