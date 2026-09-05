// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// VIEWS, WHERE NOT EVERY VIEW HAS EVERY PROPERTY.
//
// The mistake this file exists to prevent is treating one view like another. A
// legend has no level. A schedule has no crop box. A drafting view has no view
// range. A tool that asks all of them the same questions produces a report full
// of failures that describe Revit rather than the model - and the reader learns
// to ignore it, which costs more than never having checked.
//
// So APPLICABILITY IS DECLARED PER VIEW TYPE, and a property that does not
// apply is `not_applicable`: a fifth answer, distinct from all of:
//
//   not_requested   the profile asked nothing about this property
//   not_readable    the read threw; the value is unknown
//   ok              a rule ran and the view satisfies it
//   failed          a rule ran and the view does not
//
// A TEMPLATE IS NOT A VIEW WITHOUT A TEMPLATE. Counting templates among the
// views that lack one is the single most common way this area produces a large,
// confident, meaningless number.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ViewPropertyStatus
    {
        public const string Ok = "ok";
        public const string Failed = "failed";
        public const string NotRequested = "not_requested";
        public const string NotApplicable = "not_applicable";
        public const string NotReadable = "not_readable";
    }

    public static class ViewProperties
    {
        public const string Template = "template";
        public const string Scale = "scale";
        public const string DetailLevel = "detail_level";
        public const string Discipline = "discipline";
        public const string Phase = "phase";
        public const string PhaseFilter = "phase_filter";
        public const string CropActive = "crop_active";
        public const string ScopeBox = "scope_box";
        public const string Level = "level";
        public const string ViewRange = "view_range";
        public const string Filters = "filters";
        public const string OnSheet = "on_sheet";

        public static readonly string[] All =
        {
            Template, Scale, DetailLevel, Discipline, Phase, PhaseFilter,
            CropActive, ScopeBox, Level, ViewRange, Filters, OnSheet
        };
    }

    /// <summary>
    /// Which properties a view type actually has.
    ///
    /// This is the table that stops a legend being reported as a view with no
    /// level. It is deliberately explicit rather than inferred: inferring
    /// applicability from whether a read returned null is exactly the confusion
    /// between "does not apply" and "could not be read".
    /// </summary>
    public static class ViewApplicability
    {
        private static readonly HashSet<string> Plans =
            new HashSet<string>(StringComparer.Ordinal) { "FloorPlan", "CeilingPlan", "EngineeringPlan", "AreaPlan" };

        private static readonly HashSet<string> Graphical =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "FloorPlan", "CeilingPlan", "EngineeringPlan", "AreaPlan",
                "Elevation", "Section", "Detail", "ThreeD", "DraftingView", "Rendering", "Walkthrough"
            };

        private static readonly HashSet<string> Croppable =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "FloorPlan", "CeilingPlan", "EngineeringPlan", "AreaPlan",
                "Elevation", "Section", "Detail", "ThreeD"
            };

        /// <summary>
        /// View types that are Revit's own furniture, not somebody's drawing. They
        /// are excluded from the census entirely rather than reported as views
        /// failing every rule.
        /// </summary>
        public static readonly HashSet<string> Internal =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ProjectBrowser", "SystemBrowser", "Internal", "Undefined", "Report",
                "SystemsAnalysisReport", "CostReport", "LoadsReport", "PresureLossReport"
            };

        public static bool IsInternal(string viewType)
        {
            return viewType != null && Internal.Contains(viewType);
        }

        public static bool Applies(string property, string viewType)
        {
            if (viewType == null) return false;
            switch (property)
            {
                // A schedule and a legend both take a template; a legend takes a
                // scale. Neither takes a crop, a level or a view range.
                case ViewProperties.Template:
                case ViewProperties.OnSheet:
                    return !IsInternal(viewType);

                case ViewProperties.Scale:
                    return Graphical.Contains(viewType) || viewType == "Legend";

                case ViewProperties.DetailLevel:
                case ViewProperties.Discipline:
                case ViewProperties.Filters:
                    return Graphical.Contains(viewType);

                case ViewProperties.Phase:
                case ViewProperties.PhaseFilter:
                    return Graphical.Contains(viewType);

                case ViewProperties.CropActive:
                    return Croppable.Contains(viewType);

                case ViewProperties.ScopeBox:
                    return Plans.Contains(viewType) || viewType == "Section" || viewType == "Elevation";

                case ViewProperties.Level:
                case ViewProperties.ViewRange:
                    return Plans.Contains(viewType);

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// One view as the model reports it. Nothing here is judged.
    ///
    /// Named ViewStateFact rather than ViewFact because Core/PlanimetryFacts
    /// already owns that name for a different thing - the planimetry row. Two
    /// types called ViewFact in one namespace is a compile error today and a
    /// silent mis-read the day one of them moves.
    /// </summary>
    public sealed class ViewStateFact
    {
        public long ElementId;
        public string UniqueId;
        public string Name;
        public bool NameReadable = true;
        public string ViewType;
        public bool IsTemplate;

        public string TemplateName;
        public bool TemplateAssigned;
        public string LevelName;
        public int? Scale;
        public string DetailLevel;
        public string Discipline;
        public string Phase;
        public string PhaseFilter;
        public bool? CropActive;
        public bool? CropVisible;
        public bool? AnnotationCrop;
        public string ScopeBox;
        /// <summary>Project north or true north, from PLAN_VIEW_NORTH. Null when unread.</summary>
        public string NorthOrientation;
        public int? ViewRangeReadable;      // null when not applicable/unreadable
        public List<string> Filters = new List<string>();
        public int OverriddenCategories;
        public int HiddenCategories;
        public bool? IsDependent;
        public string PrimaryViewName;
        public bool PlacedOnSheet;
        public string SheetNumber;

        /// <summary>Properties whose read threw, by name. Never silently null.</summary>
        public HashSet<string> Unreadable = new HashSet<string>(StringComparer.Ordinal);
    }

    public static class ViewProfileCodes
    {
        public const string NoVersion = "view_profile_no_version";
        public const string UnknownViewType = "view_profile_unknown_view_type";
        public const string UnknownKey = "view_profile_unknown_key";
        public const string BadRule = "view_profile_bad_rule";
        public const string Absent = "view_profile_absent";
    }

    public sealed class ViewRule
    {
        public bool? TemplateRequired;
        public List<string> AllowedTemplates;
        public List<int> AllowedScales;
        public string ExpectedDetailLevel;
        public string ExpectedDiscipline;
        public string ExpectedPhase;
        public string ExpectedPhaseFilter;
        public bool? CropRequired;
        public bool? ScopeBoxRequired;
        public List<string> RequiredFilters;
        public List<string> ForbiddenFilters;
        public bool? OnSheetRequired;
        public HashSet<string> Exceptions = new HashSet<string>(StringComparer.Ordinal);
    }

    public sealed class ViewProfile
    {
        public bool Ok;
        public bool Absent;
        public string Code;
        public string Message;
        public string Version;
        public Dictionary<string, ViewRule> ByViewType = new Dictionary<string, ViewRule>(StringComparer.Ordinal);
    }

    public sealed class ViewPropertyVerdict
    {
        public string Property;
        public string Status;
        public string Detail;
    }

    public static class ViewFactsRules
    {
        public const string ApplicabilityMeans =
            "not every view has every property: a legend has no level, a schedule has no crop, a drafting view " +
            "has no view range. A property that does not apply is not_applicable, which is NOT a pass and NOT " +
            "a failure - and it is a different answer again from not_readable, where the property exists and " +
            "the read threw.";

        public const string TemplatesMean =
            "a view TEMPLATE is not a view without a template. Templates are reported as their own population " +
            "and never counted among the views that lack one, which is the usual way this area produces a " +
            "large and meaningless number.";

        /// <summary>
        /// Every view type named in the profile must be one Revit has. A rule filed
        /// under a misspelt type never runs and reports every view as acceptable.
        /// </summary>
        public static ViewProfile Read(JToken token, IEnumerable<string> knownViewTypes)
        {
            var p = new ViewProfile();
            if (token == null || token.Type == JTokenType.Null)
            {
                p.Absent = true;
                p.Code = ViewProfileCodes.Absent;
                p.Message = "no view profile was supplied, so every property of every view is not_requested. " +
                            "That is NOT a pass: which scale a section should use is one organisation's " +
                            "decision and none is compiled in here.";
                return p;
            }

            var o = token as JObject;
            if (o == null)
            {
                p.Code = ViewProfileCodes.BadRule;
                p.Message = "the view profile must be an object of view type to rules.";
                return p;
            }

            JToken v = o["version"];
            if (v == null || string.IsNullOrWhiteSpace(v.Value<string>()))
            {
                p.Code = ViewProfileCodes.NoVersion;
                p.Message = "the view profile needs a 'version'.";
                return p;
            }
            p.Version = v.Value<string>();

            var known = new HashSet<string>(knownViewTypes ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (JProperty prop in o.Properties())
            {
                if (prop.Name == "version") continue;

                if (known.Count > 0 && !known.Contains(prop.Name))
                {
                    p.Code = ViewProfileCodes.UnknownViewType;
                    p.Message = "'" + prop.Name + "' is not a view type this Revit has. A rule filed under a " +
                                "name nothing matches never runs, and the report reads as a clean pass of a " +
                                "rule nobody applied.";
                    return p;
                }

                var body = prop.Value as JObject;
                if (body == null)
                {
                    p.Code = ViewProfileCodes.BadRule;
                    p.Message = "the rules for '" + prop.Name + "' must be an object.";
                    return p;
                }

                var rule = new ViewRule();
                foreach (JProperty r in body.Properties())
                {
                    switch (r.Name)
                    {
                        case "template_required": rule.TemplateRequired = Bool(r, p); break;
                        case "crop_required": rule.CropRequired = Bool(r, p); break;
                        case "scope_box_required": rule.ScopeBoxRequired = Bool(r, p); break;
                        case "on_sheet_required": rule.OnSheetRequired = Bool(r, p); break;
                        case "allowed_templates": rule.AllowedTemplates = Strings(r, p); break;
                        case "required_filters": rule.RequiredFilters = Strings(r, p); break;
                        case "forbidden_filters": rule.ForbiddenFilters = Strings(r, p); break;
                        case "expected_detail_level": rule.ExpectedDetailLevel = Str(r, p); break;
                        case "expected_discipline": rule.ExpectedDiscipline = Str(r, p); break;
                        case "expected_phase": rule.ExpectedPhase = Str(r, p); break;
                        case "expected_phase_filter": rule.ExpectedPhaseFilter = Str(r, p); break;
                        case "exceptions":
                        {
                            List<string> ex = Strings(r, p);
                            if (ex != null) foreach (string e in ex) rule.Exceptions.Add(e);
                            break;
                        }
                        case "allowed_scales":
                        {
                            var arr = r.Value as JArray;
                            if (arr == null) { Bad(p, "'allowed_scales' must be an array of whole numbers."); return p; }
                            rule.AllowedScales = new List<int>();
                            foreach (JToken t in arr)
                            {
                                if (t.Type != JTokenType.Integer || t.Value<int>() <= 0)
                                {
                                    Bad(p, "'allowed_scales' entries must be whole numbers above zero.");
                                    return p;
                                }
                                rule.AllowedScales.Add(t.Value<int>());
                            }
                            break;
                        }
                        default:
                            p.Code = ViewProfileCodes.UnknownKey;
                            p.Message = "'" + r.Name + "' is not a rule this contract defines for a view type.";
                            return p;
                    }
                    if (p.Code != null) return p;
                }
                p.ByViewType[prop.Name] = rule;
            }

            p.Ok = true;
            return p;
        }

        private static void Bad(ViewProfile p, string message)
        {
            p.Code = ViewProfileCodes.BadRule;
            p.Message = message;
        }

        private static bool? Bool(JProperty r, ViewProfile p)
        {
            if (r.Value.Type != JTokenType.Boolean) { Bad(p, "'" + r.Name + "' must be true or false."); return null; }
            return r.Value.Value<bool>();
        }

        private static string Str(JProperty r, ViewProfile p)
        {
            if (r.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace(r.Value.Value<string>()))
            { Bad(p, "'" + r.Name + "' must be a non-empty string."); return null; }
            return r.Value.Value<string>();
        }

        private static List<string> Strings(JProperty r, ViewProfile p)
        {
            var arr = r.Value as JArray;
            if (arr == null) { Bad(p, "'" + r.Name + "' must be an array of names."); return null; }
            var list = new List<string>();
            foreach (JToken t in arr)
            {
                if (t.Type != JTokenType.String) { Bad(p, "'" + r.Name + "' must contain only names."); return null; }
                list.Add(t.Value<string>());
            }
            return list;
        }

        /// <summary>
        /// Judges one view, property by property. A view TEMPLATE is never judged:
        /// it is not a drawing and the rules are about drawings.
        /// </summary>
        public static List<ViewPropertyVerdict> Judge(ViewStateFact f, ViewProfile p)
        {
            var verdicts = new List<ViewPropertyVerdict>();
            if (f == null) return verdicts;

            ViewRule rule = null;
            bool excepted = false;
            if (p != null && p.Ok && f.ViewType != null && p.ByViewType.TryGetValue(f.ViewType, out rule))
                excepted = f.Name != null && rule.Exceptions.Contains(f.Name);
            if (excepted) rule = null;

            foreach (string prop in ViewProperties.All)
            {
                if (!ViewApplicability.Applies(prop, f.ViewType))
                {
                    verdicts.Add(V(prop, ViewPropertyStatus.NotApplicable,
                        "a " + (f.ViewType ?? "view of unknown type") + " has no " + prop.Replace('_', ' ') + "."));
                    continue;
                }
                if (f.Unreadable.Contains(prop))
                {
                    verdicts.Add(V(prop, ViewPropertyStatus.NotReadable,
                        "the read threw, so this property is unknown - not absent and not satisfied."));
                    continue;
                }
                if (rule == null)
                {
                    verdicts.Add(V(prop, ViewPropertyStatus.NotRequested,
                        excepted ? "this view is an explicit exception in your profile."
                                 : "no rule was supplied for this property."));
                    continue;
                }
                verdicts.Add(Check(prop, f, rule));
            }
            return verdicts;
        }

        private static ViewPropertyVerdict Check(string prop, ViewStateFact f, ViewRule r)
        {
            switch (prop)
            {
                case ViewProperties.Template:
                    if (r.TemplateRequired == true && !f.TemplateAssigned)
                        return V(prop, ViewPropertyStatus.Failed, "no view template is assigned.");
                    if (r.AllowedTemplates != null && f.TemplateAssigned &&
                        !r.AllowedTemplates.Contains(f.TemplateName ?? ""))
                        return V(prop, ViewPropertyStatus.Failed,
                            "template '" + f.TemplateName + "' is not in your allowed list.");
                    if (r.TemplateRequired == null && r.AllowedTemplates == null)
                        return V(prop, ViewPropertyStatus.NotRequested, "no rule was supplied for this property.");
                    return V(prop, ViewPropertyStatus.Ok, f.TemplateName);

                case ViewProperties.Scale:
                    if (r.AllowedScales == null)
                        return V(prop, ViewPropertyStatus.NotRequested, "no rule was supplied for this property.");
                    if (!f.Scale.HasValue)
                        return V(prop, ViewPropertyStatus.NotReadable, "the view reports no scale.");
                    return r.AllowedScales.Contains(f.Scale.Value)
                        ? V(prop, ViewPropertyStatus.Ok, "1:" + f.Scale.Value)
                        : V(prop, ViewPropertyStatus.Failed, "1:" + f.Scale.Value + " is not an allowed scale.");

                case ViewProperties.DetailLevel:
                    return Expect(prop, r.ExpectedDetailLevel, f.DetailLevel);
                case ViewProperties.Discipline:
                    return Expect(prop, r.ExpectedDiscipline, f.Discipline);
                case ViewProperties.Phase:
                    return Expect(prop, r.ExpectedPhase, f.Phase);
                case ViewProperties.PhaseFilter:
                    return Expect(prop, r.ExpectedPhaseFilter, f.PhaseFilter);

                case ViewProperties.CropActive:
                    if (r.CropRequired == null)
                        return V(prop, ViewPropertyStatus.NotRequested, "no rule was supplied for this property.");
                    if (!f.CropActive.HasValue)
                        return V(prop, ViewPropertyStatus.NotReadable, "the crop state could not be read.");
                    return f.CropActive.Value == r.CropRequired.Value
                        ? V(prop, ViewPropertyStatus.Ok, f.CropActive.Value ? "cropped" : "not cropped")
                        : V(prop, ViewPropertyStatus.Failed,
                            r.CropRequired.Value ? "the crop is not active." : "the crop is active.");

                case ViewProperties.ScopeBox:
                    if (r.ScopeBoxRequired == null)
                        return V(prop, ViewPropertyStatus.NotRequested, "no rule was supplied for this property.");
                    return string.IsNullOrEmpty(f.ScopeBox) == r.ScopeBoxRequired.Value
                        ? V(prop, ViewPropertyStatus.Failed,
                            r.ScopeBoxRequired.Value ? "no scope box is assigned." : "a scope box is assigned.")
                        : V(prop, ViewPropertyStatus.Ok, f.ScopeBox);

                case ViewProperties.Filters:
                {
                    if (r.RequiredFilters == null && r.ForbiddenFilters == null)
                        return V(prop, ViewPropertyStatus.NotRequested, "no rule was supplied for this property.");
                    var missing = (r.RequiredFilters ?? new List<string>())
                        .Where(x => !f.Filters.Contains(x)).ToList();
                    var present = (r.ForbiddenFilters ?? new List<string>())
                        .Where(x => f.Filters.Contains(x)).ToList();
                    if (missing.Count == 0 && present.Count == 0)
                        return V(prop, ViewPropertyStatus.Ok, f.Filters.Count + " filter(s)");
                    return V(prop, ViewPropertyStatus.Failed,
                        (missing.Count > 0 ? "missing: " + string.Join(", ", missing) + ". " : "") +
                        (present.Count > 0 ? "forbidden and present: " + string.Join(", ", present) + "." : ""));
                }

                case ViewProperties.OnSheet:
                    if (r.OnSheetRequired == null)
                        return V(prop, ViewPropertyStatus.NotRequested, "no rule was supplied for this property.");
                    return f.PlacedOnSheet == r.OnSheetRequired.Value
                        ? V(prop, ViewPropertyStatus.Ok, f.SheetNumber)
                        : V(prop, ViewPropertyStatus.Failed,
                            r.OnSheetRequired.Value ? "this view is on no sheet." : "this view is on a sheet.");

                case ViewProperties.Level:
                    return V(prop, ViewPropertyStatus.NotRequested,
                        "level is reported as a fact; no rule kind is defined for it.");
                case ViewProperties.ViewRange:
                    return V(prop, ViewPropertyStatus.NotRequested,
                        "view range is reported as a fact; no rule kind is defined for it.");
            }
            return V(prop, ViewPropertyStatus.NotRequested, "no rule was supplied for this property.");
        }

        private static ViewPropertyVerdict Expect(string prop, string expected, string actual)
        {
            if (expected == null)
                return V(prop, ViewPropertyStatus.NotRequested, "no rule was supplied for this property.");
            if (actual == null)
                return V(prop, ViewPropertyStatus.NotReadable, "the view reports no value for this property.");
            return string.Equals(expected, actual, StringComparison.Ordinal)
                ? V(prop, ViewPropertyStatus.Ok, actual)
                : V(prop, ViewPropertyStatus.Failed, "'" + actual + "' where you expect '" + expected + "'.");
        }

        private static ViewPropertyVerdict V(string prop, string status, string detail)
        {
            return new ViewPropertyVerdict { Property = prop, Status = status, Detail = detail };
        }

        public static JObject ToJson(ViewStateFact f, List<ViewPropertyVerdict> verdicts)
        {
            if (f == null) return null;
            var props = new JObject();
            foreach (ViewPropertyVerdict v in verdicts ?? new List<ViewPropertyVerdict>())
                props[v.Property] = new JObject { ["status"] = v.Status, ["detail"] = v.Detail };

            return new JObject
            {
                ["view_id"] = f.ElementId,
                ["unique_id"] = f.UniqueId,
                ["name"] = f.Name,
                ["name_readable"] = f.NameReadable,
                ["view_type"] = f.ViewType,
                ["is_template"] = f.IsTemplate,
                ["template_assigned"] = f.TemplateAssigned,
                ["template_name"] = f.TemplateName,
                ["level"] = f.LevelName,
                ["scale"] = f.Scale,
                ["detail_level"] = f.DetailLevel,
                ["discipline"] = f.Discipline,
                ["phase"] = f.Phase,
                ["phase_filter"] = f.PhaseFilter,
                ["crop_active"] = f.CropActive,
                ["crop_visible"] = f.CropVisible,
                ["annotation_crop"] = f.AnnotationCrop,
                ["scope_box"] = f.ScopeBox,
                ["north_orientation"] = f.NorthOrientation,
                ["filters"] = new JArray(f.Filters.Select(x => (JToken)x)),
                ["overridden_categories"] = f.OverriddenCategories,
                ["hidden_categories"] = f.HiddenCategories,
                ["is_dependent"] = f.IsDependent,
                ["primary_view"] = f.PrimaryViewName,
                ["placed_on_sheet"] = f.PlacedOnSheet,
                ["sheet_number"] = f.SheetNumber,
                ["unreadable_properties"] = new JArray(f.Unreadable.OrderBy(x => x, StringComparer.Ordinal)
                                                        .Select(x => (JToken)x)),
                ["properties"] = props
            };
        }

        /// <summary>The five statuses, counted. None of the three empty ones is a pass.</summary>
        public static JObject Tally(IEnumerable<List<ViewPropertyVerdict>> all)
        {
            long ok = 0, failed = 0, notRequested = 0, notApplicable = 0, notReadable = 0;
            foreach (List<ViewPropertyVerdict> vs in all ?? Enumerable.Empty<List<ViewPropertyVerdict>>())
                foreach (ViewPropertyVerdict v in vs ?? new List<ViewPropertyVerdict>())
                    switch (v.Status)
                    {
                        case ViewPropertyStatus.Ok: ok++; break;
                        case ViewPropertyStatus.Failed: failed++; break;
                        case ViewPropertyStatus.NotRequested: notRequested++; break;
                        case ViewPropertyStatus.NotApplicable: notApplicable++; break;
                        case ViewPropertyStatus.NotReadable: notReadable++; break;
                    }
            return new JObject
            {
                ["ok"] = ok,
                ["failed"] = failed,
                ["not_requested"] = notRequested,
                ["not_applicable"] = notApplicable,
                ["not_readable"] = notReadable,
                ["means"] = ApplicabilityMeans
            };
        }
    }
}
