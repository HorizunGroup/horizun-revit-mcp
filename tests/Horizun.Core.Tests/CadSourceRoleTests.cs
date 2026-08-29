// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE FAILURE WITH NO SYMPTOM.
//
// A section linked into a model looks exactly like a plan to the reader: lines,
// on layers, with an x and a y. Point a wall rule at one and it converts happily
// - and every check downstream agrees, because they all compare the model against
// the drawing and the model IS what the drawing said. The elements are created,
// the kinds verify, the audit is clean, and the walls are somewhere nobody drew,
// because in a section the horizontal axis is a distance along the section line
// and the vertical axis is height.
//
// Nobody finds that in a reply. Somebody finds it by opening the model.
//
// So a source says which view it is, and a rule may only ask that view for what
// it shows. The role is DECLARED: "A-101-SECTION.dwg" is a string somebody typed,
// and a bridge that read meaning out of file names would be carrying one office's
// convention into everybody else's project.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadSourceRoleTests
    {
        private static string Json(string role, string produces, string category)
        {
            string roleKey = role == null ? "" : ", 'role': '" + role + "'";
            return @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 's', 'version': '1.0.0', 'title': 'Sources' },
              'source': { 'units': 'millimeter'ROLE },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'PRODUCES',
                          'category': 'CATEGORY', 'height_mm': 3000,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                        'max_thickness_mm': 400, 'min_overlap_fraction': 0.5 } }]
            }".Replace('\'', '"').Replace("ROLE", roleKey.Replace('\'', '"'))
              .Replace("PRODUCES", produces).Replace("CATEGORY", category);
        }

        private static CadRequirementSet Load(string role, string produces = "wall",
                                              string category = "OST_Walls")
        {
            return CadRequirementSet.Load(JObject.Parse(Json(role, produces, category)));
        }

        private static CadRequirementSetException Refused(string role, string produces, string category)
        {
            return Assert.Throws<CadRequirementSetException>(() => Load(role, produces, category));
        }

        // ------------------------------------------------------ the ordinary case

        [Fact]
        public void A_set_that_names_no_role_is_read_as_a_floor_plan_and_SAYS_so()
        {
            // Every set written before this key existed was about a floor plan and
            // still is. What must not happen is a set that never says which view it
            // converted and nothing recording that.
            CadRequirementSet set = Load(null);

            Assert.Equal(CadSourceRole.FloorPlan, set.SourceRole);
            Assert.False(set.SourceRoleWasDeclared);
        }

        [Fact]
        public void A_declared_floor_plan_is_recorded_as_declared()
        {
            CadRequirementSet set = Load("floor_plan");

            Assert.Equal(CadSourceRole.FloorPlan, set.SourceRole);
            Assert.True(set.SourceRoleWasDeclared);
        }

        // ------------------------------------------- a view that shows nothing

        [Fact]
        public void A_SECTION_may_not_be_converted_at_all_and_the_refusal_says_why()
        {
            CadRequirementSetException e = Refused("section", "wall", "OST_Walls");

            Assert.Contains("distance ALONG the section line", e.Message);
            Assert.Contains("height", e.Message);
        }

        [Fact]
        public void And_the_refusal_names_what_a_section_is_FOR()
        {
            // A refusal that only says no leaves somebody guessing. This one says
            // where the heights it was going to be used for actually come from.
            CadRequirementSetException e = Refused("section", "wall", "OST_Walls");

            Assert.Contains("height_mm", e.Message);
            Assert.Contains("convert the PLAN", e.Message);
        }

        [Fact]
        public void An_ELEVATION_is_refused_for_its_own_reason_and_not_the_section_s()
        {
            CadRequirementSetException e = Refused("elevation", "window", "OST_Windows");
            Assert.Contains("across a facade", e.Message);
        }

        [Fact]
        public void A_DETAIL_draws_how_something_is_made_and_not_where_anything_is()
        {
            CadRequirementSetException e = Refused("detail", "wall", "OST_Walls");
            Assert.Contains("not where anything is", e.Message);
        }

        [Fact]
        public void A_reference_only_source_is_refused_because_somebody_SAID_so()
        {
            // Not a limitation of the bridge - a decision, and the message treats
            // it as one.
            CadRequirementSetException e = Refused("reference_only", "wall", "OST_Walls");
            Assert.Contains("declared reference_only", e.Message);
            Assert.Contains("Change the role", e.Message);
        }

        // --------------------------------------- a view that shows the wrong thing

        [Fact]
        public void A_reflected_ceiling_plan_cannot_place_a_FLOOR()
        {
            CadRequirementSetException e = Refused("reflected_ceiling_plan", "floor", "OST_Floors");

            Assert.Contains("does not show it", e.Message);
            Assert.Contains("ceiling", e.Message);   // it lists what that view CAN be read for
        }

        [Fact]
        public void But_it_can_place_a_ceiling()
        {
            CadRequirementSet set = Load("reflected_ceiling_plan", "ceiling", "OST_Ceilings");
            Assert.Single(set.Rules);
        }

        [Fact]
        public void A_structural_plan_is_not_where_the_FURNITURE_is()
        {
            CadRequirementSetException e = Refused("structural_plan", "furniture", "OST_Furniture");
            Assert.Contains("does not show it", e.Message);
        }

        [Fact]
        public void A_structural_plan_DOES_carry_the_walls_the_frame_sits_in()
        {
            // The lists are about what a view shows, not about who owns the layer.
            CadRequirementSet set = Load("structural_plan", "wall", "OST_Walls");
            Assert.Single(set.Rules);
        }

        [Fact]
        public void An_mep_plan_carries_the_GRID_it_is_dimensioned_from()
        {
            CadRequirementSet set = Load("mep_plan", "grid", "OST_Grids");
            Assert.Single(set.Rules);
        }

        [Fact]
        public void An_mep_plan_does_not_carry_somebody_else_s_walls()
        {
            CadRequirementSetException e = Refused("mep_plan", "wall", "OST_Walls");
            Assert.Contains("does not show it", e.Message);
        }

        // ------------------------------------------------------------- a typo

        [Fact]
        public void A_role_that_is_not_a_role_is_refused_rather_than_read_as_a_plan()
        {
            // The dangerous default. A misspelt role falling back to floor_plan
            // would convert a section as a plan, which is the whole failure.
            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(
                () => Load("Section"));      // capitalised, therefore not the value

            Assert.Contains("is not a view this bridge knows", e.Message);
            Assert.Contains("builds a building nobody drew", e.Message);
        }

        [Fact]
        public void A_MISSPELT_role_key_is_refused_rather_than_ignored()
        {
            // The source block had no allowlist, so "roles" or "Role" was silently
            // dropped and the drawing read as a floor plan - which is the exact
            // failure the key exists to prevent, arrived at by a typo.
            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(
                () => CadRequirementSet.Load(JObject.Parse(@"{
                  'schema': 'horizun.cad-requirements/1',
                  'requirement_set': { 'id': 's', 'version': '1.0.0', 'title': 'Sources' },
                  'source': { 'units': 'millimeter', 'roles': 'section' },
                  'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
                  'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'wall',
                              'category': 'OST_Walls', 'height_mm': 3000,
                              'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                            'max_thickness_mm': 400, 'min_overlap_fraction': 0.5 } }]
                }".Replace('\'', '"'))));

            Assert.Contains("Unknown key 'roles' in source", e.Message);
            Assert.Contains("builds a building nobody drew", e.Message);
        }

        [Fact]
        public void A_structural_plan_may_cut_the_WALLS_it_draws_as_well_as_the_slabs()
        {
            // It was allowed `opening` and `shaft` and refused `wall_opening`,
            // which was a gap in the table rather than a statement about the view.
            // Asked through the ROLE table directly, so a missing sill height
            // cannot answer for it.
            string why;
            Assert.True(CadSourceRole.Permits(CadSourceRole.StructuralPlan, "wall_opening", out why), why);
        }

        [Fact]
        public void A_floor_plan_can_still_produce_everything_it_could_before()
        {
            // The role table is a REFUSAL surface, so the risk is refusing
            // something legitimate. Every value in the published vocabulary must
            // still be reachable from the view every existing set is written for.
            foreach (string produces in CadRequirementSet.KnownProduces)
            {
                if (produces == "level") continue;   // refused on its own terms: a plan cannot show an elevation
                string why;
                Assert.True(CadSourceRole.Permits(CadSourceRole.FloorPlan, produces, out why),
                            "a floor plan must still produce " + produces + ": " + why);
            }
        }

        [Fact]
        public void Every_published_role_is_either_convertible_or_says_why_not()
        {
            // No role may be silently neither: a reader switching over the list
            // has to get an answer for each one.
            foreach (string role in CadSourceRole.All)
            {
                bool produces = CadSourceRole.CanProduce(role).Count > 0;
                bool explained = CadSourceRole.WhyNothing(role) != null;
                Assert.True(produces ^ explained, role + " must either produce something or say why not");
            }
        }
    }
}
