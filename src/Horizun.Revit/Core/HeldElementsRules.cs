// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHAT AM I HOLDING BEFORE I SYNCHRONIZE (field feedback 2026-11, §9).
//
// horizun_manage_worksets operation=loans answers, read-only and without borrowing anything:
// which worksets this user owns, how many elements are owned through them, and which
// elements are BORROWED (owned while their workset is somebody else's or shared) - the ones
// that stay locked for the rest of the team until a synchronize with relinquish. The census
// itself is the one the synchronize rehearsal takes (DocumentSessionCommand.TakeSyncCensus);
// this file shapes it so the arithmetic is provable at a desk:
//   * borrowed + owned_through_worksets = owned_total (a borrowed element is never counted twice);
//   * the element list is capped and says so; the counts never are;
//   * a closed workset's elements are not in the census at all, so a model with closed worksets
//     (or unreadable elements) is coverage "partial", never a clean "nothing held";
//   * a model that is not workshared is "not applicable", never an empty list.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One borrowed element, as the command read it.</summary>
    public sealed class HeldRow
    {
        public long ElementId { get; set; }
        public string UniqueId { get; set; }
        public string Category { get; set; }
        public string Workset { get; set; }
    }

    public sealed class HeldWorkset
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool IsOpen { get; set; }
        /// <summary>UserWorkset, FamilyWorkset, ViewWorkset or StandardWorkset: the census counts every kind a user can own.</summary>
        public string Kind { get; set; }
    }

    public static class HeldElementsRules
    {
        public const int MaxListedElements = 200;

        public static JObject NotApplicable(string document, string reason = null) => new JObject
        {
            ["read_only"] = true,
            ["document"] = document,
            ["applicable"] = false,
            ["workshared"] = reason == "detached",
            ["reason"] = reason ?? "not_workshared",
            ["note"] = reason == "detached"
                ? "Not applicable: this model is DETACHED from its central, so it keeps no ownership and a synchronize is not possible."
                : "Not applicable: this model is not collaborative (not workshared), so nothing can be borrowed or owned and there is no central to synchronize with."
        };

        /// <param name="censusIncomplete">The worksets or the checkout statuses could not be read in full (the sync rehearsal's
        /// ownership_census_incomplete): the counts of owned and borrowed are then NOT reliable and the answer is partial.</param>
        /// <param name="borrowedNotListed">Borrowed ids whose element could not be re-read to describe them. They stay in
        /// <c>borrowed</c> (the census counted them) and are NOT also counted as unreadable.</param>
        public static JObject Summarize(string document, string user, int scanned, int ownedTotal, int borrowedTotal, int heldByOthers,
                                        int unreadable, int borrowedNotListed, bool censusIncomplete,
                                        IList<HeldRow> borrowedRows, IList<HeldWorkset> ownedWorksets,
                                        IList<HeldWorkset> closedWorksets, int cap = MaxListedElements)
        {
            borrowedRows = borrowedRows ?? new List<HeldRow>();
            ownedWorksets = ownedWorksets ?? new List<HeldWorkset>();
            closedWorksets = closedWorksets ?? new List<HeldWorkset>();
            int throughWorksets = System.Math.Max(0, ownedTotal - borrowedTotal);

            var byCategory = new JObject();
            foreach (var g in borrowedRows.GroupBy(r => string.IsNullOrEmpty(r.Category) ? "(no category)" : r.Category)
                                          .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, System.StringComparer.Ordinal))
                byCategory[g.Key] = g.Count();

            var listed = new JArray(borrowedRows.OrderBy(r => r.ElementId).Take(cap).Select(r => (JToken)new JObject
            {
                ["element_id"] = r.ElementId, ["unique_id"] = r.UniqueId, ["category"] = r.Category, ["workset"] = r.Workset
            }));

            bool incomplete = censusIncomplete || unreadable > 0;
            bool partial = incomplete || closedWorksets.Count > 0 || borrowedNotListed > 0;
            var notes = new List<string>();
            if (incomplete)
            {
                var causes = new List<string>();
                if (censusIncomplete) causes.Add("the user name or the worksets could not be read");
                if (unreadable > 0) causes.Add("the checkout status or UniqueId of " + unreadable + " element(s) could not be read");
                notes.Add("ownership_census_incomplete: " + string.Join("; ", causes) + ", so the counts of owned and borrowed are NOT reliable " +
                          "(a sync would refuse the same way). Unreadable is unknown, never free.");
            }
            if (closedWorksets.Count > 0)
                notes.Add(closedWorksets.Count + " workset(s) are closed: their elements are not loaded, so anything held in them is not counted here.");
            if (borrowedNotListed > 0)
                notes.Add(borrowedNotListed + " borrowed element(s) could not be re-read to describe them: they are in the borrowed count and not in the list.");

            return new JObject
            {
                ["read_only"] = true,
                ["document"] = document,
                ["applicable"] = true,
                ["workshared"] = true,
                ["current_user"] = user,
                ["coverage"] = partial ? "partial" : "complete",
                ["ownership_census_incomplete"] = incomplete,
                ["counts_reliable"] = !incomplete,
                ["coverage_notes"] = new JArray(notes),
                ["counts"] = new JObject
                {
                    ["elements_scanned"] = scanned,
                    ["owned_total"] = ownedTotal,
                    ["owned_through_my_worksets"] = throughWorksets,
                    ["borrowed"] = borrowedTotal,
                    ["held_by_others"] = heldByOthers,
                    ["unreadable"] = unreadable,
                    ["borrowed_not_listed"] = borrowedNotListed
                },
                ["owned_worksets"] = new JArray(ownedWorksets.OrderBy(w => w.Id).Select(w => (JToken)new JObject
                    { ["id"] = w.Id, ["name"] = w.Name, ["kind"] = w.Kind, ["open"] = w.IsOpen })),
                ["closed_worksets"] = new JArray(closedWorksets.OrderBy(w => w.Id).Select(w => (JToken)new JObject
                    { ["id"] = w.Id, ["name"] = w.Name, ["kind"] = w.Kind })),
                ["borrowed_by_category"] = byCategory,
                ["borrowed_elements"] = listed,
                ["borrowed_elements_listed"] = listed.Count,
                ["borrowed_elements_truncated"] = borrowedRows.Count > cap,
                ["next"] = "horizun_save_document operation=sync_with_central relinquish=borrowed_only gives back only the borrowed elements " +
                           "(relinquish=all also the worksets); operation=relinquish_own frees everything this user owns without synchronizing. " +
                           "Both rehearse first."
            };
        }
    }
}
