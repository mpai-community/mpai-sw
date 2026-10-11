using System.Collections.Generic;
using System.Linq;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Editing;

// Speech, described to the shared composition engine.
//
// Derived from the audio adapter, which is the point: the two differ only in
// type names. What is NOT here is editing - audio edits an acoustic profile,
// speech edits a language, and those belong to their own AIMs.
//
// This is the whole of what is medium-specific about composing speech. A fourth
// medium costs a file of this size rather than a copy of MediaObjectEditor.
public sealed class SpeechObjectFamily : IMediaObjectFamily<BasicSpeechObject, SpeechObject>
{
    public string BasicPrefix => "BSO";
    public string FullPrefix  => "SPO";

    public string IdOfBasic(BasicSpeechObject basic) => basic.BasicSpeechObjectID ?? "";
    public string IdOfFull(SpeechObject full)        => full.SpeechObjectID ?? "";

    // BasicSpeechObject has no WithId - audio has one and speech was never
    // given the equivalent - so the copy is written here until it does.
    public BasicSpeechObject WithId(BasicSpeechObject basic, string id) => new()
    {
        Header = basic.Header,
        MInstanceID = basic.MInstanceID,
        UEnvironmentID = basic.UEnvironmentID,
        BasicSpeechObjectID = id,
        BasicSpeechObjectSpaceTime = basic.BasicSpeechObjectSpaceTime,
        Data = basic.Data,
        SpeechQualifier = basic.SpeechQualifier,
        DataXMData = basic.DataXMData,
        DescrMetadata = basic.DescrMetadata
    };

    public BasicSpeechObject BasicStub(string id) => new() { BasicSpeechObjectID = id };
    public SpeechObject FullStub(string id)       => new() { SpeechObjectID = id };

    public IReadOnlyList<(SpaceTime? Placement, string ChildId)> BasicEntriesOf(SpeechObject full) =>
        (full.BasicSpeechObjects ?? new List<BasicSpeechObjectEntry>())
            .Select(e => (e.BasicSpeechObjectSpaceTime, e.BSObjectIDOrBSObject?.BasicSpeechObjectID ?? ""))
            .Where(e => e.Item2 != "")
            .ToList();

    public IReadOnlyList<(SpaceTime? Placement, string ChildId)> SubEntriesOf(SpeechObject full) =>
        (full.SubSpeechObjects ?? new List<SubSpeechObjectEntry>())
            .Select(e => (e.SubSpeechObjectSpaceTime, e.SubSObjectIDOrSubSObject?.SpeechObjectID ?? ""))
            .Where(e => e.Item2 != "")
            .ToList();

    public SpeechObject Build(
        string id,
        SpeechObject? previous,
        IReadOnlyList<(SpaceTime? Placement, string ChildId)> basicEntries,
        IReadOnlyList<(SpaceTime? Placement, string ChildId)> subEntries,
        SpaceTime? spaceTime,
        PointOfView? listener) => new()
    {
        SpeechObjectID = id,
        SpeechObjectTime = previous?.SpeechObjectTime,

        // SpeechObjectSpaceTime is REQUIRED by the schema, so a composed Object
        // that has not been placed starts at the origin rather than at null.
        SpeechObjectSpaceTime = previous?.SpeechObjectSpaceTime ?? spaceTime,

        UserPoV = listener ?? previous?.UserPoV,
        ParentSpeechObjectIDs = previous?.ParentSpeechObjectIDs,

        BasicSpeechObjectCount = basicEntries.Count,
        BasicSpeechObjects = basicEntries.Count > 0
            ? basicEntries.Select(e => new BasicSpeechObjectEntry
              {
                  BasicSpeechObjectSpaceTime = e.Placement,
                  BSObjectIDOrBSObject      = BasicStub(e.ChildId)
              }).ToList()
            : null,

        SubSpeechObjectCount = subEntries.Count,
        SubSpeechObjects = subEntries.Count > 0
            ? subEntries.Select(e => new SubSpeechObjectEntry
              {
                  SubSpeechObjectSpaceTime  = e.Placement,
                  SubSObjectIDOrSubSObject = FullStub(e.ChildId)
              }).ToList()
            : null
    };

    public SpeechObject BuildResolved(
        string id,
        SpeechObject? stored,
        IReadOnlyList<(SpaceTime? Placement, BasicSpeechObject Child)> basicChildren,
        IReadOnlyList<(SpaceTime? Placement, SpeechObject Child)> subChildren) => new()
    {
        SpeechObjectID = id,
        SpeechObjectTime = stored?.SpeechObjectTime,
        SpeechObjectSpaceTime = stored?.SpeechObjectSpaceTime,
        UserPoV = stored?.UserPoV,
        ParentSpeechObjectIDs = stored?.ParentSpeechObjectIDs,

        BasicSpeechObjectCount = basicChildren.Count,
        BasicSpeechObjects = basicChildren.Count > 0
            ? basicChildren.Select(c => new BasicSpeechObjectEntry
              {
                  BasicSpeechObjectSpaceTime = c.Placement,
                  BSObjectIDOrBSObject      = c.Child
              }).ToList()
            : null,

        SubSpeechObjectCount = subChildren.Count,
        SubSpeechObjects = subChildren.Count > 0
            ? subChildren.Select(c => new SubSpeechObjectEntry
              {
                  SubSpeechObjectSpaceTime  = c.Placement,
                  SubSObjectIDOrSubSObject = c.Child
              }).ToList()
            : null
    };

    public SpeechObject FromBasic(string id, BasicSpeechObject basic) => new()
    {
        SpeechObjectID = id,
        BasicSpeechObjectCount = 1,
        BasicSpeechObjects = new List<BasicSpeechObjectEntry>
        {
            new BasicSpeechObjectEntry { BSObjectIDOrBSObject = basic }
        }
    };
}