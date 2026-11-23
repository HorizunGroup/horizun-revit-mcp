using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Contracts;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Core.Tests
{
    public class ReliabilityR3Tests
    {
        private readonly ITestOutputHelper output;

        public ReliabilityR3Tests(ITestOutputHelper output) => this.output = output;

        private static JObject Source(string path) => new JObject { ["path"] = path, ["sha256"] = new string('A', 64) };

        private static void Apply(JObject request, JObject rehearsal)
        {
            request["dry_run"] = false;
            request["idempotency_key"] = "r3-apply";
            request["confirmation_token"] = rehearsal.Value<string>("confirmation_token");
        }

        [Fact]
        public void Partial_batch_approves_only_verified_rows_and_keeps_assumptions_at_the_response_root()
        {
            var request = JObject.Parse(@"{
                'operation':'batch','models':[
                  {'file_path':'D:/central.rvt','detach':false},
                  {'file_path':'D:/standalone.rvt','close':'save'},
                  {'file_path':'D:/local.rvt'}
                ]}");
            var store = new ConfirmationStore();
            var applied = new List<string>();
            bool firstIsCollaborative = true;
            CommandResult Run() => DocumentBatchRun.Run(request, "2025", store, Source, () => false,
                (model, row, dry, checkActions, save) =>
                {
                    string path = model.Value<string>("file_path");
                    bool workshared = path.EndsWith("local.rvt") || (firstIsCollaborative && path.EndsWith("central.rvt"));
                    ReliabilityDefaults.StampBatchDetach(row, model, workshared);
                    string refusal = BatchSessionRules.DetachDecision(workshared, model.Value<bool?>("detach"), out _);
                    if (refusal != null) throw new InvalidOperationException(refusal);
                    if (!dry)
                    {
                        applied.Add(path);
                        if (model.Value<string>("close") == "save")
                            row["save"] = new JObject { ["data"] = (JObject)save(() => CommandResult.Ok(
                                new JObject { ["source_saved"] = true })).Data };
                    }
                    row["success"] = true;
                });

            var rehearsal = Run();
            Assert.False(rehearsal.Success);
            Assert.Equal(2, rehearsal.Detail.Value<int>("successful_models"));
            Assert.Contains("detach=false", rehearsal.Detail["models"][0].Value<string>("error"));
            Assert.NotNull(rehearsal.Detail.Value<string>("confirmation_token"));
            ReliabilityR2Tests.Canonical(rehearsal.Detail, 6);
            ReliabilityR2Tests.Canonical((JObject)rehearsal.Detail["models"][2], 3);
            Assert.Empty(applied);

            Apply(request, rehearsal.Detail);
            firstIsCollaborative = false;  // Even a row that could now pass is outside this token.
            var result = Run();
            Assert.False(result.Success);
            Assert.Equal(new[] { "D:/standalone.rvt", "D:/local.rvt" }, applied);
            Assert.True(result.Detail.Value<bool>("source_files_saved"));
            Assert.Equal(1, result.Detail.Value<int>("failed_models"));
            Assert.Equal("not_approved_in_rehearsal", result.Detail["models"][0].Value<string>("skipped"));
            Assert.Null(result.Detail["confirmation_token"]);
            Assert.Contains("already used", Run().Error);
        }

        [Theory]
        [InlineData("rehearsal")]
        [InlineData("opening")]
        [InlineData("saving")]
        [InlineData("changed")]
        public void Source_save_requires_the_same_readable_hash_before_open_and_save(string failureAt)
        {
            var request = JObject.Parse(@"{
                'operation':'batch','models':[{'file_path':'D:/source.rvt','close':'save'}]}");
            int sourceReads = 0;
            int saves = 0;
            int opens = 0;
            var store = new ConfirmationStore();
            JObject Fingerprint(string path)
            {
                sourceReads++;
                if (failureAt == "rehearsal" || (failureAt == "opening" && sourceReads == 2) ||
                    (failureAt == "saving" && sourceReads == 3))
                    return new JObject { ["path"] = path, ["error"] = "IOException" };
                var source = Source(path);
                if (failureAt == "changed" && sourceReads == 3) source["sha256"] = new string('B', 64);
                return source;
            }

            CommandResult Run() => DocumentBatchRun.Run(request, "2025", store, Fingerprint, () => false,
                (model, row, dry, _, save) =>
                {
                    opens++;
                    if (!dry) save(() => { saves++; return CommandResult.Ok(new JObject()); });
                    row["success"] = true;
                });
            var rehearsal = Run();
            if (failureAt == "rehearsal")
            {
                Assert.False(rehearsal.Success);
                Assert.Null(rehearsal.Detail["confirmation_token"]);
                Assert.Contains("SHA-256", rehearsal.Detail["models"][0].Value<string>("error"));
                Assert.Equal(0, opens);
            }
            else
            {
                ReliabilityR2Tests.Canonical((JObject)rehearsal.Data, 1);
                Apply(request, (JObject)rehearsal.Data);
                var result = Run();
                Assert.False(result.Success);
                Assert.Contains(failureAt == "changed" ? "Source changed" : "SHA-256", result.Detail["models"][0].Value<string>("error"));
                Assert.Equal(failureAt == "opening" ? 1 : 2, opens);
            }
            Assert.Equal(0, saves);
        }

        [Theory]
        [InlineData("unchanged")]
        [InlineData("csv_moved")]
        [InlineData("csv_same_placement")]
        [InlineData("spf_unchanged")]
        [InlineData("spf_type_changed")]
        [InlineData("missing_plan")]
        public void A_batch_binds_the_real_action_plan_even_when_the_model_file_is_unchanged(string scenario)
        {
            string csv = Path.GetTempFileName();
            try
            {
                File.WriteAllText(csv, "x,y\n1000,2000\n");
                bool bind = scenario.StartsWith("spf_", StringComparison.Ordinal);
                bool shouldApply = scenario == "unchanged" || scenario == "spf_unchanged";
                const string modelPath = "D:/source-model.rvt";
                string tool = bind ? "horizun_bind_shared_param" : "horizun_create_elements";
                const string length = "autodesk.spec.aec:length-2.0.0";
                string typeReadFromRevit = length;
                var guid = Guid.Parse("b07d774c-fc84-4c99-a581-cb92e6625210");
                var request = JObject.Parse(@"{
                    'operation':'batch','models':[
                      {'file_path':'D:/source-model.rvt','close':'save','actions':[
                        {'key':'action','arguments':{}}]},
                      {'file_path':'D:/independent.rvt','close':'save'}]}");
                request["models"][0]["actions"][0]["tool"] = tool;
                request["models"][0]["actions"][0]["arguments"][bind ? "spf_path" : "tabular_source"] = bind
                    ? (JToken)"D:/parameters.txt" : new JObject { ["path"] = csv };

                var store = new ConfirmationStore();
                var applied = new List<string>();
                var saved = new List<string>();
                CommandResult Run() => DocumentBatchRun.Run(request, "2025", store, Source, () => false,
                    (model, row, dry, checkActions, save) =>
                    {
                        if (model["actions"] != null)
                        {
                            JArray placements = null;
                            var child = bind
                                ? SharedParamBindingRules.ResolvePlan(tool, modelPath, "2025", "doc", guid,
                                    "Shared value", typeReadFromRevit, false, true, null, new long[] { -2000011 }, "Type", "PG_DATA")
                                : ReadCreationPlan(csv, out placements);
                            row["steps_rehearsal"] = scenario == "missing_plan" ? new JObject { ["data"] = new JObject() } :
                                new JObject { ["data"] = new JObject { ["plan_resolved"] = RehearseGraph(child).Graph.DescribeResolution() } };
                            checkActions();
                            if (!dry) applied.Add(bind ? typeReadFromRevit : placements[0]["point"].ToString(Newtonsoft.Json.Formatting.None));
                        }
                        if (!dry) save(() =>
                        {
                            saved.Add(model.Value<string>("file_path"));
                            return CommandResult.Ok(new JObject());
                        });
                        row["success"] = true;
                    });

                var rehearsal = Run();
                var preview = rehearsal.Success ? (JObject)rehearsal.Data : rehearsal.Detail;
                Assert.Equal(scenario != "missing_plan", rehearsal.Success);
                Assert.NotNull(preview["confirmation_token"]);
                Assert.Empty(applied);
                Assert.Empty(saved);
                Apply(request, preview);

                if (scenario == "csv_moved") File.WriteAllText(csv, "x,y\n9000,2000\n");
                if (scenario == "csv_same_placement") File.WriteAllText(csv, "x,y\n1000.0,2000\n");
                if (scenario == "spf_type_changed") typeReadFromRevit = "autodesk.spec.aec:area-2.0.0";

                var result = Run();
                Assert.Equal(shouldApply, result.Success);
                if (shouldApply)
                {
                    Assert.Equal(bind ? length : "[1000.0,2000.0,0.0]", Assert.Single(applied));
                    Assert.Equal(new[] { modelPath, "D:/independent.rvt" }, saved);
                    return;
                }

                Assert.Empty(applied);
                Assert.Equal("D:/independent.rvt", Assert.Single(saved));
                var row = result.Detail["models"][0];
                if (scenario == "missing_plan") Assert.Equal("not_approved_in_rehearsal", row.Value<string>("skipped"));
                else Assert.Equal("stale_plan", row.Value<string>("state"));
            }
            finally { File.Delete(csv); }
        }

        [Theory]
        [InlineData("1000,2000", true)]
        [InlineData("9000,2000", false)]
        [InlineData("1000.0,2000", false)]
        public void Creation_checks_the_csv_snapshot_after_the_atomic_graph_recheck(string csvRow, bool shouldApply)
        {
            string csv = Path.GetTempFileName();
            try
            {
                File.WriteAllText(csv, "x,y\n1000,2000\n");
                var doc = new Document();
                doc.Elements.Add(10, new FamilySymbol { Id = new ElementId(10) });
                doc.Elements.Add(11, new Level { Id = new ElementId(11) });
                var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
                var request = new JObject
                {
                    ["tabular_source"] = new JObject { ["path"] = csv, ["type_id"] = 10, ["level_id"] = 11 }
                };

                var result = ApplyAfterGraphRecheck(new CreateElementsCommand(), app, request,
                    () => File.WriteAllText(csv, "x,y\n" + csvRow + "\n"));

                Assert.Equal(shouldApply, result.Success);
                Assert.Equal(shouldApply ? 1 : 0, doc.TransactionStarts);
                if (shouldApply)
                    Assert.True(JToken.DeepEquals(new JArray(1000.0, 2000.0, 0.0), Assert.Single(doc.CreatedPlacements)));
                else
                {
                    AssertStaleBeforeWrite(result);
                    Assert.Null(doc.CreatedPlacements);
                }
            }
            finally { File.Delete(csv); }
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, true, true)]
        public void Binding_checks_the_selected_definition_after_the_atomic_graph_recheck(
            bool alreadyBound, bool changeFileType, bool shouldApply)
        {
            const string length = "autodesk.spec.aec:length-2.0.0";
            const string area = "autodesk.spec.aec:area-2.0.0";
            var guid = Guid.Parse("b07d774c-fc84-4c99-a581-cb92e6625210");
            string spf = Path.GetTempFileName();
            try
            {
                File.WriteAllText(spf, length);
                var doc = new Document();
                var category = new Category
                {
                    Id = new ElementId((long)BuiltInCategory.OST_Walls)
                };
                doc.Settings.Categories.Add(category);

                var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
                app.Application.ReadFixtureSharedParameters = path =>
                {
                    var file = new DefinitionFile();
                    var group = new DefinitionGroup();
                    group.Definitions.Add(new ExternalDefinition
                    {
                        GUID = guid,
                        Name = "Shared value",
                        DataType = File.ReadAllText(path)
                    });
                    file.Groups.Add(group);
                    return file;
                };
                var request = new JObject
                {
                    ["spf_path"] = spf,
                    ["param_guid"] = guid.ToString("D"),
                    ["categories"] = new JArray("OST_Walls"),
                    ["binding_kind"] = "Type"
                };
                var command = new BindSharedParamCommand();
                if (alreadyBound)
                {
                    // Set up a real binding through the command before rehearsing ReInsert.
                    var initial = ApplyAfterGraphRecheck(command, app, request, () => { });
                    Assert.True(initial.Success, initial.Error);
                    Assert.Equal("confirmed", ((JObject)initial.Data).Value<string>("outcome"));
                }

                var result = ApplyAfterGraphRecheck(command, app, request,
                    () => { if (changeFileType) File.WriteAllText(spf, area); });

                Assert.Equal(shouldApply, result.Success);
                Assert.Equal((alreadyBound ? 1 : 0) + (shouldApply ? 1 : 0), doc.TransactionStarts);
                Assert.Equal(shouldApply ? 1 : 0, doc.ParameterBindings.Inserts);
                Assert.Equal(alreadyBound ? 1 : 0, doc.ParameterBindings.Reinserts);
                if (shouldApply)
                {
                    var data = (JObject)result.Data;
                    Assert.Equal("confirmed", data.Value<string>("outcome"));
                    Assert.Equal(length, data["resulting_data_type"].Value<string>("actual"));
                    Assert.True(data["resulting_data_type"].Value<bool>("matches"));
                }
                else
                {
                    AssertStaleBeforeWrite(result);
                    Assert.Empty(doc.ParameterBindings.Definitions);
                }
            }
            finally { File.Delete(spf); }
        }

        private static CommandResult ApplyAfterGraphRecheck(ICommand command, UIApplication app, JObject request, Action changeSource)
        {
            int transactionsBeforeRehearsal = app.ActiveUIDocument.Document.TransactionStarts;
            var approved = Rehearse();
            var rechecked = Rehearse();
            Assert.Equal(approved.Graph.Fingerprint(), rechecked.Graph.Fingerprint());
            Assert.NotNull(rechecked.ChildFingerprint);
            Assert.Equal(transactionsBeforeRehearsal, app.ActiveUIDocument.Document.TransactionStarts);

            var applyRequest = (JObject)request.DeepClone();
            applyRequest["dry_run"] = false;
            applyRequest["__expected_plan_fingerprint"] = rechecked.ChildFingerprint;
            changeSource();
            // The host double supplies the confirmed atomic scope: no child token
            // is required, so only the child's late comparison can refuse drift.
            return command.Execute(app, applyRequest.ToString());

            (ResolvedPlan Graph, string ChildFingerprint) Rehearse()
            {
                var result = command.Execute(app, request.ToString());
                Assert.True(result.Success, result.Error);
                return RehearseGraph(command.Name, (JObject)result.Data, "fake", app.Application.VersionNumber);
            }
        }

        private static void AssertStaleBeforeWrite(CommandResult result)
        {
            Assert.Equal("stale_plan", result.Detail.Value<string>("state"));
            Assert.True(JToken.DeepEquals(new JValue(false), result.Detail["write_started"]));
            Assert.True(JToken.DeepEquals(new JValue(false), result.Detail["changes_applied"]));
        }

        private static ResolvedPlan ReadCreationPlan(string csv, out JArray placements)
        {
            Assert.Null(CreateElementsCommand.ReadPlacementCsv(csv, new JObject(), ".", "internal", 10, 11, 1,
                () => 0, () => throw new InvalidOperationException("No project transform needed"), out placements, out var provenance));
            var plan = CreateElementsCommand.ResolveCreationPlan("horizun_create_elements", "D:/source-model.rvt", "2025", "doc", provenance);
            // Revit resolves the model references. Keep them fixed so this covers the source bytes alone.
            plan.Elements.Add(new PlannedElement { UniqueId = "create:0", Category = "family_instance", Action = PlannedAction.Create });
            return plan;
        }

        private static (ResolvedPlan Graph, string ChildFingerprint) RehearseGraph(ResolvedPlan child)
        {
            var preview = new JObject { ["plan_resolved"] = child.DescribeResolution() };
            ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
            return RehearseGraph(child.Command, preview, child.DocumentKey, child.RevitVersion, child.DocumentFingerprint);
        }

        private static (ResolvedPlan Graph, string ChildFingerprint) RehearseGraph(string tool, JObject preview,
            string documentKey, string revitVersion, string documentFingerprint = null)
        {
            var ledger = new PlanLedger();
            var row = ledger.RecordRehearsal(0, "action", tool, true, preview, null);
            Assert.True(ledger.RehearsedCleanly);
            return (PlanLedger.ResolveGraph(new JArray(row), documentKey, revitVersion, documentFingerprint),
                PlanLedger.ReadChildFingerprint(row));
        }

        [Theory]
        [InlineData("verified")]
        [InlineData("write")]
        [InlineData("final_hash")]
        public void Mesh_operation_reports_canonical_assumptions_through_write_and_reread(string outcome)
        {
            string directory = Path.Combine(Path.GetTempPath(), "hz-mesh-r3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "mesh.json");
                var request = new JObject { ["format"] = "mesh_json", ["output_path"] = path };
                var options = (JObject)request.DeepClone();
                var store = new ConfirmationStore();
                var bytes = Encoding.UTF8.GetBytes("{\"triangles\":[0,0,0,1,0,0,0,1,0]}");
                string Payload(Stream destination, out JObject summary)
                {
                    summary = new JObject { ["triangles"] = 1 };
                    if (outcome == "write" && destination != Stream.Null) throw new IOException("stream blocked");
                    destination.Write(bytes, 0, bytes.Length);
                    return Convert.ToHexString(SHA256.HashData(bytes));
                }
                string ReadHash(string p) => outcome == "final_hash" && p == path ? "unmatched" : ExportDeliveryRules.MeshHash(p);
                CommandResult Run() => MeshExportRun.Run(request, path, options, "model", store, Payload, ReadHash);
                var rehearsal = Run();
                ReliabilityR2Tests.Canonical((JObject)rehearsal.Data, 3);
                Assert.False(File.Exists(path));
                Apply(request, (JObject)rehearsal.Data);

                var result = Run();
                Assert.Equal(outcome == "verified", result.Success);
                var response = result.Success ? (JObject)result.Data : result.Detail;
                ReliabilityR2Tests.Canonical(response, 3);
                if (!result.Success)
                {
                    Assert.Contains("SUPUESTO", result.Error);
                    Assert.Contains(outcome == "write" ? "stream blocked" : "final hash differs", result.Error);
                }
                Assert.Equal(outcome != "write", File.Exists(path));
                Assert.Equal(outcome == "verified", response.Value<bool?>("file_verified") ?? false);
            }
            finally
            {
                foreach (string p in Directory.GetFiles(directory)) File.Delete(p);
                Directory.Delete(directory);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Set_curve_entry_point_preserves_result_and_stamps_assumptions(bool succeeds)
        {
            var command = new TransformElementsCommand
            {
                RunTransform = request =>
                {
                    Assert.Equal("SET_CURVE", request["operations"][1].Value<string>("operation"));
                    var evidence = new JObject { ["write_started"] = !succeeds };
                    return succeeds ? CommandResult.Ok(evidence) :
                        CommandResult.FailCoded(ErrorCodes.VerificacionFallida, "Curve mismatch", extra: evidence);
                }
            };
            var result = command.Execute(null, "{'operations':[null,{'operation':'SET_CURVE'}]}");
            Assert.Equal(succeeds, result.Success);
            var response = succeeds ? (JObject)result.Data : result.Detail;
            ReliabilityR2Tests.Canonical(response, 1);
            Assert.Equal("operations[1].release_end_joins=false", (string)response["assumptions"][0]["what"]);
            if (!succeeds)
            {
                Assert.Contains("SUPUESTO", result.Error);
                Assert.Equal(ErrorCodes.VerificacionFallida, result.Detail.Value<string>("error_code"));
            }
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Mesh_traversal_keeps_exclusive_child_geometry_and_removes_either_diagonal(bool defaults, bool explicitlySelected)
        {
            double[] a = { 0, 0, 0 }, b = { 1, 0, 0 }, c = { 1, 1, 0 }, d = { 0, 1, 0 };
            var parent = new[] { new[] { a, b, c }, new[] { a, c, d } };
            var alternate = new[] { new[] { a, d, b }, new[] { b, c, d } };
            var exclusive = parent[0].Select(p => new[] { p[0], p[1], 1d }).ToArray();
            var geometry = new[] { parent, alternate.Concat(new[] { exclusive, exclusive }).ToArray(), alternate };
            var direct = explicitlySelected ? new[] { 2, 3, 1 } : new[] { 3, 1 };
            var emitted = new Dictionary<int, double[][][]>();
            new MeshHierarchyFaces().Collect(direct, id => id, id => id == 1, _ => new[] { 2 }, defaults,
                id => geometry[id - 1], (id, faces) => emitted.Add(id, faces.ToArray()));

            Assert.Equal(parent, emitted[1]);
            Assert.Same(exclusive, Assert.Single(emitted[2]));
            Assert.Equal(alternate, emitted[3]);
        }

        [Fact]
        public void Mesh_subtracts_only_coplanar_overlap_and_retains_the_winding_and_area_of_the_remainder()
        {
            double[] a = { 0, 0, 0 }, b = { 2, 0, 0 }, c = { 0, 2, 0 }, d = { 1, 1, 0 };
            var parent = new[] { a, b, d };
            var child = new[] { a, b, c };
            var offset = child.Select(p => new[] { p[0], p[1], 0.001 }).ToArray();
            var emitted = new Dictionary<int, double[][][]>();
            new MeshHierarchyFaces().Collect(new[] { 1, 2 }, id => id, id => id == 1, _ => new[] { 2 }, false,
                id => id == 1 ? new[] { parent } : new[] { child, offset }, (id, faces) => emitted.Add(id, faces.ToArray()));

            Assert.Same(offset, Assert.Single(emitted[2], t => t[0][2] > 0));
            var remainder = Assert.Single(emitted[2], t => t[0][2] == 0);
            Assert.Equal(1d, ((remainder[1][0] - remainder[0][0]) * (remainder[2][1] - remainder[0][1]) -
                             (remainder[1][1] - remainder[0][1]) * (remainder[2][0] - remainder[0][0])) / 2, 9);
        }

        [Fact]
        public void Mesh_checks_a_local_neighborhood_for_each_of_ten_thousand_unmatched_faces()
        {
            const int count = 10_000;
            var parent = new MeshSurface();
            double[][] Face(int i, double shift)
            {
                double x = 3 * (i % 100 - 50) + shift;
                double z = 3 * (i / 100 - 50);
                return new[] { new[] { x, 0d, z }, new[] { x + 1, 0d, z }, new[] { x, 1d, z } };
            }
            for (int i = 0; i < count; i++) parent.Add(Face(i, 0));
            var parents = new List<MeshSurface> { parent };
            var metrics = new MeshSurface.QueryMetrics();
            double area = 0;
            for (int i = 0; i < count; i++)
                foreach (var t in MeshSurface.Uncovered(Face(i, 0.75), parents, metrics))
                    area += ((t[1][0] - t[0][0]) * (t[2][1] - t[0][1]) - (t[1][1] - t[0][1]) * (t[2][0] - t[0][0])) / 2;

            Assert.Equal(count * 0.46875, area, 7);
            output.WriteLine("Bounds checks for {0} parent/child faces per side: {1}", count, metrics.BoundsChecks);
            Assert.InRange(metrics.BoundsChecks, 1, count * 40L);
        }

        [Theory]
        [InlineData("create_3d")]
        [InlineData("create_floor_plan")]
        [InlineData("create_drafting")]
        public void View_batch_refuses_an_explicit_name_already_taken_by_an_existing_view(string operation)
        {
            var actions = new JArray(new JObject { ["operation"] = operation, ["name"] = "Planta 1" });
            var conflict = CheckViewNames(actions, "planta 1");
            AssertViewNameRefused(conflict, "Planta 1", 0);
        }

        [Theory]
        [InlineData("create_drafting", "create_floor_plan")]
        [InlineData("create_floor_plan", "create_drafting")]
        [InlineData("create_3d", "create_perspective")]
        public void View_batch_reserves_names_across_view_kinds(string first, string second)
        {
            var actions = new JArray(new JObject { ["operation"] = first, ["name"] = "Nueva vista" },
                new JObject { ["operation"] = second, ["name"] = "NUEVA VISTA" });
            AssertViewNameRefused(CheckViewNames(actions), "NUEVA VISTA", 1);
            actions[1]["name"] = "Otra vista";
            Assert.Equal("preview-token", ((JObject)CheckViewNames(actions).Data).Value<string>("confirmation_token"));
        }

        [Fact]
        public void Repeated_sheet_titles_do_not_reserve_the_view_name()
        {
            var actions = JArray.Parse("[{'operation':'create_sheet','name':'Planta 1','number':'A1'}," +
                "{'operation':'create_sheet','name':'Planta 1','number':'A2'},{'operation':'create_floor_plan','name':'Planta 1'}]");
            Assert.True(CheckViewNames(actions).Success);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void View_batch_with_a_name_conflict_denies_fallback_but_keeps_the_capability_gap(int gapIndex)
        {
            var actions = JArray.Parse("[{'operation':'create_drafting','name':'Nueva vista'}," +
                "{'operation':'create_floor_plan','name':'Nueva vista','level_id':1}]");
            actions.Insert(gapIndex, new JObject { ["operation"] = "unsupported_view_operation" });
            var result = CheckViewNames(actions);

            AssertViewNameRefused(result, "Nueva vista", gapIndex == 0 ? 2 : 1);
            Assert.Equal("mixed_capability_and_invalid_input", result.Fallback.Reason);
            var gap = Assert.Single(result.CapabilityGaps);
            Assert.Equal(gapIndex, gap.Value<int>("index"));
            Assert.Equal(FallbackSignal.ReasonUnsupportedOperation, gap.Value<string>("reason"));
        }

        private static CommandResult CheckViewNames(JArray actions, params string[] existing)
        {
            var outcomes = new List<ActionOutcome>();
            return ViewArgumentRules.PlanBatch(actions, (index, row) =>
            {
                return row.Value<string>("operation") == "unsupported_view_operation"
                    ? ("Unsupported view operation.", FallbackSignal.ReasonUnsupportedOperation) : (null, null);
            }, () => existing, a => new[] { a.Value<string>("name") }, new JArray(), outcomes,
                () => CommandResult.Ok(new JObject { ["confirmation_token"] = "preview-token" }));
        }

        [Theory]
        [InlineData("2147483648")]
        [InlineData("9223372036854775808")]
        [InlineData("24001")]
        public void A_late_out_of_range_view_scale_keeps_the_first_name_conflict(string scale)
        {
            var actions = JArray.Parse("[{'operation':'create_drafting','name':'Nueva vista'}," +
                "{'operation':'create_drafting','name':'Nueva vista'}," +
                "{'operation':'create_drafting','name':'Otra vista','view_scale':" + scale + "}]");
            var result = CheckViewNames(actions);

            AssertViewNameRefused(result, "Nueva vista", 1);
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, result.Fallback.Reason);
            var row = Assert.Single((JArray)result.Detail["errors"]);
            Assert.Equal(2, row.Value<int>("index"));
            Assert.Equal("invalid_action", row.Value<string>("error_code"));
            Assert.Contains("view_scale", row.Value<string>("error"));
        }

        private static void AssertViewNameRefused(CommandResult conflict, string name, int index)
        {
            Assert.False(conflict.Success);
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, conflict.Detail.Value<string>("error_code"));
            Assert.False(conflict.Detail.Value<bool>("write_started"));
            Assert.False(conflict.Detail.Value<bool>("changes_applied"));
            Assert.Equal("view_name_conflict", conflict.Detail.Value<string>("code"));
            Assert.Equal(index, conflict.Detail.Value<int>("index"));
            Assert.Equal(name, conflict.Detail.Value<string>("view_name"));
            Assert.Null(conflict.Detail["confirmation_token"]);
            Assert.False(conflict.Fallback.IsAllowed);
            Assert.False(conflict.Fallback.WriteStarted);
        }

        [Fact]
        public void Spot_slope_declares_that_the_api_gap_cannot_be_retried_in_python()
        {
            var outcome = DimensionPlanRules.SpotSlopeRefusal("spot_slope", 0);
            var verdict = FallbackDecision.Decide(new[] { outcome }, writeStarted: false);
            var result = FallbackDecision.Refuse(outcome.Error, verdict);
            Assert.False(result.Success);
            Assert.Contains("spot_slope", result.Error);
            Assert.False(result.Fallback.IsAllowed);
            Assert.False(result.Fallback.WriteStarted);
            Assert.Equal("revit_api_unavailable", result.Fallback.Reason);
        }
    }
}

namespace Horizun.Revit.Commands
{
    // The production entry point runs against a simulated result from the Revit-only transaction.
    public sealed partial class TransformElementsCommand
    {
        internal Func<JObject, CommandResult> RunTransform { get; set; }
        private CommandResult ExecuteTransform(UIApplication app, JObject request) => RunTransform(request);
    }
}
