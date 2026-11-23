using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Review B1 (int/4.2): the plan hash of ModelEditRunner left out arguments that change the result, so a token
    /// rehearsed for one plan applied another. The guardian: every argument of a command that goes through the runner
    /// must be in the hash (PlanScopeFields) or in the explicit presentation list.
    ///
    /// The callers of ModelEditRunner.Run are DISCOVERED in the source (review m-1), not listed here. Each one found must be
    /// classified in <see cref="Classified"/>, and the classification says where its arguments come from:
    ///   Full    - the whole schema of the tool goes through the runner: every top-level property is checked.
    ///   Partial - only some operations of the tool do: the arguments are the quoted names in the validator's own source that
    ///             are also top-level properties of the tool's schema.
    /// A caller that is not in the table fails the guardian until somebody classifies it. Nested properties live inside the
    /// value of a top-level property and the whole value is hashed, so the top level is where a hole can be.
    ///
    /// THE IDENTITY COUNTS AS HASH (integration of R2R). The identity fields (identity, unique_id, unique_ids, expected_*) are
    /// not in PlanScopeFields and not in the schema of these commands: the dispatcher's IdentityPrepass verifies and ERASES
    /// them before the command parses anything. They are bound to the token another way: Confirmation.IdentityDigest takes a
    /// digest of the identity before the erasure and ConfirmationStore.PlanHash includes it, so an apply that omits or changes
    /// the identity of its rehearsal is PlanChanged. PresentationFields lists the bare names only so that a hand-built request
    /// that reaches a command with them is not flagged as unhashed.
    /// The test No_command_of_the_runner_is_reachable_without_the_identity_prepass checks that the dispatcher has no way
    /// to run a command that skips the prepass.
    /// </summary>
    public class ModelEditPlanHashGuardTests
    {
        internal sealed class Caller
        {
            public string Tool;
            public string ValidatorFile;    // null: the whole schema (Full)
            public string ValidatorStart;   // text where the validator method starts
            public string ValidatorEnd;     // text where it ends
        }

        private static Caller Full(string tool) => new Caller { Tool = tool };

        internal static readonly Dictionary<string, Caller> Classified = new Dictionary<string, Caller>(StringComparer.Ordinal)
        {
            ["CreateRailingCommand.cs"] = Full("horizun_create_railing"),
            ["JoinGeometryCommand.cs"] = Full("horizun_join_geometry"),
            ["ManageCurtainCommand.cs"] = Full("horizun_manage_curtain"),
            ["PlanimetryOpsCommand.cs"] = Full("horizun_planimetry_ops"),
            ["RoomFinishesCommand.cs"] = Full("horizun_room_finishes"),
            ["SheetLayoutCommand.cs"] = Full("horizun_sheet_layout"),
            ["SlabShapeCommand.cs"] = Full("horizun_slab_shape"),
            ["ViewCropCommand.cs"] = Full("horizun_view_crop"),
            ["ViewVisibilityCommand.Entry.cs"] = Full("horizun_view_visibility"),
            ["WallFinishCommand.cs"] = Full("horizun_wall_finish"),
            ["WallSweepCommand.cs"] = Full("horizun_wall_sweep"),
            // AnnotateCommand routes a load_tag_family request here; the rest of annotate does not use the runner.
            ["LoadTagFamilyEdit.cs"] = new Caller
            {
                Tool = "horizun_annotate", ValidatorFile = "LoadTagFamilyEdit.cs",
                ValidatorStart = "Allowed = new HashSet<string>", ValidatorEnd = "public static CommandResult Run"
            },
            // copy_to_levels / check_variation of manage_groups; its other operations write without the runner.
            ["ManageGroupsCommand.CopyLevels.cs"] = new Caller
            {
                Tool = "horizun_manage_groups", ValidatorFile = "GroupLevelRules.cs",
                ValidatorStart = "public static string ValidateCopyOrVariation", ValidatorEnd = "private static bool Finite"
            },
        };

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static bool IsBuildOutput(string path)
        {
            string rel = path.Replace('\\', '/');
            return rel.Contains("/obj/") || rel.Contains("/bin/");
        }

        /// <summary>File names under root that call ModelEditRunner.Run, outside comments and outside the runner itself.</summary>
        internal static List<string> DiscoverCallers(string root)
        {
            var found = new List<string>();
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                string name = Path.GetFileName(file);
                if (name == "ModelEditRunner.cs") continue;
                foreach (string line in File.ReadLines(file))
                {
                    string t = line.TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
                    if (line.Contains("ModelEditRunner.Run(")) { found.Add(name); break; }
                }
            }
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        private static string Between(string text, string start, string end)
        {
            int i = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(i >= 0, "Validator marker not found: " + start);
            int j = text.IndexOf(end, i, StringComparison.Ordinal);
            Assert.True(j > i, "Validator end marker not found: " + end);
            return text.Substring(i, j - i);
        }

        private static List<string> SchemaProps(string tool)
            => ((JObject)Contract.All.Single(c => c.Name == tool).InputSchema["properties"]).Properties().Select(x => x.Name).ToList();

        /// <summary>The arguments a classified caller takes through the runner.</summary>
        internal static List<string> ArgumentsOf(string root, Caller caller)
        {
            var schemaProps = SchemaProps(caller.Tool);
            if (caller.ValidatorFile == null) return schemaProps;
            string source = Directory.EnumerateFiles(root, caller.ValidatorFile, SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(f)).Select(File.ReadAllText).Single();
            string body = Between(source, caller.ValidatorStart, caller.ValidatorEnd);
            var quoted = new HashSet<string>(
                Regex.Matches(body, "\"([a-z][a-z0-9_]*)\"").Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.Ordinal);
            return schemaProps.Where(quoted.Contains).ToList();
        }

        /// <summary>Everything wrong under root: callers nobody classified, rows that no longer call, arguments in neither list.</summary>
        internal static List<string> Problems(string root, IDictionary<string, Caller> table)
        {
            var problems = new List<string>();
            var callers = DiscoverCallers(root);
            foreach (string file in callers)
                if (!table.ContainsKey(file))
                    problems.Add("UNCLASSIFIED CALLER " + file + ": it calls ModelEditRunner.Run, so its arguments must be in " +
                                 "PlanScopeFields or PresentationFields; classify it in ModelEditPlanHashGuardTests.Classified.");
            foreach (string file in table.Keys)
                if (!callers.Contains(file))
                    problems.Add("STALE ROW " + file + ": it no longer calls ModelEditRunner.Run.");
            var covered = new HashSet<string>(ModelEditRunner.PlanScopeFields.Concat(ModelEditRunner.PresentationFields), StringComparer.Ordinal);
            foreach (var row in table)
            {
                if (!callers.Contains(row.Key)) continue;
                foreach (string arg in ArgumentsOf(root, row.Value))
                    if (!covered.Contains(arg))
                        problems.Add(row.Key + " (" + row.Value.Tool + ")." + arg + " is in neither the plan hash nor the presentation list");
            }
            return problems;
        }

        [Fact]
        public void Changing_schedule_source_changes_the_confirmation_hash()
        {
            var first = new JObject { ["operation"] = "note_from_model", ["source"] = "schedule", ["schedule_id"] = 101 };
            var second = (JObject)first.DeepClone();
            second["schedule_id"] = 102;
            Assert.NotEqual(ConfirmationStore.PlanHash(first, ModelEditRunner.PlanScopeFields),
                            ConfirmationStore.PlanHash(second, ModelEditRunner.PlanScopeFields));
        }
        [Fact]
        public void Every_caller_of_the_runner_is_classified_and_every_argument_is_in_the_plan_hash_or_the_presentation_list()
        {
            var problems = Problems(Path.Combine(RepoRoot(), "src", "Horizun.Revit"), Classified);
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void The_two_callers_the_fixed_list_missed_are_found_and_read()
        {
            string root = Path.Combine(RepoRoot(), "src", "Horizun.Revit");
            var callers = DiscoverCallers(root);
            Assert.Contains("LoadTagFamilyEdit.cs", callers);
            Assert.Contains("ManageGroupsCommand.CopyLevels.cs", callers);
            var groups = ArgumentsOf(root, Classified["ManageGroupsCommand.CopyLevels.cs"]);
            foreach (string a in new[] { "group_ids", "level_ids", "tolerance_mm", "relative_rotations_degrees", "operation", "target_document" })
                Assert.Contains(a, groups);
            Assert.Contains("load_tag_family", ArgumentsOf(root, Classified["LoadTagFamilyEdit.cs"]));
        }

        [Fact]
        public void A_new_caller_that_nobody_classified_turns_the_guardian_red()
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-guard-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                // The real tree is copied, so the only difference is the simulated caller.
                foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Horizun.Revit"), "*.cs", SearchOption.AllDirectories))
                {
                    if (IsBuildOutput(file)) continue;
                    string target = Path.Combine(root, Path.GetFileName(file));
                    if (!File.Exists(target)) File.Copy(file, target);
                }
                Assert.Empty(Problems(root, Classified));

                File.WriteAllText(Path.Combine(root, "SimulatedNewCommand.cs"),
                    "class SimulatedNewCommand { void X() { ModelEditRunner.Run(app, paramsJson, Name, \"x\", V, P); } }");
                Assert.Contains(Problems(root, Classified), p => p.StartsWith("UNCLASSIFIED CALLER SimulatedNewCommand.cs", StringComparison.Ordinal));

                // A call in a comment is not a caller.
                File.WriteAllText(Path.Combine(root, "SimulatedNewCommand.cs"), "// ModelEditRunner.Run(app, p, Name);\n/// ModelEditRunner.Run(\n");
                Assert.Empty(Problems(root, Classified));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void A_partial_caller_whose_validator_accepts_an_unhashed_schema_property_turns_the_guardian_red()
        {
            // Pick a property of a real schema that nobody hashed, put its name in a simulated validator and classify the
            // simulated caller as Partial: the guardian must name it.
            var covered = new HashSet<string>(ModelEditRunner.PlanScopeFields.Concat(ModelEditRunner.PresentationFields), StringComparer.Ordinal);
            string unhashed = SchemaProps("horizun_export").First(p => !covered.Contains(p));

            string root = Path.Combine(Path.GetTempPath(), "hz-guard-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "Forged.cs"),
                    "public static string V(JObject r) { string[] common = { \"" + unhashed + "\" }; return null; }\nprivate static bool Finite");
                File.WriteAllText(Path.Combine(root, "Caller.cs"), "class C { void X() { ModelEditRunner.Run(a, p, N, \"t\", V, P); } }");
                var row = new Caller { Tool = "horizun_export", ValidatorFile = "Forged.cs", ValidatorStart = "public static string V", ValidatorEnd = "private static bool Finite" };
                var problems = Problems(root, new Dictionary<string, Caller> { ["Caller.cs"] = row });
                Assert.Contains(problems, p => p.Contains("Caller.cs (horizun_export)." + unhashed + " is in neither"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void No_command_of_the_runner_is_reachable_without_the_identity_prepass()
        {
            // Every classified caller is a command, and a command runs only through the dispatcher, whose three execution sites
            // (synchronous, queued job, plan step of a job) all pass through IdentityPrepass.Execute. The composite commands
            // (execute_plan, apply_corrections, cad_*...) call their children with arguments they build themselves after the
            // prepass consumed the caller's identity (a plan's children are normalized by the plan-wide walk). A validator that
            // saw an identity because a path skipped the prepass would REFUSE it (entrada_invalida), never ignore it.
            string dispatcher = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "Dispatcher.cs"));
            var lines = dispatcher.Split('\n');
            int viaPrepass = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (lines[i].Contains("LargeModelDispatch.Execute"))
                {
                    Assert.True(lines[i].Contains("IdentityPrepass.Execute("),
                        "Dispatcher.cs line " + (i + 1) + " runs a command through LargeModelDispatch without IdentityPrepass: " + t);
                    viaPrepass++;
                }
                // The only direct call of a command is the submit of a job (it queues work, which later runs through the prepass).
                bool direct = Regex.IsMatch(lines[i], @"\b(cmd|command)\.Execute\(") ||
                              (lines[i].Contains("_commands[") && lines[i].Contains(".Execute(") && !lines[i].Contains("_commands[name].Execute(null, paramsJson)"));
                Assert.False(direct, "Dispatcher.cs line " + (i + 1) + " runs a command directly, skipping IdentityPrepass: " + t);
            }
            Assert.Equal(3, viaPrepass);
            Assert.Contains("SubmitJobWithoutWaitingForUi", dispatcher);
        }

        [Fact]
        public void The_presentation_list_holds_nothing_that_changes_the_plan()
        {
            // A field cannot be hashed and presentation at once.
            Assert.Empty(ModelEditRunner.PlanScopeFields.Intersect(ModelEditRunner.PresentationFields));
        }

        [Theory]
        [InlineData("family_names", "[\"Basic Wall\"]", "[\"Curtain Wall\"]")]
        [InlineData("type_names", "[\"Generic - 200mm\"]", "[\"Generic - 300mm\"]")]
        [InlineData("columns", "2", "3")]
        [InlineData("gap_x", "10", "20")]
        [InlineData("gap_y", "10", "20")]
        [InlineData("title_gap", "5", "8")]
        [InlineData("keep_viewport_center", "true", "false")]
        [InlineData("items", "[{\"view_id\":1,\"name\":\"A\"}]", "[{\"view_id\":1,\"name\":\"B\"}]")]
        [InlineData("require_title_fit", "true", "false")]
        [InlineData("line_length", "100", "150")]
        public void Changing_a_b1_argument_changes_the_hash(string field, string a, string b)
        {
            string Hash(string v) => ConfirmationStore.PlanHash(JObject.Parse("{\"operation\":\"x\",\"" + field + "\":" + v + "}"), ModelEditRunner.PlanScopeFields);
            Assert.NotEqual(Hash(a), Hash(b));
        }
    }
}
