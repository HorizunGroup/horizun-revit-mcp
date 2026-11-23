// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_execute_python documentation_only=true (4.2-a).
//
// permission_profile=documentation limits the TYPED tools to views, sheets, annotation and
// the elements a view owns. A script can do anything the API can, so the same limit is
// enforced on the DIFF of the run instead of on the call: the script runs inside a
// TransactionGroup, Revit's own DocumentChanged account of what it created, modified and
// deleted is classified, and one element outside the documentation set rolls the whole
// group back. The reply names every offending id.
//
// Classification vocabulary is DocumentationTarget's (view, view_owned, annotation,
// model, missing). What this file adds is the decision for a DIFF, and the rule that
// matters: UNCERTAINTY REFUSES. An id nobody could classify (a deleted element that was
// not in the snapshot taken before the script, an unreadable change stream, an event
// from a document the group cannot roll back) is an offender, never a pass.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class DocumentationOffender
    {
        public long Id;
        /// <summary>created | modified | deleted.</summary>
        public string Change;
        /// <summary>A DocumentationTarget kind, or "unclassified" when nobody could say.</summary>
        public string Kind;
        public string Category;
    }

    public sealed class DocumentationOnlyVerdict
    {
        public bool Ok;
        public readonly List<DocumentationOffender> Offenders = new List<DocumentationOffender>();
        public readonly List<string> Uncertainties = new List<string>();
        public int ChecksCreated, ChecksModified, ChecksDeleted;
        public string Reason;
    }

    public static class DocumentationOnlyRules
    {
        public const string Unclassified = "unclassified";
        public const string ErrorCode = "documentacion_violada";
        public const int MaxListedOffenders = 200;

        /// <summary>
        /// Judge one run. kindNow(id) classifies an id that exists after the script (created,
        /// modified); kindBefore(id) classifies a deleted id from the snapshot taken before it
        /// ran. Either returns null when nobody could say, which refuses. otherDocumentChanged
        /// is true when Revit reported a change in a document other than the target: the group
        /// cannot roll that back, so the run cannot be proven documentation-only.
        /// </summary>
        public static DocumentationOnlyVerdict Judge(RehearsalChanges c,
            Func<long, DocumentationTarget> kindNow, Func<long, string> kindBefore,
            bool otherDocumentChanged, string listenError)
        {
            var v = new DocumentationOnlyVerdict();
            if (listenError != null) v.Uncertainties.Add("the change stream could not be read completely: " + listenError);
            if (otherDocumentChanged)
                v.Uncertainties.Add("Revit reported a change in a document other than the target; the rollback covers the target only.");
            foreach (long id in c.Created.OrderBy(x => x)) { v.ChecksCreated++; Check(v, id, "created", kindNow(id)); }
            foreach (long id in c.Modified.OrderBy(x => x)) { v.ChecksModified++; Check(v, id, "modified", kindNow(id)); }
            foreach (long id in c.Deleted.OrderBy(x => x))
            {
                v.ChecksDeleted++;
                string kind = kindBefore(id);
                if (kind != null && DocumentationTarget.IsDocumentation(kind)) continue;
                v.Offenders.Add(new DocumentationOffender { Id = id, Change = "deleted", Kind = kind ?? Unclassified });
            }
            v.Ok = v.Offenders.Count == 0 && v.Uncertainties.Count == 0;
            v.Reason = v.Ok
                ? "every created, modified and deleted element is a view, a sheet, annotation or owned by a view"
                : Describe(v);
            return v;
        }

        private static void Check(DocumentationOnlyVerdict v, long id, string change, DocumentationTarget t)
        {
            if (t != null && DocumentationTarget.IsDocumentation(t.Kind)) return;
            v.Offenders.Add(new DocumentationOffender
            {
                Id = id, Change = change, Kind = t?.Kind ?? Unclassified, Category = t?.Category
            });
        }

        private static string Describe(DocumentationOnlyVerdict v)
        {
            var parts = new List<string>();
            if (v.Offenders.Count > 0)
                parts.Add("the script changed " + v.Offenders.Count + " element(s) outside views, sheets, annotation and view-owned elements: " +
                          string.Join(", ", v.Offenders.Take(20).Select(o => o.Id + " (" + o.Change + ", " + o.Kind + ")")) +
                          (v.Offenders.Count > 20 ? ", ..." : ""));
            foreach (string u in v.Uncertainties) parts.Add(u);
            return string.Join(" ", parts);
        }

        /// <summary>What becomes of the documentation_only group when the script is done.</summary>
        public enum GroupFate
        {
            /// <summary>The diff is documentation only and the run finished: keep it as one undo step.</summary>
            Assimilate,
            /// <summary>The diff has an offender: roll back and prove it.</summary>
            RollBackJudged,
            /// <summary>The script was stopped (cancel or timeout_s): its partial work is not judged, it is rolled back and proven (review M6).</summary>
            RollBackInterrupted,
            /// <summary>The script left a Transaction open: the group is closed by rolling it back, never left open (review m8).</summary>
            RollBackAfterOpenTransaction
        }

        public const string OutcomeRolledBackInterrupted = "rolled_back_interrupted";
        public const string OutcomeRolledBackOpenTransaction = "rolled_back_transaction_left_open";
        public const string OutcomeGroupLeftOpen = "group_left_open";

        public static GroupFate Decide(bool leftModifiable, bool interrupted, bool verdictOk)
            => leftModifiable ? GroupFate.RollBackAfterOpenTransaction
             : interrupted ? GroupFate.RollBackInterrupted
             : verdictOk ? GroupFate.Assimilate
             : GroupFate.RollBackJudged;

        /// <summary>The sentence for a documentation_only run that was interrupted: it states the rollback or its absence.</summary>
        public static string InterruptedNote(bool rolledBackVerified)
            => rolledBackVerified
                ? "documentation_only=true: the script was interrupted, so the WHOLE group was rolled back and re-read - the model holds the state it had before the script. Nothing it did was kept, including what it had already committed."
                : "documentation_only=true: the script was interrupted and the rollback of its group COULD NOT BE CONFIRMED - treat the model as possibly changed and check it before doing anything else.";

        /// <summary>The sentence for a script that left a Transaction open under documentation_only.</summary>
        public static string OpenTransactionNote(bool groupClosed)
            => groupClosed
                ? "documentation_only=true NOT CONFIRMED: the script left a Transaction open. The group was rolled back as a whole (the run is not kept), so the model should hold its state from before the script; verify it."
                : "documentation_only=true NOT CONFIRMED: the script left a Transaction open and the group could NOT be closed or rolled back here. What the script wrote is NOT proven documentation-only and the group may still be open; check the model.";

        /// <summary>The block added to the reply, whether the run was kept or rolled back.</summary>
        public static JObject Report(DocumentationOnlyVerdict v, string outcome, bool? rollbackVerified,
                                     bool snapshotComplete, int snapshotSize)
        {
            var offenders = new JArray();
            foreach (DocumentationOffender o in v.Offenders.Take(MaxListedOffenders))
                offenders.Add(new JObject
                {
                    ["id"] = o.Id, ["change"] = o.Change, ["kind"] = o.Kind,
                    ["category"] = o.Category == null ? JValue.CreateNull() : (JToken)o.Category
                });
            return new JObject
            {
                ["ok"] = v.Ok,
                ["outcome"] = outcome,
                ["offending_ids"] = new JArray(v.Offenders.Take(MaxListedOffenders).Select(o => (JToken)o.Id)),
                ["offenders"] = offenders,
                ["offenders_total"] = v.Offenders.Count,
                ["uncertainties"] = new JArray(v.Uncertainties),
                ["checked"] = new JObject { ["created"] = v.ChecksCreated, ["modified"] = v.ChecksModified, ["deleted"] = v.ChecksDeleted },
                ["rollback_verified"] = rollbackVerified == null ? JValue.CreateNull() : (JToken)rollbackVerified.Value,
                ["deleted_snapshot"] = new JObject { ["complete"] = snapshotComplete, ["documentation_ids"] = snapshotSize },
                ["reason"] = v.Reason,
                ["scope"] = "Revit's DocumentChanged account of the target document while the script ran, classified as " +
                            "permission_profile=documentation classifies targets. A deleted element is judged from a snapshot of the " +
                            "views, sheets, annotation, detail lines and filled regions taken before the script: a deletion outside that " +
                            "snapshot is refused as unclassified. Files written, other documents and the network are outside this " +
                            "guarantee (Save/Close/Open calls are refused by a textual scan before the run, which is not a sandbox)."
            };
        }

        /// <summary>The failure text: it states the rollback, never a guess, and names the ids.</summary>
        public static string FailureMessage(DocumentationOnlyVerdict v, bool rolledBackVerified)
        {
            return "documentation_only=true REFUSED the result: " + v.Reason + ". " +
                   (rolledBackVerified
                       ? "The whole run was rolled back and re-read: the model holds the state it had before the script."
                       : "THE ROLLBACK COULD NOT BE CONFIRMED - treat the model as possibly changed and check the ids above before doing anything else.");
        }
    }
}
