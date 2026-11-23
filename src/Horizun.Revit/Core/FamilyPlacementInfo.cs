// -----------------------------------------------------------------------------
// Horizun Revit MCP - how a loadable family is placed, read from the family itself.
//
// An agent placing a type needs two facts before it calls horizun_create_elements:
// the Revit placement type (one point, two levels, a curve, a work plane...) and the
// host the family demands (wall, floor, ceiling, roof, any face, or none). Both live
// on the Family; neither was readable through a typed command, so agents learned them
// by failing - and a ceiling-hosted luminaire could even be accepted on a wall.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class FamilyPlacementInfo
    {
        /// <summary>wall | floor | ceiling | roof | face | null (no host).</summary>
        internal static string RequiredHost(FamilySymbol symbol)
        {
            Family family = symbol?.Family;
            if (family == null) return null;
            int behavior = -1;
            try { behavior = family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger() ?? -1; } catch { }
            switch (behavior)
            {
                case 1: return "wall";
                case 2: return "floor";
                case 3: return "ceiling";
                case 4: return "roof";
                case 5: return "face";
            }
            FamilyPlacementType placement = family.FamilyPlacementType;
            if (placement == FamilyPlacementType.WorkPlaneBased) return null;
            if (placement == FamilyPlacementType.OneLevelBasedHosted) return "face";
            return null;
        }

        internal static bool HostMatches(string required, Element host)
        {
            switch (required)
            {
                case "wall": return host is Wall;
                case "floor": return host is Floor;
                case "ceiling": return host is Ceiling;
                case "roof": return host is RoofBase;
                default: return true;
            }
        }

        internal static JObject Describe(FamilySymbol symbol)
        {
            Family family = symbol?.Family;
            if (family == null) return null;
            int behavior = -1; string behaviorText = null;
            try
            {
                Parameter p = family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR);
                if (p != null) { behavior = p.AsInteger(); behaviorText = p.AsValueString(); }
            }
            catch { }
            string host = RequiredHost(symbol);
            FamilyPlacementType pt = family.FamilyPlacementType;
            string route;
            switch (pt)
            {
                case FamilyPlacementType.OneLevelBased: route = "point on a level: horizun_create_elements kind=family_instance (level_id, point, coordinate_mode)"; break;
                case FamilyPlacementType.OneLevelBasedHosted: route = "point on its host: kind=family_instance with host_id (a " + (host ?? "host") + "), level_id, point, coordinate_mode"; break;
                case FamilyPlacementType.TwoLevelsBased: route = "between two levels: kind=structural_column (structural columns) or family_instance with level_id + top_level_id"; break;
                case FamilyPlacementType.CurveBased:
                case FamilyPlacementType.CurveBasedDetail:
                case FamilyPlacementType.CurveDrivenStructural: route = "along a line: kind=structural_framing (start, end, level_id) for framing"; break;
                case FamilyPlacementType.WorkPlaneBased: route = "on a work plane or face: kind=family_instance (point) or host_id of a face host"; break;
                case FamilyPlacementType.ViewBased: route = "view-specific (annotation/detail): not placed in 3D"; break;
                default: route = "see placement_type"; break;
            }
            return new JObject
            {
                ["placement_type"] = pt.ToString(),
                ["host"] = host,
                ["hosting_behavior"] = behavior,
                ["hosting_behavior_label"] = behaviorText,
                ["work_plane_based"] = SafeBool(family, BuiltInParameter.FAMILY_WORK_PLANE_BASED),
                ["always_vertical"] = SafeBool(family, BuiltInParameter.FAMILY_ALWAYS_VERTICAL),
                ["route"] = route
            };
        }

        private static JToken SafeBool(Family family, BuiltInParameter bip)
        {
            try { Parameter p = family.get_Parameter(bip); return p == null ? JValue.CreateNull() : (JToken)(p.AsInteger() != 0); }
            catch { return JValue.CreateNull(); }
        }
    }
}
