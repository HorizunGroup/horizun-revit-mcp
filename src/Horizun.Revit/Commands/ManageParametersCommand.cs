// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_parameters - project parameter bindings and Global Parameters.
//
// Reads: list_bindings (the whole BindingMap), global_list.
// Writes: create_shared (definition written to an SPF + binding), rebind, remove_binding,
// global_create, global_set, global_delete. Every write rehearses in a transaction
// that is rolled back (dry_run defaults to true), is applied only with the token,
// and is re-read from the model - and, for create_shared, from the SPF on disk -
// through a PostconditionCheck before and after the commit.
//
// create_project is a NAMED REFUSAL: measured by reflection over RevitAPI 2023-2027,
// no call creates a non-shared project parameter (the only ParameterElement factory
// is SharedParameterElement.Create). Python would find the same absent API, so no
// fallback is offered. ParameterType was removed in 2023; legacy names ("Text",
// "YesNo", "Length") are mapped to SpecTypeId (ParameterClassificationRules).
//
// Split across partial files: this one (entry, runner), .Bindings.cs, .Globals.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageParametersCommand : ICommand
    {
        public string Name => "horizun_manage_parameters";
        public string Description => "Project parameter bindings and Global Parameters: list, create shared, rebind, remove, global create/set/delete - rehearsed and re-read.";

        private static readonly string[] Ops =
        {
            "list_bindings", "create_shared", "create_project", "rebind", "remove_binding",
            "global_list", "global_create", "global_set", "global_delete"
        };

        public CommandResult Execute(UIApplication uiapp, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Ops.Contains(op)) return CommandResult.Fail("operation must be one of " + string.Join(", ", Ops) + ".");

            if (op == "list_bindings" || op == "global_list")
            {
                Document d = uiapp?.ActiveUIDocument?.Document;
                if (d == null) return CommandResult.Fail("No active Revit document.");
                CommandResult wrong = DocumentGate.ReadGuard(d, request, Name);
                if (wrong != null) return wrong;
                try { return CommandResult.Ok(op == "list_bindings" ? ListBindings(d) : GlobalList(d)); }
                catch (Exception ex) { return CommandResult.Fail(op + " could not be read: " + ex.Message); }
            }
            if (op == "create_project")
                return CommandResult.FailWithDetail(
                    "create_project is not possible: no Revit 2023-2027 API creates a NON-shared project parameter " +
                    "(the only parameter factory is SharedParameterElement.Create). Python would meet the same absent " +
                    "API. Use create_shared, which writes the definition to an SPF you choose and binds it. Nothing was written.",
                    new JObject { ["state"] = "refused", ["reason"] = "no_api_in_any_supported_year", ["use_instead"] = "create_shared" });

            GateResult gate = DocumentGate.ForMutation(uiapp, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            Plan plan; string error;
            try { plan = Build(uiapp, doc, op, request, out error); }
            catch (Exception ex) { plan = null; error = ex.Message; }
            if (plan == null) return CommandResult.Fail(error + " Nothing was written.");

            string hash = DocumentGate.PlanHash(request, "operation", "name", "guid", "spf_path", "spf_group", "data_type",
                "categories", "binding_kind", "group", "allow_vary_between_groups", "value", "formula", "associate");
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            ResolvedPlan resolved = Resolved(gate, uiapp, plan);

            if (dry)
            {
                Outcome r = Run(uiapp, doc, plan, true);
                if (!r.RollbackConfirmed)
                    return CommandResult.FailWithDetail("Parameter rehearsal rollback was not confirmed; model state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["rehearsal"] = r.Json(), ["write_started"] = true });
                if (!r.Verified)
                    return CommandResult.FailWithDetail("The rehearsal could not verify " + op + ". Nothing was committed." +
                        (r.Error == null ? "" : " " + r.Error), new JObject { ["state"] = "refused", ["rehearsal"] = r.Json() });
                DocumentGate.RecordResolvedPlan(resolved);
                var result = new JObject { ["dry_run"] = true, ["operation"] = op, ["plan"] = plan.Preview, ["rehearsal"] = r.Json() };
                ApplicationOutcome.StampRehearsal(result, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, true,
                    "the token binds the operation, the parameter, its binding state and the values that would be lost.");
                return CommandResult.Ok(result);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(uiapp, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            Outcome a = Run(uiapp, doc, plan, false);
            if (!a.Committed)
                return CommandResult.FailWithDetail(op + " was not committed: " + (a.Error ?? "postcondition failed") + ".",
                    new JObject { ["state"] = a.RollbackConfirmed ? "rolled_back" : "uncertain", ["result"] = a.Json(),
                                  ["write_started"] = true });
            if (!a.Verified)
                return CommandResult.FailWithDetail(op + " committed but the re-read after the commit did not confirm it; state is uncertain.",
                    new JObject { ["state"] = "uncertain", ["host_verified"] = false, ["result"] = a.Json() });
            var done = new JObject
            {
                ["state"] = "committed_verified", ["host_verified"] = true, ["operation"] = op,
                ["plan"] = plan.Preview, ["result"] = a.Json()
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0);
            DocumentGate.StampConfirmation(done, gate, Name, hash, false);
            return CommandResult.Ok(done);
        }

        /// <summary>One resolved write: how to apply it, and the checklist that proves it.</summary>
        private sealed class Plan
        {
            public string Op, Subject, Category;
            public PlannedAction Action;
            public int ValuesLost;
            public JObject Preview = new JObject();
            public readonly Dictionary<string, string> Before = new Dictionary<string, string>();
            /// <summary>create_shared only: produce the definition (temp SPF copy on a rehearsal).</summary>
            public Func<UIApplication, bool, Scratch, ExternalDefinition> Definition;
            public Action<UIApplication, Document, ExternalDefinition, Scratch> Apply;
            public Func<Document, ExternalDefinition, Scratch, PostconditionCheck> Verify;
            public Func<Document, ExternalDefinition, Scratch, JObject> Report;
        }

        /// <summary>State one run carries between apply and verify (temp SPF, created ids).</summary>
        private sealed class Scratch
        {
            public string SpfPath, TempSpf;
            public bool SpfDefinitionCreated;
            /// <summary>The SPF was switched for this run; Run's finally puts <see cref="PreviousSpf"/> back.</summary>
            public bool SpfSwitched;
            public string PreviousSpf;
            public ElementId CreatedId;
        }

        private sealed class Outcome
        {
            public bool Committed, RollbackConfirmed, Rehearsal;
            public string Error, TransactionStatus;
            public PostconditionCheck Check;
            public JObject Report;
            public bool Verified => Error == null && Check != null && Check.AllVerified;
            public JObject Json() => new JObject
            {
                ["rehearsal"] = Rehearsal, ["verified"] = Verified, ["transaction_status"] = TransactionStatus,
                ["error"] = Error, ["postconditions"] = Check?.ToJson(), ["read_back"] = Report
            };
        }

        private static Outcome Run(UIApplication uiapp, Document doc, Plan p, bool rehearse)
        {
            var o = new Outcome { Rehearsal = rehearse, RollbackConfirmed = true };
            var s = new Scratch();
            ExternalDefinition def = null;
            try
            {
                if (p.Definition != null)
                {
                    try { def = p.Definition(uiapp, rehearse, s); }
                    catch (Exception ex) { o.Error = "The shared parameter file could not be prepared: " + ex.Message; return o; }
                }
                using (var tx = new Transaction(doc, "Horizun: " + p.Op.Replace('_', ' ')))
                {
                    RevitErrorRecorder errors = RevitErrorRecorder.On(tx);
                    try
                    {
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                        p.Apply(uiapp, doc, def, s);
                        doc.Regenerate();
                        o.Check = p.Verify(doc, def, s);
                        o.Report = p.Report?.Invoke(doc, def, s);
                        if (rehearse || !o.Check.AllVerified)
                        {
                            Guard.RollbackResult rb = Guard.RollBack(tx);
                            o.TransactionStatus = rb.StatusName; o.RollbackConfirmed = rb.Confirmed;
                            if (!rehearse) o.Error = "a postcondition failed before the commit; the transaction was rolled back";
                            return o;
                        }
                        TransactionStatus st = tx.Commit();
                        o.TransactionStatus = st.ToString();
                        if (st != TransactionStatus.Committed) { o.Error = "Revit returned " + st + errors.Said(); return o; }
                        o.Committed = true;
                    }
                    catch (Exception ex)
                    {
                        o.Error = ex.Message + errors.Said();
                        try
                        {
                            if (tx.GetStatus() == TransactionStatus.Started)
                            {
                                Guard.RollbackResult rb = Guard.RollBack(tx);
                                o.TransactionStatus = rb.StatusName; o.RollbackConfirmed = rb.Confirmed;
                            }
                        }
                        catch { o.RollbackConfirmed = false; }
                        return o;
                    }
                }
                // The contract: the verdict is the model read AFTER the commit, not the one before.
                o.Check = p.Verify(doc, def, s);
                o.Report = p.Report?.Invoke(doc, def, s);
                return o;
            }
            finally
            {
                if (o.Report == null) o.Report = new JObject();
                if (s.SpfPath != null && !rehearse) o.Report["spf_definition_created"] = s.SpfDefinitionCreated;
                if (s.SpfSwitched)
                    try { uiapp.Application.SharedParametersFilename = s.PreviousSpf; }
                    catch (Exception ex) { o.Report["spf_restore_error"] = ex.Message; }
                if (s.TempSpf != null) try { System.IO.File.Delete(s.TempSpf); } catch { }
            }
        }

        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, Plan p)
        {
            var rp = new ResolvedPlan
            {
                Command = "horizun_manage_parameters", DocumentKey = gate.Fingerprint,
                RevitVersion = app.Application.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ExpectedCascadeCount = p.ValuesLost
            };
            var before = new Dictionary<string, string>(p.Before) { ["operation"] = p.Op, ["values_lost"] = p.ValuesLost.ToString() };
            rp.Elements.Add(new PlannedElement { UniqueId = p.Op + ":" + p.Subject, Category = p.Category, Action = p.Action, BeforeValues = before });
            return rp;
        }

        private static Plan Build(UIApplication uiapp, Document doc, string op, JObject r, out string error)
        {
            error = null;
            switch (op)
            {
                case "create_shared": return BuildCreateShared(uiapp, doc, r, out error);
                case "rebind": return BuildRebind(uiapp, doc, r, out error);
                case "remove_binding": return BuildRemove(doc, r, out error);
                case "global_create": return BuildGlobalCreate(doc, r, out error);
                case "global_set": return BuildGlobalSet(doc, r, out error);
                case "global_delete": return BuildGlobalDelete(doc, r, out error);
            }
            error = "operation " + op + " has no write plan.";
            return null;
        }
    }
}
