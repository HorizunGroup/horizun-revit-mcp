// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_worksets operation=borrow (CT-P1-1b): check out EXACTLY the named
// elements from the central and nothing else.
//
//   * The dry run reads each element's checkout status (it borrows nothing) and says which
//     can be asked for, which are already ours and which somebody else holds - those are
//     never forced.
//   * The apply asks the central only for the borrowable ones (WorksharingUtils.
//     CheckoutElements, giving up at once if the central is locked: nobody is at the keyboard
//     to read an answer that arrives later) and then RE-READS every status. What the central
//     returned is not the proof; the re-read is.
//   * The loan is returned by sync_with_central relinquish=borrowed_only (or all) or by
//     relinquish_own; BorrowRules.ReturnsAt says so in every reply.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageWorksetsCommand
    {
        private sealed class GiveUpOnLockedCentral : ICentralLockedCallback
        {
            public bool ShouldWaitForLockAvailability() => false;
        }

        private CommandResult Borrow(UIApplication app, GateResult gate, JObject r)
        {
            Document doc = gate.Document;
            if (!SafeWorkshared(doc))
                return Refused("borrow", "'" + doc.Title + "' is not workshared: there is no central to borrow from.");

            var ids = new List<long>();
            if (r["element_ids"] is JArray arr)
                foreach (JToken t in arr)
                {
                    if (!long.TryParse(t.ToString(), out long parsed)) return Refused("borrow", "element_ids must hold integer ids.");
                    ids.Add(parsed);
                }
            ids = ids.Distinct().ToList();
            string invalid = BorrowRules.Validate(ids);
            if (invalid != null) return Refused("borrow", invalid);

            var states = new Dictionary<long, string>();
            var classes = new Dictionary<long, string>();
            var owners = new Dictionary<long, string>();
            foreach (long raw in ids)
            {
                Element e = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) : null;
                string state = CheckoutState.Unreadable, owner = null;
                if (e != null)
                {
                    try { state = WorksharingStatusRules.State(WorksharingUtils.GetCheckoutStatus(doc, e.Id, out owner).ToString()); }
                    catch { state = CheckoutState.Unreadable; }
                }
                states[raw] = state;
                owners[raw] = WorksharingStatusRules.BorrowedBy(state, owner);
                classes[raw] = BorrowRules.Classify(e != null, state);
            }
            List<long> borrowable = ids.Where(i => classes[i] == BorrowRules.Borrowable).ToList();

            JArray Rows() => new JArray(ids.Select(i => (JToken)new JObject
            {
                ["element_id"] = i, ["class"] = classes[i], ["checkout"] = states[i], ["held_by"] = owners[i]
            }));
            var plan = new JObject
            {
                ["operation"] = "borrow",
                ["requested"] = ids.Count,
                ["will_ask_the_central_for"] = borrowable.Count,
                ["already_mine"] = ids.Count(i => classes[i] == BorrowRules.AlreadyMine),
                ["held_by_other_never_forced"] = ids.Count(i => classes[i] == BorrowRules.HeldByOther),
                ["unreadable_or_missing"] = ids.Count(i => classes[i] == BorrowRules.Unreadable || classes[i] == BorrowRules.Missing),
                ["elements"] = Rows(),
                ["returns_at"] = BorrowRules.ReturnsAt
            };

            string hash = DocumentGate.PlanHash(r, "operation", "element_ids");
            ResolvedPlan resolved = FilePlan(gate, app, "borrow", new Dictionary<string, string>
            {
                ["ask"] = string.Join(",", borrowable), ["states"] = string.Join(",", ids.Select(i => i + "=" + classes[i]))
            });
            if (r["dry_run"] == null || r.Value<bool>("dry_run"))
            {
                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true, ["plan"] = plan,
                    ["rehearsal"] = new JObject
                    {
                        ["rehearsal_kind"] = "measured_preview", ["verified"] = true,
                        ["note"] = "Nothing was borrowed: the checkout status of each element was READ. A loan cannot be rehearsed " +
                                   "provisionally; the apply asks the central and re-reads every status."
                    }
                };
                ApplicationOutcome.StampRehearsal(preview, ids.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, Name, hash, true, "the token binds the ids and the checkout status each had.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, r, Name, hash, resolved, null);
            if (refusal != null) return refusal;
            if (borrowable.Count == 0)
                return Refused("borrow", "none of the " + ids.Count + " element(s) can be asked for (already ours, held by someone else, missing or unreadable); see the dry run.");

            ICollection<ElementId> returned;
            try
            {
                var options = new TransactWithCentralOptions();
                options.SetLockCallback(new GiveUpOnLockedCentral());
                returned = WorksharingUtils.CheckoutElements(doc, new HashSet<ElementId>(borrowable.Select(Rid.Make)), options);
            }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail("CheckoutElements failed: " + ex.Message + " Re-read what is held with operation=status.",
                    new JObject { ["state"] = "uncertain", ["write_started"] = true, ["plan"] = plan });
            }

            var after = new Dictionary<long, string>();
            foreach (long raw in borrowable)
            {
                try { after[raw] = WorksharingStatusRules.State(WorksharingUtils.GetCheckoutStatus(doc, Rid.Make(raw)).ToString()); }
                catch { after[raw] = CheckoutState.Unreadable; }
            }
            BorrowVerdict v = BorrowRules.Verify(borrowable, (returned ?? new List<ElementId>()).Select(Rid.Value).ToList(), after);
            bool ok = v.Verified && v.NotBorrowed.Count == 0;
            var done = new JObject
            {
                ["state"] = ok ? "committed_verified" : (v.Borrowed.Count > 0 ? "committed_partial" : "failed"),
                ["host_verified"] = ok, ["operation"] = "borrow", ["plan"] = plan,
                ["borrowed"] = new JArray(v.Borrowed), ["not_borrowed"] = new JArray(v.NotBorrowed),
                ["unmeasured"] = new JArray(v.Unmeasured), ["problems"] = new JArray(v.Problems),
                ["not_borrowed_means"] = "the central did not hand these over (out of date with the central, or someone requested them first); " +
                                         "synchronize or ask again. Nothing was forced.",
                ["returns_at"] = BorrowRules.ReturnsAt
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, borrowable.Count, borrowable.Count, v.Borrowed.Count,
                                            ids.Count - borrowable.Count, 0, ok ? 0 : 1);
            DocumentGate.StampConfirmation(done, gate, Name, hash, false);
            if (!ok)
                return CommandResult.FailWithDetail("The loan does not re-read as asked: " + v.Borrowed.Count + " of " + borrowable.Count +
                    " element(s) are now ours" + (v.Problems.Count > 0 ? "; " + string.Join("; ", v.Problems) : "") + ".", done);
            return CommandResult.Ok(done);
        }
    }
}
