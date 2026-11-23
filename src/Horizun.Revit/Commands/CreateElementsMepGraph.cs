// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of the local MEP graph check of horizun_create_elements: the arithmetic
// is Core/MepGraphRules.cs (proved without Revit); this file only reads, from the
// document, what that arithmetic cannot know - the routing preferences of the pipe or
// duct type a fitting row will be built on.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        /// <summary>Every issue of the batch's MEP graph: refusals (the fitting cannot exist) and warnings.</summary>
        private static List<MepGraphIssue> MepGraphCheck(Document doc, JArray input, double scaleToFeet)
        {
            MepGraphRules.ReadBatch(input, scaleToFeet, out List<MepGraphCurve> curves, out List<MepGraphFitting> fittings);
            List<MepGraphIssue> issues = MepGraphRules.Check(curves, fittings, null);
            var byIndex = curves.ToDictionary(c => c.Index);
            foreach (MepGraphFitting fitting in fittings)
            {
                if (fitting.Members.Count == 0 || !byIndex.TryGetValue(fitting.Members[0], out MepGraphCurve first)) continue;
                if (first.Kind != "pipe" && first.Kind != "duct") continue;
                if (first.TypeId == null || !Rid.CanRepresent(first.TypeId.Value)) continue;
                MEPCurveType type = doc.GetElement(Rid.Make(first.TypeId.Value)) as MEPCurveType;
                RoutingPreferenceManager routing = null;
                try { routing = type?.RoutingPreferenceManager; } catch { }
                if (routing == null) continue;
                RoutingPreferenceRuleGroupType group;
                switch (fitting.Subtype)
                {
                    case "elbow": group = RoutingPreferenceRuleGroupType.Elbows; break;
                    case "tee": group = RoutingPreferenceRuleGroupType.Junctions; break;
                    case "transition": group = RoutingPreferenceRuleGroupType.Transitions; break;
                    case "union": group = RoutingPreferenceRuleGroupType.Unions; break;
                    default: continue;
                }
                int rules;
                try { rules = routing.GetNumberOfRules(group); } catch { continue; }
                string warning = MepGraphRules.RoutingPreferenceWarning(first.Kind, type.Name, fitting.Subtype, rules);
                if (warning != null)
                    issues.Add(new MepGraphIssue { Index = fitting.Index, Code = MepGraphRules.CodeNoRoutingPreference, Message = warning, Refusal = false });
            }
            return issues.OrderBy(i => i.Index).ThenBy(i => i.Code, StringComparer.Ordinal).ToList();
        }
    }
}
