// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_template_kit operation=audit: the model, judged by its kit.
//
// "Verified" is a property of one call; "done" is a property of the model. This reads
// the model through the SAME entry point every client uses (no second route to the
// add-in) and judges it against the kit it was built with: every placed type coded,
// only kit types used, every hosted family on its host, the legacy types left alone,
// traceability filled in, Revit's warnings counted. It ends in a verdict a person can
// act on - usable today / with corrections / not yet - and a check whose data could
// not be read says so instead of passing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class TemplateKitTool
    {
        /// <summary>Set once by Program: the same tools/call entry point every client uses.</summary>
        internal static Func<JObject, CancellationToken, JToken> Invoker;

        private static readonly HashSet<string> NotJudged = new HashSet<string>(StringComparer.Ordinal)
        {
            "Levels", "Grids", "Rooms", "Views", "Sheets", "Schedules", "Materials", "Project Information", "Room Tags", "Viewports",
            "Lines", "Detail Items", "Dimensions", "Text Notes", "Cameras", "Elevations", "Sections", "Reference Planes", "Scope Boxes",
            "Room Separation Lines", "Sun Path", "Work Plane Grid", "Shaft Openings", "Rectangular Straight Wall Opening",
            "Rectangular Arc Wall Opening", "Floor Openings", "Roof Openings", "Raster Images", "Area Schemes", "Areas",
            "Structural Rebar", "Structural Area Reinforcement", "Structural Path Reinforcement", "Structural Fabric Areas"
        };

        private static readonly string[] FitOut =
        {
            "OST_Casework", "OST_Furniture", "OST_FurnitureSystems", "OST_PlumbingFixtures", "OST_SpecialityEquipment"
        };

        private static JObject Audit(JObject kit, JObject args, CancellationToken ct, string kitPath)
        {
            if (Invoker == null) throw new InvalidOperationException("audit needs the live bridge; it is not wired in this host.");
            string doc = (string)args["target_document"];
            if (string.IsNullOrWhiteSpace(doc)) throw new ArgumentException("audit needs target_document: the ACTIVE model to judge.");
            int top = Math.Max(1, Math.Min(200, (int?)args["top"] ?? 20));

            var catalog = (kit["catalog"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var byKey = catalog.Where(e => e["key"] != null).GroupBy(e => (string)e["key"]).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var kitCategories = new HashSet<string>(catalog.Select(CategoryId).Where(c => c != null), StringComparer.Ordinal);
            // a NEW SIZE of a kit family (made with the entry's new_size recipe) is a kit type
            var kitFamilies = new HashSet<string>(catalog.Where(e => e["placement"]?["placement_type"] != null).Select(e => (string)e["family"]).Where(f => f != null), StringComparer.Ordinal);

            var findings = new JArray(); var coverage = new JArray();
            // 1) codes
            JObject codes = Call("horizun_query_classification", new JObject { ["target_document"] = doc, ["operation"] = "missing_codes" }, ct, coverage, "codes");
            int uncoded = (int?)codes?["placed_types_without_code"] ?? -1, unknown = (int?)codes?["types_with_unknown_code"] ?? -1;
            // 2) every instance with its type, host and traceability
            var rows = new List<JObject>(); string cursor = null; bool complete = true;
            for (int page = 0; page < 200; page++)
            {
                var a = new JObject
                {
                    ["target_document"] = doc, ["max_rows"] = 500, ["return_parameters"] = new JArray("HZ_AI_Source", "HZ_AI_Status"),
                    ["include_bounding_box"] = true, ["include_orientation"] = true, ["include_links"] = false
                };
                if (cursor != null) a["cursor"] = cursor;
                JObject q = Call("horizun_query_model", a, ct, coverage, page == 0 ? "elements" : null);
                if (q == null) { complete = false; break; }
                if (q["coverage_complete"]?.Value<bool>() == false) complete = false;
                rows.AddRange((q["rows"] as JArray ?? new JArray()).OfType<JObject>().Where(r => r["is_element_type"]?.Value<bool>() != true));
                cursor = (string)q["next_cursor"];
                if (cursor == null) break;
            }
            if (cursor != null) complete = false;
            var modelled = rows.Where(r => (string)r["category"] != null && !NotJudged.Contains((string)r["category"]) &&
                (kitCategories.Contains(CategoryId(r) ?? "") || byKey.ContainsKey((string)r["family"] + "|" + (string)r["type"]) || byKey.ContainsKey((string)r["type"] ?? ""))).ToList();
            var notKit = new List<JObject>(); var noHost = new List<JObject>(); var legacy = new List<JObject>(); var noTrace = new List<JObject>();
            var traceIssues = new JArray(); int hostCandidates = 0;
            foreach (JObject r in modelled)
            {
                string key = (string)r["family"] + "|" + (string)r["type"];
                byKey.TryGetValue(key, out JObject entry);
                if (entry == null) byKey.TryGetValue((string)r["type"] ?? "", out entry);
                bool unusable = (bool?)entry?["usable"] == false;
                // HZ_ is the kit's prefix: a system type made with an entry's new_size (same layers, another thickness) is still a kit type
                bool hzSystem = ((string)r["type"] ?? "").StartsWith("HZ_", StringComparison.Ordinal);
                if (entry == null && !hzSystem && !byKey.ContainsKey((string)r["type"] ?? "") && !kitFamilies.Contains((string)r["family"] ?? "")) notKit.Add(r);
                if (entry == null && kitFamilies.Contains((string)r["family"] ?? ""))
                    entry = catalog.FirstOrDefault(e => (string)e["family"] == (string)r["family"]);
                string host = (string)entry?["placement"]?["host"];
                if (host != null) hostCandidates++;
                if (host != null && (r["host_id"]?.Type != JTokenType.Integer || (long)r["host_id"] <= 0)) noHost.Add(r);
                if (unusable || ((string)r["type"] ?? "").IndexOf("Legacy", StringComparison.OrdinalIgnoreCase) >= 0 || ((string)r["type"] ?? "").IndexOf("do not use", StringComparison.OrdinalIgnoreCase) >= 0) legacy.Add(r);
                bool traceProblem = false;
                foreach (string parameter in new[] { "HZ_AI_Source", "HZ_AI_Status" })
                {
                    JToken cell = r["parameters"]?[parameter];
                    JToken allowed = kit["conventions"]?["traceability"]?[parameter];
                    var values = allowed as JArray ?? (allowed as JObject)?["allowed_values"] as JArray;
                    string issue = cell?["read_error"] != null ? "unreadable" : Horizun.Revit.Core.TemplateKitRules.TraceIssue(
                        cell?["exists"]?.Value<bool>() == true, (string)cell?["raw"], values?.Values<string>());
                    if (issue == null) continue;
                    traceProblem = true;
                    traceIssues.Add(new JObject { ["element_id"] = r["element_id"], ["parameter"] = parameter, ["issue"] = issue, ["value"] = cell?["raw"] });
                }
                if (traceProblem) noTrace.Add(r);
            }
            // 3) Revit warnings
            JObject audit = Call("horizun_audit_model", new JObject { ["target_document"] = doc, ["top"] = 50 }, ct, coverage, "warnings");
            JObject warnRow = (audit?["findings"] as JArray)?.OfType<JObject>().FirstOrDefault(f => (string)f["check"] == "warnings");
            JObject spanRow = (audit?["findings"] as JArray)?.OfType<JObject>().FirstOrDefault(f => (string)f["check"] == "level_span");
            JObject datumRow = (audit?["findings"] as JArray)?.OfType<JObject>().FirstOrDefault(f => (string)f["check"] == "datums");
            var datumItems = (datumRow?["items"] as JArray ?? new JArray()).OfType<JObject>().Where(i => (string)i["code"] == "coincident").ToList();
            int warnings = warnRow == null ? -1 : (int?)warnRow["count"] ?? -1;
            // 4) furniture and fixtures inside walls: a counter rotated 90 degrees crosses a wall and every other check passes.
            // include_links=TRUE: the walls and columns of a linked discipline are where fit-out lands most often, and
            // excluding them made every federated model "partial (1 link(s) not checked)" - not_assessable (INFORME_P6 6.2).
            JObject clash = Call("horizun_clash", new JObject
            {
                ["target_document"] = doc,
                ["categories_a"] = new JArray(FitOut), ["categories_b"] = new JArray("OST_Walls", "OST_StructuralColumns", "OST_Columns"),
                ["include_links"] = true, ["tolerance_mm"] = 10, ["max_results"] = 500
            }, ct, coverage, "fit_out_in_walls");
            // a hosted fixture's bracket inside ITS OWN host wall is how it hangs, not a clash - and only a wall of THIS
            // model can be its host: the same element id in a linked model names another element entirely
            var hostOf = rows.Where(r => r["host_id"]?.Type == JTokenType.Integer).GroupBy(r => r["element_id"]?.ToString()).ToDictionary(g => g.Key, g => g.First()["host_id"].ToString());
            var clashRows = (clash?["clashes"] as JArray ?? new JArray()).OfType<JObject>()
                // touching a wall face (a millimetre of rounding) is contact, not a clash: 0.1 litre and up is something IN the wall
                .Where(c => ((double?)c["intersection_volume_m3"] ?? 1) >= 0.0001)
                .Where(c => !(InHost(c["b"]) && hostOf.TryGetValue(c["a"]?["element_id"]?.ToString() ?? "", out string h) && h == c["b"]?["element_id"]?.ToString())).ToList();
            // 4b) fixtures against fixtures and the fit-out around them: a lavatory 140 mm into the shower passed every
            // check above (INFORME_P6 6.3). A basin SET INTO a counter, or a piece hung on another, overlaps by design.
            JObject fixtureClash = Call("horizun_clash", new JObject
            {
                ["target_document"] = doc,
                ["categories_a"] = new JArray(FixtureA), ["categories_b"] = new JArray(FixtureB),
                ["include_links"] = true, ["tolerance_mm"] = 10, ["max_results"] = 500
            }, ct, coverage, "fixtures_overlap");
            var fixtureRows = FixtureOverlaps(fixtureClash, rows);
            // 4c) placeholder geometry: a sanitary, door or outlet that is a plain box passed every check above
            // (field cycle 2026-10-24: "los sanitarios son horribles, una cosa generica"). One instance per type, no family opened.
            JObject geometry = Call("horizun_query_family_internals", new JObject
            {
                ["target_document"] = doc, ["mode"] = "geometry_quality", ["categories"] = new JArray(ProductCategories), ["max_types"] = 300
            }, ct, coverage, "generic_geometry");

            void Add(string id, string severity, int count, string what, IEnumerable<JObject> sample, string fix)
            {
                findings.Add(new JObject
                {
                    ["check"] = id, ["severity"] = severity, ["count"] = count, ["what"] = what, ["fix"] = fix,
                    ["sample"] = new JArray((sample ?? Enumerable.Empty<JObject>()).Take(top).Select(r => new JObject
                    { ["element_id"] = r["element_id"], ["category"] = r["category"], ["family"] = r["family"], ["type"] = r["type"] }))
                });
            }
            if (uncoded < 0) Add("codes", "not_assessable", -1, "missing_codes could not be read", null, "run horizun_query_classification operation=missing_codes");
            else if (uncoded > 0 || unknown > 0) Add("codes", "blocking", uncoded + unknown, uncoded + " placed type(s) without code, " + unknown + " with a code the keynote table does not know", null,
                "use kit types (they carry codes); reload the keynote table if codes are unknown");
            if (notKit.Count > 0) Add("kit_types", "blocking", notKit.Count, "elements whose type is not in the kit", notKit,
                "replace with a kit type (operation=find) or duplicate the closest kit type keeping its code");
            if (noHost.Count > 0) Add("hosts", "blocking", noHost.Count, "hosted families without their host", noHost, "re-place on the wall/ceiling/roof with host_id");
            if (legacy.Count > 0) Add("legacy", "blocking", legacy.Count, "placed types marked unusable or legacy", legacy, "replace with a usable kit type (operation=find) and follow its usable_reason");
            if (noTrace.Count > 0)
            {
                Add("traceability", "correction", noTrace.Count, "HZ_AI_Source or HZ_AI_Status absent, empty or outside the kit's allowed values", noTrace, "fill both traceability parameters with the kit's permitted values");
                ((JObject)findings.Last)["parameter_issues"] = new JArray(traceIssues.Take(top));
            }
            if (datumItems.Count > 0)
                findings.Add(new JObject
                {
                    ["check"] = "datums", ["severity"] = "blocking", ["count"] = datumItems.Count,
                    ["what"] = "levels or grids at the same place (one datum made twice: elements land on the wrong one)",
                    ["fix"] = "reuse and rename the template's levels (horizun_transform_elements operation=rename_level) instead of creating another at the same elevation; delete the duplicate",
                    ["sample"] = new JArray(datumItems.Take(top).Select(i => new JObject { ["first"] = i["first_name"], ["second"] = i["second_name"], ["separation_mm"] = i["separation_mm"] }))
                });
            if (clash == null) Add("fit_out_in_walls", "not_assessable", -1, "the clash check could not run", null, "run horizun_clash (Pro edition) categories_a=fit-out categories_b=OST_Walls");
            else if (clashRows.Count > 0)
                findings.Add(new JObject
                {
                    ["check"] = "fit_out_in_walls", ["severity"] = "blocking", ["count"] = clashRows.Count,
                    ["what"] = "furniture, casework or fixtures that pass through a wall or a column (wrong rotation or a point on the wrong side)",
                    ["fix"] = "re-place them: the catalog footprint is at rotation 0 (width along +X, front facing -Y); rotation_degrees = atan2(front) + 90; back-inserted items go on the wall FACE",
                    ["sample"] = new JArray(clashRows.Take(top).Select(c => new JObject
                    {
                        ["element_id"] = c["a"]?["element_id"], ["name"] = c["a"]?["name"], ["category"] = c["a"]?["category"],
                        ["wall_id"] = c["b"]?["element_id"], ["overlap_m3"] = c["intersection_volume_m3"]
                    }))
                });
            else if ((string)clash["result"] == "partial") Add("fit_out_in_walls", "not_assessable", -1, "the clash check was partial: " + (string)clash["headline"], null, "run horizun_clash (Pro edition) and read its coverage");
            if (fixtureClash == null) Add("fixtures_overlap", "not_assessable", -1, "the fixture clash check could not run", null, "run horizun_clash (Pro edition) categories_a=OST_PlumbingFixtures categories_b=fit-out");
            else if (fixtureRows.Count > 0)
                findings.Add(new JObject
                {
                    ["check"] = "fixtures_overlap", ["severity"] = "blocking", ["count"] = fixtureRows.Count,
                    ["what"] = "plumbing fixtures that overlap another fixture or a piece of fit-out (a lavatory inside the shower): they cannot both be built there",
                    ["fix"] = "move or change one of the two (a smaller shower, the lavatory on another wall); basis=plan_footprint means the solids do not touch but one piece stands in the footprint of the other (a lavatory hung over a shower tray); a basin set into a counter is not reported",
                    ["sample"] = new JArray(fixtureRows.Take(top).Select(c => new JObject
                    {
                        ["element_id"] = c["a"]?["element_id"], ["name"] = c["a"]?["name"], ["category"] = c["a"]?["category"],
                        ["other_id"] = c["b"]?["element_id"], ["other_name"] = c["b"]?["name"], ["other_category"] = c["b"]?["category"],
                        ["other_model"] = c["b"]?["source_model"], ["overlap_m3"] = c["intersection_volume_m3"],
                        ["basis"] = c["basis"], ["overlap_mm"] = c["overlap_mm"]
                    }))
                });
            else if ((string)fixtureClash["result"] == "partial") Add("fixtures_overlap", "not_assessable", -1, "the fixture clash check was partial: " + (string)fixtureClash["headline"], null, "run horizun_clash (Pro edition) and read its coverage");
            JObject generic = GenericGeometryFinding(geometry, top);
            if (generic != null) findings.Add(generic);
            // 5) free space in front of fixtures, from the kit's clearances table (rows with front_of)
            var blocked = FrontClearance(kit, rows, out int clearanceJudged, out int clearanceCandidates);
            if (blocked.Count > 0)
                findings.Add(new JObject
                {
                    ["check"] = "front_clearance", ["severity"] = "correction", ["count"] = blocked.Count,
                    ["what"] = "fixtures whose required free space in front is taken by a wall, a column or another piece (kit rules: clearances)",
                    ["fix"] = "move or turn the piece (or the obstacle) until the strip in front is free; the sample names both",
                    ["sample"] = new JArray(blocked.Take(top))
                });
            // warnings that are not cosmetic, by Revit's failure definition (stable across languages)
            var warnItems = (warnRow?["items"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            void FromWarning(string guid, string id, string severity, string what, string fix)
            {
                var hit = warnItems.Where(i => string.Equals((string)i["failure_definition_guid"], guid, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count == 0) return;
                findings.Add(new JObject
                {
                    ["check"] = id, ["severity"] = severity, ["count"] = hit.Sum(i => (int?)i["occurrences"] ?? 0), ["what"] = what, ["fix"] = fix,
                    ["sample"] = new JArray(hit.SelectMany(i => (i["failing_element_ids"] as JArray ?? new JArray()).Take(top)).Take(top).Select(v => new JObject { ["element_id"] = v }))
                });
            }
            FromWarning("b4176cef-6086-45a8-a066-c3fd424c9412", "duplicates", "blocking",
                "identical instances in the same place (a stage ran twice?): every quantity counts them twice",
                "delete one of each pair (horizun_delete_verified) and find out what created them");
            FromWarning("ce3275c6-1c51-402e-8de3-df3a3d566f5c", "rooms_not_enclosed", "correction",
                "rooms that are not in a closed region: their area is not real",
                "close the region with walls or room separators (playbook: rooms), then re-place the room");
            // Several rooms in ONE region: Revit gives the area to one and the rest read "Redundant Room" with ZERO area.
            // Found on a real tower (Acacias, 2026-10-16): 237 of 300 rooms had no area while only 34 were "not enclosed".
            FromWarning("83d4a67c-818c-4291-adaf-f2d33064fea8", "rooms_redundant", "correction",
                "several rooms share one region: all but one read 'Redundant Room' with zero area, so an area schedule is understated",
                "separate the region (walls, room separators at the openings) or keep ONE room for an open plan and delete the extras");
            if (warnings > 0) Add("warnings", "correction", warnings, "Revit warnings in the model", null, "horizun_audit_model lists them by root cause");
            else if (warnings < 0) Add("warnings", "not_assessable", -1, "the warning count could not be read", null, "run horizun_audit_model");
            if (spanRow == null || spanRow["coverage_complete"]?.Value<bool>() == false)
                Add("level_span", "not_assessable", -1, "the level-span check could not read every element", null,
                    "run horizun_audit_model and inspect level_span coverage");
            else if (spanRow.Value<int>("count") > 0)
                findings.Add(new JObject {
                    ["check"] = "level_span", ["severity"] = "blocking", ["count"] = spanRow["count"],
                    ["what"] = "elements cross another level and cannot belong to one 4D activity",
                    ["fix"] = "split at each level or document the design exception in HZ_Excepcion_Nivel",
                    ["sample"] = new JArray((spanRow["items"] as JArray ?? new JArray()).Take(top)) });
            if (spanRow != null && spanRow.Value<int?>("exceptions_total") > 0)
                findings.Add(new JObject {
                    ["check"] = "level_span_exceptions", ["severity"] = "information",
                    ["count"] = spanRow["exceptions_total"],
                    ["what"] = "documented continuity across levels",
                    ["sample"] = new JArray((spanRow["exceptions"] as JArray ?? new JArray()).Take(top)) });

            JObject foreign = AuditForeignNames(kit, doc, rows, complete, ct, findings, coverage, top);
            JObject titleblock = AuditTitleblocks(kit, kitPath, doc, rows, complete, ct, findings, coverage, top);
            var measured = new Dictionary<string, bool>
            {
                // missing_codes counts the complete type/instance census before applying
                // max_rows to its samples; it has no coverage_complete field of its own.
                ["codes"] = complete && uncoded >= 0 && unknown >= 0,
                ["level_span"] = spanRow != null && (int?)spanRow["count"] >= 0 && (bool?)spanRow["coverage_complete"] != false,
                ["front_clearance"] = complete && clearanceJudged == clearanceCandidates,
                ["datums"] = datumRow != null && (bool?)datumRow["coverage_complete"] != false,
                ["duplicates"] = warnRow != null && warnings >= 0 && (warnings == 0 ||
                    (warnRow["items"] is JArray items && items.OfType<JObject>().Sum(w => (int?)w["occurrences"] ?? 0) >= warnings))
            };
            Dictionary<string, JObject> additional = AuditKitChecks(kit, doc, rows, complete, ct, findings, coverage, top);
            if (complete && rows.Count == 0)
            {
                foreach (string id in new[] { "codes", "level_span", "duplicates", "datums" })
                    if (measured[id] && !findings.OfType<JObject>().Any(f => (string)f["check"] == id))
                        additional[id] = CheckState("not_applicable", "no model elements to examine");
            }
            // These tools report their own populations, including linked elements for clashes.
            // A host-only census cannot establish that a linked clash scope is empty.
            void EmptyClashScope(string id, JObject result)
            {
                int? a = (int?)result?["elements_a"], b = (int?)result?["elements_b"];
                if (complete && (string)result?["result"] == "complete" && (bool?)result?["coverage_complete"] != false &&
                    (bool?)result?["coverage"]?["complete"] != false && (bool?)result?["visibility_coverage"]?["coverage_complete"] != false &&
                    a.HasValue && b.HasValue && (a == 0 || b == 0))
                    additional[id] = CheckState("not_applicable", "no elements in one or both clash category sets");
            }
            EmptyClashScope("fit_out_in_walls", clash);
            EmptyClashScope("fixtures_overlap", fixtureClash);
            if (complete && (bool?)geometry?["coverage_complete"] == true && geometry?["types"] is JArray geometryTypes && geometryTypes.Count == 0)
                additional["generic_geometry"] = CheckState("not_applicable", "no product types examined for geometry");
            foreach (var evidence in AuditDeclaredEvidence(kit, args, rows, complete, findings)) additional[evidence.Key] = evidence.Value;
            JArray checks = ChecksCoverage(kit, findings, coverage, complete, foreign, titleblock, measured, additional, modelled.Count, clearanceCandidates, hostCandidates);
            var missingChecks = new JArray(checks.OfType<JObject>().Where(c => (bool)c["blocking"] && (string)c["status"] == "not_executed").Select(c => c.DeepClone()));
            var declaredChecks = new JArray(checks.OfType<JObject>().Where(c => (string)c["status"] == "executed_with_declared_evidence").Select(c => c.DeepClone()));
            bool blocking = findings.OfType<JObject>().Any(f => (string)f["severity"] == "blocking");
            bool corrections = findings.OfType<JObject>().Any(f => (string)f["severity"] == "correction");
            bool unassessed = findings.OfType<JObject>().Any(f => (string)f["severity"] == "not_assessable") || !complete;
            ct.ThrowIfCancellationRequested();
            string verdict = modelled.Count == 0 ? "not_assessable" : blocking ? "not_yet" : corrections || unassessed || missingChecks.Count > 0 || declaredChecks.Count > 0 ? "usable_with_corrections" : "usable_today";
            var byCategory = modelled.GroupBy(r => (string)r["category"]).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());
            return new JObject
            {
                ["target_document"] = doc, ["verdict"] = verdict,
                ["audit_completed"] = true,
                ["acceptance_blockers"] = new JObject
                {
                    ["measured_failures"] = new JArray(findings.OfType<JObject>().Where(f => (string)f["severity"] == "blocking" && (string)f["basis"] != "declared_evidence").Select(f => f.DeepClone())),
                    ["declared_failures"] = new JArray(findings.OfType<JObject>().Where(f => (string)f["severity"] == "blocking" && (string)f["basis"] == "declared_evidence").Select(f => f.DeepClone())),
                    ["missing_measurements"] = missingChecks.DeepClone(),
                    ["declared_evidence_only"] = declaredChecks.DeepClone(),
                    ["means"] = "The audit has finished with the stated coverage. not_yet is an acceptance verdict caused by blocking findings, not a running job. Declared failures are separate from model measurements. Missing measurements name the input or coverage required; they are never a pass."
                },
                ["verdict_es"] = verdict == "usable_today" ? "sirve hoy" : verdict == "usable_with_corrections" ? "sirve con correcciones" : verdict == "not_yet" ? "no todavía" : "no evaluable",
                ["elements_judged"] = modelled.Count, ["by_category"] = JObject.FromObject(byCategory),
                ["findings"] = findings, ["coverage"] = coverage, ["complete"] = complete,
                ["checks_coverage"] = checks, ["blocking_checks_not_executed"] = missingChecks,
                ["checks_with_declared_evidence"] = declaredChecks,
                ["note"] = "Judged against the kit's catalog and conventions. Drawing checks can consume validated external declarations; their passed field remains null and declared_passed is not a host-measured pass. Any declared evidence keeps the verdict below usable_today."
            };
        }

        /// <summary>Categories where a plain box is a placeholder, not a product.</summary>
        internal static readonly string[] ProductCategories =
        {
            "OST_PlumbingFixtures", "OST_Doors", "OST_Windows", "OST_ElectricalFixtures", "OST_LightingFixtures",
            "OST_LightingDevices", "OST_ElectricalEquipment", "OST_Furniture", "OST_Casework", "OST_SpecialityEquipment"
        };

        /// <summary>
        /// The generic_geometry finding from a horizun_query_family_internals (Pro edition) mode=geometry_quality reply:
        /// not_assessable when the read failed or was partial with nothing found, correction with the
        /// box_like types, null when none.
        /// </summary>
        internal static JObject GenericGeometryFinding(JObject geometry, int top)
        {
            JObject Unassessed(string what) => new JObject
            {
                ["check"] = "generic_geometry", ["severity"] = "not_assessable", ["count"] = -1, ["what"] = what,
                ["fix"] = "run horizun_query_family_internals (Pro edition) mode=geometry_quality (fewer categories if it was truncated)",
                ["sample"] = new JArray()
            };
            if (geometry == null) return Unassessed("the family geometry could not be read");
            var box = (geometry["types"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(t => (string)t["verdict"] == "box_like").ToList();
            if (box.Count == 0)
                return geometry["coverage_complete"]?.Type == JTokenType.Boolean && !(bool)geometry["coverage_complete"]
                    ? Unassessed("the geometry read was partial (types unreadable or truncated); no placeholder among those read")
                    : null;
            return new JObject
            {
                ["check"] = "generic_geometry", ["severity"] = "correction", ["count"] = box.Count,
                ["instances"] = box.Sum(t => (int?)t["instances"] ?? 0),
                ["what"] = "family types whose geometry is a plain box (placeholder): sanitaries, doors, outlets or luminaires that look generic in 3D",
                ["fix"] = "replace with a product family from the kit catalog (or download/model the product geometry); a box-shaped " +
                          "product (tank, cabinet carcass) can read box_like and may stay - say why",
                ["sample"] = new JArray(box.Take(top).Select(t => new JObject
                {
                    ["family"] = t["family"], ["type"] = t["type"], ["category"] = t["category"], ["instances"] = t["instances"],
                    ["sample_instance_id"] = t["sample_instance_id"], ["reasons"] = t["reasons"]
                }))
            };
        }

        private static readonly string[] FixtureA = { "OST_PlumbingFixtures" };
        private static readonly string[] FixtureB =
        {
            "OST_PlumbingFixtures", "OST_Casework", "OST_Furniture", "OST_FurnitureSystems", "OST_SpecialityEquipment"
        };

        /// <summary>A clash side that is an element of the audited model (not of a link).</summary>
        private static bool InHost(JToken side)
        {
            string source = (string)side?["source_model"];
            return source == null || source == "host";
        }

        /// <summary>
        /// The fixture-vs-fit-out clashes that are real: at least 0.1 litre, and not a piece that lies inside the other in
        /// plan (a basin in its counter, a fixture hung on another) - that overlap is how it is built. Pieces of a link
        /// have no box here, so they are always reported.
        /// </summary>
        internal static List<JObject> FixtureOverlaps(JObject clash, List<JObject> rows)
        {
            var boxes = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (JObject r in rows)
            {
                var b = r["bounding_box"] as JObject;
                var mn = b?["min"] as JArray; var mx = b?["max"] as JArray;
                string id = r["element_id"]?.ToString();
                if (id == null || mn == null || mx == null || mn.Count < 2 || mx.Count < 2 || boxes.ContainsKey(id)) continue;
                boxes[id] = new[] { (double)mn[0], (double)mn[1], (double)mx[0], (double)mx[1] };
            }
            bool Inside(double[] small, double[] big, double tol) =>
                small[0] >= big[0] - tol && small[1] >= big[1] - tol && small[2] <= big[2] + tol && small[3] <= big[3] + tol;
            bool Nested(JObject c)
            {
                if (!InHost(c["a"]) || !InHost(c["b"])) return false;
                if (!boxes.TryGetValue(c["a"]?["element_id"]?.ToString() ?? "", out double[] a)) return false;
                if (!boxes.TryGetValue(c["b"]?["element_id"]?.ToString() ?? "", out double[] b)) return false;
                return Inside(a, b, 10) || Inside(b, a, 10);
            }
            var found = (clash?["clashes"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(c => ((double?)c["intersection_volume_m3"] ?? 1) >= 0.0001)
                .Where(c => !Nested(c)).ToList();
            foreach (JObject c in found) c["basis"] = "solid";
            found.AddRange(FootprintOverlaps(rows, found));
            return found;
        }

        private static readonly HashSet<string> FixtureBNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Plumbing Fixtures", "Casework", "Furniture", "Furniture Systems", "Specialty Equipment"
        };

        /// <summary>
        /// Pieces whose SOLIDS never touch but whose plan footprints overlap: a wall-hung lavatory hangs at 800 mm over
        /// the tray of a shower, so the solid clash finds nothing, yet nobody can stand in that shower (Casa, bano 2:
        /// 400 x 140 mm inside the shower). Judged only between pieces of this model whose front is orthogonal (their
        /// box IS their footprint), with at least 50 mm of overlap both ways and a shared height; a piece lying inside
        /// the other in plan (a basin in its counter) is how it is built.
        /// </summary>
        internal static List<JObject> FootprintOverlaps(List<JObject> rows, List<JObject> already)
        {
            const double minOverlap = 50, tol = 10;
            var seen = new HashSet<string>(already.Select(c => Pair(c["a"]?["element_id"]?.ToString(), c["b"]?["element_id"]?.ToString())), StringComparer.Ordinal);
            var pieces = new List<(JObject row, double[] box)>();
            foreach (JObject r in rows)
            {
                if (!FixtureBNames.Contains((string)r["category"] ?? "")) continue;
                var facing = r["placement"]?["facing"] as JArray;
                if (facing == null || facing.Count < 2) continue;
                double fx = (double)facing[0], fy = (double)facing[1];
                if (Math.Abs(Math.Abs(fx) + Math.Abs(fy) - 1) > 0.01) continue;      // rotated: its box is not its footprint
                var b = r["bounding_box"] as JObject;
                var mn = b?["min"] as JArray; var mx = b?["max"] as JArray;
                if (mn == null || mx == null || mn.Count < 3 || mx.Count < 3) continue;
                pieces.Add((r, new[] { (double)mn[0], (double)mn[1], (double)mn[2], (double)mx[0], (double)mx[1], (double)mx[2] }));
            }
            bool InsidePlan(double[] sm, double[] g) => sm[0] >= g[0] - tol && sm[1] >= g[1] - tol && sm[3] <= g[3] + tol && sm[4] <= g[4] + tol;
            var result = new List<JObject>();
            for (int i = 0; i < pieces.Count; i++)
                for (int j = 0; j < pieces.Count; j++)
                {
                    if (i == j) continue;
                    var a = pieces[i]; var o = pieces[j];
                    if ((string)a.row["category"] != "Plumbing Fixtures") continue;                 // side A is always a fixture
                    if ((string)o.row["category"] == "Plumbing Fixtures" && j < i) continue;        // fixture pairs once
                    string key = Pair(a.row["element_id"]?.ToString(), o.row["element_id"]?.ToString());
                    if (seen.Contains(key)) continue;
                    double dx = Math.Min(a.box[3], o.box[3]) - Math.Max(a.box[0], o.box[0]);
                    double dy = Math.Min(a.box[4], o.box[4]) - Math.Max(a.box[1], o.box[1]);
                    double dz = Math.Min(a.box[5], o.box[5]) - Math.Max(a.box[2], o.box[2]);
                    if (dx < minOverlap || dy < minOverlap || dz <= tol) continue;
                    if (InsidePlan(a.box, o.box) || InsidePlan(o.box, a.box)) continue;
                    seen.Add(key);
                    result.Add(new JObject
                    {
                        ["a"] = new JObject { ["element_id"] = a.row["element_id"], ["name"] = a.row["family"] + " : " + a.row["type"], ["category"] = a.row["category"], ["source_model"] = "host" },
                        ["b"] = new JObject { ["element_id"] = o.row["element_id"], ["name"] = o.row["family"] + " : " + o.row["type"], ["category"] = o.row["category"], ["source_model"] = "host" },
                        ["intersection_volume_m3"] = null, ["basis"] = "plan_footprint",
                        ["overlap_mm"] = new JArray(Math.Round(dx), Math.Round(dy))
                    });
                }
            return result;
        }

        private static string Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

        /// <summary>
        /// The strip of the clearances table's depth in front of each fixture it names must be free of walls,
        /// columns and other fit-out. Front comes from Revit's facing: HZ free-standing families face their BACK
        /// (the wall they stand against), wall-hosted ones face the room. A piece that overlaps the fixture itself
        /// (a sink in its counter) is not an obstacle. Plan boxes only: a diagonal or curved wall is not judged.
        /// </summary>
        internal static List<JObject> FrontClearance(JObject kit, List<JObject> rows, out int judged)
            => FrontClearance(kit, rows, out judged, out _);

        private static List<JObject> FrontClearance(JObject kit, List<JObject> rows, out int judged, out int candidates)
        {
            judged = 0; candidates = 0;
            var blocked = new List<JObject>();
            var table = (kit["rules"] as JArray ?? new JArray()).OfType<JObject>().FirstOrDefault(r => (string)r["topic"] == "clearances");
            var needs = new List<(string match, double mm)>();
            foreach (JObject r in (table?["rows"] as JArray ?? new JArray()).OfType<JObject>())
            {
                double mm = (double?)r["mm"] ?? 0;
                if (mm <= 0 || r["front_of"] == null) continue;
                foreach (string m in r["front_of"].Type == JTokenType.Array ? r["front_of"].Values<string>() : new[] { (string)r["front_of"] })
                    if (!string.IsNullOrWhiteSpace(m)) needs.Add((m.ToLowerInvariant(), mm));
            }
            if (needs.Count == 0) return blocked;

            double[] Box(JObject r)
            {
                var b = r["bounding_box"] as JObject;
                var mn = b?["min"] as JArray; var mx = b?["max"] as JArray;
                if (mn == null || mx == null || mn.Count < 3 || mx.Count < 3) return null;
                return new[] { (double)mn[0], (double)mn[1], (double)mn[2], (double)mx[0], (double)mx[1], (double)mx[2] };
            }
            bool Overlap(double[] a, double[] b, double tol) =>
                Math.Min(a[3], b[3]) - Math.Max(a[0], b[0]) > tol && Math.Min(a[4], b[4]) - Math.Max(a[1], b[1]) > tol && Math.Min(a[5], b[5]) - Math.Max(a[2], b[2]) > tol;

            var obstacleCats = new HashSet<string>(StringComparer.Ordinal) { "Walls", "Structural Columns", "Columns", "Casework", "Furniture", "Furniture Systems", "Plumbing Fixtures", "Specialty Equipment" };
            var obstacles = rows.Where(r => obstacleCats.Contains((string)r["category"] ?? "")).Select(r => (row: r, box: Box(r))).Where(o => o.box != null)
                // a wall whose plan box is wide both ways is diagonal or curved: its box is not its body
                .Where(o => (string)o.row["category"] != "Walls" || Math.Min(o.box[3] - o.box[0], o.box[4] - o.box[1]) < 600).ToList();

            foreach (JObject r in rows)
            {
                string fam = ((string)r["family"] ?? "").ToLowerInvariant();
                var need = needs.FirstOrDefault(n => fam.Contains(n.match));
                if (need.match == null) continue;
                candidates++;
                double[] me = Box(r);
                var facing = r["placement"]?["facing"] as JArray;
                if (me == null || facing == null || facing.Count < 2) continue;
                double fx = (double)facing[0], fy = (double)facing[1];
                bool hosted = r["host_id"]?.Type == JTokenType.Integer;
                if (!hosted) { fx = -fx; fy = -fy; }
                if (Math.Abs(Math.Abs(fx) + Math.Abs(fy) - 1) > 0.01) continue;   // only orthogonal fronts are judged
                judged++;
                const double inset = 20;
                double[] strip = fx > 0.5 ? new[] { me[3], me[1] + inset, me[2], me[3] + need.mm, me[4] - inset, me[2] + 1500 }
                               : fx < -0.5 ? new[] { me[0] - need.mm, me[1] + inset, me[2], me[0], me[4] - inset, me[2] + 1500 }
                               : fy > 0.5 ? new[] { me[0] + inset, me[4], me[2], me[3] - inset, me[4] + need.mm, me[2] + 1500 }
                               : new[] { me[0] + inset, me[1] - need.mm, me[2], me[3] - inset, me[1], me[2] + 1500 };
                foreach (var o in obstacles)
                {
                    if (ReferenceEquals(o.row, r) || Overlap(o.box, me, 1)) continue;       // itself, or what it sits in
                    if (!Overlap(o.box, strip, 10)) continue;
                    blocked.Add(new JObject
                    {
                        ["element_id"] = r["element_id"], ["family"] = r["family"], ["type"] = r["type"], ["needs_mm"] = need.mm,
                        ["blocked_by"] = new JObject { ["element_id"] = o.row["element_id"], ["category"] = o.row["category"], ["family"] = o.row["family"], ["type"] = o.row["type"] }
                    });
                    break;
                }
            }
            if (candidates > 0)
                candidates += rows.Count(r => obstacleCats.Contains((string)r["category"] ?? "") && !obstacles.Any(o => ReferenceEquals(o.row, r)));
            return blocked;
        }

        private static JObject Call(string tool, JObject arguments, CancellationToken ct, JArray coverage, string label)
        {
            try
            {
                JToken r = Invoker(new JObject { ["name"] = tool, ["arguments"] = arguments }, ct);
                var body = r as JObject;
                bool isError = body?["isError"]?.Value<bool>() == true;
                JObject structured = body?["structuredContent"] as JObject ?? body;
                if (label != null) coverage.Add(new JObject { ["check"] = label, ["tool"] = tool, ["read"] = !isError && structured != null,
                    ["coverage_complete"] = structured?["coverage_complete"],
                    ["error"] = isError ? body?.ToString(Newtonsoft.Json.Formatting.None) : null });
                return isError ? null : structured;
            }
            catch (Exception ex)
            {
                if (label != null) coverage.Add(new JObject { ["check"] = label, ["tool"] = tool, ["read"] = false, ["error"] = ex.Message });
                return null;
            }
        }
    }
}
