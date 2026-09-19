// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// A PROJECT'S DATA, CHECKED AS ONE SET BEFORE ANY OF IT IS USED.
//
// A conversion reads a requirement set rule by rule, and refuses at the first
// name the model does not know. That is right for a write and useless for the
// person assembling the data: they learn one missing family per run. This checks
// every rule against the model at once - the type exists, its family can be
// placed the way the rule says it is hosted, the storey exists, a height is or
// is not declared, the category agrees - and says which rules would be built,
// which would be refused, and why. It reads no drawing and writes nothing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What the model says about one type name.</summary>
    public sealed class CadTypeFacts
    {
        public bool Found;
        /// <summary>OneLevelBased | OneLevelBasedHosted | WorkPlaneBased | ... ; null for system types.</summary>
        public string PlacementType;
        /// <summary>OST_* of the type's category, when readable.</summary>
        public string Category;
        /// <summary>A wall type's width, mm.</summary>
        public double? WidthMm;
        public bool IsWallType;
        /// <summary>
        /// When the name is NOT loaded: the loaded types of the family it names ("Family: Type"). MEASURED
        /// (campaign 7): Revit 2023's mechanical template has no "Rectangular Duct: Radius Elbows / Tees", and a
        /// refusal that only says so leaves the caller guessing which name to declare instead.
        /// </summary>
        public List<string> SameFamily = new List<string>();
    }

    public static class CadCatalogCheck
    {
        /// <summary>Which placement types a hosting mode can use. The same table the writer follows.</summary>
        public static readonly Dictionary<string, string[]> Compatible = new Dictionary<string, string[]>
        {
            ["(none)"] = new[] { "OneLevelBased" },
            ["wall"] = new[] { "WorkPlaneBased", "OneLevelBasedHosted" },
            ["slab"] = new[] { "WorkPlaneBased", "OneLevelBasedHosted" },
        };

        /// <summary>The family part of a "Family: Type" label; null when the label names no family.</summary>
        public static string FamilyOf(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            int at = label.IndexOf(": ", StringComparison.Ordinal);
            return at > 0 ? label.Substring(0, at).Trim() : null;
        }

        /// <summary>
        /// What a type_not_found says: the loaded types of the family it named - or, when that family is not loaded
        /// at all, the loaded types of the KIND the rule produces. MEASURED (campaign 7, Revit 2023): the family
        /// "Rectangular Duct" was absent by that name, which is what a Revit running in another language does to
        /// its system families.
        /// </summary>
        public static string LoadedOfFamily(string label, IList<string> sameFamily, string produces = null,
                                            IList<string> sameKind = null)
        {
            const string choice = ". Which of them the drawing means is the caller's decision, not this check's.";
            string family = FamilyOf(label);
            if (family == null) return "the name carries no family part, so no family can be listed";
            if (sameFamily != null && sameFamily.Count > 0)
                return "loaded types of family '" + family + "': " + string.Join(", ", sameFamily.Select(n => "'" + n + "'")) + choice;
            if (sameKind != null && sameKind.Count > 0)
                return "no type of family '" + family + "' is loaded; the " + (produces ?? "same-kind") + " types this model " +
                       "does load are " + string.Join(", ", sameKind.Select(n => "'" + n + "'")) + " (a Revit running in " +
                       "another language names its system families in that language)" + choice;
            return "no type of family '" + family + "' is loaded";
        }

        public static JObject Check(CadRequirementSet set, Func<string, CadTypeFacts> type, ICollection<string> levels,
                                    Func<string, List<string>> typesOfKind = null)
        {
            var rows = new JArray();
            int ok = 0, refused = 0, warned = 0;
            foreach (CadRule rule in set.Rules)
            {
                var problems = new List<string>();
                var warnings = new List<string>();
                var row = new JObject { ["rule"] = rule.Id, ["produces"] = rule.Produces };

                if (!string.IsNullOrWhiteSpace(rule.Level) && levels != null && !levels.Contains(rule.Level))
                    problems.Add("level_not_found: no level is named '" + rule.Level + "'");

                if (rule.WallTypes != null && rule.WallTypes.Count > 0)
                {
                    var listed = new JArray();
                    foreach (string name in rule.WallTypes)
                    {
                        CadTypeFacts f = type(name) ?? new CadTypeFacts();
                        listed.Add(new JObject
                        {
                            ["type"] = name, ["present"] = f.Found && f.IsWallType,
                            ["width_mm"] = f.WidthMm.HasValue ? Math.Round(f.WidthMm.Value, 1) : (double?)null
                        });
                        if (!f.Found) problems.Add("wall_type_not_found: '" + name + "'");
                        else if (!f.IsWallType) problems.Add("not_a_wall_type: '" + name + "'");
                    }
                    row["wall_types"] = listed;
                    // two listed types of one width make the choice by thickness a coin toss
                    var widths = listed.OfType<JObject>().Where(x => x["width_mm"]?.Type == JTokenType.Float)
                                       .GroupBy(x => (double)x["width_mm"]).Where(g => g.Count() > 1).ToList();
                    foreach (var g in widths)
                        warnings.Add("same_width_twice: " + string.Join(", ", g.Select(x => (string)x["type"])) +
                                     " are both " + g.Key + " mm; a wall of that thickness would be withdrawn as a tie");
                }

                if (!string.IsNullOrWhiteSpace(rule.FamilyType) && (rule.WallTypes == null || rule.WallTypes.Count == 0))
                {
                    CadTypeFacts f = type(rule.FamilyType) ?? new CadTypeFacts();
                    row["family_type"] = rule.FamilyType;
                    row["present"] = f.Found;
                    if (!f.Found)
                    {
                        List<string> kind = f.SameFamily.Count == 0 && typesOfKind != null
                            ? typesOfKind(rule.Produces) ?? new List<string>() : new List<string>();
                        problems.Add("type_not_found: '" + rule.FamilyType + "' is not loaded in this model; " +
                                     LoadedOfFamily(rule.FamilyType, f.SameFamily, rule.Produces, kind));
                        row["loaded_of_this_family"] = new JArray(f.SameFamily);
                        if (kind.Count > 0) row["loaded_of_this_kind"] = new JArray(kind);
                    }
                    else
                    {
                        row["placement_type"] = f.PlacementType;
                        row["category"] = f.Category;
                        if (!string.IsNullOrWhiteSpace(rule.Category) && !string.IsNullOrWhiteSpace(f.Category) &&
                            !string.Equals(rule.Category, f.Category, StringComparison.OrdinalIgnoreCase))
                            problems.Add("category_differs: the rule says " + rule.Category + ", the type is " + f.Category);
                        if (f.PlacementType != null)
                        {
                            string mode = string.IsNullOrWhiteSpace(rule.HostedOn) ? "(none)" : rule.HostedOn.ToLowerInvariant();
                            string[] can;
                            if (!Compatible.TryGetValue(mode, out can))
                                problems.Add("hosting_mode_unknown: '" + rule.HostedOn + "'");
                            else if (!can.Contains(f.PlacementType))
                                problems.Add("hosting_incompatible: a " + f.PlacementType + " family cannot be placed " +
                                             (mode == "(none)" ? "without a host" : "on a " + mode) +
                                             " (this mode takes " + string.Join(" or ", can) + ")");
                        }
                        // A PLACED FAMILY HAS A MOUNTING HEIGHT OR DOES NOT; doors and windows take sills instead.
                        if (f.PlacementType != null && rule.Produces != "door" && rule.Produces != "window")
                        {
                            row["mounting_height_mm"] = rule.OffsetMm;
                            if (!rule.OffsetMm.HasValue)
                                warnings.Add("no_mounting_height: placed at the level; its elevation stays unknown, never passed");
                        }
                    }
                }

                row["problems"] = new JArray(problems);
                row["warnings"] = new JArray(warnings);
                row["verdict"] = problems.Count > 0 ? "refused" : warnings.Count > 0 ? "usable_with_warnings" : "usable";
                if (problems.Count > 0) refused++; else if (warnings.Count > 0) warned++; else ok++;
                rows.Add(row);
            }
            return new JObject
            {
                ["requirement_set"] = set.Id + " " + set.Version,
                ["rules"] = rows,
                ["counts"] = new JObject { ["usable"] = ok, ["usable_with_warnings"] = warned, ["refused"] = refused },
                ["would_write"] = false,
                ["means"] = "every rule of this set checked against THIS model at once: the type is loaded, its family " +
                            "can be placed the way the rule hosts it, the storey exists, the category agrees and a " +
                            "mounting height is or is not declared. No drawing was read and nothing was written. A refused " +
                            "rule is one a conversion would stop at; fix all of them in one pass."
            };
        }
    }
}
