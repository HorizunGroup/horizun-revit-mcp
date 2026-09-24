// -----------------------------------------------------------------------------
// Horizun Revit MCP - small readers shared by the code-check, schedule-link and
// federation commands. Original Horizun code.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CodeCheckCommand
    {
        internal static string CategoryToken(Category c)
        {
            if (c == null) return null;
            try
            {
                long v = Rid.Value(c.Id);
                if (v < 0 && v >= int.MinValue && Enum.IsDefined(typeof(BuiltInCategory), (int)v))
                    return Enum.GetName(typeof(BuiltInCategory), (int)v);
            }
            catch { }
            return null;
        }

        internal static string SafeCatName(Category c) { try { return c?.Name; } catch { return null; } }
        internal static string SafeName(Element e) { try { return e.Name; } catch { return null; } }

        internal static string LevelName(Document doc, Element e)
        {
            try { return doc.GetElement(e.LevelId)?.Name; } catch { return null; }
        }

        internal static string Text(Element e, BuiltInParameter bip)
        {
            try { Parameter p = e.get_Parameter(bip); return p == null || !p.HasValue ? null : p.AsString(); }
            catch { return null; }
        }

        private static double? LengthMm(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                double v = p.AsDouble();
                return Math.Abs(v) < 1e-9 ? (double?)null : v * FeetToMm;
            }
            catch { return null; }
        }

        private static double? TypeLengthMm(Document doc, Element e, BuiltInParameter bip)
        {
            try { Element t = doc.GetElement(e.GetTypeId()); return t == null ? null : LengthMm(t, bip); }
            catch { return null; }
        }

        private static ForgeTypeId UnitOf(string unit)
        {
            switch (unit)
            {
                case "mm": return UnitTypeId.Millimeters;
                case "m": return UnitTypeId.Meters;
                case "m2": return UnitTypeId.SquareMeters;
                case "lx": return UnitTypeId.Lux;
                default: return null;
            }
        }

        /// <summary>
        /// A parameter by display name, or by BuiltInParameter token (ALL_MODEL_MARK) - the token is the
        /// language-independent spelling: a Spanish Revit calls Mark "Marca".
        /// </summary>
        internal static Parameter Lookup(Element e, string name)
        {
            try
            {
                if (name.IndexOf('_') > 0 && name == name.ToUpperInvariant() &&
                    Enum.TryParse(name, false, out BuiltInParameter bip))
                    return e.get_Parameter(bip);
                return e.LookupParameter(name);
            }
            catch { return null; }
        }

        /// <summary>A parameter by name on the instance, then on its type. Unreadable is reported, never a blank.</summary>
        internal static ParamFact ReadParam(Document doc, Element e, string name, string unit)
        {
            Parameter p = Lookup(e, name);
            if (p == null)
            {
                try { Element t = doc.GetElement(e.GetTypeId()); p = t == null ? null : Lookup(t, name); } catch { }
            }
            if (p == null) return new ParamFact { Exists = false };
            var f = new ParamFact { Exists = true };
            try
            {
                if (!p.HasValue) return f;
                switch (p.StorageType)
                {
                    case StorageType.String:
                        f.Text = p.AsString();
                        if (double.TryParse(f.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) f.Number = parsed;
                        break;
                    case StorageType.Integer:
                        f.Text = p.AsInteger().ToString(CultureInfo.InvariantCulture);
                        f.Number = p.AsInteger();
                        break;
                    case StorageType.Double:
                        double raw = p.AsDouble();
                        // The raw stored value, as docs/requirement-set.md documents for an
                        // assertion without a unit; converted when the rule names one.
                        f.Text = raw.ToString("R", CultureInfo.InvariantCulture);
                        if (unit == null) f.Number = raw;
                        else
                        {
                            // Only when the parameter's own spec accepts that unit: a length
                            // "converted" to lux would be a number that means nothing.
                            try
                            {
                                ForgeTypeId u = UnitOf(unit);
                                f.Number = UnitUtils.IsValidUnit(p.Definition.GetDataType(), u)
                                    ? UnitUtils.ConvertFromInternalUnits(raw, u) : (double?)null;
                            }
                            catch { f.Number = null; }
                        }
                        break;
                    case StorageType.ElementId:
                        f.Text = p.AsValueString();
                        break;
                }
            }
            catch { f.Unreadable = true; }
            return f;
        }
    }
}
