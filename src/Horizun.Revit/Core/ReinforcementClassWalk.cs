// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// WHICH REINFORCEMENT CLASSES A WALK COVERED. query_structure mode=rebar once
// walked Rebar alone and still said complete / is_whole_truth: true, while the
// bars of an area reinforcement (RebarInSystem) sat in the model unread. Whole
// truth is claimed only when EVERY class was walked and could be read; each
// class is reported with its count, and a class skipped or unreadable is named
// as a reason, so the coverage is partial.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ReinforcementClassWalk
    {
        public string Class;
        public bool Walked;
        public int Count;
        /// <summary>Why the class was skipped or would not read.</summary>
        public string Why;
    }

    public static class ReinforcementClasses
    {
        public static readonly string[] All =
        {
            "Rebar", "RebarInSystem", "AreaReinforcement", "PathReinforcement", "FabricSheet", "FabricArea"
        };

        /// <summary>
        /// Per class what was walked and its count. A class absent from `walks`, or not walked, is a reason
        /// naming it; with any reason the coverage is partial. Every class always appears in the report.
        /// </summary>
        public static JObject Report(IEnumerable<ReinforcementClassWalk> walks, out JArray reasons)
        {
            List<ReinforcementClassWalk> list = (walks ?? Enumerable.Empty<ReinforcementClassWalk>()).Where(w => w != null).ToList();
            reasons = new JArray();
            var o = new JObject();
            foreach (string cls in All)
            {
                ReinforcementClassWalk w = list.FirstOrDefault(x => x.Class == cls);
                if (w == null || !w.Walked)
                {
                    o[cls] = new JObject { ["walked"] = false, ["count"] = null };
                    reasons.Add(StructuralCoverage.Reason("reinforcement_class",
                        "the " + cls + " class was not walked" + (w == null || w.Why == null ? "" : " (" + w.Why + ")") +
                        ", so bars of that class may be missing from this answer."));
                }
                else o[cls] = new JObject { ["walked"] = true, ["count"] = w.Count };
            }
            return o;
        }

        /// <summary>Whole truth only when every class was walked.</summary>
        public static string Coverage(JArray classReasons)
        {
            return classReasons == null || classReasons.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial;
        }
    }
}
