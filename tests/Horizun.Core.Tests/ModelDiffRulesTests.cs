// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_model_diff's comparison, proved without a building: normalisation,
// identity by UniqueId, moves against the tolerance, parameter before/after,
// type changes, the re-created-model flag and the heuristic pairing that only
// ever produces rows marked inferred.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ModelDiffRulesTests
    {
        private static DiffElement El(string uid, double x, string type = "T1", string cat = "Walls",
                                      string bic = "OST_Walls", params string[] kv)
        {
            var e = new DiffElement
            {
                UniqueId = uid, Id = uid.GetHashCode() & 0xffff, BuiltInCategory = bic, Category = cat, Family = "F",
                Type = type, TypeUniqueId = "type-" + type, Level = "L1", Location = new[] { x, 0.0, 0.0 },
                BoundingBox = new[] { x - 1, -1, 0, x + 1, 1, 3 }, GeometryHash = "g"
            };
            for (int i = 0; i + 1 < kv.Length; i += 2) e.Parameters[kv[i]] = kv[i + 1];
            return e;
        }

        private static DiffSnapshot Snap(string id, params DiffElement[] els)
            => new DiffSnapshot { Id = id, Elements = els.ToList() };

        [Fact]
        public void Doubles_are_rounded_so_float_noise_is_not_a_change()
        {
            Assert.Equal(ModelDiffRules.NormalizeDouble(3.0), ModelDiffRules.NormalizeDouble(3.0000000000001));
            Assert.Equal("d:0", ModelDiffRules.NormalizeDouble(-0.0000000000001));
            Assert.True(ModelDiffRules.SameValue("d:1.0000001", "d:1.0000002", 1e-6));
            Assert.False(ModelDiffRules.SameValue("d:1.1", "d:1.2", 1e-6));
            Assert.True(ModelDiffRules.SameValue("i:5", "d:5", 1e-6));
            Assert.False(ModelDiffRules.SameValue("s:5", "i:5", 1e-6));
        }

        [Fact]
        public void Strings_are_trimmed_capped_and_displayed_without_prefix()
        {
            Assert.Equal("s:abc", ModelDiffRules.NormalizeString("  abc "));
            Assert.Equal(ModelDiffRules.NoValue, ModelDiffRules.NormalizeString(null));
            string longOne = ModelDiffRules.NormalizeString(new string('x', 1000));
            Assert.True(longOne.Length <= ModelDiffRules.MaxStringLength + 3);
            Assert.Equal("abc", ModelDiffRules.Display("s:abc"));
            Assert.Equal("", ModelDiffRules.Display(ModelDiffRules.NoValue));
        }

        [Fact]
        public void Identity_is_the_unique_id_added_deleted_and_unchanged()
        {
            var before = Snap("a", El("u1", 0), El("u2", 10));
            var after = Snap("b", El("u2", 10), El("u3", 20));
            DiffResult r = ModelDiffRules.Compare(before, after, new DiffOptions());
            Assert.Equal(1, r.Count(ModelDiffRules.Added));
            Assert.Equal(1, r.Count(ModelDiffRules.Deleted));
            Assert.Equal(0, r.Count(ModelDiffRules.Modified));
            Assert.Equal(1, r.Unchanged);
            Assert.Equal("u3", r.Rows.Single(x => x.State == ModelDiffRules.Added).UniqueId);
            Assert.Equal("u1", r.Rows.Single(x => x.State == ModelDiffRules.Deleted).UniqueId);
        }

        [Fact]
        public void A_move_below_the_tolerance_is_not_a_move_and_above_it_is_reported_in_mm()
        {
            double oneMm = 1.0 / 304.8;
            var before = Snap("a", El("u1", 0), El("u2", 0));
            var after = Snap("b", El("u1", 0.5 * oneMm), El("u2", 100 * oneMm));
            DiffResult r = ModelDiffRules.Compare(before, after, new DiffOptions());
            DiffRow moved = r.Rows.Single();
            Assert.Equal("u2", moved.UniqueId);
            Assert.Equal(100.0, moved.MovedMm.Value, 1);
            Assert.Contains(moved.Changes, c => c.Field == "location");
        }

        [Fact]
        public void A_parameter_change_carries_before_and_after_and_a_type_change_is_flagged()
        {
            var before = Snap("a", El("u1", 0, "T1", kv: new[] { "Mark", "s:A" }));
            var after = Snap("b", El("u1", 0, "T2", kv: new[] { "Mark", "s:B", "Comments", "s:new" }));
            DiffRow row = ModelDiffRules.Compare(before, after, new DiffOptions()).Rows.Single();
            Assert.True(row.TypeChanged);
            DiffChange mark = row.Changes.Single(c => c.Field == "param:Mark");
            Assert.Equal("A", mark.Before);
            Assert.Equal("B", mark.After);
            DiffChange added = row.Changes.Single(c => c.Field == "param:Comments");
            Assert.Null(added.Before);
            Assert.Equal("new", added.After);
        }

        [Fact]
        public void Type_parameter_changes_are_reported_once_on_the_type()
        {
            var t0 = new DiffType { UniqueId = "type-T1", Name = "T1" }; t0.Parameters["Width"] = "d:0.5";
            var t1 = new DiffType { UniqueId = "type-T1", Name = "T1" }; t1.Parameters["Width"] = "d:0.6";
            var before = Snap("a", El("u1", 0)); before.Types.Add(t0);
            var after = Snap("b", El("u1", 0)); after.Types.Add(t1);
            DiffResult r = ModelDiffRules.Compare(before, after, new DiffOptions());
            Assert.Empty(r.Rows);
            Assert.Equal("type_param:Width", r.TypeRows.Single().Changes.Single().Field);
        }

        [Fact]
        public void A_recreated_model_is_flagged_and_not_paired_unless_asked()
        {
            var b = Enumerable.Range(0, 20).Select(i => El("old" + i, i * 10)).ToArray();
            var a = Enumerable.Range(0, 20).Select(i => El("new" + i, i * 10)).ToArray();
            DiffResult plain = ModelDiffRules.Compare(Snap("a", b), Snap("b", a), new DiffOptions());
            Assert.True(plain.RecreatedSuspected);
            Assert.Equal(20, plain.Count(ModelDiffRules.Added));
            Assert.Equal(0, plain.InferredPairs);
            Assert.NotNull(ModelDiffRules.Summary(plain)["identity_warning"]);

            DiffResult paired = ModelDiffRules.Compare(Snap("a", b), Snap("b", a), new DiffOptions { HeuristicMatch = true });
            Assert.Equal(20, paired.InferredPairs);
            Assert.Equal(0, paired.Count(ModelDiffRules.Added));
            Assert.Equal(0, paired.Count(ModelDiffRules.Deleted));
            Assert.All(paired.Rows, row => { Assert.True(row.Inferred); Assert.NotNull(row.BeforeUniqueId); });
        }

        [Fact]
        public void The_heuristic_never_pairs_across_types_or_beyond_the_tolerance()
        {
            var deleted = new List<DiffElement> { El("d1", 0, "T1"), El("d2", 100, "T1") };
            var added = new List<DiffElement> { El("a1", 0, "T2"), El("a2", 100.5, "T1") };
            var pairs = ModelDiffRules.Pair(deleted, added, 1.0);
            var pair = Assert.Single(pairs);
            Assert.Equal("d2", pair.Key.UniqueId);
            Assert.Equal("a2", pair.Value.UniqueId);
        }

        [Fact]
        public void The_heuristic_takes_the_nearest_candidate_and_uses_each_once()
        {
            var deleted = new List<DiffElement> { El("d1", 0), El("d2", 0.1) };
            var added = new List<DiffElement> { El("a1", 0.05), El("a2", 5) };
            var pairs = ModelDiffRules.Pair(deleted, added, 0.2);
            var pair = Assert.Single(pairs);
            Assert.Equal("a1", pair.Value.UniqueId);
        }

        [Fact]
        public void Summary_groups_by_category_discipline_and_level()
        {
            var before = Snap("a", El("u1", 0));
            var after = Snap("b", El("u1", 0), El("p1", 5, "P", "Pipes", "OST_PipeCurves"));
            JObject s = ModelDiffRules.Summary(ModelDiffRules.Compare(before, after, new DiffOptions()));
            Assert.Equal(1, (int)s["by_category"]["Pipes"]["added"]);
            Assert.Equal(1, (int)s["by_discipline"]["plumbing"]["added"]);
            Assert.Equal(1, (int)s["by_level"]["L1"]["added"]);
        }

        [Theory]
        [InlineData("OST_StructuralColumns", "structure")]
        [InlineData("OST_Rebar", "structure")]
        [InlineData("OST_DuctCurves", "mechanical")]
        [InlineData("OST_Sprinklers", "plumbing")]
        [InlineData("OST_LightingFixtures", "electrical")]
        [InlineData("OST_Walls", "architecture")]
        [InlineData(null, "unknown")]
        public void Discipline_is_inferred_from_the_built_in_category(string bic, string expected)
            => Assert.Equal(expected, ModelDiffRules.DisciplineOf(bic));

        [Fact]
        public void Paging_states_total_returned_and_the_next_offset()
        {
            var rows = Enumerable.Range(0, 5).Select(i => new DiffRow { State = "added", UniqueId = "u" + i }).ToList();
            JObject page = ModelDiffRules.Page(rows, 2, 2);
            Assert.Equal(5, (int)page["total"]);
            Assert.Equal(2, (int)page["returned"]);
            Assert.True((bool)page["truncated"]);
            Assert.Equal(4, (int)page["next_offset"]);
            JObject last = ModelDiffRules.Page(rows, 4, 10);
            Assert.False((bool)last["truncated"]);
            Assert.Equal(JTokenType.Null, last["next_offset"].Type);
        }

        [Fact]
        public void Csv_quotes_and_neutralises_formulas()
        {
            Assert.Equal("\"a,b\"", ModelDiffRules.CsvCell("a,b"));
            Assert.Equal("'=SUM(A1)", ModelDiffRules.CsvCell("=SUM(A1)"));
            Assert.Equal("-5", ModelDiffRules.CsvCell("-5"));
            var before = Snap("a", El("u1", 0, kv: new[] { "Mark", "s:x\"y" }));
            var after = Snap("b", El("u1", 0, kv: new[] { "Mark", "s:z" }));
            string csv = ModelDiffRules.Csv(ModelDiffRules.Compare(before, after, new DiffOptions()));
            Assert.StartsWith("state,unique_id,", csv);
            Assert.Contains("param:Mark,\"x\"\"y\",z", csv);
        }

        [Fact]
        public void A_snapshot_round_trips_through_gzip_with_its_dates_as_written()
        {
            var s = Snap("20260924T101010Z-abcd1234", El("u1", 1.5, kv: new[] { "Mark", "s:A" }));
            s.TakenUtc = "2026-09-24T10:10:10Z";
            s.Document["title"] = "m";
            byte[] bytes = ModelDiffRules.Serialize(s);
            DiffSnapshot back = ModelDiffRules.Deserialize(bytes);
            Assert.Equal("2026-09-24T10:10:10Z", back.TakenUtc);
            Assert.Equal("s:A", back.Elements.Single().Parameters["Mark"]);
            Assert.Empty(ModelDiffRules.Compare(s, back, new DiffOptions()).Rows);
            JObject meta = ModelDiffRules.Meta(back, ModelDiffRules.Sha256Hex(bytes), bytes.Length);
            Assert.Equal(1, (int)meta["by_category"]["Walls"]);
        }

        [Fact]
        public void Ids_that_could_walk_out_of_the_directory_are_refused()
        {
            Assert.True(ModelDiffRules.IsValidId(ModelDiffRules.NewId(System.DateTime.UtcNow, "x")));
            Assert.False(ModelDiffRules.IsValidId("..\\x"));
            Assert.False(ModelDiffRules.IsValidId("a/b"));
            Assert.False(ModelDiffRules.IsValidId(""));
        }

        [Fact]
        public void Geometry_hash_changes_with_volume_and_extent_only()
        {
            string a = ModelDiffRules.GeometryHash(1.0, 2.0, new double[] { 0, 0, 0, 1, 1, 1 });
            Assert.Equal(a, ModelDiffRules.GeometryHash(1.0, 2.0, new double[] { 5, 5, 5, 6, 6, 6 }));
            Assert.NotEqual(a, ModelDiffRules.GeometryHash(1.1, 2.0, new double[] { 0, 0, 0, 1, 1, 1 }));
            Assert.NotEqual(a, ModelDiffRules.GeometryHash(1.0, 2.0, new double[] { 0, 0, 0, 2, 1, 1 }));
        }
    }
}
