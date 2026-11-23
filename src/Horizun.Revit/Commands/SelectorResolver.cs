// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of the element selector (rules in Core/ElementSelectorRules): walks the
// document once, reads each candidate's facts and keeps the ids the selector matches.
// Used by horizun_delete_verified mode=query and horizun_measure_geometry. Elements whose
// facts cannot be read are counted and named, never skipped quietly.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class SelectorResolution
    {
        public List<long> Matched = new List<long>();
        public List<long> Unknown = new List<long>();
        public int Undatable;
        public int Examined;
        public bool Truncated;
    }

    internal static class SelectorResolver
    {
        public static SelectorResolution Resolve(Document doc, SelectorSpec spec, int cap)
        {
            var res = new SelectorResolution();
            FilteredElementCollector collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            BuiltInCategory bic;
            if (spec.Category != null && TryBuiltIn(doc, spec.Category, out bic))
                collector = collector.OfCategory(bic);
            var typeCache = new Dictionary<long, Element>();
            foreach (Element e in collector)
            {
                res.Examined++;
                SelectorFacts facts;
                try { facts = Facts(doc, e, typeCache); }
                catch { res.Unknown.Add(Rid.Value(e.Id)); continue; }
                SelectorVerdict v;
                try { v = ElementSelectorRules.Evaluate(spec, facts); }
                catch { v = SelectorVerdict.Unknown; }
                switch (v)
                {
                    case SelectorVerdict.Match:
                        if (res.Matched.Count >= cap) { res.Truncated = true; break; }
                        res.Matched.Add(Rid.Value(e.Id));
                        break;
                    case SelectorVerdict.Unknown: res.Unknown.Add(Rid.Value(e.Id)); break;
                    case SelectorVerdict.Undatable: res.Undatable++; break;
                }
            }
            res.Matched.Sort();
            return res;
        }

        private static bool TryBuiltIn(Document doc, string text, out BuiltInCategory bic)
        {
            if (Enum.TryParse(text, true, out bic) && Enum.IsDefined(typeof(BuiltInCategory), bic)) return true;
            try
            {
                foreach (Category c in doc.Settings.Categories)
                    if (string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase))
                    {
                        bic = c.BuiltInCategory;
                        return Enum.IsDefined(typeof(BuiltInCategory), bic);
                    }
            }
            catch { }
            bic = BuiltInCategory.INVALID;
            return false;
        }

        internal static SelectorFacts Facts(Document doc, Element e, Dictionary<long, Element> typeCache)
        {
            Element type = null;
            try
            {
                ElementId tid = e.GetTypeId();
                if (tid != null && tid != ElementId.InvalidElementId)
                {
                    long key = Rid.Value(tid);
                    if (!typeCache.TryGetValue(key, out type)) { type = doc.GetElement(tid); typeCache[key] = type; }
                }
            }
            catch { }
            string categoryName = null, token = null;
            try
            {
                categoryName = e.Category?.Name;
                if (e.Category != null) token = e.Category.BuiltInCategory.ToString();
            }
            catch { }
            DateTime? created = null;
            bool marked = false;
            Newtonsoft.Json.Linq.JObject mark = HrzOwnership.Read(e);
            if (mark != null)
            {
                marked = true;
                if (ElementSelectorRules.TryParseUtc((string)mark["utc"] ?? "", out DateTime utc)) created = utc;
            }
            return new SelectorFacts
            {
                CategoryName = categoryName,
                CategoryToken = token,
                Family = (type as ElementType)?.FamilyName,
                TypeName = type?.Name,
                Name = e.Name,
                HasMark = marked,
                CreatedUtc = created,
                Parameter = name => ReadParameter(e, type, name)
            };
        }

        private static SelectorParam ReadParameter(Element e, Element type, string name)
        {
            Parameter p = null;
            try { p = e.LookupParameter(name); } catch { return SelectorParam.Unreadable; }
            if (p == null && type != null)
            {
                try { p = type.LookupParameter(name); } catch { return SelectorParam.Unreadable; }
            }
            if (p == null) return SelectorParam.Missing;
            try
            {
                if (!p.HasValue) return SelectorParam.Of("");
                switch (p.StorageType)
                {
                    case StorageType.String: return SelectorParam.Of(p.AsString() ?? "");
                    case StorageType.Integer: return SelectorParam.Of(p.AsInteger().ToString(CultureInfo.InvariantCulture));
                    case StorageType.Double:
                        return SelectorParam.Of(p.AsValueString() ?? p.AsDouble().ToString("R", CultureInfo.InvariantCulture));
                    case StorageType.ElementId:
                        return SelectorParam.Of(p.AsValueString() ?? Rid.Value(p.AsElementId()).ToString(CultureInfo.InvariantCulture));
                    default: return SelectorParam.Unreadable;
                }
            }
            catch { return SelectorParam.Unreadable; }
        }
    }
}
