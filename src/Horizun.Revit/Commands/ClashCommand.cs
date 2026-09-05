// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_clash — clash detection that includes linked models.
//
// The handler this replaces has 447 lines and not one mention of RevitLink. It
// only ever collects from the host document.
//
// That is not a missing feature, it is a wrong answer. On a real project the
// structure is one model and the MEP is a link — that is the whole point of
// links, and it is exactly the pair you run clash detection on. Ask the old
// handler for walls vs. ducts, get "0 clashes", and read it as "coordinated".
// It never says the ducts were in a file it did not open. Silence that reads as
// a pass is the most expensive kind of wrong: nobody investigates a clean report.
//
// So this handler:
//   * Collects from the host AND from every loaded RVT link, transforming link
//     geometry into host coordinates (GetTotalTransform — a link is placed with
//     a transform, and comparing untransformed link solids against host solids
//     produces confident nonsense).
//   * Names the source of every element in every clash: "host" or the link.
//   * REFUSES to report a clean result it cannot stand behind. If links exist
//     and were excluded, or a link is unloaded, or geometry failed on elements
//     we were asked to check, that goes in `coverage` and the headline says the
//     result is partial. A zero from this tool means zero, or it says why not.
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
    public class ClashCommand : ICommand
    {
        public string Name => "horizun_clash";

        public string Description =>
            "Clash detection between two category sets, across the host model AND loaded Revit links " +
            "(link geometry is transformed into host coordinates). Every clash names the source model of " +
            "both elements. If links were excluded or unloaded, or geometry failed, the result is reported " +
            "as PARTIAL rather than clean — a zero from this tool means zero. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var catsA = ParseCats(request["categories_a"] as JArray, out string errA);
            if (errA != null) return CommandResult.Fail(errA);
            var catsB = ParseCats(request["categories_b"] as JArray, out string errB);
            if (errB != null) return CommandResult.Fail(errB);

            bool includeLinks = request["include_links"] == null || request.Value<bool>("include_links");
            double tolMm = request["tolerance_mm"] != null ? request.Value<double>("tolerance_mm") : 0.0;
            int maxResults = request["max_results"] != null ? request.Value<int>("max_results") : 200;
            double tolFt3 = Math.Pow(tolMm / 304.8, 3);
            // Penetration planning over the SAME pairs, opt-in. Still read-only: the
            // output is a plan the caller executes through create_elements, whose own
            // rehearsal/token/verify pipeline does the writing.
            bool planPenetrations = request.Value<bool?>("plan_penetrations") == true;
            // Fold this run into the durable finding ledger (see horizun_coordination).
            // A ledger write is bridge state, not model state - the run stays read-only
            // for the document either way.
            bool recordFindings = request.Value<bool?>("record_findings") == true;
            double clearanceMm = request["clearance_mm"] != null ? request.Value<double>("clearance_mm") : 0.0;
            bool allowStructuralHosts = request.Value<bool?>("allow_structural_hosts") == true;
            long? sleeveTypeId = request.Value<long?>("sleeve_type_id");
            double clusterRadiusMm = request["cluster_radius_mm"] != null ? request.Value<double>("cluster_radius_mm") : 0.0;
            if (clusterRadiusMm < 0 || clusterRadiusMm > 5000)
                return CommandResult.Fail("cluster_radius_mm must be between 0 and 5000.");
            if (clearanceMm < 0 || clearanceMm > 500)
                return CommandResult.Fail("clearance_mm must be between 0 and 500.");

            var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };

            // ---- Sources: the host, plus every link we can actually read. ----
            var sources = new List<Src> { new Src { Name = "host", Doc = doc, Xf = Transform.Identity } };
            var linksSkipped = new JArray();

            var linkInstances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            var linkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();

            if (includeLinks)
            {
                foreach (var li in linkInstances)
                {
                    string label = SafeName(li);
                    try
                    {
                        var ldoc = li.GetLinkDocument();
                        if (ldoc == null)
                        {
                            // Not an error we may swallow: an unloaded link is a hole
                            // in the check, and the caller must see it.
                            linksSkipped.Add(new JObject
                            {
                                ["link"] = label,
                                ["reason"] = "Not loaded, so its geometry is not in this session.",
                                ["consequence"] = "Anything in this link was NOT checked. Clashes against it are unknown, not absent."
                            });
                            continue;
                        }
                        // The INSTANCE id travels with the source. Two instances of the same
                        // link share a name and different transforms, and the solid cache
                        // used to key on the name alone - handing the second instance the
                        // first one's geometry, sitting where the first one sits.
                        sources.Add(new Src
                        {
                            Name = label,
                            Doc = ldoc,
                            Xf = li.GetTotalTransform(),
                            InstanceId = li.Id.ToString()
                        });
                    }
                    catch (Exception ex)
                    {
                        linksSkipped.Add(new JObject
                        {
                            ["link"] = label,
                            ["reason"] = ex.Message,
                            ["consequence"] = "This link was NOT checked."
                        });
                    }
                }
            }
            else if (linkTypes.Count > 0)
            {
                linksSkipped.Add(new JObject
                {
                    ["link"] = $"({linkTypes.Count} link(s) in this model)",
                    ["reason"] = "include_links=false was requested.",
                    ["consequence"] = "Only the host model was checked. On a normally federated project the other " +
                                      "discipline lives in a link, so this result cannot be read as coordinated."
                });
            }

            // ---- Collect both sides, remembering where each element came from AND
            //      everything that did not make it in. ----
            var ledgerA = new SideLedger("side A");
            var ledgerB = new SideLedger("side B");
            var pairs = new PairLedger();
            var sideA = Collect(sources, catsA, ledgerA);
            var sideB = Collect(sources, catsB, ledgerB);

            // THE GAP NO LEDGER ABOVE CAN RECORD. Both ledgers count elements that entered
            // the check and elements that were dropped from it, which is every way an
            // element can be missed EXCEPT one: an element on a CLOSED WORKSET was never in
            // the document, so it was never a candidate and never a drop. A clash run over
            // a partly loaded model produces a confident zero. See
            // Core/DocumentVisibilityCoverage.cs.
            //
            // MEASURED PER SOURCE, not just for the host. A clash is a statement about a
            // FEDERATED model, and the host is one document among several: the structural
            // link can have three worksets closed while the host has none, and a check that
            // measured only the host would report full coverage over a model it had seen
            // half of. The link is where the other discipline lives, which makes it the
            // likelier place for this to happen and the one that matters more.
            bool visibilityComplete;
            JObject visibility = SourceCoverage(sources, out visibilityComplete);

            if (sideA.Count == 0 || sideB.Count == 0)
            {
                return CommandResult.Ok(new JObject
                {
                    ["clashes"] = new JArray(),
                    ["clash_count"] = 0,
                    ["result"] = "inconclusive",
                    ["elements_a"] = sideA.Count,
                    ["elements_b"] = sideB.Count,
                    ["coverage"] = Coverage(sources, linksSkipped, new JArray(), ledgerA, ledgerB, pairs),
                    ["visibility_coverage"] = visibility,
                    ["headline"] = $"One side is empty (A={sideA.Count}, B={sideB.Count}) — nothing could clash. " +
                                   "This is NOT a clean result: it means the categories matched no geometry in the " +
                                   "models checked. Verify the category names and that the discipline you expect " +
                                   "is actually loaded." +
                                   (visibilityComplete ? "" : " " + (string)visibility["note"])
                });
            }

            // ---- Broad phase on bounding boxes, narrow phase on solids. ----
            var clashes = new JArray();
            var hitDetails = new List<HitDetail>();
            var geometryFailures = new JArray();
            int bboxHits = 0, solidTests = 0;
            var solidCache = new Dictionary<string, List<Solid>>();

            foreach (var a in sideA)
            {
                foreach (var b in sideB)
                {
                    // Same element, same placement: not a clash with itself.
                    if (a.El.Id == b.El.Id && a.Source == b.Source && a.InstanceId == b.InstanceId) continue;
                    if (!BoxesOverlap(a.Box, b.Box)) continue;
                    bboxHits++;

                    // Overlapping category sets put the same physical pair in both lists,
                    // once as (X,Y) and once as (Y,X). A canonical key collapses them, so a
                    // single collision is reported once instead of twice.
                    if (!pairs.Claim(SrcKey(a), a.El.Id.ToString(), SrcKey(b), b.El.Id.ToString())) continue;

                    List<Solid> sa, sb;
                    try { sa = Solids(a, options, solidCache); }
                    catch (Exception ex) { geometryFailures.Add(Fail(a, ex)); pairs.MarkUnresolved(); continue; }
                    try { sb = Solids(b, options, solidCache); }
                    catch (Exception ex) { geometryFailures.Add(Fail(b, ex)); pairs.MarkUnresolved(); continue; }

                    if (sa.Count == 0 || sb.Count == 0)
                    {
                        // Nothing was intersected here. Falling through silently made this
                        // indistinguishable from a pair that was tested and found clean.
                        pairs.MarkNoSolids();
                        continue;
                    }

                    solidTests++;
                    double vol = 0; bool hit = false; string boolError = null; int boolFailures = 0;
                    double centroidVol = 0; XYZ centroidSum = XYZ.Zero;
                    foreach (var x in sa)
                    {
                        foreach (var y in sb)
                        {
                            try
                            {
                                var inter = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                                if (inter != null && inter.Volume > tolFt3)
                                {
                                    hit = true; vol += inter.Volume;
                                    if (planPenetrations || recordFindings)
                                        try { centroidSum += inter.ComputeCentroid() * inter.Volume; centroidVol += inter.Volume; }
                                        catch { }
                                }
                            }
                            catch (Exception ex) { boolError = ex.Message; boolFailures++; }
                        }
                    }

                    // A boolean that threw makes the pair unresolved WHETHER OR NOT the
                    // other solids happened to hit. This used to be discarded when hit was
                    // true, so a partially-failed pair was published as a clash with a
                    // volume short by an unknown amount, and coverage still said complete.
                    if (boolFailures > 0)
                    {
                        pairs.MarkUnresolved();
                        geometryFailures.Add(new JObject
                        {
                            ["a"] = Describe(a),
                            ["b"] = Describe(b),
                            ["error"] = boolFailures + " boolean intersection(s) failed: " + boolError,
                            ["consequence"] = hit
                                ? "A clash IS reported for this pair, but its intersection volume is short by an " +
                                  "unknown amount: some solid pairs never resolved."
                                : "This PAIR is unresolved. It is not reported as clean."
                        });
                    }

                    if (!hit) continue;

                    clashes.Add(new JObject
                    {
                        ["a"] = Describe(a),
                        ["b"] = Describe(b),
                        ["intersection_volume_m3"] = Math.Round(Guard.ToM3(vol), 6),
                        ["intersection_volume_is_complete"] = boolFailures == 0,
                        ["cross_model"] = a.Source != b.Source || a.InstanceId != b.InstanceId
                    });
                    if (planPenetrations || recordFindings)
                    {
                        var detail = new HitDetail { A = a, B = b, ClashIndex = clashes.Count - 1 };
                        if (centroidVol > 1e-12)
                        {
                            detail.Centroid = centroidSum / centroidVol;
                            detail.PointBasis = "intersection_centroid";
                        }
                        else
                        {
                            // The centroid could not be computed; the overlap of the two
                            // host-coordinate boxes still locates the crossing, and the
                            // row SAYS which basis it used.
                            detail.Centroid = BoxOverlapMidpoint(a.Box, b.Box);
                            detail.PointBasis = "bbox_overlap_midpoint";
                        }
                        hitDetails.Add(detail);
                    }
                    if (clashes.Count >= maxResults) goto done;
                }
            }
        done:

            // Completeness now accounts for elements that never entered the check and pairs
            // that were never resolved, not just skipped links. Those were the two ways a
            // partial run used to be published as "complete".
            bool partial = linksSkipped.Count > 0
                           || geometryFailures.Count > 0
                           || clashes.Count >= maxResults
                           || !ledgerA.Complete
                           || !ledgerB.Complete
                           || !pairs.Complete
                           // A closed workset means elements were never offered to either
                           // side. That is the same consequence as dropping them, so it
                           // reaches the same flag.
                           || !visibilityComplete;

            var clashResult = new JObject
            {
                ["clash_count"] = clashes.Count,
                ["result"] = partial ? "partial" : "complete",
                ["elements_a"] = sideA.Count,
                ["elements_b"] = sideB.Count,
                ["bbox_candidates"] = bboxHits,
                ["solid_tests"] = solidTests,
                ["pairs_tested"] = pairs.Tested,
                ["pairs_deduplicated"] = pairs.Duplicates,
                ["truncated"] = clashes.Count >= maxResults,
                ["coverage"] = Coverage(sources, linksSkipped, geometryFailures, ledgerA, ledgerB, pairs),
                ["visibility_coverage"] = visibility,
                ["clashes"] = clashes,
                ["headline"] = Headline(clashes.Count, partial, linksSkipped.Count, geometryFailures.Count,
                                        sources.Count, ledgerA, ledgerB, pairs) +
                               (visibilityComplete ? "" : " " + (string)visibility["note"])
            };
            if (planPenetrations)
            {
                JObject nextArguments;
                clashResult["penetrations"] = PlanPenetrations(doc, hitDetails, clearanceMm / 304.8,
                    allowStructuralHosts, sleeveTypeId, clusterRadiusMm / 304.8, out nextArguments);
                if (nextArguments != null) clashResult["next_arguments"] = nextArguments;
            }
            if (recordFindings)
                clashResult["findings"] = RecordFindings(doc, request, hitDetails, tolMm, includeLinks, partial);
            return CommandResult.Ok(clashResult);
        }

        /// <summary>
        /// How much of EACH source was loaded, and one answer over all of them.
        ///
        /// A clash is a statement about a federated model. Measuring the host alone would
        /// report full coverage over a check that never saw three worksets of the
        /// structural link - and the link is exactly where the other discipline lives, so
        /// it is both the likelier place for this and the one that matters more.
        ///
        /// The aggregate is AND, not average: one source with a closed workset makes the
        /// whole answer incomplete, because a clash that could not happen is not a clash
        /// that did not happen.
        /// </summary>
        private static JObject SourceCoverage(List<Src> sources, out bool allComplete)
        {
            var bySource = new JArray();
            var incomplete = new List<string>();
            allComplete = true;

            foreach (Src s in sources)
            {
                DocumentVisibilityCoverage c = DocumentVisibility.Measure(s.Doc);
                JObject entry = c.ToJson();
                entry["source"] = s.Name;
                entry["is_host"] = string.Equals(s.Name, "host", StringComparison.Ordinal);
                if (!string.IsNullOrEmpty(s.InstanceId)) entry["link_instance_id"] = s.InstanceId;
                bySource.Add(entry);

                if (!c.CoverageComplete)
                {
                    allComplete = false;
                    incomplete.Add(s.Name + (c.WorksetsClosed.HasValue
                        ? " (" + c.WorksetsClosed.Value + " of " + c.WorksetsTotal.Value + " worksets closed)"
                        : " (coverage unreadable)"));
                }
            }

            return new JObject
            {
                ["coverage_complete"] = allComplete,
                ["sources_measured"] = sources.Count,
                ["sources_incomplete"] = incomplete.Count,
                ["by_source"] = bySource,
                ["note"] = allComplete
                    ? "Every source in this check - the host and each loaded link - had all of its worksets open, " +
                      "so the geometry compared was all the geometry there is."
                    : "INCOMPLETE COVERAGE in " + incomplete.Count + " of " + sources.Count + " source(s): " +
                      string.Join("; ", incomplete) + ". The elements on a closed workset are not hidden - they are " +
                      "NOT IN THAT DOCUMENT, so they were never offered to either side of this check and appear in " +
                      "no ledger. A clash against them could not have been found. DO NOT READ AN ABSENCE of " +
                      "clashes here as coordination: re-open the affected model(s) with all worksets open and run " +
                      "it again."
            };
        }

        private static string Headline(int count, bool partial, int skipped, int failures, int sourceCount,
                                       SideLedger a, SideLedger b, PairLedger pairs)
        {
            if (count == 0 && !partial)
                return $"No clashes, and the check was complete across {sourceCount} model(s). This zero can be relied on.";

            // Name what was missed, specifically. "Partial" without the reason is a label
            // nobody acts on.
            var gaps = new List<string>();
            if (skipped > 0) gaps.Add(skipped + " link(s) not checked");
            int dropped = (a.Candidates - a.Included) + (b.Candidates - b.Included);
            if (dropped > 0) gaps.Add(dropped + " element(s) never entered the check");
            if (pairs.Unresolved > 0) gaps.Add(pairs.Unresolved + " pair(s) unresolved");
            if (pairs.SkippedNoSolids > 0) gaps.Add(pairs.SkippedNoSolids + " pair(s) with no usable solid");
            if (failures > 0 && gaps.Count == 0) gaps.Add(failures + " geometry failure(s)");
            string why = gaps.Count == 0 ? "" : " (" + string.Join(", ", gaps) + ")";

            if (count == 0 && partial)
                return "No clashes found, but the check was PARTIAL" + why + ". Do not read this as coordinated - " +
                       "see coverage. A zero that was never measured is not a zero.";
            if (partial)
                return count + " clash(es) found, and the check was PARTIAL" + why + " - there may be more. " +
                       "See coverage.";
            return $"{count} clash(es) found across {sourceCount} model(s). The check was complete.";
        }

        private static JObject Coverage(List<Src> sources, JArray skipped, JArray failures,
                                        SideLedger a, SideLedger b, PairLedger pairs)
        {
            return new JObject
            {
                ["models_checked"] = new JArray(sources.Select(s => (JToken)s.Name)),
                ["links_not_checked"] = skipped,
                ["unresolved_pairs"] = failures,
                ["side_a"] = Side(a),
                ["side_b"] = Side(b),
                ["pairs"] = new JObject
                {
                    ["tested"] = pairs.Tested,
                    ["deduplicated"] = pairs.Duplicates,
                    ["unresolved"] = pairs.Unresolved,
                    ["skipped_no_solids"] = pairs.SkippedNoSolids,
                    ["complete"] = pairs.Complete
                },
                // complete now means: no link skipped, no geometry failure, every collected
                // element actually entered the check, and every claimed pair resolved.
                ["complete"] = skipped.Count == 0 && failures.Count == 0 &&
                               a.Complete && b.Complete && pairs.Complete
            };
        }

        private static JObject Side(SideLedger l) => new JObject
        {
            ["candidates"] = l.Candidates,
            ["checked"] = l.Included,
            ["excluded"] = l.Excluded,
            ["failed"] = l.Failed,
            ["complete"] = l.Complete,
            ["reasons"] = new JArray(l.Reasons.Select(r => (JToken)new JObject
            {
                ["reason"] = r.Key,
                ["elements"] = r.Value
            })),
            ["examples"] = new JArray(l.Examples.Select(e => (JToken)e)),
            ["summary"] = l.Describe()
        };

        /// <summary>Source identity for the pair key: the model plus the link placement.</summary>
        private static string SrcKey(Item it) => PairLedger.ElementKey(it.Source, it.InstanceId, null);

        // ---------------------------------------------------------------------
        // Penetration planning over the clash pairs. READ-ONLY: the output is a
        // plan; the writing happens through create_elements' own pipeline.
        // ---------------------------------------------------------------------
        private class HitDetail
        {
            public Item A, B; public int ClashIndex; public XYZ Centroid; public string PointBasis;
        }

        private static XYZ BoxOverlapMidpoint(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            double x1 = Math.Max(a.Min.X, b.Min.X), x2 = Math.Min(a.Max.X, b.Max.X);
            double y1 = Math.Max(a.Min.Y, b.Min.Y), y2 = Math.Min(a.Max.Y, b.Max.Y);
            double z1 = Math.Max(a.Min.Z, b.Min.Z), z2 = Math.Min(a.Max.Z, b.Max.Z);
            return new XYZ((x1 + x2) / 2, (y1 + y2) / 2, (z1 + z2) / 2);
        }

        private static bool HostIsStructural(Element host)
        {
            try
            {
                if (host is Wall wall)
                    return wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;
                if (host is Floor floor)
                    return floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;
                Category category = host.Category;
                if (category != null)
                {
                    long id = Rid.Value(category.Id);
                    if (id == (long)BuiltInCategory.OST_StructuralFraming ||
                        id == (long)BuiltInCategory.OST_StructuralColumns ||
                        id == (long)BuiltInCategory.OST_StructuralFoundation) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Fold this run's hits into the document's durable finding ledger. A hit
        /// whose element identity cannot be read is COUNTED AND NAMED rather than
        /// silently dropped, and a partial run refreshes but resolves nothing -
        /// CoordinationRules holds that line; this method only feeds it facts.
        /// </summary>
        private static JObject RecordFindings(Document doc, JObject request, List<HitDetail> details,
                                              double tolMm, bool includeLinks, bool partial)
        {
            var catsAScope = ((JArray)request["categories_a"]).Select(t => (string)t).ToList();
            var catsBScope = ((JArray)request["categories_b"]).Select(t => (string)t).ToList();
            string scope = CoordinationRules.ScopeKey(catsAScope, catsBScope, tolMm, includeLinks);
            var detected = new List<CoordinationDetected>();
            var unrecordable = new JArray();
            foreach (HitDetail detail in details)
            {
                string uidA = null, uidB = null;
                try { uidA = detail.A.El.UniqueId; } catch { }
                try { uidB = detail.B.El.UniqueId; } catch { }
                if (string.IsNullOrEmpty(uidA) || string.IsNullOrEmpty(uidB))
                {
                    unrecordable.Add(new JObject
                    {
                        ["clash_index"] = detail.ClashIndex,
                        ["reason"] = "an element's UniqueId could not be read, so this pair has no durable identity"
                    });
                    continue;
                }
                detected.Add(new CoordinationDetected
                {
                    SideA = CoordinationRules.SideKey(detail.A.Source, detail.A.InstanceId, uidA),
                    SideB = CoordinationRules.SideKey(detail.B.Source, detail.B.InstanceId, uidB),
                    CategoryA = SafeCat(detail.A.El),
                    CategoryB = SafeCat(detail.B.El),
                    PointMm = detail.Centroid == null ? null : new[]
                    {
                        Math.Round(detail.Centroid.X * 304.8, 1),
                        Math.Round(detail.Centroid.Y * 304.8, 1),
                        Math.Round(detail.Centroid.Z * 304.8, 1)
                    }
                });
            }
            // Any pair that could not be recorded makes the run's resolution evidence
            // incomplete for this scope, exactly like a partial run.
            bool runComplete = !partial && unrecordable.Count == 0;
            string docPath; try { docPath = doc.PathName; } catch { docPath = null; }
            string ledgerPath = CoordinationLedger.PathFor(doc.Title, docPath);
            string ledgerDoc;
            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out ledgerDoc);
            string now = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            CoordinationMergeOutcome outcome = CoordinationRules.Merge(ledger, detected, now, runComplete, scope);
            CoordinationLedger.Save(ledgerPath, doc.Title, ledger);
            int openTotal = ledger.Values.Count(f => f.Status != CoordinationRules.StatusResolvedByModel &&
                                                     f.Status != CoordinationRules.StatusClosedByDecision);
            var block = new JObject
            {
                ["ledger_path"] = ledgerPath,
                ["scope"] = scope,
                ["new"] = outcome.New,
                ["persisting"] = outcome.Persisting,
                ["regressions"] = outcome.Regressions,
                ["resolved_by_model"] = outcome.ResolvedByModel,
                ["resolution_skipped_because_partial"] = outcome.ResolutionSkippedBecausePartial || !runComplete,
                ["not_recordable"] = unrecordable,
                ["open_or_assigned_total"] = openTotal,
                ["note"] = "Work the ledger with horizun_coordination (list/update/export)."
            };
            return block;
        }

        private static JArray PlanPenetrations(Document doc, List<HitDetail> details, double clearanceFeet,
                                               bool allowStructuralHosts, long? sleeveTypeId,
                                               double clusterRadiusFeet, out JObject nextArguments)
        {
            nextArguments = null;
            var rows = new JArray();
            var elements = new JArray();
            // Plannable candidates collected first so clustering can fold them by host.
            var wallCandidates = new List<JObject>();   // {host_id, corner_1/2 (feet arrays), row}
            var slabCandidates = new List<JObject>();
            foreach (HitDetail detail in details)
            {
                bool aIsMep = detail.A.El is MEPCurve, bIsMep = detail.B.El is MEPCurve;
                var row = new JObject { ["clash_index"] = detail.ClashIndex };
                rows.Add(row);
                string code, reason; bool penetrantIsA;
                if (!PenetrationRules.ClassifyPair(aIsMep, bIsMep, out penetrantIsA, out code, out reason))
                {
                    row["status"] = "skipped"; row["code"] = code; row["reason"] = reason;
                    continue;
                }
                Item pen = penetrantIsA ? detail.A : detail.B;
                Item host = penetrantIsA ? detail.B : detail.A;
                row["penetrant"] = Describe(pen);
                if (pen.InstanceId != null) row["penetrant"]["link_instance_id"] = pen.InstanceId;
                row["host"] = Describe(host);
                row["point_mm"] = PointMm(detail.Centroid);
                row["point_basis"] = detail.PointBasis;

                if (!PenetrationRules.HostPermitted(host.InstanceId == null, HostIsStructural(host.El),
                                                    allowStructuralHosts, out code, out reason))
                {
                    row["status"] = "refused"; row["code"] = code; row["reason"] = reason;
                    continue;
                }

                XYZ direction = null;
                if ((pen.El.Location as LocationCurve)?.Curve is Line line)
                    direction = pen.Xf.OfVector(line.Direction);
                string shape; double widthFeet, heightFeet;
                bool profiled = MepFacts.TryProfile(pen.El, out shape, out widthFeet, out heightFeet);
                if (direction == null || !profiled)
                {
                    row["status"] = "refused";
                    row["code"] = PenetrationRules.CodeNoCrossSection;
                    row["reason"] = direction == null
                        ? "the penetrant is not a straight run, so the crossing has no single direction to size an opening from."
                        : "the penetrant has no profiled connector, so its cross-section could not be measured.";
                    continue;
                }
                row["direction"] = new JArray(Math.Round(direction.X, 4), Math.Round(direction.Y, 4), Math.Round(direction.Z, 4));
                row["cross_section"] = new JObject
                {
                    ["shape"] = shape,
                    ["width_mm"] = Math.Round(widthFeet * 304.8, 1),
                    ["height_mm"] = Math.Round(heightFeet * 304.8, 1)
                };

                bool hostIsSlab = host.El is Floor || host.El is RoofBase || host.El is Ceiling;
                if (host.El is Wall)
                {
                    double[] corner1, corner2;
                    if (!PenetrationRules.OpeningCorners(detail.Centroid.X, detail.Centroid.Y, detail.Centroid.Z,
                                                         direction.X, direction.Y, direction.Z,
                                                         widthFeet, heightFeet, clearanceFeet,
                                                         out corner1, out corner2, out code, out reason))
                    {
                        row["status"] = "refused"; row["code"] = code; row["reason"] = reason;
                        continue;
                    }
                    row["status"] = "plannable";
                    row["plan"] = "wall_opening";
                    bool structuralWall = allowStructuralHosts && HostIsStructural(host.El);
                    wallCandidates.Add(new JObject
                    {
                        ["host_id"] = Rid.Value(host.El.Id),
                        ["c1"] = new JArray(corner1), ["c2"] = new JArray(corner2),
                        ["allow_structural"] = structuralWall
                    });
                }
                else if (hostIsSlab && Math.Abs(direction.Z) > PenetrationRules.MaxVerticalComponentForWallOpening)
                {
                    // A near-vertical run through a horizontal host: the slab opening,
                    // circular for round penetrants, rectangular otherwise.
                    row["status"] = "plannable";
                    row["plan"] = "slab_opening";
                    bool structuralSlab = allowStructuralHosts && HostIsStructural(host.El);
                    slabCandidates.Add(new JObject
                    {
                        ["host_id"] = Rid.Value(host.El.Id),
                        ["shape"] = shape == "round" ? "circular" : "rectangular",
                        ["cx"] = detail.Centroid.X, ["cy"] = detail.Centroid.Y, ["cz"] = detail.Centroid.Z,
                        ["w"] = widthFeet + 2 * clearanceFeet, ["h"] = heightFeet + 2 * clearanceFeet,
                        ["allow_structural"] = structuralSlab
                    });
                }
                else if (sleeveTypeId != null)
                {
                    row["status"] = "plannable";
                    row["plan"] = "sleeve_family_instance";
                    // Orient the sleeve along the run's plan direction, stated.
                    double rotation = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
                    row["note"] = "the sleeve is placed at the crossing point and rotated " +
                                  Math.Round(rotation, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                  " degrees about Z to the run's plan direction.";
                    elements.Add(new JObject
                    {
                        ["kind"] = "family_instance",
                        ["type_id"] = sleeveTypeId.Value,
                        ["point"] = PointMm(detail.Centroid),
                        ["rotation_degrees"] = Math.Round(rotation, 2)
                    });
                }
                else
                {
                    row["status"] = "refused";
                    row["code"] = PenetrationRules.CodeOpeningWallsOnly;
                    row["reason"] = "the host takes no rectangular opening from this crossing (not a wall; not a " +
                                    "near-vertical run through a slab). Pass sleeve_type_id to plan a point-placed, " +
                                    "run-oriented sleeve family here instead.";
                }
            }

            // ---- clustering: crossings that share a host fold into one opening. ----
            EmitWallOpenings(elements, wallCandidates, clusterRadiusFeet, rows);
            EmitSlabOpenings(elements, slabCandidates, clusterRadiusFeet, rows);

            if (elements.Count > 0)
            {
                nextArguments = new JObject
                {
                    ["tool"] = "horizun_create_elements",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = doc.Title,
                        ["units"] = "mm",
                        ["elements"] = elements
                    }
                };
            }
            return rows;
        }

        private static void EmitWallOpenings(JArray elements, List<JObject> candidates, double clusterRadiusFeet,
                                             JArray rows)
        {
            foreach (var byHost in candidates.GroupBy(c => (long)c["host_id"]))
            {
                var members = byHost.ToList();
                var centers = members.Select(m =>
                {
                    var c1 = (JArray)m["c1"]; var c2 = (JArray)m["c2"];
                    return new[] { ((double)c1[0] + (double)c2[0]) / 2, ((double)c1[1] + (double)c2[1]) / 2,
                                   ((double)c1[2] + (double)c2[2]) / 2 };
                }).ToList();
                foreach (List<int> group in PenetrationRules.Cluster(centers, clusterRadiusFeet))
                {
                    double[] corner1, corner2;
                    PenetrationRules.ClusterCorners(
                        group.Select(i => ((JArray)members[i]["c1"]).Select(t => (double)t).ToArray()).ToList(),
                        group.Select(i => ((JArray)members[i]["c2"]).Select(t => (double)t).ToArray()).ToList(),
                        out corner1, out corner2);
                    var opening = new JObject
                    {
                        ["kind"] = "wall_opening",
                        ["host_id"] = byHost.Key,
                        ["corner_1"] = FeetToMm(corner1),
                        ["corner_2"] = FeetToMm(corner2)
                    };
                    if (group.Any(i => (bool)members[i]["allow_structural"])) opening["allow_structural"] = true;
                    if (group.Count > 1) opening["clusters_crossings"] = group.Count;
                    elements.Add(opening);
                }
            }
        }

        private static void EmitSlabOpenings(JArray elements, List<JObject> candidates, double clusterRadiusFeet,
                                             JArray rows)
        {
            foreach (var byHost in candidates.GroupBy(c => (long)c["host_id"]))
            {
                var members = byHost.ToList();
                var centers = members.Select(m => new[] { (double)m["cx"], (double)m["cy"], (double)m["cz"] }).ToList();
                foreach (List<int> group in PenetrationRules.Cluster(centers, clusterRadiusFeet))
                {
                    if (group.Count == 1)
                    {
                        JObject m = members[group[0]];
                        var single = new JObject
                        {
                            ["kind"] = "slab_opening",
                            ["host_id"] = byHost.Key,
                            ["shape"] = (string)m["shape"],
                            ["center"] = FeetToMm(new[] { (double)m["cx"], (double)m["cy"], (double)m["cz"] })
                        };
                        if ((string)m["shape"] == "circular") single["diameter"] = Math.Round(Math.Max((double)m["w"], (double)m["h"]) * 304.8, 1);
                        else { single["width"] = Math.Round((double)m["w"] * 304.8, 1); single["height"] = Math.Round((double)m["h"] * 304.8, 1); }
                        if ((bool)m["allow_structural"]) single["allow_structural"] = true;
                        elements.Add(single);
                        continue;
                    }
                    // A cluster becomes one RECTANGLE spanning every member plus its size.
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, z = 0;
                    bool anyStructural = false;
                    foreach (int i in group)
                    {
                        JObject m = members[i];
                        double cx = (double)m["cx"], cy = (double)m["cy"], hw = (double)m["w"] / 2, hh = (double)m["h"] / 2;
                        minX = Math.Min(minX, cx - hw); maxX = Math.Max(maxX, cx + hw);
                        minY = Math.Min(minY, cy - hh); maxY = Math.Max(maxY, cy + hh);
                        z = (double)m["cz"];
                        if ((bool)m["allow_structural"]) anyStructural = true;
                    }
                    var clustered = new JObject
                    {
                        ["kind"] = "slab_opening",
                        ["host_id"] = byHost.Key,
                        ["shape"] = "rectangular",
                        ["center"] = FeetToMm(new[] { (minX + maxX) / 2, (minY + maxY) / 2, z }),
                        ["width"] = Math.Round((maxX - minX) * 304.8, 1),
                        ["height"] = Math.Round((maxY - minY) * 304.8, 1),
                        ["clusters_crossings"] = group.Count
                    };
                    if (anyStructural) clustered["allow_structural"] = true;
                    elements.Add(clustered);
                }
            }
        }

        private static JArray PointMm(XYZ point) => new JArray(
            Math.Round(point.X * 304.8, 1), Math.Round(point.Y * 304.8, 1), Math.Round(point.Z * 304.8, 1));

        private static JArray FeetToMm(double[] point) => new JArray(
            Math.Round(point[0] * 304.8, 1), Math.Round(point[1] * 304.8, 1), Math.Round(point[2] * 304.8, 1));

        private class Src
        {
            public string Name;
            public Document Doc;
            public Transform Xf;
            /// <summary>The RevitLinkInstance id, or null for the host. Part of the cache key.</summary>
            public string InstanceId;
        }

        private class Item
        {
            public Element El;
            public string Source;
            public string InstanceId;
            public Transform Xf;
            public BoundingBoxXYZ Box;   // already in host coordinates
        }

        /// <summary>
        /// Collect one side, RECORDING every element that does not make it.
        ///
        /// This used to be three bare `continue`s - a failed collector, a throwing
        /// bounding box, and a null bounding box - so elements left the check without a
        /// trace and the reported candidate count was the count of survivors. An element
        /// missing from a clash run does not produce a wrong clash; it produces a missing
        /// one, which reads exactly like coordination.
        /// </summary>
        private static List<Item> Collect(List<Src> sources, List<BuiltInCategory> cats, SideLedger ledger)
        {
            var list = new List<Item>();
            foreach (var s in sources)
            {
                foreach (var c in cats)
                {
                    IList<Element> els;
                    try
                    {
                        els = new FilteredElementCollector(s.Doc)
                            .OfCategory(c).WhereElementIsNotElementType().ToElements();
                    }
                    catch (Exception ex)
                    {
                        // A whole category of a whole model, gone. Counted as ONE failure
                        // because we cannot know how many elements it would have held -
                        // and said so, rather than implying the category was empty.
                        ledger.Add(ClashInclusion.Failed,
                            "The collector threw for category " + c + " in '" + s.Name + "', so an UNKNOWN number " +
                            "of elements from it were never considered: " + ex.Message,
                            c + " @ " + s.Name);
                        continue;
                    }

                    foreach (var e in els)
                    {
                        BoundingBoxXYZ bb;
                        try { bb = e.get_BoundingBox(null); }
                        catch (Exception ex)
                        {
                            ledger.Add(ClashInclusion.Failed,
                                "Bounding box could not be read: " + ex.Message,
                                Ident(e, s.Name));
                            continue;
                        }

                        if (bb == null)
                        {
                            ledger.Add(ClashInclusion.Excluded,
                                "No bounding box. The element has no extent the broad phase can use, so it was " +
                                "never tested against anything.",
                                Ident(e, s.Name));
                            continue;
                        }

                        ledger.Add(ClashInclusion.Included);
                        list.Add(new Item
                        {
                            El = e,
                            Source = s.Name,
                            InstanceId = s.InstanceId,
                            Xf = s.Xf,
                            Box = ToHost(bb, s.Xf)
                        });
                    }
                }
            }
            return list;
        }

        private static string Ident(Element e, string source)
        {
            try { return source + " #" + e.Id; } catch { return source + " #(id unreadable)"; }
        }

        /// <summary>
        /// A link is placed with a transform. Comparing raw link coordinates against
        /// host coordinates yields confident nonsense — clashes where nothing touches
        /// and silence where things collide.
        /// </summary>
        private static BoundingBoxXYZ ToHost(BoundingBoxXYZ bb, Transform xf)
        {
            if (xf == null || xf.IsIdentity) return bb;
            // Transform all 8 corners: a rotated link's AABB is not the transformed AABB.
            var pts = new List<XYZ>();
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                        pts.Add(xf.OfPoint(new XYZ(x, y, z)));

            return new BoundingBoxXYZ
            {
                Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)),
                Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z))
            };
        }

        private static bool BoxesOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
                && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
                && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
        }

        private static List<Solid> Solids(Item it, Options opt, Dictionary<string, List<Solid>> cache)
        {
            // Source name + link INSTANCE id + element id, with a separator that cannot
            // appear in any of them. Keyed on the name alone, two placements of one link
            // collided and the second silently reused the first's transformed solids.
            var key = PairLedger.ElementKey(it.Source, it.InstanceId, it.El.Id.ToString());
            if (cache.TryGetValue(key, out var hit)) return hit;

            var acc = new List<Solid>();
            var geom = it.El.get_Geometry(opt);
            if (geom != null) Harvest(geom, acc);

            // Same reason as the bounding box: link solids must land in host space.
            if (it.Xf != null && !it.Xf.IsIdentity)
                acc = acc.Select(s => SolidUtils.CreateTransformed(s, it.Xf)).ToList();

            cache[key] = acc;
            return acc;
        }

        private static void Harvest(GeometryObject go, List<Solid> acc)
        {
            if (go is Solid s) { if (s.Volume > 1e-9 && s.Faces.Size > 0) acc.Add(s); }
            else if (go is GeometryInstance gi)
            {
                var g = gi.GetInstanceGeometry();
                if (g != null) foreach (var o in g) Harvest(o, acc);
            }
            else if (go is GeometryElement ge) { foreach (var o in ge) Harvest(o, acc); }
        }

        private static JObject Describe(Item it)
        {
            return new JObject
            {
                ["element_id"] = it.El.Id.ToString(),
                ["source_model"] = it.Source,
                ["name"] = SafeName(it.El),
                ["category"] = SafeCat(it.El)
            };
        }

        private static JObject Fail(Item it, Exception ex)
        {
            return new JObject
            {
                ["element"] = Describe(it),
                ["error"] = "Geometry could not be read: " + ex.Message,
                ["consequence"] = "This element was NOT checked for clashes. Its pairs are unresolved, not clean."
            };
        }

        private static List<BuiltInCategory> ParseCats(JArray arr, out string error)
        {
            error = null;
            var outp = new List<BuiltInCategory>();
            if (arr == null || arr.Count == 0) { error = "categories_a and categories_b are required and must not be empty."; return outp; }
            foreach (var t in arr)
            {
                var s = t.ToString();
                if (!Enum.TryParse<BuiltInCategory>(s, true, out var bic))
                {
                    // A category name we cannot parse must not be dropped silently:
                    // the caller would think that discipline had been checked.
                    error = $"'{s}' is not a BuiltInCategory name. Expected e.g. OST_Walls, OST_DuctCurves. " +
                            "Refusing to run rather than quietly check fewer categories than you asked for.";
                    return outp;
                }
                outp.Add(bic);
            }
            return outp;
        }

        private static string SafeName(Element e) { try { return e?.Name; } catch { return null; } }
        private static string SafeCat(Element e) { try { return e?.Category?.Name; } catch { return null; } }
    }
}
