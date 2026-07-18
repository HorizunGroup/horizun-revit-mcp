// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// The Revit-FREE arithmetic behind the "cannot lie" contract, split out of
// HorizunGuard so it can be unit-tested WITHOUT loading Autodesk assemblies.
//
// HorizunGuard.cs imports Autodesk.Revit.DB for Commit/Assimilate, which pins it
// to the Revit runtime and keeps it out of the test project. But the honesty of
// horizun_quantities is pure arithmetic — "do these two measured volumes agree,
// and by how much" — and that is exactly the logic that must never quietly pick
// one number. It has no business being untestable. So it lives here, HorizunGuard
// delegates to it, and HorizunReconcileTests pins it.
//
// No `using Autodesk.*` in this file, ever. That is the whole point.
// -----------------------------------------------------------------------------
using System;

namespace RvtMcp.Plugin
{
    public static class HorizunReconcile
    {
        // Metric at the boundary. Horizun bills in m3/m2, not cubic feet.
        public const double FT3_TO_M3 = 0.0283168466;
        public const double FT2_TO_M2 = 0.09290304;
        public const double FT_TO_M = 0.3048;

        public static double ToM3(double cubicFeet) => cubicFeet * FT3_TO_M3;
        public static double ToM2(double squareFeet) => squareFeet * FT2_TO_M2;
        public static double ToM(double feet) => feet * FT_TO_M;

        /// <summary>
        /// The result of comparing two measurements of one quantity. Deliberately a
        /// value with named fields, not an anonymous shape — the caller builds the
        /// response JSON, this only decides agree/how-far.
        /// </summary>
        public struct Comparison
        {
            public bool Agree;
            public double Difference;      // absolute, rounded to 4
            public double DifferencePct;   // relative to the larger magnitude, 0..100, rounded to 1
        }

        /// <summary>
        /// Do two measured values of the same quantity agree within tolerance, and by
        /// how much do they differ. `tolerance` is relative (0.01 = 1%). The percentage
        /// is taken against the LARGER magnitude so a value of zero on one side does not
        /// divide by zero and does not flatter the agreement.
        /// </summary>
        public static Comparison Compare(double a, double b, double tolerance = 0.01)
        {
            var biggest = Math.Max(Math.Abs(a), Math.Abs(b));
            var delta = Math.Abs(a - b);
            var rel = biggest > 1e-9 ? delta / biggest : 0.0;
            return new Comparison
            {
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
