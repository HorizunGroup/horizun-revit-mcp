// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_coordination operation=import_navisworks - the Revit side of the
// naviscoord-mcp -> Revit MCP handoff. navis_handoff writes
// coordination_handoff.json (every issue, both sides) or the lighter
// revit_worklist.json (one side only). This reads either, resolves each named
// element in the ACTIVE document or a loaded RVT link (matched by source_file,
// naviscoord/interop.py normalized), and RE-DETECTS the pair here before it ever
// becomes a finding.
//
// THE RULE THIS FOLLOWS, same as the BCF import beside it: AN EXTERNAL TOOL NEVER
// CREATES A FINDING BY ASSERTION. Navisworks says a pair clashes; this measures
// whether the SAME two elements, in THIS model, still do - a hard boolean
// intersection, or a report of how far apart they measured when they do not. Only
// a REPRODUCED pair is folded into the ledger, with runComplete=false always: a
// spot-check over named issues is not a detection run over a category pair, and it
// must never mark anything resolved_by_model - that stays horizun_clash's and
// horizun_resolve_clash's job exclusively (CoordinationRules.Merge already refuses
// to resolve on an incomplete run; this command never even claims completeness).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CoordinationCommand
    {
        private const double MmPerFoot3 = 304.8;
        private const double TinyVolumeFt3 = 1e-6;
        private const int MaxTargetsPerSide = 10;   // bounds the cross product per issue
        private const int MaxIssuesReported = 200;

        private static CommandResult ImportNavisworks(Document doc, JObject request, string ledgerPath)
        {
            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.Fail("path is required: the coordination_handoff.json (preferred) or " +
                    "revit_worklist.json navis_handoff wrote.");
            if (!File.Exists(path))
                return CommandResult.Fail("no file at '" + path + "'. Nothing was read.");

            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex) { return CommandResult.Fail("'" + path + "' could not be read: " + ex.Message); }

            NavisHandoffParseResult parsed = NavisworksHandoff.Parse(json);
            if (!parsed.Ok)
                return CommandResult.Fail("'" + path + "' could not be parsed as a naviscoord handoff: " + parsed.Error);
            if (parsed.Issues.Count == 0)
                return CommandResult.Fail("'" + path + "' parsed with schema '" + parsed.Schema + "' and holds no " +
                    "issue (folded issues are already excluded upstream). Nothing to import.");

            // ---- where an element can live: the host, plus every LOADED rvt link -------
            var linkSources = new List<LinkSrc>();
            var linksUnloaded = new List<string>();
            foreach (RevitLinkInstance li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                string label = SafeLinkName(li);
                Document ldoc;
                try { ldoc = li.GetLinkDocument(); }
                catch { ldoc = null; }
                if (ldoc == null) { linksUnloaded.Add(label); continue; }
                linkSources.Add(new LinkSrc { Label = label, Doc = ldoc, Xf = li.GetTotalTransform(), InstanceId = li.Id.ToString() });
            }

            int issuesTotal = parsed.Issues.Count;
            int traceable = 0, matched = 0, reproduced = 0, notReproduced = 0;
            var notTraceable = new JArray();
            var reproducedRows = new JArray();
            var notReproducedRows = new JArray();
            var detected = new List<CoordinationDetected>();
            var matchRule = "source_file matched against the active document's title and every LOADED link's " +
                             "document title, both normalized: extension stripped (.rvt/.nwc/.nwd/.nwf/.ifc), " +
                             "then a trailing NWC/export/copy/'(n)' decoration stripped, compared case-insensitively.";

            if (parsed.OneSidedOnly)
            {
                // revit_worklist.json keeps only the responsible side (naviscoord's own
                // revit_worklist filters the OTHER side out) - there is no fixed-side element
                // to pair against, so nothing here can be re-detected. Reported, not guessed.
                foreach (NavisIssue oneSided in parsed.Issues.Take(MaxIssuesReported))
                    notTraceable.Add(new JObject
                    {
                        ["issue_id"] = oneSided.IssueId,
                        ["reason"] = "revit_worklist.json carries only the responsible side per issue" +
                                     (string.IsNullOrEmpty(oneSided.Responsible) ? "" : " (" + oneSided.Responsible + ")") +
                                     "; the fixed side has no element here to pair against. Use " +
                                     "coordination_handoff.json to reproduce the clash."
                    });
                return BuildImportReport(doc, request, path, parsed, issuesTotal, 0, 0, 0, 0, issuesTotal,
                    notTraceable, new JArray(), new JArray(), linksUnloaded, matchRule, ledgerPath,
                    new List<CoordinationDetected>());
            }

            foreach (NavisIssue issue in parsed.Issues.Take(MaxIssuesReported))
            {
                List<NavisTarget> sideA = issue.Side("a").Where(t => t.ActionableInRevit && t.ElementIdValue().HasValue).Take(MaxTargetsPerSide).ToList();
                List<NavisTarget> sideB = issue.Side("b").Where(t => t.ActionableInRevit && t.ElementIdValue().HasValue).Take(MaxTargetsPerSide).ToList();
                if (sideA.Count == 0 || sideB.Count == 0)
                {
                    notTraceable.Add(new JObject
                    {
                        ["issue_id"] = issue.IssueId,
                        ["reason"] = "no Element Id on side " + (sideA.Count == 0 ? "a" : "b") +
                                     " (the NWC likely carries no 'Element Id' property for it)"
                    });
                    continue;
                }
                traceable++;

                var resolvedA = sideA.Select(t => ResolveTarget(doc, linkSources, t)).ToList();
                var resolvedB = sideB.Select(t => ResolveTarget(doc, linkSources, t)).ToList();
                var okA = resolvedA.Where(r => r.Element != null).ToList();
                var okB = resolvedB.Where(r => r.Element != null).ToList();
                if (okA.Count == 0 || okB.Count == 0)
                {
                    var reasons = resolvedA.Concat(resolvedB).Where(r => r.Element == null)
                        .Select(r => r.Reason).Distinct().Take(3);
                    notTraceable.Add(new JObject
                    {
                        ["issue_id"] = issue.IssueId,
                        ["reason"] = "no target on side " + (okA.Count == 0 ? "a" : "b") +
                                     " resolved to a loaded document: " + string.Join("; ", reasons)
                    });
                    continue;
                }
                matched++;

                bool issueReproduced = false;
                JObject bestPair = null;
                var pairRows = new JArray();
                var cache = new Dictionary<string, List<Solid>>();
                var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                foreach (ResolvedTarget ra in okA)
                    foreach (ResolvedTarget rb in okB)
                    {
                        if (ReferenceEquals(ra.Element, rb.Element) && ra.InstanceId == rb.InstanceId) continue;
                        JObject pair = ReDetectPair(doc, ra, rb, options, cache);
                        pairRows.Add(pair);
                        if (pair.Value<bool>("reproduced"))
                        {
                            issueReproduced = true;
                            if (bestPair == null) bestPair = pair;
                            string uidA = SafeUid(ra.Element), uidB = SafeUid(rb.Element);
                            if (uidA != null && uidB != null)
                                detected.Add(new CoordinationDetected
                                {
                                    SideA = CoordinationRules.SideKey(ra.Source, ra.InstanceId, uidA),
                                    SideB = CoordinationRules.SideKey(rb.Source, rb.InstanceId, uidB),
                                    CategoryA = SafeCategory(ra.Element), CategoryB = SafeCategory(rb.Element),
                                    PointMm = pair["point_mm"] is JArray pm ? new[] { (double)pm[0], (double)pm[1], (double)pm[2] } : null,
                                    ExternalSource = "navisworks", ExternalIssueId = issue.IssueId,
                                    Priority = issue.Priority, Responsible = issue.Responsible,
                                    ImmovableDiscipline = issue.ImmovableSide,
                                    ImmovableSideIsA = ImmovableSideIsAForPair(issue, ra, rb),
                                    SuggestedAction = issue.SuggestedAction
                                });
                        }
                    }

                var issueRow = new JObject
                {
                    ["issue_id"] = issue.IssueId, ["priority"] = issue.Priority, ["responsible"] = issue.Responsible,
                    ["immovable_side"] = issue.ImmovableSide, ["suggested_action"] = issue.SuggestedAction,
                    ["pairs_tested"] = pairRows.Count, ["pairs"] = new JArray(pairRows.Take(5))
                };
                if (issueReproduced) { reproduced++; reproducedRows.Add(issueRow); }
                else { notReproduced++; notReproducedRows.Add(issueRow); }
            }

            return BuildImportReport(doc, request, path, parsed, issuesTotal, traceable, matched, reproduced,
                notReproduced, issuesTotal - matched, notTraceable, reproducedRows, notReproducedRows,
                linksUnloaded, matchRule, ledgerPath, detected);
        }

        private static CommandResult BuildImportReport(Document doc, JObject request, string path,
            NavisHandoffParseResult parsed, int issuesTotal, int traceable, int matched, int reproduced,
            int notReproduced, int notTraceableCount, JArray notTraceable, JArray reproducedRows,
            JArray notReproducedRows, List<string> linksUnloaded, string matchRule,
            string ledgerPath, List<CoordinationDetected> detected)
        {
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            var summary = new JObject
            {
                ["document"] = doc.Title,
                ["path"] = path,
                ["schema"] = parsed.Schema,
                ["issues_total"] = issuesTotal,
                ["traceable"] = traceable,
                ["matched"] = matched,
                ["reproduced"] = reproduced,
                ["not_reproduced"] = notReproduced,
                ["not_traceable"] = notTraceableCount,
                ["not_traceable_rows"] = new JArray(notTraceable.Take(50)),
                ["reproduced_rows"] = new JArray(reproducedRows.Take(50)),
                ["not_reproduced_rows"] = new JArray(notReproducedRows.Take(50)),
                ["links_not_loaded"] = new JArray(linksUnloaded),
                ["match_rule"] = matchRule,
                ["counts_mean"] =
                    "not_traceable = issues_total - matched: it covers BOTH causes the parameter name implies - " +
                    "no Element Id on a side (never even traceable) AND an Element Id that names no element in a " +
                    "LOADED document (traceable but unmatched, e.g. its model is not open or not linked here). " +
                    "traceable is the narrower, purely structural count (both sides carry an Element Id) reported " +
                    "for transparency between the two.",
                ["means"] = "Only REPRODUCED pairs (a measured solid intersection in THIS session) become or " +
                            "refresh a ledger finding, with origin navisworks; a matched-but-not-reproduced pair " +
                            "is reported, never invented into a finding. This import never marks anything " +
                            "resolved_by_model - that stays a MEASURED complete detection run's job."
            };

            if (dry)
            {
                summary["dry_run"] = true;
                summary["would_record"] = detected.Count;
                return CommandResult.Ok(summary);
            }

            if (detected.Count == 0)
            {
                summary["dry_run"] = false;
                summary["recorded"] = 0;
                return CommandResult.Ok(summary);
            }

            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out string ledgerTitle);
            string now = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            // runComplete is ALWAYS false here: a spot-check over named issues is not a
            // complete detection run over a category scope, and must never resolve anything.
            CoordinationMergeOutcome outcome = CoordinationRules.Merge(ledger, detected, now, runComplete: false, scopeKey: "navisworks");
            CoordinationLedger.Save(ledgerPath, ledgerTitle ?? doc.Title, ledger);

            // Re-read: the ledger is a small file and the contract does not bend for one.
            Dictionary<string, CoordinationFinding> reread = CoordinationLedger.Load(ledgerPath, out _);
            int verified = detected.Count(d =>
            {
                string a = d.SideA, b = d.SideB;
                return reread.TryGetValue(CoordinationRules.FindingId(a, b), out CoordinationFinding f) &&
                       f.ExternalSource == "navisworks";
            });

            summary["dry_run"] = false;
            summary["recorded"] = detected.Count;
            summary["new"] = outcome.New;
            summary["persisting"] = outcome.Persisting;
            summary["ledger_path"] = ledgerPath;
            summary["verified_by_reread"] = verified == detected.Count;
            if (verified != detected.Count)
                return CommandResult.FailWithDetail("The ledger was written but re-reading it shows only " + verified +
                    " of " + detected.Count + " navisworks finding(s). Success is not claimed; inspect " + ledgerPath + ".",
                    summary);
            return CommandResult.Ok(summary);
        }

        // ---- resolving one navis target to a Revit element ---------------------------

        private sealed class LinkSrc { public string Label; public Document Doc; public Transform Xf; public string InstanceId; }

        private sealed class ResolvedTarget
        {
            public Element Element;
            public string Source;        // "host" or the link's label, matching ClashCommand's convention
            public string InstanceId;
            public Transform Xf;
            public string Reason;        // set only when Element is null
        }

        private static ResolvedTarget ResolveTarget(Document hostDoc, List<LinkSrc> links, NavisTarget target)
        {
            long? id = target.ElementIdValue();
            if (id == null || !Rid.CanRepresent(id.Value))
                return new ResolvedTarget { Reason = "'" + target.RevitElementId + "' is not a usable Element Id" };

            if (NavisworksHandoff.SourceFileMatches(target.SourceFile, SafeTitle(hostDoc)))
            {
                Element e = TryGet(hostDoc, id.Value);
                if (e == null) return new ResolvedTarget { Reason = "element " + id + " does not exist in the active document" };
                return new ResolvedTarget { Element = e, Source = "host", InstanceId = null, Xf = Transform.Identity };
            }

            foreach (LinkSrc link in links)
                if (NavisworksHandoff.SourceFileMatches(target.SourceFile, SafeTitle(link.Doc)))
                {
                    Element e = TryGet(link.Doc, id.Value);
                    if (e == null) continue;   // try another instance/link of the same title before giving up
                    return new ResolvedTarget { Element = e, Source = link.Label, InstanceId = link.InstanceId, Xf = link.Xf };
                }

            return new ResolvedTarget
            {
                Reason = "source_file '" + target.SourceFile + "' (normalized '" +
                         NavisworksHandoff.NormalizeSourceFile(target.SourceFile) + "') matches neither the " +
                         "active document nor any LOADED link"
            };
        }

        private static Element TryGet(Document d, long id)
        {
            try { return d.GetElement(Rid.Make(id)); } catch { return null; }
        }

        private static string SafeTitle(Document d) { try { return d?.Title; } catch { return null; } }
        private static string SafeLinkName(RevitLinkInstance li) { try { return li?.Name; } catch { return "(unnamed link)"; } }
        private static string SafeUid(Element e) { try { return e?.UniqueId; } catch { return null; } }
        private static string SafeCategory(Element e) { try { return e?.Category?.Name; } catch { return null; } }

        /// <summary>
        /// Which LEDGER side (A or B, as CoordinationRules.Merge will order THIS pair) the
        /// issue's immovable_side names - resolved from the issue's own a/b letters, which
        /// is why this needs to know whether ra/rb came from navis side a or b.
        /// </summary>
        private static bool? ImmovableSideIsAForPair(NavisIssue issue, ResolvedTarget ra, ResolvedTarget rb)
        {
            // ra is always navis side "a" and rb navis side "b" here (the cross product is
            // built that way above), so the issue's own resolution already answers it.
            return issue.ImmovableSideIsA();
        }

        // ---- re-detecting one pair, host-coordinate solids, box distance otherwise ----

        private static JObject ReDetectPair(Document hostDoc, ResolvedTarget a, ResolvedTarget b, Options options,
                                            Dictionary<string, List<Solid>> cache)
        {
            var row = new JObject
            {
                ["a"] = new JObject { ["element_id"] = Rid.Value(a.Element.Id), ["source"] = a.Source },
                ["b"] = new JObject { ["element_id"] = Rid.Value(b.Element.Id), ["source"] = b.Source }
            };
            BoundingBoxXYZ boxA, boxB;
            try { boxA = ToHostBox(a.Element.get_BoundingBox(null), a.Xf); }
            catch (Exception ex) { row["reproduced"] = false; row["error"] = "side a geometry: " + ex.Message; return row; }
            try { boxB = ToHostBox(b.Element.get_BoundingBox(null), b.Xf); }
            catch (Exception ex) { row["reproduced"] = false; row["error"] = "side b geometry: " + ex.Message; return row; }
            if (boxA == null || boxB == null)
            {
                row["reproduced"] = false;
                row["error"] = "one side has no bounding box in this session";
                return row;
            }

            if (!BoxesOverlap(boxA, boxB))
            {
                double gap = BoxGapMm(boxA, boxB);
                row["reproduced"] = false;
                row["distance_mm"] = Math.Round(gap, 1);
                row["measure"] = "bounding_box_gap";
                return row;
            }

            List<Solid> sa = Solids(a, options, cache), sb = Solids(b, options, cache);
            double vol = 0; bool hit = false; XYZ centroidSum = XYZ.Zero; double centroidVol = 0;
            if (sa.Count > 0 && sb.Count > 0)
                foreach (Solid x in sa)
                    foreach (Solid y in sb)
                        try
                        {
                            Solid inter = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                            if (inter != null && inter.Volume > TinyVolumeFt3)
                            {
                                hit = true; vol += inter.Volume;
                                try { centroidSum += inter.ComputeCentroid() * inter.Volume; centroidVol += inter.Volume; } catch { }
                            }
                        }
                        catch { /* an unresolved boolean here is a near-miss, not a hit - never invented into one */ }

            if (hit)
            {
                row["reproduced"] = true;
                row["intersection_volume_mm3"] = Math.Round(vol * MmPerFoot3 * MmPerFoot3 * MmPerFoot3, 1);
                row["measure"] = "solid_intersection";
                XYZ centroid = centroidVol > 1e-12 ? centroidSum / centroidVol : BoxOverlapMidpoint(boxA, boxB);
                row["point_mm"] = new JArray(Math.Round(centroid.X * MmPerFoot3, 1), Math.Round(centroid.Y * MmPerFoot3, 1), Math.Round(centroid.Z * MmPerFoot3, 1));
                return row;
            }

            row["reproduced"] = false;
            row["distance_mm"] = 0.0;
            row["measure"] = sa.Count == 0 || sb.Count == 0 ? "no_solid_geometry" : "bounding_boxes_overlap_but_solids_do_not";
            return row;
        }

        private static List<Solid> Solids(ResolvedTarget t, Options options, Dictionary<string, List<Solid>> cache)
        {
            string key = (t.Source ?? "") + "|" + (t.InstanceId ?? "") + "|" + t.Element.Id;
            if (cache.TryGetValue(key, out List<Solid> hit)) return hit;
            var acc = new List<Solid>();
            try
            {
                GeometryElement g = t.Element.get_Geometry(options);
                if (g != null) Harvest(g, acc);
            }
            catch { }
            if (t.Xf != null && !t.Xf.IsIdentity)
                try { acc = acc.Select(s => SolidUtils.CreateTransformed(s, t.Xf)).ToList(); } catch { }
            cache[key] = acc;
            return acc;
        }

        private static void Harvest(GeometryObject go, List<Solid> acc)
        {
            if (go is Solid s) { if (s.Volume > 1e-9 && s.Faces.Size > 0) acc.Add(s); }
            else if (go is GeometryInstance gi) { GeometryElement g = gi.GetInstanceGeometry(); if (g != null) foreach (GeometryObject o in g) Harvest(o, acc); }
            else if (go is GeometryElement ge) { foreach (GeometryObject o in ge) Harvest(o, acc); }
        }

        private static BoundingBoxXYZ ToHostBox(BoundingBoxXYZ bb, Transform xf)
        {
            if (bb == null) return null;
            if (xf == null || xf.IsIdentity) return bb;
            var pts = new List<XYZ>();
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
                foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                        pts.Add(xf.OfPoint(new XYZ(x, y, z)));
            return new BoundingBoxXYZ
            {
                Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)),
                Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z))
            };
        }

        private static bool BoxesOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
            a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
            a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

        private static double BoxGapMm(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            double GapAxis(double aMin, double aMax, double bMin, double bMax) =>
                aMax < bMin ? bMin - aMax : (bMax < aMin ? aMin - bMax : 0);
            double gx = GapAxis(a.Min.X, a.Max.X, b.Min.X, b.Max.X);
            double gy = GapAxis(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y);
            double gz = GapAxis(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z);
            return Math.Sqrt(gx * gx + gy * gy + gz * gz) * MmPerFoot3;
        }

        private static XYZ BoxOverlapMidpoint(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            double x1 = Math.Max(a.Min.X, b.Min.X), x2 = Math.Min(a.Max.X, b.Max.X);
            double y1 = Math.Max(a.Min.Y, b.Min.Y), y2 = Math.Min(a.Max.Y, b.Max.Y);
            double z1 = Math.Max(a.Min.Z, b.Min.Z), z2 = Math.Min(a.Max.Z, b.Max.Z);
            return new XYZ((x1 + x2) / 2, (y1 + y2) / 2, (z1 + z2) / 2);
        }
    }
}
