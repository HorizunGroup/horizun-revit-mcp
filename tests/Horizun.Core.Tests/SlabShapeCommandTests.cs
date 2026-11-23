using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SlabShapeCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void New_slab_split_plans_without_an_editor_and_applies_after_regeneration(bool editorInitiallyThrows)
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            Floor slab = FloorIn(doc);
            slab.EditorInitiallyUnavailable = true;
            slab.EditorInitiallyThrows = editorInitiallyThrows;
            slab.TopFace = new RectangularSlabFace(8, 40000 / 304.8, 9000 / 304.8, 44000 / 304.8, 12000 / 304.8);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new SlabShapeCommand();
            var request = JObject.Parse("{'element_id':1,'operation':'add_split_line','units':'mm','start':[40000,10000],'end':[44000,10000]}");

            CommandResult preview = command.Execute(app, request.ToString());
            Assert.True(preview.Success, preview.Error);
            Assert.True(slab.EditorReads > 0);
            Assert.Equal(1, doc.TestTransactionStarts);
            Assert.False(slab.Shape.IsEnabled);
            Assert.Empty(slab.Shape.TestCreases);
            JObject data = Assert.IsType<JObject>(preview.Data);
            Assert.Equal("preview_rolled_back", data.Value<string>("transaction_status"));
            Assert.False(string.IsNullOrEmpty(data.Value<string>("confirmation_token")));
            request["confirmation_token"] = data["confirmation_token"];
            request["idempotency_key"] = "split-new-floor";
            request["dry_run"] = false;

            CommandResult applied = command.Execute(app, request.ToString());

            Assert.True(applied.Success, applied.Error);
            Assert.True(doc.Assimilated);
            Assert.Equal(2, doc.TestTransactionStarts);
            Assert.Equal(1, slab.Shape.EnableCalls);
            SlabShapeCrease crease = Assert.Single(slab.Shape.SlabShapeCreases);
            XYZ a = crease.Curve.GetEndPoint(0);
            XYZ b = crease.Curve.GetEndPoint(1);
            if (a.X > b.X) (a, b) = (b, a);
            Assert.Equal(40000, a.X * 304.8, 6);
            Assert.Equal(44000, b.X * 304.8, 6);
            Assert.Equal(10000, a.Y * 304.8, 6);
            Assert.Equal(10000, b.Y * 304.8, 6);
            Assert.Equal(8.0, a.Z);
            Assert.Equal(8.0, b.Z);
            var appliedData = Assert.IsType<JObject>(applied.Data);
            Assert.Equal("pick_support", (string)appliedData["evidence"]["split_method"]);
            Assert.Equal("Committed", appliedData.Value<string>("transaction_status"));
            CommandResult read = command.Execute(app, "{'element_id':1,'operation':'read','units':'mm'}");
            Assert.True(read.Success, read.Error);
            Assert.Single(Assert.IsType<JObject>(read.Data)["creases"]);
        }

        [Fact]
        public void Split_planning_still_refuses_an_element_that_is_neither_floor_nor_roof()
        {
            var doc = new Document();
            doc.Elements.Add(1, new Wall { Id = new ElementId(1) });
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            CommandResult reply = new SlabShapeCommand().Execute(app,
                "{'element_id':1,'operation':'add_split_line','start':[0,1000],'end':[4000,1000]}");

            Assert.False(reply.Success);
            Assert.Contains("edits floors and roofs", reply.Error);
            Assert.Equal(0, doc.TestTransactionStarts);
        }

        [Theory]
        [InlineData(SlabShapeCreaseType.Boundary, 0, "split_line_coverage")]
        [InlineData(SlabShapeCreaseType.Boundary, 0, "new_split_creases")]
        [InlineData(SlabShapeCreaseType.UserDrawn, 0.25, "top_elevation_preserved")]
        public void Boundary_split_rolls_back_if_the_api_returns_the_wrong_crease_or_shifts_the_top_face(
            SlabShapeCreaseType creaseType, double topShift, string failedProperty)
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            Floor slab = FloorIn(doc);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new SlabShapeCommand();
            var request = JObject.Parse("{'element_id':1,'operation':'add_split_line','units':'feet','start':[0,1],'end':[4,1]}");

            CommandResult preview = command.Execute(app, request.ToString());
            Assert.True(preview.Success, preview.Error);
            slab.SupportCreaseType = creaseType;
            slab.TopFaceShiftAfterSupport = topShift;
            request["confirmation_token"] = Assert.IsType<JObject>(preview.Data)["confirmation_token"];
            request["idempotency_key"] = "bad-support-split";
            request["dry_run"] = false;

            CommandResult applied = command.Execute(app, request.ToString());

            Assert.False(applied.Success);
            Assert.False(doc.Assimilated);
            Assert.Equal(2, doc.TransactionGroupRollbacks);
            Assert.False(applied.Detail.Value<bool>("changes_applied"));
            Assert.Equal("RolledBack", applied.Detail.Value<string>("rollback_status"));
            Assert.Contains(applied.Detail["postconditions"]["properties"],
                row => !row.Value<bool>("matches") && row.Value<string>("property") == failedProperty);
        }

        [Fact]
        public void Split_requires_new_coverage_in_the_requested_span()
        {
            VerifySplitCoverage(3, false, false);
            VerifySplitCoverage(3, true, false);
            VerifySplitCoverage(2, false, true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ARQ22_rehearses_and_rereads_the_recorded_chord(bool shapedFirst)
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            Floor slab = RecordedFloor(doc);
            SlabShapeVertex left = VertexMm(40000, -39000, 0, SlabShapeVertexType.Edge);
            SlabShapeVertex right = VertexMm(44000, -39000, 0, SlabShapeVertexType.Edge);
            SlabShapeVertex raised = VertexMm(42000, -38500, 50);
            void Draw(params SlabShapeVertex[] path)
            {
                for (int i = 1; i < path.Length; i++)
                {
                    var crease = Crease(SlabShapeCreaseType.UserDrawn, path[i], path[i - 1]);
                    crease.CurveFailure = new Autodesk.Revit.Exceptions.InvalidOperationException(" ");
                    slab.Shape.TestCreases.Add(crease);
                }
            }
            int draws = 0;
            void VerifyEndpoints(SlabShapeVertex a, SlabShapeVertex b)
            {
                draws++;
                Assert.Equal(0, left.Position.DistanceTo(a.Position), 8);
                Assert.Equal(0, right.Position.DistanceTo(b.Position), 8);
            }
            if (shapedFirst)
            {
                slab.Shape.Enable();
                slab.Shape.TestVertices.Add(raised);
                foreach (double x in new[] { 40000d, 44000d })
                    foreach (double y in new[] { -40000d, -37000d })
                        slab.Shape.TestCreases.Add(Crease(SlabShapeCreaseType.Auto, VertexMm(x, y, 0), raised));
#if REVIT2025
                slab.Shape.TestDrawPoint = p => Assert.True(p.X == left.Position.X || p.X == right.Position.X);
                slab.Shape.TestLegacySplit = (_, _) => throw new InvalidOperationException("Expected the native chord.");
#else
                slab.Shape.TestVertices.AddRange(new[] { left, right });
#endif
                slab.Shape.TestSplit = (a, b) =>
                {
                    VerifyEndpoints(a, b);
                    Draw(a, VertexMm(41333.333333, -39000, 100d / 3), VertexMm(42666.666667, -39000, 100d / 3), b);
                };
            }
            else
            {
#if REVIT2025
                slab.Shape.TestSplit = (_, _) => throw new InvalidOperationException("Expected the flat boundary draw.");
                slab.Shape.TestLegacySplit = (a, b) => { VerifyEndpoints(a, b); Draw(a, b); };
#else
                slab.Shape.TestSupport = _ =>
                {
                    draws++;
                    slab.Shape.TestVertices.AddRange(new[] { left, right });
                    Draw(left, right);
                };
#endif
            }

            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new SlabShapeCommand();
            var request = JObject.Parse(RecordedSplit);
            CommandResult preview = command.Execute(app, request.ToString());
            Assert.True(preview.Success, preview.Error);
            Assert.Equal(1, draws);
            Assert.DoesNotContain(slab.Shape.TestCreases, c => c.CreaseType == SlabShapeCreaseType.UserDrawn);
            request["confirmation_token"] = Assert.IsType<JObject>(preview.Data)["confirmation_token"];
            request["idempotency_key"] = "q90-edge-chord";
            request["dry_run"] = false;
            CommandResult applied = command.Execute(app, request.ToString());
            Assert.True(applied.Success, applied.Error);
            Assert.Equal(2, draws);
            var evidence = Assert.IsType<JObject>(applied.Data)["evidence"];
            Assert.Equal(4000, (double)evidence["split_coverage_added_mm"], 6);
            Assert.Equal(0, (double)evidence["split_coverage_before_mm"]);
            Assert.Single(evidence["split_coverage_trace"]);
            if (shapedFirst) Assert.Contains(raised, slab.Shape.TestVertices);

            CommandResult read = command.Execute(app, "{'element_id':1428537,'operation':'read','units':'mm'}");
            Assert.True(read.Success, read.Error);
            var creases = Assert.IsType<JObject>(read.Data)["creases"].Where(c => (string)c["type"] == "UserDrawn").ToArray();
#if REVIT2025
            Assert.Equal(shapedFirst ? "native_chord" : "boundary_vertex", (string)evidence["split_method"]);
#endif
            Assert.Equal(shapedFirst ? 3 : 1, creases.Length);
            Assert.Equal(new[] { 44000d, -39000d, 0 }, creases.Last()["start"].Values<double>());
            Assert.Equal(new[] { 40000d, -39000d, 0 }, creases.First()["end"].Values<double>());
        }

        private const string RecordedSplit =
            "{'operation':'add_split_line','element_id':1428537,'units':'mm','start':[40000,-39000],'end':[44000,-39000]}";

        private static Floor RecordedFloor(Document doc)
        {
            Floor slab = FloorIn(doc, 1428537, 0);
            slab.TopFace = new RectangularSlabFace(0, 40000 / 304.8, -40000 / 304.8, 44000 / 304.8, -37000 / 304.8);
            slab.BottomFace = new RectangularSlabFace(-0.5);
            return slab;
        }

        private static SlabShapeVertex VertexMm(double x, double y, double z,
            SlabShapeVertexType type = SlabShapeVertexType.Interior) =>
            new SlabShapeVertex { Position = new XYZ(x / 304.8, y / 304.8, z / 304.8), VertexType = type };

        private static SlabShapeCrease Crease(SlabShapeCreaseType type, SlabShapeVertex start, SlabShapeVertex end) =>
            new SlabShapeCrease { CreaseType = type, EndPoints = new[] { start, end } };

        [Theory]
        [InlineData("SlabShapeEditor.SlabShapeCreases")]
        [InlineData("SlabShapeCrease.CreaseType")]
#if REVIT2025
        [InlineData("SlabShapeEditor.DrawPoint")]
#endif
        public void ARQ22_rehearsal_names_the_native_call_on_a_blank_exception(string call)
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            Floor slab = RecordedFloor(doc);
            slab.Shape.Enable();
            var failure = new Autodesk.Revit.Exceptions.InvalidOperationException(" ");
            switch (call)
            {
                case "SlabShapeEditor.SlabShapeCreases":
                    slab.Shape.TestCreasesFailure = failure;
                    break;
                case "SlabShapeCrease.CreaseType":
                    var crease = Crease(SlabShapeCreaseType.Auto, VertexMm(40000, -40000, 0), VertexMm(42000, -38500, 50));
                    crease.CreaseTypeFailure = failure;
                    slab.Shape.TestCreases.Add(crease);
                    break;
                default:
                    slab.Shape.TestDrawPoint = _ => throw failure;
                    break;
            }
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            CommandResult result = new SlabShapeCommand().Execute(app, RecordedSplit);

            Assert.False(result.Success);
            Assert.Contains(call + (call.EndsWith("DrawPoint") ? " (boundary vertex)" : "") + " failed", result.Error);
            Assert.Contains("(40000, -39000) to (44000, -39000)", result.Error);
            Assert.Null(result.Detail["confirmation_token"]);
            Assert.Equal("RolledBack", result.Detail.Value<string>("rollback_status"));
        }

        private static void VerifySplitCoverage(double initiallyCoveredTo, bool subdivide, bool expectedSuccess)
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            Floor slab = FloorIn(doc);
            var editor = slab.Shape;
            editor.Enable();
            editor.TestVertices.Add(new SlabShapeVertex { Position = new XYZ(1, 1, 8) });
            editor.TestVertices.Add(new SlabShapeVertex { Position = new XYZ(3, 1, 8) });
            SlabShapeCrease Crease(double start, double end, double y = 1) => new SlabShapeCrease
            {
                Curve = Line.CreateBound(new XYZ(start, y, 8), new XYZ(end, y, 8)),
                CreaseType = SlabShapeCreaseType.UserDrawn
            };
            editor.TestCreases.Add(Crease(1, initiallyCoveredTo));
            editor.TestSplit = (_, _) =>
            {
                if (subdivide)
                {
                    editor.TestCreases.Clear();
                    editor.TestCreases.Add(Crease(1, 2));
                    editor.TestCreases.Add(Crease(2, 3));
                }
                else if (expectedSuccess) editor.TestCreases.Add(Crease(3, 2));
                else editor.TestCreases.Add(Crease(1, 3, 2));
            };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new SlabShapeCommand();
            var request = JObject.Parse("{'element_id':1,'operation':'add_split_line','units':'feet','start':[1,1],'end':[3,1]}");
            CommandResult preview = command.Execute(app, request.ToString());
            Assert.Equal(expectedSuccess, preview.Success);
            if (!expectedSuccess)
            {
                Assert.Equal("RolledBack", preview.Detail.Value<string>("rollback_status"));
                Assert.Null(preview.Detail["confirmation_token"]);
                Assert.Contains(preview.Detail["postconditions"]["properties"],
                    row => row.Value<bool>("matches") && row.Value<string>("property") == "split_line_coverage");
                Assert.Contains(preview.Detail["postconditions"]["properties"],
                    row => !row.Value<bool>("matches") && row.Value<string>("property") == "new_split_creases");
                return;
            }
            request["confirmation_token"] = Assert.IsType<JObject>(preview.Data)["confirmation_token"];
            request["idempotency_key"] = "verify-new-coverage";
            request["dry_run"] = false;

            CommandResult applied = command.Execute(app, request.ToString());

            Assert.True(applied.Success, applied.Error);
            Assert.True(doc.Assimilated);
            Assert.Equal(304.8, (double)Assert.IsType<JObject>(applied.Data)["evidence"]["split_coverage_added_mm"], 6);
        }

        [Fact]
        public void ARQ22_blank_native_failure_reports_actual_endpoints_and_refuses_confirmation()
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            Floor slab = FloorIn(doc, 1428537);
            slab.TopFace = new RectangularSlabFace(8, 40000 / 304.8, -40000 / 304.8, 44000 / 304.8, -37000 / 304.8);
            slab.Shape.Enable();
            slab.Shape.TestVertices.Add(new SlabShapeVertex { Position = new XYZ(40000 / 304.8, -39000 / 304.8, 8) });
            slab.Shape.TestVertices.Add(new SlabShapeVertex { Position = new XYZ(44000 / 304.8, -39000 / 304.8, 8) });
            slab.Shape.TestVertices.Add(new SlabShapeVertex { Position = new XYZ(42000 / 304.8, -38500 / 304.8, 8 + 50 / 304.8) });
            slab.Shape.TestSplit = (_, _) => throw new Autodesk.Revit.Exceptions.InvalidOperationException(" ");
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new SlabShapeCommand();
            CommandResult result = command.Execute(app,
                "{'operation':'add_split_line','element_id':1428537,'units':'mm','start':[40000,-39000],'end':[44000,-39000]}");

            Assert.False(result.Success);
            Assert.Contains("Slab 1428537", result.Error);
            Assert.Contains("(40000, -39000) to (44000, -39000)", result.Error);
            Assert.Contains("drawing between Boundary and Boundary vertices", result.Error);
            Assert.Contains("SlabShapeEditor.AddSplitLine failed", result.Error);
            Assert.Null(result.Detail["confirmation_token"]);
            Assert.Equal("RolledBack", result.Detail.Value<string>("rollback_status"));
            Assert.False(result.Detail.Value<bool>("changes_applied"));
            Assert.False(doc.Assimilated);
        }

        [Fact]
        public void Read_returns_both_boundary_loops_from_a_flat_floor_with_a_disabled_editor()
        {
            var doc = new Document();
            Floor slab = FloorIn(doc);
            slab.TopFace.Hole = RectangularSlabFace.Loop(1, 1, 2, 2, 8);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            CommandResult reply = new SlabShapeCommand().Execute(app, "{'operation':'read','element_id':1,'units':'feet'}");

            Assert.True(reply.Success, reply.Error);
            var data = Assert.IsType<JObject>(reply.Data);
            Assert.False(data.Value<bool>("enabled"));
            Assert.Equal("top_face_boundary", data.Value<string>("vertex_source"));
            Assert.True(data.Value<bool>("boundary_read_complete"));
            var loops = Assert.IsType<JArray>(data["boundary_loops"]);
            Assert.Equal(2, loops.Count);
            Assert.All(loops, loop => Assert.Equal(4, Assert.IsType<JArray>(loop).Count));
            Assert.Equal(new[] { 1.0, 1.0, 8.0 }, new[]
            {
                (double)loops[1][0][0], (double)loops[1][0][1], (double)loops[1][0][2]
            });
            Assert.Equal("Boundary", (string)loops[1][0][3]);
            Assert.Equal(0, doc.TestTransactionStarts);
        }

        private static Floor FloorIn(Document doc, long id = 1, double elevation = 8)
        {
            var slab = new Floor { Id = new ElementId(id), LevelId = new ElementId(2), Document = doc };
            doc.Elements.Add(id, slab);
            doc.Elements.Add(2, new Level { Id = new ElementId(2), ProjectElevation = elevation });
            doc.CaptureFixtureState = () => slab.Shape.CaptureState();
            return slab;
        }
    }
}
