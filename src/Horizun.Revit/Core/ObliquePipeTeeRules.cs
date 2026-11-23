using System;

namespace Horizun.Revit.Core
{
    /// <summary>Geometry for a three-pipe junction.</summary>
    public static class ObliquePipeTeeRules
    {
        public static double AcuteBranchAngleDegrees(double[] run, double[] branch)
        {
            double r = Norm(run), b = Norm(branch);
            if (r < 1e-9 || b < 1e-9) throw new ArgumentException("junction directions must have length");
            double dot = Dot(run, branch) / (r * b);
            return Math.Acos(Math.Min(1, Math.Max(0, Math.Abs(dot)))) * 180.0 / Math.PI;
        }

        /// <summary>A temporary branch perpendicular to the main, on the drawn branch's side.</summary>
        public static double[] PerpendicularTowardsBranch(double[] run, double[] branch)
        {
            double rr = Dot(run, run);
            if (rr < 1e-18) throw new ArgumentException("main direction must have length");
            double scale = Dot(run, branch) / rr;
            var p = new[] { branch[0] - scale * run[0], branch[1] - scale * run[1], branch[2] - scale * run[2] };
            double n = Norm(p);
            if (n < 1e-9) throw new ArgumentException("branch is collinear with the main");
            return new[] { p[0] / n, p[1] / n, p[2] / n };
        }

        /// <summary>
        /// A pipe end connector's BasisZ points out of the pipe and into the junction.
        /// The temporary pipe must extend from that junction back towards the pipe.
        /// </summary>
        public static double[] PerpendicularPipeExtensionFromConnector(double[] runConnector, double[] branchConnector) =>
            PerpendicularTowardsBranch(runConnector,
                new[] { -branchConnector[0], -branchConnector[1], -branchConnector[2] });

        /// <summary>
        /// A wye's branch axis meets its run at a point the family chooses, not at the
        /// junction where Revit placed the temporary tee, so after the branch is turned to
        /// the drawn angle its port can sit beside the drawn branch (162 mm for a 4" manufacturer
        /// yee). Returns the signed distance to slide the fitting along the run so the port
        /// lands on the drawn branch axis, or null when run and branch are parallel.
        /// All vectors share one unit; the run direction need not be normalised.
        /// </summary>
        public static double? SlideAlongRunToMeetBranch(double[] run, double[] port, double[] branchPoint, double[] branchDirection)
        {
            double rn = Norm(run), dn = Norm(branchDirection);
            if (rn < 1e-9 || dn < 1e-9) throw new ArgumentException("run and branch directions must have length");
            var u = new[] { run[0] / rn, run[1] / rn, run[2] / rn };
            var d = new[] { branchDirection[0] / dn, branchDirection[1] / dn, branchDirection[2] / dn };
            // Remove from the port offset the part along the branch; what is left must be
            // cancelled by t*u projected the same way: minimise |w + t*u_perp|.
            var w = new[] { port[0] - branchPoint[0], port[1] - branchPoint[1], port[2] - branchPoint[2] };
            double wd = Dot(w, d), ud = Dot(u, d);
            var wPerp = new[] { w[0] - wd * d[0], w[1] - wd * d[1], w[2] - wd * d[2] };
            var uPerp = new[] { u[0] - ud * d[0], u[1] - ud * d[1], u[2] - ud * d[2] };
            double uu = Dot(uPerp, uPerp);
            if (uu < 1e-12) return null;
            return -Dot(wPerp, uPerp) / uu;
        }

        /// <summary>
        /// A branch port may be accepted only when it points along the drawn branch (out of
        /// the fitting, towards the pipe's far end): its BasisZ is anti-parallel to the pipe
        /// connector's BasisZ. A 90-degree port slid onto a 45-degree branch fails this.
        /// </summary>
        public static bool PortFacesBranch(double[] portDirection, double[] towardsFarEnd, double toleranceDegrees = 3.0)
        {
            double a = Norm(portDirection), b = Norm(towardsFarEnd);
            if (a < 1e-9 || b < 1e-9) return false;
            double cos = Dot(portDirection, towardsFarEnd) / (a * b);
            return Math.Acos(Math.Min(1, Math.Max(-1, cos))) * 180.0 / Math.PI <= toleranceDegrees;
        }

        public sealed class RollAndSlide
        {
            public double RollRadians;       // about the run axis through the fitting origin
            public double SlideFeet;         // along the run, after the roll
            public double ResidualFeet;      // port distance from the drawn branch axis after both
            public double AngleMismatchDegrees; // family branch angle vs drawn branch angle
            public string Problem;           // non-null: no solution
        }

        /// <summary>
        /// Where the family's branch port must go: roll the fitting about the run axis until the
        /// port's direction has the same azimuth as the drawn branch, then slide it along the run
        /// until the port is on the drawn axis. Connector.Angle does not do this (measured: the
        /// port stayed 80 mm off the axis at every angle), so the roll is a rotation of the
        /// fitting. Nothing here uses Revit types.
        /// </summary>
        public static RollAndSlide SolveRollAndSlide(double[] run, double[] fittingOrigin, double[] port, double[] portDir,
            double[] junction, double[] towardsFarEnd, double angleToleranceDegrees = 3.0)
        {
            var res = new RollAndSlide();
            double rn = Norm(run), nn = Norm(portDir), fn = Norm(towardsFarEnd);
            if (rn < 1e-9 || nn < 1e-9 || fn < 1e-9) { res.Problem = "a direction has no length"; return res; }
            double[] u = Scale(run, 1 / rn), n = Scale(portDir, 1 / nn), f = Scale(towardsFarEnd, 1 / fn);
            double nu = Dot(n, u), fu = Dot(f, u);
            double angN = Math.Acos(Math.Min(1, Math.Max(-1, nu))) * 180.0 / Math.PI;
            double angF = Math.Acos(Math.Min(1, Math.Max(-1, fu))) * 180.0 / Math.PI;
            res.AngleMismatchDegrees = Math.Abs(angN - angF);
            if (res.AngleMismatchDegrees > angleToleranceDegrees)
            {
                res.Problem = "the wye's branch leans " + angN.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " degrees from the run direction, the drawn branch " +
                    angF.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " (a wye rolls about the run, it cannot change that angle; the family's lean may point the other way)";
                return res;
            }
            double[] np = Sub(n, Scale(u, nu)), fp = Sub(f, Scale(u, fu));
            if (Norm(np) < 1e-6 || Norm(fp) < 1e-6) { res.Problem = "branch is along the run"; return res; }
            res.RollRadians = Math.Atan2(Dot(u, Cross(np, fp)), Dot(np, fp));
            double[] o = fittingOrigin;
            double[] rotated = Add(o, Rotate(Sub(port, o), u, res.RollRadians));
            double? slide = SlideAlongRunToMeetBranch(u, rotated, junction, f);
            if (slide == null) { res.Problem = "run and branch parallel"; return res; }
            res.SlideFeet = slide.Value;
            double[] p2 = Add(rotated, Scale(u, slide.Value));
            double[] w = Sub(p2, junction);
            double[] perp = Sub(w, Scale(f, Dot(w, f)));
            res.ResidualFeet = Norm(perp);
            return res;
        }

        /// <summary>Rodrigues rotation of v about unit axis u.</summary>
        public static double[] Rotate(double[] v, double[] u, double theta)
        {
            double c = Math.Cos(theta), s = Math.Sin(theta), d = Dot(u, v);
            double[] x = Cross(u, v);
            return new[] { v[0] * c + x[0] * s + u[0] * d * (1 - c), v[1] * c + x[1] * s + u[1] * d * (1 - c), v[2] * c + x[2] * s + u[2] * d * (1 - c) };
        }

        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static double[] Scale(double[] a, double k) => new[] { a[0] * k, a[1] * k, a[2] * k };

        /// <summary>
        /// True when the wye's branch leans the opposite way along the run from the drawn branch
        /// (its angle to the run is 180 minus the drawn one). Rolling cannot fix that; the fitting is
        /// reversed along the run (a half turn about <see cref="FlipAxis"/>) and its run pipes reconnected.
        /// (Swapping the run members given to NewTeeFitting does not reverse it: measured live in Revit 2025.)
        /// </summary>
        public static bool LeanIsOpposite(double[] run, double[] portDir, double[] towardsFarEnd, double toleranceDegrees = 3.0)
        {
            double rn = Norm(run), nn = Norm(portDir), fn = Norm(towardsFarEnd);
            if (rn < 1e-9 || nn < 1e-9 || fn < 1e-9) return false;
            double angN = Math.Acos(Math.Min(1, Math.Max(-1, Dot(portDir, run) / (nn * rn)))) * 180.0 / Math.PI;
            double angF = Math.Acos(Math.Min(1, Math.Max(-1, Dot(towardsFarEnd, run) / (fn * rn)))) * 180.0 / Math.PI;
            return Math.Abs(angN - angF) > toleranceDegrees && Math.Abs(angN - (180.0 - angF)) <= toleranceDegrees;
        }

        /// <summary>
        /// Axis that reverses a wye along its run while keeping its branch in the run-branch plane
        /// and on the same side: the unit component of the branch port's direction perpendicular
        /// to the run (it lies in that plane and is perpendicular to the run). A half turn about it
        /// maps the run direction u to -u and a branch at angle t to the run to 180 - t, with the
        /// same azimuth about the run. Null when the port runs along the run (nothing to flip).
        /// </summary>
        public static double[] FlipAxis(double[] run, double[] portDir)
        {
            double rn = Norm(run);
            if (rn < 1e-9 || Norm(portDir) < 1e-9) return null;
            double[] u = Scale(run, 1 / rn);
            double[] perp = Sub(portDir, Scale(u, Dot(portDir, u)));
            double n = Norm(perp);
            return n < 1e-6 * Norm(portDir) ? null : Scale(perp, 1 / n);
        }

        /// <summary>A direction after the half turn about <paramref name="axis"/> (unit).</summary>
        public static double[] FlipDirection(double[] v, double[] axis) => Rotate(v, axis, Math.PI);

        /// <summary>A point after the half turn about the line through <paramref name="axisPoint"/> along <paramref name="axis"/> (unit).</summary>
        public static double[] FlipPoint(double[] point, double[] axisPoint, double[] axis) =>
            Add(axisPoint, Rotate(Sub(point, axisPoint), axis, Math.PI));

        /// <summary>Foot of the perpendicular from <paramref name="point"/> on the line through <paramref name="linePoint"/> along <paramref name="run"/>.</summary>
        public static double[] ProjectOntoLine(double[] point, double[] linePoint, double[] run)
        {
            double rn = Norm(run);
            if (rn < 1e-9) throw new ArgumentException("run direction must have length");
            double[] u = Scale(run, 1 / rn);
            return Add(linePoint, Scale(u, Dot(Sub(point, linePoint), u)));
        }

        /// <summary>
        /// After the flip the port must lean as expected: the half turn of its old direction
        /// (same azimuth about the run, angle 180 minus the old one).
        /// </summary>
        public static bool FlipLeanMatches(double[] expectedDir, double[] actualDir, double toleranceDegrees = 3.0) =>
            PortFacesBranch(actualDir, expectedDir, toleranceDegrees);

        /// <summary>
        /// A run port is coaxial with the run when its origin is within <paramref name="distanceTolerance"/>
        /// of the run line and its direction is parallel (either way) within <paramref name="toleranceDegrees"/>.
        /// </summary>
        public static bool IsCoaxialWithRun(double[] run, double[] runPoint, double[] portOrigin, double[] portDir,
            double distanceTolerance, double toleranceDegrees = 2.0)
        {
            double rn = Norm(run), dn = Norm(portDir);
            if (rn < 1e-9 || dn < 1e-9) return false;
            double[] foot = ProjectOntoLine(portOrigin, runPoint, run);
            if (Norm(Sub(portOrigin, foot)) > distanceTolerance) return false;
            double cos = Math.Abs(Dot(run, portDir) / (rn * dn));
            return Math.Acos(Math.Min(1, cos)) * 180.0 / Math.PI <= toleranceDegrees;
        }

        /// <summary>
        /// Two ports meet when they point at each other (anti-parallel): a pipe end's BasisZ
        /// points into the junction and the fitting port's out of it.
        /// </summary>
        public static bool PortsOppose(double[] a, double[] b, double toleranceDegrees = 3.0)
        {
            double an = Norm(a), bn = Norm(b);
            if (an < 1e-9 || bn < 1e-9) return false;
            double cos = Dot(a, b) / (an * bn);
            return Math.Acos(Math.Min(1, Math.Max(-1, cos))) * 180.0 / Math.PI >= 180.0 - toleranceDegrees;
        }

        /// <summary>
        /// Reversing a wye puts the port that faced run pipe A towards pipe B and the other way
        /// round. Null when each run pipe's bore equals the bore of the port it will meet, else the
        /// reason (a reducing wye cannot be reversed without changing which pipe is the larger).
        /// Radii in feet.
        /// </summary>
        public static string FlipBoreProblem(double portAFeet, double portBFeet, double pipeAFeet, double pipeBFeet, double toleranceFeet)
        {
            bool aToB = Math.Abs(portBFeet - pipeAFeet) <= toleranceFeet;
            bool bToA = Math.Abs(portAFeet - pipeBFeet) <= toleranceFeet;
            if (aToB && bToA) return null;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "the wye's two run ports have different bores (" + (portAFeet * 2 * 304.8).ToString("0.#", ci) + " and " +
                (portBFeet * 2 * 304.8).ToString("0.#", ci) + " mm) against run pipes of " +
                (pipeAFeet * 2 * 304.8).ToString("0.#", ci) + " and " + (pipeBFeet * 2 * 304.8).ToString("0.#", ci) +
                " mm: reversing it would put the large port on the small pipe, so it is not reversed";
        }

        public static bool NeedsObliqueWorkaround(double acuteAngleDegrees) =>
            acuteAngleDegrees >= 5.0 && acuteAngleDegrees < 85.0;

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Norm(double[] a) => Math.Sqrt(Dot(a, a));
    }
}
