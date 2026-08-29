// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The provenance RECORD, apart from the store that reads and writes it.
//
// Split out so the audit rules - which compare records, and never touch Revit -
// can be tested without a Revit. The storage half needs Extensible Storage; the
// meaning of a record does not, and a rule that decides whether a model still
// agrees with a drawing is exactly the kind of thing that must be provable at a
// desk rather than only in front of an open model.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What one element remembers about the drawing it was built from.</summary>
    public sealed class CadProvenance
    {
        public int SchemaVersion;
        public string CandidateId;          // the REVISION id: which entity, in which issue of the drawing
        public string GeometryId;           // what the thing IS - survives a re-issue of the file
        public string SemanticId;           // what it is and on which layer - what an incremental run matches by
        public string RuleId;               // which rule in the requirement set decided it
        public string RequirementSetId;
        public string RequirementSetVersion;
        public string RequirementSetSha256;
        public string SourceFingerprint;    // which drawing, which bytes, which transform
        public string SourceFileSha256;
        public string Layer;
        public string PlanFingerprint;
        /// <summary>
        /// The geometry this element was BUILT with, in mm, as "x,y,z;x,y,z".
        ///
        /// The one field an incremental update cannot work without. When the
        /// element and a new revision of the drawing disagree, there are two
        /// reasons and they need opposite treatment: the DRAWING moved, in which
        /// case updating the element is the whole point, or a PERSON moved it, in
        /// which case updating would silently destroy their work. Without a
        /// record of where it started, those two are indistinguishable, and the
        /// honest answer becomes "something changed and I cannot say what".
        /// </summary>
        public string BuiltGeometry;
        public string WrittenUtc;
        public double Confidence;

        public JObject ToJson() => new JObject
        {
            ["schema_version"] = SchemaVersion,
            ["candidate_id"] = CandidateId,
            ["geometry_id"] = GeometryId,
            ["semantic_id"] = SemanticId,
            ["rule_id"] = RuleId,
            ["requirement_set"] = new JObject
            {
                ["id"] = RequirementSetId,
                ["version"] = RequirementSetVersion,
                ["sha256"] = RequirementSetSha256
            },
            ["source_fingerprint"] = SourceFingerprint,
            ["source_file_sha256"] = SourceFileSha256,
            ["layer"] = Layer,
            ["plan_fingerprint"] = PlanFingerprint,
            ["built_geometry_mm"] = BuiltGeometry,
            ["written_utc"] = WrittenUtc,
            ["confidence"] = Math.Round(Confidence, 4)
        };
    }
}
