using System.Collections.Generic;
using System.Linq;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Editing;

// Audio, described to the shared composition engine.
//
// This is the whole of what is medium-specific about composing audio. A fourth
// medium costs a file of this size rather than a copy of MediaObjectEditor.
public sealed class AudioObjectFamily : IMediaObjectFamily<BasicAudioObject, AudioObject>
{
    public string BasicPrefix => "BAO";
    public string FullPrefix  => "AUO";

    public string IdOfBasic(BasicAudioObject basic) => basic.BasicAudioObjectID ?? "";
    public string IdOfFull(AudioObject full)        => full.AudioObjectID ?? "";

    public BasicAudioObject WithId(BasicAudioObject basic, string id) => basic.WithId(id);

    public BasicAudioObject BasicStub(string id) => new() { BasicAudioObjectID = id };
    public AudioObject FullStub(string id)       => new() { AudioObjectID = id };

    public IReadOnlyList<(SpaceTime? Placement, string ChildId)> BasicEntriesOf(AudioObject full) =>
        (full.BasicAudioObjects ?? new List<BasicAudioObjectEntry>())
            .Select(e => (e.BasicAudioObjectSpaceTime, e.BAObjectIDOrBAObject?.BasicAudioObjectID ?? ""))
            .Where(e => e.Item2 != "")
            .ToList();

    public IReadOnlyList<(SpaceTime? Placement, string ChildId)> SubEntriesOf(AudioObject full) =>
        (full.SubAudioObjects ?? new List<SubAudioObjectEntry>())
            .Select(e => (e.SubAudioObjectSpaceTime, e.SubAObjectIDOrSubAObject?.AudioObjectID ?? ""))
            .Where(e => e.Item2 != "")
            .ToList();

    public AudioObject Build(
        string id,
        AudioObject? previous,
        IReadOnlyList<(SpaceTime? Placement, string ChildId)> basicEntries,
        IReadOnlyList<(SpaceTime? Placement, string ChildId)> subEntries,
        SpaceTime? spaceTime,
        PointOfView? listener) => new()
    {
        AudioObjectID = id,
        AudioObjectTime = previous?.AudioObjectTime,

        // AudioObjectSpaceTime is REQUIRED by the schema, so a composed Object
        // that has not been placed starts at the origin rather than at null.
        AudioObjectSpaceTime = previous?.AudioObjectSpaceTime ?? spaceTime,

        UserPoV = listener ?? previous?.UserPoV,
        ParentAudioObjectIDs = previous?.ParentAudioObjectIDs,

        BasicAudioObjectCount = basicEntries.Count,
        BasicAudioObjects = basicEntries.Count > 0
            ? basicEntries.Select(e => new BasicAudioObjectEntry
              {
                  BasicAudioObjectSpaceTime = e.Placement,
                  BAObjectIDOrBAObject      = BasicStub(e.ChildId)
              }).ToList()
            : null,

        SubAudioObjectCount = subEntries.Count,
        SubAudioObjects = subEntries.Count > 0
            ? subEntries.Select(e => new SubAudioObjectEntry
              {
                  SubAudioObjectSpaceTime  = e.Placement,
                  SubAObjectIDOrSubAObject = FullStub(e.ChildId)
              }).ToList()
            : null
    };

    public AudioObject BuildResolved(
        string id,
        AudioObject? stored,
        IReadOnlyList<(SpaceTime? Placement, BasicAudioObject Child)> basicChildren,
        IReadOnlyList<(SpaceTime? Placement, AudioObject Child)> subChildren) => new()
    {
        AudioObjectID = id,
        AudioObjectTime = stored?.AudioObjectTime,
        AudioObjectSpaceTime = stored?.AudioObjectSpaceTime,
        UserPoV = stored?.UserPoV,
        ParentAudioObjectIDs = stored?.ParentAudioObjectIDs,

        BasicAudioObjectCount = basicChildren.Count,
        BasicAudioObjects = basicChildren.Count > 0
            ? basicChildren.Select(c => new BasicAudioObjectEntry
              {
                  BasicAudioObjectSpaceTime = c.Placement,
                  BAObjectIDOrBAObject      = c.Child
              }).ToList()
            : null,

        SubAudioObjectCount = subChildren.Count,
        SubAudioObjects = subChildren.Count > 0
            ? subChildren.Select(c => new SubAudioObjectEntry
              {
                  SubAudioObjectSpaceTime  = c.Placement,
                  SubAObjectIDOrSubAObject = c.Child
              }).ToList()
            : null
    };

    public AudioObject FromBasic(string id, BasicAudioObject basic) => new()
    {
        AudioObjectID = id,
        BasicAudioObjectCount = 1,
        BasicAudioObjects = new List<BasicAudioObjectEntry>
        {
            new BasicAudioObjectEntry { BAObjectIDOrBAObject = basic }
        }
    };
}