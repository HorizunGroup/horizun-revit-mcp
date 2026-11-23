using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class MepOffsetProfileRules
    {
        internal static string LegClearanceError(int leg, double lengthFeet, double startTrimFeet,
            double endTrimFeet, double minimumStraightFeet)
        {
            double minimum = Math.Max(0, startTrimFeet) + Math.Max(0, endTrimFeet) + minimumStraightFeet;
            if (lengthFeet > minimum) return null;
            string Mm(double feet) => (feet * 304.8).ToString("0.###", CultureInfo.InvariantCulture);
            return "profile " + new[] { "rise", "run", "fall" }[leg] + " is " + Mm(lengthFeet) +
                " mm; the measured elbows require a leg longer than " + Mm(minimum) + " mm.";
        }

        internal static bool TrimFitsLeg(double trim, double offAxis, double length, double tolerance)
            => offAxis <= tolerance && trim >= -tolerance && trim < length;

        // A rectangular axis has no sign. Choose the smallest twist that aligns it.
        internal static double RectangularRoll(double cosine, double sine)
        {
            double angle = Math.Atan2(sine, cosine);
            if (angle > Math.PI / 2) return angle - Math.PI;
            if (angle < -Math.PI / 2) return angle + Math.PI;
            return angle;
        }

        internal static bool RectangularSectionsMate(double runWidth, double runHeight,
            double fittingWidth, double fittingHeight, double widthAxisDot, double crossAxisDot, double sizeTolerance)
            => (Math.Abs(widthAxisDot) >= 0.999999 &&
                Math.Abs(runWidth - fittingWidth) <= sizeTolerance && Math.Abs(runHeight - fittingHeight) <= sizeTolerance)
            || (Math.Abs(crossAxisDot) >= 0.999999 &&
                Math.Abs(runWidth - fittingHeight) <= sizeTolerance && Math.Abs(runHeight - fittingWidth) <= sizeTolerance);

        internal static bool InlineFittingCoversEnd(double trim, double offAxis, double length,
            double oppositeStation, double oppositeOffAxis, double portGap, bool connected, double tolerance)
            => connected && portGap <= tolerance && TrimFitsLeg(trim, offAxis, length, tolerance) &&
                oppositeStation <= tolerance && oppositeOffAxis <= tolerance;

        public static void ApplyBatches(int count, int batchSize, Action<int> build, Action regenerate, Action<int> join)
        {
            for (int start = 0; start < count; start += batchSize)
            {
                int end = Math.Min(count, start + batchSize);
                for (int i = start; i < end; i++) build(i);
                regenerate();
                for (int i = start; i < end; i++) join(i);
            }
        }

        public static JObject SectionMeasurement(double expectedFeet, double? measuredFeet, out bool verified)
        {
            verified = measuredFeet.HasValue &&
                Math.Abs(measuredFeet.Value - expectedFeet) <= MepRoutingRules.SizeToleranceFeet;
            return new JObject
            {
                ["expected_mm"] = expectedFeet * 304.8,
                ["measured_mm"] = measuredFeet * 304.8,
                ["verified"] = verified
            };
        }

        // Coordinates and tolerances all use the caller's length unit.
        public static double[][] Plan(double[] a, double[] b, double fixedZ,
            double rise, double runX, double runY, double fall, double tolerance,
            double minimumLength, out string error, bool measureDuctClearance = false)
        {
            error = null;
            double run = Math.Sqrt(runX * runX + runY * runY);
            double legMinimum = measureDuctClearance ? 0 : minimumLength;
            if (new[] { fixedZ, rise, run, fall }.Concat(a).Concat(b).Any(x => double.IsNaN(x) || double.IsInfinity(x))
                || rise <= legMinimum || run <= legMinimum || fall <= legMinimum)
            {
                error = measureDuctClearance ? "rise, horizontal run and fall must be finite and positive"
                    : "rise, horizontal run and fall must be finite and longer than Revit's minimum segment length";
                return null;
            }

            if (Math.Abs(a[2] - b[2]) <= minimumLength || Math.Abs(a[0] - b[0]) > tolerance || Math.Abs(a[1] - b[1]) > tolerance)
            {
                error = "the source must be a straight vertical run";
                return null;
            }

            double[] anchor = a[2] < b[2] ? a : b;
            if (Math.Abs(anchor[2] - fixedZ) > tolerance)
            {
                error = "the lower endpoint must lie in the fixed point's elevation plane";
                return null;
            }

            double crest = anchor[2] + rise;
            double x = anchor[0] + runX, y = anchor[1] + runY, endZ = crest - fall;
            if (new[] { crest, x, y, endZ }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
            {
                error = "the profile produces coordinates outside the numeric range";
                return null;
            }

            return new[] { (double[])anchor.Clone(), new[] { anchor[0], anchor[1], crest },
                new[] { x, y, crest }, new[] { x, y, endZ } };
        }
    }

    public static class MepDiameterRules
    {
        internal static string DeclaredConduitUnionError(string subtype, bool hasDeclaredConduit,
            double? firstFeet, double? secondFeet)
            => subtype == "union" && hasDeclaredConduit && firstFeet.HasValue && secondFeet.HasValue
                ? Mismatch(firstFeet.Value, secondFeet.Value, false) : null;

        public static void InheritConnected<T>(IEnumerable<T> conduits, Func<T, double?> connectedSize,
            Func<T, double> defaultSize, Action<T, double, bool> setSize)
        {
            var pending = conduits.ToList();
            while (pending.Count > 0)
            {
                var inherited = pending.Select(row => (Row: row, Size: connectedSize(row)))
                    .Where(row => row.Size.HasValue).ToList();
                if (inherited.Count == 0)
                {
                    // Seed one unresolved component, then give its neighbors a chance to inherit.
                    T seed = pending[0];
                    setSize(seed, defaultSize(seed), false);
                    pending.RemoveAt(0);
                    continue;
                }
                foreach (var row in inherited)
                {
                    setSize(row.Row, row.Size.Value, true);
                    pending.Remove(row.Row);
                }
            }
        }

        public static string FittingFor(string requested, double firstFeet, double secondFeet)
            => requested == "union" && Mismatch(firstFeet, secondFeet, false) != null ? "transition" : requested;

        public static T ConstructReducer<T>(double firstFeet, double secondFeet, Func<T> construct,
            Func<T, bool> verifies) where T : class
        {
            string mismatch = Mismatch(firstFeet, secondFeet, false);
            T fitting;
            try { fitting = construct(); }
            catch (InvalidOperationException ex) when (mismatch != null)
            {
                throw new InvalidOperationException(mismatch + ". " + ex.Message, ex);
            }
            if (fitting == null || !verifies(fitting))
                throw new InvalidOperationException(mismatch + ". Revit did not produce a fitting joining both measured sizes.");
            return fitting;
        }

        public static double? Resolve(double? specified, double? connected, double? type, out string source)
        {
            source = specified.HasValue ? null : connected.HasValue ? "connected run" : type.HasValue ? "conduit type" : "Revit's default for the conduit type";
            return specified ?? connected ?? type;
        }

        public static string Mismatch(double firstFeet, double secondFeet, bool admitsReducer)
        {
            if (admitsReducer || Math.Abs(firstFeet - secondFeet) <= MepRoutingRules.SizeToleranceFeet)
                return null;
            string Mm(double feet) => (feet * 304.8).ToString("0.###", CultureInfo.InvariantCulture);
            return "diámetros distintos: " + Mm(firstFeet) + " mm y " + Mm(secondFeet) + " mm";
        }
    }
}
