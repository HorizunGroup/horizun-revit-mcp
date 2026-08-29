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
        public const string NormalDiffers = "normal_differs";
        public const string SideDiffers = "side_differs";
        public const string StyleDiffers = "style_differs";
        public const string MarkDiffers = "mark_differs";
        public const string LengthDiffers = "length_differs";
        // Terminations
        public const string HookDiffers = "hook_differs";
        public const string OrientationDiffers = "orientation_differs";
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
        public const string BarMarkDuplicate = "bar_mark_duplicate";
        public const string HostChanged = "host_changed";
        public const string UndeclaredSetInHost = "undeclared_set_in_host";
        public const string ProvenanceMissing = "provenance_missing";
        public const string ManuallyDiverged = "manually_diverged";
        public const string StaleRequirementSet = "stale_requirement_set";
        // Reading failures - never silence
        public const string Unreadable = "unreadable";

        public static readonly string[] All =
        {
            HostMissing, HostIneligible, RuleBuiltNothing, TypeDiffers, DiameterDiffers, ShapeDiffers,
            BarOutsideHost, BarPartiallyOutsideHost, CoverViolated, ContainmentNotEvaluable,
            GeometryDiffers, GeometryReversed, PlaneDiffers,
            NormalDiffers, SideDiffers, StyleDiffers, MarkDiffers, LengthDiffers,
            HookDiffers, OrientationDiffers, LayoutDiffers, QuantityDiffers, SpacingDiffers,
            ArrayLengthDiffers, MissingFirstBar, MissingLastBar, PositionCountDiffers,
            SuppressedBarsDiffer, CoverDiffers, CoverUnreadable,
            BarMarkDuplicate, HostChanged, UndeclaredSetInHost, ProvenanceMissing,
            StaleRequirementSet, Unreadable
        };

        /// <summary>
        /// Named so a caller can publish them: these are findings this bridge does
        /// NOT look for. Silence about them is a gap, and a gap nobody wrote down
        /// is indistinguishable from a gap nobody found.
        /// </summary>
        public static readonly string[] NotChecked =
        {
            "duplicate_bar", "overlapping_bar", "lap_insufficient",
            "missing_coupler", "coupler_incompatible", "partition_differs",
            // Whether a bar is still WORTH what it was: a set whose host was
            // resized keeps every property this compares and is now the wrong
            // length for the concrete around it. Containment catches the bar
            // leaving the host; nothing here proposes what it should have become.
            "rebar_should_have_been_regenerated"
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

    public static class RebarAuditRules
    {
        /// <summary>
        /// Compare ONE expected row against ONE observed bar and return every
        /// finding. `expected` is a row from ReinforcementResolver; `observed` is a
        /// row from RebarFacts. Both are the shapes those two actually emit -
        /// deliberately, so this cannot drift from either.
        /// </summary>
        public static JArray CompareBar(JObject expected, JObject observed, StructuralTolerances tol)
        {
            var findings = new JArray();
            // A BAR THAT COULD NOT BE DESCRIBED AT ALL used to contribute zero
            // findings, and zero findings summarise to `agrees`. A total read failure
            // and a clean bar were the same reply.
            if (expected == null || observed == null)
            {
                findings.Add(Unknown(RebarFinding.Unreadable, null, -1, null,
                    expected == null
                        ? "there is nothing to compare against: the rule did not resolve."
                        : "the bar could not be described, so nothing about it was compared."));
                return findings;
            }
            string rule = (string)expected["rule_id"];
            long barId = observed.Value<long?>("id") ?? -1;

            // ------------------------------------------------------------ host
            long wantHost = expected.Value<long?>("host_id") ?? -1;
            JToken gotHostTok = observed["host"] == null ? null : observed["host"]["id"];
            bool hostResolved = observed["host"] != null && (bool?)observed["host"]["resolved"] == true;
            long gotHost = gotHostTok == null ? -1 : gotHostTok.Value<long>();
            if (!hostResolved)
                findings.Add(Finding(RebarFinding.HostMissing, RebarSeverity.Error, rule, barId,
                    wantHost, gotHost, "exact",
                    "the bar does not resolve to a host in this document.", false,
                    "horizun_apply_reinforcement"));
            else if (gotHost != wantHost)
                findings.Add(Finding(RebarFinding.HostMissing, RebarSeverity.Error, rule, barId,
                    wantHost, gotHost, "exact",
                    "the bar is hosted by a different element than the rule names.", true,
                    "horizun_apply_reinforcement"));

            // -------------------------------------------------------- bar type
            long wantType = Path(expected, "bar_type", "id")?.Value<long>() ?? -1;
            JToken gotTypeTok = Path(observed, "bar_type", "id");
            bool typeReadable = (bool?)Path(observed, "bar_type", "resolved") != false;
            if (gotTypeTok == null || !typeReadable)
                findings.Add(Unknown(RebarFinding.TypeDiffers, rule, barId, wantType,
                    "the bar type could not be read from the model."));
            else if (gotTypeTok.Value<long>() != wantType)
                findings.Add(Finding(RebarFinding.TypeDiffers, RebarSeverity.Error, rule, barId,
                    wantType, gotTypeTok.Value<long>(), "exact",
                    "the bar carries a different bar type than the rule declares.", true,
                    "horizun_apply_reinforcement"));

            double? wantDia = Path(expected, "bar_type", "nominal_diameter_mm")?.Value<double?>();
            double? gotDia = Path(observed, "bar_type", "nominal_diameter_mm")?.Value<double?>();
            if (wantDia.HasValue)
            {
                if (!gotDia.HasValue)
                    findings.Add(Unknown(RebarFinding.DiameterDiffers, rule, barId, wantDia,
                        "the model would not report a nominal diameter."));
                else if (Math.Abs(gotDia.Value - wantDia.Value) > tol.LengthMm)
                    findings.Add(Finding(RebarFinding.DiameterDiffers, RebarSeverity.Error, rule, barId,
                        wantDia, gotDia, tol.LengthMm + " mm",
                        "the bar diameter in the model differs from the one the declared bar type carries.",
                        true, "horizun_apply_reinforcement"));
            }

            // ----------------------------------------------------------- shape
            if ((bool?)Path(expected, "shape", "declared") == true)
            {
                long wantShape = Path(expected, "shape", "id")?.Value<long>() ?? -1;
                JToken gotShape = Path(observed, "shape", "id");
                // `resolved: false` is the reader saying it could not read the shape,
                // and it publishes -1 in that case. Comparing the -1 turned "I could
                // not look" into "the shape is definitely different", at severity
                // error and marked fixable.
                bool shapeReadable = (bool?)Path(observed, "shape", "resolved") != false;
                if (gotShape == null || !shapeReadable)
                    findings.Add(Unknown(RebarFinding.ShapeDiffers, rule, barId, wantShape,
                        "the shape could not be read."));
                else if (gotShape.Value<long>() != wantShape)
                    findings.Add(Finding(RebarFinding.ShapeDiffers, RebarSeverity.Error, rule, barId,
                        wantShape, gotShape.Value<long>(), "exact",
                        "the bar uses a different rebar shape than the rule declares.", true,
                        "horizun_apply_reinforcement"));
            }

            // ---------------------------------------------------------- layout
            string wantRule = (string)Path(expected, "layout", "rule");
            string gotRule = (string)Path(observed, "layout", "rule_horizun");
            if (gotRule == null)
                findings.Add(Unknown(RebarFinding.LayoutDiffers, rule, barId, wantRule,
                    "the model reports a layout rule this bridge has no word for; it is named in the raw reply."));
            else if (!string.Equals(gotRule, wantRule, StringComparison.Ordinal))
                findings.Add(Finding(RebarFinding.LayoutDiffers, RebarSeverity.Error, rule, barId,
                    wantRule, gotRule, "exact",
                    "the set is laid out by a different rule than the one declared.", true,
                    "horizun_apply_reinforcement"));

            CompareInt(findings, RebarFinding.QuantityDiffers, rule, barId,
                       Path(expected, "layout", "quantity"), Path(observed, "layout", "quantity"),
                       "the number of BARS standing differs from the number the layout predicted.");

            CompareInt(findings, RebarFinding.QuantityDiffers, rule, barId,
                       Path(expected, "layout", "number_of_bar_positions"),
                       Path(observed, "layout", "number_of_bar_positions"),
                       "the number of array POSITIONS differs from the number the layout predicted. Positions " +
                       "and bars are not the same count.");

            // A BOUND, NOT AN EQUALITY. Revit lays a set out over somewhere between
            // the declared array length and one MODEL bar diameter less than it,
            // and eleven measurements across Revit 2023 and 2026 found no rule that
            // says which - see RebarArrayGeometry. Comparing the declaration against
            // the model for equality raised a finding on every correctly built
            // array whose bar was thicker than the tolerance, which is every real
            // bar. The plan publishes the allowed shortfall; without it this falls
            // back to strict equality, which is the old behaviour and is only
            // reachable for a plan that could not read its bar type's diameter.
            CompareArrayLength(findings, rule, barId,
                               Path(expected, "layout", "array_length_mm"),
                               Path(observed, "layout", "array_length_mm"),
                               Path(expected, "layout", "array_length_shortfall_allowed_mm"),
                               tol.LengthMm);

            // Spacing is compared only where the layout HAS one. A single bar has
            // no spacing, and reporting a difference of null against null would be
            // a finding about nothing.
            // AGAINST THE PITCH MEASURED FROM THE BAR POSITIONS, not against
            // Rebar.MaxSpacing.
            //
            // MEASURED on Revit 2026: MaxSpacing is the value that was DECLARED to
            // the layout, not the pitch the bars ended up at. maximum_spacing of
            // 300 mm over a 1000 mm array reports MaxSpacing 300 and lays the bars
            // at 250; minimum_clear_spacing of 100 mm over 900 reports 100 and lays
            // them at 128.57, because that number is a CLEAR distance and the pitch
            // is centre to centre. Comparing the plan's resulting spacing against it
            // therefore raised spacing_differs on every correct set of those two
            // layouts - a false alarm against a model that is exactly right, which
            // is the mirror of the failure this repository is built to avoid.
            //
            // The pitch between consecutive bar positions is the same quantity the
            // plan computes, read from the geometry Revit produced. And it is an
            // ERROR rather than a note: a set at the wrong pitch is a set at the
            // wrong pitch.
            JToken wantSpacing = Path(expected, "layout", "resulting_spacing_mm");
            if (wantSpacing != null && wantSpacing.Type != JTokenType.Null)
                CompareLength(findings, RebarFinding.SpacingDiffers, rule, barId,
                              wantSpacing, Path(observed, "layout", "measured_pitch_mm"), tol.SpacingMm,
                              "the pitch measured between consecutive bar positions is not the spacing this " +
                              "layout produces.");

            // NOT ON A SINGLE BAR. There is no first or last bar of an array of one,
            // Revit raises rather than answering, and an audit that turned that into
            // an `unreadable` finding would report every correct single-bar rule as
            // INCOMPLETE - a partly audited model, for a question that never arose.
            bool singleBar = string.Equals(wantRule, RebarLayout.Single, StringComparison.Ordinal);
            if (!singleBar)
            {
                CompareBool(findings, RebarFinding.MissingFirstBar, rule, barId,
                            Path(expected, "layout", "include_first_bar"),
                            Path(observed, "layout", "include_first_bar"),
                            "the first bar of the set is included in one and not the other.");
                CompareBool(findings, RebarFinding.MissingLastBar, rule, barId,
                            Path(expected, "layout", "include_last_bar"),
                            Path(observed, "layout", "include_last_bar"),
                            "the last bar of the set is included in one and not the other.");
            }

            // ----------------------------------------------------------- style
            // Compared because CreateFromCurvesAndShape takes the style FROM THE
            // SHAPE: a rule declaring stirrup_tie against a Standard shape is caught
            // at plan time now, but a bar somebody built another way is not.
            CompareWord(findings, RebarFinding.StyleDiffers, rule, barId,
                        Path(expected, "style"), Path(observed, "style_horizun"),
                        "the bar's style differs from the one the rule declares. A stirrup and a standard bar " +
                        "are laid out and scheduled differently.");

            // ------------------------------------------- direction and side
            // THE SET'S DIRECTION. Without this, a set distributing along Y where
            // the rule says X matches on every other field - same count, same array
            // length, same type, same hooks - and the audit says `agrees` about
            // steel running the wrong way through the member.
            JToken wantN = Path(expected, "normal");
            JToken gotN = Path(observed, "layout", "normal");
            if (wantN != null)
            {
                if (gotN == null || gotN.Type == JTokenType.Null)
                    findings.Add(Unknown(RebarFinding.NormalDiffers, rule, barId, Show(wantN),
                        "the model would not report the direction this set marches in."));
                else
                {
                    double dot = Math.Abs(Dot(wantN, gotN));
                    // Unit vectors both: a dot product of 1 is the same axis. The
                    // SIGN is carried by bars_on_normal_side, so it is compared there
                    // rather than doubled up here.
                    if (dot < Math.Cos(tol.AngleDegrees * Math.PI / 180.0))
                        findings.Add(Finding(RebarFinding.NormalDiffers, RebarSeverity.Error, rule, barId,
                            Show(wantN), Show(gotN), tol.AngleDegrees + " degrees",
                            "the set marches in a different direction than the rule declares. Every count can " +
                            "match while the steel runs the wrong way through the member.", true,
                            "horizun_apply_reinforcement"));
                }
            }

            CompareBool(findings, RebarFinding.SideDiffers, rule, barId,
                        Path(expected, "layout", "bars_on_normal_side"),
                        Path(observed, "layout", "bars_on_normal_side"),
                        "the set marches to the other side of the declared bar. The count is the same and the " +
                        "steel is in the other half of the member.");

            // ------------------------------------------------------------ mark
            JToken wantMark = Path(expected, "mark");
            if (wantMark != null && wantMark.Type != JTokenType.Null)
                CompareWord(findings, RebarFinding.MarkDiffers, rule, barId, wantMark,
                            Path(observed, "measured", "schedule_mark"),
                            "the schedule mark on this set is not the one the rule declares, so it appears in " +
                            "the bending schedule under a different line or none.");

            // ---------------------------------------------------------- length
            // MEASURED steel against declared steel, and only where the comparison
            // is meaningful: Revit adds hook length itself, so a hooked bar always
            // reads longer than its declared centreline and comparing them would
            // fail on every correct one.
            JToken wantLen = Path(expected, "expected_total_steel_length_mm");
            bool hooked = HasHook(expected, "start") || HasHook(expected, "end");
            if (wantLen != null && wantLen.Type != JTokenType.Null && !hooked)
                CompareLength(findings, RebarFinding.LengthDiffers, rule, barId, wantLen,
                              Path(observed, "measured", "total_length_mm"),
                              Math.Max(tol.LengthMm, tol.LengthMm * QuantityOf(expected)),
                              "the steel in this set does not measure what the declared centreline and bar " +
                              "count come to. The bar has been reshaped, or bars are missing from the set.");

            // -------------------------------------------------------- geometry
            // POINT BY POINT, not just the total. The length comparison above
            // catches a bar somebody stretched; it passes a bar somebody reshaped
            // to the same length, and it is skipped entirely on a hooked bar.
            // AGAINST THE HOOK-FREE, BEND-FREE FORM when the model gives one. A
            // declaration draws sharp corners and no hooks; the bar as drawn has
            // both. Comparing the two reported the hook length as a difference on
            // every correctly built stirrup - an error-severity finding, marked
            // fixable, about a bar this bridge had verified minutes earlier.
            List<double[]> observedShape = PointsOf(Path(observed, "geometry", "centreline_points_as_declared_mm"));
            double bendAllowance = 0;
            if (observedShape == null)
            {
                // Fall back to the drawn form, and pay for it with an allowance.
                observedShape = PointsOf(Path(observed, "geometry", "centreline_points_mm"));
                bendAllowance = BendAllowanceMm(expected, observed);
            }
            CompareGeometry(findings, rule, barId,
                            PointsOf(expected["curve_mm"]),
                            observedShape,
                            (bool?)expected["closed"] == true,
                            tol.LengthMm,
                            bendAllowance);

            // ---------------------------------------------------- terminations
            var observedEnds = observed["terminations"] as JArray ?? new JArray();
            foreach (string which in new[] { "start", "end" })
            {
                int endIndex = which == "start" ? 0 : 1;
                JObject want = Path(expected, "terminations", which) as JObject;
                JObject got = observedEnds.OfType<JObject>()
                    .FirstOrDefault(o => (o.Value<int?>("end") ?? -1) == endIndex);
                if (want == null) continue;
                // A TERMINATION THE MODEL DID NOT REPORT IS NOT AN AGREEING ONE.
                // This used to `continue` on a missing row, so an empty or short
                // terminations array - which RebarFacts emits whenever the read
                // threw - produced no finding at all about a declared hook.
                if (got == null)
                {
                    findings.Add(Unknown(RebarFinding.HookDiffers, rule, barId, want.Value<long?>("hook_type_id"),
                        "the model reported nothing about the " + which + " end of this bar."));
                    continue;
                }

                long wantHook = want.Value<long?>("hook_type_id") ?? -1;
                long gotHook = got.Value<long?>("hook_type_id") ?? -1;
                // -1 means TWO THINGS on the observed side - no hook, and a hook that
                // could not be read - and RebarFacts now says which.
                if (got["hook_readable"] != null && got.Value<bool?>("hook_readable") == false)
                    findings.Add(Unknown(RebarFinding.HookDiffers, rule, barId, wantHook,
                        "the hook type at the " + which + " end could not be read."));
                else if (wantHook != gotHook)
                    findings.Add(Finding(RebarFinding.HookDiffers, RebarSeverity.Error, rule, barId,
                        wantHook, gotHook, "exact",
                        "the hook type at the " + which + " end differs from the rule.", true,
                        "horizun_apply_reinforcement"));

                string wantOr = want.Value<string>("orientation");
                string gotOr = got.Value<string>("orientation");
                if (gotOr == null)
                    findings.Add(Unknown(RebarFinding.OrientationDiffers, rule, barId, wantOr,
                        "the model would not report the termination orientation at the " + which + " end."));
                else if (!string.Equals(wantOr, gotOr, StringComparison.Ordinal))
                    findings.Add(Finding(RebarFinding.OrientationDiffers, RebarSeverity.Error, rule, barId,
                        wantOr, gotOr, "exact",
                        "the termination at the " + which + " end turns the other way.", true,
                        "horizun_apply_reinforcement"));
            }

            return findings;
        }

        /// <summary>
        /// Provenance findings: is this bar ours, does it name the set that is being
        /// audited, and does it still agree with it.
        /// </summary>
        public static JArray CheckProvenance(JObject observedProvenance, string ruleId, long barId,
                                             string setId, string setSha)
        {
            var findings = new JArray();
            if (observedProvenance == null || (bool?)observedProvenance["written"] != true)
            {
                findings.Add(Finding(RebarFinding.ProvenanceMissing, RebarSeverity.Info, ruleId, barId,
                    setId, null, "exact",
                    "this bar carries no Horizun provenance. That is not a fault - a bar somebody modelled by " +
                    "hand carries none either - but it means nothing here can say which rule it came from, and " +
                    "an update cannot claim it.", false, null));
                return findings;
            }
            // THE SET IT NAMES. This was passed in and never compared: a bar built
            // from an entirely different requirement set, on the same host under the
            // same rule id, read as agreement.
            string gotSetId = (string)observedProvenance["requirement_set_id"];
            if (!string.IsNullOrEmpty(setId) && !string.IsNullOrEmpty(gotSetId) &&
                !string.Equals(gotSetId, setId, StringComparison.Ordinal))
                findings.Add(Finding(RebarFinding.StaleRequirementSet, RebarSeverity.Error, ruleId, barId,
                    setId, gotSetId, "exact",
                    "this bar records a DIFFERENT requirement set, not merely a different version of this one. " +
                    "Two sets are writing to the same host under the same rule id.", false, null));

            string gotSha = (string)observedProvenance["requirement_set_sha256"];
            if (string.IsNullOrEmpty(gotSha))
                findings.Add(Unknown(RebarFinding.StaleRequirementSet, ruleId, barId, setSha,
                    "this bar carries provenance with no requirement-set digest, so whether it was built from " +
                    "this set or another cannot be decided."));
            else if (!string.IsNullOrEmpty(setSha) &&
                     !string.Equals(gotSha, setSha, StringComparison.Ordinal))
                findings.Add(Finding(RebarFinding.StaleRequirementSet, RebarSeverity.Info, ruleId, barId,
                    setSha, gotSha, "exact",
                    "this bar was built from a DIFFERENT version of the requirement set. Whether that matters " +
                    "depends on what changed between them; it is reported rather than judged.", true,
                    "horizun_apply_reinforcement"));
            return findings;
        }

        /// <summary>Two bars carrying one schedule mark. Every schedule then counts them as one.</summary>
        public static JArray DuplicateMarks(IEnumerable<JObject> observedBars)
        {
            var findings = new JArray();
            var byMark = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            foreach (JObject b in observedBars)
            {
                string mark = (string)Path(b, "measured", "schedule_mark");
                if (string.IsNullOrWhiteSpace(mark)) continue;
                if (!byMark.ContainsKey(mark)) byMark[mark] = new List<long>();
                byMark[mark].Add(b.Value<long?>("id") ?? -1);
            }
            foreach (var pair in byMark.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (pair.Value.Count < 2) continue;
                JObject f = Finding(RebarFinding.BarMarkDuplicate, RebarSeverity.Error, null, pair.Value[0],
                    "one bar per mark", pair.Value.Count + " bars carry mark '" + pair.Key + "'", "exact",
                    "a schedule groups by mark, so these are counted as one line and the steel of the others " +
                    "never appears. Revit permits it: duplicate marks are a warning, not a refusal.", true,
                    "horizun_write_params_verified");
                f["rebar_ids"] = new JArray(pair.Value.Cast<object>().ToArray());
                findings.Add(f);
            }
            return findings;
        }

        // ---------------------------------------------------------- comparisons

        private static void CompareInt(JArray into, string code, string rule, long barId,
                                       JToken want, JToken got, string why)
        {
            if (want == null || want.Type == JTokenType.Null) return;
            if (got == null || got.Type == JTokenType.Null)
            {
                into.Add(Unknown(code, rule, barId, want, "the model would not report this count."));
                return;
            }
            if (want.Value<int>() != got.Value<int>())
                into.Add(Finding(code, RebarSeverity.Error, rule, barId, want.Value<int>(), got.Value<int>(),
                                 "exact", why, true, "horizun_apply_reinforcement"));
        }

        /// <summary>
        /// The array length, held to the measured BOUND rather than to equality.
        ///
        /// A model that reports between the declared length and one MODEL bar
        /// diameter less than it is what Revit does - eleven measurements across
        /// Revit 2023 and 2026, with no rule found that says how much of the
        /// diameter any given bar loses. Anything outside that bound is a finding,
        /// including an array LONGER than declared: nothing measured has ever
        /// produced one, and an unknown is not a pass.
        ///
        /// Without an allowance the bound collapses to equality, which is the old
        /// behaviour. That is only reachable for a plan whose bar type would not
        /// report a model diameter.
        /// </summary>
        private static void CompareArrayLength(JArray into, string rule, long barId,
                                               JToken declared, JToken model, JToken allowedShortfall,
                                               double tolMm)
        {
            if (declared == null || declared.Type == JTokenType.Null) return;
            if (model == null || model.Type == JTokenType.Null)
            {
                into.Add(Unknown(RebarFinding.ArrayLengthDiffers, rule, barId, declared,
                                 "the model would not report the array length."));
                return;
            }

            double want = declared.Value<double>();
            double got = model.Value<double>();
            double allowed = 0;
            if (allowedShortfall != null && allowedShortfall.Type != JTokenType.Null)
            {
                double a = allowedShortfall.Value<double>();
                if (a > 0) allowed = a;
            }

            double shortfall = want - got;
            if (shortfall >= -tolMm && shortfall <= allowed + tolMm) return;

            into.Add(Finding(RebarFinding.ArrayLengthDiffers, RebarSeverity.Error, rule, barId,
                             Math.Round(want, 3), Math.Round(got, 3),
                             Mm(allowed) + " short, plus " + tolMm + " mm",
                             "the array in the model differs from the declared length by " + Mm(shortfall) +
                             ", against a measured allowance of " + Mm(allowed) + " - one model bar diameter. " +
                             "Revit fits the bars into the length it is given and can come up to a diameter " +
                             "short; more than that means something moved the array rather than Revit laying " +
                             "it out.",
                             true, "horizun_apply_reinforcement"));
        }

        private static void CompareLength(JArray into, string code, string rule, long barId,
                                          JToken want, JToken got, double tolMm, string why,
                                          string severity = RebarSeverity.Error)
        {
            if (want == null || want.Type == JTokenType.Null) return;
            if (got == null || got.Type == JTokenType.Null)
            {
                into.Add(Unknown(code, rule, barId, want, "the model would not report this length."));
                return;
            }
            double w = want.Value<double>(), g = got.Value<double>();
            if (Math.Abs(w - g) > tolMm)
                into.Add(Finding(code, severity, rule, barId, Math.Round(w, 3), Math.Round(g, 3),
                                 tolMm + " mm", why, true, "horizun_apply_reinforcement"));
        }

        private static void CompareBool(JArray into, string code, string rule, long barId,
                                        JToken want, JToken got, string why)
        {
            if (want == null || want.Type == JTokenType.Null) return;
            if (got == null || got.Type == JTokenType.Null)
            {
                into.Add(Unknown(code, rule, barId, want, "the model would not report this flag."));
                return;
            }
            if (want.Value<bool>() != got.Value<bool>())
                into.Add(Finding(code, RebarSeverity.Error, rule, barId, want.Value<bool>(), got.Value<bool>(),
                                 "exact", why, true, "horizun_apply_reinforcement"));
        }

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

        /// <summary>The tally, and the one honest verdict that can be drawn from it.</summary>
        public static JObject Summarise(JArray findings)
        {
            int errors = 0, unknown = 0, info = 0;
            var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (JObject f in findings.OfType<JObject>())
            {
                string sev = (string)f["severity"];
                if (sev == RebarSeverity.Error) errors++;
                else if (sev == RebarSeverity.Unknown) unknown++;
                else info++;
                string code = (string)f["code"];
                byCode[code] = (byCode.ContainsKey(code) ? byCode[code] : 0) + 1;
            }
            var counts = new JObject();
            foreach (var p in byCode.OrderBy(p => p.Key, StringComparer.Ordinal)) counts[p.Key] = p.Value;
            return new JObject
            {
                ["findings"] = findings.Count,
                ["errors"] = errors,
                ["unknown"] = unknown,
                ["info"] = info,
                ["by_code"] = counts,
                // NOT "passed", and FOUR words rather than three. `agrees` used to be
                // returned whenever `errors` and `unknown` were both zero - which
                // included every reply carrying an `info` finding, and info is where
                // "this bar was built from a different version of the set" and "this
                // bar carries no provenance" live. The verdict line said every
                // property matched, directly above findings saying otherwise.
                ["verdict"] = errors > 0 ? "differences_found"
                            : unknown > 0 ? "incomplete"
                            : info > 0 ? "agrees_with_notes"
                            : "agrees",
                ["verdict_means"] =
                    "agrees: every property this bridge checks was read, matched, and there is nothing else to " +
                    "report. agrees_with_notes: nothing disagreed and nothing was unreadable, AND there are " +
                    "findings you should read - a bar with no provenance, or one built from another version of " +
                    "this set. incomplete: nothing disagreed AND something could not be read, so the model has " +
                    "not been audited clean - it has been partly audited. differences_found: at least one " +
                    "property does not match.",
                ["not_checked"] = new JArray(RebarFinding.NotChecked)
            };
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

        /// <summary>
        /// How far a declared sharp corner may sit from the arc Revit draws there.
        /// For a bend of radius R turning a right angle, the corner is R(sqrt2 - 1)
        /// off the arc - about 0.41R - and that is the worst case for any turn up to
        /// ninety degrees. The radius comes from the bar type when the model reports
        /// it; when it does not, this returns zero rather than inventing a number,
        /// and the comparison is correspondingly strict.
        /// </summary>
        public static double BendAllowanceMm(JObject expected, JObject observed)
        {
            // A stirrup bends to a different radius than a straight bar, and Revit
            // keeps the two numbers separately on the bar type. Reading the wrong
            // one is a silent few millimetres of allowance in the wrong direction.
            // NO DECLARED STYLE MEANS NO ALLOWANCE. The fallback used to be the
            // standard bend diameter, which is the LARGER of the two in practice -
            // so the unknown case bought more slack, in a file whose rule is that
            // unknown is never a pass. And when the model DISAGREES with the
            // declaration about the style, Revit drew with the style it has, so that
            // is the bend radius the comparison must allow for.
            JToken declaredStyle = expected["style"];
            if (declaredStyle == null || declaredStyle.Type == JTokenType.Null) return 0;
            string style = (string)(observed["style_horizun"] ?? declaredStyle);
            string field = style == StructuralStyle.StirrupTie
                ? "stirrup_tie_bend_diameter_mm"
                : "standard_bend_diameter_mm";
            double? d = Path(observed, "bar_type", field)?.Value<double?>()
                        ?? Path(expected, "bar_type", field)?.Value<double?>();
            if (!d.HasValue || double.IsNaN(d.Value) || double.IsInfinity(d.Value) || d.Value <= 0) return 0;
            return 0.4143 * (d.Value / 2.0);
        }


        /// <summary>
        /// The declared centreline against the one Revit drew, point by point.
        ///
        /// The measured LENGTH was the only geometric comparison before this, and a
        /// stirrup somebody stretched from 220x220 to 300x300 keeps its type, its
        /// host, its quantity, its array length and its shape id - so a set could be
        /// reshaped and still agree on everything the audit looked at.
        ///
        /// Two things make this harder than subtracting coordinates. Revit BENDS the
        /// bar: a declared sharp corner arrives as a fillet, and the declared corner
        /// then sits up to 0.41 of the bend radius off the path Revit actually drew.
        /// That allowance is passed in and published rather than absorbed into a
        /// generous tolerance. And the comparison must be TWO-SIDED: measuring only
        /// the declared points against the drawn path misses an excursion the model
        /// has and the declaration does not.
        /// </summary>
        public static void CompareGeometry(JArray into, string rule, long barId,
            IList<double[]> want, IList<double[]> got, bool closed,
            double toleranceMm, double bendAllowanceMm)
        {
            if (want == null || want.Count < 2) return;   // nothing was declared to compare

            if (got == null || got.Count < 2)
            {
                into.Add(Unknown(RebarFinding.GeometryDiffers, rule, barId, want.Count,
                    "the bar would not return a centreline, so its shape was not compared. " +
                    "Unreadable is not agreement."));
                return;
            }

            double allowance = toleranceMm + Math.Max(0, bendAllowanceMm);

            // REVERSED. A bar drawn end-for-end passes every distance-based
            // comparison, so it is looked for first - but it is NOT a reason to stop
            // looking. This used to return here, which meant a bar that was reversed
            // AND reshaped was reported as merely reversed, under a sentence
            // asserting that its shape was unchanged. Three things stated as fact
            // that had never been measured.
            //
            // The endpoints are judged on the declared tolerance ALONE. The bend
            // allowance is a corner phenomenon and has no business at a bar's ends:
            // on a large bar it is tens of millimetres, and both ends could be that
            // far out while the bar was still called "merely reversed".
            if (!closed)
            {
                bool forward = Near(want[0], got[0], toleranceMm) &&
                               Near(want[want.Count - 1], got[got.Count - 1], toleranceMm);
                bool backward = Near(want[0], got[got.Count - 1], toleranceMm) &&
                                Near(want[want.Count - 1], got[0], toleranceMm);
                if (!forward && backward)
                    into.Add(Finding(RebarFinding.GeometryReversed, RebarSeverity.Error, rule, barId,
                        Pt(want[0]), Pt(got[0]), Mm(toleranceMm),
                        "the bar runs from the declared END to the declared START. Its hooks, its " +
                        "terminations and its bar mark are at the wrong ends. Whether anything ELSE about it " +
                        "differs is reported separately - this finding is about direction only.",
                        true, "horizun_apply_reinforcement"));
            }

            // CLOSEDNESS IS A PROPERTY OF EACH POLYLINE, not a flag to impose on
            // both. `closed` comes from the DECLARATION, and it was applied to the
            // observed path too - synthesising a closing segment the model may not
            // have, so a stirrup Revit drew with a leg missing had that leg invented
            // for it and the comparison agreed.
            bool gotClosed = Near(got[0], got[got.Count - 1], Math.Max(toleranceMm, 1e-6));
            if (closed != gotClosed)
                into.Add(Finding(RebarFinding.GeometryDiffers, RebarSeverity.Error, rule, barId,
                    closed ? "a closed shape" : "an open shape",
                    gotClosed ? "a closed shape" : "an open shape", Mm(toleranceMm),
                    "the declaration draws a " + (closed ? "closed" : "open") + " bar and the model carries " +
                    "an " + (gotClosed ? "closed" : "open") + " one. A closed stirrup with a leg missing is " +
                    "an open bar, and comparing it as though it closed would invent the missing leg.",
                    true, "horizun_apply_reinforcement"));

            double aToB = WorstDistanceToPath(want, got, closed && gotClosed);
            double bToA = WorstDistanceToPath(got, want, closed && gotClosed);
            if (double.IsNaN(aToB) || double.IsNaN(bToA))
            {
                into.Add(Unknown(RebarFinding.GeometryDiffers, rule, barId, null,
                    "a point of one of the two centrelines was not a finite number, so they were not " +
                    "compared."));
                return;
            }

            double worst = Math.Max(aToB, bToA);
            if (worst > allowance)
            {
                into.Add(Finding(RebarFinding.GeometryDiffers, RebarSeverity.Error, rule, barId,
                    Mm(0), Mm(worst), Mm(allowance),
                    "the centreline in the model departs from the declared one by " + Mm(worst) +
                    " at its worst point, measured both ways so an excursion in either is caught. " +
                    "The allowance includes " + Mm(bendAllowanceMm) + " for the bend Revit puts in a " +
                    "corner the declaration draws sharp.", true, "horizun_apply_reinforcement"));
            }

            // THE PLANE. A flat bar rotated about its own long axis keeps every
            // vertex the same distance from the declared path only if it is
            // straight; for a shape it does not, so this mostly catches the case
            // the distance comparison already flags. It is here because it names
            // WHAT is wrong - a stirrup lying in the wrong plane is a different
            // finding from one that is the wrong size.
            // ONLY BETWEEN TWO THINGS THAT HAVE A PLANE. A polyline that is not
            // planar does not lie in one, so the "difference" between its fitted
            // plane and anything else is a property of the fitting rather than of
            // the bar. Whatever is wrong with a non-planar bar is reported by the
            // distance comparison above, which measures it rather than inferring it.
            double wantFlat, gotFlat;
            bool wantPlanar = RebarPlanRules.IsPlanar(want, toleranceMm, out wantFlat);
            bool gotPlanar = RebarPlanRules.IsPlanar(got, toleranceMm, out gotFlat);
            double[] wantNormal = wantPlanar ? RebarPlanRules.BestFitNormal(want) : null;
            double[] gotNormal = gotPlanar ? RebarPlanRules.BestFitNormal(got) : null;
            if (wantNormal != null && gotNormal != null)
            {
                double dot = Math.Abs(wantNormal[0] * gotNormal[0] + wantNormal[1] * gotNormal[1] +
                                      wantNormal[2] * gotNormal[2]);
                if (dot > 1) dot = 1;
                double degrees = Math.Acos(dot) * 180.0 / Math.PI;
                // The lever the tolerance is measured over is the GEOMETRY'S OWN
                // reach, not a metre. A fixed metre made the same declared tolerance
                // five times stricter on a 220 mm stirrup and four times looser on a
                // four-metre bar.
                double limit = AngleToleranceDegrees(toleranceMm, Reach(want));
                if (degrees > limit)
                    into.Add(Finding(RebarFinding.PlaneDiffers, RebarSeverity.Error, rule, barId,
                        0, Math.Round(degrees, 3), Math.Round(limit, 4) + " degrees",
                        "the bar lies in a different plane than the declaration draws it in, by " +
                        Math.Round(degrees, 2) + " degrees - which over this bar's " + Mm(Reach(want)) +
                        " reach is more than the declared " + Mm(toleranceMm) + ". The sign of a normal is " +
                        "not compared: a plane does not have a front.", true, "horizun_apply_reinforcement"));
            }
        }

        /// <summary>
        /// How far the worst point of one polyline is from the other polyline - not
        /// from its nearest VERTEX, which would call a straight bar and a zigzag
        /// through the same endpoints identical.
        /// </summary>
        public static double WorstDistanceToPath(IList<double[]> points, IList<double[]> path, bool closed)
        {
            double worst = 0;
            foreach (double[] p in points)
            {
                double d = DistanceToPath(p, path, closed);
                if (double.IsNaN(d)) return double.NaN;
                if (d > worst) worst = d;
            }
            return worst;
        }

        public static double DistanceToPath(double[] p, IList<double[]> path, bool closed)
        {
            if (p == null || p.Length < 3 || path == null || path.Count == 0) return double.NaN;
            for (int k = 0; k < 3; k++) if (!Ok(p[k])) return double.NaN;

            double best = double.MaxValue;
            int n = path.Count;
            int last = closed ? n : n - 1;
            for (int i = 0; i < last; i++)
            {
                double[] a = path[i], b = path[(i + 1) % n];
                if (a == null || b == null || a.Length < 3 || b.Length < 3) return double.NaN;
                double d = PointSegment(p, a, b);
                if (double.IsNaN(d)) return double.NaN;
                if (d < best) best = d;
            }
            if (last == 0)
            {
                double d = PointSegment(p, path[0], path[0]);
                if (d < best) best = d;
            }
            return best;
        }

        private static double PointSegment(double[] p, double[] a, double[] b)
        {
            double vx = b[0] - a[0], vy = b[1] - a[1], vz = b[2] - a[2];
            double wx = p[0] - a[0], wy = p[1] - a[1], wz = p[2] - a[2];
            if (!Ok(vx) || !Ok(vy) || !Ok(vz) || !Ok(wx) || !Ok(wy) || !Ok(wz)) return double.NaN;
            double vv = vx * vx + vy * vy + vz * vz;
            double t = vv < 1e-18 ? 0 : (wx * vx + wy * vy + wz * vz) / vv;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            double dx = wx - vx * t, dy = wy - vy * t, dz = wz - vz * t;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static bool Ok(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static bool Near(double[] a, double[] b, double tol)
        {
            if (a == null || b == null || a.Length < 3 || b.Length < 3) return false;
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            if (!Ok(dx) || !Ok(dy) || !Ok(dz)) return false;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= tol;
        }

        /// <summary>
        /// The angle two directions may differ by, for a shape of a given reach.
        ///
        /// One declared number still governs both, but the lever it is measured over
        /// is the geometry's own: a fixed metre made the same tolerance five times
        /// stricter on a 220 mm stirrup than the declaration asked for, and four
        /// times looser on a four-metre bar.
        /// </summary>
        public static double AngleToleranceDegrees(double toleranceMm, double leverMm)
        {
            double t = toleranceMm > 0 ? toleranceMm : 1.0;
            double lever = leverMm > 1.0 ? leverMm : 1.0;
            return Math.Round(Math.Atan2(t, lever) * 180.0 / Math.PI, 4);
        }

        /// <summary>How far the furthest point of a polyline is from its centroid.</summary>
        public static double Reach(IList<double[]> points)
        {
            if (points == null || points.Count == 0) return 0;
            var c = new double[3];
            foreach (double[] p in points)
            {
                if (p == null || p.Length < 3) return 0;
                c[0] += p[0]; c[1] += p[1]; c[2] += p[2];
            }
            c[0] /= points.Count; c[1] /= points.Count; c[2] /= points.Count;
            double worst = 0;
            foreach (double[] p in points)
            {
                double dx = p[0] - c[0], dy = p[1] - c[1], dz = p[2] - c[2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > worst) worst = d;
            }
            return worst;
        }

        private static string Pt(double[] p)
        {
            if (p == null || p.Length < 3) return "(none)";
            return "(" + Mm(p[0]) + ", " + Mm(p[1]) + ", " + Mm(p[2]) + ")";
        }

        private static string Mm(double v)
        {
            return Math.Round(v, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + " mm";
        }

        /// <summary>Compare two words for exact equality, with absence reported rather than assumed.</summary>
        private static void CompareWord(JArray into, string code, string rule, long barId,
                                        JToken want, JToken got, string why)
        {
            if (want == null || want.Type == JTokenType.Null) return;
            if (got == null || got.Type == JTokenType.Null)
            {
                into.Add(Unknown(code, rule, barId, (string)want, "the model would not report this value."));
                return;
            }
            if (!string.Equals((string)want, (string)got, StringComparison.Ordinal))
                into.Add(Finding(code, RebarSeverity.Error, rule, barId, (string)want, (string)got,
                                 "exact", why, true, "horizun_apply_reinforcement"));
        }

        private static double Dot(JToken a, JToken b)
        {
            double ax = a["x"]?.Value<double>() ?? 0, ay = a["y"]?.Value<double>() ?? 0, az = a["z"]?.Value<double>() ?? 0;
            double bx = b["x"]?.Value<double>() ?? 0, by = b["y"]?.Value<double>() ?? 0, bz = b["z"]?.Value<double>() ?? 0;
            double na = Math.Sqrt(ax * ax + ay * ay + az * az);
            double nb = Math.Sqrt(bx * bx + by * by + bz * bz);
            if (na < 1e-12 || nb < 1e-12) return 0;
            return (ax * bx + ay * by + az * bz) / (na * nb);
        }

        private static string Show(JToken v)
        {
            return v == null ? null : v.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool HasHook(JObject expected, string which)
        {
            JToken t = Path(expected, "terminations", which, "has_hook");
            return t != null && t.Type == JTokenType.Boolean && t.Value<bool>();
        }

        private static int QuantityOf(JObject expected)
        {
            JToken q = Path(expected, "layout", "quantity");
            return q == null || q.Type == JTokenType.Null ? 1 : Math.Max(1, q.Value<int>());
        }

        private static JToken Path(JObject o, params string[] keys)
        {
            JToken cur = o;
            foreach (string k in keys)
            {
                if (cur == null) return null;
                cur = cur[k];
            }
            return cur;
        }
    }
}
