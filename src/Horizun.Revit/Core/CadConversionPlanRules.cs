// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The plan: candidates become an ORDERED set of typed create_elements actions,
// or they become a reason nobody built them.
//
// This file does the one thing that turns an interpretation into something a
// model can be built from, and it does it WITHOUT touching Revit, so the whole
// argument - what gets built, in what order, and what is deliberately left out -
// is inspectable and testable before any transaction opens.
//
// THREE THINGS IT REFUSES TO PAPER OVER:
//
//   ORDER IS A DEPENDENCY, NOT A PREFERENCE. A door cannot be hosted by a wall
//   that does not exist yet. The plan emits stages, each stage naming what it
//   needs from the one before, so a partial run leaves a model that is missing
//   things rather than a model with things in the wrong place.
//
//   NOTHING INELIGIBLE IS PLANNED. A candidate a reviewer has to look at is
//   listed as deferred, with the reason carried through verbatim. An unattended
//   run therefore builds only what nobody has to argue about, which is the
//   difference between a tool you can leave running and one you must watch.
//
//   THE PLAN NAMES ITS OWN BINDING. Everything that could make the same request
//   mean something different later - the drawing's bytes, the link's transform,
//   the requirement set's hash, the resolved candidate set - is folded into one
//   fingerprint. If any of it moved, the apply is refused rather than aimed at a
//   model that has changed underneath it.
//
// Revit-free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One typed action the plan will ask create_elements to perform.</summary>
    public sealed class CadPlannedAction
    {
        public int Stage;
        public string Kind;                 // the create_elements kind
        public string CandidateId;          // the REVISION id: this entity, in this issue of the drawing
        public string GeometryId;           // what the thing is - survives a re-issue
        public string SemanticId;           // what it is and on which layer - what an incremental run matches by
        public string RuleId;
        public string Layer;
        public JObject Arguments;           // the element entry, ready to send
        public List<string> ExpectedVerification = new List<string>();
        public double Confidence;

        /// <summary>
        /// The drawing entities this row was read from - every line of a wall,
        /// the handle of a block - so a merged reading still names all of them.
        /// </summary>
        public List<string> SourceEntities = new List<string>();

        /// <summary>
        /// WHY THIS ACTION MAY NOT BE BUILT YET, or null when it may.
        ///
        /// A run whose rule declares a slope has no height until a fall walk gives
        /// it one: the drawing is flat, the rule says how steep and not which way,
        /// and building it at the rule's elevation produces a level drain that is
        /// connected, passes every check this bridge has, and drains nowhere.
        ///
        /// It is not a warning. AsCreateRequests LEAVES A BLOCKED ACTION OUT, so
        /// the row never reaches horizun_create_elements - from this process, from
        /// a persisted plan, or from a caller who assembled the JSON by hand.
        /// </summary>
        public string BlockedUntil;

        public bool Blocked { get { return BlockedUntil != null; } }

        /// <summary>
        /// THE STABLE NAME OF THIS ROW, 1-based, assigned when the plan is
        /// emitted and written onto the row as `source_row`.
        ///
        /// It is what lets a row be withdrawn without moving anybody else's
        /// provenance: the index that pairs candidates with created elements is
        /// built by reading this number back off the rows that remain, never by
        /// counting positions.
        /// </summary>
        public int SourceRow;
    }

    /// <summary>A candidate that will NOT be built, and exactly why.</summary>
    public sealed class CadDeferred
    {
        public string CandidateId;
        public string ProposedKind;
        public string RuleId;
        public string Layer;
        public double Confidence;
        public List<string> Reasons = new List<string>();
        public List<string> Alternatives = new List<string>();
        public List<string> UnresolvedFacts = new List<string>();
    }

    /// <summary>The whole plan: what to build, in what order, what is left, and what binds it.</summary>
    public sealed class CadConversionPlan
    {
        public List<CadPlannedAction> Actions = new List<CadPlannedAction>();
        public List<CadDeferred> Deferred = new List<CadDeferred>();
        public Dictionary<string, int> CountsByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> CountsByDiscipline = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<string> Warnings = new List<string>();
        public string PlanFingerprint;
        public double CoverageFraction;
        public int SegmentsConsidered;
        public int SegmentsConsumed;

        public int StageCount => Actions.Count == 0 ? 0 : Actions.Max(a => a.Stage) + 1;
    }

    public static class CadConversionPlanRules
    {
        /// <summary>
        /// Dependency stages. The order is the order a building goes up in, and
        /// each entry exists because the thing after it cannot be placed first.
        /// A kind nobody listed lands in the last stage rather than silently
        /// first, because "unknown" should never jump the queue.
        /// </summary>
        private static readonly Dictionary<string, int> StageOf = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // 0: the frame everything else is measured from
            ["level"] = 0, ["grid"] = 0,
            // 1: hosts
            ["wall"] = 1, ["curtain_wall"] = 1, ["structural_column"] = 1, ["column"] = 1,
            ["foundation"] = 1,
            // 2: horizontal hosts, which need the vertical ones to bound them
            ["floor"] = 2, ["ceiling"] = 2, ["roof"] = 2, ["beam"] = 2, ["brace"] = 2,
            // 3: holes in what now exists
            ["opening"] = 3, ["shaft"] = 3, ["wall_opening"] = 3,
            // 4: things hosted BY a wall or a floor
            ["door"] = 4, ["window"] = 4, ["stair"] = 4, ["railing"] = 4,
            // 5: rooms need their bounding elements to exist first
            ["room_separator"] = 5, ["room"] = 6,
            // 7: services
            ["pipe"] = 7, ["duct"] = 7, ["conduit"] = 7, ["cable_tray"] = 7,
            ["pipe_accessory"] = 8, ["duct_accessory"] = 8, ["air_terminal"] = 8,
            ["plumbing_fixture"] = 8, ["mechanical_equipment"] = 8, ["electrical_fixture"] = 8,
            // 9: loose furniture and anything not otherwise placed
            ["furniture"] = 9, ["generic_model"] = 9
        };

        private const int UnknownStage = 10;

        /// <summary>
        /// The create_elements kind a produced thing maps to. Null means this
        /// bridge has no typed way to build it, which is reported rather than
        /// approximated with something similar.
        /// </summary>
        /// <summary>
        /// What Revit will not place without a host wall. A door or a window
        /// placed free-standing is not a lenient reading of the drawing; it is a
        /// different building, and one that verifies happily.
        /// </summary>
        /// <summary>
        /// Where "structural" means something create_elements can act on. A room
        /// or a grid does not bear load, and passing the flag there would be a
        /// silent no-op that reads like a setting.
        /// </summary>
        /// <summary>
        /// Plan-internal: a run's height above its storey, resolved to absolute Z by
        /// the plan command (which knows the storey) and never sent to create_elements.
        /// </summary>
        public const string OffsetFromLevelKey = "offset_from_level_mm";

        /// <summary>
        /// Resolve a run's declared height above its storey into absolute end Z, in
        /// mm, and remove the plan-internal key. A run whose two ends already differ
        /// in Z carries a fall (or a drawn slope) and is left as it is: the fall is
        /// the more specific statement. Returns true when the row was changed.
        /// </summary>
        public static bool ResolveOffsetFromLevel(JObject row, double levelElevationMm)
        {
            if (row == null || row[OffsetFromLevelKey] == null) return false;
            double offset = row.Value<double>(OffsetFromLevelKey);
            row.Remove(OffsetFromLevelKey);
            var start = row["start"] as JArray;
            var end = row["end"] as JArray;
            if (start == null || end == null || start.Count < 3 || end.Count < 3) return false;
            double zA = start[2].Value<double>(), zB = end[2].Value<double>();
            if (Math.Abs(zA - zB) > 1e-6) return false;
            double z = Math.Round(levelElevationMm + zA + offset, 4, MidpointRounding.AwayFromZero);
            start[2] = z;
            end[2] = z;
            return true;
        }

        /// <summary>The runs Revit carries a system and a bore on.</summary>
        private static readonly HashSet<string> MepKinds =
            new HashSet<string>(StringComparer.Ordinal) { "pipe", "duct", "conduit", "cable_tray" };

        private static readonly HashSet<string> StructuralKinds =
            new HashSet<string>(StringComparer.Ordinal) { "wall", "floor" };

        /// <summary>
        /// Where a name means something create_elements can set. A wall's name IS
        /// its type name and is not a per-instance identity, so emitting one
        /// there would be a key the command quietly drops.
        /// </summary>
        /// <summary>One ring as the array of points every profile-taking kind reads.</summary>
        private static JArray Loop(List<CadPoint> points)
        {
            return new JArray(points.Select(Pt));
        }

        private static double Round(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

        /// <summary>
        /// The two vertices of a ring that are farthest apart.
        ///
        /// For a rectangle at any angle that is a diagonal, and its projection onto
        /// the rectangle's own long axis is exactly the long side - which is what a
        /// hole cut into a wall has to span. The bounding box gives that answer only
        /// when the rectangle happens to sit on the cardinal axes.
        /// </summary>
        private static void LongestDiagonal(List<CadPoint> ring, out CadPoint from, out CadPoint to)
        {
            from = ring[0];
            to = ring.Count > 1 ? ring[1] : ring[0];
            double best = -1;
            for (int i = 0; i < ring.Count; i++)
                for (int j = i + 1; j < ring.Count; j++)
                {
                    double dx = ring[j].X - ring[i].X, dy = ring[j].Y - ring[i].Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 <= best) continue;
                    best = d2; from = ring[i]; to = ring[j];
                }
        }

        /// <summary>
        /// Is this ring a rectangle, at any angle?
        ///
        /// Equal diagonals that bisect each other is exactly a rectangle among
        /// quadrilaterals, and it is the shape Revit's rectangular opening can cut.
        /// Anything else is refused rather than squared off: a hole cut to a
        /// bounding box takes out the corner an L-shaped one leaves solid.
        /// </summary>
        private static bool IsRectangleAtAnyAngle(List<CadPoint> ring, double tolerance)
        {
            var distinct = new List<CadPoint>();
            foreach (CadPoint p in ring)
                if (!distinct.Any(k => Math.Abs(k.X - p.X) <= tolerance && Math.Abs(k.Y - p.Y) <= tolerance))
                    distinct.Add(p);
            if (distinct.Count != 4) return false;

            // Traversal order, so 0-2 and 1-3 are the diagonals.
            double d02 = Distance(distinct[0], distinct[2]);
            double d13 = Distance(distinct[1], distinct[3]);
            if (Math.Abs(d02 - d13) > Math.Max(tolerance, d02 * 0.01)) return false;

            double mx0 = (distinct[0].X + distinct[2].X) / 2, my0 = (distinct[0].Y + distinct[2].Y) / 2;
            double mx1 = (distinct[1].X + distinct[3].X) / 2, my1 = (distinct[1].Y + distinct[3].Y) / 2;
            return Math.Abs(mx0 - mx1) <= tolerance && Math.Abs(my0 - my1) <= tolerance;
        }

        private static double Distance(CadPoint a, CadPoint b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Is this ring the axis-aligned rectangle its bounding box would claim?
        /// Every vertex has to sit on a corner of that box, or the box is bigger
        /// than the thing it is describing.
        /// </summary>
        private static bool IsAxisAlignedRectangle(List<CadPoint> ring, double minX, double maxX,
                                                   double minY, double maxY, double tolerance)
        {
            if (ring.Count < 4) return false;
            if (maxX - minX <= tolerance || maxY - minY <= tolerance) return false;
            var corners = new List<CadPoint>
            {
                new CadPoint(minX, minY), new CadPoint(maxX, minY),
                new CadPoint(maxX, maxY), new CadPoint(minX, maxY)
            };
            // A VERTEX IN THE MIDDLE OF AN EDGE IS STILL A RECTANGLE. A ring is a
            // chain of drawn segments and a drawing splits a side whenever anything
            // touched it - a dimension witness, a trimmed line, an earlier edit. So
            // what disqualifies a point is being off the boundary, not being
            // somewhere other than a corner.
            foreach (CadPoint p in ring)
                if (!OnTheBox(p, minX, maxX, minY, maxY, tolerance))
                    return false;
            // And every corner has to be used, or a triangle inside the box passes.
            foreach (CadPoint k in corners)
                if (!ring.Any(p => Math.Abs(k.X - p.X) <= tolerance && Math.Abs(k.Y - p.Y) <= tolerance))
                    return false;
            return true;
        }

        /// <summary>
        /// Is this ring a circle? Every vertex the same distance from the centre,
        /// and enough of them that it was drawn as one rather than being a square
        /// whose corners happen to be equidistant - which they always are.
        /// </summary>
        /// <summary>On the boundary of the box: on one of its four sides, within it.</summary>
        private static bool OnTheBox(CadPoint p, double minX, double maxX, double minY, double maxY,
                                     double tolerance)
        {
            bool inX = p.X >= minX - tolerance && p.X <= maxX + tolerance;
            bool inY = p.Y >= minY - tolerance && p.Y <= maxY + tolerance;
            if (!inX || !inY) return false;
            return Math.Abs(p.X - minX) <= tolerance || Math.Abs(p.X - maxX) <= tolerance ||
                   Math.Abs(p.Y - minY) <= tolerance || Math.Abs(p.Y - maxY) <= tolerance;
        }

        private static bool IsCircle(List<CadPoint> ring, double cx, double cy, double tolerance)
        {
            if (ring.Count < 8) return false;
            double radius = 0;
            foreach (CadPoint p in ring)
                radius += Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            radius /= ring.Count;
            if (radius <= tolerance) return false;
            foreach (CadPoint p in ring)
            {
                double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
                // A chorded circle sits INSIDE its true radius by the sagitta, so
                // the allowance is the set's own arc tolerance and not a hair.
                if (Math.Abs(d - radius) > Math.Max(tolerance, radius * 0.02)) return false;
            }
            return true;
        }

        private static readonly HashSet<string> NameableKinds =
            new HashSet<string>(StringComparer.Ordinal) { "grid", "level", "room" };

        private static readonly HashSet<string> NeedsWallHost =
            new HashSet<string>(StringComparer.Ordinal) { "door", "window" };

        private static readonly Dictionary<string, string> CreateKindOf = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wall"] = "wall",
            ["floor"] = "floor",
            ["ceiling"] = "ceiling",
            ["roof"] = "roof",
            ["grid"] = "grid",
            ["level"] = "level",
            ["room"] = "room",
            ["room_separator"] = "room_separator",
            // AN OPENING IN A SLAB, and a SHAFT, are different elements with
            // different APIs. A shaft cuts every floor its extent passes through;
            // an opening cuts the one it is hosted in. Reading the second as the
            // first leaves a shaft that stops existing the day somebody adds a
            // storey.
            ["opening"] = "slab_opening",
            // AND A HOLE IN A WALL IS A THIRD THING. It is cut into the ONE wall
            // it is hosted in, between two heights the drawing cannot give, which
            // is why it is its own produces value rather than a flavour of the
            // slab one.
            ["wall_opening"] = "wall_opening",
            ["shaft"] = "shaft",
            ["column"] = "family_instance",
            ["structural_column"] = "structural_column",
            ["beam"] = "structural_framing",
            ["brace"] = "structural_framing",
            ["pipe"] = "pipe",
            ["duct"] = "duct",
            ["conduit"] = "conduit",
            ["cable_tray"] = "cable_tray",
            ["door"] = "family_instance",
            ["window"] = "family_instance",
            ["furniture"] = "family_instance",
            ["generic_model"] = "family_instance",
            ["plumbing_fixture"] = "family_instance",
            ["mechanical_equipment"] = "family_instance",
            ["electrical_fixture"] = "family_instance",
            ["air_terminal"] = "family_instance",
            ["pipe_accessory"] = "family_instance"
        };

        /// <summary>
        /// EVERY create_elements kind a requirement set can end up asking for.
        ///
        /// Published because it is a coupling, not a detail: each of these has to
        /// be re-readable by the command that builds it, and a kind added here
        /// and not there builds happily and verifies as the wrong thing. That
        /// went wrong for both kinds added in one sitting, and the model said so
        /// only because the verification refused to claim what it could not read.
        /// </summary>
        public static IEnumerable<string> CreateKinds => CreateKindOf.Values.Distinct();

        /// <summary>
        /// Turn an interpretation into a plan.
        ///
        /// <paramref name="includeIneligible"/> is the switch between "show me
        /// everything a reviewer could approve" and "build what nobody has to
        /// argue about". It defaults to false everywhere it is called from an
        /// unattended path, and the plan says which it was.
        /// </summary>
        public static CadConversionPlan Plan(CadInterpretation interpretation, CadRequirementSet set,
                                             string sourceFingerprint, bool includeIneligible = false)
        {
            var plan = new CadConversionPlan();
            if (interpretation == null) throw new ArgumentNullException(nameof(interpretation));
            if (set == null) throw new ArgumentNullException(nameof(set));

            plan.SegmentsConsidered = interpretation.SegmentsConsidered;
            plan.SegmentsConsumed = interpretation.SegmentsConsumed;
            plan.CoverageFraction = interpretation.CoverageFraction;

            foreach (CadCandidate c in interpretation.Candidates)
            {
                bool wanted = c.EligibleForAutomaticApply || includeIneligible;
                string createKind;
                bool buildable = CreateKindOf.TryGetValue(c.ProposedKind, out createKind);

                if (!buildable)
                {
                    plan.Deferred.Add(Defer(c, "no typed way to build a '" + c.ProposedKind +
                        "' exists in this bridge; approximating it with a different element would be a " +
                        "different building"));
                    continue;
                }
                if (!wanted)
                {
                    plan.Deferred.Add(Defer(c, null));
                    continue;
                }

                // A MIRROR NOBODY HAS RESOLVED IS NOT A ROW.
                //
                // The set's policy and the block's own measured geometry decide
                // what a reflected insertion means; when they leave it open, the
                // candidate is DEFERRED with that reason rather than built
                // unmirrored. Nine of twenty-one symbols in one apartment are
                // reflected, so this is the difference between a converted unit
                // and a unit with nine devices facing the wrong way.
                if (c.MirrorResolution != null && c.MirrorResolution.StartsWith("pending", StringComparison.Ordinal))
                {
                    plan.Deferred.Add(Defer(c, "this symbol is drawn MIRRORED and the mirror is unresolved (" +
                        c.MirrorResolution + "). " +
                        (c.MirrorEvidence?["means"]?.ToString() ?? "")));
                    continue;
                }

                JObject args = BuildArguments(c, createKind, set);
                if (args == null)
                {
                    plan.Deferred.Add(Defer(c, "the candidate carries no geometry a '" + createKind +
                        "' could be built from"));
                    continue;
                }

                int stage;
                if (!StageOf.TryGetValue(c.ProposedKind, out stage)) stage = UnknownStage;

                plan.Actions.Add(new CadPlannedAction
                {
                    Stage = stage,
                    Kind = createKind,
                    CandidateId = c.Id,
                    GeometryId = c.GeometryId,
                    SemanticId = c.SemanticId,
                    RuleId = c.RuleId,
                    Layer = c.Layer,
                    Arguments = args,
                    ExpectedVerification = c.ExpectedVerification.ToList(),
                    Confidence = c.Confidence,
                    SourceEntities = c.SourceSurrogates.Where(s => !string.Equals(s, c.Id, StringComparison.Ordinal)).ToList(),

                    // A DECLARED SLOPE WITHOUT A FALL IS NOT A FLAT RUN, it is a
                    // run whose height nobody has worked out yet.
                    BlockedUntil = NeedsFall(c, createKind)
                        ? "a fall: this run's rule declares a slope of " +
                          c.SlopePercent.Value.ToString("0.###", CultureInfo.InvariantCulture) +
                          "% and this reading is flat. Name the OUTFALL on this call and the run is " +
                          "planned at the heights the slope implies; until then it is NOT built, because a " +
                          "drain laid level is connected, passes every other check here, and drains nowhere."
                        : null
                });
                Bump(plan.CountsByKind, c.ProposedKind);
                if (!string.IsNullOrEmpty(c.Discipline)) Bump(plan.CountsByDiscipline, c.Discipline);
            }

            plan.Actions = plan.Actions
                .OrderBy(a => a.Stage)
                .ThenByDescending(a => a.Confidence)
                .ThenBy(a => a.CandidateId, StringComparer.Ordinal)
                .ToList();

            // Warnings a reviewer should see BEFORE approving, not after.
            if (plan.CoverageFraction < 0.5 && plan.SegmentsConsidered > 0)
                plan.Warnings.Add("this reading accounts for only " +
                    (plan.CoverageFraction * 100).ToString("0", CultureInfo.InvariantCulture) +
                    "% of the drawn segments; a requirement set that quietly matches a tenth of a drawing " +
                    "looks exactly like one that worked");
            foreach (CadUnclaimed u in interpretation.Unclaimed.Where(x => x.Reason == "no_rule_matched")
                                                               .OrderByDescending(x => x.EntityCount)
                                                               .Take(10))
                plan.Warnings.Add("layer '" + u.Layer + "' carries " + u.EntityCount +
                    " drawn element(s) that no rule claims");
            if (plan.Actions.Count == 0)
                plan.Warnings.Add("nothing is planned: every candidate was deferred, or none was produced");

            plan.PlanFingerprint = Fingerprint(plan, set, sourceFingerprint, includeIneligible);
            return plan;
        }

        /// <summary>
        /// A run that cannot be built until something gives it a height.
        ///
        /// Only where a slope is DECLARED and non-zero: a rule that declares no
        /// slope is saying the run is level, which is a decision, and a declared
        /// zero is a decision too. This is for the third case - a number that says
        /// how steep with nothing yet saying which way.
        /// </summary>
        private static bool NeedsFall(CadCandidate c, string createKind)
        {
            return c != null
                && FallKinds.Contains(createKind ?? "")
                && c.SlopePercent.HasValue
                && Math.Abs(c.SlopePercent.Value) > 1e-9;
        }

        private static CadDeferred Defer(CadCandidate c, string extraReason)
        {
            var d = new CadDeferred
            {
                CandidateId = c.Id,
                ProposedKind = c.ProposedKind,
                RuleId = c.RuleId,
                Layer = c.Layer,
                Confidence = c.Confidence,
                Reasons = c.IneligibleReasons.ToList(),
                Alternatives = c.Alternatives.ToList(),
                UnresolvedFacts = c.UnresolvedFacts.ToList()
            };
            if (extraReason != null) d.Reasons.Add(extraReason);
            if (d.Reasons.Count == 0)
                d.Reasons.Add("this run was asked to build only what needs no review");
            return d;
        }

        /// <summary>
        /// The element entry for create_elements. Units are MILLIMETRES because
        /// every caller of this bridge passes units explicitly and mm is what the
        /// CAD layer normalised to.
        /// </summary>
        private static JObject BuildArguments(CadCandidate c, string createKind, CadRequirementSet set)
        {
            if (c.Geometry == null || c.Geometry.Count == 0) return null;

            var o = new JObject { ["kind"] = createKind };
            switch (createKind)
            {
                case "wall":
                    if (c.Geometry.Count < 2) return null;
                    o["start"] = Pt(c.Geometry[0]);
                    o["end"] = Pt(c.Geometry[c.Geometry.Count - 1]);
                    if (c.HeightMm.HasValue) o["height"] = c.HeightMm.Value;
                    if (c.OffsetMm.HasValue) o["offset"] = c.OffsetMm.Value;
                    // THE TYPE BY THICKNESS, chosen where the model's widths can be
                    // read (PlanFromCadCommand.ResolveWallTypes), from these.
                    CadRule wallRule = set?.Rules.FirstOrDefault(x => x.Id == c.RuleId);
                    if (wallRule?.WallTypes != null && c.ThicknessMm.HasValue)
                    {
                        o["wall_type_choices"] = new JArray(wallRule.WallTypes);
                        o["wall_type_tolerance_mm"] = wallRule.WallTypeToleranceMm ?? set.ThicknessToleranceMm;
                        o["wall_type_otherwise"] = wallRule.WallTypeOtherwise;
                        o["interpreted_thickness_mm"] = Math.Round(c.ThicknessMm.Value, 3);
                    }
                    break;

                case "grid":
                    if (c.Geometry.Count < 2) return null;
                    o["start"] = Pt(c.Geometry[0]);
                    o["end"] = Pt(c.Geometry[c.Geometry.Count - 1]);
                    break;

                case "pipe":
                case "duct":
                case "conduit":
                case "cable_tray":
                case "structural_framing":
                    if (c.Geometry.Count < 2) return null;
                    o["start"] = Pt(c.Geometry[0]);
                    o["end"] = Pt(c.Geometry[c.Geometry.Count - 1]);
                    // THE RULE'S HEIGHT, which never reached the run. offset_mm was
                    // parsed onto the candidate and then dropped here: every duct and
                    // pipe of a flat plan was emitted at the drawing's Z (0), so a
                    // supply main declared 2743 mm above the storey was built IN the
                    // floor (measured, campaign 6, M106). The height is relative to
                    // the storey, and only the plan command knows the storey's
                    // elevation - so it travels as a plan-internal key that
                    // PlanFromCadCommand resolves together with the level and removes
                    // before anything reaches create_elements.
                    if (c.OffsetMm.HasValue && MepKinds.Contains(createKind))
                        o[OffsetFromLevelKey] = c.OffsetMm.Value;
                    break;

                case "shaft":
                    // A SHAFT TAKES ITS PROFILE AND ITS TWO LEVELS. The levels are
                    // resolved by the command that has the document open; here the
                    // rule's declaration travels so that a shaft with only one
                    // level named is refused before anything is built.
                    if (c.Geometry.Count < 3) return null;
                    o["profile"] = new JArray { Loop(c.Geometry) };
                    if (!string.IsNullOrWhiteSpace(c.BaseLevel)) o["base_level_name"] = c.BaseLevel;
                    if (!string.IsNullOrWhiteSpace(c.TopLevel)) o["top_level_name"] = c.TopLevel;
                    // A SHAFT CUTS EVERY SLAB BETWEEN TWO STOREYS, which is more
                    // load-bearing floor than any single opening reaches. It used
                    // to cut all of them with no opt-in at all, while an `opening`
                    // aimed at ONE of those same slabs was refused without one.
                    if (c.AllowStructural == true) o["allow_structural"] = true;
                    break;

                case "slab_opening":
                {
                    // The typed slab opening takes a CENTRE and a size, and a
                    // drawing gives a ring. The comment here used to say that a
                    // rectangle converts exactly and anything else does not - and
                    // then took the bounding box of whatever arrived. A 300 mm
                    // circular penetration became a 300x300 square, 27% more slab
                    // removed than was drawn; an L-shaped riser had the corner that
                    // should stay solid cut out of it. The plan, the row and the
                    // verification all agreed with each other and none of them
                    // agreed with the drawing.
                    //
                    // So the ring is MEASURED before it is described. Revit's own
                    // typed opening takes a rectangle or a circle, so those two are
                    // converted exactly and everything else is refused - which is
                    // what the comment always claimed.
                    if (c.Geometry.Count < 3) return null;
                    double minX = c.Geometry.Min(pt => pt.X), maxX = c.Geometry.Max(pt => pt.X);
                    double minY = c.Geometry.Min(pt => pt.Y), maxY = c.Geometry.Max(pt => pt.Y);
                    double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
                    double tolerance = Math.Max(set?.PointToleranceMm ?? 1.0, 0.5);

                    if (IsAxisAlignedRectangle(c.Geometry, minX, maxX, minY, maxY, tolerance))
                    {
                        o["shape"] = "rectangular";
                        o["width"] = Round(maxX - minX);
                        o["height"] = Round(maxY - minY);
                    }
                    else if (IsCircle(c.Geometry, cx, cy, tolerance))
                    {
                        o["shape"] = "circular";
                        o["diameter"] = Round(((maxX - minX) + (maxY - minY)) / 2);
                    }
                    else
                    {
                        return null;   // reported as deferred, with the reason below
                    }
                    o["center"] = new JArray(Round(cx), Round(cy), Round(c.Geometry[0].Z));
                    o["hosted_on"] = "slab";

                    // THE PERMISSION, and only when it was given. A hole through a
                    // load-bearing floor is an engineering decision; the bridge
                    // refuses it unless the requirement set said so, and an absent
                    // key stays absent rather than becoming a false.
                    if (c.AllowStructural == true) o["allow_structural"] = true;
                    break;
                }

                case "wall_opening":
                {
                    // THE RING'S OWN DIAGONAL, not its bounding box.
                    //
                    // Revit projects the two corners onto the host, and the first
                    // version of this passed the bounding-box diagonal with a
                    // comment claiming that worked "for a wall running in ANY
                    // direction". MEASURED, it does not. For a hole of length L and
                    // width W drawn along a wall at angle t, the bounding-box
                    // diagonal projects onto the wall as L + W*sin(2t) when the
                    // wall rises and L*cos(2t) when it falls: a 1000 mm hole
                    // becomes 1200 mm at +45 degrees, 500 mm at -30, and EXACTLY
                    // ZERO at -45, where that diagonal is perpendicular to the
                    // wall. Only a wall on a cardinal axis gives L - which is the
                    // only geometry the tests and the live fixture had.
                    //
                    // The ring's own longest diagonal projects to exactly L at
                    // every angle, and for an axis-aligned ring it IS the bounding
                    // box diagonal, so nothing about the ordinary case changes.
                    if (c.Geometry.Count < 3) return null;
                    if (!c.SillHeightMm.HasValue || !c.HeadHeightMm.HasValue) return null;

                    double tolerance = Math.Max(set?.PointToleranceMm ?? 1.0, 0.5);
                    if (!IsRectangleAtAnyAngle(c.Geometry, tolerance)) return null;

                    CadPoint fromPt, toPt;
                    LongestDiagonal(c.Geometry, out fromPt, out toPt);
                    o["corner_1"] = new JArray(Round(fromPt.X), Round(fromPt.Y), Round(c.SillHeightMm.Value));
                    o["corner_2"] = new JArray(Round(toPt.X), Round(toPt.Y), Round(c.HeadHeightMm.Value));
                    double loX = c.Geometry.Min(pt => pt.X), hiX = c.Geometry.Max(pt => pt.X);
                    double loY = c.Geometry.Min(pt => pt.Y), hiY = c.Geometry.Max(pt => pt.Y);
                    // AT THE RING'S OWN HEIGHT, not at the sill.
                    //
                    // Which wall a hole belongs to is a question in PLAN, and the
                    // host search measures a 3D distance to the wall's location
                    // curve. Carrying the sill here put the point 900 mm above the
                    // curve, so a ring drawn dead on the wall was refused as
                    // host_too_far by exactly the sill height - a refusal that
                    // named the drawing and was about this line.
                    o["host_point"] = new JArray(Round((loX + hiX) / 2), Round((loY + hiY) / 2),
                                                 Round(c.Geometry[0].Z));
                    o["hosted_on"] = "wall";
                    if (c.AllowStructural == true) o["allow_structural"] = true;
                    break;
                }

                case "room_separator":
                    if (c.Geometry.Count < 2) return null;
                    o["profile"] = new JArray { Loop(c.Geometry) };
                    break;

                case "floor":
                case "ceiling":
                case "roof":
                    if (c.Geometry.Count < 3) return null;
                    // AN ARRAY OF LOOPS: the outer ring first, then every hole.
                    //
                    // horizun_create_elements reads profile as an array OF LOOPS,
                    // each an array of points; this emitted a FLAT array of points,
                    // so every floor, ceiling and roof this plan ever produced was
                    // refused at create time with "point/start/end must contain 3
                    // XYZ coordinates". It failed loudly, which is the only reason
                    // nothing was ever built wrong.
                    var rings = new JArray { new JArray(c.Geometry.Select(Pt)) };
                    foreach (List<CadPoint> hole in c.Holes) rings.Add(new JArray(hole.Select(Pt)));
                    o["profile"] = rings;
                    if (c.OffsetMm.HasValue) o["offset"] = c.OffsetMm.Value;
                    break;

                case "room":
                    // A ROOM IS PLACED BY A POINT, not by a profile.
                    //
                    // This shared the profile arm, and horizun_create_elements reads
                    // item["point"] for a room and ignores profile entirely - so a
                    // room rule refused at create time, every time. The point is the
                    // one the interpretation found INSIDE the ring: an L-shaped
                    // room's centroid is outside it, and a room placed there lands
                    // in the corridor next door.
                    if (c.InteriorPoint == null) return null;
                    o["point"] = new JArray(Round(c.InteriorPoint.Value.X), Round(c.InteriorPoint.Value.Y));
                    break;

                case "family_instance":
                case "structural_column":
                    o["coordinate_mode"] = "absolute";
                    o["point"] = Pt(c.Geometry[0]);
                    // A MOUNTING HEIGHT IS A HEIGHT ABOVE THE LEVEL, declared by the rule and
                    // never read from the plan: the row carries it as a level offset, and the
                    // command verifies the instance's origin at that height.
                    if (createKind == "family_instance" && c.OffsetMm.HasValue)
                    {
                        o["coordinate_mode"] = "level_offset";
                        o["point"] = new JArray(Math.Round(c.Geometry[0].X, 3), Math.Round(c.Geometry[0].Y, 3),
                                                Math.Round(c.OffsetMm.Value, 3));
                    }

                    // A DOOR IS NOT A THING THAT STANDS IN A ROOM.
                    //
                    // Revit hosts a door or a window IN a wall, and an instance
                    // placed without one is a door-shaped object floating beside
                    // the opening it was meant to be. The drawing cannot name the
                    // wall - it has no ids - so the plan says WHAT KIND of host
                    // the element needs, and the command that has the document
                    // open resolves it and records which wall it chose.
                    // A door or a window is hosted by what it IS; anything else is
                    // hosted because the RULE said so - and the rule wins, because a
                    // set that says hosted_on has made a decision this cannot improve on.
                    if (!string.IsNullOrWhiteSpace(c.HostedOn)) o["hosted_on"] = c.HostedOn;
                    if (!string.IsNullOrWhiteSpace(c.HostedOn)) o["face_allowance_mm"] = set.FaceProjectionMm;
                    // WHICH FACE, when the symbol is drawn over the wall: the
                    // declared facing if there is one, the centreline side beyond
                    // the dead band otherwise - never the rotation (CadDeviceSide).
                    if (c.HostedOn == "wall" && (string)o["kind"] == "family_instance")
                    {
                        o["side_dead_band_mm"] = set.PointToleranceMm;
                        if (c.Facing.HasValue)
                            o["facing_degrees"] = Math.Round(Math.Atan2(c.Facing.Value.Y, c.Facing.Value.X) * 180.0 / Math.PI, 6);
                    }
                    CadRule hostRule = set.Rules.FirstOrDefault(x => x.Id == c.RuleId);
                    if (hostRule != null && hostRule.HostLayers.Count > 0 && c.HostedOn == "wall")
                        o["host_layers"] = new JArray(hostRule.HostLayers);
                    if (hostRule?.HostFaces != null && hostRule.HostFaces.Contains("end") && c.HostedOn == "wall")
                        o["host_faces"] = new JArray(hostRule.HostFaces);
                    else if (NeedsWallHost.Contains(c.ProposedKind)) o["hosted_on"] = "wall";

                    // ORIENTATION, WHERE THE DRAWING GAVE ONE.
                    //
                    // A symbol read from a block instance knows how it was placed,
                    // and a receptacle rotated 90 degrees in the drawing that goes
                    // in at zero is on the wrong wall. The drawing's rotation is in
                    // radians; create_elements takes degrees.
                    if (c.RotationRadians.HasValue)
                        o["rotation_degrees"] = Round(c.RotationRadians.Value * 180.0 / Math.PI);

                    // A MIRRORED SYMBOL IS NOT A ROTATED ONE. No angle reproduces a
                    // reflection, and a mirrored fixture placed unmirrored is the
                    // handing reversed - which on a door or a vanity is the whole
                    // point of the symbol.
                    // THE ROW ASKS FOR A REFLECTION ONLY WHEN ONE IS MEANT.
                    //
                    // A symbol whose own geometry is its mirror image gains
                    // nothing from being flipped, and asking for an operation the
                    // family does not support would refuse a row that is already
                    // correct. The resolution decides; "preserve_requested" is
                    // the only one that asks.
                    //
                    // THE DEFAULT IS TO ASK. A caller that never measured the
                    // symbol's symmetry has not decided that the mirror is
                    // meaningless - it has decided nothing - and dropping the
                    // reflection there would be the silent failure this whole
                    // policy exists to prevent. So the row asks for it unless
                    // something MEASURED says it would do nothing, or unless the
                    // set named a different type for the mirrored product.
                    bool mirrorIsANoOp = c.MirrorResolution == "symmetric_by_measurement" ||
                                         c.MirrorResolution == "mirror_has_no_effect" ||
                                         c.MirrorResolution == "variant_type";
                    if (c.Mirrored && !mirrorIsANoOp) o["flip"] = true;
                    break;

                default:
                    return null;
            }

            // THE ARC, when the candidate is one. Everything above still holds:
            // start and end are the centreline's ends, so a reader that only knows
            // about straight things is not broken by this - it simply builds the
            // chord instead of the curve, which is why the arc is a separate block
            // rather than a different shape of start/end.
            if (c.Arc != null)
                o["arc"] = new JObject
                {
                    ["centre"] = Pt(c.Arc.Centre),
                    ["radius"] = Math.Round(c.Arc.RadiusMm, 4, MidpointRounding.AwayFromZero),
                    ["clockwise"] = c.Arc.Clockwise
                };

            // The type and level are the rule's declarations, passed through by
            // NAME. Resolving them to ids is Revit's job and happens in the
            // command, where a name that matches nothing becomes a refusal.
            // WHAT IT IS CALLED, when the requirement set said. Emitted only for
            // the kinds create_elements can actually name, because a key that
            // reaches a command which ignores it is a promise nothing keeps.
            if (NameableKinds.Contains(createKind))
            {
                if (!string.IsNullOrWhiteSpace(c.AssignedName)) o["name"] = c.AssignedName;
                if (!string.IsNullOrWhiteSpace(c.AssignedNumber) && createKind == "room")
                    o["number"] = c.AssignedNumber;
            }

            // THE PARAMETERS THIS RULE WRITES, travelling WITH the row that makes
            // the element so the two cannot drift apart. They are not applied
            // here: horizun_apply_cad_plan hands them to
            // horizun_write_params_verified, which is the one writer in this
            // bridge that coerces, refuses and re-reads. A second writer would be
            // a second set of rules about units.
            if (c.Parameters != null && c.Parameters.Count > 0)
                o["parameters"] = new JArray(c.Parameters.Select(x => x.ToJson()));

            if (!string.IsNullOrWhiteSpace(c.FamilyType)) o["type_name"] = c.FamilyType;

            // LOAD-BEARING OR NOT, when the rule said. Omitted otherwise, so the
            // document's own default stands rather than this quietly deciding.
            if (c.Structural.HasValue && StructuralKinds.Contains(createKind))
                o["structural"] = c.Structural.Value;

            // THE SYSTEM, and the BORE. Revit will not create a pipe or a duct
            // without a system type, and a drawn line carries no width - so both
            // come from the rule or from nowhere, and "nowhere" is a refusal
            // rather than a default that builds somebody a 15 mm main.
            if (!string.IsNullOrWhiteSpace(c.SystemType) && MepKinds.Contains(createKind))
                o["system_type_name"] = c.SystemType;
            if (c.DiameterMm.HasValue && MepKinds.Contains(createKind))
                o["diameter"] = c.DiameterMm.Value;
            // THE SECTION THE DRAWING WROTE for this run, when the rule reads one - width and height
            // travel together to create_elements, which re-reads both and the orientation.
            if (c.SectionWidthMm.HasValue && c.SectionHeightMm.HasValue && createKind == "duct")
            {
                o["width"] = Math.Round(c.SectionWidthMm.Value, 3);
                o["height"] = Math.Round(c.SectionHeightMm.Value, 3);
            }
            // THE JOIN RULE, finally travelling. It has been parsed since this
            // schema existed and read by nothing: a set could say join_rule none
            // and Revit joined anyway, which moved the wall's ends and then failed
            // the postcondition that checked them.
            if (!string.IsNullOrWhiteSpace(c.JoinRule) && createKind == "wall") o["join_rule"] = c.JoinRule;
            if (!string.IsNullOrWhiteSpace(c.Level)) o["level_name"] = c.Level;

            // THE CATEGORY IS NOT WRITTEN ON THE ROW. It used to be, and it was
            // read by nothing: the plan command resolves names and never looks at
            // it, horizun_create_elements refuses it as not applicable, and the
            // type id already decides the category. MEASURED: it refused every
            // row of a real conversion. The candidate still carries it - that is
            // where a reviewer reads what the rule meant.
            return o;
        }

        private static JArray Pt(CadPoint p) => new JArray(
            Math.Round(p.X, 4, MidpointRounding.AwayFromZero),
            Math.Round(p.Y, 4, MidpointRounding.AwayFromZero),
            Math.Round(p.Z, 4, MidpointRounding.AwayFromZero));

        // ====================================================================
        //  THE FALL
        // ====================================================================

        /// <summary>What applying a computed fall to a plan did, and what it would not do.</summary>
        public sealed class CadFallApplication
        {
            /// <summary>Set when NOTHING was applied. The plan is untouched.</summary>
            public string Refusal;

            public int ActionsGivenFall;

            /// <summary>One entry per run that could have taken a fall and did not, with the reason.</summary>
            public List<JObject> NotGivenFall = new List<JObject>();

            /// <summary>
            /// True once any action changed: the plan fingerprint was cleared and
            /// must be computed again before the plan is applied.
            /// </summary>
            public bool NeedsRefingerprinting;

            public bool Ok => Refusal == null;

            public JObject ToJson() => new JObject
            {
                ["applied"] = Ok,
                ["refused"] = Refusal,
                ["actions_given_fall"] = ActionsGivenFall,
                // THE DENOMINATOR, because "3 runs took a fall" reads as success
                // and is the same sentence whether there were 3 or 300.
                ["runs_that_could_have_taken_one"] = ActionsGivenFall + NotGivenFall.Count,
                ["not_given_fall"] = new JArray(NotGivenFall.Take(200).Select(x => (JToken)x)),
                ["not_given_fall_truncated"] = NotGivenFall.Count > 200,
                ["heights_are"] = "CENTRELINES. The fall walk computes INVERTS - inside bottom, which is " +
                                  "what a drainage drawing puts on its nodes - and create_elements places a " +
                                  "run on its centreline, so each end is the invert plus half the declared " +
                                  "bore. A run with no declared bore takes no fall, because there is no way " +
                                  "to make that conversion and half a diameter is 75 mm on a 150 mm drain.",
                ["datum_is"] = "the requirement set's. The outfall invert declared on the call is read in the " +
                               "same reference the set's own elevations use, and every height written here " +
                               "is in that one - nothing converts between datums, because nothing here can " +
                               "tell two datums apart. An outfall invert given in a different reference " +
                               "moves the whole network by the difference, consistently, and every check " +
                               "downstream agrees with it.",
                ["means"] = Ok
                    ? "the planned runs now carry the heights the drawing's slopes imply, oriented by the " +
                      "NETWORK. Runs listed in not_given_fall keep the flat elevation their rule declared."
                    : "NOTHING was changed. The plan still builds every run at the flat elevation its rule " +
                      "declared, which is what it did before this call."
            };
        }

        /// <summary>The kinds a fall can be applied to: the ones create_elements gives two end points and a bore.</summary>
        private static readonly HashSet<string> FallKinds =
            new HashSet<string>(StringComparer.Ordinal) { "pipe", "duct" };

        /// <summary>
        /// Put the inverts a fall walk computed onto the ends of the planned runs.
        ///
        /// The plan and the network must have been read from the SAME geometry with
        /// the SAME point tolerance - that is what makes a run's semantic id the same
        /// string on both sides, and the semantic id is what this matches by. When the
        /// tolerances differ the ids differ, nothing matches, and every run comes back
        /// in not_given_fall rather than quietly keeping a flat elevation.
        /// </summary>
        public static CadFallApplication ApplyFall(CadConversionPlan plan, CadNetwork network,
                                                   CadFall fall, double planToleranceMm)
        {
            var result = new CadFallApplication();
            if (plan == null || network == null || fall == null)
            {
                result.Refusal = "nothing_to_apply: a plan, the network it was read from and a computed fall " +
                                 "are all required.";
                return result;
            }
            if (!fall.Ok)
            {
                result.Refusal = "fall_refused: " + fall.Refusal + " No run was given a height.";
                return result;
            }

            // A NODE AT TWO HEIGHTS APPLIES TO NOTHING.
            //
            // The walk reports a conflict rather than resolving it: the outfall
            // reaches the node two ways and the two paths disagree. Building from it
            // would put one of the two numbers into the model and there is nothing
            // here that can say which - and the result would look finished.
            if (fall.Conflicts.Count > 0)
            {
                result.Refusal = "fall_conflicts: the walk found " + fall.Conflicts.Count + " node(s) that " +
                                 "the outfall reaches two ways at different inverts. A drain cannot be at two " +
                                 "heights, and nothing here can choose. NOTHING was applied - resolve the " +
                                 "loop or the slopes and compute the fall again.";
                return result;
            }

            // Runs by semantic id, and the ids that name more than one run by that
            // name at all - an ambiguous id is not a match, it is two matches.
            var runOf = new Dictionary<string, CadRun>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (CadRun run in network.Runs)
            {
                if (run == null || run.SemanticId == null) continue;
                if (runOf.ContainsKey(run.SemanticId)) ambiguous.Add(run.SemanticId);
                else runOf[run.SemanticId] = run;
            }

            var fallOf = new Dictionary<string, CadRunFall>(StringComparer.Ordinal);
            foreach (CadRunFall f in fall.Runs)
                if (f != null && f.RunId != null) fallOf[f.RunId] = f;

            // WHICH RUNS THE WALK STOPPED AT, AND WHY IT STOPPED.
            //
            // A run whose layer declares no slope and a run the walk could not
            // reach through the network are both "no height", and they are fixed in
            // opposite places: one is a line in the requirement set, the other is a
            // piece of pipework not connected to what it drains into.
            var blockedLayerOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JObject b in fall.Blocked)
            {
                string id = b == null ? null : b.Value<string>("run_id");
                if (id != null && b.Value<string>("reason") == "no_declared_slope")
                    blockedLayerOf[id] = b.Value<string>("layer");
            }

            double tolerance = planToleranceMm > 0 ? planToleranceMm : 1.0;

            foreach (CadPlannedAction a in plan.Actions)
            {
                if (a == null || a.Arguments == null) continue;
                if (!FallKinds.Contains(a.Kind ?? "")) continue;

                var start = a.Arguments["start"] as JArray;
                var end = a.Arguments["end"] as JArray;
                if (start == null || end == null || start.Count < 3 || end.Count < 3)
                {
                    Skipped(result, a, "this action has no two end points to put a height on.");
                    continue;
                }

                if (a.SemanticId == null || ambiguous.Contains(a.SemanticId))
                {
                    Skipped(result, a, a.SemanticId == null
                        ? "this action carries no semantic id, so it cannot be matched to a run of the network."
                        : "more than one run of the network carries this semantic id, so the match is two " +
                          "matches and not one.");
                    continue;
                }

                CadRun matched;
                if (!runOf.TryGetValue(a.SemanticId, out matched))
                {
                    Skipped(result, a, "no run of the network carries this semantic id. The reading that " +
                                       "built the network and the reading that built this plan must use the " +
                                       "same point tolerance, or their ids do not agree.");
                    continue;
                }

                CadRunFall heights;
                if (!fallOf.TryGetValue(matched.Id, out heights))
                {
                    string blockedLayer;
                    Skipped(result, a, blockedLayerOf.TryGetValue(matched.Id, out blockedLayer)
                        ? "the requirement set declares no slope for layer '" +
                          (blockedLayer ?? matched.Layer ?? "(unnamed)") + "', so the fall through this run " +
                          "is unknown and so is every invert beyond it. The walk stopped here rather than " +
                          "assuming the run is level. This one is fixed in the requirement set."
                        : "the fall walk never reached this run through the network. Nothing connects it to " +
                          "the outfall, which is a fact about the drawing rather than about the rules - the " +
                          "walk's own unreachable list names it.");
                    continue;
                }

                // WHICH END IS WHICH, BY POSITION.
                var planStart = new CadPoint(start[0].Value<double>(), start[1].Value<double>(), 0);
                var planEnd = new CadPoint(end[0].Value<double>(), end[1].Value<double>(), 0);
                CadPoint runStart = new CadPoint(matched.Start.X, matched.Start.Y, 0);
                CadPoint runEnd = new CadPoint(matched.End.X, matched.End.Y, 0);

                bool straight = planStart.PlanDistanceTo(runStart) <= tolerance &&
                                planEnd.PlanDistanceTo(runEnd) <= tolerance;
                bool swapped = planStart.PlanDistanceTo(runEnd) <= tolerance &&
                               planEnd.PlanDistanceTo(runStart) <= tolerance;

                if (straight && swapped)
                {
                    Skipped(result, a, "both ends of this run are within tolerance of both ends of the " +
                                       "matching network run, so position cannot say which end is " +
                                       "downstream. A fall applied backwards is a pipe running uphill in a " +
                                       "model that looks finished.");
                    continue;
                }
                if (!straight && !swapped)
                {
                    Skipped(result, a, "the ends of this run are not where the matching network run's ends " +
                                       "are, within " + tolerance.ToString("0.###", CultureInfo.InvariantCulture) +
                                       " mm. The id matched and the geometry did not, which is worth looking at.");
                    continue;
                }

                // AN INVERT IS NOT A CENTRELINE.
                double? bore = a.Arguments.Value<double?>("diameter");
                if (!bore.HasValue || bore.Value <= 0)
                {
                    Skipped(result, a, "this run declares no bore, so the invert the walk computed cannot be " +
                                       "turned into the centreline create_elements places a run on. Half a " +
                                       "diameter is 75 mm on a 150 mm drain - it is not a rounding.");
                    continue;
                }

                // A run that already has two different end heights was given a fall by
                // something else. Two sources of truth for one number, and the later
                // one silently winning, is exactly what this codebase refuses.
                double zA = start[2].Value<double>(), zB = end[2].Value<double>();
                if (Math.Abs(zA - zB) > 1e-6)
                {
                    Skipped(result, a, "this run already has two different end heights, so something has " +
                                       "already given it a fall. It is left as it is rather than overwritten.");
                    continue;
                }

                // THE EXPECTATION WRITTEN FOR A FLAT RUN NO LONGER HOLDS.
                //
                // "its endpoints match the drawn line" is true of a run built at
                // the rule's elevation and false of one carrying a fall - in plan
                // it still holds, in Z it does not, and that is the point of this
                // call. Two statements about one element, one of them wrong, is
                // how a verification list stops being read.
                for (int i = 0; i < a.ExpectedVerification.Count; i++)
                    if (a.ExpectedVerification[i] != null &&
                        a.ExpectedVerification[i].StartsWith("its endpoints match the drawn line",
                                                             StringComparison.Ordinal))
                        a.ExpectedVerification[i] =
                            "its endpoints match the drawn line IN PLAN within the point tolerance. In Z they " +
                            "do NOT match the drawing, which is flat: they carry the fall described below.";

                double half = bore.Value / 2.0;
                double centreAtStart = (straight ? heights.StartZMm : heights.EndZMm) + half;
                double centreAtEnd = (straight ? heights.EndZMm : heights.StartZMm) + half;

                start[2] = Round(centreAtStart);
                end[2] = Round(centreAtEnd);
                // The fall IS this run's height; the rule's flat offset no longer applies.
                a.Arguments.Remove(OffsetFromLevelKey);

                // THE BLOCK IS LIFTED HERE AND NOWHERE ELSE, because here is the
                // only place the two heights were actually written.
                a.BlockedUntil = null;

                double drop = Math.Abs(centreAtStart - centreAtEnd);
                a.ExpectedVerification.Add(
                    "its two ends differ by " + Round(drop).ToString("0.#", CultureInfo.InvariantCulture) +
                    " mm: the fall the drawing's slopes imply, at " +
                    heights.SlopePercent.ToString("0.###", CultureInfo.InvariantCulture) + "%, with the " +
                    (straight ? (heights.Upstream == "start" ? "first" : "second")
                              : (heights.Upstream == "start" ? "second" : "first")) +
                    " point upstream. The heights are CENTRELINES: the computed invert plus half the " +
                    bore.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm bore.");

                result.ActionsGivenFall++;
            }

            if (result.ActionsGivenFall > 0)
            {
                // THE FINGERPRINT NO LONGER DESCRIBES THIS PLAN.
                //
                // It exists so an apply can tell that the plan it was handed is the
                // plan that was reviewed. Leaving the old one on a plan whose runs
                // now build at different heights would defeat exactly that.
                plan.PlanFingerprint = null;
                result.NeedsRefingerprinting = true;
            }

            if (result.NotGivenFall.Count > 0)
                plan.Warnings.Add(result.NotGivenFall.Count + " run(s) that could have taken a fall did not " +
                                  "and will be built at the flat elevation their rule declares. The fall " +
                                  "application block says why, one by one.");

            return result;
        }

        private static void Skipped(CadFallApplication result, CadPlannedAction a, string why)
        {
            result.NotGivenFall.Add(new JObject
            {
                ["candidate_id"] = a.CandidateId,
                ["semantic_id"] = a.SemanticId,
                ["kind"] = a.Kind,
                ["layer"] = a.Layer,
                ["why"] = why,
                ["keeps"] = "the flat elevation its rule declared."
            });
        }

        /// <summary>
        /// What this plan is bound to. Everything that could make the same
        /// request mean something different when it is applied: the drawing, the
        /// rules, the resolved candidates, and whether review was bypassed.
        /// </summary>
        public static string Fingerprint(CadConversionPlan plan, CadRequirementSet set,
                                         string sourceFingerprint, bool includeIneligible)
        {
            var parts = new List<string>
            {
                "src=" + (sourceFingerprint ?? "(none)"),
                "set=" + (set.Id ?? "?") + "@" + (set.Version ?? "?") + "#" + (set.Sha256 ?? "?"),
                "review_bypassed=" + (includeIneligible ? "yes" : "no"),
                "actions=" + plan.Actions.Count
            };
            parts.AddRange(plan.Actions
                .Select(a => a.Stage.ToString(CultureInfo.InvariantCulture) + ":" + a.Kind + ":" +
                             a.CandidateId + ":" + a.Arguments.ToString(Newtonsoft.Json.Formatting.None))
                .OrderBy(x => x, StringComparer.Ordinal));
            return "cadplan:" + CadIdentity.Sha256Hex(string.Join("\n", parts)).Substring(0, 32);
        }

        /// <summary>
        /// THE ACTIONS ARE PART OF THE BINDING, or the binding means nothing.
        ///
        /// The defect this closes: the apply received the binding and the actions
        /// separately, checked the drawing and the rules, and then built whatever
        /// actions arrived. A caller could take a legitimate binding from a real
        /// plan and send different coordinates, a different family type, extra
        /// elements or fewer, and every check passed - because nothing the command
        /// verified covered the thing it was about to build.
        ///
        /// Canonical over property ORDER (a serializer's business) but not over
        /// array order (stage order is a dependency, and element order is what
        /// provenance is keyed by), so a reformatted plan is the same plan and a
        /// reordered one is not.
        /// </summary>
        /// <summary>
        /// The keys that say HOW a call is made rather than WHAT it builds, and
        /// so cannot be inside the fingerprint.
        ///
        /// MEASURED on the live chain, 2026-08-27: the rehearsal issues a
        /// confirmation token, the caller sends it back with the real apply, and
        /// the fingerprint - which covered every byte - then declared the actions
        /// had moved. The two-phase apply this bridge is built around was
        /// impossible to complete. Nothing about the model had changed.
        ///
        /// Excluding them costs nothing, because each is checked harder
        /// elsewhere: the token is verified against the state it was issued
        /// for by the command that issued it, dry_run is the caller's own
        /// choice of whether to write at all, and idempotency_key is compared
        /// against the ledger of what already ran. What the fingerprint is FOR -
        /// coordinates, types, ids, kinds, element order, stage order - is
        /// untouched by this list.
        /// </summary>
        private static readonly HashSet<string> NotWhatIsBuilt = new HashSet<string>(StringComparer.Ordinal)
        {
            "confirmation_token", "dry_run", "idempotency_key"
        };

        public static string ActionsFingerprint(JToken actions)
        {
            var sb = new StringBuilder();
            CanonicalJson(Normalise(actions ?? new JArray()), sb);
            return "cadacts:" + CadIdentity.Sha256Hex(sb.ToString()).Substring(0, 32);
        }

        /// <summary>
        /// Strip the how-it-is-called keys, and ONLY at the two levels where they
        /// mean that: the action itself and its top-level arguments. A key of the
        /// same name deeper in - on an element row - is left alone, because there
        /// it would be part of what is built.
        /// </summary>
        private static JToken Normalise(JToken actions)
        {
            JArray list = actions as JArray;
            if (list == null) return actions;
            var outp = new JArray();
            foreach (JToken item in list)
            {
                JObject action = item as JObject;
                if (action == null) { outp.Add(item); continue; }
                var copy = new JObject();
                foreach (JProperty p in action.Properties())
                {
                    if (NotWhatIsBuilt.Contains(p.Name)) continue;
                    if (p.Name != "arguments" || !(p.Value is JObject args)) { copy[p.Name] = p.Value; continue; }
                    var argCopy = new JObject();
                    foreach (JProperty a in args.Properties())
                        if (!NotWhatIsBuilt.Contains(a.Name)) argCopy[a.Name] = a.Value;
                    copy[p.Name] = argCopy;
                }
                outp.Add(copy);
            }
            return outp;
        }

        private static void CanonicalJson(JToken t, StringBuilder sb)
        {
            if (t == null || t.Type == JTokenType.Null) { sb.Append("null"); return; }
            switch (t.Type)
            {
                case JTokenType.Object:
                    sb.Append('{');
                    foreach (JProperty p in ((JObject)t).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        sb.Append(Newtonsoft.Json.JsonConvert.ToString(p.Name)).Append(':');
                        CanonicalJson(p.Value, sb);
                        sb.Append(',');
                    }
                    sb.Append('}');
                    return;
                case JTokenType.Array:
                    sb.Append('[');
                    foreach (JToken item in (JArray)t) { CanonicalJson(item, sb); sb.Append(','); }
                    sb.Append(']');
                    return;
                case JTokenType.Integer:
                case JTokenType.Float:
                    sb.Append(((double)t).ToString("R", CultureInfo.InvariantCulture));
                    return;
                case JTokenType.Boolean:
                    sb.Append((bool)t ? "true" : "false");
                    return;
                default:
                    sb.Append(Newtonsoft.Json.JsonConvert.ToString(t.ToString()));
                    return;
            }
        }

        /// <summary>
        /// The plan as one create_elements request per stage, ready to send.
        ///
        /// A BLOCKED ACTION IS NOT IN IT. That is the barrier: the row never
        /// reaches the command that would build it, so the block survives being
        /// persisted, re-read, or pasted into a caller's own request - none of
        /// which carries the reasoning, all of which carry the rows.
        /// </summary>
        public static List<JObject> AsCreateRequests(CadConversionPlan plan, string targetDocument, int maxPerBatch = 200)
        {
            var requests = new List<JObject>();
            int emitted = 0;
            foreach (var stage in plan.Actions.Where(a => !a.Blocked).GroupBy(a => a.Stage).OrderBy(g => g.Key))
            {
                List<CadPlannedAction> actions = stage.ToList();
                for (int i = 0; i < actions.Count; i += maxPerBatch)
                {
                    List<CadPlannedAction> batch = actions.Skip(i).Take(maxPerBatch).ToList();

                    // EVERY ROW CARRIES ITS OWN NAME. source_row is part of what
                    // horizun_create_elements already accepts, and it is what the
                    // candidate index is rebuilt from after any exclusion.
                    foreach (CadPlannedAction a in batch)
                    {
                        a.SourceRow = ++emitted;
                        a.Arguments["source_row"] = a.SourceRow;
                    }

                    requests.Add(new JObject
                    {
                        ["target_document"] = targetDocument,
                        ["units"] = "mm",
                        ["stage"] = stage.Key,
                        ["batch_of_stage"] = i / maxPerBatch,
                        ["elements"] = new JArray(batch.Select(a => a.Arguments))
                    });
                }
            }
            return requests;
        }

        private static void Bump(Dictionary<string, int> d, string key)
        {
            int n;
            d[key] = d.TryGetValue(key, out n) ? n + 1 : 1;
        }

        public static JObject ToJson(CadConversionPlan plan, CadRequirementSet set)
        {
            return new JObject
            {
                ["plan_fingerprint"] = plan.PlanFingerprint,
                ["requirement_set"] = set.Stamp(),
                ["stages"] = plan.StageCount,
                ["actions"] = plan.Actions.Count,
                ["deferred"] = plan.Deferred.Count,
                ["counts_by_kind"] = new JObject(plan.CountsByKind.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new JProperty(kv.Key, kv.Value))),
                ["counts_by_discipline"] = new JObject(plan.CountsByDiscipline.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new JProperty(kv.Key, kv.Value))),
                ["coverage"] = new JObject
                {
                    ["segments_considered"] = plan.SegmentsConsidered,
                    ["segments_consumed"] = plan.SegmentsConsumed,
                    ["fraction"] = Math.Round(plan.CoverageFraction, 4),
                    ["means"] = "how much of the DRAWN geometry this reading accounts for. A high action count " +
                                "over a low coverage means the rules matched a corner of the drawing well."
                },
                ["warnings"] = new JArray(plan.Warnings),
                ["blocked"] = plan.Actions.Count(a => a.Blocked),
                ["blocked_detail"] = new JArray(plan.Actions.Where(a => a.Blocked).Take(200)
                    .Select(a => (JToken)new JObject
                    {
                        ["candidate_id"] = a.CandidateId,
                        ["semantic_id"] = a.SemanticId,
                        ["kind"] = a.Kind,
                        ["layer"] = a.Layer,
                        ["waiting_for"] = a.BlockedUntil
                    })),
                ["blocked_means"] = plan.Actions.Any(a => a.Blocked)
                    ? "these actions are PLANNED and are NOT in execute_plan_request: the emitted create " +
                      "requests leave them out, so nothing can build them until what they are waiting for " +
                      "arrives. They are not warnings and they are not deferrals - the reading is settled, " +
                      "the height is not."
                    : "nothing in this plan is waiting on a fact it does not have.",
                ["actions_by_stage"] = new JArray(plan.Actions.GroupBy(a => a.Stage).OrderBy(g => g.Key)
                    .Select(g => new JObject
                    {
                        ["stage"] = g.Key,
                        ["count"] = g.Count(),
                        ["kinds"] = new JArray(g.Select(a => a.Kind).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                    })),
                ["deferred_detail"] = new JArray(plan.Deferred.Take(200).Select(d => new JObject
                {
                    ["candidate_id"] = d.CandidateId,
                    ["proposed_kind"] = d.ProposedKind,
                    ["rule_id"] = d.RuleId,
                    ["layer"] = d.Layer,
                    ["confidence"] = Math.Round(d.Confidence, 3),
                    ["reasons"] = new JArray(d.Reasons),
                    ["alternatives"] = new JArray(d.Alternatives),
                    ["unresolved_facts"] = new JArray(d.UnresolvedFacts)
                })),
                ["deferred_truncated"] = plan.Deferred.Count > 200
            };
        }
    }
}
