// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of SyncLedgerRules: after every command the dispatcher calls
// AfterCommand, which (1) records a committed write on a WORKSHARED document with its
// spatial check and, for a script, its evidence status; (2) lets a clean
// horizun_verify_changes check clear the blockers it covered; and (3) stamps
// `pending_sync` on the reply of every non-read tool while the active workshared
// document has local work that has not been synchronized. Never fails the call.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class SyncReminder
    {
        public static void AfterCommand(UIApplication app, string tool, string paramsJson, ChangeWatch watch, CommandResult result)
        {
            try
            {
                if (result == null) return;
                CommandContract contract = Contract.Find(tool);
                // The request is parsed ONCE, and only when something needs it (R2V m-c: it used to be parsed twice).
                JObject request = null;
                bool requestParsed = false;
                JObject Request()
                {
                    if (!requestParsed)
                    {
                        requestParsed = true;
                        try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); } catch { request = new JObject(); }
                    }
                    return request;
                }
                string readOperation = null;
                if (result.Success && IdFreshnessRules.ReadsIds(tool))
                {
                    try { readOperation = Request().Value<string>("operation"); } catch { readOperation = null; }
                }
                bool handsOutIds = result.Success && IdFreshnessRules.ReadsIds(tool, readOperation);
                bool validatesIds = result.Success && IdFreshnessRules.IsValidateIds(tool, readOperation);
                bool writesIds = result.Success && !handsOutIds && !validatesIds && contract != null &&
                                 (contract.Effect == ToolEffect.Mutating || contract.Effect == ToolEffect.MutatingUnlessDryRun) &&
                                 IsCommittedWrite(tool, result.Data as JObject);
                if (handsOutIds || validatesIds || writesIds)
                {
                    // R2U M-A: not any read clears the notice; the reply is handed over so that only a revalidation of the ids held does.
                    // R2V M-3: the ids a WRITE returns are tracked as well (three notes created, a synchronize, a read of the levels).
                    Document readDoc = app?.ActiveUIDocument?.Document;
                    if (readDoc != null)
                    {
                        // The reply is read in place when it already is a JSON tree; it is converted only when it is not (R2V m-c).
                        JToken reply = result.Data as JToken ?? (result.Data == null ? null : JToken.FromObject(result.Data));
                        JObject changed = IdFreshnessRules.NoteReply(ChangeWatch.Key(readDoc), DateTime.UtcNow, tool, readOperation, reply, writesIds);
                        if (changed != null && result.Data is JObject replyObject)
                        {
                            replyObject["ids_changed"] = changed;
                            result.ReplaceData(replyObject);
                        }
                    }
                }
                if (contract == null || contract.Effect == ToolEffect.ReadOnly || contract.Effect == ToolEffect.HostState) return;
                JObject data = result.Success ? result.Data as JObject : result.Detail;
                if (data == null) return;
                if (!SyncLedgerRules.ShouldRecordResult(result.Success, data)) return;
                SaveSuggestion(app, tool, Request(), watch, result, data);
                if (tool == "horizun_verify_changes" && result.Success) ClearOnCleanCheck(app, Request(), data);
                else if (IsCommittedWrite(tool, data) && watch != null)
                {
                    foreach (ChangeWatch.DocChanges d in watch.Documents)
                    {
                        if (d.Added.Count + d.Modified.Count + d.Deleted.Count == 0) continue;
                        Document doc = d.Document;
                        if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument || !doc.IsWorkshared) continue;
                        SyncLedgerRules.RecordWrite(ChangeWatch.Key(doc), SyncLedgerRules.FromResult(tool, result.Success, data,
                            d.Added.Concat(d.Modified).Concat(d.Deleted).Distinct().ToArray()));
                    }
                }

                // INT-11 A: pending_sync is the block of the document the call OPERATED ON. horizun_save_document (every operation) and
                // horizun_document_session sync_with_central name it in target_document; the reply of synchronizing L2 used to carry the
                // block of the ACTIVE document (L3). The active document's block, when it is another document, goes in
                // active_document_pending_sync, with its document named. Any other tool keeps the active document's block, as before.
                Document active = app?.ActiveUIDocument?.Document;
                ReminderCapabilities caps = null;
                JObject BlockOf(Document d)
                {
                    if (d == null) return null;
                    try { if (d.IsFamilyDocument) return null; } catch { return null; }
                    bool workshared = false; bool? modified = null;
                    try { workshared = d.IsWorkshared; } catch { }
                    try { modified = d.IsModified; } catch { }
                    JObject b = SyncLedgerRules.PendingBlock(ChangeWatch.Key(d), workshared, modified);
                    if (b == null) return null;
                    ReminderCapabilities.Apply(b, caps ?? (caps = Capabilities()));
                    b["document"] = SafeTitle(d);
                    return b;
                }
                bool stamped = false;
                string named = OperatedDocumentName(tool, Request());
                if (named == null)
                {
                    JObject block = BlockOf(active);
                    if (block != null) { data["pending_sync"] = block; stamped = true; }
                }
                else
                {
                    // An unresolved name (closed, ambiguous) never borrows the active document's block.
                    Document target = ResolveNamed(app, named);
                    JObject targetBlock = BlockOf(target);
                    if (targetBlock != null) { data["pending_sync"] = targetBlock; stamped = true; }
                    if (!SameDocument(target, active))
                    {
                        JObject activeBlock = BlockOf(active);
                        if (activeBlock != null) { data["active_document_pending_sync"] = activeBlock; stamped = true; }
                    }
                }
                if (stamped && result.Success) result.ReplaceData(data);
            }
            catch (Exception ex) { Log.Error("pending_sync stamp failed for '" + tool + "'", ex); }
        }

        /// <summary>
        /// INT-11 A: the name of the document a call operates on when it is not necessarily the active one - horizun_save_document (any
        /// operation) and horizun_document_session operation=sync_with_central - read from target_document or its aliases. null = the
        /// call's reminder is the active document's (every other tool, or no name given).
        /// </summary>
        internal static string OperatedDocumentName(string tool, JObject request)
        {
            if (request == null) return null;
            string operation = null;
            try { operation = request.Value<string>("operation"); } catch { }
            bool names = tool == "horizun_save_document" || (tool == "horizun_document_session" && operation == "sync_with_central");
            if (!names) return null;
            foreach (string field in new[] { "target_document", "expected_document", "target_document_title" })
            {
                string value = null;
                try { value = request.Value<string>(field); } catch { }
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }

        /// <summary>The one open, non-linked document with that title (or that full path), or null when none or several match.</summary>
        private static Document ResolveNamed(UIApplication app, string name)
        {
            System.Collections.IEnumerable documents = null;
            try { documents = app?.Application?.Documents; } catch { }
            if (documents == null || string.IsNullOrWhiteSpace(name)) return null;
            bool byPath = false;
            try { byPath = System.IO.Path.IsPathRooted(name); } catch { }
            Document found = null;
            int matches = 0;
            foreach (object o in documents)
            {
                if (!(o is Document d)) continue;
                try
                {
                    if (d.IsLinked) continue;
                    string have = byPath ? d.PathName : d.Title;
                    if (!string.Equals(have, name, StringComparison.OrdinalIgnoreCase)) continue;
                }
                catch { continue; }
                found = d;
                matches++;
            }
            return matches == 1 ? found : null;
        }

        private static bool SameDocument(Document a, Document b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            try { return string.Equals(ChangeWatch.Key(a), ChangeWatch.Key(b), StringComparison.Ordinal); } catch { return false; }
        }

        /// <summary>Health's "a synchronize happened since ids were last read" notice for the active document, or null.</summary>
        public static JObject IdsMayHaveChanged(Document active)
        {
            try { return active == null || active.IsFamilyDocument ? null : IdFreshnessRules.Block(ChangeWatch.Key(active)); }
            catch { return null; }
        }

        /// <summary>Health's block for the active document, or null.</summary>
        public static JObject ForHealth(Document active)
        {
            try
            {
                if (active == null || active.IsFamilyDocument) return null;
                JObject b = SyncLedgerRules.PendingBlock(ChangeWatch.Key(active), active.IsWorkshared, active.IsModified);
                if (b != null) { ReminderCapabilities.Apply(b, Capabilities()); b["document"] = SafeTitle(active); }
                return b;
            }
            catch { return null; }
        }

        /// <summary>
        /// LP-CP-5: counts committed writes per document and, from the Nth, adds save_suggestion to the reply.
        /// It only suggests: nothing here saves (Core/SaveSuggestionRules.cs).
        /// </summary>
        private static void SaveSuggestion(UIApplication app, string tool, JObject request, ChangeWatch watch, CommandResult result, JObject data)
        {
            try
            {
                Document active = app?.ActiveUIDocument?.Document;
                if (active == null || active.IsFamilyDocument) return;
                string key = ChangeWatch.Key(active);
                if (tool == "horizun_save_document")
                {
                    string op = (string)request["operation"] ?? "save_local";
                    if (result.Success && op == "save_local" && data.Value<bool?>("dry_run") != true) SaveSuggestionRules.RecordSave(key);
                    return;
                }
                if (result.Success && IsCommittedWrite(tool, data) && watch != null)
                    foreach (ChangeWatch.DocChanges d in watch.Documents)
                    {
                        if (d.Document == null || !d.Document.IsValidObject) continue;
                        if (d.Added.Count + d.Modified.Count + d.Deleted.Count == 0) continue;
                        SaveSuggestionRules.RecordWrite(ChangeWatch.Key(d.Document));
                    }
                bool? modified = null;
                try { modified = active.IsModified; } catch { }
                if (!result.Success) return;
                JObject block = SaveSuggestionRules.Block(key,
                    SaveSuggestionRules.ThresholdFrom(Environment.GetEnvironmentVariable(SaveSuggestionRules.ThresholdVariable)),
                    modified, SafeTitle(active));
                if (block == null || !IsCommittedWrite(tool, data)) return;
                data["save_suggestion"] = block;
                result.ReplaceData(data);
            }
            catch (Exception ex) { Log.Error("save suggestion failed for '" + tool + "'", ex); }
        }

        /// <summary>
        /// What this session may actually call, read from the same admission tools/list uses.
        /// Unreadable settings fall to "not available": a reminder never promises a tool it cannot prove.
        /// </summary>
        internal static ReminderCapabilities Capabilities()
        {
            var caps = new ReminderCapabilities();
            try
            {
                // One read of the settings decides the profile, the owner's grant and the flag together (INT-6 m-1).
                Settings.SyncSettingsSnapshot syncSettings = Settings.ReadSyncSettings();
                caps.Profile = syncSettings.Profile;
                CommandContract verify = Contract.Find("horizun_verify_changes");
                // The same photo decides whether the two tools are listed (INT-7 m-k): no second and third read of the file.
                caps.VerifyAvailable = verify != null && Settings.IsToolAllowed(verify, out string _, syncSettings);
                CommandContract save = Contract.Find("horizun_save_document");
                ReminderCapabilities decided = ReminderCapabilities.For(syncSettings.Profile, syncSettings.OwnerSyncGrant,
                    syncSettings.SyncOnlyFlag, caps.VerifyAvailable, save != null && Settings.IsToolAllowed(save, out string _, syncSettings));
                caps.SyncOnly = decided.SyncOnly;
                caps.SyncAvailable = decided.SyncAvailable;
            }
            catch { }
            return caps;
        }

        private static bool IsCommittedWrite(string tool, JObject data)
        {
            if (data == null) return true;
            if (data.Value<bool?>("dry_run") == true) return false;
            if (tool == "horizun_execute_python" && (data.Value<bool?>("read_only") == true || data["rehearsal"] != null)) return false;
            if (tool == "horizun_save_document") return false;
            return true;
        }

        private static void ClearOnCleanCheck(UIApplication app, JObject request, JObject data)
        {
            string op = (string)request["operation"] ?? "check";
            if (op != "check") return;
            JObject sc = data["spatial_check"] as JObject;
            if (!SyncLedgerRules.IsCleanSpatialCheck(sc)) return;
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared) return;
            string key = ChangeWatch.Key(doc);
            var checkedIds = new List<long>();
            if (request["element_ids"] is JArray ids && ids.Count > 0)
                checkedIds.AddRange(ids.Select(t => (long)t));
            else if ((string)request["scope"] == "session")
            {
                DateTime? since = null;
                if (request["since_utc"] != null)
                {
                    if (!DateTime.TryParse((string)request["since_utc"], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed)) return;
                    since = parsed;
                }
                var rows = ChangeLedger.HistoryFor(doc).Select(h => new SessionScopeRules.WriteEntry
                    { AtUtc = h.AtUtc, Tool = h.Tool, Added = h.Added, Modified = h.Modified }).ToList();
                var union = SessionScopeRules.Union(rows, since);
                checkedIds.AddRange(union.Ids.Where(Rid.CanRepresent));
            }
            else
            {
                ChangeLedger.Entry last = ChangeLedger.For(doc);
                if (last != null) checkedIds.AddRange((last.Added ?? new long[0]).Concat(last.Modified ?? new long[0]).Where(Rid.CanRepresent));
            }
            checkedIds = checkedIds.Distinct().ToList();
            JObject scope = data["scope"] as JObject;
            if (checkedIds.Count == 0 || scope == null || scope.Value<int?>("model_elements") != checkedIds.Count) return;
            SyncLedgerRules.ClearVerified(key, checkedIds, false);
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
    }
}
