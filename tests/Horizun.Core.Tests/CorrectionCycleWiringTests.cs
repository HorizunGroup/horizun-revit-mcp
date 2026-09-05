// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE CORRECTION CYCLE AND THE PREVENTION GATE, GUARDED IN SOURCE.
//
// The Revit halves need a UIApplication and a Document, so nothing here can
// execute them. What can be checked from a desk is that the wiring is real:
// the audit stamps finding ids and records the set, the apply rehearses THROUGH
// the typed tools and re-audits after writing, the gate is consulted BEFORE the
// file is touched, and no Revit event was subscribed to on the way.
//
// The lesson behind every one of these: both surfaces were once pure rules
// with green tests and no caller, which reads exactly like a feature.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CorrectionCycleWiringTests
    {
        private static DirectoryInfo Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d;
        }

        private static string Source(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { Root().FullName }.Concat(parts).ToArray()));

        private static string Audit() => Source("src", "Horizun.Revit", "Commands", "AuditModelCommand.cs");
        private static string Apply() => Source("src", "Horizun.Revit", "Commands", "ApplyCorrectionsCommand.cs");
        private static string Save() => Source("src", "Horizun.Revit", "Commands", "SaveDocumentCommand.cs");
        private static string Export() => Source("src", "Horizun.Revit", "Commands", "ExportCommand.cs");
        private static string Gate() => Source("src", "Horizun.Revit", "Commands", "OperationGate.cs");

        // ---------------------------------------------------------- identity

        [Fact]
        public void Every_finding_is_stamped_with_its_id_and_the_reply_carries_the_set_fingerprint()
        {
            string src = Audit();
            Assert.Contains("finding[\"finding_id\"] = FindingIdentity.IdOf(", src);
            Assert.Contains("run.FindingSetFingerprint = FindingIdentity.SetFingerprint(run.DocumentFingerprint, top,", src);
            Assert.Contains("[\"finding_set_fingerprint\"] = run.FindingSetFingerprint,", src);
            // ONE identity scheme: the document half is the snapshot's digest.
            Assert.Contains("DocumentGate.IdentityOf(doc, app?.Application?.VersionNumber)?.FingerprintDigest()", src);
        }

        [Fact]
        public void The_audit_records_the_set_it_published_for_the_apply_to_read()
        {
            Assert.Contains("AuditFindingSetStore.Session.Record(FindingSetRecord.From(run.FindingSetFingerprint", Audit());
        }

        [Fact]
        public void No_placeholder_survives_in_a_correction_block()
        {
            Assert.DoesNotContain("<CHOOSE", Audit());
            Assert.Contains("[\"requires_input\"] = new JArray(\"template_view_id\")", Audit());
        }

        [Fact]
        public void The_rooms_finding_carries_the_typed_code_the_registry_filters_on()
        {
            string src = Audit();
            Assert.Contains("[\"problem_code\"] = b.code", src);
            Assert.Contains("RoomProblemCode.Unplaced", src);
            Assert.Contains("RoomProblemCode.NotEnclosed", src);
        }

        // ---------------------------------------------------------- the apply

        [Fact]
        public void The_apply_is_registered_published_and_in_the_audit_pack()
        {
            Assert.Contains("d.Register(new ApplyCorrectionsCommand(d.ResolveCommand));", Source("src", "Horizun.Revit", "App.cs"));
            Assert.NotNull(Horizun.Contracts.Contract.Find("horizun_apply_corrections"));
            Assert.Contains("horizun_apply_corrections", ToolPacks.MembersOf("audit"));
        }

        [Fact]
        public void The_apply_reads_the_recorded_audit_and_refuses_another_document()
        {
            string src = Apply();
            Assert.Contains("AuditFindingSetStore.Session.TryGet(setFingerprint, out record)", src);
            Assert.Contains("[\"code\"] = \"unknown_finding_set\"", src);
            Assert.Contains("!string.Equals(record.DocumentFingerprint, fingerprint, StringComparison.Ordinal)", src);
            Assert.Contains("ProposalRefusal.WrongDocument", src);
        }

        [Fact]
        public void The_cited_checks_are_re_run_and_compared_before_anything_rehearses_or_writes()
        {
            string src = Apply();
            int rerun = src.IndexOf("AuditModelCommand.RunChecks(app, doc, record.Top, null, checks)", StringComparison.Ordinal);
            int drift = src.IndexOf("FindingSetDrift.Describe(record, checks", StringComparison.Ordinal);
            int rehearse = src.IndexOf("child.Execute(app, ChildArguments(step.Arguments, gate, true)", StringComparison.Ordinal);
            int apply = src.IndexOf("ChildArguments(step.Arguments, gate, false)", StringComparison.Ordinal);
            Assert.True(rerun > 0 && drift > rerun && rehearse > drift && apply > rehearse,
                "the order must be: re-run cited checks, compare ids, rehearse through the typed tool, apply");
            Assert.Contains("[\"state\"] = \"stale_plan\"", src);
        }

        [Fact]
        public void The_rehearsal_is_the_typed_tools_own_dry_run_not_a_generated_argument_object()
        {
            string src = Apply();
            Assert.Contains("ApplicationOutcome.IsValidRehearsal(state)", src);
            Assert.Contains("[\"plan_resolved\"]?[\"fingerprint\"]", src);
            // And the token is issued only over a clean rehearsal, together with the plan.
            Assert.Contains("if (rehearsedCleanly) DocumentGate.RecordResolvedPlan(plan);", src);
            Assert.Contains("DocumentGate.StampConfirmation(preview, gate, Name, planHash, rehearsedCleanly,", src);
        }

        [Fact]
        public void The_apply_spends_the_token_against_the_recomputed_plan_before_the_confirmed_scope_opens()
        {
            string src = Apply();
            int confirm = src.IndexOf("DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null)", StringComparison.Ordinal);
            int scope = src.IndexOf("using (DocumentGate.EnterConfirmedAtomicPlan())", StringComparison.Ordinal);
            Assert.True(confirm > 0 && scope > confirm);
            // Delete's own fingerprint check rides along, as it does in execute_plan.
            Assert.Contains("args[\"__expected_plan_fingerprint\"] = step.ChildPlanFingerprint;", src);
        }

        [Fact]
        public void Children_run_under_the_same_permission_rules_as_a_direct_call()
        {
            string src = Apply();
            Assert.Contains("Core.Settings.IsToolAllowed(contract, out reason)", src);
            Assert.Contains("CorrectionActionState.NotPermitted", src);
        }

        [Fact]
        public void The_apply_decides_each_step_on_the_declaration_and_re_audits_afterwards()
        {
            string src = Apply();
            // The DECISION moved to CorrectionApplyLoop, in Core, so it can be
            // exercised without a Revit; what this guards is that the command still
            // hands it the typed child's own declaration rather than deciding on
            // Success, and that the executor it supplies is the only way in.
            Assert.Contains("CorrectionApplyLoop.Apply(actions, step =>", src);
            Assert.Contains("State = ApplicationOutcome.Read(applied.Data)", src);
            Assert.Contains("ApplicationOutcome.IsFullyApplied", Loop());
            int scopeEnd = src.IndexOf("// RE-AUDIT.", StringComparison.Ordinal);
            int rerun = src.IndexOf("AuditModelCommand.AuditRun after = AuditModelCommand.RunChecks(app, doc, record.Top, null, checks);", StringComparison.Ordinal);
            Assert.True(scopeEnd > 0 && rerun > scopeEnd);
            Assert.Contains("ReAuditRules.Compare(action, after.FindingFor(action.Check), after.CheckFailed(action.Check))", src);
        }

        [Fact]
        public void The_rollback_scope_is_stated_as_per_action_and_never_as_atomic()
        {
            string src = Apply();
            Match scope = Regex.Match(Loop(), "public const string RollbackScope = \"([a-z_]+)\";");
            Assert.True(scope.Success, "the rollback scope constant is gone");
            Assert.Equal("per_action", scope.Groups[1].Value);
            Assert.Contains("public const string RollbackScope = CorrectionApplyLoop.RollbackScope;", src);
            Assert.DoesNotContain("new TransactionGroup(", src);
            Assert.Contains("[\"rollback_scope\"] = RollbackScope,", src);
            Assert.Contains("[\"rollback_means\"] = RollbackMeans,", src);
        }

        [Fact]
        public void Nothing_in_the_cycle_reaches_arbitrary_code()
        {
            // The headers SAY it is not reachable; the code must not name it.
            Assert.DoesNotContain("horizun_execute_python", CodeOnly(Apply()));
            Assert.DoesNotContain("horizun_execute_python", CodeOnly(Source("src", "Horizun.Revit", "Core", "CorrectionRegistry.cs")));
        }

        /// <summary>The loop itself, now that it lives in Core.</summary>
        private static string Loop() => Source("src", "Horizun.Revit", "Core", "CorrectionApplyLoop.cs");

        private static string CodeOnly(string src)
            => string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        // ---------------------------------------------------------- the allowlist

        [Fact]
        public void The_atomic_plan_allowlist_is_a_superset_of_every_tool_the_registry_names()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "ExecutePlanCommand.cs");
            int start = src.IndexOf("HashSet<string> Allowed", StringComparison.Ordinal);
            int open = src.IndexOf('{', start);
            int close = src.IndexOf("};", open, StringComparison.Ordinal);
            var allowed = new HashSet<string>(
                Regex.Matches(src.Substring(open, close - open), "\"(horizun_[a-z_]+)\"")
                     .Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.Ordinal);

            foreach (string tool in CorrectionRegistry.Tools())
                Assert.True(allowed.Contains(tool),
                    "the correction registry names '" + tool + "' and horizun_execute_plan refuses it, so the " +
                    "correction cannot be composed atomically. Add it to the allowlist and the contract enum.");
        }

        // ---------------------------------------------------------- the gate

        [Fact]
        public void The_save_consults_the_gate_before_doc_Save_and_carries_the_decision()
        {
            string src = Save();
            int gate = src.IndexOf("OperationGate.Evaluate(app, doc, req[\"require_gate\"]", StringComparison.Ordinal);
            int refuse = src.IndexOf("if (gateDecision.Refusal != null) return gateDecision.Refusal;", StringComparison.Ordinal);
            int save = src.IndexOf("doc.Save();", StringComparison.Ordinal);
            Assert.True(gate > 0 && refuse > gate && save > refuse,
                "the gate must be evaluated and able to refuse ABOVE doc.Save(), so a refused save leaves the file untouched");
            Assert.Contains("GatedOperation.Save", src);
            Assert.Contains("payload[\"prevention\"] = gateDecision.Prevention;", src);
        }

        [Fact]
        public void The_export_consults_the_gate_before_any_exporter_runs_and_carries_the_decision_on_both_paths()
        {
            string src = Export();
            int gate = src.IndexOf("OperationGate.Evaluate(app, doc, request[\"require_gate\"]", StringComparison.Ordinal);
            int refuse = src.IndexOf("if (gateDecision.Refusal != null) return gateDecision.Refusal;", StringComparison.Ordinal);
            int dryRun = src.IndexOf("if (dryRun)", StringComparison.Ordinal);
            int export = src.IndexOf("doc.Export(", StringComparison.Ordinal);
            Assert.True(gate > 0 && refuse > gate && dryRun > refuse && export > refuse);
            Assert.Contains("GatedOperation.Export", src);
            Assert.Equal(2, Regex.Matches(src, Regex.Escape("[\"prevention\"] = gateDecision.Prevention;")).Count);
            // Deliberately outside the plan hash: a gate added between rehearsal and
            // apply must not read as a changed plan.
            Assert.DoesNotContain("\"require_gate\"", src.Substring(src.IndexOf("DocumentGate.PlanHash(request,", StringComparison.Ordinal), 400));
        }

        [Fact]
        public void Without_require_gate_the_gate_reads_nothing_and_decides_nothing()
        {
            string src = Gate();
            Assert.Contains("if (requireGate == null || requireGate.Type == JTokenType.Null) return result;", src);
            Assert.Contains("public bool Requested { get { return Prevention != null; } }", src);
        }

        [Fact]
        public void The_gate_measures_now_with_the_audits_own_checks_and_evaluator()
        {
            string src = Gate();
            Assert.Contains("AuditModelCommand.RunChecks(app, doc, top, options, null)", src);
            Assert.Contains("PreDeliveryGateRules.Evaluate(AuditModelCommand.Declared(request.Requirements)", src);
            Assert.Contains("AuditModelCommand.Measurements(doc, run)", src);
            Assert.Contains("OperationGateRules.Decide(request, evidence, clock)", src);
            Assert.Contains("if (!decision.Proceed)", src);
            // And the audit's own reply reads the same rows from the same units.
            string audit = Audit();
            Assert.Contains("PreDeliveryGateRules.Evaluate(Declared(requirementSet), Measurements(doc, run),", audit);
        }

        /// <summary>
        /// THE CLOCK IS READ ON THE OPERATION PATH AND NOWHERE ELSE.
        ///
        /// The gate's rules are Revit-free and take the reference time as an
        /// argument so an expiry is exact in a test. That is exactly why the read
        /// has to happen at the boundary: if a rules file ever reaches for
        /// DateTime.UtcNow itself, the rule stops being provable, and if the
        /// boundary stops reading it, the caller's now_utc quietly becomes the
        /// authority again - which is the defect this pair of assertions holds shut.
        /// </summary>
        [Fact]
        public void The_operation_resolves_the_reference_time_from_the_machine_clock_not_from_the_request()
        {
            string src = Gate();
            int resolve = src.IndexOf("GateClock.Resolve(request.NowUtc, DateTime.UtcNow)", StringComparison.Ordinal);
            int decide = src.IndexOf("OperationGateRules.Decide(request, evidence, clock)", StringComparison.Ordinal);
            Assert.True(resolve > 0 && decide > resolve,
                "the gated operation must resolve its reference clock from this machine before it decides");
            Assert.Contains("OperationGateRules.ToJson(request, evidence, decision, clock)", src);

            // And the rules themselves stay clock-free, so every one of them is exact
            // at a desk. GateClock takes the machine time as a parameter.
            foreach (string rules in new[] { "OperationGateRules.cs", "PreventionGateRules.cs", "PreDeliveryGateRules.cs" })
            {
                string text = File.ReadAllText(Path.Combine(Root().FullName, "src", "Horizun.Revit", "Core", rules));
                foreach (string line in text.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    Assert.False(line.Contains("DateTime.UtcNow") || line.Contains("DateTime.Now"),
                        rules + " reads a clock: '" + line.Trim() + "'. These rules take the reference time as an " +
                        "argument so an expiry is provable without waiting for one.");
                }
            }
        }

        [Fact]
        public void No_Revit_event_subscription_was_added_beyond_the_two_in_Interference()
        {
            string dir = Path.Combine(Root().FullName, "src", "Horizun.Revit");
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (string ev in new[] { "DocumentSaving", "DocumentSavingAs", "DocumentSynchronizingWithCentral",
                                              "DocumentClosing", "FileExporting", "DocumentOpened", "DocumentChanged",
                                              "ViewActivated", "Idling" })
                    if (Regex.IsMatch(text, @"\." + ev + @"\s*\+="))
                        offenders.Add(Path.GetFileName(file) + " subscribes to " + ev);
                foreach (Match m in Regex.Matches(text, @"\.(\w+)\s*\+=\s*On\w+"))
                    if (!file.EndsWith("Interference.cs", StringComparison.Ordinal))
                        offenders.Add(Path.GetFileName(file) + " subscribes to " + m.Groups[1].Value);
            }
            Assert.True(offenders.Count == 0, string.Join("; ", offenders));

            string interference = File.ReadAllText(Path.Combine(dir, "Core", "Interference.cs"));
            Assert.Equal(2, Regex.Matches(interference, @"\+=\s*On\w+").Count);
        }
    }
}
