// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Clash findings as durable state. A clash row is a measurement; a FINDING is
// that measurement with an identity, a state and a history, so two runs a week
// apart talk about the same physical problem. The rules here are arithmetic
// over strings and dictionaries - provable without a model:
//
//   * IDENTITY IS ORDER-NORMALIZED. The same two elements clash whichever
//     category set found them first.
//   * resolved_by_model IS MEASURED, NEVER ASSERTED. A person can close a
//     finding as a decision; only a COMPLETE detection run can say the model
//     itself no longer clashes - and a partial run resolves nothing, because
//     "not seen" and "not there" are different facts.
//   * A RESOLVED FINDING THAT COMES BACK IS A REGRESSION, and it says so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Horizun.Revit.Core
{
    public sealed class CoordinationEvent
    {
        public string AtUtc;
        public string Kind;      // opened | status | assignee | comment | resolved_by_model | regression
        public string Text;
    }

    public sealed class CoordinationFinding
    {
        public string Id;
        /// <summary>The detection scope that owns resolution for this finding.</summary>
        public string Scope;
        public string Status = CoordinationRules.StatusOpen;
        public string Assignee;
        public string Note;
        public string SideA, SideB;          // normalized "source|instance|uid", SideA <= SideB ordinally
        public string CategoryA, CategoryB;
        public double[] PointMm;
        public string FirstSeenUtc, LastSeenUtc, ResolvedUtc, UpdatedUtc;
        public int TimesSeen;
        public bool Regression;
        /// <summary>Append-only. A finding's story is evidence; an overwritten note is not.</summary>
        public List<CoordinationEvent> History = new List<CoordinationEvent>();
    }

    public sealed class CoordinationDetected
    {
        public string SideA, SideB, CategoryA, CategoryB;
        public double[] PointMm;
    }

    public sealed class CoordinationMergeOutcome
    {
        public int New, Persisting, Regressions, ResolvedByModel;
        public bool ResolutionSkippedBecausePartial;
    }

    public static class CoordinationRules
    {
        public const int MaxHistoryEvents = 200;

        /// <summary>Append one event; the oldest fall away past the cap, and the cap is a fact of the record.</summary>
        public static void AppendEvent(CoordinationFinding finding, string kind, string text, string nowUtc)
        {
            if (finding == null) return;
            finding.History.Add(new CoordinationEvent { AtUtc = nowUtc, Kind = kind, Text = text });
            while (finding.History.Count > MaxHistoryEvents) finding.History.RemoveAt(0);
        }

        public const string StatusOpen = "open";
        public const string StatusAssigned = "assigned";
        public const string StatusAcceptedRisk = "accepted_risk";
        public const string StatusClosedByDecision = "closed_by_decision";
        public const string StatusResolvedByModel = "resolved_by_model";

        public static readonly string[] HumanStatuses =
            { StatusOpen, StatusAssigned, StatusAcceptedRisk, StatusClosedByDecision };

        /// <summary>One clash side, canonically: model, placement, element.</summary>
        public static string SideKey(string source, string instanceId, string uniqueId) =>
            (source ?? "") + "|" + (instanceId ?? "") + "|" + (uniqueId ?? "");

        /// <summary>
        /// The finding's identity: the pair, order-normalized, hashed. Control-char
        /// separator so no element name can forge another pair's identity.
        /// </summary>
        public static string FindingId(string sideA, string sideB)
        {
            string first = string.CompareOrdinal(sideA, sideB) <= 0 ? sideA : sideB;
            string second = ReferenceEquals(first, sideA) ? sideB : sideA;
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(first + "\x1f" + second));
                var hex = new StringBuilder(32);
                for (int i = 0; i < 16; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        /// <summary>Normalize a pair so SideA <= SideB ordinally, whichever way detection met them.</summary>
        public static void NormalizePair(ref string sideA, ref string sideB, ref string categoryA, ref string categoryB)
        {
            if (string.CompareOrdinal(sideA, sideB) <= 0) return;
            (sideA, sideB) = (sideB, sideA);
            (categoryA, categoryB) = (categoryB, categoryA);
        }

        /// <summary>
        /// Whether a PERSON may move a finding from one status to another, and why not.
        /// resolved_by_model is detection's verdict: asserting it by hand would launder
        /// an opinion into a measurement.
        /// </summary>
        public static bool CanTransition(string from, string to, out string reason)
        {
            reason = null;
            if (Array.IndexOf(HumanStatuses, to) < 0)
            {
                reason = to == StatusResolvedByModel
                    ? "resolved_by_model is MEASURED by a complete detection run, never asserted by a person. " +
                      "Close it as closed_by_decision if the decision is yours, or re-run horizun_clash with " +
                      "record_findings and let the model answer."
                    : "'" + to + "' is not a status. Statuses: " + string.Join(", ", HumanStatuses) +
                      " (resolved_by_model is set only by detection).";
                return false;
            }
            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                reason = "the finding is already " + from + "; nothing to change.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The detection run's scope, canonically: what was compared against what,
        /// under which tolerance, links included or not. Resolution belongs to the
        /// scope: a complete pipes-versus-walls run says NOTHING about a finding a
        /// ducts-versus-floors run opened.
        /// </summary>
        public static string ScopeKey(IEnumerable<string> categoriesA, IEnumerable<string> categoriesB,
                                      double toleranceMm, bool includeLinks)
        {
            var a = new List<string>(categoriesA ?? new List<string>()); a.Sort(StringComparer.Ordinal);
            var b = new List<string>(categoriesB ?? new List<string>()); b.Sort(StringComparer.Ordinal);
            string canon = string.Join(",", a) + "\x1f" + string.Join(",", b) + "\x1f" +
                           toleranceMm.ToString("0.###", CultureInfo.InvariantCulture) + "\x1f" +
                           (includeLinks ? "links" : "host-only");
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canon));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        /// <summary>
        /// Fold one detection run into the ledger. Mutates `ledger`; the outcome
        /// carries the counts a caller reports. `runComplete` must be the detection's
        /// own coverage verdict - a partial run adds and refreshes but resolves
        /// NOTHING, because an element that never entered the check is not evidence
        /// its clash is gone. Resolution applies ONLY to findings owned by
        /// `scopeKey`: this run measured this scope, and no other.
        /// </summary>
        public static CoordinationMergeOutcome Merge(IDictionary<string, CoordinationFinding> ledger,
                                                    IEnumerable<CoordinationDetected> detected,
                                                    string nowUtc, bool runComplete, string scopeKey)
        {
            var outcome = new CoordinationMergeOutcome();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CoordinationDetected hit in detected ?? new List<CoordinationDetected>())
            {
                string a = hit.SideA, b = hit.SideB, ca = hit.CategoryA, cb = hit.CategoryB;
                NormalizePair(ref a, ref b, ref ca, ref cb);
                string id = FindingId(a, b);
                if (!seen.Add(id)) continue;
                CoordinationFinding finding;
                if (!ledger.TryGetValue(id, out finding))
                {
                    var opened = new CoordinationFinding
                    {
                        Id = id, Scope = scopeKey, SideA = a, SideB = b, CategoryA = ca, CategoryB = cb,
                        PointMm = hit.PointMm, Status = StatusOpen,
                        FirstSeenUtc = nowUtc, LastSeenUtc = nowUtc, TimesSeen = 1
                    };
                    AppendEvent(opened, "opened", "detected as a clash", nowUtc);
                    ledger[id] = opened;
                    outcome.New++;
                    continue;
                }
                finding.LastSeenUtc = nowUtc;
                finding.TimesSeen++;
                if (hit.PointMm != null) finding.PointMm = hit.PointMm;
                if (finding.Status == StatusResolvedByModel)
                {
                    // It was measured gone and it is BACK. That is a regression, and
                    // pretending it is merely open again would hide the round trip.
                    finding.Status = StatusOpen;
                    finding.Regression = true;
                    finding.ResolvedUtc = null;
                    AppendEvent(finding, "regression", "a finding measured resolved is CLASHING AGAIN", nowUtc);
                    outcome.Regressions++;
                }
                else outcome.Persisting++;
            }

            if (!runComplete)
            {
                outcome.ResolutionSkippedBecausePartial = true;
                return outcome;
            }
            foreach (CoordinationFinding finding in ledger.Values)
            {
                if (seen.Contains(finding.Id)) continue;
                if (!string.Equals(finding.Scope, scopeKey, StringComparison.Ordinal)) continue;
                if (finding.Status == StatusResolvedByModel) continue;
                finding.Status = StatusResolvedByModel;
                finding.ResolvedUtc = nowUtc;
                AppendEvent(finding, "resolved_by_model", "a complete detection run of this finding's scope no longer sees the pair", nowUtc);
                outcome.ResolvedByModel++;
            }
            return outcome;
        }

        // ---- BCF 2.1 -------------------------------------------------------------
        // Structurally honest BCF: the XML is built deterministically here (no
        // dependencies), the produced zip is re-read and re-parsed by the caller,
        // and the claim is EXACTLY that - "structurally valid BCF 2.1"; no
        // consumer's round-trip is proven, and the reply says so.

        /// <summary>The finding's stable topic GUID: its 16-byte id, as a Guid.</summary>
        public static string BcfTopicGuid(string findingId)
        {
            if (string.IsNullOrEmpty(findingId) || findingId.Length != 32) return System.Guid.Empty.ToString();
            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
                bytes[i] = System.Convert.ToByte(findingId.Substring(i * 2, 2), 16);
            return new System.Guid(bytes).ToString();
        }

        public static string BcfVersionXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<Version VersionId=\"2.1\"><DetailedVersion>2.1</DetailedVersion></Version>\n";

        /// <summary>One topic's markup.bcf. Status maps to the BCF vocabulary; the
        /// finding's history becomes BCF comments in order.</summary>
        public static string BcfMarkupXml(CoordinationFinding f, string documentTitle)
        {
            string guid = BcfTopicGuid(f.Id);
            string topicStatus =
                f.Status == StatusResolvedByModel || f.Status == StatusClosedByDecision ? "Closed" :
                f.Status == StatusAcceptedRisk ? "Closed" : "Open";
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Markup>\n");
            sb.Append("  <Topic Guid=\"").Append(guid).Append("\" TopicType=\"Clash\" TopicStatus=\"")
              .Append(topicStatus).Append("\">\n");
            sb.Append("    <Title>").Append(Xml(f.CategoryA + " vs " + f.CategoryB + " [" + f.Id.Substring(0, 8) + "]"))
              .Append("</Title>\n");
            if (!string.IsNullOrEmpty(f.FirstSeenUtc))
                sb.Append("    <CreationDate>").Append(Xml(f.FirstSeenUtc)).Append("</CreationDate>\n");
            if (!string.IsNullOrEmpty(f.Assignee))
                sb.Append("    <AssignedTo>").Append(Xml(f.Assignee)).Append("</AssignedTo>\n");
            sb.Append("    <Description>").Append(Xml(
                "Horizun coordination finding " + f.Id + " in '" + documentTitle + "'. " +
                (f.PointMm == null ? "" : "Point (mm): " + string.Join(", ", System.Array.ConvertAll(f.PointMm,
                    v => v.ToString("0.0", CultureInfo.InvariantCulture))) + ". ") +
                "Status: " + f.Status + (f.Regression ? " (REGRESSION)" : "") + "."))
              .Append("</Description>\n");
            sb.Append("  </Topic>\n");
            int commentIndex = 0;
            foreach (CoordinationEvent entry in f.History ?? new List<CoordinationEvent>())
            {
                commentIndex++;
                // Deterministic per-comment guid: topic guid bytes xor the index.
                sb.Append("  <Comment Guid=\"").Append(CommentGuid(f.Id, commentIndex)).Append("\">\n");
                if (!string.IsNullOrEmpty(entry.AtUtc))
                    sb.Append("    <Date>").Append(Xml(entry.AtUtc)).Append("</Date>\n");
                sb.Append("    <Author>Horizun</Author>\n");
                sb.Append("    <Comment>").Append(Xml("[" + entry.Kind + "] " + (entry.Text ?? "")))
                  .Append("</Comment>\n");
                sb.Append("  </Comment>\n");
            }
            sb.Append("</Markup>\n");
            return sb.ToString();
        }

        private static string CommentGuid(string findingId, int index)
        {
            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
                bytes[i] = System.Convert.ToByte(findingId.Substring(i * 2, 2), 16);
            bytes[15] ^= (byte)(index & 0xFF); bytes[14] ^= (byte)((index >> 8) & 0xFF);
            return new System.Guid(bytes).ToString();
        }

        public static string Xml(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        // ---- export ----------------------------------------------------------------

        public static readonly string[] CsvHeader =
        {
            "finding_id", "status", "assignee", "note", "category_a", "category_b",
            "side_a", "side_b", "point_mm", "first_seen_utc", "last_seen_utc",
            "resolved_utc", "times_seen", "regression"
        };

        public static string CsvRow(CoordinationFinding f)
        {
            string point = f.PointMm == null ? "" : string.Join(" ", Array.ConvertAll(f.PointMm,
                v => v.ToString("0.0", CultureInfo.InvariantCulture)));
            string[] cells =
            {
                f.Id, f.Status, f.Assignee ?? "", f.Note ?? "", f.CategoryA ?? "", f.CategoryB ?? "",
                f.SideA ?? "", f.SideB ?? "", point, f.FirstSeenUtc ?? "", f.LastSeenUtc ?? "",
                f.ResolvedUtc ?? "", f.TimesSeen.ToString(CultureInfo.InvariantCulture),
                f.Regression ? "true" : "false"
            };
            var sb = new StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(CsvEscape(cells[i]));
            }
            return sb.ToString();
        }

        public static string CsvEscape(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return "";
            bool needsQuotes = cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuotes) return cell;
            return "\"" + cell.Replace("\"", "\"\"") + "\"";
        }
    }
}
