// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE HRZ OWNERSHIP MARK (FIELD FEEDBACK 4.1): an extensible-storage
// entity "HorizunOwnership" on every element a typed write of this bridge added.
// Fields: tool (which command made it), utc (when), session (which Revit process).
// Public read, vendor write - the same policy as the framing and CAD/IFC stores:
// anyone may see who made an element, only this add-in may claim it.
//
// ADDING A FIELD TO A SCHEMA ALREADY IN A DOCUMENT MAKES THAT DOCUMENT UNREADABLE:
// this field list is frozen; the next field takes a new GUID.
//
// Stamping happens in the dispatcher (AfterCommand), from the ids ChangeWatch saw
// added by the command's committed transactions, in ONE short transaction named
// HrzOwnershipRules.TransactionName. It never fails the command: the reply gets an
// ownership_stamp block {stamped, skipped, error} and nothing else changes.
//
// R2U M-B - THE MARK NAMES A USER. Version 1 (above) cannot take another field, so the user lives in a SECOND schema, HorizunOwnershipV2
// (tool, utc, session, user), under a new GUID. Both are written on every stamp: an add-in that only knows version 1 keeps reading its mark,
// and version 2 adds who made it. The identity guard believes a mark only when version 2 names THIS user (Revit's Application.Username, or
// the Windows user when Revit has none); a mark that has only version 1 (made before this change), another user's mark and an unreadable one
// are somebody else's. Consequence, documented in docs/IDENTITY-GUARD.md: an element Horizun marked before version 2 needs its unique_id
// for a write that carries only expected_category / expected_type, even in a model that is not collaborative.
//
// R2V M-4 - WHO THE USER IS. Version 2 also records the Windows account (winuser) and its SID (sid). R2V added the two fields to the same schema; R2W m-5
// corrected that: widening a schema in place breaks a document that holds it, so the 6-field definition has its OWN GUID (see SchemaGuidV2 for the table
// of GUIDs). A document stamped by a development build of R2U holds the old 4-field schema, which this build does not read: such an element counts as
// not ours (fail closed) and a new stamp goes to the new schema. Users are compared by HrzOwnershipRules.SameUser: by SID when both sides have it, else by name without domain,
// "os:" prefix or case. LIMIT: two people on one generic account with no SID in the mark look like the same user (docs/IDENTITY-GUARD.md).
//
// R2V m-g - A STAMP IS BOTH VERSIONS OR IT IS NOT A STAMP. If version 2 cannot be written, version 1 is removed again from that element; if that
// fails too, the element is reported skipped (version 1 alone has no user, so the guard reads it as somebody else's).
//
// 5.0.4 - NO PERSONAL DATA IN THE MODEL. Version 2 wrote the Revit user name, the Windows account (DOMAIN\user, which names the machine on a
// local account) and the SID into every element it marked, and so into templates that are handed out. Version 3 (HorizunOwnershipV3, its
// own GUID) records tool, utc, session, an ANONYMOUS owner id (a random GUID per Windows user and machine, %USERPROFILE%\.horizun\owner-id)
// and the MCP version - nothing that names a person or a machine. A stamp writes version 1 and version 3 and removes a version 2 entity the
// element already had. Version 2 marks already in documents are still READ for the ownership rule (so old work stays ours), but their personal
// fields are never echoed in a reply. The owner id is compared alone: two people on one Windows account on one machine look like one owner.
//
// KNOWN LIMITS. Extensible storage travels with a copy, so an element a person
// copies from a marked one is marked too. The stamp is its own undo step in Revit:
// a manual Ctrl+Z first removes the mark, then the command's work.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using MarkFacts = Horizun.Revit.Core.HrzOwnershipRules.MarkFacts;

namespace Horizun.Revit.Core
{
    public static class HrzOwnership
    {
        public static readonly Guid SchemaGuid = new Guid("c5a8bf46-fb81-48fe-963e-fd79fb4b700d");
        public const string SchemaName = "HorizunOwnership";
        private const string FTool = "tool", FUtc = "utc", FSession = "session";

        /// <summary>
        /// Version 2 of the mark: the same three fields and the user, the Windows account and its SID (R2U M-B, R2V M-4). A new schema, never a field added to version 1.
        /// R2W m-5, the table of GUIDs: c5a8bf46-... = version 1 (rc2, tool/utc/session). 7e3b1d52-0c4a-4a8e-9f6b-2d51c8a3e9b7 = version 2 as R2U defined it (4 fields), NEVER
        /// published: only development builds ever wrote it, and this build neither reads nor writes it (an element carrying only that schema counts as unmarked, fail closed).
        /// ea8c326c-9b78-4025-bf3e-b44621bdd938 = version 2 as it is now (6 fields: + winuser, sid). The name is new too, so that a document that already holds the old schema
        /// does not collide with it when this one is built.
        /// </summary>
        public static readonly Guid SchemaGuidV2 = new Guid("ea8c326c-9b78-4025-bf3e-b44621bdd938");
        public const string SchemaNameV2 = "HorizunOwnershipUserV2";
        private const string FUser = "user", FWindows = "winuser", FSid = "sid";

        /// <summary>Version 3 (5.0.4): tool, utc, session, the anonymous owner id and the MCP version. No user, account, machine or SID.</summary>
        public static readonly Guid SchemaGuidV3 = new Guid("3f6d2a91-7c4e-4b58-a1d0-9e8b5c27f4a3");
        public const string SchemaNameV3 = "HorizunOwnershipV3";
        private const string FOwner = "owner", FVersion = "mcp_version";
        private static Schema _cachedV3;

        private static readonly Lazy<string> _ownerId = new Lazy<string>(() =>
        {
            string root = null;
            try { root = HorizunPaths.DataRoot(); } catch { }
            return string.IsNullOrEmpty(root) ? null : HrzOwnershipRules.OwnerIdFrom(System.IO.Path.Combine(root, "owner-id"));
        });

        /// <summary>The anonymous owner id of this Windows user on this machine, or null when it cannot be kept.</summary>
        public static string OwnerId => _ownerId.Value;

        public static Schema GetOrCreateV3()
        {
            if (_cachedV3 != null && _cachedV3.IsValidObject) return _cachedV3;
            Schema existing = Schema.Lookup(SchemaGuidV3);
            if (existing != null) { _cachedV3 = existing; return _cachedV3; }
            var b = new SchemaBuilder(SchemaGuidV3);
            b.SetSchemaName(SchemaNameV3);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.SetVendorId(HorizunVendor.Id);
            b.SetDocumentation("Horizun ownership mark (HRZ), version 3: created by the Horizun MCP; owner is an anonymous id, no personal data.");
            b.AddSimpleField(FTool, typeof(string));
            b.AddSimpleField(FUtc, typeof(string));
            b.AddSimpleField(FSession, typeof(string));
            b.AddSimpleField(FOwner, typeof(string));
            b.AddSimpleField(FVersion, typeof(string));
            _cachedV3 = b.Finish();
            return _cachedV3;
        }

        /// <summary>The user this Revit stamps and recognises: Revit's own user name, or the Windows user when Revit has none.</summary>
        public static string CurrentUser(Document doc)
        {
            string revit = null;
            try { revit = doc?.Application?.Username; } catch { }
            string os = null;
            try { os = Environment.UserName; } catch { }
            return HrzOwnershipRules.UserIdentity(revit, os);
        }

        /// <summary>The user of this Revit as the mark records and compares it: Revit's name, the Windows account and its SID (any may be null).</summary>
        public static HrzOwnershipRules.UserRef CurrentUserRef(Document doc)
        {
            string windows = null, sid = null;
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    windows = id?.Name;
                    sid = id?.User?.Value;
                }
            }
            catch { }
            // User, account and SID stay in memory only, to recognise version 2 marks made before 5.0.4; they are never written to a model.
            return new HrzOwnershipRules.UserRef { User = CurrentUser(doc), Windows = windows, Sid = sid, Owner = OwnerId };
        }

        // Version 2 is only READ since 5.0.4 (it holds personal data): there is no builder for it any more.

        /// <summary>This Revit process's session id: the mark says which session made an element.</summary>
        public static readonly string Session = Guid.NewGuid().ToString("N").Substring(0, 12);

        private static Schema _cached;

        public static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return _cached; }
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.SetVendorId(HorizunVendor.Id);
            b.SetDocumentation("Horizun ownership mark (HRZ): this element was created by the Horizun MCP.");
            b.AddSimpleField(FTool, typeof(string));
            b.AddSimpleField(FUtc, typeof(string));
            b.AddSimpleField(FSession, typeof(string));
            _cached = b.Finish();
            return _cached;
        }

        /// <summary>
        /// Marks the elements, inside a transaction the CALLER already opened. Per-element
        /// failures are counted as skipped (an element that takes no entity), never thrown.
        /// </summary>
        public static int Stamp(Document doc, IEnumerable<ElementId> ids, string tool, out int skipped, List<string> reasons = null, List<ElementId> written = null)
        {
            skipped = 0;
            int stamped = 0;
            Schema schema = GetOrCreate();
            Schema schemaV3 = GetOrCreateV3();
            Schema legacyV2 = null;
            try { legacyV2 = Schema.Lookup(SchemaGuidV2); } catch { }
            string owner = OwnerId ?? "";
            string version = "";
            try { version = Build.Version ?? ""; } catch { }
            string utc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            bool workshared = false;
            try { workshared = doc.IsWorkshared; } catch { }
            foreach (ElementId id in ids)
            {
                Element e = null;
                try { e = doc.GetElement(id); } catch { }
                if (e == null || !e.IsValidObject) { skipped++; Note(reasons, "gone"); continue; }
                if (e.Category == null && !(e is View)) { skipped++; Note(reasons, "internal (no category)"); continue; }
                if (workshared)
                {
                    try
                    {
                        if (WorksharingUtils.GetCheckoutStatus(doc, id) == CheckoutStatus.OwnedByOtherUser)
                        { skipped++; Note(reasons, "owned by another user"); continue; }
                    }
                    catch { }
                }
                // R2V m-g: both versions or none. v1 first (an add-in that only knows it keeps reading its mark); if v2 fails, v1 is taken off again.
                bool v1 = false, v2 = false, rolledBack = false;
                try
                {
                    var entity = new Entity(schema);
                    entity.Set(FTool, tool ?? "");
                    entity.Set(FUtc, utc);
                    entity.Set(FSession, Session);
                    e.SetEntity(entity);
                    v1 = true;
                }
                catch { }
                if (v1)
                {
                    try
                    {
                        var entityV3 = new Entity(schemaV3);
                        entityV3.Set(FTool, tool ?? "");
                        entityV3.Set(FUtc, utc);
                        entityV3.Set(FSession, Session);
                        entityV3.Set(FOwner, owner);
                        entityV3.Set(FVersion, version);
                        e.SetEntity(entityV3);
                        v2 = true;
                        // An older version 2 entity on this element holds personal data: it goes.
                        if (legacyV2 != null)
                        {
                            try { Entity old = e.GetEntity(legacyV2); if (old != null && old.IsValid()) e.DeleteEntity(legacyV2); } catch { }
                        }
                    }
                    catch { }
                    if (!v2)
                    {
                        try { rolledBack = e.DeleteEntity(schema); } catch { rolledBack = false; }
                    }
                }
                switch (HrzOwnershipRules.StampResult(v1, v2, rolledBack))
                {
                    case HrzOwnershipRules.StampOutcome.Stamped: stamped++; written?.Add(id); break;
                    case HrzOwnershipRules.StampOutcome.SkippedV1RolledBack: skipped++; Note(reasons, "the version 2 mark could not be written (version 1 was removed again)"); break;
                    case HrzOwnershipRules.StampOutcome.SkippedV1Left: skipped++; Note(reasons, "the version 2 mark could not be written and version 1 could not be removed: it counts as somebody else's"); break;
                    default: skipped++; Note(reasons, "does not accept extensible storage"); break;
                }
            }
            return stamped;
        }

        private static void Note(List<string> reasons, string r)
        {
            if (reasons != null && !reasons.Contains(r) && reasons.Count < 8) reasons.Add(r);
        }

        /// <summary>True when the element carries the HRZ mark.</summary>
        public static bool IsOwned(Element element)
        {
            try
            {
                if (element == null) return false;
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema != null) { Entity e = element.GetEntity(schema); if (e != null && e.IsValid()) return true; }
                Schema v2 = Schema.Lookup(SchemaGuidV2);
                if (v2 != null) { Entity e2 = element.GetEntity(v2); if (e2 != null && e2.IsValid()) return true; }
                Schema v3 = Schema.Lookup(SchemaGuidV3);
                if (v3 != null) { Entity e3 = element.GetEntity(v3); if (e3 != null && e3.IsValid()) return true; }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// The mark, believed (R2T M-2, R2U M-B, R2V M-2): present, naming THIS user (<see cref="HrzOwnershipRules.SameUser"/>), and not the copy of a mark
        /// made in this session (<see cref="HrzOwnershipRules.Owned"/>, which asks the ledger of creations by UniqueId). What the identity guard calls
        /// "ours", and the one question every ownership gate asks: the guard, the delete cascade, view_visibility, view_crop and sheet_layout.
        /// </summary>
        public static bool IsTrustedOwned(Document doc, Element element)
        {
            try
            {
                MarkFacts facts = ReadFacts(element);
                if (!facts.Present) return false;
                return HrzOwnershipRules.Owned(ChangeWatch.Key(doc), element.UniqueId, facts, Session, CurrentUserRef(doc));
            }
            catch { return false; }
        }

        /// <summary>Stable alias of IsOwned for OwnershipProbe.Mark (Pro-B): the element carries the HRZ mark.</summary>
        public static bool IsMarked(Element element) => IsOwned(element);

        // An override for genuinely unmarked elements must distinguish absence from a failed read.
        internal static bool? MarkPresence(Element element)
        {
            try
            {
                return element.GetEntitySchemaGuids().Any(g => g == SchemaGuid || g == SchemaGuidV2 || g == SchemaGuidV3);
            }
            catch { return null; }
        }

        private static string Field(Entity e, string name)
        {
            try { return e.Get<string>(name); }
            catch { return null; }
        }

        /// <summary>What was READ about the mark of an element (version 2 preferred); Present=false when it has none or it cannot be read.</summary>
        public static MarkFacts ReadFacts(Element element)
        {
            var none = new MarkFacts { Present = false };
            try
            {
                if (element == null) return none;
                Schema v3 = Schema.Lookup(SchemaGuidV3);
                Entity e3 = v3 == null ? null : element.GetEntity(v3);
                if (e3 != null && e3.IsValid())
                {
                    // An empty or malformed owner is nobody's: SameUser refuses an owner that is not 32 hex digits (fail closed).
                    string owner = Field(e3, FOwner);
                    return new MarkFacts
                    {
                        Present = true, Session = Field(e3, FSession),
                        User = new HrzOwnershipRules.UserRef { Owner = string.IsNullOrWhiteSpace(owner) ? "-" : owner }
                    };
                }
                Schema v2 = Schema.Lookup(SchemaGuidV2);
                Entity e2 = v2 == null ? null : element.GetEntity(v2);
                if (e2 != null && e2.IsValid())
                    return new MarkFacts
                    {
                        Present = true, Session = Field(e2, FSession),
                        User = new HrzOwnershipRules.UserRef { User = Field(e2, FUser), Windows = Field(e2, FWindows), Sid = Field(e2, FSid) }
                    };
                Schema schema = Schema.Lookup(SchemaGuid);
                Entity e = schema == null ? null : element.GetEntity(schema);
                if (e == null || !e.IsValid()) return none;
                return new MarkFacts { Present = true, Session = Field(e, FSession), User = null };   // version 1: no user
            }
            catch { return none; }
        }

        /// <summary>The mark as JSON, or null when the element has none.</summary>
        public static JObject Read(Element element)
        {
            try
            {
                if (element == null) return null;
                Schema v3 = Schema.Lookup(SchemaGuidV3);
                Entity e3 = v3 == null ? null : element.GetEntity(v3);
                if (e3 != null && e3.IsValid())
                    return new JObject { ["tool"] = Field(e3, FTool), ["utc"] = Field(e3, FUtc), ["session"] = Field(e3, FSession), ["owner"] = Field(e3, FOwner), ["mcp_version"] = Field(e3, FVersion), ["mark_version"] = 3 };
                Schema v2 = Schema.Lookup(SchemaGuidV2);
                Entity e2 = v2 == null ? null : element.GetEntity(v2);
                // A version 2 mark holds a user name, a Windows account and a SID: they are never echoed (5.0.4).
                if (e2 != null && e2.IsValid())
                    return new JObject { ["tool"] = Field(e2, FTool), ["utc"] = Field(e2, FUtc), ["session"] = Field(e2, FSession), ["mark_version"] = 2,
                                         ["personal_fields"] = "present in the model, not shown; restamping the element replaces this mark with version 3" };
                Schema schema = Schema.Lookup(SchemaGuid);
                Entity e = schema == null ? null : element.GetEntity(schema);
                if (e == null || !e.IsValid()) return null;
                return new JObject { ["tool"] = e.Get<string>(FTool), ["utc"] = e.Get<string>(FUtc), ["session"] = e.Get<string>(FSession) };   // version 1: no user
            }
            catch { return null; }
        }

        /// <summary>A Horizun write of THIS Revit session added the element (ledger of creations, by UniqueId: R2U M-A).</summary>
        public static bool CreatedThisSession(Document doc, Element element)
        {
            try { return SessionCreationLedger.Created(ChangeWatch.Key(doc), element?.UniqueId); }
            catch { return false; }
        }

        /// <summary>
        /// The belief of the restricted tools' gate (view_crop, sheet_layout: HrzOwnershipRules.GateTrusts - this user's mark; a version 1
        /// mark only in a model that is not collaborative; or created in this session) WITHOUT an override: null when the element is ours,
        /// else why not, in words. Same inputs as <see cref="Require"/>.
        /// </summary>
        public static string GateReason(Document doc, Element e)
        {
            bool created = CreatedThisSession(doc, e);
            if (created) return null;
            return HrzOwnershipRules.RefusalReason(ReadFacts(e), Session, CurrentUserRef(doc), created, doc.IsWorkshared);
        }

        /// <summary>The restricted tools' gate: marked, created this session, or overridden. Throws the refusal.</summary>
        public static void Require(Document doc, Element e, string what, bool overrideFlag, string overrideName)
        {
            // R2V M-2: the same belief as the guard (another user's mark and a mark of version 1 are somebody else's), not the mere presence of a mark.
            string refusal = HrzOwnershipRules.RequireRefusal(what, Rid.Value(e.Id), ReadFacts(e), Session, CurrentUserRef(doc), CreatedThisSession(doc, e), overrideFlag, overrideName, doc.IsWorkshared);
            if (refusal != null) throw new ArgumentException(refusal);
        }

        /// <summary>
        /// The dispatcher's hook, after a command returned and its spatial check ran: stamp
        /// what the command's committed transactions added, in one short transaction per
        /// document, and add ownership_stamp to the reply. Never throws; never turns a
        /// success into a failure.
        /// </summary>
        internal static void AfterCommand(string tool, CommandContract contract, ChangeWatch changes, CommandResult result)
        {
            if (result == null || changes == null || contract == null) return;
            bool failed = !result.Success;
            JObject data = null;
            int stamped = 0, skipped = 0;
            string error = null;
            var reasons = new List<string>();
            try
            {
                bool mutating = contract.Effect == ToolEffect.Mutating || contract.Effect == ToolEffect.MutatingUnlessDryRun;
                if (failed)
                {
                    // A failure's work that stayed in the model is still ours (4.2-a): see ShouldStampAfterFailure.
                    if (!HrzOwnershipRules.ShouldStampAfterFailure(tool, mutating)) return;
                    data = new JObject();
                }
                else
                {
                    data = result.Data as JObject ?? (result.Data == null ? new JObject() : JObject.FromObject(result.Data));
                    if (!HrzOwnershipRules.ShouldStamp(tool, mutating, true, data.Value<bool?>("dry_run") == true)) return;
                }
                changes.Settle();
                var work = changes.Documents.Where(d => d.Added.Count > 0).ToList();
                if (work.Count == 0) return;
                foreach (ChangeWatch.DocChanges d in work)
                {
                    Document doc = d.Document;
                    if (doc == null || !doc.IsValidObject || doc.IsLinked || doc.IsFamilyDocument) { skipped += d.Added.Count; Note(reasons, "not a project document"); continue; }
                    if (doc.IsReadOnly || doc.IsModifiable) { skipped += d.Added.Count; Note(reasons, "document not editable now"); continue; }
                    List<ElementId> ids = d.Added.Where(Rid.CanRepresent).OrderBy(v => v).Select(Rid.Make).ToList();
                    // INT-9 m-3: what the write only DUPLICATED (annotations of a legend duplicated with its detailing) is not ours to mark.
                    string docKey = ChangeWatch.Key(doc);
                    int before = ids.Count;
                    ids = ids.Where(id => { try { return !SessionCreationLedger.IsDuplicated(docKey, doc.GetElement(id)?.UniqueId); } catch { return true; } }).ToList();
                    if (ids.Count < before) { skipped += before - ids.Count; Note(reasons, "duplicated from another element, not created by this write"); }
                    if (ids.Count > HrzOwnershipRules.MaxStampedPerCall)
                    {
                        skipped += ids.Count - HrzOwnershipRules.MaxStampedPerCall;
                        Note(reasons, "over the per-call cap of " + HrzOwnershipRules.MaxStampedPerCall);
                        ids = ids.Take(HrzOwnershipRules.MaxStampedPerCall).ToList();
                    }
                    using (var tx = new Transaction(doc, HrzOwnershipRules.TransactionName))
                    {
                        try
                        {
                            if (tx.Start() != TransactionStatus.Started) { skipped += ids.Count; error = "the stamp transaction did not start"; continue; }
                            var written = new List<ElementId>();
                            Stamp(doc, ids, tool, out int sk, reasons, written);
                            TransactionStatus st = tx.Commit();
                            if (st == TransactionStatus.Committed)
                            {
                                skipped += sk;
                                var stampedUids = new List<string>();
                                foreach (ElementId id in written)
                                {
                                    JObject mark = null;
                                    Element stampedElement = null;
                                    try { stampedElement = doc.GetElement(id); mark = Read(stampedElement); } catch { }
                                    if (mark != null && (string)mark["tool"] == (tool ?? "") &&
                                        (string)mark["session"] == Session && !string.IsNullOrEmpty((string)mark["utc"]))
                                    {
                                        stamped++;
                                        try { if (stampedElement != null) stampedUids.Add(stampedElement.UniqueId); } catch { }
                                    }
                                    else { skipped++; Note(reasons, "mark could not be read back"); }
                                }
                                // INT-9 m-1: marked = believed. A failed call never reached the ledger of creations (it is filled on success),
                                // so what it left in the model and we marked is entered here: the same session reads it as its own, not as a copy.
                                if (HrzOwnershipRules.ShouldRecordCreationAfterFailure(failed, tool))
                                    SessionCreationLedger.RecordFor(tool, ChangeWatch.Key(doc), stampedUids);
                            }
                            else { skipped += ids.Count; error = "the stamp transaction ended " + st; }
                        }
                        catch (Exception ex)
                        {
                            try { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); } catch { }
                            skipped += ids.Count;
                            error = ex.Message;
                        }
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            try
            {
                if (data == null) return;
                if (stamped == 0 && skipped == 0 && error == null) return;
                JObject report = HrzOwnershipRules.Report(stamped, skipped, error, reasons);
                if (failed) { result.ReplaceDetail(HrzOwnershipRules.MergeIntoDetail(result.Detail, report)); return; }
                data["ownership_stamp"] = report;
                result.ReplaceData(data);
            }
            catch { /* the command's own reply stands */ }
        }
    }
}
