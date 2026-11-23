// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_execute_python rehearsal=true.
//
// Field data (October): the most repeated hand-written pattern was "rehearsal rolled
// back -> apply -> photo", one person ran eight rolled-back rehearsals in a row for one
// operation, and every one of them was scaffolding written again. A rehearsal is the
// read_only TransactionGroup (always rolled back) PLUS a record of what the script
// changed while it ran, taken from Revit's own DocumentChanged event - so "what would
// this do" is the host's account, not the script's.
//
// This file nets the event stream (an element created and deleted inside the run is
// neither), and judges the rollback from two re-reads: every created id gone, every
// deleted id back.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class RehearsalChanges
    {
        public readonly HashSet<long> Created = new HashSet<long>();
        public readonly HashSet<long> Deleted = new HashSet<long>();
        public readonly HashSet<long> Modified = new HashSet<long>();
        public int Events;
        public readonly List<string> Transactions = new List<string>();

        /// <summary>One DocumentChanged event, folded into the net change of the run.</summary>
        public void Record(IEnumerable<long> added, IEnumerable<long> deleted, IEnumerable<long> modified, IEnumerable<string> transactionNames = null)
        {
            Events++;
            foreach (long id in added ?? Enumerable.Empty<long>()) { Created.Add(id); Deleted.Remove(id); Modified.Remove(id); }
            foreach (long id in deleted ?? Enumerable.Empty<long>())
            {
                // Made and unmade inside the run: no net change.
                if (Created.Remove(id)) continue;
                Deleted.Add(id);
                Modified.Remove(id);
            }
            foreach (long id in modified ?? Enumerable.Empty<long>())
                if (!Created.Contains(id) && !Deleted.Contains(id)) Modified.Add(id);
            foreach (string t in transactionNames ?? Enumerable.Empty<string>())
                if (Transactions.Count < 50) Transactions.Add(t);
        }
    }

    public sealed class RehearsalVerdict
    {
        public bool RolledBackVerified;
        public List<long> CreatedStillPresent = new List<long>();
        public List<long> DeletedStillMissing = new List<long>();
        public string Note;
    }

    public static class RehearsalRules
    {
        public static JObject CancellationReport(JObject proof, int? line, double elapsed)
        {
            var gaps = new JArray();
            if (proof == null) gaps.Add("change observation unavailable");
            else
            {
                if ((bool?)proof["group_rolled_back"] != true) gaps.Add("group rollback not confirmed");
                if ((bool?)proof["other_document_changed"] == true) gaps.Add("another document changed");
                if (proof["listen_error"] != null) gaps.Add("change observation failed: " + (string)proof["listen_error"]);
                if ((bool?)proof["reread_verified"] != true) gaps.Add((string)proof["note"] ?? "element re-read not verified");
            }
            return new JObject { ["line"] = line.HasValue ? (JToken)line.Value : JValue.CreateNull(),
                ["elapsed_s"] = System.Math.Round(elapsed, 3), ["rolled_back_verified"] = gaps.Count == 0,
                ["rollback_gaps"] = gaps,
                ["limit"] = "Modified values are not compared individually; the group rollback is the guarantee." };
        }
        public const int MaxListedIds = 200;

        /// <summary>
        /// The rollback is VERIFIED only when the group rolled back AND every id the run created
        /// is gone AND every id it deleted is back. present(id) re-reads the document.
        /// </summary>
        public static RehearsalVerdict Judge(bool groupRolledBack, RehearsalChanges c, System.Func<long, bool?> present)
        {
            var v = new RehearsalVerdict();
            int unknown = 0;
            foreach (long id in c.Created.OrderBy(x => x))
            {
                bool? p = present(id);
                if (p == null) unknown++;
                else if (p.Value) v.CreatedStillPresent.Add(id);
            }
            foreach (long id in c.Deleted.OrderBy(x => x))
            {
                bool? p = present(id);
                if (p == null) unknown++;
                else if (!p.Value) v.DeletedStillMissing.Add(id);
            }
            v.RolledBackVerified = groupRolledBack && unknown == 0 && v.CreatedStillPresent.Count == 0 && v.DeletedStillMissing.Count == 0;
            v.Note = !groupRolledBack ? "The rehearsal group did NOT confirm its rollback: treat the run as a real write until the model is checked."
                   : unknown > 0 ? unknown + " id(s) could not be re-read after the rollback; the rollback is not verified."
                   : v.CreatedStillPresent.Count > 0 ? v.CreatedStillPresent.Count + " element(s) the run created are still in the model after the rollback."
                   : v.DeletedStillMissing.Count > 0 ? v.DeletedStillMissing.Count + " element(s) the run deleted did not come back after the rollback."
                   : "Rolled back and re-read: every element the run created is gone and every element it deleted is back.";
            return v;
        }

        /// <summary>{count, by_category, ids (capped), ids_truncated} for one change kind.</summary>
        public static JObject Block(IEnumerable<long> ids, System.Func<long, string> category)
        {
            List<long> l = ids.OrderBy(x => x).ToList();
            return new JObject
            {
                ["count"] = l.Count,
                ["by_category"] = SelectionContextRules.ByCategory(l.Select(category)),
                ["ids"] = new JArray(l.Take(MaxListedIds)),
                ["ids_truncated"] = l.Count > MaxListedIds
            };
        }
    }
}
