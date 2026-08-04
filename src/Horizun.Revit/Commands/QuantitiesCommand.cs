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
            "A real zero is a measurement, not an absence. Pass element_ids or a category. Read-only.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
                       ""description"": ""Elements to measure. Omit and pass 'category' instead to sweep a whole category."" },
    ""category"": { ""type"": ""string"",
                    ""description"": ""BuiltInCategory name, e.g. OST_StructuralFraming, OST_Walls, OST_Floors. Used when element_ids is omitted."" },
    ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""], ""default"": ""Fine"",
                        ""description"": ""Geometry detail level. Fine is the default here on purpose: Coarse geometry under-reports, and this number gets billed."" },
    ""tolerance_pct"": { ""type"": ""number"", ""default"": 1.0,
                         ""description"": ""Relative disagreement above which sources are flagged as not agreeing."" },
    ""top"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1,
                ""description"": ""Max element rows returned. Totals and coverage are EXACT and independent of this; a shortened list sets truncated=true and rows_matching says how many there were."" },
    ""code_parameter"": { ""type"": ""string"",
                    ""description"": ""Name of the parameter that carries each element's budget/classification code (instance first, then type). Supplied per call - no organisation's parameter is compiled in. Adds 'code' to every row and a by_code rollup whose sums state how many elements they actually cover."" },
    ""only_disagreements"": { ""type"": ""boolean"", ""default"": false,
                              ""description"": ""List only the elements whose sources disagree. Totals still cover everything."" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var detail = ParseDetail(request.Value<string>("detail_level"));
            double tolPct = request["tolerance_pct"] != null ? request.Value<double>("tolerance_pct") : 1.0;
            bool onlyBad = request.Value<bool>("only_disagreements");
            int top = request["top"] != null ? Math.Max(1, request.Value<int>("top")) : 200;

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
                return CommandResult.Fail("No elements matched. Nothing to measure — reporting a total of zero here would read as 'this is empty' rather than 'you asked for nothing'.");

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
