// -----------------------------------------------------------------------------
// Horizun Revit MCP - building area, path and fabric systems, and reading them back
// (acero 3.0, L4b). Original Horizun code.
//
// CREATE sets every parameter the rule declares and nothing it does not: the
// declared layers active with their bar type and spacing, the others inactive;
// the primary bars of a path; the cover offset of a fabric area when one is
// declared. A parameter Revit will not take throws, and the whole batch rolls back.
//
// VERIFY reads every one of them back from the model after the commit, plus the
// one fact that says the system is not an empty sketch: that it produced at least
// one RebarInSystem (or one fabric sheet). Verified is true only when every check
// is. The bars Revit lays from these numbers are NOT measured - that is named in
// the audit's not_checked list, not implied by silence.
//
// Names measured by reflection on RevitAPI.dll 2023, 2024 and 2027 and read from
// RevitAPI.xml 2027: REBAR_SYSTEM_*_TOP_* / *_BOTTOM_* are a floor's layers,
// *_FRONT_* is "Exterior" and *_BACK_* is "Interior" on a wall, DIR_1 is "Major",
// DIR_2 is "Minor"; AreaReinforcementLayerType pairs them the same way
// (TopOrFrontMajor ... BottomOrBackMinor).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public static class SystemReinforcementWriter
    {
        public const double FtToMm = 304.8;
        public const double MmToFt = 1.0 / 304.8;

        // Revit's order: top/front major, top/front minor, bottom/back major, bottom/back minor.
        private static readonly AreaReinforcementLayerType[] LayerTypes =
        {
            AreaReinforcementLayerType.TopOrFrontMajor, AreaReinforcementLayerType.TopOrFrontMinor,
            AreaReinforcementLayerType.BottomOrBackMajor, AreaReinforcementLayerType.BottomOrBackMinor
        };

        private static readonly BuiltInParameter[] SlabSpacing =
        {
            BuiltInParameter.REBAR_SYSTEM_SPACING_TOP_DIR_1, BuiltInParameter.REBAR_SYSTEM_SPACING_TOP_DIR_2,
            BuiltInParameter.REBAR_SYSTEM_SPACING_BOTTOM_DIR_1, BuiltInParameter.REBAR_SYSTEM_SPACING_BOTTOM_DIR_2
        };
        private static readonly BuiltInParameter[] WallSpacing =
        {
            BuiltInParameter.REBAR_SYSTEM_SPACING_FRONT_DIR_1, BuiltInParameter.REBAR_SYSTEM_SPACING_FRONT_DIR_2,
            BuiltInParameter.REBAR_SYSTEM_SPACING_BACK_DIR_1, BuiltInParameter.REBAR_SYSTEM_SPACING_BACK_DIR_2
        };
        private static readonly BuiltInParameter[] SlabBarType =
        {
            BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_TOP_DIR_1, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_TOP_DIR_2,
            BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BOTTOM_DIR_1, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BOTTOM_DIR_2
        };
        private static readonly BuiltInParameter[] WallBarType =
        {
            BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_FRONT_DIR_1, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_FRONT_DIR_2,
            BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BACK_DIR_1, BuiltInParameter.REBAR_SYSTEM_BAR_TYPE_BACK_DIR_2
        };
        private static readonly BuiltInParameter[] SlabHook =
        {
            BuiltInParameter.REBAR_SYSTEM_HOOK_TYPE_TOP_DIR_1, BuiltInParameter.REBAR_SYSTEM_HOOK_TYPE_TOP_DIR_2,
            BuiltInParameter.REBAR_SYSTEM_HOOK_TYPE_BOTTOM_DIR_1, BuiltInParameter.REBAR_SYSTEM_HOOK_TYPE_BOTTOM_DIR_2
        };
        private static readonly BuiltInParameter[] WallHook =
        {
            BuiltInParameter.REBAR_SYSTEM_HOOK_TYPE_FRONT_DIR_1, BuiltInParameter.REBAR_SYSTEM_HOOK_TYPE_FRONT_DIR_2,
            BuiltInParameter.REBAR_SYSTEM_HOOK_TYPE_BACK_DIR_1, BuiltInParameter.REBAR_SYSTEM_HOOK_TYPE_BACK_DIR_2
        };

        // -------------------------------------------------------------- create

        /// <summary>Build the system a resolved row declares. Inside an open transaction; throws on any refusal.</summary>
        public static Element Create(Document doc, ResolvedSystemRow r)
        {
            Element made;
            if (r.Area != null) made = CreateArea(doc, r);
            else if (r.Path != null) made = CreatePath(doc, r);
            else made = CreateFabric(doc, r);
            if (made == null)
                throw new InvalidOperationException(
                    "Revit returned no element for " + r.Kind + " rule '" + r.RuleId + "' on host " +
                    Rid.Value(r.Host.Id) + ", without raising. Nothing in this batch is kept.");
            if (!string.IsNullOrWhiteSpace(r.Mark))
                SetText(made, BuiltInParameter.ALL_MODEL_MARK, r.Mark, r, "mark");
            return made;
        }

        private static AreaReinforcement CreateArea(Document doc, ResolvedSystemRow r)
        {
            StructuralAreaRule a = r.Area;
            List<Curve> loop = Lines(a.BoundaryMm, closed: true);
            // The bar type passed here is replaced layer by layer below; it is the first
            // DECLARED layer's, never a type the set did not name. No hook: this schema
            // declares none (InvalidElementId is Revit's "no hooks").
            ElementId firstBar = r.BarTypes[a.Layers[0].Name].Id;
            AreaReinforcement ar = AreaReinforcement.Create(doc, r.Host, loop, Xyz(a.MajorDirectionMm, 1.0),
                                                            r.SystemType.Id, firstBar, ElementId.InvalidElementId);
            if (ar == null) return null;

            string[] slots = SystemReinforcementRules.LayersOf(a.LayerFamily);
            bool wall = a.LayerFamily == SystemReinforcementRules.FamilyWall;
            // ACTIVATE FIRST, THEN DEACTIVATE: switching an undeclared layer off before a
            // declared one is on could ask Revit for a system with no layer at all.
            for (int i = 0; i < 4; i++)
            {
                StructuralSystemLayer l = a.Layers.FirstOrDefault(x => x.Name == slots[i]);
                if (l == null) continue;
                ar.SetLayerActive(true, LayerTypes[i]);
                SetId(ar, (wall ? WallBarType : SlabBarType)[i], r.BarTypes[l.Name].Id, r, l.Name + ".bar_type");
                SetDouble(ar, (wall ? WallSpacing : SlabSpacing)[i], l.SpacingMm * MmToFt, r, l.Name + ".spacing_mm");
            }
            for (int i = 0; i < 4; i++)
                if (!a.Layers.Any(x => x.Name == slots[i]))
                    ar.SetLayerActive(false, LayerTypes[i]);
            return ar;
        }

        private static PathReinforcement CreatePath(Document doc, ResolvedSystemRow r)
        {
            StructuralPathRule p = r.Path;
            PathReinforcement pr = PathReinforcement.Create(doc, r.Host, Lines(p.PathMm, closed: false), p.Flip,
                                                            r.SystemType.Id, r.BarTypes["primary"].Id,
                                                            ElementId.InvalidElementId, ElementId.InvalidElementId);
            if (pr == null) return null;
            SetId(pr, BuiltInParameter.PATH_REIN_TYPE_1, r.BarTypes["primary"].Id, r, "primary.bar_type");
            SetDouble(pr, BuiltInParameter.PATH_REIN_SPACING, p.PrimarySpacingMm * MmToFt, r, "primary.spacing_mm");
            SetDouble(pr, BuiltInParameter.PATH_REIN_LENGTH_1, p.PrimaryLengthMm * MmToFt, r, "primary.length_mm");
            // ONLY the primary bars were declared, so alternating bars are off - that is what
            // the declaration means, not a default this bridge chose.
            SetInt(pr, BuiltInParameter.PATH_REIN_ALTERNATING, 0, r, "alternating_bars");
            return pr;
        }

        private static FabricArea CreateFabric(Document doc, ResolvedSystemRow r)
        {
            StructuralFabricRule f = r.Fabric;
            CurveLoop loop = CurveLoop.Create(Lines(f.BoundaryMm, closed: true));
            FabricArea fa = FabricArea.Create(doc, r.Host, new List<CurveLoop> { loop }, Xyz(f.MajorDirectionMm, 1.0),
                                              Xyz(f.MajorDirectionOriginMm, MmToFt), r.SystemType.Id, r.SheetType.Id);
            if (fa == null) return null;
            if (f.CoverOffsetMm.HasValue)
            {
                double ft = f.CoverOffsetMm.Value * MmToFt;
                if (!fa.IsCoverOffsetValid(ft))
                    throw new InvalidOperationException(
                        "fabric rule '" + r.RuleId + "' declares cover_offset_mm " +
                        f.CoverOffsetMm.Value.ToString("0.###", CultureInfo.InvariantCulture) +
                        " and Revit says it is not valid for this fabric area. Nothing in this batch is kept.");
                fa.CoverOffset = ft;
            }
            return fa;
        }

        private static List<Curve> Lines(List<double[]> pts, bool closed)
        {
            var curves = new List<Curve>();
            int n = closed ? pts.Count : pts.Count - 1;
            for (int i = 0; i < n; i++)
                curves.Add(Line.CreateBound(Xyz(pts[i], MmToFt), Xyz(pts[(i + 1) % pts.Count], MmToFt)));
            return curves;
        }

        private static XYZ Xyz(double[] v, double scale) => new XYZ(v[0] * scale, v[1] * scale, v[2] * scale);

        private static Parameter Writable(Element e, BuiltInParameter bip, ResolvedSystemRow r, string what)
        {
            Parameter p = null;
            try { p = e.get_Parameter(bip); } catch { }
            if (p == null || p.IsReadOnly)
                throw new InvalidOperationException(
                    r.Kind + " rule '" + r.RuleId + "': Revit gives no writable " + bip + " for " + what + " on the " +
                    "system it just created, so the declared value cannot be set. Nothing in this batch is kept.");
            return p;
        }

        private static void Took(bool ok, BuiltInParameter bip, ResolvedSystemRow r, string what)
        {
            if (!ok)
                throw new InvalidOperationException(
                    r.Kind + " rule '" + r.RuleId + "': Revit refused " + what + " (" + bip + "). Nothing in this " +
                    "batch is kept.");
        }

        private static void SetDouble(Element e, BuiltInParameter bip, double v, ResolvedSystemRow r, string what) =>
            Took(Writable(e, bip, r, what).Set(v), bip, r, what);

        private static void SetInt(Element e, BuiltInParameter bip, int v, ResolvedSystemRow r, string what) =>
            Took(Writable(e, bip, r, what).Set(v), bip, r, what);

        private static void SetId(Element e, BuiltInParameter bip, ElementId v, ResolvedSystemRow r, string what) =>
            Took(Writable(e, bip, r, what).Set(v), bip, r, what);

        private static void SetText(Element e, BuiltInParameter bip, string v, ResolvedSystemRow r, string what) =>
            Took(Writable(e, bip, r, what).Set(v), bip, r, what);

        // -------------------------------------------------------------- verify

        /// <summary>
        /// Read a built system back and compare every parameter the rule declared. The row
        /// carries `checks`, `verified` (true only when every check is) and the counts.
        /// Used by the apply after the commit and by the audit.
        /// </summary>
        public static JObject Verify(Document doc, ElementId id, ResolvedSystemRow r, StructuralTolerances tol)
        {
            var row = new JObject
            {
                ["kind"] = r.Kind,
                ["rule_id"] = r.RuleId,
                ["element_id"] = Rid.Value(id),
                ["host_id"] = Rid.Value(r.Host.Id)
            };
            Element e = doc.GetElement(id);
            bool rightClass = r.Area != null ? e is AreaReinforcement : r.Path != null ? e is PathReinforcement : e is FabricArea;
            if (e == null || !rightClass)
            {
                row["present"] = false;
                row["verified"] = false;
                row["why"] = "the id the creation returned is not a " + r.Kind + " system in the document now.";
                return row;
            }
            row["present"] = true;

            var checks = new JObject();
            checks["host"] = SystemReinforcementRules.CheckId(Rid.Value(r.Host.Id), HostOf(e));
            checks["system_type"] = SystemReinforcementRules.CheckId(Rid.Value(r.SystemType.Id), Safe(() => Rid.Value(e.GetTypeId())));

            if (e is AreaReinforcement ar)
            {
                StructuralAreaRule a = r.Area;
                bool wall = a.LayerFamily == SystemReinforcementRules.FamilyWall;
                string[] slots = SystemReinforcementRules.LayersOf(a.LayerFamily);
                var want = new Dictionary<string, SystemLayerWant>(StringComparer.Ordinal);
                foreach (StructuralSystemLayer l in a.Layers)
                    want[l.Name] = new SystemLayerWant { BarTypeId = Rid.Value(r.BarTypes[l.Name].Id), SpacingMm = l.SpacingMm };
                var read = new Dictionary<string, SystemLayerRead>(StringComparer.Ordinal);
                for (int i = 0; i < 4; i++)
                {
                    int k = i;
                    read[slots[i]] = new SystemLayerRead
                    {
                        Active = SafeBool(() => ar.IsLayerActive(LayerTypes[k])),
                        SpacingMm = ReadDouble(ar, (wall ? WallSpacing : SlabSpacing)[i]) * FtToMm,
                        BarTypeId = ReadId(ar, (wall ? WallBarType : SlabBarType)[i]),
                        HookTypeId = ReadId(ar, (wall ? WallHook : SlabHook)[i])
                    };
                }
                foreach (JProperty p in SystemReinforcementRules.AreaLayerChecks(a.LayerFamily, want, read, tol.SpacingMm).Properties())
                    checks[p.Name] = p.Value;
                checks["major_direction"] = SystemReinforcementRules.CheckDirection(a.MajorDirectionMm,
                    Vec(SafeObj(() => ar.Direction)), tol.AngleDegrees);
                int? bars = SafeObj(() => ar.GetRebarInSystemIds())?.Count;
                checks["rebar_in_system"] = SystemReinforcementRules.CheckAtLeast(1, bars);
                row["rebar_in_system"] = bars.HasValue ? (JToken)bars.Value : JValue.CreateNull();
                row["layout_rule_read"] = ReadIntToken(ar, BuiltInParameter.REBAR_SYSTEM_LAYOUT_RULE);
            }
            else if (e is PathReinforcement pr)
            {
                StructuralPathRule p = r.Path;
                checks["primary.bar_type_id"] = SystemReinforcementRules.CheckId(Rid.Value(r.BarTypes["primary"].Id),
                    ReadId(pr, BuiltInParameter.PATH_REIN_TYPE_1));
                checks["primary.spacing_mm"] = SystemReinforcementRules.CheckLengthMm(p.PrimarySpacingMm,
                    ReadDouble(pr, BuiltInParameter.PATH_REIN_SPACING) * FtToMm, tol.SpacingMm);
                checks["primary.length_mm"] = SystemReinforcementRules.CheckLengthMm(p.PrimaryLengthMm,
                    ReadDouble(pr, BuiltInParameter.PATH_REIN_LENGTH_1) * FtToMm, tol.LengthMm);
                checks["primary.start_hook_type_id"] = SystemReinforcementRules.CheckId(-1, ReadId(pr, BuiltInParameter.PATH_REIN_HOOK_TYPE_1));
                checks["primary.end_hook_type_id"] = SystemReinforcementRules.CheckId(-1, ReadId(pr, BuiltInParameter.PATH_REIN_END_HOOK_TYPE_1));
                int? alt = ReadInt(pr, BuiltInParameter.PATH_REIN_ALTERNATING);
                checks["alternating_bars"] = SystemReinforcementRules.CheckFlag(false, alt.HasValue ? alt.Value != 0 : (bool?)null);
                int? bars = SafeObj(() => pr.GetRebarInSystemIds())?.Count;
                checks["rebar_in_system"] = SystemReinforcementRules.CheckAtLeast(1, bars);
                row["rebar_in_system"] = bars.HasValue ? (JToken)bars.Value : JValue.CreateNull();
                checks["flip"] = SystemReinforcementRules.NotApplicable(
                    "the side a path was flipped to is not read back by this build (path_reinforcement_flip is in " +
                    "the audit's not_checked list); it was sent to Revit as declared: " + (p.Flip ? "true" : "false") + ".");
            }
            else if (e is FabricArea fa)
            {
                StructuralFabricRule f = r.Fabric;
                checks["fabric_sheet_type"] = SystemReinforcementRules.CheckId(Rid.Value(r.SheetType.Id),
                    Safe(() => Rid.Value(fa.FabricSheetTypeId)));
                checks["major_direction"] = SystemReinforcementRules.CheckDirection(f.MajorDirectionMm,
                    Vec(SafeObj(() => fa.Direction)), tol.AngleDegrees);
                double[] origin = Vec(SafeObj(() => fa.DirectionOrigin));
                checks["major_direction_origin_mm"] = origin == null
                    ? SystemReinforcementRules.Check(false, f.MajorDirectionOriginMm, null, "the model would not report the origin.")
                    : SystemReinforcementRules.Check(Dist(origin, f.MajorDirectionOriginMm, FtToMm) <= tol.LengthMm,
                        f.MajorDirectionOriginMm, origin.Select(x => Math.Round(x * FtToMm, 3)).ToArray());
                double? cover = SafeNum(() => fa.CoverOffset);
                checks["cover_offset_mm"] = f.CoverOffsetMm.HasValue
                    ? SystemReinforcementRules.CheckLengthMm(f.CoverOffsetMm.Value, cover * FtToMm, tol.CoverMm)
                    : SystemReinforcementRules.NotApplicable(
                        "no cover_offset_mm was declared, so Revit's own value stands; it is reported, not compared.",
                        cover.HasValue ? (object)Math.Round(cover.Value * FtToMm, 3) : null);
                int? sheets = SafeObj(() => fa.GetFabricSheetElementIds())?.Count;
                checks["fabric_sheets"] = SystemReinforcementRules.CheckAtLeast(1, sheets);
                row["fabric_sheets"] = sheets.HasValue ? (JToken)sheets.Value : JValue.CreateNull();
            }

            if (!string.IsNullOrWhiteSpace(r.Mark))
                checks["mark"] = SystemReinforcementRules.CheckText(r.Mark, SafeObj(() => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()));

            row["checks"] = checks;
            row["verified"] = SystemReinforcementRules.AllVerified(checks);
            row["verified_means"] =
                "every parameter this rule set was read back from the model and agreed, and the system produced at " +
                "least one bar (or sheet). Where Revit laid those bars is not measured.";
            return row;
        }

        private static long? HostOf(Element e)
        {
            if (e is AreaReinforcement a) return Safe(() => Rid.Value(a.GetHostId()));
            if (e is PathReinforcement p) return Safe(() => Rid.Value(p.GetHostId()));
            if (e is FabricArea f) return Safe(() => Rid.Value(f.HostId));
            return null;
        }

        private static double Dist(double[] ft, double[] mm, double scale)
        {
            double dx = ft[0] * scale - mm[0], dy = ft[1] * scale - mm[1], dz = ft[2] * scale - mm[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double[] Vec(XYZ v) => v == null ? null : new[] { v.X, v.Y, v.Z };

        private static double? ReadDouble(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                return p.AsDouble();
            }
            catch { return null; }
        }

        private static int? ReadInt(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Integer) return null;
                return p.AsInteger();
            }
            catch { return null; }
        }

        private static JToken ReadIntToken(Element e, BuiltInParameter bip)
        {
            int? v = ReadInt(e, bip);
            return v.HasValue ? (JToken)v.Value : JValue.CreateNull();
        }

        /// <summary>An element-id parameter: -1 for "none", null when unreadable.</summary>
        private static long? ReadId(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || p.StorageType != StorageType.ElementId) return null;
                ElementId v = p.AsElementId();
                return v == null || v == ElementId.InvalidElementId ? -1 : Rid.Value(v);
            }
            catch { return null; }
        }

        private static long? Safe(Func<long> f) { try { return f(); } catch { return null; } }
        private static double? SafeNum(Func<double> f) { try { return f(); } catch { return null; } }
        private static bool? SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
        private static T SafeObj<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
    }
}
