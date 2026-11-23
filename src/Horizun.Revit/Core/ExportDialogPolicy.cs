// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// How an export answers the modal dialogs Revit raises while it runs (4.2-c).
//
// Field report: the PDF exporter was rejected on every unattended run because the
// bridge cancelled "Update Resources" (a keynote file kept in the cloud is out of
// date), and the only message was "PDF exporter rejected output". The dialog was
// the cause, and nobody could say so.
//
// The rule here is a CLOSED catalog, not a general "press OK":
//   - `cancel` (default) is what the bridge always did: nobody is at the keyboard,
//     so do not proceed on anyone's behalf. The dialog is now NAMED in the reply.
//   - `continue_known` answers only dialogs that appear in the catalog below, with
//     the catalog's own answer. Anything else - unknown ids, anything that offers a
//     reload - is still cancelled and says why.
//   - Reload is never automatic. No catalog entry may answer with a reload.
//
// Revit-free on purpose: the catalog and the decision are the part worth proving
// without a Revit. The watcher that ACTS on a decision lives in Interference.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public enum ExportDialogMode
    {
        Cancel,
        ContinueKnown
    }

    /// <summary>One dialog Revit raised during an export and what the bridge did about it.</summary>
    public sealed class ExportDialogDecision
    {
        public string DialogId;
        public string Message;
        public bool InCatalog;
        public string CatalogKey;
        /// <summary>"cancel" or "continue".</summary>
        public string Action;
        /// <summary>The value passed to OverrideResult (2 = cancel).</summary>
        public int Result;
        /// <summary>False until the catalog answer has been confirmed against a real Revit of every supported year.</summary>
        public bool ButtonVerified;
        public string Reason;

        public JObject ToJson() => new JObject
        {
            ["dialog_id"] = DialogId,
            ["message"] = string.IsNullOrEmpty(Message) ? null : (Message.Length > 300 ? Message.Substring(0, 300) + "..." : Message),
            ["in_catalog"] = InCatalog,
            ["catalog_key"] = CatalogKey,
            ["action"] = Action,
            ["result_code"] = Result,
            ["button_verified_in_revit"] = ButtonVerified,
            ["reason"] = Reason
        };
    }

    public static class ExportDialogPolicy
    {
        public const int CancelResult = 2;

        private sealed class Entry
        {
            public string Key;
            public string IdContains;
            public string What;
            public int ContinueResult;
            public bool Verified;
            public string Why;
        }

        // The closed catalog. Every entry answers WITHOUT reloading anything. An entry is
        // added only after its dialog id and button were read from a real Revit; until a
        // live run confirms the button, Verified stays false and every decision says so.
        private static readonly Entry[] Catalog =
        {
            new Entry
            {
                Key = "update_resources",
                IdContains = "TaskDialog_Update_Resources",
                What = "Update Resources: a linked resource (for example a keynote file stored in the cloud) is out of date",
                ContinueResult = 1,
                Verified = false,
                Why = "Acknowledged and continued WITHOUT reloading: the export uses the resources already loaded in the model."
            }
        };

        /// <summary>Parse the dialog_policy argument. Null or empty is Cancel; a typo is an ERROR, never a silent fallback.</summary>
        public static ExportDialogMode Parse(string raw, out string error)
        {
            error = null;
            string v = (raw ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0 || v == "cancel") return ExportDialogMode.Cancel;
            if (v == "continue_known") return ExportDialogMode.ContinueKnown;
            error = "dialog_policy must be 'cancel' or 'continue_known' (got '" + raw + "'). Nothing was exported.";
            return ExportDialogMode.Cancel;
        }

        /// <summary>
        /// Review m4: dialog_policy governs the modal dialogs of a SINGLE-FILE export. format=pdf_qa only reads an existing PDF and
        /// raises none, so the argument used to be accepted there and ignored; the other multi-file or non-dialog formats refuse it too.
        /// </summary>
        public static bool AppliesTo(string format, bool dwgSet)
        {
            switch ((format ?? "").ToLowerInvariant())
            {
                case "pdf": case "ifc": case "nwc": case "fbx": case "image": case "schedule_csv": return true;
                case "dwg": return !dwgSet;
                default: return false;
            }
        }

        public static string Name(ExportDialogMode mode) => mode == ExportDialogMode.ContinueKnown ? "continue_known" : "cancel";

        public static ExportDialogDecision Decide(ExportDialogMode mode, string dialogId, string message)
        {
            Entry known = Catalog.FirstOrDefault(e => string.Equals(dialogId, e.IdContains, StringComparison.OrdinalIgnoreCase));
            var d = new ExportDialogDecision
            {
                DialogId = dialogId, Message = message,
                InCatalog = known != null, CatalogKey = known?.Key,
                Action = "cancel", Result = CancelResult, ButtonVerified = true
            };

            if (OffersReload(dialogId, message))
            {
                d.Reason = "cancelled: the dialog offers a reload and reload is never automatic.";
                return d;
            }
            if (known != null && mode == ExportDialogMode.ContinueKnown)
            {
                d.Action = "continue"; d.Result = known.ContinueResult;
                d.ButtonVerified = known.Verified; d.Reason = known.Why;
                return d;
            }
            if (known != null)
                d.Reason = "cancelled: dialog_policy is 'cancel' (the default). This dialog is in the catalog (" + known.What +
                           "); dialog_policy='continue_known' continues past it without reloading.";
            else
                d.Reason = "cancelled: this dialog is not in the catalog of known export dialogs" +
                           (mode == ExportDialogMode.ContinueKnown ? ", so dialog_policy='continue_known' does not apply to it." : ".");
            return d;
        }

        private static bool OffersReload(string dialogId, string message)
            => (dialogId ?? "").IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0
               || (message ?? "").IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The catalog as the reply and the docs show it.</summary>
        public static JArray CatalogJson() => new JArray(Catalog.Select(e => new JObject
        {
            ["key"] = e.Key, ["dialog_id_contains"] = e.IdContains, ["what"] = e.What,
            ["continue_answer"] = "acknowledge, no reload", ["button_verified_in_revit"] = e.Verified
        }));

        /// <summary>The block the export reply carries: what was asked, the default, and every decision taken.</summary>
        public static JObject Block(ExportDialogMode mode, bool requested, IList<ExportDialogDecision> decisions)
        {
            var list = new JArray((decisions ?? new List<ExportDialogDecision>()).Select(x => (JToken)x.ToJson()));
            return new JObject
            {
                ["policy"] = Name(mode),
                ["requested"] = requested,
                ["default"] = "cancel",
                ["dialogs_seen"] = list.Count,
                ["decisions"] = list,
                ["catalog"] = CatalogJson(),
                ["reload_is_never_automatic"] = true
            };
        }

        /// <summary>
        /// Puts the cause first. "PDF exporter rejected output" says nothing; the dialog
        /// that was cancelled is the reason, so it leads the message.
        /// </summary>
        public static string CauseFirst(IList<ExportDialogDecision> decisions, string baseMessage)
        {
            if (decisions == null || decisions.Count == 0) return baseMessage;
            ExportDialogDecision first = decisions.FirstOrDefault(x => x.Action == "cancel") ?? decisions[0];
            string lead = "Revit raised the dialog '" + first.DialogId + "' during the export and the bridge answered: " +
                          first.Action + ". " + first.Reason + " ";
            return lead + baseMessage;
        }
    }
}
