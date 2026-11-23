using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ArchitectureCreationRulesTests
    {
        [Theory]
        [InlineData(null, "unreadable")]
        [InlineData(double.NaN, "unreadable")]
        [InlineData(double.PositiveInfinity, "unreadable")]
        [InlineData(0.0, "not_enclosed")]
        [InlineData(25.0, "enclosed")]
        public void Room_placement_does_not_claim_enclosure_when_no_area_was_measured(double? area, string expected)
            => Assert.Equal(expected, ArchitectureCreationRules.RoomStatus(area));

        [Theory]
        [InlineData(0, 10, 10)]
        [InlineData(12, 22, 10)]
        [InlineData(-4, 6, 10)]
        public void Wall_height_uses_absolute_base_and_top(double bottom, double top, double expected)
            => Assert.Equal(expected, ArchitectureCreationRules.WallHeight(bottom, top));

        [Fact]
        public void Missing_upper_level_defaults_to_three_metres_regardless_of_project_elevation()
            => Assert.Equal(3000, ArchitectureCreationRules.WallHeight(100, null) * 304.8, 8);

        [Theory]
        [InlineData(10, 10)]
        [InlineData(10, 9)]
        [InlineData(0, double.NaN)]
        [InlineData(0, double.PositiveInfinity)]
        public void An_invalid_explicit_top_is_not_replaced_by_an_assumption(double bottom, double top)
            => Assert.Throws<ArgumentException>(() => ArchitectureCreationRules.WallHeight(bottom, top));

        [Theory]
        [InlineData("  Muro 200  ", "MURO 200", 0)]
        [InlineData("Muro 200", "Muro 201", 1)]
        [InlineData("", "abc", 3)]
        public void Closest_type_name_has_a_stable_case_insensitive_distance(string a, string b, int distance)
            => Assert.Equal(distance, ArchitectureCreationRules.NameDistance(a, b));

        [Fact]
        public void Open_chain_preserves_order_parameters_and_each_world_coordinate_without_mutating_input()
        {
            var input = JObject.Parse(@"{'kind':'wall_chain','points':[[0,0,50],[10,0,50],[10,20,50]],'level_id':7,'parameters':{'Mark':'X'}}");
            string before = input.ToString();
            var rows = ArchitectureCreationRules.WallChain(input);
            Assert.Equal(2, rows.Count);
            Assert.True(JToken.DeepEquals(input["points"][1], rows[1]["start"]));
            Assert.True(JToken.DeepEquals(input["points"][2], rows[1]["end"]));
            Assert.Equal("wall", rows[0].Value<string>("kind"));
            Assert.Equal(7, rows[0].Value<int>("level_id"));
            rows[0]["parameters"]["Mark"] = "changed";
            Assert.Equal("X", rows[1]["parameters"].Value<string>("Mark"));
            Assert.Equal(before, input.ToString());
        }

        [Theory]
        [InlineData("[[0,0,0],[10,0,0],[10,10,0]]")]
        [InlineData("[[0,0,0],[10,0,0],[10,10,0],[0,0,0]]")]
        public void Closed_chain_adds_one_closing_segment_and_never_duplicates_an_existing_one(string points)
        {
            var rows = ArchitectureCreationRules.WallChain(new JObject { ["points"] = JArray.Parse(points), ["closed"] = true });
            Assert.Equal(3, rows.Count);
            Assert.True(JToken.DeepEquals(rows[0]["start"], rows[2]["end"]));
        }

        [Theory]
        [InlineData("{'points':[[0,0,0]]}")]
        [InlineData("{'points':[[0,0],[1,1]]}")]
        [InlineData("{'points':[[0,0,0],[1,1,0]],'closed':true}")]
        [InlineData("{'points':[[0,0,0],['1',1,0]]}")]
        [InlineData("{'points':[[0,0,0],[1,1,0]],'closed':'true'}")]
        public void Invalid_chain_shape_is_rejected_before_a_transaction(string json)
            => Assert.Throws<ArgumentException>(() => ArchitectureCreationRules.WallChain(JObject.Parse(json)));

        [Fact]
        public void Assumptions_are_machine_readable_and_labelled_in_spanish()
        {
            var row = ArchitectureCreationRules.Assumption("height", 3000, "no upper level");
            Assert.Equal(new[] { "flag", "what", "why", "override" }, row.Properties().Select(x => x.Name));
            Assert.Equal("SUPUESTO", row.Value<string>("flag"));
            Assert.Equal("height = 3000", row.Value<string>("what"));
            Assert.Equal("no upper level", row.Value<string>("why"));
            Assert.Equal("Supply height explicitly.", row.Value<string>("override"));
        }

        private static ArchitectureCreationRules.TypeCandidate Type(long id, string name, string family = "Basic", bool preferred = true)
            => new ArchitectureCreationRules.TypeCandidate { Id = id, Name = name, Family = family, Preferred = preferred };

        [Fact]
        public void Type_selection_uses_document_default_instead_of_shortest_name()
        {
            var notes = new JArray();
            Assert.Equal(2, ArchitectureCreationRules.SelectType(new[] { Type(1, "A"), Type(2, "Default wall") }, null, null, 2, notes));
            var response = AssumptionNotes.Stamp(new JObject { ["dry_run"] = true }, notes.OfType<JObject>());
            Assert.True(response.Value<bool>("has_assumptions"));
            Assert.Equal("SUPUESTO", response["assumptions"][0].Value<string>("flag"));
            Assert.Contains("Default wall", response["assumptions"][0].Value<string>("what"));
            Assert.NotNull(response["assumptions"][0]["override"]);
        }

        [Fact]
        public void Type_selection_accepts_exact_qualified_and_previously_excluded_types()
        {
            var types = new[] { Type(1, "Same", "First"), Type(2, "Same", "Second"), Type(3, "Curtain", "Curtain", false) };
            Assert.Throws<ArgumentException>(() => ArchitectureCreationRules.SelectType(types, null, "Same", 1, new JArray()));
            Assert.Equal(2, ArchitectureCreationRules.SelectType(types, null, "Second: Same", 1, new JArray()));
            Assert.Equal(3, ArchitectureCreationRules.SelectType(types, null, "Curtain", 1, new JArray()));
            Assert.Equal(3, ArchitectureCreationRules.SelectType(types, 3, null, 1, new JArray()));
            Assert.Throws<ArgumentException>(() => ArchitectureCreationRules.SelectType(types, 999, null, 1, new JArray()));
            var notes = new JArray();
            Assert.Equal(3, ArchitectureCreationRules.SelectType(types, null, "Curtain: Curtain", 1, notes));
            Assert.Empty(notes);
        }

        [Theory]
        [InlineData("[[0,0,0],[10,0,0],[0,0,0]]", true)]
        public void Overlapping_or_short_chain_names_the_refusal(string points, bool closed)
            => Assert.Throws<ArgumentException>(() => ArchitectureCreationRules.WallChain(new JObject { ["points"] = JArray.Parse(points), ["closed"] = closed }));

        [Theory]
        [InlineData(true, "[[0,0,0],[4000,0,0],[8000,0,0]]", 2, 0)]
        [InlineData(false, "[[0,0,0],[4000,0,0],[1000,0,0]]", 1, 0)]
        [InlineData(false, "[[0,0,0],[4000,0,0],[6000,3000,0],[6000,0,0],[2000,0,0]]", 3, 0)]
        [InlineData(false, "[[0,0,0],[8000,4000,0],[9000,1000,0],[6000,3000,0],[2000,1000,0]]", 3, 0)]
        public void Expansion_rejects_positive_length_overlap_even_with_distinct_endpoints(bool closed, string points,
            int segment, int earlier)
        {
            var item = new JObject { ["kind"] = "wall_chain", ["level_id"] = 7, ["join_rule"] = "none",
                ["points"] = JArray.Parse(points), ["closed"] = closed };
            var error = Assert.Throws<ArgumentException>(() => ArchitectureCreationRules.Expand(new JArray(item), 1,
                (_, _, _) => throw new InvalidOperationException("No hosted items are present."), out _));
            Assert.Contains($"segment {segment} overlaps an earlier segment ({earlier})", error.Message);
        }

        [Theory]
        [InlineData(false, "[[0,0,0],[4000,0,0],[8000,0,0]]", 2)]
        [InlineData(true, "[[0,0,0],[4000,0,0],[8000,0,0],[8000,4000,0],[0,4000,0]]", 5)]
        [InlineData(false, "[[0,0,0],[4000,4000,0],[0,4000,0],[4000,0,0]]", 3)]
        [InlineData(false, "[[0,0,0],[8000,0,0],[8000,-4000,0],[6000,-0.25,0],[2000,0.25,0]]", 4)]
        [InlineData(false, "[[0,0,0],[8000,0,0],[8000,-0.25,0],[0,0.25,0]]", 2)]
        public void Wall_chain_keeps_adjacent_segments_and_noncollinear_crossings(bool closed, string points, int count)
        {
            var item = new JObject { ["kind"] = "wall_chain", ["level_id"] = 7, ["join_rule"] = "none",
                ["points"] = JArray.Parse(points), ["closed"] = closed };
            JArray rows = ArchitectureCreationRules.Expand(new JArray(item), 1, null, out _);
            Assert.Equal(count, rows.Count);
        }

        [Fact]
        public void Short_chain_segment_is_skipped_without_losing_other_segments()
        {
            var rows = ArchitectureCreationRules.WallChain(JObject.Parse("{'points':[[0,0,0],[0.00000001,0,0],[10,0,0]]}"));
            Assert.Single(rows);
            Assert.Equal(1, rows[0].Value<int>("_segment_index"));
        }

        [Fact]
        public void Closure_uses_numeric_tolerance()
            => Assert.Equal(3, ArchitectureCreationRules.WallChain(JObject.Parse("{'points':[[0,0,0],[10,0,0],[10,10,0],[0.00000001,0,0]],'closed':true}")).Count);

        [Theory]
        [InlineData(0.0, 1.0, false)]
        [InlineData(100.0, 110.0, true)]
        [InlineData(100, null, false)]
        public void Infer_wall_height_binds_only_a_reasonable_next_level(double bottom, double? top, bool bind)
            => Assert.Equal(bind, ArchitectureCreationRules.BindInferredTop(bottom, top));

        [Fact]
        public void Expansion_accepts_explicit_point_but_refuses_all_enclosed()
        {
            Assert.Null(EnclosedRules.PlacementFieldsProblem(JObject.Parse("{'placement':'point'}")));
            Assert.Null(ArchitectureCreationRules.ExpansionConflict(JArray.Parse("[{'kind':'door'},{'kind':'room','placement':'point'}]")));
            Assert.Contains("all_enclosed", ArchitectureCreationRules.ExpansionConflict(JArray.Parse("[{'kind':'door'},{'kind':'room','placement':'all_enclosed'}]")));
        }

        private static void Canonical(JObject response, int count)
        {
            Assert.True(response.Value<bool>("has_assumptions"));
            Assert.Equal(count, ((JArray)response["assumptions"]).Count);
            foreach (JObject note in (JArray)response["assumptions"])
            {
                Assert.Equal(new[] { "flag", "what", "why", "override" }, note.Properties().Select(p => p.Name));
                Assert.Equal("SUPUESTO", note.Value<string>("flag"));
                Assert.False(string.IsNullOrEmpty(note.Value<string>("override")));
            }
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void Creation_response_stamps_one_note_per_chain_on_rehearsal_apply_and_failure(bool dryRun, bool success)
        {
            var input = JArray.Parse("[{'kind':'wall_chain','level_id':7,'points':[[0,0,0],[10,0,0],[10,10,0]]}]");
            JArray expanded = ArchitectureCreationRules.Expand(input, 0.001, null, out JArray evidence);
            var plans = expanded.Select((_, index) => (index,
                (JArray)ArchitectureCreationRules.ResolveWallHeight(100, 110, false, 1, "Upper")["assumptions"]));
            var hostResult = new JObject { ["dry_run"] = dryRun, ["write_started"] = !dryRun };
            CommandResult result = ArchitectureCreationRules.CreationResponse(
                success ? CommandResult.Ok(hostResult) : CommandResult.FailWithDetail("Construction refused", hostResult),
                plans, evidence, "SUPUESTO: ");
            Assert.Equal(success, result.Success);
            var response = success ? Assert.IsType<JObject>(result.Data) : result.Detail;
            Canonical(response, 1);
            Assert.Equal(dryRun, response.Value<bool>("dry_run"));
            Assert.Equal(2, ((JArray)response["architectural_expansion"]).Count);
            if (!success) Assert.StartsWith("SUPUESTO: ", result.Error);
        }

        [Fact]
        public void Errors_after_a_recipe_name_the_row_the_caller_sent()
        {
            // Three chain segments become rows 0-2; the caller's typo is elements[1], not the expanded row 3.
            var input = JArray.Parse("[{'kind':'wall_chain','level_id':7,'points':[[0,0,0],[10,0,0],[10,10,0],[0,10,0]]}," +
                "{'kind':'wall','start_point':[0,0,0],'end':[1000,0,0]}]");
            JArray expanded = ArchitectureCreationRules.Expand(input, 0.001, null, out _);
            Assert.Equal(4, expanded.Count);
            var batch = CreationBatch<JObject>.Plan(expanded, (i, item, kind) => (item, null, null));
            var row = Assert.Single(batch.Errors.OfType<JObject>());
            Assert.Equal(3, row.Value<int>("index"));
            Assert.Equal(1, row.Value<int>("elements_index"));
            Assert.Equal("elements[1].start_point", row.Value<string>("field_path"));
            Assert.Equal(1, Assert.Single(batch.Outcomes, o => o.Error != null).Index);
            Assert.Equal(3, batch.Plans.Count);
            Assert.All(batch.Plans, plan => Assert.Equal(0, plan.Value<int>(ArchitectureCreationRules.SourceKey)));
        }

        [Theory]
        [InlineData("4")]
        [InlineData("null")]
        public void A_caller_row_carrying_the_expansion_bookkeeping_is_refused(string value)
        {
            var input = JArray.Parse("[{'kind':'wall','start':[0,0,0],'end':[1,0,0],'recipe_from':" + value + "}]");
            var error = Assert.Throws<ArgumentException>(() => ArchitectureCreationRules.Expand(input, 0.001, null, out _));
            Assert.Contains("elements[0]: recipe_from", error.Message);
        }

        [Fact]
        public void Explicit_values_do_not_create_empty_or_spurious_assumptions()
        {
            Assert.Null(ArchitectureCreationRules.ResolveType(new[] { Type(1, "Wall") }, 1, null, null)["assumptions"]);
            Assert.Null(ArchitectureCreationRules.ResolveWallHeight(100, 101, true, 1, "Explicit")["assumptions"]);
            var response = ArchitectureCreationRules.ResolveHostedDefaults(JObject.Parse("{'level_id':8,'coordinate_mode':'absolute'}"), 7);
            Assert.Null(response["assumptions"]);
            Assert.Equal("absolute", response["element"].Value<string>("coordinate_mode"));
            CommandResult applied = ArchitectureCreationRules.CreationResponse(CommandResult.Ok(new JObject()),
                new[] { (index: 0, notes: new JArray()) }, new JArray(), "SUPUESTO: ");
            Assert.Null(((JObject)applied.Data)["assumptions"]);
            Assert.Null(((JObject)applied.Data)["has_assumptions"]);
        }

        [Fact]
        public void Creation_response_reports_architectural_and_build_assumptions_once()
        {
            var height = (JObject)ArchitectureCreationRules.ResolveWallHeight(100, 110, false, 1, "Upper")["assumptions"][0];
            var wye = JunctionRuleClassifier.WyeSubstitution(2, "Preferred", "Alternative",
                new[] { new KeyValuePair<string, string>("Preferred", "Revit refused the preferred type") });
            var expansion = JArray.Parse("[{index:0,elements_index:0},{index:1,elements_index:0}]");
            var result = ArchitectureCreationRules.CreationResponse(CommandResult.Ok(new JObject()),
                new[] { (0, new JArray(height)), (1, new JArray(height)), (2, new JArray(wye)) }, expansion, "SUPUESTO: ");
            JObject response = Assert.IsType<JObject>(result.Data);
            Assert.True(response.Value<bool>("has_assumptions"));
            var notes = Assert.IsType<JArray>(response["assumptions"]);
            Assert.Equal(2, notes.Count);
            Assert.True(JToken.DeepEquals(height, notes[0]));
            Assert.True(JToken.DeepEquals(wye, notes[1]));
        }

        [Fact]
        public void Inferred_height_uses_default_for_a_low_next_level_and_binds_a_valid_one()
        {
            var low = ArchitectureCreationRules.ResolveWallHeight(100, 101, false, 1 / 304.8, "Low");
            Assert.False(low.Value<bool>("bind_top"));
            Assert.Equal(3000 / 304.8, low.Value<double>("height_feet"));
            var upper = ArchitectureCreationRules.ResolveWallHeight(100, 110, false, 1 / 304.8, "Upper");
            Assert.True(upper.Value<bool>("bind_top"));
            Assert.Equal(10, upper.Value<double>("height_feet"));
        }

        [Fact]
        public void Room_status_never_claims_enclosed_with_an_open_boundary()
        {
            JObject reply = ArchitectureCreationRules.RoomPlacement(20, false);
            Assert.Equal("not_enclosed", reply.Value<string>("room_status"));
            Assert.False(reply.Value<bool>("area_enclosed"));
            Assert.False(reply.Value<bool>("boundary_closed"));
            Assert.Equal(20, reply.Value<double>("area_sqft"));
            Assert.Contains("positive area", reply.Value<string>("area_means"));
            Assert.Contains("could not be verified", reply.Value<string>("area_means"));
            Canonical(reply, 1);
            Assert.Equal("redundant", ArchitectureCreationRules.RoomStatus(0, true, true));
            Assert.Equal("unreadable", ArchitectureCreationRules.RoomStatus(null, false));
        }

        [Fact]
        public void Native_region_warning_must_name_a_new_room_to_refuse_placement()
        {
            var created = new[] { 40L, 41L };
            Assert.Null(ArchitectureCreationRules.RedundantCreatedRoom(created, new[] { 20L, 21L }));
            Assert.Equal(41L, ArchitectureCreationRules.RedundantCreatedRoom(created, new[] { 21L, 41L }));
            Assert.Equal(40L, ArchitectureCreationRules.RedundantCreatedRoom(created, new[] { 40L, 41L }));
        }

        [Theory]
        [InlineData("[[0,0,0],[0.001,0,0]]", false, 1)]
        [InlineData("[[0,0,0],[0.001,0,0],[0.001,0.001,0]]", true, 2)]
        public void Short_chain_reports_original_indices_and_keeps_an_independent_wall(string points, bool closed, int count)
        {
            var input = JArray.Parse("[{'kind':'wall_chain','level_id':7,'points':" + points + "}," +
                "{'kind':'wall','level_id':7,'start':[0,0,0],'end':[10,0,0]}]");
            input[0]["closed"] = closed;
            JArray expanded = ArchitectureCreationRules.Expand(input, 0.01, null, out JArray evidence);
            var kept = (JObject)Assert.Single(expanded).DeepClone();
            Assert.Equal(1, kept.Value<int>(ArchitectureCreationRules.SourceKey));
            kept.Remove(ArchitectureCreationRules.SourceKey);
            Assert.True(JToken.DeepEquals(input[1], kept));
            JObject skipped = Assert.IsType<JObject>(Assert.Single(evidence));
            Assert.Equal(0, skipped.Value<int>("elements_index"));
            Assert.Equal("recipe_omitted", skipped.Value<string>("state"));
            Assert.Equal(Enumerable.Range(0, count), skipped["skipped_segment_indices"].Values<int>());
            var result = ArchitectureCreationRules.CreationResponse(CommandResult.Ok(new JObject()),
                new[] { (index: 0, notes: new JArray()) }, evidence, "SUPUESTO: ");
            Canonical(Assert.IsType<JObject>(result.Data), 1);
        }

        [Theory]
        [InlineData(3L, 111, 10, "top_level_elev_mm")]
        [InlineData(4L, 110, 10, "top_level_uid")]
        [InlineData(3L, 110, 11, "effective_height_feet")]
        public void Public_create_elements_refuses_a_changed_resolved_wall_plan(long id, double top,
            double height, string changed)
        {
            var originalTop = new Level { Id = new ElementId(3), ProjectElevation = 110 };
            var doc = new Document { CreationConfirmations = new ConfirmationStore(), CreationTopLevel = originalTop, CreationWallHeight = 10 };
            doc.Elements.Add(2, new Level { Id = new ElementId(2), ProjectElevation = 100 });
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new Horizun.Revit.Commands.CreateElementsCommand();
            var request = JObject.Parse("{'target_document':'test','elements':[{'kind':'wall','level_id':2,'start':[0,0,0],'end':[1000,0,0]}]}");
            CommandResult Execute() => command.Execute(app, request.ToString());
            CommandResult preview = Execute();
            Assert.True(preview.Success);
            var rehearsal = Assert.IsType<JObject>(preview.Data);
            JToken row = Assert.Single(Assert.IsType<JArray>(rehearsal["change_preview"]?["rows"]));
            Assert.Equal("create:0", row.Value<string>("unique_id"));
            string token = rehearsal.Value<string>("confirmation_token");
            Assert.False(string.IsNullOrEmpty(token));
            Assert.Equal(0, doc.CreationWrites);

            request["confirmation_token"] = token;
            request["dry_run"] = false;
            request["idempotency_key"] = "stable-wall";
            doc.CreationTopLevel = new Level { Id = new ElementId(id), ProjectElevation = top };
            doc.CreationWallHeight = height;
            CommandResult refusal = Execute();
            Assert.False(refusal.Success);
            Assert.Equal("stale_plan", refusal.Detail.Value<string>("state"));
            Assert.Contains(changed, refusal.Error);
            Assert.Equal(0, doc.CreationWrites);

            doc.CreationTopLevel = originalTop;
            doc.CreationWallHeight = 10;
            Assert.True(Execute().Success);
            Assert.Equal(1, doc.CreationWrites);
        }

        [Theory]
        [InlineData(null, "wye-a", "wye-b", false)]
        [InlineData(null, "wye-a", null, false)]
        [InlineData(null, null, "wye-a", false)]
        [InlineData(null, "wye-a", "wye-a", true)]
        [InlineData("arguments", "wye-a", "wye-a", true)]
        [InlineData("revit_rollback", "wye-a", "wye-a", true)]
        public void Public_create_elements_rehearses_wye_and_rolls_back_unapproved_construction(
            string validationMode, string approved, string built, bool applies)
        {
            Document doc = WithJunctionPipes(approved);
            Element originalBranch = doc.Elements[13];
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new Horizun.Revit.Commands.CreateElementsCommand();
            JObject request = JunctionRequest();
            if (validationMode != null) request["validation_mode"] = validationMode;
            int commits = 0;
            doc.TestAfterCommit = () => commits++;

            CommandResult preview = command.Execute(app, request.ToString());
            Assert.True(preview.Success, preview.Error);
            var rehearsal = Assert.IsType<JObject>(preview.Data);
            Assert.Equal("RolledBack", rehearsal.Value<string>("transaction_status"));
            Assert.Equal("RolledBack", (string)rehearsal["application"]["transaction_status"]);
            Assert.Equal("rehearsed", (string)rehearsal["application"]["state"]);
            Assert.Equal("revit_construction_rolled_back", rehearsal.Value<string>("validation_level"));
            Assert.Equal("RolledBack", (string)rehearsal["api_rehearsal"]["transaction_status"]);
            Assert.True((bool)rehearsal["api_rehearsal"]["provisional_elements_absent"]);
            Assert.Equal(1, commits);
            Assert.Equal(1, doc.TransactionGroupRollbacks);
            Assert.Equal(0, doc.CreationWrites);
            Assert.Same(originalBranch, doc.Elements[13]);
            JToken row = Assert.Single((JArray)rehearsal["change_preview"]["rows"]);
            if (approved != null) Assert.Equal(approved, row["proposed_values"].Value<string>("wye_type_unique_id"));
            request["confirmation_token"] = rehearsal.Value<string>("confirmation_token");
            request["dry_run"] = false;
            request["idempotency_key"] = "apply-wye";
            doc.CreationWyeType = built;

            CommandResult apply = command.Execute(app, request.ToString());
            Assert.Equal(applies, apply.Success);
            Assert.Equal(2, commits);
            Assert.Equal(applies ? 1 : 0, doc.CreationWrites);
            Assert.Equal(applies, doc.Assimilated);
            Assert.Equal(applies ? 1 : 2, doc.TransactionGroupRollbacks);
            if (!applies)
            {
                Assert.Same(originalBranch, doc.Elements[13]);
                Assert.Equal("stale_plan", apply.Detail.Value<string>("state"));
                Assert.Equal("RolledBack", apply.Detail.Value<string>("rollback_status"));
                Assert.False(apply.Detail.Value<bool>("changes_applied"));
            }
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("revit_rollback", true)]
        public void Right_angle_pipe_tee_rehearses_in_revit_only_when_requested(string validationMode, bool constructs)
        {
            Document doc = WithJunctionPipes(null, branchX: 0);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            JObject request = JunctionRequest();
            if (validationMode != null) request["validation_mode"] = validationMode;

            CommandResult reply = new Horizun.Revit.Commands.CreateElementsCommand().Execute(app, request.ToString());

            Assert.True(reply.Success, reply.Error);
            var data = Assert.IsType<JObject>(reply.Data);
            string status = constructs ? "RolledBack" : "not_started";
            Assert.Equal(status, data.Value<string>("transaction_status"));
            Assert.Equal(status, (string)data["application"]["transaction_status"]);
            Assert.Equal(constructs ? 1 : 0, doc.TestTransactionStarts);
            Assert.Equal(constructs ? 1 : 0, doc.TransactionGroupRollbacks);
            Assert.Equal(0, doc.CreationWrites);
        }

        [Theory]
        [InlineData("wye-a", true)]
        [InlineData("wye-b", false)]
        public void Public_plan_issues_and_rechecks_its_own_confirmation_for_a_rolled_back_wye(string applyType, bool applies)
        {
            Document doc = WithJunctionPipes("wye-a");
            Element originalBranch = doc.Elements[13];
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var creation = new Horizun.Revit.Commands.CreateElementsCommand();
            var command = new Horizun.Revit.Commands.ExecutePlanCommand(name => name == creation.Name ? creation : null);
            var request = new JObject
            {
                ["target_document"] = "test",
                ["actions"] = new JArray(new JObject
                {
                    ["key"] = "junction",
                    ["tool"] = creation.Name,
                    ["arguments"] = JunctionRequest()
                })
            };

            CommandResult preview = command.Execute(app, request.ToString());

            Assert.True(preview.Success, preview.Error);
            var plan = Assert.IsType<JObject>(preview.Data);
            var action = Assert.Single(Assert.IsType<JArray>(plan["actions"]));
            Assert.Equal("not_started", plan.Value<string>("transaction_status"));
            Assert.Contains("transaction_status applies to the parent plan only", plan.Value<string>("note"));
            Assert.Equal("RolledBack", (string)action["data"]["application"]["transaction_status"]);
            Assert.Equal("rehearsed", (string)action["application_state"]);
            Assert.True(plan.Value<bool>("rehearsed_cleanly"));
            Assert.False(plan.Value<bool>("confirmation_withheld"));
            string token = plan.Value<string>("confirmation_token");
            Assert.False(string.IsNullOrEmpty(token));
            Assert.NotEqual((string)action["data"]["confirmation_token"], token);
            Assert.Equal(1, doc.TransactionGroupRollbacks);
            Assert.Equal(0, doc.CreationWrites);
            Assert.Same(originalBranch, doc.Elements[13]);

            request["confirmation_token"] = token;
            request["idempotency_key"] = "apply-wye-plan";
            request["dry_run"] = false;
            doc.CreationWyeType = applyType;

            CommandResult applied = command.Execute(app, request.ToString());

            Assert.Equal(applies, applied.Success);
            Assert.Equal(2, doc.TransactionGroupRollbacks);
            Assert.Equal(applies ? 1 : 0, doc.CreationWrites);
            Assert.Equal(applies, doc.Assimilated);
            if (applies)
                Assert.Equal("verified_applied", (string)((JObject)applied.Data)["results"]["junction"]["application"]["state"]);
            else
            {
                Assert.Equal("StalePlan", applied.Detail.Value<string>("confirmation_state"));
                Assert.Equal("stale_plan", applied.Detail.Value<string>("state"));
                Assert.Same(originalBranch, doc.Elements[13]);
            }
        }

        [Theory]
        [InlineData("wye-a", true, true)]
        [InlineData("wye-b", true, false)]
        [InlineData("wye-a", false, false)]
        public void Atomic_create_elements_checks_the_child_fingerprint_after_building_the_wye(string built, bool approved, bool applies)
        {
            Document doc = WithJunctionPipes("wye-a");
            Element originalBranch = doc.Elements[13];
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new Horizun.Revit.Commands.CreateElementsCommand();
            JObject request = JunctionRequest();
            JObject rehearsal = Assert.IsType<JObject>(command.Execute(app, request.ToString()).Data);
            request["dry_run"] = false;
            if (approved) request["__expected_plan_fingerprint"] = rehearsal["change_preview"]["fingerprint"];
            doc.CreationWyeType = built;

            DocumentGate.InConfirmedAtomicPlan = true;
            try
            {
                CommandResult reply = command.Execute(app, request.ToString());
                Assert.Equal(applies, reply.Success);
                Assert.Equal(applies ? 1 : 0, doc.CreationWrites);
                Assert.Equal(applies, doc.Assimilated);
                Assert.Equal(applies ? 1 : 2, doc.TransactionGroupRollbacks);
                if (!applies)
                {
                    Assert.Same(originalBranch, doc.Elements[13]);
                    Assert.Equal("stale_plan", reply.Detail.Value<string>("state"));
                    Assert.Equal("RolledBack", reply.Detail.Value<string>("rollback_status"));
                }
            }
            finally { DocumentGate.InConfirmedAtomicPlan = false; }
        }

        [Fact]
        public void Declared_53_mm_conduit_union_to_25_4_mm_refuses_before_any_transaction()
        {
            var doc = WithJunctionPipes("unused");
            var existing = new Autodesk.Revit.DB.Electrical.Conduit
            {
                Id = new ElementId(11), ProbeDiameter = 25.4 / 304.8
            };
            doc.Elements[11] = existing;
            doc.TestTransactionStarted = _ => throw new InvalidOperationException("the invalid batch must not open a transaction");
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var request = JObject.Parse(@"{
                'target_document':'test', 'units':'mm', 'validation_mode':'revit_rollback',
                'elements': [
                    {'kind':'conduit','start':[27000,30000,1000],'end':[30000,30000,1000],'diameter':53},
                    {'kind':'fitting','fitting':'union','elements':[{'batch_index':0},{'element_id':11}]}
                ]
            }");
            var command = new Horizun.Revit.Commands.CreateElementsCommand();

            var rehearsed = command.Execute(app, request.ToString());
            Assert.False(rehearsed.Success);
            Assert.Contains("conduit_union_diameter_mismatch", rehearsed.Error);
            var preview = Assert.IsType<JObject>(rehearsed.Detail);
            Assert.Null(preview["confirmation_token"]);
            Assert.Equal("not_started", (string)preview["transaction_status"]);
            var refusal = Assert.Single(Assert.IsType<JArray>(preview["errors"]));
            Assert.Equal(1, refusal.Value<int>("index"));
            Assert.Contains("diámetros distintos: 53 mm y 25.4 mm", refusal.ToString());

            request["dry_run"] = false;
            var applied = command.Execute(app, request.ToString());
            Assert.False(applied.Success);
            Assert.Contains("conduit_union_diameter_mismatch", applied.Error);
            Assert.Equal(0, doc.CreationWrites);
            Assert.Same(existing, doc.Elements[11]);
        }

        [Fact]
        public void Diameter_probe_reports_unconfirmed_rollback_when_the_subtransaction_throws()
        {
            var doc = WithJunctionPipes("unused");
            doc.IsModifiable = true;
            doc.CreationProbeRollback = () => throw new InvalidOperationException("probe rollback threw");
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var request = JObject.Parse("{'target_document':'test','elements':[{'kind':'conduit'}]}");

            CommandResult reply = new Horizun.Revit.Commands.CreateElementsCommand().Execute(app, request.ToString());

            Assert.False(reply.Success);
            Assert.Contains("Could not confirm rollback", reply.Error);
            Assert.Equal("uncertain", reply.Detail.Value<string>("state"));
            Assert.Equal("unconfirmed", reply.Detail.Value<string>("rollback_status"));
            Assert.Equal("probe rollback threw", reply.Detail.Value<string>("rollback_error"));
            Assert.True(reply.Detail.Value<bool>("write_started"));
            Assert.Equal(JTokenType.Null, reply.Detail["changes_applied"].Type);
        }

        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(false, true, 1)]
        [InlineData(true, false, 1)]
        [InlineData(true, true, 0)]
        public void Reducer_ports_are_verified_after_each_commit(bool afterGroup, bool disconnect, int portIndex)
        {
            var doc = WithJunctionPipes("unused");
            for (int id = 11; id <= 12; id++)
            {
                var run = (Pipe)doc.Elements[id];
                run.Ports = new[] { new Connector { Id = 1, Owner = run, Radius = id == 11 ? 0.2 : 0.1 } };
            }
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new Horizun.Revit.Commands.CreateElementsCommand();
            var request = JObject.Parse("{'target_document':'test','elements':[{'kind':'fitting','fitting':'union'," +
                "'elements':[{'element_id':11},{'element_id':12}]}]}");
            JObject preview = Assert.IsType<JObject>(command.Execute(app, request.ToString()).Data);
            request["dry_run"] = false;
            request["confirmation_token"] = preview["confirmation_token"];

            void ChangePort()
            {
                var port = doc.Elements.Values.OfType<FamilyInstance>().Single().Ports[portIndex];
                if (disconnect) port.DisconnectFrom(port.AllRefs.Single());
                else port.Radius *= 2;
            }
            if (afterGroup) doc.TestAfterAssimilate = ChangePort;
            else doc.TestAfterCommit = ChangePort;

            CommandResult reply = command.Execute(app, request.ToString());

            Assert.False(reply.Success);
            Assert.Equal("verificacion_fallida", reply.Detail.Value<string>("error_code"));
            var row = Assert.Single((JArray)reply.Detail["verification"]);
            Assert.False(row.Value<bool>("verified"));
            Assert.Contains("reducer ports", row.Value<string>("reducer_ports_error"));
            Assert.Equal(afterGroup ? "Committed" : "RolledBack", reply.Detail.Value<string>("transaction_status"));
            Assert.Equal(afterGroup, reply.Detail.Value<bool>("changes_applied"));
        }

        [Fact]
        public void Reducer_port_within_plan_tolerance_must_match_the_measured_run_during_construction()
        {
            var doc = WithJunctionPipes("unused");
            var first = (Pipe)doc.Elements[11];
            var second = (Pipe)doc.Elements[12];
            const double planned = 0.4;
            double offset = MepRoutingRules.SizeToleranceFeet * 0.75;
            double measured = planned - offset;
            double fittingDiameter = planned + offset;
            var runPort = new Connector { Id = 1, Owner = first, Radius = planned / 2 };
            first.Ports = new[] { runPort };
            second.Ports = new[] { new Connector { Id = 1, Owner = second, Radius = 0.1 } };
            doc.TestTransactionStarted = _ => runPort.Radius = measured / 2;
            doc.TestRegenerate = () => runPort.AllRefs.Single().Radius = fittingDiameter / 2;
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var request = JObject.Parse("{'target_document':'test','elements':[{'kind':'fitting','fitting':'union'," +
                "'elements':[{'element_id':11},{'element_id':12}]}]}");

            CommandResult reply = new Horizun.Revit.Commands.CreateElementsCommand().Execute(app, request.ToString());

            Assert.False(reply.Success);
            Assert.Equal("verificacion_fallida", reply.Detail.Value<string>("error_code"));
            Assert.Equal("create", reply.Detail.Value<string>("phase"));
            Assert.Equal("RolledBack", reply.Detail.Value<string>("transaction_status"));
            Assert.False(reply.Detail.Value<bool>("changes_applied"));
            Assert.Equal(0, doc.CreationWrites);
        }

        private static JObject JunctionRequest() => JObject.Parse(
            "{'target_document':'test','elements':[{'kind':'fitting','fitting':'tee'," +
            "'elements':[{'element_id':11},{'element_id':12},{'element_id':13}]}]}");

        private static Document WithJunctionPipes(string wyeType, double branchX = 0.7071067811865476)
        {
            var doc = new Document { CreationConfirmations = DocumentGate.Confirmations, CreationWyeType = wyeType };
            doc.Elements.Add(2, new Level { Id = new ElementId(2) });
            AddPipe(11, 1, 0);
            AddPipe(12, -1, 0);
            AddPipe(13, branchX, Math.Sqrt(1 - branchX * branchX));
            return doc;

            void AddPipe(int id, double x, double y) => doc.Elements.Add(id, new Pipe
            {
                Id = new ElementId(id), Document = doc,
                Connector = new ConnectorFact { DirX = x, DirY = y, Domain = "piping" }
            });
        }
    }
}
