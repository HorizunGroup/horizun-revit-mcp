// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A DECLARATIVE RULE FOR A TYPE CHANGE APPLIED PER INSTANCE, not one type
// forced on a whole batch. Field session 2026-09-25: 1,000 instances needed a
// type chosen by their OWN geometry (a narrow slab strip quantified ML - per
// linear metre - against a wide one quantified M2 - per square metre) and the
// only way to do it was a hand-written Python loop, invisible to dry_run and
// to WriteVerificationCatalog alike.
//
// This file is Revit-free: it decides, for one instance, which rule in an
// ordered list matches its MEASURED values, and it computes the short/long
// side of a rectangle from two lengths a caller already read off a face's own
// UV bounding box. Neither needs a Document to be correct, so neither needs
// one to be tested.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One branch: a set of comparisons on named measures, all of which must
    /// hold, or an "else" that matches whatever reached it - and the type id to use
    /// when it does.</summary>
    public sealed class TypeChangeRule
    {
        public int Index;
        public bool IsElse;
        /// <summary>measure name -> (operator, threshold). Every entry must hold.</summary>
        public List<Tuple<string, string, double>> When = new List<Tuple<string, string, double>>();
        public long TypeId;
    }

    public sealed class TypeChangeRuleSet
    {
        public List<TypeChangeRule> Rules = new List<TypeChangeRule>();
        public string Error;
        public bool Ok => Error == null;
    }

    /// <summary>Which rule matched an instance, or why none did.</summary>
    public sealed class TypeChangeMatch
    {
        public bool Matched;
        public int RuleIndex = -1;
        public long TypeId;
        public string Reason;
    }

    public static class TypeChangeRuleRules
    {
        public static readonly string[] SupportedOperators = { "lt", "lte", "gt", "gte", "eq" };

        /// <summary>
        /// Parses the "rule" array: [{"when": {"measure": {"op": value}, ...}, "type_id": X}, ...,
        /// {"else": true, "type_id": Y}]. At most one "else", and it must be last if present -
        /// a rule after "else" would never be reached, which is a mistake worth refusing rather
        /// than silently ignoring.
        /// </summary>
        public static TypeChangeRuleSet Parse(JArray rule)
        {
            var set = new TypeChangeRuleSet();
            if (rule == null || rule.Count == 0)
            {
                set.Error = "rule must be a non-empty array of {when, type_id} or {else, type_id} entries.";
                return set;
            }
            bool sawElse = false;
            for (int i = 0; i < rule.Count; i++)
            {
                var o = rule[i] as JObject;
                if (o == null) { set.Error = "rule[" + i + "] is not an object."; return set; }
                if (sawElse) { set.Error = "rule[" + i + "] follows 'else', which already matches everything before it - unreachable."; return set; }
                var r = new TypeChangeRule { Index = i };
                JToken typeIdToken = o["type_id"];
                if (typeIdToken == null || typeIdToken.Type != JTokenType.Integer)
                { set.Error = "rule[" + i + "].type_id is required and must be an integer."; return set; }
                r.TypeId = typeIdToken.Value<long>();

                bool hasElse = o["else"]?.Type == JTokenType.Boolean && o.Value<bool>("else");
                var when = o["when"] as JObject;
                if (hasElse == (when != null))
                { set.Error = "rule[" + i + "] must carry exactly one of 'when' (an object) or 'else': true."; return set; }

                if (hasElse) { r.IsElse = true; sawElse = true; set.Rules.Add(r); continue; }

                foreach (JProperty measure in when.Properties())
                {
                    var cmp = measure.Value as JObject;
                    if (cmp == null || cmp.Count != 1)
                    { set.Error = "rule[" + i + "].when." + measure.Name + " must be an object with exactly one operator."; return set; }
                    JProperty op = cmp.Properties().First();
                    if (Array.IndexOf(SupportedOperators, op.Name) < 0)
                    { set.Error = "rule[" + i + "].when." + measure.Name + " uses an unsupported operator; supported: " + string.Join(", ", SupportedOperators) + "."; return set; }
                    double threshold;
                    if (op.Value.Type != JTokenType.Integer && op.Value.Type != JTokenType.Float)
                    { set.Error = "rule[" + i + "].when." + measure.Name + "." + op.Name + " must be a number."; return set; }
                    threshold = op.Value.Value<double>();
                    r.When.Add(Tuple.Create(measure.Name, op.Name, threshold));
                }
                if (r.When.Count == 0)
                { set.Error = "rule[" + i + "].when must compare at least one measure."; return set; }
                set.Rules.Add(r);
            }
            return set;
        }

        /// <summary>Evaluates the ordered rules against one instance's measured values, first match wins.
        /// An UNMEASURED instance (measured is null/empty - its face could not be measured, or was not
        /// a plain rectangle) matches NO rule, not even 'else': 'else' exists to catch an instance whose
        /// measures were read but did not satisfy any 'when', not one that was never measured at all. A
        /// silent 'else' would classify a mismeasured instance exactly like a deliberately-caught one,
        /// with nothing in the reply distinguishing the two.</summary>
        public static TypeChangeMatch Evaluate(TypeChangeRuleSet set, IDictionary<string, double> measured)
        {
            if (measured == null || measured.Count == 0)
                return new TypeChangeMatch { Matched = false, Reason = "unmeasured: this instance's face could not be measured " +
                    "(no short_side_mm/long_side_mm/area_m2), so no rule applies to it - not even 'else'" };
            foreach (TypeChangeRule r in set.Rules)
            {
                if (r.IsElse)
                    return new TypeChangeMatch { Matched = true, RuleIndex = r.Index, TypeId = r.TypeId, Reason = "else" };
                bool allHold = true;
                string why = null;
                foreach (Tuple<string, string, double> cmp in r.When)
                {
                    double v;
                    if (measured == null || !measured.TryGetValue(cmp.Item1, out v))
                    { allHold = false; why = cmp.Item1 + " was not measured for this instance"; break; }
                    bool holds = Holds(v, cmp.Item2, cmp.Item3);
                    if (!holds) { allHold = false; why = cmp.Item1 + "=" + v.ToString("0.###", CultureInfo.InvariantCulture) + " " + cmp.Item2 + " " + cmp.Item3.ToString("0.###", CultureInfo.InvariantCulture) + " is false"; break; }
                }
                if (allHold)
                {
                    string reason = string.Join(" and ", r.When.Select(c =>
                        c.Item1 + " " + c.Item2 + " " + c.Item3.ToString("0.###", CultureInfo.InvariantCulture)));
                    return new TypeChangeMatch { Matched = true, RuleIndex = r.Index, TypeId = r.TypeId, Reason = reason };
                }
            }
            return new TypeChangeMatch { Matched = false, Reason = "no rule matched and no 'else' was declared" };
        }

        private static bool Holds(double v, string op, double threshold)
        {
            switch (op)
            {
                case "lt": return v < threshold;
                case "lte": return v <= threshold;
                case "gt": return v > threshold;
                case "gte": return v >= threshold;
                case "eq": return Math.Abs(v - threshold) < 1e-6;
                default: return false;
            }
        }

        /// <summary>
        /// The short and long side of the rectangle a face's own UV bounding box describes -
        /// which for a PlanarFace is metric along the face's own orthonormal basis, so this is
        /// the actual physical width and length, not merely an axis-aligned proxy for them.
        /// Same units in, same units out.
        /// </summary>
        public static Tuple<double, double> ShortLong(double uExtent, double vExtent)
        {
            double a = Math.Abs(uExtent), b = Math.Abs(vExtent);
            return a <= b ? Tuple.Create(a, b) : Tuple.Create(b, a);
        }

        /// <summary>
        /// Whether a face's own UV bounding box can be TRUSTED as its true short/long side and
        /// area - i.e. the face is a plain rectangle, not merely convex-hulled into looking like
        /// one. The checks, ALL of which must hold:
        ///   * exactly one edge loop (a second loop is an opening/hole - the bounding box still
        ///     spans the outer boundary, but the face is not a solid rectangle);
        ///   * at least four edges in that loop;
        ///   * its own area agrees with uExtent*vExtent within toleranceFraction (default 2%).
        /// The AREA is what proves the shape: a polygon lying inside its own bounding box with
        /// the same area IS that box, so an L-shape, a triangle, a trapezoid or a chamfer fails
        /// here whatever its edge count. The edge count is NOT required to be four: measured
        /// live 2026-09-26 in all four Revit years, a plain wall's exterior face comes back with
        /// its sides split where other walls and floors meet it (collinear edges), and "exactly
        /// four" left every such wall unmeasured.
        /// Any failure means MeasureInstance leaves the instance UNMEASURED rather than reporting
        /// a short/long side that looks precise but is not the element's real geometry.
        /// </summary>
        public static bool IsRectangularFace(int edgeLoopCount, int outerLoopEdgeCount, double areaFt2,
            double uExtentFt, double vExtentFt, double toleranceFraction = 0.02) =>
            WhyNotRectangular(edgeLoopCount, outerLoopEdgeCount, areaFt2, uExtentFt, vExtentFt, toleranceFraction) == null;

        /// <summary>Null when the face is a plain rectangle; otherwise the reason, in words, with the numbers.</summary>
        public static string WhyNotRectangular(int edgeLoopCount, int outerLoopEdgeCount, double areaFt2,
            double uExtentFt, double vExtentFt, double toleranceFraction = 0.02)
        {
            if (edgeLoopCount != 1)
                return edgeLoopCount + " edge loops (an opening or hole in the face)";
            if (outerLoopEdgeCount < 4)
                return "only " + outerLoopEdgeCount + " edges";
            double bboxAreaFt2 = Math.Abs(uExtentFt * vExtentFt);
            if (bboxAreaFt2 <= 0) return "an empty bounding box";
            double diff = Math.Abs(areaFt2 - bboxAreaFt2);
            if (diff > bboxAreaFt2 * toleranceFraction)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "its area is {0:0.###} m2 against {1:0.###} m2 for its bounding box (not a rectangle, or rotated in its own UV frame)",
                    areaFt2 * 0.09290304, bboxAreaFt2 * 0.09290304);
            return null;
        }
    }
}
