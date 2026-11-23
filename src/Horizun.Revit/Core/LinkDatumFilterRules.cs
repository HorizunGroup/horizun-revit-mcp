// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// LINKED DATUMS DRAWN OVER THE HOST'S (§6, field report 2026-11-17, Revit 2024).
//
// Every link drew its grids and levels on top of the host's: up to 14 identical bubbles. The
// API offers no way to switch a link's Grids/Levels off per category (RevitLinkGraphicsSettings
// has no Custom category table in 2023-2027) and HideElements takes host ids only. What WORKED
// in the field: a ParameterFilterElement, invisible in the view, with "Type Name not equal to"
// each host type. Host datums keep their type names and stay; a linked datum of any other type
// name is hidden (93 level clashes solved). It cannot separate a linked datum whose type has
// the SAME name as a host type, nor a link whose display does not take the host's filters:
// those are reported, never claimed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class LinkDatumFilterRules
    {
        /// <summary>At most this many "not equal" rules per filter.</summary>
        public const int MaxNames = 200;

        public const string SameName = "same_type_name_as_host";
        public const string NoHostNames = "host_has_no_type_names";
        public const string Unreadable = "type_name_unreadable";
        public const string Bypass = "link_display_ignores_host_filters";
        public const string BypassUnknown = "link_display_may_ignore_host_filters";

        /// <summary>The filter's name: one per kind and view, so a repeat updates it instead of piling up.</summary>
        public static string FilterName(string kind, long viewId) =>
            "Horizun linked " + (kind == ViewDatumRules.KindGrid ? "grids" : "levels") + " hidden - view " + viewId;

        /// <summary>
        /// The host type names the rule keeps: EVERY type of the kind in the host, drawn today or not. Taking only the
        /// types drawn today (review B3) would hide a host datum of another type the day somebody shows it again.
        /// </summary>
        public static List<string> HostNames(IEnumerable<string> allOfKind) =>
            (allOfKind ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Why a HOST datum would not be kept by the rule; null when its type name is among the rule's values.</summary>
        public static string HostUnprotected(string typeName, ICollection<string> hostNames)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return Unreadable;
            return hostNames != null && hostNames.Any(h => string.Equals(h, typeName, StringComparison.OrdinalIgnoreCase)) ? null : "type_not_in_rule";
        }

        public const string NotEqualsShape = "not_equals";

        /// <summary>
        /// Is a read-back string rule a "not equal to"? <paramref name="inverted"/>: wrapped in a FilterInverseRule.
        /// Accepted: an inverted FilterStringRule with FilterStringEquals, or a FilterStringRule whose evaluator is a
        /// not-equals one (by name, should a Revit year add it). Anything else: null (unreadable).
        /// </summary>
        public static string RuleShape(bool inverted, string ruleType, string evaluatorType)
        {
            if (ruleType != "FilterStringRule" || string.IsNullOrEmpty(evaluatorType)) return null;
            if (inverted && evaluatorType == "FilterStringEquals") return NotEqualsShape;
            if (!inverted && evaluatorType == "FilterStringNotEquals") return NotEqualsShape;
            return null;
        }

        /// <summary>Why the rules read back are not the ones written; null when they are.</summary>
        public static string RulesReadBackError(IList<string> values, IList<string> wanted)
        {
            if (values == null) return "the rules read back are unreadable (not a 'Type Name not equal to' shape this version recognises).";
            return SameNames(values, wanted) ? null : "the rule values read back (" + string.Join(", ", values) + ") are not the host type names written.";
        }

        /// <summary>The rule values read back are exactly the names asked: same set (case-insensitive), no extra, no repeat.</summary>
        public static bool SameNames(IList<string> got, IList<string> wanted)
        {
            if (got == null || wanted == null) return false;
            var a = new HashSet<string>(got, StringComparer.OrdinalIgnoreCase);
            return a.Count == got.Count && a.SetEquals(wanted);
        }

        /// <summary>
        /// Does the link take the host view's filters? visibilityType: null (no override = by host view), ByHostView,
        /// ByLinkView, Custom. viewFilterType (2025+): ByHostView/ByLinkView/None or null when not readable.
        /// True: it does; false: it does not; null: not known (Custom on 2024, where the filter switch is not readable).
        /// </summary>
        public static bool? TakesHostFilters(string visibilityType, string viewFilterType)
        {
            if (string.IsNullOrEmpty(visibilityType) || visibilityType == "ByHostView") return true;
            if (visibilityType == "ByLinkView") return false;
            if (string.IsNullOrEmpty(viewFilterType)) return null;
            return viewFilterType == "ByHostView";
        }

        /// <summary>Why the filter cannot hide this linked datum; null when it does.</summary>
        public static string Unresolved(string typeName, ICollection<string> hostNames, bool? takesHostFilters)
        {
            if (hostNames == null || hostNames.Count == 0) return NoHostNames;
            if (string.IsNullOrWhiteSpace(typeName)) return Unreadable;
            if (hostNames.Any(h => string.Equals(h, typeName.Trim(), StringComparison.OrdinalIgnoreCase))) return SameName;
            if (takesHostFilters == false) return Bypass;
            if (takesHostFilters == null) return BypassUnknown;
            return null;
        }

        // ---- template-controlled views (5.2.1) -------------------------------------------------------------------
        // The refusals below never end in "Nothing was written": ModelEditRunner (CommandResult.RefuseBeforeWrite) adds it once.
        public const string TemplateRefuse = "refuse";
        public const string TemplateDuplicateView = "duplicate_view";
        public const string TemplateEdit = "edit_template";
        public static readonly string[] TemplateModes = { TemplateRefuse, TemplateDuplicateView, TemplateEdit };

        /// <summary>At most this many views that use a template are listed in a rehearsal (the count is always exact).</summary>
        public const int MaxListedTemplateUsers = 200;

        /// <summary>template_mode (default refuse), or null with the refusal in <paramref name="error"/>.</summary>
        public static string ParseTemplateMode(JToken token, out string error)
        {
            error = null;
            if (token == null || token.Type == JTokenType.Null) return TemplateRefuse;
            string s = token.Type == JTokenType.String ? ((string)token).Trim().ToLowerInvariant() : null;
            if (s != null && TemplateModes.Contains(s)) return s;
            error = "template_mode must be one of: " + string.Join(", ", TemplateModes) + ".";
            return null;
        }

        /// <summary>
        /// The refusal for a view whose Filters come from a template. It names both ways out and what each one costs: a missing
        /// choice is the caller's to make, not a dead end.
        /// </summary>
        public static string TemplateRefusal(string view, string template, long templateId) =>
            "view '" + view + "' takes its filters from template '" + template + "' (id " + templateId + "): a filter added to the view would be ignored. " +
            "Choose with template_mode: duplicate_view (makes a NEW view in the browser, a copy of this one with its detailing, with the template released " +
            "and its settings applied once; the copy is not placed on any sheet and later changes to the template do not reach it; this view and the template are not changed) " +
            "or edit_template (adds the filter to the template, only when the template and every view that uses it are ours; every one of those views changes).";

        /// <summary>The refusal of edit_template on a template that is not ours (HrzOwnership gate: a mark of this user).</summary>
        public static string ForeignTemplateRefusal(string template, long templateId, string reason) =>
            "template '" + template + "' (" + templateId + ") " + (reason ?? "is not ours") + ": edit_template only changes a template that carries this user's " +
            "Horizun mark (or one created in this session). Use template_mode=duplicate_view to work on a copy of the view.";

        /// <summary>The refusal of edit_template when views that use the template are not ours: the edit would change them too.</summary>
        public static string ForeignTemplateUsersRefusal(string template, long templateId, int total, IList<string> foreign, int foreignTotal) =>
            "template '" + template + "' (" + templateId + ") is used by " + total + " view(s), and " + foreignTotal + " of them are not ours (" +
            string.Join(", ", (foreign ?? new List<string>()).Take(20)) + (foreignTotal > 20 ? " and " + (foreignTotal - 20) + " more" : "") +
            "): editing the template would change somebody else's views. Use template_mode=duplicate_view to work on a copy of the view.";

        /// <summary>name_separator on a template would hide by NAME in every view that uses it: refused, the copy is the way.</summary>
        public const string NameSeparatorOnTemplateRefusal =
            "name_separator cannot be combined with template_mode=edit_template: a Name rule on a template hides, in every view that uses it, any host grid or level " +
            "renamed or added later. Run edit_template without name_separator, or use template_mode=duplicate_view for the name rule on a copy.";

        /// <summary>
        /// Is <paramref name="userId"/> a view that would make rewriting an existing filter change somebody else's view? The target itself
        /// is never one, and when the target is a TEMPLATE neither is a view governed by it: it shows the template's filters, so reading the
        /// filter through it is the template's own use (repeating edit_template must not refuse itself).
        /// </summary>
        public static bool OtherFilterUser(long userId, long userTemplateId, bool usesFilter, long targetId, bool targetIsTemplate)
        {
            if (!usesFilter || userId == targetId) return false;
            if (targetIsTemplate && userTemplateId == targetId) return false;
            return true;
        }

        /// <summary>The name of the working copy: unique among <paramref name="existing"/> (Revit view names are unique, case-insensitively).</summary>
        public static string CopyName(string source, IEnumerable<string> existing)
        {
            var taken = new HashSet<string>(existing ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string baseName = source + " - Horizun datums";
            if (!taken.Contains(baseName)) return baseName;
            for (int i = 2; i < 10000; i++) if (!taken.Contains(baseName + " (" + i + ")")) return baseName + " (" + i + ")";
            return baseName + " (" + Guid.NewGuid().ToString("N").Substring(0, 8) + ")";
        }

        /// <summary>What the rehearsal says about the copy's name: chosen at apply, never fixed by the rehearsal (a retry may find it taken).</summary>
        public static string CopyNameAtApply(string source) =>
            "chosen at apply: '" + source + " - Horizun datums', or the first free ' (n)' suffix if that name is taken then";

        /// <summary>The settings the duplicate_view re-read does not compare (said as a SUPUESTO, never as verified).</summary>
        public static readonly string[] DuplicateViewUnmeasured =
        {
            "graphic overrides (colours, lines, patterns) of categories and filters", "view range and crop", "phase and phase filter",
            "underlay", "visual style and other graphic display options", "per-link display settings"
        };

        /// <summary>The SUPUESTO rows of duplicate_view: what the copy is, and what the re-read does not measure.</summary>
        public static List<JObject> DuplicateViewAssumptions(string template, IEnumerable<string> sheets) => new List<JObject>
        {
            AssumptionNotes.Row("the copy is a new view in the project browser and is NOT placed on any sheet" +
                                (sheets != null && sheets.Any() ? " (the original stays on " + string.Join(", ", sheets) + ")" : ""),
                "a view can be on one sheet only and the original keeps its place; the copy is a working view",
                "place the copy with horizun_sheet_layout if it should replace the original on its sheet"),
            AssumptionNotes.Row("the copy has no view template: template '" + template + "' is released and its settings are applied to the copy once; later changes to the template do not reach the copy",
                "the template sets the Filters row, so a filter added under it would be ignored",
                "use template_mode=edit_template to change the template itself (only when it and every view that uses it are ours)"),
            AssumptionNotes.Row("these settings were not compared between the copy and the original and are assumed equal: " + string.Join(", ", DuplicateViewUnmeasured),
                "the re-read compares scale, detail level, filters with their visibility and hidden categories only",
                "compare the two views with horizun_capture_view")
        };

        /// <summary>The SUPUESTO row of the Type Name filter, in a coordinator's words: a new or renamed host type falls under it.</summary>
        public static JObject TypeFilterAssumption(string kind)
        {
            string what = kind == ViewDatumRules.KindGrid ? "grid" : "level";
            return AssumptionNotes.Row(
                "the host's " + what + " types today are all the types this filter keeps visible",
                "the filter hides every " + what + " whose type is not one of the host's " + what + " types now. If someone creates a new " + what +
                " type in the host (or renames one) and uses it, those host " + what + "s will be hidden in this view until link_datum_cleanup is run again",
                "run link_datum_cleanup again after adding or renaming " + what + " types");
        }

        /// <summary>The SUPUESTO row of the Name filter, in a coordinator's words: a renamed or new host datum falls under it.</summary>
        public static JObject NameFilterAssumption(string kind)
        {
            string what = kind == ViewDatumRules.KindGrid ? "grid" : "level";
            return AssumptionNotes.Row(
                "the host's " + what + " names today are all the names the name filter keeps visible",
                "the name filter hides every " + what + " whose name is not one of the host's " + what + " names now. If someone renames a host " + what +
                " or creates a new one, it will be hidden in this view until link_datum_cleanup is run again (a new " + what + " type falls under the type filter the same way)",
                "run link_datum_cleanup again after renaming or adding " + what + "s or " + what + " types");
        }

        // ---- the datum NAME as a second separator (5.2.1) ---------------------------------------------------------
        /// <summary>One name filter per kind and view, beside the type-name one.</summary>
        public static string NameFilterName(string kind, long viewId) =>
            "Horizun linked " + (kind == ViewDatumRules.KindGrid ? "grids" : "levels") + " hidden by name - view " + viewId;

        /// <summary>
        /// The host datum names a "Name not equal to" rule would keep; null when the rule would not be safe: a host datum
        /// with an empty name or one with edge spaces (the rule on the real text would not match the trimmed one).
        /// </summary>
        public static List<string> HostDatumNames(IEnumerable<string> allHostNames)
        {
            var all = (allHostNames ?? Enumerable.Empty<string>()).ToList();
            if (all.Count == 0 || all.Any(n => string.IsNullOrWhiteSpace(n) || n != n.Trim())) return null;
            return all.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Would the name rule hide this linked datum: a readable name that no host datum carries.</summary>
        public static bool NameSeparates(string linkedName, ICollection<string> hostNames) =>
            !string.IsNullOrWhiteSpace(linkedName) && linkedName == linkedName.Trim() && hostNames != null && hostNames.Count > 0 &&
            !hostNames.Any(h => string.Equals(h, linkedName, StringComparison.OrdinalIgnoreCase));

        /// <summary>How one linked datum is counted: by the type filter, ONLY by the name filter, or unresolved. Never by both.</summary>
        public enum DatumOutcome { ByType, ByNameOnly, Unresolved }

        /// <summary>
        /// <paramref name="typeReason"/>: why the type filter cannot hide the datum (null: it does). <paramref name="nameHides"/>: the name
        /// rule would hide it. A datum the type filter already hides is never counted again for the name filter.
        /// </summary>
        public static DatumOutcome Classify(string typeReason, bool nameHides)
        {
            if (typeReason == null) return DatumOutcome.ByType;
            return typeReason == SameName && nameHides ? DatumOutcome.ByNameOnly : DatumOutcome.Unresolved;
        }

        /// <summary>
        /// Does a NESTED link take the host view's filters, as far as the API says? The parent link's own answer first; then, for a parent
        /// shown Custom, its NestedLinks setting (2025+): ByHostView ("by parent link") takes them, ByLinkView does not, unreadable is null.
        /// A prediction only: whether Revit really applies a host filter to a nested link's datums is not measured.
        /// </summary>
        public static bool? NestedTakesHostFilters(bool? parentTakes, string parentVisibility, string nestedLinks)
        {
            if (parentTakes != true) return parentTakes;
            if (string.IsNullOrEmpty(parentVisibility) || parentVisibility == "ByHostView") return true;
            if (nestedLinks == "ByHostView") return true;
            if (nestedLinks == "ByLinkView") return false;
            return null;
        }

        /// <summary>Linked documents read in ONE call: the top links and every nested one together (never per link), each document once.</summary>
        public const int MaxNestedDocuments = 400;
        public const int MaxNestedDepth = 4;

        /// <summary>
        /// The bounded walk of linked documents for one call. A top link's document is always read (each instance is drawn on its own);
        /// a nested document is read once for the whole call, and none past the global cap (which sets <see cref="Truncated"/>).
        /// </summary>
        public sealed class NestedBudget
        {
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int Documents { get; private set; }
            public bool Truncated { get; private set; }
            public void AddTop(string key) { if (_seen.Add(key)) Documents++; }
            public bool TryAdd(string key)
            {
                if (_seen.Contains(key)) return false;
                if (Documents >= MaxNestedDocuments) { Truncated = true; return false; }
                _seen.Add(key); Documents++;
                return true;
            }
        }

        /// <summary>The full chain of links down to a nested document, from the top link: "A (link instance 12) > B (link instance 7)".</summary>
        public static string LinkPath(IEnumerable<KeyValuePair<string, long>> chain) =>
            string.Join(" > ", (chain ?? Enumerable.Empty<KeyValuePair<string, long>>()).Select(s => s.Key + " (link instance " + s.Value + ")"));

        /// <summary>
        /// What duplicate_view compares between the original (as it is drawn: a setting the template controls is read from the template)
        /// and the copy. Filters: filter id -> state (<see cref="FilterState"/>). A null scale or detail level was not readable and never matches.
        /// </summary>
        public sealed class ViewLook
        {
            public int? Scale;
            public string DetailLevel;
            public Dictionary<long, string> Filters = new Dictionary<long, string>();
            public HashSet<long> HiddenCategories = new HashSet<long>();
        }

        /// <summary>A filter's state in a view, as compared: its visibility and whether it is enabled.</summary>
        public static string FilterState(bool visible, bool? enabled) =>
            (visible ? "visible" : "hidden") + "|" + (enabled == null ? "enabled_unreadable" : enabled.Value ? "enabled" : "disabled");

        /// <summary>
        /// The differences between two filter sets (which filters, and the state of each); empty when equal. <paramref name="ignore"/>:
        /// the filters this call added, which exist on one side only by design.
        /// </summary>
        public static List<string> FilterDifferences(IDictionary<long, string> before, IDictionary<long, string> after, ICollection<long> ignore = null)
        {
            var diffs = new List<string>();
            before = before ?? new Dictionary<long, string>(); after = after ?? new Dictionary<long, string>();
            foreach (var kv in before.OrderBy(k => k.Key))
            {
                if (ignore != null && ignore.Contains(kv.Key)) continue;
                if (!after.TryGetValue(kv.Key, out string now)) diffs.Add("filter " + kv.Key + " missing");
                else if (now != kv.Value) diffs.Add("filter " + kv.Key + ": " + kv.Value + " -> " + now);
            }
            foreach (var kv in after.OrderBy(k => k.Key))
                if (!before.ContainsKey(kv.Key) && (ignore == null || !ignore.Contains(kv.Key))) diffs.Add("filter " + kv.Key + " added");
            return diffs;
        }

        /// <summary>The differences between the original's look and the copy's: scale, detail level, filters, hidden categories.</summary>
        public static List<string> LookDifferences(ViewLook original, ViewLook copy, ICollection<long> ignoreFilters)
        {
            var diffs = new List<string>();
            if (original == null || copy == null) { diffs.Add("a view could not be read"); return diffs; }
            if (original.Scale == null || copy.Scale == null) diffs.Add("scale unreadable");
            else if (original.Scale != copy.Scale) diffs.Add("scale 1:" + original.Scale + " -> 1:" + copy.Scale);
            if (original.DetailLevel == null || copy.DetailLevel == null) diffs.Add("detail level unreadable");
            else if (original.DetailLevel != copy.DetailLevel) diffs.Add("detail level " + original.DetailLevel + " -> " + copy.DetailLevel);
            diffs.AddRange(FilterDifferences(original.Filters, copy.Filters, ignoreFilters));
            foreach (long c in original.HiddenCategories.Where(c => !copy.HiddenCategories.Contains(c)).OrderBy(c => c)) diffs.Add("category " + c + " shown in the copy");
            foreach (long c in copy.HiddenCategories.Where(c => !original.HiddenCategories.Contains(c)).OrderBy(c => c)) diffs.Add("category " + c + " hidden in the copy");
            return diffs;
        }

        public static string Advice(string reason)
        {
            switch (reason)
            {
                case SameName: return "the link uses a host type name; rename the type in the link (or the host's) so a Type Name rule can tell them apart, or switch the link's Grids/Levels off by hand (V/G > Revit Links > Custom) and spread it with copy_link_overrides";
                case NoHostNames: return "the host has no type of this kind, so no rule can keep host datums";
                case Unreadable: return "the linked datum's type name could not be read";
                case Bypass: return "this link shows By linked view or with its own filters, so host filters do not reach it; set it By host view or use copy_link_overrides";
                case BypassUnknown: return "this link is Custom and Revit 2024 does not say whether it takes the host's filters; check with horizun_capture_view";
                default: return null;
            }
        }
    }
}
