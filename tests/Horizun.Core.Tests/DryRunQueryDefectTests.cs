// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// DRY-RUN DEFECTS 9, 10, 11 AND 16, PROVED AT A DESK.
//
//   9  horizun_query_model grouped 397 beams, 91 rooms, 6 roofs and 5 stairs under
//      "(no level)": a `??` chain stopped at the first level parameter that EXISTED,
//      not the first that named a level.
//  10  horizun_quantities mode=takeoff read HOST_AREA_COMPUTED as a display name
//      ("absent") and "Type Name" on the instance ("(empty)").
//  11  sum_parameters summed square feet and said nothing about it.
//  16  audit_model and model_scan counted views off sheets / without template as
//      28 / 36 and 34 / 42 with nothing saying the difference was the schedules.
//
// Each fake below stands in for the Revit half: it answers what a source holds,
// and the rules decide.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DryRunQueryDefectTests
    {
        // ================================================================ #9 levels

        private static Func<string, LevelProbe> Fake(Dictionary<string, LevelProbe> sources) =>
            s => sources.TryGetValue(s, out LevelProbe p) ? p : LevelProbe.Absent();

        [Fact]
        public void A_beam_resolves_to_its_reference_level_past_level_parameters_that_hold_no_level()
        {
            // THE MEASURED FAILURE. A beam: LevelId invalid, a schedule/family level
            // parameter present but empty, the Reference Level holding the answer. The old
            // chain stopped at the first parameter that existed and answered "(no level)".
            var beam = new Dictionary<string, LevelProbe>
            {
                [LevelResolutionRules.ElementLevelId] = LevelProbe.NoLevel(),
                ["FAMILY_LEVEL_PARAM"] = LevelProbe.NoLevel(),
                ["SCHEDULE_LEVEL_PARAM"] = LevelProbe.NoLevel(),
                ["INSTANCE_REFERENCE_LEVEL_PARAM"] = LevelProbe.Found("02 - Floor")
            };
            LevelResolution r = LevelResolutionRules.Resolve(Fake(beam));
            Assert.Equal("02 - Floor", r.LevelName);
            Assert.Equal("INSTANCE_REFERENCE_LEVEL_PARAM", r.Source);
        }

        [Fact]
        public void Rooms_roofs_and_stairs_resolve_from_their_own_sources()
        {
            LevelResolution room = LevelResolutionRules.Resolve(Fake(new Dictionary<string, LevelProbe>
            { [LevelResolutionRules.ElementLevelId] = LevelProbe.Found("01 - Entry Level") }));
            Assert.Equal("01 - Entry Level", room.LevelName);
            Assert.Equal(LevelResolutionRules.ElementLevelId, room.Source);

            LevelResolution roof = LevelResolutionRules.Resolve(Fake(new Dictionary<string, LevelProbe>
            {
                [LevelResolutionRules.ElementLevelId] = LevelProbe.NoLevel(),
                ["ROOF_BASE_LEVEL_PARAM"] = LevelProbe.Found("Roof")
            }));
            Assert.Equal("ROOF_BASE_LEVEL_PARAM", roof.Source);

            LevelResolution stair = LevelResolutionRules.Resolve(Fake(new Dictionary<string, LevelProbe>
            {
                [LevelResolutionRules.ElementLevelId] = LevelProbe.NoLevel(),
                ["STAIRS_BASE_LEVEL_PARAM"] = LevelProbe.Found("01 - Entry Level")
            }));
            Assert.Equal("STAIRS_BASE_LEVEL_PARAM", stair.Source);
            Assert.Equal("01 - Entry Level", stair.LevelName);
        }

        [Fact]
        public void The_first_source_that_names_a_level_wins_in_the_published_order()
        {
            // LevelId before any parameter; base/reference before the schedule level.
            var both = new Dictionary<string, LevelProbe>
            {
                [LevelResolutionRules.ElementLevelId] = LevelProbe.Found("A"),
                ["SCHEDULE_LEVEL_PARAM"] = LevelProbe.Found("B")
            };
            Assert.Equal("A", LevelResolutionRules.Resolve(Fake(both)).LevelName);

            var instance = new Dictionary<string, LevelProbe>
            {
                ["INSTANCE_REFERENCE_LEVEL_PARAM"] = LevelProbe.Found("Ref"),
                [LevelResolutionRules.HostLevel] = LevelProbe.Found("Host"),
                ["SCHEDULE_LEVEL_PARAM"] = LevelProbe.Found("Sched")
            };
            Assert.Equal("INSTANCE_REFERENCE_LEVEL_PARAM", LevelResolutionRules.Resolve(Fake(instance)).Source);

            var order = LevelResolutionRules.Sources.ToList();
            Assert.Equal(LevelResolutionRules.ElementLevelId, order[0]);
            Assert.True(order.IndexOf("WALL_BASE_CONSTRAINT") < order.IndexOf("SCHEDULE_LEVEL_PARAM"));
            Assert.True(order.IndexOf("INSTANCE_REFERENCE_LEVEL_PARAM") < order.IndexOf(LevelResolutionRules.HostLevel));
            Assert.Equal("SCHEDULE_LEVEL_PARAM", order[order.Count - 1]);
            Assert.Equal(order.Count, order.Distinct().Count());
        }

        [Fact]
        public void No_level_is_kept_only_when_every_source_was_asked_and_none_named_one()
        {
            var asked = new List<string>();
            LevelResolution r = LevelResolutionRules.Resolve(s => { asked.Add(s); return LevelProbe.NoLevel(); });
            Assert.False(r.Resolved);
            Assert.Null(r.LevelName);
            Assert.Equal(LevelResolutionRules.NoSourceLabel, LevelResolutionRules.SourceKey(r));
            Assert.Equal(LevelResolutionRules.Sources, asked);
        }

        [Fact]
        public void An_unreadable_source_is_counted_and_passed_over_not_read_as_no_level()
        {
            var sources = new Dictionary<string, LevelProbe>
            {
                [LevelResolutionRules.ElementLevelId] = LevelProbe.Failed("threw"),
                ["WALL_BASE_CONSTRAINT"] = LevelProbe.Found("L1")
            };
            LevelResolution r = LevelResolutionRules.Resolve(Fake(sources));
            Assert.Equal("L1", r.LevelName);
            Assert.Equal(1, r.UnreadableSources);

            LevelResolution thrown = LevelResolutionRules.Resolve(s => throw new InvalidOperationException("boom"));
            Assert.False(thrown.Resolved);
            Assert.Equal(LevelResolutionRules.Sources.Count, thrown.UnreadableSources);
        }

        [Fact]
        public void A_level_without_a_readable_name_is_still_a_level()
        {
            LevelResolution r = LevelResolutionRules.Resolve(Fake(new Dictionary<string, LevelProbe>
            { [LevelResolutionRules.ElementLevelId] = LevelProbe.Found(null) }));
            Assert.True(r.Resolved);
            Assert.NotEqual(LevelResolutionRules.NoLevelLabel, r.LevelName);
        }

        [Fact]
        public void The_summary_counts_levels_by_source_and_keeps_its_old_shape_without_sources()
        {
            var s = new QuerySummaryAccumulator();
            s.Add("Structural Framing", "02 - Floor", "host", "M", null, 1, "INSTANCE_REFERENCE_LEVEL_PARAM");
            s.Add("Rooms", "01 - Entry Level", "host", "M", null, 2, LevelResolutionRules.ElementLevelId);
            s.Add("Grids", null, "host", "M", null, 3, LevelResolutionRules.NoSourceLabel);
            JObject json = s.ToJson();
            Assert.Equal(1, (int)json["by_level_source"]["INSTANCE_REFERENCE_LEVEL_PARAM"]);
            Assert.Equal(1, (int)json["by_level_source"][LevelResolutionRules.NoSourceLabel]);
            Assert.Equal(1, (int)json["by_level"]["(no level)"]);

            var old = new QuerySummaryAccumulator();
            old.Add("Walls", "L1", "host", "M");
            Assert.Null(old.ToJson()["by_level_source"]);
        }

        // ======================================================= #10 parameter resolver

        private static readonly HashSet<string> Bips = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "HOST_AREA_COMPUTED", "SYMBOL_NAME_PARAM", "ALL_MODEL_TYPE_NAME", "SYMBOL_FAMILY_NAME_PARAM", "ALL_MODEL_FAMILY_NAME" };

        private static bool IsBip(string s) => s != null && Bips.Contains(s);

        /// <summary>A fake element pair: (scope, kind, token) -> (found, value).</summary>
        private static Func<ParameterProbe, ParameterProbeResult<string>> FakeElement(
            Dictionary<string, string> instanceByToken, Dictionary<string, string> typeByToken)
        {
            return step =>
            {
                Dictionary<string, string> on = step.Scope == ParameterResolutionRules.Type ? typeByToken : instanceByToken;
                if (on == null || !on.TryGetValue(step.Kind + ":" + step.Token, out string v))
                    return ParameterProbeResult<string>.Absent();
                return ParameterProbeResult<string>.Of(step.Scope + "=" + v, !string.IsNullOrEmpty(v));
            };
        }

        [Fact]
        public void A_builtin_token_is_looked_up_as_a_builtin_parameter_not_as_a_display_name()
        {
            // THE MEASURED FAILURE: takeoff answered "absent" for HOST_AREA_COMPUTED.
            Assert.Equal(ParameterSpecKind.BuiltIn, ParameterResolutionRules.KindOf("HOST_AREA_COMPUTED", IsBip));
            Assert.Equal(ParameterSpecKind.Guid, ParameterResolutionRules.KindOf(Guid.NewGuid().ToString(), IsBip));
            Assert.Equal(ParameterSpecKind.Name, ParameterResolutionRules.KindOf("Area", IsBip));

            var wall = new Dictionary<string, string> { ["BuiltIn:HOST_AREA_COMPUTED"] = "12.5" };
            var plan = ParameterResolutionRules.Plan("HOST_AREA_COMPUTED", IsBip, hasDistinctType: true);
            ParameterResolution<string> r = ParameterResolutionRules.Resolve(plan, FakeElement(wall, null));
            Assert.True(r.Found);
            Assert.Equal("instance=12.5", r.Parameter);
        }

        [Fact]
        public void An_ordinary_spec_reads_the_instance_first_then_the_type_unchanged()
        {
            var inst = new Dictionary<string, string> { ["Name:Comments"] = "" };
            var type = new Dictionary<string, string> { ["Name:Comments"] = "type comment" };
            var plan = ParameterResolutionRules.Plan("Comments", IsBip, true);
            Assert.Equal(new[] { "instance", "type" }, plan.Select(p => p.Scope));
            ParameterResolution<string> r = ParameterResolutionRules.Resolve(plan, FakeElement(inst, type));
            // An empty instance value is the instance's answer for an ordinary parameter.
            Assert.Equal("instance", r.Scope);
            Assert.Equal("instance=", r.Parameter);

            ParameterResolution<string> typeOnly = ParameterResolutionRules.Resolve(plan, FakeElement(null, type));
            Assert.Equal("type", typeOnly.Scope);
        }

        [Fact]
        public void Type_Name_reads_the_type_even_when_the_instance_carries_an_empty_parameter_of_that_name()
        {
            // THE MEASURED FAILURE: classification_parameter "Type Name" read "(empty)".
            var inst = new Dictionary<string, string> { ["Name:Type Name"] = "" };
            var type = new Dictionary<string, string>
            {
                ["BuiltIn:SYMBOL_NAME_PARAM"] = "Interior - 138mm Partition (1-hr)",
                ["Name:Type Name"] = "Interior - 138mm Partition (1-hr)"
            };
            foreach (string spec in new[] { "Type Name", "type name", "SYMBOL_NAME_PARAM", "ALL_MODEL_TYPE_NAME" })
            {
                var plan = ParameterResolutionRules.Plan(spec, IsBip, true);
                ParameterResolution<string> r = ParameterResolutionRules.Resolve(plan, FakeElement(inst, type));
                Assert.True(r.Found, spec);
                Assert.Equal("type", r.Scope);
                Assert.Equal("type=Interior - 138mm Partition (1-hr)", r.Parameter);
            }
            Assert.True(ParameterResolutionRules.IsTypeLevel("Family Name"));
            Assert.False(ParameterResolutionRules.IsTypeLevel("Comments"));
        }

        [Fact]
        public void A_type_level_spec_still_resolves_the_old_way_when_the_type_holds_nothing()
        {
            var inst = new Dictionary<string, string> { ["Name:Type Name"] = "from instance" };
            var plan = ParameterResolutionRules.Plan("Type Name", IsBip, true);
            ParameterResolution<string> r = ParameterResolutionRules.Resolve(plan, FakeElement(inst, new Dictionary<string, string>()));
            Assert.Equal("instance=from instance", r.Parameter);

            // An element that IS a type has no distinct type: no type probes are planned.
            Assert.All(ParameterResolutionRules.Plan("Type Name", IsBip, false), p => Assert.Equal("instance", p.Scope));
        }

        [Fact]
        public void An_ambiguous_or_unreadable_probe_ends_the_search_with_its_error()
        {
            var plan = ParameterResolutionRules.Plan("Area", IsBip, true);
            ParameterResolution<string> r = ParameterResolutionRules.Resolve(plan, step =>
                step.Scope == "instance"
                    ? ParameterProbeResult<string>.Failed("parameter name 'Area' is ambiguous")
                    : ParameterProbeResult<string>.Of("type=1", true));
            Assert.False(r.Found);
            Assert.Contains("ambiguous", r.Error);
        }

        [Fact]
        public void Query_model_and_takeoff_share_one_parameter_resolver()
        {
            string commands = Path.Combine(Root(), "src", "Horizun.Revit", "Commands");
            string query = File.ReadAllText(Path.Combine(commands, "QueryModelCommand.cs"));
            string quantities = File.ReadAllText(Path.Combine(commands, "QuantitiesCommand.cs"));
            Assert.Contains("ParameterResolver.Resolve(", query);
            Assert.Contains("ParameterResolver.Resolve(", quantities);
            // The name-only lookup that turned HOST_AREA_COMPUTED into "absent" is gone.
            Assert.DoesNotContain("LookupParameter(d.Parameter)", quantities);
            Assert.DoesNotContain("LookupParameter(parameterName)", quantities);
            // And query_model's level is the shared ordered rule, not a `??` chain.
            Assert.Contains("new ElementLevelReader(source)", query);
            Assert.DoesNotContain("get_Parameter(BuiltInParameter.LEVEL_PARAM) ??", query);
        }

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        // ============================================================ #11 sum units

        private static JObject Sum(double internalSum, string quantity, string spec = "autodesk.spec.aec:area-2.0.0",
                                   DisplayUnitFact display = null, int summed = 1)
            => SumUnitRules.Describe(internalSum, summed, new Dictionary<string, string> { [spec] = quantity }, display);

        [Fact]
        public void A_room_area_of_416_16_square_feet_reads_38_66_square_metres_with_its_unit()
        {
            // THE MEASURED CASE: Vest. 101, ROOM_AREA 416.16 internal = 38.66 m2.
            JObject o = Sum(416.16, SumUnitRules.Area);
            Assert.Equal("area", (string)o["quantity"]);
            Assert.Equal("ft2 (Revit internal)", (string)o["sum_unit"]);
            Assert.Equal("m2", (string)o["unit"]);
            Assert.Equal(38.66, (double)o["value"], 2);
            Assert.Equal("autodesk.spec.aec:area-2.0.0", (string)o["spec"]);
        }

        [Fact]
        public void Length_and_volume_convert_with_the_takeoff_factors()
        {
            JObject len = Sum(10, SumUnitRules.Length, "autodesk.spec.aec:length-2.0.0");
            Assert.Equal("m", (string)len["unit"]);
            Assert.Equal(3.048, (double)len["value"], 6);
            JObject vol = Sum(100, SumUnitRules.Volume, "autodesk.spec.aec:volume-2.0.0");
            Assert.Equal("m3", (string)vol["unit"]);
            Assert.Equal(Reconcile.ToM3(100), (double)vol["value"], 6);
            Assert.Equal("ft3 (Revit internal)", (string)vol["sum_unit"]);
        }

        [Fact]
        public void The_document_display_unit_is_added_when_it_is_a_plain_scale_and_refused_when_it_has_an_offset()
        {
            var sqm = new DisplayUnitFact { UnitTypeId = "autodesk.unit.unit:squareMeters-1.0.1", Label = "Square meters", Factor = 0.09290304, Offset = 0 };
            JObject o = Sum(416.16, SumUnitRules.Area, display: sqm);
            Assert.Equal(38.66, (double)o["value_display"], 2);
            Assert.Equal("Square meters", (string)o["unit_display"]);

            var celsius = new DisplayUnitFact { UnitTypeId = "autodesk.unit.unit:celsius-1.0.1", Label = "Celsius", Factor = 1.0 / 1.8, Offset = -273.15 };
            JObject t = Sum(600, SumUnitRules.Measurable, "autodesk.spec.aec.hvac:temperature-2.0.0", celsius);
            Assert.Equal(JTokenType.Null, t["value_display"].Type);
            Assert.Contains("offset", (string)t["unit_note"]);
        }

        [Fact]
        public void A_number_that_is_not_a_measurable_quantity_is_summed_as_is_and_labelled_unitless()
        {
            JObject o = Sum(7, SumUnitRules.Unitless, "autodesk.spec:int64-2.0.0");
            Assert.Equal("unitless", (string)o["unit"]);
            Assert.Equal("unitless", (string)o["sum_unit"]);
            Assert.Equal(7.0, (double)o["value"]);
        }

        [Fact]
        public void Mixed_specs_and_element_ids_are_never_given_a_unit()
        {
            JObject mixed = SumUnitRules.Describe(5, 2, new Dictionary<string, string>
            {
                ["autodesk.spec.aec:area-2.0.0"] = SumUnitRules.Area,
                ["autodesk.spec.aec:volume-2.0.0"] = SumUnitRules.Volume
            }, null);
            Assert.Equal("mixed", (string)mixed["quantity"]);
            Assert.Equal(JTokenType.Null, mixed["value"].Type);

            JObject ids = Sum(1234, SumUnitRules.Identifier, "");
            Assert.Equal(JTokenType.Null, ids["value"].Type);
            Assert.Equal(JTokenType.Null, ids["spec"].Type);

            JObject none = SumUnitRules.Describe(0, 0, new Dictionary<string, string>(), null);
            Assert.Equal(JTokenType.Null, none["unit"].Type);
            Assert.NotNull((string)none["unit_note"]);
        }

        [Fact]
        public void Every_described_sum_names_what_its_raw_number_is_in()
        {
            foreach (string q in new[] { SumUnitRules.Length, SumUnitRules.Area, SumUnitRules.Volume,
                                         SumUnitRules.Measurable, SumUnitRules.Unitless, SumUnitRules.Identifier,
                                         SumUnitRules.Unknown })
            {
                JObject o = Sum(1, q);
                Assert.False(string.IsNullOrEmpty((string)o["sum_unit"]), q);
                Assert.Equal(q, (string)o["quantity"]);
            }
        }

        // ======================================================= #16 view-count scope

        [Fact]
        public void Audit_counts_reconcile_with_model_scan_by_naming_the_excluded_schedules()
        {
            // The measured model: 28 off-sheet views in the audit, 8 schedules left out.
            var tally = new ViewCountTally();
            for (int i = 0; i < 20; i++) tally.Counted("FloorPlan");
            for (int i = 0; i < 8; i++) tally.Counted("Section");
            for (int i = 0; i < 8; i++) tally.Excluded("Schedule");
            JObject o = ViewCountScopeRules.Describe(ViewCountScopeRules.AuditOffSheetScope, tally, "horizun_model_scan");
            Assert.Contains("schedules", (string)o["scope"]);
            Assert.Equal(20, (int)o["by_view_type"]["FloorPlan"]);
            Assert.Equal(8, (int)o["excluded_by_view_type"]["Schedule"]);
            Assert.Contains("= 36", (string)o["reconcile"]);
            Assert.Contains("8 Schedule", (string)o["reconcile"]);
        }

        [Fact]
        public void Model_scan_counts_reconcile_downwards_and_views_counted_only_here_are_subtracted()
        {
            var tally = new ViewCountTally();
            for (int i = 0; i < 34; i++) tally.Counted("FloorPlan");
            for (int i = 0; i < 8; i++) tally.Counted("Schedule", otherCountsIt: false);
            Assert.Equal(42, tally.CountedTotal);
            string r = ViewCountScopeRules.Reconcile(tally, "horizun_audit_model");
            Assert.Contains("42 counted here - 8 Schedule counted here but not there = 34", r);

            // The audit counting sheets that model_scan never lists is part of the difference too.
            var audit = new ViewCountTally();
            for (int i = 0; i < 32; i++) audit.Counted("FloorPlan");
            for (int i = 0; i < 2; i++) audit.Counted("DrawingSheet", ViewCountScopeRules.ScanListsViewType("DrawingSheet"));
            for (int i = 0; i < 10; i++) audit.Excluded("Schedule");
            Assert.Contains("= 42", ViewCountScopeRules.Reconcile(audit, "horizun_model_scan"));
        }

        [Fact]
        public void Identical_definitions_say_so_and_the_type_predicates_match_the_commands()
        {
            var tally = new ViewCountTally();
            tally.Counted("ThreeD");
            Assert.Contains("should also report 1", ViewCountScopeRules.Reconcile(tally, "x"));
            Assert.False(ViewCountScopeRules.ScanListsViewType("DrawingSheet"));
            Assert.False(ViewCountScopeRules.ScanListsViewType("ProjectBrowser"));
            Assert.True(ViewCountScopeRules.ScanListsViewType("Schedule"));
            Assert.False(ViewCountScopeRules.AuditOffSheetConsidersViewType("Schedule"));
            Assert.False(ViewCountScopeRules.AuditOffSheetConsidersViewType("Legend"));
            Assert.True(ViewCountScopeRules.AuditOffSheetConsidersViewType("FloorPlan"));
        }

        [Fact]
        public void Both_tools_publish_the_scope_of_their_view_counts()
        {
            string commands = Path.Combine(Root(), "src", "Horizun.Revit", "Commands");
            string audit = File.ReadAllText(Path.Combine(commands, "AuditModelCommand.cs"));
            string scan = File.ReadAllText(Path.Combine(commands, "ModelScanCommand.cs"));
            Assert.Contains("ViewCountScopeRules.AuditOffSheetScope", audit);
            Assert.Contains("ViewCountScopeRules.AuditNoTemplateScope", audit);
            Assert.Contains("finding[\"count_scope\"]", audit);
            Assert.Contains("[\"views_not_on_sheet_scope\"]", scan);
            Assert.Contains("[\"views_no_template_scope\"]", scan);
        }

        [Fact]
        public void Model_scan_root_verdict_includes_cooperative_interruption()
        {
            string scan = File.ReadAllText(Path.Combine(Root(), "src", "Horizun.Revit", "Commands", "ModelScanCommand.cs"));
            Assert.Contains("bool scanComplete = failed.Count == 0 && NotMeasuredSections(result).Count == 0 && coverage.CoverageComplete && (coopScope == null || coopScope.Complete)", scan);
            Assert.Contains("[\"complete\"] = scanComplete", scan);
            Assert.Contains("coopScope?.PartialWarning()", scan);
        }

        [Fact]
        public void Started_timeout_reports_the_execution_may_still_run()
        {
            string dispatcher = File.ReadAllText(Path.Combine(Root(), "src", "Horizun.Revit", "Core", "Dispatcher.cs"));
            Assert.Contains("[\"execution_may_still_run\"] = !removedBeforeStart", dispatcher);
        }

        [Fact]
        public void Session_recheck_must_not_clear_uncovered_old_sync_blockers()
        {
            string source = File.ReadAllText(Path.Combine(Root(), "src", "Horizun.Revit", "Core", "SyncReminder.cs"));
            Assert.DoesNotContain("ClearVerified(key, null, true)", source);
            Assert.Contains("SessionScopeRules.Union", source);
        }

        [Fact]
        public void The_scan_reads_schedule_placement_from_ScheduleSheetInstance()
        {
            // Measured live 2026-09-30: GetAllPlacedViews() does not return schedules, so the
            // scan listed placed schedules as off-sheet and its count missed the audit's by 3.
            string scan = File.ReadAllText(Path.Combine(Root(), "src", "Horizun.Revit", "Commands", "ModelScanCommand.cs"));
            int doc = scan.IndexOf("private static JObject DocumentationSection", StringComparison.Ordinal);
            int next = scan.IndexOf("private static JObject ", doc + 10, StringComparison.Ordinal);
            string section = scan.Substring(doc, next - doc);
            Assert.Contains("OfClass(typeof(ScheduleSheetInstance))", section);
            Assert.Contains("placed.Add(ssi.ScheduleId", section);
        }
    }
}
