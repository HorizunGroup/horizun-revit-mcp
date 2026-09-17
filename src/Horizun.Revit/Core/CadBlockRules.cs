// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// A SYMBOL IS A BLOCK, AND A BLOCK HAS THE NAME ITS AUTHOR TYPED.
//
// Everything else in this conversion reads line work and infers: two parallel
// lines might be a wall, a closed ring might be a slab, a cluster of marks might
// be a fixture. A block instance is different in kind - somebody drew a symbol,
// named it, and placed it. `OUT2`, `TEL-DATA`, `E-LTS-VANITY`, `PNL`: those are
// not inferences, they are statements, and they are the shortest path there is
// from a drawing to a family type.
//
// The requirement set could already say `from: "blocks"` and the interpreter had
// no branch for it - the rule parsed, matched nothing, and reported nothing. This
// is that branch.
//
// WHAT IT REFUSES TO DO. It does not guess a family from a block name: the
// mapping is the caller's artefact, like every other mapping in this bridge. It
// does not resolve a tie between two rules of equal precedence - that is two
// readings of one symbol and a person decides. And it does not invent the things
// a plan symbol cannot carry: a height, a circuit, a load. Those are declared or
// they are missing, and missing is said out loud.
//
// XREF-QUALIFIED NAMES. A block that arrives through an external reference is
// called "DRAWING|NAME" - measured on a real permit set, where every symbol in
// the unit plan came through as "UNIT-XREF|VANITY-LIGHT". A pattern matches
// against both the qualified name and the bare one, because a caller writing a
// mapping should not have to know which drawing a symbol was nested in.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A block name no rule claimed, with what was seen of it.</summary>
    public sealed class CadUnclaimedBlock
    {
        public string BlockName;
        public int Count;
        public List<string> Layers = new List<string>();
        public List<string> AttributeTags = new List<string>();
        public CadPoint? Example;

        public JObject ToJson() => new JObject
        {
            ["block_name"] = BlockName,
            ["bare_name"] = CadBlockRules.BareName(BlockName),
            ["count"] = Count,
            ["layers"] = new JArray(Layers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            ["attribute_tags"] = new JArray(AttributeTags.OrderBy(x => x, StringComparer.Ordinal)),
            ["example_at_mm"] = Example.HasValue
                ? (JToken)new JArray(Math.Round(Example.Value.X, 2), Math.Round(Example.Value.Y, 2))
                : JValue.CreateNull()
        };
    }

    /// <summary>Two rules of equal precedence claiming one block. Reported, never resolved.</summary>
    public sealed class CadBlockTie
    {
        public string BlockName;
        public List<string> RuleIds = new List<string>();
        public int Precedence;
        public int Count;

        public JObject ToJson() => new JObject
        {
            ["block_name"] = BlockName,
            ["rules"] = new JArray(RuleIds),
            ["precedence"] = Precedence,
            ["instances"] = Count,
            ["means"] = "two rules claim this block at the same precedence, which is two readings of one " +
                        "symbol. Nothing was produced from it: taking the first would give the instances " +
                        "whichever family sorted first by rule id, in a reply that looked completely ordinary."
        };
    }

    public sealed class CadBlockReading
    {
        public List<CadCandidate> Candidates = new List<CadCandidate>();
        public List<CadUnclaimedBlock> Unclaimed = new List<CadUnclaimedBlock>();
        public List<CadBlockTie> Ties = new List<CadBlockTie>();
        public int InstancesConsidered;
        public int InstancesClaimed;

        /// <summary>What became of each instance handed in, by the instance itself.</summary>
        public readonly Dictionary<CadIrEntity, CadBlockOutcome> Outcomes =
            new Dictionary<CadIrEntity, CadBlockOutcome>();

        /// <summary>How much of the drawing's symbol population a mapping covers, 0..1.</summary>
        public double Coverage => InstancesConsidered == 0 ? 0 : (double)InstancesClaimed / InstancesConsidered;

        public JObject SummaryJson() => new JObject
        {
            ["instances_considered"] = InstancesConsidered,
            ["instances_claimed"] = InstancesClaimed,
            ["coverage"] = Math.Round(Coverage, 4),
            ["candidates"] = Candidates.Count,
            ["unclaimed_block_names"] = Unclaimed.Count,
            ["ties"] = Ties.Count,
            ["means"] = "coverage is the fraction of BLOCK INSTANCES a rule claimed. The unclaimed names " +
                        "below are the drawing's own vocabulary with counts - they are what a mapping is " +
                        "written against, and the ones with the highest counts are worth the most."
        };
    }

    /// <summary>One instance's fate in a reading: claimed by a rule, unclaimed, or tied.</summary>
    public sealed class CadBlockOutcome
    {
        public string Outcome;
        public string RuleId;
        public CadCandidate Candidate;
        public List<string> TieRules;
    }

    public static class CadBlockRules
    {
        /// <summary>The name without its external-reference prefix: "XREF|OUT2" is "OUT2".</summary>
        public static string BareName(string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return blockName;
            int bar = blockName.LastIndexOf('|');
            return bar >= 0 && bar + 1 < blockName.Length ? blockName.Substring(bar + 1) : blockName;
        }

        /// <summary>
        /// An anonymous block - "*U12", "A$C1E4116A0" - carries no statement about
        /// what it is. AutoCAD generates the name; nobody typed it.
        ///
        /// They are not dropped: a drawing where most instances are anonymous is
        /// worth knowing about, because it means the symbol vocabulary this whole
        /// approach rests on is not there.
        /// </summary>
        public static bool IsAnonymous(string blockName)
        {
            string bare = BareName(blockName);
            if (string.IsNullOrEmpty(bare)) return true;
            return bare[0] == '*' || bare.StartsWith("A$", StringComparison.Ordinal);
        }

        private static bool Claims(CadRule rule, CadIrEntity instance, bool caseSensitive)
        {
            if (rule.Geometry == null || rule.Geometry.Source != CadGeometrySource.Blocks) return false;

            // A rule that names layers must still match the layer: a symbol on the
            // demolition layer is not the same statement as one on the new-work
            // layer, whatever it is called.
            if (rule.LayerPatterns.Count > 0 && !set_matches_layer(rule, instance.Layer, caseSensitive)) return false;

            if (!AttributesMatch(rule.Geometry.BlockAttributes, instance.Attributes)) return false;

            if (rule.Geometry.BlockPatterns.Count == 0)
                return rule.LayerPatterns.Count > 0;   // "every block on my layers"

            string name = instance.BlockName ?? "";
            string bare = BareName(name);
            foreach (string p in rule.Geometry.BlockPatterns)
                if (CadGlob.IsMatch(name, p, caseSensitive) || CadGlob.IsMatch(bare, p, caseSensitive))
                    return true;
            return false;
        }

        private static bool AttributesMatch(Dictionary<string, bool> wanted, Dictionary<string, string> has)
        {
            if (wanted == null || wanted.Count == 0) return true;
            foreach (var kv in wanted)
            {
                bool present = has != null && has.Keys.Any(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (present != kv.Value) return false;
            }
            return true;
        }

        private static bool set_matches_layer(CadRule rule, string layer, bool caseSensitive)
        {
            layer = layer ?? "";
            foreach (string ex in rule.ExcludeLayerPatterns)
                if (CadGlob.IsMatch(layer, ex, caseSensitive)) return false;
            foreach (string inc in rule.LayerPatterns)
                if (CadGlob.IsMatch(layer, inc, caseSensitive)) return true;
            return false;
        }

        /// <summary>
        /// Block instances into candidates, through the caller's own mapping.
        ///
        /// <paramref name="instances"/> are the IR's block instances - which only a
        /// reader that reads the FILE can supply, because Revit's import carries no
        /// block name at all.
        /// </summary>
        public static CadBlockReading Interpret(IList<CadIrEntity> instances, CadRequirementSet set,
                                                string sourceHash)
        {
            var reading = new CadBlockReading();
            if (set == null) throw new ArgumentNullException("set");
            if (instances == null) return reading;

            bool caseSensitive = set.CaseSensitiveLayers;
            var unclaimed = new Dictionary<string, CadUnclaimedBlock>(StringComparer.OrdinalIgnoreCase);
            var ties = new Dictionary<string, CadBlockTie>(StringComparer.OrdinalIgnoreCase);
            int n = 0;

            foreach (CadIrEntity e in instances)
            {
                if (e == null || e.Kind != CadEntityKind.BlockInstance) continue;
                if (e.Points.Count == 0) continue;
                reading.InstancesConsidered++;

                List<CadRule> claiming = set.Rules.Where(r => Claims(r, e, caseSensitive)).ToList();
                if (claiming.Count == 0)
                {
                    Note(unclaimed, e);
                    reading.Outcomes[e] = new CadBlockOutcome { Outcome = CadInventoryOutcome.Unclaimed };
                    continue;
                }

                int best = claiming.Max(r => r.Precedence);
                List<CadRule> top = claiming.Where(r => r.Precedence == best).ToList();
                if (top.Count > 1)
                {
                    string key = e.BlockName ?? "(unnamed)";
                    CadBlockTie tie;
                    if (!ties.TryGetValue(key, out tie))
                        ties[key] = tie = new CadBlockTie
                        {
                            BlockName = key,
                            Precedence = best,
                            RuleIds = top.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList()
                        };
                    tie.Count++;
                    reading.Outcomes[e] = new CadBlockOutcome
                    {
                        Outcome = CadInventoryOutcome.Tie,
                        TieRules = top.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList()
                    };
                    continue;
                }

                CadRule rule = top[0];
                CadPoint at = e.Points[0];
                var c = new CadCandidate
                {
                    Id = "b" + (++n).ToString(CultureInfo.InvariantCulture),
                    ProposedKind = rule.Produces,
                    RuleId = rule.Id,
                    Layer = e.Layer,
                    Discipline = rule.Discipline,
                    Category = rule.Category,
                    FamilyType = rule.FamilyType,
                    HostedOn = rule.HostedOn,
                    MirrorPolicy = rule.Mirror ?? "preserve",
                    MirrorEvidenceVariant = rule.MirrorVariantType,
                    Level = rule.Level,
                    RotationRadians = e.RotationRadians,
                    Mirrored = (e.ScaleX.HasValue && e.ScaleX.Value < 0) ^ (e.ScaleY.HasValue && e.ScaleY.Value < 0),
                    SourceBlockName = e.BlockName
                };
                c.Geometry.Add(at);

                // WHICH WAY IT FACES, only where the set says so for this block.
                foreach (KeyValuePair<string, CadVector> f in rule.Geometry.BlockFacing)
                {
                    string name = e.BlockName ?? "";
                    if (!CadGlob.IsMatch(name, f.Key, caseSensitive) &&
                        !CadGlob.IsMatch(BareName(name), f.Key, caseSensitive)) continue;
                    c.Facing = CadDeviceSide.InPlan(f.Value, e.RotationRadians, e.ScaleX, e.ScaleY);
                    if (c.Facing.HasValue) c.FacingDeclaredBy = f.Key;
                    break;
                }

                // CONFIDENCE IS EARNED HERE TOO. A symbol matched by the NAME its
                // author typed is the strongest evidence this conversion ever gets;
                // one matched only because it sits on a claimed layer is a good
                // deal weaker, and an anonymous block - a name AutoCAD generated,
                // that nobody chose - is weaker again. A flat 1.0 would have made
                // the eligibility machinery downstream meaningless for every symbol.
                bool byName = rule.Geometry.BlockPatterns.Count > 0;
                bool anonymous = IsAnonymous(e.BlockName);
                c.ConfidenceFactors.Add(new CadConfidenceFactor("block_match", 0.6, byName ? 1.0 : 0.4,
                    byName
                        ? "the rule names this block explicitly"
                        : "the rule claims every block on this layer and does not name this one"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("author_named_it", 0.2, anonymous ? 0.0 : 1.0,
                    anonymous
                        ? "'" + (e.BlockName ?? "(unnamed)") + "' is an anonymous block: AutoCAD generated " +
                          "the name and it says nothing about what the symbol is"
                        : "the block carries a name somebody typed"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("orientation", 0.2,
                    e.RotationRadians.HasValue ? 1.0 : 0.0,
                    e.RotationRadians.HasValue
                        ? "the instance reports its rotation"
                        : "the instance reports no rotation, so this would be placed at zero"));

                // IDENTITY. The geometry id is what the thing IS - this block, at
                // this place - and the semantic id adds the layer, so the same
                // symbol on two layers is two different things. Both are derived
                // exactly as the line-work path derives them, because an audit that
                // matches elements back to a drawing has to work the same way for
                // a symbol as for a pipe.
                c.GeometryId = CadIdentity.GeometryId(CadCurveKind.Line, new List<CadPoint> { at },
                                                      set.PointToleranceMm);
                c.SemanticId = CadIdentity.SemanticIdOf(e.Layer, (e.BlockName ?? "root"), c.GeometryId);

                // THE REVISION ID, NOT A READING ORDINAL. This candidate used to keep
                // "b" + its position in the reading as its id, and provenance recorded
                // that - so a revision that added or removed one symbol earlier in the
                // order shifted every later id, and the audit's "same entity in the
                // same issue" rung paired the wrong symbols. A re-issued file matched
                // on it too, because the ordinal carries no source. The id is now what
                // it is for every other candidate: this entity, in this issue.
                c.Id = CadIdentity.RevisionId(sourceHash, c.SemanticId);
                c.SourceSurrogates.Add(c.Id);
                if (e.Handle != null) c.SourceSurrogates.Add("handle:" + e.Handle);
                c.SourceSurrogates.Add("block:" + (e.BlockName ?? "(unnamed)"));

                // ATTRIBUTES ARE OBSERVED FACTS, and they travel as parameter
                // writes only where the rule asked for them. A tag nobody mapped is
                // still reported - it is evidence about the symbol - but it is not
                // written into the model under a name this bridge invented.
                if (e.Attributes != null && e.Attributes.Count > 0)
                {
                    c.ObservedAttributes = new Dictionary<string, string>(e.Attributes, StringComparer.Ordinal);
                    foreach (CadParameterWrite w in rule.Parameters ?? new List<CadParameterWrite>())
                    {
                        string fromTag = w.FromBlockAttribute;
                        string value;
                        if (fromTag != null && e.Attributes.TryGetValue(fromTag, out value))
                            c.Parameters.Add(new CadParameterWrite
                            {
                                Parameter = w.Parameter,
                                Value = value,
                                Scope = w.Scope,
                                Required = w.Required,
                                FromBlockAttribute = fromTag
                            });
                    }
                }

                // WHAT A PLAN SYMBOL CANNOT SAY, said rather than defaulted.
                if (rule.Level == null)
                    c.UnresolvedFacts.Add("level: a symbol in a plan carries none and the rule declared none");
                if (rule.OffsetMm == null)
                    c.UnresolvedFacts.Add("height: a symbol in a plan is at the drawing's Z and the rule " +
                                          "declared no offset, so this instance would be placed at the level");
                if (!e.RotationRadians.HasValue)
                    c.UnresolvedFacts.Add("orientation: the instance reports no rotation");

                c.ExpectedVerification.Add("the created instance re-reads as category " +
                                           (rule.Category ?? "(rule declared none)"));
                c.ExpectedVerification.Add("its location is within the point tolerance of the block's " +
                                           "insertion point");

                // ELIGIBILITY IS DECIDED THE SAME WAY AS EVERYWHERE ELSE.
                //
                // A candidate this file produced and did not finalise was never
                // eligible, so the plan silently built nothing from it - the same
                // class of failure as a rule that parses and matches nothing.
                c.EligibleForAutomaticApply = c.IneligibleReasons.Count == 0 &&
                                              c.Confidence >= rule.MinConfidence;
                if (!c.EligibleForAutomaticApply && c.IneligibleReasons.Count == 0)
                    c.IneligibleReasons.Add(
                        "confidence " + c.Confidence.ToString("0.00", CultureInfo.InvariantCulture) +
                        " is under the " + rule.MinConfidence.ToString("0.00", CultureInfo.InvariantCulture) +
                        " this rule requires. For a symbol that is usually the block being anonymous, or the " +
                        "rule claiming every block on a layer rather than naming this one.");

                reading.Candidates.Add(c);
                reading.InstancesClaimed++;
                reading.Outcomes[e] = new CadBlockOutcome
                {
                    Outcome = CadInventoryOutcome.Claimed, RuleId = rule.Id, Candidate = c
                };
            }

            // ONE IDENTITY, ONE SYMBOL - or a person decides. Two instances that
            // quantise to the same id cannot be told apart by provenance, audit or
            // update; neither is dropped and neither is built unattended.
            foreach (var clash in reading.Candidates.GroupBy(x => x.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
                foreach (CadCandidate twin in clash)
                {
                    twin.EligibleForAutomaticApply = false;
                    string why = clash.Count() + " instances share the identity " + clash.Key + " - the same block on " +
                                 "the same layer within the point tolerance - so nothing could tell their elements " +
                                 "apart later. None was dropped; decide which is real.";
                    if (!twin.IneligibleReasons.Contains(why)) twin.IneligibleReasons.Add(why);
                }

            reading.Unclaimed = unclaimed.Values
                .OrderByDescending(u => u.Count)
                .ThenBy(u => u.BlockName, StringComparer.OrdinalIgnoreCase).ToList();
            reading.Ties = ties.Values.OrderBy(t => t.BlockName, StringComparer.OrdinalIgnoreCase).ToList();
            return reading;
        }

        private static void Note(Dictionary<string, CadUnclaimedBlock> map, CadIrEntity e)
        {
            string key = e.BlockName ?? "(unnamed)";
            CadUnclaimedBlock u;
            if (!map.TryGetValue(key, out u))
                map[key] = u = new CadUnclaimedBlock { BlockName = key, Example = e.Points[0] };
            u.Count++;
            if (e.Layer != null && !u.Layers.Contains(e.Layer, StringComparer.OrdinalIgnoreCase))
                u.Layers.Add(e.Layer);
            if (e.Attributes != null)
                foreach (string tag in e.Attributes.Keys)
                    if (!u.AttributeTags.Contains(tag, StringComparer.Ordinal)) u.AttributeTags.Add(tag);
        }
    }
}
