using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.Bls;   // BasicLiDARSceneDescriptors / entry / object DTOs

namespace Mpai.Osd.Ava;

// OSD-AVA-V1.5 - Audio-Visual Alignment, as an AIF IAimProcessor.
//
// Aligns the Basic Audio, Visual and LiDAR Scene Descriptors that share the same
// spatial attitude. Its purpose in MMC-HCI is to give the VISUAL objects their
// points of view: LiDAR measures a real 3D spatial attitude, the camera does not,
// so AVA copies each LiDAR object's SpaceTime onto the matching visual object.
// It emits the aligned Audio and Visual Scene Descriptors and the Basic Audio and
// Visual Scene Geometries (each object's SpaceTime + qualifier). The LiDAR Scene
// Descriptors are consumed to resolve the visual points of view and are not
// re-emitted. Matching is by object index (single-object scenes today); direction-
// of-arrival / bearing matching is the multi-object refinement.
public sealed class OsdAvaAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _inAudio;    // OSD-BAS
    private readonly string _inVisual;   // OSD-BVS
    private readonly string _inLiDAR;    // OSD-BLS
    private readonly string _outAudio;   // OSD-BAS  (aligned)
    private readonly string _outVisual;  // OSD-BVS  (aligned)
    private readonly string _outAudioGeom;   // OSD-BAG
    private readonly string _outVisualGeom;  // OSD-BVG

    public OsdAvaAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId    = instanceId;
        _inAudio       = ports.Input("OSD-BAS-V1.5");
        _inVisual      = ports.Input("OSD-BVS-V1.5");
        _inLiDAR       = ports.Input("OSD-BLS-V1.5");
        _outAudio      = ports.Output("OSD-BAS-V1.5");
        _outVisual     = ports.Output("OSD-BVS-V1.5");
        _outAudioGeom  = ports.Output("OSD-BAG-V1.5");
        _outVisualGeom = ports.Output("OSD-BVG-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        var bas = Read<BasicAudioSceneDescriptors>(message, _inAudio);
        var bvs = Read<BasicVisualSceneDescriptors>(message, _inVisual);
        var bls = Read<BasicLiDARSceneDescriptors>(message, _inLiDAR);

        if (bas is null && bvs is null)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId,
                "no Audio or Visual Scene Descriptors to align"));

        // --- align the visual objects: give each its point of view from the
        //     LiDAR object at the same index (its measured spatial attitude). ---
        var alignedVisualEntries = new List<BasicVisualSceneEntry>();
        if (bvs is not null)
        {
            var v = bvs.BasicVisualSceneDescriptorsEntries;
            var l = bls?.BasicLiDARSceneDescriptorsEntries;
            for (int i = 0; i < v.Count; i++)
            {
                var lidarSpaceTime = (l is not null && i < l.Count) ? l[i].ObjectSpaceTime : v[i].VisualObjectSpaceTime;
                alignedVisualEntries.Add(new BasicVisualSceneEntry
                {
                    VisualObjectSpaceTime = lidarSpaceTime,      // resolved from LiDAR
                    VObjectIDOrVObject    = v[i].VObjectIDOrVObject,
                    PointOfView           = v[i].PointOfView
                });
            }
        }

        var alignedVisual = bvs is null ? null : new BasicVisualSceneDescriptors
        {
            Header                             = "OSD-BVS-V1.5",
            MInstanceID                        = bvs.MInstanceID,
            BasicVisualSceneDescriptorsID      = bvs.BasicVisualSceneDescriptorsID,
            BVSDescriptorsSpaceTime            = bvs.BVSDescriptorsSpaceTime,
            ViewerPointOfView                  = bvs.ViewerPointOfView,
            VisualObjectCount                  = alignedVisualEntries.Count,
            BasicVisualSceneDescriptorsEntries = alignedVisualEntries
        };

        // --- aligned audio: pass the audio scene through unchanged ---
        var alignedAudio = bas;

        // --- geometries: each object's SpaceTime + qualifier ---
        var bvg = new BasicVisualSceneGeometry
        {
            Header                          = "OSD-BVG-V1.5",
            BasicVisualSceneGeometryID      = System.Guid.NewGuid().ToString(),
            BasicVisualSceneGeometrySpaceTime = bvs?.BVSDescriptorsSpaceTime,
            VisualObjectCount               = alignedVisualEntries.Count,
            VisualObjects                   = alignedVisualEntries.Select(e => new VisualSceneGeometryItem
            {
                VisualObjectSpaceTime = e.VisualObjectSpaceTime,
                VisualObjectQualifier = e.VObjectIDOrVObject?.VisualQualifier
            }).ToList()
        };

        var audioEntries = bas?.BasicAudioSceneDescriptorsEntries ?? new List<BasicAudioSceneEntry>();
        var bag = new BasicAudioSceneGeometry
        {
            Header                         = "OSD-BAG-V1.5",
            BasicAudioSceneGeometryID      = System.Guid.NewGuid().ToString(),
            BasicAudioSceneGeometrySpaceTime = bas?.BASSpaceTime,
            AudioObjectCount               = audioEntries.Count,
            AudioObjects                   = audioEntries.Select(e => new AudioSceneGeometryItem
            {
                AudioObjectSpaceTime = e.AudioObjectSpaceTime,
                AudioObjectQualifier = e.AudioObjectIDOrAudioObject?.AudioQualifier
            }).ToList()
        };

        var ports = new Dictionary<string, string>();
        if (alignedAudio  is not null) ports[_outAudio]  = MpaiJson.ToJson(alignedAudio);
        if (alignedVisual is not null) ports[_outVisual] = MpaiJson.ToJson(alignedVisual);
        ports[_outAudioGeom]  = MpaiJson.ToJson(bag);
        ports[_outVisualGeom] = MpaiJson.ToJson(bvg);

        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = message.MessageType,
            Ports       = ports
        });
    }

    private static T? Read<T>(Message message, string port) where T : class
    {
        if (!message.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        return MpaiJson.FromJson<T>(json);
    }
}

// ---- geometry DTOs (OSD-BAG / OSD-BVG); Mpai.Core.OSD has none yet ----
// Mirror schemas OSD/V1.5/data/BasicAudioSceneGeometry.json and BasicVisualSceneGeometry.json.

public sealed class BasicAudioSceneGeometry
{
    public string Header { get; init; } = "OSD-BAG-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicAudioSceneGeometryID { get; init; } = "";
    public SpaceTime? BasicAudioSceneGeometrySpaceTime { get; init; }
    public int AudioObjectCount { get; init; }
    public List<AudioSceneGeometryItem> AudioObjects { get; init; } = new();
    public string? DescrMetadata { get; init; }
}
public sealed class AudioSceneGeometryItem
{
    public SpaceTime? AudioObjectSpaceTime { get; init; }
    public AudioQualifier? AudioObjectQualifier { get; init; }
}

public sealed class BasicVisualSceneGeometry
{
    public string Header { get; init; } = "OSD-BVG-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicVisualSceneGeometryID { get; init; } = "";
    public SpaceTime? BasicVisualSceneGeometrySpaceTime { get; init; }
    public int VisualObjectCount { get; init; }
    public List<VisualSceneGeometryItem> VisualObjects { get; init; } = new();
    public string? DescrMetadata { get; init; }
}
public sealed class VisualSceneGeometryItem
{
    public SpaceTime? VisualObjectSpaceTime { get; init; }
    public VisualQualifier? VisualObjectQualifier { get; init; }
}
