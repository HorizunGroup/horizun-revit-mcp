// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Pro 3.5.0: the typed tools that replace the purposes the team kept rewriting in
// horizun_execute_python (telemetry 2026-10-19..23, 398 samples, 10.3 % error). Each
// tool has one case with a known answer checked independently of the code under test,
// and one case where something cannot be read and must stay "unreadable", never 0.
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
    public class MeasureGeometryRulesTests
    {
        // A box 300 x 500 in section and 6000 long, rotated by `deg` about Z, built by plain
        // trigonometry (not by the rule under test): along = (cos, sin, 0), across = (-sin, cos, 0).
        private static List<double[]> Beam(double deg, double widthMm, double heightMm, double lengthMm)
        {
            double t = deg * Math.PI / 180.0, c = Math.Cos(t), s = Math.Sin(t);
            var pts = new List<double[]>();
            foreach (double a in new[] { 0.0, lengthMm })
                foreach (double w in new[] { -widthMm / 2, widthMm / 2 })
                    foreach (double h in new[] { 0.0, heightMm })
                        pts.Add(new[] { (a * c - w * s) / 304.8, (a * s + w * c) / 304.8, h / 304.8 });
            return pts;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(37)]
        [InlineData(90)]
        [InlineData(213)]
        public void A_300_by_500_beam_reads_300_by_500_however_it_is_turned(double deg)
        {
            double t = deg * Math.PI / 180.0;
            SectionProfileResult r = GeometryMeasureRules.SectionProfile(Beam(deg, 300, 500, 6000), new[] { Math.Cos(t), Math.Sin(t), 0.0 });
            Assert.True(r.Ok);
            Assert.Equal(300, r.WidthFeet * 304.8, 3);
            Assert.Equal(500, r.HeightFeet * 304.8, 3);
            Assert.Equal(6000, r.LengthFeet * 304.8, 3);
        }

        [Fact]
        public void A_vertical_column_is_measured_across_its_axis()
        {
            var pts = new List<double[]>();
            foreach (double x in new[] { 0.0, 400 }) foreach (double y in new[] { 0.0, 600 }) foreach (double z in new[] { 0.0, 3000 })
                pts.Add(new[] { x / 304.8, y / 304.8, z / 304.8 });
            SectionProfileResult r = GeometryMeasureRules.SectionProfile(pts, new[] { 0.0, 0.0, 1.0 });
            Assert.True(r.Ok);
            Assert.Equal(new[] { 400.0, 600.0 }, new[] { r.WidthFeet * 304.8, r.HeightFeet * 304.8 }.OrderBy(x => x).ToArray(), new DoubleComparer());
            Assert.Equal(3000, r.LengthFeet * 304.8, 3);
        }

        private sealed class DoubleComparer : IEqualityComparer<double[]>
        {
            public bool Equals(double[] a, double[] b) => a.Length == b.Length && a.Zip(b, (x, y) => Math.Abs(x - y) < 1e-3).All(v => v);
            public int GetHashCode(double[] o) => o.Length;
        }

        [Fact]
        public void Without_vertices_or_axis_the_profile_is_unreadable_with_a_reason_and_no_zero()
        {
            SectionProfileResult none = GeometryMeasureRules.SectionProfile(new List<double[]>(), new[] { 1.0, 0, 0 });
            Assert.False(none.Ok);
            Assert.Equal(UnreadableReason.NoSolidGeometry, none.Reason);
            SectionProfileResult noAxis = GeometryMeasureRules.SectionProfile(Beam(0, 300, 500, 6000), new[] { 0.0, 0.0, 0.0 });
            Assert.False(noAxis.Ok);
            Assert.Equal(UnreadableReason.NoAxis, noAxis.Reason);
            // A flat set of points has no section: it must not read as a 0 x 0 member that "exists".
            var flat = new List<double[]> { new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 2.0, 0, 0 }, new[] { 3.0, 0, 0 } };
            SectionProfileResult line = GeometryMeasureRules.SectionProfile(flat, new[] { 1.0, 0, 0 });
            Assert.False(line.Ok);
            Assert.Equal(UnreadableReason.DegenerateProfile, line.Reason);
        }

        [Fact]
        public void The_six_faces_of_a_box_make_six_normals_with_their_areas()
        {
            var faces = new List<KeyValuePair<double[], double>>
            {
                new KeyValuePair<double[], double>(new[] { 1.0, 0, 0 }, 3.0), new KeyValuePair<double[], double>(new[] { -1.0, 0, 0 }, 3.0),
                new KeyValuePair<double[], double>(new[] { 0.0, 1, 0 }, 5.0), new KeyValuePair<double[], double>(new[] { 0.0, -1, 0 }, 5.0),
                new KeyValuePair<double[], double>(new[] { 0.0, 0, 1 }, 7.0), new KeyValuePair<double[], double>(new[] { 0.0, 0, -1 }, 7.0),
                // a face split in two by the modeller, same normal: one group, summed area
                new KeyValuePair<double[], double>(new[] { 0.0, 0, 1 }, 1.0)
            };
            List<NormalGroup> groups = GeometryMeasureRules.GroupNormals(faces);
            Assert.Equal(6, groups.Count);
            Assert.Equal(8.0, groups.Single(g => g.Normal[2] > 0.9).AreaSquareFeet, 6);
            Assert.Equal(2, groups.Single(g => g.Normal[2] > 0.9).Faces);
        }

        [Fact]
        public void The_request_needs_ids_or_a_selector_and_known_measures()
        {
            Assert.False(GeometryMeasureRules.TryParse(JObject.Parse("{\"measures\":[\"solid_volume\"]}"), out _, out string e1));
            Assert.Contains("ids or selector", e1);
            Assert.False(GeometryMeasureRules.TryParse(JObject.Parse("{\"ids\":[1],\"measures\":[\"weight\"]}"), out _, out string e2));
            Assert.Contains("weight", e2);
            Assert.False(GeometryMeasureRules.TryParse(JObject.Parse("{\"ids\":[1],\"selector\":{\"category\":\"Walls\"},\"measures\":[\"bounding_box\"]}"), out _, out string e3));
            Assert.Contains("not both", e3);
            Assert.False(GeometryMeasureRules.TryParse(JObject.Parse("{\"ids\":[1],\"measures\":[\"bounding_box\"],\"unit\":\"yards\"}"), out _, out string e4));
            Assert.Contains("unit", e4);
            Assert.True(GeometryMeasureRules.TryParse(JObject.Parse("{\"ids\":[7,7,8],\"measures\":[\"bounding_box\",\"end_points\",\"bounding_box\"],\"unit\":\"m\"}"), out MeasureRequest ok, out _));
            Assert.Equal(new long[] { 7, 8 }, ok.Ids.ToArray());
            Assert.Equal(2, ok.Measures.Count);
            Assert.Equal(0.3048, ok.LengthFromFeet, 6);
        }
    }

    public class RelationRulesTests
    {
        private static string Cat(long id) => id == 11 ? "Structural Columns" : id == 12 ? "Generic Models" : id == 13 ? "Structural Framing" : "Walls";

        [Fact]
        public void A_wall_with_one_join_and_one_cut_returns_both_each_once()
        {
            var joins = new[] { new JoinFact { OtherId = 11 } };                // a column, joined, nobody cuts
            var voidCutters = new long[] { 12 };                                // an opening family cutting the wall
            Dictionary<string, List<RelatedItem>> r = RelationRules.Classify(joins, voidCutters, new long[0], Cat);
            Assert.Equal(new long[] { 11 }, r[RelationKinds.Joined].Select(i => i.Id).ToArray());
            Assert.Equal("Structural Columns", r[RelationKinds.Joined][0].Category);
            Assert.Equal("geometry_join", r[RelationKinds.Joined][0].RelationType);
            Assert.Equal(new long[] { 12 }, r[RelationKinds.CutBy].Select(i => i.Id).ToArray());
            Assert.Equal("void_cut", r[RelationKinds.CutBy][0].RelationType);
            Assert.Empty(r[RelationKinds.Cutting]);
        }

        [Fact]
        public void A_join_with_a_cutting_direction_is_reported_once_under_the_cut_not_under_joined()
        {
            var joins = new[]
            {
                new JoinFact { OtherId = 13, OtherCutsThis = true },
                new JoinFact { OtherId = 14, ThisCutsOther = true },
                new JoinFact { OtherId = 15 },
                new JoinFact { OtherId = 15 }
            };
            Dictionary<string, List<RelatedItem>> r = RelationRules.Classify(joins, new long[] { 13 }, new long[] { 14 }, Cat);
            Assert.Equal(new long[] { 13 }, r[RelationKinds.CutBy].Select(i => i.Id).Distinct().ToArray());
            Assert.Equal(new long[] { 14 }, r[RelationKinds.Cutting].Select(i => i.Id).Distinct().ToArray());
            Assert.Equal(new long[] { 15 }, r[RelationKinds.Joined].Select(i => i.Id).ToArray());
        }

        [Fact]
        public void Box_distance_is_zero_when_touching_and_the_gap_otherwise()
        {
            double[] min = { 0, 0, 0 }, max = { 1, 1, 1 };
            Assert.Equal(0, RelationRules.BoxDistance(min, max, new[] { 1.0, 0, 0 }, new[] { 2.0, 1, 1 }), 9);
            Assert.Equal(0, RelationRules.BoxDistance(min, max, new[] { 0.5, 0.5, 0.5 }, new[] { 2.0, 2, 2 }), 9);
            Assert.Equal(3.0, RelationRules.BoxDistance(min, max, new[] { 4.0, 0, 0 }, new[] { 5.0, 1, 1 }), 9);
            Assert.Equal(5.0, RelationRules.BoxDistance(min, max, new[] { 4.0, 5, 0 }, new[] { 6.0, 6, 1 }), 9);   // 3-4-5 in plan
        }

        [Fact]
        public void The_request_names_ids_and_known_relations_within_a_bounded_radius()
        {
            Assert.False(RelationRules.TryParse(JObject.Parse("{\"ids\":[1],\"relations\":[\"owner\"]}"), out _, out string e1));
            Assert.Contains("owner", e1);
            Assert.False(RelationRules.TryParse(JObject.Parse("{\"ids\":[1],\"relations\":[\"neighbors\"],\"radius_mm\":99999}"), out _, out string e2));
            Assert.Contains("radius_mm", e2);
            Assert.False(RelationRules.TryParse(JObject.Parse("{\"relations\":[\"joined\"]}"), out _, out _));
            Assert.True(RelationRules.TryParse(JObject.Parse("{\"ids\":[1,2],\"relations\":[\"joined\",\"neighbors\"],\"radius_mm\":300}"), out RelationRequest ok, out _));
            Assert.Equal(300, ok.RadiusMm);
        }
    }

    public class ElementSelectorRulesTests
    {
        private static SelectorFacts Facts(string cat, string family, string type, string name, string param = null, string created = null)
        {
            return new SelectorFacts
            {
                CategoryName = cat, CategoryToken = "OST_" + cat, Family = family, TypeName = type, Name = name,
                HasMark = created != null,
                CreatedUtc = created == null ? (DateTime?)null : DateTime.Parse(created, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                Parameter = n => n == "Comments" ? (param == null ? SelectorParam.Missing : SelectorParam.Of(param)) : SelectorParam.Missing
            };
        }

        private static SelectorSpec Spec(string json)
        {
            Assert.True(ElementSelectorRules.TryParse(JObject.Parse(json), out SelectorSpec spec, out string error), error);
            return spec;
        }

        [Fact]
        public void An_empty_or_unknown_selector_is_refused()
        {
            Assert.False(ElementSelectorRules.TryParse(JObject.Parse("{}"), out _, out string e1));
            Assert.Contains("empty", e1);
            Assert.False(ElementSelectorRules.TryParse(JObject.Parse("{\"colour\":\"red\"}"), out _, out string e2));
            Assert.Contains("colour", e2);
            Assert.False(ElementSelectorRules.TryParse(JObject.Parse("{\"created_since\":\"yesterday-ish\"}"), out _, out _));
        }

        [Fact]
        public void Category_family_prefix_and_parameter_narrow_together()
        {
            SelectorSpec spec = Spec("{\"category\":\"Pipes\",\"prefix\":\"HZ_\",\"parameter\":{\"name\":\"Comments\",\"value\":\"temp\"}}");
            Assert.Equal(SelectorVerdict.Match, ElementSelectorRules.Evaluate(spec, Facts("Pipes", "Pipe Types", "HZ_Test", "x", "TEMP")));
            Assert.Equal(SelectorVerdict.NoMatch, ElementSelectorRules.Evaluate(spec, Facts("Ducts", "Pipe Types", "HZ_Test", "x", "temp")));
            Assert.Equal(SelectorVerdict.NoMatch, ElementSelectorRules.Evaluate(spec, Facts("Pipes", "Pipe Types", "Other", "x", "temp")));
            Assert.Equal(SelectorVerdict.NoMatch, ElementSelectorRules.Evaluate(spec, Facts("Pipes", "Pipe Types", "HZ_Test", "x", "final")));
            Assert.Equal(SelectorVerdict.NoMatch, ElementSelectorRules.Evaluate(spec, Facts("Pipes", "Pipe Types", "HZ_Test", "x")));
        }

        [Fact]
        public void A_parameter_that_cannot_be_read_is_unknown_not_a_miss()
        {
            SelectorSpec spec = Spec("{\"parameter\":\"Comments=temp\"}");
            SelectorFacts f = Facts("Pipes", "F", "T", "n");
            f.Parameter = _ => SelectorParam.Unreadable;
            Assert.Equal(SelectorVerdict.Unknown, ElementSelectorRules.Evaluate(spec, f));
        }

        [Fact]
        public void Created_since_matches_only_marked_elements_and_counts_the_rest_as_undatable()
        {
            SelectorSpec spec = Spec("{\"category\":\"Walls\",\"created_since\":\"2026-10-23T08:00:00Z\"}");
            Assert.Equal(SelectorVerdict.Match, ElementSelectorRules.Evaluate(spec, Facts("Walls", "W", "T", "n", null, "2026-10-23T09:00:00Z")));
            Assert.Equal(SelectorVerdict.NoMatch, ElementSelectorRules.Evaluate(spec, Facts("Walls", "W", "T", "n", null, "2026-10-22T09:00:00Z")));
            Assert.Equal(SelectorVerdict.Undatable, ElementSelectorRules.Evaluate(spec, Facts("Walls", "W", "T", "n")));
        }

        [Fact]
        public void Numeric_parameter_values_compare_as_numbers()
        {
            Assert.True(ElementSelectorRules.ParameterValueMatches("3000", "3000.0"));
            Assert.True(ElementSelectorRules.ParameterValueMatches("Temp", " temp "));
            Assert.False(ElementSelectorRules.ParameterValueMatches("3000", "3001"));
            Assert.False(ElementSelectorRules.ParameterValueMatches(null, "3000"));
        }
    }

    public class DeleteBySelectorFlowTests
    {
        // A tiny model: element id -> (category, parent that takes it along when deleted).
        private sealed class FakeModel
        {
            public Dictionary<long, string> Category = new Dictionary<long, string>();
            public Dictionary<long, long> TakenAlongBy = new Dictionary<long, long>();   // child -> parent
            public List<long> Matching(string category) => Category.Where(kv => kv.Value == category).Select(kv => kv.Key).OrderBy(x => x).ToList();
            public List<CascadeEdge> CascadeOf(IEnumerable<long> deleted)
            {
                var set = new HashSet<long>(deleted);
                return TakenAlongBy.Where(kv => set.Contains(kv.Value) && Category.ContainsKey(kv.Key)).Select(kv => new CascadeEdge { ParentId = kv.Value, ChildId = kv.Key }).ToList();
            }
            public void Delete(IEnumerable<long> ids) { foreach (long id in ids.ToList()) { Category.Remove(id); } foreach (long c in TakenAlongBy.Where(kv => !Category.ContainsKey(kv.Value)).Select(kv => kv.Key).ToList()) Category.Remove(c); }
        }

        private static FakeModel Build()
        {
            var m = new FakeModel();
            foreach (long id in new long[] { 101, 102, 103 }) m.Category[id] = "Pipes";
            m.Category[201] = "Pipe Fittings"; m.TakenAlongBy[201] = 102;     // a fitting hosted on pipe 102
            m.Category[301] = "Walls";
            return m;
        }

        [Fact]
        public void Deleting_by_selector_leaves_the_selector_at_zero_and_the_rehearsed_cascade_is_the_applied_one()
        {
            FakeModel model = Build();
            List<long> selected = model.Matching("Pipes");
            List<CascadeEdge> rehearsal = model.CascadeOf(selected);                 // dry run
            Assert.Equal(new long[] { 201 }, rehearsal.Select(c => c.ChildId).ToArray());
            List<ProtectSuggestion> suggestions = DeleteSelectorRules.SuggestProtect(selected, rehearsal);
            Assert.Single(suggestions);
            Assert.Equal(102, suggestions[0].ProtectId);
            Assert.Equal(new long[] { 201 }, suggestions[0].WouldAlsoTake.ToArray());

            List<CascadeEdge> applied = model.CascadeOf(selected);
            model.Delete(selected);                                                   // apply
            Assert.True(DeleteSelectorRules.CascadeAgrees(rehearsal.Select(c => c.ChildId), applied.Select(c => c.ChildId)));

            SelectorReread reread = DeleteSelectorRules.Reread(selected.Count, model.Matching("Pipes"), new long[0], 0);   // same selector again
            Assert.Equal(0, reread.Remaining);
            Assert.True(reread.Cleared);
            Assert.Equal(3, reread.MatchedBefore);
            Assert.Contains(301, model.Category.Keys);                               // what the selector did not name survived
        }

        [Fact]
        public void A_protected_survivor_is_reported_apart_and_an_unprotected_one_is_not_cleared()
        {
            var protectedOnly = DeleteSelectorRules.Reread(3, new long[] { 102 }, new long[] { 102 }, 0);
            Assert.True(protectedOnly.Cleared);
            Assert.Equal(1, protectedOnly.RemainingProtected);
            var leftover = DeleteSelectorRules.Reread(3, new long[] { 102, 103 }, new long[] { 102 }, 0);
            Assert.False(leftover.Cleared);
            Assert.Equal(new long[] { 103 }, leftover.RemainingUnprotectedIds.ToArray());
            var blind = DeleteSelectorRules.Reread(3, new long[0], new long[0], 2);
            Assert.False(blind.Cleared);   // elements that could not be read are never counted as gone
        }

        [Fact]
        public void A_cascade_that_differs_between_rehearsal_and_apply_does_not_agree()
        {
            Assert.False(DeleteSelectorRules.CascadeAgrees(new long[] { 201 }, new long[] { 201, 202 }));
            Assert.True(DeleteSelectorRules.CascadeAgrees(new long[] { 202, 201 }, new long[] { 201, 202 }));
        }
    }

    public class Pro350WiringTests
    {
        private static string Repo()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        [Fact]
        public void The_delete_selector_path_uses_the_catalogue_for_its_refusals()
        {
            string text = File.ReadAllText(Path.Combine(Repo(), "src", "Horizun.Revit", "Commands", "DeleteCommand.cs"));
            int start = text.IndexOf("private static CommandResult DeleteBySelector", StringComparison.Ordinal);
            int end = text.IndexOf("Mode 2: purge unused", start, StringComparison.Ordinal);
            string body = text.Substring(start, end - start);
            Assert.DoesNotContain("CommandResult.Fail(", body);
            Assert.Contains("ErrorCodes.VerificacionFallida", body);
            Assert.Contains("SelectorResolver.Resolve", body);
        }
    }
}
