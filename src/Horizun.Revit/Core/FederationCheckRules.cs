// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// FEDERATION CHECK - the Revit-free half of horizun_federation_check.
//
// Which disciplines a model may carry, which links a federation expects, and
// whether each link agrees with the host about where the site is - all DECLARED
// by the caller. The bridge only compares. A model the rules do not classify is
// reported as unclassified, never judged against a guess from its file name; a
// model two rules claim is ambiguous and judged by neither.
//
// SAME SITE is measured, not read from a flag: three points of the link are
// taken to shared coordinates twice - through the link's own project location,
// and through the instance transform plus the host's - and the largest distance
// between the two answers is the disagreement. An unloaded link has no document
// to ask, so it is not_decidable rather than coherent.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class FederationModelFact
    {
        public string Title;
        public bool IsHost;
        /// <summary>Category token (OST_*) -> element count, plus the display names for matching.</summary>
        public Dictionary<string, int> CategoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CategoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<long>> SampleIds = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class FederationLinkFact
    {
        public long InstanceId;
        public string Title;
        public bool Loaded;
        public string Workset;
        public string Phase;
        /// <summary>Largest disagreement in mm between host and link shared coordinates; null = not measured.</summary>
        public double? SiteDeltaMm;
        public string SiteWhyNot;
        public string LinkSiteName;
    }

    public static class FederationCheckRules
    {
        private static readonly HashSet<string> RuleKeys = new HashSet<string>(StringComparer.Ordinal) { "models", "expected_links", "same_site" };

        /// <summary>Null when the rules are usable, else the refusal.</summary>
        public static string Validate(JObject rules)
        {
            if (rules == null) return "rules is required.";
            foreach (JProperty p in rules.Properties())
                if (!RuleKeys.Contains(p.Name)) return "rules: unknown key '" + p.Name + "'. Known: models, expected_links, same_site.";
            foreach (JObject m in (rules["models"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string match = m.Value<string>("match");
                if (string.IsNullOrWhiteSpace(match)) return "every models entry needs match (a regex on the model title, or '$host').";
                if (match != "$host" && !IsRegex(match)) return "models match '" + match + "' is not a valid regex.";
                if (m["allowed_categories"] == null && m["forbidden_categories"] == null)
                    return "models entry '" + match + "' declares neither allowed_categories nor forbidden_categories: it would check nothing.";
            }
            foreach (JObject l in (rules["expected_links"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string nm = l.Value<string>("name_matches");
                if (string.IsNullOrWhiteSpace(nm) || !IsRegex(nm)) return "every expected_links entry needs a valid name_matches regex.";
                if (l["workset_matches"] != null && !IsRegex(l.Value<string>("workset_matches"))) return "workset_matches '" + l["workset_matches"] + "' is not a valid regex.";
            }
            if (rules["models"] == null && rules["expected_links"] == null && rules["same_site"] == null)
                return "rules declares nothing to check.";
            return null;
        }

        private static bool IsRegex(string p)
        {
            try { _ = new Regex(p); return true; } catch { return false; }
        }

        private static bool Matches(string pattern, FederationModelFact m) =>
            pattern == "$host" ? m.IsHost : m.Title != null && Regex.IsMatch(m.Title, pattern, RegexOptions.IgnoreCase);

        private static bool InList(JArray list, string token, string name) =>
            list != null && list.Any(t => string.Equals((string)t, token, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals((string)t, name, StringComparison.OrdinalIgnoreCase));

        public static JObject Evaluate(JObject rules, IList<FederationModelFact> models, IList<FederationLinkFact> links,
                                       double toleranceMm, int maxItems)
        {
            var modelRows = new JArray();
            int outOfPlace = 0, unclassified = 0;
            var modelRules = (rules["models"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            foreach (FederationModelFact m in models)
            {
                var claims = modelRules.Where(r => Matches(r.Value<string>("match"), m)).ToList();
                var row = new JObject { ["model"] = m.Title, ["host"] = m.IsHost };
                if (modelRules.Count == 0) continue;
                if (claims.Count == 0) { row["state"] = "unclassified"; unclassified++; modelRows.Add(row); continue; }
                if (claims.Count > 1)
                {
                    row["state"] = "ambiguous";
                    row["claimed_by"] = new JArray(claims.Select(c => c.Value<string>("match")));
                    unclassified++; modelRows.Add(row); continue;
                }
                JObject rule = claims[0];
                row["discipline"] = rule.Value<string>("discipline");
                var allowed = rule["allowed_categories"] as JArray;
                var forbidden = rule["forbidden_categories"] as JArray;
                var misplaced = new JArray();
                foreach (var kv in m.CategoryCounts.Where(k => k.Value > 0).OrderByDescending(k => k.Value))
                {
                    m.CategoryNames.TryGetValue(kv.Key, out string name);
                    bool bad = InList(forbidden, kv.Key, name) || (allowed != null && !InList(allowed, kv.Key, name));
                    if (!bad) continue;
                    outOfPlace += kv.Value;
                    m.SampleIds.TryGetValue(kv.Key, out List<long> ids);
                    misplaced.Add(new JObject
                    {
                        ["category"] = kv.Key, ["category_name"] = name, ["elements"] = kv.Value,
                        ["element_ids"] = new JArray((ids ?? new List<long>()).Take(maxItems)),
                        ["why"] = InList(forbidden, kv.Key, name) ? "forbidden" : "not in allowed_categories"
                    });
                }
                row["state"] = misplaced.Count == 0 ? "clean" : "elements_out_of_place";
                row["out_of_place"] = misplaced;
                modelRows.Add(row);
            }

            // ---- expected, missing and duplicate links ------------------------------
            var linkRows = new JArray();
            var claimed = new HashSet<long>();
            int missing = 0, duplicated = 0, wrongWorkset = 0;
            foreach (JObject e in (rules["expected_links"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string nm = e.Value<string>("name_matches");
                int want = e.Value<int?>("count") ?? 1;
                var hits = links.Where(l => l.Title != null && Regex.IsMatch(l.Title, nm, RegexOptions.IgnoreCase)).ToList();
                foreach (var h in hits) claimed.Add(h.InstanceId);
                string ws = e.Value<string>("workset_matches");
                var badWs = ws == null ? new List<FederationLinkFact>()
                    : hits.Where(h => h.Workset == null || !Regex.IsMatch(h.Workset, ws, RegexOptions.IgnoreCase)).ToList();
                wrongWorkset += badWs.Count;
                string state = hits.Count == 0 ? "missing" : hits.Count < want ? "fewer_than_expected" : hits.Count > want ? "duplicated" : "present";
                if (state == "missing" || state == "fewer_than_expected") missing++;
                if (state == "duplicated") duplicated++;
                linkRows.Add(new JObject
                {
                    ["name_matches"] = nm, ["expected"] = want, ["found"] = hits.Count, ["state"] = state,
                    ["instances"] = new JArray(hits.Take(maxItems).Select(h => h.InstanceId)),
                    ["wrong_workset"] = new JArray(badWs.Take(maxItems).Select(h => new JObject { ["instance_id"] = h.InstanceId, ["workset"] = h.Workset }))
                });
            }
            var unexpected = rules["expected_links"] == null ? new List<FederationLinkFact>()
                : links.Where(l => !claimed.Contains(l.InstanceId)).ToList();

            // ---- same site --------------------------------------------------------------
            bool sameSite = rules.Value<bool?>("same_site") ?? true;
            var siteRows = new JArray();
            int incoherent = 0, undecided = 0;
            if (sameSite)
                foreach (FederationLinkFact l in links)
                {
                    string st = l.SiteDeltaMm == null ? "not_decidable" : l.SiteDeltaMm <= toleranceMm ? "coherent" : "incoherent";
                    if (st == "incoherent") incoherent++;
                    if (st == "not_decidable") undecided++;
                    siteRows.Add(new JObject
                    {
                        ["instance_id"] = l.InstanceId, ["title"] = l.Title, ["state"] = st,
                        ["max_delta_mm"] = l.SiteDeltaMm == null ? null : (JToken)Math.Round(l.SiteDeltaMm.Value, 1),
                        ["reason"] = l.SiteWhyNot, ["link_site"] = l.LinkSiteName
                    });
                }

            bool fails = outOfPlace > 0 || missing > 0 || duplicated > 0 || wrongWorkset > 0 || incoherent > 0;
            bool open = unclassified > 0 || undecided > 0;
            return new JObject
            {
                ["verdict"] = fails ? "fails" : open ? "not_decidable" : "passes",
                ["summary"] = new JObject
                {
                    ["elements_out_of_place"] = outOfPlace, ["models_unclassified"] = unclassified,
                    ["links_missing"] = missing, ["links_duplicated"] = duplicated, ["links_wrong_workset"] = wrongWorkset,
                    ["links_unexpected"] = unexpected.Count, ["links_site_incoherent"] = incoherent, ["links_site_not_decidable"] = undecided
                },
                ["models"] = modelRows,
                ["expected_links"] = linkRows,
                ["unexpected_links"] = new JArray(unexpected.Take(maxItems).Select(l => new JObject { ["instance_id"] = l.InstanceId, ["title"] = l.Title })),
                ["site"] = siteRows,
                ["links"] = new JArray(links.Take(maxItems).Select(l => new JObject
                {
                    ["instance_id"] = l.InstanceId, ["title"] = l.Title, ["loaded"] = l.Loaded, ["workset"] = l.Workset, ["phase"] = l.Phase
                })),
                ["tolerance_mm"] = toleranceMm
            };
        }
    }
}
