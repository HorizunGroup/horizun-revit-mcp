using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class TemplateKitTool
    {
        private static JObject CheckState(string status, string reason = null) => new JObject { ["status"] = status, ["reason"] = reason };

        // Catalog category labels are part of the kit format, not the UI language.
        // Explicit OST_ categories in a check take precedence over this mapping.
        private static readonly Dictionary<string, string> KitCategoryIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Walls"] = "OST_Walls", ["Floors"] = "OST_Floors", ["Roofs"] = "OST_Roofs", ["Ceilings"] = "OST_Ceilings",
            ["Columns"] = "OST_Columns", ["Structural Columns"] = "OST_StructuralColumns", ["Structural Framing"] = "OST_StructuralFraming",
            ["Structural Foundations"] = "OST_StructuralFoundation", ["Doors"] = "OST_Doors", ["Windows"] = "OST_Windows",
            ["Furniture"] = "OST_Furniture", ["Casework"] = "OST_Casework", ["Plumbing Fixtures"] = "OST_PlumbingFixtures",
            ["Mechanical Equipment"] = "OST_MechanicalEquipment", ["Electrical Equipment"] = "OST_ElectricalEquipment",
            ["Electrical Fixtures"] = "OST_ElectricalFixtures", ["Lighting Fixtures"] = "OST_LightingFixtures",
            ["Specialty Equipment"] = "OST_SpecialityEquipment", ["Generic Models"] = "OST_GenericModel",
            ["Structural Rebar"] = "OST_Rebar", ["Stairs"] = "OST_Stairs", ["Ramps"] = "OST_Ramps"
        };
        private static readonly string[] JoinCategoryIds = { "OST_Walls", "OST_Floors", "OST_StructuralColumns", "OST_StructuralFraming", "OST_StructuralFoundation" };
        private static string CategoryId(JObject row)
        {
            string stable = (string)row["built_in_category"];
            if (stable != null && stable.StartsWith("OST_", StringComparison.Ordinal)) return stable;
            string c = (string)row["category"] ?? "";
            return c.StartsWith("OST_", StringComparison.Ordinal) ? c : KitCategoryIds.TryGetValue(c, out string id) ? id : null;
        }
        private static List<string> ExplicitCategories(JToken token) => (token as JArray ?? new JArray()).Values<string>()
            .Where(c => c != null && c.StartsWith("OST_", StringComparison.Ordinal)).Distinct().ToList();
        private static double[] AuditBox(JObject row)
        {
            var min = row["bounding_box"]?["min"] as JArray; var max = row["bounding_box"]?["max"] as JArray;
            if (min == null || max == null || min.Count != 3 || max.Count != 3 || min.Concat(max).Any(v => v.Type != JTokenType.Float && v.Type != JTokenType.Integer)) return null;
            return min.Concat(max).Values<double>().ToArray();
        }

        private static JObject KitDatum(JObject kit) => kit["datum"] as JObject ?? (kit["project_context"] as JObject)?["datum"] as JObject ??
            (kit["project"] as JObject)?["datum"] as JObject ?? (kit["conventions"] as JObject)?["datum"] as JObject;

        private static double? DeclaredElevationMm(JObject level, JObject datum)
        {
            bool Numeric(JToken t) => t?.Type == JTokenType.Float || t?.Type == JTokenType.Integer;
            if (Numeric(level["elevation_mm"])) return (double)level["elevation_mm"];
            if (Numeric(level["elevation_m"])) return (double)level["elevation_m"] * 1000;
            if (!Numeric(level["elevation"])) return null;
            string units = (string)level["units"] ?? (string)datum["units"];
            double? scale = units == "mm" ? 1 : units == "m" ? 1000 : units == "feet" ? 304.8 : (double?)null;
            return scale.HasValue ? (double)level["elevation"] * scale.Value : (double?)null;
        }

        private static List<JObject> CheckRows(string doc, JObject query, CancellationToken ct, JArray coverage, string label, out bool complete)
        {
            var rows = new List<JObject>(); complete = true;
            var a = (JObject)query.DeepClone(); a["target_document"] = doc; a["max_rows"] = 500; a["include_links"] = false;
            for (int page = 0; page < 200; page++)
            {
                ct.ThrowIfCancellationRequested();
                JObject r = Call("horizun_query_model", a, ct, coverage, label);
                if (r == null || !(r["rows"] is JArray values)) { complete = false; break; }
                complete &= (bool?)r["coverage_complete"] != false;
                rows.AddRange(values.OfType<JObject>());
                string next = (string)r["next_cursor"];
                if (next == null) return rows;
                a["cursor"] = next;
            }
            complete = false;
            return rows;
        }

        // Each continuation re-runs the deterministic sweep. A full page is not
        // incomplete geometry: the independent coverage ledgers distinguish the two.
        // Bound work explicitly at 50,000 full rows; hitting that limit never passes.
        private const int MaxAuditClashPages = 100;
        private static JArray CallClashPages(JObject arguments, CancellationToken ct, JArray coverage, string label, out bool complete, out string reason, Action<JObject> observe = null)
        {
            const int pageSize = 500;
            var args = (JObject)arguments.DeepClone(); args["max_results"] = pageSize;
            var clashes = new JArray(); var problems = new HashSet<string>(StringComparer.Ordinal);
            string fingerprint = null; int offset = 0;
            for (int page = 0; page < MaxAuditClashPages; page++)
            {
                ct.ThrowIfCancellationRequested(); args["offset"] = offset;
                JObject reply = Call("horizun_clash", args, ct, coverage, label);
                if (!(reply?["clashes"] is JArray rows)) { problems.Add("clash page could not be read"); break; }
                if (page == 0) observe?.Invoke(reply);
                string currentFingerprint = (string)reply["sweep_fingerprint"];
                if (page > 0 && (string.IsNullOrWhiteSpace(currentFingerprint) || currentFingerprint != fingerprint))
                { problems.Add("clash sweep fingerprint changed or was missing between pages"); break; }
                JToken next = reply["next_offset"];
                bool hasNext = next != null && next.Type != JTokenType.Null;
                if (hasNext && string.IsNullOrWhiteSpace(currentFingerprint))
                { problems.Add("clash continuation lacks a sweep fingerprint"); break; }
                fingerprint = currentFingerprint;
                if (rows.Count > pageSize || (reply["offset"] != null && (reply["offset"].Type != JTokenType.Integer || (int)reply["offset"] != offset)))
                { problems.Add("clash page size or returned offset disagrees with the request"); break; }
                bool indicesValid = rows.OfType<JObject>().Select((r, i) => r["sweep_index"] == null ||
                    (r["sweep_index"].Type == JTokenType.Integer && (int)r["sweep_index"] == offset + i)).All(valid => valid);
                if (!indicesValid) { problems.Add("clash sweep indices repeat or skip positions"); break; }
                foreach (JObject row in rows.OfType<JObject>()) clashes.Add(row.DeepClone());
                bool ledgersComplete = (bool?)reply["coverage"]?["complete"] == true &&
                    (bool?)reply["visibility_coverage"]?["coverage_complete"] == true && (bool?)reply["coverage_complete"] != false;
                if ((bool?)reply["coverage"]?["complete"] == false || (bool?)reply["visibility_coverage"]?["coverage_complete"] == false || (bool?)reply["coverage_complete"] == false)
                    problems.Add("clash geometry or source coverage was incomplete: " + ((string)reply["headline"] ?? "read the clash coverage ledgers"));
                if (!hasNext)
                {
                    if ((bool?)reply["truncated"] == true || (string)reply["result"] != "complete")
                        problems.Add("clash sweep ended without complete coverage: " + ((string)reply["headline"] ?? (string)reply["result"]));
                    complete = problems.Count == 0; reason = complete ? null : string.Join("; ", problems);
                    return clashes;
                }
                if ((string)reply["result"] != "partial" || (bool?)reply["truncated"] != true || !ledgersComplete)
                    problems.Add("clash continuation has partial geometry or unmeasured coverage: " + ((string)reply["headline"] ?? "read the clash coverage ledgers"));
                if (next.Type != JTokenType.Integer || next.Value<long>() <= offset || next.Value<long>() != (long)offset + rows.Count || next.Value<long>() > int.MaxValue)
                { problems.Add("clash next_offset did not advance contiguously"); break; }
                offset = (int)next;
                if (page == MaxAuditClashPages - 1) problems.Add("clash audit reached its explicit limit of " + MaxAuditClashPages + " pages / 50000 rows");
            }
            complete = false; reason = string.Join("; ", problems);
            return clashes;
        }

        private static Dictionary<string, JObject> AuditKitChecks(JObject kit, string doc, List<JObject> rows, bool complete,
            CancellationToken ct, JArray findings, JArray coverage, int top)
        {
            var states = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var checks = (kit["checks"] as JArray ?? new JArray()).OfType<JObject>()
                .GroupBy(c => CanonicalCheck((string)c["id"] ?? (string)c["name"] ?? "unnamed"))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var catalog = (kit["catalog"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var categories = catalog.Select(CategoryId).Where(c => c != null).Distinct().ToList();
            void Finding(string id, JArray sample, string what, int? count = null)
            {
                if ((count ?? sample.Count) == 0) return;
                findings.Add(new JObject { ["check"] = id, ["severity"] = "blocking", ["count"] = count ?? sample.Count,
                    ["what"] = what, ["sample"] = new JArray(sample.Take(top)) });
            }
            JObject Measurement(bool measured, string reason = null) => CheckState(measured ? "executed" : "not_executed", measured ? null : reason ?? "required measurement failed or was partial");

            if (checks.TryGetValue("hosts", out JObject hostCheck))
            {
                var hostCats = ExplicitCategories(hostCheck["args"]?["categories"]);
                if (hostCats.Count > 0)
                {
                    // The check may include a newly loaded type that is absent from the
                    // catalog. Its required host is still part of this check's scope.
                    var hosted = CheckRows(doc, new JObject { ["categories"] = new JArray(hostCats) }, ct, coverage, "hosts", out bool hostCoverage);
                    var missing = new JArray(hosted.Where(r => r["host_id"]?.Type != JTokenType.Integer || (long)r["host_id"] <= 0));
                    foreach (JObject existing in findings.OfType<JObject>().Where(f => (string)f["check"] == "hosts").ToList()) existing.Remove();
                    Finding("hosts", missing, "hosted instances in the kit check's categories have no host");
                    states["hosts"] = !complete || !hostCoverage ? Measurement(false) : hosted.Count == 0
                        ? CheckState("not_applicable", "no hosted instances in the check categories") : Measurement(true);
                }
            }

            if (checks.TryGetValue("no_hard_clashes", out JObject hard))
            {
                var options = hard["args"] as JObject ?? new JObject();
                List<string> a = ExplicitCategories(options["categories_a"]), b = ExplicitCategories(options["categories_b"]);
                if (a.Count == 0) a = categories.Where(c => c != "OST_Rebar").ToList();
                // A discipline's catalog intentionally lacks the other discipline's
                // types. A placeholder B must still inspect structural links.
                if (b.Count == 0) b = categories.Concat(JoinCategoryIds).Concat(new[] { "OST_Columns" }).Distinct().ToList();
                bool measured = complete && a.Count > 0 && b.Count > 0; var hits = new JArray(); var seen = new HashSet<string>(); var clashProblems = new List<string>();
                bool countsKnown = true, anyA = false, anyB = false;
                double tolerance = (double?)options["tolerance_mm"] ?? 10;
                for (int i = 0; i < a.Count; i += 10) for (int j = 0; j < b.Count; j += 10)
                {
                    JArray clashRows = CallClashPages(new JObject { ["target_document"] = doc,
                        ["categories_a"] = new JArray(a.Skip(i).Take(10)), ["categories_b"] = new JArray(b.Skip(j).Take(10)),
                        ["include_links"] = true, ["tolerance_mm"] = tolerance }, ct, coverage, "no_hard_clashes", out bool clashComplete, out string clashReason, reply =>
                        {
                            int? countA = (int?)reply["elements_a"], countB = (int?)reply["elements_b"];
                            countsKnown &= countA.HasValue && countB.HasValue;
                            anyA |= countA > 0; anyB |= countB > 0;
                        });
                    measured &= clashComplete;
                    if (clashReason != null) clashProblems.Add(clashReason);
                    foreach (JObject c in clashRows.OfType<JObject>())
                    {
                        string Side(JToken s) => ((string)s?["source_model"] ?? "host") + ":" + s?["link_instance_id"] + ":" + s?["element_id"];
                        string first = Side(c["a"]), second = Side(c["b"]);
                        string key = string.CompareOrdinal(first, second) < 0 ? first + "|" + second : second + "|" + first;
                        if (first == second || !seen.Add(key)) continue;
                        // The fixture inside its own host is an intentional hosted connection.
                        bool OwnHost(JToken x, JToken y) => InHost(x) && InHost(y) && rows.Any(r => r["element_id"]?.ToString() == x?["element_id"]?.ToString() &&
                            r["host_id"]?.Type == JTokenType.Integer && r["host_id"].ToString() == y?["element_id"]?.ToString());
                        if (!OwnHost(c["a"], c["b"]) && !OwnHost(c["b"], c["a"])) hits.Add(c.DeepClone());
                    }
                }
                Finding("no_hard_clashes", hits, "hard clashes beyond the declared tolerance (" + tolerance + " mm)");
                if (hits.Count == 0 && !countsKnown) { measured = false; clashProblems.Add("clash element counts were unavailable"); }
                states["no_hard_clashes"] = measured && hits.Count == 0 && countsKnown && (!anyA || !anyB)
                    ? CheckState("not_applicable", "no elements in one or both clash category sets")
                    : Measurement(measured, clashProblems.Count > 0 ? string.Join("; ", clashProblems.Distinct()) : "kit categories or complete clash coverage were unavailable");
                states["no_hard_clashes"]["tolerance_mm"] = tolerance;
                states["no_hard_clashes"]["categories_a"] = new JArray(a); states["no_hard_clashes"]["categories_b"] = new JArray(b);
                states["no_hard_clashes"]["scope"] = "kit discipline against kit and structural categories, host and loaded links; hosted connections are exempt";
            }

            if (checks.TryGetValue("audit_support", out JObject support))
            {
                var options = support["args"] as JObject ?? new JObject();
                var supported = new HashSet<string>(JoinCategoryIds.Concat(new[] { "OST_Columns", "OST_Roofs", "OST_Ceilings", "OST_Furniture", "OST_Casework", "OST_PlumbingFixtures", "OST_MechanicalEquipment", "OST_ElectricalEquipment", "OST_SpecialityEquipment", "OST_Stairs", "OST_Ramps" }));
                var floatingCats = ExplicitCategories(options["categories"]);
                if (floatingCats.Count == 0) floatingCats = categories.Where(supported.Contains).ToList();
                bool measured = complete && floatingCats.Count > 0; var hits = new JArray();
                bool countsKnown = true; int examined = 0;
                void ReadSupport(JObject args, string mode)
                {
                    args["target_document"] = doc; args["mode"] = mode;
                    JObject reply = Call("horizun_audit_support", args, ct, coverage, "audit_support:" + mode);
                    int? count = (int?)reply?["examined"]; countsKnown &= count.HasValue; examined += count ?? 0;
                    measured &= reply != null && (bool?)reply["coverage_complete"] == true && (bool?)reply["findings_truncated"] != true;
                    foreach (JObject f in (reply?["findings"] as JArray ?? new JArray()).OfType<JObject>())
                    { var hit = (JObject)f.DeepClone(); hit["mode"] = mode; hits.Add(hit); }
                    if (mode == "model_offset" && (bool?)reply?["aligned"] == false)
                        hits.Add(new JObject { ["mode"] = mode, ["link_instance_id"] = args["link_instance_id"], ["p95_offset_mm"] = reply["p95_offset_mm"] });
                }
                for (int i = 0; i < floatingCats.Count; i += 10)
                    ReadSupport(new JObject { ["categories"] = new JArray(floatingCats.Skip(i).Take(10)), ["include_links"] = true,
                        ["tolerance_mm"] = options["tolerance_mm"] ?? new JValue(50) }, "floating");
                var modes = (support["also_run_modes"] as JArray ?? new JArray()).Values<string>().ToList();
                if (modes.Contains("coverage_gaps"))
                {
                    var rooms = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_Rooms") }, ct, coverage, "support_rooms", out bool roomCoverage);
                    measured &= roomCoverage && rooms.All(r => r["element_id"]?.Type == JTokenType.Integer);
                    foreach (var batch in rooms.Where(r => r["element_id"]?.Type == JTokenType.Integer).Select((r, i) => new { r, i }).GroupBy(x => x.i / 500))
                        ReadSupport(new JObject { ["room_ids"] = new JArray(batch.Select(x => x.r["element_id"])), ["include_links"] = true }, "coverage_gaps");
                }
                if (modes.Contains("model_offset"))
                {
                    var links = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_RvtLinks") }, ct, coverage, "support_links", out bool linkCoverage);
                    measured &= linkCoverage;
                    foreach (JObject link in links.Where(r => (bool?)r["is_element_type"] != true))
                    {
                        if (link["element_id"]?.Type != JTokenType.Integer) { measured = false; continue; }
                        ReadSupport(new JObject { ["link_instance_id"] = link["element_id"], ["tolerance_mm"] = 10 }, "model_offset");
                    }
                }
                Finding("audit_support", hits, "unsupported elements, uncovered rooms or shifted links");
                if (hits.Count == 0 && !countsKnown) measured = false;
                states["audit_support"] = measured && hits.Count == 0 && countsKnown && examined == 0
                    ? CheckState("not_applicable", "no elements, rooms or links examined for support")
                    : Measurement(measured, "support, room or linked-grid measurement was incomplete");
                states["audit_support"]["categories"] = new JArray(floatingCats);
            }

            if (checks.TryGetValue("support_coverage_per_level", out JObject perLevelCheck))
            {
                var levels = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_Levels"), ["return_fields"] = new JArray("name") },
                    ct, coverage, "support_coverage_levels", out bool levelsComplete);
                bool measured = levelsComplete && levels.Count > 0;
                var perLevel = new JArray(); var gaps = new JArray(); var reasons = new HashSet<string>();
                var options = perLevelCheck["args"] as JObject ?? new JObject();
                foreach (JObject level in levels)
                {
                    var result = new JObject { ["level_id"] = level["element_id"], ["level_name"] = level["name"] };
                    perLevel.Add(result);
                    if (level["element_id"]?.Type != JTokenType.Integer || (long)level["element_id"] <= 0)
                    { measured = false; result["status"] = "not_executed"; result["reason"] = "level id could not be resolved"; reasons.Add((string)result["reason"]); continue; }
                    var request = new JObject { ["target_document"] = doc, ["mode"] = "coverage_gaps", ["level_id"] = level["element_id"],
                        ["include_links"] = (bool?)options["include_links"] ?? true };
                    if (options["grid_mm"]?.Type == JTokenType.Float || options["grid_mm"]?.Type == JTokenType.Integer) request["grid_mm"] = options["grid_mm"];
                    JObject reply = Call("horizun_audit_support", request, ct, coverage, "support_coverage_per_level");
                    result["retry_tool"] = "horizun_audit_support";
                    result["retry_arguments"] = request.DeepClone();
                    int? examined = (int?)reply?["examined"], count = (int?)reply?["findings_total"];
                    bool all = reply != null && (bool?)reply["coverage_complete"] == true && (bool?)reply["findings_truncated"] != true && examined > 0 && count >= 0;
                    string why = examined == 0 ? "0 recintos examinados; no demuestra cobertura de las habitaciones esperadas" : "room coverage measurement failed or was partial";
                    result["examined"] = examined; result["findings_total"] = count; result["coverage_complete"] = reply?["coverage_complete"];
                    result["status"] = all ? "executed" : "not_executed"; result["reason"] = all ? null : why;
                    measured &= all; if (!all) reasons.Add(why);
                    foreach (JObject gap in (reply?["findings"] as JArray ?? new JArray()).OfType<JObject>())
                    { var item = (JObject)gap.DeepClone(); item["level_id"] = level["element_id"]; item["level_name"] = level["name"]; gaps.Add(item); }
                }
                if (!levelsComplete || levels.Count == 0) reasons.Add("host levels were not completely resolved");
                Finding("support_coverage_per_level", gaps, "floor support coverage gaps by host level");
                states["support_coverage_per_level"] = Measurement(measured, string.Join("; ", reasons));
                states["support_coverage_per_level"]["examined_by_level"] = perLevel;
                if (!measured) states["support_coverage_per_level"]["required_input"] = new JObject
                {
                    ["model"] = doc,
                    ["rooms"] = "The active target document must contain bounded Rooms on every level being judged. A datum-only audit copy or a model without Rooms cannot establish coverage of expected rooms.",
                    ["coverage"] = "Resolve the level ids, then rerun each listed retry_tool/retry_arguments. Every level needs examined > 0, coverage_complete=true, findings_truncated=false and findings_total >= 0. Missing expected rooms still require comparison against the drawing or declared scope.",
                    ["time"] = "No unfinished background evaluator is implied; inspect coverage entries for failed or partial calls."
                };
                states["support_coverage_per_level"]["scope"] = "all resolved host levels; floor top-face coverage includes loaded links";
                states["support_coverage_per_level"]["not_measured_properties"] = new JArray("expected rooms absent from the model", "room finish coverage separate from structural slab coverage");
            }

            if (checks.ContainsKey("datum_names"))
            {
                JObject datum = KitDatum(kit);
                JToken convention = datum?["naming_convention"];
                var conventionObject = convention as JObject;
                string pattern = convention?.Type == JTokenType.String ? (string)convention : (string)conventionObject?["level_regex"] ?? (string)conventionObject?["regex"] ?? (string)conventionObject?["pattern"];
                var declared = (datum?["levels"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                double tolerance = (double?)datum?["elevation_tolerance_mm"] ?? 0;
                bool datumReady = datum != null && !string.IsNullOrWhiteSpace(pattern) && pattern.StartsWith("^", StringComparison.Ordinal) && pattern.EndsWith("$", StringComparison.Ordinal) &&
                    declared.Count > 0 && declared.All(l => !string.IsNullOrWhiteSpace((string)l["name"]) && DeclaredElevationMm(l, datum).HasValue) && tolerance >= 0;
                if (!datumReady)
                    states["datum_names"] = CheckState("not_executed", "requiere datum del encargo: datum.naming_convention con patrón explícito y datum.levels con nombres, cotas y unidades declaradas");
                else
                {
                    try
                    {
                        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                        var levels = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_Levels"), ["include_bounding_box"] = true,
                            ["coordinate_units"] = "mm", ["return_parameters"] = new JArray("LEVEL_ELEV") }, ct, coverage, "datum_names", out bool datumComplete);
                        var issues = new JArray(); var actual = new List<(JObject row, string name, double elevation)>();
                        foreach (JObject level in levels)
                        {
                            string name = (string)level["name"]; double? elevation = (double?)level["datum"]?["elevation"];
                            JToken raw = level["parameters"]?["LEVEL_ELEV"]?["raw"];
                            if (!elevation.HasValue && (raw?.Type == JTokenType.Integer || raw?.Type == JTokenType.Float)) elevation = (double)raw * 304.8;
                            if (name == null || !elevation.HasValue) { datumComplete = false; continue; }
                            actual.Add((level, name, elevation.Value));
                            if (!regex.IsMatch(name)) issues.Add(new JObject { ["element_id"] = level["element_id"], ["name"] = name, ["issue"] = "name violates declared datum.naming_convention" });
                            JObject expected = declared.FirstOrDefault(l => (string)l["name"] == name);
                            if (expected == null) issues.Add(new JObject { ["element_id"] = level["element_id"], ["name"] = name, ["issue"] = "level is absent from the declared project datum" });
                            else if (Math.Abs(elevation.Value - DeclaredElevationMm(expected, datum).Value) > tolerance)
                                issues.Add(new JObject { ["element_id"] = level["element_id"], ["name"] = name, ["issue"] = "elevation differs from the declared project datum",
                                    ["actual_mm"] = elevation, ["expected_mm"] = DeclaredElevationMm(expected, datum) });
                        }
                        foreach (JObject expected in declared.Where(l => !actual.Any(a => a.name == (string)l["name"])))
                            issues.Add(new JObject { ["name"] = expected["name"], ["issue"] = "declared datum level is missing from the model" });
                        for (int i = 0; i < actual.Count; i++) for (int j = i + 1; j < actual.Count; j++)
                            if (actual[i].name == actual[j].name || Math.Abs(actual[i].elevation - actual[j].elevation) <= tolerance)
                                issues.Add(new JObject { ["element_id"] = actual[i].row["element_id"], ["other_id"] = actual[j].row["element_id"], ["issue"] = "duplicate datum name or elevation" });
                        Finding("datum_names", issues, "level names, elevations or duplicates disagree with the declared project datum");
                        states["datum_names"] = Measurement(datumComplete, "datum level names or elevations were not completely readable");
                        states["datum_names"]["naming_pattern"] = pattern; states["datum_names"]["elevation_tolerance_mm"] = tolerance;
                        states["datum_names"]["levels_examined"] = actual.Count;
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is System.Text.RegularExpressions.RegexMatchTimeoutException)
                    { states["datum_names"] = CheckState("not_executed", "datum del encargo no evaluable: " + ex.Message); }
                }
            }

            if (checks.ContainsKey("shared_volume"))
            {
                var members = rows.Where(r => JoinCategoryIds.Contains(CategoryId(r))).ToList();
                bool measured = complete && rows.All(r => r["category"] == null || r["category"].Type == JTokenType.Null || CategoryId(r) != null) && members.All(r => AuditBox(r) != null && r["element_id"]?.Type == JTokenType.Integer);
                var pairs = new List<JArray>();
                for (int i = 0; i < members.Count; i++) for (int j = i + 1; j < members.Count; j++)
                {
                    double[] a = AuditBox(members[i]), b = AuditBox(members[j]);
                    if (a != null && b != null && Enumerable.Range(0, 3).All(k => a[k] <= b[k + 3] + 1 && b[k] <= a[k + 3] + 1))
                        pairs.Add(new JArray(members[i]["element_id"], members[j]["element_id"]));
                }
                double total = 0; var hits = new JArray();
                for (int i = 0; i < pairs.Count; i += 200)
                {
                    JObject reply = Call("horizun_join_geometry", new JObject { ["target_document"] = doc, ["operation"] = "read", ["pairs"] = new JArray(pairs.Skip(i).Take(200)) }, ct, coverage, "shared_volume");
                    var read = reply?["pairs"] as JArray;
                    measured &= reply != null && (bool?)reply["coverage_complete"] == true && read?.Count == Math.Min(200, pairs.Count - i);
                    double? volume = (double?)reply?["shared_volume_total_m3"];
                    if (volume == null) measured = false; else total += volume.Value;
                    foreach (JObject p in (read ?? new JArray()).OfType<JObject>())
                        if ((double?)p["shared_volume_m3"] > 1e-6) hits.Add(p.DeepClone());
                }
                // The join API only accepts host ids. Measure cross-document intersections
                // with clash instead of confusing a linked id with a host element's id.
                var links = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_RvtLinks") }, ct, coverage, "shared_volume_links", out bool linksComplete);
                measured &= linksComplete;
                double linkedTotal = 0; string linkedProblem = null;
                if (links.Count > 0)
                {
                    JArray crossRows = CallClashPages(new JObject { ["target_document"] = doc,
                        ["categories_a"] = new JArray(JoinCategoryIds), ["categories_b"] = new JArray(JoinCategoryIds),
                        ["include_links"] = true, ["tolerance_mm"] = 0 }, ct, coverage, "shared_volume_linked", out bool crossComplete, out string crossReason);
                    measured &= crossComplete;
                    linkedProblem = crossReason;
                    if (crossReason != null) coverage.Add(new JObject { ["check"] = "shared_volume_linked", ["coverage_complete"] = false, ["reason"] = crossReason });
                    foreach (JObject pair in crossRows.OfType<JObject>()
                        .Where(p => (bool?)p["cross_model"] == true || !InHost(p["a"]) || !InHost(p["b"])))
                    {
                        double? volume = (double?)pair["intersection_volume_m3"];
                        if (volume == null || (bool?)pair["intersection_volume_is_complete"] == false) { measured = false; continue; }
                        linkedTotal += volume.Value;
                        if (volume > 1e-6) hits.Add(pair.DeepClone());
                    }
                }
                total += linkedTotal;
                Finding("shared_volume", hits, "shared volume exceeds 0.000001 m3", total > 1e-6 ? Math.Max(1, hits.Count) : 0);
                states["shared_volume"] = measured && members.Count == 0 && links.Count == 0
                    ? CheckState("not_applicable", "no joinable elements or linked models")
                    : Measurement(measured, linkedProblem ?? "shared-volume intersections or complete model geometry could not be measured");
                states["shared_volume"]["shared_volume_total_m3"] = measured ? (JToken)total : null;
                states["shared_volume"]["tolerance_m3"] = 1e-6; states["shared_volume"]["pairs_examined"] = pairs.Count;
                states["shared_volume"]["linked_shared_volume_m3"] = measured ? (JToken)linkedTotal : null;
                states["shared_volume"]["scope"] = "joinable host pairs through join_geometry read; non-host pairs (including internal pairs of loaded links) through clash with zero distance tolerance";
            }

            if (checks.TryGetValue("coordination_view", out JObject coordination))
            {
                var views = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_Views"),
                    ["return_fields"] = new JArray("name", "view_type", "is_view_template"),
                    ["return_parameters"] = new JArray("VIEW_DETAIL_LEVEL") }, ct, coverage, "coordination_view", out bool viewCoverage);
                string wanted = (string)coordination["args"]?["view_name"];
                var candidates = views.Where(v => (bool?)v["is_view_template"] == false && (string)v["view_type"] == "ThreeD" &&
                    (wanted != null ? (string)v["name"] == wanted : Norm((string)v["name"]).Contains("coordina"))).ToList();
                bool measured = viewCoverage;
                var bad = new JArray();
                if (candidates.Count == 0) bad.Add(new JObject { ["problem"] = "coordination 3D view is missing" });
                foreach (JObject view in candidates)
                {
                    JToken raw = view["parameters"]?["VIEW_DETAIL_LEVEL"]?["raw"];
                    if (raw?.Type != JTokenType.Integer) measured = false;
                    else if ((int)raw != 3) bad.Add(new JObject { ["element_id"] = view["element_id"], ["name"] = view["name"], ["detail_level"] = raw, ["expected"] = "Fine (3)" });
                }
                Finding("coordination_view", bad, "coordination 3D view must exist and use Fine detail");
                states["coordination_view"] = Measurement(measured, "view identity or detail level was not readable");
                states["coordination_view"]["verified_properties"] = new JArray("exists", "ThreeD", "Fine detail");
                states["coordination_view"]["not_measured_properties"] = new JArray("shading", "colour by link or system");
            }

            if (checks.TryGetValue("beam_join_cutback", out JObject cutback))
            {
                var beams = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_StructuralFraming"),
                    ["return_parameters"] = new JArray("START_JOIN_CUTBACK", "END_JOIN_CUTBACK") }, ct, coverage, "beam_join_cutback", out bool measured);
                var bad = new JArray();
                foreach (JObject beam in beams) foreach (string end in new[] { "START_JOIN_CUTBACK", "END_JOIN_CUTBACK" })
                {
                    JToken raw = beam["parameters"]?[end]?["raw"];
                    if (raw?.Type != JTokenType.Integer && raw?.Type != JTokenType.Float) { measured = false; continue; }
                    if (Math.Abs((double)raw) > 1e-6) bad.Add(new JObject { ["element_id"] = beam["element_id"], ["parameter"] = end, ["raw_feet"] = raw });
                }
                Finding("beam_join_cutback", bad, "beam ends have a nonzero join cutback");
                states["beam_join_cutback"] = !complete || !measured ? Measurement(false) : beams.Count == 0
                    ? CheckState("not_applicable", "no structural framing in this model") : Measurement(true);
            }
            if (checks.ContainsKey("levels_honoured"))
            {
                var levels = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_Levels"), ["include_bounding_box"] = true }, ct, coverage, "levels_honoured", out bool levelCoverage);
                var elevations = levels.Select(l => (double?)l["datum"]?["elevation"]).Where(z => z.HasValue).Select(z => z.Value).ToList();
                bool measured = complete && rows.All(r => r["category"] == null || r["category"].Type == JTokenType.Null || CategoryId(r) != null) && levelCoverage && elevations.Count == levels.Count && elevations.Count > 0;
                var floors = rows.Where(r => CategoryId(r) == "OST_Floors").Select(AuditBox).ToList();
                var foundations = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_StructuralFoundation"), ["include_bounding_box"] = true }, ct, coverage, "levels_honoured_foundations", out bool foundationCoverage).Select(AuditBox).ToList();
                measured &= floors.All(b => b != null) && foundationCoverage && foundations.All(b => b != null);
                var bad = new JArray();
                var levelElements = rows.Where(r => new[] { "OST_Walls", "OST_Columns", "OST_StructuralColumns", "OST_StructuralFraming" }.Contains(CategoryId(r))).ToList();
                foreach (JObject element in levelElements)
                {
                    double[] box = AuditBox(element);
                    if (box == null) { measured = false; continue; }
                    const double tolerance = 50;
                    bool PlanTouches(double[] b) => b != null && b[0] <= box[3] && box[0] <= b[3] && b[1] <= box[4] && box[1] <= b[4];
                    var touchingFloors = floors.Where(PlanTouches).ToList();
                    bool OnDatum(double z) => elevations.Any(e => Math.Abs(e - z) <= tolerance);
                    bool AtFloor(double z) => touchingFloors.Any(b => Math.Abs(b[2] - z) <= tolerance || Math.Abs(b[5] - z) <= tolerance);
                    bool AtFoundationTop(double z) => foundations.Any(b => PlanTouches(b) && Math.Abs(b[5] - z) <= tolerance);
                    bool beam = CategoryId(element) == "OST_StructuralFraming";
                    bool valid = beam ? touchingFloors.Any(b => Math.Abs(b[5] - box[5]) <= tolerance)
                        : (OnDatum(box[2]) || AtFloor(box[2]) || AtFoundationTop(box[2])) && (OnDatum(box[5]) || AtFloor(box[5]));
                    if (beam && touchingFloors.Count == 0) { measured = false; continue; }
                    if (!valid) bad.Add(new JObject { ["element_id"] = element["element_id"], ["bottom_z_mm"] = box[2], ["top_z_mm"] = box[5],
                        ["problem"] = beam ? "beam top differs from overlapping slab top" : "wall/column bottom or top is away from an accepted level or surface" });
                }
                Finding("levels_honoured", bad, "element extents do not honour levels, overlapping slab faces or foundation tops (50 mm tolerance)");
                states["levels_honoured"] = measured && levelElements.Count == 0
                    ? CheckState("not_applicable", "no walls, columns or structural framing in this model")
                    : Measurement(measured, "level elevations, foundation boxes, element extents or an overlapping slab for a beam were not measured");
                states["levels_honoured"]["method"] = "box-level in mm; wall/column ends against levels or slab faces, bottom also against overlapping foundation top; beam top against overlapping slab top";
                states["levels_honoured"]["tolerance_mm"] = 50;
            }
            if (checks.TryGetValue("rebar_clash_free", out JObject rebar))
            {
                var bars = CheckRows(doc, new JObject { ["categories"] = new JArray("OST_Rebar") }, ct, coverage, "rebar_instances", out bool barsComplete);
                if (!complete || !barsComplete)
                {
                    states["rebar_clash_free"] = CheckState("not_executed", "rebar instances were not completely read");
                    return states;
                }
                if (bars.Count == 0)
                {
                    states["rebar_clash_free"] = CheckState("not_applicable", "no rebar in this model");
                    return states;
                }
                var args = (JObject)(rebar["args"] as JObject ?? new JObject()).DeepClone();
                args["target_document"] = doc; args["mode"] = "steel_review";
                JObject reply = Call("horizun_query_structure", args, ct, coverage, "rebar_clash_free");
                string verdict = (string)reply?["verdict"];
                bool measured = verdict == "clean" || verdict == "findings";
                int count = new[] { "duplicates", "intersections", "closer_than_declared_clearance" }.Sum(k => (int?)reply?["counts"]?[k]?["set_pairs"] ?? 0);
                Finding("rebar_clash_free", reply?["pairs"] as JArray ?? new JArray(), "reinforcement clashes", count);
                states["rebar_clash_free"] = Measurement(measured, "steel review is incomplete or the kit lacks required tolerances/clearances");
            }
            return states;
        }

        private static JObject AuditForeignNames(JObject kit, string doc, List<JObject> rows, bool complete,
            CancellationToken ct, JArray findings, JArray coverage, int top)
        {
            var rules = kit["conventions"]?["foreign_names"] as JObject;
            if (rules == null) return CheckState("not_applicable", "kit has no foreign_names convention");
            string[] Strings(JToken t) => t is JArray a ? a.Values<string>().ToArray() : t?.Type == JTokenType.String ? new[] { (string)t } : new string[0];
            string[] patterns = Strings(rules["forbidden_regex"]), fragments = Strings(rules["forbidden"] ?? rules["fragments"]);
            if (patterns.Length + fragments.Length == 0) return CheckState("not_executed", "foreign_names has no regex or fragments");
            var matches = new JArray(); bool readable = complete;
            try
            {
                TemplateKitRules.ForbiddenName("", patterns, fragments); // validate even on an empty model
                var ids = rows.Where(r => r["type_id"]?.Type == JTokenType.Integer).Select(r => (long)r["type_id"]).Where(id => id > 0).Distinct().ToList();
                // Explicit type ids prevent an instance Description from hiding its type's Description.
                if (rows.Any(r => r["type_id"] == null && !NotJudged.Contains((string)r["category"] ?? ""))) readable = false;
                for (int i = 0; i < ids.Count; i += 500)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = ids.Skip(i).Take(500).ToList();
                    var args = new JObject
                    {
                        ["target_document"] = doc, ["element_ids"] = new JArray(batch), ["include_types"] = true, ["max_rows"] = 500,
                        ["include_links"] = false,
                        ["return_parameters"] = new JArray("ALL_MODEL_MANUFACTURER", "ALL_MODEL_MODEL", "ALL_MODEL_DESCRIPTION", "ALL_MODEL_TYPE_COMMENTS", "HZ_Fabricante", "HZ_Referencia")
                    };
                    JObject reply = Call("horizun_query_model", args, ct, coverage, "foreign_names");
                    var types = (reply?["rows"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    if (reply == null || reply["coverage_complete"]?.Value<bool>() == false || reply["next_cursor"]?.Type == JTokenType.String || types.Count != batch.Count) readable = false;
                    foreach (JObject type in types)
                    {
                        var fields = new Dictionary<string, string> { ["family"] = (string)type["family"], ["type"] = (string)type["type"] ?? (string)type["name"] };
                        foreach (JProperty p in (type["parameters"] as JObject ?? new JObject()).Properties())
                        {
                            if (p.Value["read_error"] != null) readable = false;
                            fields[p.Name] = (string)p.Value["raw"];
                        }
                        foreach (var f in fields)
                            if (TemplateKitRules.ForbiddenName(f.Value, patterns, fragments))
                                matches.Add(new JObject { ["type_id"] = type["element_id"], ["family"] = type["family"], ["type"] = type["type"], ["field"] = f.Key, ["value"] = f.Value });
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is ArgumentException || ex is System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return CheckState("not_executed", "foreign_names convention could not be evaluated: " + ex.Message);
            }
            if (matches.Count > 0) findings.Add(new JObject
            {
                ["check"] = "foreign_names", ["severity"] = "blocking", ["count"] = matches.Count,
                ["what"] = "used types contain names or metadata forbidden by the kit", ["sample"] = new JArray(matches.Take(top))
            });
            if (readable && !rows.Any(r => r["type_id"]?.Type == JTokenType.Integer && (long)r["type_id"] > 0))
                return CheckState("not_applicable", "no used types in this model");
            return CheckState(readable ? "executed" : "not_executed", readable ? null : "used type names or parameters were not completely read");
        }

        private static JObject AuditTitleblocks(JObject kit, string path, string doc, List<JObject> rows, bool complete,
            CancellationToken ct, JArray findings, JArray coverage, int top)
        {
            if (!(kit["conventions"]?["titleblock_text"] is JObject)) return CheckState("not_applicable", "kit has no titleblock_text convention");
            var contract = Contract.Find("horizun_query_family_internals");
            if (!(contract?.InputSchema?["properties"]?["mode"]?["enum"] as JArray ?? new JArray()).Values<string>().Contains("audit_titleblock_text"))
                return CheckState("not_executed", "audit_titleblock_text mode is not available in this server");
            // Query by BuiltInCategory: row category labels follow Revit's language.
            JObject query = Call("horizun_query_model", new JObject
            {
                ["target_document"] = doc, ["categories"] = new JArray("OST_TitleBlocks"), ["max_rows"] = 500, ["include_links"] = false
            }, ct, coverage, "titleblocks");
            if (query == null || query["coverage_complete"]?.Value<bool>() == false || query["next_cursor"]?.Type == JTokenType.String)
                return CheckState("not_executed", "titleblock instances were not completely read");
            var blocks = (query["rows"] as JArray ?? new JArray()).OfType<JObject>().GroupBy(r => (string)r["family"]).Select(g => g.First()).ToList();
            if (blocks.Count == 0) return complete ? CheckState("not_applicable", "no titleblocks placed in this model") : CheckState("not_executed", "model query was incomplete");
            bool measured = true;
            foreach (JObject block in blocks)
            {
                JObject reply = Call("horizun_query_family_internals", new JObject
                {
                    ["target_document"] = doc, ["mode"] = "audit_titleblock_text", ["element_id"] = block["element_id"], ["template_kit_path"] = path
                }, ct, coverage, "audit_titleblock_text");
                if (reply?["coverage_complete"]?.Value<bool>() != true || (string)reply["brand_check"] == "not_measured_no_convention") measured = false;
                var issues = reply?["findings"] as JArray ?? new JArray();
                if (issues.Count > 0) findings.Add(new JObject
                {
                    ["check"] = "audit_titleblock_text", ["severity"] = "correction", ["family"] = block["family"],
                    ["count"] = issues.Count, ["sample"] = new JArray(issues.Take(top)),
                    ["what"] = "titleblock brand spelling or candidate text overlaps violate the kit convention"
                });
            }
            return CheckState(measured ? "executed" : "not_executed", measured ? null : "titleblock text, brand or bounding boxes were not completely measured");
        }

        private static string CanonicalCheck(string id)
        {
            switch (Norm(id).Replace(' ', '_'))
            {
                case "every_placed_type_is_coded": case "no_uncoded_types": return "codes";
                case "hosted_elements_have_their_host": return "hosts";
                case "volumen_compartido_=_0": case "volumen_compartido_0": case "shared_volume": return "shared_volume";
                case "vista_3d_de_coordinacion": case "coordination_view": return "coordination_view";
                default: return Norm(id).Replace(' ', '_');
            }
        }

        private static JArray ChecksCoverage(JObject kit, JArray findings, JArray reads, bool complete, JObject foreign, JObject titleblock, Dictionary<string, bool> measured, Dictionary<string, JObject> additional, int modelledCount, int clearanceCandidates, int hostCandidates)
        {
            var result = new JArray();
            var elementChecks = new HashSet<string>(new[] { "kit_types", "hosts", "legacy", "unusable_types", "traceability", "front_clearance" }, StringComparer.Ordinal);
            var readChecks = new HashSet<string>(new[] { "codes", "warnings", "level_span", "duplicates", "datums", "fit_out_in_walls", "fixtures_overlap", "generic_geometry" }, StringComparer.Ordinal);
            foreach (JObject check in (kit["checks"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string id = (string)check["id"] ?? (string)check["name"] ?? "unnamed";
                string canonical = CanonicalCheck(id);
                JObject state;
                if (canonical == "foreign_names") state = foreign;
                else if (canonical == "audit_titleblock_text") state = titleblock;
                else if (additional.TryGetValue(canonical, out JObject evaluated)) state = evaluated;
                else if (DrawingChecks.Contains(canonical))
                    state = CheckState("not_executed", "requiere planos");
                else if (elementChecks.Contains(canonical))
                {
                    bool all = complete && (!measured.TryGetValue(canonical, out bool available) || available);
                    state = !all ? CheckState("not_executed", "element query or required geometry measurement was incomplete")
                        : (canonical == "front_clearance" ? clearanceCandidates == 0 : canonical == "hosts" ? hostCandidates == 0 : modelledCount == 0)
                            ? CheckState("not_applicable", canonical == "front_clearance" ? "no front clearance candidates in this model" : canonical == "hosts" ? "no kit elements requiring a host in this model" : "no applicable kit elements in this model")
                            : CheckState("executed");
                }
                else if (readChecks.Contains(canonical))
                {
                    string source = canonical == "level_span" || canonical == "duplicates" || canonical == "datums" ? "warnings" : canonical;
                    bool read = reads.OfType<JObject>().Any(r => (string)r["check"] == source && (bool?)r["read"] == true && (bool?)r["coverage_complete"] != false);
                    bool partial = findings.OfType<JObject>().Any(f => (string)f["check"] == canonical && (string)f["severity"] == "not_assessable");
                    if (measured.TryGetValue(canonical, out bool available)) read &= available;
                    state = CheckState(read && !partial ? "executed" : "not_executed", read && !partial ? null : "required measurement failed or was partial");
                }
                else state = CheckState("not_executed", "no audit evaluator for this kit check; recipe definitions alone do not prove its pass condition");
                var row = (JObject)state.DeepClone();
                row["id"] = id; row["name"] = check["name"];
                row["passed"] = (string)row["status"] == "executed" ? (JToken)!findings.OfType<JObject>().Any(f => CanonicalCheck((string)f["check"] ?? "") == canonical &&
                    ((string)f["severity"] == "blocking" || (string)f["severity"] == "correction")) : null;
                row["blocking"] = (bool?)check["blocking"] ?? ((string)check["severity"] != "information" && (string)check["severity"] != "optional");
                result.Add(row);
            }
            // Conventions still affect the verdict in older kits without a checks entry.
            foreach (var pair in new[] { (id: "foreign_names", state: foreign), (id: "audit_titleblock_text", state: titleblock) })
                if (!result.OfType<JObject>().Any(r => (string)r["id"] == pair.id) && (string)pair.state["status"] != "not_applicable")
                {
                    var row = (JObject)pair.state.DeepClone(); row["id"] = pair.id; row["blocking"] = true; result.Add(row);
                }
            return result;
        }
    }
}
