// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
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

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunQuantitiesHandler : IRevitCommand
    {
        public string Name => "horizun_quantities";

        public string Description =>
            "Volume takeoff in m3 from all three sources Revit offers — the Volume parameter, the real solid " +
            "geometry, and the material takeoff — reported side by side with the disagreement measured. " +
            "Handlers that report a single volume are picking one silently; we have measured a 75% gap " +
            "between the parameter and the geometry on the same beam. Pass element_ids or a category. " +
            "Read-only.";

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

            // ---- Resolve the element set. ----
            var elements = new List<Element>();
            var failed = new JArray();

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
                    if (!RevitCompat.CanRepresentElementId(id))
                    {
                        failed.Add(new JObject { ["element_id"] = id, ["error"] = RevitCompat.ElementIdRangeError(id) });
                        continue;
                    }
                    var e = doc.GetElement(RevitCompat.ToElementId(id));
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
            double totParam = 0, totGeom = 0, totMat = 0;
            int measured = 0, disagreeing = 0, noGeometry = 0;

            foreach (var e in elements)
            {
                double? vParam = ReadParamVolumeFt3(e);
                double? vGeom = ReadGeometryVolumeFt3(e, options, out string geomNote);
                double? vMat = ReadMaterialVolumeFt3(e);

                if (vGeom == null && vParam == null && vMat == null)
                {
                    // Not an error: many elements (lines, tags, annotations) have no
                    // volume at all. Silently counting them as 0.0 would dilute the
                    // average and hide the ones that DO have geometry and read zero.
                    noGeometry++;
                    continue;
                }
                measured++;

                double pM3 = HorizunGuard.ToM3(vParam ?? 0), gM3 = HorizunGuard.ToM3(vGeom ?? 0), mM3 = HorizunGuard.ToM3(vMat ?? 0);
                totParam += pM3; totGeom += gM3; totMat += mM3;

                // The comparison that matters: the cheap cached number vs. what is
                // actually modelled. Only meaningful when both exist.
                JObject rec = null;
                bool agree = true;
                if (vParam.HasValue && vGeom.HasValue)
                {
                    rec = JObject.FromObject(HorizunGuard.Reconcile(
                        "volume", "Volume parameter", pM3, "solid geometry (" + detail + ")", gM3, "m3", tolPct / 100.0));
                    agree = (bool)rec["agree"];
                }
                if (!agree) disagreeing++;
                if (onlyBad && agree) continue;

                rows.Add(new JObject
                {
                    ["element_id"] = e.Id.ToString(),
                    ["name"] = SafeName(e),
                    ["category"] = SafeCategory(e),
                    ["type"] = SafeTypeName(doc, e),
                    ["volume_parameter_m3"] = vParam.HasValue ? (JToken)Math.Round(pM3, 4) : null,
                    ["volume_geometry_m3"] = vGeom.HasValue ? (JToken)Math.Round(gM3, 4) : null,
                    ["volume_material_takeoff_m3"] = vMat.HasValue ? (JToken)Math.Round(mM3, 4) : null,
                    ["reconciliation"] = rec,
                    ["note"] = geomNote
                });
            }

            var totalRec = JObject.FromObject(HorizunGuard.Reconcile(
                "total volume", "sum of Volume parameters", totParam, "sum of solid geometry", totGeom, "m3", tolPct / 100.0));

            return CommandResult.Ok(new JObject
            {
                ["detail_level"] = detail.ToString(),
                ["elements_requested"] = elements.Count,
                ["elements_measured"] = measured,
                ["elements_without_volume"] = noGeometry,
                ["elements_disagreeing"] = disagreeing,
                ["totals_m3"] = new JObject
                {
                    ["volume_parameter"] = Math.Round(totParam, 4),
                    ["volume_geometry"] = Math.Round(totGeom, 4),
                    ["volume_material_takeoff"] = Math.Round(totMat, 4)
                },
                ["total_reconciliation"] = totalRec,
                ["elements"] = rows,
                ["failed"] = failed,
                ["headline"] = disagreeing == 0
                    ? $"All {measured} measured element(s) agree within {tolPct}% between the Volume parameter and the real geometry."
                    : $"{disagreeing} of {measured} element(s) DISAGREE by more than {tolPct}% between the Volume " +
                      "parameter and their real geometry. No single number is reported here on purpose: which " +
                      "source is correct depends on your measurement criteria, and whoever signs the takeoff " +
                      "is the one who gets to choose.",
                ["shown"] = rows.Count,
                ["truncated"] = false
            });
        }

        /// <summary>The cached parameter. Cheap, and the one everyone reads.</summary>
        private static double? ReadParamVolumeFt3(Element e)
        {
            try
            {
                var p = e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                if (p == null || !p.HasValue) p = e.LookupParameter("Volume");
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                var v = p.AsDouble();
                return v > 1e-9 ? v : (double?)null;
            }
            catch { return null; }
        }

        /// <summary>What is actually modelled, at this detail level.</summary>
        private static double? ReadGeometryVolumeFt3(Element e, Options opt, out string note)
        {
            note = null;
            try
            {
                var geom = e.get_Geometry(opt);
                if (geom == null) return null;
                int solids = 0;
                double v = SumSolids(geom, ref solids);
                if (solids == 0) return null;
                if (v <= 1e-9)
                {
                    note = "Geometry exists but its solids have no volume — usually a void-only or purely " +
                           "surface element. Not counted as zero volume; excluded from the totals.";
                    return null;
                }
                return v;
            }
            catch (Exception ex)
            {
                // NOT swallowed. An element whose geometry we could not read is an
                // element missing from a takeoff, and the caller must know which.
                note = "Geometry could not be read (" + ex.Message + "). This element is NOT in the geometry total.";
                return null;
            }
        }

        /// <summary>What the material schedule will say — the third opinion.</summary>
        private static double? ReadMaterialVolumeFt3(Element e)
        {
            try
            {
                var mats = e.GetMaterialIds(false);
                if (mats == null || mats.Count == 0) return null;
                double v = 0; bool any = false;
                foreach (var m in mats)
                {
                    try { v += e.GetMaterialVolume(m); any = true; } catch { }
                }
                if (!any || v <= 1e-9) return null;
                return v;
            }
            catch { return null; }
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

        private static string SafeName(Element e) { try { return e?.Name; } catch { return null; } }
        private static string SafeCategory(Element e) { try { return e?.Category?.Name; } catch { return null; } }
        private static string SafeTypeName(Document d, Element e)
        {
            try { var t = d.GetElement(e.GetTypeId()); return t?.Name; } catch { return null; }
        }
    }
}
