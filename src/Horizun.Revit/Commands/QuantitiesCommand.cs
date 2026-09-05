// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_quantities — volume takeoff that refuses to pick a number for you.
//
// This is the tool whose output becomes money, so it is the one most worth being
// paranoid about.
//
// Revit reports an element's volume from three places, and they do not always
// agree. On a real beam in our own test model:
//
//     Volume parameter ......... 0.4531 m3
//     Actual solid geometry .... 0.7913 m3
//     Material takeoff ......... 0.7913 m3
//
// A 75% gap on the quantity you bill. Every handler we looked at reads exactly
// one of these — usually the parameter, because it is the cheap one — and
// reports it as "the volume", with no hint the other two exist or disagree. If
// you had billed that beam from the parameter you would have billed 57% of the
// concrete you poured.
//
// (Why they disagree is legitimate: the parameter is cached and can lag joins,
// openings and cuts; the geometry is what is actually there at this detail
// level; the material takeoff is what the material schedule will say. Which one
// is "right" depends on your measurement criteria — which is precisely why this
// handler will not choose. It reports all three and flags the gap.)
//
// So: all three sources, side by side, in m3, with the disagreement measured and
// named. The quantity surveyor decides. That is their job, and their signature.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public class QuantitiesCommand : ICommand
    {
        public string Name => "horizun_quantities";

        public string Description =>
            "Volume takeoff in m3 from all three sources Revit offers — the Volume parameter, the real solid " +
            "geometry, and the material takeoff — reported side by side with the disagreement measured. " +
            "Handlers that report a single volume are picking one silently; we have measured a 75% gap " +
            "between the parameter and the geometry on the same beam. COVERAGE IS EXPLICIT: a value that " +
            "could not be read is never a zero, each source reports candidates/measured/not_applicable/failed " +
            "with total_is_complete, and two sources are compared ONLY where both produced a number — " +
            "all_agree is null when nothing was comparable and false when coverage is partial, never true. " +
            "A real zero is a measurement, not an absence. Pass element_ids or a category. Read-only. " +
            "MODE takeoff: mode='takeoff' with quantities=[{name, source, parameter?, unit}] and " +
            "classification_parameter measures the caller-named quantities per element for horizun_budget_compare; " +
            "every reading is measured | absent | empty | unreadable | invalid, include_links sweeps loaded links " +
            "with provenance, and units are declared by the caller, never compiled in.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var wantedTitle = request.Value<string>("target_document_title");
            if (!string.IsNullOrWhiteSpace(wantedTitle))
            {
                string actualTitle;
                try { actualTitle = doc.Title; } catch { actualTitle = null; }
                if (!TitlesMatch(wantedTitle, actualTitle))
                    return CommandResult.Fail(
                        "Refusing to measure: you asked for '" + wantedTitle + "' but the active document is '" +
                        (actualTitle ?? "(title unreadable)") + "'. Nothing was read. A takeoff of the wrong model " +
                        "is a priced bill of quantities for a file nobody looked at. Activate the intended document, " +
                        "or check you are talking to the right Revit host.");
            }

            var detail = ParseDetail(request.Value<string>("detail_level"));
            double tolPct = request["tolerance_pct"] != null ? request.Value<double>("tolerance_pct") : 1.0;
            bool onlyBad = request.Value<bool>("only_disagreements");
            int top = request["top"] != null ? Math.Max(1, request.Value<int>("top")) : 200;

            // Two modes, one tool. 'volume' is the reconciliation above and stays the
            // default; 'takeoff' measures the caller-named quantities per element for the
            // budget join. The takeoff-only keys are refused in volume mode rather than
            // ignored: a caller who passed quantities and got the three-source volume
            // back would have every reason to believe they had been measured.
            string mode = request.Value<string>("mode");
            if (string.IsNullOrWhiteSpace(mode)) mode = "volume";
            if (mode == "takeoff") return ExecuteTakeoff(doc, request, detail, top);
            if (mode != "volume")
                return CommandResult.Fail("mode must be 'volume' (the three-source reconciliation, the default) or 'takeoff'. Nothing was measured.");
            foreach (string takeoffOnly in new[] { "quantities", "classification_parameter", "include_links" })
                if (request[takeoffOnly] != null)
                    return CommandResult.Fail("'" + takeoffOnly + "' is only read in mode 'takeoff'; in mode 'volume' it would be " +
                                              "silently ignored, and you would read the volume reconciliation as though your " +
                                              "quantities had been measured. Pass mode: 'takeoff', or drop the key. Nothing was measured.");

            // ---- Resolve the element set. ----
            var elements = new List<Element>();
            var failed = new JArray();

            string codeParameter = request.Value<string>("code_parameter");
            if (string.IsNullOrWhiteSpace(codeParameter)) codeParameter = null;
            var idsToken = request["element_ids"] as JArray;
            if (idsToken != null && idsToken.Count > 0)
            {
                foreach (var tok in idsToken)
                {
                    if (tok.Type != JTokenType.Integer)
                    {
                        failed.Add(new JObject { ["element_id"] = tok.ToString(), ["error"] = "Not an integer element id." });
                        continue;
                    }
                    var id = tok.Value<long>();
                    if (!Rid.CanRepresentElementId(id))
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = Rid.ElementIdRangeError(id) });
                        continue;
                    }
                    var e = doc.GetElement(Rid.ToElementId(id));
                    if (e == null) { failed.Add(new JObject { ["element_id"] = id, ["error"] = "Element not found." }); continue; }
                    elements.Add(e);
                }
            }
            else
            {
                var catName = request.Value<string>("category");
                if (string.IsNullOrWhiteSpace(catName))
                    return CommandResult.Fail("Pass element_ids, or a category to sweep.");
                if (!Enum.TryParse<BuiltInCategory>(catName, true, out var bic))
                    return CommandResult.Fail($"'{catName}' is not a BuiltInCategory name. Expected something like OST_StructuralFraming.");
                elements = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .ToList();
            }

            if (elements.Count == 0 && failed.Count == 0)
            {
                // A dry "nothing matched" error is only honest when this document is
                // COMPLETE. With a closed workset, zero LOADED elements says nothing
                // about the model - the whole category may live on the workset nobody
                // opened - and an error here would be this tool violating its own
                // doctrine: an absence in the answer read as an absence in the model.
                DocumentVisibilityCoverage emptyVisibility = DocumentVisibility.Measure(doc);
                if (!emptyVisibility.CoverageComplete)
                    return CommandResult.Ok(new JObject
                    {
                        ["detail_level"] = detail.ToString(),
                        ["visibility_coverage"] = emptyVisibility.ToJson(),
                        ["candidates"] = 0,
                        ["rows"] = new JArray(),
                        ["failed"] = failed,
                        ["headline"] = "0 elements of the requested set are LOADED - and that is NOT a measurement of the model. " +
                            "INCOMPLETE COVERAGE: " + emptyVisibility.WorksetsClosed + " of " + emptyVisibility.WorksetsTotal +
                            " workset(s) are CLOSED, and the elements you asked about may live entirely on them. " +
                            "DO NOT READ AN ABSENCE HERE AS AN ABSENCE IN THE MODEL. Re-open the model with all " +
                            "worksets open and run this again before pricing anything on it."
                    });
                return CommandResult.Fail("No elements matched. Nothing to measure — reporting a total of zero here would read as 'this is empty' rather than 'you asked for nothing'.");
            }

            var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = detail };

            var rows = new JArray();

            // One tally per source, plus the pairwise one. Each knows the difference
            // between a number, a quantity that does not apply, and a read that failed -
            // and a total is the sum over the FIRST of those only.
            var tParam = new SourceTally("Volume parameter");
            var tGeom = new SourceTally("solid geometry (" + detail + ")");
            var tMat = new SourceTally("material takeoff");
            var pair = new PairTally("Volume parameter", "solid geometry (" + detail + ")");

            var byCode = new Dictionary<string, CodeTally>(StringComparer.Ordinal);
            double tol = tolPct / 100.0;
            int noQuantityAtAll = 0;
            int rowsWanted = 0;

            foreach (var e in elements)
            {
                Measurement mParam = ReadParamVolume(e).Convert(Guard.ToM3);
                Measurement mGeom = ReadGeometryVolume(e, options).Convert(Guard.ToM3);
                Measurement mMat = ReadMaterialVolume(e).Convert(Guard.ToM3);

                // An element none of the three sources applies to (a tag, a line) is not a
                // measurement problem. One that FAILED on every source is, and the two must
                // not be counted together.
                // The budget code, when asked for. Read for EVERY element in scope -
                // including the ones no volume source applies to, because a tag-like
                // element with a code is still a row somebody's budget references, and a
                // rollup that quietly skipped it would under-count the code.
                string code = null;
                if (codeParameter != null)
                {
                    code = ReadCode(doc, e, codeParameter);
                    CodeTally tally;
                    if (!byCode.TryGetValue(code, out tally)) byCode[code] = tally = new CodeTally();
                    tally.Elements++;
                }

                bool nothingApplies = mParam.State == MeasureState.NotApplicable &&
                                      mGeom.State == MeasureState.NotApplicable &&
                                      mMat.State == MeasureState.NotApplicable;
                if (nothingApplies) { noQuantityAtAll++; if (code != null) byCode[code].NoQuantity++; continue; }

                if (code != null)
                {
                    CodeTally tally = byCode[code];
                    // .Value.Value: Measurement.Value is nullable and Measured is the
                    // state that guarantees it. Throwing on a broken invariant beats
                    // adding a silent zero to somebody's budget.
                    if (mGeom.State == MeasureState.Measured) { tally.GeomM3 += mGeom.Value.Value; tally.GeomMeasured++; }
                    if (mParam.State == MeasureState.Measured) { tally.ParamM3 += mParam.Value.Value; tally.ParamMeasured++; }
                }

                tParam.Add(mParam);
                tGeom.Add(mGeom);
                tMat.Add(mMat);

                // Compared ONLY where both sides produced a number. This used to start from
                // `agree = true` and stay there when the comparison never happened, so an
                // element nobody could compare was counted as an element that agreed.
                JObject rec = null;
                bool compared = pair.Add(mParam, mGeom, (a, b) =>
                {
                    rec = JObject.FromObject(Guard.Reconcile(
                        "volume", "Volume parameter", a, "solid geometry (" + detail + ")", b, "m3", tol));
                    return rec["agree"] != null && rec["agree"].Type == JTokenType.Boolean && (bool)rec["agree"];
                });

                bool agreedHere = compared && rec != null && rec["agree"] != null &&
                                  rec["agree"].Type == JTokenType.Boolean && (bool)rec["agree"];
                // only_disagreements hides agreements, never the elements that could not be
                // compared: those are exactly what a partial takeoff needs to show.
                if (onlyBad && agreedHere) continue;

                // Rows are capped; the COUNTS above are not. Measured live: one category of
                // a real model produced a 463 KB response with "truncated": false written in
                // as a constant, which is a claim rather than a fact.
                rowsWanted++;
                if (rows.Count >= top) continue;

                var row = new JObject
                {
                    ["element_id"] = e.Id.ToString(),
                    ["name"] = SafeName(e),
                    ["category"] = SafeCategory(e),
                    ["type"] = SafeTypeName(doc, e),
                    ["volume_parameter_m3"] = Report(mParam),
                    ["volume_geometry_m3"] = Report(mGeom),
                    ["volume_material_takeoff_m3"] = Report(mMat),
                    ["reconciliation"] = rec,
                    ["compared"] = compared,
                    ["not_compared_because"] = compared ? null : WhyNotCompared(mParam, mGeom)
                };
                if (code != null) row["code"] = code;
                rows.Add(row);
            }

            // Totals from two sources cover DIFFERENT element sets, so summing each and
            // reconciling the sums compares unlike things. The only honest total-vs-total
            // comparison is over the elements both sources measured.
            var totalRec = pair.Compared == 0
                ? null
                : JObject.FromObject(Guard.Reconcile(
                    "total volume over the " + pair.Compared + " element(s) BOTH sources measured",
                    "sum of Volume parameters", pair.ComparableTotalA,
                    "sum of solid geometry", pair.ComparableTotalB, "m3", tol));

            // WHAT THE TOTALS ARE TOTALS OF. Everything above measures the elements this
            // command was given, and reports honestly about the ones it could not read.
            // None of that can see a CLOSED WORKSET: its elements are not in the document,
            // so they were never candidates, never failures, and never missing from any
            // count - the quantity is simply smaller, and looks exactly like a smaller
            // building. See Core/DocumentVisibilityCoverage.cs.
            DocumentVisibilityCoverage visibility = DocumentVisibility.Measure(doc);

            // The by_code rollup: what the budget pipeline joins on. Every sum states
            // how many elements it actually covers, because a code whose volume summed 3
            // of its 40 elements is not that code's volume - it is a fragment wearing the
            // code's name, and Excel cannot tell the difference once the number lands.
            JObject codeRollup = null;
            if (codeParameter != null)
            {
                codeRollup = new JObject();
                foreach (var kv in byCode.OrderByDescending(k => k.Value.Elements)
                                         .ThenBy(k => k.Key, StringComparer.Ordinal).Take(500))
                {
                    codeRollup[kv.Key] = new JObject
                    {
                        ["elements"] = kv.Value.Elements,
                        ["no_quantity"] = kv.Value.NoQuantity,
                        ["volume_geometry_m3"] = kv.Value.GeomM3,
                        ["volume_geometry_measured"] = kv.Value.GeomMeasured,
                        ["volume_parameter_m3"] = kv.Value.ParamM3,
                        ["volume_parameter_measured"] = kv.Value.ParamMeasured,
                        ["complete"] = kv.Value.GeomMeasured + kv.Value.NoQuantity == kv.Value.Elements
                    };
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["detail_level"] = detail.ToString(),
                ["code_parameter"] = codeParameter,
                ["by_code"] = codeRollup,
                ["by_code_truncated"] = codeRollup != null && byCode.Count > 500,
                ["visibility_coverage"] = visibility.ToJson(),
                ["elements_requested"] = elements.Count,
                ["elements_with_no_such_quantity"] = noQuantityAtAll,
                ["elements_considered"] = tParam.Candidates,
                ["coverage"] = new JObject
                {
                    ["volume_parameter"] = Coverage(tParam),
                    ["volume_geometry"] = Coverage(tGeom),
                    ["volume_material_takeoff"] = Coverage(tMat),
                    ["note"] = "known_total is the sum over measured elements ONLY. An element whose read " +
                               "FAILED is absent from it, which is why total_is_complete exists: a total that " +
                               "silently omits elements does not look wrong, it looks cheap."
                },
                ["comparison"] = new JObject
                {
                    ["source_a"] = pair.SourceA,
                    ["source_b"] = pair.SourceB,
                    ["candidates"] = pair.Candidates,
                    ["compared"] = pair.Compared,
                    ["agreed"] = pair.Agreed,
                    ["disagreed"] = pair.Disagreed,
                    ["not_comparable"] = pair.NotComparable,
                    ["coverage_complete"] = pair.CoverageComplete,
                    // true / false / null. null means nothing could be compared at all -
                    // which is not agreement, and must never be published as one.
                    ["all_agree"] = pair.AllAgree.HasValue ? (JToken)pair.AllAgree.Value : JValue.CreateNull(),
                    ["comparable_total_parameter_m3"] = Math.Round(pair.ComparableTotalA, 4),
                    ["comparable_total_geometry_m3"] = Math.Round(pair.ComparableTotalB, 4)
                },
                ["total_reconciliation"] = totalRec,
                ["elements"] = rows,
                ["failed"] = failed,
                // The headline is the sentence a caller quotes. A quantity taken over a
                // partly loaded model must not be quotable without the caveat attached.
                ["headline"] = pair.Headline(tolPct) +
                               (visibility.CoverageComplete ? "" : " " + visibility.Note()),
                // Totals and coverage above are EXACT and independent of this cap; only the
                // per-element list is shortened, and it says so rather than asserting false.
                ["rows_matching"] = rowsWanted,
                ["shown"] = rows.Count,
                ["top"] = top,
                ["truncated"] = rowsWanted > rows.Count
            });
        }

        // =====================================================================
        // mode: takeoff - the caller-named quantities, per element, for the budget join.
        //
        // Nothing about a budget is compiled in: which parameter carries the code, which
        // quantities matter and what unit each is billed in are all declared per call.
        // What IS fixed is the vocabulary of not-having-a-number, because that is what
        // the comparison downstream branches on: measured (zero included), absent (no
        // such parameter / no such geometry), empty (the parameter exists and holds
        // nothing), unreadable (the read threw), invalid (a value that is not a number,
        // or a dimensioned parameter whose unit is not the one declared).
        // =====================================================================

        private sealed class TakeoffDefinition
        {
            public string Name, Source, Parameter, Unit, UnitNormalised;
        }

        private sealed class TakeoffReading
        {
            public string State, Reason, MeasuredIn;
            public double? Value;
            public static TakeoffReading Of(double v, string measuredIn) => new TakeoffReading { State = QuantityState.Measured, Value = v, MeasuredIn = measuredIn };
            public static TakeoffReading Not(string state, string why) => new TakeoffReading { State = state, Reason = why };
        }

        private sealed class TakeoffCodeTally
        {
            public int Elements;
            public Dictionary<string, TakeoffQuantityTally> Quantities = new Dictionary<string, TakeoffQuantityTally>(StringComparer.Ordinal);
        }

        private sealed class TakeoffQuantityTally
        {
            public double Total;
            public int Measured, Absent, Empty, Unreadable, Invalid;
        }

        /// <summary>
        /// ONE PLACEMENT TO MEASURE. The host is one of these with no Link and no
        /// Placement; each loaded link instance is another - INCLUDING the second
        /// instance of a file already in the scope, which shares Owner and Elements with
        /// the first and has a Link and a Placement of its own. That sharing is the fix:
        /// the scope used to be keyed by Document, and Revit gives both instances of one
        /// linked file the same Document.
        /// </summary>
        private sealed class ScopeEntry
        {
            public Document Owner;
            public List<Element> Elements;
            public RevitLinkInstance Link;          // null: the host
            public TakeoffPlacement Placement;      // null: the host
        }

        private CommandResult ExecuteTakeoff(Document doc, JObject request, ViewDetailLevel detail, int top)
        {
            string problem;
            List<TakeoffDefinition> defs = ParseTakeoffDefinitions(request["quantities"], out problem);
            if (defs == null) return CommandResult.Fail(problem + " Nothing was measured.");

            string classificationParameter = request.Value<string>("classification_parameter");
            if (string.IsNullOrWhiteSpace(classificationParameter))
                return CommandResult.Fail("mode 'takeoff' needs classification_parameter: the parameter (instance first, then type) that " +
                                          "carries each element's budget code. No organisation's parameter is compiled in. Nothing was measured.");
            classificationParameter = classificationParameter.Trim();

            JToken linksTok = request["include_links"];
            if (linksTok != null && linksTok.Type != JTokenType.Boolean)
                return CommandResult.Fail("include_links must be true or false. Nothing was measured.");
            bool includeLinks = linksTok != null && (bool)linksTok;

            var idsToken = request["element_ids"] as JArray;
            bool byIds = idsToken != null && idsToken.Count > 0;
            string catName = request.Value<string>("category");
            BuiltInCategory bic = BuiltInCategory.INVALID;
            if (!byIds)
            {
                if (string.IsNullOrWhiteSpace(catName))
                    return CommandResult.Fail("Pass element_ids, or a category to sweep. Nothing was measured.");
                if (!Enum.TryParse<BuiltInCategory>(catName, true, out bic))
                    return CommandResult.Fail($"'{catName}' is not a BuiltInCategory name. Expected something like OST_StructuralFraming. Nothing was measured.");
            }
            else if (includeLinks)
                // An ElementId names an element in ONE document. The same integer names a
                // different element in every link, so a list of ids swept across links
                // would measure whatever happened to wear those numbers there.
                return CommandResult.Fail("include_links needs a category, not element_ids: an element id is only unique inside one " +
                                          "document, and the same integer names an unrelated element in every link. Nothing was measured.");

            var failed = new JArray();
            // ONE ENTRY PER PLACEMENT, host first. Not per document: two RevitLinkInstances
            // of the same linked file answer GetLinkDocument() with the SAME Document, so a
            // scope keyed by document collapsed them - both were measured and both were
            // stamped with the last instance's id. See Core/TakeoffScopeRules.cs.
            var scope = new List<ScopeEntry>();
            var documents = new JArray();
            var linksNotLoaded = new JArray();

            var hostElements = new List<Element>();
            if (byIds)
            {
                foreach (var tok in idsToken)
                {
                    if (tok.Type != JTokenType.Integer)
                    {
                        failed.Add(new JObject { ["element_id"] = tok.ToString(), ["error"] = "Not an integer element id." });
                        continue;
                    }
                    var id = tok.Value<long>();
                    if (!Rid.CanRepresentElementId(id))
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = Rid.ElementIdRangeError(id) });
                        continue;
                    }
                    var e = doc.GetElement(Rid.ToElementId(id));
                    if (e == null) { failed.Add(new JObject { ["element_id"] = id, ["error"] = "Element not found." }); continue; }
                    hostElements.Add(e);
                }
            }
            else
            {
                hostElements = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements().ToList();
            }
            scope.Add(new ScopeEntry { Owner = doc, Elements = hostElements });

            var repeatedDocuments = new JArray();
            if (includeLinks)
            {
                List<RevitLinkInstance> instances;
                try
                {
                    instances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>().OrderBy(i => Rid.Value(i.Id)).ToList();
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Could not enumerate the Revit links: " + ex.Message + ". Nothing was measured.");
                }
                // Collected ONCE per linked file, measured once per PLACEMENT. Revit hands
                // both instances of a twice-placed link the same Document, so re-running
                // the collector for the second placement would ask the same question twice
                // and get the same answer more slowly.
                var elementsByKey = new Dictionary<string, List<Element>>(StringComparer.Ordinal);
                var documentByKey = new Dictionary<string, Document>(StringComparer.Ordinal);
                var instanceById = new Dictionary<string, RevitLinkInstance>(StringComparer.Ordinal);
                var linkFacts = new List<TakeoffLinkFact>();
                foreach (RevitLinkInstance li in instances)
                {
                    Document linked = null;
                    try { linked = li.GetLinkDocument(); } catch { linked = null; }
                    if (linked == null)
                    {
                        // Not loaded means not measured, and a takeoff that quietly skipped a
                        // link would look exactly like a takeoff of a smaller building.
                        linksNotLoaded.Add(new JObject
                        {
                            ["link_instance_id"] = li.Id.ToString(),
                            ["name"] = SafeName(li),
                            ["state"] = "not_loaded",
                            ["means"] = "this link's elements were NOT measured. Load it and run again before pricing anything on this takeoff."
                        });
                        continue;
                    }
                    string title = SafeTitle(linked);
                    string path = SafePath(linked);
                    string key = !string.IsNullOrWhiteSpace(path) ? path
                               : !string.IsNullOrWhiteSpace(title) ? "title:" + title
                               : "instance:" + li.Id;
                    if (!elementsByKey.ContainsKey(key))
                    {
                        List<Element> linkedElements;
                        try
                        {
                            linkedElements = new FilteredElementCollector(linked).OfCategory(bic).WhereElementIsNotElementType().ToElements().ToList();
                        }
                        catch (Exception ex)
                        {
                            linksNotLoaded.Add(new JObject
                            {
                                ["link_instance_id"] = li.Id.ToString(),
                                ["name"] = SafeName(li),
                                ["state"] = "collector_failed",
                                ["error"] = ex.Message,
                                ["means"] = "this link's elements were NOT measured."
                            });
                            continue;
                        }
                        elementsByKey[key] = linkedElements;
                        documentByKey[key] = linked;
                    }
                    instanceById[li.Id.ToString()] = li;
                    linkFacts.Add(new TakeoffLinkFact
                    {
                        LinkInstanceId = li.Id.ToString(),
                        DocumentKey = key,
                        Title = title,
                        Path = path
                    });
                }

                // ONE SCOPE ENTRY PER PLACEMENT, numbered, with the repetition declared.
                TakeoffScope resolved = TakeoffScopeRules.Resolve(linkFacts);
                foreach (TakeoffPlacement placement in resolved.Placements)
                    scope.Add(new ScopeEntry
                    {
                        Owner = documentByKey[placement.Link.DocumentKey],
                        Elements = elementsByKey[placement.Link.DocumentKey],
                        Link = instanceById[placement.Link.LinkInstanceId],
                        Placement = placement
                    });
                repeatedDocuments = resolved.RepeatedDocuments;
            }

            int requested = scope.Sum(s => s.Elements.Count);
            if (requested == 0 && failed.Count == 0)
            {
                DocumentVisibilityCoverage emptyVisibility = DocumentVisibility.Measure(doc);
                if (!emptyVisibility.CoverageComplete)
                    return CommandResult.Ok(new JObject
                    {
                        ["mode"] = "takeoff",
                        ["visibility_coverage"] = emptyVisibility.ToJson(),
                        ["elements_requested"] = 0,
                        ["rows"] = new JArray(),
                        ["by_code"] = new JObject(),
                        ["links_not_loaded"] = linksNotLoaded,
                        ["failed"] = failed,
                        ["coverage_complete"] = false,
                        ["headline"] = "0 elements of the requested set are LOADED - and that is NOT a measurement of the model. " +
                            "INCOMPLETE COVERAGE: " + emptyVisibility.WorksetsClosed + " of " + emptyVisibility.WorksetsTotal +
                            " workset(s) are CLOSED. DO NOT READ AN ABSENCE HERE AS AN ABSENCE IN THE MODEL."
                    });
                return CommandResult.Fail("No elements matched. Nothing to measure - a takeoff of nothing would read as 'this is empty' rather than 'you asked for nothing'.");
            }

            var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = detail };
            var rows = new JArray();
            var byCode = new Dictionary<string, TakeoffCodeTally>(StringComparer.Ordinal);
            int rowsWanted = 0, unreadableReads = 0, invalidReads = 0;
            bool visibilityComplete = true;
            bool anyLinkMeasured = false;

            foreach (ScopeEntry entry in scope)
            {
                Document owner = entry.Owner;
                RevitLinkInstance link = entry.Link;
                string title = SafeTitle(owner);
                DocumentVisibilityCoverage visibility = DocumentVisibility.Measure(owner);
                if (!visibility.CoverageComplete) visibilityComplete = false;
                if (link != null) anyLinkMeasured = true;

                var docJson = new JObject
                {
                    ["document"] = title,
                    ["path"] = SafePath(owner),
                    ["kind"] = link == null ? "host" : "link",
                    ["link_instance_id"] = link == null ? null : link.Id.ToString(),
                    // FROM THIS PLACEMENT'S OWN INSTANCE. Two placements of one linked file
                    // sit at two different transforms, and both used to be reported with
                    // whichever instance the scope had kept last.
                    ["transform"] = link == null ? null : TransformJson(link),
                    ["placement"] = entry.Placement == null ? null : (JToken)entry.Placement.Occurrence,
                    ["placements_of_this_document"] = entry.Placement == null ? null : (JToken)entry.Placement.OccurrencesOfDocument,
                    ["elements"] = entry.Elements.Count,
                    ["visibility_coverage"] = visibility.ToJson()
                };
                // A LINK'S coverage is worth less than the host's, and saying so is
                // the difference between a measurement and a claim: Revit does not
                // expose the workset configuration a link was loaded with.
                if (link != null)
                    ((JObject)docJson["visibility_coverage"])["linked_document_means"] =
                        DocumentVisibilityCoverage.LinkedDocumentMeans;
                documents.Add(docJson);

                foreach (Element e in entry.Elements)
                {
                    string code = ReadCode(owner, e, classificationParameter);
                    TakeoffCodeTally tally;
                    if (!byCode.TryGetValue(code, out tally)) byCode[code] = tally = new TakeoffCodeTally();
                    tally.Elements++;

                    var quantities = new JObject();
                    foreach (TakeoffDefinition d in defs)
                    {
                        TakeoffReading r = ReadTakeoffQuantity(owner, e, d, options);
                        TakeoffQuantityTally qt;
                        if (!tally.Quantities.TryGetValue(d.Name, out qt)) tally.Quantities[d.Name] = qt = new TakeoffQuantityTally();
                        switch (r.State)
                        {
                            case QuantityState.Measured: qt.Measured++; qt.Total += r.Value.Value; break;
                            case QuantityState.Absent: qt.Absent++; break;
                            case QuantityState.Empty: qt.Empty++; break;
                            case QuantityState.Invalid: qt.Invalid++; invalidReads++; break;
                            default: qt.Unreadable++; unreadableReads++; break;
                        }
                        quantities[d.Name] = new JObject
                        {
                            ["value"] = r.Value.HasValue ? (JToken)Math.Round(r.Value.Value, 6) : JValue.CreateNull(),
                            ["state"] = r.State,
                            ["unit"] = d.Unit,
                            ["measured_in"] = r.MeasuredIn,
                            ["reason"] = r.Reason
                        };
                    }

                    rowsWanted++;
                    if (rows.Count >= top) continue;
                    var row = new JObject
                    {
                        ["element_id"] = e.Id.ToString(),
                        ["document"] = title,
                        // THE ROW'S IDENTITY IS (document, link_instance_id, element_id).
                        // An element id names an element inside ONE document, and a linked
                        // file placed twice produces the same id through two placements -
                        // which is why the instance is part of the identity and not decoration.
                        ["document_path"] = SafePath(owner),
                        ["link_instance_id"] = link == null ? null : link.Id.ToString(),
                        ["placement"] = entry.Placement == null ? null : (JToken)entry.Placement.Occurrence,
                        ["name"] = SafeName(e),
                        ["category"] = SafeCategory(e),
                        ["type"] = SafeTypeName(owner, e),
                        ["classification_code"] = code,
                        ["quantities"] = quantities
                    };
                    rows.Add(row);
                }
            }

            var codeRollup = new JObject();
            foreach (var kv in byCode.OrderByDescending(k => k.Value.Elements).ThenBy(k => k.Key, StringComparer.Ordinal).Take(500))
            {
                var q = new JObject();
                foreach (TakeoffDefinition d in defs)
                {
                    TakeoffQuantityTally qt;
                    if (!kv.Value.Quantities.TryGetValue(d.Name, out qt)) qt = new TakeoffQuantityTally();
                    q[d.Name] = new JObject
                    {
                        ["unit"] = d.Unit,
                        ["known_total"] = Math.Round(qt.Total, 6),
                        ["measured"] = qt.Measured,
                        ["absent"] = qt.Absent,
                        ["empty"] = qt.Empty,
                        ["unreadable"] = qt.Unreadable,
                        ["invalid"] = qt.Invalid,
                        ["complete"] = qt.Measured == kv.Value.Elements
                    };
                }
                codeRollup[kv.Key] = new JObject
                {
                    ["elements"] = kv.Value.Elements,
                    ["classified"] = !ClassificationNonValue.IsNonValue(kv.Key),
                    ["quantities"] = q
                };
            }

            bool coverageComplete = visibilityComplete && linksNotLoaded.Count == 0 && unreadableReads == 0 && invalidReads == 0 && failed.Count == 0;
            string headline = requested + " element(s) measured across " + documents.Count + " document(s) for " + defs.Count +
                              " quantity(ies); " + byCode.Count + " distinct code value(s) under '" + classificationParameter + "'.";
            if (unreadableReads > 0) headline += " " + unreadableReads + " reading(s) could NOT be read - every code they touch is a lower bound.";
            if (invalidReads > 0) headline += " " + invalidReads + " reading(s) were not numbers or not in the declared unit.";
            if (linksNotLoaded.Count > 0) headline += " " + linksNotLoaded.Count + " link(s) were NOT measured (not loaded).";
            if (repeatedDocuments.Count > 0)
                headline += " " + repeatedDocuments.Count + " linked file(s) are placed MORE THAN ONCE and are counted " +
                            "once per placement - see repeated_link_documents.";
            if (!visibilityComplete) headline += " At least one document has CLOSED worksets: what is not loaded was not measured." +
                (anyLinkMeasured ? " For a LINKED document that flag is the linked model's own state; the configuration a link was loaded with is not readable." : "");

            return CommandResult.Ok(new JObject
            {
                ["mode"] = "takeoff",
                ["classification_parameter"] = classificationParameter,
                ["detail_level"] = detail.ToString(),
                ["quantity_definitions"] = new JArray(defs.Select(d => new JObject
                {
                    ["name"] = d.Name, ["source"] = d.Source, ["parameter"] = d.Parameter, ["unit"] = d.Unit,
                    ["measured_in"] = MeasuredInOf(d.Source) ?? "the parameter's own unit: m / m2 / m3 for Length / Area / Volume specs (checked against 'unit'), the raw value otherwise"
                })),
                ["include_links"] = includeLinks,
                ["documents"] = documents,
                ["documents_are_placements"] = "one entry per PLACEMENT, not per file. A linked file placed twice " +
                                               "appears twice, with its own link_instance_id and transform each time.",
                ["links_not_loaded"] = linksNotLoaded,
                ["repeated_link_documents"] = repeatedDocuments,
                ["elements_requested"] = requested,
                ["by_code"] = codeRollup,
                ["by_code_truncated"] = byCode.Count > 500,
                ["coverage_complete"] = coverageComplete,
                ["coverage"] = new JObject
                {
                    ["unreadable_readings"] = unreadableReads,
                    ["invalid_readings"] = invalidReads,
                    ["links_not_loaded"] = linksNotLoaded.Count,
                    ["repeated_link_documents"] = repeatedDocuments.Count,
                    ["all_worksets_open"] = visibilityComplete,
                    ["note"] = "known_total per code is the sum over MEASURED readings only. A code with an unreadable or " +
                               "invalid reading is a lower bound, and horizun_budget_compare will refuse to compare it. " +
                               "A linked file placed more than once is measured once per placement and its rows are told " +
                               "apart by link_instance_id; repeated_link_documents names every such file."
                },
                ["rows"] = rows,
                ["failed"] = failed,
                ["headline"] = headline,
                ["rows_matching"] = rowsWanted,
                ["shown"] = rows.Count,
                ["top"] = top,
                ["truncated"] = rowsWanted > rows.Count,
                ["truncated_note"] = rowsWanted > rows.Count
                    ? "by_code and coverage are EXACT; only the per-element rows were shortened. horizun_budget_compare refuses a truncated reply - re-run with top >= rows_matching."
                    : null
            });
        }

        private static List<TakeoffDefinition> ParseTakeoffDefinitions(JToken token, out string problem)
        {
            problem = null;
            var arr = token as JArray;
            if (arr == null || arr.Count == 0)
            {
                problem = "mode 'takeoff' needs quantities: a non-empty array of {name, source, parameter?, unit}.";
                return null;
            }
            if (arr.Count > 50) { problem = "quantities lists " + arr.Count + " definitions; at most 50 per call."; return null; }
            var defs = new List<TakeoffDefinition>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < arr.Count; i++)
            {
                var o = arr[i] as JObject;
                if (o == null) { problem = "quantities[" + i + "] must be an object {name, source, parameter?, unit}."; return null; }
                foreach (JProperty p in o.Properties())
                    if (p.Name != "name" && p.Name != "source" && p.Name != "parameter" && p.Name != "unit")
                    { problem = "quantities[" + i + "]." + p.Name + " is not a known key (name, source, parameter, unit)."; return null; }
                string name = (string)o["name"];
                if (string.IsNullOrWhiteSpace(name)) { problem = "quantities[" + i + "].name is required."; return null; }
                name = name.Trim();
                if (!names.Add(name)) { problem = "quantities names '" + name + "' twice."; return null; }
                string source = (string)o["source"];
                if (source != "parameter" && source != "geometry_volume" && source != "geometry_area" && source != "length" && source != "count")
                { problem = "quantities[" + i + "].source must be one of parameter, geometry_volume, geometry_area, length, count."; return null; }
                string parameter = (string)o["parameter"];
                if (source == "parameter" && string.IsNullOrWhiteSpace(parameter))
                { problem = "quantities[" + i + "] (source parameter) needs 'parameter': the name of the parameter to read (instance first, then type)."; return null; }
                if (source != "parameter" && o["parameter"] != null)
                { problem = "quantities[" + i + "].parameter is only read when source is 'parameter'; it would be ignored here, so it is refused."; return null; }
                string unit = (string)o["unit"];
                if (string.IsNullOrWhiteSpace(unit)) { problem = "quantities[" + i + "].unit is required: the unit this quantity is billed in. Nothing is compiled in."; return null; }
                unit = unit.Trim();
                string normalised = NormaliseUnit(unit);
                string fixedUnit = MeasuredInOf(source);
                if (fixedUnit != null && normalised != fixedUnit)
                {
                    problem = "quantities[" + i + "] (source " + source + ") measures in " + fixedUnit + " and declares unit '" + unit +
                              "'. Declare '" + fixedUnit + "' here; a conversion to another unit belongs in the comparison mapping, declared with its factor.";
                    return null;
                }
                defs.Add(new TakeoffDefinition { Name = name, Source = source, Parameter = parameter == null ? null : parameter.Trim(), Unit = unit, UnitNormalised = normalised });
            }
            return defs;
        }

        /// <summary>The SI unit a geometric source always measures in; null for parameter (decided per read) and count.</summary>
        private static string MeasuredInOf(string source)
        {
            switch (source)
            {
                case "geometry_volume": return "m3";
                case "geometry_area": return "m2";
                case "length": return "m";
                default: return null;
            }
        }

        /// <summary>m³, M3, m^3 and 'm 3' all mean m3. Only spelling is normalised, never meaning.</summary>
        private static string NormaliseUnit(string unit)
        {
            if (unit == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in unit.Trim().ToLowerInvariant())
            {
                if (c == '³') sb.Append('3');
                else if (c == '²') sb.Append('2');
                else if (c == '^' || char.IsWhiteSpace(c)) continue;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static TakeoffReading ReadTakeoffQuantity(Document owner, Element e, TakeoffDefinition d, Options options)
        {
            switch (d.Source)
            {
                case "count":
                    return TakeoffReading.Of(1, d.Unit);
                case "geometry_volume":
                    return FromMeasurement(ReadGeometryVolume(e, options).Convert(Guard.ToM3), "m3");
                case "geometry_area":
                    return FromMeasurement(ReadGeometryArea(e, options).Convert(Guard.ToM2), "m2");
                case "length":
                    return FromMeasurement(ReadLocationLength(e).Convert(Guard.ToM), "m");
                default:
                    return ReadParameterQuantity(owner, e, d);
            }
        }

        private static TakeoffReading FromMeasurement(Measurement m, string measuredIn)
        {
            if (m.IsMeasured) return TakeoffReading.Of(m.Value.Value, measuredIn);
            // The mapping is TakeoffReadingRules', beside Measurement and Revit-free,
            // so a read that threw can be exercised with a substituted measurement:
            // an element whose geometry Revit itself cannot evaluate is not something
            // a fixture can build on demand.
            return TakeoffReading.Not(TakeoffReadingRules.StateFor(m), m.Detail);
        }

        /// <summary>
        /// A named parameter, instance first then type. A dimensioned parameter (Length,
        /// Area, Volume) is converted from Revit's internal feet to m / m2 / m3 and the
        /// declared unit must be that one - a Volume read in m3 and labelled ft3 would be
        /// a number wearing the wrong unit, which the comparison downstream cannot see.
        /// Any other spec is returned raw, in whatever unit the caller declared.
        /// </summary>
        private static TakeoffReading ReadParameterQuantity(Document owner, Element e, TakeoffDefinition d)
        {
            Parameter p;
            try
            {
                p = e.LookupParameter(d.Parameter);
                if (p == null)
                {
                    Element t = owner.GetElement(e.GetTypeId());
                    p = t == null ? null : t.LookupParameter(d.Parameter);
                }
            }
            catch (Exception ex) { return TakeoffReading.Not(QuantityState.Unreadable, "parameter lookup threw: " + ex.Message); }
            if (p == null) return TakeoffReading.Not(QuantityState.Absent, "no parameter '" + d.Parameter + "' on the instance or its type.");
            try
            {
                if (!p.HasValue) return TakeoffReading.Not(QuantityState.Empty, "parameter '" + d.Parameter + "' holds no value.");
                switch (p.StorageType)
                {
                    case StorageType.Double:
                    {
                        double raw = p.AsDouble();
                        if (double.IsNaN(raw) || double.IsInfinity(raw))
                            return TakeoffReading.Not(QuantityState.Invalid, "parameter '" + d.Parameter + "' is not a finite number.");
                        string measuredIn = null;
                        double value = raw;
                        ForgeTypeId spec = null;
                        try { spec = p.Definition.GetDataType(); } catch { spec = null; }
                        if (spec != null)
                        {
                            if (spec == SpecTypeId.Length) { measuredIn = "m"; value = Guard.ToM(raw); }
                            else if (spec == SpecTypeId.Area) { measuredIn = "m2"; value = Guard.ToM2(raw); }
                            else if (spec == SpecTypeId.Volume) { measuredIn = "m3"; value = Guard.ToM3(raw); }
                        }
                        if (measuredIn != null && measuredIn != d.UnitNormalised)
                            return TakeoffReading.Not(QuantityState.Invalid,
                                "parameter '" + d.Parameter + "' is a " + measuredIn + " quantity (Revit spec) but this takeoff declares unit '" + d.Unit +
                                "'. Declare '" + measuredIn + "' and put any conversion in the comparison mapping.");
                        return TakeoffReading.Of(value, measuredIn ?? d.Unit);
                    }
                    case StorageType.Integer:
                        return TakeoffReading.Of(p.AsInteger(), d.Unit);
                    case StorageType.String:
                        return TakeoffReading.Not(QuantityState.Invalid, "parameter '" + d.Parameter + "' is text, not a number: '" + (p.AsString() ?? "") + "'.");
                    default:
                        return TakeoffReading.Not(QuantityState.Invalid, "parameter '" + d.Parameter + "' has storage type " + p.StorageType + ", which is not a quantity.");
                }
            }
            catch (Exception ex) { return TakeoffReading.Not(QuantityState.Unreadable, "parameter '" + d.Parameter + "' could not be read: " + ex.Message); }
        }

        /// <summary>
        /// The total face area of every solid in the element's geometry. Stated, because
        /// "area" has several meanings and this is ONE of them: for a wall it is both faces
        /// plus the edges, not the schedule's Area parameter - read that with source
        /// 'parameter' when it is the quantity you bill.
        /// </summary>
        private static Measurement ReadGeometryArea(Element e, Options opt)
        {
            try
            {
                var geom = e.get_Geometry(opt);
                if (geom == null) return Measurement.NotApplicable("This element exposes no geometry.");
                int solids = 0;
                double a = SumFaceAreas(geom, ref solids);
                if (solids == 0) return Measurement.NotApplicable("Geometry exists but contains no solids.");
                return Measurement.Of(a);
            }
            catch (Exception ex) { return Measurement.Failed("Geometry could not be read: " + ex.Message); }
        }

        private static double SumFaceAreas(GeometryObject go, ref int solids)
        {
            double a = 0;
            if (go is Solid s)
            {
                if (s.Volume > 1e-9 && s.Faces.Size > 0)
                {
                    solids++;
                    foreach (Face f in s.Faces) a += f.Area;
                }
            }
            else if (go is GeometryInstance gi)
            {
                var inst = gi.GetInstanceGeometry();
                if (inst != null) foreach (var o in inst) a += SumFaceAreas(o, ref solids);
            }
            else if (go is GeometryElement ge)
            {
                foreach (var o in ge) a += SumFaceAreas(o, ref solids);
            }
            return a;
        }

        /// <summary>The length of the element's location curve - a wall, beam, duct, pipe. Point-located elements have none.</summary>
        private static Measurement ReadLocationLength(Element e)
        {
            try
            {
                var lc = e.Location as LocationCurve;
                if (lc == null || lc.Curve == null)
                    return Measurement.NotApplicable("This element has no location curve (it is point-located or has no location).");
                return Measurement.Of(lc.Curve.Length);
            }
            catch (Exception ex) { return Measurement.Failed("Location curve could not be read: " + ex.Message); }
        }

        private static JObject TransformJson(RevitLinkInstance link)
        {
            try
            {
                Transform t = link.GetTotalTransform();
                return new JObject
                {
                    ["is_identity"] = t.IsIdentity,
                    ["origin_m"] = new JArray(Guard.ToM(t.Origin.X), Guard.ToM(t.Origin.Y), Guard.ToM(t.Origin.Z)),
                    ["applies_to_quantities"] = false,
                    ["note"] = "volumes, areas, lengths and counts are invariant under the link's placement; the transform is recorded for provenance, not applied to them."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["is_identity"] = JValue.CreateNull(), ["error"] = "transform could not be read: " + ex.Message };
            }
        }

        private static string SafeTitle(Document d) { try { return d?.Title; } catch { return null; } }
        private static string SafePath(Document d) { try { return d?.PathName; } catch { return null; } }

        /// <summary>
        /// The cached parameter. Cheap, and the one everyone reads.
        /// A stored zero is a MEASUREMENT of zero and is returned as one - it used to be
        /// folded into "no data", which hides the case that matters most: a parameter
        /// saying zero while the geometry says otherwise.
        /// </summary>
        private static Measurement ReadParamVolume(Element e)
        {
            try
            {
                var p = e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                if (p == null || !p.HasValue) p = e.LookupParameter("Volume");
                if (p == null) return Measurement.NotApplicable("This element has no Volume parameter.");
                if (!p.HasValue) return Measurement.NotApplicable("The Volume parameter holds no value.");
                if (p.StorageType != StorageType.Double)
                    return Measurement.NotApplicable("The Volume parameter is not numeric (" + p.StorageType + ").");
                return Measurement.Of(p.AsDouble());
            }
            catch (Exception ex)
            {
                return Measurement.Failed("Volume parameter could not be read: " + ex.Message);
            }
        }

        /// <summary>What is actually modelled, at this detail level.</summary>
        private static Measurement ReadGeometryVolume(Element e, Options opt)
        {
            try
            {
                var geom = e.get_Geometry(opt);
                if (geom == null) return Measurement.NotApplicable("This element exposes no geometry.");

                int solids = 0;
                double v = SumSolids(geom, ref solids);
                if (solids == 0)
                    return Measurement.NotApplicable("Geometry exists but contains no solids - a surface, a " +
                                                     "curve, or an annotation.");
                // Solids that sum to nothing are a measured zero, not an absence. Returning
                // "no data" here used to remove the element from the comparison entirely,
                // so a void-only element whose parameter claimed volume never got flagged.
                return Measurement.Of(v);
            }
            catch (Exception ex)
            {
                // NOT swallowed. An element whose geometry we could not read is an
                // element missing from a takeoff, and the caller must know which.
                return Measurement.Failed("Geometry could not be read: " + ex.Message);
            }
        }

        /// <summary>
        /// What the material schedule will say - the third opinion.
        /// A per-material read that throws makes the SUM partial, so the whole measurement
        /// fails rather than publishing an under-count as a number. The inner catch used to
        /// be empty, which is exactly how a partial sum becomes an authoritative one.
        /// </summary>
        private static Measurement ReadMaterialVolume(Element e)
        {
            try
            {
                var mats = e.GetMaterialIds(false);
                if (mats == null || mats.Count == 0)
                    return Measurement.NotApplicable("This element reports no materials.");

                double v = 0;
                int read = 0;
                var failures = new List<string>();
                foreach (var m in mats)
                {
                    try { v += e.GetMaterialVolume(m); read++; }
                    catch (Exception ex) { failures.Add(m.ToString() + ": " + ex.Message); }
                }

                if (failures.Count > 0)
                    return Measurement.Failed(failures.Count + " of " + mats.Count + " material volume(s) could " +
                                              "not be read, so the sum would be short by an unknown amount (" +
                                              string.Join("; ", failures.Take(3)) + ").");

                return read == 0
                    ? Measurement.NotApplicable("No material volume was available.")
                    : Measurement.Of(v);
            }
            catch (Exception ex)
            {
                return Measurement.Failed("Material takeoff could not be read: " + ex.Message);
            }
        }

        /// <summary>A measured number, or an explicit reason - never a defaulted zero.</summary>
        private static JToken Report(Measurement m)
        {
            if (m.IsMeasured) return Math.Round(m.Value.Value, 4);
            return new JObject
            {
                ["value"] = JValue.CreateNull(),
                ["state"] = m.State.ToString(),
                ["reason"] = m.Detail
            };
        }

        private static JObject Coverage(SourceTally t) => new JObject
        {
            ["candidates"] = t.Candidates,
            ["measured"] = t.Measured,
            ["not_applicable"] = t.NotApplicable,
            ["failed"] = t.Failed,
            ["known_total_m3"] = Math.Round(t.KnownTotal, 4),
            ["total_is_complete"] = t.TotalIsComplete,
            ["summary"] = t.Describe()
        };

        private static string WhyNotCompared(Measurement a, Measurement b)
        {
            var parts = new List<string>();
            if (!a.IsMeasured) parts.Add("Volume parameter: " + (a.Detail ?? a.State.ToString()));
            if (!b.IsMeasured) parts.Add("solid geometry: " + (b.Detail ?? b.State.ToString()));
            return parts.Count == 0 ? null : string.Join(" | ", parts);
        }

        private static double SumSolids(GeometryObject go, ref int solids)
        {
            double v = 0;
            if (go is Solid s)
            {
                if (s.Volume > 1e-9 && s.Faces.Size > 0) { solids++; v += s.Volume; }
            }
            else if (go is GeometryInstance gi)
            {
                var inst = gi.GetInstanceGeometry();
                if (inst != null) foreach (var o in inst) v += SumSolids(o, ref solids);
            }
            else if (go is GeometryElement ge)
            {
                foreach (var o in ge) v += SumSolids(o, ref solids);
            }
            return v;
        }

        private static bool TitlesMatch(string wanted, string actual)
        {
            if (actual == null) return false;
            return string.Equals(StripRvt(wanted), StripRvt(actual), StringComparison.OrdinalIgnoreCase);
        }

        private static string StripRvt(string s)
        {
            s = (s ?? "").Trim();
            if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s;
        }

        private static ViewDetailLevel ParseDetail(string s)
        {
            switch ((s ?? "Fine").ToLowerInvariant())
            {
                case "coarse": return ViewDetailLevel.Coarse;
                case "medium": return ViewDetailLevel.Medium;
                default: return ViewDetailLevel.Fine;
            }
        }

        private sealed class CodeTally
        {
            public int Elements, NoQuantity, GeomMeasured, ParamMeasured;
            public double GeomM3, ParamM3;
        }

        /// <summary>
        /// The element's budget code: instance parameter first, then its type. The three
        /// non-values stay distinct - "(no such parameter)", "(empty)", "(unreadable)" -
        /// because a rollup that pooled them would hide exactly the elements a budget
        /// review needs to see, under a key that looks like a finding.
        /// </summary>
        private static string ReadCode(Document doc, Element e, string parameterName)
        {
            try
            {
                Parameter p = e.LookupParameter(parameterName);
                if (p == null)
                {
                    Element t = doc.GetElement(e.GetTypeId());
                    p = t == null ? null : t.LookupParameter(parameterName);
                }
                if (p == null) return "(no such parameter)";
                string v;
                try { v = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString(); }
                catch { return "(unreadable)"; }
                return string.IsNullOrWhiteSpace(v) ? "(empty)" : v.Trim();
            }
            catch { return "(unreadable)"; }
        }

        private static string SafeName(Element e) { try { return e?.Name; } catch { return null; } }
        private static string SafeCategory(Element e) { try { return e?.Category?.Name; } catch { return null; } }
        private static string SafeTypeName(Document d, Element e)
        {
            try { var t = d.GetElement(e.GetTypeId()); return t?.Name; } catch { return null; }
        }
    }
}
