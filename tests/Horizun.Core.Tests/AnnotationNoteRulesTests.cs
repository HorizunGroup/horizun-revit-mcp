// horizun_annotate on_failure / load_tag_family and horizun_plan_annotations model_notes, without Revit.
// FIELD FEEDBACK 2.4 / E5: one tag without a loaded tag family threw away the
// whole batch of twelve; notes from the model had no traceability.
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AnnotationNoteRulesTests
    {
        [Fact]
        public void On_failure_defaults_to_atomic_and_refuses_unknown_words()
        {
            string err;
            Assert.Equal("atomic", AnnotationNoteRules.ParseOnFailure(null, out err)); Assert.Null(err);
            Assert.Equal("best_effort", AnnotationNoteRules.ParseOnFailure(new JValue("best_effort"), out err));
            Assert.Null(AnnotationNoteRules.ParseOnFailure(new JValue("partial"), out err)); Assert.NotNull(err);
            Assert.Null(AnnotationNoteRules.ParseOnFailure(new JValue(true), out err)); Assert.NotNull(err);
        }

        [Fact]
        public void Best_effort_binds_the_applied_rows_into_the_hash_and_atomic_does_not()
        {
            Assert.Equal("h", AnnotationNoteRules.BindApplySet("h", "atomic", new[] { 0, 2 }));
            string a = AnnotationNoteRules.BindApplySet("h", "best_effort", new[] { 2, 0 });
            Assert.Equal("h|best_effort:0,2", a);
            Assert.NotEqual(a, AnnotationNoteRules.BindApplySet("h", "best_effort", new[] { 0, 1, 2 }));
        }

        private static List<NoteLayer> Wall() => new List<NoteLayer>
        {
            new NoteLayer { Function = "Finish1", Material = "Pañete exterior", ThicknessMm = 15 },
            new NoteLayer { Function = "Structure", Material = "Ladrillo", ThicknessMm = 120, IsCore = true },
            new NoteLayer { Function = "Finish2", Material = "Pintura vinílica", ThicknessMm = 2.5 }
        };

        [Fact]
        public void Layers_text_lists_exterior_to_interior_with_total_and_source_id_last()
        {
            string t = AnnotationNoteRules.LayersText("Muro ext 137.5", Wall(), 12345, true);
            string[] lines = t.Split('\n');
            Assert.Equal("Muro ext 137.5 (137.5 mm)", lines[0]);
            Assert.Equal("1. Acabado 1 - Pañete exterior - 15 mm", lines[1]);
            Assert.Equal("2. Estructura - Ladrillo - 120 mm [núcleo]", lines[2]);
            Assert.Equal("3. Acabado 2 - Pintura vinílica - 2.5 mm", lines[3]);
            Assert.Equal("id 12345", lines[4]);
        }

        [Fact]
        public void Source_id_line_can_be_omitted_and_empty_layers_refuse()
        {
            Assert.DoesNotContain("id 7", AnnotationNoteRules.LayersText("T", Wall(), 7, false));
            Assert.Throws<System.ArgumentException>(() => AnnotationNoteRules.LayersText("T", new List<NoteLayer>(), 7, true));
        }

        [Fact]
        public void Params_text_says_sin_dato_for_unread_values_never_blank()
        {
            string t = AnnotationNoteRules.ParamsText("Piso 100", null, null, AnnotationNoteRules.FinishFromLayers(Wall()), 9, true);
            Assert.Equal("Tipo: Piso 100\nMaterial: sin dato\nEspesor: sin dato\nAcabado: Pañete exterior / Pintura vinílica\nid 9", t);
        }

        [Fact]
        public void Approved_list_parses_array_or_object_and_bad_json_approves_nothing()
        {
            string err;
            Assert.Equal(new[] { "C:/a.rfa" }, AnnotationNoteRules.ParseApprovedList(@"[""C:/a.rfa""]", out err));
            Assert.Equal(new[] { "C:/b.rfa" }, AnnotationNoteRules.ParseApprovedList(@"{""paths"":[""C:/b.rfa"", 3]}", out err));
            Assert.Empty(AnnotationNoteRules.ParseApprovedList("{nope", out err)); Assert.NotNull(err);
        }

        [Fact]
        public void Only_user_listed_paths_or_the_autodesk_library_are_approved()
        {
            // Exercise the host path API on both Windows and Linux, including traversal.
            string root = Path.Combine(Path.GetTempPath(), "hz-annotation-tests");
            string pd = Path.Combine(root, "ProgramData");
            string list = Path.Combine(root, ".horizun", "approved-tag-families.json");
            string library = Path.Combine(pd, "Autodesk", "RVT 2026");
            string family = Path.Combine(root, "Fam", "Etiqueta.rfa");
            Assert.Null(AnnotationNoteRules.CheckApproved(Path.Combine(library, "Libraries", "Spanish", "Anotaciones", "Etiqueta de puerta.rfa"), null, pd, list));
            Assert.Null(AnnotationNoteRules.CheckApproved(family, new[] { family.ToLowerInvariant() }, pd, list));
            string refused = AnnotationNoteRules.CheckApproved(Path.Combine(root, "Fam", "Otra.rfa"), new[] { family }, pd, list);
            Assert.Contains("approved-tag-families.json", refused);
            Assert.NotNull(AnnotationNoteRules.CheckApproved(Path.Combine(pd, "Autodesk", "Evil", "x.rfa"), null, pd, list));
            Assert.NotNull(AnnotationNoteRules.CheckApproved(Path.Combine(library, "..", "..", "x.rfa"), null, pd, list));
            Assert.NotNull(AnnotationNoteRules.CheckApproved(Path.Combine(library, "x.rvt"), null, pd, list));
            Assert.NotNull(AnnotationNoteRules.CheckApproved("relativo.rfa", null, pd, list));
        }

        [Fact]
        public void Tag_categories_are_the_OST_Tags_family()
        {
            Assert.True(AnnotationNoteRules.IsTagCategoryName("OST_DoorTags"));
            Assert.True(AnnotationNoteRules.IsTagCategoryName("OST_MaterialTags"));
            Assert.False(AnnotationNoteRules.IsTagCategoryName("OST_GenericAnnotation"));
            Assert.False(AnnotationNoteRules.IsTagCategoryName("OST_Doors"));
        }
    }
}
