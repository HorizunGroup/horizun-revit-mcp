// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A SNAPSHOT IS A MEASUREMENT WITH A DATE ON IT, and a comparison between two of
// them is the only way to answer the question every project manager actually
// asks: is this better than last week.
//
// Two of the eight products in the benchmark have a trend store, and one of those
// does it through a cloud telemetry database on a three-hour sync. Nothing here
// leaves the machine. There is no endpoint, no upload and no identifier that
// outlives the model it describes.
//
// WHAT A COMPARISON MUST REFUSE TO DO is the interesting part. Two snapshots of
// DIFFERENT models are not a trend; two snapshots taken under different
// requirement sets are not a trend either, because the second run was asked a
// different question. `not_comparable` is a real answer and it is the one that
// keeps the other five honest.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>One check's numbers at one moment.</summary>
    public sealed class SnapshotCheck
    {
        public string Check;
        public bool IsIssue;
        public double? Count;
        public bool CountIsLowerBound;
        public bool CoverageComplete;
        public Dictionary<string, double?> Parts;
    }

    /// <summary>
    /// Everything a run recorded. The fingerprint is what makes two snapshots
    /// comparable or not; everything else is what changed.
    /// </summary>
    public sealed class DiagnosticsSnapshot
    {
        public const string SchemaId = "horizun.diagnostics-snapshot/1";

        public string Schema = SchemaId;
        /// <summary>Identifies the MODEL, not the run. Two snapshots of different models never compare.</summary>
        public string ModelFingerprint;
        public string DocumentTitle;
        public string RevitVersion;
        public string RevitBuild;
        public string HorizunCommit;
        public string ScanVersion;
        /// <summary>SHA-256 of the requirement set this run was judged against, or null when none was given.</summary>
        public string RequirementSetSha256;
        public string TakenUtc;
        public double? FileSizeMb;
        public long? ElementCount;
        public long? ElementTypeCount;
        public long? WarningCount;
        public long DurationMs;
        public bool CoverageComplete;
        public string GateVerdict;
        public List<SnapshotCheck> Checks = new List<SnapshotCheck>();
        public Dictionary<string, double?> DimensionScores;
    }

    public static class SnapshotChangeKind
    {
        public const string New = "new";
        public const string Resolved = "resolved";
        public const string Persistent = "persistent";
        public const string Worsened = "worsened";
        public const string Improved = "improved";
        public const string NotComparable = "not_comparable";

        /// <summary>
        /// The number moved in the improving direction, but one of the runs read
        /// less of the model than the other. Distinct from not_comparable on
        /// purpose: not_comparable says the two runs cannot be lined up at all,
        /// while this says they CAN and the apparent gain is unproven. They lead a
        /// reader to different next steps - fix the coverage, then compare again.
        /// </summary>
        public const string CoverageChanged = "coverage_changed";

        /// <summary>
        /// The two runs were judged against different rules, so their verdicts
        /// answer different questions. Named apart from not_comparable because the
        /// fix is to re-run with the earlier profile, not to investigate the model.
        /// </summary>
        public const string ProfileChanged = "profile_changed";
    }

    public sealed class SnapshotChange
    {
        public string Check;
        public string Kind;
        public double? Before;
        public double? After;
        public double? Delta;
        public string Why;
    }

    public sealed class SnapshotComparison
    {
        public bool Comparable;
        public string WhyNot;
        public string FromUtc, ToUtc;
        public List<SnapshotChange> Changes = new List<SnapshotChange>();
        public int New, Resolved, Persistent, Worsened, Improved, NotComparable;
        /// <summary>Rows where the direction improved but the coverage did not hold.</summary>
        public int CoverageChanged;
        /// <summary>Set when the whole comparison was refused, and why in one word.</summary>
        public string RefusalKind;
    }

    public static class SnapshotRules
    {
        /// <summary>
        /// Can these two be compared at all? Everything below depends on this, and
        /// getting it wrong produces a trend line between two different buildings.
        /// </summary>
        public static bool AreComparable(DiagnosticsSnapshot a, DiagnosticsSnapshot b, out string whyNot)
        {
            whyNot = null;
            if (a == null || b == null) { whyNot = "one of the two snapshots is missing."; return false; }

            if (string.IsNullOrEmpty(a.ModelFingerprint) || string.IsNullOrEmpty(b.ModelFingerprint))
            {
                whyNot = "a snapshot without a model fingerprint cannot be shown to be about the same model as " +
                         "another one, and a trend between two different models is worse than no trend.";
                return false;
            }
            if (!string.Equals(a.ModelFingerprint, b.ModelFingerprint, StringComparison.Ordinal))
            {
                whyNot = "these are snapshots of DIFFERENT models ('" + a.DocumentTitle + "' and '" +
                         b.DocumentTitle + "'). Nothing about one says anything about the other.";
                return false;
            }

            // A DIFFERENT REQUIREMENT SET IS A DIFFERENT QUESTION. The counts might
            // look comparable and the verdicts are not: a model that "improved" only
            // because the second run was judged more leniently is the exact lie a
            // trend report exists to prevent.
            if (!string.Equals(a.RequirementSetSha256 ?? "", b.RequirementSetSha256 ?? "", StringComparison.Ordinal))
            {
                whyNot = "the two runs were judged against DIFFERENT requirement sets, so their verdicts answer " +
                         "different questions. The counts may still be read individually; the trend may not.";
                return false;
            }

            // A different Revit or a different build of this bridge can change what a
            // check can see. It is not a refusal - the numbers are still the model's -
            // but it is worth saying, so it rides along in the reason of every row.
            return true;
        }

        /// <summary>
        /// What changed. A check present in one and absent from the other is
        /// `not_comparable` for that row rather than new or resolved: a check that
        /// did not run has not found nothing.
        /// </summary>
        public static SnapshotComparison Compare(DiagnosticsSnapshot before, DiagnosticsSnapshot after)
        {
            var c = new SnapshotComparison
            {
                FromUtc = before == null ? null : before.TakenUtc,
                ToUtc = after == null ? null : after.TakenUtc
            };
            string whyNot;
            c.Comparable = AreComparable(before, after, out whyNot);
            c.WhyNot = whyNot;
            if (!c.Comparable)
            {
                // A refusal caused by DIFFERENT RULES is named apart from every
                // other refusal: the fix is to re-run with the earlier profile,
                // not to go looking at the model.
                c.RefusalKind = whyNot != null && whyNot.Contains("requirement sets")
                    ? SnapshotChangeKind.ProfileChanged
                    : SnapshotChangeKind.NotComparable;
                return c;
            }

            var byName = new Dictionary<string, SnapshotCheck>(StringComparer.Ordinal);
            foreach (SnapshotCheck s in before.Checks) if (s != null && s.Check != null) byName[s.Check] = s;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (SnapshotCheck now in after.Checks)
            {
                if (now == null || now.Check == null) continue;
                seen.Add(now.Check);
                SnapshotCheck then;
                if (!byName.TryGetValue(now.Check, out then))
                {
                    c.Changes.Add(new SnapshotChange
                    {
                        Check = now.Check, Kind = SnapshotChangeKind.NotComparable, After = now.Count,
                        Why = "this check did not run in the earlier snapshot, so there is nothing to compare " +
                              "it with. A check that did not run has not found nothing."
                    });
                    continue;
                }
                c.Changes.Add(Judge(now.Check, then, now));
            }

            foreach (KeyValuePair<string, SnapshotCheck> kv in byName)
            {
                if (seen.Contains(kv.Key)) continue;
                c.Changes.Add(new SnapshotChange
                {
                    Check = kv.Key, Kind = SnapshotChangeKind.NotComparable, Before = kv.Value.Count,
                    Why = "this check ran earlier and not in the later snapshot. Its absence is not a resolution."
                });
            }

            foreach (SnapshotChange ch in c.Changes)
            {
                switch (ch.Kind)
                {
                    case SnapshotChangeKind.New: c.New++; break;
                    case SnapshotChangeKind.Resolved: c.Resolved++; break;
                    case SnapshotChangeKind.Persistent: c.Persistent++; break;
                    case SnapshotChangeKind.Worsened: c.Worsened++; break;
                    case SnapshotChangeKind.Improved: c.Improved++; break;
                    case SnapshotChangeKind.CoverageChanged: c.CoverageChanged++; break;
                    default: c.NotComparable++; break;
                }
            }
            return c;
        }

        private static SnapshotChange Judge(string check, SnapshotCheck then, SnapshotCheck now)
        {
            var ch = new SnapshotChange { Check = check, Before = then.Count, After = now.Count };

            if (!then.Count.HasValue || !now.Count.HasValue)
            {
                ch.Kind = SnapshotChangeKind.NotComparable;
                ch.Why = "one of the two runs produced no count for this check.";
                return ch;
            }

            // A LOWER BOUND CANNOT PROVE AN IMPROVEMENT. If either side could not read
            // everything, a smaller number may simply be a smaller sample - and
            // reporting that as progress is the most flattering possible error.
            bool bounded = then.CountIsLowerBound || now.CountIsLowerBound ||
                           !then.CoverageComplete || !now.CoverageComplete;

            ch.Delta = now.Count.Value - then.Count.Value;

            if (then.Count.Value == 0 && now.Count.Value > 0)
            {
                ch.Kind = SnapshotChangeKind.New;
                ch.Why = "clean before, " + Fmt(now.Count.Value) + " now.";
                return ch;
            }
            if (then.Count.Value > 0 && now.Count.Value == 0)
            {
                if (bounded)
                {
                    ch.Kind = SnapshotChangeKind.CoverageChanged;
                    ch.Why = "it reads as resolved, but one of the runs could not read everything it examined, " +
                             "so zero may be a smaller sample rather than a fixed model.";
                    return ch;
                }
                ch.Kind = SnapshotChangeKind.Resolved;
                ch.Why = Fmt(then.Count.Value) + " before, none now, with complete coverage both times.";
                return ch;
            }
            if (ch.Delta.Value == 0)
            {
                ch.Kind = SnapshotChangeKind.Persistent;
                ch.Why = "unchanged at " + Fmt(now.Count.Value) + ".";
                return ch;
            }
            if (ch.Delta.Value > 0)
            {
                ch.Kind = SnapshotChangeKind.Worsened;
                ch.Why = "up " + Fmt(ch.Delta.Value) + ", from " + Fmt(then.Count.Value) + " to " +
                         Fmt(now.Count.Value) + ".";
                return ch;
            }
            if (bounded)
            {
                ch.Kind = SnapshotChangeKind.CoverageChanged;
                ch.Why = "the number fell from " + Fmt(then.Count.Value) + " to " + Fmt(now.Count.Value) +
                         ", but one of the runs could not read everything, so this may be a smaller sample " +
                         "rather than an improvement.";
                return ch;
            }
            ch.Kind = SnapshotChangeKind.Improved;
            ch.Why = "down " + Fmt(-ch.Delta.Value) + ", from " + Fmt(then.Count.Value) + " to " +
                     Fmt(now.Count.Value) + ", with complete coverage both times.";
            return ch;
        }

        internal static string Fmt(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        public const string Means =
            "a snapshot is a measurement with a date on it, stored on THIS MACHINE and nowhere else. Two " +
            "snapshots of different models never compare, and neither do two runs judged against different " +
            "requirement sets - the second was asked a different question, and a model that 'improved' because " +
            "the standard got easier is the lie a trend report exists to prevent.";
    }
}
