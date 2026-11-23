// -----------------------------------------------------------------------------
// Horizun Revit MCP - what the model says versus what the requirement set asked.
// Original Horizun code. No Revit types: both sides arrive as JSON, so the
// comparison is provable at a desk.
//
// TWO RULES SHAPE EVERY FINDING HERE.
//
// UNKNOWN IS NEVER A PASS. A property that could not be read produces a finding
// saying it could not be read - not silence, and not agreement. An audit whose
// clean result includes everything it failed to look at is worse than no audit,
// because somebody acts on it.
//
// A FINDING CARRIES ITS EVIDENCE. Every one names what was expected, what was
// observed, the tolerance it was judged against and whether a typed action could
// fix it. "quantity_differs" on its own is a complaint; "expected 9, model has
// 8, tolerance is exact, fixable by re-applying rule beam-stirrups" is a job.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RebarFinding
    {
        // Host and identity
        public const string HostMissing = "host_missing";
        public const string HostIneligible = "host_ineligible";
        public const string RuleBuiltNothing = "rule_built_nothing";
        // Type and geometry
        public const string TypeDiffers = "type_differs";
        public const string DiameterDiffers = "diameter_differs";
        public const string ShapeDiffers = "shape_differs";
        public const string BarOutsideHost = "bar_outside_host";
        public const string BarPartiallyOutsideHost = "bar_partially_outside_host";
        public const string CoverViolated = "cover_violated";
        public const string ContainmentNotEvaluable = "containment_not_evaluable";
        public const string GeometryDiffers = "geometry_differs";
        public const string GeometryReversed = "geometry_reversed";
        public const string PlaneDiffers = "plane_differs";
        /// <summary>
        /// D1 option A: a stirrup declared closed with a hook at both ends, which Revit's
        /// as-declared form leaves open at its first corner - published as info, never an error.
        /// </summary>
        public const string HookCornerOpening = "hook_corner_opening";
        // An open bar's hooked end carried on along the bar by Revit (acero 3.0 L2) - info only
        public const string HookEndExtension = "hook_end_extension";
        public const string NormalDiffers = "normal_differs";
        public const string SideDiffers = "side_differs";
        public const string StyleDiffers = "style_differs";
        public const string MarkDiffers = "mark_differs";
        public const string LengthDiffers = "length_differs";
        // Terminations
        public const string HookDiffers = "hook_differs";
        public const string OrientationDiffers = "orientation_differs";
        /// <summary>
        /// Info: Revit's orientation WORD at a hooked end disagrees with the side the hook is DRAWN on, and the drawing
        /// is what was declared (measured, Revit 2024: a straight bar matched to M_01 drew 'left' and read 'right').
        /// </summary>
        public const string OrientationLabelDiffersFromDrawing = "orientation_label_differs_from_drawing";
        /// <summary>
        /// A hook length the set declares (hook_types[].lengths_mm) that the BAR TYPE does not carry,
        /// or carries auto-calculated. A type property: every bar of that type with that hook has it.
        /// </summary>
        public const string HookLengthDiffers = "hook_length_differs";
        // Layout
        public const string LayoutDiffers = "layout_differs";
        public const string QuantityDiffers = "quantity_differs";
        public const string SpacingDiffers = "spacing_differs";
        public const string ArrayLengthDiffers = "array_length_differs";
        public const string MissingFirstBar = "missing_first_bar";
        public const string MissingLastBar = "missing_last_bar";
        public const string PositionCountDiffers = "position_count_differs";
        public const string SuppressedBarsDiffer = "suppressed_bars_differ";
        // Cover
        public const string CoverDiffers = "cover_differs";
        public const string CoverUnreadable = "cover_unreadable";
        // Marks and provenance
        /// <summary>
        /// One schedule mark on sets that are NOT the same bar (bar type, shape, or one bar's length
        /// beyond the set's length tolerance). Several IDENTICAL sets under one mark are how a
        /// schedule is meant to work and are not a finding (L9; it replaced bar_mark_duplicate).
        /// </summary>
        public const string BarMarkConflict = "bar_mark_conflict";
        public const string HostChanged = "host_changed";
        public const string UndeclaredSetInHost = "undeclared_set_in_host";
        public const string ProvenanceMissing = "provenance_missing";
        public const string ManuallyDiverged = "manually_diverged";
        public const string StaleRequirementSet = "stale_requirement_set";
        // Laps a section rule declared (F2.1)
        public const string LapInsufficient = "lap_insufficient";
        // Steel declared into another element (acero 3.0, L1): the anchored length it has there
        public const string AnchorageShort = "anchorage_short";
        // Area, path and fabric systems (acero 3.0, L4b): a parameter the system was built with
        // reads back as something other than what was declared
        public const string SystemParameterDiffers = "system_parameter_differs";
        // A coupler a section layer declares (acero 3.0, L7): absent from either bar, or of another type
        public const string MissingCoupler = "missing_coupler";
        // Info (L7): a coupled end Revit left short of the station, inside the coupler's body.
        public const string CoupledEndSeated = "coupled_end_seated";
        // Info (L10): the row at the stirrup hooks' corner seated in the other pass's bend, within half a stirrup.
        public const string HookCornerSeat = "hook_corner_seat";
        public const string CouplerTypeDiffers = "coupler_type_differs";
        // A hoop or spiral placed from a rebar shape (L7) whose drawn centreline or parameters are not the placement
        public const string PlacedShapeDiffers = "placed_shape_differs";
        /// <summary>
        /// acero 3.0: bars of DIFFERENT rules closer than the set's declared clearances, or passing THROUGH
        /// each other whatever is declared (Revit moves such a bar until it touches the other). One per pair
        /// of sets, with both rule ids, both bar ids, the clear distance and the point. See RebarOverlap.
        /// </summary>
        public const string OverlappingBar = "overlapping_bar";
        /// <summary>
        /// acero 3.0 (stage 1c): two bars of different rules or sets laid on ONE centreline - every point of each
        /// within the length tolerance of the other, the same model diameter. One bar built twice. Reported once,
        /// as this, never also as overlapping_bar; compared against bars no rule built as well. See RebarOverlap.
        /// </summary>
        public const string DuplicateBar = "duplicate_bar";
        /// <summary>
        /// etapa 1c: a set the declared crossing_order displaced does not stand where the displacement put it,
        /// measured from the DECLARED position along the displacement (RebarCrossing.Measure).
        /// </summary>
        public const string CrossingDisplacementDiffers = "crossing_displacement_differs";
        // Reading failures - never silence
        public const string Unreadable = "unreadable";

        public static readonly string[] All =
        {
            HostMissing, HostIneligible, RuleBuiltNothing, TypeDiffers, DiameterDiffers, ShapeDiffers,
            BarOutsideHost, BarPartiallyOutsideHost, CoverViolated, ContainmentNotEvaluable,
            GeometryDiffers, GeometryReversed, PlaneDiffers, HookCornerOpening, HookEndExtension,
            NormalDiffers, SideDiffers, StyleDiffers, MarkDiffers, LengthDiffers,
            HookDiffers, OrientationDiffers, OrientationLabelDiffersFromDrawing, HookLengthDiffers, LayoutDiffers, QuantityDiffers, SpacingDiffers,
            ArrayLengthDiffers, MissingFirstBar, MissingLastBar, PositionCountDiffers,
            SuppressedBarsDiffer, CoverDiffers, CoverUnreadable,
            BarMarkConflict, HostChanged, UndeclaredSetInHost, ProvenanceMissing,
            StaleRequirementSet, LapInsufficient, AnchorageShort, SystemParameterDiffers, MissingCoupler, CouplerTypeDiffers, CoupledEndSeated, HookCornerSeat,
            PlacedShapeDiffers, OverlappingBar, DuplicateBar, CrossingDisplacementDiffers, Unreadable
        };

        /// <summary>
        /// Named so a caller can publish them: these are findings this bridge does
        /// NOT look for. Silence about them is a gap, and a gap nobody wrote down
        /// is indistinguishable from a gap nobody found.
        /// </summary>
        public static readonly string[] NotChecked =
        {
            // overlapping_bar compares the bars THIS SET's rules built with each other. Bars no rule of the
            // set built - another set's, somebody's hand modelling, an area/path system's - are compared with
            // them for duplicate_bar only, not for crossing or clearance (horizun_query_structure
            // mode=steel_review judges every bar of a model against every other); fabric is not compared.
            "overlap_with_unattributed_bar",
            // Revit's own numbering on sets whose rule declared no mark (measured: all read '1').
            "undeclared_mark_numbering",
            // A splice only a section rule's laps declare is measured (lap_insufficient); bars
            // somebody lapped by hand, or two reinforcement_rules that happen to overlap, are not paired.
            "undeclared_lap",
            // L7: a coupler a section layer declares is checked on both bars (missing_coupler,
            // coupler_type_differs). Whether Revit judges it COMPATIBLE with the bars - size,
            // engagement - is Revit's to refuse at creation and is not re-checked here.
            "coupler_incompatible", "partition_differs",
            // A placed hoop or spiral (L7) is compared by its placement - radius, plane or
            // height, parameters - and not point by point: where round the axis its lap or its
            // first turn starts, and how long the lap is, are the shape's and are not compared.
            "placed_shape_start_angle", "placed_shape_lap_length",
            // Whether a bar is still WORTH what it was: a set whose host was
            // resized keeps every property this compares and is now the wrong
            // length for the concrete around it. Containment catches the bar
            // leaving the host; nothing here proposes what it should have become.
            "rebar_should_have_been_regenerated",
            // Area, path and fabric systems (acero 3.0, L4b). Their PARAMETERS are re-read and
            // compared; the bars Revit lays from them are not. Where each RebarInSystem lies, whether
            // it stays inside the host and keeps its cover, and where the outline's EDGES run (only
            // its corners are held to the host face) are not measured. Nor are the sheets a fabric
            // area cuts, the side a path was flipped to, or an area system's layout rule.
            "area_reinforcement_geometry", "path_reinforcement_geometry", "path_reinforcement_flip",
            "fabric_sheet_layout", "area_reinforcement_layout_rule"
        };
    }

    public static class RebarSeverity
    {
        /// <summary>The model does not carry what was asked for.</summary>
        public const string Error = "error";
        /// <summary>Something could not be measured, so the answer is not known.</summary>
        public const string Unknown = "unknown";
        /// <summary>A difference that is a fact rather than a fault.</summary>
        public const string Info = "info";
    }

    /// <summary>The finding rows the steel review of horizun_query_structure shapes.</summary>
    public static class RebarAuditRules
    {
        // ------------------------------------------------------------- shaping

        public static JObject Finding(string code, string severity, string ruleId, long rebarId,
                                      object expected, object observed, string tolerance, string why,
                                      bool fixable, string suggestedTool)
        {
            return new JObject
            {
                ["code"] = code,
                ["severity"] = severity,
                ["rule_id"] = ruleId,
                ["rebar_id"] = rebarId,
                ["expected"] = expected == null ? JValue.CreateNull() : JToken.FromObject(expected),
                ["observed"] = observed == null ? JValue.CreateNull() : JToken.FromObject(observed),
                ["tolerance"] = tolerance,
                ["why"] = why,
                ["fixable"] = fixable,
                ["suggested_typed_action"] = suggestedTool
            };
        }

        /// <summary>
        /// A property that could not be read. Severity `unknown`, never `info` and
        /// never absent: an audit that stays quiet about what it could not measure
        /// reports a clean model it never looked at.
        /// </summary>
        public static JObject Unknown(string code, string ruleId, long rebarId, object expected, string why)
        {
            JObject f = Finding(code, RebarSeverity.Unknown, ruleId, rebarId, expected, null, "n/a",
                                why + " UNKNOWN IS NOT A PASS.", false, null);
            f["code"] = RebarFinding.Unreadable;
            f["about"] = code;
            return f;
        }

        // ------------------------------------------------------------ geometry

        /// <summary>A JSON array of [x, y, z] triples, or null when it is not one.</summary>
        public static List<double[]> PointsOf(JToken token)
        {
            var arr = token as JArray;
            if (arr == null) return null;
            var outp = new List<double[]>(arr.Count);
            foreach (JToken t in arr)
            {
                var p = t as JArray;
                if (p == null || p.Count < 3) return null;
                var v = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (p[i] == null || (p[i].Type != JTokenType.Float && p[i].Type != JTokenType.Integer))
                        return null;
                    v[i] = p[i].Value<double>();
                }
                outp.Add(v);
            }
            return outp.Count == 0 ? null : outp;
        }
    }
}
