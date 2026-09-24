// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit-free rules behind horizun_manage_phases and
// horizun_manage_assemblies_parts: phase order, phase-filter presentation names,
// element names that must stay unique, and the assembly view kinds. Pure, so the
// cases that matter (a demolition before the creation, a presentation key nobody
// defined, a rename onto a name already taken) are tested without a Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PhaseRules
    {
        /// <summary>The four element states a phase filter decides how to show, in Revit's dialog order.</summary>
        public static readonly string[] FilterStatuses = { "new", "existing", "demolished", "temporary" };

        /// <summary>Wire names for PhaseStatusPresentation: ShowByCategory, ShowOverriden, DontShow.</summary>
        public static readonly string[] Presentations = { "by_category", "overridden", "hidden" };

        /// <summary>Assembly view kinds, each mapped to one AssemblyViewUtils call.</summary>
        public static readonly string[] AssemblyViewKinds = { "3d", "plan", "section_a", "section_b", "elevation_front", "part_list" };

        /// <summary>
        /// Null when an element may end with these phases, else why not. Indexes are
        /// positions in the document's phase sequence; -1 for demolished means "never
        /// demolished". Created and demolished in the SAME phase is legal (Revit calls
        /// that Temporary); a demolition EARLIER than the creation is not.
        /// </summary>
        public static string OrderError(int createdIndex, int demolishedIndex)
        {
            if (createdIndex < 0) return "the created phase is not one of the document's phases";
            if (demolishedIndex < -1) return "the demolished phase is not one of the document's phases";
            if (demolishedIndex >= 0 && demolishedIndex < createdIndex)
                return "the demolished phase (position " + demolishedIndex + ") comes before the created phase (position " +
                       createdIndex + "); an element cannot be demolished before it exists";
            return null;
        }

        /// <summary>
        /// Validates a presentation object {new|existing|demolished|temporary: by_category|overridden|hidden}.
        /// Null when valid (an absent object is valid: nothing to set). An empty object is refused:
        /// asking to set nothing is a caller mistake, not a no-op to verify by construction.
        /// </summary>
        public static string PresentationError(JToken presentation)
        {
            if (presentation == null || presentation.Type == JTokenType.Null) return null;
            var o = presentation as JObject;
            if (o == null) return "presentation must be an object keyed by new, existing, demolished or temporary";
            if (!o.Properties().Any()) return "presentation is empty; name at least one of new, existing, demolished, temporary";
            foreach (JProperty p in o.Properties())
            {
                if (!FilterStatuses.Contains(p.Name))
                    return "presentation key '" + p.Name + "' is not a phase-filter state (new, existing, demolished, temporary)";
                string v = p.Value.Type == JTokenType.String ? (string)p.Value : null;
                if (v == null || !Presentations.Contains(v))
                    return "presentation." + p.Name + " must be by_category, overridden or hidden";
            }
            return null;
        }

        /// <summary>
        /// Null when `name` can be given to an element whose current name is `current`
        /// among `existing` names of its kind. Revit compares these names without case.
        /// </summary>
        public static string NameError(string name, IEnumerable<string> existing, string current)
        {
            if (string.IsNullOrWhiteSpace(name)) return "name is empty";
            if (name.Trim() != name) return "name has leading or trailing spaces";
            if (name.IndexOfAny(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\' }) >= 0)
                return "name contains a character Revit does not allow in element names";
            if (current != null && string.Equals(current, name, StringComparison.Ordinal))
                return "the name is already '" + name + "'; there is nothing to change";
            foreach (string e in existing ?? Enumerable.Empty<string>())
                if (string.Equals(e, name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(e, current, StringComparison.Ordinal))
                    return "the name '" + name + "' is already used";
            return null;
        }

        /// <summary>Null when every requested view kind is known and none repeats.</summary>
        public static string ViewKindsError(IList<string> kinds)
        {
            if (kinds == null || kinds.Count == 0) return "views must name at least one of " + string.Join(", ", AssemblyViewKinds);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string k in kinds)
            {
                if (k == null || !AssemblyViewKinds.Contains(k)) return "view kind '" + k + "' is not one of " + string.Join(", ", AssemblyViewKinds);
                if (!seen.Add(k)) return "view kind '" + k + "' is repeated";
            }
            return null;
        }
    }
}
