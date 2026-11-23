// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// DRAW ORDER OF 2D DETAIL (horizun_detail_2d operation=draw_order).
//
// Revit stacks the detail elements of a view (detail lines, filled and masking regions,
// detail components, text) back to front, and DetailElementOrderUtils exposes the four
// moves of the Arrange menu: Bring to Front, Send to Back, Bring Forward, Send Backward,
// plus GetDrawOrderForDetails, which reads the stack back (earlier = drawn first = under).
//
// Revit-free half: the move words, and the judgement a RE-READ of the stack must pass. The
// order is read over the whole set of detail elements of the view, not only the moved ones,
// because "in front" is a statement about the others.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class DrawOrderRules
    {
        public const string Front = "front", Back = "back", Forward = "forward", Backward = "backward";
        public static readonly string[] Orders = { Front, Back, Forward, Backward };
        public const int MaxElements = 1000;

        public static string Parse(string word)
        {
            string w = (word ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Orders, w) >= 0 ? w : null;
        }

        public static string OrderError() => "order must be front, back, forward or backward (Bring to Front, Send to Back, Bring Forward, Send Backward).";

        public static string MembersError(IList<long> ids)
        {
            if (ids == null || ids.Count == 0) return "element_ids must name at least one detail element.";
            if (ids.Count > MaxElements) return "element_ids holds more than " + MaxElements + " entries.";
            if (ids.Distinct().Count() != ids.Count) return "element_ids repeats an element.";
            return null;
        }

        /// <summary>
        /// Null when the stack read AFTER the move is what the move promises, otherwise why not.
        /// <paramref name="before"/> and <paramref name="after"/> hold the whole detail set of the view, back to front.
        /// </summary>
        public static string Verify(string order, IList<long> before, IList<long> after, ISet<long> moved)
        {
            if (before == null || after == null || moved == null || moved.Count == 0) return "the draw order could not be read.";
            if (before.Count != after.Count || !new HashSet<long>(before).SetEquals(after))
                return "the set of detail elements in the view changed during the move.";
            if (!moved.IsSubsetOf(after)) return "a moved element is not among the view's detail elements after the move.";
            int k = moved.Count;
            switch (order)
            {
                case Front:
                    if (!after.Skip(after.Count - k).All(moved.Contains)) return "the moved elements are not the last (frontmost) " + k + " of the stack.";
                    break;
                case Back:
                    if (!after.Take(k).All(moved.Contains)) return "the moved elements are not the first (rearmost) " + k + " of the stack.";
                    break;
                case Forward:
                    if (!Moved(moved, before, after, +1)) return "a moved element did not stay in place or move toward the front, or none moved although one could.";
                    break;
                case Backward:
                    if (!Moved(moved, before, after, -1)) return "a moved element did not stay in place or move toward the back, or none moved although one could.";
                    break;
                default: return OrderError();
            }
            // Everything that was NOT moved keeps its relative order: an arrange move never shuffles the others.
            var others = new HashSet<long>(before.Where(x => !moved.Contains(x)));
            if (!before.Where(others.Contains).SequenceEqual(after.Where(others.Contains)))
                return "elements that were not moved changed their relative order.";
            if ((order == Front || order == Back) &&
                !before.Where(moved.Contains).SequenceEqual(after.Where(moved.Contains)))
                return "the moved elements did not keep their own relative order.";
            return null;
        }

        private static bool Moved(ISet<long> moved, IList<long> before, IList<long> after, int direction)
        {
            var pre = Index(before); var post = Index(after);
            bool anyMoved = false;
            foreach (long id in moved)
            {
                int delta = post[id] - pre[id];
                if (delta * direction < 0) return false;
                if (delta != 0) anyMoved = true;
            }
            if (anyMoved) return true;
            // Nothing moved: only legitimate when every moved element already sat at that end.
            int n = before.Count, k = moved.Count;
            return direction > 0 ? moved.All(id => pre[id] >= n - k) : moved.All(id => pre[id] < k);
        }

        private static Dictionary<long, int> Index(IList<long> order)
        {
            var d = new Dictionary<long, int>();
            for (int i = 0; i < order.Count; i++) d[order[i]] = i;
            return d;
        }

        /// <summary>Whether the move changes anything at all: reported, so an already-in-place request is not mistaken for a failure.</summary>
        public static bool Changed(IList<long> before, IList<long> after) => !before.SequenceEqual(after);
    }
}
