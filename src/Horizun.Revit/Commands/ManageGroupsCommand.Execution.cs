using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageGroupsCommand : ICommand
    {
        private static readonly GroupAnnotationSession AnnotationSession = new GroupAnnotationSession();
        public string Name => "horizun_manage_groups";
        public string Description => "Find colocated group/family instances by type, or list and manage groups with verified writes.";

        private static readonly string[] Writes =
            { "create", "add_members", "remove_members", "rename_type", "duplicate_type", "swap_type", "change_group_type", "set_group_level", "place_group", "ungroup" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (op == "copy_to_levels" || op == "check_variation") return ExecuteCopyOrVariation(app, request);

            if (op == "list" || op == "find_duplicates" || op == "annotation_report")
            {
                Document readDoc = app.ActiveUIDocument?.Document;
                if (readDoc == null) return CommandResult.Fail("No document is open.");
                CommandResult wrong = DocumentGate.ReadGuard(readDoc, request, Name);
                if (wrong != null) return wrong;
                if (op == "annotation_report")
                {
                    var report = AnnotationSession.Read(SessionKey(readDoc));
                    return CommandResult.Ok(new JObject { ["operation"] = op, ["document"] = readDoc.Title,
                        ["operations"] = report.Operations, ["tags_at_risk"] = report.TagsAtRisk,
                        ["tags_recreated"] = report.TagsRecreated,
                        ["tags_lost"] = Math.Max(0, report.TagsAtRisk - report.TagsRecreated),
                        ["dimensions_lost"] = report.DimensionsLost,
                        ["scope"] = "this Revit process and this document" });
                }
                return op == "list" ? List(readDoc, request) : FindDuplicates(readDoc, request);
            }
            if (op == "convert_to_link")
                return CommandResult.FailWithDetail(
                    "convert_to_link is not available: the Revit API (2023-2027) has no call that turns a group into a " +
                    "linked model - GroupType offers LoadFrom and nothing that saves a group as a file. Do it in Revit " +
                    "(select the group, Link). Nothing was written.",
                    new JObject { ["state"] = "refused", ["code"] = "api_absent", ["operation"] = op, ["write_started"] = false },
                    FallbackSignal.NotAllowed("api_absent", false), null);
            if (op == "edit_type_members") return ExecuteEditType(app, request);
            if (!Writes.Contains(op))
                return CommandResult.Fail("operation must be list, find_duplicates, annotation_report, " + string.Join(", ", Writes) + ", edit_type_members or convert_to_link.");

            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string error; Plan plan = MakePlan(doc, request, op, out error);
            if (plan == null)
                return CommandResult.FailWithDetail("Precondition not met: " + error + " Nothing was written. Satisfy the named requirement, then rehearse again.",
                    new JObject { ["state"] = "refused", ["code"] = ErrorCodes.PrecondicionNoCumplida,
                        ["precondition"] = error, ["how_to_satisfy"] = "Satisfy the specific requirement in precondition; it names the required ids, scope, level or group state.",
                        ["operation"] = op, ["write_started"] = false });

            if (op == "add_members")
            {
                var copies = plan.TypeAddition ? plan.OtherIds.OrderBy(Rid.Value).ToList() : new List<ElementId>();
                string placementError = GroupLevelRules.ValidateAdditionRotations(request["relative_rotations_degrees"], copies.Select(Rid.Value).ToList());
                if (placementError != null)
                    return CommandResult.FailWithDetail(placementError + " Nothing was written.",
                        new JObject { ["state"] = "refused", ["code"] = ErrorCodes.PrecondicionNoCumplida,
                            ["operation"] = op, ["write_started"] = false, ["copy_group_ids"] = new JArray(copies.Select(Rid.Value)) });
                if (plan.TypeAddition && plan.OtherIds.Count > 0)
                {
                    plan.ReferencePlacementBefore = CaptureGroupPlacement(doc, plan.Groups[0]);
                    var angles = ((JArray)request["relative_rotations_degrees"]).Values<double>().ToList();
                    for (int index = 0; index < copies.Count; index++)
                    {
                        ElementId id = copies[index];
                        plan.CopyRotations[id] = angles[index];
                        plan.CopyPlacementsBefore[id] = CaptureGroupPlacement(doc, (Group)doc.GetElement(id));
                    }
                    plan.LoosePlacementBefore = ReadMemberBounds(doc, plan.Elements.Concat(plan.AllowedExtra).Distinct().OrderBy(Rid.Value), true);
                    if (angles.Any(GroupLevelRules.NeedsPlanGeometry))
                        foreach (GroupMemberBounds member in plan.LoosePlacementBefore)
                            member.CapturedGeometryPoints = member.ReadGeometryPoints?.Invoke();
                    foreach (GroupMemberBounds member in plan.LoosePlacementBefore)
                        member.ReadGeometryPoints = null;
                }
                plan.AnnotationsBefore = AnnotationDependencyAudit.ReadTargets(doc, plan.AnnotationTargets, true);
                if (plan.AnnotationsBefore.Value<bool>("coverage_complete") != true)
                    return CommandResult.FailWithDetail("Some annotation references could not be read, so add_members cannot verify their preservation. Nothing was written.",
                        new JObject { ["annotations_at_risk_by_view"] = plan.AnnotationsBefore,
                            ["error_code"] = ErrorCodes.PrecondicionNoCumplida, ["write_started"] = false, ["changes_applied"] = false });
            }
            string hash = DocumentGate.PlanHash(request, "operation", "group_ids", "type_id", "element_ids", "name", "scope", "level_id", "keep_position", "pattern_group_id", "point_mm", "accept_lost_connections", "accept_member_regeneration", "relative_rotations_degrees");
            ResolvedPlan resolved = Resolved(gate, app, plan);
            if (plan.ReferencePlacementBefore != null)
                resolved.Elements.Add(new PlannedElement { UniqueId = "groups:placement", Category = "group", Action = PlannedAction.Read,
                    BeforeValues = new Dictionary<string, string>
                    {
                        ["reference"] = plan.ReferencePlacementBefore.ToJson().ToString(Newtonsoft.Json.Formatting.None),
                        ["copies"] = new JArray(plan.CopyRotations.Keys.OrderBy(Rid.Value).Select(id => plan.CopyPlacementsBefore[id].ToJson())).ToString(Newtonsoft.Json.Formatting.None),
                        ["loose"] = GroupPlacementSnapshot.MemberRows(plan.LoosePlacementBefore).ToString(Newtonsoft.Json.Formatting.None)
                    } });
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string txName = "Horizun: groups " + op;

            if (dry)
            {
                JObject annotationsAtRisk = op == "add_members" ? plan.AnnotationsBefore : AnnotationDependencyAudit.Read(doc,
                    op == "place_group" ? new ElementId[0] : plan.Groups.Select(g => g.Id));
                if (op == "set_group_level" || op == "change_group_type") plan.AnnotationsBefore = annotationsAtRisk;
                JObject rehearsal = Rehearse(doc, plan, txName);
                if (rehearsal.Value<bool>("rollback_confirmed") != true)
                    return CommandResult.FailWithDetail("Group rehearsal rollback was not confirmed; model state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["rehearsal"] = rehearsal, ["annotations_at_risk_by_view"] = annotationsAtRisk, ["write_started"] = true });
                if (rehearsal.Value<bool>("verified") != true)
                    return CommandResult.FailWithDetail("The group rehearsal could not verify the change (" + RehearsalWhy(rehearsal) + "). Nothing was committed.",
                        new JObject { ["state"] = "refused", ["rehearsal"] = rehearsal, ["annotations_at_risk_by_view"] = annotationsAtRisk });
                if (op == "add_members")
                {
                    resolved.Elements.AddRange(plan.ApprovedDeletions.Select(d => d.PlanRow()));
                    resolved.ContextFingerprint = plan.InnerCommit.AdditionRoles.ToString(Newtonsoft.Json.Formatting.None);
                }
                DocumentGate.RecordResolvedPlan(resolved);
                var result = new JObject { ["dry_run"] = true, ["plan"] = PlanJson(plan),
                    ["rehearsal"] = rehearsal, ["annotations_at_risk_by_view"] = annotationsAtRisk };
                ApplicationOutcome.StampRehearsal(result, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, true,
                    op == "add_members"
                        ? "the token binds the group plan, the addition roles and every deletion observed in rehearsal."
                        : "the token binds the group instances, types, member ids and instance counts named in the plan.");
                return CommandResult.Ok(result);
            }

            if (op == "add_members")
            {
                CommandResult changesRefusal = ResolveApprovedChanges(doc, plan, request, gate, hash, resolved);
                if (changesRefusal != null) return changesRefusal;
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;
            if (op == "add_members" && (plan.ApprovedDeletions == null || plan.ApprovedAdditionRoles == null))
                return CommandResult.RefuseBeforeWrite("The rehearsed group additions or deletions are unavailable. Rehearse add_members again.",
                    ErrorCodes.ConfirmacionRequerida);
            if (op == "set_group_level" || op == "change_group_type") plan.AnnotationsBefore = AnnotationDependencyAudit.Read(doc, plan.Groups.Select(g => g.Id));

            var failures = new FailureLog();
            var verification = new GroupRebuildChecks();
            using var changes = op == "add_members" ? new ChangeWatch(app.Application) : null;
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started) return CommandResult.Fail("Could not start the group TransactionGroup.");
                var tx = new Transaction(doc, txName);
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the group transaction.");
                    Quiet(tx, failures);
                    Apply(doc, plan);
                    doc.Regenerate();
                    VerifyThroughCommit(doc, plan, tx, failures, changes, verification);
                    if (!verification.Verified)
                        throw new InvalidOperationException(verification.Committed
                            ? "a postcondition failed after the inner commit."
                            : "a postcondition failed before the inner commit.");
                    TransactionStatus gs = group.Assimilate();
                    if (gs != TransactionStatus.Committed) return CommandResult.FailWithDetail("The group TransactionGroup did not assimilate; state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["transaction_group_status"] = gs.ToString(), ["write_started"] = true });
                }
                catch (Exception ex)
                {
                    try { if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx); } catch { }
                    Guard.RollbackResult? gr; try { gr = Guard.RollBack(group); } catch { gr = null; }
                    bool rolled = gr.HasValue && gr.Value.Confirmed;
                    return CommandResult.FailWithDetail("Group " + op + " failed and was " + (rolled ? "rolled back" : "NOT confirmed rolled back") + ": " + ex.Message +
                        (failures.Errors.Count > 0 ? " Revit failure details: " + string.Join("; ", failures.Errors) : "") +
                        ". Resolve the named Revit constraint or supply the missing group precondition, then rehearse again.",
                        new JObject { ["state"] = rolled ? "rolled_back" : "uncertain",
                                      ["code"] = rolled ? ErrorCodes.TransaccionRevertida : "revit_edit_failed",
                                      ["precondition"] = ex.Message,
                                      ["how_to_satisfy"] = "Resolve the Revit constraint named in the failure details or supply the missing group precondition, then rehearse again.",
                                      ["failure_details"] = new JArray(failures.Errors), ["write_started"] = true,
                                      ["transaction_group_status"] = gr.HasValue ? gr.Value.StatusName : "Error",
                                      ["postconditions"] = verification.Check?.ToJson() ?? SafeJson(doc, plan), ["revit_warnings"] = new JArray(failures.Warnings) });
                }
                finally { tx.Dispose(); }
            }
            var done = new JObject
            {
                ["operation"] = op,
                ["transaction_status"] = ApplicationOutcome.Committed,
                ["transaction_group_status"] = ApplicationOutcome.Committed,
                ["write_started"] = true,
                ["changes_applied"] = true,
                ["revit_warnings"] = new JArray(failures.Warnings)
            };
            PostconditionCheck after;
            try
            {
                MembershipChanges reread = changes == null ? null : ReadMembershipChanges(doc, plan, changes);
                after = VerifyObserved(doc, plan, reread);
                JObject resultJson = ResultJson(doc, plan);
                if (reread != null)
                {
                    resultJson["also_created"] = reread.Additions;
                    if (after.AllVerified)
                        resultJson["deleted_elements"] = new JArray(plan.ApprovedDeletions.Select(d => d.Evidence(plan.AcceptRegeneration)));
                }
                done["result"] = resultJson;
            }
            catch (Exception ex)
            {
                // Assimilation already committed the write. A read failure cannot undo it.
                done["verification_error"] = ex.Message;
                after = new PostconditionCheck("postcommit_read").Unreadable("postcommit_read", true, ex.Message);
            }
            done["state"] = after.AllVerified ? "committed_verified" : "uncertain";
            done["host_verified"] = after.AllVerified;
            done["postconditions"] = after.ToJson();
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, 1, 1, after.AllVerified ? 1 : 0, 0, 0, after.AllVerified ? 0 : 1);
            if (!after.AllVerified)
            {
                done["code"] = "postcommit_verification_failed";
                return CommandResult.FailWithDetail("The change committed, but post-commit verification did not pass. Inspect the model before retrying.", done);
            }
            if (op == "change_group_type")
                AnnotationSession.Record(SessionKey(doc), op,
                    plan.TagsBefore.Count, plan.RestoredTags.Count, plan.LostDimensions.Count);
            return CommandResult.Ok(done);
        }

        private static string SessionKey(Document doc) => doc.GetHashCode() + "|" + doc.PathName + "|" + doc.Title;

        private sealed class MembershipChanges
        {
            public JArray Additions;
            public JArray AdditionRoles;
            public JArray OutOfScopeAdditions;
            public long[] Deletions;
        }

        private static MembershipChanges ReadMembershipChanges(Document doc, Plan plan, ChangeWatch watch)
        {
            var observed = new MembershipChanges();
            string key = ChangeWatch.Key(doc);
            var changes = watch.Documents.FirstOrDefault(d => ChangeWatch.Key(d.Document) == key);
            if (!watch.ObservationComplete || changes == null) return observed;
            // An element the rebuild made and removed again was nobody's work and is gone either way: it is
            // not a deletion to authorize (after the rollback it cannot be read, so it would read as unidentified).
            observed.Deletions = changes.Deleted.Where(id => !changes.Transient.Contains(id)).Concat(plan.ExplicitDeletes)
                .Distinct().OrderBy(id => id).ToArray();
            long newGroupId = Rid.Value(plan.NewGroupId), newTypeId = Rid.Value(plan.NewTypeId);
            var roles = new Dictionary<long, string> { [newTypeId] = "group-type" };
            ReadAdditionRoles(doc, plan.NewGroupId, "reference", plan.AnnotationTargets, roles);
            if (plan.TypeAddition)
                foreach (ElementId copyId in plan.OtherIds)
                    ReadAdditionRoles(doc, CurrentCopyId(plan, copyId), "copy/" + Rid.Value(copyId), plan.AnnotationTargets, roles);
            observed.AdditionRoles = new JArray(roles.Values.OrderBy(role => role, StringComparer.Ordinal));
            observed.OutOfScopeAdditions = new JArray(changes.Added.Except(roles.Keys).OrderBy(id => id));
            observed.Additions = GroupLevelRules.AlsoCreated(newGroupId, newTypeId, changes.Added, roles.Keys,
                id => Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id))?.UniqueId : null);
            return observed;
        }

        private static ElementId CurrentCopyId(Plan plan, ElementId originalId) =>
            plan.RecreatedCopies.TryGetValue(originalId, out ElementId currentId) ? currentId : originalId;

        // Use original source ids for slot comparisons and actual ids for the change audit.
        private static List<GroupMemberBounds> ReadReferenceMembers(Document doc, Plan plan, Group group)
        {
            var found = HeightMembers(doc, group, readPlacement: true);
            var before = plan.ReferenceMembersBeforeRegeneration.ToDictionary(m => m.Id);
            var seen = new HashSet<long>(found.Select(m => m.Id));
            var available = before.Values.Where(m => !seen.Contains(m.Id) && doc.GetElement(Rid.Make(m.Id)) == null).ToList();
            foreach (GroupMemberBounds member in found)
            {
                if (before.ContainsKey(member.Id)) continue;
                if (plan.AnnotationTargets.Contains(member.Id)) return null;
                var matches = available.Where(original => GroupLevelRules.SameRecreatedCurveMember(original, member)).ToList();
                if (matches.Count != 1) return null;
                member.Id = matches[0].Id;
                available.Remove(matches[0]);
            }
            return found;
        }

        // Revit can allocate different ids on apply. Identify a new member by its
        // slot under the reference or an existing copy and by its element kind.
        private static void ReadAdditionRoles(Document doc, ElementId id, string path,
            HashSet<long> existing, Dictionary<long, string> roles)
        {
            Element element = doc.GetElement(id) ?? throw new InvalidOperationException("Group or member " + Rid.Value(id) + " could not be read.");
            if (!existing.Contains(Rid.Value(id)))
            {
                string kind = element.GetType().Name;
                if (!(element is Group)) kind += "/" + Rid.Value(element.GetTypeId());
                roles.Add(Rid.Value(id), path + ":" + kind);
            }
            if (element is Group group)
            {
                int slot = 0;
                foreach (ElementId member in group.GetMemberIds())
                    ReadAdditionRoles(doc, member, path + "/" + slot++, existing, roles);
            }
        }

        private static List<GroupRebuildChecks.Deletion> ReadDeletionIdentities(Document doc, Plan plan, IEnumerable<long> deletedIds)
        {
            List<long> ids = deletedIds.ToList();
            HashSet<long> definition = plan.Op == "add_members"
                ? TypeDefinitionClosure(doc, plan.DefinitionTypeId, ids) : new HashSet<long>();
            return ids.Select(id =>
            {
                Element e = Rid.CanRepresent(id) ? Safe(() => doc.GetElement(Rid.Make(id))) : null;
                return new GroupRebuildChecks.Deletion
                {
                    Id = id, UniqueId = Safe(() => e?.UniqueId),
                    Owned = e != null && Holds(() => HrzOwnership.IsTrustedOwned(doc, e)),
                    Category = Safe(() => e?.Category?.Name ?? e?.GetType().Name),
                    OwnershipReason = e == null ? "not found after the rollback" : Safe(() => HrzOwnership.GateReason(doc, e)),
                    TypeDefinition = definition.Contains(id),
                    // The ownership gate and annotation audit already walk this exact closure.
                    InRegenerationScope = plan.AnnotationTargets.Contains(id) || plan.RequiredDeletions.Contains(id)
                        || definition.Contains(id)
                        // Revit's own automatic sketch dimensions go with the member it regenerates and
                        // are made again by Revit; nobody authors them (MEASURED 2026-11-21, Revit 2025:
                        // adding fixture wall 573298 deleted its OST_WeakDims 585049). Still needs the
                        // same ownership/opt-in as any regenerated element.
                        || Safe(() => e == null ? null : CategoryKey.Of(e)) == "OST_WeakDims"
                };
            }).ToList();
        }

        private CommandResult ResolveApprovedChanges(Document doc, Plan plan, JObject request, GateResult gate,
            string hash, ResolvedPlan resolved)
        {
            ResolvedPlan rehearsal = DocumentGate.Confirmations.ReadRehearsedPlan(request.Value<string>("confirmation_token"),
                Name, gate.Fingerprint, hash);
            if (rehearsal == null) return null; // The confirmation gate diagnoses an unknown, spent or expired token.
            if (!string.IsNullOrEmpty(rehearsal.ContextFingerprint))
                plan.ApprovedAdditionRoles = JArray.Parse(rehearsal.ContextFingerprint);
            resolved.ContextFingerprint = rehearsal.ContextFingerprint;
            plan.ApprovedDeletions = ReadDeletionIdentities(doc, plan, rehearsal.Elements
                .Where(e => e.Action == PlannedAction.Delete && e.ElementId.HasValue).Select(e => e.ElementId.Value));
            if (plan.ApprovedDeletions.Any(d => !d.Authorized(plan.AcceptRegeneration)))
                return CommandResult.FailWithDetail("A rehearsed deletion is unavailable or no longer authorized. Rehearse add_members again.",
                    new JObject { ["state"] = "stale_plan", ["write_started"] = false,
                        ["deleted_elements"] = new JArray(plan.ApprovedDeletions.Select(d => d.Evidence(plan.AcceptRegeneration))) });
            resolved.Elements.AddRange(plan.ApprovedDeletions.Select(d => d.PlanRow()));
            return null;
        }

        private static JObject Rehearse(Document doc, Plan p, string name)
        {
            if (p.Op == "add_members" || p.Op == "remove_members") return RehearseCommitted(doc, p, name);
            var failures = new FailureLog();
            PostconditionCheck check = null; string error = null; Guard.RollbackResult? rb = null; string rbError = null;
            using (var tx = new Transaction(doc, name))
            {
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction did not start");
                    Quiet(tx, failures);
                    Apply(doc, p); doc.Regenerate(); check = Verify(doc, p);
                }
                catch (Exception ex) { error = ex.Message; }
                var shifts = new JArray(p.Shifts.Select(kv => new JObject { ["group_id"] = Rid.Value(kv.Key), ["measured_shift_ft"] = new JArray(kv.Value) }));
                try { rb = Guard.RollBack(tx); } catch (Exception ex) { rbError = ex.Message; }
                return new JObject
                {
                    ["verified"] = error == null && check != null && check.AllVerified,
                    ["postconditions"] = check?.ToJson(),
                    ["error"] = error,
                    ["swapped_instance_shifts"] = shifts,
                    ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "exception: " + rbError,
                    ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed
                };
            }
        }

        /// <summary>
        /// A membership change rehearsed THROUGH the commit, inside a TransactionGroup that is then
        /// rolled back: Revit refuses some redefinitions only when the transaction commits ("Can't
        /// make change to Group ... with more than one instance"), which a rolled-back transaction
        /// never reaches. The rehearsal reports that refusal by Revit's own words.
        /// </summary>
        private static JObject RehearseCommitted(Document doc, Plan p, string name)
        {
            var failures = new FailureLog();
            var verification = new GroupRebuildChecks();
            string error = null; Guard.RollbackResult? rb = null; string rbError = null;
            using var changes = p.Op == "add_members" ? new ChangeWatch(doc.Application) : null;
            using (var group = new TransactionGroup(doc, name))
            {
                if (group.Start() != TransactionStatus.Started)
                    return new JObject { ["verified"] = false, ["error"] = "the TransactionGroup did not start", ["rollback_confirmed"] = true };
                var tx = new Transaction(doc, name);
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction did not start");
                    Quiet(tx, failures);
                    Apply(doc, p); doc.Regenerate();
                    VerifyThroughCommit(doc, p, tx, failures, changes, verification);
                }
                catch (Exception ex) { error = ex.Message; }
                finally { try { if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx); } catch { } tx.Dispose(); }
                var shifts = new JArray(p.Shifts.Select(kv => new JObject { ["group_id"] = Rid.Value(kv.Key), ["measured_shift_ft"] = new JArray(kv.Value) }));
                try { rb = Guard.RollBack(group); } catch (Exception ex) { rbError = ex.Message; }
                JArray deletions = null;
                if (rb.HasValue && rb.Value.Confirmed && p.InnerCommit?.Deletions != null)
                {
                    // Revit only supplies ids for deleted elements. Read the restored originals
                    // for both identity and ownership before publishing any approval.
                    p.ApprovedDeletions = ReadDeletionIdentities(doc, p, p.InnerCommit.Deletions);
                    deletions = new JArray(p.ApprovedDeletions.Select(d => d.Evidence(p.AcceptRegeneration)));
                    if (p.ApprovedDeletions.Any(d => !d.Authorized(p.AcceptRegeneration)))
                        error = "A deletion could not be identified or is outside the authorized group regeneration scope.";
                }
                var o = new JObject
                {
                    ["verified"] = error == null && verification.Verified,
                    ["postconditions"] = verification.Check?.ToJson(),
                    ["error"] = error,
                    ["committed_inside_group"] = verification.Committed,
                    ["dependents_included"] = new JArray(p.DependentsIncluded.Select(Rid.Value)),
                    ["swapped_instance_shifts"] = shifts,
                    ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "exception: " + rbError,
                    ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed
                };
                if (changes != null)
                {
                    o["deleted_elements"] = deletions;
                    o["addition_roles"] = p.InnerCommit?.AdditionRoles;
                }
                if (failures.Errors.Count > 0) o["revit_errors"] = new JArray(failures.Errors);
                if (failures.Warnings.Count > 0) o["revit_warnings"] = new JArray(failures.Warnings);
                return o;
            }
        }

        private static void VerifyThroughCommit(Document doc, Plan p, Transaction tx, FailureLog failures,
            ChangeWatch changes, GroupRebuildChecks verification)
        {
            verification.Run(() => Verify(doc, p), () =>
            {
                TransactionStatus status = tx.Commit();
                if (status != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit refused the commit (" + status + ")" +
                        (failures.Errors.Count > 0 ? ": " + string.Join(" | ", failures.Errors) : ""));
            }, () =>
            {
                MembershipChanges observed = changes == null ? null : ReadMembershipChanges(doc, p, changes);
                try { return VerifyObserved(doc, p, observed); }
                finally { p.InnerCommit = observed; }
            });
        }

        private static PostconditionCheck VerifyObserved(Document doc, Plan plan, MembershipChanges observed)
        {
            PostconditionCheck check = Verify(doc, plan, observesChanges: observed != null);
            if (observed != null)
            {
                GroupRebuildChecks.RecordChanges(check, plan.InnerCommit?.Additions, observed.Additions,
                    observed.Deletions, plan.RequiredDeletions, plan.ApprovedDeletions?.Select(d => d.Id).ToArray(),
                    id => !Rid.CanRepresent(id) || doc.GetElement(Rid.Make(id)) != null);
                check.Record("out_of_scope_additions", new JArray(), observed.OutOfScopeAdditions, observed.OutOfScopeAdditions?.Count == 0);
                check.Record("addition_roles", plan.ApprovedAdditionRoles, observed.AdditionRoles,
                    observed.AdditionRoles != null && (plan.ApprovedAdditionRoles == null ||
                    JToken.DeepEquals(plan.ApprovedAdditionRoles, observed.AdditionRoles)));
            }
            return check;
        }

        /// <summary>Which property failed, in the text a probe or a person reads - not only in the detail block.</summary>
        private static string RehearsalWhy(JObject rehearsal)
        {
            string err = rehearsal.Value<string>("error");
            if (!string.IsNullOrEmpty(err)) return "error: " + err;
            var props = (rehearsal["postconditions"] as JObject)?["properties"] as JArray;
            var failedRows = props == null ? new List<JObject>() : props.OfType<JObject>()
                .Where(x => x.Value<bool?>("matches") != true).ToList();
            if (failedRows.Count == 0) return "no postcondition was recorded";
            // Name WHICH instance and WHY, not only the property: "failed: other_instances" over 28
            // instances told nobody which one to look at.
            var parts = new List<string>();
            foreach (JObject row in failedRows)
            {
                string name = row.Value<string>("property");
                var found = row["found_in_committed_model"];
                var list = found as JArray ?? ((found as JObject)?["lost_rows"] as JArray);
                var bad = list?.OfType<JObject>().Where(x => x.Value<bool?>("held") == false || x.Value<bool?>("verified") == false || x["external_id"] != null || x["fault_injected"] != null).ToList();
                if (bad == null || bad.Count == 0) { parts.Add(name); continue; }
                var shown = bad.Take(5).Select(x =>
                    (x["group_id"] != null ? "group " + x["group_id"] : "") +
                    (x["member_id"] != null ? " member " + x["member_id"] : "") +
                    (x["external_id"] != null ? " connected to " + x["external_id"] : "") +
                    ": " + (x.Value<string>("why") ?? (x["fault_injected"] != null ? "fault injected for a test (" + x["fault_injected"] + ")" : "did not hold")));
                parts.Add(name + " - " + bad.Count + " failing (" + string.Join("; ", shown) + (bad.Count > 5 ? "; ..." : "") + ")");
            }
            return "failed: " + string.Join(" | ", parts);
        }

        private static JObject SafeJson(Document doc, Plan p) { try { return Verify(doc, p).ToJson(); } catch { return null; } }

        private static T Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
        private static bool Holds(Func<bool> f) { try { return f(); } catch { return false; } }
    }
}
