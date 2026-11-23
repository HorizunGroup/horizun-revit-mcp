// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHAT IS "OURS" IN A SHARED MODEL (FIELD FEEDBACK 2.6, 4.3, 4.4).
//
// In a collaborative model a documentation agent may change the visibility of
// ITS OWN views and delete ITS OWN elements, and nothing else. "Own" is decided
// from evidence, strongest first, and the evidence travels with the verdict:
//
//   ownership_mark        the element carries the Horizun ownership mark (the
//                         marking the Pro edition stamps on what the MCP creates;
//                         wired through OwnershipProbe.Mark).
//   created_this_session  this add-in recorded the element as ADDED by one of its
//                         writes since Revit started (ChangeLedger).
//   worksharing_creator   Revit's worksharing tooltip names the current Revit user
//                         as the element's creator.
//   not_workshared        a file nobody else works in: the rule does not apply.
//
// No evidence is NOT ownership: unknown is foreign. That is the safe reading in a
// model where somebody else's view looks exactly like ours.
// -----------------------------------------------------------------------------
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class OwnershipVerdict
    {
        public bool Own;
        public string Evidence;
        public string Reason;
    }

    public static class OwnershipRules
    {
        public const string Mark = "ownership_mark";
        public const string Session = "created_this_session";
        public const string Creator = "worksharing_creator";
        public const string NotWorkshared = "not_workshared";
        public const string Foreign = "foreign";
        public const string Unknown = "unknown";

        public static OwnershipVerdict Decide(bool workshared, bool? marked, bool createdThisSession, bool? creatorIsMe, string creator = null)
        {
            if (!workshared) return new OwnershipVerdict { Own = true, Evidence = NotWorkshared, Reason = "the document is not workshared" };
            if (marked == true) return new OwnershipVerdict { Own = true, Evidence = Mark };
            if (createdThisSession) return new OwnershipVerdict { Own = true, Evidence = Session };
            if (creatorIsMe == true) return new OwnershipVerdict { Own = true, Evidence = Creator };
            if (creatorIsMe == false)
                return new OwnershipVerdict { Own = false, Evidence = Foreign, Reason = "created by " + (creator ?? "another user") };
            return new OwnershipVerdict { Own = false, Evidence = Unknown, Reason = "no ownership evidence could be read; unknown is treated as foreign" };
        }

        /// <summary>The ids among <paramref name="verdicts"/> that are not own, in input order.</summary>
        public static List<long> NotOwn(IEnumerable<KeyValuePair<long, OwnershipVerdict>> verdicts)
        {
            var list = new List<long>();
            foreach (var kv in verdicts) if (kv.Value == null || !kv.Value.Own) list.Add(kv.Key);
            return list;
        }
    }

    /// <summary>"Hide at scales coarser than" for the markers of new detail views (2.6, 4.3).</summary>
    public static class ViewMarkerRules
    {
        /// <summary>1:1 - the marker shows only in views at 1:1, i.e. in nobody's plan.</summary>
        public const int HiddenEverywhere = 1;

        /// <summary>
        /// The value to write on a NEW section/callout, or null to leave Revit's own.
        /// requested 0 = leave Revit's default; a positive scale = that scale; omitted =
        /// hidden everywhere in a workshared model (nothing of ours in others' views), and
        /// Revit's default in a file nobody else opens.
        /// </summary>
        public static int? ForNewView(bool workshared, int? requested)
        {
            if (requested.HasValue) return requested.Value == 0 ? (int?)null : requested.Value;
            return workshared ? HiddenEverywhere : (int?)null;
        }

        public static string Validate(int? requested)
        {
            if (!requested.HasValue) return null;
            if (requested.Value < 0 || requested.Value > 24000) return "hide_marker_coarser_than must be 0 (Revit's default) or a scale 1..24000.";
            return null;
        }
    }
}
