// -----------------------------------------------------------------------------
// Horizun Revit MCP - structural steel connections. Original Horizun code.
//
// G09 of the 2026-09-14 competitive inventory asked for "armaduras y acero".
// Re-verified against 1.3.3, the armadura half was already there and thorough -
// horizun_plan_reinforcement, horizun_apply_reinforcement, horizun_audit_rein-
// forcement, plus RebarApi, RebarArrayGeometry, StirrupZoneRules, RebarTakeoff.
// The steel half was not there at all: StructuralConnectionHandler appeared
// nowhere in the source.
//
// WHAT IS AND IS NOT POSSIBLE HERE, checked against the installed API rather
// than remembered:
//
//   StructuralConnectionHandler and StructuralConnectionHandlerType live in
//   RevitAPI.dll, so a connection between named members can be created without
//   the Steel extension being loaded. What a DETAILED connection then generates
//   - plates, bolts, welds - belongs to RevitAPISteel and to the Steel
//   Connections add-in; this command does not reach into it and does not pretend
//   to. It creates the connection and reports what Revit made of it.
//
//   A GENERIC connection (no type) is always available and is what Revit itself
//   places when you connect members without choosing a family. A TYPED one needs
//   its type loaded in the document, and the refusal for a missing type lists
//   what IS loaded rather than saying "not found".
//
// THE REFUSAL THAT MATTERS MOST: incompatible members. Revit will accept a
// connection request over elements that cannot carry one and produce an element
// that connects nothing. Every member is therefore checked for a structural
// category and for an analytical/physical presence BEFORE the transaction, and
// the refusal names the element and the reason - not "Revit returned null".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class StructuralConnectionsCommand : ICommand
    {
        public string Name => "horizun_structural_connections";

        public string Description =>
            "Create structural connections between named steel members, with every member checked first and the " +
            "connection re-read from the model afterwards.";

        /// <summary>
        /// The categories a connection can actually join. Checked by CATEGORY rather than
        /// by class: a beam can be a FamilyInstance and so can a door, and the difference
        /// is exactly what a caller who passed the wrong id needs told.
        /// </summary>
        private static readonly BuiltInCategory[] Connectable =
        {
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFoundation,
            BuiltInCategory.OST_StructuralStiffener,
            BuiltInCategory.OST_StructuralTruss,
            BuiltInCategory.OST_VerticalBracing,
            BuiltInCategory.OST_HorizontalBracing
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray raw = request["actions"] as JArray;
            if (raw == null || raw.Count < 1 || raw.Count > 100)
                return CommandResult.Fail("actions must contain 1..100 entries.");

            string error;
            List<Plan> plans = PlanAll(doc, raw, out error);
            if (plans == null) return CommandResult.Fail(error + " Nothing was written.");

            string hash = DocumentGate.PlanHash(request, "actions");
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            ResolvedPlan resolved = Resolved(gate, app, plans);

            if (dry)
            {
                Rehearsal rehearsal = Rehearse(doc, plans);
                if (!rehearsal.RollbackConfirmed)
                    return CommandResult.FailWithDetail(
                        "The connection rehearsal could not confirm its rollback; model state is uncertain.",
                        new JObject
                        {
                            ["state"] = "uncertain",
                            ["rollback_status"] = rehearsal.RollbackStatus,
                            ["write_started"] = true
                        });
                if (!rehearsal.Verified)
                    return CommandResult.FailWithDetail(
                        "The rehearsal created the connections and could not read them back. Nothing was " +
                        "committed. Revit accepts a connection over members that cannot carry one and produces " +
                        "an element that connects nothing; this is that case, caught before it shipped.",
                        new JObject { ["state"] = "refused", ["rehearsal"] = rehearsal.Json });

                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["valid"] = plans.Count,
                    ["rehearsal"] = rehearsal.Json,
                    ["plan"] = new JArray(plans.Select(p => p.Json()))
                };
                ApplicationOutcome.StampRehearsal(preview, plans.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, Name, hash, true,
                    "the token binds every member's unique id and type, so a member swapped between the " +
                    "rehearsal and the apply refuses as a stale plan.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name") ?? "Horizun: structural connections";
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Could not start the connection TransactionGroup.");
                var tx = new Transaction(doc, txName);
                TransactionStatus txStatus = TransactionStatus.Uninitialized;
                try
                {
                    if (tx.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Could not start the connection transaction.");
                    // ROW BY ROW, RECORDING EACH. The batch stays atomic - a steel
                    // connection creates an element, and half a batch would leave handlers
                    // somebody has to hunt for - but a failure now names which row and why
                    // instead of handing back one exception for up to two hundred.
                    foreach (Plan p in plans)
                    {
                        try
                        {
                            Apply(doc, p);
                            p.AttemptOutcome = p.CreatedId == null
                                ? p.Fail("Revit accepted the request and returned no connection.")
                                : Plan.OutcomeCreated;
                        }
                        catch (Exception rowEx)
                        {
                            p.Fail("failed: " + rowEx.Message);
                        }
                        if (p.AttemptOutcome != Plan.OutcomeCreated)
                            throw new InvalidOperationException(
                                "row " + p.Index + " (" + p.Key + ") " + p.AttemptReason +
                                " The whole batch is being rolled back and NOTHING is written: a partly " +
                                "applied batch would leave connection elements nobody asked for.");
                    }

                    doc.Regenerate();
                    foreach (Plan p in plans)
                        if (!Verify(doc, p))
                        {
                            p.Fail("was created and then did not read back as connecting its members. This " +
                                   "is the failure the verification exists to catch: a factory returning an " +
                                   "element says nothing about what that element connects.");
                            throw new InvalidOperationException(
                                "row " + p.Index + " (" + p.Key + ") could not be read back while the batch " +
                                "was still reversible.");
                        }
                    txStatus = tx.Commit();
                    if (txStatus != TransactionStatus.Committed)
                        throw new InvalidOperationException("The transaction returned " + txStatus + ".");
                    foreach (Plan p in plans)
                        if (!Verify(doc, p))
                        {
                            p.Fail("did not read back after the transaction committed.");
                            throw new InvalidOperationException(
                                "row " + p.Index + " (" + p.Key + ") could not be read back after the " +
                                "transaction committed.");
                        }
                    TransactionStatus assimilated = group.Assimilate();
                    if (assimilated != TransactionStatus.Committed)
                        return CommandResult.FailWithDetail(
                            "The connection group did not assimilate; state is uncertain.",
                            new JObject
                            {
                                ["state"] = "uncertain",
                                ["transaction_group_status"] = assimilated.ToString()
                            });
                }
                catch (Exception ex)
                {
                    Guard.RollbackResult? txRollback = null;
                    try { if (tx.GetStatus() == TransactionStatus.Started) txRollback = Guard.RollBack(tx); } catch { }
                    Guard.RollbackResult? groupRollback;
                    try { groupRollback = Guard.RollBack(group); } catch { groupRollback = null; }
                    bool rolledBack = groupRollback.HasValue && groupRollback.Value.Confirmed;
                    return CommandResult.FailWithDetail(
                        "The connection batch failed and was rolled back: " + ex.Message,
                        new JObject
                        {
                            ["state"] = rolledBack ? "rolled_back" : "uncertain",
                            ["transaction_status"] = txRollback.HasValue
                                ? txRollback.Value.StatusName : txStatus.ToString(),
                            ["transaction_group_status"] = groupRollback.HasValue
                                ? groupRollback.Value.StatusName : "Error",
                            ["rows"] = new JArray(plans.Select(p => p.Attempt())),
                            ["first_failed_row"] = plans.Where(p => p.AttemptOutcome == Plan.OutcomeFailed)
                                                        .Select(p => (JToken)p.Index).FirstOrDefault()
                                                   ?? JValue.CreateNull(),
                            ["rows_mean"] = rolledBack
                                ? "the states of the ATTEMPT, not of the model. The group rolled back, so " +
                                  "none of these connections exists - and an id under created_in_attempt " +
                                  "belongs to an element that was undone. Do not go looking for it; it is " +
                                  "here so the failing row can be found and fixed."
                                : "the states of the attempt. The rollback could NOT be confirmed, so an id " +
                                  "under created_in_attempt may or may not be a live element and the model " +
                                  "has to be read before anything is resent."
                        });
                }
            }

            var done = new JObject
            {
                ["state"] = "committed_verified",
                ["host_verified"] = true,
                ["requested"] = plans.Count,
                ["created"] = plans.Count,
                ["coverage_complete"] = true,
                ["rows"] = new JArray(plans.Select(p => p.Result(doc)))
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed,
                                            plans.Count, plans.Count, plans.Count, 0, 0, 0);
            return CommandResult.Ok(done);
        }

        // =====================================================================
        // Planning
        // =====================================================================

        private static List<Plan> PlanAll(Document doc, JArray raw, out string error)
        {
            error = null;
            var plans = new List<Plan>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < raw.Count; i++)
            {
                JObject a = raw[i] as JObject;
                if (a == null) { error = "actions[" + i + "] is not an object."; return null; }

                string key = a.Value<string>("key");
                if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                { error = "actions[" + i + "].key is empty or duplicated."; return null; }

                JArray memberIds = a["member_ids"] as JArray;
                if (memberIds == null || memberIds.Count < 2 || memberIds.Count > 50)
                {
                    error = "actions[" + i + "].member_ids must hold 2..50 element ids. A connection joins " +
                            "members; one member is not a connection.";
                    return null;
                }

                var members = new List<Element>();
                var ids = new List<ElementId>();
                foreach (JToken token in memberIds)
                {
                    long rawId = token.Value<long?>() ?? -1;
                    if (!Rid.CanRepresent(rawId))
                    { error = "actions[" + i + "].member_ids holds a value that is not an element id."; return null; }

                    Element member = doc.GetElement(Rid.Make(rawId));
                    if (member == null)
                    { error = "actions[" + i + "]: element " + rawId + " does not exist."; return null; }

                    string why = NotConnectable(member);
                    if (why != null)
                    { error = "actions[" + i + "]: element " + rawId + " " + why; return null; }

                    if (ids.Any(existing => Rid.Value(existing) == rawId))
                    { error = "actions[" + i + "]: element " + rawId + " is listed twice."; return null; }

                    members.Add(member);
                    ids.Add(member.Id);
                }

                var plan = new Plan { Index = i, Key = key, Members = members, MemberIds = ids };

                string typeName = a.Value<string>("connection_type");
                long typeId = a.Value<long?>("connection_type_id") ?? -1;
                if (Rid.CanRepresent(typeId) || !string.IsNullOrWhiteSpace(typeName))
                {
                    StructuralConnectionHandlerType type = ResolveType(doc, typeId, typeName, out string typeError);
                    if (type == null) { error = "actions[" + i + "]: " + typeError; return null; }
                    plan.Type = type;
                }

                plans.Add(plan);
            }
            return plans;
        }

        /// <summary>
        /// Why this element cannot carry a structural connection, or null.
        ///
        /// Checked by CATEGORY, not by class. A structural beam and a door are both
        /// FamilyInstance, and a caller who passed the wrong id is far better served by
        /// "that is a door" than by a null reference inside Revit.
        /// </summary>
        private static string NotConnectable(Element element)
        {
            Category category;
            try { category = element.Category; } catch { category = null; }
            if (category == null)
                return "has no category, so it cannot take part in a structural connection.";

            long id = Rid.Value(category.Id);
            if (!Connectable.Any(c => (long)c == id))
                return "is in category '" + category.Name + "', which cannot take part in a structural " +
                       "connection. Connections join structural framing, columns, foundations, stiffeners, " +
                       "trusses and bracing.";

            // A member with no physical geometry connects nothing. Revit will still make
            // the handler, and the result is an element in the project browser that does
            // nothing at all - the failure this check exists to name.
            try
            {
                var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
                GeometryElement geometry = element.get_Geometry(options);
                if (geometry == null)
                    return "reports no geometry, so a connection over it would join nothing.";
            }
            catch { /* a geometry read that throws is not proof of absence; let Revit decide */ }

            return null;
        }

        private static StructuralConnectionHandlerType ResolveType(Document doc, long typeId, string typeName,
                                                                   out string error)
        {
            error = null;
            List<StructuralConnectionHandlerType> loaded = new FilteredElementCollector(doc)
                .OfClass(typeof(StructuralConnectionHandlerType))
                .Cast<StructuralConnectionHandlerType>()
                .OrderBy(t => Rid.Value(t.Id))
                .ToList();

            if (Rid.CanRepresent(typeId))
            {
                StructuralConnectionHandlerType byId = loaded.FirstOrDefault(t => Rid.Value(t.Id) == typeId);
                if (byId != null) return byId;
                error = "connection_type_id " + typeId + " is not a loaded structural connection type.";
            }
            else
            {
                StructuralConnectionHandlerType byName = loaded.FirstOrDefault(
                    t => string.Equals(SafeName(t), typeName, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
                error = "no structural connection type named '" + typeName + "' is loaded.";
            }

            error += loaded.Count == 0
                ? " This document has NO connection types loaded at all, so only a generic connection is " +
                  "available - omit connection_type to create one. Detailed connection types come with the " +
                  "Steel Connections content and must be loaded into the project first."
                : " Loaded types: " + string.Join(", ", loaded.Take(25).Select(SafeName)) +
                  (loaded.Count > 25 ? " (and " + (loaded.Count - 25) + " more)" : "") + ".";
            return null;
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; } catch { return "(unnamed " + Rid.Value(element.Id) + ")"; }
        }

        // =====================================================================
        // Apply and verify
        // =====================================================================

        /// <summary>
        /// Create the connection.
        ///
        /// THE GENERIC ONE HAS ITS OWN FACTORY. StructuralConnectionHandler.Create has no
        /// two-argument overload - its shapes are (doc, ids, typeId), (doc, ids, typeId,
        /// inputPoints), (doc, ids, typeId, string) and (doc, ids, string) - and the
        /// untyped connection Revit places when no family is chosen is
        /// CreateGenericConnection. Read out of the installed RevitAPI.xml rather than
        /// remembered, after the first version of this file called a Create overload that
        /// does not exist.
        /// </summary>
        private static void Apply(Document doc, Plan p)
        {
            StructuralConnectionHandler created = p.Type == null
                ? StructuralConnectionHandler.CreateGenericConnection(doc, p.MemberIds)
                : StructuralConnectionHandler.Create(doc, p.MemberIds, p.Type.Id);
            p.CreatedId = created == null ? null : created.Id;
        }

        /// <summary>
        /// Ask the MODEL. The handler must exist, and it must actually reference the
        /// members that were asked for - a handler that was created and connected
        /// nothing is the exact outcome this command exists to refuse to report as
        /// success.
        /// </summary>
        private static bool Verify(Document doc, Plan p)
        {
            if (p.CreatedId == null) return false;
            var handler = doc.GetElement(p.CreatedId) as StructuralConnectionHandler;
            if (handler == null) return false;
            try
            {
                ICollection<ElementId> connected = handler.GetConnectedElementIds();
                if (connected == null) return false;
                var actual = new HashSet<long>(connected.Select(Rid.Value));
                return p.MemberIds.All(id => actual.Contains(Rid.Value(id)));
            }
            catch { return false; }
        }

        private static Rehearsal Rehearse(Document doc, List<Plan> plans)
        {
            var rehearsal = new Rehearsal();
            var tx = new Transaction(doc, "Horizun: rehearse structural connections");
            try
            {
                if (tx.Start() != TransactionStatus.Started)
                {
                    rehearsal.Json = new JObject { ["error"] = "the rehearsal transaction would not start" };
                    return rehearsal;
                }
                foreach (Plan p in plans) Apply(doc, p);
                doc.Regenerate();
                rehearsal.Verified = plans.All(p => Verify(doc, p));
                rehearsal.Json = new JObject
                {
                    ["rehearsed"] = plans.Count,
                    ["verified"] = plans.Count(p => Verify(doc, p)),
                    ["means"] = "each connection was created provisionally and then asked which elements it " +
                                "actually connects, before all of it was rolled back."
                };
            }
            catch (Exception ex)
            {
                rehearsal.Verified = false;
                rehearsal.Json = new JObject { ["error"] = ex.Message };
            }
            finally
            {
                Guard.RollbackResult? rollback = null;
                try { if (tx.GetStatus() == TransactionStatus.Started) rollback = Guard.RollBack(tx); }
                catch { rollback = null; }
                rehearsal.RollbackConfirmed = rollback.HasValue && rollback.Value.Confirmed;
                rehearsal.RollbackStatus = rollback.HasValue ? rollback.Value.StatusName : "Error";
                // The rehearsal's ids belong to a document state that no longer exists.
                foreach (Plan p in plans) p.CreatedId = null;
            }
            return rehearsal;
        }

        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, List<Plan> plans)
        {
            var resolved = new ResolvedPlan
            {
                Command = "horizun_structural_connections",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app.Application.VersionNumber,
                DocumentFingerprint = gate.Identity.FingerprintDigest()
            };
            foreach (Plan p in plans)
            {
                var before = new Dictionary<string, string>
                {
                    ["members"] = string.Join(",", p.Members.Select(SafeUniqueId)),
                    ["member_types"] = string.Join(",", p.Members.Select(TypeNameOf)),
                    ["connection_type"] = p.Type == null ? "<generic>" : SafeName(p.Type)
                };
                resolved.Elements.Add(new PlannedElement
                {
                    UniqueId = "connection:" + p.Key,
                    Category = "structural_connection",
                    Action = PlannedAction.Create,
                    BeforeValues = before
                });
            }
            return resolved;
        }

        private static string SafeUniqueId(Element element)
        {
            try { return element.UniqueId; } catch { return "<unreadable>"; }
        }

        private static string TypeNameOf(Element element)
        {
            try
            {
                Element type = element.Document.GetElement(element.GetTypeId());
                return type == null ? "<none>" : SafeName(type);
            }
            catch { return "<unreadable>"; }
        }

        private sealed class Rehearsal
        {
            public bool Verified;
            public bool RollbackConfirmed;
            public string RollbackStatus = "Unknown";
            public JObject Json = new JObject();
        }

        private sealed class Plan
        {
            public int Index;
            public string Key;
            public List<Element> Members;

            /// <summary>
            /// A List, not an array: both StructuralConnectionHandler factories take
            /// IList&lt;ElementId&gt;, which an array does not satisfy for the generic one.
            /// </summary>
            public List<ElementId> MemberIds;
            public StructuralConnectionHandlerType Type;
            public ElementId CreatedId;

            public const string OutcomeNotAttempted = "not_attempted";
            public const string OutcomeCreated = "created_and_verified_in_transaction";
            public const string OutcomeFailed = "failed";

            /// <summary>
            /// What happened to THIS row during the apply. Never a claim about the model:
            /// this batch is atomic, so every row can reach OutcomeCreated and the group
            /// still roll back, and the reply that carries these says so.
            /// </summary>
            public string AttemptOutcome = OutcomeNotAttempted;

            public string AttemptReason;

            public string Fail(string reason)
            {
                AttemptOutcome = OutcomeFailed;
                AttemptReason = reason;
                return OutcomeFailed;
            }

            /// <summary>This row's attempt. Claims nothing about what is in the document.</summary>
            public JObject Attempt()
            {
                JObject row = Json();
                row["attempt_outcome"] = AttemptOutcome;
                row["attempt_reason"] = AttemptReason == null ? (JToken)JValue.CreateNull() : AttemptReason;
                row["created_in_attempt"] = CreatedId == null ? (JToken)JValue.CreateNull() : Rid.Value(CreatedId);
                return row;
            }

            public JObject Json() => new JObject
            {
                ["index"] = Index,
                ["key"] = Key,
                ["member_ids"] = new JArray(MemberIds.Select(Rid.Value)),
                ["connection_type"] = Type == null ? "generic" : SafeName(Type),
                ["connection_type_id"] = Type == null ? (JToken)JValue.CreateNull() : Rid.Value(Type.Id)
            };

            public JObject Result(Document doc)
            {
                JObject row = Json();
                row["connection_id"] = CreatedId == null ? (JToken)JValue.CreateNull() : Rid.Value(CreatedId);
                var handler = CreatedId == null ? null : doc.GetElement(CreatedId) as StructuralConnectionHandler;
                JArray connected = new JArray();
                try
                {
                    if (handler != null)
                        foreach (ElementId id in handler.GetConnectedElementIds()) connected.Add(Rid.Value(id));
                }
                catch { /* an unreadable list stays empty and the verified flag says so */ }
                row["connected_element_ids"] = connected;
                row["verified"] = handler != null && connected.Count >= MemberIds.Count;
                row["means"] = "connected_element_ids was READ from the connection after the commit. What a " +
                               "DETAILED connection then generates - plates, bolts, welds - belongs to the " +
                               "Steel Connections add-in and is not reported here, because this command did " +
                               "not make it and cannot vouch for it.";
                return row;
            }
        }
    }
}
