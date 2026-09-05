// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// PHASES, and the category that never had one.
//
// Most categories in a Revit model carry Phase Created and Phase Demolished.
// Some do not: a level, a grid, a view, a sheet, a material. A check that walks
// everything and reports "no phase" produces a large number made mostly of
// elements for which the question is meaningless - and the reader, quite
// reasonably, stops reading.
//
// So a category that does not SUPPORT phases is `not_applicable`, decided by
// asking the element for the parameter rather than by a compiled-in list of
// categories that would drift with every Revit release.
//
// THE ONE RELATIONSHIP THAT IS ACTUALLY WRONG is an element demolished in a
// phase that comes before the phase it was created in. That is not a matter of
// standards - it is a contradiction inside the model - so it is reported
// without any profile. Everything else here is a census.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PhaseState
    {
        public const string Created = "created";
        public const string Demolished = "demolished";
        /// <summary>The element's category has no phase parameters at all.</summary>
        public const string NotApplicable = "not_applicable";
        /// <summary>The category supports phases and this element reports none.</summary>
        public const string NoPhase = "no_phase";
        public const string Unreadable = "unreadable";

        public static readonly string[] All = { Created, Demolished, NotApplicable, NoPhase, Unreadable };
    }

    public sealed class PhaseFact
    {
        public long ElementId;
        public string Name;
        public int Sequence;
        public bool NameReadable = true;
    }

    public sealed class PhasedElementFact
    {
        public long ElementId;
        public string Category;
        /// <summary>False when the element's category carries no phase parameters.</summary>
        public bool SupportsPhases = true;
        public string CreatedPhase;
        public int? CreatedSequence;
        public string DemolishedPhase;
        public int? DemolishedSequence;
        public bool Readable = true;

        /// <summary>
        /// Demolished BEFORE it was created. A contradiction in the model itself,
        /// not a deviation from anybody's standard. Null when either sequence is
        /// unknown - an unknown order is not an invalid one.
        /// </summary>
        public bool? DemolishedBeforeCreated
        {
            get
            {
                if (!CreatedSequence.HasValue || !DemolishedSequence.HasValue) return null;
                return DemolishedSequence.Value < CreatedSequence.Value;
            }
        }
    }

    public static class PhaseCensusRules
    {
        public const string ApplicabilityMeans =
            "a level, a grid, a view and a sheet have no phase, and asking them for one produces a large " +
            "'no phase' number made of elements the question does not apply to. Applicability is decided by " +
            "asking the element for the parameter, not from a compiled-in list of categories that would drift " +
            "with every Revit release.";

        public const string InvalidMeans =
            "an element demolished in a phase EARLIER than the one it was created in is a contradiction inside " +
            "the model, not a deviation from anybody's standard, so it is reported with no profile required. " +
            "Everything else in this section is a census and none of it is a finding.";

        public static string StateOf(PhasedElementFact f)
        {
            if (f == null) return PhaseState.Unreadable;
            if (!f.Readable) return PhaseState.Unreadable;
            if (!f.SupportsPhases) return PhaseState.NotApplicable;
            if (!string.IsNullOrEmpty(f.DemolishedPhase)) return PhaseState.Demolished;
            if (!string.IsNullOrEmpty(f.CreatedPhase)) return PhaseState.Created;
            return PhaseState.NoPhase;
        }

        /// <summary>
        /// Phases in the order the document defines, which is NOT alphabetical and
        /// is the only order in which "before" means anything.
        /// </summary>
        public static List<PhaseFact> InSequence(IEnumerable<PhaseFact> phases)
        {
            return (phases ?? Enumerable.Empty<PhaseFact>())
                .Where(p => p != null)
                .OrderBy(p => p.Sequence)
                .ToList();
        }

        public static List<PhasedElementFact> Contradictions(IEnumerable<PhasedElementFact> elements)
        {
            return (elements ?? Enumerable.Empty<PhasedElementFact>())
                .Where(e => e != null && e.DemolishedBeforeCreated == true)
                .ToList();
        }

        public static JObject Tally(IEnumerable<PhasedElementFact> elements)
        {
            List<PhasedElementFact> all =
                (elements ?? Enumerable.Empty<PhasedElementFact>()).Where(e => e != null).ToList();

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string s in PhaseState.All) counts[s] = 0;
            foreach (PhasedElementFact e in all) counts[StateOf(e)]++;

            var byCategory = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (PhasedElementFact e in all.Where(x => StateOf(x) == PhaseState.NoPhase))
            {
                string c = e.Category ?? "(no category)";
                long had;
                byCategory[c] = byCategory.TryGetValue(c, out had) ? had + 1 : 1;
            }

            var rows = new JArray();
            foreach (KeyValuePair<string, long> kv in GroupOptionRules.Ranked(byCategory))
                rows.Add(new JObject { ["category"] = kv.Key, ["elements"] = kv.Value });

            var o = new JObject { ["examined"] = all.Count };
            foreach (string s in PhaseState.All) o[s] = counts[s];
            o["categories_examined"] = all.Select(e => e.Category).Where(c => c != null)
                                          .Distinct(StringComparer.Ordinal).Count();
            o["no_phase_by_category"] = rows;
            o["demolished_before_created"] = Contradictions(all).Count;
            o["order_unknown"] = all.Count(e => e.DemolishedBeforeCreated == null &&
                                                !string.IsNullOrEmpty(e.DemolishedPhase));
            o["counts_are_exact"] = counts[PhaseState.Unreadable] == 0;
            o["applicability_means"] = ApplicabilityMeans;
            o["invalid_means"] = InvalidMeans;
            return o;
        }

        public static JObject ToJson(PhasedElementFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["element_id"] = f.ElementId,
                ["category"] = f.Category,
                ["state"] = StateOf(f),
                ["supports_phases"] = f.SupportsPhases,
                ["phase_created"] = f.CreatedPhase,
                ["phase_demolished"] = f.DemolishedPhase,
                ["demolished_before_created"] = f.DemolishedBeforeCreated
            };
        }

        public static JObject ToJson(PhaseFact p)
        {
            if (p == null) return null;
            return new JObject
            {
                ["phase_id"] = p.ElementId,
                ["name"] = p.Name,
                ["sequence"] = p.Sequence,
                ["name_readable"] = p.NameReadable
            };
        }
    }
}
