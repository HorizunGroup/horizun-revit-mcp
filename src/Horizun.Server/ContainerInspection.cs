// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// The ISO 19650 inspection core, shared by the two readers of a CDE:
//
//   horizun_information_container operation=inspect  - local or synced folders
//   horizun_cde_cloud operation=inspect               - a cloud CDE read over HTTP
//
// Both observe the same thing - files sitting in the four container states - and
// must judge it the same way: name compliance, the cross-state revision rules, the
// MIDP deliverable cross, and one sorted, paginated list of findings. The walk is
// the only part that differs (a directory tree with sidecars vs. an API with
// versions), so the walk stays with each tool and everything after it lives here,
// once. Two copies of "overdue" would drift the day one of them is fixed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    /// <summary>One observed file in one CDE state. Null means "not known", never "empty".</summary>
    internal sealed class ContainerRecord
    {
        public string State, Path, Relative, Name, Status, Revision, Sha256, Extension;
        public bool HasSidecar;
    }

    internal static class ContainerInspection
    {
        // Kinds are listed in the order findings are sorted, most consequential first.
        internal static readonly string[] FindingOrder =
        {
            "deliverable_overdue", "deliverable_missing", "deliverable_insufficient_state", "hash_mismatch",
            "same_revision_different_content", "sidecar_inconsistent", "state_status_mismatch", "revision_incoherent",
            "orphan_sidecar", "missing_sidecar", "name_noncompliant", "deliverable_not_assessable"
        };

        /// <summary>
        /// Checks a file's stem against the naming rules. A compliant name fills the
        /// record's Name (and Status/Revision when the name carries them); a
        /// non-compliant one adds a name_noncompliant finding and leaves them null.
        /// </summary>
        internal static bool CheckName(ContainerNaming naming, ContainerRecord rec, string stem, List<JObject> findings)
        {
            ContainerSpec parsed;
            ContainerValidation nameCheck = InformationContainer.CheckStem(naming, stem, out parsed);
            if (nameCheck.Valid)
            {
                rec.Name = nameCheck.Name;
                rec.Status = parsed?.Status;
                rec.Revision = parsed?.Revision;
                return true;
            }
            findings.Add(Finding("name_noncompliant", rec.State, rec.Path, new JObject { ["problems"] = nameCheck.Problems }));
            return false;
        }

        internal static JObject Finding(string kind, string state, string path, JObject detail)
        {
            var f = new JObject
            {
                ["kind"] = kind,
                ["severity"] = kind == "name_noncompliant" || kind == "missing_sidecar" || kind == "orphan_sidecar" ||
                               kind == "state_status_mismatch" ? "warning" : "error",
                ["state"] = state,
                ["path"] = path
            };
            if (detail != null) foreach (JProperty p in detail.Properties()) f[p.Name] = p.Value;
            return f;
        }

        /// <summary>
        /// The same container across states. Two rules, both about the sealed record and
        /// never about dates: one revision code carrying two different contents, and a
        /// published copy whose newest revision is BELOW the newest shared one of the same
        /// kind (a P02 published while P03 is the latest shared - somebody published a
        /// superseded copy, or the shared one was never taken forward). Revisions of
        /// different kinds (P vs C) are not ordered, so they never produce the second.
        /// A record without a SHA-256 (a cloud file, whose API gives none) never takes
        /// part in the first rule: no hash is not a different hash.
        /// </summary>
        internal static void CrossStates(List<ContainerRecord> records, List<JObject> findings)
        {
            foreach (IGrouping<string, ContainerRecord> g in records.Where(r => r.Name != null).GroupBy(r => r.Name, StringComparer.Ordinal))
            {
                foreach (IGrouping<string, ContainerRecord> sameRev in g.Where(r => r.Revision != null && r.Sha256 != null)
                                                               .GroupBy(r => r.Revision + "|" + (r.Extension ?? "").ToLowerInvariant(), StringComparer.Ordinal))
                {
                    if (sameRev.Select(r => r.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                        findings.Add(new JObject
                        {
                            ["kind"] = "same_revision_different_content", ["severity"] = "error", ["container"] = g.Key,
                            ["revision"] = sameRev.First().Revision, ["state"] = null,
                            ["copies"] = new JArray(sameRev.Select(r => new JObject { ["state"] = r.State, ["path"] = r.Path, ["sha256"] = r.Sha256 })),
                            ["reason"] = "one revision code seals two different contents"
                        });
                }

                string sharedMax = MaxRevision(g.Where(r => r.State == InformationContainer.StateShared));
                string publishedMax = MaxRevision(g.Where(r => r.State == InformationContainer.StatePublished));
                if (sharedMax != null && publishedMax != null)
                {
                    int? cmp = InformationContainer.CompareRevisions(publishedMax, sharedMax);
                    if (cmp.HasValue && cmp.Value < 0)
                        findings.Add(new JObject
                        {
                            ["kind"] = "revision_incoherent", ["severity"] = "warning", ["container"] = g.Key,
                            ["state"] = InformationContainer.StatePublished,
                            ["published_revision"] = publishedMax, ["shared_revision"] = sharedMax,
                            ["reason"] = "the newest published revision is lower than the newest shared revision of the same kind"
                        });
                }
            }
        }

        private static string MaxRevision(IEnumerable<ContainerRecord> records)
        {
            string best = null;
            foreach (ContainerRecord r in records)
            {
                if (r.Revision == null) continue;
                if (best == null) { best = r.Revision; continue; }
                int? cmp = InformationContainer.CompareRevisions(r.Revision, best);
                if (cmp.HasValue && cmp.Value > 0) best = r.Revision;
            }
            return best;
        }

        /// <summary>
        /// The MIDP cross. A deliverable is SATISFIED when a copy in wip/shared/published
        /// carries a status at least as advanced as the required one (by position in the
        /// declared status list; by CDE state when either code is unknown to that list).
        /// Archived copies do not satisfy: archived is superseded. Not satisfied and due
        /// before as_of is overdue; a pair nobody can order is not_assessable, never a pass.
        /// </summary>
        internal static JArray Deliverables(JArray list, List<ContainerRecord> records, ContainerNaming naming,
                                            DateTime asOf, List<JObject> findings)
        {
            var result = new JArray();
            if (list == null) return result;
            foreach (JToken d in list)
            {
                string container = (string)d?["container"];
                if (string.IsNullOrEmpty(container))
                    throw new ToolRefusal("Every deliverable needs a 'container' name. Nothing was read.");
                string required = (string)d["required_status"];
                string format = (string)d["format"];
                string dueText = (string)d["due"];
                DateTime due = DateTime.MaxValue;
                bool hasDue = dueText != null && DateTime.TryParseExact(dueText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out due);
                if (!hasDue) due = DateTime.MaxValue;

                List<ContainerRecord> copies = records.Where(r => r.Name == container && r.State != InformationContainer.StateArchived &&
                    (format == null || string.Equals((r.Extension ?? "").TrimStart('.'), format, StringComparison.OrdinalIgnoreCase))).ToList();
                ContainerRecord best = null; string verdict;
                bool? satisfied = null;
                if (copies.Count == 0) { verdict = "missing"; satisfied = false; }
                else if (required == null) { verdict = "present"; satisfied = true; best = copies[0]; }
                else
                {
                    foreach (ContainerRecord c in copies)
                    {
                        bool? ok = Reaches(naming, c.Status, c.State, required);
                        if (ok == true) { satisfied = true; best = c; break; }
                        if (ok == false && satisfied == null) satisfied = false;
                        best = best ?? c;
                    }
                    verdict = satisfied == true ? "satisfied" : satisfied == false ? "insufficient_state" : "not_assessable";
                }
                bool overdue = satisfied != true && hasDue && due < asOf;
                if (overdue) verdict = "overdue";

                var row = new JObject
                {
                    ["container"] = container, ["title"] = d["title"], ["task_team"] = d["task_team"], ["due"] = dueText,
                    ["required_status"] = required, ["format"] = format, ["verdict"] = verdict,
                    ["copies"] = new JArray(copies.Select(c => new JObject { ["state"] = c.State, ["path"] = c.Path, ["status"] = c.Status, ["revision"] = c.Revision }))
                };
                result.Add(row);
                string kind = verdict == "overdue" ? "deliverable_overdue" : verdict == "missing" ? "deliverable_missing" :
                              verdict == "insufficient_state" ? "deliverable_insufficient_state" :
                              verdict == "not_assessable" ? "deliverable_not_assessable" : null;
                if (kind != null)
                    findings.Add(new JObject
                    {
                        ["kind"] = kind, ["severity"] = kind == "deliverable_not_assessable" ? "warning" : "error",
                        ["container"] = container, ["state"] = best?.State, ["due"] = dueText, ["required_status"] = required,
                        ["reached_status"] = best?.Status,
                        ["reason"] = kind == "deliverable_overdue"
                            ? "due " + dueText + " and " + (copies.Count == 0 ? "no copy exists" : "no copy has reached " + required)
                            : kind == "deliverable_missing" ? "no copy exists in wip, shared or published"
                            : kind == "deliverable_insufficient_state" ? "no copy has reached " + required
                            : "the reached and required statuses cannot be ordered (custom codes without a CDE state)"
                    });
            }
            return result;
        }

        private static bool? Reaches(ContainerNaming naming, string status, string folderState, string required)
        {
            if (status == null) return null;
            int have = InformationContainer.StatusRank(naming, status), need = InformationContainer.StatusRank(naming, required);
            if (have >= 0 && need >= 0) return have >= need;
            string hs = InformationContainer.StateOfStatus(status) ?? folderState;
            string ns = InformationContainer.StateOfStatus(required);
            if (hs == null || ns == null) return null;
            return InformationContainer.StateRank(hs) >= InformationContainer.StateRank(ns);
        }

        /// <summary>
        /// Sorts every finding by consequence, counts them by kind and writes one page of
        /// them (findings, finding_counts, total_findings, offset, limit, truncated,
        /// next_offset) into <paramref name="into"/>.
        /// </summary>
        internal static void Page(List<JObject> findings, int offset, int limit, JObject into)
        {
            List<JObject> ordered = findings
                .OrderBy(f => Array.IndexOf(FindingOrder, (string)f["kind"]))
                .ThenBy(f => (string)f["state"] ?? "", StringComparer.Ordinal)
                .ThenBy(f => (string)f["path"] ?? (string)f["container"] ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            var counts = new JObject();
            foreach (string kind in FindingOrder)
            {
                int c = ordered.Count(f => (string)f["kind"] == kind);
                if (c > 0) counts[kind] = c;
            }
            List<JObject> page = ordered.Skip(offset).Take(limit).ToList();
            bool more = offset + page.Count < ordered.Count;
            into["finding_counts"] = counts;
            into["total_findings"] = ordered.Count;
            into["offset"] = offset;
            into["limit"] = limit;
            into["findings"] = new JArray(page);
            into["truncated"] = more;
            into["next_offset"] = more ? (JToken)(offset + page.Count) : JValue.CreateNull();
        }
    }
}
