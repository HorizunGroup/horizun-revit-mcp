// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// set_view_properties (horizun_manage_views): scale, detail level, discipline, visual
// style and phase of one or several EXISTING views, as a direct operation. Until now only
// the scale (on create/duplicate/apply_template), the phase (set_phase) and the style
// (set_graphic_display) were reachable, and detail level and discipline only as a
// correction tied to a planimetry finding.
//
// This file is the Revit-free half: which fields exist, how their words map to Revit's
// enums, and the refusal that names a view TEMPLATE which governs a property. A template
// that controls a property keeps its own value when a view's setter is called - Revit
// accepts the call and nothing changes - so the answer is a typed refusal that names the
// template, never an unbinding done behind the caller's back.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class ViewPropertyRules
    {
        /// <summary>The request fields set_view_properties understands, in the order they are written.</summary>
        public static readonly string[] Fields = { "view_scale", "detail_level", "discipline", "display_style", "phase_id" };

        public const int MaxViews = 200;

        /// <summary>The BuiltInParameter names a view template must leave uncontrolled for the field to take effect.</summary>
        public static string[] TemplateParameters(string field)
        {
            switch (field)
            {
                case "view_scale": return new[] { "VIEW_SCALE", "VIEW_SCALE_PULLDOWN_METRIC", "VIEW_SCALE_PULLDOWN_IMPERIAL" };
                case "detail_level": return new[] { "VIEW_DETAIL_LEVEL" };
                case "discipline": return new[] { "VIEW_DISCIPLINE" };
                case "display_style": return new[] { "MODEL_GRAPHICS_STYLE" };
                case "phase_id": return new[] { "VIEW_PHASE" };
                default: return new string[0];
            }
        }

        /// <summary>The fields the action actually carries, in write order.</summary>
        public static List<string> Requested(Func<string, bool> has) => Fields.Where(f => has(f)).ToList();

        public static string NothingRequested() =>
            "set_view_properties changes nothing: pass at least one of " + string.Join(", ", Fields) +
            ". A no-op action in a verified batch can only be a mistake.";

        private static readonly Dictionary<string, string> Levels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["coarse"] = "Coarse", ["medium"] = "Medium", ["fine"] = "Fine"
        };

        /// <summary>The ViewDetailLevel name a word means, or null and the accepted words.</summary>
        public static string ParseDetailLevel(string word, out string error)
        {
            error = null;
            if (word != null && Levels.TryGetValue(word.Trim(), out string name)) return name;
            error = "detail_level must be coarse, medium or fine.";
            return null;
        }

        private static readonly Dictionary<string, string> Disciplines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["architectural"] = "Architectural", ["structural"] = "Structural", ["mechanical"] = "Mechanical",
            ["electrical"] = "Electrical", ["plumbing"] = "Plumbing", ["coordination"] = "Coordination"
        };

        /// <summary>The ViewDiscipline name a word means, or null and the accepted words.</summary>
        public static string ParseDiscipline(string word, out string error)
        {
            error = null;
            if (word != null && Disciplines.TryGetValue(word.Trim(), out string name)) return name;
            error = "discipline must be architectural, structural, mechanical, electrical, plumbing or coordination.";
            return null;
        }

        /// <summary>
        /// The fields a view template controls among the requested ones, or null when none is. The
        /// controlled set holds BuiltInParameter names (the template's controlled parameters).
        /// </summary>
        public static string TemplateRefusal(IEnumerable<string> requested, ISet<string> controlled, string templateName, long templateId)
        {
            if (requested == null || controlled == null || controlled.Count == 0) return null;
            var blocked = requested.Where(f => TemplateParameters(f).Any(controlled.Contains)).ToList();
            if (blocked.Count == 0) return null;
            return "view template '" + templateName + "' (" + templateId + ") controls " + string.Join(", ", blocked) +
                   ": the view would keep the template's value and the write would change nothing. Change the property " +
                   "on the template itself (the same action with view_id=" + templateId + "), apply a template that " +
                   "leaves it uncontrolled, or detach the template first with apply_template template_view_id=-1. " +
                   "The template was not detached for you. Nothing was written.";
        }

        /// <summary>What a view of this kind cannot take: sheets and schedules have no drawing scale, detail level, discipline or style.</summary>
        public static string KindRefusal(string kindName, bool isSheetOrSchedule, bool isPerspective, IEnumerable<string> requested)
        {
            if (isSheetOrSchedule)
                return "a " + kindName + " has no scale, detail level, discipline, visual style or phase of its own; " +
                       "set_view_properties works on views (plans, sections, elevations, 3D, drafting, legends) and view templates.";
            if (isPerspective && requested != null && requested.Contains("view_scale"))
                return "view_scale cannot apply to a perspective 3D view (it has no drawing scale).";
            return null;
        }

        /// <summary>The ids of one action: a single id, or a list; never both, never a repeated or an excessive one.</summary>
        public static string IdsError(bool hasSingle, bool hasKey, IList<long> list)
        {
            if (list == null)
                return hasSingle || hasKey ? null : "set_view_properties needs view_id, view_key or view_ids.";
            if (hasSingle || hasKey) return "pass view_id/view_key OR view_ids, not both.";
            if (list.Count < 1 || list.Count > MaxViews) return "view_ids must list 1.." + MaxViews + " views.";
            if (list.Distinct().Count() != list.Count) return "view_ids repeats a view.";
            return null;
        }
    }
}
