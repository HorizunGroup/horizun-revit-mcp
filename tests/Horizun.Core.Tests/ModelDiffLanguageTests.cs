// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// FIELD FEEDBACK 2.10: the diff of a model must not change because
// Revit's interface changed language. The same model read in an English and a
// Spanish Revit carries the same BuiltInCategory and the same type UniqueId,
// and different category and system-family NAMES ("Walls"/"Muros",
// "Basic Wall"/"Muro básico"). Those names are display, never identity.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ModelDiffLanguageTests
    {
        private static DiffElement Wall(string uid, string cat, string family, double x = 0, double? volume = 10)
        {
            var e = new DiffElement
            {
                UniqueId = uid, Id = 100, BuiltInCategory = "OST_Walls", Category = cat, Family = family,
                Type = "Generic - 200mm", TypeUniqueId = "type-1", Level = "L1",
                Location = new[] { x, 0.0, 0.0, x + 10, 0.0, 0.0 },
                BoundingBox = new[] { x, -0.5, 0, x + 10, 0.5, 10 }, GeometryHash = "g", Volume = volume
            };
            e.Parameters["bip:ALL_MODEL_MARK"] = "s:M1";
            return e;
        }

        private static DiffSnapshot Snap(params DiffElement[] els) => new DiffSnapshot { Id = "s", Elements = els.ToList() };

        [Fact]
        public void The_same_model_read_in_two_languages_has_no_changes()
        {
            var en = Snap(Wall("u1", "Walls", "Basic Wall"));
            var es = Snap(Wall("u1", "Muros", "Muro básico"));
            DiffResult r = ModelDiffRules.Compare(en, es, new DiffOptions());
            Assert.Empty(r.Rows);
            Assert.Equal(1, r.Unchanged);
        }

        [Fact]
        public void A_type_change_is_decided_by_the_type_unique_id_not_its_names()
        {
            var was = Wall("u1", "Walls", "Basic Wall");
            var now = Wall("u1", "Muros", "Muro básico");
            now.TypeUniqueId = "type-2";
            DiffRow row = ModelDiffRules.Compare(Snap(was), Snap(now), new DiffOptions()).Rows.Single();
            Assert.True(row.TypeChanged);
            Assert.Equal("OST_Walls", row.BuiltInCategory);
        }

        [Fact]
        public void By_category_is_keyed_by_built_in_category_with_the_changed_parameters()
        {
            var before = Snap(Wall("u1", "Walls", "Basic Wall"), Wall("u2", "Walls", "Basic Wall", 20));
            var w1 = Wall("u1", "Muros", "Muro básico");
            w1.Parameters["bip:ALL_MODEL_MARK"] = "s:M2";
            var added = Wall("u3", "Muros", "Muro básico", 40);
            var after = Snap(w1, added);
            JObject s = ModelDiffRules.Summary(ModelDiffRules.Compare(before, after, new DiffOptions()));
            JObject walls = (JObject)s["by_category"]["OST_Walls"];
            Assert.NotNull(walls);
            Assert.Equal(1, (int)walls["added"]);
            Assert.Equal(1, (int)walls["deleted"]);
            Assert.Equal(1, (int)walls["modified"]);
            Assert.Equal("Muros", (string)walls["name"]);
            Assert.Equal(1, (int)walls["changed_fields"]["param:bip:ALL_MODEL_MARK"]);
            Assert.Null(s["by_category"]["Muros"]);
            Assert.Null(s["by_category"]["Walls"]);
        }

        [Fact]
        public void A_snapshot_listing_counts_by_built_in_category()
        {
            JObject meta = ModelDiffRules.Meta(Snap(Wall("u1", "Walls", "Basic Wall"), Wall("u2", "Muros", "Muro básico")), "x", 1);
            Assert.Equal(2, (int)meta["by_category"]["OST_Walls"]);
        }

        [Fact]
        public void The_heuristic_pairs_across_languages_by_category_and_type_identity()
        {
            var deleted = new System.Collections.Generic.List<DiffElement> { Wall("d1", "Walls", "Basic Wall") };
            var added = new System.Collections.Generic.List<DiffElement> { Wall("a1", "Muros", "Muro básico") };
            Assert.Single(ModelDiffRules.Pair(deleted, added, 1.0));
        }

        [Fact]
        public void A_volume_change_is_reported_in_cubic_metres_and_an_unread_volume_is_not_a_change()
        {
            var was = Wall("u1", "Walls", "Basic Wall", volume: 10);
            var now = Wall("u1", "Walls", "Basic Wall", volume: 12);
            DiffChange vol = ModelDiffRules.Compare(Snap(was), Snap(now), new DiffOptions()).Rows.Single()
                .Changes.Single(c => c.Field == "volume_m3");
            Assert.Equal("0.283168", vol.Before);
            Assert.Equal("0.339802", vol.After);

            var old = Wall("u1", "Walls", "Basic Wall", volume: null);
            Assert.Empty(ModelDiffRules.Compare(Snap(old), Snap(now), new DiffOptions()).Rows);
        }
    }
}
