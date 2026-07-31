// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The Revit-FREE arithmetic behind the "cannot lie" contract, kept out of Guard
// so it can be unit-tested WITHOUT loading Autodesk assemblies.
//
// Guard.cs imports Autodesk.Revit.DB for Commit/Assimilate, which pins it to the
// Revit runtime. But the honesty of a quantities takeoff is pure arithmetic —
// "do these two measured volumes agree, and by how much" — and that is exactly
// the logic that must never quietly pick one number, so it has no business being
// untestable. It lives here; Guard delegates to it.
//
// No `using Autodesk.*` in this file, ever. That is the whole point.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class Reconcile
    {
        // Metric at the boundary. Horizun bills in m3/m2, not cubic feet.
        public const double Ft3ToM3 = 0.0283168466;
        public const double Ft2ToM2 = 0.09290304;
        public const double FtToM = 0.3048;

        public static double ToM3(double cubicFeet) => cubicFeet * Ft3ToM3;
        public static double ToM2(double squareFeet) => squareFeet * Ft2ToM2;
        public static double ToM(double feet) => feet * FtToM;

        /// <summary>
        /// The result of comparing two measurements of one quantity. A value with
        /// named fields, not an anonymous shape — the caller builds the response
        /// JSON, this only decides agree/how-far.
        /// </summary>
        public struct Comparison
        {
            public bool Agree;
            public double Difference;      // absolute, rounded to 4
            public double DifferencePct;   // relative to the larger magnitude, 0..100, rounded to 1

            /// <summary>
            /// False when at least one input was not a finite number, so no comparison was
            /// possible at all. Difference/DifferencePct are then meaningless and the caller
            /// must report them as unknown — NOT as 0, and never as agreement.
            /// </summary>
            public bool Comparable;
        }

        /// <summary>A real, comparable measurement: neither NaN nor an infinity.</summary>
        public static bool IsMeasurement(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>
        /// Do two measured values of one quantity agree within tolerance, and by how
        /// much. `tolerance` is relative (0.01 = 1%). The percentage is taken against
        /// the LARGER magnitude so a zero on one side neither divides by zero nor
        /// flatters the agreement.
        ///
        /// A non-finite input is not a small measurement, it is the ABSENCE of one, and
        /// it is refused up front: Math.Max(NaN, x) is NaN, NaN passes no comparison, and
        /// the zero-guard below would have swallowed it into rel = 0 and reported
        /// Agree = true — a fabricated agreement between a real number and nothing. That
        /// is exactly the lie this file exists to prevent.
        /// </summary>
        public static Comparison Compare(double a, double b, double tolerance = 0.01)
        {
            if (!IsMeasurement(a) || !IsMeasurement(b))
                return new Comparison { Comparable = false, Agree = false, Difference = 0.0, DifferencePct = 0.0 };

            double biggest = Math.Max(Math.Abs(a), Math.Abs(b));
            double delta = Math.Abs(a - b);
            double rel = biggest > 1e-9 ? delta / biggest : 0.0;
            return new Comparison
            {
                Comparable = true,
                Agree = rel <= tolerance,
                Difference = Math.Round(delta, 4),
                DifferencePct = Math.Round(rel * 100, 1)
            };
        }

        /// <summary>
        /// Did a write land: does the count the model reports match what we intended.
        /// The whole anti-"758 purged" idea in one comparison — intent is never evidence.
        /// </summary>
        public static bool Verified(int intended, int actual) => intended == actual;
    }
}
