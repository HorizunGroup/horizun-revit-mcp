// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_fix_planimetry's CONTRACT and WIRING. The command itself cannot be
// constructed without a Revit, so the properties that decide whether it is safe
// are asserted from the shared declaration and from the shipped source - the
// same technique PlanimetryContractTests and FallbackWiringTests use, and for
// the same reason.
//
// The contract half: it is a MUTATING tool (so the dispatcher demands an
// idempotency key and the MCP annotations warn a client), it defaults to a dry
// run, it takes a confirmation token, its schema is closed, and it is hidden
// from a read-only profile - the auditor is visible there and the corrector must
// not be.
//
// The wiring half, in the order the damage would be worst:
//
//   1. NO PYTHON. The correction path may not mention execute_python at all: a
//      capability that falls back to arbitrary code is not a typed capability.
//   2. NO PDF, NO EXPORT. Verification reads the database. A fix that proved
//      itself by exporting a sheet would be reading a picture of its own work.
//   3. ATOMIC. One TransactionGroup, and the rollback is reachable from every
//      failure path.
//   4. ORGANISATION-NEUTRAL. No company's names, codes, scales or patterns.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryFixContractTests
    {
        private const string Tool = "horizun_fix_planimetry";

        private static CommandContract Fix() => Contract.Find(Tool);

        // =====================================================================
        // CONTRACT
        // =====================================================================

        [Fact]
        public void The_tool_is_declared_and_forwards_to_the_addin()
        {
            CommandContract c = Fix();
            Assert.NotNull(c);
            Assert.Equal(Tool, c.Command);
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
        }

        [Fact]
        public void It_is_MutatingUnlessDryRun_so_a_dry_run_needs_no_key_and_an_apply_does()
        {
            // The dispatcher reads exactly this to decide whether to demand a durable
            // idempotency key. MutatingUnlessDryRun is what makes dry_run:true free and
            // dry_run:false at-most-once.
            Assert.Equal(ToolEffect.MutatingUnlessDryRun, Fix().Effect);
        }

        [Fact]
        public void The_contract_injects_the_idempotency_key_into_the_schema()
        {
            JObject key = (JObject)Fix().InputSchema["properties"]["idempotency_key"];
            Assert.NotNull(key);
            Assert.Equal("string", (string)key["type"]);
            Assert.Equal(200, (int)key["maxLength"]);
        }

        [Fact]
        public void It_is_not_destructive_and_not_open_world()
        {
            CommandContract c = Fix();
            // destructiveHint is about removing something a caller cannot get back. Every
            // operation here is a reversible property write or ONE title-block instance;
            // deletion belongs to horizun_delete_verified, which does carry the hint.
            Assert.False(c.Destructive,
                "fix_planimetry writes reversible properties; if an operation that destroys " +
                "something is ever added, this hint must change with it");
            Assert.False(c.OpenWorld, "it changes the model and nothing outside it");
        }

        [Fact]
        public void The_schema_is_closed_and_requires_the_document_the_audit_and_the_actions()
        {
            JObject schema = Fix().InputSchema;
            Assert.False((bool)schema["additionalProperties"]);
            string[] required = schema["required"].Select(t => (string)t).ToArray();
            Assert.Contains("target_document", required);
            Assert.Contains("source_audit", required);
            Assert.Contains("actions", required);
        }

        [Fact]
        public void The_schema_defaults_to_a_dry_run_and_names_the_confirmation_token()
        {
            JObject properties = (JObject)Fix().InputSchema["properties"];
            Assert.True((bool)properties["dry_run"]["default"],
                "dry_run must default to TRUE: a correction that writes on the first ordinary call " +
                "is a correction nobody approved");
            Assert.NotNull(properties["confirmation_token"]);
        }

        [Fact]
        public void The_schema_publishes_exactly_the_nine_operations_the_catalog_implements()
        {
            string[] published = Fix().InputSchema["properties"]["actions"]["items"]
                ["properties"]["operation"]["enum"].Select(t => (string)t).ToArray();

            // The schema and the pure catalog are two declarations of one fact, and this
            // is the only place they meet. Either drifting is a tool that advertises an
            // operation nobody implemented, or implements one nobody may call.
            Assert.Equal(PlanimetryFixRules.Catalog.Select(o => o.Name).OrderBy(x => x, StringComparer.Ordinal),
                         published.OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void Every_operations_own_fields_are_declared_in_the_action_schema()
        {
            var declared = new HashSet<string>(
                ((JObject)Fix().InputSchema["properties"]["actions"]["items"]["properties"])
                    .Properties().Select(p => p.Name), StringComparer.Ordinal);

            foreach (PlanimetryFixOperation op in PlanimetryFixRules.Catalog)
                foreach (string field in op.Fields)
                    Assert.True(declared.Contains(field),
                        "operation '" + op.Name + "' accepts field '" + field + "', which the schema does not " +
                        "declare - a caller sending it would be refused by the schema before the command " +
                        "could honour it");
        }

        [Fact]
        public void Each_action_must_cite_a_finding_with_its_evidence()
        {
            JObject action = (JObject)Fix().InputSchema["properties"]["actions"]["items"];
            Assert.Contains("finding", action["required"].Select(t => (string)t));

            JObject finding = (JObject)action["properties"]["finding"];
            Assert.False((bool)finding["additionalProperties"]);
            string[] required = finding["required"].Select(t => (string)t).ToArray();
            foreach (string field in new[]
                     { "rule_id", "requirement_set", "requirement_set_version", "element_ids", "observed" })
                Assert.Contains(field, required);
        }

        [Fact]
        public void The_batch_is_bounded()
        {
            JObject actions = (JObject)Fix().InputSchema["properties"]["actions"];
            Assert.Equal(1, (int)actions["minItems"]);
            Assert.Equal(100, (int)actions["maxItems"]);
        }

        [Fact]
        public void Geometry_declares_its_units_and_its_tolerance()
        {
            JObject properties = (JObject)Fix().InputSchema["properties"];
            Assert.Equal(new[] { "mm", "m", "feet" },
                         properties["units"]["enum"].Select(t => (string)t).ToArray());
            Assert.Equal("mm", (string)properties["units"]["default"]);
            Assert.NotNull(properties["tolerance"]);
            Assert.Equal(0, (int)properties["tolerance"]["exclusiveMinimum"]);
        }

        [Fact]
        public void A_point_is_two_dimensional_because_sheet_and_view_frames_have_no_third_axis()
        {
            JObject point = (JObject)Fix().InputSchema["properties"]["actions"]["items"]
                ["properties"]["point"];
            Assert.Equal(2, (int)point["minItems"]);
            Assert.Equal(2, (int)point["maxItems"]);
        }

        [Fact]
        public void The_scale_bounds_in_the_schema_match_the_pure_rule()
        {
            JObject scale = (JObject)Fix().InputSchema["properties"]["actions"]["items"]
                ["properties"]["scale"];
            Assert.Equal(PlanimetryFixRules.MinScale, (int)scale["minimum"]);
            Assert.Equal(PlanimetryFixRules.MaxScale, (int)scale["maximum"]);
        }

        [Fact]
        public void The_description_promises_the_guarantees_and_names_the_refusals()
        {
            string d = Fix().Description;
            foreach (string promise in new[]
                     { "stale finding", "stale observation", "TransactionGroup", "re-reads",
                       "resolved", "persistent", "NEW" })
                Assert.Contains(promise, d, StringComparison.Ordinal);

            // And the later phases are named as refused rather than left to be assumed.
            foreach (string refusal in new[] { "packing", "auto-tagging", "revision generation" })
                Assert.Contains(refusal, d, StringComparison.Ordinal);
        }

        [Fact]
        public void The_description_states_that_no_export_and_no_Python_are_involved()
        {
            string d = Fix().Description;
            Assert.Contains("No PDF or export", d, StringComparison.Ordinal);
            Assert.Contains("no Python is involved", d, StringComparison.Ordinal);
        }

        // =====================================================================
        // PERMISSIONS
        // =====================================================================

        [Fact]
        public void A_read_only_profile_hides_the_corrector_while_keeping_the_auditor()
        {
            WithSettings("{\"permission_profile\":\"read_only\"}", () =>
            {
                string reason;
                Assert.False(Settings.IsToolAllowed(Fix(), out reason),
                    "a read-only machine must not be offered a tool that writes");
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_audit_planimetry"), out _),
                    "the auditor is read-only and must stay visible");
            });
        }

        [Fact]
        public void It_is_visible_from_safe_write_upward()
        {
            foreach (string profile in new[] { "safe_write", "full_write", "unsafe_code" })
                WithSettings("{\"permission_profile\":\"" + profile + "\"}", () =>
                {
                    string reason;
                    Assert.True(Settings.IsToolAllowed(Fix(), out reason),
                        Tool + " must be available under " + profile + " but was refused: " + reason);
                });
        }

        [Fact]
        public void An_explicit_denial_hides_it_without_touching_the_auditor()
        {
            WithSettings("{\"denied_tools\":[\"" + Tool + "\"]}", () =>
            {
                Assert.False(Settings.IsToolAllowed(Fix(), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_audit_planimetry"), out _));
            });
        }

        // =====================================================================
        // SOURCE WIRING
        // =====================================================================

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

        private static string CommandSource() => File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "FixPlanimetryCommand.cs"));

        private static string RulesSource() => File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "PlanimetryFixRules.cs"));

        [Fact]
        public void The_correction_path_never_mentions_Python()
        {
            foreach (string source in new[] { CommandSource(), RulesSource() })
            {
                Assert.DoesNotContain("execute_python", source, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("PythonEngine", source, StringComparison.Ordinal);
                Assert.DoesNotContain("IronPython", source, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_correction_path_never_exports_or_prints_to_verify()
        {
            // Verification reads the Revit database. A fix that proved itself from an
            // exported sheet would be reading a picture of its own work.
            string source = CommandSource();
            foreach (string forbidden in new[]
                     {
                         ".Export(", "PrintManager", "PDFExportOptions", "DWGExportOptions",
                         "NavisworksExportOptions", "ImageExportOptions", "horizun_export",
                         "horizun_capture_view"
                     })
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_correction_path_writes_no_file_of_its_own()
        {
            string source = CommandSource();
            foreach (string forbidden in new[]
                     {
                         "File.WriteAllText", "File.WriteAllBytes", "File.Create(", "StreamWriter",
                         "File.AppendAllText", "File.Copy(", "File.Move(", "File.Delete(",
                         "File.ReadAllText", "StreamReader"
                     })
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_correction_never_saves_the_document()
        {
            string source = CommandSource();
            Assert.DoesNotContain(".Save(", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".SaveAs(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SynchronizeWithCentral", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_batch_commits_inside_one_TransactionGroup_that_can_roll_back()
        {
            string source = CommandSource();
            Assert.Contains("new TransactionGroup(", source, StringComparison.Ordinal);
            Assert.Contains("Guard.RollBack(group)", source, StringComparison.Ordinal);
            Assert.Contains("Guard.Assimilate(group", source, StringComparison.Ordinal);

            // Exactly one group: two would be two atomic units wearing one promise.
            Assert.Single(Regex.Matches(source, @"new\s+TransactionGroup\s*\(").Cast<Match>());
        }

        [Fact]
        public void The_rehearsal_rolls_its_provisional_transaction_back_and_proves_it()
        {
            string source = CommandSource();
            Assert.Contains("rehearsal)", source, StringComparison.Ordinal);
            Assert.Contains("RollbackConfirmed", source, StringComparison.Ordinal);
            // A rehearsal whose rollback is not CONFIRMED must not hand back a token: the
            // provisional elements may still be in the model.
            Assert.Contains("!rehearsal.RollbackConfirmed", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_document_gate_and_the_still_the_same_recheck_both_run_before_the_write()
        {
            string source = CommandSource();
            Assert.Contains("DocumentGate.ForMutation(app, request, Name)", source, StringComparison.Ordinal);
            Assert.Contains("DocumentGate.RequireConfirmation(", source, StringComparison.Ordinal);
            Assert.Contains("DocumentGate.StillTheSame(", source, StringComparison.Ordinal);

            int stillTheSame = source.IndexOf("DocumentGate.StillTheSame(", StringComparison.Ordinal);
            int group = source.IndexOf("new TransactionGroup(", StringComparison.Ordinal);
            Assert.True(stillTheSame >= 0 && group > stillTheSame,
                "the active-document recheck must run BEFORE the committing group opens");
        }

        [Fact]
        public void The_confirmation_binds_the_resolved_plan_and_not_only_the_request()
        {
            string source = CommandSource();
            Assert.Contains("new ResolvedPlan", source, StringComparison.Ordinal);
            Assert.Contains("DocumentGate.RecordResolvedPlan(resolvedPlan)", source, StringComparison.Ordinal);
            // The plan is passed to the apply-time check, which is what turns a moved
            // model into stale_plan rather than a correction applied to something else.
            Assert.Contains("resolvedPlan, null)", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_plan_hash_covers_every_field_that_decides_what_is_written()
        {
            string source = CommandSource();
            Match m = Regex.Match(source, @"DocumentGate\.PlanHash\(request,(?<fields>[^;]*)\);",
                                  RegexOptions.Singleline);
            Assert.True(m.Success, "the command must compute a plan hash");
            string fields = m.Groups["fields"].Value;
            foreach (string field in new[]
                     { "units", "tolerance", "source_audit", "requirement_set", "actions" })
                Assert.True(fields.Contains("\"" + field + "\"", StringComparison.Ordinal),
                    "'" + field + "' changes what gets written and must be inside the plan hash, or a token " +
                    "issued for one request would be accepted for a different one");
        }

        [Fact]
        public void Resolution_is_decided_by_re_running_the_rules_not_by_the_postconditions()
        {
            string source = CommandSource();
            Assert.Contains("PlanimetryFixRules.Reconcile(", source, StringComparison.Ordinal);
            Assert.Contains("PlanimetryRules.EvaluateUniversal", source, StringComparison.Ordinal);
            // And when the re-audit itself fails, nothing may be declared resolved.
            Assert.Contains("NO finding is declared resolved", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Findings_are_recomputed_through_the_auditors_own_inventory_and_rules()
        {
            // Two collectors is how the auditor and the corrector start disagreeing about
            // what is on a sheet - the defect the read-only phase designed out. The
            // corrector therefore recomputes findings through PlanimetryInventory and
            // PlanimetryRules, exactly as horizun_audit_planimetry does.
            string source = CommandSource();
            Assert.Contains("PlanimetryInventory.Collect", source, StringComparison.Ordinal);
            Assert.Contains("PlanimetryRules.EvaluateUniversal", source, StringComparison.Ordinal);
            Assert.Contains("PlanimetryRules.EvaluateRequirementSet", source, StringComparison.Ordinal);
            Assert.Contains("AuditPlanimetryCommand.ParameterNames", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_only_direct_collectors_are_the_pre_write_uniqueness_and_count_checks()
        {
            // Direct collection IS legitimate here and the audit inventory cannot replace
            // it: "does another view already hold this name" and "how many title blocks
            // are on this sheet" are questions about the state INSIDE the transaction,
            // asked again after the write. What must never happen is a collector standing
            // in for the finding recomputation, so each one is pinned to its purpose.
            string source = CommandSource();
            var collectors = Regex.Matches(source, @"new FilteredElementCollector\((?<args>[^)]*)\)")
                                  .Cast<Match>().ToList();
            Assert.True(collectors.Count > 0, "the uniqueness checks collect directly");

            foreach (Match m in collectors)
            {
                int line = source.Take(m.Index).Count(c => c == '\n') + 1;
                string context = source.Substring(m.Index, Math.Min(220, source.Length - m.Index));
                bool purposeful =
                    context.Contains("typeof(View)", StringComparison.Ordinal) ||
                    context.Contains("typeof(ViewSheet)", StringComparison.Ordinal) ||
                    context.Contains("OST_TitleBlocks", StringComparison.Ordinal);
                Assert.True(purposeful,
                    "the collector at line " + line + " is neither a name-uniqueness check nor the " +
                    "title-block count. A collector that stands in for the audit recomputation would let " +
                    "the corrector and the auditor disagree about what a finding is.");
            }
        }

        [Fact]
        public void The_correction_hardcodes_no_corporate_standard()
        {
            string[] corporateTokens = { "Pro" + "desa", "HRZ" + "_", "PRD" + "_" };
            foreach (string source in new[] { CommandSource(), RulesSource() })
                foreach (string token in corporateTokens)
                    Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void No_scale_template_or_naming_pattern_is_compiled_in()
        {
            string rules = RulesSource();
            // A regex over names, or a list of "allowed" scales, would be a standard
            // welded into the bridge. The only numbers here are Revit's own limits and
            // the geometric tolerance.
            Assert.DoesNotContain("new Regex(", rules, StringComparison.Ordinal);
            Assert.DoesNotContain("1:50", rules, StringComparison.Ordinal);
            Assert.DoesNotContain("1:100", rules, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE THREE THAT COULD PASS WITHOUT MEASURING, pinned by name.
        ///
        /// Each of these shipped in the first draft of the verification code and each
        /// is the same substitution wearing a different hat: a before-value nobody
        /// could read, turned into agreement. They are asserted from source because
        /// the command needs a Revit to construct, and asserted INDIVIDUALLY because
        /// a general rule ("never record a literal true") would also flag the one
        /// legitimate case, where both sides really were just measured.
        /// </summary>
        [Theory]
        [InlineData("crop_visible_unchanged",
            "an unread CropBoxVisible was recorded as a MATCH, claiming the crop's visibility was left " +
            "alone while never having known what it was")]
        [InlineData("category_override_unchanged",
            "two unreadable category-override signatures rendered as the same sentence and compared EQUAL, " +
            "reporting 'it did not move' on the strength of having failed to read it twice")]
        public void An_unreadable_before_value_makes_its_postcondition_unmeasured(string property, string was)
        {
            string source = CommandSource();
            Assert.True(source.Contains("Unreadable(\"" + property + "\"", StringComparison.Ordinal),
                "'" + property + "' must have a path that records it as UNMEASURED. Previously " + was + ".");
        }

        [Fact]
        public void The_category_override_signature_reports_unreadable_as_null_not_as_a_sentence()
        {
            string source = CommandSource();
            int start = source.IndexOf("private static string CategoryOverrideSignature(", StringComparison.Ordinal);
            Assert.True(start > 0, "CategoryOverrideSignature must exist");
            int end = source.IndexOf("private static void PlanSetCrop(", start, StringComparison.Ordinal);
            string body = end > start ? source.Substring(start, end - start) : source.Substring(start);
            Assert.DoesNotContain("(unreadable: ", body, StringComparison.Ordinal);
            Assert.Contains("catch { return null; }", body, StringComparison.Ordinal);
        }

        [Fact]
        public void The_viewport_containment_check_declares_itself_before_the_checklist_is_built()
        {
            // A postcondition recorded only in its failing branch fails through
            // PostconditionCheck's "unexpected property" path - the right outcome by
            // accident, from a checklist that did not declare what it covers. The
            // decision must precede the construction.
            string source = CommandSource();
            int decide = source.IndexOf("bool containmentMeasurable", StringComparison.Ordinal);
            int build = source.IndexOf("? new PostconditionCheck(\"box_center\", \"inside_sheet_extent\")",
                                       StringComparison.Ordinal);
            Assert.True(decide > 0 && build > decide,
                "measurability must be decided before the checklist declares its required properties");
        }

        [Fact]
        public void The_command_is_registered_in_the_app()
        {
            string app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "App.cs"));
            Assert.Contains("new FixPlanimetryCommand()", app);
        }

        [Fact]
        public void Every_postcondition_the_operations_promise_is_actually_re_read()
        {
            // One verifier per operation, and the switch that dispatches to them names
            // every operation in the catalog. A missing arm would fall to the default,
            // which reports unreadable rather than verified - honest, but it would mean
            // an operation that can never succeed.
            string source = CommandSource();
            foreach (PlanimetryFixOperation op in PlanimetryFixRules.Catalog)
                Assert.True(source.Contains("case \"" + op.Name + "\": check =", StringComparison.Ordinal) ||
                            source.Contains("case \"" + op.Name + "\": check = Verify", StringComparison.Ordinal),
                    "operation '" + op.Name + "' has no post-commit verification arm");
        }

        [Fact]
        public void Every_operation_has_an_application_arm_as_well_as_a_verification_arm()
        {
            string source = CommandSource();
            int apply = source.IndexOf("private static void Apply(Document doc, Plan plan)", StringComparison.Ordinal);
            Assert.True(apply > 0, "the shared apply method must exist");
            string applyBody = source.Substring(apply);
            foreach (PlanimetryFixOperation op in PlanimetryFixRules.Catalog)
                Assert.True(applyBody.Contains("case \"" + op.Name + "\":", StringComparison.Ordinal),
                    "operation '" + op.Name + "' has no application arm");
        }

        private static void WithSettings(string json, Action action)
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-fixplan-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp);
                    File.WriteAllText(HorizunPaths.SettingsPath(), json);
                    action();
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }
    }
}
