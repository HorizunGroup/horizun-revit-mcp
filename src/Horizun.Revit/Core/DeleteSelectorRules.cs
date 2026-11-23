// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_delete_verified mode=query.
//
//  - SuggestProtect: the selected ids whose deletion would take elements the selector
//    did not name. The dry run lists them as protect_ids sugeridos, with what each one
//    drags along, so the caller can decide before applying; nothing is protected for them.
//  - CascadeAgrees: the apply must delete the same collateral the rehearsal counted.
//  - Reread: after the commit the SAME selector is read again. Zero is the only clean
//    answer; an id still matching that the caller protected is reported apart, and an
//    id that cannot be read is never counted as gone.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CascadeEdge
    {
        public long ParentId;
        public long ChildId;
    }

    public sealed class ProtectSuggestion
    {
        public long ProtectId;
        public List<long> WouldAlsoTake = new List<long>();
    }

    public sealed class SelectorReread
    {
        public int MatchedBefore;
        public int Remaining;
        public int RemainingProtected;
        public int RemainingUnprotected;
        public int Unreadable;
        public bool Cleared;
        public List<long> RemainingUnprotectedIds = new List<long>();
    }

    public static class DeleteSelectorRules
    {
        public const int MaxSelected = 5000;

        public static List<ProtectSuggestion> SuggestProtect(IEnumerable<long> selected, IEnumerable<CascadeEdge> cascades)
        {
            var sel = new HashSet<long>(selected ?? Enumerable.Empty<long>());
            return (cascades ?? Enumerable.Empty<CascadeEdge>())
                .Where(c => sel.Contains(c.ParentId) && !sel.Contains(c.ChildId))
                .GroupBy(c => c.ParentId)
                .OrderBy(g => g.Key)
                .Select(g => new ProtectSuggestion
                {
                    ProtectId = g.Key,
                    WouldAlsoTake = g.Select(c => c.ChildId).Distinct().OrderBy(x => x).ToList()
                })
                .ToList();
        }

        public static bool CascadeAgrees(IEnumerable<long> rehearsal, IEnumerable<long> applied)
        {
            var a = new HashSet<long>(rehearsal ?? Enumerable.Empty<long>());
            var b = new HashSet<long>(applied ?? Enumerable.Empty<long>());
            return a.SetEquals(b);
        }

        public static SelectorReread Reread(int matchedBefore, IEnumerable<long> stillMatching,
                                            IEnumerable<long> protectedIds, int unreadable)
        {
            var prot = new HashSet<long>(protectedIds ?? Enumerable.Empty<long>());
            List<long> remaining = (stillMatching ?? Enumerable.Empty<long>()).Distinct().OrderBy(x => x).ToList();
            List<long> unprotected = remaining.Where(id => !prot.Contains(id)).ToList();
            return new SelectorReread
            {
                MatchedBefore = matchedBefore,
                Remaining = remaining.Count,
                RemainingProtected = remaining.Count - unprotected.Count,
                RemainingUnprotected = unprotected.Count,
                Unreadable = unreadable,
                RemainingUnprotectedIds = unprotected.Take(50).ToList(),
                Cleared = unprotected.Count == 0 && unreadable == 0
            };
        }
    }
}
