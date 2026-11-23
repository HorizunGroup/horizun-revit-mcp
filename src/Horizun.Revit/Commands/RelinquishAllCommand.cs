// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Give back everything this user owns in a workshared model, and MEASURE what
// changed rather than assume it.
//
// WorksharingUtils.RelinquishOwnership returns the ids it attempted to release,
// but that is not a postcondition. The honest result is a second ownership census:
// worksets by Owner and every element by GetCheckoutStatus. Only zero in BOTH,
// with no unreadable element, can be declared fully relinquished.
//
// A non-workshared document is refused, not "succeeded with nothing to do":
// asking to relinquish a file that has no ownership at all means the caller
// believes something false about the model, and that is worth stopping for.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed class RelinquishAllCommand : ICommand
    {
        public string Name => "horizun_relinquish_all";

        public string Description =>
            "Relinquish every workset and element this user owns in the ACTIVE workshared document, then MEASURE " +
            "the result: worksets and element-level checkout statuses are read before and after, and the ids Revit " +
            "reported as relinquished are also returned, " +
            "so a partial relinquish cannot pass as a complete one. Refuses a document that is not workshared. " +
            "Does not synchronize with central and does not save.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string expectedTitle = null;
            JObject req = null;
            try
            {
                req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson);
                expectedTitle = req.Value<string>("expected_document");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // WHICH model. Relinquishing in the wrong workshared model hands somebody
            // else's elements back mid-edit, and expected_document was optional here.
            GateResult gate = DocumentGate.ForMutation(app, req, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            if (!doc.IsWorkshared)
            {
                return CommandResult.Fail(
                    "This document is not workshared, so nothing can be owned or relinquished in it. If you " +
                    "expected a workshared model, you are looking at the wrong document — that is worth checking " +
                    "before anything else runs against it.");
            }

            return Release(app, doc, null);
        }

        /// <summary>The release and its two censuses, shared by this tool and save_document.</summary>
        internal static CommandResult Release(UIApplication app, Document doc, string operation)
        {
            string me = app.Application.Username;
            int? ownedBefore = CountWorksetsOwnedBy(doc, me);
            ElementOwnershipCensus elementsBefore = CountElementsOwnedByCurrentUser(doc);
            var apiRelinquishedElements = new List<long>();
            var apiRelinquishedWorksets = new List<long>();

            try
            {
                var options = new RelinquishOptions(true)
                {
                    CheckedOutElements = true,
                    FamilyWorksets = true,
                    StandardWorksets = true,
                    UserWorksets = true,
                    ViewWorksets = true
                };
                using (RelinquishedItems apiResult = WorksharingUtils.RelinquishOwnership(
                    doc, options, new TransactWithCentralOptions()))
                {
                    if (apiResult != null)
                    {
                        ICollection<ElementId> ids = apiResult.GetRelinquishedElements();
                        if (ids != null)
                            foreach (ElementId id in ids) apiRelinquishedElements.Add(Rid.GetId(id));

                        ICollection<WorksetId> worksets = apiResult.GetRelinquishedWorksets();
                        if (worksets != null)
                            foreach (WorksetId id in worksets) apiRelinquishedWorksets.Add(WorksetIdValue(id));
                    }
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Revit refused to relinquish: " + ex.Message);
            }

            int? ownedAfter = CountWorksetsOwnedBy(doc, me);
            ElementOwnershipCensus elementsAfter = CountElementsOwnedByCurrentUser(doc);

            bool? complete = ownedAfter.HasValue && elementsAfter.Complete
                ? (bool?)(ownedAfter.Value == 0 && elementsAfter.Owned == 0)
                : null;

            return CommandResult.Ok(new
            {
                operation,
                relinquish_attempted = true,
                relinquished = complete == true,
                document = doc.Title,
                user = me,
                // Measured, not claimed. Null means the count could not be read at all.
                worksets_owned_before = ownedBefore,
                worksets_owned_after = ownedAfter,
                elements_scanned_before = elementsBefore.Scanned,
                elements_owned_before = elementsBefore.Owned,
                elements_unreadable_before = elementsBefore.Unreadable,
                elements_owned_before_sample = elementsBefore.OwnedSample,
                elements_scanned_after = elementsAfter.Scanned,
                elements_owned_after = elementsAfter.Owned,
                elements_unreadable_after = elementsAfter.Unreadable,
                elements_owned_after_sample = elementsAfter.OwnedSample,
                api_reported_relinquished_element_ids = apiRelinquishedElements,
                api_reported_relinquished_workset_ids = apiRelinquishedWorksets,
                fully_relinquished = complete,
                measured_how = "Worksets were counted by Owner. Every collectable element was checked with " +
                               "WorksharingUtils.GetCheckoutStatus before and after. Revit's returned " +
                               "RelinquishedItems ids are reported separately because they describe the call, " +
                               "not proof of the state after it.",
                note = complete == false
                    ? "STILL OWNED: " + ownedAfter + " workset(s) and " + elementsAfter.Owned +
                      " element(s) remain under this user after the relinquish. Do not treat the model as free."
                    : (complete == null
                        ? "UNVERIFIED: the post-relinquish census could not read every required ownership fact " +
                          "(workset count readable=" + ownedAfter.HasValue + ", unreadable elements=" +
                          elementsAfter.Unreadable + "). This is not a claim of full release."
                        : null),
                also_note = complete == true
                    ? "This did not synchronize with central and did not save. The postcondition census verified " +
                      "that no workset or element remains owned by this user; your changes are wherever they already were."
                    : "This did not synchronize with central and did not save. Complete ownership release was NOT " +
                      "verified; your changes are wherever they already were."
            });
        }

        internal static JObject ExitOwnership(Document doc)
        {
            int? worksets = CountWorksetsOwnedBy(doc, doc.Application.Username);
            var elements = CountElementsOwnedByCurrentUser(doc);
            return new JObject { ["owned_worksets"] = worksets, ["owned_elements"] = elements.Owned,
                ["unreadable_elements"] = elements.Unreadable,
                ["warning"] = !worksets.HasValue || worksets > 0 || elements.Owned > 0 || !elements.Complete
                    ? "Closing may leave borrowed elements/worksets. Relinquish before exit; exit does not synchronize." : null };
        }

        /// <summary>
        /// horizun_save_document operation=relinquish_own: the same release and the same
        /// before/after census as this command, behind a rehearsal. dry_run (the default)
        /// lists what this user owns - the worksets by name, the elements by count and a
        /// sample of ids - and issues a single-use token bound to that census; the apply
        /// refuses when the ownership moved since. Only what THIS user owns is ever
        /// released: RelinquishOwnership has no way to touch another user's checkout.
        /// </summary>
        internal static CommandResult RelinquishOwn(UIApplication app, GateResult gate, JObject req)
        {
            Document doc = gate.Document;
            bool? workshared = null;
            try { workshared = doc.IsWorkshared; } catch { }
            if (workshared != true)
                return CommandResult.Fail("operation=relinquish_own: '" + doc.Title + "' is not workshared (or its " +
                    "worksharing state could not be read), so nothing can be owned or relinquished in it. Nothing ran.");

            string me = app.Application.Username;
            List<string> ownedWorksets = WorksetsOwnedBy(doc, me);
            ElementOwnershipCensus census = CountElementsOwnedByCurrentUser(doc);
            var preview = new JObject
            {
                ["user"] = me,
                ["owned_worksets"] = ownedWorksets == null ? null : new JArray(ownedWorksets),
                ["owned_elements"] = census.Owned,
                ["owned_elements_sample"] = new JArray(census.OwnedSample),
                ["elements_unreadable"] = census.Unreadable
            };
            string planHash = DocumentGate.PlanHash(preview, "user", "owned_worksets", "owned_elements",
                                                    "owned_elements_sample", "elements_unreadable");
            const string scope = "horizun_save_document:relinquish_own";
            bool dryRun = req.Value<bool?>("dry_run") ?? true;
            if (dryRun)
            {
                Confirmation issued = DocumentGate.Confirmations.Issue(scope, gate.Fingerprint, planHash);
                preview["operation"] = "relinquish_own";
                preview["dry_run"] = true;
                preview["document"] = doc.Title;
                preview["would_release"] = ownedWorksets == null
                    ? "UNKNOWN: the worksets could not be read."
                    : ownedWorksets.Count + " workset(s) and " + census.Owned + " element(s) owned by '" + me + "'" +
                      (census.Unreadable > 0 ? "; " + census.Unreadable + " element(s) could not be read" : "") + ".";
                preview["does_not"] = "Does not synchronize, does not save, and never touches another user's checkout.";
                preview["confirmation_token"] = issued.Token;
                preview["confirmation_expires_utc"] = issued.ExpiresUtc.ToString("u");
                return CommandResult.Ok(preview);
            }
            ConfirmationCheck check = DocumentGate.Confirmations.Validate(req.Value<string>("confirmation_token"),
                                                                          scope, gate.Fingerprint, planHash);
            if (!check.Ok)
                return CommandResult.Fail("REFUSING TO RELINQUISH in '" + doc.Title + "': " + check.Message +
                    " Run operation=relinquish_own with dry_run=true again. Nothing was released.");
            return Release(app, doc, "relinquish_own");
        }

        private static List<string> WorksetsOwnedBy(Document doc, string user)
        {
            try
            {
                var names = new List<string>();
                foreach (Workset w in new FilteredWorksetCollector(doc))
                    if (w != null && string.Equals(w.Owner, user, StringComparison.OrdinalIgnoreCase)) names.Add(w.Name);
                names.Sort(StringComparer.Ordinal);
                return names;
            }
            catch { return null; }
        }

        /// <summary>Worksets owned by the given user, or null when the count cannot be taken at all.</summary>
        private static int? CountWorksetsOwnedBy(Document doc, string user)
        {
            try
            {
                int n = 0;
                var collector = new FilteredWorksetCollector(doc);
                foreach (Workset w in collector)
                {
                    if (w == null) continue;
                    if (string.Equals(w.Owner, user, StringComparison.OrdinalIgnoreCase)) n++;
                }
                return n;
            }
            catch
            {
                return null;
            }
        }

        private sealed class ElementOwnershipCensus
        {
            public int Scanned;
            public int Owned;
            public int Unreadable;
            public readonly List<long> OwnedSample = new List<long>();
            public bool Complete => Unreadable == 0;
        }

        /// <summary>
        /// Re-read every element's checkout status. A failed read is counted, never
        /// converted into NotOwned; otherwise an inaccessible locked element would make
        /// an incomplete release look complete.
        /// </summary>
        private static ElementOwnershipCensus CountElementsOwnedByCurrentUser(Document doc)
        {
            var result = new ElementOwnershipCensus();
            ICollection<ElementId> ids;
            // TWO FILTERED COLLECTORS (backlog #19). A collector with no filter at all throws in
            // Revit ("one filter must be applied"), so the census came back Unreadable=1 and
            // fully_relinquished could never be true. Types and instances together are every
            // element, as before.
            try
            {
                var all = new List<ElementId>(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds());
                all.AddRange(new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds());
                ids = all;
            }
            catch
            {
                result.Unreadable = 1;
                return result;
            }

            foreach (ElementId id in ids)
            {
                result.Scanned++;
                try
                {
                    if (WorksharingUtils.GetCheckoutStatus(doc, id) != CheckoutStatus.OwnedByCurrentUser) continue;
                    result.Owned++;
                    if (result.OwnedSample.Count < 100) result.OwnedSample.Add(Rid.GetId(id));
                }
                catch { result.Unreadable++; }
            }
            return result;
        }

        private static long WorksetIdValue(WorksetId id)
        {
            return id.IntegerValue;
        }
    }
}
