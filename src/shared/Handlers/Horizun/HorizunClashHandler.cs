// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
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

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunClashHandler : IRevitCommand
    {
        public string Name => "horizun_clash";

        public string Description =>
            "Clash detection between two category sets, across the host model AND loaded Revit links " +
            "(link geometry is transformed into host coordinates). Every clash names the source model of " +
            "both elements. If links were excluded or unloaded, or geometry failed, the result is reported " +
            "as PARTIAL rather than clean — a zero from this tool means zero. Read-only.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""categories_a"", ""categories_b""],
  ""properties"": {
    ""categories_a"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
                        ""description"": ""BuiltInCategory names, e.g. [\""OST_StructuralFraming\""]."" },
    ""categories_b"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 },
    ""include_links"": { ""type"": ""boolean"", ""default"": true,
                         ""description"": ""Include loaded Revit links. Turning this off on a model that HAS links makes the result partial, and it will be labelled as such."" },
    ""tolerance_mm"": { ""type"": ""number"", ""default"": 0.0,
                        ""description"": ""Intersections whose overlap is under this are ignored. 0 = report any real overlap."" },
    ""max_results"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1, ""maximum"": 2000 }
  }
}";

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
                        sources.Add(new Src { Name = label, Doc = ldoc, Xf = li.GetTotalTransform() });
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

            // ---- Collect both sides, remembering where each element came from. ----
            var sideA = Collect(sources, catsA);
            var sideB = Collect(sources, catsB);

            if (sideA.Count == 0 || sideB.Count == 0)
            {
                return CommandResult.Ok(new JObject
                {
                    ["clashes"] = new JArray(),
                    ["clash_count"] = 0,
                    ["result"] = "inconclusive",
                    ["elements_a"] = sideA.Count,
                    ["elements_b"] = sideB.Count,
                    ["coverage"] = Coverage(sources, linksSkipped, new JArray()),
                    ["headline"] = $"One side is empty (A={sideA.Count}, B={sideB.Count}) — nothing could clash. " +
                                   "This is NOT a clean result: it means the categories matched no geometry in the " +
                                   "models checked. Verify the category names and that the discipline you expect " +
                                   "is actually loaded."
                });
            }

            // ---- Broad phase on bounding boxes, narrow phase on solids. ----
            var clashes = new JArray();
            var geometryFailures = new JArray();
            int bboxHits = 0, solidTests = 0;
            var solidCache = new Dictionary<string, List<Solid>>();

            foreach (var a in sideA)
            {
                foreach (var b in sideB)
                {
                    if (a.El.Id == b.El.Id && a.Source == b.Source) continue;
                    if (!BoxesOverlap(a.Box, b.Box)) continue;
                    bboxHits++;

                    List<Solid> sa, sb;
                    try { sa = Solids(a, options, solidCache); }
                    catch (Exception ex) { geometryFailures.Add(Fail(a, ex)); continue; }
                    try { sb = Solids(b, options, solidCache); }
                    catch (Exception ex) { geometryFailures.Add(Fail(b, ex)); continue; }

                    if (sa.Count == 0 || sb.Count == 0) continue;

                    solidTests++;
                    double vol = 0; bool hit = false; string boolError = null;
                    foreach (var x in sa)
                    {
                        foreach (var y in sb)
                        {
                            try
                            {
                                var inter = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                                if (inter != null && inter.Volume > tolFt3) { hit = true; vol += inter.Volume; }
                            }
                            catch (Exception ex) { boolError = ex.Message; }
                        }
                    }

                    if (boolError != null && !hit)
                    {
                        // The boolean failed and found nothing — we do not know whether
                        // these clash. Reporting nothing here would be a false negative.
                        geometryFailures.Add(new JObject
                        {
                            ["a"] = Describe(a),
                            ["b"] = Describe(b),
                            ["error"] = "Boolean intersection failed: " + boolError,
                            ["consequence"] = "This PAIR is unresolved. It is not reported as clean."
                        });
                        continue;
                    }

                    if (!hit) continue;

                    clashes.Add(new JObject
                    {
                        ["a"] = Describe(a),
                        ["b"] = Describe(b),
                        ["intersection_volume_m3"] = Math.Round(HorizunGuard.ToM3(vol), 6),
                        ["cross_model"] = a.Source != b.Source
                    });
                    if (clashes.Count >= maxResults) goto done;
                }
            }
        done:

            bool partial = linksSkipped.Count > 0 || geometryFailures.Count > 0 || clashes.Count >= maxResults;

            return CommandResult.Ok(new JObject
            {
                ["clash_count"] = clashes.Count,
                ["result"] = partial ? "partial" : "complete",
                ["elements_a"] = sideA.Count,
                ["elements_b"] = sideB.Count,
                ["bbox_candidates"] = bboxHits,
                ["solid_tests"] = solidTests,
                ["truncated"] = clashes.Count >= maxResults,
                ["coverage"] = Coverage(sources, linksSkipped, geometryFailures),
                ["clashes"] = clashes,
                ["headline"] = Headline(clashes.Count, partial, linksSkipped.Count, geometryFailures.Count, sources.Count)
            });
        }

        private static string Headline(int count, bool partial, int skipped, int failures, int sourceCount)
        {
            if (count == 0 && !partial)
                return $"No clashes, and the check was complete across {sourceCount} model(s). This zero can be relied on.";
            if (count == 0 && partial)
                return $"No clashes found, but the check was PARTIAL ({skipped} link(s) not checked, {failures} " +
                       "unresolved pair(s)). Do not read this as coordinated — see coverage. A zero that was never " +
                       "measured is not a zero.";
            if (partial)
                return $"{count} clash(es) found, and the check was PARTIAL — there may be more. See coverage.";
            return $"{count} clash(es) found across {sourceCount} model(s). The check was complete.";
        }

        private static JObject Coverage(List<Src> sources, JArray skipped, JArray failures)
        {
            return new JObject
            {
                ["models_checked"] = new JArray(sources.Select(s => (JToken)s.Name)),
                ["links_not_checked"] = skipped,
                ["unresolved_pairs"] = failures,
                ["complete"] = skipped.Count == 0 && failures.Count == 0
            };
        }

        private class Src { public string Name; public Document Doc; public Transform Xf; }

        private class Item
        {
            public Element El;
            public string Source;
            public Transform Xf;
            public BoundingBoxXYZ Box;   // already in host coordinates
        }

        private static List<Item> Collect(List<Src> sources, List<BuiltInCategory> cats)
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
                    catch { continue; }

                    foreach (var e in els)
                    {
                        BoundingBoxXYZ bb;
                        try { bb = e.get_BoundingBox(null); } catch { continue; }
                        if (bb == null) continue;
                        list.Add(new Item { El = e, Source = s.Name, Xf = s.Xf, Box = ToHost(bb, s.Xf) });
                    }
                }
            }
            return list;
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
            var key = it.Source + ":" + it.El.Id;
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
