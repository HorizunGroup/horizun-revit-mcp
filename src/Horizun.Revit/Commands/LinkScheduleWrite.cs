// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_link_schedule, operation=write. Original Horizun code.
//
// Stamps each matched element's selected activity field (and optionally start/finish, ISO
// yyyy-MM-dd) into TEXT INSTANCE parameters. A row whose parameter is missing,
// ambiguous, read-only, a type parameter or not text is listed as unresolved, as is a
// row outside the ownership gate. A failed Set requests rollback of the batch.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class LinkScheduleCommand
    {
        private CommandResult Write(UIApplication app, JObject request, ScheduleImportResult schedule, JObject spec,
                                    string fileSha, JObject head, int maxRows)
        {
            string error = ScheduleWriteOptions.Read(request, out var options);
            if (error != null) return CommandResult.Fail(error);
            var assumptions = new[] { ScheduleLinkWriteRules.ActivityFieldAssumption(options.RequestedField, options.ActivityParameter) };

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            ScheduleMatch m = ScheduleLinkRules.Match(spec, schedule, Facts(doc, spec, null));
            Element Resolve(string uniqueId) => doc.GetElement(uniqueId);
            bool IsOwned(Element e) => HrzOwnership.IsTrustedOwned(doc, e) || HrzOwnership.CreatedThisSession(doc, e);
            var rows = ScheduleLinkWriteRules.Prepare(m, options, Resolve, IsOwned, InspectWritableParameter, HrzOwnership.MarkPresence);
            var ready = rows.Where(r => r.Unresolved == null).ToList();

            var plan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            ScheduleLinkWriteRules.BindPlan(plan, ready);
            string planHash = ScheduleLinkRules.PlanHash(request, fileSha, "write");

            head["document"] = doc.Title;
            MatchJson(head, m, maxRows);
            head["activity_field"] = options.ActivityField;
            if (dryRun)
                return ScheduleLinkWriteRules.Rehearse(head, rows, ready, maxRows,
                    DocumentGate.Confirmations, gate.Identity, planHash, plan, assumptions);

            int unresolved = ScheduleLinkWriteRules.StampPreparation(head, rows, ready, maxRows, dryRun: false);
            if (ready.Count == 0)
                return ScheduleLinkWriteRules.NothingToWrite(head, unresolved, assumptions);

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null);
            if (refusal != null) return refusal;

            TransactionStatus status = TransactionStatus.Uninitialized;
            string cleanupError = null;
            try
            {
                using var tx = new Transaction(doc, "Horizun: link schedule (4D) parameters");
                tx.Start();
                string RollBack() => Guard.RollBack(tx).StatusName;
                string TransactionState() => tx.GetStatus().ToString();
                CommandResult writeFailure = ScheduleLinkWriteRules.ApplyValues(ready, Resolve, IsOwned,
                    SetResolvedValue, RollBack, head, unresolved, assumptions, HrzOwnership.MarkPresence, TransactionState);
                if (writeFailure != null) return writeFailure;
                try { status = Guard.Commit(tx, "link schedule parameters"); }
                catch (SilentRollbackException ex) { status = ex.Status; }
                catch (Exception ex)
                {
                    return ScheduleLinkRules.FailAfterRollback("Revit could not commit the schedule parameters: " + ex.Message,
                        RollBack, head, ready.Count, unresolved, assumptions, TransactionState);
                }
            }
            catch (Exception ex) when (status == TransactionStatus.Committed)
            {
                cleanupError = ex.Message;
            }

            DocumentGate.StampConfirmation(head, gate, Name, planHash, false);
            var result = ScheduleLinkWriteRules.CompleteAfterCommit(status.ToString(), ready, Resolve, ReadWrittenValue,
                head, unresolved, maxRows, assumptions);
            return ScheduleLinkRules.ReportCommittedCleanupFailure(result, cleanupError);
        }

        private static string ParameterIdentity(Parameter p)
        {
            if (p.IsShared)
            {
                Guid guid = p.GUID;
                return guid == Guid.Empty ? null : "guid:" + guid.ToString("D");
            }
            if (p.Definition is InternalDefinition def && def.BuiltInParameter != BuiltInParameter.INVALID)
                return "builtin:" + def.BuiltInParameter;
            return p.Id == ElementId.InvalidElementId ? null : "id:" + Rid.Value(p.Id).ToString(CultureInfo.InvariantCulture);
        }

        private static Parameter ResolveParameter(Element e, string identity)
        {
            if (identity.StartsWith("guid:", StringComparison.Ordinal))
                return e.get_Parameter(Guid.ParseExact(identity.Substring(5), "D"));
            if (identity.StartsWith("builtin:", StringComparison.Ordinal))
                return e.get_Parameter((BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), identity.Substring(8)));

            // Instance and family parameter IDs do not always name a ParameterElement.
            long id = long.Parse(identity.Substring(3), CultureInfo.InvariantCulture);
            return e.Parameters.Cast<Parameter>().SingleOrDefault(p => Rid.Value(p.Id) == id && !p.IsShared);
        }

        private static bool SetResolvedValue(Element e, string identity, string value)
        {
            Parameter p = ResolveParameter(e, identity);
            if (p?.StorageType != StorageType.String || p.IsReadOnly) return false;
            // Revit returns false from Set when the value did not change.
            return string.Equals(p.AsString() ?? "", value, StringComparison.Ordinal) || p.Set(value);
        }

        private static string ReadWrittenValue(Element e, string identity)
        {
            Parameter p = ResolveParameter(e, identity);
            return p?.StorageType == StorageType.String ? p.AsString() ?? "" : null;
        }

        /// <summary>Null when the instance parameter can take text; else why not.</summary>
        private static string InspectWritableParameter(Element e, string name, out ScheduleWriteField field)
        {
            field = null;
            try
            {
                if (e.GroupId != ElementId.InvalidElementId)
                    return "the element is a group member; its instance parameters may be locked by the group.";
                string refusal = ScheduleLinkRules.ResolveParameterName(name, NamedParameters(e, name), out Parameter p);
                if (refusal != null) return refusal;
                if (p == null) return "no INSTANCE parameter '" + name + "' (a type parameter would stamp every instance of the type).";
                if (p.StorageType != StorageType.String) return "'" + name + "' is not a text parameter.";
                if (p.IsReadOnly) return "'" + name + "' is read-only.";
                string identity = ParameterIdentity(p);
                if (identity == null) return "could not establish the identity of '" + name + "'.";
                field = new ScheduleWriteField { Identity = identity, Before = p.AsString() ?? "" };
            }
            catch (Exception ex) { return "could not inspect '" + name + "' before writing: " + ex.Message; }
            return null;
        }
    }
}
