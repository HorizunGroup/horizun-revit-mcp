// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// PlanLedger proves the DECISION. These prove the WIRING: that execute_plan really
// asks it, and that every tool a plan may contain really declares what it applied.
//
// They read the SOURCE, for the same reason PlanWiringTests does: none of these
// commands can be constructed without a Revit, so the mistake they guard against -
// a new tool added to the plan's allowlist without a declaration, or the apply loop
// quietly going back to reading Success - cannot be caught any other way. It is a
// coarse instrument and deliberately so.
//
// The failure mode without them is silent and expensive: an undeclared child reads
// as Uncertain and every plan using it starts refusing. That is the SAFE direction,
// but it is still a bug, and it should be caught here rather than in a model.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ApplicationDeclarationWiringTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        private static Dictionary<string, string> CommandSources()
        {
            string dir = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            return Directory.GetFiles(dir, "*.cs").ToDictionary(Path.GetFileName, File.ReadAllText);
        }

        private static string PlanSource() => CommandSources()["ExecutePlanCommand.cs"];

        [Fact]
        public void Document_session_never_claims_open_all_was_applied_to_an_already_open_document()
        {
            string src = CommandSources()["DocumentSessionCommand.cs"];
            int branch = src.IndexOf("if (already != null)", StringComparison.Ordinal);
            int end = src.IndexOf("UIDocument uidoc;", branch, StringComparison.Ordinal);
            Assert.True(branch >= 0 && end > branch);
            string alreadyOpen = src.Substring(branch, end - branch);

            Assert.Contains("MeasureWorksetConfiguration(already, openRequest)", alreadyOpen);
            Assert.Contains("openRequest.OpenAllWorksets && !alreadyWorksets.Applied", alreadyOpen);
            Assert.Contains("[\"workset_configuration_applied\"] = false", alreadyOpen);
            Assert.Contains("[\"workset_configuration_satisfied\"]", alreadyOpen);
            Assert.DoesNotContain("[\"workset_configuration_applied\"] = openRequest.OpenAllWorksets", alreadyOpen);
        }

        /// <summary>
        /// The tools a plan may contain, read out of the production allowlist rather than
        /// copied - so a tool added there is a tool this test immediately demands a
        /// declaration for.
        /// </summary>
        private static List<string> AllowedTools()
        {
            string src = PlanSource();
            int start = src.IndexOf("HashSet<string> Allowed", StringComparison.Ordinal);
            Assert.True(start > 0, "the plan's allowlist could not be found - this test is reading the wrong thing");
            int open = src.IndexOf('{', start);
            int close = src.IndexOf("};", open, StringComparison.Ordinal);
            var tools = Regex.Matches(src.Substring(open, close - open), "\"(horizun_[a-z_]+)\"")
                             .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.NotEmpty(tools);
            return tools;
        }

        [Fact]
        public void Every_tool_a_plan_may_contain_declares_what_it_applied()
        {
            var sources = CommandSources();
            var missing = new List<string>();

            foreach (string tool in AllowedTools())
            {
                // Where the tool is implemented. The recipe-backed tools are thin subclasses
                // in RecipeTools.cs whose whole result is built by RecipeCommand, so the
                // declaration belongs to the base and is looked for there.
                var owner = sources.FirstOrDefault(kv =>
                    kv.Value.Contains("Name => \"" + tool + "\""));
                Assert.False(owner.Key == null, "no command source declares " + tool);

                string body = owner.Key == "RecipeTools.cs" ? sources["RecipeCommand.cs"] : owner.Value;
                if (!body.Contains("ApplicationOutcome.Stamp"))
                    missing.Add(tool + " (" + owner.Key + ")");
            }

            Assert.True(missing.Count == 0,
                "These tools may appear in an atomic plan and never declare what they applied, so every plan " +
                "containing one now refuses: " + string.Join(", ", missing));
        }

        /// <summary>
        /// The files that implement the tools a plan may contain, keyed by file name. The
        /// recipe-backed tools are thin subclasses whose whole result is built by
        /// RecipeCommand, so the base is what carries their obligations.
        /// </summary>
        private static Dictionary<string, string> PlanToolSources()
        {
            var sources = CommandSources();
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string tool in AllowedTools())
            {
                var owner = sources.FirstOrDefault(kv => kv.Value.Contains("Name => \"" + tool + "\""));
                Assert.False(owner.Key == null, "no command source declares " + tool);
                string file = owner.Key == "RecipeTools.cs" ? "RecipeCommand.cs" : owner.Key;
                owners[file] = sources[file];
            }
            return owners;
        }

        /// <summary>Source split into lines, so a rule can ignore what is only commentary.</summary>
        private static IEnumerable<string> SourceLines(string src)
            => src.Split('\n').Select(line => line.TrimEnd('\r'));

        [Fact]
        public void No_plan_tool_throws_away_the_status_its_commit_returned()
        {
            // THE BLOCKER review found: create_schedule called tx.Commit() and discarded
            // the TransactionStatus, then declared the literal "Committed". Revit's
            // Commit() answers RolledBack or Pending WITHOUT throwing - that is the entire
            // reason Guard exists - so a discarded status is a declaration with no evidence
            // under it.
            //
            // Two shapes are acceptable: Guard.Commit (which throws on anything that is not
            // Committed) or assigning the returned status to something. A bare
            // `whatever.Commit();` as a statement is neither.
            var offenders = new List<string>();

            foreach (var file in PlanToolSources())
                foreach (Match m in Regex.Matches(file.Value, @"^[ \t]*([A-Za-z_][A-Za-z0-9_]*)\.Commit\(\s*\)\s*;",
                                                  RegexOptions.Multiline))
                    offenders.Add(file.Key + ": " + m.Value.Trim());

            Assert.True(offenders.Count == 0,
                "These commit and discard the TransactionStatus Revit returned, so whatever they declare " +
                "afterwards rests on nothing: " + string.Join(" | ", offenders) +
                ". Use Guard.Commit, or capture the returned status and decide on it.");
        }

        [Fact]
        public void The_literal_committed_status_is_only_used_where_the_commit_was_actually_checked()
        {
            // Passing ApplicationOutcome.Committed is honest ONLY when reaching that line
            // proves the commit was Committed - which is what Guard.Commit and
            // Guard.Assimilate buy, by throwing on anything else. A file that names the
            // literal without one of them is asserting a status it never read.
            var offenders = new List<string>();

            foreach (var file in PlanToolSources())
            {
                if (!file.Value.Contains("ApplicationOutcome.Committed")) continue;

                bool checks = file.Value.Contains("Guard.Commit") || file.Value.Contains("Guard.Assimilate");
                // RecipeCommand commits through Recipe.cs, which uses Guard.Commit, and
                // turns its SilentRollbackException into a failure.
                if (!checks && file.Key == "RecipeCommand.cs")
                    checks = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "Recipe.cs"))
                                 .Contains("Guard.Commit") && file.Value.Contains("SilentRollbackException");

                if (!checks) offenders.Add(file.Key);
            }

            Assert.True(offenders.Count == 0,
                "These declare the literal Committed status without committing through a checked path: " +
                string.Join(", ", offenders));
        }

        [Fact]
        public void Create_schedule_declares_the_status_its_commit_returned_and_never_a_literal()
        {
            // Named specifically because this is the file the general rules above were
            // written from, and a regression here is the one that ships a false "Committed".
            string src = CommandSources()["CreateScheduleCommand.cs"];

            Assert.Contains("commitStatus = Guard.Commit(tx, \"create schedule\")", src);
            Assert.Contains("commitStatus = ex.Status", src);
            // A status that is not Committed short-circuits BEFORE the post-commit read:
            // the element may not exist, and an ElementId can be reused.
            Assert.Contains("if (commitStatus != TransactionStatus.Committed)", src);
            Assert.Contains("CommandResult.FailWithDetail", src);
            // And the declaration is derived from that variable plus the measured
            // post-condition, never from the constant.
            Assert.Contains("WriteTally.OneObject(commitStatus.ToString(), postconditionVerified)", src);
            Assert.DoesNotContain("ApplicationOutcome.Committed", src);
        }

        [Fact]
        public void Create_family_does_not_discard_the_reference_subtransaction_status()
        {
            string src = CommandSources()["CreateFamilyCommand.cs"];
            string guard = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "Guard.cs"));

            Assert.Contains("Guard.Commit(referenceTx, \"create family reference planes\")", src);
            Assert.DoesNotContain("referenceTx.Commit();", src);
            Assert.Contains("public static TransactionStatus Commit(SubTransaction t, string what)", guard);
        }

        [Fact]
        public void Create_schedule_re_reads_every_property_its_request_named()
        {
            // The post-condition used to cover fields, include_links and itemized - three
            // of the five things the request carries. Name and category were never re-read,
            // and the reply reported `category` off the Category object resolved BEFORE the
            // commit, which is the request talking back rather than the model.
            string src = CommandSources()["CreateScheduleCommand.cs"];

            // The verdict comes from the checklist, not from an && of whatever was handy.
            Assert.Contains("bool postconditionVerified = postcondition.AllVerified;", src);

            // And the checklist is told WHICH properties it must cover, so deleting one
            // comparison fails coverage instead of passing on the ones that remain.
            Assert.Matches(new Regex(
                @"new PostconditionCheck\(\s*""name"",\s*""category"",\s*""fields"",\s*" +
                @"\r?\n?\s*""include_links"",\s*""itemized""\s*\)"), src);

            // All five are MEASURED. Compare/Record only - deliberately not Unreadable,
            // which is the catch-path call every one of them also has. Measured: accepting
            // Unreadable as evidence let three of these mutations survive, because renaming
            // the happy-path key still left the catch matching the pattern.
            foreach (string property in new[] { "\"name\"", "\"category\"", "\"fields\"",
                                                "\"include_links\"", "\"itemized\"" })
                Assert.Matches(new Regex(@"postcondition\.(Compare|Record)\(\s*" + Regex.Escape(property)), src);

            // And the two that were missing are compared against a READ of the committed
            // schedule, not against the pre-commit objects.
            Assert.Contains("postcondition.Compare(\"name\", scheduleName, verified.Name)", src);

            // The category check is read off the committed definition AND its verdict is the
            // comparison of the two ids. Asserting only that CategoryId is mentioned lets a
            // hardcoded `true` verdict survive - measured: that mutation passed every other
            // test in this file.
            int categoryCheck = src.IndexOf("postcondition.Record(\"category\"", StringComparison.Ordinal);
            Assert.True(categoryCheck > 0, "the category comparison could not be found");
            string categoryRegion = src.Substring(categoryCheck,
                src.IndexOf("catch (Exception ex)", categoryCheck, StringComparison.Ordinal) - categoryCheck);
            Assert.Contains("actualCategoryId == category.Id", categoryRegion);
            Assert.Contains("ElementId actualCategoryId = verified.Definition.CategoryId;", src);

            // The SUCCESS payload's category stops echoing the pre-commit resolution and is
            // read off the committed schedule instead. Scoped to that payload on purpose:
            // the dry run and the did-not-commit reply have no committed schedule to read,
            // so naming the resolved category there is the only thing they can honestly do.
            int success = src.IndexOf("var csResult = new JObject", StringComparison.Ordinal);
            Assert.True(success > 0, "the success payload could not be found");
            string successPayload = src.Substring(success,
                src.IndexOf("return CommandResult.Ok(csResult);", success, StringComparison.Ordinal) - success);
            Assert.DoesNotContain("[\"category\"] = category.Name", successPayload);
            Assert.Contains("verified.Definition.CategoryId", successPayload);

            // And the checklist travels with the answer.
            Assert.Contains("[\"postcondition\"] = postcondition.ToJson()", src);
        }

        [Fact]
        public void Set_keynote_reports_write_failures_from_the_post_commit_read_alone()
        {
            // writes_failed was verifyFailed + failed.Count, which double-counted a refused
            // write (in the detailed array AND unverified afterwards) and added ids that
            // never became write targets at all.
            string src = CommandSources()["SetKeynoteCommand.cs"];

            Assert.Contains("[\"writes_failed\"] = verifyFailed,", src);
            Assert.DoesNotContain("[\"writes_failed\"] = verifyFailed + failed.Count", src);

            // The old value survives only under a name that says what it is.
            Assert.Contains("[\"writes_failed_legacy\"] = verifyFailed + failed.Count,", src);
            Assert.Contains("DEPRECATED", src);

            // The three are published apart, and the refusal count is the pre-commit
            // diagnostic that must never be summed with the evidence.
            Assert.Contains("[\"ids_unresolved\"] = unresolvedIds,", src);
            Assert.Contains("[\"writes_refused_in_transaction\"] = writesRefused,", src);
            Assert.Contains("[\"targets_unverified_after_commit\"] = verifyFailed,", src);
        }

        [Fact]
        public void The_mixed_failure_array_feeds_no_semantic_total_in_set_keynote()
        {
            // `failed` is one array appended to from three places. Its Count may appear in
            // the deprecated field and nowhere else: every semantic number - what was
            // requested, what failed, what the declaration rests on - has to come from a
            // count that means one thing.
            string src = CommandSources()["SetKeynoteCommand.cs"];

            // Comment lines are not code: the deprecation note names the old expression on
            // purpose, and counting that would make the rule impossible to document.
            string code = string.Join(Environment.NewLine, SourceLines(src)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            var uses = Regex.Matches(code, @"failed\.Count").Cast<Match>().Count();
            int inLegacy = Regex.Matches(code, @"\[""writes_failed_legacy""\] = verifyFailed \+ failed\.Count").Cast<Match>().Count();
            int inFreeze = Regex.Matches(code, @"int unresolvedIds = failed\.Count;").Cast<Match>().Count();

            Assert.Equal(1, inLegacy);
            Assert.Equal(1, inFreeze);
            Assert.True(uses == inLegacy + inFreeze,
                "failed.Count is read " + uses + " times but is only allowed in the deprecated field and in the " +
                "pre-write freeze of unresolvedIds; every other total must come from a count that means one thing.");
        }

        [Fact]
        public void Set_keynote_counts_its_three_failures_apart_and_never_from_the_mixed_array()
        {
            // `failed` is ONE array appended to from three places: ids that never resolved,
            // a Set() that threw, and a Set() that was refused. Reading its Count after the
            // write loop mixes all three, and deriving `requested` from it counted a
            // refused write twice - once as a target, once as a failure - while calling it
            // an unresolved id.
            string src = CommandSources()["SetKeynoteCommand.cs"];

            // The resolution failures are frozen BEFORE the write loop can add to them.
            Assert.Contains("int unresolvedIds = failed.Count;", src);
            Assert.True(src.IndexOf("int unresolvedIds = failed.Count;", StringComparison.Ordinal) <
                        src.IndexOf("// ---- Write. ----", StringComparison.Ordinal),
                        "unresolvedIds must be taken before the write loop appends its own refusals");

            // Refused writes are their own count, not folded into either of the others.
            Assert.Contains("writesRefused++", src);

            // And the declaration takes the three separately.
            Assert.Contains("WriteTally.PerTarget", src);
            Assert.Contains("unresolvedIds: unresolvedIds", src);
            Assert.Contains("verifiedTargets: verified", src);
            Assert.Contains("unverifiedTargets: verifyFailed", src);

            // The exact regression: requested derived from the mixed array.
            Assert.DoesNotContain("byTarget.Count + failed.Count", src);
        }

        [Fact]
        public void No_plan_tool_returns_a_payload_nothing_ever_declared_on()
        {
            // The rule that would have caught delete's purge-unsupported branch, which
            // returned CommandResult.Ok over an inline object literal that no Stamp call
            // could possibly have reached. A payload has to be a named local (or come from
            // a helper that stamps) so that a declaration can be put on it at all.
            var offenders = new List<string>();

            foreach (var file in PlanToolSources())
            {
                foreach (Match m in Regex.Matches(file.Value, @"CommandResult\.Ok\(\s*new "))
                    offenders.Add(file.Key + ": builds a result over an inline object literal");

                // EVERY Ok over a named payload, not only the ones in `return` position.
                // Four sites in this allowlist build their result as an ARGUMENT -
                // `return FallbackDecision.Attach(CommandResult.Ok(result), ...)` - and a
                // rule that only looked at `return CommandResult.Ok(x);` walked straight
                // past all four. Measured during the second audit pass.
                foreach (Match m in Regex.Matches(file.Value,
                         @"CommandResult\.Ok\(([A-Za-z_][A-Za-z0-9_]*)\)"))
                {
                    string local = m.Groups[1].Value;
                    if (Regex.IsMatch(file.Value, @"ApplicationOutcome\.Stamp\w*\(\s*" + local + @"\b")) continue;

                    // Or the local came out of a helper in this file that stamps for it.
                    Match assigned = Regex.Match(file.Value,
                        @"\b" + local + @"\s*=\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(");
                    if (assigned.Success &&
                        Regex.IsMatch(file.Value,
                            @"\b" + assigned.Groups[1].Value + @"\s*\([^)]*\)\s*\{[\s\S]*?ApplicationOutcome\.Stamp"))
                        continue;

                    offenders.Add(file.Key + ": returns '" + local + "' with no declaration on it");
                }
            }

            Assert.True(offenders.Count == 0,
                "These return a result a plan cannot read, so every plan containing them refuses: " +
                string.Join(" | ", offenders));
        }

        [Fact]
        public void The_purge_branch_that_could_not_look_declares_uncertain()
        {
            // Delete's GetUnused-unsupported branch. It opens no transaction, so on
            // transaction_status alone it is shaped exactly like a legitimate no-op - and
            // it is the opposite of one. It must say so in the declaration, or a plan
            // assimilates over a purge nobody was able to examine.
            string src = CommandSources()["DeleteCommand.cs"];

            int branch = src.IndexOf("[\"purge_supported\"] = false", StringComparison.Ordinal);
            Assert.True(branch > 0, "the purge-unsupported branch could not be found");

            // Look only at that branch, up to its return.
            int end = src.IndexOf("return CommandResult.Ok(unexamined);", branch, StringComparison.Ordinal);
            Assert.True(end > branch, "the purge-unsupported branch no longer returns a named, declared payload");

            string region = src.Substring(branch, end - branch);
            Assert.Contains("ApplicationState.Uncertain", region);
            Assert.Contains("ApplicationOutcome.Stamp(unexamined", region);
        }

        [Fact]
        public void The_apply_loop_decides_on_the_ledger_and_not_on_Success()
        {
            string src = PlanSource();

            // The child's answer goes through the ledger, which is what reads the declaration.
            Assert.Contains("applyLedger.RecordExecuted", src);
            // And a false answer from it throws, which is what reaches the rollback path.
            Assert.Matches(new Regex(@"if\s*\(!applyLedger\.RecordExecuted[\s\S]{0,400}?throw new InvalidOperationException"), src);
        }

        [Fact]
        public void Actions_verified_is_never_the_size_of_the_execution_trace()
        {
            string src = PlanSource();

            // The exact regression: actions_verified = executed.Count.
            Assert.DoesNotContain("[\"actions_verified\"] = executed.Count", src);
            // It comes from the ledger's counted total instead.
            Assert.Contains("applyLedger.SuccessPayload", src);
        }

        [Fact]
        public void The_dry_run_withholds_its_confirmation_when_a_rehearsal_did_not_resolve()
        {
            string src = PlanSource();

            Assert.Contains("bool rehearsedCleanly = ledger.RehearsedCleanly;", src);
            // Both the token and the recorded plan are gated on it - a token without a plan,
            // or a plan without a token, would each be half a refusal.
            Assert.Contains("if (rehearsedCleanly) DocumentGate.RecordResolvedPlan", src);
            Assert.Contains("planHash, rehearsedCleanly,", src);
        }

        [Fact]
        public void A_deferred_reference_is_recorded_as_a_dirty_rehearsal()
        {
            string src = PlanSource();

            Assert.Contains("ledger.RecordDeferred", src);
            Assert.Contains("recheckLedger.RecordDeferred", src);
            Assert.DoesNotContain("rows.Add(PlanLedger.Deferred", src);
        }

        [Fact]
        public void Reference_values_are_compared_before_the_consumer_is_invoked()
        {
            string src = PlanSource();
            int compare = src.IndexOf("PlanReferences.CompareBinding", StringComparison.Ordinal);
            int childExecute = src.IndexOf("CommandResult result = _resolve", compare, StringComparison.Ordinal);

            Assert.True(compare > 0, "the apply-time reference binding comparison is missing");
            Assert.True(childExecute > compare, "the consumer runs before its reference binding is checked");
            Assert.Contains("reference_binding_changed; the consumer was not executed", src);
        }

        [Fact]
        public void Delete_materialises_targets_and_cascades_and_rechecks_the_child_fingerprint()
        {
            string delete = CommandSources()["DeleteCommand.cs"];
            string plan = PlanSource();

            Assert.Contains("DocumentGate.RecordResolvedPlan(DeletePlan", delete);
            Assert.Contains("ExpectedCascadeCount = cascades?.Count ?? 0", delete);
            Assert.Contains("ValidateDeletePlan", delete);
            Assert.Contains("__expected_plan_fingerprint", delete);
            Assert.Contains("child[\"__expected_plan_fingerprint\"] = expectedChildPlan", plan);
        }

        [Fact]
        public void Create_elements_stamps_before_building_the_command_result()
        {
            string src = CommandSources()["CreateElementsCommand.cs"];
            int stamp = src.IndexOf("ApplicationOutcome.StampRehearsal(result", StringComparison.Ordinal);
            int resultFactory = src.IndexOf("CommandResult rehearsal", StringComparison.Ordinal);

            Assert.True(stamp > 0 && resultFactory > stamp,
                "create_elements depends on CommandResult.Ok retaining a mutable JObject alias");
        }

        [Fact]
        public void The_pre_apply_recheck_holds_the_same_bar_as_the_dry_run()
        {
            Assert.Contains("if (!recheckLedger.RehearsedCleanly)", PlanSource());
        }

        [Fact]
        public void The_plan_takes_its_decision_from_the_declaration_not_from_a_list_of_tool_names()
        {
            string src = PlanSource();

            // The allowlist is legitimate - it says WHICH tools may appear. What must not
            // exist is a second list deciding how much any of them is to be believed.
            int allowlistEnd = src.IndexOf("};", src.IndexOf("HashSet<string> Allowed", StringComparison.Ordinal),
                                           StringComparison.Ordinal);
            string afterAllowlist = src.Substring(allowlistEnd);

            // The command naming ITSELF is identity, not a decision about another tool.
            var toolNamesInLogic = Regex.Matches(afterAllowlist, "\"horizun_[a-z_]+\"")
                                        .Cast<Match>().Select(m => m.Value).Distinct()
                                        .Where(n => n != "\"horizun_execute_plan\"").ToList();

            Assert.True(toolNamesInLogic.Count == 0,
                "The plan's execution logic names specific tools: " + string.Join(", ", toolNamesInLogic) +
                ". Whether an action counts as applied must come from what the action declared, so that a new " +
                "tool is covered by default instead of when somebody remembers to edit this file.");
        }

        [Fact]
        public void The_failure_diagnostic_still_carries_everything_a_caller_branches_on()
        {
            string src = PlanSource();

            foreach (string field in new[]
                     {
                         "transactionGroupStarted:", "transactionGroupStatus:", "rollbackAttempted:",
                         "rollbackStatus:", "executionTrace:", "failedAction:"
                     })
                Assert.Contains(field, src);

            // rollback_confirmed is computed inside PlanFailure from the group's FINAL
            // status; the plan must not be assembling its own answer to that question.
            Assert.DoesNotContain("[\"rollback_confirmed\"]", src);
        }

        [Fact]
        public void The_failure_path_cannot_lose_its_diagnostic_to_a_throwing_rollback()
        {
            // Every read in the catch is a call into a Revit that has already misbehaved
            // once. An escape there costs the whole structured answer at the moment the
            // model's state is least certain.
            string src = PlanSource();

            Assert.Matches(new Regex(@"try\s*\{\s*statusBeforeRollback\s*=\s*group\.GetStatus\(\)"), src);
            Assert.Matches(new Regex(@"try\s*\{\s*rollbackStatus\s*=\s*Guard\.RollBack\(group\)\.StatusName"), src);
            Assert.Contains("rollbackStatus = \"threw\"", src);
            // And the rollback is actually ATTEMPTED while the group is still open. Without
            // this the guard above can be neutered without a single test noticing: the
            // diagnostic still gets built, it just reports a rollback nobody tried.
            Assert.Contains("if (statusReadError == null && statusBeforeRollback == TransactionStatus.Started)", src);
            Assert.Contains("rollbackAttempted = true;", src);
            Assert.Contains("[\"rollback_error\"]", src);
            Assert.Contains("[\"transaction_group_status_error\"]", src);
            // And the diagnostic is still what gets returned.
            Assert.Contains("return CommandResult.FailWithDetail(PlanFailure.Message(diag), diag);", src);
        }

        [Fact]
        public void A_child_s_structured_answer_reaches_the_plan_trace_on_all_three_paths()
        {
            // AGENTS.md: a caller branches on the fallback BLOCK, never on the wording of an
            // error. Four allowlist tools can return one, so the plan has to carry it.
            string src = PlanSource();

            Assert.Equal(3, Regex.Matches(src, @"FallbackJson\((rehearsal|again|result)\)").Count);
            Assert.Contains("rehearsal.Detail, FallbackJson(rehearsal)", src);
            Assert.Contains("again.Detail, FallbackJson(again)", src);
            Assert.Contains("result.Detail, FallbackJson(result)", src);
        }

        [Fact]
        public void The_dry_run_names_the_actions_it_could_not_rehearse_at_all()
        {
            // A deferred action is never previewed, and rehearsed_cleanly says nothing about
            // it. An approver reading only that field could not tell one was in the graph.
            string src = PlanSource();

            Assert.Contains("[\"actions_not_rehearsed\"] = notRehearsed.Count", src);
            Assert.Contains("[\"not_rehearsed\"] = notRehearsed", src);
            Assert.Contains("[\"rehearsed_cleanly_means\"]", src);
        }

        [Fact]
        public void The_gate_the_confirmation_and_the_post_commit_verification_are_all_still_there()
        {
            string src = PlanSource();

            // Requirement 7: this change must not have bought its guarantee by weakening
            // any of the ones that already existed.
            Assert.Contains("DocumentGate.ForMutation", src);
            Assert.Contains("DocumentGate.RequireConfirmation", src);
            Assert.Contains("DocumentGate.EnterConfirmedAtomicPlan", src);
            Assert.Contains("Guard.Assimilate", src);
            Assert.Contains("Guard.RollBack(group)", src);
        }
    }
}
