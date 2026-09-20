// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE ARITHMETIC THAT DECIDES WHERE SOMEBODY'S BUILDING LANDS.
//
// Every number these three files produce ends up as a coordinate in a model. A
// placement composed in the wrong order, a unit read as metres when the file said
// millimetres, a profile whose hole was quietly dropped - none of them throw, none
// of them look wrong in the reply, and all of them are discovered by a person
// opening the model a week later.
//
// So the cases here are the ones a live model makes expensive to stage: a rotated
// storey with a rotated wall inside it, a file in feet, a placement chain that
// loops, a RefDirection that is not perpendicular to its axis, a slab whose
// opening cannot be expressed. Each is a one-line fixture here and an afternoon
// in Revit.
//
// NOT EXECUTED. Written under a standing instruction that authorises writing
// tests and does not authorise running them. Nothing in this file has been run,
// and no claim anywhere in this campaign rests on it passing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IfcGeometryTests
    {
        // =====================================================================
        // Units
        // =====================================================================

        [Fact]
        public void Millimetre_and_metre_files_differ_by_a_thousand()
        {
            string basis;
            Assert.Equal(1.0, IfcPlacement.LengthScale(Parse(
                "#1=IFCUNITASSIGNMENT((#2));\n#2=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);"), out basis));
            Assert.Contains("MILLI", basis);

            Assert.Equal(1000.0, IfcPlacement.LengthScale(Parse(
                "#1=IFCUNITASSIGNMENT((#2));\n#2=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);"), out basis));
            Assert.Contains("METRE", basis);
        }

        [Fact]
        public void A_conversion_based_unit_is_read_through_its_factor_not_assumed_to_be_metres()
        {
            // A file in FEET. Assuming metres here makes the building 3.28 times too
            // big, which is the kind of error that reads as a modelling mistake rather
            // than as an import defect.
            string basis;
            double scale = IfcPlacement.LengthScale(Parse(
                "#1=IFCUNITASSIGNMENT((#2));\n" +
                "#2=IFCCONVERSIONBASEDUNIT(#9,.LENGTHUNIT.,'foot',#3);\n" +
                "#3=IFCMEASUREWITHUNIT(IFCLENGTHMEASURE(0.3048),#4);\n" +
                "#4=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);"), out basis);
            Assert.Equal(304.8, scale, 6);
            Assert.Contains("foot", basis);
        }

        [Fact]
        public void A_file_that_declares_no_length_unit_says_so_rather_than_claiming_to_know()
        {
            string basis;
            Assert.Equal(1000.0, IfcPlacement.LengthScale(Parse("#1=IFCPROJECT('x',$,$);"), out basis));
            Assert.Contains("assumed", basis);
        }

        // =====================================================================
        // Placements
        // =====================================================================

        [Fact]
        public void A_chained_placement_composes_parent_then_child()
        {
            // Storey at (1000,0,0) rotated 90 degrees; a wall 2000 along the storey's
            // own +X. Composed correctly the wall is at (1000,2000,0). Composed the
            // other way round it is at (2000,1000,0) - a plausible-looking building in
            // the wrong place, which is the whole reason this test exists.
            IfcStepReader.Document ifc = Parse(
                "#1=IFCCARTESIANPOINT((1000.,0.,0.));\n" +
                "#2=IFCDIRECTION((0.,0.,1.));\n" +
                "#3=IFCDIRECTION((0.,1.,0.));\n" +          // storey +X points along world +Y
                "#4=IFCAXIS2PLACEMENT3D(#1,#2,#3);\n" +
                "#5=IFCLOCALPLACEMENT($,#4);\n" +
                "#6=IFCCARTESIANPOINT((2000.,0.,0.));\n" +
                "#7=IFCAXIS2PLACEMENT3D(#6,$,$);\n" +
                "#8=IFCLOCALPLACEMENT(#5,#7);");

            string why;
            IfcTransform world = IfcPlacement.World(ifc, ifc.ById[8], 1.0, out why);
            Assert.Null(why);
            Assert.Equal(1000.0, world.Origin[0], 6);
            Assert.Equal(2000.0, world.Origin[1], 6);
            Assert.Equal(90.0, world.PlanRotationDegrees().Value, 6);
        }

        [Fact]
        public void A_RefDirection_that_is_not_perpendicular_to_the_axis_is_projected_not_trusted()
        {
            // IFC permits a RefDirection merely "in the plane". Using it raw builds a
            // skewed basis, and a skewed basis is not a rotation - it is a shear that
            // moves every point by a different amount.
            IfcStepReader.Document ifc = Parse(
                "#1=IFCCARTESIANPOINT((0.,0.,0.));\n" +
                "#2=IFCDIRECTION((0.,0.,1.));\n" +
                "#3=IFCDIRECTION((1.,0.,0.7));\n" +
                "#4=IFCAXIS2PLACEMENT3D(#1,#2,#3);");

            string why;
            IfcTransform t = IfcPlacement.Axis(ifc, ifc.ById[4], 1.0, out why);
            Assert.Null(why);
            Assert.Equal(0.0, Dot(t.X, t.Z), 9);
            Assert.Equal(0.0, Dot(t.X, t.Y), 9);
            Assert.Equal(1.0, Length(t.X), 9);
        }

        [Fact]
        public void A_placement_cycle_is_reported_rather_than_followed()
        {
            // Exporters have produced these. Following one hangs Revit's UI thread,
            // which is worse than any wrong answer.
            IfcStepReader.Document ifc = Parse(
                "#1=IFCCARTESIANPOINT((0.,0.,0.));\n" +
                "#2=IFCAXIS2PLACEMENT3D(#1,$,$);\n" +
                "#3=IFCLOCALPLACEMENT(#4,#2);\n" +
                "#4=IFCLOCALPLACEMENT(#3,#2);");

            string why;
            Assert.Null(IfcPlacement.World(ifc, ifc.ById[3], 1.0, out why));
            Assert.Contains("loops back", why);
        }

        [Fact]
        public void A_grid_placement_is_refused_rather_than_read_as_the_origin()
        {
            // The identity IS the project origin. An element silently placed there
            // looks like a successful import until somebody opens the model.
            IfcStepReader.Document ifc = Parse("#1=IFCGRIDPLACEMENT($,$);");
            string why;
            Assert.Null(IfcPlacement.World(ifc, ifc.ById[1], 1.0, out why));
            Assert.Contains("project origin", why);
        }

        [Fact]
        public void A_tilted_basis_reports_no_plan_rotation()
        {
            // Revit's point placement takes a rotation about Z. A tilted placement
            // cannot be expressed that way, and PlanRotationDegrees saying "none" is
            // what makes the column planner refuse instead of standing it upright.
            var tilted = new IfcTransform
            {
                X = new[] { 1.0, 0, 0 },
                Y = new[] { 0.0, 0.7071067811865476, -0.7071067811865476 },
                Z = new[] { 0.0, 0.7071067811865476, 0.7071067811865476 }
            };
            Assert.Null(tilted.PlanRotationDegrees());
            Assert.NotNull(IfcTransform.Identity.PlanRotationDegrees());
        }

        // =====================================================================
        // Profiles
        // =====================================================================

        [Fact]
        public void A_polyline_profile_drops_its_repeated_closing_point()
        {
            IfcStepReader.Document ifc = Parse(Rectangle());
            IfcProfileShape shape = IfcProfile.Read(ifc, ifc.ById[20], 1.0);
            Assert.Null(shape.Refusal);
            Assert.Equal(4, shape.Outer.Points.Count);
            Assert.Equal(6000000.0, Math.Abs(shape.Outer.SignedArea()), 3);
        }

        [Fact]
        public void An_arc_in_a_boundary_refuses_the_profile_instead_of_chording_it()
        {
            IfcStepReader.Document ifc = Parse(
                "#1=IFCCARTESIANPOINTLIST2D(((0.,0.),(1000.,0.),(1000.,1000.)));\n" +
                "#2=IFCINDEXEDPOLYCURVE(#1,(IFCLINEINDEX((1,2)),IFCARCINDEX((2,3,1))),$);\n" +
                "#3=IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,'curved',#2);");
            IfcProfileShape shape = IfcProfile.Read(ifc, ifc.ById[3], 1.0);
            Assert.NotNull(shape.Refusal);
            Assert.Contains("arc segment", shape.Refusal);
        }

        [Fact]
        public void A_circular_profile_is_refused_rather_than_polygonised()
        {
            IfcStepReader.Document ifc = Parse("#1=IFCCIRCLEPROFILEDEF(.AREA.,'round',$,300.);");
            IfcProfileShape shape = IfcProfile.Read(ifc, ifc.ById[1], 1.0);
            Assert.NotNull(shape.Refusal);
            Assert.Contains("not the same shape", shape.Refusal);
        }

        [Fact]
        public void A_void_that_cannot_be_expressed_refuses_the_whole_profile()
        {
            // THE POINT OF THIS ONE. A slab whose opening was silently dropped passes
            // every check and is wrong exactly where somebody planned a stair. Losing
            // the hole must cost the perimeter too.
            IfcStepReader.Document ifc = Parse(
                Rectangle() +
                "\n#30=IFCCIRCLE(#31,200.);\n" +
                "#31=IFCAXIS2PLACEMENT2D(#32,$);\n" +
                "#32=IFCCARTESIANPOINT((500.,500.));\n" +
                "#33=IFCARBITRARYPROFILEDEFWITHVOIDS(.AREA.,'holed',#21,(#30));");
            IfcProfileShape shape = IfcProfile.Read(ifc, ifc.ById[33], 1.0);
            Assert.NotNull(shape.Refusal);
            Assert.Contains("missing an opening", shape.Refusal);
        }

        [Fact]
        public void A_rectangle_profile_honours_its_own_2d_placement()
        {
            IfcStepReader.Document ifc = Parse(
                "#1=IFCCARTESIANPOINT((100.,50.));\n" +
                "#2=IFCAXIS2PLACEMENT2D(#1,$);\n" +
                "#3=IFCRECTANGLEPROFILEDEF(.AREA.,'r',#2,400.,200.);");
            IfcProfileShape shape = IfcProfile.Read(ifc, ifc.ById[3], 1.0);
            Assert.Null(shape.Refusal);
            Assert.Equal(4, shape.Outer.Points.Count);
            Assert.Equal(-100.0, shape.Outer.Points.Min(p => p[0]), 6);
            Assert.Equal(300.0, shape.Outer.Points.Max(p => p[0]), 6);
            Assert.Equal(-50.0, shape.Outer.Points.Min(p => p[1]), 6);
            Assert.Equal(150.0, shape.Outer.Points.Max(p => p[1]), 6);
        }

        [Fact]
        public void A_degenerate_boundary_is_refused_rather_than_handed_to_Revit()
        {
            // Three collinear points enclose no area. Revit answers a floor sketch like
            // that with an exception nobody can read; this answers it with a sentence.
            IfcStepReader.Document ifc = Parse(
                "#1=IFCCARTESIANPOINT((0.,0.));\n#2=IFCCARTESIANPOINT((1000.,0.));\n" +
                "#3=IFCCARTESIANPOINT((2000.,0.));\n#4=IFCPOLYLINE((#1,#2,#3));\n" +
                "#5=IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,'flat',#4);");
            IfcProfileShape shape = IfcProfile.Read(ifc, ifc.ById[5], 1.0);
            Assert.NotNull(shape.Refusal);
            Assert.Contains("no area", shape.Refusal);
        }

        // =====================================================================
        // Extrusions, in the frame they actually live in
        // =====================================================================

        [Fact]
        public void An_extrusion_is_measured_in_the_world_frame_not_its_own()
        {
            IfcStepReader.Document ifc = Parse(Rectangle() + "\n" + ExtrusionOf(20, 2500));
            IfcExtrusion extrusion = IfcProfile.Extrusion(ifc, ifc.ById[40], 1.0);
            Assert.Null(extrusion.Refusal);
            Assert.Equal(2500.0, extrusion.Depth, 6);

            double[] v = IfcProfile.WorldExtrusion(extrusion, IfcTransform.Identity);
            Assert.Equal(0.0, v[0], 6);
            Assert.Equal(0.0, v[1], 6);
            Assert.Equal(2500.0, v[2], 6);
        }

        [Fact]
        public void A_revolution_or_a_brep_is_named_rather_than_approximated()
        {
            IfcStepReader.Document ifc = Parse("#1=IFCFACETEDBREP(#2);\n#2=IFCCLOSEDSHELL(());");
            IfcExtrusion extrusion = IfcProfile.Extrusion(ifc, ifc.ById[1], 1.0);
            Assert.NotNull(extrusion.Refusal);
            Assert.Contains("IFCFACETEDBREP", extrusion.Refusal);
        }

        // =====================================================================
        // Element shapes
        // =====================================================================

        [Fact]
        public void A_wall_with_a_three_point_axis_is_skipped_with_its_reason()
        {
            // Splitting it invents joins the file does not describe; joining it invents
            // a curve. Neither is the wall somebody drew.
            IfcStepReader.Document ifc = Parse(WallWithAxis("(#51,#52,#53)"));
            IfcShape shape = IfcSubset.Shape(ifc, ifc.ById[60], 1.0);
            Assert.False(shape.HasAxis);
            Assert.Contains("3 points", shape.AxisRefusal);
        }

        [Fact]
        public void A_wall_axis_arrives_in_world_coordinates()
        {
            IfcStepReader.Document ifc = Parse(WallWithAxis("(#51,#52)"));
            IfcShape shape = IfcSubset.Shape(ifc, ifc.ById[60], 1.0);
            Assert.True(shape.HasAxis);
            Assert.Equal(5000.0, shape.AxisStart[0], 6);      // the wall placement's own offset
            Assert.Equal(9000.0, shape.AxisEnd[0], 6);
        }

        [Fact]
        public void A_body_that_is_not_a_swept_solid_names_what_it_is()
        {
            IfcStepReader.Document ifc = Parse(
                "#1=IFCCARTESIANPOINT((0.,0.,0.));\n#2=IFCAXIS2PLACEMENT3D(#1,$,$);\n" +
                "#3=IFCLOCALPLACEMENT($,#2);\n" +
                "#4=IFCSHAPEREPRESENTATION($,'Body','Brep',(#5));\n" +
                "#5=IFCFACETEDBREP(#6);\n#6=IFCCLOSEDSHELL(());\n" +
                "#7=IFCPRODUCTDEFINITIONSHAPE($,$,(#4));\n" +
                "#8=IFCWALL('GUID8',$,'W',$,$,#3,#7,$);");
            IfcShape shape = IfcSubset.Shape(ifc, ifc.ById[8], 1.0);
            Assert.False(shape.HasBody);
            Assert.Contains("Brep", shape.BodyRefusal);
        }

        // =====================================================================
        // Relationships
        // =====================================================================

        [Fact]
        public void An_opening_filled_by_a_door_is_indexed_both_ways()
        {
            IfcStepReader.Document ifc = Parse(
                "#1=IFCWALL('W1',$,'w',$,$,$,$,$);\n" +
                "#2=IFCOPENINGELEMENT('O1',$,'o',$,$,$,$,$);\n" +
                "#3=IFCDOOR('D1',$,'d',$,$,$,$,$);\n" +
                "#4=IFCRELVOIDSELEMENT('R1',$,$,$,#1,#2);\n" +
                "#5=IFCRELFILLSELEMENT('R2',$,$,$,#2,#3);");
            IfcRelations relations = IfcSubset.Relations(ifc);
            Assert.Equal(1, relations.HostOfOpening[2]);
            Assert.Equal(3, relations.FilledBy[2].Id);
            Assert.Equal(2, relations.FillsOpening[3]);
            Assert.Single(relations.VoidsIn[1]);
        }

        [Fact]
        public void A_layer_set_says_it_is_one_rather_than_reporting_one_material()
        {
            // A three-layer IFC construction is not a Revit compound type, and a
            // reply naming only the first layer would read as though it were.
            IfcStepReader.Document ifc = Parse(
                "#1=IFCMATERIAL('Concrete');\n#2=IFCMATERIAL('Insulation');\n" +
                "#3=IFCMATERIALLAYER(#1,200.,$);\n#4=IFCMATERIALLAYER(#2,50.,$);\n" +
                "#5=IFCMATERIALLAYERSET((#3,#4),'Wall build-up');");
            string name = IfcSubset.MaterialName(ifc, ifc.ById[5], 0);
            Assert.StartsWith("Concrete", name);
            Assert.Contains("2 layers", name);
        }

        // =====================================================================
        // Fixtures
        // =====================================================================

        private static IfcStepReader.Document Parse(string data)
        {
            string error;
            IfcStepReader.Document ifc = IfcStepReader.Parse(
                "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" + data + "\nENDSEC;\nEND-ISO-10303-21;",
                out error);
            Assert.Null(error);
            Assert.NotNull(ifc);
            return ifc;
        }

        /// <summary>A 3000 x 2000 rectangle as #21 (polyline) inside #20 (profile).</summary>
        private static string Rectangle() =>
            "#11=IFCCARTESIANPOINT((0.,0.));\n" +
            "#12=IFCCARTESIANPOINT((3000.,0.));\n" +
            "#13=IFCCARTESIANPOINT((3000.,2000.));\n" +
            "#14=IFCCARTESIANPOINT((0.,2000.));\n" +
            "#21=IFCPOLYLINE((#11,#12,#13,#14,#11));\n" +
            "#20=IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,'plate',#21);";

        private static string ExtrusionOf(int profileId, double depth) =>
            "#41=IFCCARTESIANPOINT((0.,0.,0.));\n" +
            "#42=IFCAXIS2PLACEMENT3D(#41,$,$);\n" +
            "#43=IFCDIRECTION((0.,0.,1.));\n" +
            "#40=IFCEXTRUDEDAREASOLID(#" + profileId + ",#42,#43," +
            depth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ");";

        /// <summary>A wall at x=5000 whose local axis runs 4000 along +X.</summary>
        private static string WallWithAxis(string points) =>
            "#50=IFCCARTESIANPOINT((5000.,0.,0.));\n" +
            "#54=IFCAXIS2PLACEMENT3D(#50,$,$);\n" +
            "#55=IFCLOCALPLACEMENT($,#54);\n" +
            "#51=IFCCARTESIANPOINT((0.,0.));\n" +
            "#52=IFCCARTESIANPOINT((4000.,0.));\n" +
            "#53=IFCCARTESIANPOINT((8000.,0.));\n" +
            "#56=IFCPOLYLINE(" + points + ");\n" +
            "#57=IFCSHAPEREPRESENTATION($,'Axis','Curve2D',(#56));\n" +
            "#58=IFCPRODUCTDEFINITIONSHAPE($,$,(#57));\n" +
            "#60=IFCWALLSTANDARDCASE('GUID60',$,'Wall 1',$,$,#55,#58,$);";

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Length(double[] a) => Math.Sqrt(Dot(a, a));
    }
}
