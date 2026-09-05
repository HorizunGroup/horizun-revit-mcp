// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_model_scan — one native pass that replaces two IronPython audit engines
// (preentrega_audit.py, 412 lines; revit_audit.py, 510 lines).
//
// Those two engines are the most silently-broken code in the estate, and every
// defect below was measured on real models. This handler exists to make each one
// unrepresentable rather than merely fixed:
//
//   1. `views_default_named_count = len(default_named)` where default_named was
//      capped at CAP=60 DURING the loop. 300 off-standard views got reported as
//      60 — a number the reader has no way to distrust. Here the full list is
//      built first and only the RETURN is capped, so `total` is the model's count
//      and `truncated` says out loud that you are not seeing all of it.
//   2. `section()` wrote {"_error": ...} and the host indexed d["warnings"]
//      without .get(). A section that threw either exploded in the host or, worse,
//      rendered as "CAD importado: 0 → OK". Here a failed section carries NO
//      buckets at all: there is no empty list to mistake for a clean result, and
//      `complete` is false at the top level.
//   3. `except: area = 0` then `elif area == 0: unbounded += 1` — a room whose
//      Area could not be read was reported as an unbounded room. "I could not
//      look" was silently spelled "there is something wrong there". Both engines
//      do it. Here unreadable is its own bucket, always.
//   4. `ename()` returned "?" on failure and "?" then failed every naming check.
//      A read error was reported as a naming violation. Here names come back raw
//      with `name_unreadable` beside them, and NOTHING here judges a name: the
//      standard is regex, the host has stdlib, and this tool has no opinion.
//   5. s_views computed no_template and threw the list away, so the correction
//      step had no ids to act on. It is returned.
//   6. 'unused' was derived from FamilyInstance only — see TypesWithNoInstances.
//
// Read-only by construction: no transaction is opened, so a scan cannot damage
// the model it is judging.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public class ModelScanCommand : ICommand
    {
        public string Name => "horizun_model_scan";

        public string Description =>
            "One deep native pass over the active model: cleanliness (CAD imported vs linked, IMPORT-* patterns, " +
            "unused templates/filters/group types/types, stray lines, in-place families), naming inputs (RAW view/" +
            "sheet/level/grid names — never judged here; validate them host-side with a regex), documentation " +
            "(views without template WITH ids, views not on a sheet, sheets missing a titleblock), project info " +
            "(raw values, placeholders are the caller's call), health (warnings grouped with failing element ids, " +
            "rooms/areas), links, worksets, categories, design options and the element-type universe. Every section " +
            "reports status ok|failed(reason) — a section that threw returns no buckets, so it can never read as " +
            "clean. Every bucket reports total (exact) vs returned vs truncated. Unreadable elements get their own " +
            "bucket: 'I could not look' is never spelled 'there is nothing there'.";

        private static readonly string[] AllSections =
        {
            "document", "categories", "cleanliness", "naming", "documentation",
            "project_info", "health", "links", "worksets", "design_options", "lines", "types",
            "coordinates", "datums", "level_association", "worksharing", "families",
            "views", "sheets", "annotations",
            "parameters", "spatial", "groups", "design_options_census", "phases", "mep", "structure", "federation", "external_content", "documentary_context",
            "delivery_readiness",
            // Runs last and reads the others' output rather than the model.
            "weight"
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            _census = null;   // the model may have changed since the last request
            _censusUnreadable = 0;
            _censusUnreadableError = null;

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // THE SHAPE OF THE REQUEST, before any of it is acted on. This tool
            // already refuses an unknown SECTION name and says why: a name that
            // silently does nothing is how a caller thinks it checked something it
            // never checked. The same reasoning belongs one level up, and did not
            // used to be applied there - a misspelt option was accepted and a full
            // clean-looking scan came back.
            ScanRequestVerdict shape = ScanRequestRules.Check(request, AllSections);
            if (!shape.Ok) return CommandResult.Fail(Name + ": " + shape.Message);

            var wanted = request.Value<string>("target_document_title");
            if (string.IsNullOrWhiteSpace(wanted))
                return CommandResult.Fail(
                    "target_document_title is required. Two Revit hosts run side by side here, and a scan of the " +
                    "wrong document is worse than no scan: it is a clean report about a file nobody looked at. " +
                    "The active document is '" + (SafeTitle(doc) ?? "(title unreadable)") + "'.");

            var actual = SafeTitle(doc);
            if (!TitlesMatch(wanted, actual))
                return CommandResult.Fail(
                    "Refusing to scan: you asked for '" + wanted + "' but the active document is '" +
                    (actual ?? "(title unreadable)") + "'. Nothing was read. Activate the intended document, " +
                    "or check you are talking to the right Revit host.");

            // Validated above, so this is a read rather than a repair. It used to
            // clamp with Math.Max(1, ...), which turned a limit nobody could honour
            // into a reply shaped by a number the caller did not choose.
            int top = 50;
            JToken topToken = request["top"];
            if (topToken != null && topToken.Type != JTokenType.Null) top = topToken.Value<int>();
            var targetParam = request.Value<string>("target_parameter");

            HashSet<string> sections;
            var secToken = request["sections"] as JArray;
            if (secToken == null || secToken.Count == 0)
            {
                sections = new HashSet<string>(AllSections, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in secToken)
                {
                    var s = t.ToString();
                    // An unknown section name silently doing nothing is how a caller
                    // thinks it checked something it never checked.
                    if (!AllSections.Contains(s, StringComparer.OrdinalIgnoreCase))
                        return CommandResult.Fail("Unknown section '" + s + "'. Known sections: " + string.Join(", ", AllSections));
                    sections.Add(s);
                }
            }

            // ---- the budget, and where each bucket resumes ------------------------
            //
            // `top` survives as the DEFAULT for every bucket that section_limits does
            // not name - that is the whole of its meaning now, and it is documented as
            // a legacy fallback rather than a second, competing knob. It cannot
            // contradict section_limits because it never wins against it.
            BudgetPlan budget = SectionBudgets.Parse(request["section_limits"], AllSections, top);

            // Read ONCE, here, so the section cannot silently fall back to a
            // default of its own when the request carries none.
            WarningProfile warningProfile = WarningRules.ReadProfile(request["warning_profile"]);
            NamingProfile namingProfile = NamingProfileRules.Read(request["naming_profile"]);
            FamilyProfile familyProfile = FamilyCensusRules.Read(request["family_profile"]);
            // The view types THIS Revit has, so a rule filed under a misspelt one
            // is refused rather than silently never running.
            ViewProfile viewProfile = ViewFactsRules.Read(request["view_profile"],
                                                          Enum.GetNames(typeof(ViewType)));
            SheetRules sheetRules = SheetAnnotationRules.Read(request["sheet_rules"]);
            // The BuiltInParameter names THIS Revit has, so a rule pinned to one
            // that does not exist is refused rather than silently never running.
            ParameterProfile parameterProfile = ParameterStandardRules.Read(
                request["parameter_profile"],
                n => { BuiltInParameter bip; return Enum.TryParse(n, out bip); });

            // The documentary profile is READ BY THE SAME PARSER as the parameter
            // profile, so wrong_guid, placeholder and empty mean one thing in this
            // bridge rather than two.
            ParameterProfile documentaryProfile = ParameterStandardRules.Read(
                request["documentary_profile"],
                n => { BuiltInParameter bip; return Enum.TryParse(n, out bip); });

            // 4D and 5D roles are parameter rules: which parameter carries an
            // activity id is the caller's declaration, read by the same parser.
            ParameterProfile fourDProfile = ParameterStandardRules.Read(
                request["fourd_profile"],
                n => { BuiltInParameter bip; return Enum.TryParse(n, out bip); });
            ParameterProfile fiveDProfile = ParameterStandardRules.Read(
                request["fived_profile"],
                n => { BuiltInParameter bip; return Enum.TryParse(n, out bip); });
            // No taxonomy is compiled in; the catalogue arrives as an argument.
            ClassificationCatalogue classificationCatalogue =
                ClassificationCatalogueRules.Read(request["classification_catalogue"]);

            // No guid is compiled in: which FailureDefinitionId means "redundant"
            // is the caller's to declare, exactly as the default workset names are.
            var redundantWarningGuids = new List<string>();
            var spatialRules = request["spatial_rules"] as JObject;
            if (spatialRules != null)
            {
                var arr = spatialRules["redundant_warning_guids"] as JArray;
                if (arr != null)
                    foreach (JToken t in arr)
                        if (t.Type == JTokenType.String) redundantWarningGuids.Add(t.Value<string>());
            }
            // The triage budget is a CANDIDATE count, not a page: the reply says
            // how many families were ranked and how many it passed over.
            int familyCandidateBudget = 20;
            JToken fb = request["family_budget"];
            if (fb != null && fb.Type == JTokenType.Integer) familyCandidateBudget = fb.Value<int>();
            if (!budget.Ok) return CommandResult.Fail(Name + ": " + budget.Message);

            var paging = new ScanPagingContext
            {
                Plan = budget,
                // The fingerprint a cursor is checked against. A cursor minted over
                // one model must not silently page another.
                DocumentFingerprint = CursorFingerprint(doc),
                RawCursor = request.Value<string>("cursor"),
            };

            var result = new JObject();
            var failed = new JArray();
            var skipped = new JArray();

            // HOW MUCH OF THE MODEL THIS SCAN IS ABOUT. Measured once, before any
            // section runs. A closed workset's elements are not in the document at all,
            // so every collector below sees a model with holes in it and no count comes
            // back short by a knowable amount - "0 imported CAD instances" over a partly
            // loaded model is a true statement about what was loaded, presented as a
            // statement about the building. See Core/DocumentVisibilityCoverage.cs.
            DocumentVisibilityCoverage coverage = DocumentVisibility.Measure(doc);

            Section(result, failed, skipped, sections, "document", () => DocumentSection(doc, app));
            Section(result, failed, skipped, sections, "categories", () => CategoriesSection(doc, paging));
            Section(result, failed, skipped, sections, "cleanliness", () => CleanlinessSection(doc, paging));
            Section(result, failed, skipped, sections, "naming", () => NamingSection(doc, paging, namingProfile));
            Section(result, failed, skipped, sections, "documentation", () => DocumentationSection(doc, paging));
            Section(result, failed, skipped, sections, "project_info", () => ProjectInfoSection(doc));
            Section(result, failed, skipped, sections, "health", () => HealthSection(doc, paging, warningProfile));
            Section(result, failed, skipped, sections, "links", () => LinksSection(doc, paging));
            Section(result, failed, skipped, sections, "worksets", () => WorksetsSection(doc, paging));
            Section(result, failed, skipped, sections, "design_options", () => DesignOptionsSection(doc, paging));
            Section(result, failed, skipped, sections, "lines", () => LinesSection(doc));
            Section(result, failed, skipped, sections, "types", () => TypesSection(doc, paging, targetParam));
            Section(result, failed, skipped, sections, "coordinates", () => CoordinatesSection(doc, paging));
            Section(result, failed, skipped, sections, "datums", () => DatumsSection(doc, paging));
            Section(result, failed, skipped, sections, "level_association",
                    () => LevelAssociationSection(doc, paging));
            Section(result, failed, skipped, sections, "worksharing",
                    () => WorksharingSection(doc, paging, app.Application.Username));
            Section(result, failed, skipped, sections, "families",
                    () => FamiliesSection(doc, paging, familyProfile, familyCandidateBudget));
            Section(result, failed, skipped, sections, "views",
                    () => ViewsSection(doc, paging, viewProfile));
            Section(result, failed, skipped, sections, "sheets",
                    () => SheetsSection(doc, paging, sheetRules));
            Section(result, failed, skipped, sections, "annotations",
                    () => AnnotationsSection(doc, paging, sheetRules));
            Section(result, failed, skipped, sections, "parameters",
                    () => ParametersSection(doc, paging, parameterProfile));
            Section(result, failed, skipped, sections, "spatial",
                    () => SpatialSection(doc, paging, redundantWarningGuids));
            Section(result, failed, skipped, sections, "groups", () => GroupsSection(doc, paging));
            Section(result, failed, skipped, sections, "design_options_census",
                    () => DesignOptionsCensus(doc, paging));
            Section(result, failed, skipped, sections, "phases", () => PhasesSection(doc, paging));
            Section(result, failed, skipped, sections, "mep", () => MepSection(doc, paging));
            Section(result, failed, skipped, sections, "structure", () => StructureSection(doc, paging));
            Section(result, failed, skipped, sections, "federation", () => FederationSection(doc, paging));
            Section(result, failed, skipped, sections, "external_content",
                    () => ExternalContentSection(doc, paging));
            Section(result, failed, skipped, sections, "documentary_context",
                    () => DocumentaryContextSection(doc, paging, documentaryProfile));
            Section(result, failed, skipped, sections, "delivery_readiness",
                    () => DeliveryReadinessSection(doc, paging, fourDProfile, fiveDProfile,
                                                   classificationCatalogue));

            // ---- weight, LAST, over what the other sections already measured -------
            //
            // It collects nothing of its own. Every number it ranks was taken by a
            // section above and carries that section's coverage, so a second collector
            // cannot disagree with the first about the same population.
            Section(result, failed, skipped, sections, "weight", () =>
            {
                List<Contributor> built = WeightAttributionFromScan.Build(result, sections);
                WeightProfile wp = WeightAttributionRules.ReadProfile(
                    request["weight_profile"], WeightAttributionFromScan.Kinds);
                WeightAttribution ranked = WeightAttributionRules.Attribute(built, wp);
                return WeightAttributionFromScan.ToJson(ranked, built);
            });

            return CommandResult.Ok(new JObject
            {
                ["document_title"] = actual,
                ["document_verified"] = true,
                ["top"] = top,
                // What each bucket was actually allowed, and whether it resumed. A
                // budget accepted at the door and ignored by the emitters is the
                // defect this reporting exists to make visible.
                ["budget"] = budget.ToJson(),
                ["paged"] = paging.Paged,
                ["cursor_problems"] = paging.CursorProblems,
                ["cursor_used"] = paging.CursorUsed,
                // `sections` is what was REQUESTED and `skipped` is what was not, so
                // subtracting one from the other mixes two disjoint sets: asking for 3
                // sections gave 3 - 9 = -6. Failures are the only thing that takes a
                // requested section away from you, so they are the only thing to subtract.
                ["sections_ok"] = sections.Count - failed.Count,
                ["sections_requested"] = sections.Count,
                ["sections_failed"] = failed,
                ["sections_not_requested"] = skipped,
                // The host must be able to refuse to render a verdict. It cannot do
                // that off a per-section flag it might forget to read, so the whole
                // scan carries one: complete=false means no bucket in here supports
                // the sentence "the model is clean".
                //
                // COVERAGE IS PART OF THAT, and it used not to be. A scan where every
                // section ran perfectly over a model with three closed worksets reported
                // complete=true, and everything it did not find was reported as not
                // there. Both ways of failing to see the model end in the same flag,
                // because they have the same consequence for the reader.
                ["complete"] = failed.Count == 0 && coverage.CoverageComplete,
                ["note"] = failed.Count == 0 && coverage.CoverageComplete
                    ? null
                    : string.Join(" ", new[]
                      {
                          failed.Count == 0 ? null
                            : failed.Count + " section(s) could not run — see sections_failed. Those sections " +
                              "returned NO buckets on purpose: an empty list would be indistinguishable from a " +
                              "clean result. Do not read a missing finding as a pass.",
                          coverage.CoverageComplete ? null : coverage.Note(),
                          "This scan is INCOMPLETE."
                      }.Where(s => s != null)),
                // One shape, on every read-only command, so a caller learns to look for
                // it once rather than per tool.
                ["visibility_coverage"] = coverage.ToJson(),
                ["sections"] = result
            });
        }

        // ---- Section plumbing: failure is structural, not a flag in a footnote. ----
        private static void Section(JObject into, JArray failed, JArray skipped,
                                    HashSet<string> wanted, string name, Func<JObject> build)
        {
            if (!wanted.Contains(name))
            {
                // Distinct from both "ok, empty" and "failed". A section nobody asked
                // for must not be mistakable for one that came back clean.
                skipped.Add(name);
                into[name] = new JObject { ["status"] = "not_requested" };
                return;
            }

            JObject body;
            try { body = build(); }
            catch (Exception ex)
            {
                // NO buckets on a failed section. The IronPython original wrote
                // {"_error": ...} next to an empty list and the checklist rendered
                // "CAD importado: 0 → OK 🟢" over a scan that threw.
                into[name] = new JObject
                {
                    ["status"] = "failed",
                    ["reason"] = ex.Message,
                    ["consequence"] = "'" + name + "' was NOT scanned. Its findings are unknown, not clean. " +
                                      "No count from this section exists, because a zero here would be a lie."
                };
                failed.Add(name);
                return;
            }

            var wrap = new JObject { ["status"] = "ok" };
            foreach (var p in body.Properties()) wrap[p.Name] = p.Value;
            into[name] = wrap;
        }

        /// <summary>
        /// total is the model's count, ALWAYS. `items` is the complete list; only
        /// the RETURN is shortened. This is the shape that makes the CAP=60 lie
        /// (count taken from a list capped mid-loop) impossible to write.
        /// </summary>
        /// <summary>
        /// What a cursor is checked against. Stable for one model, and carries
        /// NOTHING personal: the title and Revit's own creation GUID, hashed. A
        /// path would name a user; a title alone would let a cursor minted over
        /// one copy of a model page a different copy of it.
        /// </summary>
        private static string CursorFingerprint(Document doc)
        {
            // ProjectInformation.UniqueId rather than Document.CreationGUID: the latter
            // does not exist on Revit 2023 at all, and the 2023 build failed on it.
            // Measured by reflection over RevitAPI.dll. This one is present in every
            // supported year, so there is no version branch here - the brief asks for
            // minimal, explicit conditional compatibility, and none at all is better.
            string seed;
            try { seed = (SafeTitle(doc) ?? "") + "|" + (doc.ProjectInformation?.UniqueId ?? ""); }
            catch { seed = SafeTitle(doc) ?? ""; }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed)))
                    .Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        // ================================ document ================================
        private static JObject DocumentSection(Document doc, UIApplication app)
        {
            return new JObject
            {
                ["title"] = SafeTitle(doc),
                ["path"] = SafePath(doc),
                ["is_workshared"] = TryBool(() => doc.IsWorkshared),
                ["is_family_document"] = TryBool(() => doc.IsFamilyDocument),
                ["revit_version"] = TryStr(() => app.Application.VersionNumber),
                ["revit_build"] = TryStr(() => app.Application.VersionBuild),
                ["user"] = TryStr(() => app.Application.Username),
                ["file_size_mb"] = FileSizeMb(doc),
                ["element_count"] = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(),
                ["element_type_count"] = new FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount(),
                ["scanned_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z"
            };
        }

        // ============================== categories ================================
        private static JObject CategoriesSection(Document doc, ScanPagingContext paging)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int total = 0, unreadable = 0;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                total++;
                string nm;
                try
                {
                    var c = e.Category;
                    nm = c == null ? "(no category)" : c.Name;
                }
                catch { unreadable++; continue; }
                int n;
                counts.TryGetValue(nm, out n);
                counts[nm] = n + 1;
            }

            var rows = counts.OrderByDescending(kv => kv.Value)
                .Select(kv => (JToken)new JObject { ["category"] = kv.Key, ["instances"] = kv.Value })
                .ToList();

            return new JObject
            {
                ["total_instances"] = total,
                ["distinct_categories"] = counts.Count,
                // Not folded into any category: an element whose Category threw is
                // not an element "with no category".
                ["category_unreadable"] = unreadable,
                ["by_category"] = paging.Bucket(rows, "categories", "by_category")
            };
        }

        // ============================== cleanliness ===============================
        private static JObject CleanlinessSection(Document doc, ScanPagingContext paging)
        {
            var o = new JObject();

            // ---- CAD. An import is permanent weight; a link is not. Never one bucket. ----
            var hardImports = new List<JToken>();
            var cadLinks = new List<JToken>();
            var cadUnreadable = new List<JToken>();
            foreach (var imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                bool isLink;
                try { isLink = imp.IsLinked; }
                catch (Exception ex)
                {
                    // IsLinked is the ONLY thing separating "blocks delivery" from
                    // "fine". Guessing false here (as the python does) would file an
                    // unreadable link under hard imports and vice versa.
                    cadUnreadable.Add(new JObject
                    {
                        ["id"] = imp.Id.ToString(),
                        ["error"] = "IsLinked unreadable: " + ex.Message,
                        ["consequence"] = "Cannot tell import from link for this one. It is in neither bucket."
                    });
                    continue;
                }

                var rec = new JObject
                {
                    ["id"] = imp.Id.ToString(),
                    ["type_name"] = TypeNameOf(doc, imp),
                    ["category"] = CategoryOf(imp),
                    ["view_specific"] = TryBool(() => imp.ViewSpecific),
                    ["owner_view_id"] = TryStr(() => imp.ViewSpecific ? imp.OwnerViewId.ToString() : null),
                    ["pinned"] = TryBool(() => imp.Pinned)
                };
                if (isLink) cadLinks.Add(rec); else hardImports.Add(rec);
            }

            o["cad_imported"] = paging.Bucket(hardImports, "cleanliness", "cad_imported");
            o["cad_linked"] = paging.Bucket(cadLinks, "cleanliness", "cad_linked");
            o["cad_unreadable"] = paging.Bucket(cadUnreadable, "cleanliness", "cad_unreadable");
            o["cad_link_types"] = new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).GetElementCount();
            o["cad_note"] = hardImports.Count == 0
                ? null
                : hardImports.Count + " CAD file(s) are IMPORTED, not linked. An import writes its layers, line " +
                  "patterns and text styles into this model's namespace permanently — deleting the instance does " +
                  "not take them back out. That is why the IMPORT-* pattern buckets below are usually non-zero too.";

            // ---- IMPORT-* patterns: the residue an import leaves behind. ----
            var impLine = new List<JToken>();
            int lineTotal = 0;
            foreach (var lp in new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)))
            {
                lineTotal++;
                var n = SafeName(lp);
                if (n != null && n.ToUpperInvariant().Contains("IMPORT"))
                    impLine.Add(new JObject { ["id"] = lp.Id.ToString(), ["name"] = n });
            }
            var impFill = new List<JToken>();
            int fillTotal = 0;
            foreach (var fp in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
            {
                fillTotal++;
                var n = SafeName(fp);
                if (n != null && n.ToUpperInvariant().Contains("IMPORT"))
                    impFill.Add(new JObject { ["id"] = fp.Id.ToString(), ["name"] = n });
            }
            o["line_patterns_total"] = lineTotal;
            o["line_patterns_import"] = paging.Bucket(impLine, "cleanliness", "line_patterns_import");
            o["fill_patterns_total"] = fillTotal;
            o["fill_patterns_import"] = paging.Bucket(impFill, "cleanliness", "fill_patterns_import");

            // ---- Unused view templates. A template nobody applies is dead weight
            //      that still has to be reviewed by whoever inherits the model. ----
            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var usedTemplates = new HashSet<string>(StringComparer.Ordinal);
            var usedFilters = new HashSet<string>(StringComparer.Ordinal);
            var templates = new List<View>();
            // Every read that throws below silently demotes a LIVE template or filter to
            // "unused": its reference never reaches usedTemplates/usedFilters, and the
            // correction step deletes off those lists. So each failure is recorded here
            // and both lists degrade to an UPPER BOUND while any of them exist.
            //
            // Keyed BY VIEW ID, not one row per failure. Two reads are attempted per
            // view and the ViewTemplateId catch deliberately does not `continue` — the
            // filters are still worth collecting, since giving up on them would demote
            // MORE live filters to "unused". So one view can fail twice. A list that
            // appended per failure would make this bucket's `total` an event count
            // while the file's contract says `total` is always the model's count, and
            // would make UnusedNote say "2 view(s)" about one view. Both numbers are
            // the reader's only handle on how far off the unused lists are, so an
            // inflated one is not a safe over-warning — it is a wrong number.
            var unreadableById = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var viewsUnreadable = new List<JToken>();   // insertion order, one entry per view
            foreach (var v in allViews)
            {
                bool isTpl;
                try { isTpl = v.IsTemplate; }
                catch (Exception ex)
                {
                    NoteViewUnreadable(unreadableById, viewsUnreadable, v,
                        "IsTemplate unreadable: " + ex.Message,
                        "Neither the template it applies nor the filters it uses could be collected, " +
                        "so anything this view references may be listed as unused below.");
                    continue;
                }
                if (isTpl) { templates.Add(v); continue; }
                try
                {
                    var t = v.ViewTemplateId;
                    if (t != null && t != ElementId.InvalidElementId) usedTemplates.Add(t.ToString());
                }
                catch (Exception ex)
                {
                    NoteViewUnreadable(unreadableById, viewsUnreadable, v,
                        "ViewTemplateId unreadable: " + ex.Message,
                        "The template this view applies is unknown. If it applies one, that template " +
                        "is in view_templates_unused and is NOT unused.");
                }
                try { foreach (var fid in v.GetFilters()) usedFilters.Add(fid.ToString()); }
                catch (Exception ex)
                {
                    NoteViewUnreadable(unreadableById, viewsUnreadable, v,
                        "GetFilters unreadable: " + ex.Message,
                        "The filters this view applies are unknown. Any of them may be sitting in " +
                        "filters_unused while this view uses it.");
                }
            }
            // A template can also be applied BY another template's owner, and filters
            // are referenced from templates too — templates are Views, so the loop
            // above already walked them for GetFilters(). Deliberate.
            foreach (var t in templates)
            {
                try { foreach (var fid in t.GetFilters()) usedFilters.Add(fid.ToString()); }
                catch (Exception ex)
                {
                    NoteViewUnreadable(unreadableById, viewsUnreadable, t,
                        "GetFilters unreadable on a view template: " + ex.Message,
                        "The filters this template applies are unknown. Any of them may be sitting " +
                        "in filters_unused while this template uses it.");
                }
            }

            o["views_unreadable"] = paging.Bucket(viewsUnreadable, "cleanliness", "views_unreadable");
            o["views_unreadable_contract"] = "One entry per VIEW, never per failed read: `total` is the number of " +
                                             "views we could not fully interrogate, and a view whose template AND " +
                                             "filters both threw carries both messages in its `errors` array rather " +
                                             "than appearing twice.";

            var unusedTpl = templates
                .Where(t => !usedTemplates.Contains(t.Id.ToString()))
                .Select(t => (JToken)new JObject { ["id"] = t.Id.ToString(), ["name"] = SafeName(t) })
                .ToList();
            o["view_templates_total"] = templates.Count;
            o["view_templates_unused"] = paging.Bucket(unusedTpl, "cleanliness", "view_templates_unused");
            o["view_templates_unused_note"] = UnusedNote(viewsUnreadable.Count, "view template");

            // ---- Unused view filters. ----
            var allFilters = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).ToList();
            var unusedFilters = allFilters
                .Where(f => !usedFilters.Contains(f.Id.ToString()))
                .Select(f => (JToken)new JObject { ["id"] = f.Id.ToString(), ["name"] = SafeName(f) })
                .ToList();
            o["filters_total"] = allFilters.Count;
            o["filters_unused"] = paging.Bucket(unusedFilters, "cleanliness", "filters_unused");
            o["filters_unused_note"] = UnusedNote(viewsUnreadable.Count, "filter");

            // ---- Orphan group types: full geometry in the file, in no view. ----
            var placedGroupTypes = new HashSet<string>(StringComparer.Ordinal);
            // A placed group whose GetTypeId() throws leaves its GroupType out of this
            // set, and group_types_orphan is a list people delete from. The group IS in
            // the building; only our read of it failed.
            var groupsUnreadable = new List<JToken>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Group))
                         .WhereElementIsNotElementType().Cast<Group>())
            {
                try { placedGroupTypes.Add(g.GetTypeId().ToString()); }
                catch (Exception ex)
                {
                    groupsUnreadable.Add(new JObject
                    {
                        ["id"] = g.Id.ToString(),
                        ["error"] = "GetTypeId unreadable: " + ex.Message,
                        ["consequence"] = "This group instance is PLACED but we cannot say which group type it uses. " +
                                          "That type may be listed in group_types_orphan; deleting it would delete " +
                                          "geometry that is in the building."
                    });
                }
            }
            var groupTypes = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
            var orphanGroups = groupTypes
                .Where(gt => !placedGroupTypes.Contains(gt.Id.ToString()))
                .Select(gt => (JToken)new JObject
                {
                    ["id"] = gt.Id.ToString(),
                    ["name"] = SafeName(gt),
                    ["category"] = CategoryOf(gt)
                })
                .ToList();
            o["group_types_total"] = groupTypes.Count;
            o["group_types_orphan"] = paging.Bucket(orphanGroups, "cleanliness", "group_types_orphan");
            o["group_instances_type_unreadable"] = paging.Bucket(groupsUnreadable, "cleanliness", "group_instances_type_unreadable");
            o["group_types_orphan_note"] = orphanGroups.Count == 0
                ? null
                : groupsUnreadable.Count > 0
                    ? orphanGroups.Count + " group type(s) are listed here because no placed instance pointed at " +
                      "them — but " + groupsUnreadable.Count + " placed group instance(s) would not report their " +
                      "type (see group_instances_type_unreadable), so any of those types could be one of these. " +
                      "This is an UPPER BOUND, not a delete list: deleting off it can delete geometry that is " +
                      "placed in the building. Resolve the unreadable instances first."
                    : orphanGroups.Count + " group type(s) have ZERO placed instances. They carry their full geometry " +
                      "in the file and appear in no view. Listing group INSTANCES never finds them, which is why a " +
                      "model can be inexplicably large with nothing visibly wrong.";

            // ---- Stray model lines. ----
            o["stray_lines_ost_lines"] = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Lines).WhereElementIsNotElementType().GetElementCount();
            o["scope_boxes"] = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().GetElementCount();
            o["materials"] = new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount();

            // ---- In-place families. ----
            var inPlace = new List<JToken>();
            int loadable = 0, famUnreadable = 0;
            foreach (var fam in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
            {
                bool ip;
                try { ip = fam.IsInPlace; } catch { famUnreadable++; continue; }
                if (!ip) { loadable++; continue; }
                inPlace.Add(new JObject
                {
                    ["id"] = fam.Id.ToString(),
                    ["name"] = SafeName(fam),
                    ["category"] = TryStr(() => fam.FamilyCategory?.Name)
                });
            }
            o["families_loadable"] = loadable;
            o["families_unreadable"] = famUnreadable;
            o["families_in_place"] = paging.Bucket(inPlace, "cleanliness", "families_in_place");

            // ---- populations nothing here used to count ----------------------------
            //
            // The weight attribution asks about images, point clouds, nested groups and
            // MEP content with no system. None of them was extracted anywhere, so the
            // attribution reported them not_assessable - correct, and useless. They are
            // counted HERE rather than in a second collector of their own, because a
            // second place that counts the same population is how two answers about one
            // model start to disagree.
            //
            // Every count is guarded separately. A category that throws leaves its own
            // number null and says so; it does not take the section down and it does not
            // read as zero.
            Count(o, "raster_images", () => new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RasterImages)
                .WhereElementIsNotElementType().GetElementCount());

            Count(o, "point_clouds", () => new FilteredElementCollector(doc)
                .OfClass(typeof(PointCloudInstance)).GetElementCount());

            // A group that contains another group. Revit will not tell you directly, so
            // the members are read - and a group whose members cannot be read is counted
            // as unreadable rather than as "not nested".
            int nested = 0, groupsUnread = 0;
            try
            {
                foreach (Group g in new FilteredElementCollector(doc)
                             .OfClass(typeof(Group)).WhereElementIsNotElementType().Cast<Group>())
                {
                    try
                    {
                        if (g.GetMemberIds().Any(id => doc.GetElement(id) is Group)) nested++;
                    }
                    catch { groupsUnread++; }
                }
                o["group_instances_nested"] = nested;
                o["group_instances_membership_unreadable"] = groupsUnread;
            }
            catch (Exception ex)
            {
                o["group_instances_nested"] = null;
                o["group_instances_nested_error"] = ex.Message;
            }

            // MEP content carrying no system. Read off the element, never inferred from
            // its category: a duct with no system and a duct this failed to read are
            // different answers and are counted apart.
            int mepNoSystem = 0, mepUnread = 0, mepSeen = 0;
            try
            {
                foreach (MEPCurve c in new FilteredElementCollector(doc)
                             .OfClass(typeof(MEPCurve)).WhereElementIsNotElementType().Cast<MEPCurve>())
                {
                    mepSeen++;
                    try { if (c.MEPSystem == null) mepNoSystem++; }
                    catch { mepUnread++; }
                }
                o["mep_curves_examined"] = mepSeen;
                o["mep_curves_without_system"] = mepNoSystem;
                o["mep_curves_system_unreadable"] = mepUnread;
            }
            catch (Exception ex)
            {
                o["mep_curves_without_system"] = null;
                o["mep_curves_error"] = ex.Message;
            }

            return o;
        }

        /// <summary>
        /// One guarded count. A collector that throws leaves a null and an error beside
        /// it - never a zero, which would read as "there are none of those".
        /// </summary>
        private static void Count(JObject into, string key, Func<int> read)
        {
            try { into[key] = read(); }
            catch (Exception ex)
            {
                into[key] = null;
                into[key + "_error"] = ex.Message;
            }
        }

        /// <summary>
        /// Record a failed read against the VIEW it happened on, folding a second
        /// failure on the same view into the existing entry. Two reads are attempted
        /// per view and neither aborts the other, so appending per failure would count
        /// one view twice — in this bucket's `total` and in UnusedNote's "N view(s)".
        /// `order` keeps insertion order so that a truncated return is stable and
        /// Dictionary enumeration order never leaks into the response.
        /// </summary>
        private static void NoteViewUnreadable(Dictionary<string, JObject> byId, List<JToken> order,
                                               Element v, string error, string consequence)
        {
            var id = v.Id.ToString();
            JObject rec;
            if (!byId.TryGetValue(id, out rec))
            {
                rec = new JObject
                {
                    ["id"] = id,
                    ["errors"] = new JArray(),
                    ["consequences"] = new JArray()
                };
                byId[id] = rec;
                order.Add(rec);
            }
            ((JArray)rec["errors"]).Add(error);
            ((JArray)rec["consequences"]).Add(consequence);
        }

        /// <summary>
        /// "Unused" here means "no view told us it uses this". One view we could not
        /// read turns that into "no view we could ask told us", which is a different
        /// sentence and the difference is somebody's deleted template. types_no_instances
        /// already says out loud that it is an upper bound; these two lists never did,
        /// so they read as verified-dead and got purged.
        /// </summary>
        private static string UnusedNote(int viewsUnreadable, string what)
        {
            if (viewsUnreadable == 0)
                return "Derived from what every view reports it applies. Every view was read, so this list is as " +
                       "good as the model's own answer — still confirm before deleting.";
            return viewsUnreadable + " view(s) would not report the templates/filters they apply (see " +
                   "views_unreadable). Anything they use is listed here as unused. This " + what + " list is an " +
                   "UPPER BOUND, NOT a delete list: it contains an unknown number of live " + what + "s.";
        }

        // ================================ naming ==================================
        // RAW NAMES ONLY. This section decides nothing.
        //
        // The python it replaces hand-rolled ^[A-Z]{1,4}-\d{2,3}[A-Za-z]?$ out of
        // split()/isalpha()/isdigit() ONLY because IronPython inside Revit has no
        // stdlib and therefore no `re`. That constraint is gone: the host has
        // stdlib. Re-implementing the standard here would fork it into a second
        // place that drifts, and — worse — would make a name we could not READ
        // (ename() returning "?") come back as a name that FAILED, which is a
        // different fact entirely. So: names out, verdicts nowhere.
        /// <summary>
        /// The names of every class NamingProfileRules can judge.
        ///
        /// EACH COLLECTOR IS GUARDED ON ITS OWN. A document that will not enumerate
        /// its MEP systems still has levels, and one throw taking out the lot would
        /// report thirteen classes as uncollected because of the fourteenth. A
        /// class whose collector threw is left OUT of the dictionary, so the route
        /// reports it not_collected - never as an empty pass - and the reason is
        /// published beside it.
        /// </summary>
        private static Dictionary<string, List<NamedThing>> NamingPopulations(
            Document doc, out List<NamingNotApplicable> notApplicable, out JObject collectorErrors)
        {
            var pops = new Dictionary<string, List<NamedThing>>(StringComparer.Ordinal);
            notApplicable = new List<NamingNotApplicable>();
            collectorErrors = new JObject();
            JObject errors = collectorErrors;

            Action<string, Func<List<NamedThing>>> collect = (cls, read) =>
            {
                try { pops[cls] = read(); }
                catch (Exception ex) { errors[cls] = ex.Message; }
            };

            Func<IEnumerable<Element>, List<NamedThing>> named = els =>
            {
                var list = new List<NamedThing>();
                foreach (Element e in els)
                {
                    bool bad;
                    string nm = SafeName(e, out bad);
                    list.Add(new NamedThing { Id = e.Id.ToString(), Name = nm, Readable = !bad });
                }
                return list;
            };

            collect("levels", () => named(new FilteredElementCollector(doc).OfClass(typeof(Level))));
            collect("grids", () => named(new FilteredElementCollector(doc).OfClass(typeof(Grid))));

            // A ViewSheet IS a View. Left in, every sheet would be judged twice -
            // once against the view rule it was never meant to satisfy.
            collect("views", () => named(new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>().Where(v => !v.IsTemplate && !(v is ViewSheet)).Cast<Element>()));
            collect("view_templates", () => named(new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>().Where(v => v.IsTemplate).Cast<Element>()));
            collect("sheets", () => named(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))));

            collect("families", () => named(new FilteredElementCollector(doc).OfClass(typeof(Family))));
            collect("types", () => named(new FilteredElementCollector(doc).WhereElementIsElementType()));
            collect("links", () => named(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType))));
            collect("groups", () => named(new FilteredElementCollector(doc).OfClass(typeof(GroupType))));
            collect("rooms", () => named(new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()));
            collect("spaces", () => named(new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType()));
            collect("filters", () => named(new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))));

            // MEPSystem is abstract, so it cannot be given to OfClass. The three
            // concrete kinds are collected instead and reported as one class.
            collect("systems", () =>
            {
                var all = new List<Element>();
                all.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.Mechanical.MechanicalSystem)));
                all.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipingSystem)));
                all.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.Electrical.ElectricalSystem)));
                return named(all);
            });

            // WORKSETS: absent is not empty. A document that was never workshared
            // has no user worksets, and judging that emptiness "ok" is the
            // non-workshared-document-as-clean-result confusion this bridge refuses
            // to make anywhere else either.
            bool? workshared = null;
            try { workshared = doc.IsWorkshared; }
            catch (Exception ex) { errors["worksets"] = "IsWorkshared failed: " + ex.Message; }

            if (workshared == false)
            {
                notApplicable.Add(new NamingNotApplicable
                {
                    Class = "worksets",
                    Reason = "this document is not workshared, so it has no user worksets. That is an ABSENCE, " +
                             "not an empty set that passed a rule."
                });
            }
            else if (workshared == true)
            {
                collect("worksets", () =>
                {
                    var list = new List<NamedThing>();
                    foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    {
                        string nm = null;
                        bool bad = false;
                        try { nm = w.Name; } catch { bad = true; }
                        list.Add(new NamedThing { Id = w.Id.ToString(), Name = nm, Readable = !bad });
                    }
                    return list;
                });
            }

            return pops;
        }

        private static JObject NamingSection(Document doc, ScanPagingContext paging,
                                             NamingProfile namingProfile)
        {
            List<NamingNotApplicable> namingNotApplicable;
            JObject namingErrors;
            Dictionary<string, List<NamedThing>> namingPops =
                NamingPopulations(doc, out namingNotApplicable, out namingErrors);

            var views = new List<JToken>();
            int viewUnreadable = 0;
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                bool isTpl;
                string vt;
                try { isTpl = v.IsTemplate; vt = v.ViewType.ToString(); }
                catch { viewUnreadable++; continue; }
                bool bad;
                var nm = SafeName(v, out bad);
                views.Add(new JObject
                {
                    ["id"] = v.Id.ToString(),
                    ["name"] = nm,
                    ["name_unreadable"] = bad,
                    ["view_type"] = vt,
                    ["is_template"] = isTpl
                });
            }

            var sheets = new List<JToken>();
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                bool bad;
                var nm = SafeName(s, out bad);
                bool numBad = false;
                string num = null;
                try { num = s.SheetNumber; } catch { numBad = true; }
                sheets.Add(new JObject
                {
                    ["id"] = s.Id.ToString(),
                    ["sheet_number"] = num,
                    ["sheet_number_unreadable"] = numBad,
                    ["name"] = nm,
                    ["name_unreadable"] = bad
                });
            }

            var levels = new List<JToken>();
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                bool bad;
                var nm = SafeName(l, out bad);
                levels.Add(new JObject
                {
                    ["id"] = l.Id.ToString(),
                    ["name"] = nm,
                    ["name_unreadable"] = bad,
                    ["elevation_m"] = TryDouble(() => Guard.ToM(l.Elevation))
                });
            }

            var grids = new List<JToken>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                bool bad;
                var nm = SafeName(g, out bad);
                grids.Add(new JObject { ["id"] = g.Id.ToString(), ["name"] = nm, ["name_unreadable"] = bad });
            }

            return new JObject
            {
                ["contract"] = "Raw names, never judged. Validate them host-side against ONE standard with a real " +
                               "regex. A name with name_unreadable=true was NOT read — it is not a name that failed.",
                ["views_name_unreadable_skipped"] = viewUnreadable,
                ["views"] = paging.Bucket(views, "naming", "views"),
                ["sheets"] = paging.Bucket(sheets, "naming", "sheets"),
                ["levels"] = paging.Bucket(levels, "naming", "levels"),
                ["grids"] = paging.Bucket(grids, "naming", "grids"),
                // The census above is what the model is called. THIS is whether
                // those names satisfy a rule somebody actually wrote - and every
                // class the profile can mention is accounted for, including the
                // ones nothing collected.
                ["verdicts"] = NamingFromScan.Judge(namingPops, namingNotApplicable, namingProfile),
                ["collector_errors"] = namingErrors.HasValues ? namingErrors : null
            };
        }

        // ============================= documentation ==============================
        private static JObject DocumentationSection(Document doc, ScanPagingContext paging)
        {
            // GetAllPlacedViews() and not the Viewport collector: schedules and
            // revision schedules are placed on sheets WITHOUT a Viewport, so a
            // Viewport-only scan reports every schedule on every sheet as "not
            // placed" and the list is noise the reader learns to ignore.
            var unreadable = new List<JToken>();
            var placed = new HashSet<string>(StringComparer.Ordinal);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            int sheetsPlacementUnreadable = 0;
            foreach (var s in sheets)
            {
                try { foreach (var vid in s.GetAllPlacedViews()) placed.Add(vid.ToString()); }
                catch (Exception ex)
                {
                    // `placed` is the ONLY evidence a view is on a sheet. A sheet we
                    // could not open up donates none of its views to it, so every view
                    // it carries lands in views_not_on_sheet as if it were off-sheet.
                    sheetsPlacementUnreadable++;
                    unreadable.Add(new JObject
                    {
                        ["id"] = s.Id.ToString(),
                        ["sheet_number"] = TryStr(() => s.SheetNumber),
                        ["error"] = "GetAllPlacedViews unreadable: " + ex.Message,
                        ["consequence"] = "The views placed on this sheet are unknown. Each of them is now listed in " +
                                          "views_not_on_sheet even though it IS placed."
                    });
                }
            }

            var noTemplate = new List<JToken>();
            var notOnSheet = new List<JToken>();
            // Both counters gate the notes below. Without them views_no_template is a
            // silent LOWER bound: a view we could not classify at all, and a view whose
            // ViewTemplateId threw, are both absent from it — and absent reads exactly
            // like "this view has a template". The cleanliness section already refuses
            // to let an unreadable view pass unremarked (UnusedNote); this section runs
            // the same two reads and had a caveat only for views_not_on_sheet.
            int viewsClassifyUnreadable = 0;   // in NEITHER list: we never got past IsTemplate/ViewType
            int viewTemplateUnreadable = 0;    // in the model, template state unknown

            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                bool isTpl;
                ViewType vt;
                try { isTpl = v.IsTemplate; vt = v.ViewType; }
                catch (Exception ex)
                {
                    viewsClassifyUnreadable++;
                    unreadable.Add(new JObject
                    {
                        ["id"] = v.Id.ToString(),
                        ["error"] = ex.Message,
                        ["consequence"] = "This view could not be classified, so it is in NEITHER views_no_template " +
                                          "NOR views_not_on_sheet. Its absence from both is not a pass."
                    });
                    continue;
                }
                if (isTpl) continue;
                if (vt == ViewType.ProjectBrowser || vt == ViewType.SystemBrowser ||
                    vt == ViewType.Internal || vt == ViewType.Undefined || vt == ViewType.DrawingSheet)
                    continue;

                var rec = new JObject
                {
                    ["id"] = v.Id.ToString(),
                    ["name"] = SafeName(v),
                    ["view_type"] = vt.ToString()
                };

                try
                {
                    var t = v.ViewTemplateId;
                    // Returned, not discarded. s_views computed exactly this list and
                    // dropped it on the floor, leaving the correction step with no ids.
                    if (t == null || t == ElementId.InvalidElementId) noTemplate.Add(rec.DeepClone());
                }
                catch (Exception ex)
                {
                    // Not counted as 'no template': we do not know whether it has one.
                    // Counted HERE though, because "we could not look" that nothing
                    // reports is indistinguishable from "we looked and it was fine".
                    viewTemplateUnreadable++;
                    unreadable.Add(new JObject
                    {
                        ["id"] = v.Id.ToString(),
                        ["error"] = "ViewTemplateId unreadable: " + ex.Message,
                        ["consequence"] = "Not counted as 'no template'. We could not look."
                    });
                }

                if (!placed.Contains(v.Id.ToString())) notOnSheet.Add(rec.DeepClone());
            }

            var missingTb = new List<JToken>();
            // The failure had no counter at all: it landed only in `unreadable`, which is
            // emitted through paging.Bucket() under this section's own budget. The
            // GetAllPlacedViews failures share that list, so titleblock failures could be
            // truncated out of the response entirely — a sheet nobody scanned, absent from
            // sheets_missing_titleblock, in a section still reporting status "ok". A count
            // survives truncation; an error record does not.
            int sheetsTitleblockUnreadable = 0;
            foreach (var s in sheets)
            {
                try
                {
                    int n = new FilteredElementCollector(doc, s.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsNotElementType()
                        .GetElementCount();
                    if (n == 0)
                        missingTb.Add(new JObject
                        {
                            ["id"] = s.Id.ToString(),
                            ["sheet_number"] = TryStr(() => s.SheetNumber),
                            ["name"] = SafeName(s)
                        });
                }
                catch (Exception ex)
                {
                    sheetsTitleblockUnreadable++;
                    unreadable.Add(new JObject
                    {
                        ["id"] = s.Id.ToString(),
                        ["error"] = "Titleblock scan failed: " + ex.Message,
                        ["consequence"] = "This sheet is NOT in sheets_missing_titleblock. We never counted its " +
                                          "titleblocks, so its absence from that list is not a pass."
                    });
                }
            }

            return new JObject
            {
                ["sheets_total"] = sheets.Count,
                ["sheets_placement_unreadable"] = sheetsPlacementUnreadable,
                ["views_classify_unreadable"] = viewsClassifyUnreadable,
                ["view_template_unreadable"] = viewTemplateUnreadable,
                ["sheets_titleblock_unreadable"] = sheetsTitleblockUnreadable,
                ["views_no_template"] = paging.Bucket(noTemplate, "documentation", "views_no_template"),
                ["views_no_template_note"] = NoTemplateNote(viewTemplateUnreadable, viewsClassifyUnreadable),
                ["views_not_on_sheet"] = paging.Bucket(notOnSheet, "documentation", "views_not_on_sheet"),
                ["views_not_on_sheet_note"] = NotOnSheetNote(sheetsPlacementUnreadable, viewsClassifyUnreadable),
                ["sheets_missing_titleblock"] = paging.Bucket(missingTb, "documentation", "sheets_missing_titleblock"),
                ["sheets_missing_titleblock_note"] = MissingTitleblockNote(sheetsTitleblockUnreadable),
                // Error records, one per failed READ, over a mix of views and sheets —
                // a sheet that failed both GetAllPlacedViews and the titleblock scan is
                // in here twice. Nothing derives a count of things from it; the counters
                // above are what the notes are gated on, and each is per element.
                ["unreadable_contract"] = "A list of failed reads, not of elements: one element can appear more " +
                                          "than once and views and sheets are mixed. It is capped by `top`, so it " +
                                          "is not a census of the failures — use views_classify_unreadable, " +
                                          "view_template_unreadable, sheets_placement_unreadable and " +
                                          "sheets_titleblock_unreadable for counts.",
                ["unreadable"] = paging.Bucket(unreadable, "documentation", "unreadable"),
                ["note"] = "views_not_on_sheet is a review list, not a defect list — working views legitimately " +
                           "live off-sheet. Placement is read from ViewSheet.GetAllPlacedViews(), so schedules " +
                           "count as placed."
            };
        }

        /// <summary>
        /// sheets_missing_titleblock had neither a note nor a counter while both its
        /// neighbours had one, and it fails the same way they do: a sheet whose
        /// titleblock scan threw is absent from the list, and absent reads as "this
        /// sheet has a titleblock". The correction step works off this list, so an
        /// unscanned sheet would otherwise be left untouched AND unmentioned.
        /// </summary>
        private static string MissingTitleblockNote(int titleblockUnreadable)
        {
            if (titleblockUnreadable == 0)
                return "Every sheet was scanned for titleblocks, so this list is the model's own answer.";
            return titleblockUnreadable + " sheet(s) could not be scanned for titleblocks (see unreadable, which " +
                   "is itself capped by `top` — this count is not). None of them are in this list, so it is a " +
                   "LOWER BOUND: there may be further sheets with no titleblock. Do not read this count as the total.";
        }

        /// <summary>
        /// views_no_template is built by ASKING each view. Every view we could not ask
        /// is missing from it, and a view missing from a defect list reads as a view
        /// that passed. So the list is a LOWER bound whenever either read failed, and
        /// this says so — the correction step assigns templates off it and would
        /// otherwise leave the unreadable ones untouched and unmentioned.
        /// </summary>
        private static string NoTemplateNote(int templateUnreadable, int classifyUnreadable)
        {
            int blind = templateUnreadable + classifyUnreadable;
            if (blind == 0)
                return "Every view reported whether it applies a template, so this list is the model's own answer.";
            return blind + " view(s) never reported their template state (" + templateUnreadable +
                   " whose ViewTemplateId threw, " + classifyUnreadable + " that could not be classified at all — " +
                   "see unreadable). None of them are in this list, so it is a LOWER BOUND: there are an unknown " +
                   "number of further views with no template. Do not read this count as the total.";
        }

        /// <summary>
        /// views_not_on_sheet is wrong in BOTH directions at once, from two different
        /// failures, so one sentence about sheets was never the whole story: an
        /// unreadable sheet INFLATES the list (its placed views look off-sheet), and an
        /// unclassifiable view DEFLATES it (it is in no list at all). Reporting only the
        /// first would let a reader treat the list as a superset it is not.
        /// </summary>
        private static string NotOnSheetNote(int sheetsPlacementUnreadable, int classifyUnreadable)
        {
            if (sheetsPlacementUnreadable == 0 && classifyUnreadable == 0) return null;
            var parts = new List<string>();
            if (sheetsPlacementUnreadable > 0)
                parts.Add(sheetsPlacementUnreadable + " sheet(s) would not report their placed views, so this list " +
                          "is INFLATED by everything placed on them: a view in it is not known to be off-sheet");
            if (classifyUnreadable > 0)
                parts.Add(classifyUnreadable + " view(s) could not be classified, so they are absent from this list " +
                          "whether or not they are on a sheet: it is also INCOMPLETE");
            return string.Join("; ", parts.ToArray()) + " (see unreadable). This count is not the model's number.";
        }

        // ============================= project_info ===============================
        // RAW VALUES. Whether "Enter address here" is a placeholder is the caller's
        // comparison to make against the caller's list; baking that list in here
        // would put the client's standard in a compiled DLL. Three states are kept
        // apart on purpose: absent (no such parameter), empty (present, ""), and
        // unreadable (the read threw) — the python collapsed all three into "".
        private static JObject ProjectInfoSection(Document doc)
        {
            var pi = doc.ProjectInformation;
            if (pi == null) throw new InvalidOperationException("Document has no ProjectInformation element.");

            var fields = new JObject();
            AddInfo(fields, pi, "name", BuiltInParameter.PROJECT_NAME);
            AddInfo(fields, pi, "number", BuiltInParameter.PROJECT_NUMBER);
            AddInfo(fields, pi, "status", BuiltInParameter.PROJECT_STATUS);
            AddInfo(fields, pi, "address", BuiltInParameter.PROJECT_ADDRESS);
            AddInfo(fields, pi, "client_name", BuiltInParameter.CLIENT_NAME);
            AddInfo(fields, pi, "building_name", BuiltInParameter.PROJECT_BUILDING_NAME);
            AddInfo(fields, pi, "author", BuiltInParameter.PROJECT_AUTHOR);
            AddInfo(fields, pi, "issue_date", BuiltInParameter.PROJECT_ISSUE_DATE);
            AddInfo(fields, pi, "organization_name", BuiltInParameter.PROJECT_ORGANIZATION_NAME);
            AddInfo(fields, pi, "organization_description", BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION);

            return new JObject
            {
                ["element_id"] = pi.Id.ToString(),
                ["contract"] = "Raw values. 'present' false means the parameter does not exist; 'readable' false " +
                               "means the read threw and the value is unknown — neither is an empty string. " +
                               "Compare against your own placeholder list host-side.",
                ["fields"] = fields
            };
        }

        private static void AddInfo(JObject into, Element pi, string key, BuiltInParameter bip)
        {
            var o = new JObject();
            Parameter p = null;
            try { p = pi.get_Parameter(bip); }
            catch (Exception ex)
            {
                o["present"] = null;
                o["readable"] = false;
                o["value"] = null;
                o["error"] = ex.Message;
                into[key] = o;
                return;
            }

            if (p == null)
            {
                o["present"] = false;
                o["readable"] = true;
                o["value"] = null;
                into[key] = o;
                return;
            }

            o["present"] = true;
            try
            {
                var v = p.AsString();
                if (v == null) v = p.AsValueString();
                o["readable"] = true;
                o["value"] = v;
                o["is_empty"] = string.IsNullOrEmpty(v);
            }
            catch (Exception ex)
            {
                o["readable"] = false;
                o["value"] = null;
                o["error"] = ex.Message;
            }
            into[key] = o;
        }

        // ================================ health ==================================
        private static JObject HealthSection(Document doc, ScanPagingContext paging, WarningProfile warningProfile)
        {
            var o = new JObject();

            // ---- Warnings, keyed on FailureDefinitionId and NOT on the localized
            // description text. Grouping by the text splits one warning across two
            // session languages and merges two different warnings that read alike;
            // see Core/WarningRules for the whole argument. ----
            var all = doc.GetWarnings();
            var facts = new List<WarningFact>();
            foreach (var w in all)
            {
                var f = new WarningFact();
                try { f.DefinitionGuid = w.GetFailureDefinitionId().Guid.ToString("D"); }
                catch { f.DefinitionGuid = null; }
                try { f.Description = w.GetDescriptionText(); }
                catch { f.Description = null; }
                if (string.IsNullOrEmpty(f.Description)) f.Description = "(description unreadable)";
                // Null when unreadable, which is not the same as a warning with
                // no severity - Revit has no such thing.
                try { f.Severity = w.GetSeverity().ToString(); }
                catch { f.Severity = null; }
                try
                {
                    foreach (var id in w.GetFailingElements()) f.FailingElementIds.Add(Rid.Value(id));
                }
                catch (Exception ex)
                {
                    // Unlike every other bucket in this file, this one's `total` would
                    // be what the loop managed to collect - not the model's count.
                    // Swallowing here emits total:0 next to a non-zero `occurrences`:
                    // a warning with failing elements we never read, dressed as one
                    // with none.
                    f.IdsReadable = false;
                    f.IdsError = "GetFailingElements failed: " + ex.Message;
                }
                facts.Add(f);
            }

            List<WarningGroup> groups = WarningRules.Group(facts);
            WarningRules.Triage(groups, warningProfile);

            var rows = groups.Select(g =>
            {
                JObject row = WarningRules.ToJson(g);
                var ids = g.FailingElementIds.Select(x => (JToken)x).ToList();

                // The id list gets its own total/returned/truncated: a warning type
                // with 4,000 failing elements must not hand back 50 ids and look
                // like it hands back all of them.
                if (!g.IdsComplete)
                {
                    // No total at all rather than a total we know is short. A number
                    // here would be read as the model's count, which is the one
                    // thing it is not.
                    var shown = ids.Take(paging.LimitFor("health", "failing_elements")).ToList();
                    row["failing_elements"] = new JObject
                    {
                        ["total"] = JValue.CreateNull(),
                        ["total_unknown_reason"] = "At least one occurrence of this warning would not enumerate " +
                                                   "its failing elements (see failing_element_ids_error). How many " +
                                                   "elements this warning touches is UNKNOWN; the ids below are " +
                                                   "only the ones that could be read and are a lower bound, never " +
                                                   "a complete set.",
                        ["returned"] = shown.Count,
                        ["truncated"] = JValue.CreateNull(),
                        ["items"] = new JArray(shown)
                    };
                }
                else
                {
                    row["failing_elements"] = paging.Bucket(ids, "health", "failing_elements");
                }
                return (JToken)row;
            }).ToList();

            o["warnings_total"] = all.Count;
            o["warnings_distinct"] = groups.Count;
            o["warnings_identity"] = WarningRules.IdentityMeans;
            o["warnings_occurrences_means"] = WarningRules.OccurrencesMeans;
            o["warning_profile"] = ProfileStatus(warningProfile);
            o["warnings_by_type"] = paging.Bucket(rows, "health", "warnings_by_type");

            // ---- Rooms. Unreadable is NOT unbounded. ----
            o["rooms"] = SpatialBucket(doc, BuiltInCategory.OST_Rooms, paging, "rooms");
            o["areas"] = SpatialBucket(doc, BuiltInCategory.OST_Areas, paging, "areas");
            return o;
        }


        /// <summary>
        /// What happened to a caller-supplied profile, published rather than
        /// implied. A refused profile that produced an ordinary-looking reply is
        /// the failure this field exists to prevent: the caller believes their
        /// triage ran and every warning came back with Revit's severity instead.
        /// </summary>
        private static JObject ProfileStatus(WarningProfile p)
        {
            if (p == null) return new JObject { ["status"] = "not_requested" };
            if (p.Absent) return new JObject { ["status"] = "not_requested", ["means"] = p.Message };
            if (!p.Ok)
                return new JObject { ["status"] = "refused", ["code"] = p.Code, ["message"] = p.Message };
            return new JObject { ["status"] = "ok", ["version"] = p.Version, ["entries"] = p.ByGuid.Count };
        }

        /// <summary>
        /// Rooms/areas split four ways. Both python engines do `except: area = 0`
        /// and then classify area==0 as unbounded — so a room whose Area could not
        /// be read is reported to the client as a defective room. Unreadable gets
        /// its own bucket here and is never merged into a finding.
        /// </summary>
        private static JObject SpatialBucket(Document doc, BuiltInCategory bic, ScanPagingContext paging, string prefix)
        {
            var unplaced = new List<JToken>();
            var unbounded = new List<JToken>();
            var unreadable = new List<JToken>();
            int total = 0;

            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                total++;
                var se = e as SpatialElement;
                if (se == null)
                {
                    unreadable.Add(new JObject
                    {
                        ["id"] = e.Id.ToString(),
                        ["error"] = "Not a SpatialElement; cannot read placement or area."
                    });
                    continue;
                }

                bool placed;
                try { placed = se.Location != null; }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["id"] = e.Id.ToString(), ["error"] = "Location unreadable: " + ex.Message });
                    continue;
                }

                if (!placed)
                {
                    unplaced.Add(new JObject
                    {
                        ["id"] = e.Id.ToString(),
                        ["name"] = SafeName(e),
                        ["problem"] = "Unplaced: it has no location. It still appears in schedules, with no area."
                    });
                    continue;
                }

                double area;
                try { area = se.Area; }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["id"] = e.Id.ToString(), ["error"] = "Area unreadable: " + ex.Message });
                    continue;
                }

                if (area <= 0.0)
                    unbounded.Add(new JObject
                    {
                        ["id"] = e.Id.ToString(),
                        ["name"] = SafeName(e),
                        ["problem"] = "Not enclosed: area is 0. Its boundary is open, so it measures nothing."
                    });
            }

            return new JObject
            {
                ["total"] = total,
                ["unplaced"] = paging.Bucket(unplaced, "health", prefix + ".unplaced"),
                ["unbounded"] = paging.Bucket(unbounded, "health", prefix + ".unbounded"),
                ["unreadable"] = paging.Bucket(unreadable, "health", prefix + ".unreadable"),
                ["note"] = "unreadable is separate from unbounded on purpose: a read that threw is not a defect " +
                           "we found, it is a defect we cannot rule out. Unplaced and unbounded both understate " +
                           "any area takeoff taken from this model."
            };
        }

        // ================================= links ==================================
        private static JObject LinksSection(Document doc, ScanPagingContext paging)
        {
            var rows = new List<JToken>();
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var rec = new JObject
                {
                    ["instance_id"] = li.Id.ToString(),
                    ["name"] = SafeName(li),
                    ["pinned"] = TryBool(() => li.Pinned)
                };

                string status = null, pathType = null, path = null;
                string statusError = null, pathError = null;
                try
                {
                    var lt = doc.GetElement(li.GetTypeId()) as RevitLinkType;
                    if (lt == null)
                    {
                        statusError = "Link type could not be resolved.";
                    }
                    else
                    {
                        rec["type_id"] = lt.Id.ToString();

                        // Status is asked of the TYPE, which answers for a cloud-hosted link
                        // too. It used to be read through GetExternalFileReference(), which
                        // THROWS on a cloud link ("This Element does not represent an external
                        // file") - so on an Autodesk Docs model every link's status came back
                        // unreadable, and the tally then called them not loaded.
                        try { status = lt.GetLinkedFileStatus().ToString(); }
                        catch (Exception ex) { statusError = "GetLinkedFileStatus unreadable: " + ex.Message; }

                        // Path and path type are a SEPARATE question. A cloud link genuinely
                        // has no external file path, and that absence says nothing about
                        // whether the link is loaded - so it gets its own field and never
                        // contaminates the status.
                        try
                        {
                            var efr = lt.GetExternalFileReference();
                            if (efr == null)
                                pathError = "No external file reference. Normal for a cloud-hosted link; " +
                                            "it does not mean the link is missing.";
                            else
                            {
                                pathType = efr.PathType.ToString();
                                try { path = ModelPathUtils.ConvertModelPathToUserVisiblePath(efr.GetAbsolutePath()); }
                                catch (Exception ex) { pathError = "Path unreadable: " + ex.Message; }
                            }
                        }
                        catch (Exception ex)
                        {
                            pathError = "External file reference unreadable: " + ex.Message +
                                        " (expected for a cloud-hosted link).";
                        }
                    }
                }
                catch (Exception ex) { statusError = ex.Message; }

                rec["load_status"] = status;
                rec["path_type"] = pathType;
                rec["path"] = path;
                rec["path_error"] = pathError;
                // null status + explicit error, never a defaulted "not loaded". A link
                // we could not interrogate is not a link we know is broken.
                rec["status_unreadable"] = statusError != null;
                rec["status_error"] = statusError;
                rows.Add(rec);
            }

            int unpinned = rows.Count(r => r["pinned"] != null && r["pinned"].Type == JTokenType.Boolean && !(bool)r["pinned"]);

            // The tally is computed from the status STRINGS, in one shared place, because
            // this line used to read `r["load_status"] != null` - and a JSON null is a
            // JValue, not a C# null, so every link we could not interrogate was counted
            // as NOT LOADED. See Core/LinkStatusTally.cs. An unread status is UNKNOWN.
            LinkTally tally = LinkStatusTally.Of(rows.Select(r =>
                r["load_status"] != null && r["load_status"].Type != JTokenType.Null
                    ? (string)r["load_status"]
                    : null));

            var cadUnpinned = new List<JToken>();
            // This runs the SAME imp.IsLinked read that CleanlinessSection hardens into
            // cad_unreadable, and it used to swallow it with a bare catch{}. That made
            // total:0 mean either "every CAD link is pinned" or "the read threw on every
            // single one of them", with nothing in the response able to tell them apart:
            // an empty defect list reads as a pass. The failures get their own bucket and
            // gate the note below, exactly as the sibling site does.
            var cadPinUnreadable = new List<JToken>();
            foreach (var i in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                try
                {
                    if (i.IsLinked && !i.Pinned)
                        cadUnpinned.Add(new JObject { ["id"] = i.Id.ToString(), ["type_name"] = TypeNameOf(doc, i) });
                }
                catch (Exception ex)
                {
                    cadPinUnreadable.Add(new JObject
                    {
                        ["id"] = i.Id.ToString(),
                        ["type_name"] = TryStr(() => TypeNameOf(doc, i)),
                        ["error"] = "IsLinked/Pinned unreadable: " + ex.Message,
                        ["consequence"] = "Neither read succeeded, so this one is NOT in cad_links_unpinned. " +
                                          "Its absence from that list is not a pass."
                    });
                }
            }

            return new JObject
            {
                ["rvt_link_instances"] = rows.Count,
                ["rvt_link_types"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).GetElementCount(),
                ["rvt_links_unpinned"] = unpinned,
                ["rvt_links_loaded"] = tally.Loaded,
                ["rvt_links_not_loaded"] = tally.NotLoaded,
                ["rvt_links_status_unknown"] = tally.Unknown,
                // Says out loud whether the split above describes every link or only the
                // ones that answered. loaded + not_loaded + status_unknown == instances.
                ["rvt_links_coverage_complete"] = tally.Complete,
                ["rvt_links"] = paging.Bucket(rows, "links", "rvt_links"),
                ["cad_links_unpinned"] = paging.Bucket(cadUnpinned, "links", "cad_links_unpinned"),
                ["cad_pin_unreadable"] = paging.Bucket(cadPinUnreadable, "links", "cad_pin_unreadable"),
                ["cad_links_unpinned_note"] = CadUnpinnedNote(cadPinUnreadable.Count),
                // INSTANCES, not types - audit_model counts the other one, and on a model
                // with the same link loaded several times the two disagree while both are
                // right. The unit is in the sentence so the reader does not supply one.
                ["note"] = tally.Complete && tally.NotLoaded == 0 ? null : tally.Summary("link instance")
            };
        }

        /// <summary>
        /// cad_links_unpinned is built by ASKING each ImportInstance whether it is a
        /// link and whether it is pinned. Every one that refused to answer is missing
        /// from the list, and a missing element reads as a pinned one. So the list is a
        /// LOWER BOUND whenever the read failed, and this says so rather than letting
        /// total:0 be read as "all CAD links are pinned".
        /// </summary>
        private static string CadUnpinnedNote(int pinUnreadable)
        {
            if (pinUnreadable == 0)
                return "Every CAD instance reported its link and pin state, so this list is the model's own answer.";
            return pinUnreadable + " CAD instance(s) never reported their link/pin state (see cad_pin_unreadable). " +
                   "None of them are in this list, so it is a LOWER BOUND: there may be further unpinned CAD links " +
                   "we could not see. Do not read this count as the total.";
        }

        // =============================== worksets =================================
        private static JObject WorksetsSection(Document doc, ScanPagingContext paging)
        {
            bool ws;
            try { ws = doc.IsWorkshared; }
            catch (Exception ex) { throw new InvalidOperationException("IsWorkshared unreadable: " + ex.Message); }

            if (!ws)
                return new JObject
                {
                    ["is_workshared"] = false,
                    ["note"] = "Model is not workshared, so there are no user worksets. This is a fact about the " +
                               "model, not a failed check."
                };

            var names = new Dictionary<int, string>();
            var counts = new Dictionary<int, int>();
            var isOpen = new Dictionary<int, bool>();
            // The ids, so a finding can be navigated to. A count with no ids tells a
            // modeller they have 400 elements in the wrong place and not one place
            // to start.
            var idsByWorkset = new Dictionary<int, List<JToken>>();
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                names[w.Id.IntegerValue] = w.Name;
                counts[w.Id.IntegerValue] = 0;
                idsByWorkset[w.Id.IntegerValue] = new List<JToken>();
                // A CLOSED workset's elements are not in the document, so its `elements`
                // count below is 0 for a reason that has nothing to do with the model.
                // Reporting the count without the state is how "this workset is empty"
                // gets read off a workset nobody loaded.
                isOpen[w.Id.IntegerValue] = w.IsOpen;
            }

            // Two increments that used to share one counter, meaning two different
            // things: "the model told us this element sits outside the user worksets"
            // and "the WorksetId read threw, so we know nothing about it". Merging them
            // reports "I could not look" as "this element is outside the user worksets"
            // — defect #3 from this file's header, reproduced. They stay apart.
            int outsideUserWorksets = 0;   // read SUCCEEDED, the id is not a user workset
            int worksetUnreadable = 0;     // read THREW: assignment unknown, in no count below
            var outsideIds = new List<JToken>();
            // The denominator for every share below, NAMED in the reply: "78% of the
            // model" and "78% of what this scan could see" are different claims about
            // a file with a closed workset in it.
            long scanned = 0;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                int wid;
                try { wid = e.WorksetId.IntegerValue; }
                catch { worksetUnreadable++; continue; }
                int n;
                if (counts.TryGetValue(wid, out n))
                {
                    counts[wid] = n + 1;
                    idsByWorkset[wid].Add(Rid.Value(e.Id));
                }
                else
                {
                    outsideUserWorksets++;   // family/view worksets etc. — not a user workset
                    outsideIds.Add(Rid.Value(e.Id));
                }
                scanned++;
            }

            var rows = names.Keys
                .Select(k => (JToken)new JObject
                {
                    ["workset_id"] = k,
                    ["name"] = names[k],
                    ["is_open"] = isOpen[k],
                    ["elements"] = counts[k],
                    ["share_of_scanned_percent"] = WorksetPlacementRules.ShareOfScanned(counts[k], scanned),
                    ["element_ids"] = paging.Bucket(idsByWorkset[k], "worksets", "element_ids"),
                    ["elements_note"] = isOpen[k]
                        ? null
                        : "This workset is CLOSED, so its elements were never loaded into the document. The 0 " +
                          "above is what this scan could see, not what the workset holds."
                })
                .OrderByDescending(r => (int)r["elements"])
                .ToList();

            int closed = isOpen.Values.Count(v => !v);

            return new JObject
            {
                ["is_workshared"] = true,
                ["user_worksets"] = names.Count,
                ["worksets_open"] = names.Count - closed,
                ["worksets_closed"] = closed,
                ["elements_outside_user_worksets"] = outsideUserWorksets,
                ["elements_outside_user_worksets_ids"] = paging.Bucket(outsideIds, "worksets", "elements_outside_user_worksets_ids"),
                ["workset_unreadable"] = worksetUnreadable,
                ["elements_scanned"] = scanned,
                ["share_denominator"] = "every share_of_scanned_percent below is over elements_scanned - the " +
                                        "elements this walk could see - and NOT over the model. A closed " +
                                        "workset's elements are in neither number.",
                ["coverage_complete"] = WorksetPlacementRules.CoverageComplete(closed, worksetUnreadable),
                ["coverage_note"] = WorksetPlacementRules.CoverageNote(closed, worksetUnreadable),
                ["worksets"] = paging.Bucket(rows, "worksets", "worksets"),
                ["worksets_note"] = WorksetsNote(worksetUnreadable, closed, names.Count)
            };
        }

        /// <summary>
        /// Every per-workset `elements` count is built by asking each element which
        /// workset it is on. An element whose WorksetId threw is on no workset in this
        /// report — so the per-workset counts are LOWER bounds, and a reader dividing
        /// the model across worksets is dividing fewer elements than the model holds.
        /// Nothing else in the section would tell them that.
        /// </summary>
        private static string WorksetsNote(int worksetUnreadable, int closed, int total)
        {
            var parts = new List<string>();

            // The bigger of the two, and the one that used to be missing entirely: a
            // closed workset's elements are not in the document, so they are absent from
            // every count in this whole scan, not just from this section.
            if (closed > 0)
                parts.Add(closed + " of " + total + " workset(s) are CLOSED. Their elements were never loaded into " +
                          "the document, so they are missing from EVERY count in this scan - not only from this " +
                          "section - and their per-workset `elements` reads 0 for that reason rather than because " +
                          "the workset is empty. See visibility_coverage at the top of this reply.");

            if (worksetUnreadable > 0)
                parts.Add(worksetUnreadable + " element(s) never reported a workset (the WorksetId read threw). " +
                          "They are in NO count here — not in any workset's `elements`, and not in " +
                          "elements_outside_user_worksets, which counts only elements the model placed outside the " +
                          "user worksets. Every per-workset count is therefore a LOWER BOUND and they do not sum " +
                          "to the model's element total.");

            if (parts.Count == 0)
                return "Every workset is open and every element reported its workset, so these counts account for " +
                       "the whole model.";
            return string.Join(" ", parts);
        }

        // ============================ design_options ==============================
        private static JObject DesignOptionsSection(Document doc, ScanPagingContext paging)
        {
            var rows = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>()
                .Select(o => (JToken)new JObject
                {
                    ["id"] = o.Id.ToString(),
                    ["name"] = SafeName(o),
                    ["is_primary"] = TryBool(() => o.IsPrimary)
                })
                .ToList();

            return new JObject
            {
                ["design_options"] = paging.Bucket(rows, "design_options", "design_options"),
                ["note"] = rows.Count == 0
                    ? null
                    : "Elements in a non-primary design option are in the file and in nobody's takeoff. Confirm " +
                      "this is intended before delivering."
            };
        }

        // ============================ delivery readiness ==========================
        //
        // 4D and 5D, measured PER LEAF CATEGORY the caller declared - not over the
        // model. The global average is the number that makes a model look ready
        // while a whole discipline has nothing.
        //
        // The role's identity, scope, categories and validation come from the SAME
        // ParameterRule the parameter standards use, so "the wrong Fire Rating" and
        // "the wrong activity id" are one idea with one implementation.
        private static JObject DeliveryReadinessSection(Document doc, ScanPagingContext paging,
                                                        ParameterProfile fourD, ParameterProfile fiveD,
                                                        ClassificationCatalogue catalogue)
        {
            var roles = new List<DeliveryRole>();
            AddRoles(roles, fourD, DeliveryDimension.FourD);
            AddRoles(roles, fiveD, DeliveryDimension.FiveD);

            var verdicts = new List<RoleVerdictByCategory>();
            var codeStatuses = new List<string>();
            var idRows = new List<JToken>();

            foreach (DeliveryRole role in roles)
            {
                // The categories the ROLE declares. A role that names none is
                // measured on nothing rather than on everything: silently widening
                // it to the whole model is how a rule about doors becomes a finding
                // about ducts.
                foreach (string category in role.Rule.Categories)
                {
                    // MEASURED ONCE. The ids come from the same walk as the counts,
                    // both because a second pass is a second full traversal of the
                    // model and because two walks can disagree.
                    RoleCategoryMeasurement m = MeasureRoleOnCategory(
                        doc, role, category, catalogue, codeStatuses);

                    RoleVerdictByCategory v = DeliveryReadinessRules.Judge(m, role.Required);
                    v.Dimension = role.Dimension;
                    verdicts.Add(v);

                    foreach (long id in m.IncompleteIds)
                        idRows.Add(new JObject
                        {
                            ["role"] = role.Id,
                            ["dimension"] = role.Dimension,
                            ["category"] = category,
                            ["element_id"] = id
                        });
                }
            }

            var rows = verdicts
                .OrderBy(v => v.Dimension, StringComparer.Ordinal)
                .ThenBy(v => v.RoleId, StringComparer.Ordinal)
                .ThenBy(v => v.Category, StringComparer.Ordinal)
                .Select(v => (JToken)DeliveryReadinessRules.ToJson(v))
                .ToList();

            return new JObject
            {
                ["profiles"] = new JObject
                {
                    ["fourd"] = ProfileState(fourD),
                    ["fived"] = ProfileState(fiveD)
                },
                ["4d"] = DeliveryReadinessRules.Dimension(DeliveryDimension.FourD, verdicts, roles),
                ["5d"] = DeliveryReadinessRules.Dimension(DeliveryDimension.FiveD, verdicts, roles),
                ["classification"] = ClassificationCatalogueRules.Tally(codeStatuses, catalogue),
                ["by_role_and_category"] = paging.Bucket(rows, "delivery_readiness", "by_role_and_category"),
                ["incomplete_element_ids"] = paging.Bucket(
                    idRows, "delivery_readiness", "incomplete_element_ids")
            };
        }

        /// <summary>
        /// Turns a caller's parameter profile into roles for one dimension. The
        /// rule IS the role's identity: nothing about which parameter carries an
        /// activity id is decided here.
        /// </summary>
        private static void AddRoles(List<DeliveryRole> into, ParameterProfile profile, string dimension)
        {
            if (profile == null || !profile.Ok) return;
            foreach (ParameterRule r in profile.Rules)
                into.Add(new DeliveryRole
                {
                    Id = r.Id,
                    Dimension = dimension,
                    Rule = r,
                    Required = r.Required,
                    // A role validated against the catalogue is one whose rule says
                    // so by declaring a specification of "classification_code".
                    ValidateAgainstCatalogue = dimension == DeliveryDimension.FiveD &&
                                               r.Specification == "classification_code"
                });
        }

        private static RoleCategoryMeasurement MeasureRoleOnCategory(
            Document doc, DeliveryRole role, string category,
            ClassificationCatalogue catalogue, List<string> codeStatuses)
        {
            var m = new RoleCategoryMeasurement { RoleId = role.Id, Category = category };
            bool wantsType = role.Rule.Scope == ParameterScope.Type;

            try
            {
                FilteredElementCollector collector = wantsType
                    ? new FilteredElementCollector(doc).WhereElementIsElementType()
                    : new FilteredElementCollector(doc).WhereElementIsNotElementType();

                foreach (Element e in collector)
                {
                    string cat;
                    try { cat = e.Category == null ? null : e.Category.Name; } catch { continue; }
                    if (!string.Equals(cat, category, StringComparison.OrdinalIgnoreCase)) continue;

                    m.Population++;

                    string value = null;
                    bool present = false;
                    try
                    {
                        Parameter p = null;
                        if (role.Rule.BuiltIn != null)
                        {
                            BuiltInParameter bip;
                            if (Enum.TryParse(role.Rule.BuiltIn, out bip)) p = e.get_Parameter(bip);
                        }
                        if (p == null && role.Rule.Guid != null)
                        {
                            Guid g;
                            if (Guid.TryParse(role.Rule.Guid, out g)) p = e.get_Parameter(g);
                        }
                        if (p == null && role.Rule.Name != null) p = e.LookupParameter(role.Rule.Name);

                        if (p != null)
                        {
                            present = true;
                            value = p.AsString();
                            if (string.IsNullOrEmpty(value)) value = p.AsValueString();
                        }
                        m.Evaluated++;
                    }
                    catch { m.Unreadable++; continue; }

                    bool complete = present && !string.IsNullOrWhiteSpace(value);

                    // A PLACEHOLDER IS NOT A VALUE. The role's own rule says which
                    // strings are placeholders; nothing is assumed here.
                    if (complete && role.Rule.Placeholders != null &&
                        role.Rule.Placeholders.Any(ph => string.Equals(ph, value, StringComparison.OrdinalIgnoreCase)))
                        complete = false;

                    if (role.ValidateAgainstCatalogue)
                    {
                        string status = ClassificationCatalogueRules.Classify(value, catalogue, role.Required);
                        codeStatuses.Add(status);
                        // A GROUP CODE IS NOT COMPLETE. It exists, it validates, and
                        // nobody can price it.
                        if (status != CodeStatus.Leaf && status != CodeStatus.NotRequired &&
                            status != CodeStatus.CatalogueNotSupplied)
                            complete = false;
                    }

                    if (complete) m.Complete++;
                    else
                    {
                        m.Incomplete++;
                        if (m.IncompleteIds.Count < 5000) m.IncompleteIds.Add(Rid.Value(e.Id));
                        if (!string.IsNullOrWhiteSpace(value) && m.SampleValues.Count < 5 &&
                            !m.SampleValues.Contains(value)) m.SampleValues.Add(value);
                    }
                }
            }
            catch
            {
                // The walk itself failed. Everything in scope is unreadable, which
                // Judge turns into unreadable rather than zero coverage.
                m.Unreadable += m.Population;
                m.Evaluated = 0;
            }
            return m;
        }

        private static JObject ProfileState(ParameterProfile p)
        {
            if (p == null) return new JObject { ["status"] = "not_requested" };
            if (p.Absent) return new JObject { ["status"] = "not_requested", ["means"] = p.Message };
            if (!p.Ok) return new JObject { ["status"] = "refused", ["code"] = p.Code, ["message"] = p.Message };
            return new JObject { ["status"] = "ok", ["version"] = p.Version, ["roles"] = p.Rules.Count };
        }

        // ========================== documentary context ===========================
        //
        // The fields somebody reads off a title block, collected as facts and judged
        // only against a profile the caller supplies. Nothing about which fields a
        // project MUST carry is compiled in here.
        //
        // Every field is collected with its identity - the BuiltInParameter it came
        // from, and the shared-parameter GUID where it has one - so a rule keyed by
        // GUID is never satisfied by a parameter that merely shares a name.
        private static JObject DocumentaryContextSection(Document doc, ScanPagingContext paging,
                                                         ParameterProfile profile)
        {
            var facts = new List<DocumentaryFact>();

            Element pi = null;
            try { pi = doc.ProjectInformation; } catch { pi = null; }

            // The Project Information fields Revit itself defines. Their NAMES here
            // are field ids a profile refers to; they are not a claim that any of
            // them is required.
            var builtIns = new List<KeyValuePair<string, BuiltInParameter>>
            {
                new KeyValuePair<string, BuiltInParameter>("project_name", BuiltInParameter.PROJECT_NAME),
                new KeyValuePair<string, BuiltInParameter>("project_number", BuiltInParameter.PROJECT_NUMBER),
                new KeyValuePair<string, BuiltInParameter>("project_status", BuiltInParameter.PROJECT_STATUS),
                new KeyValuePair<string, BuiltInParameter>("project_address", BuiltInParameter.PROJECT_ADDRESS),
                new KeyValuePair<string, BuiltInParameter>("client_name", BuiltInParameter.CLIENT_NAME),
                new KeyValuePair<string, BuiltInParameter>("building_name", BuiltInParameter.PROJECT_BUILDING_NAME),
                new KeyValuePair<string, BuiltInParameter>("author", BuiltInParameter.PROJECT_AUTHOR),
                new KeyValuePair<string, BuiltInParameter>("issue_date", BuiltInParameter.PROJECT_ISSUE_DATE),
                new KeyValuePair<string, BuiltInParameter>(
                    "organization_name", BuiltInParameter.PROJECT_ORGANIZATION_NAME),
                new KeyValuePair<string, BuiltInParameter>(
                    "organization_description", BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION),
            };

            foreach (KeyValuePair<string, BuiltInParameter> kv in builtIns)
                facts.Add(ReadDocumentaryField(pi, kv.Key, kv.Value));

            // Every OTHER parameter on Project Information, so a caller's own
            // project parameters and shared parameters are judgeable by the same
            // rules. Collected with their GUIDs, which is what makes homonyms
            // distinguishable.
            try
            {
                var seen = new HashSet<string>(builtIns.Select(k => k.Key), StringComparer.OrdinalIgnoreCase);
                foreach (Parameter p in pi.Parameters)
                {
                    string name;
                    try { name = p.Definition == null ? null : p.Definition.Name; } catch { continue; }
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                    var f = new DocumentaryFact
                    {
                        Field = name,
                        Surface = DocumentarySurface.SharedParameters,
                        Present = true,
                        ElementId = pi == null ? -1 : Rid.Value(pi.Id)
                    };
                    try { f.Guid = p.IsShared ? p.GUID.ToString("D") : null; } catch { f.Guid = null; }
                    try
                    {
                        string s = p.AsString();
                        if (string.IsNullOrEmpty(s)) s = p.AsValueString();
                        f.Value = s;
                    }
                    catch { f.Readable = false; }
                    facts.Add(f);
                }
            }
            catch { }

            // Surfaces beyond Project Information, reported as facts that a profile
            // may or may not speak about.
            facts.Add(Documentary(DocumentarySurface.Units, "length_unit",
                                  () => LabelUtils.GetLabelForUnit(
                                      doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId())));
            facts.Add(Documentary(DocumentarySurface.ProjectLocation, "active_location",
                                  () => doc.ActiveProjectLocation == null ? null : doc.ActiveProjectLocation.Name));
            facts.Add(Documentary(DocumentarySurface.Phases, "phase_count",
                                  () => doc.Phases.Size.ToString(CultureInfo.InvariantCulture)));
            facts.Add(Documentary(DocumentarySurface.Templates, "view_template_count",
                                  () => new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                                        .Count(v => v.IsTemplate).ToString(CultureInfo.InvariantCulture)));
            facts.Add(Documentary(DocumentarySurface.Sheets, "sheet_count",
                                  () => new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                                        .GetElementCount().ToString(CultureInfo.InvariantCulture)));
            facts.Add(Documentary(DocumentarySurface.Revisions, "revision_count",
                                  () => new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Revisions)
                                        .WhereElementIsNotElementType().GetElementCount()
                                        .ToString(CultureInfo.InvariantCulture)));
            facts.Add(Documentary(DocumentarySurface.Links, "revit_link_count",
                                  () => new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType))
                                        .GetElementCount().ToString(CultureInfo.InvariantCulture)));

            List<DocumentaryVerdict> verdicts = DocumentaryContextRules.EvaluateAll(facts, profile);

            var factRows = facts
                .OrderBy(f => f.Surface, StringComparer.Ordinal).ThenBy(f => f.Field, StringComparer.Ordinal)
                .Select(f => (JToken)DocumentaryContextRules.ToJson(f)).ToList();

            // Only the answers a reader can act on are listed; the rest are counted.
            var actionable = new HashSet<string>(StringComparer.Ordinal)
            {
                ParameterOutcome.Missing, ParameterOutcome.Empty, ParameterOutcome.Placeholder,
                ParameterOutcome.InvalidValue, ParameterOutcome.WrongGuid, ParameterOutcome.Unreadable
            };
            var verdictRows = verdicts
                .Where(v => v != null && actionable.Contains(v.Outcome))
                .OrderBy(v => v.Field, StringComparer.Ordinal)
                .Select(v => (JToken)DocumentaryContextRules.ToJson(v)).ToList();

            JObject o = DocumentaryContextRules.Tally(verdicts, profile);
            o["fields_collected"] = facts.Count;
            o["facts"] = paging.Bucket(factRows, "documentary_context", "facts");
            o["findings"] = paging.Bucket(verdictRows, "documentary_context", "findings");
            return o;
        }

        /// <summary>
        /// Reads one Project Information field. PRESENT and READABLE are separate:
        /// a parameter that does not exist and one whose read threw are different
        /// answers, and both differ again from one that exists and is blank.
        /// </summary>
        private static DocumentaryFact ReadDocumentaryField(Element pi, string field, BuiltInParameter bip)
        {
            var f = new DocumentaryFact
            {
                Field = field,
                Surface = DocumentarySurface.ProjectInformation,
                BuiltIn = bip.ToString(),
                ElementId = pi == null ? -1 : Rid.Value(pi.Id)
            };
            if (pi == null) { f.Readable = false; return f; }

            try
            {
                Parameter p = pi.get_Parameter(bip);
                if (p == null) { f.Present = false; return f; }
                f.Present = true;
                try { f.Guid = p.IsShared ? p.GUID.ToString("D") : null; } catch { f.Guid = null; }
                string s = p.AsString();
                if (string.IsNullOrEmpty(s)) s = p.AsValueString();
                f.Value = s;
            }
            catch { f.Readable = false; }
            return f;
        }

        /// <summary>A documentary fact from a surface other than Project Information.</summary>
        private static DocumentaryFact Documentary(string surface, string field, Func<string> read)
        {
            var f = new DocumentaryFact { Field = field, Surface = surface, Present = true, ElementId = -1 };
            try { f.Value = read(); }
            catch { f.Readable = false; }
            return f;
        }

        // =============================== federation ===============================
        //
        // The facts the links section collects and never reported: attachment vs
        // overlay, the workset a link sits on, room bounding, and - the one nothing
        // else looks for - LINKS INSIDE LINKS, with their cycles.
        private static JObject FederationSection(Document doc, ScanPagingContext paging)
        {
            var facts = new List<LinkFederationFact>();
            var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)))
            {
                var lt = e as RevitLinkType;
                if (lt == null) continue;
                var f = new LinkFederationFact { ElementId = Rid.Value(e.Id) };
                bool bad;
                f.Name = SafeName(e, out bad);
                f.NameReadable = !bad;

                try { f.AttachmentType = lt.AttachmentType.ToString(); } catch { f.AttachmentType = null; }
                try { f.IsLoaded = RevitLinkType.IsLoaded(doc, lt.Id); } catch { f.IsLoaded = null; }

                // NESTED LINKS come from the linked DOCUMENT, which is already open
                // in memory when the link is loaded - nothing here opens a file.
                // NESTING COMES FROM THE TYPE GRAPH, not from opening the link.
                //
                // RevitLinkType.GetChildIds exists in all five supported years, and
                // it answers even when the link is UNLOADED. The earlier version
                // read nesting through GetLinkDocument(), which returns null for an
                // unloaded link - so an unloaded link reported its nesting as
                // "unreadable" when the model knew perfectly well. That was a false
                // unreadable: we had asked the wrong question.
                try
                {
                    var nested = new List<string>();
                    foreach (ElementId childId in lt.GetChildIds())
                        nested.Add(SafeName(doc.GetElement(childId)) ?? "(unnamed link)");
                    f.NestedLinkNames = nested;
                }
                catch { f.NestedReadable = false; }

                try { f.IsNested = lt.IsNestedLink; } catch { f.IsNested = null; }

                if (f.Name != null)
                {
                    if (!graph.ContainsKey(f.Name)) graph[f.Name] = new List<string>();
                    foreach (string n in f.NestedLinkNames)
                    {
                        graph[f.Name].Add(n);
                        if (!graph.ContainsKey(n)) graph[n] = new List<string>();
                    }
                }
                facts.Add(f);
            }

            // Instance-level facts: the workset a link sits on and whether it bounds
            // rooms are properties of the INSTANCE, not of the type.
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                long typeId;
                try { typeId = Rid.Value(e.GetTypeId()); } catch { continue; }
                LinkFederationFact f = facts.FirstOrDefault(x => x.ElementId == typeId);
                if (f == null) continue;
                if (f.WorksetName == null) f.WorksetName = WorksetNameOf(doc, e);
                if (f.IsRoomBounding == null)
                {
                    try
                    {
                        Parameter p = e.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                        f.IsRoomBounding = p == null ? (bool?)null : p.AsInteger() != 0;
                    }
                    catch { f.IsRoomBounding = null; }
                }
            }

            List<List<string>> cycles = FederationContentRules.CircularReferences(graph);
            var cycleRows = cycles
                .Select(c => (JToken)new JObject
                {
                    ["loop"] = new JArray(c.Select(x => (JToken)x)),
                    ["length"] = c.Count
                })
                .ToList();

            var rows = facts
                .OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.ElementId)
                .Select(f => (JToken)FederationContentRules.ToJson(f)).ToList();

            return new JObject
            {
                ["link_types"] = facts.Count,
                ["links_with_nested_links"] = facts.Count(f => f.NestedReadable && f.NestedLinkNames.Count > 0),
                ["links_whose_nesting_is_unreadable"] = facts.Count(f => !f.NestedReadable),
                ["circular_references"] = cycles.Count,
                ["nesting_means"] = FederationContentRules.NestingMeans,
                ["nesting_source"] = "nested links are read from the linked DOCUMENT, which Revit already has " +
                                     "in memory when the link is loaded. Nothing here opens a file, and a link " +
                                     "that is not loaded reports its nesting as unreadable rather than as none.",
                ["links"] = paging.Bucket(rows, "federation", "links"),
                ["cycles"] = paging.Bucket(cycleRows, "federation", "cycles")
            };
        }

        // ============================ external content ============================
        //
        // A model carrying a four-gigabyte point cloud, a linked image and a texture
        // nobody can resolve scans as clean when nothing looks for them.
        private static JObject ExternalContentSection(Document doc, ScanPagingContext paging)
        {
            var paths = new List<ExternalPathFact>();

            Action<string, Type, Func<Element, string>> read = (kind, t, pathOf) =>
            {
                try
                {
                    foreach (Element e in new FilteredElementCollector(doc).OfClass(t))
                    {
                        var f = new ExternalPathFact
                        {
                            Kind = kind,
                            ElementId = Rid.Value(e.Id),
                            Name = SafeName(e)
                        };
                        try { f.Path = pathOf(e); } catch { f.Path = null; }
                        // NULL means there is no path to check, which is a different
                        // answer from a path that does not resolve.
                        f.Resolves = string.IsNullOrWhiteSpace(f.Path) ? (bool?)null : FileExists(f.Path);
                        paths.Add(f);
                    }
                }
                catch { }
            };

            read("image", typeof(ImageType), e =>
            {
                Parameter p = e.get_Parameter(BuiltInParameter.RASTER_SYMBOL_FILENAME);
                return p == null ? null : p.AsString();
            });

            // GetPath(), not a BuiltInParameter: there is no POINTCLOUDTYPE_FILE_PATH
            // in any supported year, and the typed accessor is the one that exists.
            read("point_cloud", typeof(PointCloudType), e =>
            {
                var pc = e as PointCloudType;
                if (pc == null) return null;
                ModelPath mp = pc.GetPath();
                return mp == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
            });

            long imageInstances = 0, pointCloudInstances = 0;
            try
            {
                imageInstances = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_RasterImages).WhereElementIsNotElementType()
                    .GetElementCount();
            }
            catch { imageInstances = -1; }
            try
            {
                pointCloudInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(PointCloudInstance)).GetElementCount();
            }
            catch { pointCloudInstances = -1; }

            // The KEYNOTE FILE the codes are validated against. Its path is checked
            // because a keynote table pointing at a file nobody can reach validates
            // nothing while looking configured.
            string keynotePath = null;
            bool? keynoteResolves = null;
            bool keynoteReadable = true;
            try
            {
                KeynoteTable kt = KeynoteTable.GetKeynoteTable(doc);
                ExternalFileReference r = kt == null ? null : kt.GetExternalFileReference();
                if (r != null)
                {
                    ModelPath mp = r.GetAbsolutePath();
                    keynotePath = mp == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                    keynoteResolves = string.IsNullOrWhiteSpace(keynotePath)
                        ? (bool?)null : FileExists(keynotePath);
                }
            }
            catch { keynoteReadable = false; }

            long appearanceAssets = 0;
            try
            {
                appearanceAssets = new FilteredElementCollector(doc)
                    .OfClass(typeof(AppearanceAssetElement)).GetElementCount();
            }
            catch { appearanceAssets = -1; }

            var rows = paths
                .OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.ElementId)
                .Select(p => (JToken)FederationContentRules.ToJson(p)).ToList();

            JObject tally = FederationContentRules.PathTally(paths);
            var o = new JObject
            {
                ["image_types"] = paths.Count(p => p.Kind == "image"),
                ["image_instances"] = imageInstances < 0 ? null : (JToken)imageInstances,
                ["point_cloud_types"] = paths.Count(p => p.Kind == "point_cloud"),
                ["point_cloud_instances"] = pointCloudInstances < 0 ? null : (JToken)pointCloudInstances,
                ["appearance_assets"] = appearanceAssets < 0 ? null : (JToken)appearanceAssets,
                ["keynote_table_path"] = keynotePath,
                ["keynote_table_resolves"] = keynoteResolves,
                ["keynote_table_readable"] = keynoteReadable,
                // NOT ZERO. There is no way to count decals in any supported year.
                ["decals"] = "not_observable",
                ["decals_mean"] = FederationContentRules.DecalsMean,
                ["texture_paths"] = "not_read. An appearance asset's texture paths are behind the visual " +
                                    "materials API, which this scan does not open; the asset COUNT is reported " +
                                    "and the paths are an admitted gap rather than a zero.",
                ["paths"] = paging.Bucket(rows, "external_content", "paths")
            };
            foreach (JProperty prop in tally.Properties()) o[prop.Name] = prop.Value;
            return o;
        }

        private static bool FileExists(string path)
        {
            try { return System.IO.File.Exists(path); } catch { return false; }
        }

        // ================================== mep ===================================
        //
        // Facts only. Nothing here claims a system is calculated, balanced, sized or
        // hydraulically continuous - see Core/MepCensusRules for why connectivity
        // cannot support any of those words.
        private static JObject MepSection(Document doc, ScanPagingContext paging)
        {
            var systems = new List<MepSystemFact>();
            var systemElementCount = new Dictionary<long, long>();

            Action<Type, string> readSystems = (t, kind) =>
            {
                try
                {
                    foreach (Element e in new FilteredElementCollector(doc).OfClass(t))
                    {
                        var f = new MepSystemFact { ElementId = Rid.Value(e.Id), Kind = kind };
                        bool bad;
                        f.Name = SafeName(e, out bad);
                        f.NameReadable = !bad;
                        try
                        {
                            // ABSENT is null. "Undefined" is a value Revit itself uses
                            // for a system somebody left unclassified, and collapsing
                            // the two loses which of them happened.
                            Parameter p = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM);
                            f.Classification = p == null ? null : p.AsString();
                        }
                        catch { f.ClassificationReadable = false; }
                        systems.Add(f);
                    }
                }
                catch { }
            };

            readSystems(typeof(Autodesk.Revit.DB.Mechanical.MechanicalSystem), "mechanical");
            readSystems(typeof(Autodesk.Revit.DB.Plumbing.PipingSystem), "piping");
            readSystems(typeof(Autodesk.Revit.DB.Electrical.ElectricalSystem), "electrical");

            var elements = new List<MepElementFact>();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                ConnectorManager cm = null;
                try
                {
                    var curve = e as MEPCurve;
                    if (curve != null) cm = curve.ConnectorManager;
                    else
                    {
                        var fi = e as FamilyInstance;
                        if (fi != null && fi.MEPModel != null) cm = fi.MEPModel.ConnectorManager;
                    }
                }
                catch { cm = null; }
                if (cm == null) continue;      // not an MEP element; not a finding

                var f = new MepElementFact { ElementId = Rid.Value(e.Id) };
                try { f.Category = e.Category == null ? null : e.Category.Name; } catch { f.Category = null; }

                var claimed = new HashSet<long>();
                try
                {
                    foreach (Connector c in cm.Connectors)
                    {
                        f.ConnectorsTotal++;
                        try
                        {
                            if (c.IsConnected) f.ConnectorsConnected++;
                            else f.ConnectorsOpen++;
                        }
                        catch { f.ConnectorsUnreadable++; }

                        try
                        {
                            MEPSystem sys = c.MEPSystem;
                            if (sys != null) claimed.Add(Rid.Value(sys.Id));
                        }
                        catch { }
                    }
                    f.SystemCount = claimed.Count;
                    if (claimed.Count == 1)
                        f.SystemName = SafeName(doc.GetElement(Rid.Make(claimed.First())));
                }
                catch { f.Readable = false; f.SystemCount = null; }

                foreach (long s in claimed)
                {
                    long had;
                    systemElementCount[s] = systemElementCount.TryGetValue(s, out had) ? had + 1 : 1;
                }
                elements.Add(f);
            }

            foreach (MepSystemFact s in systems)
            {
                long n;
                s.ElementCount = systemElementCount.TryGetValue(s.ElementId, out n) ? n : 0;
            }

            var systemRows = systems
                .OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => (JToken)MepCensusRules.ToJson(s)).ToList();
            var elementRows = elements
                .Where(e => e.State != MepSystemState.InSystem)   // the ones worth looking at
                .OrderBy(e => e.State, StringComparer.Ordinal).ThenBy(e => e.ElementId)
                .Select(e => (JToken)MepCensusRules.ToJson(e)).ToList();

            JObject o = MepCensusRules.Tally(elements, systems);
            o["listed_rule"] = "the element list carries those NOT in exactly one system - no_system, " +
                               "multiple_systems and unreadable. Elements in one system are counted and not " +
                               "listed, because a list of every duct is not a finding.";
            o["systems_list"] = paging.Bucket(systemRows, "mep", "systems_list");
            o["elements_list"] = paging.Bucket(elementRows, "mep", "elements_list");
            return o;
        }

        // ================================ structure ===============================
        //
        // Modelling facts only: no safety, capacity, adequacy or code compliance.
        // Nothing in a Revit document supports those judgements.
        private static JObject StructureSection(Document doc, ScanPagingContext paging)
        {
            var pops = new List<StructuralPopulationFact>();

            Action<string, BuiltInCategory> count = (name, bic) =>
            {
                var f = new StructuralPopulationFact { Population = name };
                try
                {
                    foreach (Element e in new FilteredElementCollector(doc)
                             .OfCategory(bic).WhereElementIsNotElementType())
                    {
                        f.Total++;
                        try
                        {
                            Parameter p = e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                            string m = p == null ? "(none)" : SafeName(doc.GetElement(p.AsElementId()));
                            if (string.IsNullOrEmpty(m)) m = "(none)";
                            long had;
                            f.ByMaterial[m] = f.ByMaterial.TryGetValue(m, out had) ? had + 1 : 1;
                        }
                        catch { f.MaterialUnreadable++; }
                    }
                    pops.Add(f);
                }
                catch
                {
                    f.Unreadable++;
                    pops.Add(f);
                }
            };

            count(StructuralPopulations.Columns, BuiltInCategory.OST_StructuralColumns);
            count(StructuralPopulations.Framing, BuiltInCategory.OST_StructuralFraming);
            count(StructuralPopulations.Foundations, BuiltInCategory.OST_StructuralFoundation);
            count(StructuralPopulations.Connections, BuiltInCategory.OST_StructConnections);

            // Structural WALLS and FLOORS are not categories of their own: they are
            // walls and floors whose structural flag is set. Counting the whole
            // category would report every partition as structure.
            CountStructuralHosts(doc, BuiltInCategory.OST_Walls, StructuralPopulations.Walls, pops);
            CountStructuralHosts(doc, BuiltInCategory.OST_Floors, StructuralPopulations.Floors, pops);

            var rebar = new List<RebarFact>();
            try
            {
                foreach (Element e in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rebar).WhereElementIsNotElementType())
                {
                    var r = new RebarFact { ElementId = Rid.Value(e.Id) };
                    try { r.TypeName = SafeName(doc.GetElement(e.GetTypeId())); } catch { r.TypeName = null; }
                    try
                    {
                        // HOST IS A THREE-STATE READ. Null means the question failed,
                        // which is never the same as "this bar has no host".
                        ElementId hostId = null;
                        var rb = e as Autodesk.Revit.DB.Structure.Rebar;
                        if (rb != null) hostId = rb.GetHostId();
                        r.HasHost = hostId != null && hostId != ElementId.InvalidElementId;
                        if (r.HasHost == true)
                        {
                            r.HostId = Rid.Value(hostId);
                            Element host = doc.GetElement(hostId);
                            r.HostCategory = host == null || host.Category == null ? null : host.Category.Name;
                        }
                    }
                    catch { r.HasHost = null; }

                    try
                    {
                        Parameter q = e.get_Parameter(BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS);
                        r.BarsInSet = q == null ? (int?)null : q.AsInteger();
                    }
                    catch { r.BarsInSet = null; }

                    try
                    {
                        Parameter c = e.get_Parameter(BuiltInParameter.CLEAR_COVER);
                        if (c == null) { r.CoverMm = null; r.CoverReadable = false; }
                        else r.CoverMm = Math.Round(c.AsDouble() * 304.8, 3);
                    }
                    catch { r.CoverReadable = false; }

                    rebar.Add(r);
                }
            }
            catch { }

            var rebarRows = StructureCensusRules.WithoutHost(rebar)
                .OrderBy(r => r.ElementId)
                .Select(r => (JToken)StructureCensusRules.ToJson(r)).ToList();

            JObject o = StructureCensusRules.Summary(pops, rebar);
            o["structural_flag_note"] = "structural_walls and structural_floors count walls and floors whose " +
                                        "STRUCTURAL flag is set, not whole categories - counting the category " +
                                        "would report every partition as structure.";
            o["rebar_without_host_list"] = paging.Bucket(rebarRows, "structure", "rebar_without_host_list");
            return o;
        }

        private static void CountStructuralHosts(Document doc, BuiltInCategory bic, string population,
                                                 List<StructuralPopulationFact> into)
        {
            var f = new StructuralPopulationFact { Population = population };
            try
            {
                foreach (Element e in new FilteredElementCollector(doc)
                         .OfCategory(bic).WhereElementIsNotElementType())
                {
                    try
                    {
                        Parameter p = e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
                        if (p == null) p = e.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                        if (p == null || p.AsInteger() == 0) continue;
                        f.Total++;
                        Parameter m = e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                        string name = m == null ? "(none)" : SafeName(doc.GetElement(m.AsElementId()));
                        if (string.IsNullOrEmpty(name)) name = "(none)";
                        long had;
                        f.ByMaterial[name] = f.ByMaterial.TryGetValue(name, out had) ? had + 1 : 1;
                    }
                    catch { f.Unreadable++; }
                }
            }
            catch { f.Unreadable++; }
            into.Add(f);
        }

        // ================================ phases ==================================
        //
        // Whether a category HAS phases is decided by asking the element for the
        // parameter, not from a list of categories compiled in here - such a list
        // drifts with every Revit release and turns into a wrong answer nobody
        // notices.
        private static JObject PhasesSection(Document doc, ScanPagingContext paging)
        {
            var phases = new List<PhaseFact>();
            var sequenceOf = new Dictionary<long, int>();
            try
            {
                int i = 0;
                foreach (Phase ph in doc.Phases)
                {
                    long id = Rid.Value(ph.Id);
                    bool bad;
                    var f = new PhaseFact { ElementId = id, Sequence = i };
                    f.Name = SafeName(ph, out bad);
                    f.NameReadable = !bad;
                    // doc.Phases is ordered by the document's own phase sequence,
                    // which is the only order in which "before" means anything -
                    // "Phase 10" sorts before "Phase 2" as text.
                    sequenceOf[id] = i;
                    phases.Add(f);
                    i++;
                }
            }
            catch { }

            var elements = new List<PhasedElementFact>();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var f = new PhasedElementFact { ElementId = Rid.Value(e.Id) };
                try { f.Category = e.Category == null ? null : e.Category.Name; } catch { f.Category = null; }

                try
                {
                    Parameter created = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    Parameter demolished = e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);

                    // NEITHER PARAMETER PRESENT means this category does not carry
                    // phases at all - a level, a grid, a view. That is not_applicable
                    // and never "no phase".
                    if (created == null && demolished == null)
                    {
                        f.SupportsPhases = false;
                        elements.Add(f);
                        continue;
                    }

                    if (created != null)
                    {
                        ElementId pid = created.AsElementId();
                        if (pid != null && pid != ElementId.InvalidElementId)
                        {
                            f.CreatedPhase = SafeName(doc.GetElement(pid));
                            int seq;
                            f.CreatedSequence = sequenceOf.TryGetValue(Rid.Value(pid), out seq)
                                ? seq : (int?)null;
                        }
                    }
                    if (demolished != null)
                    {
                        ElementId pid = demolished.AsElementId();
                        if (pid != null && pid != ElementId.InvalidElementId)
                        {
                            f.DemolishedPhase = SafeName(doc.GetElement(pid));
                            int seq;
                            f.DemolishedSequence = sequenceOf.TryGetValue(Rid.Value(pid), out seq)
                                ? seq : (int?)null;
                        }
                    }
                }
                catch { f.Readable = false; }

                elements.Add(f);
            }

            var phaseRows = PhaseCensusRules.InSequence(phases)
                .Select(p => (JToken)PhaseCensusRules.ToJson(p)).ToList();
            var contradictionRows = PhaseCensusRules.Contradictions(elements)
                .OrderBy(e => e.ElementId)
                .Select(e => (JToken)PhaseCensusRules.ToJson(e)).ToList();

            // Views carry a phase and a phase filter of their own; reported as a
            // fact beside the element census rather than mixed into it.
            var viewRows = new List<JToken>();
            try
            {
                foreach (Element v in new FilteredElementCollector(doc).OfClass(typeof(View)))
                {
                    var view = v as View;
                    if (view == null || view.IsTemplate) continue;
                    string phase = null, filter = null;
                    try { phase = ParamString(v, BuiltInParameter.VIEW_PHASE); } catch { }
                    try { filter = ParamString(v, BuiltInParameter.VIEW_PHASE_FILTER); } catch { }
                    if (phase == null && filter == null) continue;
                    viewRows.Add(new JObject
                    {
                        ["view_id"] = Rid.Value(v.Id),
                        ["view_name"] = SafeName(v),
                        ["phase"] = phase,
                        ["phase_filter"] = filter
                    });
                }
            }
            catch { }

            long phaseFilters = 0;
            try { phaseFilters = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).GetElementCount(); }
            catch { phaseFilters = -1; }

            JObject o = PhaseCensusRules.Tally(elements);
            o["phases"] = phases.Count;
            o["phase_filters"] = phaseFilters < 0 ? null : (JToken)phaseFilters;
            o["phases_list"] = paging.Bucket(phaseRows, "phases", "phases_list");
            o["contradictions"] = paging.Bucket(contradictionRows, "phases", "contradictions");
            o["views_by_phase"] = paging.Bucket(viewRows, "phases", "views_by_phase");
            return o;
        }

        // ================================ groups ==================================
        //
        // A group TYPE with no instances and a group type with no MEMBERS are two
        // different findings with two different fixes. Both are reported; neither is
        // derived from the other.
        private static JObject GroupsSection(Document doc, ScanPagingContext paging)
        {
            var instances = new List<GroupInstanceFact>();
            var countByType = new Dictionary<long, int>();
            // Which group instances are members of another group, so nesting is read
            // from the model rather than assumed.
            var nestedIds = new HashSet<long>();
            var parentOf = new Dictionary<long, long>();

            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Group)))
            {
                var g = e as Group;
                if (g == null) continue;
                try
                {
                    foreach (ElementId m in g.GetMemberIds())
                    {
                        if (doc.GetElement(m) is Group)
                        {
                            nestedIds.Add(Rid.Value(m));
                            parentOf[Rid.Value(m)] = Rid.Value(e.Id);
                        }
                    }
                }
                catch { }
            }

            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Group)))
            {
                var g = e as Group;
                if (g == null) continue;
                long id = Rid.Value(e.Id);
                var f = new GroupInstanceFact { ElementId = id };
                try
                {
                    ElementId tid = g.GetTypeId();
                    f.TypeId = Rid.Value(tid);
                    f.TypeName = SafeName(doc.GetElement(tid));
                    int had;
                    countByType[f.TypeId] = countByType.TryGetValue(f.TypeId, out had) ? had + 1 : 1;
                }
                catch { f.Readable = false; }

                try { f.LevelName = SafeName(doc.GetElement(e.LevelId)); } catch { f.LevelName = null; }
                f.WorksetName = WorksetNameOf(doc, e);

                // Membership was read for the whole model above, so nesting is known
                // rather than guessed. Null only where that read threw for this one.
                f.IsNested = nestedIds.Contains(id);
                long parent;
                f.ParentGroupId = parentOf.TryGetValue(id, out parent) ? parent : (long?)null;

                instances.Add(f);
            }

            var types = new List<GroupTypeFact>();
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(GroupType)))
            {
                var gt = e as GroupType;
                if (gt == null) continue;
                var f = new GroupTypeFact { ElementId = Rid.Value(e.Id) };
                bool bad;
                f.Name = SafeName(e, out bad);
                f.NameReadable = !bad;

                int n;
                f.InstanceCount = countByType.TryGetValue(f.ElementId, out n) ? n : 0;

                // MEMBERS come from one placed instance's member list, because a
                // GroupType does not enumerate its own members. A type nothing
                // places therefore has an UNKNOWN member count - null, never 0,
                // because 0 would report it as an empty group when it may be full.
                try
                {
                    Group sample = new FilteredElementCollector(doc).OfClass(typeof(Group))
                        .Cast<Group>()
                        .FirstOrDefault(x => Rid.Value(x.GetTypeId()) == f.ElementId);
                    if (sample == null)
                    {
                        f.MemberCount = null;
                        f.MembersReadable = false;
                    }
                    else
                    {
                        ICollection<ElementId> members = sample.GetMemberIds();
                        f.MemberCount = members.Count;
                        foreach (ElementId m in members)
                        {
                            string cat = "(category unreadable)";
                            try
                            {
                                Element me = doc.GetElement(m);
                                cat = me == null || me.Category == null ? "(no category)" : me.Category.Name;
                            }
                            catch { }
                            long had;
                            f.MemberCategories[cat] =
                                f.MemberCategories.TryGetValue(cat, out had) ? had + 1 : 1;
                        }
                    }
                }
                catch { f.MemberCount = null; f.MembersReadable = false; }

                types.Add(f);
            }

            var typeRows = types
                .OrderByDescending(t => t.InstanceCount).ThenBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => (JToken)GroupOptionRules.ToJson(t)).ToList();
            var instanceRows = instances
                .OrderBy(i => i.TypeName, StringComparer.Ordinal).ThenBy(i => i.ElementId)
                .Select(i => (JToken)GroupOptionRules.ToJson(i)).ToList();

            JObject o = GroupOptionRules.GroupTotals(types, instances);
            o["member_source"] = "member lists come from one PLACED instance of each type: a GroupType does " +
                                 "not enumerate its own members. A type nothing places has an UNKNOWN member " +
                                 "count, reported as null - never 0, which would call a full group empty.";
            o["group_types_list"] = paging.Bucket(typeRows, "groups", "group_types_list");
            o["group_instances_list"] = paging.Bucket(instanceRows, "groups", "group_instances_list");
            return o;
        }

        // ============================= design options =============================
        private static JObject DesignOptionsCensus(Document doc, ScanPagingContext paging)
        {
            var sets = new List<Element>();
            try
            {
                sets = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DesignOptionSets).WhereElementIsNotElementType()
                    .ToList();
            }
            catch { }

            // NO OPTIONS IS NOT A PASS. The document has none, which is a different
            // statement from "the options are in order".
            if (sets.Count == 0) return GroupOptionRules.NoDesignOptions();

            var byOption = new Dictionary<long, long>();
            long elementsInMainModel = 0, elementsUnreadable = 0;
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                try
                {
                    DesignOption d = e.DesignOption;
                    if (d == null) { elementsInMainModel++; continue; }
                    long key = Rid.Value(d.Id), had;
                    byOption[key] = byOption.TryGetValue(key, out had) ? had + 1 : 1;
                }
                catch { elementsUnreadable++; }
            }

            var facts = new List<DesignOptionFact>();
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(DesignOption)))
            {
                var d = e as DesignOption;
                if (d == null) continue;
                var f = new DesignOptionFact { ElementId = Rid.Value(e.Id) };
                bool bad;
                f.Name = SafeName(e, out bad);
                f.Readable = !bad;
                try { f.IsPrimary = d.IsPrimary; } catch { f.IsPrimary = null; }
                try
                {
                    Parameter p = e.get_Parameter(BuiltInParameter.OPTION_SET_ID);
                    f.SetName = p == null ? null : SafeName(doc.GetElement(p.AsElementId()));
                }
                catch { f.SetName = null; }
                long n;
                f.ElementCount = byOption.TryGetValue(f.ElementId, out n) ? n : 0;
                facts.Add(f);
            }

            var rows = facts
                .OrderBy(f => f.SetName, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => (JToken)GroupOptionRules.ToJson(f)).ToList();

            return new JObject
            {
                ["status"] = "ok",
                ["option_sets"] = sets.Count,
                ["options"] = facts.Count,
                ["options_with_no_elements"] = facts.Count(f => f.ElementCount == 0),
                ["elements_in_main_model"] = elementsInMainModel,
                ["elements_unreadable"] = elementsUnreadable,
                ["means"] = GroupOptionRules.OptionsMean,
                ["options_list"] = paging.Bucket(rows, "design_options_census", "options_list")
            };
        }

        // ================================ spatial =================================
        //
        // Rooms, MEP spaces and areas, counted as THREE populations.
        //
        // REDUNDANCY IS NOT A PROPERTY. Revit exposes no IsRedundant: a redundant
        // room reports zero area and no boundary segments, which is exactly what an
        // unenclosed room reports. The only place the distinction exists is the
        // model's own warning list - and the warning that means "redundant" is
        // identified by a FailureDefinitionId the CALLER declares, because no guid
        // is compiled into this bridge.
        //
        // Without that declaration is_redundant stays null and the state falls
        // honestly to not_enclosed or zero_area. It is never guessed from the area.
        private static JObject SpatialSection(Document doc, ScanPagingContext paging,
                                              IReadOnlyCollection<string> redundantGuids)
        {
            // Which elements the model itself complains about, by warning identity.
            var flagged = new Dictionary<long, HashSet<string>>();
            long warningsUnreadable = 0;
            try
            {
                foreach (FailureMessage w in doc.GetWarnings())
                {
                    string guid;
                    try { guid = w.GetFailureDefinitionId().Guid.ToString("D"); }
                    catch { warningsUnreadable++; continue; }
                    try
                    {
                        foreach (ElementId id in w.GetFailingElements())
                        {
                            long key = Rid.Value(id);
                            HashSet<string> set;
                            if (!flagged.TryGetValue(key, out set))
                            {
                                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                flagged[key] = set;
                            }
                            set.Add(guid);
                        }
                    }
                    catch { warningsUnreadable++; }
                }
            }
            catch { warningsUnreadable++; }

            var facts = new List<SpatialFact>();
            var opts = new SpatialElementBoundaryOptions();

            ReadSpatial(doc, BuiltInCategory.OST_Rooms, SpatialKind.Room, facts, flagged, redundantGuids, opts);
            ReadSpatial(doc, BuiltInCategory.OST_MEPSpaces, SpatialKind.Space, facts, flagged, redundantGuids, opts);
            ReadSpatial(doc, BuiltInCategory.OST_Areas, SpatialKind.Area, facts, flagged, redundantGuids, opts);

            long schemes = 0;
            try { schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).GetElementCount(); }
            catch { schemes = -1; }

            var rows = facts
                .OrderBy(f => f.Kind, StringComparer.Ordinal)
                .ThenBy(f => f.Number, StringComparer.Ordinal)
                .ThenBy(f => f.ElementId)
                .Select(f => (JToken)SpatialCensusRules.ToJson(f))
                .ToList();

            return new JObject
            {
                ["rooms"] = SpatialCensusRules.Tally(facts, SpatialKind.Room),
                ["spaces"] = SpatialCensusRules.Tally(facts, SpatialKind.Space),
                ["areas"] = SpatialCensusRules.Tally(facts, SpatialKind.Area),
                // A population of its own, as the mandate asks: an area scheme is
                // not an area.
                ["area_schemes"] = schemes < 0 ? null : (JToken)schemes,
                ["warnings_unreadable"] = warningsUnreadable,
                ["redundancy_source"] = redundantGuids == null || redundantGuids.Count == 0
                    ? "NOT DETERMINED. Revit exposes no IsRedundant, and a redundant element reports zero area " +
                      "and no boundary exactly as an unenclosed one does. Declare the FailureDefinitionId guid " +
                      "your Revit uses for redundancy in spatial_rules.redundant_warning_guids; until then " +
                      "is_redundant is null and no element is called redundant."
                    : "the model's own warnings, matched against the " + redundantGuids.Count +
                      " guid(s) you declared.",
                ["states_mean"] = SpatialCensusRules.StatesMean,
                ["populations_mean"] = SpatialCensusRules.PopulationsMean,
                ["elements"] = paging.Bucket(rows, "spatial", "elements")
            };
        }

        private static void ReadSpatial(Document doc, BuiltInCategory bic, string kind,
                                        List<SpatialFact> into,
                                        Dictionary<long, HashSet<string>> flagged,
                                        IReadOnlyCollection<string> redundantGuids,
                                        SpatialElementBoundaryOptions opts)
        {
            foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                var f = new SpatialFact { ElementId = Rid.Value(e.Id), Kind = kind };
                var se = e as SpatialElement;
                if (se == null) { f.Readable = false; into.Add(f); continue; }

                bool bad;
                f.Name = SafeName(e, out bad);
                f.NameReadable = !bad;
                try { f.Number = se.Number; } catch { f.NumberReadable = false; }
                try { f.LevelName = se.Level == null ? null : se.Level.Name; } catch { f.LevelName = null; }
                try { f.Phase = ParamString(e, BuiltInParameter.ROOM_PHASE); } catch { f.Phase = null; }

                // UNPLACED IS A LOCATION QUESTION, not an area question.
                try { f.HasLocation = se.Location != null; } catch { f.HasLocation = null; }

                try { f.AreaSqM = Math.Round(se.Area * 0.09290304, 6); } catch { f.AreaSqM = null; }

                // Enclosure comes from the boundary, not from the area.
                try
                {
                    IList<IList<BoundarySegment>> b = se.GetBoundarySegments(opts);
                    f.IsEnclosed = b != null && b.Count > 0;
                }
                catch { f.IsEnclosed = null; }

                if (redundantGuids != null && redundantGuids.Count > 0)
                {
                    HashSet<string> guids;
                    f.IsRedundant = flagged.TryGetValue(f.ElementId, out guids) &&
                                    guids.Any(g => redundantGuids.Contains(g));
                }

                if (kind == SpatialKind.Area)
                {
                    try
                    {
                        var area = e as Area;
                        f.AreaScheme = area != null && area.AreaScheme != null ? area.AreaScheme.Name : null;
                    }
                    catch { f.AreaScheme = null; }
                    try { f.ViewName = SafeName(doc.GetElement(e.OwnerViewId)); } catch { f.ViewName = null; }
                }

                into.Add(f);
            }
        }

        // ============================== parameters ================================
        //
        // A TYPE IS OBSERVED ONCE. The instances that use it are collected first and
        // attached to the single type observation as ids, so one wrong type produces
        // one finding carrying a count - not four hundred findings that bury
        // everything else in the report.
        //
        // Specification comes from Definition.GetDataType(), which exists in every
        // supported year. Definition.ParameterGroup does NOT - it is present in 2023
        // and gone by 2027 - so it is never read here.
        private static JObject ParametersSection(Document doc, ScanPagingContext paging, ParameterProfile profile)
        {
            var observations = new List<ParameterObservation>();
            long elementsUnreadable = 0;

            // Which instances use which type, so a type finding can name them.
            var instancesByType = new Dictionary<long, List<long>>();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                try
                {
                    ElementId tid = e.GetTypeId();
                    if (tid == null || tid == ElementId.InvalidElementId) continue;
                    long key = Rid.Value(tid);
                    List<long> list;
                    if (!instancesByType.TryGetValue(key, out list))
                    {
                        list = new List<long>();
                        instancesByType[key] = list;
                    }
                    list.Add(Rid.Value(e.Id));
                }
                catch { }
            }

            if (profile != null && profile.Ok)
            {
                foreach (ParameterRule rule in profile.Rules)
                {
                    bool wantsType = rule.Scope == ParameterScope.Type;
                    FilteredElementCollector collector = wantsType
                        ? new FilteredElementCollector(doc).WhereElementIsElementType()
                        : new FilteredElementCollector(doc).WhereElementIsNotElementType();

                    foreach (Element e in collector)
                    {
                        ParameterObservation o;
                        try { o = Observe(e, rule, wantsType); }
                        catch { elementsUnreadable++; continue; }
                        if (o == null) continue;

                        if (wantsType)
                        {
                            List<long> users;
                            if (instancesByType.TryGetValue(Rid.Value(e.Id), out users))
                                o.AffectedInstanceIds.AddRange(users);
                        }
                        observations.Add(o);
                    }
                }
            }

            List<ParameterVerdict> verdicts = ParameterStandardRules.Evaluate(observations, profile);

            // Only the answers a reader can act on are listed; the rest are counted.
            var actionable = new HashSet<string>(StringComparer.Ordinal)
            {
                ParameterOutcome.Missing, ParameterOutcome.Empty, ParameterOutcome.Placeholder,
                ParameterOutcome.WrongScope, ParameterOutcome.WrongBinding, ParameterOutcome.WrongGuid,
                ParameterOutcome.WrongStorageType, ParameterOutcome.WrongSpecification,
                ParameterOutcome.InvalidValue, ParameterOutcome.Unreadable
            };

            var rows = verdicts
                .Where(v => v != null && actionable.Contains(v.Outcome))
                .OrderBy(v => v.RuleId, StringComparer.Ordinal)
                .ThenBy(v => v.Outcome, StringComparer.Ordinal)
                .ThenBy(v => v.ElementId)
                .Select(v => (JToken)ParameterStandardRules.ToJson(v))
                .ToList();

            JObject tally = ParameterStandardRules.Tally(verdicts);
            var o2 = new JObject
            {
                ["profile"] = ParameterProfileStatus(profile),
                ["rules_evaluated"] = profile != null && profile.Ok ? profile.Rules.Count : 0,
                ["observations"] = observations.Count,
                ["elements_unreadable"] = elementsUnreadable,
                ["outcomes"] = tally,
                ["findings"] = paging.Bucket(rows, "parameters", "findings")
            };
            return o2;
        }

        /// <summary>
        /// Reads one parameter off one element, by whichever identity the rule
        /// declared. A rule pinned to a GUID still LOOKS UP by name when it has one,
        /// so the reply can say "a parameter of this name exists and its guid is
        /// different" - which is actionable - rather than "missing", which is not.
        /// </summary>
        private static ParameterObservation Observe(Element e, ParameterRule rule, bool isType)
        {
            var o = new ParameterObservation
            {
                ElementId = Rid.Value(e.Id),
                IsType = isType,
                Binding = isType ? ParameterScope.Type : ParameterScope.Instance
            };
            try { o.Category = e.Category == null ? null : e.Category.Name; } catch { o.Category = null; }
            try { o.ElementClass = e.GetType().Name; } catch { o.ElementClass = null; }

            Parameter p = null;
            try
            {
                if (rule.BuiltIn != null)
                {
                    BuiltInParameter bip;
                    if (Enum.TryParse(rule.BuiltIn, out bip)) p = e.get_Parameter(bip);
                }
                if (p == null && rule.Guid != null)
                {
                    Guid g;
                    if (Guid.TryParse(rule.Guid, out g)) p = e.get_Parameter(g);
                }
                if (p == null && rule.Name != null) p = e.LookupParameter(rule.Name);
            }
            catch
            {
                o.Present = true;
                o.Readable = false;
                return o;
            }

            if (p == null) { o.Present = false; return o; }
            o.Present = true;

            try { o.IsShared = p.IsShared; } catch { o.IsShared = false; }
            // GUID throws for a parameter that is not shared. Absent, not empty.
            try { o.Guid = o.IsShared ? p.GUID.ToString("D") : null; } catch { o.Guid = null; }
            try { o.StorageType = p.StorageType.ToString(); } catch { o.StorageType = null; }
            try
            {
                // GetDataType(), never ParameterGroup: the latter is gone by 2027.
                ForgeTypeId spec = p.Definition == null ? null : p.Definition.GetDataType();
                o.Specification = spec == null ? null : spec.TypeId;
            }
            catch { o.Specification = null; }

            try { o.HasValue = p.HasValue; } catch { o.HasValue = false; }
            try
            {
                string s = p.AsString();
                if (string.IsNullOrEmpty(s)) s = p.AsValueString();
                o.ValueAsString = s;
            }
            catch { o.ValueAsString = null; o.Readable = false; }

            try
            {
                if (p.StorageType == StorageType.Double) o.ValueAsDouble = p.AsDouble();
                else if (p.StorageType == StorageType.Integer) o.ValueAsDouble = p.AsInteger();
            }
            catch { o.ValueAsDouble = null; }

            return o;
        }

        private static JObject ParameterProfileStatus(ParameterProfile p)
        {
            if (p == null) return new JObject { ["status"] = "not_requested" };
            if (p.Absent) return new JObject { ["status"] = "not_requested", ["means"] = p.Message };
            if (!p.Ok) return new JObject { ["status"] = "refused", ["code"] = p.Code, ["message"] = p.Message };
            return new JObject { ["status"] = "ok", ["version"] = p.Version, ["rules"] = p.Rules.Count };
        }

        // ================================ sheets ==================================
        //
        // Planimetry audited from the MODEL, with no PDF anywhere.
        //
        // Schedules and viewports are collected separately and never added into one
        // "contents" number, because Revit places a schedule as a
        // ScheduleSheetInstance: a sheet holding nothing but schedules has zero
        // viewports, and a check that counts only viewports calls it empty.
        private static JObject SheetsSection(Document doc, ScanPagingContext paging, SheetRules rules)
        {
            // One pass each, keyed by sheet, rather than a search per sheet.
            var titleBlocks = new Dictionary<long, int>();
            var schedules = new Dictionary<long, int>();
            long titleBlocksUnreadable = 0, schedulesUnreadable = 0;

            foreach (Element e in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                try { Add(titleBlocks, Rid.Value(e.OwnerViewId)); }
                catch { titleBlocksUnreadable++; }
            }

            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
            {
                try { Add(schedules, Rid.Value(e.OwnerViewId)); }
                catch { schedulesUnreadable++; }
            }

            var facts = new List<SheetStateFact>();
            var viewportRows = new List<JToken>();
            long viewportsUnreadable = 0;

            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
            {
                var sheet = e as ViewSheet;
                if (sheet == null) continue;
                long id = Rid.Value(e.Id);

                var f = new SheetStateFact { ElementId = id };
                try { f.UniqueId = e.UniqueId; } catch { f.UniqueId = null; }
                try { f.Number = sheet.SheetNumber; } catch { f.NumberReadable = false; }
                bool bad;
                f.Name = SafeName(e, out bad);
                f.NameReadable = !bad;
                try { f.IsPlaceholder = sheet.IsPlaceholder; } catch { f.IsPlaceholder = null; }

                int n;
                // A sheet nothing was counted for has ZERO title blocks, which is a
                // real answer. Unreadable is recorded globally, not turned into a
                // per-sheet zero.
                f.TitleBlockCount = titleBlocks.TryGetValue(id, out n) ? n : 0;
                if (titleBlocksUnreadable > 0) f.Unreadable.Add("title_blocks");
                f.ScheduleInstanceCount = schedules.TryGetValue(id, out n) ? n : 0;

                try
                {
                    ICollection<ElementId> vps = sheet.GetAllViewports();
                    f.ViewportCount = vps.Count;
                    foreach (ElementId vpId in vps)
                    {
                        var vp = doc.GetElement(vpId) as Viewport;
                        if (vp == null) { viewportsUnreadable++; continue; }
                        viewportRows.Add(ViewportJson(doc, vp, f.Number));
                    }
                }
                catch { f.Unreadable.Add("viewports"); }

                try { f.RevisionCount = sheet.GetAllRevisionIds().Count; }
                catch { f.Unreadable.Add("revisions"); }

                facts.Add(f);
            }

            List<string> duplicates = SheetAnnotationRules.DuplicateNumbers(facts);
            List<SheetFinding> findings = SheetAnnotationRules.Judge(facts, rules);

            var sheetRows = facts
                .OrderBy(f => f.Number, StringComparer.Ordinal).ThenBy(f => f.ElementId)
                .Select(f => (JToken)SheetAnnotationRules.ToJson(f))
                .ToList();

            var findingRows = findings
                .Select(x => (JToken)new JObject
                {
                    ["code"] = x.Code,
                    ["sheet_id"] = x.SheetId,
                    ["sheet_number"] = x.SheetNumber,
                    ["detail"] = x.Detail
                })
                .ToList();

            return new JObject
            {
                ["sheets_total"] = facts.Count,
                ["sheets_empty"] = facts.Count(f => f.IsEmpty),
                ["sheets_without_title_block"] =
                    facts.Count(f => !f.Unreadable.Contains("title_blocks") && f.TitleBlockCount == 0),
                ["sheets_with_multiple_title_blocks"] =
                    facts.Count(f => !f.Unreadable.Contains("title_blocks") && f.TitleBlockCount > 1),
                ["duplicate_numbers"] = new JArray(duplicates.Select(x => (JToken)x)),
                ["title_blocks_unreadable"] = titleBlocksUnreadable,
                ["schedule_instances_unreadable"] = schedulesUnreadable,
                ["viewports_unreadable"] = viewportsUnreadable,
                ["emptiness_means"] = SheetAnnotationRules.EmptinessMeans,
                ["rules"] = SheetRulesStatus(rules),
                ["sheets"] = paging.Bucket(sheetRows, "sheets", "sheets"),
                ["viewports"] = paging.Bucket(viewportRows, "sheets", "viewports"),
                ["findings"] = paging.Bucket(findingRows, "sheets", "findings")
            };
        }

        private static JObject ViewportJson(Document doc, Viewport vp, string sheetNumber)
        {
            var o = new JObject { ["viewport_id"] = Rid.Value(vp.Id), ["sheet_number"] = sheetNumber };
            try { o["view_id"] = Rid.Value(vp.ViewId); } catch { o["view_id"] = null; }
            try { o["view_name"] = SafeName(doc.GetElement(vp.ViewId)); } catch { o["view_name"] = null; }
            try { o["type_name"] = SafeName(doc.GetElement(vp.GetTypeId())); } catch { o["type_name"] = null; }
            try
            {
                XYZ c = vp.GetBoxCenter();
                o["center_x_mm"] = c.X * 304.8;
                o["center_y_mm"] = c.Y * 304.8;
            }
            catch { o["center_x_mm"] = null; o["center_y_mm"] = null; }
            try { o["rotation"] = vp.Rotation.ToString(); } catch { o["rotation"] = null; }
            try
            {
                Parameter p = vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
                o["detail_number"] = p == null ? null : p.AsString();
            }
            catch { o["detail_number"] = null; }
            // The label outline is observable; whether the label is VISIBLE is a
            // type property this read does not resolve, so it is not claimed.
            try { o["label_outline_readable"] = vp.GetLabelOutline() != null; }
            catch { o["label_outline_readable"] = false; }
            return o;
        }

        // ============================== annotations ===============================
        //
        // Counted per view and per KIND. A dimension and a tag are not
        // interchangeable, and neither is evidence that a view is documented.
        //
        // Only view-specific elements are counted: a door tag lives in one view, the
        // door does not. Mixing them produces a documentation count that grows when
        // somebody models a wall.
        private static JObject AnnotationsSection(Document doc, ScanPagingContext paging, SheetRules rules)
        {
            var byView = new Dictionary<long, AnnotationCensus>();
            long unreadable = 0, notViewSpecific = 0;

            foreach (KeyValuePair<string, BuiltInCategory> kind in AnnotationCategories)
            {
                foreach (Element e in new FilteredElementCollector(doc)
                         .OfCategory(kind.Value).WhereElementIsNotElementType())
                {
                    try
                    {
                        if (!e.ViewSpecific) { notViewSpecific++; continue; }
                        long viewId = Rid.Value(e.OwnerViewId);
                        AnnotationCensus c;
                        if (!byView.TryGetValue(viewId, out c))
                        {
                            var v = doc.GetElement(e.OwnerViewId) as View;
                            c = new AnnotationCensus
                            {
                                ViewId = viewId,
                                ViewName = v == null ? null : SafeName(v),
                                ViewType = v == null ? null : SafeViewType(v)
                            };
                            byView[viewId] = c;
                        }
                        long had;
                        c.ByKind[kind.Key] = c.ByKind.TryGetValue(kind.Key, out had) ? had + 1 : 1;
                    }
                    catch { unreadable++; }
                }
            }

            // TAGS and FILLED REGIONS span many categories each - there is no single
            // OST_Tags - so they are collected by CLASS. Counting a fixed list of
            // tag categories would silently miss every tag category not on it.
            foreach (KeyValuePair<string, Type> byClass in new[]
                     {
                         new KeyValuePair<string, Type>(AnnotationKinds.Tags, typeof(IndependentTag)),
                         new KeyValuePair<string, Type>(AnnotationKinds.FilledRegions, typeof(FilledRegion))
                     })
            {
                foreach (Element e in new FilteredElementCollector(doc).OfClass(byClass.Value))
                {
                    try
                    {
                        if (!e.ViewSpecific) { notViewSpecific++; continue; }
                        long viewId = Rid.Value(e.OwnerViewId);
                        AnnotationCensus c;
                        if (!byView.TryGetValue(viewId, out c))
                        {
                            var v = doc.GetElement(e.OwnerViewId) as View;
                            c = new AnnotationCensus
                            {
                                ViewId = viewId,
                                ViewName = v == null ? null : SafeName(v),
                                ViewType = v == null ? null : SafeViewType(v)
                            };
                            byView[viewId] = c;
                        }
                        long had;
                        c.ByKind[byClass.Key] = c.ByKind.TryGetValue(byClass.Key, out had) ? had + 1 : 1;
                    }
                    catch { unreadable++; }
                }
            }

            List<AnnotationCensus> below = SheetAnnotationRules.BelowMinimum(byView.Values, rules);

            var rows = byView.Values
                .OrderByDescending(c => c.Total).ThenBy(c => c.ViewId)
                .Select(c => (JToken)SheetAnnotationRules.ToJson(c))
                .ToList();

            return new JObject
            {
                ["views_with_annotations"] = byView.Count,
                ["annotations_total"] = byView.Values.Sum(c => c.Total),
                ["annotations_unreadable"] = unreadable,
                ["model_elements_skipped"] = notViewSpecific,
                ["population"] = "view-specific elements only. A door tag lives in one view; the door does not, " +
                                 "and counting both produces a documentation number that grows when somebody " +
                                 "models a wall.",
                ["means"] = SheetAnnotationRules.AnnotationMeans,
                ["views_below_declared_minimum"] = below.Count,
                ["by_view"] = paging.Bucket(rows, "annotations", "by_view")
            };
        }

        private static string SafeViewType(View v)
        {
            try { return v.ViewType.ToString(); } catch { return null; }
        }

        private static void Add(Dictionary<long, int> into, long key)
        {
            int had;
            into[key] = into.TryGetValue(key, out had) ? had + 1 : 1;
        }

        /// <summary>
        /// The annotation kinds, each with the category that actually holds it.
        /// Verified present in Revit 2023 through 2027 by reflection over each
        /// RevitAPI.dll rather than assumed, so no kind is silently absent on one
        /// year and counted on another.
        /// </summary>
        private static readonly KeyValuePair<string, BuiltInCategory>[] AnnotationCategories =
        {
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.Dimensions, BuiltInCategory.OST_Dimensions),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.Text, BuiltInCategory.OST_TextNotes),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.GenericAnnotations, BuiltInCategory.OST_GenericAnnotation),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.DetailItems, BuiltInCategory.OST_DetailComponents),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.MaskingRegions, BuiltInCategory.OST_MaskingRegion),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.RevisionClouds, BuiltInCategory.OST_RevisionClouds),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.Callouts, BuiltInCategory.OST_Callouts),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.Sections, BuiltInCategory.OST_Sections),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.Elevations, BuiltInCategory.OST_Elev),
            new KeyValuePair<string, BuiltInCategory>(AnnotationKinds.Keynotes, BuiltInCategory.OST_KeynoteTags),
        };

        private static JObject SheetRulesStatus(SheetRules r)
        {
            if (r == null) return new JObject { ["status"] = "not_requested" };
            if (r.Absent) return new JObject { ["status"] = "not_requested", ["means"] = r.Message };
            if (!r.Ok) return new JObject { ["status"] = "refused", ["code"] = r.Code, ["message"] = r.Message };
            return new JObject { ["status"] = "ok", ["version"] = r.Version };
        }

        // ================================= views ==================================
        //
        // EVERY READ IS GUARDED AND NAMED. A property whose read threw goes into
        // the view's `unreadable` set, so the judgement reports not_readable rather
        // than turning a failed read into a failed rule. A property the view type
        // does not HAVE never reaches a read at all - Core/ViewFactsRules decides
        // that from the view type, so a legend is never asked for its level.
        private static JObject ViewsSection(Document doc, ScanPagingContext paging, ViewProfile profile)
        {
            // Which views sit on a sheet, gathered once. Asking each view whether it
            // is placed is a search per view; this is one pass.
            var onSheet = new Dictionary<long, string>();
            long viewportsUnreadable = 0;
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Viewport)))
            {
                var vp = e as Viewport;
                if (vp == null) continue;
                try
                {
                    var sheet = doc.GetElement(vp.SheetId) as ViewSheet;
                    onSheet[Rid.Value(vp.ViewId)] = sheet == null ? "(sheet unreadable)" : sheet.SheetNumber;
                }
                catch { viewportsUnreadable++; }
            }

            var facts = new List<ViewStateFact>();
            var verdicts = new List<List<ViewPropertyVerdict>>();
            long templates = 0, internalViews = 0;

            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                var v = e as View;
                if (v == null) continue;
                // A sheet is a View. It is documented by its own section, not here.
                if (v is ViewSheet) continue;

                var f = new ViewStateFact { ElementId = Rid.Value(e.Id) };
                try { f.UniqueId = e.UniqueId; } catch { f.UniqueId = null; }
                bool bad;
                f.Name = SafeName(e, out bad);
                f.NameReadable = !bad;

                try { f.ViewType = v.ViewType.ToString(); }
                catch { f.ViewType = null; }

                if (ViewApplicability.IsInternal(f.ViewType)) { internalViews++; continue; }

                try { f.IsTemplate = v.IsTemplate; } catch { f.IsTemplate = false; }
                if (f.IsTemplate) templates++;

                Read(f, ViewProperties.Template, () =>
                {
                    ElementId tid = v.ViewTemplateId;
                    f.TemplateAssigned = tid != null && tid != ElementId.InvalidElementId;
                    f.TemplateName = f.TemplateAssigned ? SafeName(doc.GetElement(tid)) : null;
                });
                Read(f, ViewProperties.Scale, () => { f.Scale = v.Scale; });
                Read(f, ViewProperties.DetailLevel, () => { f.DetailLevel = v.DetailLevel.ToString(); });
                Read(f, ViewProperties.Discipline, () => { f.Discipline = v.Discipline.ToString(); });
                Read(f, ViewProperties.CropActive, () =>
                {
                    f.CropActive = v.CropBoxActive;
                    f.CropVisible = v.CropBoxVisible;
                });
                Read(f, ViewProperties.Level, () => { f.LevelName = v.GenLevel == null ? null : v.GenLevel.Name; });
                Read(f, ViewProperties.Phase, () => { f.Phase = ParamString(v, BuiltInParameter.VIEW_PHASE); });
                Read(f, ViewProperties.PhaseFilter,
                     () => { f.PhaseFilter = ParamString(v, BuiltInParameter.VIEW_PHASE_FILTER); });
                Read(f, ViewProperties.ScopeBox,
                     () => { f.ScopeBox = ParamString(v, BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP); });
                // PLAN_VIEW_NORTH exists in every supported year - verified by
                // reflection over each RevitAPI.dll, see the evidence doc. This was
                // the per-view orientation gap story 3 had recorded as open.
                try { f.NorthOrientation = ParamString(v, BuiltInParameter.PLAN_VIEW_NORTH); }
                catch { f.NorthOrientation = null; }
                Read(f, ViewProperties.Filters, () =>
                {
                    foreach (ElementId fid in v.GetFilters())
                        f.Filters.Add(SafeName(doc.GetElement(fid)) ?? "(filter unreadable)");
                });

                try { f.AnnotationCrop = ParamBool(v, BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE); }
                catch { f.AnnotationCrop = null; }

                // A dependent view has a primary. Reported as a fact: a dependent
                // view lacking its own template is normal, not a finding.
                try
                {
                    ElementId primary = v.GetPrimaryViewId();
                    f.IsDependent = primary != null && primary != ElementId.InvalidElementId;
                    f.PrimaryViewName = f.IsDependent == true ? SafeName(doc.GetElement(primary)) : null;
                }
                catch { f.IsDependent = null; }

                try
                {
                    int overridden = 0, hidden = 0;
                    foreach (Category c in doc.Settings.Categories)
                    {
                        try
                        {
                            if (!c.get_AllowsVisibilityControl(v)) continue;
                            if (!c.get_Visible(v)) hidden++;
                            OverrideGraphicSettings og = v.GetCategoryOverrides(c.Id);
                            if (og != null && !IsEmptyOverride(og)) overridden++;
                        }
                        catch { }
                    }
                    f.OverriddenCategories = overridden;
                    f.HiddenCategories = hidden;
                }
                catch { }

                string sheetNumber;
                f.PlacedOnSheet = onSheet.TryGetValue(f.ElementId, out sheetNumber);
                f.SheetNumber = sheetNumber;

                facts.Add(f);
                // A TEMPLATE IS NOT A VIEW. It is reported, and never judged against
                // rules written for drawings - including "has no template".
                verdicts.Add(f.IsTemplate ? new List<ViewPropertyVerdict>()
                                          : ViewFactsRules.Judge(f, profile));
            }

            var rows = new List<JToken>();
            for (int i = 0; i < facts.Count; i++) rows.Add(ViewFactsRules.ToJson(facts[i], verdicts[i]));

            long placed = facts.Count(f => !f.IsTemplate && f.PlacedOnSheet);
            long notPlaced = facts.Count(f => !f.IsTemplate && !f.PlacedOnSheet);

            return new JObject
            {
                ["views_total"] = facts.Count,
                ["view_templates"] = templates,
                ["views_excluding_templates"] = facts.Count - templates,
                ["internal_views_excluded"] = internalViews,
                ["views_on_sheets"] = placed,
                ["views_not_on_sheets"] = notPlaced,
                ["viewports_unreadable"] = viewportsUnreadable,
                ["templates_mean"] = ViewFactsRules.TemplatesMean,
                ["profile"] = ViewProfileStatus(profile),
                ["property_tally"] = ViewFactsRules.Tally(verdicts),
                ["views"] = paging.Bucket(rows, "views", "views")
            };
        }

        /// <summary>
        /// Runs one guarded read. A throw names the PROPERTY in the fact's
        /// unreadable set, so the judgement can answer not_readable instead of
        /// turning a failed read into a failed rule.
        /// </summary>
        private static void Read(ViewStateFact f, string property, Action read)
        {
            try { read(); }
            catch { f.Unreadable.Add(property); }
        }

        private static string ParamString(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            if (p == null) return null;
            string s = p.AsValueString();
            return string.IsNullOrEmpty(s) ? p.AsString() : s;
        }

        private static bool? ParamBool(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            return p == null ? (bool?)null : p.AsInteger() != 0;
        }

        /// <summary>
        /// Whether an override actually overrides anything. A default
        /// OverrideGraphicSettings is returned for every category in every view, so
        /// counting non-null would report every view as fully overridden.
        /// </summary>
        private static bool IsEmptyOverride(OverrideGraphicSettings o)
        {
            try
            {
                return o.ProjectionLineColor != null && !o.ProjectionLineColor.IsValid
                       && o.CutLineColor != null && !o.CutLineColor.IsValid
                       && o.ProjectionLineWeight == OverrideGraphicSettings.InvalidPenNumber
                       && o.CutLineWeight == OverrideGraphicSettings.InvalidPenNumber
                       && o.Halftone == false
                       && o.Transparency == 0;
            }
            catch { return true; }
        }

        private static JObject ViewProfileStatus(ViewProfile p)
        {
            if (p == null) return new JObject { ["status"] = "not_requested" };
            if (p.Absent) return new JObject { ["status"] = "not_requested", ["means"] = p.Message };
            if (!p.Ok)
                return new JObject { ["status"] = "refused", ["code"] = p.Code, ["message"] = p.Message };
            return new JObject { ["status"] = "ok", ["version"] = p.Version };
        }

        // ================================ families ================================
        //
        // NOTHING HERE OPENS AN .rfa. Opening a family document changes the active
        // document, and a diagnostic that changes what it is measuring is not one.
        // Everything below is read from the project.
        //
        // The three kinds are collected by two different routes because they ARE
        // two different things: loadable and in-place families are Family elements;
        // a system family is not, and exists only as a group of ElementTypes that
        // share a FamilyName. A census built on OfClass(Family) alone reports fewer
        // families than the model has, silently.
        private static JObject FamiliesSection(Document doc, ScanPagingContext paging,
                                               FamilyProfile profile, int candidateBudget)
        {
            var facts = new List<FamilyFact>();

            // ---- instances first, so type usage and distribution are known ----
            var instancesByType = new Dictionary<long, long>();
            var worksetByFamily = new Dictionary<long, Dictionary<string, long>>();
            var hostByFamily = new Dictionary<long, Dictionary<string, long>>();
            var nestedDepthByFamily = new Dictionary<long, int>();
            long instancesUnreadableGlobal = 0;

            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
            {
                var fi = e as FamilyInstance;
                if (fi == null) continue;
                long famId;
                try
                {
                    FamilySymbol sym = fi.Symbol;
                    if (sym == null) { instancesUnreadableGlobal++; continue; }
                    long typeId = Rid.Value(sym.Id);
                    long had;
                    instancesByType[typeId] = instancesByType.TryGetValue(typeId, out had) ? had + 1 : 1;
                    famId = Rid.Value(sym.Family.Id);
                }
                catch
                {
                    // The symbol or its family would not read. The instance exists and
                    // is counted as unreadable; it is NOT attributed to any family,
                    // because guessing which one would inflate that family's count.
                    instancesUnreadableGlobal++;
                    continue;
                }

                Bump(worksetByFamily, famId, WorksetNameOf(doc, e));
                Bump(hostByFamily, famId, HostCategoryOf(fi));

                try
                {
                    int depth = 0;
                    Element parent = fi.SuperComponent;
                    while (parent != null && depth < 32)
                    {
                        depth++;
                        parent = (parent as FamilyInstance)?.SuperComponent;
                    }
                    int seen;
                    if (!nestedDepthByFamily.TryGetValue(famId, out seen) || depth > seen)
                        nestedDepthByFamily[famId] = depth;
                }
                catch { }
            }

            // ---- loadable and in-place families ----
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Family)))
            {
                var fam = e as Family;
                if (fam == null) continue;
                var f = new FamilyFact { ElementId = Rid.Value(e.Id) };
                try { f.UniqueId = e.UniqueId; } catch { f.UniqueId = null; }
                bool bad;
                f.Name = SafeName(e, out bad);
                f.NameReadable = !bad;
                try { f.Category = fam.FamilyCategory == null ? null : fam.FamilyCategory.Name; }
                catch { f.Category = null; }

                try { f.IsInPlace = fam.IsInPlace; } catch { f.IsInPlace = null; }
                f.Kind = f.IsInPlace == null ? FamilyKind.Unreadable
                       : f.IsInPlace.Value ? FamilyKind.InPlace
                       : FamilyKind.Loadable;

                // FAMILY_SHARED is often absent. Absent is null, never false: false
                // would be the model telling us the family is not shared, and it
                // has not said that.
                try
                {
                    Parameter shared = fam.get_Parameter(BuiltInParameter.FAMILY_SHARED);
                    f.IsShared = shared == null ? (bool?)null : shared.AsInteger() != 0;
                }
                catch { f.IsShared = null; }

                try
                {
                    ICollection<ElementId> symbolIds = fam.GetFamilySymbolIds();
                    f.TypeCount = symbolIds.Count;
                    foreach (ElementId sid in symbolIds)
                    {
                        long n;
                        long used = instancesByType.TryGetValue(Rid.Value(sid), out n) ? n : 0;
                        if (used == 0) f.UnusedTypeCount++;
                        f.InstanceCount += used;
                    }
                }
                catch { f.UnreadableTypeCount++; }

                try { f.ParameterCount = fam.Parameters == null ? 0 : fam.Parameters.Size; }
                catch { f.ParametersReadable = false; }

                Dictionary<string, long> d;
                if (worksetByFamily.TryGetValue(f.ElementId, out d)) f.WorksetDistribution = d;
                if (hostByFamily.TryGetValue(f.ElementId, out d)) f.HostDistribution = d;

                // Null when nothing is placed. Zero would claim a depth was observed.
                int depthSeen;
                f.NestedDepthObserved = nestedDepthByFamily.TryGetValue(f.ElementId, out depthSeen)
                    ? depthSeen : (int?)null;

                facts.Add(f);
            }

            // ---- system families: grouped ElementTypes, not Family elements ----
            var systemGroups = new Dictionary<string, FamilyFact>(StringComparer.Ordinal);
            long typesUnreadable = 0;
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                var et = e as ElementType;
                if (et == null) continue;
                if (et is FamilySymbol) continue;    // that is a loadable family's type

                string famName, catName;
                try { famName = et.FamilyName; } catch { typesUnreadable++; continue; }
                if (string.IsNullOrEmpty(famName)) famName = "(unnamed system family)";
                try { catName = et.Category == null ? null : et.Category.Name; } catch { catName = null; }

                string key = (catName ?? "(no category)") + "" + famName;
                FamilyFact f;
                if (!systemGroups.TryGetValue(key, out f))
                {
                    f = new FamilyFact
                    {
                        ElementId = -1,          // a system family has no Family element
                        Name = famName,
                        Category = catName,
                        Kind = FamilyKind.System,
                        IsInPlace = false,
                        // Sharing is a loadable-family concept; it is not absent
                        // because a read failed, so it is reported as false rather
                        // than counted among the unreadable.
                        IsShared = false
                    };
                    systemGroups[key] = f;
                }
                f.TypeCount++;
                long used;
                long n = instancesByType.TryGetValue(Rid.Value(e.Id), out used) ? used : 0;
                if (n == 0) f.UnusedTypeCount++;
                f.InstanceCount += n;
            }
            facts.AddRange(systemGroups.Values);

            JObject totals = FamilyCensusRules.Totals(facts);
            totals["types_unreadable_ungrouped"] = typesUnreadable;
            totals["instances_unattributable"] = instancesUnreadableGlobal;

            var rows = facts
                .OrderBy(f => f.Kind, StringComparer.Ordinal)
                .ThenBy(f => f.Category, StringComparer.Ordinal)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => (JToken)FamilyCensusRules.ToJson(f))
                .ToList();

            List<FamilyFinding> findings = FamilyCensusRules.Judge(facts, profile);
            var findingRows = findings
                .Select(x => (JToken)new JObject
                {
                    ["code"] = x.Code,
                    ["family"] = x.FamilyName,
                    ["family_id"] = x.ElementId,
                    ["detail"] = x.Detail
                })
                .ToList();

            var o = new JObject();
            foreach (JProperty prop in totals.Properties()) o[prop.Name] = prop.Value;
            o["opened_no_family_document"] = true;
            o["profile"] = FamilyProfileStatus(profile);
            o["families"] = paging.Bucket(rows, "families", "families");
            o["findings"] = paging.Bucket(findingRows, "families", "findings");
            o["candidates"] = FamilyCensusRules.Candidates(facts, candidateBudget);
            return o;
        }

        private static JObject FamilyProfileStatus(FamilyProfile p)
        {
            if (p == null) return new JObject { ["status"] = "not_requested" };
            if (p.Absent) return new JObject { ["status"] = "not_requested", ["means"] = p.Message };
            if (!p.Ok)
                return new JObject { ["status"] = "refused", ["code"] = p.Code, ["message"] = p.Message };
            return new JObject { ["status"] = "ok", ["version"] = p.Version };
        }

        private static void Bump(Dictionary<long, Dictionary<string, long>> into, long key, string bucket)
        {
            Dictionary<string, long> d;
            if (!into.TryGetValue(key, out d)) { d = new Dictionary<string, long>(StringComparer.Ordinal); into[key] = d; }
            long had;
            d[bucket] = d.TryGetValue(bucket, out had) ? had + 1 : 1;
        }

        private static string WorksetNameOf(Document doc, Element e)
        {
            try
            {
                Workset w = doc.GetWorksetTable().GetWorkset(e.WorksetId);
                return w == null ? "(workset unreadable)" : w.Name;
            }
            catch { return "(workset unreadable)"; }
        }

        /// <summary>
        /// The host's CATEGORY, not its id: a distribution over ids is one row per
        /// element and answers nothing. An unhosted family is "(none)", which is a
        /// real answer and different from a host that would not read.
        /// </summary>
        private static string HostCategoryOf(FamilyInstance fi)
        {
            try
            {
                Element host = fi.Host;
                if (host == null) return "(none)";
                return host.Category == null ? "(host without category)" : host.Category.Name;
            }
            catch { return "(host unreadable)"; }
        }

        // ============================== worksharing ===============================
        //
        // The ownership question used to be answerable only by RELINQUISHING - the
        // one call that gave the number also changed the model, for everyone.
        // GetCheckoutStatus reads and takes nothing, and this section is built on
        // it: nothing here mutates, and nothing here is inside a transaction.
        private static JObject WorksharingSection(Document doc, ScanPagingContext paging, string me)
        {
            var o = new JObject();

            bool? workshared = null, detached = null, inCloud = null;
            string worksharedError = null;
            try { workshared = doc.IsWorkshared; }
            catch (Exception ex) { worksharedError = ex.Message; }
            try { detached = doc.IsDetached; } catch { detached = null; }
            try { inCloud = doc.IsModelInCloud; } catch { inCloud = null; }

            o["is_workshared"] = workshared;
            o["is_workshared_error"] = worksharedError;
            o["is_detached"] = detached;
            o["is_model_in_cloud"] = inCloud;
            o["current_user"] = me;

            string central = null, centralError = null;
            try
            {
                ModelPath mp = workshared == true ? doc.GetWorksharingCentralModelPath() : null;
                central = mp == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
            }
            catch (Exception ex) { centralError = ex.Message; }
            o["central_model_path"] = central;
            o["central_model_path_error"] = centralError;

            // A DOCUMENT THAT WAS NEVER WORKSHARED IS NOT A WORKSHARING PROBLEM.
            // It has no ownership at all, so the census is absent rather than four
            // zeros - four zeros are a census that ran and found nothing.
            if (workshared != true)
            {
                o["ownership"] = OwnershipCensus.NotApplicable(
                    worksharedError != null
                        ? ("whether this document is workshared could not be read (" + worksharedError +
                           "), so no ownership census was attempted.")
                        : "this document is not workshared, so no element has an owner.");
                o["population"] = null;
                return o;
            }

            var tally = new OwnershipTally();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                long id = Rid.Value(e.Id);
                try
                {
                    CheckoutStatus status = WorksharingUtils.GetCheckoutStatus(doc, e.Id);
                    if (status == CheckoutStatus.OwnedByCurrentUser)
                    {
                        tally.Count(CheckoutState.Me, null, id);
                    }
                    else if (status == CheckoutStatus.NotOwned)
                    {
                        tally.Count(CheckoutState.NoOne, null, id);
                    }
                    else
                    {
                        // Owned by somebody else. The NAME is a second read that can
                        // fail on its own, and failing it does not make the element
                        // unowned - the census keeps it owned and says the name is
                        // what could not be read.
                        string owner = null;
                        try { owner = WorksharingUtils.GetWorksharingTooltipInfo(doc, e.Id).Owner; }
                        catch { owner = null; }
                        tally.Count(CheckoutState.Others, owner, id);
                    }
                }
                catch
                {
                    tally.Count(CheckoutState.Unreadable, null, id);
                }
            }

            JObject census = OwnershipCensus.ToJson(tally);
            var otherIds = tally.OwnedByOthersIds.Select(x => (JToken)x).ToList();
            census["owned_by_others_ids"] = paging.Bucket(otherIds, "worksharing", "owned_by_others_ids");
            o["ownership"] = census;
            o["population"] = "every element that is not an element type. Types are not owned individually, so " +
                              "including them would inflate the denominator with things that cannot be borrowed.";
            return o;
        }

        // ============================== coordinates ===============================
        // WHERE THE MODEL THINKS IT IS, as facts. Every judgement about these
        // numbers lives in Core/CoordinateRules and is applied by audit_model; this
        // section reads and reports, and calls the SAME reader audit_model calls so
        // the two tools can never disagree about the same document.
        private static JObject CoordinatesSection(Document doc, ScanPagingContext paging)
        {
            CoordinateFacts f = DiagnosticsFacts.ReadCoordinates(doc, CoordinateRules.DefaultFarRadiusMm);

            double? farthest;
            long beyond = CoordinateRules.CountBeyond(f.Outliers, CoordinateRules.DefaultFarRadiusMm, out farthest);

            var outlierRows = f.Outliers
                .OrderByDescending(o => o.DistanceMm)
                .Select(o => (JToken)new JObject
                {
                    ["id"] = o.ElementId,
                    ["category"] = o.Category,
                    ["name"] = o.Name,
                    ["distance_from_internal_origin_mm"] = o.DistanceMm
                })
                .ToList();

            var linkRows = f.Links
                .Select(l => (JToken)new JObject
                {
                    ["instance_id"] = l.InstanceId,
                    ["name"] = l.Name,
                    ["transform_readable"] = l.TransformReadable,
                    ["why"] = l.Why,
                    ["origin_offset_mm"] = l.TransformReadable ? (JToken)l.OriginOffsetMm : null,
                    ["has_rotation"] = l.TransformReadable ? (JToken)l.HasRotation : null,
                    ["has_reflection"] = l.TransformReadable ? (JToken)l.HasReflection : null,
                    // NULL, AND NOW WITH THE REASON. Not "cannot yet" - the API has
                    // no read path at all, established by reflection over all five
                    // supported years. See CoordinateRules.SharedPositionNotObservable.
                    ["shared_position_matches_host"] = l.SharedPositionMatchesHost,
                    ["shared_position_means"] = CoordinateRules.SharedPositionNotObservable
                })
                .ToList();

            return new JObject
            {
                ["control_points"] = new JObject
                {
                    ["internal_origin"] = PointJson(f.InternalOrigin),
                    ["project_base_point"] = PointJson(f.ProjectBasePoint),
                    ["survey_point"] = PointJson(f.SurveyPoint),
                    ["means"] = "three DIFFERENT points. The internal origin is Revit's own (0,0,0) and cannot " +
                                "move; the project base point and the survey point are decisions somebody made. " +
                                "A survey point far from the internal origin is normal and is what it is for."
                },
                ["project_location"] = new JObject
                {
                    ["readable"] = f.LocationReadable,
                    ["why"] = f.LocationWhy,
                    ["active_name"] = f.ActiveLocationName,
                    ["named_location_count"] = f.NamedLocationCount
                },
                ["true_north"] = new JObject
                {
                    ["readable"] = f.TrueNorthReadable,
                    ["degrees"] = f.TrueNorthDegrees,
                    // Zero is a value, not a blank: it means project north and true
                    // north agree, which is an ordinary thing for a model to say.
                    ["means"] = "degrees from project north to true north. 0 is a real answer - the two agree - " +
                                "and is not evidence that nobody set it."
                },
                ["site_location"] = new JObject
                {
                    ["readable"] = f.SiteReadable,
                    ["why"] = f.SiteWhy,
                    ["latitude_degrees"] = f.LatitudeDegrees,
                    ["longitude_degrees"] = f.LongitudeDegrees,
                    ["place_name"] = f.PlaceName,
                    ["time_zone_hours"] = f.TimeZoneHours,
                    ["means"] = CoordinateRules.SiteMeans
                },
                ["units"] = new JObject
                {
                    ["readable"] = f.UnitsReadable,
                    ["length_unit"] = f.LengthUnitName
                },
                ["geometry_extent"] = new JObject
                {
                    ["elements_measured"] = f.ElementsMeasured,
                    ["elements_unreadable"] = f.ElementsUnreadable,
                    ["farthest_element_mm"] = farthest,
                    ["radius_used_mm"] = CoordinateRules.DefaultFarRadiusMm,
                    ["beyond_radius_count"] = beyond,
                    ["note"] = CoordinateRules.OriginNote(beyond, CoordinateRules.DefaultFarRadiusMm,
                                                          f.ElementsMeasured, f.ElementsUnreadable),
                    ["means"] = CoordinateRules.DistanceMeans
                },
                ["outliers"] = paging.Bucket(outlierRows, "coordinates", "outliers"),
                ["links"] = paging.Bucket(linkRows, "coordinates", "links")
            };
        }

        private static JObject PointJson(PointFact p)
        {
            if (p == null) return null;
            return new JObject
            {
                ["readable"] = p.Readable,
                ["why"] = p.Why,
                ["x_mm"] = p.Readable ? (JToken)p.XMm : null,
                ["y_mm"] = p.Readable ? (JToken)p.YMm : null,
                ["z_mm"] = p.Readable ? (JToken)p.ZMm : null,
                ["distance_from_internal_origin_mm"] =
                    p.Readable ? (JToken)p.DistanceFromInternalOriginMm : null,
                ["clipped"] = p.Clipped
            };
        }

        /// <summary>
        /// Every scope box in the document, with ITS OWN bounding box.
        ///
        /// The extents come from the scope box element, never from the elements it
        /// crops - see Core/ScopeBoxRules for why that substitution would be a
        /// guess shaped like a measurement.
        /// </summary>
        private static Dictionary<long, ScopeBoxFact> ReadScopeBoxes(Document doc)
        {
            var boxes = new Dictionary<long, ScopeBoxFact>();
            try
            {
                foreach (Element e in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType())
                {
                    var f = new ScopeBoxFact { ElementId = Rid.Value(e.Id) };
                    bool bad;
                    f.Name = SafeName(e, out bad);
                    f.NameReadable = !bad;
                    try
                    {
                        BoundingBoxXYZ bb = e.get_BoundingBox(null);
                        if (bb == null) f.GeometryReadable = false;
                        else
                        {
                            f.MinXMm = bb.Min.X * 304.8; f.MinYMm = bb.Min.Y * 304.8; f.MinZMm = bb.Min.Z * 304.8;
                            f.MaxXMm = bb.Max.X * 304.8; f.MaxYMm = bb.Max.Y * 304.8; f.MaxZMm = bb.Max.Z * 304.8;
                        }
                    }
                    catch { f.GeometryReadable = false; }
                    boxes[f.ElementId] = f;
                }
            }
            catch { }
            return boxes;
        }

        /// <summary>
        /// What one datum says about its scope box. DATUM_VOLUME_OF_INTEREST exists
        /// in every supported year - verified by reflection, see
        /// docs/evidence/coordinate-and-datum-api-evidence.md.
        /// </summary>
        private static ScopeBoxAssignment ReadScopeBoxAssignment(Element e, string ownerKind,
                                                                 BuiltInParameter bip,
                                                                 Dictionary<long, ScopeBoxFact> boxes)
        {
            var a = new ScopeBoxAssignment { OwnerId = Rid.Value(e.Id), OwnerKind = ownerKind };
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null) return a;                       // not assigned: a decision
                ElementId id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return a;

                a.ScopeBoxId = Rid.Value(id);
                ScopeBoxFact box;
                if (boxes.TryGetValue(a.ScopeBoxId.Value, out box))
                {
                    a.ScopeBoxName = box.Name;
                    // ASSIGNED STAYS ASSIGNED. A box whose extents will not come back
                    // has not stopped being the box this datum is scoped to.
                    a.GeometryMissing = !box.GeometryReadable;
                }
                else a.ScopeBoxName = "(scope box not found)";
            }
            catch { a.Readable = false; }
            return a;
        }

        // ================================= datums =================================
        // Levels and grids as facts. The duplicate/coincident/off-axis JUDGEMENTS
        // live in Core/DatumRules and are applied by audit_model over this same
        // reader, so a level is never measured twice by two different walks.
        private static JObject DatumsSection(Document doc, ScanPagingContext paging)
        {
            long levelNamesUnreadable, gridNamesUnreadable;
            List<LevelFact> levels = DiagnosticsFacts.ReadLevels(doc, out levelNamesUnreadable);
            List<GridFact> grids = DiagnosticsFacts.ReadGrids(doc, out gridNamesUnreadable);

            Dictionary<long, ScopeBoxFact> scopeBoxes = ReadScopeBoxes(doc);
            var scopeAssignments = new List<ScopeBoxAssignment>();
            var scopeByOwner = new Dictionary<long, ScopeBoxAssignment>();
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Grid)))
            {
                ScopeBoxAssignment a = ReadScopeBoxAssignment(
                    e, "grid", BuiltInParameter.DATUM_VOLUME_OF_INTEREST, scopeBoxes);
                scopeAssignments.Add(a);
                scopeByOwner[a.OwnerId] = a;
            }
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                ScopeBoxAssignment a = ReadScopeBoxAssignment(
                    e, "level", BuiltInParameter.DATUM_VOLUME_OF_INTEREST, scopeBoxes);
                scopeAssignments.Add(a);
                scopeByOwner[a.OwnerId] = a;
            }

            var levelRows = levels
                .OrderBy(l => l.ElevationMm ?? double.MaxValue).ThenBy(l => l.ElementId)
                .Select(l => (JToken)new JObject
                {
                    ["id"] = l.ElementId,
                    ["name"] = l.Name,
                    ["name_readable"] = l.NameReadable,
                    ["elevation_mm"] = l.ElevationMm,
                    // NOT the same number as elevation_mm, and the difference is the
                    // whole reason a shared-elevation reading was unavailable before.
                    ["project_elevation_mm"] = l.ProjectElevationMm,
                    ["is_building_story"] = l.IsBuildingStory,
                    ["view_count"] = l.ViewCount,
                    ["scope_box"] = ScopeJson(scopeByOwner, l.ElementId)
                })
                .ToList();

            var gridRows = grids
                .OrderBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.ElementId)
                .Select(g => (JToken)new JObject
                {
                    ["id"] = g.ElementId,
                    ["name"] = g.Name,
                    ["name_readable"] = g.NameReadable,
                    ["geometry_readable"] = g.GeometryReadable,
                    ["why"] = g.Why,
                    ["is_curved"] = g.IsCurved,
                    ["x1_mm"] = g.X1Mm,
                    ["y1_mm"] = g.Y1Mm,
                    ["x2_mm"] = g.X2Mm,
                    ["y2_mm"] = g.Y2Mm,
                    ["length_mm"] = g.LengthMm,
                    ["scope_box"] = ScopeJson(scopeByOwner, g.ElementId)
                })
                .ToList();

            return new JObject
            {
                ["levels_total"] = levels.Count,
                ["level_names_unreadable"] = levelNamesUnreadable,
                ["grids_total"] = grids.Count,
                ["grid_names_unreadable"] = gridNamesUnreadable,
                ["levels"] = paging.Bucket(levelRows, "datums", "levels"),
                ["grids"] = paging.Bucket(gridRows, "datums", "grids"),
                ["scope_boxes_detail"] = paging.Bucket(
                    scopeBoxes.Values.OrderBy(b => b.Name, StringComparer.Ordinal)
                              .Select(b => (JToken)ScopeBoxRules.ToJson(b)).ToList(),
                    "datums", "scope_boxes_detail"),
                ["scope_box_summary"] = ScopeBoxRules.Tally(scopeAssignments, scopeBoxes.Values),
                ["means"] = "geometry, not a verdict. A curved grid reports is_curved with no endpoints rather " +
                            "than a length it does not have, and view_count 0 means no view is associated with " +
                            "that level - not that the level is unused. How many ELEMENTS sit on each level is " +
                            "a different question, answered by the 'level_association' section."
            };
        }

        private static JToken ScopeJson(Dictionary<long, ScopeBoxAssignment> byOwner, long ownerId)
        {
            ScopeBoxAssignment a;
            return byOwner.TryGetValue(ownerId, out a) ? ScopeBoxRules.ToJson(a) : null;
        }

        // ============================ level association ===========================
        private static JObject LevelAssociationSection(Document doc, ScanPagingContext paging)
        {
            LevelAssociationFacts f = DiagnosticsFacts.ReadLevelAssociation(doc);

            var byCategory = new JArray();
            foreach (KeyValuePair<string, long> kv in LevelAssociationRules.WithoutByCategoryRanked(f))
                byCategory.Add(new JObject { ["category"] = kv.Key, ["without_level"] = kv.Value });

            var byLevel = f.CountByLevel
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                .Select(kv => (JToken)new JObject
                {
                    ["level_id"] = kv.Key,
                    ["level_name"] = SafeName(SafeGet(doc, kv.Key)),
                    ["element_count"] = kv.Value
                })
                .ToList();

            var unassociatedRows = f.Unassociated
                .OrderBy(u => u.Category, StringComparer.Ordinal).ThenBy(u => u.ElementId)
                .Select(u => (JToken)new JObject
                {
                    ["id"] = u.ElementId,
                    ["category"] = u.Category,
                    ["name"] = u.Name
                })
                .ToList();

            return new JObject
            {
                ["examined"] = f.Examined,
                ["with_level"] = f.WithLevel,
                ["without_level"] = f.WithoutLevel,
                ["level_unreadable"] = f.Unreadable,
                // Null when nothing was measured. Neither 0 nor 100 - both would be
                // read as a result, and a census with nothing in it has none.
                ["percent_with_level"] = LevelAssociationRules.PercentWithLevel(f.WithLevel, f.WithoutLevel),
                ["counts_are_exact"] = LevelAssociationRules.IsExact(f.Unreadable),
                ["population"] = "model-category elements that are not types and not view-specific. Nothing was " +
                                 "dropped for 'not needing a level': a hidden exclusion list is one " +
                                 "organisation's opinion, so Levels and Grids appear in the breakdown below " +
                                 "reporting no level, exactly as the model says.",
                ["level_read"] = "Element.LevelId, Revit's own consolidated answer. A read that THREW is counted " +
                                 "in level_unreadable and excluded from the percentage - it is neither " +
                                 "associated nor unassociated.",
                ["note"] = LevelAssociationRules.Note(f),
                ["means"] = LevelAssociationRules.CensusMeans,
                ["without_by_category"] = byCategory,
                ["by_level"] = paging.Bucket(byLevel, "level_association", "by_level"),
                ["unassociated"] = paging.Bucket(unassociatedRows, "level_association", "unassociated")
            };
        }

        private static Element SafeGet(Document doc, long id)
        {
            try { return Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null; }
            catch { return null; }
        }

        // ================================= lines ==================================
        // This section returns COUNTS ONLY - it has no list to page, so it takes no
        // budget. Accepting one and ignoring it is the thing this whole change is
        // about, so it does not accept one.
        private static JObject LinesSection(Document doc)
        {
            int model = 0, detail = 0, unreadable = 0;
            foreach (var ce in new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
            {
                try { if (ce.ViewSpecific) detail++; else model++; }
                catch { unreadable++; }
            }

            return new JObject
            {
                ["model_lines"] = model,
                ["detail_lines"] = detail,
                // The python called this bucket "other", which reads like a third
                // kind of line. It is not: it is lines we failed to classify.
                ["view_specific_unreadable"] = unreadable,
                ["ost_lines_category"] = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Lines).WhereElementIsNotElementType().GetElementCount(),
                ["text_notes"] = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).GetElementCount(),
                ["filled_regions"] = new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).GetElementCount()
            };
        }

        // ================================= types ==================================
        // Instance method, unlike its neighbours: the type census is per-request
        // state, cleared on entry to Execute, because the dispatcher keeps one
        // handler alive for the whole session and a cache that outlived a request
        // would answer the next scan from a model that has since changed.
        private JObject TypesSection(Document doc, ScanPagingContext paging, string targetParam)
        {
            var census = CensusOf(doc);

            var rows = new List<JToken>();
            var noInstances = new List<JToken>();
            int familySymbols = 0, familySymbolsNoInstances = 0;

            foreach (var t in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                int n;
                if (!census.TryGetValue(t.Id.ToString(), out n)) n = 0;

                var sym = t as FamilySymbol;
                if (sym != null)
                {
                    familySymbols++;
                    if (n == 0) familySymbolsNoInstances++;
                }

                var rec = new JObject
                {
                    ["id"] = t.Id.ToString(),
                    ["category"] = CategoryOf(t),
                    ["family"] = FamilyNameOf(t),
                    ["name"] = SafeName(t),
                    ["instances"] = n,
                    ["is_family_symbol"] = sym != null
                };
                if (!string.IsNullOrEmpty(targetParam)) rec["target_parameter"] = ReadNamed(t, targetParam);

                rows.Add(rec);
                if (n == 0) noInstances.Add(rec.DeepClone());
            }

            // Every count on this section that says "zero instances" is only as complete
            // as the census behind it, so the census's own failures travel with them.
            var censusCaveat = _censusUnreadable == 0
                ? ""
                : " ALSO: " + _censusUnreadable + " element(s) would not report GetTypeId() during the census " +
                  "(first error: " + (_censusUnreadableError ?? "unknown") + "), so their types got no vote. " +
                  "`instances` is a LOWER bound and this list — plus family_symbols_no_instances — is inflated by " +
                  "however many types those elements use. A type in here may be in the building.";

            return new JObject
            {
                ["target_parameter"] = targetParam,
                ["types_total"] = rows.Count,
                ["family_symbols_total"] = familySymbols,
                ["family_symbols_no_instances"] = familySymbolsNoInstances,
                // Not folded into any type's count: an element we could not read is not
                // an element that uses nothing.
                ["census_unreadable_elements"] = _censusUnreadable,
                ["census_unreadable_error"] = _censusUnreadableError,
                ["types_no_instances"] = paging.Bucket(noInstances, "types", "types_no_instances"),
                ["types"] = paging.Bucket(rows, "types", "types"),
                // BOTH python engines derive "unused" from FilteredElementCollector
                // (doc).OfClass(FamilyInstance) alone. A wall/floor/roof/duct is not
                // a FamilyInstance, so every system-family type in the model came back
                // with zero users and got labelled purgable — the field in
                // preentrega_audit.py is literally named family_symbols_unused_aprox,
                // conceding the point. Deleting off that list deletes types that are
                // in the building. Here the census walks GetTypeId() over EVERY
                // non-elementtype element, so system families are counted like
                // anything else.
                ["instances_note"] = "instances comes from one census of GetTypeId() over ALL non-elementtype " +
                                     "elements, so system-family types (walls, floors, ducts) are counted — deriving " +
                                     "this from FamilyInstance alone reports every wall type as unused.",
                ["no_instances_note"] = "types_no_instances is NOT a purge list. A type with no instances can still " +
                                        "be referenced by another type (a compound wall's layers, a nested family, a " +
                                        "stacked wall), and those references are invisible to GetTypeId(). It is an " +
                                        "upper bound on what purge might remove. Purge with the real API and " +
                                        "re-verify inside the transaction." + censusCaveat
            };
        }

        private static JObject ReadNamed(Element e, string paramName)
        {
            var o = new JObject();
            Parameter p;
            try { p = e.LookupParameter(paramName); }
            catch (Exception ex)
            {
                o["present"] = null; o["readable"] = false; o["value"] = null; o["error"] = ex.Message;
                return o;
            }
            if (p == null)
            {
                // Absent is not empty. A caller counting "coded types" must not count
                // a type that has no such parameter as a type with a blank code.
                o["present"] = false; o["readable"] = true; o["value"] = null;
                return o;
            }
            o["present"] = true;
            try
            {
                var v = p.AsString();
                if (v == null) v = p.AsValueString();
                o["readable"] = true;
                o["value"] = v;
                o["is_empty"] = string.IsNullOrEmpty(v);
            }
            catch (Exception ex)
            {
                o["readable"] = false; o["value"] = null; o["error"] = ex.Message;
            }
            return o;
        }

        // ---- One census per request; the model may change between requests. ----
        private Dictionary<string, int> _census;
        private int _censusUnreadable;
        private string _censusUnreadableError;

        private Dictionary<string, int> CensusOf(Document doc)
        {
            if (_census != null) return _census;
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            int unreadable = 0;
            string firstError = null;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                ElementId tid;
                try { tid = e.GetTypeId(); }
                catch (Exception ex)
                {
                    // This census is the sole evidence that a type is in use. An element
                    // dropped here takes its type's only vote with it: `instances` goes
                    // down and the type surfaces in types_no_instances as unused. "I
                    // could not read this element" must not become "this type has no users".
                    unreadable++;
                    if (firstError == null) firstError = ex.Message;
                    continue;
                }
                if (tid == null || tid == ElementId.InvalidElementId) continue;
                var key = tid.ToString();
                int n;
                map.TryGetValue(key, out n);
                map[key] = n + 1;
            }
            _censusUnreadable = unreadable;
            _censusUnreadableError = firstError;
            _census = map;
            return map;
        }

        // ---- Small, boring, and each one honest about failing. ----
        private static bool TitlesMatch(string wanted, string actual)
        {
            if (actual == null) return false;
            return string.Equals(Strip(wanted), Strip(actual), StringComparison.OrdinalIgnoreCase);
        }

        private static string Strip(string s)
        {
            s = (s ?? "").Trim();
            if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s;
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafePath(Document d) { try { return d.PathName; } catch { return null; } }
        private static string SafeName(Element e) { try { return e?.Name; } catch { return null; } }

        private static string SafeName(Element e, out bool unreadable)
        {
            unreadable = false;
            try { return e.Name; }
            catch { unreadable = true; return null; }
        }

        private static string CategoryOf(Element e)
        {
            try { return e.Category?.Name; } catch { return null; }
        }

        private static string FamilyNameOf(Element e)
        {
            var et = e as ElementType;
            if (et == null) return null;
            try { return et.FamilyName; } catch { return null; }
        }

        private static string TypeNameOf(Document doc, Element e)
        {
            try
            {
                var tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) return null;
                return SafeName(doc.GetElement(tid));
            }
            catch { return null; }
        }

        private static JToken TryBool(Func<bool> f) { try { return f(); } catch { return null; } }
        private static JToken TryStr(Func<string> f) { try { return f(); } catch { return null; } }
        private static JToken TryDouble(Func<double> f) { try { return Math.Round(f(), 4); } catch { return null; } }

        private static JToken FileSizeMb(Document doc)
        {
            try
            {
                var p = doc.PathName;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
                return Math.Round(new FileInfo(p).Length / 1048576.0, 2);
            }
            catch { return null; }
        }
    }
}
