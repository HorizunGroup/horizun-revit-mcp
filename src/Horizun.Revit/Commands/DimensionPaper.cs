// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// What a dimension looks like ON PAPER (backlog P4): the text the sheet prints,
// formatted with the dimension TYPE's units (Dimension.ValueString uses the
// project's), and whether it prints at all (a dimension whose reference lives in
// a hidden link or element exists, has no box in the view and never reaches the
// sheet). Reads only; the decisions are in Core/DimensionPaperRules.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    internal static class DimensionPaper
    {
        /// <summary>
        /// printed_text (single segment), printed_segments (chains), the format source
        /// and Revit's own ValueString beside it. A fact that cannot be read is null with
        /// a note, never the project-formatted string passed off as the printed one.
        /// </summary>
        public static JObject PrintedText(Document doc, Dimension d, XYZ spotPoint = null, string spotPointSource = null)
        {
            var o = new JObject();
            if (d is SpotDimension spot) return SpotText(doc, spot, spotPoint, spotPointSource);
            Func<double, string> format = null;
            string source = null;
            string notComposed = NotComposedReason(doc, d);
            if (notComposed != null)
            {
                o["printed_text"] = JValue.CreateNull();
                o["note"] = notComposed;
                try { o["value_string_project_format"] = d.ValueString; } catch { }
                return o;
            }
            try
            {
                var type = doc.GetElement(d.GetTypeId()) as DimensionType;
                bool angular = false;
                try { angular = d.DimensionShape == DimensionShape.Angular; } catch { }
                ForgeTypeId spec = angular ? SpecTypeId.Angle : SpecTypeId.Length;
                FormatOptions own = type?.GetUnitsFormatOptions();
                bool? useProject = own == null ? (bool?)null : own.UseDefault;
                source = DimensionPrintRules.FormatSource(useProject);
                Units units = doc.GetUnits();
                if (source == DimensionPrintRules.SourceDimensionType) units.SetFormatOptions(spec, own);
                format = v => UnitFormatUtils.Format(units, spec, v, false);
            }
            catch (Exception ex)
            {
                o["note"] = "the dimension type's unit format could not be read: " + ex.Message;
            }
            o["format_source"] = source;
            int count = 0;
            try { count = d.NumberOfSegments; } catch { }
            if (count > 1)
            {
                var segs = new JArray();
                try
                {
                    foreach (DimensionSegment s in d.Segments)
                    {
                        string text = null;
                        try
                        {
                            double? v = s.Value;
                            text = DimensionPrintRules.Compose(s.Prefix,
                                v.HasValue && format != null ? format(v.Value) : null, s.Suffix, s.ValueOverride);
                        }
                        catch { text = null; }
                        segs.Add(text == null ? JValue.CreateNull() : (JToken)text);
                    }
                }
                catch (Exception ex) { o["note"] = "segments unreadable: " + ex.Message; }
                o["printed_text"] = JValue.CreateNull();
                o["printed_segments"] = segs;
            }
            else
            {
                string text = null;
                try
                {
                    double? v = d.Value;
                    text = DimensionPrintRules.Compose(d.Prefix, v.HasValue && format != null ? format(v.Value) : null,
                                                       d.Suffix, d.ValueOverride);
                }
                catch { text = null; }
                o["printed_text"] = text;
                try { o["value_string_project_format"] = d.ValueString; } catch { }
            }
            return o;
        }

        /// <summary>
        /// What this composition does not reproduce: the R / diameter symbol of radial and
        /// diameter types, the EQ text of equalised segments, and alternate units. Such a
        /// dimension gets printed_text null with the reason, never a partial text passed
        /// off as the printed one.
        /// </summary>
        private static string NotComposedReason(Document doc, Dimension d)
        {
            try
            {
                DimensionShape shape = d.DimensionShape;
                if (shape == DimensionShape.Radial || shape == DimensionShape.Diameter)
                    return "prefix/alternate units not composed: a " + shape.ToString().ToLowerInvariant() +
                           " dimension prints its type's radius/diameter symbol, which is not reproduced here";
            }
            catch (Exception ex) { return "prefix/alternate units not composed: shape unreadable (" + ex.Message + ")"; }
            try
            {
                if (d.NumberOfSegments > 1 && d.AreSegmentsEqual)
                    return "prefix/alternate units not composed: the segments are equalised and may print the EQ text";
            }
            catch { }
            try
            {
                var type = doc.GetElement(d.GetTypeId()) as DimensionType;
                Parameter alt = type?.get_Parameter(BuiltInParameter.ALTERNATE_UNITS);
                if (alt != null && alt.StorageType == StorageType.Integer && alt.AsInteger() != 0)
                    return "prefix/alternate units not composed: the type prints alternate units beside the value";
            }
            catch (Exception ex) { return "prefix/alternate units not composed: alternate units unreadable (" + ex.Message + ")"; }
            return null;
        }

        /// <summary>
        /// The dimension's OWN visibility in its view: outside the crop, hidden itself,
        /// its category hidden in the view or by the view's template, view filters (not
        /// evaluated: unknown) and, in a workshared model, its workset. Decisions in
        /// DimensionPrintRules.DimensionFacts.
        /// </summary>
        public static void DimensionVisibility(Document doc, View view, Dimension d, IEnumerable<XYZ> extraPoints,
                                               List<string> hidden, List<string> unknown)
        {
            bool? insideCrop = null, dimHidden = null, catHidden = null, templateHides = null, worksetVisible = null;
            int? filters = null;
            bool workshared = false;
            try
            {
                if (!view.CropBoxActive) insideCrop = true;
                else
                {
                    var pts = new List<XYZ>();
                    if (extraPoints != null) pts.AddRange(extraPoints.Where(x => x != null));
                    try
                    {
                        if (d.NumberOfSegments > 1) { foreach (DimensionSegment s in d.Segments) pts.Add(s.Origin); }
                        else pts.Add(d.Origin);
                    }
                    catch { }
                    JObject detail = null;
                    insideCrop = pts.Count == 0 ? null : DimensionGeometryProbe.InsideCrop(view, pts, out detail);
                    if (detail != null && detail["unreadable"] != null) insideCrop = null;
                }
            }
            catch { insideCrop = null; }
            try { dimHidden = d.IsHidden(view); } catch { }
            ElementId catId = null;
            try { catId = d.Category?.Id; } catch { }
            if (catId != null)
            {
                try { catHidden = view.GetCategoryHidden(catId); } catch { }
                try
                {
                    ElementId tid = view.ViewTemplateId;
                    var template = tid == null || tid == ElementId.InvalidElementId ? null : doc.GetElement(tid) as View;
                    bool? controls = null, tHidden = null;
                    if (template != null)
                    {
                        try
                        {
                            var annotation = new ElementId(BuiltInParameter.VIS_GRAPHICS_ANNOTATION);
                            controls = !template.GetNonControlledTemplateParameterIds().Contains(annotation);
                        }
                        catch { controls = null; }
                        if (controls == true) tHidden = template.GetCategoryHidden(catId);
                    }
                    templateHides = DimensionPrintRules.TemplateHidesCategory(template != null, controls, tHidden);
                }
                catch { }
            }
            try
            {
                int n = 0;
                foreach (ElementId f in view.GetFilters()) if (view.GetIsFilterEnabled(f)) n++;
                filters = n;
            }
            catch { }
            try
            {
                workshared = doc.IsWorkshared;
                if (workshared) worksetVisible = view.IsWorksetVisible(d.WorksetId);
            }
            catch { workshared = true; worksetVisible = null; }
            DimensionPrintRules.DimensionFacts(insideCrop, dimHidden, catHidden, templateHides, filters, workshared,
                                               worksetVisible, hidden, unknown);
        }

        /// <summary>
        /// A spot has no Value / ValueString (null, measured live on 2025). Its text is
        /// the measured point read from the type's base, formatted with the type's units:
        /// elevation = Z, coordinate = N (Y) / E (X). Every base is reported; the printed
        /// one is chosen from the type's base NAME (Core SpotBaseRules) and is null when
        /// the name is not recognised, never a guess. The point comes from the caller
        /// (the face point the plan projected) or, failing that, from Dimension.Origin,
        /// and says which.
        /// </summary>
        private static JObject SpotText(Document doc, SpotDimension d, XYZ point, string pointSource)
        {
            var o = new JObject();
            bool coordinate = false;
            try { coordinate = d.Category != null && Rid.Value(d.Category.Id) == (long)BuiltInCategory.OST_SpotCoordinates; }
            catch { }
            o["spot_kind"] = coordinate ? "coordinate" : "elevation";
            if (point == null)
            {
                try { point = d.Origin; pointSource = "dimension_origin"; } catch { point = null; }
            }
            if (point == null)
            {
                o["printed_text"] = JValue.CreateNull();
                o["note"] = "the spot's point could not be read";
                return o;
            }
            o["point_source"] = pointSource ?? "caller";
            o["point_internal_feet"] = new JArray(point.X, point.Y, point.Z);

            Units units = doc.GetUnits();
            string source;
            var type = doc.GetElement(d.GetTypeId()) as DimensionType;
            try
            {
                FormatOptions own = type?.GetUnitsFormatOptions();
                source = DimensionPrintRules.FormatSource(own == null ? (bool?)null : own.UseDefault);
                if (source == DimensionPrintRules.SourceDimensionType) units.SetFormatOptions(SpecTypeId.Length, own);
            }
            catch (Exception ex)
            {
                o["format_note"] = "type units unreadable, project units used: " + ex.Message;
                source = DimensionPrintRules.SourceProject;
                units = doc.GetUnits();
            }
            o["format_source"] = source;
            Func<double, string> fmt = v => UnitFormatUtils.Format(units, SpecTypeId.Length, v, false);

            XYZ pbp = null, shared = null;
            try { pbp = BasePoint.GetProjectBasePoint(doc)?.Position; } catch { }
            try { shared = doc.ActiveProjectLocation?.GetTotalTransform()?.Inverse.OfPoint(point); } catch { }
            Func<XYZ, JObject> row = q => q == null ? null : new JObject
            {
                ["north_internal_feet"] = q.Y, ["east_internal_feet"] = q.X, ["elevation_internal_feet"] = q.Z,
                ["north"] = fmt(q.Y), ["east"] = fmt(q.X), ["elevation"] = fmt(q.Z)
            };
            var bases = new JObject { [SpotBaseRules.InternalOrigin] = row(point) };
            if (pbp != null) bases[SpotBaseRules.ProjectBasePoint] = row(point.Subtract(pbp));
            if (shared != null) bases[SpotBaseRules.SurveyPoint] = row(shared);

            BuiltInParameter baseParam = coordinate ? BuiltInParameter.SPOT_COORDINATE_BASE : BuiltInParameter.SPOT_ELEV_BASE;
            string baseName = null;
            try { baseName = type?.get_Parameter(baseParam)?.AsValueString(); } catch { }
            string kind = SpotBaseRules.Classify(baseName);
            if (kind == SpotBaseRules.Relative && !coordinate)
            {
                try
                {
                    ElementId levelId = d.get_Parameter(BuiltInParameter.SPOT_ELEV_RELATIVE_BASE)?.AsElementId();
                    var level = levelId == null ? null : doc.GetElement(levelId) as Level;
                    if (level != null)
                        bases[SpotBaseRules.Relative] = row(new XYZ(point.X, point.Y, point.Z - level.ProjectElevation));
                }
                catch { }
            }
            o["base_name"] = baseName;
            o["base"] = kind;
            o["by_base"] = bases;

            JObject chosen = kind == null ? null : bases[kind] as JObject;
            Func<BuiltInParameter, string> text = bip =>
            {
                try { return d.get_Parameter(bip)?.AsString() ?? type?.get_Parameter(bip)?.AsString(); }
                catch { return null; }
            };
            if (chosen == null)
            {
                o["printed_text"] = JValue.CreateNull();
                o["note"] = baseName == null
                    ? "the type's base could not be read; pick the line from by_base"
                    : "base '" + baseName + "' not recognised; pick the line from by_base";
                return o;
            }
            if (!coordinate)
            {
                o["printed_text"] = DimensionPrintRules.Compose(text(BuiltInParameter.SPOT_ELEV_SINGLE_OR_UPPER_PREFIX),
                    (string)chosen["elevation"], text(BuiltInParameter.SPOT_ELEV_SINGLE_OR_UPPER_SUFFIX), null);
                o["indicator"] = text(BuiltInParameter.SPOT_ELEV_IND_ELEVATION);
            }
            else
            {
                o["printed_text"] = JValue.CreateNull();
                o["printed_lines"] = new JObject
                {
                    ["north"] = (string)chosen["north"], ["east"] = (string)chosen["east"],
                    ["north_indicator"] = text(BuiltInParameter.SPOT_ELEV_IND_NS),
                    ["east_indicator"] = text(BuiltInParameter.SPOT_ELEV_IND_EW)
                };
                o["note"] = "which line prints on top, and the per-line prefix/suffix, follow the type's Top/Bottom " +
                            "Value settings and are not composed here";
            }
            return o;
        }

        /// <summary>
        /// Why each reference would not show in the view, and what could not be read.
        /// Link per-category display overrides and view filters are not evaluated; the
        /// committed dimension's own box (post-commit) is what catches those.
        /// </summary>
        public static void ReferenceVisibility(Document doc, View view, IEnumerable<Reference> refs,
                                               List<string> hidden, List<string> unknown)
        {
            if (refs == null) return;
            int i = 0;
            foreach (Reference r in refs)
            {
                string at = "references[" + i++ + "]";
                if (r == null) continue;
                try
                {
                    Element owner = doc.GetElement(r.ElementId);
                    if (owner is RevitLinkInstance link)
                    {
                        if (link.IsHidden(view))
                            hidden.Add(DimensionPrintRules.ReasonLinkHidden + ": " + at + " is in link instance " +
                                       Rid.Value(link.Id) + ", hidden in this view");
                        if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_RvtLinks)))
                            hidden.Add(DimensionPrintRules.ReasonLinkCategoryHidden + ": " + at +
                                       " is in a link and the RVT Links category is hidden in this view");
                        Document linked = link.GetLinkDocument();
                        Element inner = linked?.GetElement(r.LinkedElementId);
                        Category cat = inner?.Category;
                        if (cat != null && Rid.Value(cat.Id) < 0 && view.GetCategoryHidden(cat.Id))
                            hidden.Add(DimensionPrintRules.ReasonCategoryHidden + ": " + at + " (" + cat.Name +
                                       ") is a category hidden in this view");
                        continue;
                    }
                    if (owner == null) { unknown.Add(at + ": owner element unreadable"); continue; }
                    if (owner.IsHidden(view))
                        hidden.Add(DimensionPrintRules.ReasonElementHidden + ": " + at + " (element " +
                                   Rid.Value(owner.Id) + ") is hidden in this view");
                    Category c = owner.Category;
                    if (c != null && view.GetCategoryHidden(c.Id))
                        hidden.Add(DimensionPrintRules.ReasonCategoryHidden + ": " + at + " (" + c.Name +
                                   ") is a category hidden in this view");
                }
                catch (Exception ex) { unknown.Add(at + ": visibility unreadable (" + ex.Message + ")"); }
            }
        }

        public static JObject VerdictJson(DimensionPrintRules.Verdict v)
        {
            return new JObject
            {
                ["will_print"] = v.WillPrint.HasValue ? (JToken)v.WillPrint.Value : JValue.CreateNull(),
                ["reasons"] = new JArray(v.Reasons),
                ["unknown"] = new JArray(v.Unknown)
            };
        }

        /// <summary>The full verdict for a committed dimension in its own view.</summary>
        public static DimensionPrintRules.Verdict ForCommitted(Document doc, View view, Dimension d)
        {
            var hidden = new List<string>();
            var unknown = new List<string>();
            bool? hasBox = null;
            try { hasBox = d.get_BoundingBox(view) != null; } catch (Exception ex) { unknown.Add("box: " + ex.Message); }
            var refs = new List<Reference>();
            try { foreach (Reference r in d.References) refs.Add(r); }
            catch (Exception ex) { unknown.Add("references: " + ex.Message); }
            ReferenceVisibility(doc, view, refs, hidden, unknown);
            DimensionVisibility(doc, view, d, null, hidden, unknown);
            return DimensionPrintRules.Evaluate(hasBox, hidden, unknown);
        }

        // ---- surface-pattern joints ------------------------------------------------

        /// <summary>
        /// The extent of the face behind a pattern reference along a unit direction, in
        /// host coordinates. Null for linked faces and family-instance faces, whose
        /// frame is not read here (the check then reports "unavailable", never a pass).
        /// </summary>
        public static bool TryFaceExtent(Document doc, string faceStable, XYZ dir, out double min, out double max,
                                         out string why)
        {
            min = double.MaxValue; max = double.MinValue; why = null;
            try
            {
                Reference fr = Reference.ParseFromStableRepresentation(doc, faceStable);
                if (fr.LinkedElementId != null && fr.LinkedElementId != ElementId.InvalidElementId)
                { why = "the patterned face is in a link; its extent is not measured"; return false; }
                Element owner = doc.GetElement(fr.ElementId);
                if (owner == null || owner is FamilyInstance)
                { why = "the patterned face's frame is not read for this owner"; return false; }
                var face = owner.GetGeometryObjectFromReference(fr) as Face;
                if (face == null) { why = "the patterned face could not be read"; return false; }
                foreach (EdgeArray loop in face.EdgeLoops)
                    foreach (Edge e in loop)
                        foreach (XYZ q in e.Tessellate())
                        {
                            double s = q.DotProduct(dir);
                            if (s < min) min = s;
                            if (s > max) max = s;
                        }
                if (min > max) { why = "the patterned face has no readable boundary"; return false; }
                return true;
            }
            catch (Exception ex) { why = "face extent unreadable: " + ex.Message; return false; }
        }
    }
}
