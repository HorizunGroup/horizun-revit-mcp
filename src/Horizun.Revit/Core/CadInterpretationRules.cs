// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Interpretation: geometry plus a requirement set becomes CANDIDATES.
//
// Nothing here builds anything. It produces proposals, each carrying the
// evidence for itself, and the single most important thing it produces is the
// list of proposals it REFUSES to make on its own.
//
// WHAT CONFIDENCE IS, AND WHAT IT IS NOT.
//
// Confidence here is a weighted score over NAMED, MEASURED factors - overlap
// fraction, angular deviation, how specific the matching layer pattern was, how
// many rules competed for the same geometry. It is not a probability. Nothing
// calibrated it against outcomes, and no number it produces means "84% of these
// are correct". It exists for one purpose: to ORDER candidates and to sit
// against a threshold the requirement set declared, so that a reviewer looks at
// the weakest ones first. Every score ships with the factors that produced it,
// so a reviewer can disagree with the weighting instead of with the number.
//
// AMBIGUITY IS AN OUTPUT, NOT AN ERROR. Two rules claiming one piece of
// geometry, a loop that could be a room or a slab, a line pair that could be a
// wall or a stair - these are the normal content of a real drawing. An
// unattended run models NONE of them and says so; that is the difference
// between a tool somebody can leave running and a tool somebody has to watch.
//
// Revit-free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One named factor that moved a candidate's confidence, and what was measured.</summary>
    public sealed class CadConfidenceFactor
    {
        public string Name;
        public double Weight;          // how much of the score this factor can contribute
        public double Score;           // 0..1, what was measured
        public string Observed;        // the measurement in words, for a human reading a review

        public CadConfidenceFactor(string name, double weight, double score, string observed)
        { Name = name; Weight = weight; Score = Math.Max(0, Math.Min(1, score)); Observed = observed; }

        public double Contribution => Weight * Score;
    }

    /// <summary>A proposal: what this geometry could become, and why anyone should believe it.</summary>
    public sealed class CadCandidate
    {
        /// <summary>The revision id: this entity, in THIS issue of the drawing. Cited by provenance.</summary>
        public string Id;
        /// <summary>What the thing IS - no file, no layer. Survives a re-issue; used to spot a relayered entity.</summary>
        public string GeometryId;
        /// <summary>What it is AND which layer it sits on. The identity an incremental run matches by.</summary>
        public string SemanticId;
        public string ProposedKind;                         // rule.Produces
        public string RuleId;
        public string Layer;
        public string Discipline;
        public string Category;
        public string FamilyType;
        public string Level;

        /// <summary>The CAD entities this reading consumed. Provenance starts here.</summary>
        public List<string> SourceSurrogates = new List<string>();

        /// <summary>The geometry to build: a centreline for a wall, a ring for a floor, a point for a symbol.</summary>
        public List<CadPoint> Geometry = new List<CadPoint>();

        /// <summary>
        /// The ARC this candidate is, when it is one. Null for a straight thing,
        /// and null is not "radius zero" - it means the reading is a line.
        ///
        /// Geometry still holds the centreline's two ends, so everything that
        /// only needs endpoints keeps working; this is what lets a curved wall be
        /// built as ONE curved wall rather than as one straight wall per chord.
        /// </summary>
        public CadArcFact Arc;

        /// <summary>Whether the rule that read this said it bears load. Null: the document decides.</summary>
        public bool? Structural;

        /// <summary>Permission the rule gave to cut a load-bearing slab. Never inferred.</summary>
        public bool? AllowStructural;

        /// <summary>
        /// What the requirement set says this is CALLED, and null when it says
        /// nothing. A drawing cannot supply either: text is unreachable from
        /// imported DWG geometry, so a grid bubble is a few arcs and a room tag
        /// is a few more.
        /// </summary>
        public string AssignedName;
        public string AssignedNumber;

        /// <summary>
        /// A SHAFT RUNS BETWEEN TWO LEVELS, and that is what makes it a shaft
        /// rather than a hole. A plan drawing shows only the ring, so both names
        /// come from the rule; neither can be derived from the geometry.
        /// </summary>
        public string BaseLevel;
        public string TopLevel;

        /// <summary>
        /// Parameters the rule writes on this, in the order the rule declared
        /// them. Empty for most candidates, because most rules declare none.
        /// </summary>
        public List<CadParameterWrite> Parameters = new List<CadParameterWrite>();
        /// <summary>What the name was assigned ON, so a reviewer can check it without re-deriving it.</summary>
        public string NamedOn;

        /// <summary>The MEP system this run belongs to, by name. Null when the rule named none.</summary>
        public string SystemType;

        /// <summary>
        /// The rings INSIDE this one: a lift shaft through a slab, a void in a
        /// ceiling. Empty for a solid thing.
        ///
        /// A hole is not a second floor, and reading it as one produces a slab
        /// standing in the opening it was supposed to leave - which looks right in
        /// plan and is wrong in every section. Nesting is decided by containment,
        /// not by drawing order or by which ring came first.
        /// </summary>
        public List<List<CadPoint>> Holes = new List<List<CadPoint>>();

        /// <summary>
        /// A point INSIDE the ring, for the things Revit places by a point rather
        /// than by a profile - a room, a tag. Null when there is no ring.
        ///
        /// The centroid is not good enough: an L-shaped room's centroid is outside
        /// it, and Revit would put the room in the corridor next door.
        /// </summary>
        public CadPoint? InteriorPoint;
        public double? ThicknessMm;
        public double? HeightMm;
        public double? OffsetMm;

        /// <summary>The vertical extent of a hole in a wall. Never derived from the drawing.</summary>
        public double? SillHeightMm;
        public double? HeadHeightMm;
        public double? DiameterMm;
        public double? AreaMm2;

        public List<CadConfidenceFactor> ConfidenceFactors = new List<CadConfidenceFactor>();
        public double Confidence => ConfidenceFactors.Count == 0
            ? 0
            : Math.Max(0, Math.Min(1, ConfidenceFactors.Sum(f => f.Contribution) /
                                      Math.Max(1e-9, ConfidenceFactors.Sum(f => f.Weight))));

        /// <summary>Other readings of the SAME geometry that were defensible. Non-empty means ambiguous.</summary>
        public List<string> Alternatives = new List<string>();

        /// <summary>Things taken as true without evidence - each one a place a reviewer should look.</summary>
        public List<string> Assumptions = new List<string>();

        /// <summary>Facts the drawing does not carry and nothing here invented.</summary>
        public List<string> UnresolvedFacts = new List<string>();

        /// <summary>What must be re-read from the model after the commit for this to count as built.</summary>
        public List<string> ExpectedVerification = new List<string>();

        /// <summary>May an unattended run build this? False whenever a human should look first.</summary>
        public bool EligibleForAutomaticApply;

        /// <summary>Why it is not eligible, when it is not. Empty when it is.</summary>
        public List<string> IneligibleReasons = new List<string>();
    }

    /// <summary>A piece of geometry nobody claimed, or that too many claimed.</summary>
    public sealed class CadUnclaimed
    {
        public string Layer;
        public string Reason;       // no_rule_matched | below_min_confidence | ambiguous | geometry_not_found
        public int EntityCount;
        public List<string> RuleIds = new List<string>();
    }

    /// <summary>The whole reading of one drawing: proposals, refusals and coverage.</summary>
    public sealed class CadInterpretation
    {
        public List<CadCandidate> Candidates = new List<CadCandidate>();
        public List<CadUnclaimed> Unclaimed = new List<CadUnclaimed>();
        /// <summary>layer -> the rules that claimed it. The map a reviewer reads first.</summary>
        public Dictionary<string, List<string>> LayerMap =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public int SegmentsConsidered;
        public int SegmentsConsumed;

        /// <summary>
        /// What each rule's naming pass decided, keyed by rule id. Empty when no
        /// rule asked for names - which is most of them, because only a grid, a
        /// level and a room carry an identity a caller can set.
        /// </summary>
        public Dictionary<string, CadNamingOutcome> Naming =
            new Dictionary<string, CadNamingOutcome>(StringComparer.Ordinal);

        /// <summary>Everything a naming pass refused, flattened for a reader who only wants the verdict.</summary>
        public List<string> NamingProblems = new List<string>();

        /// <summary>How much of the drawing this reading accounts for, by segment count.</summary>
        public double CoverageFraction =>
            SegmentsConsidered == 0 ? 0 : (double)SegmentsConsumed / SegmentsConsidered;

        public IEnumerable<CadCandidate> AutomaticallyApplicable => Candidates.Where(c => c.EligibleForAutomaticApply);
        public IEnumerable<CadCandidate> NeedingReview => Candidates.Where(c => !c.EligibleForAutomaticApply);
    }

    public static class CadInterpretationRules
    {
        /// <summary>
        /// Read the segments through the requirement set.
        ///
        /// <paramref name="sourceHash"/> is the drawing's identity and rides into
        /// every surrogate, so a candidate can always be traced back to the file
        /// it came from and a re-issued drawing never looks like an edit.
        /// </summary>
        public static CadInterpretation Interpret(IList<CadSegment> segments, CadRequirementSet set, string sourceHash)
            => Interpret(segments, set, sourceHash, null);

        /// <summary>
        /// The same reading, with the ARCS the harvest kept as arcs.
        ///
        /// They travel beside the segments rather than inside them because they
        /// are a different kind of fact: a segment is a chord that exists in the
        /// drawing, and an arc is the curve those chords approximate. A rule asks
        /// for one or the other, and passing null here is what a caller with no
        /// arc reading does - it is not the same as a drawing with no arcs in it,
        /// and a rule that wants arcs then produces nothing rather than guessing.
        /// </summary>
        public static CadInterpretation Interpret(IList<CadSegment> segments, CadRequirementSet set,
                                                  string sourceHash, IList<CadArcFact> arcs)
            => Interpret(segments, set, sourceHash, arcs, null);

        /// <summary>
        /// <paramref name="existingNames"/> is what the DOCUMENT already calls
        /// things of these categories. A grid name must be unique, so a set that
        /// asks for one the model already has has to be refused BEFORE anything
        /// is built - Revit refuses it at creation and takes the batch down after
        /// building part of it. Null means the caller could not ask, and the
        /// check is then simply not made rather than assumed to pass.
        /// </summary>
        public static CadInterpretation Interpret(IList<CadSegment> segments, CadRequirementSet set,
                                                  string sourceHash, IList<CadArcFact> arcs,
                                                  IEnumerable<string> existingNames)
        {
            var result = new CadInterpretation();
            if (set == null) throw new ArgumentNullException(nameof(set));
            segments = segments ?? new List<CadSegment>();
            result.SegmentsConsidered = segments.Count;

            // Which rules claim which layers, computed once and published: the
            // layer map is the first thing a reviewer checks, because a rule that
            // matched nothing is nearly always a misspelt pattern.
            var byLayer = segments.GroupBy(s => s.Layer ?? "(no layer)", StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // A LAYER THAT EXISTS ONLY IN THE ARCS STILL EXISTS.
            //
            // This loop is driven by segments because everything except arcs is
            // line work. In practice an arc contributes chords as well, so its
            // layer is already here - but "in practice" is not a guarantee, and a
            // harvest that gave an arc without chords would make an arc rule match
            // nothing while the drawing plainly contains one. The layer is added
            // with an empty segment list; the arc producer does not read segments.
            if (arcs != null)
                foreach (CadArcFact arc in arcs)
                {
                    string arcLayer = arc?.Layer ?? "(no layer)";
                    if (!byLayer.ContainsKey(arcLayer)) byLayer[arcLayer] = new List<CadSegment>();
                }

            var consumed = new HashSet<int>();

            foreach (var group in byLayer)
            {
                string layer = group.Key;
                List<CadRule> claiming = set.RulesFor(layer);
                result.LayerMap[layer] = claiming.Select(r => r.Id).ToList();

                if (claiming.Count == 0)
                {
                    result.Unclaimed.Add(new CadUnclaimed
                    {
                        Layer = layer,
                        Reason = "no_rule_matched",
                        EntityCount = group.Value.Count
                    });
                    continue;
                }

                CadRule winner = claiming[0];
                var tied = claiming.Where(r => r.Precedence == winner.Precedence).ToList();
                bool ambiguousRules = tied.Count > 1;

                List<CadSegment> layerSegments = group.Value;
                var indices = new List<int>();
                for (int i = 0; i < segments.Count; i++)
                    if (string.Equals(segments[i].Layer ?? "(no layer)", layer, StringComparison.OrdinalIgnoreCase))
                        indices.Add(i);

                List<CadCandidate> produced = ProduceFor(winner, set, layerSegments, indices, sourceHash,
                                                         consumed, arcs);

                if (ambiguousRules)
                {
                    string others = string.Join(", ", tied.Skip(1).Select(r => r.Id + " -> " + r.Produces));
                    foreach (CadCandidate c in produced)
                    {
                        c.Alternatives.Add(others);
                        Ineligible(c, "more than one rule claims layer '" + layer + "' at precedence " +
                                      winner.Precedence + ": " + winner.Id + " -> " + winner.Produces +
                                      ", and " + others);
                    }
                }

                foreach (CadCandidate c in produced)
                {
                    if (c.Confidence < winner.MinConfidence)
                        Ineligible(c, "confidence " + c.Confidence.ToString("0.00", CultureInfo.InvariantCulture) +
                                      " is under the " + winner.MinConfidence.ToString("0.00", CultureInfo.InvariantCulture) +
                                      " this rule requires");
                    if (winner.OnAmbiguous == CadAmbiguityPolicy.Reject && c.Alternatives.Count > 0)
                        continue;   // the set said drop these entirely
                    result.Candidates.Add(c);
                }

                if (produced.Count == 0)
                    result.Unclaimed.Add(new CadUnclaimed
                    {
                        Layer = layer,
                        Reason = "geometry_not_found",
                        EntityCount = layerSegments.Count,
                        RuleIds = claiming.Select(r => r.Id).ToList()
                    });
            }

            // NAMES, LAST, AND PER RULE.
            //
            // A name is decided from a whole line-up - "the third grid along x" -
            // so it cannot be settled while candidates are still being produced
            // one layer at a time. And it is per RULE because two rules naming
            // the same way is a coincidence, not a shared sequence: grids on
            // S-GRID numbered 1..4 and grids on S-GRID-SEC numbered 1..3 are two
            // sequences, and merging them would give one of them the wrong names.
            foreach (CadRule rule in set.Rules)
            {
                if (rule?.Naming == null) continue;
                List<CadCandidate> mine = result.Candidates
                    .Where(c => c != null && string.Equals(c.RuleId, rule.Id, StringComparison.Ordinal))
                    .ToList();

                CadNamingOutcome outcome = CadNamingRules.Assign(
                    rule.Naming, mine, set.PointToleranceMm, existingNames);
                result.Naming[rule.Id] = outcome;

                foreach (string problem in outcome.Problems)
                    result.NamingProblems.Add("rule '" + rule.Id + "': " + problem);

                // A REFUSED PASS ASSIGNS NOTHING.
                //
                // Assign() writes its Names even while adding the Problems that
                // make them unusable - an ordered set one value short still names
                // every candidate, just shifted by one. horizun_plan_from_cad is
                // the only caller that ever checked Problems, so the AUDIT and the
                // INCREMENTAL read those shifted names as ground truth and reported
                // that somebody had hand-renamed grids nobody had touched. That is
                // a verification reporting a failure for work that landed, which is
                // the worst answer either of them can give.
                //
                // So the names stop here. The candidates stay - an audit is still
                // entitled to every geometric finding it can make about them - and
                // they carry the reason instead of a name.
                if (outcome.Refused)
                {
                    string why = "rule '" + rule.Id + "' names nothing usable: " +
                                 string.Join(" ", outcome.Problems) +
                                 " No name from this pass can be believed, so none was assigned.";
                    foreach (CadCandidate c in mine) Ineligible(c, why);
                    continue;
                }

                foreach (CadCandidate c in mine)
                {
                    string name;
                    if (outcome.Names.TryGetValue(c.SemanticId ?? "", out name)) c.AssignedName = name;
                    string number;
                    if (outcome.Numbers.TryGetValue(c.SemanticId ?? "", out number)) c.AssignedNumber = number;
                    string why;
                    if (outcome.Evidence.TryGetValue(c.SemanticId ?? "", out why)) c.NamedOn = why;

                    // A CANDIDATE THE NAMING COULD NOT NAME IS NOT AUTOMATIC.
                    //
                    // Building an unnamed grid and letting Revit call it whatever
                    // it likes is the failure this whole path exists to prevent -
                    // it looks finished and every dimension drawn from it cites a
                    // reference nobody chose. review says so; leave_unnamed is the
                    // explicit way to accept it.
                    if (c.AssignedName == null && rule.Naming.OnUnnamed != "leave_unnamed")
                        Ineligible(c, "the requirement set names this rule's output and supplied no name for " +
                                      "this one. An unnamed " + (rule.Produces ?? "element") + " takes whatever " +
                                      "Revit calls it, which is a reference nobody chose.");
                }
            }

            result.SegmentsConsumed = consumed.Count;
            return result;
        }

        private static void Ineligible(CadCandidate c, string reason)
        {
            c.EligibleForAutomaticApply = false;
            if (!c.IneligibleReasons.Contains(reason)) c.IneligibleReasons.Add(reason);
        }

        /// <summary>
        /// Every producer's output, finalised HERE.
        ///
        /// Finalise used to be each producer's own last line, and the fifth
        /// producer - curved walls from arc pairs - did not have it. Nothing
        /// failed: EligibleForAutomaticApply simply stayed at its default of
        /// false, so every curved wall ever read was held back for review at
        /// confidence 1.00 with no reason given, and the plan emitted nothing.
        /// A default that means "not eligible" is right; a producer that can
        /// forget to overrule it is not. So the decision lives on the one path
        /// every candidate takes, and a sixth producer cannot repeat this.
        /// </summary>
        private static List<CadCandidate> ProduceFor(CadRule rule, CadRequirementSet set,
                                                     List<CadSegment> layerSegments, List<int> indices,
                                                     string sourceHash, HashSet<int> consumed,
                                                     IList<CadArcFact> arcs)
        {
            List<CadCandidate> produced = Produce(rule, set, layerSegments, indices, sourceHash, consumed, arcs);
            foreach (CadCandidate c in produced) Finalise(c, rule);
            return produced;
        }

        private static List<CadCandidate> Produce(CadRule rule, CadRequirementSet set,
                                                  List<CadSegment> layerSegments, List<int> indices,
                                                  string sourceHash, HashSet<int> consumed,
                                                  IList<CadArcFact> arcs)
        {
            switch (rule.Geometry.Source)
            {
                case CadGeometrySource.DoubleLines:
                    return BridgeOpenings(rule, set, sourceHash,
                        FromDoubleLines(rule, set, layerSegments, indices, sourceHash, consumed));
                case CadGeometrySource.DoubleArcs: return FromDoubleArcs(rule, set, layerSegments, indices, sourceHash, consumed, arcs);
                case CadGeometrySource.ClosedLoops: return FromLoops(rule, set, layerSegments, indices, sourceHash, consumed);
                case CadGeometrySource.SingleLines: return FromSingleLines(rule, set, layerSegments, indices, sourceHash, consumed);
                case CadGeometrySource.PointClusters: return FromPointClusters(rule, set, layerSegments, indices, sourceHash, consumed);
                default: return new List<CadCandidate>();   // blocks are the harvester's business, not the segments'
            }
        }

        // ---------------------------------------------------------------------
        // Walls from CONCENTRIC ARC pairs
        // ---------------------------------------------------------------------

        /// <summary>
        /// A curved wall is two concentric arcs, exactly as a straight wall is two
        /// parallel lines: same centre, radii a wall thickness apart, and the same
        /// stretch of angle.
        ///
        /// It is a SEPARATE producer rather than a special case inside
        /// FromDoubleLines because the tests are different in kind - concentricity
        /// and angular overlap, not parallelism and linear overlap - and folding
        /// them together would make each one harder to read and neither easier to
        /// get right.
        ///
        /// Chorded arcs are not used here at all. The harvest keeps the real arcs
        /// beside the chords, and this reads those: a wall built from chords is N
        /// straight walls, and no audit can match those back to one drawing entity.
        /// </summary>
        private static List<CadCandidate> FromDoubleArcs(CadRule rule, CadRequirementSet set,
                                                          List<CadSegment> layerSegments, List<int> indices,
                                                          string sourceHash, HashSet<int> consumed,
                                                          IList<CadArcFact> arcs)
        {
            var produced = new List<CadCandidate>();
            CadGeometryCriteria g = rule.Geometry;
            if (arcs == null || arcs.Count == 0) return produced;

            List<CadArcFact> mine = arcs
                .Where(a => a != null && ClaimsLayer(rule, a.Layer))
                .OrderByDescending(a => a.RadiusMm)
                .ToList();
            if (mine.Count < 2) return produced;

            double centreTolerance = Math.Max(set.PointToleranceMm, 1.0);
            double minThickness = g.MinThicknessMm ?? 0;
            double maxThickness = g.MaxThicknessMm ?? double.MaxValue;
            double minOverlapFraction = g.MinOverlapFraction ?? 0.5;

            // EVERY VALID PAIRING FIRST, THEN THE CHOOSING.
            //
            // MEASURED on a DWG this repository exported from Revit 2026: ONE
            // curved wall arrives as SIX concentric arcs, not two. A compound
            // wall draws one arc per material-layer boundary, and those six arcs
            // form several thickness-valid pairs - so a reading that takes the
            // first pair it finds and moves on proposes a SECOND curved wall,
            // built out of the first one's insides. That is where the
            // straight-line reader's own compound-wall defect sat, and the fix
            // is the same shape: choose widest-first, then recognise a pairing
            // that is simply the inside of a wall already taken.
            var pairings = new List<ArcPair>();
            for (int i = 0; i < mine.Count; i++)
                for (int j = i + 1; j < mine.Count; j++)
                {
                    CadArcFact outer = mine[i], inner = mine[j];

                    // CONCENTRIC, or they are not two faces of one wall.
                    if (outer.Centre.PlanDistanceTo(inner.Centre) > centreTolerance) continue;

                    double thickness = Math.Abs(outer.RadiusMm - inner.RadiusMm);
                    if (thickness < minThickness || thickness > maxThickness) continue;

                    // THE SAME STRETCH OF ANGLE. Two arcs round one centre at a
                    // wall's thickness apart are still not a wall if one runs
                    // north and the other south.
                    double overlap = AngularOverlapFraction(outer, inner);
                    if (overlap < minOverlapFraction) continue;

                    pairings.Add(new ArcPair(outer, inner, thickness, overlap));
                }

            var used = new HashSet<string>(StringComparer.Ordinal);
            var chosen = new List<ArcPair>();
            var absorbed = new Dictionary<ArcPair, List<ArcPair>>();
            foreach (ArcPair pair in pairings.OrderByDescending(x => x.ThicknessMm)
                                             .ThenByDescending(x => x.OverlapFraction))
            {
                if (used.Contains(pair.Outer.CurveId) || used.Contains(pair.Inner.CurveId)) continue;
                used.Add(pair.Outer.CurveId);
                used.Add(pair.Inner.CurveId);

                // Is this pairing simply the inside of a wall already taken? Then
                // its two arcs ARE explained - they are that wall's material
                // layers - and a second wall standing in the first one's core is
                // the duplicate this measured.
                ArcPair host = chosen.FirstOrDefault(w =>
                    w.Outer.Centre.PlanDistanceTo(pair.Outer.Centre) <= centreTolerance &&
                    pair.OuterRadius <= w.OuterRadius + centreTolerance &&
                    pair.InnerRadius >= w.InnerRadius - centreTolerance &&
                    AngularOverlapFraction(w.Outer, pair.Outer) >= minOverlapFraction);
                if (host != null)
                {
                    List<ArcPair> inside;
                    if (!absorbed.TryGetValue(host, out inside)) absorbed[host] = inside = new List<ArcPair>();
                    inside.Add(pair);
                    foreach (int k in ChordIndicesOf(layerSegments, indices, pair.Outer.CurveId)) consumed.Add(k);
                    foreach (int k in ChordIndicesOf(layerSegments, indices, pair.Inner.CurveId)) consumed.Add(k);
                    continue;
                }
                chosen.Add(pair);
            }

            // The arcs NO pairing could claim. A compound wall's innermost
            // boundaries can be millimetres apart - below any wall thickness
            // anyone would declare - so nothing pairs them; reporting them as
            // unaccounted-for geometry would send a reviewer hunting for a wall
            // that is already there.
            foreach (CadArcFact stray in mine)
            {
                if (used.Contains(stray.CurveId)) continue;
                ArcPair band = chosen.FirstOrDefault(w =>
                    w.Outer.Centre.PlanDistanceTo(stray.Centre) <= centreTolerance &&
                    stray.RadiusMm <= w.OuterRadius + centreTolerance &&
                    stray.RadiusMm >= w.InnerRadius - centreTolerance &&
                    AngularOverlapFraction(w.Outer, stray) >= minOverlapFraction);
                if (band == null) continue;
                used.Add(stray.CurveId);
                foreach (int k in ChordIndicesOf(layerSegments, indices, stray.CurveId)) consumed.Add(k);
            }

            foreach (ArcPair pair in chosen)
            {
                CadArcFact outer = pair.Outer, best = pair.Inner;
                double bestThickness = pair.ThicknessMm;
                foreach (int i in ChordIndicesOf(layerSegments, indices, outer.CurveId)) consumed.Add(i);
                foreach (int i in ChordIndicesOf(layerSegments, indices, best.CurveId)) consumed.Add(i);

                // The CENTRELINE arc: same centre, mean radius, and the mid-radius
                // points at each end. Built rather than picked, because neither
                // face is the wall's line.
                double meanRadius = (outer.RadiusMm + best.RadiusMm) / 2.0;
                var centreline = new CadArcFact(
                    outer.CurveId + "|centre",
                    outer.Centre, meanRadius,
                    OnCircle(outer.Centre, meanRadius, outer.Start),
                    OnCircle(outer.Centre, meanRadius, outer.End),
                    OnCircle(outer.Centre, meanRadius, outer.Middle),
                    outer.Layer, outer.ChordCount, outer.SagittaMm);

                var c = NewCandidate(rule, set, sourceHash, outer.Layer,
                    new List<CadPoint> { centreline.Start, centreline.End }, CadCurveKind.Arc);
                c.Arc = centreline;
                // THE IDENTITY MUST KNOW IT IS AN ARC. Two arcs can share both
                // ends - a minor and a major arc of one chord, or two radii - and
                // an id taken over the endpoints collides between them, so an
                // audit would match an element to the wrong drawing entity.
                c.GeometryId = CadIdentity.ArcGeometryId(centreline.Centre, centreline.RadiusMm,
                    centreline.Start, centreline.End, centreline.Clockwise, set.PointToleranceMm);
                c.SemanticId = CadIdentity.SemanticIdOf(outer.Layer, "root", c.GeometryId);
                c.Id = CadIdentity.RevisionId(sourceHash, c.SemanticId);
                c.ThicknessMm = rule.ThicknessMm ?? bestThickness;
                c.HeightMm = rule.HeightMm;
                c.OffsetMm = rule.OffsetMm;

                double sweep = centreline.SweepRadians * 180.0 / Math.PI;
                c.ConfidenceFactors.Add(new CadConfidenceFactor("concentricity", 0.35,
                    1 - Math.Min(1, outer.Centre.PlanDistanceTo(best.Centre) / Math.Max(centreTolerance, 1e-6)),
                    outer.Centre.PlanDistanceTo(best.Centre).ToString("0.###", CultureInfo.InvariantCulture) +
                    " mm between the two centres, tolerance " +
                    centreTolerance.ToString("0.##", CultureInfo.InvariantCulture)));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("angular_overlap", 0.35,
                    AngularOverlapFraction(outer, best),
                    (AngularOverlapFraction(outer, best) * 100).ToString("0", CultureInfo.InvariantCulture) +
                    "% of the shorter arc runs alongside the other"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("layer_specificity", 0.20,
                    LayerSpecificity(rule, outer.Layer), "matched by rule '" + rule.Id + "'"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("sweep_plausibility", 0.10,
                    sweep >= 5 ? 1.0 : sweep / 5.0,
                    sweep.ToString("0.#", CultureInfo.InvariantCulture) + " degrees of arc"));

                List<ArcPair> insides;
                if (absorbed.TryGetValue(pair, out insides) && insides.Count > 0)
                    c.Assumptions.Add(insides.Count + " narrower concentric pairing" +
                        (insides.Count == 1 ? " was" : "s were") + " read as this wall's own material " +
                        "layers rather than as " + (insides.Count == 1 ? "a second wall" : "more walls") +
                        " standing in its core: " +
                        string.Join(", ", insides.Select(x => x.ThicknessMm.ToString("0.#",
                            CultureInfo.InvariantCulture) + " mm").ToArray()) +
                        " inside this " + bestThickness.ToString("0.#", CultureInfo.InvariantCulture) +
                        " mm. Thickness bounds tight enough to admit only the faces never reach this.");

                c.Assumptions.Add("read as ONE curved wall of " +
                    bestThickness.ToString("0.#", CultureInfo.InvariantCulture) + " mm, radius " +
                    meanRadius.ToString("0.#", CultureInfo.InvariantCulture) + " mm on the centreline, sweeping " +
                    sweep.ToString("0.#", CultureInfo.InvariantCulture) + " degrees " +
                    (centreline.Clockwise ? "clockwise" : "anticlockwise") + ". The drawing also carries " +
                    outer.ChordCount + " chords per face at a declared " +
                    outer.SagittaMm.ToString("0.##", CultureInfo.InvariantCulture) + " mm; building from THOSE " +
                    "would make one straight wall per chord, and no audit could match them back to this entity.");
                if (rule.HeightMm == null)
                    c.UnresolvedFacts.Add("height: a plan drawing does not carry one, and the rule did not declare one");
                if (rule.Level == null)
                    c.UnresolvedFacts.Add("level: not declared by the rule and not derivable from a 2D drawing");
                c.ExpectedVerification.Add("the created element re-reads as an ARC of radius " +
                    meanRadius.ToString("0.#", CultureInfo.InvariantCulture) + " mm about (" +
                    outer.Centre.X.ToString("0.#", CultureInfo.InvariantCulture) + ", " +
                    outer.Centre.Y.ToString("0.#", CultureInfo.InvariantCulture) + ")");
                produced.Add(c);
            }
            return produced;
        }

        /// <summary>
        /// JOIN THE RUNS A PLAN DRAWING BREAKS AT EVERY OPENING.
        ///
        /// MEASURED: a 12 m wall with a door and a window in it exports as THREE
        /// separate pairs of lines, because a plan section of a building shows
        /// the wall interrupted at each opening. Read literally that is three
        /// walls with gaps - and the door then has nowhere to live, which is how
        /// this was found: horizun_plan_from_cad refused to host a door whose
        /// nearest wall centreline was 500 mm away, correctly, because the wall
        /// it belonged to had been read as two.
        ///
        /// Two runs are ONE wall when they are collinear within the angle
        /// tolerance, offset from each other by less than the point tolerance,
        /// the same thickness, and separated by a gap no wider than the set
        /// declares. Every gap crossed is named in the assumptions, because
        /// joining is a judgement and a reading that made one has to say so.
        /// </summary>
        private static List<CadCandidate> BridgeOpenings(CadRule rule, CadRequirementSet set,
                                                         string sourceHash, List<CadCandidate> walls)
        {
            double? maxGap = rule.Geometry.BridgeOpeningsMm;
            if (maxGap == null || walls == null || walls.Count < 2) return walls;

            var merged = new List<CadCandidate>();
            var taken = new HashSet<CadCandidate>();

            foreach (CadCandidate seed in walls.OrderByDescending(RunLength))
            {
                if (taken.Contains(seed)) continue;
                taken.Add(seed);

                var run = new List<CadCandidate> { seed };
                var gaps = new List<double>();
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (CadCandidate other in walls)
                    {
                        if (taken.Contains(other)) continue;
                        double gap;
                        if (!Continues(run, other, set, maxGap.Value, out gap)) continue;
                        taken.Add(other);
                        run.Add(other);
                        gaps.Add(gap);
                        grew = true;
                    }
                }

                if (run.Count == 1) { merged.Add(seed); continue; }
                merged.Add(Weld(rule, set, sourceHash, run, gaps));
            }
            return merged;
        }

        private static double RunLength(CadCandidate c)
        {
            if (c?.Geometry == null || c.Geometry.Count < 2) return 0;
            return c.Geometry[0].PlanDistanceTo(c.Geometry[c.Geometry.Count - 1]);
        }

        /// <summary>Is this run the same wall as the ones already gathered, across an opening?</summary>
        private static bool Continues(List<CadCandidate> run, CadCandidate other, CadRequirementSet set,
                                      double maxGap, out double gap)
        {
            gap = 0;
            if (other?.Geometry == null || other.Geometry.Count < 2) return false;

            foreach (CadCandidate part in run)
            {
                if (part.Geometry == null || part.Geometry.Count < 2) continue;
                if (!string.Equals(part.Layer, other.Layer, StringComparison.Ordinal)) continue;

                // THE SAME THICKNESS. A 100 mm partition in line with a 350 mm
                // wall is two walls, however neatly they meet.
                if (part.ThicknessMm.HasValue && other.ThicknessMm.HasValue &&
                    Math.Abs(part.ThicknessMm.Value - other.ThicknessMm.Value) > set.PointToleranceMm) continue;

                CadPoint a0 = part.Geometry[0], a1 = part.Geometry[part.Geometry.Count - 1];
                CadPoint b0 = other.Geometry[0], b1 = other.Geometry[other.Geometry.Count - 1];

                CadVector? da = UnitBetween(a0, a1);
                CadVector? db = UnitBetween(b0, b1);
                if (da == null || db == null) continue;
                if (da.Value.UndirectedAngleDegrees(db.Value) > set.AngleToleranceDegrees) continue;

                // COLLINEAR, not merely parallel: two parallel walls a room apart
                // are two walls.
                if (PerpendicularOffset(a0, da.Value, b0) > set.PointToleranceMm) continue;
                if (PerpendicularOffset(a0, da.Value, b1) > set.PointToleranceMm) continue;

                // The nearest ends, and how far apart they are ALONG the line.
                double best = double.MaxValue;
                foreach (CadPoint pa in new[] { a0, a1 })
                    foreach (CadPoint pb in new[] { b0, b1 })
                        best = Math.Min(best, pa.PlanDistanceTo(pb));
                if (best > maxGap) continue;

                // OVERLAPPING runs are not a wall and its opening - they are the
                // same stretch read twice, and welding them would hide that.
                if (Overlaps(a0, a1, b0, b1, da.Value)) continue;

                gap = best;
                return true;
            }
            return false;
        }

        /// <summary>The unit direction from one point to another in plan, or null when they coincide.</summary>
        private static CadVector? UnitBetween(CadPoint a, CadPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 1e-9) return null;
            return new CadVector(dx / len, dy / len);
        }

        private static double PerpendicularOffset(CadPoint origin, CadVector along, CadPoint p)
        {
            double dx = p.X - origin.X, dy = p.Y - origin.Y;
            return Math.Abs(dx * along.Y - dy * along.X);
        }

        private static bool Overlaps(CadPoint a0, CadPoint a1, CadPoint b0, CadPoint b1, CadVector along)
        {
            Func<CadPoint, double> at = p => (p.X - a0.X) * along.X + (p.Y - a0.Y) * along.Y;
            double aLo = Math.Min(at(a0), at(a1)), aHi = Math.Max(at(a0), at(a1));
            double bLo = Math.Min(at(b0), at(b1)), bHi = Math.Max(at(b0), at(b1));
            return Math.Min(aHi, bHi) - Math.Max(aLo, bLo) > 0;
        }

        /// <summary>One wall from a run of collinear pieces: end to end, and it says what it joined.</summary>
        private static CadCandidate Weld(CadRule rule, CadRequirementSet set, string sourceHash,
                                         List<CadCandidate> run, List<double> gaps)
        {
            CadPoint origin = run[0].Geometry[0];
            CadVector along = UnitBetween(origin, run[0].Geometry[run[0].Geometry.Count - 1])
                              ?? new CadVector(1, 0);
            Func<CadPoint, double> at = p => (p.X - origin.X) * along.X + (p.Y - origin.Y) * along.Y;

            CadPoint low = origin, high = origin;
            double lowAt = double.MaxValue, highAt = double.MinValue;
            foreach (CadCandidate part in run)
                foreach (CadPoint p in new[] { part.Geometry[0], part.Geometry[part.Geometry.Count - 1] })
                {
                    double t = at(p);
                    if (t < lowAt) { lowAt = t; low = p; }
                    if (t > highAt) { highAt = t; high = p; }
                }

            var c = NewCandidate(rule, set, sourceHash, run[0].Layer,
                new List<CadPoint> { low, high }, CadCurveKind.Line);
            c.ThicknessMm = run[0].ThicknessMm;
            c.HeightMm = run[0].HeightMm;
            c.OffsetMm = run[0].OffsetMm;

            // The pieces it was made of stay reachable: an audit that matched an
            // element to one of them must still find it.
            foreach (CadCandidate part in run)
                foreach (string surrogate in part.SourceSurrogates)
                    if (!c.SourceSurrogates.Contains(surrogate)) c.SourceSurrogates.Add(surrogate);

            foreach (CadConfidenceFactor f in run[0].ConfidenceFactors) c.ConfidenceFactors.Add(f);
            foreach (CadCandidate part in run)
                foreach (string a in part.Assumptions)
                    if (!c.Assumptions.Contains(a)) c.Assumptions.Add(a);
            foreach (string v in run[0].ExpectedVerification) c.ExpectedVerification.Add(v);
            foreach (CadCandidate part in run)
                foreach (string u in part.UnresolvedFacts)
                    if (!c.UnresolvedFacts.Contains(u)) c.UnresolvedFacts.Add(u);

            c.Assumptions.Add(run.Count + " collinear runs of the same thickness were read as ONE wall " +
                (gaps.Count == 1 ? "across a gap of " : "across gaps of ") +
                string.Join(", ", gaps.OrderBy(x => x)
                    .Select(x => x.ToString("0", CultureInfo.InvariantCulture) + " mm").ToArray()) +
                ", which the rule allows up to " +
                rule.Geometry.BridgeOpeningsMm.Value.ToString("0", CultureInfo.InvariantCulture) +
                " mm. A plan drawing breaks a wall at every door and window; a Revit wall is continuous and " +
                "the opening cuts it. If two of these are really separate walls, lower bridge_openings_mm.");
            return c;
        }

        /// <summary>
        /// Two concentric arcs that could be the two faces of one curved wall,
        /// with what makes the pairing believable. A compound wall offers several
        /// of these and only the widest is the wall.
        /// </summary>
        private sealed class ArcPair
        {
            public readonly CadArcFact Outer;
            public readonly CadArcFact Inner;
            public readonly double ThicknessMm;
            public readonly double OverlapFraction;

            public ArcPair(CadArcFact outer, CadArcFact inner, double thicknessMm, double overlapFraction)
            {
                // Named for what they ARE, not for the order they arrived in.
                if (inner.RadiusMm > outer.RadiusMm) { CadArcFact swap = outer; outer = inner; inner = swap; }
                Outer = outer; Inner = inner;
                ThicknessMm = thicknessMm; OverlapFraction = overlapFraction;
            }

            public double OuterRadius { get { return Outer.RadiusMm; } }
            public double InnerRadius { get { return Inner.RadiusMm; } }
        }

        /// <summary>
        /// Does this rule claim that layer? The same globs the rest of the reading
        /// uses, including the exclusions - an arc rule must not quietly claim a
        /// layer a line rule was told to leave alone.
        /// </summary>
        private static bool ClaimsLayer(CadRule rule, string layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            bool claimed = rule.LayerPatterns.Any(p => CadGlob.IsMatch(layer, p, false));
            if (!claimed) return false;
            return !rule.ExcludeLayerPatterns.Any(p => CadGlob.IsMatch(layer, p, false));
        }

        /// <summary>Which rings are holes, and in which.</summary>
        private sealed class LoopNesting
        {
            public readonly HashSet<CadLoop> IsHole = new HashSet<CadLoop>();
            public readonly Dictionary<CadLoop, List<CadLoop>> HolesOf = new Dictionary<CadLoop, List<CadLoop>>();
        }

        /// <summary>
        /// Nest rings by containment. A ring is a hole in the SMALLEST ring that
        /// contains it, so a void inside a slab inside a podium belongs to the
        /// slab - and a ring nested two deep is an island in the hole, which is a
        /// separate element rather than a hole in a hole.
        /// </summary>
        private static LoopNesting NestLoops(List<CadLoop> loops)
        {
            var nesting = new LoopNesting();
            foreach (CadLoop inner in loops)
            {
                CadLoop smallestContainer = null;
                foreach (CadLoop outer in loops)
                {
                    if (ReferenceEquals(inner, outer)) continue;
                    if (outer.Area <= inner.Area) continue;
                    if (!CadTopologyRules.ContainsPoint(outer.Points, inner.Points[0])) continue;
                    if (smallestContainer == null || outer.Area < smallestContainer.Area) smallestContainer = outer;
                }
                if (smallestContainer == null) continue;

                // Depth: a ring inside a hole is an ISLAND, and an island is its
                // own element. Only odd depth is a hole.
                int depth = 0;
                foreach (CadLoop other in loops)
                    if (!ReferenceEquals(inner, other) && other.Area > inner.Area &&
                        CadTopologyRules.ContainsPoint(other.Points, inner.Points[0])) depth++;
                if (depth % 2 == 0) continue;

                nesting.IsHole.Add(inner);
                List<CadLoop> bucket;
                if (!nesting.HolesOf.TryGetValue(smallestContainer, out bucket))
                    nesting.HolesOf[smallestContainer] = bucket = new List<CadLoop>();
                bucket.Add(inner);
            }
            return nesting;
        }

        /// <summary>
        /// A point genuinely INSIDE the ring and outside every hole.
        ///
        /// The centroid is the obvious answer and it is wrong: an L-shaped room's
        /// centroid is outside it, and a room placed there lands in the corridor.
        /// So the centroid is TRIED, and when it is not inside, the ring is
        /// sampled until a point that is inside is found - and if none is, the
        /// answer is null rather than a guess.
        /// </summary>
        private static CadPoint? InteriorOf(CadLoop ring, List<List<CadPoint>> holes)
        {
            if (ring == null || ring.Points.Count < 3) return null;
            Func<CadPoint, bool> inside = p =>
            {
                if (!CadTopologyRules.ContainsPoint(ring.Points, p)) return false;
                foreach (List<CadPoint> hole in holes)
                    if (CadTopologyRules.ContainsPoint(hole, p)) return false;
                return true;
            };

            double cx = ring.Points.Average(p => p.X), cy = ring.Points.Average(p => p.Y);
            var centroid = new CadPoint(cx, cy, ring.Points[0].Z);
            if (inside(centroid)) return centroid;

            // Sample: the midpoint between each corner and the centroid, then
            // between pairs of corners. Cheap, deterministic, and it finds the
            // inside of every concave shape a floor plan actually contains.
            for (int i = 0; i < ring.Points.Count; i++)
            {
                var toCentre = new CadPoint((ring.Points[i].X + cx) / 2, (ring.Points[i].Y + cy) / 2,
                                            ring.Points[i].Z);
                if (inside(toCentre)) return toCentre;
            }
            for (int i = 0; i < ring.Points.Count; i++)
                for (int j = i + 1; j < ring.Points.Count; j++)
                {
                    var mid = new CadPoint((ring.Points[i].X + ring.Points[j].X) / 2,
                                           (ring.Points[i].Y + ring.Points[j].Y) / 2, ring.Points[i].Z);
                    if (inside(mid)) return mid;
                }
            return null;
        }

        /// <summary>How much of the shorter arc runs alongside the other, as a fraction of its own sweep.</summary>
        private static double AngularOverlapFraction(CadArcFact a, CadArcFact b)
        {
            double a0 = Math.Atan2(a.Start.Y - a.Centre.Y, a.Start.X - a.Centre.X);
            double b0 = Math.Atan2(b.Start.Y - b.Centre.Y, b.Start.X - b.Centre.X);
            double sweepA = a.SweepRadians, sweepB = b.SweepRadians;
            if (sweepA <= 0 || sweepB <= 0) return 0;

            // Both arcs measured from A's start, in A's direction, so the two
            // spans can be intersected on one line.
            double startB = a.Clockwise ? (a0 - b0) : (b0 - a0);
            while (startB < 0) startB += 2 * Math.PI;
            while (startB >= 2 * Math.PI) startB -= 2 * Math.PI;

            double lo = Math.Max(0, startB);
            double hi = Math.Min(sweepA, startB + sweepB);
            double shared = Math.Max(0, hi - lo);
            return shared / Math.Min(sweepA, sweepB);
        }

        /// <summary>The point at the given radius on the ray from the centre through a reference point.</summary>
        private static CadPoint OnCircle(CadPoint centre, double radius, CadPoint through)
        {
            double dx = through.X - centre.X, dy = through.Y - centre.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 1e-9) return new CadPoint(centre.X + radius, centre.Y, through.Z);
            return new CadPoint(centre.X + dx / len * radius, centre.Y + dy / len * radius, through.Z);
        }

        /// <summary>The harvest indices of every chord that came from one curve.</summary>
        private static IEnumerable<int> ChordIndicesOf(List<CadSegment> segments, List<int> indices, string curveId)
        {
            for (int i = 0; i < segments.Count && i < indices.Count; i++)
                if (string.Equals(segments[i].SourceCurveId, curveId, StringComparison.Ordinal))
                    yield return indices[i];
        }

        // ---------------------------------------------------------------------
        // Walls from parallel line pairs
        // ---------------------------------------------------------------------
        private static List<CadCandidate> FromDoubleLines(CadRule rule, CadRequirementSet set,
                                                          List<CadSegment> layerSegments, List<int> indices,
                                                          string sourceHash, HashSet<int> consumed)
        {
            var produced = new List<CadCandidate>();
            CadGeometryCriteria g = rule.Geometry;
            List<CadDoubleLine> pairs = CadTopologyRules.FindDoubleLines(
                layerSegments,
                g.MinThicknessMm.Value, g.MaxThicknessMm.Value,
                set.AngleToleranceDegrees,
                g.MinOverlapMm ?? 0,
                g.MinOverlapFraction ?? 0,
                g.SameLayerOnly);

            // ONE FACE, ONE WALL - and the pairs that lose are REPORTED, not dropped.
            //
            // MEASURED against a DWG this repository exported from Revit: a wall
            // does not arrive as two lines. A compound wall exports one line per
            // MATERIAL LAYER boundary, so a single 352 mm wall came back as four
            // parallel lines - the two outer faces and the two core boundaries -
            // and those four lines form several thickness-valid pairs. The first
            // version silently kept one and discarded the rest, which is the
            // exact behaviour this file refuses everywhere else: a reading that
            // had rivals must say so.
            //
            // The widest pair wins, because the outer faces are what bound a
            // wall and the narrower pairs are its insides. That choice is
            // recorded as an assumption naming the widths it beat, so a reviewer
            // can see that the drawing admitted another reading - and a
            // requirement set whose thickness bounds are tight enough never
            // reaches this code at all, which is the better fix and the one the
            // message points at.
            var usedFaces = new HashSet<int>();
            var rivalsByFace = new Dictionary<int, List<CadDoubleLine>>();
            foreach (CadDoubleLine p in pairs)
            {
                foreach (int face in new[] { p.SegmentIndexA, p.SegmentIndexB })
                {
                    List<CadDoubleLine> bucket;
                    if (!rivalsByFace.TryGetValue(face, out bucket)) rivalsByFace[face] = bucket = new List<CadDoubleLine>();
                    bucket.Add(p);
                }
            }

            // SELECT FIRST, BUILD AFTER. A pairing can only be recognised as the
            // inside of a wall once that wall has been accepted, so the choosing
            // is its own pass and the candidates are built from what survived.
            var chosen = new List<CadDoubleLine>();
            var absorbed = new Dictionary<CadDoubleLine, List<CadDoubleLine>>();
            foreach (CadDoubleLine pair in pairs.OrderByDescending(p => p.ThicknessMm)
                                                .ThenByDescending(p => p.OverlapFraction)
                                                .ThenByDescending(p => p.OverlapLengthMm))
            {
                if (usedFaces.Contains(pair.SegmentIndexA) || usedFaces.Contains(pair.SegmentIndexB)) continue;
                if (g.MinLengthMm != null && pair.LengthMm < g.MinLengthMm.Value) continue;
                if (g.MaxLengthMm != null && pair.LengthMm > g.MaxLengthMm.Value) continue;

                usedFaces.Add(pair.SegmentIndexA);
                usedFaces.Add(pair.SegmentIndexB);
                if (pair.SegmentIndexA < indices.Count) consumed.Add(indices[pair.SegmentIndexA]);
                if (pair.SegmentIndexB < indices.Count) consumed.Add(indices[pair.SegmentIndexB]);

                // Is this pairing simply the inside of a wall already taken? Then
                // its two lines ARE explained - they are that wall's material
                // layers - and proposing a second wall on top of the first is the
                // duplicate the live chain measured.
                CadDoubleLine host = chosen.FirstOrDefault(w =>
                    CadTopologyRules.IsInnerBoundaryOf(pair, w, set.AngleToleranceDegrees, set.PointToleranceMm));
                if (host != null)
                {
                    List<CadDoubleLine> inside;
                    if (!absorbed.TryGetValue(host, out inside)) absorbed[host] = inside = new List<CadDoubleLine>();
                    inside.Add(pair);
                    continue;
                }
                chosen.Add(pair);
            }

            // The lines NO pairing could claim. A compound wall's innermost
            // boundaries are often millimetres apart - the fixture's are 19 mm,
            // below any wall thickness anyone would declare - so nothing pairs
            // them, and reporting them as unaccounted-for geometry would send a
            // reviewer hunting for a wall that is already there.
            var loose = new Dictionary<CadDoubleLine, int>();
            for (int i = 0; i < layerSegments.Count; i++)
            {
                if (usedFaces.Contains(i)) continue;
                foreach (CadDoubleLine w in chosen)
                {
                    if (!CadTopologyRules.IsInsideBandOf(layerSegments[i], w,
                            set.AngleToleranceDegrees, set.PointToleranceMm)) continue;
                    usedFaces.Add(i);
                    if (i < indices.Count) consumed.Add(indices[i]);
                    int n;
                    loose[w] = loose.TryGetValue(w, out n) ? n + 1 : 1;
                    break;
                }
            }

            foreach (CadDoubleLine pair in chosen)
            {
                var c = NewCandidate(rule, set, sourceHash, pair.Layer,
                    new List<CadPoint> { pair.Start, pair.End }, CadCurveKind.Line);
                c.ThicknessMm = rule.ThicknessMm ?? pair.ThicknessMm;
                c.HeightMm = rule.HeightMm;
                c.OffsetMm = rule.OffsetMm;

                c.ConfidenceFactors.Add(new CadConfidenceFactor("face_overlap", 0.40, pair.OverlapFraction,
                    pair.OverlapLengthMm.ToString("0", CultureInfo.InvariantCulture) + " mm of the shorter face runs alongside (" +
                    (pair.OverlapFraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%)"));
                double angleScore = set.AngleToleranceDegrees <= 0 ? 1
                    : 1 - Math.Min(1, pair.AngleDeviationDegrees / set.AngleToleranceDegrees);
                c.ConfidenceFactors.Add(new CadConfidenceFactor("parallelism", 0.25, angleScore,
                    pair.AngleDeviationDegrees.ToString("0.000", CultureInfo.InvariantCulture) + " degrees off parallel, tolerance " +
                    set.AngleToleranceDegrees.ToString("0.##", CultureInfo.InvariantCulture)));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("layer_specificity", 0.20,
                    LayerSpecificity(rule, pair.Layer),
                    "matched by rule '" + rule.Id + "'"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("length_plausibility", 0.15,
                    pair.LengthMm >= 500 ? 1.0 : pair.LengthMm / 500.0,
                    pair.LengthMm.ToString("0", CultureInfo.InvariantCulture) + " mm long"));

                // Every OTHER thickness this pair's faces could have made.
                var rivalWidths = new List<double>();
                foreach (int face in new[] { pair.SegmentIndexA, pair.SegmentIndexB })
                {
                    List<CadDoubleLine> bucket;
                    if (!rivalsByFace.TryGetValue(face, out bucket)) continue;
                    foreach (CadDoubleLine other in bucket)
                        if (!ReferenceEquals(other, pair) &&
                            !rivalWidths.Any(w => Math.Abs(w - other.ThicknessMm) < 0.5))
                            rivalWidths.Add(other.ThicknessMm);
                }
                if (rivalWidths.Count > 0)
                {
                    string widths = string.Join(", ", rivalWidths.OrderByDescending(w => w)
                        .Select(w => w.ToString("0.#", CultureInfo.InvariantCulture) + " mm"));
                    c.Assumptions.Add("these faces also paired at " + widths + "; the widest reading (" +
                        pair.ThicknessMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm) was taken, because " +
                        "the outer faces bound a wall and the narrower pairs are its material layers. Narrow the " +
                        "rule's thickness bounds to remove the choice.");
                    c.Alternatives.Add("the same faces read as a " + widths + " wall");
                }

                List<CadDoubleLine> insideThis;
                absorbed.TryGetValue(pair, out insideThis);
                int looseLines;
                loose.TryGetValue(pair, out looseLines);
                int absorbedLines = (insideThis == null ? 0 : insideThis.Count * 2) + looseLines;
                if (absorbedLines > 0)
                {
                    c.Assumptions.Add(absorbedLines + " further line" + (absorbedLines == 1 ? "" : "s") +
                        " on this layer lie INSIDE this wall's faces and were read as its material-layer " +
                        "boundaries, not as separate walls" +
                        (insideThis != null && insideThis.Count > 0
                            ? " (" + insideThis.Count + " of them formed pairing" + (insideThis.Count == 1 ? "" : "s") +
                              " at " + string.Join(", ", insideThis.OrderByDescending(w => w.ThicknessMm)
                                  .Select(w => w.ThicknessMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm")) + ")"
                            : "") +
                        ". A compound wall exports one line per layer boundary, so this is the expected shape of " +
                        "a Revit-authored DWG - but if the drawing really does show nested walls, put them on " +
                        "separate layers or narrow the rule's thickness bounds.");
                    if (insideThis != null && insideThis.Count > 0)
                        c.Alternatives.Add("the inner pairing" + (insideThis.Count == 1 ? "" : "s") + " (" +
                            string.Join(", ", insideThis.Select(w => w.ThicknessMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm")) +
                            ") read as separate wall" + (insideThis.Count == 1 ? "" : "s") + " within this one");
                }

                if (rule.ThicknessMm != null && Math.Abs(rule.ThicknessMm.Value - pair.ThicknessMm) > 1.0)
                    c.Assumptions.Add("the rule declares " + rule.ThicknessMm.Value.ToString("0.#", CultureInfo.InvariantCulture) +
                        " mm thick, but the drawing measures " + pair.ThicknessMm.ToString("0.#", CultureInfo.InvariantCulture) +
                        " mm; the rule was taken as authoritative");
                if (rule.HeightMm == null)
                    c.UnresolvedFacts.Add("height: a plan drawing does not carry one, and the rule did not declare one");
                if (rule.Level == null)
                    c.UnresolvedFacts.Add("level: not declared by the rule and not derivable from a 2D drawing");

                c.ExpectedVerification.Add("the created element re-reads as category " + (rule.Category ?? "(rule declared none)"));
                c.ExpectedVerification.Add("its location curve matches the centreline within the point tolerance");
                c.ExpectedVerification.Add("its width re-reads as " + (c.ThicknessMm ?? 0).ToString("0.#", CultureInfo.InvariantCulture) + " mm");
                produced.Add(c);
            }
            return produced;
        }

        // ---------------------------------------------------------------------
        // Floors, rooms, ceilings from closed rings
        // ---------------------------------------------------------------------
        private static List<CadCandidate> FromLoops(CadRule rule, CadRequirementSet set,
                                                    List<CadSegment> layerSegments, List<int> indices,
                                                    string sourceHash, HashSet<int> consumed)
        {
            var produced = new List<CadCandidate>();
            List<CadLoop> loops = CadTopologyRules.FindLoops(layerSegments, set.GapToleranceMm, out List<IList<CadPoint>> open);
            CadGeometryCriteria g = rule.Geometry;

            // WHICH RINGS ARE HOLES IN WHICH.
            //
            // A ring entirely inside another is a HOLE - a shaft through a slab, a
            // void in a ceiling - not a second slab. Reading it as one produces a
            // floor standing in the opening it was meant to leave: right in plan,
            // wrong in every section. Decided by containment and by area, never by
            // drawing order.
            var nesting = NestLoops(loops);

            foreach (CadLoop loop in loops)
            {
                if (nesting.IsHole.Contains(loop)) continue;   // reported on its parent
                if (g.MinAreaMm2 != null && loop.Area < g.MinAreaMm2.Value) continue;
                if (g.MaxAreaMm2 != null && loop.Area > g.MaxAreaMm2.Value) continue;
                foreach (int ix in loop.SegmentIndices)
                    if (ix < indices.Count) consumed.Add(indices[ix]);

                CadLoop ccw = loop.AsCounterClockwise();
                var c = NewCandidate(rule, set, sourceHash, loop.Layer, ccw.Points.ToList(), CadCurveKind.Polyline);

                List<CadLoop> holes;
                double holeArea = 0;
                if (nesting.HolesOf.TryGetValue(loop, out holes))
                    foreach (CadLoop hole in holes)
                    {
                        // A hole runs the OTHER way round from its outer ring. Revit
                        // takes the winding as the statement of which is which.
                        c.Holes.Add(hole.AsClockwise().Points.ToList());
                        holeArea += hole.Area;
                        foreach (int ix in hole.SegmentIndices)
                            if (ix < indices.Count) consumed.Add(indices[ix]);
                    }

                // THE AREA THAT WILL EXIST, not the area of the outline. A reader
                // comparing this against the built element would otherwise see a
                // disagreement the size of every hole.
                c.AreaMm2 = loop.Area - holeArea;
                c.InteriorPoint = InteriorOf(ccw, c.Holes);
                if (c.Holes.Count > 0)
                    c.Assumptions.Add(c.Holes.Count + " ring" + (c.Holes.Count == 1 ? " lies" : "s lie") +
                        " entirely inside this one and " + (c.Holes.Count == 1 ? "was" : "were") +
                        " read as " + (c.Holes.Count == 1 ? "a hole" : "holes") + ", not as separate " +
                        (rule.Produces ?? "element") + "s. The area above is the outline's " +
                        (loop.Area / 1_000_000.0).ToString("0.###", CultureInfo.InvariantCulture) +
                        " m2 LESS the " + (holeArea / 1_000_000.0).ToString("0.###", CultureInfo.InvariantCulture) +
                        " m2 they take out.");
                c.OffsetMm = rule.OffsetMm;
                c.ThicknessMm = rule.ThicknessMm;

                double closureScore = set.GapToleranceMm <= 0 ? 1
                    : 1 - Math.Min(1, loop.LargestClosedGapMm / set.GapToleranceMm);
                c.ConfidenceFactors.Add(new CadConfidenceFactor("closure", 0.45, closureScore,
                    loop.LargestClosedGapMm <= 0
                        ? "the ring was already closed"
                        : "the largest gap snapped shut was " + loop.LargestClosedGapMm.ToString("0.#", CultureInfo.InvariantCulture) +
                          " mm, tolerance " + set.GapToleranceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("layer_specificity", 0.25,
                    LayerSpecificity(rule, loop.Layer), "matched by rule '" + rule.Id + "'"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("shape_sanity", 0.30,
                    ccw.Points.Count >= 3 && loop.Area > 0 ? 1.0 : 0.0,
                    ccw.Points.Count + " corners, " + (loop.Area / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " m2"));

                if (loop.LargestClosedGapMm > 0)
                    c.Assumptions.Add("the ring was not closed in the drawing; a gap of " +
                        loop.LargestClosedGapMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm was snapped shut");
                if (rule.Level == null)
                    c.UnresolvedFacts.Add("level: not declared by the rule and not derivable from a 2D drawing");

                c.ExpectedVerification.Add("the created element re-reads as category " + (rule.Category ?? "(rule declared none)"));
                c.ExpectedVerification.Add("its area re-reads within tolerance of " +
                    (loop.Area / 1_000_000.0).ToString("0.###", CultureInfo.InvariantCulture) + " m2");
                produced.Add(c);
            }

            // An open chain on a layer a loop rule claims is exactly the drawing
            // that will not close, and it is reported rather than forced shut.
            foreach (IList<CadPoint> chain in open)
                if (chain.Count >= 3)
                {
                    var c = NewCandidate(rule, set, sourceHash, layerSegments.FirstOrDefault()?.Layer,
                        chain.ToList(), CadCurveKind.Polyline);
                    c.ConfidenceFactors.Add(new CadConfidenceFactor("closure", 0.45, 0.0,
                        "the chain does not close within the declared gap tolerance"));
                    c.ConfidenceFactors.Add(new CadConfidenceFactor("layer_specificity", 0.25,
                        LayerSpecificity(rule, c.Layer), "matched by rule '" + rule.Id + "'"));
                    c.ConfidenceFactors.Add(new CadConfidenceFactor("shape_sanity", 0.30, 0.0,
                        chain.Count + " points, not a ring"));
                    c.UnresolvedFacts.Add("the outline does not close; a " + rule.Produces +
                        " needs a boundary, and inventing one is how a slab ends up over a corridor");
                    Ineligible(c, "the outline is open");
                    produced.Add(c);
                }
            return produced;
        }

        // ---------------------------------------------------------------------
        // Grids, routes, single-line walls
        // ---------------------------------------------------------------------
        private static List<CadCandidate> FromSingleLines(CadRule rule, CadRequirementSet set,
                                                          List<CadSegment> layerSegments, List<int> indices,
                                                          string sourceHash, HashSet<int> consumed)
        {
            var produced = new List<CadCandidate>();
            CadGeometryCriteria g = rule.Geometry;
            List<CadSegment> merged = CadTopologyRules.MergeCollinear(
                layerSegments, set.PointToleranceMm, set.AngleToleranceDegrees, out int mergedAway);

            for (int i = 0; i < merged.Count; i++)
            {
                CadSegment s = merged[i];
                if (g.MinLengthMm != null && s.PlanLength < g.MinLengthMm.Value) continue;
                if (g.MaxLengthMm != null && s.PlanLength > g.MaxLengthMm.Value) continue;
                if (i < indices.Count) consumed.Add(indices[i]);

                var c = NewCandidate(rule, set, sourceHash, s.Layer,
                    new List<CadPoint> { s.A, s.B }, s.SourceKind);
                c.ThicknessMm = rule.ThicknessMm;
                c.HeightMm = rule.HeightMm;
                c.OffsetMm = rule.OffsetMm;

                c.ConfidenceFactors.Add(new CadConfidenceFactor("layer_specificity", 0.50,
                    LayerSpecificity(rule, s.Layer), "matched by rule '" + rule.Id + "'"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("length_plausibility", 0.30,
                    s.PlanLength >= 500 ? 1.0 : s.PlanLength / 500.0,
                    s.PlanLength.ToString("0", CultureInfo.InvariantCulture) + " mm long"));
                c.ConfidenceFactors.Add(new CadConfidenceFactor("single_reading", 0.20, 1.0,
                    "a single line has one reading as a " + rule.Produces + " under this rule"));

                if (rule.DiameterMm == null && (rule.Produces == "pipe" || rule.Produces == "duct" ||
                                                rule.Produces == "conduit" || rule.Produces == "cable_tray"))
                    c.UnresolvedFacts.Add("size: the rule declares no diameter and a line in a drawing does not carry one");
                if (rule.SlopePercent == null && (rule.Produces == "pipe"))
                    c.UnresolvedFacts.Add("slope: not declared; a plan line is horizontal unless something says otherwise");

                c.ExpectedVerification.Add("the created element re-reads as category " + (rule.Category ?? "(rule declared none)"));
                c.ExpectedVerification.Add("its endpoints match the drawn line within the point tolerance");
                produced.Add(c);
            }
            return produced;
        }

        // ---------------------------------------------------------------------
        // Symbols: several marks that mean one thing
        // ---------------------------------------------------------------------
        private static List<CadCandidate> FromPointClusters(CadRule rule, CadRequirementSet set,
                                                            List<CadSegment> layerSegments, List<int> indices,
                                                            string sourceHash, HashSet<int> consumed)
        {
            var produced = new List<CadCandidate>();
            var mids = layerSegments.Select(s => s.Midpoint).ToList();
            List<List<int>> clusters = CadTopologyRules.ClusterPoints(mids, rule.Geometry.ClusterRadiusMm.Value);

            foreach (List<int> cluster in clusters)
            {
                foreach (int ix in cluster) if (ix < indices.Count) consumed.Add(indices[ix]);
                double cx = cluster.Average(ix => mids[ix].X);
                double cy = cluster.Average(ix => mids[ix].Y);
                var centre = new CadPoint(cx, cy, mids[cluster[0]].Z);

                var c = NewCandidate(rule, set, sourceHash, layerSegments[cluster[0]].Layer,
                    new List<CadPoint> { centre }, CadCurveKind.Unknown);
                c.ConfidenceFactors.Add(new CadConfidenceFactor("layer_specificity", 0.50,
                    LayerSpecificity(rule, c.Layer), "matched by rule '" + rule.Id + "'"));
                // A symbol is usually several marks. One lone segment is more
                // likely to be a stray line than a fixture, and that shows here.
                c.ConfidenceFactors.Add(new CadConfidenceFactor("cluster_density", 0.50,
                    Math.Min(1.0, cluster.Count / 4.0),
                    cluster.Count + " drawn element(s) within " +
                    rule.Geometry.ClusterRadiusMm.Value.ToString("0", CultureInfo.InvariantCulture) + " mm"));

                c.UnresolvedFacts.Add("orientation: a cluster of marks does not carry one unless the rule declares it");
                c.ExpectedVerification.Add("the created instance re-reads at the cluster centre within the point tolerance");
                produced.Add(c);
            }
            return produced;
        }

        // ---------------------------------------------------------------------

        private static CadCandidate NewCandidate(CadRule rule, CadRequirementSet set, string sourceHash,
                                                 string layer, List<CadPoint> geometry, CadCurveKind kind)
        {
            var c = new CadCandidate
            {
                ProposedKind = rule.Produces,
                RuleId = rule.Id,
                Layer = layer,
                Discipline = rule.Discipline,
                Category = rule.Category,
                FamilyType = rule.FamilyType,
                Level = rule.Level,
                BaseLevel = rule.BaseLevel,
                TopLevel = rule.TopLevel,
                Parameters = rule.Parameters,
                Structural = rule.Structural,
                AllowStructural = rule.AllowStructural,
                SillHeightMm = rule.SillHeightMm,
                HeadHeightMm = rule.HeadHeightMm,
                SystemType = rule.SystemType,
                DiameterMm = rule.DiameterMm,
                Geometry = geometry
            };
            // Three identities, because one cannot do three jobs. The rule id is
            // NOT part of them: the same drawn line read by a different rule is
            // still the same drawn line, and folding the rule in would make
            // editing a requirement set look like redrawing the building.
            bool closed = kind == CadCurveKind.Polyline && geometry.Count > 2;
            c.GeometryId = CadIdentity.GeometryId(kind, geometry, set.PointToleranceMm, closed);
            c.SemanticId = CadIdentity.SemanticId(layer, "root", kind, geometry, set.PointToleranceMm, closed);
            c.Id = CadIdentity.RevisionId(sourceHash, c.SemanticId);
            c.SourceSurrogates.Add(c.Id);
            return c;
        }

        /// <summary>
        /// How specific was the pattern that matched? An exact layer name is a
        /// deliberate mapping; '*' is a catch-all somebody may not have meant to
        /// apply here. Measured as: the longest matching pattern's non-wildcard
        /// length over the layer's length.
        /// </summary>
        public static double LayerSpecificity(CadRule rule, string layer)
        {
            if (string.IsNullOrEmpty(layer)) return 0;
            double best = 0;
            foreach (string pattern in rule.LayerPatterns)
            {
                if (!CadGlob.IsMatch(layer, pattern, false)) continue;
                int literal = pattern.Count(ch => ch != '*' && ch != '?');
                double score = Math.Min(1.0, (double)literal / layer.Length);
                if (score > best) best = score;
            }
            return best;
        }

        /// <summary>
        /// The last word on a candidate: eligible only when nothing above marked
        /// it otherwise, it clears the rule's threshold, and no fact it needs is
        /// missing. Defaulting to eligible would be exactly backwards.
        /// </summary>
        private static void Finalise(CadCandidate c, CadRule rule)
        {
            bool ok = c.IneligibleReasons.Count == 0 && c.Confidence >= rule.MinConfidence;
            if (!ok && c.IneligibleReasons.Count == 0)
                c.IneligibleReasons.Add("confidence " + c.Confidence.ToString("0.00", CultureInfo.InvariantCulture) +
                                        " is under the " + rule.MinConfidence.ToString("0.00", CultureInfo.InvariantCulture) +
                                        " this rule requires");
            // An unresolved fact is not automatically disqualifying - a wall with
            // no declared height can still be built from a rule that declares one
            // - but a MISSING one the rule needed is. That distinction lives in
            // the rule, so only the explicit reasons gate eligibility here.
            c.EligibleForAutomaticApply = ok;
        }
    }
}
