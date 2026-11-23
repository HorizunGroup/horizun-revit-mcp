// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Field frictions (2026-10-22/23, planos and MEP agents) fixed after Pro 3.5.0. The
// pure rules are proved directly; the Revit-side half reads the SOURCE, as
// Pro340WiringTests does, to catch a rule that is correct and no longer asked.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RehearsalFrictionFixesTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { RepoRoot(), "src", "Horizun.Revit" }.Concat(parts).ToArray()));

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(2 * Math.PI, 0.0)]
        [InlineData(-Math.PI / 2, 3 * Math.PI / 2)]
        [InlineData(5 * Math.PI, Math.PI)]
        public void Normalize_lands_in_one_turn(double input, double expected)
        {
            Assert.Equal(expected, AngleRules.Normalize(input), 12);
        }

        [Fact]
        public void A_full_turn_and_zero_are_the_same_rotation()
        {
            // 360 degrees asked, normalised to 0; Revit stored a hair under 2*pi.
            double asked = AngleRules.Normalize(2 * Math.PI);
            double stored = 2 * Math.PI - 1e-10;
            Assert.True(Math.Abs(AngleRules.NearestTurn(asked, stored) - asked) < 1e-8);
            Assert.True(Math.Abs(AngleRules.NearestTurn(2 * Math.PI - 1e-10, 0.0) - (2 * Math.PI - 1e-10)) < 1e-8);
        }

        [Fact]
        public void A_real_rotation_error_is_still_measured()
        {
            double asked = Math.PI / 2;
            Assert.Equal(Math.PI, AngleRules.NearestTurn(asked, Math.PI), 12);
            Assert.True(Math.Abs(AngleRules.NearestTurn(asked, -Math.PI / 2) - asked) > 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("QA-HVAC")]
        [InlineData("a b")]
        public void Text_that_survives_Set_passes(string value)
        {
            Assert.Null(TextValueRules.EdgeWhitespaceError("Comments", value));
        }

        [Theory]
        [InlineData("QA-HVAC ")]
        [InlineData(" QA-HVAC")]
        [InlineData("QA\t")]
        [InlineData(" ")]
        public void Text_Revit_would_trim_is_refused_with_the_fix(string value)
        {
            string error = TextValueRules.EdgeWhitespaceError("Comments", value);
            Assert.NotNull(error);
            Assert.Contains("'Comments'", error);
            Assert.Contains("trimmed", error);
        }

        [Fact]
        public void Create_elements_compares_rotation_by_angular_gap()
        {
            Assert.Contains("AngleRules.NearestTurn(", Src("Commands", "CreateElementsGeometry.cs"));
        }

        [Fact]
        public void Manage_views_rehearsal_checks_names_and_the_default_template_scale()
        {
            string views = Src("Commands", "ManageViewsCommand.cs");
            Assert.Contains("PlanimetryFixRules.NameError(\"name\"", views);
            Assert.Contains("DefaultTemplateId", views);
            foreach (string family in new[] { "FloorPlan", "CeilingPlan", "StructuralPlan" })
                Assert.Contains("RefuseScaleUnderDefaultTemplate(doc, a, ViewFamily." + family + ")", views);
            Assert.Contains("ControlsViewScale(template)", views);
        }

        [Fact]
        public void The_two_most_retried_Revit_failures_carry_a_way_through()
        {
            Assert.Contains("Move the whole connected run",
                RevitFailureHints.For("did not commit. Revit said: The family is connected in a network and can no longer keep the connectivity. Disconnect?"));
            Assert.Contains("intermediate segment",
                RevitFailureHints.For("Fitting cannot be created between the input connectors because the angle between them is too small or too large."));
            Assert.Equal("", RevitFailureHints.For("Line is too short"));
            Assert.Equal("", RevitFailureHints.For(null));
        }

        [Fact]
        public void The_server_heartbeat_is_cancelled_before_it_is_disposed()
        {
            string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Server", "Program.cs"));
            Assert.Contains("return new HeartbeatStop(stop);", program);
            Assert.Contains("source.Cancel();", program);
            Assert.DoesNotContain("            return stop;", program);
        }

        [Fact]
        public void The_vertical_range_spans_every_level_with_its_margin()
        {
            Assert.True(GridExtentRules.VerticalRange(new[] { 0.0, 8.038, 40.19 }, out double bottom, out double top));
            Assert.Equal(-GridExtentRules.VerticalMarginFeet, bottom, 9);
            Assert.Equal(40.19 + GridExtentRules.VerticalMarginFeet, top, 9);
            Assert.True(GridExtentRules.CoversLevels(bottom, top, new[] { 0.0, 8.038, 40.19 }, 1e-3, out _));
            Assert.False(GridExtentRules.VerticalRange(new double[0], out _, out _));
        }

        [Fact]
        public void A_sheet_name_with_a_colon_is_refused_by_the_shared_name_rule()
        {
            Assert.NotNull(PlanimetryFixRules.NameError("name", "PLANTA 1:100"));
            Assert.Null(PlanimetryFixRules.NameError("name", "PLANTA 1-100"));
        }
    }
}
