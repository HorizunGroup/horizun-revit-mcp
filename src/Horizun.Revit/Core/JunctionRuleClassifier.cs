using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    public enum JunctionRuleKind { Wye, Tee, Other }

    /// <summary>One Junctions routing rule as read from the pipe type (no Revit types).</summary>
    public sealed class JunctionRuleInfo
    {
        public JunctionRuleInfo(int index, string name, string partType)
        { Index = index; Name = name ?? ""; PartType = partType ?? ""; }
        public int Index { get; }
        public string Name { get; }
        public string PartType { get; }
    }

    public sealed class ObliqueJunctionChoice
    {
        public int WyeIndex = -1;
        public int TeeIndex = -1;
        /// <summary>
        /// Every wye-like rule, best first (Part Type Wye / LateralTee before a Tee promoted by its
        /// name, then by index). WyeIndex is the first. The oblique build tries them in this order:
        /// Revit refuses to swap a tee for some manufacturer wyes ("Can't make type"), and another
        /// wye in the same pipe type may be accepted.
        /// </summary>
        public List<int> WyeCandidates = new List<int>();
        /// <summary>Null when both were found; otherwise a self-explaining refusal naming the rules seen.</summary>
        public string Problem;
    }

    /// <summary>
    /// Decides which Junctions rule of a pipe type is the wye and which is the perpendicular
    /// tee for the oblique-branch workaround. The decision rests on the family's Part Type
    /// (language independent). Family and type names are model data in the model's language,
    /// so they are used only as a fallback to PROMOTE a Tee to a wye: manufacturer families
    /// often mark a wye as plain Tee (Revit routing preferences refuse a Wye part as a
    /// Junction, measured on Revit 2025), and a name never demotes or creates a tee.
    /// Wye-like: PartType Wye or LateralTee, or PartType Tee whose name says wye/yee, a
    /// explicit phrase ("en Y", "tipo Y", "Y-pattern", "Tee Y" at the end, "45 degrees"); a bare Y or 45 is not evidence. Perpendicular tee: PartType
    /// Tee that is not wye-like.
    /// </summary>
    public static class JunctionRuleClassifier
    {
        // Explicit phrases only. A bare "Y" or "45" is NOT evidence: "Y" is also the Spanish
        // conjunction in any case ("TE RECTA Y REDUCIDA") and "45" appears in sizes ("45 mm").
        private static readonly Regex NameSaysWyePhrase = new Regex(
            @"wye|yee|(?<![\p{L}\p{N}])(?:en|tipo)\s+Y(?![\p{L}\p{N}])|(?<![\p{L}\p{N}])Y[\s-]*pattern|" +
            @"(?<![\p{L}\p{N}])Te{1,2}\s+Y\s*(?:$|[:,;()\[\]/|-])|" +
            @"(?<![\p{L}\p{N}.,])45\s*(?:\u00b0|\u00ba|deg|grad)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static bool NameSuggestsWye(string name)
            => !string.IsNullOrEmpty(name) && NameSaysWyePhrase.IsMatch(name);

        private static bool IsWyePartType(string partType)
        {
            string p = (partType ?? "").Trim();
            return p.Equals("Wye", StringComparison.OrdinalIgnoreCase) ||
                   p.Equals("LateralTee", StringComparison.OrdinalIgnoreCase);
        }

        public static JunctionRuleKind Classify(string partType, string name)
        {
            string p = (partType ?? "").Trim();
            if (IsWyePartType(p)) return JunctionRuleKind.Wye;
            if (p.Equals("Tee", StringComparison.OrdinalIgnoreCase))
                return NameSuggestsWye(name) ? JunctionRuleKind.Wye : JunctionRuleKind.Tee;
            return JunctionRuleKind.Other;
        }

        public static ObliqueJunctionChoice Choose(IList<JunctionRuleInfo> rules)
        {
            var choice = new ObliqueJunctionChoice();
            // A Part Type Wye/LateralTee beats a Tee promoted by its name, whatever the index.
            foreach (var r in rules)
                if (IsWyePartType(r.PartType)) { choice.WyeIndex = r.Index; break; }
            if (choice.WyeIndex < 0)
                foreach (var r in rules)
                    if (Classify(r.PartType, r.Name) == JunctionRuleKind.Wye) { choice.WyeIndex = r.Index; break; }
            foreach (var r in rules)
                if (r.Index != choice.WyeIndex && Classify(r.PartType, r.Name) == JunctionRuleKind.Tee)
                { choice.TeeIndex = r.Index; break; }
            foreach (var r in rules)
                if (IsWyePartType(r.PartType)) choice.WyeCandidates.Add(r.Index);
            foreach (var r in rules)
                if (!IsWyePartType(r.PartType) && Classify(r.PartType, r.Name) == JunctionRuleKind.Wye) choice.WyeCandidates.Add(r.Index);
            if (choice.WyeIndex < 0)
                choice.Problem = "oblique branch: no junction rule is a wye (Part Type Wye or LateralTee, or a Tee whose name says wye/yee/en Y/tipo Y/Y-pattern/45deg.). Rules seen: " + Describe(rules);
            else if (choice.TeeIndex < 0)
                choice.Problem = "oblique wye needs a perpendicular tee (Part Type Tee that is not a wye) as another junction rule. Rules seen: " + Describe(rules);
            return choice;
        }

        /// <summary>
        /// The refusal when Revit would not change the temporary tee to ANY wye of the pipe type. It names each
        /// wye tried with Revit's own words and the likeliest cause of that class of refusal, so the caller
        /// knows what to change in the family instead of reading "Can't make type".
        /// </summary>
        public static string SwitchRefusal(IList<KeyValuePair<string, string>> attempts)
        {
            var sb = new StringBuilder("the temporary tee could not change to a wye: Revit refused every wye rule of this pipe type. ");
            if (attempts != null)
                foreach (var a in attempts)
                    sb.Append("[").Append(a.Key).Append("] Revit said \"").Append(a.Value).Append("\". ");
            sb.Append("Revit swaps a placed tee for another fitting type only when both have the same connector layout: the same number of connectors, in the same piping domain and ")
              .Append("system classification, with sizes it can drive from the pipes already joined. A manufacturer wye with other connectors or fixed sizes is refused this way. ")
              .Append("Nothing was built. Load a wye made for this pipe type (horizun_family_reload (Pro edition) operation=load reads each family's connectors in its rehearsal), ")
              .Append("or put such a wye first in the Junctions rules (horizun_mep_routing (Pro edition) set_rules).");
            return sb.ToString();
        }

        /// <summary>
        /// The SUPUESTO row when the oblique build used a wye OTHER than the preferred one (the first wye rule):
        /// which wye was preferred, which one was used, and Revit's own words for each wye it refused before.
        /// Null when the preferred wye was used (nothing was assumed).
        /// </summary>
        public static Newtonsoft.Json.Linq.JObject WyeSubstitution(int itemIndex, string preferred, string used,
                                                                   IList<KeyValuePair<string, string>> refusals)
        {
            if (string.Equals(preferred ?? "", used ?? "", StringComparison.Ordinal)) return null;
            var why = new StringBuilder("Revit refused to change the temporary tee to the preferred wye (the first wye of the Junctions rules)");
            if (refusals != null && refusals.Count > 0)
            {
                why.Append(": ");
                for (int i = 0; i < refusals.Count; i++)
                {
                    if (i > 0) why.Append("; ");
                    why.Append("[").Append(refusals[i].Key).Append("] Revit said \"").Append(refusals[i].Value).Append("\"");
                }
            }
            why.Append(". The next wye rule of the same pipe type was accepted and used instead.");
            var row = AssumptionNotes.Row("item " + itemIndex + ": wye used = '" + used + "' (preferred: '" + preferred + "')", why.ToString(),
                "put the wye you want first in the Junctions rules (horizun_mep_routing (Pro edition) set_rules), or load a wye whose connectors match the tee (horizun_family_reload (Pro edition) operation=load reports them).");
            row["item"] = itemIndex;
            row["preferred_wye"] = preferred;
            row["used_wye"] = used;
            var refused = new Newtonsoft.Json.Linq.JArray();
            if (refusals != null)
                foreach (var r in refusals)
                    refused.Add(new Newtonsoft.Json.Linq.JObject { ["wye"] = r.Key, ["revit_said"] = r.Value });
            row["refused"] = refused;
            return row;
        }

        public static string Describe(IList<JunctionRuleInfo> rules)
        {
            var sb = new StringBuilder();
            foreach (var r in rules)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append('[').Append(r.Index).Append("] \"").Append(r.Name).Append("\" part type ")
                  .Append(string.IsNullOrEmpty(r.PartType) ? "unknown" : r.PartType)
                  .Append(" => ").Append(Classify(r.PartType, r.Name));
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }
    }
}
