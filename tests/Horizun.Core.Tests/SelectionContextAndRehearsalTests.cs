// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// 2026-10, from the field data on execute_python: the selection with its context, the
// before/after photograph, the rehearsal's net change and rollback proof, the API lookup
// by reflection, and the open-end classification with the caller's thresholds. Known
// answers are worked by hand.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SelectionContextRulesTests
    {
        private static bool Parse(string json, out SelectionContextRequest r, out string e) =>
            SelectionContextRules.TryParse(JObject.Parse(json), out r, out e);

        [Fact]
        public void Context_with_no_source_reads_the_current_selection_with_every_section()
        {
            Assert.True(Parse("{}", out SelectionContextRequest r, out string e), e);
            Assert.Equal(SelectionContextOps.Context, r.Operation);
            Assert.True(r.Selection);
            Assert.Equal(SelectionContextSections.All.OrderBy(x => x), r.Sections.OrderBy(x => x));
            Assert.Equal(300, r.RadiusMm);
            Assert.Equal(6, r.MaxFaces);
        }

        [Fact]
        public void Context_takes_ids_up_to_fifty_and_refuses_a_selector()
        {
            string ids = "[" + string.Join(",", Enumerable.Range(1, 51)) + "]";
            Assert.False(Parse("{\"ids\":" + ids + "}", out _, out string e));
            Assert.Contains("at most 50", e);
            Assert.True(Parse("{\"ids\":[5,5,7]}", out SelectionContextRequest r, out e), e);
            Assert.Equal(new long[] { 5, 7 }, r.Ids.ToArray());
            Assert.False(r.Selection);
            Assert.False(Parse("{\"selector\":{\"category\":\"Walls\"}}", out _, out e));
            Assert.Contains("snapshot and diff", e);
            Assert.False(Parse("{\"ids\":[1],\"selection\":true}", out _, out e));
            Assert.Contains("one source", e);
        }

        [Fact]
        public void Include_radius_faces_and_parameters_are_bounded()
        {
            Assert.True(Parse("{\"include\":[\"neighbors\",\"geometry\"],\"radius_mm\":1200,\"max_faces\":0,\"parameters\":[\"Base Offset\",\"Base Offset\"]}", out SelectionContextRequest r, out string e), e);
            Assert.Equal(2, r.Sections.Count);
            Assert.Equal(1200, r.RadiusMm);
            Assert.Equal(0, r.MaxFaces);
            Assert.Equal(new[] { "Base Offset" }, r.Parameters.ToArray());
            Assert.False(Parse("{\"include\":[]}", out _, out e));
            Assert.False(Parse("{\"include\":[\"photos\"]}", out _, out e));
            Assert.Contains("photos", e);
            Assert.False(Parse("{\"radius_mm\":6000}", out _, out e));
            Assert.False(Parse("{\"max_faces\":61}", out _, out e));
            Assert.False(Parse("{\"parameters\":[\"\"]}", out _, out e));
            Assert.False(Parse("{\"unit\":\"inch\"}", out _, out e));
        }

        [Fact]
        public void Snapshot_needs_a_name_and_a_source_and_diff_needs_neither_source_nor_self()
        {
            Assert.False(Parse("{\"operation\":\"snapshot\",\"snapshot_name\":\"a\"}", out _, out string e));
            Assert.Contains("never photographs the whole model", e);
            Assert.False(Parse("{\"operation\":\"snapshot\",\"ids\":[1]}", out _, out e));
            Assert.Contains("snapshot_name", e);
            Assert.True(Parse("{\"operation\":\"snapshot\",\"snapshot_name\":\"a\",\"selector\":{\"category\":\"Walls\"}}", out SelectionContextRequest r, out e), e);
            Assert.NotNull(r.Selector);
            Assert.False(Parse("{\"operation\":\"diff\",\"snapshot_name\":\"a\",\"ids\":[1]}", out _, out e));
            Assert.Contains("not a source", e);
            Assert.False(Parse("{\"operation\":\"diff\",\"snapshot_name\":\"a\",\"against\":\"a\"}", out _, out e));
            Assert.False(Parse("{\"operation\":\"snapshot\",\"snapshot_name\":\"a\",\"ids\":[1],\"against\":\"b\"}", out _, out e));
            Assert.Contains("diff only", e);
            Assert.True(Parse("{\"operation\":\"diff\",\"snapshot_name\":\"a\",\"against\":\"b\",\"tolerance_mm\":2}", out r, out e), e);
            Assert.Equal(2, r.ToleranceMm);
        }

        [Fact]
        public void Main_faces_are_the_largest_first_and_ties_keep_their_order()
        {
            Assert.Equal(new[] { 2, 0, 3 }, SelectionContextRules.TopFaces(new[] { 5.0, 1.0, 9.0, 5.0 }, 3).ToArray());
            Assert.Empty(SelectionContextRules.TopFaces(new[] { 1.0 }, 0));
            Assert.Equal(new[] { 1 }, SelectionContextRules.TopFaces(new[] { double.NaN, 2.0 }, 5).ToArray());
        }

        [Fact]
        public void A_neighbour_carries_its_join_relation_or_nothing()
        {
            var joined = new[] { 10L }; var cutBy = new[] { 11L }; var cutting = new[] { 12L };
            Assert.Equal(new[] { "joined" }, SelectionContextRules.NeighborRelations(10, joined, cutBy, cutting).ToArray());
            Assert.Equal(new[] { "cuts_this" }, SelectionContextRules.NeighborRelations(11, joined, cutBy, cutting).ToArray());
            Assert.Equal(new[] { "cut_by_this" }, SelectionContextRules.NeighborRelations(12, joined, cutBy, cutting).ToArray());
            Assert.Empty(SelectionContextRules.NeighborRelations(13, joined, cutBy, cutting));
        }

        [Fact]
        public void A_box_carried_through_a_quarter_turn_keeps_its_size_swapped()
        {
            // 2 x 1 x 3 box rotated 90 degrees about Z: the envelope is 1 x 2 x 3.
            var corners = SelectionContextRules.Corners(new[] { 0.0, 0, 0 }, new[] { 2.0, 1, 3 })
                .Select(p => new[] { -p[1], p[0], p[2] });
            SelectionContextRules.Envelope(corners, out double[] min, out double[] max);
            Assert.Equal(new[] { -1.0, 0, 0 }, min);
            Assert.Equal(new[] { 0.0, 2, 3 }, max);
        }

        private static ElementFingerprint Fp(long id, double x, string mark = null, double? volume = 1)
        {
            var f = new ElementFingerprint { Id = id, Category = "Walls", TypeId = 100, LevelId = 7,
                Points = new List<double[]> { new[] { x, 0.0, 0 }, new[] { x + 10, 0.0, 0 } },
                BoxMin = new[] { x, -0.5, 0 }, BoxMax = new[] { x + 10, 0.5, 10 }, VolumeCubicFeet = volume };
            if (mark != null) f.Params["Mark"] = mark;
            return f;
        }

        [Fact]
        public void Diff_names_added_removed_moved_and_retyped_and_ignores_moves_under_tolerance()
        {
            var before = new Dictionary<long, ElementFingerprint> { [1] = Fp(1, 0), [2] = Fp(2, 0), [3] = Fp(3, 0, "A"), [4] = Fp(4, 0) };
            var after = new Dictionary<long, ElementFingerprint> { [1] = Fp(1, 0.001), [2] = Fp(2, 1), [3] = Fp(3, 0, "B"), [5] = Fp(5, 0) };
            FingerprintDiff d = SelectionContextRules.Compare(before, after, 0.5 / 304.8);
            Assert.Equal(new long[] { 5 }, d.Added.ToArray());
            Assert.Equal(new long[] { 4 }, d.Removed.ToArray());
            Assert.Equal(new long[] { 2, 3 }, d.Changed.Select(c => c.Id).ToArray());
            FingerprintChange moved = d.Changed.Single(c => c.Id == 2);
            Assert.Contains("location", moved.Fields);
            Assert.Contains("bounding_box", moved.Fields);
            Assert.Equal(1.0, moved.MaxDisplacementFeet.Value, 9);
            FingerprintChange marked = d.Changed.Single(c => c.Id == 3);
            Assert.Equal(new[] { "parameters" }, marked.Fields.ToArray());
            Assert.Equal(new[] { "A", "B" }, marked.ParamChanges["Mark"]);
            Assert.Equal(1, d.Unchanged); // id 1 moved 0.3 mm, under 0.5 mm
        }

        [Fact]
        public void An_unreadable_fact_is_not_comparable_never_unchanged()
        {
            var a = Fp(1, 0); a.Unreadable.Add("volume");
            FingerprintDiff d = SelectionContextRules.Compare(new Dictionary<long, ElementFingerprint> { [1] = a },
                                                             new Dictionary<long, ElementFingerprint> { [1] = Fp(1, 0) }, 0.001);
            Assert.Equal(0, d.Unchanged);
            Assert.Equal(new long[] { 1 }, d.NotComparable.ToArray());
        }

        [Fact]
        public void Type_level_and_volume_changes_are_fields_of_their_own()
        {
            var b = Fp(1, 0, volume: 2.0); b.TypeId = 101; b.LevelId = 8;
            FingerprintDiff d = SelectionContextRules.Compare(new Dictionary<long, ElementFingerprint> { [1] = Fp(1, 0) },
                                                             new Dictionary<long, ElementFingerprint> { [1] = b }, 0.001);
            FingerprintChange c = d.Changed.Single();
            Assert.Equal(new[] { "type", "level", "volume" }, c.Fields.ToArray());
            Assert.Equal(1.0, c.VolumeDeltaCubicFeet.Value, 9);
        }

        [Fact]
        public void The_hash_is_stable_across_order_and_changes_with_a_value()
        {
            string h1 = SelectionContextRules.Hash(new[] { Fp(1, 0), Fp(2, 0) });
            string h2 = SelectionContextRules.Hash(new[] { Fp(2, 0), Fp(1, 0) });
            Assert.Equal(h1, h2);
            Assert.NotEqual(h1, SelectionContextRules.Hash(new[] { Fp(1, 0), Fp(2, 0, "X") }));
            Assert.Equal(16, h1.Length);
        }

        [Fact]
        public void The_store_keeps_names_per_document_replaces_by_name_and_drops_the_oldest()
        {
            ElementSnapshotStore.ClearForTests();
            DateTime t0 = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < SelectionContextRules.MaxSnapshotsPerDocument; i++)
                Assert.Null(ElementSnapshotStore.Put(new StoredSnapshot { Name = "s" + i, DocumentKey = "path:a.rvt", TakenUtc = t0.AddMinutes(i) }));
            Assert.Null(ElementSnapshotStore.Put(new StoredSnapshot { Name = "s3", DocumentKey = "path:a.rvt", TakenUtc = t0.AddHours(1) }));
            Assert.Equal("s0", ElementSnapshotStore.Put(new StoredSnapshot { Name = "new", DocumentKey = "path:A.RVT", TakenUtc = t0.AddHours(2) }));
            Assert.Null(ElementSnapshotStore.Get("path:a.rvt", "s0"));
            Assert.NotNull(ElementSnapshotStore.Get("path:a.rvt", "new"));
            Assert.Null(ElementSnapshotStore.Get("path:b.rvt", "new"));
            Assert.Equal("new", ElementSnapshotStore.Names("path:a.rvt").Last());
            ElementSnapshotStore.ClearForTests();
        }

        [Fact]
        public void By_category_counts_most_first_and_names_the_missing_category()
        {
            JObject o = SelectionContextRules.ByCategory(new[] { "Walls", null, "Doors", "Walls" });
            Assert.Equal(new[] { "Walls", "(no category)", "Doors" }.OrderBy(x => x).ToArray(), o.Properties().Select(p => p.Name).OrderBy(x => x).ToArray());
            Assert.Equal("Walls", o.Properties().First().Name);
            Assert.Equal(2, (int)o["Walls"]);
        }
    }

    public class RehearsalRulesTests
    {
        [Fact]
        public void Timeout_without_checkpoint_reports_real_elapsed_time_not_cancellation()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var control = new PythonRunControl(1, () => now);
            now = now.AddSeconds(2); Assert.Equal(PythonRunControl.TimedOut, control.Poll());
            now = now.AddSeconds(3);
            JObject report = control.NotPreemptible(0, 1);
            Assert.Equal(5, (double)report["elapsed_s"]);
            Assert.Null(control.NotPreemptible(1, 1));
            Assert.Null(control.NotPreemptible(0, 6));
            Assert.DoesNotContain("cancelled", report.ToString());
        }
        [Fact]
        public void Deadline_must_not_exceed_timeout()
        {
            Assert.False(PythonRunControl.TryParseDeadline(new JValue  (11), 10, out _, out string error));
            Assert.Contains("deadline_s must be less than or equal to timeout_s", error);
            Assert.True(PythonRunControl.TryParseDeadline(new JValue(10), 10, out double? deadline, out _));
            Assert.Equal(10, deadline);
        }
        [Fact]
        public void Cancellation_requires_group_other_document_and_element_evidence()
        {
            var proof = new JObject { ["group_rolled_back"] = true, ["other_document_changed"] = false,
                ["reread_verified"] = true };
            Assert.True((bool)RehearsalRules.CancellationReport(proof, 12, 3)["rolled_back_verified"]);
            proof["other_document_changed"] = true;
            Assert.Contains("another document changed", RehearsalRules.CancellationReport(proof, 12, 3)["rollback_gaps"].ToString());
            proof["other_document_changed"] = false; proof["group_rolled_back"] = false;
            Assert.False((bool)RehearsalRules.CancellationReport(proof, null, 3)["rolled_back_verified"]);
            proof["group_rolled_back"] = true; proof["reread_verified"] = false; proof["note"] = "element still present";
            Assert.Contains("element still present", RehearsalRules.CancellationReport(proof, null, 3)["rollback_gaps"].ToString());
        }
        [Fact]
        public void Made_and_unmade_inside_the_run_is_no_change_and_modified_excludes_both()
        {
            var c = new RehearsalChanges();
            c.Record(new long[] { 1, 2 }, null, new long[] { 9 }, new[] { "Place walls" });
            c.Record(null, new long[] { 2, 5 }, new long[] { 1, 5, 9 });
            Assert.Equal(new long[] { 1 }, c.Created.ToArray());
            Assert.Equal(new long[] { 5 }, c.Deleted.ToArray());
            Assert.Equal(new long[] { 9 }, c.Modified.ToArray());
            Assert.Equal(2, c.Events);
            Assert.Equal(new[] { "Place walls" }, c.Transactions.ToArray());
        }

        [Fact]
        public void The_rollback_is_verified_only_when_created_are_gone_and_deleted_are_back()
        {
            var c = new RehearsalChanges();
            c.Record(new long[] { 1 }, new long[] { 5 }, null);
            var present = new Dictionary<long, bool?> { [1] = false, [5] = true };
            RehearsalVerdict ok = RehearsalRules.Judge(true, c, id => present[id]);
            Assert.True(ok.RolledBackVerified, ok.Note);

            present[1] = true;
            RehearsalVerdict stuck = RehearsalRules.Judge(true, c, id => present[id]);
            Assert.False(stuck.RolledBackVerified);
            Assert.Equal(new long[] { 1 }, stuck.CreatedStillPresent.ToArray());

            present[1] = false; present[5] = false;
            Assert.Equal(new long[] { 5 }, RehearsalRules.Judge(true, c, id => present[id]).DeletedStillMissing.ToArray());
            present[5] = null;
            Assert.False(RehearsalRules.Judge(true, c, id => present[id]).RolledBackVerified);
            present[5] = true;
            RehearsalVerdict notRolled = RehearsalRules.Judge(false, c, id => present[id]);
            Assert.False(notRolled.RolledBackVerified);
            Assert.Contains("did NOT confirm", notRolled.Note);
        }

        [Fact]
        public void A_block_caps_its_ids_and_counts_by_category()
        {
            JObject b = RehearsalRules.Block(Enumerable.Range(1, 250).Select(i => (long)i), id => id % 2 == 0 ? "Walls" : "Doors");
            Assert.Equal(250, (int)b["count"]);
            Assert.Equal(200, ((JArray)b["ids"]).Count);
            Assert.True((bool)b["ids_truncated"]);
            Assert.Equal(125, (int)b["by_category"]["Walls"]);
        }
    }

    public class ApiHelpRulesTests
    {
        private static readonly List<Type> Types = ApiHelpRules.PublicTypes(new[] { typeof(string).Assembly, typeof(JObject).Assembly });

        [Fact]
        public void A_type_is_found_by_full_name_then_by_simple_name()
        {
            Assert.Equal(typeof(JObject), ApiHelpRules.Find(Types, "Newtonsoft.Json.Linq.JObject").Single());
            Assert.Equal(typeof(JObject), ApiHelpRules.Find(Types, "JObject").Single());
            Assert.Equal(typeof(JObject), ApiHelpRules.Find(Types, "jobject").Single());
            Assert.Empty(ApiHelpRules.Find(Types, "NoSuchTypeAnywhere"));
        }

        [Fact]
        public void A_misspelt_name_gets_the_closest_names()
        {
            List<string> s = ApiHelpRules.Suggest(Types, "JObjct");
            Assert.Contains("Newtonsoft.Json.Linq.JObject", s);
            Assert.True(s.Count <= ApiHelpRules.MaxSuggestions);
        }

        [Fact]
        public void A_missing_member_suggests_reflected_members_and_their_signatures()
        {
            JObject d = ApiHelpRules.Describe(typeof(JObject), "Parze", true, 200);
            Assert.Equal(0, (int)d["members_total"]);
            var candidates = ((JArray)d["closest_members"]).OfType<JObject>().ToList();
            Assert.Contains(candidates, m => (string)m["name"] == "Parse" &&
                ((string)m["signature"]).Contains("Parse(string"));
            Assert.True(candidates.Count <= ApiHelpRules.MaxSuggestions);
            Assert.Null(ApiHelpRules.Describe(typeof(JObject), "Parse", true, 200)["closest_members"]);
        }

        [Fact]
        public void Members_carry_signatures_accessors_and_static_and_filter_by_name()
        {
            JObject d = ApiHelpRules.Describe(typeof(JObject), "Property", true, 200);
            Assert.Equal("Newtonsoft.Json.Linq.JObject", (string)d["full_name"]);
            var members = ((JArray)d["members"]).OfType<JObject>().ToList();
            Assert.All(members, m => Assert.Contains("property", ((string)m["name"]).ToLowerInvariant()));
            JObject method = members.First(m => (string)m["kind"] == "method" && (string)m["name"] == "Property");
            Assert.Contains("JProperty Property(string name", (string)method["signature"]);
            JObject parse = ((JArray)ApiHelpRules.Describe(typeof(JObject), "Parse", true, 200)["members"]).OfType<JObject>().First(m => (string)m["name"] == "Parse");
            Assert.True((bool)parse["static"]);
            JObject count = ((JArray)ApiHelpRules.Describe(typeof(JObject), "Count", true, 200)["members"]).OfType<JObject>().First(m => (string)m["name"] == "Count");
            Assert.Equal("property", (string)count["kind"]);
            Assert.True((bool)count["get"]);
            Assert.False((bool)count["set"]);
        }

        [Fact]
        public void Enums_list_their_values_and_the_cap_is_declared()
        {
            JObject d = ApiHelpRules.Describe(typeof(DayOfWeek), null, true, 3);
            Assert.Equal("enum", (string)d["kind"]);
            Assert.Equal(7, (int)d["members_total"]);
            Assert.Equal(3, ((JArray)d["members"]).Count);
            Assert.True((bool)d["truncated"]);
        }

        [Fact]
        public void Object_members_are_noise_unless_asked_for()
        {
            var names = ((JArray)ApiHelpRules.Describe(typeof(Version), null, true, 500)["members"]).Select(m => (string)m["name"]).ToList();
            Assert.DoesNotContain("GetType", names);
            Assert.Contains("Major", names);
            Assert.Equal("IList<string>", ApiHelpRules.TypeName(typeof(IList<string>)));
            Assert.Equal("int[]", ApiHelpRules.TypeName(typeof(int[])));
        }
    }

    public class HealthMultipleInstancesTests
    {
        [Fact]
        public void Several_instances_are_an_answer_with_the_list_and_target_required()
        {
            var instances = new JArray(new JObject { ["pid"] = 10, ["year"] = "2025" }, new JObject { ["pid"] = 11, ["year"] = "2026" });
            string refusal = "2 Revit instances are running with the bridge loaded (Revit 2025 pid 10, Revit 2026 pid 11) and nothing says which one you mean.";
            Assert.True(HealthState.IsMultipleInstancesRefusal(refusal));
            Assert.False(HealthState.IsMultipleInstancesRefusal("No Revit with process id 5 has published a bridge."));
            JObject body = HealthState.MultipleInstances(instances, refusal);
            Assert.Equal(HealthState.VariasInstancias, (string)body["estado"]);
            Assert.True((bool)body["ok"]);
            Assert.True((bool)body["target_required"]);
            Assert.False((bool)body["ready"]);
            Assert.Equal(2, ((JArray)body["instances"]).Count);
            Assert.Contains("horizun_target", (string)body["que_hacer"]);
            Assert.Contains(HealthState.VariasInstancias, HealthState.All);
        }
    }
}
