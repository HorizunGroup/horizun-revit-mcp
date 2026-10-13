// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// MEASURED, Revit 2024 (anonymized regression case roof, etapa 1b): the top bar along the 6 % slope, a 180 degree hook at its
// high end, its out-to-out end declared 25 mm from the slab's end face. The hook is square to the sloped bar,
// so its bend leans out over the face: drawn, its outermost point comes 2.04 mm inside the 25 mm cover.
// horizun_audit_reinforcement reported cover_violated (surface 22.961 mm from the face) and the apply had
// reported the same set inside_host_solid: verified. Same function, different centreline: the audit measured
// the curves Revit drew TESSELLATED; the apply measured only each curve's two END POINTS, so a 180 degree
// bend became its chord and the bend's outermost point was never measured. The audit is right.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HookCoverApplyAuditTests
    {
        // The drawn centreline at the high end, as GetCenterlineCurves returns it tessellated (read back live, bar
        // anonymized): the hook's tail, its 180 degree bend, and the start of the main bar - cut at local y = -500 on the
        // straight, which lies on the line between the two drawn points (127.727, 126.333) and (-7377.406, -332.701).
        private static List<double[]> Tessellated()
        {
            return new List<double[]>
            {
                new[] { 0.000, 98.307, 57.909 }, new[] { 0.000, 131.786, 59.957 }, new[] { 0.000, 148.109, 65.418 },
                new[] { 0.000, 159.513, 78.309 }, new[] { 0.000, 162.945, 95.175 }, new[] { 0.000, 157.483, 111.497 },
                new[] { 0.000, 144.593, 122.902 }, new[] { 0.000, 127.727, 126.333 },
                new[] { 0.0, -500.0, 126.333 - (127.727 + 500.0) * (126.333 + 332.701) / (127.727 + 7377.406) }
            };
        }

        // The same bar as the apply read it: each curve's two end points (tail line, bend arc, main line).
        private static List<double[]> CurveEndPointsOnly()
        {
            List<double[]> t = Tessellated();
            return new List<double[]> { t[0], t[1], t[1], t[7], t[7], t[8] };
        }

        // The slab around that end, its end face where the live audit placed it (22.961 + 4.75 + 162.945).
        private static HostMesh Slab() => HostMesh.Box(new[] { -79.735, -1000.000, -50.000 }, new[] { 8020.265, 190.656, 200.000 });

        private static SetContainment Measure(List<double[]> pts) =>
            RebarContainment.Check(Slab(), pts, false, new List<double> { 0.0 }, new double[] { 1, 0, 0 }, 4.75,
                                   25.0, 2.0, RebarContainment.DefaultSampleStepMm);

        [Fact]
        public void The_drawn_hook_measured_tessellated_is_short_of_its_cover_as_the_audit_said()
        {
            SetContainment c = Measure(Tessellated());
            Assert.Equal(SolidContainment.InsideCoverViolated, c.Word);
        }

        [Fact]
        public void Measured_on_curve_end_points_only_the_bend_is_its_chord_and_the_shortfall_vanishes()
        {
            // This is how the apply passed the set: not a different rule, a different centreline.
            Assert.Equal(SolidContainment.Inside, Measure(CurveEndPointsOnly()).Word);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        [Fact]
        public void The_apply_measures_the_drawn_bar_on_the_same_centreline_as_the_audit()
        {
            string apply = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "ApplyReinforcementCommand.cs"));
            string audit = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "AuditReinforcementCommand.cs"));
            Assert.Contains("RebarFacts.CentrelinePointsMm(bar, false) ?? e.PointsMm", audit);
            // The apply reads the bar it verifies with the very call the audit uses - no end-points-only reader left.
            Assert.Matches(new Regex(@"List<double\[\]> barPoints = RebarFacts\.CentrelinePointsMm\(bar, false\);"), apply);
            Assert.DoesNotContain("ActualCentrelinePoints", apply);
        }
    }
}
