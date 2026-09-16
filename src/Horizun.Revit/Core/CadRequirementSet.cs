// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The document that says what a DWG MEANS, and the refusals that keep it honest.
//
// This bridge compiles no organisation's standard into itself. A layer called
// A-WALL-EXTR means an exterior wall in one office and nothing at all in the
// next, so the mapping arrives as a VERSIONED ARTEFACT the caller supplies, is
// validated whole, and stamps its identity onto everything produced from it.
// Three consequences, all deliberate:
//
//   A MALFORMED SET IS REFUSED ENTIRELY. Not "the valid rules ran". A document
//   with a typo in one rule is a document whose author believed something this
//   file cannot confirm, and applying the other nine rules to a model produces
//   a result nobody asked for that looks exactly like one somebody did.
//
//   EVERY RULE DECLARES ITS THRESHOLDS. Wall thickness bounds, overlap
//   fractions, gap tolerances, minimum confidence - none of them have a value
//   chosen in this file, because a number chosen here is a number nobody can
//   argue with in a review.
//
//   THE SET IS HASHED. Anything produced from a requirement set carries the
//   set's id, version and SHA-256, so a model can be asked which rules built it
//   and an incremental run can tell "the drawing changed" from "the rules did".
//
// Revit-free: JSON in, validated structure out, exceptions with reasons.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A requirement set that could not be trusted, and why.</summary>
    public sealed class CadRequirementSetException : Exception
    {
        public CadRequirementSetException(string message) : base(message) { }
    }

    /// <summary>What a rule asks the interpreter to look for in the geometry.</summary>
    public enum CadGeometrySource
    {
        /// <summary>Pairs of parallel lines: the classic wall in plan.</summary>
        DoubleLines,
        /// <summary>
        /// Pairs of CONCENTRIC ARCS: the curved wall. Read from the arcs the
        /// harvest kept as arcs, never from the chords they were also broken
        /// into - a wall built from chords is one straight wall per chord, and no
        /// audit can match those back to the one entity the drawing shows.
        /// </summary>
        DoubleArcs,
        /// <summary>Closed rings: rooms, slabs, shafts, ceilings.</summary>
        ClosedLoops,
        /// <summary>Individual runs: grids, MEP routes, single-line walls.</summary>
        SingleLines,
        /// <summary>Nested instances: blocks placed in the drawing.</summary>
        Blocks,
        /// <summary>Point-like clusters: symbols, fixtures, tags.</summary>
        PointClusters
    }

    /// <summary>What to do when two readings of the same geometry are equally defensible.</summary>
    public enum CadAmbiguityPolicy
    {
        /// <summary>Produce the candidates, mark them, and MODEL NOTHING. The default, and the only one safe unattended.</summary>
        LeaveForReview,
        /// <summary>Drop them entirely; the drawing is treated as not saying this.</summary>
        Reject,
        /// <summary>Take the highest-precedence reading and RECORD that a choice was made.</summary>
        HighestPrecedenceWins
    }

    /// <summary>What to do when the model has been edited by a human since the last run.</summary>
    public enum CadDivergencePolicy
    {
        /// <summary>Never touch it; report it. The default, because somebody's edit is somebody's decision.</summary>
        Preserve,
        /// <summary>Report it and do nothing else - identical to Preserve today, kept distinct so a caller can say which they meant.</summary>
        ReportOnly,
        /// <summary>Overwrite. Requires the caller to say so explicitly, per rule, in writing.</summary>
        Overwrite
    }

    /// <summary>Thresholds a rule declares. Not one of them has a default chosen in this file.</summary>
    /// <summary>
    /// How a rule assigns names and numbers to what it produces.
    ///
    /// Every strategy is EXPLICIT. There is deliberately no "first line is grid
    /// 1" - an implicit order is a decision nobody wrote down, and the direction
    /// it depends on is exactly what a reader would have to guess at later.
    /// </summary>
    public sealed class CadNaming
    {
        /// <summary>ordered | by_semantic_id | by_position</summary>
        public string Strategy;

        // ---- ordered -------------------------------------------------------
        /// <summary>x | y | distance_from_origin - which coordinate orders them.</summary>
        public string Axis;
        /// <summary>ascending | descending.</summary>
        public string Direction;
        /// <summary>The names, in that order. Length is checked against what was read.</summary>
        public List<string> Values = new List<string>();
        /// <summary>Two candidates closer together than this along the axis cannot be ordered.</summary>
        public double? OrderToleranceMm;

        // ---- by_semantic_id ------------------------------------------------
        /// <summary>Semantic id to name. Survives a re-issue of the drawing; a revision id does not.</summary>
        public Dictionary<string, string> BySemanticId =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // ---- by_position ---------------------------------------------------
        /// <summary>Declared coordinates, each with the name that belongs there.</summary>
        public List<CadNamedPosition> ByPosition = new List<CadNamedPosition>();

        /// <summary>
        /// What to do when the reading produces something no strategy names.
        /// refuse (default) | review | leave_unnamed. Never "invent".
        /// </summary>
        public string OnUnnamed = "refuse";

        /// <summary>
        /// What to do when a name is supplied that nothing in the drawing
        /// matches. refuse (default) | report. A name with no candidate usually
        /// means the drawing changed under the set.
        /// </summary>
        public string OnUnmatched = "refuse";

        public JObject ToJson()
        {
            var o = new JObject { ["strategy"] = Strategy };
            if (Axis != null) o["axis"] = Axis;
            if (Direction != null) o["direction"] = Direction;
            if (Values.Count > 0) o["values"] = new JArray(Values);
            if (OrderToleranceMm.HasValue) o["order_tolerance_mm"] = OrderToleranceMm.Value;
            if (BySemanticId.Count > 0)
            {
                var m = new JObject();
                foreach (var kv in BySemanticId.OrderBy(k => k.Key, StringComparer.Ordinal)) m[kv.Key] = kv.Value;
                o["names_by_semantic_id"] = m;
            }
            if (ByPosition.Count > 0)
                o["by_position"] = new JArray(ByPosition.Select(x => x.ToJson()));
            o["on_unnamed"] = OnUnnamed;
            o["on_unmatched"] = OnUnmatched;
            return o;
        }
    }

    /// <summary>One declared coordinate and the name that belongs to whatever is there.</summary>
    public sealed class CadNamedPosition
    {
        public double? X;
        public double? Y;
        public double ToleranceMm;
        public string Name;
        public string Number;

        public JObject ToJson()
        {
            var o = new JObject { ["tolerance_mm"] = ToleranceMm };
            if (X.HasValue) o["x_mm"] = X.Value;
            if (Y.HasValue) o["y_mm"] = Y.Value;
            if (Name != null) o["name"] = Name;
            if (Number != null) o["number"] = Number;
            return o;
        }
    }

    /// <summary>
    /// One parameter a rule writes on what it produces.
    ///
    /// The VALUE is passed to horizun_write_params_verified unchanged, so the
    /// coercion, the unit parsing and the refusals are that command's - there is
    /// one writer in this bridge and this is not a second one.
    /// </summary>
    public sealed class CadParameterWrite
    {
        /// <summary>A BuiltInParameter name, a shared-parameter GUID, or the name as it reads in the UI.</summary>
        public string Parameter;

        /// <summary>String, number, boolean or null - whatever the parameter takes.</summary>
        public JToken Value;

        /// <summary>instance (default) | type. Writing a type changes every instance of it.</summary>
        public string Scope = "instance";

        /// <summary>
        /// When true, a parameter that cannot be written fails the plan BEFORE
        /// anything is created. False lets the conversion proceed and reports the
        /// parameter as not written - which is a legitimate choice for a nice-to-
        /// have, and a terrible one for a fire rating.
        /// </summary>
        public bool Required = true;

        /// <summary>
        /// The block ATTRIBUTE TAG this value is read from, when it is read from
        /// the drawing rather than declared.
        ///
        /// It is how a circuit number or a panel name gets from the symbol the
        /// electrician annotated into the parameter the schedule reads, without
        /// anybody retyping it - and without this bridge inventing a value when
        /// the tag is not there.
        /// </summary>
        public string FromBlockAttribute;

        public JObject ToJson() => new JObject
        {
            ["parameter"] = Parameter,
            ["value"] = Value,
            ["scope"] = Scope,
            ["required"] = Required
        };
    }

    public sealed class CadGeometryCriteria
    {
        public CadGeometrySource Source;
        public double? MinThicknessMm;
        public double? MaxThicknessMm;
        public double? MinOverlapMm;
        public double? MinOverlapFraction;
        public double? MinLengthMm;
        public double? MaxLengthMm;
        public double? MinAreaMm2;
        public double? MaxAreaMm2;
        public double? ClusterRadiusMm;

        /// <summary>
        /// For from:"blocks" - which block DEFINITION names this rule claims, as
        /// globs. Empty means "every block on the layers this rule claims", which
        /// is a legitimate reading for a drawing that keeps one symbol per layer
        /// and a dangerous one for a drawing that does not.
        ///
        /// A block name is the closest thing a DWG has to a statement of what
        /// something IS: its author typed it. Matching on it is how a symbol
        /// becomes a family type without anybody writing a row by hand.
        /// </summary>
        public List<string> BlockPatterns = new List<string>();

        /// <summary>
        /// For from:"blocks" - which way each block's DEVICE faces, in the block's
        /// own frame: "+x", "-x", "+y" or "-y", keyed by block-name glob.
        ///
        /// MEASURED on the electrical plan: a switch's symbol runs from the wall
        /// into the room along its +y, a receptacle's along its +x, and a symbol
        /// drawn over a wall's hatch says which face it belongs to ONLY through
        /// that direction. Nothing in the block says which end is the room - the
        /// switch's far end is the room and the receptacle's far end is too, on
        /// different axes - so it is declared, never inferred. Undeclared, a
        /// symbol inside the wall is placed by the side of the centreline it is
        /// drawn on, or not at all.
        /// </summary>
        public List<KeyValuePair<string, CadVector>> BlockFacing = new List<KeyValuePair<string, CadVector>>();
        public bool SameLayerOnly = true;

        /// <summary>
        /// How wide a break in a run of wall may be READ AS AN OPENING rather
        /// than as the end of one wall and the start of another. Null - the
        /// default - means every break is a break.
        ///
        /// A plan drawing shows a wall INTERRUPTED at every door and window,
        /// because that is what a plan section of a building looks like. A Revit
        /// wall is continuous and the door cuts it. Read literally, a wall with
        /// two openings in it becomes three walls with gaps between them - the
        /// door then has no wall to live in, the walls do not join, and the
        /// count is wrong in every schedule.
        ///
        /// Bridging is opt-in and bounded because it can be wrong: two genuinely
        /// separate walls in line across a corridor look exactly like one wall
        /// with a very wide opening, and only the person who knows the building
        /// can say which. The number IS that judgement, written down.
        /// </summary>
        public double? BridgeOpeningsMm;
    }

    /// <summary>One mapping: WHICH layers, WHAT they become, and on what evidence.</summary>
    /// <summary>
    /// THE PART OF THE DRAWING THIS SET IS ABOUT, in millimetres, in the
    /// drawing's own coordinates.
    ///
    /// A layer is drawing-wide and a floor plan holds every apartment on the
    /// storey, so without this a set for receptacles claims all of them. The
    /// bound is on the INTERPRETATION, never on the reading: the drawing is
    /// still read whole, so coverage, the layer map and every "no rule matched"
    /// verdict still describe the real file rather than a window onto it.
    /// </summary>
    public sealed class CadExtent
    {
        public const string CrossingExclude = "exclude";
        public const string CrossingWhole = "whole";

        public double MinX, MinY, MaxX, MaxY;

        /// <summary>
        /// WHAT HAPPENS TO AN ENTITY THE ZONE CUTS: "exclude" (the default) or
        /// "whole".
        ///
        /// Excluding is right for a run that leaves the apartment - half a conduit
        /// ends in mid air. It is wrong for the walls that BOUND the apartment:
        /// a corridor wall or a party wall is drawn as lines that run past the
        /// zone on both sides, so excluding them removes exactly the walls the
        /// unit's devices are mounted on (MEASURED: six of eight unhosted devices
        /// in one apartment). "whole" keeps such an entity entire - it is not
        /// clipped, because a wall cut at an invisible line is a wall nobody drew.
        /// The set declares it; the zone itself does not change.
        /// </summary>
        public string Crossing = CrossingExclude;

        public bool Contains(CadPoint p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

        /// <summary>
        /// Does the segment reach the zone at all - an end inside it, or passing
        /// straight through? A line through the zone with both ends outside it
        /// crosses it; it is not "outside".
        /// </summary>
        public bool Touches(CadPoint a, CadPoint b)
        {
            if (Contains(a) || Contains(b)) return true;
            // Liang-Barsky against the box, in plan.
            double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { a.X - MinX, MaxX - a.X, a.Y - MinY, MaxY - a.Y };
            for (int i = 0; i < 4; i++)
            {
                if (p[i] == 0) { if (q[i] < 0) return false; continue; }
                double r = q[i] / p[i];
                if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            return t0 <= t1;
        }

        public string Describe() =>
            MinX.ToString("0.#", CultureInfo.InvariantCulture) + "," +
            MinY.ToString("0.#", CultureInfo.InvariantCulture) + " to " +
            MaxX.ToString("0.#", CultureInfo.InvariantCulture) + "," +
            MaxY.ToString("0.#", CultureInfo.InvariantCulture) + " mm" +
            (Crossing == CrossingWhole ? ", entities it cuts kept whole" : "");
    }

    public sealed class CadRule
    {
        public string Id;
        public int Precedence;                       // higher wins; ties are an ambiguity, not a coin toss
        public string Discipline;                    // free text, the caller's own vocabulary
        public List<string> LayerPatterns = new List<string>();
        public List<string> ExcludeLayerPatterns = new List<string>();
        public string Produces;                      // the closed vocabulary below
        public string Category;                      // OST_* - resolved against Revit later, never invented here
        public string FamilyType;                    // "Family: Type", or a system-type name
        public string Level;
        public string Phase;
        public string DesignOption;
        public CadGeometryCriteria Geometry = new CadGeometryCriteria();
        public double? HeightMm;

        /// <summary>
        /// HOW HIGH A HOLE IN A WALL STARTS AND STOPS, in millimetres above the
        /// storey.
        ///
        /// A plan drawing shows where an opening is along a wall and says NOTHING
        /// about its height - the section does, and until this bridge reads one,
        /// the requirement set is the only place those two numbers can come from.
        /// Both are required for a wall opening and neither is defaulted: a hole
        /// at the wrong height is invisible in the plan it was drawn on, and
        /// would be found by somebody standing in the building.
        /// </summary>
        public double? SillHeightMm;
        public double? HeadHeightMm;
        public double? OffsetMm;
        public double? ThicknessMm;                  // when the rule DECLARES it rather than measuring it
        public double? DiameterMm;
        public double? SlopePercent;
        public string JoinRule;                      // none | auto | butt - passed through, applied by the writer

        /// <summary>
        /// WHAT THIS RULE'S SYMBOLS ARE MOUNTED ON: "wall", "slab", or null for
        /// an element that stands on its own.
        ///
        /// A door is hosted because of what a door IS; a receptacle is hosted
        /// because somebody decided this symbol means a wall outlet rather than a
        /// floor box, and the drawing shows both as a mark. So it is the set's
        /// statement, and the host is still resolved against the model per
        /// instance: the drawing has no element ids to name one with.
        /// </summary>
        public string HostedOn;

        /// <summary>
        /// The layers this rule's HOSTS are drawn on, as globs. Optional. When
        /// given, a symbol whose nearest line on these layers is not one of its
        /// chosen host's faces is withdrawn: the drawing shows it against a wall
        /// the model does not have.
        /// </summary>
        public List<string> HostLayers = new List<string>();

        /// <summary>
        /// WHAT A MIRRORED INSERTION MEANS FOR THIS RULE'S SYMBOLS:
        /// preserve (the default), symmetric, variant, or pending.
        ///
        /// A drawing reflects a block with a negative scale factor, and whether
        /// that is a fact about the device or about the draughtsman is not
        /// something geometry can decide on its own - so the set decides, and
        /// "symmetric" is checked against the block's own geometry before it is
        /// believed.
        /// </summary>
        public string Mirror;

        /// <summary>For mirror = variant: the family type a mirrored symbol means instead.</summary>
        public string MirrorVariantType;

        /// <summary>
        /// Whether what this rule produces bears load.
        ///
        /// It is not a label. A structural wall and an architectural one of the
        /// same thickness are different elements to every analytical model, every
        /// structural schedule and every rule about who may move them - and a
        /// drawing distinguishes them by LAYER, which is exactly what a rule is
        /// for. Null means the document's own default, which is what a rule that
        /// does not know should say.
        /// </summary>
        public bool? Structural;

        /// <summary>
        /// PERMISSION TO CUT A STRUCTURAL SLAB, which is not a reading of the
        /// drawing but a decision somebody made.
        ///
        /// A hole through a load-bearing floor is an engineering decision, and
        /// the bridge refuses to make it: an opening rule aimed at a structural
        /// slab is refused unless the set says this word. It is deliberately NOT
        /// the same key as `structural` - that one declares what an element IS,
        /// this one declares what a person accepts - and reading one as the other
        /// would let a rule that describes a slab quietly authorise cutting it.
        /// </summary>
        public bool? AllowStructural;

        /// <summary>
        /// WHERE NAMES COME FROM, since a drawing cannot supply them.
        ///
        /// MEASURED: no string is reachable from imported DWG geometry at any
        /// depth. Text arrives as curves on its own layer - the layer name
        /// survives and the words do not - so a grid bubble reading "A" is, to
        /// this bridge, a few arcs. A grid named by guessing is worse than a
        /// grid not named: every dimension drawn from it then cites the wrong
        /// reference, and nothing in the model says so.
        ///
        /// Null means the rule asks for no name, which is different from asking
        /// for an empty one.
        /// </summary>
        public CadNaming Naming;

        /// <summary>
        /// The two levels a shaft runs between. A plan drawing shows one ring and
        /// says nothing about height, so both are the set's statement - and a
        /// shaft with only one of them named is refused rather than given a
        /// default, because a shaft that stops at the wrong storey looks correct
        /// in plan.
        /// </summary>
        public string BaseLevel;
        public string TopLevel;

        /// <summary>
        /// The MEP system a run belongs to, by name - "Domestic Cold Water",
        /// "Supply Air".
        ///
        /// Revit will not create a pipe or a duct without one, and it is not a
        /// label either: the system decides what connects to what, what the run
        /// is called in every schedule, and what a clash between two of them
        /// means. A drawing distinguishes systems by layer, which is what a rule
        /// is for; nothing here can be guessed from geometry.
        /// </summary>
        public string SystemType;
        public double MinConfidence;
        public CadAmbiguityPolicy OnAmbiguous = CadAmbiguityPolicy.LeaveForReview;
        public CadDivergencePolicy OnManualDivergence = CadDivergencePolicy.Preserve;
        /// <summary>
        /// PARAMETERS THE RULE WRITES ONTO WHAT IT PRODUCES.
        ///
        /// This was parsed and then read by nothing for the whole life of the
        /// schema: a set could declare a fire rating on every wall it produced
        /// and the walls came out blank, with no error and no note. A key a
        /// loader accepts and a builder ignores is worse than one it refuses,
        /// because the refusal is visible on the first run and the silence is
        /// visible on none.
        /// </summary>
        public List<CadParameterWrite> Parameters = new List<CadParameterWrite>();

        /// <summary>Does this rule claim this layer? Excludes beat includes, always.</summary>
        public bool Matches(string layer, bool caseSensitive)
        {
            if (layer == null) return false;
            foreach (string ex in ExcludeLayerPatterns)
                if (CadGlob.IsMatch(layer, ex, caseSensitive)) return false;
            foreach (string inc in LayerPatterns)
                if (CadGlob.IsMatch(layer, inc, caseSensitive)) return true;
            return false;
        }
    }

    /// <summary>
    /// Layer patterns, as globs rather than regular expressions.
    ///
    /// A regex in a config file written by a draughtsman is a support ticket:
    /// a stray '(' is a crash and '.' silently matches everything. Globs are
    /// what CAD standards are written in anyway - A-WALL-*, *-DEMO - and the
    /// only two metacharacters are the two everyone already knows.
    /// </summary>
    public static class CadGlob
    {
        public static bool IsMatch(string text, string pattern, bool caseSensitive)
        {
            if (text == null || pattern == null) return false;
            if (!caseSensitive) { text = text.ToUpperInvariant(); pattern = pattern.ToUpperInvariant(); }
            return Match(text, 0, pattern, 0);
        }

        private static bool Match(string s, int si, string p, int pi)
        {
            while (pi < p.Length)
            {
                char pc = p[pi];
                if (pc == '*')
                {
                    // Collapse runs of '*' - "**" says nothing "*" does not.
                    while (pi < p.Length && p[pi] == '*') pi++;
                    if (pi == p.Length) return true;
                    for (int skip = si; skip <= s.Length; skip++)
                        if (Match(s, skip, p, pi)) return true;
                    return false;
                }
                if (si >= s.Length) return false;
                if (pc != '?' && pc != s[si]) return false;
                si++; pi++;
            }
            return si == s.Length;
        }
    }

    /// <summary>The whole artefact: header, tolerances, rules, and the hash that names it.</summary>
    public sealed class CadRequirementSet
    {
        public const string SchemaName = "horizun.cad-requirements/1";

        public string Id;
        public string Version;
        public string Title;
        /// <summary>The unit the DRAWING is in, declared. Never inferred from the size of the numbers.</summary>
        public string SourceUnits;
        public double SourceUnitsToMm;
        public bool CaseSensitiveLayers;

        /// <summary>
        /// WHICH VIEW THIS DRAWING IS - floor_plan, section, elevation and the
        /// rest. Declared, never guessed from a file name: "A-101-SECTION.dwg" is
        /// a string somebody typed, and reading meaning out of it would carry one
        /// office's naming convention into everybody else's project.
        /// </summary>
        public string SourceRole = CadSourceRole.Default;

        /// <summary>True when nothing said, so the reply can say the default was used.</summary>
        public bool SourceRoleWasDeclared;

        public double PointToleranceMm;
        public double GapToleranceMm;
        public double AngleToleranceDegrees;
        public double ArcSagittaMm;

        public List<CadRule> Rules = new List<CadRule>();

        /// <summary>SHA-256 over the canonical form. Stamped on everything produced.</summary>
        public string Sha256;

        /// <summary>
        /// The zone this set converts, or null for the whole drawing.
        ///
        /// It is part of the set, so it is part of the set's hash, so a plan made
        /// for one apartment cannot be applied as if it were the floor: the
        /// fingerprint an apply re-measures changes with it.
        /// </summary>
        public CadExtent ExtentMm;

        /// <summary>
        /// How far a reflected symbol may move and still be the same symbol, in mm.
        ///
        /// Its own number, small by default. MEASURED: borrowing point_mm
        /// accredited a symbol as symmetric whose reflection moved its geometry
        /// 152 mm, because point_mm had been raised to 250 for a different job -
        /// finding the wall a device is drawn beside. A receptacle symbol is
        /// barely 300 mm across, so 250 mm of slack accredits anything.
        /// </summary>
        public double SymmetryToleranceMm = 1.0;

        /// <summary>
        /// How far from a wall a symbol may be drawn and still belong to it.
        ///
        /// A SEARCH tolerance, not an acceptance one: it decides which wall a
        /// device is hosted on and says nothing about whether the built element
        /// ended up where it was asked for. Defaults to point_mm, which is what
        /// it used to borrow.
        /// </summary>
        public double HostSearchMm;

        /// <summary>
        /// How far a symbol may sit from the FACE it is placed on, once its host
        /// is known. The point is projected onto the face and the distance is
        /// reported; beyond this the row refuses rather than reaching.
        /// </summary>
        public double FaceProjectionMm = 300.0;

        /// <summary>
        /// How far a built element may be from where THIS revision of the drawing
        /// puts it before an audit calls it moved. Defaults to point_mm.
        /// </summary>
        public double RevisionCompareMm;

        /// <summary>
        /// How far two wall readings' SOLIDS may overlap and still be two walls.
        /// Drafting leaves touching walls a hair into each other; anything more is
        /// one wall read twice, or a pairing that crossed from one wall into the
        /// next. Not point_mm: that is how close two endpoints are, and at 25 mm
        /// it would call a 10 mm overlap "touching".
        /// </summary>
        public double WallOverlapMm = 1.0;

        /// <summary>The closed vocabulary of things a rule may produce. A typo here is a refusal.</summary>
        public static readonly string[] KnownProduces =
        {
            "wall", "curtain_wall", "floor", "ceiling", "roof", "room", "room_separator",
            "column", "structural_column", "beam", "brace", "foundation", "grid", "level",
            "door", "window", "opening", "wall_opening", "shaft", "stair", "railing",
            "furniture", "generic_model",
            "pipe", "duct", "conduit", "cable_tray", "pipe_accessory", "duct_accessory",
            "air_terminal", "plumbing_fixture", "mechanical_equipment", "electrical_fixture"
        };

        private static readonly HashSet<string> TopLevelKeys = new HashSet<string>(StringComparer.Ordinal)
        { "schema", "requirement_set", "source", "tolerances", "rules" };

        /// <summary>
        /// The source block had no allowlist, so a misspelt key was silently
        /// ignored - and "roles" or "Role" instead of "role" meant a SECTION read
        /// as a floor plan, which is the whole failure that key exists to prevent.
        /// </summary>
        private static readonly HashSet<string> SourceKeys = new HashSet<string>(StringComparer.Ordinal)
        { "units", "case_sensitive_layers", "role", "extent_mm" };

        private static readonly HashSet<string> RuleKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "precedence", "discipline", "layers", "exclude_layers", "produces", "category", "hosted_on",
            "host_layers",
            "mirror", "mirror_variant_type",
            "family_type", "system_type", "level", "base_level", "top_level", "phase",
            "design_option", "geometry",
            "height_mm", "offset_mm", "sill_height_mm", "head_height_mm",
            "thickness_mm", "diameter_mm", "slope_percent", "join_rule", "min_confidence", "structural",
            "allow_structural",
            "naming",
            "on_ambiguous", "on_manual_divergence", "parameters"
        };

        private static readonly HashSet<string> GeometryKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "from", "min_thickness_mm", "max_thickness_mm", "min_overlap_mm", "min_overlap_fraction",
            "min_length_mm", "max_length_mm", "min_area_mm2", "max_area_mm2", "cluster_radius_mm",
            "same_layer_only", "bridge_openings_mm", "blocks", "block_facing"
        };

        /// <summary>
        /// Parse and validate, or refuse the whole document.
        ///
        /// Every refusal here exists because the silent alternative produces a
        /// model: a rule that matches nothing because its layer pattern was
        /// misspelt, a wall with no thickness bounds that pairs a facade with a
        /// door swing, a set with no units whose 200 means 200 metres.
        /// </summary>
        public static CadRequirementSet Load(JObject doc)
        {
            if (doc == null) throw new CadRequirementSetException("The requirement set is empty.");

            foreach (JProperty prop in doc.Properties())
                if (!TopLevelKeys.Contains(prop.Name))
                    throw new CadRequirementSetException(
                        "Unknown top-level key '" + prop.Name + "'. Known: " +
                        string.Join(", ", TopLevelKeys.OrderBy(x => x, StringComparer.Ordinal)) +
                        ". A misspelt section is a section that silently does not run.");

            string schema = doc.Value<string>("schema");
            if (!string.Equals(schema, SchemaName, StringComparison.Ordinal))
                throw new CadRequirementSetException(
                    "schema must be '" + SchemaName + "'; this document says '" + (schema ?? "(absent)") +
                    "'. The schema name is how a set written for a different version of this bridge is " +
                    "refused instead of half-understood.");

            var set = new CadRequirementSet();

            JObject header = doc["requirement_set"] as JObject;
            if (header == null)
                throw new CadRequirementSetException("requirement_set (id, version, title) is required.");
            set.Id = header.Value<string>("id");
            set.Version = header.Value<string>("version");
            set.Title = header.Value<string>("title");
            if (string.IsNullOrWhiteSpace(set.Id))
                throw new CadRequirementSetException("requirement_set.id is required; every element produced cites it.");
            if (string.IsNullOrWhiteSpace(set.Version))
                throw new CadRequirementSetException("requirement_set.version is required; an incremental run compares versions to tell a changed drawing from changed rules.");

            // ---- source: the units the drawing is in, DECLARED -------------------
            JObject source = doc["source"] as JObject;
            if (source == null)
                throw new CadRequirementSetException(
                    "source.units is required. A drawing whose walls are '0.2' apart is either metres or a " +
                    "mistake, and nothing in this bridge will decide which for you.");
            foreach (JProperty prop in source.Properties())
                if (!SourceKeys.Contains(prop.Name))
                    throw new CadRequirementSetException(
                        "Unknown key '" + prop.Name + "' in source. Known: " +
                        string.Join(", ", SourceKeys.OrderBy(x => x, StringComparer.Ordinal)) +
                        ". A misspelt 'role' would be ignored and the drawing read as a floor plan - and a " +
                        "section converted as a plan builds a building nobody drew.");

            set.SourceUnits = source.Value<string>("units");
            double? perUnit = CadUnits.MillimetresPer(set.SourceUnits);
            if (perUnit == null)
                throw new CadRequirementSetException(
                    "source.units '" + (set.SourceUnits ?? "(absent)") + "' is not a unit this bridge can resolve. " +
                    "Use one of: millimeter, centimeter, decimeter, meter, inch, foot, ussurveyfoot. " +
                    "'default' and 'custom' are NOT resolvable - read the real unit off the CAD link and state it.");
            set.SourceUnitsToMm = perUnit.Value;
            set.CaseSensitiveLayers = source.Value<bool?>("case_sensitive_layers") ?? false;

            // WHAT KIND OF DRAWING THIS IS.
            //
            // Every set written before this key existed was about a floor plan and
            // still is, so the default is that - but a set that never says which
            // view it converted is a set nobody can check, and the reply says the
            // default was used rather than staying silent about it.
            string role = source.Value<string>("role");
            set.SourceRoleWasDeclared = role != null;
            if (role != null)
            {
                if (!CadSourceRole.IsKnown(role))
                    throw new CadRequirementSetException(
                        "source.role '" + role + "' is not a view this bridge knows. Use one of: " +
                        string.Join(", ", CadSourceRole.All) + ". A typo here would read as a floor plan, " +
                        "and a section converted as a plan builds a building nobody drew.");
                set.SourceRole = role;
            }

            // ---- extent: which part of the drawing, when not all of it -----------
            JObject extent = source["extent_mm"] as JObject;
            if (source["extent_mm"] != null && extent == null)
                throw new CadRequirementSetException(
                    "source.extent_mm must be an object with min_x, min_y, max_x and max_y in millimetres, " +
                    "in the drawing's own coordinates. NOTHING was read from it.");
            if (extent != null)
            {
                foreach (JProperty prop in extent.Properties())
                    if (prop.Name != "min_x" && prop.Name != "min_y" && prop.Name != "max_x" && prop.Name != "max_y" &&
                        prop.Name != "crossing")
                        throw new CadRequirementSetException(
                            "Unknown key '" + prop.Name + "' in source.extent_mm. Known: crossing, max_x, max_y, min_x, " +
                            "min_y. A misspelt bound would be ignored and the zone would silently become the whole drawing.");

                var box = new CadExtent
                {
                    MinX = RequireNumber(extent, "min_x"),
                    MinY = RequireNumber(extent, "min_y"),
                    MaxX = RequireNumber(extent, "max_x"),
                    MaxY = RequireNumber(extent, "max_y")
                };
                if (box.MaxX <= box.MinX || box.MaxY <= box.MinY)
                    throw new CadRequirementSetException(
                        "source.extent_mm is empty or inverted (" + box.Describe() + "): max must exceed min on " +
                        "both axes. An inverted box contains nothing, and a set that converts nothing is not a " +
                        "set somebody meant to write.");

                JToken crossing = extent["crossing"];
                if (crossing != null)
                {
                    string c = crossing.Type == JTokenType.String ? ((string)crossing ?? "").Trim() : null;
                    if (c != CadExtent.CrossingExclude && c != CadExtent.CrossingWhole)
                        throw new CadRequirementSetException(
                            "source.extent_mm.crossing must be 'exclude' or 'whole'; it reads " +
                            crossing.ToString(Newtonsoft.Json.Formatting.None) + ". It decides what happens to an " +
                            "entity the zone cuts, and a guess would either drop the walls that bound the zone or " +
                            "pull in the runs that leave it.");
                    box.Crossing = c;
                }
                set.ExtentMm = box;
            }

            // ---- tolerances: all four, all declared ------------------------------
            JObject tol = doc["tolerances"] as JObject;
            if (tol == null)
                throw new CadRequirementSetException(
                    "tolerances is required with point_mm, gap_mm, angle_degrees and arc_sagitta_mm. " +
                    "Every one of them changes what the drawing is read to say, so none of them is chosen here.");
            set.PointToleranceMm = RequirePositive(tol, "point_mm", "how close two endpoints must be to be the same node");
            set.GapToleranceMm = RequirePositive(tol, "gap_mm", "the largest opening that may be snapped shut to close a loop");
            set.AngleToleranceDegrees = RequirePositive(tol, "angle_degrees", "how far from parallel two wall faces may be");
            set.ArcSagittaMm = RequirePositive(tol, "arc_sagitta_mm", "how far a chord may depart from the arc it replaces");

            // SYMMETRY HAS ITS OWN TOLERANCE, and it is small unless a set says
            // otherwise. One number cannot answer both "are these two endpoints
            // the same node" and "is this mark its own mirror image".
            set.SymmetryToleranceMm = tol.Value<double?>("symmetry_mm") ?? 1.0;

            // EACH QUESTION ITS OWN NUMBER, defaulting to what it used to borrow
            // so that re-reading an existing set changes nothing.
            set.HostSearchMm = tol.Value<double?>("host_search_mm") ?? set.PointToleranceMm;
            set.FaceProjectionMm = tol.Value<double?>("face_projection_mm") ?? 300.0;
            set.RevisionCompareMm = tol.Value<double?>("revision_compare_mm") ?? set.PointToleranceMm;
            set.WallOverlapMm = tol.Value<double?>("wall_overlap_mm") ?? 1.0;
            if (set.WallOverlapMm < 0)
                throw new CadRequirementSetException(
                    "tolerances.wall_overlap_mm cannot be negative: it is how far two walls may overlap and still be two.");
            foreach (string name in new[] { "host_search_mm", "face_projection_mm", "revision_compare_mm" })
            {
                double? v = tol.Value<double?>(name);
                if (v.HasValue && v.Value <= 0)
                    throw new CadRequirementSetException(
                        "tolerances." + name + " must be positive when declared; it is a distance, and zero " +
                        "would mean a question nothing can answer.");
            }
            if (set.SymmetryToleranceMm <= 0)
                throw new CadRequirementSetException(
                    "tolerances.symmetry_mm must be positive: it is how far a reflected symbol may move and " +
                    "still be called the same symbol.");
            if (set.GapToleranceMm < set.PointToleranceMm)
                throw new CadRequirementSetException(
                    "tolerances.gap_mm (" + set.GapToleranceMm.ToString(CultureInfo.InvariantCulture) +
                    ") is smaller than point_mm (" + set.PointToleranceMm.ToString(CultureInfo.InvariantCulture) +
                    "). Points already merge at point_mm, so a smaller gap tolerance can never do anything - " +
                    "which means the document says something its author did not mean.");

            // ---- rules ------------------------------------------------------------
            JArray rules = doc["rules"] as JArray;
            if (rules == null || rules.Count == 0)
                throw new CadRequirementSetException("rules must contain at least one rule; a set that maps nothing is not a mapping.");

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in rules)
            {
                JObject r = token as JObject;
                if (r == null) throw new CadRequirementSetException("every entry of rules must be an object.");
                set.Rules.Add(LoadRule(r, seenIds));
            }

            // Two rules that claim exactly the same layers at the same precedence
            // cannot both win, and picking one is the coin toss this refuses.
            for (int i = 0; i < set.Rules.Count; i++)
                for (int j = i + 1; j < set.Rules.Count; j++)
                {
                    CadRule a = set.Rules[i], b = set.Rules[j];
                    if (a.Precedence != b.Precedence) continue;

                    // A BLOCK RULE DISCRIMINATES BY NAME, NOT BY LAYER.
                    //
                    // Every electrical symbol in a real drawing sits on a handful
                    // of layers - E-PWR, E-LTS - and a mapping names the SYMBOLS:
                    // one rule for receptacles, one for panels, one for vanity
                    // lights, all of them claiming E-*. Judging those by layer
                    // alone made every realistic electrical mapping impossible to
                    // load, which is a tie that does not exist. Two block rules
                    // collide only when they claim the same NAMES as well.
                    if (a.Geometry != null && b.Geometry != null &&
                        a.Geometry.Source == CadGeometrySource.Blocks &&
                        b.Geometry.Source == CadGeometrySource.Blocks &&
                        !SamePatterns(a.Geometry.BlockPatterns, b.Geometry.BlockPatterns))
                        continue;

                    if (!string.Equals(a.Produces, b.Produces, StringComparison.Ordinal) &&
                        SamePatterns(a.LayerPatterns, b.LayerPatterns))
                        throw new CadRequirementSetException(
                            "rules '" + a.Id + "' and '" + b.Id + "' claim the same layers at precedence " +
                            a.Precedence + " but produce '" + a.Produces + "' and '" + b.Produces +
                            "'. Nothing here can choose between them; give one a higher precedence, or narrow " +
                            "one of the layer patterns.");
                }

            set.Sha256 = CanonicalHash(doc);
            // A RULE MAY ONLY ASK A VIEW FOR WHAT IT SHOWS.
            //
            // Checked here, where the set is whole: the role lives on the source
            // and the producer lives on the rule, and neither half can answer this
            // on its own. A section converted as a plan is the failure with no
            // symptom - the elements are created, verified and audited clean,
            // because the model and the drawing agree with each other.
            foreach (CadRule rule in set.Rules)
            {
                string why;
                if (CadSourceRole.Permits(set.SourceRole, rule.Produces, out why)) continue;
                throw new CadRequirementSetException("rule '" + rule.Id + "': " + why);
            }

            return set;
        }

        private static bool SamePatterns(List<string> a, List<string> b)
        {
            var sa = a.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var sb = b.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return sa.SequenceEqual(sb, StringComparer.Ordinal);
        }

        private static CadRule LoadRule(JObject r, HashSet<string> seenIds)
        {
            foreach (JProperty prop in r.Properties())
            {
                // AN UNDERSCORE PREFIX IS A NOTE, not a key this bridge acts on.
                // The layer profiler hands back a skeleton with a `_measured` line
                // per rule saying what it counted, and a loader that refused it
                // would mean the bridge produced a document it will not read.
                if (prop.Name.Length > 0 && prop.Name[0] == '_') continue;
                if (!RuleKeys.Contains(prop.Name))
                    throw new CadRequirementSetException(
                        "Unknown key '" + prop.Name + "' in a rule. Known: " +
                        string.Join(", ", RuleKeys.OrderBy(x => x, StringComparer.Ordinal)) +
                        ". A key beginning with an underscore is treated as a note and ignored.");
            }

            var rule = new CadRule();
            rule.Id = r.Value<string>("id");
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new CadRequirementSetException("every rule needs an id; produced elements cite the rule that made them.");
            if (!seenIds.Add(rule.Id))
                throw new CadRequirementSetException("rule id '" + rule.Id + "' appears twice; ids are how provenance is read back.");

            rule.Precedence = r.Value<int?>("precedence") ?? 0;
            rule.Discipline = r.Value<string>("discipline");

            JArray layers = r["layers"] as JArray;
            if (layers == null || layers.Count == 0)
                throw new CadRequirementSetException("rule '" + rule.Id + "' needs at least one layer pattern.");
            foreach (JToken l in layers)
            {
                if (l.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)l))
                    throw new CadRequirementSetException("rule '" + rule.Id + "': every layer pattern must be a non-empty string.");
                rule.LayerPatterns.Add((string)l);
            }
            foreach (JToken l in r["exclude_layers"] as JArray ?? new JArray())
            {
                if (l.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)l))
                    throw new CadRequirementSetException("rule '" + rule.Id + "': every exclude_layers pattern must be a non-empty string.");
                rule.ExcludeLayerPatterns.Add((string)l);
            }

            rule.Produces = r.Value<string>("produces");
            if (string.IsNullOrWhiteSpace(rule.Produces))
                throw new CadRequirementSetException("rule '" + rule.Id + "' must say what it produces.");
            if (!KnownProduces.Contains(rule.Produces, StringComparer.Ordinal))
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' produces '" + rule.Produces + "', which is not something this bridge " +
                    "knows how to build. Known: " + string.Join(", ", KnownProduces.OrderBy(x => x, StringComparer.Ordinal)));

            rule.Category = r.Value<string>("category");
            rule.FamilyType = r.Value<string>("family_type");

            // THE MIRROR, and what this rule says it means.
            string mirror = r.Value<string>("mirror");
            if (mirror != null)
            {
                if (mirror != "preserve" && mirror != "symmetric" && mirror != "variant" && mirror != "pending")
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "' says mirror '" + mirror + "'. Known: pending, preserve, " +
                        "symmetric, variant. There is deliberately no 'ignore': a reflection that is dropped " +
                        "without a reason is a device built the wrong way round.");
                rule.Mirror = mirror;
            }
            rule.MirrorVariantType = r.Value<string>("mirror_variant_type");
            if (rule.Mirror == "variant" && string.IsNullOrWhiteSpace(rule.MirrorVariantType))
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' says mirror 'variant' and names no mirror_variant_type. A variant " +
                    "nobody named is not a variant.");
            if (rule.Mirror != "variant" && !string.IsNullOrWhiteSpace(rule.MirrorVariantType))
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' names a mirror_variant_type and its mirror policy is '" +
                    (rule.Mirror ?? "preserve") + "'. The type would be read by nothing.");

            // THE HOST, when the rule declares one. Two words only: a typo here
            // would otherwise mean "not hosted", and a wall outlet placed on the
            // level is a receptacle lying on the floor.
            string hostedOn = r.Value<string>("hosted_on");
            if (hostedOn != null)
            {
                if (hostedOn != "wall" && hostedOn != "slab")
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "' says hosted_on '" + hostedOn + "'. Known: slab, wall. " +
                        "A host this bridge cannot resolve is refused rather than ignored, because ignoring " +
                        "it places a wall-mounted symbol on the floor.");
                rule.HostedOn = hostedOn;
            }
            JToken hostLayers = r["host_layers"];
            if (hostLayers != null)
            {
                var list = hostLayers as JArray;
                if (list == null || list.Count == 0 || list.Any(x => x.Type != JTokenType.String ||
                                                                    string.IsNullOrWhiteSpace((string)x)))
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "' declares host_layers, which must be a non-empty array of layer " +
                        "globs. An empty list would check the host against nothing and say it passed.");
                if (rule.HostedOn != "wall")
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "' declares host_layers and is not hosted_on 'wall'. The layers " +
                        "would be read by nothing.");
                rule.HostLayers = list.Select(x => ((string)x).Trim()).ToList();
            }
            rule.Level = r.Value<string>("level");
            rule.Phase = r.Value<string>("phase");
            rule.DesignOption = r.Value<string>("design_option");
            rule.HeightMm = r.Value<double?>("height_mm");
            rule.OffsetMm = r.Value<double?>("offset_mm");
            rule.ThicknessMm = r.Value<double?>("thickness_mm");
            rule.DiameterMm = r.Value<double?>("diameter_mm");
            rule.SlopePercent = r.Value<double?>("slope_percent");
            rule.SystemType = r.Value<string>("system_type");
            rule.Structural = r.Value<bool?>("structural");

            // A HOLE IN A WALL NEEDS BOTH ENDS OF ITS HEIGHT, and a rule that is
            // not cutting a wall may not name either: a key that reaches a builder
            // which ignores it is a promise nothing keeps.
            rule.SillHeightMm = r.Value<double?>("sill_height_mm");
            rule.HeadHeightMm = r.Value<double?>("head_height_mm");
            if (rule.Produces == "wall_opening")
            {
                if (!rule.SillHeightMm.HasValue || !rule.HeadHeightMm.HasValue)
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "' produces a wall opening and names " +
                        (!rule.SillHeightMm.HasValue ? "no sill_height_mm" : "no head_height_mm") +
                        ". A plan drawing shows where a hole is along a wall and says nothing about how high " +
                        "it is, so both come from this rule or from nowhere. A default would be a hole at a " +
                        "height nobody chose, and a hole at the wrong height is invisible in the plan it was " +
                        "drawn on.");
                if (rule.HeadHeightMm.Value <= rule.SillHeightMm.Value)
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': head_height_mm (" + rule.HeadHeightMm.Value +
                        ") is at or below sill_height_mm (" + rule.SillHeightMm.Value +
                        "), so the opening would have no height and cut nothing.");
            }
            else if (rule.SillHeightMm.HasValue || rule.HeadHeightMm.HasValue)
            {
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' declares sill_height_mm/head_height_mm and produces '" +
                    rule.Produces + "', which has no such pair. Those two numbers are the vertical extent " +
                    "of a hole cut in a WALL; a key that reaches a builder which ignores it is a promise " +
                    "nothing keeps.");
            }

            rule.AllowStructural = r.Value<bool?>("allow_structural");
            // A SHAFT CUTS MORE LOAD-BEARING FLOOR THAN ANY SINGLE OPENING, so it
            // was the one kind that most needed this permission and the one kind
            // the loader refused to let declare it.
            if (rule.AllowStructural.HasValue && rule.Produces != "opening" &&
                rule.Produces != "wall_opening" && rule.Produces != "shaft")
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' declares allow_structural and produces '" + rule.Produces +
                    "', which cuts nothing. That key is permission to cut a LOAD-BEARING slab, and a key " +
                    "that reaches a builder which ignores it is a promise nothing keeps - here it would " +
                    "read as an authorisation somebody gave and this bridge never asked for.");
            // A LEVEL IS NOT SOMETHING A PLAN DRAWING CONTAINS.
            //
            // `level` is in the produces vocabulary and has no create-kind mapping,
            // so every candidate deferred with "carries no geometry a level could
            // be built from" - on candidates that plainly carried geometry. No
            // amount of loosening tolerances or fixing the naming would ever change
            // it, and the reason given pointed at the drawing instead of at the
            // rule. An elevation is the one thing a plan cannot show.
            if (rule.Produces == "level")
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' produces a level, and a plan drawing cannot supply one: a storey " +
                    "is an ELEVATION, and a plan is the one view that does not show it. Create the levels " +
                    "first - horizun_create_elements takes an elevation and a name - and then name them on " +
                    "the rules that build ON them. Nothing about this rule can be adjusted to make it work.");

            rule.Naming = LoadNaming(r["naming"] as JObject, rule);
            rule.BaseLevel = r.Value<string>("base_level");
            rule.TopLevel = r.Value<string>("top_level");
            if (rule.Produces == "shaft")
            {
                if (string.IsNullOrWhiteSpace(rule.BaseLevel) || string.IsNullOrWhiteSpace(rule.TopLevel))
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "' produces a shaft and names " +
                        (string.IsNullOrWhiteSpace(rule.BaseLevel) ? "no base_level" : "no top_level") +
                        ". A shaft runs BETWEEN two levels - that is what separates it from a hole in one " +
                        "slab - and a drawing carries neither. A default would be a shaft stopping at a " +
                        "storey nobody chose, which looks correct in plan.");
                if (string.Equals(rule.BaseLevel, rule.TopLevel, StringComparison.Ordinal))
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': base_level and top_level are both '" + rule.BaseLevel +
                        "', so the shaft would have no height and cut nothing.");
            }
            else if (!string.IsNullOrWhiteSpace(rule.BaseLevel) || !string.IsNullOrWhiteSpace(rule.TopLevel))
            {
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' declares base_level/top_level and produces '" + rule.Produces +
                    "', which is hosted on ONE level. A key that reaches a builder which ignores it is a " +
                    "promise nothing keeps.");
            }
            rule.JoinRule = r.Value<string>("join_rule");
            if (rule.JoinRule != null && rule.JoinRule != "none" && rule.JoinRule != "auto" && rule.JoinRule != "butt")
                throw new CadRequirementSetException("rule '" + rule.Id + "': join_rule must be none, auto or butt.");

            rule.MinConfidence = r.Value<double?>("min_confidence") ?? 0.0;
            if (rule.MinConfidence < 0 || rule.MinConfidence > 1)
                throw new CadRequirementSetException("rule '" + rule.Id + "': min_confidence must be between 0 and 1.");

            string amb = r.Value<string>("on_ambiguous") ?? "leave_for_review";
            switch (amb)
            {
                case "leave_for_review": rule.OnAmbiguous = CadAmbiguityPolicy.LeaveForReview; break;
                case "reject": rule.OnAmbiguous = CadAmbiguityPolicy.Reject; break;
                case "highest_precedence_wins": rule.OnAmbiguous = CadAmbiguityPolicy.HighestPrecedenceWins; break;
                default: throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': on_ambiguous must be leave_for_review, reject or highest_precedence_wins.");
            }

            string div = r.Value<string>("on_manual_divergence") ?? "preserve";
            switch (div)
            {
                case "preserve": rule.OnManualDivergence = CadDivergencePolicy.Preserve; break;
                case "report_only": rule.OnManualDivergence = CadDivergencePolicy.ReportOnly; break;
                case "overwrite": rule.OnManualDivergence = CadDivergencePolicy.Overwrite; break;
                default: throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': on_manual_divergence must be preserve, report_only or overwrite. " +
                    "overwrite discards somebody's edit and has to be said out loud.");
            }

            JObject parameters = r["parameters"] as JObject;
            if (parameters != null)
                foreach (JProperty p in parameters.Properties())
                    rule.Parameters.Add(ReadParameterWrite(p.Name, p.Value, rule));

            rule.Geometry = LoadGeometry(r["geometry"] as JObject, rule);
            return rule;
        }

        private static readonly HashSet<string> NamingKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "strategy", "axis", "direction", "values", "order_tolerance_mm",
            "names_by_semantic_id", "by_position", "on_unnamed", "on_unmatched"
        };

        private static readonly HashSet<string> PositionKeys = new HashSet<string>(StringComparer.Ordinal)
        { "x_mm", "y_mm", "tolerance_mm", "name", "number" };

        /// <summary>
        /// A declared string, trimmed - and a refusal when it was declared BLANK.
        ///
        /// Whitespace is not a name. Untrimmed, " A " passes the model-collision
        /// pre-check that "A" would have failed, and then every later audit reports
        /// the element as hand-renamed because " A " and "A" are different strings.
        /// Blank-but-present is worse: the entry silently becomes no name at all
        /// and nothing downstream ever says one was expected.
        /// </summary>
        private static string DeclaredText(JObject entry, string key, CadRule rule, string what)
        {
            string raw = entry.Value<string>(key);
            if (raw == null) return null;
            if (string.IsNullOrWhiteSpace(raw))
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': " + what + " declares a " + key + " that is blank. Whitespace is " +
                    "not a name - omit the key if this entry supplies none, because a blank one silently " +
                    "becomes no name at all and nothing later says one was expected.");
            return raw.Trim();
        }

        /// <summary>
        /// Read a naming block, refusing every shape that would later produce a
        /// name nobody chose.
        ///
        /// The refusals are the point. A strategy that silently falls back to
        /// enumeration order gives grid "A" to whichever line Revit happened to
        /// return first, and that is not stable between runs, let alone between
        /// machines.
        /// </summary>
        private static CadNaming LoadNaming(JObject n, CadRule rule)
        {
            if (n == null) return null;
            foreach (JProperty prop in n.Properties())
                if (!NamingKeys.Contains(prop.Name))
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': unknown naming key '" + prop.Name + "'. Known: " +
                        string.Join(", ", NamingKeys.OrderBy(x => x, StringComparer.Ordinal)) +
                        ". A misspelt key is a rule that silently names nothing.");

            var naming = new CadNaming();
            naming.Strategy = n.Value<string>("strategy");
            if (string.IsNullOrWhiteSpace(naming.Strategy))
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': naming needs a strategy (ordered, by_semantic_id or by_position). " +
                    "There is deliberately no default: an implicit order is a decision nobody wrote down.");

            string onUnnamed = n.Value<string>("on_unnamed");
            if (onUnnamed != null)
            {
                if (onUnnamed != "refuse" && onUnnamed != "review" && onUnnamed != "leave_unnamed")
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': naming.on_unnamed must be refuse, review or leave_unnamed. " +
                        "There is no option that invents a name.");
                naming.OnUnnamed = onUnnamed;
            }
            string onUnmatched = n.Value<string>("on_unmatched");
            if (onUnmatched != null)
            {
                if (onUnmatched != "refuse" && onUnmatched != "report")
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': naming.on_unmatched must be refuse or report.");
                naming.OnUnmatched = onUnmatched;
            }

            switch (naming.Strategy)
            {
                case "ordered":
                    naming.Axis = n.Value<string>("axis");
                    if (naming.Axis != "x" && naming.Axis != "y" && naming.Axis != "distance_from_origin")
                        throw new CadRequirementSetException(
                            "rule '" + rule.Id + "': naming.axis must be x, y or distance_from_origin. Ordering " +
                            "without naming the axis is ordering by whatever Revit returned first.");
                    naming.Direction = n.Value<string>("direction") ?? "ascending";
                    if (naming.Direction != "ascending" && naming.Direction != "descending")
                        throw new CadRequirementSetException(
                            "rule '" + rule.Id + "': naming.direction must be ascending or descending.");
                    foreach (JToken v in n["values"] as JArray ?? new JArray())
                    {
                        string name = v?.ToString();
                        if (string.IsNullOrWhiteSpace(name))
                            throw new CadRequirementSetException(
                                "rule '" + rule.Id + "': naming.values contains an empty name. A blank name is " +
                                "not a name, and Revit would keep whatever it chose itself.");
                        naming.Values.Add(name.Trim());
                    }
                    if (naming.Values.Count == 0)
                        throw new CadRequirementSetException(
                            "rule '" + rule.Id + "': naming.strategy 'ordered' needs values.");
                    var seenValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string v in naming.Values)
                        if (!seenValue.Add(v))
                            throw new CadRequirementSetException(
                                "rule '" + rule.Id + "': naming.values repeats '" + v + "'. Two grids of one " +
                                "name is a model nobody can dimension from, and Revit refuses the second.");
                    naming.OrderToleranceMm = n.Value<double?>("order_tolerance_mm");
                    if (naming.OrderToleranceMm.HasValue && naming.OrderToleranceMm.Value <= 0)
                        throw new CadRequirementSetException(
                            "rule '" + rule.Id + "': naming.order_tolerance_mm must be positive.");
                    break;

                case "by_semantic_id":
                    JObject map = n["names_by_semantic_id"] as JObject;
                    if (map == null || !map.HasValues)
                        throw new CadRequirementSetException(
                            "rule '" + rule.Id + "': naming.strategy 'by_semantic_id' needs " +
                            "names_by_semantic_id.");
                    foreach (JProperty entry in map.Properties())
                    {
                        string name = entry.Value?.ToString();
                        if (string.IsNullOrWhiteSpace(name))
                            throw new CadRequirementSetException(
                                "rule '" + rule.Id + "': naming maps '" + entry.Name + "' to an empty name.");
                        naming.BySemanticId[entry.Name] = name.Trim();
                    }
                    break;

                case "by_position":
                    JArray positions = n["by_position"] as JArray;
                    if (positions == null || positions.Count == 0)
                        throw new CadRequirementSetException(
                            "rule '" + rule.Id + "': naming.strategy 'by_position' needs by_position.");
                    foreach (JObject entry in positions.OfType<JObject>())
                    {
                        foreach (JProperty prop in entry.Properties())
                            if (!PositionKeys.Contains(prop.Name))
                                throw new CadRequirementSetException(
                                    "rule '" + rule.Id + "': unknown by_position key '" + prop.Name + "'.");
                        var pos = new CadNamedPosition
                        {
                            X = entry.Value<double?>("x_mm"),
                            Y = entry.Value<double?>("y_mm"),
                            Name = DeclaredText(entry, "name", rule, "a by_position entry"),
                            Number = DeclaredText(entry, "number", rule, "a by_position entry"),
                            ToleranceMm = entry.Value<double?>("tolerance_mm") ?? 0
                        };
                        if (!pos.X.HasValue && !pos.Y.HasValue)
                            throw new CadRequirementSetException(
                                "rule '" + rule.Id + "': a by_position entry names neither x_mm nor y_mm.");
                        if (string.IsNullOrWhiteSpace(pos.Name) && string.IsNullOrWhiteSpace(pos.Number))
                            throw new CadRequirementSetException(
                                "rule '" + rule.Id + "': a by_position entry carries neither name nor number.");
                        if (pos.ToleranceMm <= 0)
                            throw new CadRequirementSetException(
                                "rule '" + rule.Id + "': a by_position entry needs a positive tolerance_mm. " +
                                "Matching a coordinate exactly is matching nothing.");
                        naming.ByPosition.Add(pos);
                    }
                    break;

                default:
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': naming.strategy '" + naming.Strategy + "' is not one this " +
                        "bridge knows. Known: ordered, by_semantic_id, by_position.");
            }
            return naming;
        }

        private static readonly HashSet<string> ParameterKeys = new HashSet<string>(StringComparer.Ordinal)
        { "value", "scope", "required", "from_block_attribute" };

        /// <summary>
        /// One entry of the rule's `parameters` map. The short form is a bare
        /// value; the long form declares scope and whether it is required.
        ///
        ///   "parameters": {
        ///     "Fire Rating": "2 h",
        ///     "Comments":    { "value": "from DWG", "scope": "instance", "required": false }
        ///   }
        /// </summary>
        private static CadParameterWrite ReadParameterWrite(string name, JToken token, CadRule rule)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': a parameter entry has no name.");

            var write = new CadParameterWrite { Parameter = name.Trim() };
            var asObject = token as JObject;
            if (asObject == null)
            {
                write.Value = token;
                return write;
            }

            foreach (JProperty prop in asObject.Properties())
                if (!ParameterKeys.Contains(prop.Name))
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': unknown key '" + prop.Name + "' on parameter '" + name +
                        "'. Known: " + string.Join(", ", ParameterKeys.OrderBy(x => x, StringComparer.Ordinal)) +
                        ".");

            // A VALUE READ FROM THE DRAWING IS STILL A VALUE.
            //
            // from_block_attribute says "take it from the symbol's own attribute",
            // which is the whole point of reading a DWG properly: the circuit the
            // electrician typed on the outlet goes into the parameter the schedule
            // reads, and nobody retypes three hundred of them. It stands in for
            // "value" and refuses to be combined with it - two sources for one
            // number, with the later one silently winning, is what this codebase
            // refuses everywhere else.
            string fromAttribute = asObject.Value<string>("from_block_attribute");
            if (!string.IsNullOrWhiteSpace(fromAttribute))
            {
                if (asObject["value"] != null)
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': parameter '" + name + "' declares BOTH a value and a " +
                        "from_block_attribute. One of them would be ignored and nothing here can say which " +
                        "the author meant.");
                write.FromBlockAttribute = fromAttribute.Trim();
                write.Scope = asObject.Value<string>("scope") ?? write.Scope;
                bool? req = asObject.Value<bool?>("required");
                if (req.HasValue) write.Required = req.Value;
                return write;
            }

            if (asObject["value"] == null)
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': parameter '" + name + "' declares no value, and no " +
                    "from_block_attribute to read one from. Omit the parameter rather than declaring one " +
                    "with nothing to write.");
            write.Value = asObject["value"];

            string scope = asObject.Value<string>("scope");
            if (scope != null)
            {
                if (scope != "instance" && scope != "type")
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': parameter '" + name + "' has scope '" + scope +
                        "'. It must be instance or type - and a TYPE write changes every instance of that " +
                        "type in the model, including ones this conversion did not create.");
                write.Scope = scope;
            }
            write.Required = asObject.Value<bool?>("required") ?? true;
            return write;
        }

        private static CadGeometryCriteria LoadGeometry(JObject g, CadRule rule)
        {
            if (g == null)
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' needs a geometry block saying what to look for " +
                    "(from: double_lines, double_arcs, closed_loops, single_lines, blocks or point_clusters).");
            foreach (JProperty prop in g.Properties())
                if (!GeometryKeys.Contains(prop.Name))
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': unknown geometry key '" + prop.Name + "'. Known: " +
                        string.Join(", ", GeometryKeys.OrderBy(x => x, StringComparer.Ordinal)));

            var c = new CadGeometryCriteria();
            string from = g.Value<string>("from");
            // A RING IS NOT A CHAIN, and a separator built from one is drawn on
            // three sides of four. The plan emits a room_separator's profile as the
            // candidate's point list, which for a closed loop is the ring WITHOUT
            // its closing edge - so the boundary has a gap, the plan and the apply
            // both report success, the verification agrees (it IS a curve in the
            // right category), and the room inside bleeds through the missing side
            // into the next space. Every area and every schedule line is wrong, and
            // nothing anywhere says why.
            if (rule.Produces == "room_separator" && from == "closed_loops")
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "' produces a room separator from closed_loops. A separator is a " +
                    "CHAIN of curves and a ring read this way loses its closing edge, so the boundary would " +
                    "be drawn on three sides of four - built, verified, and leaking into the next room. Use " +
                    "single_lines for the lines that divide a space; the walls are what close it.");
            switch (from)
            {
                case "double_lines": c.Source = CadGeometrySource.DoubleLines; break;
                case "double_arcs": c.Source = CadGeometrySource.DoubleArcs; break;
                case "closed_loops": c.Source = CadGeometrySource.ClosedLoops; break;
                case "single_lines": c.Source = CadGeometrySource.SingleLines; break;
                case "blocks": c.Source = CadGeometrySource.Blocks; break;
                case "point_clusters": c.Source = CadGeometrySource.PointClusters; break;
                default: throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': geometry.from must be double_lines, closed_loops, single_lines, " +
                    "blocks or point_clusters; got '" + (from ?? "(absent)") + "'.");
            }

            JToken blocksToken = g["blocks"];
            if (blocksToken is JArray)
                foreach (JToken b2 in (JArray)blocksToken)
                {
                    string pattern = b2 == null ? null : b2.ToString();
                    if (!string.IsNullOrWhiteSpace(pattern)) c.BlockPatterns.Add(pattern.Trim());
                }
            else if (blocksToken != null && blocksToken.Type != JTokenType.Null)
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': geometry.blocks must be a list of block-name patterns.");

            JToken facingToken = g["block_facing"];
            if (facingToken != null && facingToken.Type != JTokenType.Null)
            {
                var facing = facingToken as JObject;
                if (facing == null || facing.Count == 0)
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': geometry.block_facing must be an object mapping block-name patterns " +
                        "to \"+x\", \"-x\", \"+y\" or \"-y\".");
                if (c.Source != CadGeometrySource.Blocks)
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': geometry.block_facing only means something with from:\"blocks\".");
                foreach (JProperty f in facing.Properties())
                {
                    string axis = f.Value.Type == JTokenType.String ? (string)f.Value : null;
                    CadVector v;
                    switch (axis)
                    {
                        case "+x": v = new CadVector(1, 0); break;
                        case "-x": v = new CadVector(-1, 0); break;
                        case "+y": v = new CadVector(0, 1); break;
                        case "-y": v = new CadVector(0, -1); break;
                        default: throw new CadRequirementSetException(
                            "rule '" + rule.Id + "': geometry.block_facing['" + f.Name + "'] must be \"+x\", \"-x\", " +
                            "\"+y\" or \"-y\" - the direction the device faces in the block's own frame; got " +
                            f.Value.ToString(Newtonsoft.Json.Formatting.None) + ".");
                    }
                    if (string.IsNullOrWhiteSpace(f.Name))
                        throw new CadRequirementSetException(
                            "rule '" + rule.Id + "': geometry.block_facing has an empty block-name pattern.");
                    c.BlockFacing.Add(new KeyValuePair<string, CadVector>(f.Name.Trim(), v));
                }
            }

            if (c.Source != CadGeometrySource.Blocks && c.BlockPatterns.Count > 0)
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': geometry.blocks only means something with from:\"blocks\". A rule " +
                    "that names blocks and reads line work would silently ignore the names.");

            c.MinThicknessMm = g.Value<double?>("min_thickness_mm");
            c.MaxThicknessMm = g.Value<double?>("max_thickness_mm");
            c.MinOverlapMm = g.Value<double?>("min_overlap_mm");
            c.MinOverlapFraction = g.Value<double?>("min_overlap_fraction");
            c.MinLengthMm = g.Value<double?>("min_length_mm");
            c.MaxLengthMm = g.Value<double?>("max_length_mm");
            c.MinAreaMm2 = g.Value<double?>("min_area_mm2");
            c.MaxAreaMm2 = g.Value<double?>("max_area_mm2");
            c.ClusterRadiusMm = g.Value<double?>("cluster_radius_mm");
            c.SameLayerOnly = g.Value<bool?>("same_layer_only") ?? true;
            if (g["bridge_openings_mm"] != null)
            {
                double bridge = g.Value<double>("bridge_openings_mm");
                if (bridge <= 0)
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': geometry.bridge_openings_mm must be positive. Omit it to read " +
                        "every break in a run of wall as the end of that wall, which is the default.");
                c.BridgeOpeningsMm = bridge;
            }

            if (c.Source == CadGeometrySource.DoubleLines || c.Source == CadGeometrySource.DoubleArcs)
            {
                // A double-line rule with no thickness bounds pairs a facade with
                // whatever happens to run beside it. These are the numbers that
                // decide what a wall IS, so they are required, not defaulted.
                if (c.MinThicknessMm == null || c.MaxThicknessMm == null)
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': geometry.from is double_lines, so min_thickness_mm and " +
                        "max_thickness_mm are required. Without them any two parallel lines are a wall.");
                if (c.MinThicknessMm.Value <= 0 || c.MaxThicknessMm.Value <= c.MinThicknessMm.Value)
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': min_thickness_mm must be positive and less than max_thickness_mm.");
                if (c.MinOverlapFraction != null && (c.MinOverlapFraction.Value < 0 || c.MinOverlapFraction.Value > 1))
                    throw new CadRequirementSetException(
                        "rule '" + rule.Id + "': min_overlap_fraction is a fraction between 0 and 1.");
            }
            if (c.Source == CadGeometrySource.PointClusters && c.ClusterRadiusMm == null)
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': geometry.from is point_clusters, so cluster_radius_mm is required - " +
                    "it is the whole of what 'the same symbol' means.");
            if (c.MinAreaMm2 != null && c.MaxAreaMm2 != null && c.MaxAreaMm2.Value <= c.MinAreaMm2.Value)
                throw new CadRequirementSetException(
                    "rule '" + rule.Id + "': max_area_mm2 must exceed min_area_mm2.");
            return c;
        }

        private static double RequirePositive(JObject o, string key, string why)
        {
            double? v = o.Value<double?>(key);
            if (v == null)
                throw new CadRequirementSetException("tolerances." + key + " is required (" + why + ").");
            if (v.Value <= 0)
                throw new CadRequirementSetException("tolerances." + key + " must be positive (" + why + ").");
            return v.Value;
        }

        /// <summary>
        /// The canonical hash: property order and whitespace cannot change it, so
        /// a reformatted file is the same requirement set and a changed number is
        /// not.
        /// </summary>
        /// <summary>A number the set must state. Absent and unreadable are the same refusal.</summary>
        private static double RequireNumber(JObject o, string key)
        {
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null)
                throw new CadRequirementSetException(
                    "source.extent_mm." + key + " is required. Three bounds and a guess is not a zone.");
            double? v = t.Type == JTokenType.Integer || t.Type == JTokenType.Float ? (double?)t.Value<double>() : null;
            if (v == null)
                throw new CadRequirementSetException(
                    "source.extent_mm." + key + " must be a number in millimetres; it reads '" +
                    t.ToString(Newtonsoft.Json.Formatting.None) + "'.");
            return v.Value;
        }

        public static string CanonicalHash(JToken doc)
        {
            var sb = new StringBuilder();
            Canonicalize(doc, sb);
            return CadIdentity.Sha256Hex(sb.ToString());
        }

        private static void Canonicalize(JToken t, StringBuilder sb)
        {
            if (t == null || t.Type == JTokenType.Null) { sb.Append("null"); return; }
            switch (t.Type)
            {
                case JTokenType.Object:
                    sb.Append('{');
                    foreach (JProperty p in ((JObject)t).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        sb.Append(JsonConvert.ToString(p.Name)).Append(':');
                        Canonicalize(p.Value, sb);
                        sb.Append(',');
                    }
                    sb.Append('}');
                    return;
                case JTokenType.Array:
                    // Array ORDER is meaningful - rule order is not, but a point
                    // list is - so arrays are never sorted.
                    sb.Append('[');
                    foreach (JToken item in (JArray)t) { Canonicalize(item, sb); sb.Append(','); }
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
                    sb.Append(JsonConvert.ToString(t.ToString()));
                    return;
            }
        }

        /// <summary>
        /// The rules that claim a layer, best first. Precedence decides; an exact
        /// tie is returned as a tie so the caller can see the ambiguity rather
        /// than receive a silent winner.
        /// </summary>
        public List<CadRule> RulesFor(string layer)
        {
            return Rules.Where(r => r.Matches(layer, CaseSensitiveLayers))
                        .OrderByDescending(r => r.Precedence)
                        .ThenBy(r => r.Id, StringComparer.Ordinal)
                        .ToList();
        }

        /// <summary>The stamp every produced element and every finding carries.</summary>
        public JObject Stamp()
        {
            return new JObject
            {
                ["id"] = Id,
                ["version"] = Version,
                ["title"] = Title,
                ["sha256"] = Sha256,
                ["source_units"] = SourceUnits,
                ["rule_count"] = Rules.Count,
                ["tolerances"] = new JObject
                {
                    ["point_mm"] = PointToleranceMm,
                    ["gap_mm"] = GapToleranceMm,
                    ["angle_degrees"] = AngleToleranceDegrees,
                    ["arc_sagitta_mm"] = ArcSagittaMm
                }
            };
        }
    }
}
