// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Give back everything this user owns in a workshared model, and MEASURE what
// changed rather than assume it.
//
// WorksharingUtils.RelinquishOwnership tells you nothing useful about the result,
// so the honest way to report it is to count what this user owns before and after
// and hand over both numbers. If the count did not drop to zero, that is the
// answer — elements can stay checked out for reasons Revit does not advertise,
// and a cheerful "relinquished" over a still-locked model is how the next person
// gets blocked with no idea why.
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
            "the result: the count of worksets owned by this user is read before and after and both are reported, " +
            "so a partial relinquish cannot pass as a complete one. Refuses a document that is not workshared. " +
            "Does not synchronize with central and does not save.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string expectedTitle = null;
            JObject req = null;
            try
            {
                req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
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

            string me = app.Application.Username;
            int? ownedBefore = CountWorksetsOwnedBy(doc, me);

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
                WorksharingUtils.RelinquishOwnership(doc, options, new TransactWithCentralOptions());
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Revit refused to relinquish: " + ex.Message);
            }

            int? ownedAfter = CountWorksetsOwnedBy(doc, me);

            bool? complete = (ownedAfter.HasValue) ? (bool?)(ownedAfter.Value == 0) : null;

            return CommandResult.Ok(new
            {
                relinquished = true,
                document = doc.Title,
                user = me,
                // Measured, not claimed. Null means the count could not be read at all.
                worksets_owned_before = ownedBefore,
                worksets_owned_after = ownedAfter,
                fully_relinquished = complete,
                measured_how = "Worksets in this document whose Owner is the current user, counted before and " +
                               "after. Element-level checkouts are released by the same call but are not counted " +
                               "here — this number is about worksets, and says so rather than implying more.",
                note = complete == false
                    ? "STILL OWNED: " + ownedAfter + " workset(s) remain under this user after the relinquish. " +
                      "Do not treat the model as free; find out which and why before handing it over."
                    : null,
                also_note = "This did not synchronize with central and did not save. Ownership was returned; " +
                            "your changes are wherever they already were."
            });
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
    }
}
