// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THREE THINGS horizun_detail_2d SAID TOO LITTLE ABOUT (field cycle, 2026-10-21).
//
//  1. A call that was only a REHEARSAL (dry_run defaults to true, or an invalid action
//     stopped it) answered with a plan and no word about whether anything was drawn; a
//     client that read "valid: 40" believed it had applied. Every dry answer now says
//     applied=false and what would be needed to apply.
//
//  2. "1 of 1 failed the post-commit re-read" did not say which property differed. The
//     row held the comparison, but the headline did not. FailedChecks pulls every
//     check that did not match out of the rows and Headline puts the first ones in the
//     message.
//
//  3. Very short lines (a few millimetres) and collinear lines that overlap are the
//     ones whose ends Revit joins or merges, so the re-read finds a different line from
//     the one sent. LineHazards names them BEFORE anything is drawn and, when a re-read
//     fails, says which action was at risk and why. It does not loosen the check: a line
//     that moved is still a failure, now with its cause named.
//
// Revit-free: it reads the JSON rows and the requested segments, nothing else.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class DetailSegment
    {
        public int Index;
        /// <summary>View-plane endpoints in feet (x, y [, z]).</summary>
        public double[] A, B;
    }

    public static class Detail2DOutcomeRules
    {
        public const double MmPerFoot = 304.8;
        /// <summary>Below this a detail line's ends are at risk of being joined or merged.</summary>
        public const double ShortLineMm = 3.0;

        /// <summary>A dry answer: nothing was drawn, and the result says so in a field a client cannot misread.</summary>
        public static void StampNotApplied(JObject result, int invalid, bool constructible)
        {
            bool canApply = invalid == 0 && constructible;
            result["applied"] = false;
            result["write_started"] = false;
            result["status"] = "rehearsal_only";
            result["can_apply"] = canApply;
            result["applied_note"] = canApply
                ? "Nothing was drawn. This is a rehearsal: send the SAME actions with dry_run=false, the confirmation_token and a new idempotency_key to apply."
                : "Nothing was drawn and nothing can be applied yet: " +
                  (invalid > 0 ? invalid + " action(s) are invalid (see errors)." : "the rehearsal could not construct every element.");
        }

        /// <summary>Every check that did not match, across all rows: [{index, operation, field, requested, read}].</summary>
        public static JArray FailedChecks(JArray rows)
        {
            var failed = new JArray();
            if (rows == null) return failed;
            foreach (JObject row in rows.OfType<JObject>())
            {
                if (!(row.SelectToken("verification.checks") is JArray checks)) continue;
                foreach (JObject check in checks.OfType<JObject>())
                {
                    if ((bool?)check["match"] == true) continue;
                    failed.Add(new JObject
                    {
                        ["index"] = row["index"],
                        ["operation"] = row["operation"],
                        ["field"] = check["field"],
                        ["requested"] = check["requested"],
                        ["read"] = check["read"]
                    });
                }
            }
            return failed;
        }

        private static string Short(JToken t)
        {
            string s = t == null ? "null" : t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > 120 ? s.Substring(0, 117) + "..." : s;
        }

        /// <summary>The first `max` differences as one sentence for the error message.</summary>
        public static string Headline(JArray failed, int max = 3)
        {
            if (failed == null || failed.Count == 0) return "no differing check was recorded (see each row)";
            var parts = failed.OfType<JObject>().Take(max).Select(f =>
                "action " + Short(f["index"]) + " (" + Short(f["operation"]) + ") " + Short(f["field"]) +
                ": asked " + Short(f["requested"]) + ", read " + Short(f["read"]));
            string text = string.Join("; ", parts);
            if (failed.Count > max) text += "; and " + (failed.Count - max) + " more in failed_checks";
            return text;
        }

        private static double Len(double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Lines at risk of an end being joined or merged: shorter than ShortLineMm, or collinear with
        /// and overlapping another line of the batch. Keyed by action index; the text says why.
        /// </summary>
        public static IDictionary<int, string> LineHazards(IList<DetailSegment> segments, double toleranceFeet)
        {
            var hazards = new Dictionary<int, List<string>>();
            void Add(int index, string why)
            {
                if (!hazards.TryGetValue(index, out List<string> list)) hazards[index] = list = new List<string>();
                if (!list.Contains(why)) list.Add(why);
            }
            if (segments == null) return new Dictionary<int, string>();
            double shortFeet = ShortLineMm / MmPerFoot;
            foreach (DetailSegment s in segments)
                if (Len(s.A, s.B) < shortFeet)
                    Add(s.Index, "shorter than " + ShortLineMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm (" +
                                 (Len(s.A, s.B) * MmPerFoot).ToString("0.###", CultureInfo.InvariantCulture) + " mm): Revit may join its ends to a neighbour");
            for (int i = 0; i < segments.Count; i++)
                for (int j = i + 1; j < segments.Count; j++)
                {
                    double overlap = CollinearOverlap(segments[i], segments[j], toleranceFeet);
                    if (overlap > toleranceFeet)
                    {
                        string mm = (overlap * MmPerFoot).ToString("0.###", CultureInfo.InvariantCulture);
                        Add(segments[i].Index, "collinear with action " + segments[j].Index + " over " + mm + " mm: Revit may merge or trim the overlap");
                        Add(segments[j].Index, "collinear with action " + segments[i].Index + " over " + mm + " mm: Revit may merge or trim the overlap");
                    }
                }
            return hazards.ToDictionary(kv => kv.Key, kv => string.Join("; ", kv.Value));
        }

        /// <summary>Length of the stretch two segments share while lying on one line (0 when parallel-but-apart, crossing, or apart).</summary>
        public static double CollinearOverlap(DetailSegment p, DetailSegment q, double tol)
        {
            double lp = Len(p.A, p.B), lq = Len(q.A, q.B);
            if (lp < 1e-12 || lq < 1e-12) return 0;
            double ux = (p.B[0] - p.A[0]) / lp, uy = (p.B[1] - p.A[1]) / lp;
            // Both ends of q must lie on p's line.
            double dA = Math.Abs((q.A[0] - p.A[0]) * uy - (q.A[1] - p.A[1]) * ux);
            double dB = Math.Abs((q.B[0] - p.A[0]) * uy - (q.B[1] - p.A[1]) * ux);
            if (dA > tol || dB > tol) return 0;
            double tA = (q.A[0] - p.A[0]) * ux + (q.A[1] - p.A[1]) * uy;
            double tB = (q.B[0] - p.A[0]) * ux + (q.B[1] - p.A[1]) * uy;
            double lo = Math.Max(0, Math.Min(tA, tB)), hi = Math.Min(lp, Math.Max(tA, tB));
            return Math.Max(0, hi - lo);
        }

        /// <summary>The largest join tolerance a caller may grant (mm).</summary>
        public const double MaxJoinToleranceMm = 5.0;

        /// <summary>join_tolerance_mm in feet, or an error: it must be in (0, MaxJoinToleranceMm].</summary>
        public static string ParseJoinTolerance(double mm, out double feet)
        {
            feet = 0;
            if (double.IsNaN(mm) || double.IsInfinity(mm) || mm <= 0 || mm > MaxJoinToleranceMm)
                return "join_tolerance_mm must be in (0, " + MaxJoinToleranceMm.ToString("0.#", CultureInfo.InvariantCulture) + "] mm.";
            feet = mm / MmPerFoot;
            return null;
        }

        /// <summary>
        /// How far the ends of the line that was READ are from the ends that were ASKED, matched in the better
        /// orientation (Revit may reverse a line). The larger of the two end distances, in feet.
        /// </summary>
        public static double EndpointDisplacement(double[] askedA, double[] askedB, double[] readA, double[] readB)
        {
            double straight = Math.Max(Len(askedA, readA), Len(askedB, readB));
            double reversed = Math.Max(Len(askedA, readB), Len(askedB, readA));
            return Math.Min(straight, reversed);
        }

        /// <summary>
        /// Does a read line pass? Strictly within `strictFeet`; or, ONLY when the action is a join hazard and
        /// the caller granted a join tolerance, within that. `joined` says the second route was the one taken.
        /// </summary>
        public static bool EndpointsAcceptable(double displacementFeet, double strictFeet, double joinToleranceFeet,
                                               bool isJoinHazard, out bool joined)
        {
            joined = false;
            if (displacementFeet <= strictFeet) return true;
            if (isJoinHazard && joinToleranceFeet > 0 && displacementFeet <= joinToleranceFeet)
            {
                joined = true;
                return true;
            }
            return false;
        }

        /// <summary>The failed checks of the actions that were at risk, with the reason, for the failure detail.</summary>
        public static JArray AtRisk(JArray failed, IDictionary<int, string> hazards)
        {
            var risk = new JArray();
            if (failed == null || hazards == null) return risk;
            foreach (int index in failed.OfType<JObject>().Select(f => (int?)f["index"]).Where(i => i.HasValue).Select(i => i.Value).Distinct())
                if (hazards.TryGetValue(index, out string why))
                    risk.Add(new JObject { ["index"] = index, ["likely_cause"] = why });
            return risk;
        }

        /// <summary>
        /// Drops vertices that sit within `toleranceFeet` of the previous kept vertex (and of the
        /// first, closing the loop), so a boundary of tiny edges becomes one Revit accepts.
        /// Returns the kept vertices (view feet); `removed` counts what was dropped. Fewer than
        /// 3 left is returned as is: the caller refuses (a triangle needs three).
        /// </summary>
        public static List<double[]> SimplifyLoop(IList<double[]> vertices, double toleranceFeet, out int removed)
        {
            removed = 0;
            var kept = new List<double[]>();
            if (vertices == null) return kept;
            foreach (double[] v in vertices)
            {
                if (kept.Count > 0 && Len(kept[kept.Count - 1], v) <= toleranceFeet) { removed++; continue; }
                kept.Add(v);
            }
            while (kept.Count > 1 && Len(kept[kept.Count - 1], kept[0]) <= toleranceFeet) { kept.RemoveAt(kept.Count - 1); removed++; }
            return kept;
        }
    }
}
