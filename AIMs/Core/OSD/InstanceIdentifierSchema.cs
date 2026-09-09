using System;
using System.Collections.Generic;

using Mpai.Core;

namespace Mpai.Core;

// ---------------------------------------------------------------------------
//  Instance Identifier - OSD/V1.5/data/InstanceIdentifier.json, header
//  OSD-IID-V1.5. The shared output of the identity AIMs: FIR (face -> who),
//  SIR (voice -> who), and the scene identifiers (VSI/ASI) all produce this
//  SAME type. Downstream gets a uniform "identity of an instance".
//
//  LAYERED identification. An object can be identified at different levels of
//  specificity, and the LAYER is carried by each candidate's Taxonomy path:
//    "a sound"          -> TaxonomyLevelIDs ["sound"]
//    "speech"           -> ["sound","speech"]
//    "my wife's speech" -> InstanceLabel "my wife", ["sound","speech","speaker"]
//    "ambulance siren"  -> InstanceLabel "ambulance", ["sound","siren","ambulance"]
//  So InstanceIdentifierData can hold several candidates, each self-describing
//  its layer via TaxonomyLevelIDs (broad -> narrow). The first element is the
//  primary; the top-level InstanceIdentifier string mirrors that primary label.
//  Empty identification is not representable (schema requires >=1 candidate), so
//  "unknown" is expressed as a candidate at the coarsest known layer (e.g.
//  "a sound"/"a face") with low confidence, not an empty list.
// ---------------------------------------------------------------------------
public sealed class InstanceIdentifier
{
    public string Header { get; init; } = "OSD-IID-V1.5";
    public string MInstanceID { get; init; } = "";
    public string? UEnvironmentID { get; init; }

    // Primary instance identifier - equivalent to the first element's label.
    public string InstanceIdentifier_ { get; init; } = "";

    public SimpleTime? InstanceTime { get; init; }
    public SpaceTime? InstanceSpaceTime { get; init; }

    // The object being identified (schema field is misspelt "ObjrctID").
    public string? ObjectID { get; init; }

    // Ordered candidates, first is primary. Schema requires at least one.
    public List<InstanceCandidate> InstanceIdentifierData { get; init; } = new();

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

// One candidate identity hypothesis, at a particular taxonomy layer.
public sealed class InstanceCandidate
{
    public string InstanceLabel { get; init; } = "";
    public double LabelConfidenceLevel { get; init; }          // [0..1]
    public InstanceTaxonomy Taxonomy { get; init; } = new();
    public double? TaxonomyConfidenceLevel { get; init; }      // [0..1], optional
}

// The taxonomy situating a candidate's label - the hierarchical LAYER path plus
// a URI to the taxonomy definition.
public sealed class InstanceTaxonomy
{
    // Ordered level identifiers, broad -> narrow, e.g. ["sound","speech","speaker"].
    public List<string> TaxonomyLevelIDs { get; init; } = new();
    public string TaxonomyDataURI { get; init; } = "";
}
