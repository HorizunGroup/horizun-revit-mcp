// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHICH WAY A DRAWN HOOK TURNS (acero 3.0, etapa 1b). A hook's orientation is declared as Revit's word, left or
// right, and Revit reports a word back. MEASURED on Revit 2024 (anonymized regression case roof): a straight bar with a 180 degree
// hook at both ends, declared 'left' with normal +Y, matched the shape M_01, drew both hooks on the left - and
// read back 'right' at both ends. Declared 'right' it drew them on the right and read 'right'. The word Revit
// keeps is not a fact about where the steel is; the drawn centreline is.
//
// The side is Revit's own convention, measured on bars whose word and drawing agree (a bent bar of the same mat):
// LEFT is the side normal x tangent, where the tangent is the bar's direction at that end (its first segment at
// the start, its last at the end) and the normal is the bar's plane normal, the set's distribution direction.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class HookSide
    {
        public const string Left = "left";
        public const string Right = "right";

        /// <summary>A hook whose tail is nearer than this to where it is attached was not drawn.</summary>
        public const double NoHookMm = 1.0;

        /// <summary>
        /// The share of the hook's offset that must lie across the bar, in its plane, for the side to be read. A tail
        /// displaced mostly ALONG the normal is not in the bar's plane, and no side of the bar is honest about it.
        /// </summary>
        public const double MinimumSideShare = 0.25;

        /// <summary>
        /// Which way the hook at <paramref name="end"/> (0 start, 1 end) turns: "left", "right", or null with the
        /// reason in <paramref name="why"/>. <paramref name="drawn"/> is the centreline Revit drew WITH its hooks,
        /// <paramref name="declared"/> the same bar without them (GetCenterlineCurves suppressing hooks and bend
        /// radii, which is the declared centreline), both in model millimetres.
        /// </summary>
        public static string Measure(IList<double[]> drawn, IList<double[]> declared, double[] normal, int end, out string why)
        {
            why = null;
            if (drawn == null || drawn.Count < 2 || declared == null || declared.Count < 2)
            {
                why = "the drawn or the declared centreline could not be read.";
                return null;
            }
            double[] n = Unit(normal);
            if (n == null) { why = "the bar's normal could not be read."; return null; }

            double[] attach, tangent, tail;
            if (end == 0)
            {
                attach = declared[0];
                tangent = FirstDirection(declared, forward: true);
                tail = drawn[0];
            }
            else
            {
                attach = declared[declared.Count - 1];
                tangent = FirstDirection(declared, forward: false);
                tail = drawn[drawn.Count - 1];
            }
            if (tangent == null) { why = "the declared centreline has no length at this end."; return null; }

            double[] v = Sub(tail, attach);
            double len = Length(v);
            if (len < NoHookMm)
            {
                why = "no hook is drawn at this end: the drawn bar ends where the declared one does.";
                return null;
            }
            double[] side = Cross(n, tangent);
            double across = Dot(v, side);
            if (Math.Abs(across) < MinimumSideShare * len)
            {
                why = "the drawn hook's tail is not displaced across the bar in the bar's plane (" +
                      Math.Round(across, 2) + " mm of " + Math.Round(len, 2) + " mm), so no side of the bar can be read.";
                return null;
            }
            return across > 0 ? Left : Right;
        }

        // The direction the bar runs at an end: from the end's vertex to the next distinct one (start), or from the
        // last distinct one to the end's vertex (end).
        private static double[] FirstDirection(IList<double[]> p, bool forward)
        {
            int count = p.Count;
            if (forward)
            {
                for (int i = 1; i < count; i++)
                {
                    double[] d = Unit(Sub(p[i], p[0]));
                    if (d != null && Length(Sub(p[i], p[0])) > 1e-6) return d;
                }
            }
            else
            {
                for (int i = count - 2; i >= 0; i--)
                {
                    double[] d = Unit(Sub(p[count - 1], p[i]));
                    if (d != null && Length(Sub(p[count - 1], p[i])) > 1e-6) return d;
                }
            }
            return null;
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Length(double[] a) => Math.Sqrt(Dot(a, a));
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double[] Unit(double[] a)
        {
            if (a == null || a.Length != 3) return null;
            double l = Length(a);
            if (double.IsNaN(l) || double.IsInfinity(l) || l < 1e-9) return null;
            return new[] { a[0] / l, a[1] / l, a[2] / l };
        }
    }
}
