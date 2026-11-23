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
    public class SheetLayoutCommandResponseTests
    {
        private static (Document doc, UIApplication app, Viewport vp) Scene()
        {
            var d = new Document { Title=Guid.NewGuid().ToString(), TestConfirmationStore = new ConfirmationStore() };
            var sheet = new ViewSheet { Id=new ElementId(1), Document=d };
            var view = new View { Id=new ElementId(2), Document=d, Scale=75 };
            var vp = new Viewport { Id=new ElementId(3), Document=d, ViewId=view.Id, SheetId=sheet.Id, TestBox=new[] { 0.2,0.2,0.3,0.3 } };
            d.Elements[1]=sheet; d.Elements[2]=view; d.Elements[3]=vp;
            var frame = new FamilyInstance { Id=new ElementId(4), Document=d, TestCategory=BuiltInCategory.OST_TitleBlocks, TestOwnerView=1 };
            foreach (var ends in new[] { new[] {0,0,1,0},new[] {1,0,1,1},new[] {1,1,0,1},new[] {0,1,0,0} })
                frame.TestGeometry.Add(Line.CreateBound(new XYZ(ends[0],ends[1],0),new XYZ(ends[2],ends[3],0)));
            d.Elements[4]=frame;
            return (d,new UIApplication { ActiveUIDocument=new UIDocument { Document=d } },vp);
        }

        private static JObject ConfirmedApply(JObject request, CommandResult preview)
        {
            Assert.True(preview.Success, preview.Error);
            var token = ((JObject)preview.Data).Value<string>("confirmation_token");
            Assert.False(string.IsNullOrWhiteSpace(token));
            return new JObject(request)
            {
                ["dry_run"] = false,
                ["confirmation_token"] = token,
                ["idempotency_key"] = Guid.NewGuid().ToString()
            };
        }

        private static CommandResult Execute(UIApplication app, string json, bool dryRun = false)
        {
            var cmd = new SheetLayoutCommand();
            CommandResult preview = cmd.Execute(app, json);
            return dryRun ? preview : cmd.Execute(app, ConfirmedApply(JObject.Parse(json), preview).ToString());
        }

        private static JObject Canonical(CommandResult result, string what, string overrideHow)
        {
            Assert.True(result.Success,result.Error);
            var response=Assert.IsType<JObject>(result.Data);
            Assert.True(response.Value<bool>("has_assumptions"));
            var rows=Assert.IsType<JArray>(response["assumptions"]);
            JObject row=rows.OfType<JObject>().Single(r => r.Value<string>("what")==what);
            Assert.Equal(new[] { "flag","what","why","override" },row.Properties().Select(p=>p.Name));
            Assert.Equal("SUPUESTO",(string)row["flag"]);
            Assert.False(string.IsNullOrWhiteSpace((string)row["why"]));
            Assert.Equal(overrideHow,(string)row["override"]);
            Assert.Null(response["plan"]?["assumptions"]);
            Assert.Null(response["evidence"]?["assumptions"]);
            return response;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Real_set_view_scale_response_retains_current_scale_and_stamps_assumptions_at_top(bool dryRun)
        {
            var s=Scene();
            var result = Execute(s.app, "{operation:'set_view_scale',viewport_id:3}", dryRun);
            Canonical(result,"scale=75","send scale");
            Canonical(result,"center_anchor=box","send keep_viewport_center=false");
            Assert.Equal(75,((View)s.doc.Elements[2]).Scale);
            Assert.Equal(0, s.vp.MoveCalls);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Real_move_viewport_without_anchor_stamps_the_chosen_box_at_top(bool dryRun)
        {
            var s=Scene();
            Canonical(Execute(s.app, "{operation:'move_viewport',viewport_id:3,center:[100,100]}", dryRun), "anchor=box", "send anchor");
        }

        [Theory]
        [InlineData(true, -20, "resolved_anchor")]
        [InlineData(true, 0, "resolved_anchor")]
        [InlineData(false, -0.02, "move_feet")]
        public void Move_rejects_changed_anchor_or_delta_even_when_the_viewport_box_did_not_move(
            bool cropWasUnreadable, double cropShiftMm, string changedField)
        {
            var s = Scene();
            double feet = SheetLayoutRules.FeetPerMm;
            s.vp.TestBox = new[] { 80 * feet, 80 * feet, 120 * feet, 120 * feet };
            var view = (View)s.doc.Elements[2];
            view.CropBoxActive = true;
            view.TestCropTransformUnreadable = cropWasUnreadable;
            view.TestCropBox = CropAt(0);
            var cmd = new SheetLayoutCommand();
            var request = JObject.Parse("{operation:'move_viewport',viewport_id:3,center:[150,100]}");
            var preview = cmd.Execute(s.app, request.ToString());
            var apply = ConfirmedApply(request, preview);
            Assert.Equal(cropWasUnreadable ? "box" : "crop", (string)((JObject)preview.Data)["plan"]["anchor"]);
            Assert.Equal(50, (double)((JObject)preview.Data)["plan"]["move_mm"][0]);

            view.TestCropTransformUnreadable = false;
            view.TestCropBox = CropAt(cropShiftMm);
            var denied = cmd.Execute(s.app, apply.ToString());
            Assert.False(denied.Success);
            Assert.Equal("StalePlan", (string)denied.Detail["confirmation_state"]);
            Assert.Contains(changedField, denied.Error);
            Assert.Equal(0, s.doc.TestTransactionStarts);
            Assert.Equal(0, s.vp.MoveCalls);

            var fresh = cmd.Execute(s.app, request.ToString());
            Assert.True(fresh.Success, fresh.Error);
            Assert.Equal("crop", (string)((JObject)fresh.Data)["plan"]["anchor"]);
            Assert.Equal(SheetLayoutRules.Mm((50 - cropShiftMm) * feet), (double)((JObject)fresh.Data)["plan"]["move_mm"][0]);
            var applied = cmd.Execute(s.app, ConfirmedApply(request, fresh).ToString());
            Assert.True(applied.Success, applied.Error);
            Assert.Equal(1, s.vp.MoveCalls);

            BoundingBoxXYZ CropAt(double x) => new BoundingBoxXYZ
            {
                Min = new XYZ((x - 10) * feet, -10 * feet, 0),
                Max = new XYZ((x + 10) * feet, 10 * feet, 0)
            };
        }

        [Fact]
        public void Explicit_values_leave_no_empty_assumptions_array()
        {
            var s=Scene();
            var result=new SheetLayoutCommand().Execute(s.app,"{operation:'set_view_scale',viewport_id:3,scale:75,keep_viewport_center:false}");
            Assert.True(result.Success,result.Error);
            var response=(JObject)result.Data;
            Assert.Null(response["assumptions"]); Assert.Null(response["has_assumptions"]);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void Move_checks_the_other_viewport_title_in_both_reads(bool afterAssimilate, bool unreadableTitle)
        {
            double[] PaperBox(params double[] mm) => mm.Select(value => value * SheetLayoutRules.FeetPerMm).ToArray();

            var s = Scene();
            var before = PaperBox(20, 20, 40, 40);
            s.vp.TestBox = before;
            var other = new Viewport
            {
                Id = new ElementId(5), Document = s.doc, ViewId = new ElementId(2), SheetId = new ElementId(1),
                TestBox = PaperBox(65, 50, 95, 75)
            };
            s.doc.Elements.Add(5, other);
            // B's box clears A. Only the title can reveal the collision.
            void AddTitle()
            {
                other.TestLabel = PaperBox(70, 31, 95, 36);
                other.UnreadableLabel = unreadableTitle;
            }
            if (afterAssimilate) s.doc.TestAfterAssimilate = AddTitle;
            else s.doc.TestAfterCommit = AddTitle;

            var result = Execute(s.app, "{operation:'move_viewport',viewport_id:3,anchor:'box',center:[80,30]}");

            Assert.False(result.Success);
            Assert.Equal(afterAssimilate ? "postcommit_verification_failed" : "postcondition_failed", (string)result.Detail["code"]);
            Assert.Equal(afterAssimilate, (bool)result.Detail["changes_applied"]);
            Assert.Equal(afterAssimilate ? 0 : 1, s.doc.TestRollbacks);
            Assert.Equal(1, s.vp.MoveCalls);
            Assert.Equal(0, other.MoveCalls);
            if (!afterAssimilate) Assert.Equal(before, s.vp.TestBox);
            var evidence = result.Detail["evidence"];
            var checks = ((JArray)result.Detail["postconditions"]["properties"]).ToDictionary(p => (string)p["property"]);
            Assert.True((bool)checks["position"]["matches"]);
            Assert.True((bool)checks["inside_title_block"]["matches"]);
            Assert.Equal(!unreadableTitle, (bool)checks["no_overlap"]["measured"]);
            if (unreadableTitle)
            {
                var unreadable = Assert.Single((JArray)evidence["unreadable_viewports"]);
                Assert.Equal(5, (long)unreadable["viewport_id"]);
                Assert.Contains("label unavailable", (string)unreadable["why"]);
                Assert.Equal(JTokenType.Null, checks["no_overlap"]["matches"].Type);
                Assert.Contains("viewport 5", (string)checks["no_overlap"]["error"]);
            }
            else
            {
                Assert.Null(evidence["unreadable_viewports"]);
                Assert.False((bool)checks["no_overlap"]["matches"]);
                Assert.Equal(new[] { "3", "5" }, Assert.Single((JArray)evidence["overlaps"]).Values<string>());
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Move_checks_the_current_sheet_bounds_in_both_reads(bool afterAssimilate, bool useOutline)
        {
            double Feet(double mm) => mm * SheetLayoutRules.FeetPerMm;
            BoundingBoxXYZ TitleBlockBounds(double width) => new BoundingBoxXYZ
            {
                Min = XYZ.Zero, Max = new XYZ(Feet(width), Feet(100), 0)
            };

            var s = Scene();
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            var sheet = Assert.IsType<ViewSheet>(s.doc.Elements[1]);
            var before = new[] { Feet(20), Feet(20), Feet(40), Feet(40) };
            s.vp.TestBox = before;
            frame.TestBoundingBox = TitleBlockBounds(100);

            // The intended right edge is 90 mm. Either the box shrinks or its read
            // becomes unavailable and the current sheet outline provides the smaller bound.
            void ShrinkTitleBlock()
            {
                frame.TestBoundingBox = useOutline ? null : TitleBlockBounds(80);
                sheet.TestOutline = new BoundingBoxUV { Max = new UV { U = Feet(80), V = Feet(100) } };
            }
            if (afterAssimilate) s.doc.TestAfterAssimilate = ShrinkTitleBlock;
            else s.doc.TestAfterCommit = ShrinkTitleBlock;

            var result = Execute(s.app, "{operation:'move_viewport',viewport_id:3,anchor:'box',center:[80,30]}");

            Assert.False(result.Success);
            Assert.Equal(afterAssimilate ? "postcommit_verification_failed" : "postcondition_failed", (string)result.Detail["code"]);
            Assert.Equal(afterAssimilate, (bool)result.Detail["changes_applied"]);
            Assert.Equal(afterAssimilate ? 0 : 1, s.doc.TestRollbacks);
            Assert.Equal(1, s.vp.MoveCalls);
            if (afterAssimilate) Assert.Equal(90, SheetLayoutRules.Mm(s.vp.TestBox[2]));
            else Assert.Equal(before, s.vp.TestBox);

            var checks = ((JArray)result.Detail["postconditions"]["properties"]).ToDictionary(p => (string)p["property"]);
            Assert.True((bool)checks["position"]["matches"]);
            Assert.True((bool)checks["no_overlap"]["matches"]);
            Assert.True((bool)checks["inside_title_block"]["measured"]);
            Assert.False((bool)checks["inside_title_block"]["matches"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Move_marks_sheet_and_title_bounds_unmeasured_when_both_sources_are_unreadable(bool afterAssimilate)
        {
            var s = Scene();
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            var sheet = Assert.IsType<ViewSheet>(s.doc.Elements[1]);
            s.vp.TestLabel = new[] { 0.2, 0.15, 0.3, 0.17 };
            var before = s.vp.TestBox;
            frame.TestBoundingBox = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(1, 1, 0) };
            void LoseBounds()
            {
                frame.TestBoundingBox = null;
                sheet.TestOutline = null;
            }
            if (afterAssimilate) s.doc.TestAfterAssimilate = LoseBounds;
            else s.doc.TestAfterCommit = LoseBounds;

            var result = Execute(s.app, "{operation:'move_viewport',viewport_id:3,anchor:'box',center:[90,90]}");

            Assert.False(result.Success);
            Assert.Equal(afterAssimilate ? "postcommit_verification_failed" : "postcondition_failed", (string)result.Detail["code"]);
            Assert.Equal(afterAssimilate, (bool)result.Detail["changes_applied"]);
            Assert.Equal(afterAssimilate ? 0 : 1, s.doc.TestRollbacks);
            if (!afterAssimilate) Assert.Equal(before, s.vp.TestBox);
            var checks = ((JArray)result.Detail["postconditions"]["properties"]).ToDictionary(p => (string)p["property"]);
            Assert.False((bool)checks["inside_title_block"]["measured"]);
            Assert.Equal(JTokenType.Null, checks["inside_title_block"]["matches"].Type);
            Assert.True((bool)checks["no_overlap"]["matches"]);
            var title = result.Detail["evidence"]["title_check"];
            Assert.Equal("not_measured", (string)title["state"]);
            Assert.Contains("title block and sheet outline", (string)title["why"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Move_checks_projected_ink_against_current_bounds_in_both_reads(bool afterAssimilate)
        {
            double Feet(double mm) => mm * SheetLayoutRules.FeetPerMm;
            var s = Scene();
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            var before = new[] { Feet(20), Feet(20), Feet(40), Feet(40) };
            s.vp.TestBox = before;
            frame.TestBoundingBox = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(Feet(100), Feet(100), 0) };
            void ShrinkTitleBlock() => frame.TestBoundingBox = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(Feet(95), Feet(100), 0) };
            if (afterAssimilate) s.doc.TestAfterAssimilate = ShrinkTitleBlock;
            else s.doc.TestAfterCommit = ShrinkTitleBlock;

            var result = Execute(s.app,
                "{operation:'move_viewport',viewport_id:3,anchor:'ink',ink_box:[-10,-10,20,10],center:[85,30]}");

            Assert.False(result.Success);
            Assert.Equal(afterAssimilate ? "postcommit_verification_failed" : "postcondition_failed", (string)result.Detail["code"]);
            Assert.Equal(afterAssimilate, (bool)result.Detail["changes_applied"]);
            Assert.Equal(afterAssimilate ? 0 : 1, s.doc.TestRollbacks);
            if (afterAssimilate) Assert.Equal(90, SheetLayoutRules.Mm(s.vp.TestBox[2]));
            else Assert.Equal(before, s.vp.TestBox);
            var checks = ((JArray)result.Detail["postconditions"]["properties"]).ToDictionary(p => (string)p["property"]);
            Assert.True((bool)checks["position"]["matches"]);
            Assert.True((bool)checks["no_overlap"]["matches"]);
            Assert.True((bool)checks["inside_title_block"]["measured"]);
            Assert.False((bool)checks["inside_title_block"]["matches"]);
            var evidence = result.Detail["evidence"];
            Assert.Equal(100, (double)evidence["ink_after"]["max"][0]);
            Assert.False((bool)evidence["ink_remeasured"]);
        }

        [Fact]
        public void Repair_preserves_a_narrow_overlap_and_the_unmoved_axis()
        {
            double[] PaperBox(params double[] mm) => mm.Select(value => value * SheetLayoutRules.FeetPerMm).ToArray();

            var s = Scene();
            var before = PaperBox(11, -20, 45, 80);
            s.vp.TestBox = before;
            var other = new Viewport
            {
                Id = new ElementId(5), Document = s.doc, ViewId = new ElementId(2), SheetId = new ElementId(1),
                TestBox = PaperBox(44.995, 50, 60, 60)
            };
            s.doc.Elements.Add(5, other);

            var result = Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true}");

            Assert.True(result.Success, result.Error);
            Assert.Equal(0, s.doc.TestRollbacks);
            Assert.Equal(1, s.vp.MoveCalls);
            Assert.Equal(0, other.MoveCalls);
            Assert.Equal(before[0], s.vp.TestBox[0]);
            Assert.Equal(before[2], s.vp.TestBox[2]);
            Assert.Equal(0, SheetLayoutRules.Mm(s.vp.TestBox[1]));
            Assert.Equal(100, SheetLayoutRules.Mm(s.vp.TestBox[3]));
        }

        [Fact]
        public void Failure_of_a_write_using_an_assumption_names_it_and_preserves_the_failure_detail()
        {
            var s=Scene(); s.vp.ThrowOnMove=true;
            var result = Execute(s.app, "{operation:'move_viewport',viewport_id:3,center:[100,100]}");
            Assert.False(result.Success); Assert.Contains("SUPUESTO",result.Error);
            Assert.True(result.Detail.Value<bool>("has_assumptions"));
            Assert.Equal("RolledBack",(string)result.Detail["rollback_status"]);
        }

        [Fact]
        public void Unreadable_box_after_scale_change_explains_the_failed_centre_check()
        {
            var s=Scene(); s.vp.UnreadableAfterMove=true;
            var result = Execute(s.app, "{operation:'set_view_scale',viewport_id:3,scale:100}");
            Assert.False(result.Success); Assert.Contains("the viewport box could not be read",result.Detail.ToString());
        }

        [Fact]
        public void Real_repair_only_plans_and_moves_C_despite_old_overlap_A_B_and_reads_frame_once_per_pass()
        {
            var s=Scene(); s.vp.TestBox=new[] {0.1,0.1,0.3,0.3};
            var b=new Viewport {Id=new ElementId(5),Document=s.doc,ViewId=new ElementId(2),SheetId=new ElementId(1),TestBox=new[] {0.2,0.2,0.4,0.4}};
            var c=new Viewport {Id=new ElementId(6),Document=s.doc,ViewId=new ElementId(2),SheetId=new ElementId(1),TestBox=new[] {0.9,0.6,1.1,0.8}};
            s.doc.Elements[5]=b; s.doc.Elements[6]=c;
            var cmd=new SheetLayoutCommand();
            var request = JObject.Parse("{operation:'auto_layout',sheet_id:1,repair_outside:true}");
            var preview = cmd.Execute(s.app, request.ToString());
            Assert.True(preview.Success,preview.Error);
            var placements=(JArray)((JObject)preview.Data)["plan"]["placements"];
            Assert.Equal(6,(long)Assert.Single(placements)["viewport_id"]);
            Assert.Equal(new long[] {6},s.doc.TestOwnershipChecks);
            s.doc.TestOwnershipChecks.Clear(); ((FamilyInstance)s.doc.Elements[4]).GeometryReads=0;
            var applied = cmd.Execute(s.app, ConfirmedApply(request, preview).ToString());
            Assert.True(applied.Success,applied.Error);
            Assert.Equal(0,s.vp.MoveCalls); Assert.Equal(0,b.MoveCalls); Assert.Equal(1,c.MoveCalls);
            Assert.Equal(new long[] {6},s.doc.TestOwnershipChecks);
            Assert.Equal(3,((FamilyInstance)s.doc.Elements[4]).GeometryReads);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 5)]
        [InlineData(true, 5)]
        public void Repair_checks_the_current_drawing_frame_in_both_reads(bool afterAssimilate, int inset)
        {
            double Feet(double mm) => mm * SheetLayoutRules.FeetPerMm;
            var s = Scene();
            var before = new[] { Feet(75), Feet(20), Feet(120), Feet(40) };
            s.vp.TestBox = before;
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            frame.TestBoundingBox = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(Feet(100), Feet(100), 0) };
            void SetFrame(double width) => frame.TestGeometry = FrameLinesMm(0, 0, width, 100);
            SetFrame(100);
            if (afterAssimilate) s.doc.TestAfterAssimilate = () => SetFrame(80);
            else s.doc.TestAfterCommit = () => SetFrame(80);
            var request = new JObject
            {
                ["operation"] = "auto_layout", ["sheet_id"] = 1, ["repair_outside"] = true,
                ["margins"] = new JArray(0, inset, 0, 0), ["notes_width"] = inset
            };

            var result = Execute(s.app, request.ToString());

            Assert.False(result.Success);
            Assert.Equal(afterAssimilate ? "postcommit_verification_failed" : "postcondition_failed", (string)result.Detail["code"]);
            Assert.Equal(afterAssimilate, (bool)result.Detail["changes_applied"]);
            Assert.Equal(afterAssimilate ? 0 : 1, s.doc.TestRollbacks);
            Assert.Equal(1, s.vp.MoveCalls);
            if (afterAssimilate) Assert.Equal(100 - 2 * inset, SheetLayoutRules.Mm(s.vp.TestBox[2]));
            else Assert.Equal(before, s.vp.TestBox);
            var checks = ((JArray)result.Detail["postconditions"]["properties"]).ToDictionary(p => (string)p["property"]);
            Assert.True((bool)checks["positions"]["matches"]);
            Assert.True((bool)checks["no_overlap"]["matches"]);
            Assert.False((bool)checks["inside_usable_area"]["matches"]);
            Assert.False((bool)checks["drawing_area_after"]["matches"]);
            var evidence = result.Detail["evidence"];
            Assert.Equal(80, (double)evidence["usable_area_basis"]["frame"]["max"][0]);
            Assert.Equal(80 - 2 * inset, (double)evidence["usable_area"]["max"][0]);
            var area = evidence["drawing_area_after"];
            Assert.Equal("viewports_outside", (string)area["state"]);
            var outside = Assert.Single((JArray)area["outside"]);
            Assert.Equal(3, (long)outside["viewport_id"]);
            Assert.Equal(20, (double)outside["outside_mm"]["right"]);
        }

        [Theory]
        [InlineData(false, "empty", "no straight title block lines")]
        [InlineData(true, "empty", "no straight title block lines")]
        [InlineData(false, "short_lines", "no drawing frame boundary")]
        [InlineData(true, "short_lines", "no drawing frame boundary")]
        [InlineData(false, "outer_perimeter", "right")]
        [InlineData(true, "outer_perimeter", "right")]
        [InlineData(false, "corner_box", "corner title box")]
        [InlineData(true, "corner_box", "corner title box")]
        [InlineData(false, "no_usable_area", "margins and notes_width leave no usable area")]
        [InlineData(true, "no_usable_area", "margins and notes_width leave no usable area")]
        public void Repair_marks_bounds_unmeasured_when_the_current_area_cannot_be_verified(bool afterAssimilate, string change, string reason)
        {
            double Feet(double mm) => mm * SheetLayoutRules.FeetPerMm;
            var s = Scene();
            bool inset = change == "no_usable_area";
            bool cornerBox = change == "corner_box";
            var before = new[] { Feet(inset ? 85 : 75), Feet(20), Feet(100), Feet(40) };
            s.vp.TestBox = before;
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            frame.TestBoundingBox = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(Feet(100), Feet(100), 0) };
            frame.TestGeometry = FrameLinesMm(0, 0, inset || cornerBox ? 100 : 80, 100);
            if (cornerBox)
                foreach (GeometryObject line in FrameLinesMm(70, 5, 98, 40)) frame.TestGeometry.Add(line);
            var other = new Viewport
            {
                Id = new ElementId(5), Document = s.doc, ViewId = new ElementId(2), SheetId = new ElementId(1),
                TestBox = new[] { Feet(35), Feet(50), Feet(50), Feet(65) }
            };
            s.doc.Elements.Add(5, other);
            void ChangeGeometry()
            {
                frame.TestGeometry = change switch
                {
                    "empty" => new GeometryElement(),
                    "short_lines" => FrameLinesMm(40, 40, 50, 50),
                    "corner_box" => FrameLinesMm(0, 0, 100, 100),
                    "outer_perimeter" => FrameLinesMm(0, 0, 100, 100),
                    "no_usable_area" => FrameLinesMm(0, 0, 80, 100),
                    _ => throw new ArgumentException(change)
                };
                if (change == "short_lines") other.TestBox = new[] { Feet(75), Feet(50), Feet(90), Feet(65) };
            }
            if (afterAssimilate) s.doc.TestAfterAssimilate = ChangeGeometry;
            else s.doc.TestAfterCommit = ChangeGeometry;

            var request = new JObject { ["operation"] = "auto_layout", ["sheet_id"] = 1, ["repair_outside"] = true };
            if (inset)
            {
                request["margins"] = new JArray(35, 30, 0, 0);
                request["notes_width"] = 16;
            }
            var result = Execute(s.app, request.ToString());

            Assert.False(result.Success);
            Assert.Equal(afterAssimilate ? "postcommit_verification_failed" : "postcondition_failed", (string)result.Detail["code"]);
            Assert.Equal(afterAssimilate, (bool)result.Detail["changes_applied"]);
            Assert.Equal(afterAssimilate ? 0 : 1, s.doc.TestRollbacks);
            if (afterAssimilate) Assert.Equal(inset ? 54 : cornerBox ? 70 : 80, SheetLayoutRules.Mm(s.vp.TestBox[2]));
            else Assert.Equal(before, s.vp.TestBox);
            var checks = ((JArray)result.Detail["postconditions"]["properties"]).ToDictionary(p => (string)p["property"]);
            Assert.True((bool)checks["positions"]["matches"]);
            Assert.True((bool)checks["no_overlap"]["matches"]);
            Assert.False((bool)checks["inside_usable_area"]["measured"]);
            Assert.False((bool)checks["drawing_area_after"]["measured"]);
            Assert.Equal(JTokenType.Null, checks["inside_usable_area"]["matches"].Type);
            Assert.Equal(JTokenType.Null, checks["drawing_area_after"]["matches"].Type);
            var evidence = result.Detail["evidence"];
            Assert.Equal(JTokenType.Null, evidence["usable_area"].Type);
            var row = Assert.Single((JArray)evidence["viewports"]);
            Assert.True((bool)row["at_target"]);
            Assert.Equal(JTokenType.Null, row["inside"].Type);
            var area = evidence["drawing_area_after"];
            Assert.Equal("not_measured", (string)area["state"]);
            Assert.Contains(reason, (string)area["why"]);
        }

        [Theory]
        [InlineData(false, "outer_line_replaces_inner", "right")]
        [InlineData(true, "outer_line_replaces_inner", "right")]
        [InlineData(false, "corner_box_replaced", "corner title box")]
        [InlineData(true, "corner_box_replaced", "corner title box")]
        [InlineData(false, "corner_box_match_reused", "corner title box")]
        [InlineData(true, "corner_box_match_reused", "corner title box")]
        [InlineData(false, "perimeter_loses_three_sides", "right edge, top edge, bottom edge")]
        [InlineData(true, "perimeter_loses_three_sides", "right edge, top edge, bottom edge")]
        [InlineData(false, "perimeter_loses_right_side_and_box_expands", "right")]
        [InlineData(true, "perimeter_loses_right_side_and_box_expands", "right")]
        public void Repair_reports_lost_boundary_evidence_in_both_reads(bool afterAssimilate, string change, string reason)
        {
            double Feet(double mm) => mm * SheetLayoutRules.FeetPerMm;
            var s = Scene();
            var before = new[] { Feet(75), Feet(50), Feet(120), Feet(65) };
            s.vp.TestBox = before;
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            BoundingBoxXYZ Paper(double width) => new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(Feet(width), Feet(100), 0) };
            frame.TestBoundingBox = Paper(100);
            frame.TestGeometry = FrameLinesMm(0, 0, 100, 100);
            double targetRight = 100;
            double otherRight = 55;
            Action changeFrame;
            switch (change)
            {
                case "outer_line_replaces_inner":
                    frame.TestGeometry.Add(FrameLineMm(80, 0, 80, 100));
                    frame.TestGeometry.Add(FrameLineMm(95, 0, 95, 100));
                    changeFrame = () =>
                    {
                        frame.TestGeometry = FrameLinesMm(0, 0, 100, 100);
                        frame.TestGeometry.Add(FrameLineMm(95, 0, 95, 100));
                    };
                    targetRight = 80;
                    otherRight = 90;
                    break;
                case "corner_box_replaced":
                    foreach (GeometryObject line in FrameLinesMm(70, 5, 98, 40)) frame.TestGeometry.Add(line);
                    changeFrame = () =>
                    {
                        frame.TestGeometry = FrameLinesMm(0, 0, 100, 100);
                        foreach (GeometryObject line in FrameLinesMm(2, 5, 30, 40)) frame.TestGeometry.Add(line);
                    };
                    targetRight = 70;
                    otherRight = 90;
                    break;
                case "corner_box_match_reused":
                    foreach (GeometryObject line in FrameLinesMm(69, 5, 98, 40)) frame.TestGeometry.Add(line);
                    frame.TestGeometry.Add(FrameLineMm(69.8, 5, 69.8, 40));
                    changeFrame = () =>
                    {
                        frame.TestGeometry = FrameLinesMm(0, 0, 100, 100);
                        foreach (GeometryObject line in FrameLinesMm(69.4, 5, 98, 40)) frame.TestGeometry.Add(line);
                        foreach (GeometryObject line in FrameLinesMm(2, 80, 25, 95)) frame.TestGeometry.Add(line);
                    };
                    targetRight = 69;
                    break;
                case "perimeter_loses_three_sides":
                    changeFrame = () => frame.TestGeometry = PartialFrameLinesMm();
                    break;
                case "perimeter_loses_right_side_and_box_expands":
                    changeFrame = () =>
                    {
                        frame.TestBoundingBox = Paper(120);
                        frame.TestGeometry = PartialFrameLinesMm();
                    };
                    otherRight = 110;
                    break;
                default: throw new ArgumentException(change);
            }
            var other = new Viewport
            {
                Id = new ElementId(5), Document = s.doc, ViewId = new ElementId(2), SheetId = new ElementId(1),
                TestBox = new[] { Feet(40), Feet(10), Feet(55), Feet(20) }
            };
            s.doc.Elements.Add(5, other);
            void ChangeGeometry()
            {
                changeFrame();
                other.TestBox = new[] { Feet(otherRight - 15), Feet(10), Feet(otherRight), Feet(20) };
            }
            if (afterAssimilate) s.doc.TestAfterAssimilate = ChangeGeometry;
            else s.doc.TestAfterCommit = ChangeGeometry;

            var result = Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true}");

            Assert.False(result.Success);
            Assert.Equal(afterAssimilate ? "postcommit_verification_failed" : "postcondition_failed", (string)result.Detail["code"]);
            Assert.Equal(afterAssimilate, (bool)result.Detail["changes_applied"]);
            Assert.Equal(afterAssimilate ? 0 : 1, s.doc.TestRollbacks);
            Assert.Equal(0, other.MoveCalls);
            if (afterAssimilate) Assert.Equal(targetRight, SheetLayoutRules.Mm(s.vp.TestBox[2]));
            else Assert.Equal(before, s.vp.TestBox);
            var checks = ((JArray)result.Detail["postconditions"]["properties"]).ToDictionary(p => (string)p["property"]);
            Assert.True((bool)checks["positions"]["matches"]);
            Assert.True((bool)checks["no_overlap"]["matches"]);
            foreach (string property in new[] { "inside_usable_area", "drawing_area_after" })
            {
                Assert.False((bool)checks[property]["measured"]);
                Assert.Equal(JTokenType.Null, checks[property]["matches"].Type);
            }
            var evidence = result.Detail["evidence"];
            Assert.Equal(JTokenType.Null, evidence["usable_area"].Type);
            Assert.Equal(JTokenType.Null, Assert.Single((JArray)evidence["viewports"])["inside"].Type);
            var area = evidence["drawing_area_after"];
            Assert.Equal("not_measured", (string)area["state"]);
            Assert.Contains(reason, (string)area["why"]);
            if (change == "corner_box_match_reused")
                Assert.Equal(2, ((JArray)evidence["usable_area_basis"]["title_box_in_corner"]).Count);
        }

        [Fact]
        public void Repair_accepts_distinct_corner_matches_when_the_first_match_must_be_reassigned()
        {
            double Feet(double mm) => mm * SheetLayoutRules.FeetPerMm;
            var s = Scene();
            s.vp.TestBox = new[] { Feet(75), Feet(50), Feet(120), Feet(65) };
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            frame.TestBoundingBox = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(Feet(100), Feet(100), 0) };
            frame.TestGeometry = FrameLinesMm(0, 0, 100, 100);
            frame.TestGeometry.Add(FrameLineMm(69.8, 5, 69.8, 40));
            foreach (GeometryObject line in FrameLinesMm(69, 5, 98, 40)) frame.TestGeometry.Add(line);
            s.doc.TestAfterCommit = () =>
            {
                frame.TestGeometry = FrameLinesMm(0, 0, 100, 100);
                foreach (GeometryObject line in FrameLinesMm(69.4, 5, 98, 40)) frame.TestGeometry.Add(line);
                frame.TestGeometry.Add(FrameLineMm(69.95, 5, 69.95, 40));
            };

            var before = new SheetLayoutCommand().Execute(s.app, "{operation:'get_viewport_box',sheet_id:1}");
            var original = (JArray)((JObject)before.Data)["drawing_area_check"]["title_box_in_corner"];
            Assert.Equal(new[] { 69.8, 69 }, original.Select(box => (double)box["min"][0]));

            var result = Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true}");

            Assert.True(result.Success, result.Error);
            var area = ((JObject)result.Data)["evidence"]["drawing_area_after"];
            var current = (JArray)area["title_box_in_corner"];
            Assert.Equal(2, current.Count);
            Assert.Equal(69.4, (double)current[0]["min"][0]);
            Assert.Equal("ok", (string)area["state"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Repair_distinguishes_a_measured_perimeter_from_box_fallback(bool hasRightLine)
        {
            double Feet(double mm) => mm * SheetLayoutRules.FeetPerMm;
            var s = Scene();
            s.vp.TestBox = new[] { Feet(75), Feet(50), Feet(120), Feet(65) };
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            frame.TestBoundingBox = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(Feet(100), Feet(100), 0) };
            frame.TestGeometry = PartialFrameLinesMm(hasRightLine ? 100 : (double?)null);
            s.doc.TestAfterCommit = () => frame.TestGeometry = PartialFrameLinesMm(hasRightLine ? 100.25 : (double?)null);

            var result = Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true}");

            Assert.True(result.Success, result.Error);
            var evidence = ((JObject)result.Data)["evidence"];
            var area = evidence["drawing_area_after"];
            Assert.Equal("approximate", (string)area["state"]);
            Assert.Contains("no inner frame or corner title box cut", (string)area["means"]);
            Assert.Contains("0.5 mm", (string)area["means"]);
            var lines = area["boundary_lines_mm"];
            Assert.Equal(0, (double)lines["left"]);
            if (hasRightLine) Assert.InRange((double)lines["right"], 100.2, 100.3);
            else Assert.Equal(JTokenType.Null, lines["right"].Type);
            Assert.Equal(JTokenType.Null, lines["top"].Type);
            Assert.Equal(JTokenType.Null, lines["bottom"].Type);
            Assert.True(JToken.DeepEquals(lines, evidence["usable_area_basis"]["boundary_lines_mm"]));
        }

        private static GeometryElement PartialFrameLinesMm(double? right = null)
        {
            // Four straight instance lines keep the read on instance geometry. Only the left line delimits the frame.
            var lines = new GeometryElement
            {
                FrameLineMm(0, 0, 0, 100), FrameLineMm(15, 15, 20, 15),
                FrameLineMm(20, 25, 25, 25), FrameLineMm(25, 35, 30, 35)
            };
            if (right is double x) lines.Add(FrameLineMm(x, 0, x, 100));
            return lines;
        }

        private static Line FrameLineMm(double x0, double y0, double x1, double y1)
        {
            XYZ Point(double x, double y) => new XYZ(x * SheetLayoutRules.FeetPerMm, y * SheetLayoutRules.FeetPerMm, 0);
            return Line.CreateBound(Point(x0, y0), Point(x1, y1));
        }

        private static GeometryElement FrameLinesMm(double left, double bottom, double right, double top)
        {
            XYZ Point(double x, double y) => new XYZ(x * SheetLayoutRules.FeetPerMm, y * SheetLayoutRules.FeetPerMm, 0);
            var points = new[] { Point(left, bottom), Point(right, bottom), Point(right, top), Point(left, top), Point(left, bottom) };
            var geometry = new GeometryElement();
            for (int i = 0; i < 4; i++) geometry.Add(Line.CreateBound(points[i], points[i + 1]));
            return geometry;
        }

        [Fact]
        public void Repair_refuses_to_plan_with_only_short_title_block_lines()
        {
            var s = Scene();
            s.vp.TestBox = new[] { 0.9, 0.2, 1.1, 0.3 };
            var frame = Assert.IsType<FamilyInstance>(s.doc.Elements[4]);
            frame.TestGeometry = FrameLinesMm(140, 140, 150, 150);

            var result = new SheetLayoutCommand().Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true}");

            Assert.False(result.Success);
            Assert.Contains("no drawing frame boundary", result.Error);
            Assert.Equal(0, s.doc.TestTransactionStarts);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Real_repair_without_outside_viewports_opens_no_transaction_and_needs_no_ownership(bool emptySheet)
        {
            var s=Scene();
            if (emptySheet) s.doc.Elements.Remove(3);
            var result=new SheetLayoutCommand().Execute(s.app,"{operation:'auto_layout',sheet_id:1,repair_outside:true,dry_run:false}");
            Assert.True(result.Success,result.Error);
            Assert.Equal("nada que reparar",(string)((JObject)result.Data)["message"]);
            Assert.Equal(0,s.doc.TestTransactionStarts); Assert.Empty(s.doc.TestOwnershipChecks); Assert.Equal(0,s.vp.MoveCalls);
        }

        [Fact]
        public void Repair_refuses_an_unreadable_title_before_writing()
        {
            var s = Scene();
            s.vp.UnreadableLabel = true;
            var cmd = new SheetLayoutCommand();
            var query = cmd.Execute(s.app, "{operation:'get_viewport_box',sheet_id:1}");
            var check = ((JObject)query.Data)["drawing_area_check"];
            Assert.Equal("partial", (string)check["state"]);
            Assert.Equal(3, (long)check["unreadable"][0]["viewport_id"]);

            var result = cmd.Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true,dry_run:false}");
            Assert.False(result.Success);
            Assert.Contains("viewport 3: viewport title is unreadable", result.Error);
            Assert.Equal(0, s.doc.TestTransactionStarts);
        }

        [Fact]
        public void Real_irreparable_viewport_refuses_by_id_without_rows_or_transaction()
        {
            var s=Scene(); s.vp.TestBox=new[] {0.0,0.0,1.2,0.2};
            var result=new SheetLayoutCommand().Execute(s.app,"{operation:'auto_layout',sheet_id:1,repair_outside:true,dry_run:false}");
            Assert.False(result.Success); Assert.Contains("viewport 3",result.Error); Assert.Contains("too large",result.Error);
            Assert.Equal(0,s.doc.TestTransactionStarts); Assert.Equal(0,s.vp.MoveCalls);
        }

        [Fact]
        public void Drawing_area_after_rolls_back_when_only_an_unmoved_viewport_ends_up_outside()
        {
            var s = Scene();
            var before = new[] { 0.9, 0.2, 1.1, 0.3 };
            s.vp.TestBox = before;
            var other = new Viewport
            {
                Id = new ElementId(5), Document = s.doc, ViewId = new ElementId(2), SheetId = new ElementId(1),
                TestBox = new[] { 0.7, 0.7, 0.9, 0.8 }
            };
            s.doc.Elements.Add(5, other);
            s.doc.TestAfterCommit = () => other.TestBox = new[] { 0.9, 0.7, 1.1, 0.8 };

            var result = Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true}");
            Assert.False(result.Success);
            Assert.Equal(1, s.doc.TestRollbacks);
            Assert.Equal(1, s.vp.MoveCalls);
            Assert.Equal(0, other.MoveCalls);
            Assert.Equal(before, s.vp.TestBox);
            var area = result.Detail["evidence"]["drawing_area_after"];
            Assert.Equal("viewports_outside", (string)area["state"]);
            Assert.Equal(5, (long)Assert.Single((JArray)area["outside"])["viewport_id"]);
            var checks = ((JArray)result.Detail["postconditions"]["properties"])
                .ToDictionary(p => (string)p["property"], p => (bool)p["matches"]);
            Assert.True(checks["positions"]);
            Assert.True(checks["inside_usable_area"]);
            Assert.True(checks["no_overlap"]);
            Assert.False(checks["drawing_area_after"]);
        }

        [Fact]
        public void Repair_rolls_back_when_the_reread_finds_an_old_overlap_grew()
        {
            var s = Scene();
            var before = new[] { 0.75, 0.2, 1.2, 0.6 };
            s.vp.TestBox = before;
            var other = new Viewport
            {
                Id = new ElementId(5), Document = s.doc, ViewId = new ElementId(2), SheetId = new ElementId(1),
                TestBox = new[] { 0.75, 0.5, 0.8, 0.6 }
            };
            s.doc.Elements.Add(5, other);
            var cmd = new SheetLayoutCommand();
            var request = JObject.Parse("{operation:'auto_layout',sheet_id:1,repair_outside:true}");
            var apply = ConfirmedApply(request, cmd.Execute(s.app, request.ToString()));
            s.doc.TestAfterCommit = () => other.TestBox = new[] { 0.55, 0.2, 0.8, 0.6 };

            var result = cmd.Execute(s.app, apply.ToString());
            Assert.False(result.Success);
            Assert.Equal(1, s.doc.TestRollbacks);
            Assert.Equal(before, s.vp.TestBox);
            var overlap = Assert.Single((JArray)result.Detail["evidence"]["overlaps"]);
            Assert.Equal(new[] { "3", "5" }, overlap.Values<string>());
            var checks = ((JArray)result.Detail["postconditions"]["properties"])
                .ToDictionary(p => (string)p["property"], p => (bool)p["matches"]);
            Assert.True(checks["positions"]);
            Assert.True(checks["drawing_area_after"]);
            Assert.False(checks["no_overlap"]);
        }

        [Fact]
        public void Repair_rolls_back_when_the_reread_crosses_the_overlap_threshold_with_less_area()
        {
            double[] PaperBox(params double[] mm) => mm.Select(value => value * SheetLayoutRules.FeetPerMm).ToArray();

            var s = Scene();
            var before = PaperBox(20, -20, 40, 80);
            s.vp.TestBox = before;
            var otherBefore = PaperBox(39.6, 50, 60, 60);
            var other = new Viewport
            {
                Id = new ElementId(5), Document = s.doc, ViewId = new ElementId(2), SheetId = new ElementId(1),
                TestBox = otherBefore
            };
            s.doc.Elements.Add(5, other);
            // The planned translation preserves the 0.4 x 10 mm intersection.
            // The first re-read measures 0.8 x 2 mm, smaller but over tolerance in both axes.
            s.doc.TestAfterCommit = () => other.TestBox = PaperBox(39.2, 98, 59.6, 108);

            var result = Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true}");

            Assert.False(result.Success);
            Assert.Equal(1, s.doc.TestRollbacks);
            Assert.Equal(1, s.vp.MoveCalls);
            Assert.Equal(0, other.MoveCalls);
            Assert.Equal(before, s.vp.TestBox);
            Assert.Equal(otherBefore, other.TestBox);
            var overlap = Assert.Single((JArray)result.Detail["evidence"]["overlaps"]);
            Assert.Equal(new[] { "3", "5" }, overlap.Values<string>());
            var checks = ((JArray)result.Detail["postconditions"]["properties"])
                .ToDictionary(p => (string)p["property"], p => (bool)p["matches"]);
            Assert.True(checks["positions"]);
            Assert.True(checks["inside_usable_area"]);
            Assert.True(checks["drawing_area_after"]);
            Assert.False(checks["no_overlap"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Repair_cannot_verify_overlap_when_one_unmoved_title_read_fails(bool afterAssimilate)
        {
            double[] PaperBox(params double[] mm) => mm.Select(value => value * SheetLayoutRules.FeetPerMm).ToArray();

            var s = Scene();
            var before = PaperBox(20, -20, 40, 80);
            s.vp.TestBox = before;
            var otherBefore = PaperBox(39.6, 50, 60, 60);
            var other = new Viewport
            {
                Id = new ElementId(5), Document = s.doc, ViewId = new ElementId(2), SheetId = new ElementId(1),
                TestBox = otherBefore
            };
            s.doc.Elements.Add(5, other);
            var otherAfter = PaperBox(39.2, 98, 59.6, 108);
            void ChangeOverlapAndFailOneRead()
            {
                other.TestBox = otherAfter;
                other.TestLabelReadsUntilError = 1;
            }
            if (afterAssimilate) s.doc.TestAfterAssimilate = ChangeOverlapAndFailOneRead;
            else s.doc.TestAfterCommit = ChangeOverlapAndFailOneRead;

            var result = Execute(s.app, "{operation:'auto_layout',sheet_id:1,repair_outside:true}");

            Assert.False(result.Success);
            Assert.Equal(afterAssimilate ? "postcommit_verification_failed" : "postcondition_failed", (string)result.Detail["code"]);
            Assert.Equal(afterAssimilate, (bool)result.Detail["changes_applied"]);
            Assert.Equal(afterAssimilate ? 0 : 1, s.doc.TestRollbacks);
            Assert.Equal(1, s.vp.MoveCalls);
            Assert.Equal(0, other.MoveCalls);
            if (!afterAssimilate) Assert.Equal(before, s.vp.TestBox);
            Assert.Equal(afterAssimilate ? otherAfter : otherBefore, other.TestBox);
            var area = result.Detail["evidence"]["drawing_area_after"];
            Assert.Equal("partial", (string)area["state"]);
            Assert.Equal(1, (int)area["unreadable_viewports"]);
            var unreadable = Assert.Single((JArray)area["unreadable"]);
            Assert.Equal(5, (long)unreadable["viewport_id"]);
            Assert.Contains("label unavailable", (string)unreadable["why"]);
            var checks = ((JArray)result.Detail["postconditions"]["properties"]).ToDictionary(p => (string)p["property"]);
            Assert.True((bool)checks["positions"]["matches"]);
            Assert.True((bool)checks["inside_usable_area"]["matches"]);
            Assert.False((bool)checks["drawing_area_after"]["matches"]);
            Assert.False((bool)checks["no_overlap"]["measured"]);
            Assert.Equal(JTokenType.Null, checks["no_overlap"]["matches"].Type);
            Assert.Contains("viewport 5", (string)checks["no_overlap"]["error"]);
        }

        [Theory]
        [InlineData(true, "PlanChanged")]
        [InlineData(false, "StalePlan")]
        public void Repair_rejects_a_token_for_different_arguments_or_a_different_resolved_plan(bool changeArguments, string state)
        {
            var s = Scene();
            s.vp.TestBox = new[] { 0.9, 0.2, 1.1, 0.3 };
            var cmd = new SheetLayoutCommand();
            var request = JObject.Parse("{operation:'auto_layout',sheet_id:1,repair_outside:true}");
            var apply = ConfirmedApply(request, cmd.Execute(s.app, request.ToString()));
            if (changeArguments) apply["margins"] = new JArray(5, 5, 5, 5);
            else s.vp.TestBox = new[] { 0.95, 0.2, 1.15, 0.3 };

            var denied = cmd.Execute(s.app, apply.ToString());
            Assert.False(denied.Success);
            Assert.Equal(state, (string)denied.Detail["confirmation_state"]);
            Assert.Equal(0, s.doc.TestTransactionStarts);
            Assert.Equal(0, s.vp.MoveCalls);

            var fresh = Execute(s.app, request.ToString());
            Assert.True(fresh.Success, fresh.Error);
            Assert.Equal(1, s.vp.MoveCalls);
        }

        [Fact]
        public void Explicit_margins_inset_the_measured_frame_instead_of_the_paper_edge()
        {
            var s=Scene(); var frame=(FamilyInstance)s.doc.Elements[4]; frame.TestGeometry.Clear();
            foreach (double[] e in new[] { new[] {0.1,0.1,0.9,0.1},new[] {0.9,0.1,0.9,0.9},new[] {0.9,0.9,0.1,0.9},new[] {0.1,0.9,0.1,0.1} })
                frame.TestGeometry.Add(Line.CreateBound(new XYZ(e[0],e[1],0),new XYZ(e[2],e[3],0)));
            s.vp.TestBox=new[] {0.85,0.4,1.0,0.5};
            var result=new SheetLayoutCommand().Execute(s.app,"{operation:'auto_layout',sheet_id:1,repair_outside:true,margins:[10,20,30,40]}");
            Assert.True(result.Success,result.Error);
            JObject area=(JObject)((JObject)result.Data)["plan"]["usable_area"];
            Assert.Equal(SheetLayoutRules.Mm(0.9)-20,(double)area["max"][0],1);
            Assert.Equal(SheetLayoutRules.Mm(0.1)+40,(double)area["min"][1],1);
        }

        [Fact]
        public void Unreadable_frame_is_named_in_the_repair_refusal_and_the_read_response()
        {
            var s=Scene(); ((FamilyInstance)s.doc.Elements[4]).TestGeometry.Clear();
            var cmd=new SheetLayoutCommand();
            var read=cmd.Execute(s.app,"{operation:'get_viewport_box',sheet_id:1}");
            Assert.True(read.Success,read.Error);
            Assert.Contains("family not editable",((JObject)read.Data)["drawing_area_check"]["frame_read_warning"].ToString());
            var repair=cmd.Execute(s.app,"{operation:'auto_layout',sheet_id:1,repair_outside:true}");
            Assert.False(repair.Success); Assert.Contains("drawing frame could not be read",repair.Error);
            Assert.Equal(0,s.doc.TestTransactionStarts);
        }

        [Theory]
        [InlineData("sheet_id:1")]
        [InlineData("viewport_id:3")]
        public void Viewport_box_reads_the_current_title_offset_from_Revit_on_every_call(string target)
        {
            var s = Scene();
            s.vp.TestLabel = new[] { 0.2, 0.1, 0.4, 0.15 };
            string json = "{operation:'get_viewport_box'," + target + "}";
            s.vp.LabelOffset = new XYZ(0.1, -0.2, 0);
            JObject first = ReadViewport(s.app, json);
            Assert.Equal(new[] { 30.5, -61.0 }, first["label_offset_mm"].Values<double>());
            Assert.True(JToken.DeepEquals(first["label_offset_mm"], first["label_offset"]));

            s.vp.LabelOffset = new XYZ(-0.3, 0.25, 0);
            JObject second = ReadViewport(s.app, json);
            Assert.Equal(new[] { -91.4, 76.2 }, second["label_offset_mm"].Values<double>());
            Assert.True(JToken.DeepEquals(second["label_offset_mm"], second["label_offset"]));
            Assert.True(JToken.DeepEquals(first["label"], second["label"]));
            Assert.Equal(0, s.doc.TestTransactionStarts);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Title_offset_application_verifies_the_visible_label_movement(bool labelFollowsOffset)
        {
            var s = Scene();
            double dy = -12 * SheetLayoutRules.FeetPerMm;
            s.vp.TestLabel = new[] { 0.2, 0.1, 0.4, 0.15 };
            s.vp.LabelOffset = new XYZ(4 * SheetLayoutRules.FeetPerMm, 7 * SheetLayoutRules.FeetPerMm, 0);
            s.doc.CaptureFixtureState = () =>
            {
                XYZ offset = s.vp.LabelOffset;
                return () => s.vp.LabelOffset = offset;
            };
            bool committedRequestedOffset = false;
            s.doc.TestAfterCommit = () =>
            {
                committedRequestedOffset = Math.Abs(s.vp.LabelOffset.X) < 1e-9 && Math.Abs(s.vp.LabelOffset.Y - dy) < 1e-9;
                if (labelFollowsOffset)
                    s.vp.TestLabel = new[] { 0.2 - 4 * SheetLayoutRules.FeetPerMm, 0.1 - 19 * SheetLayoutRules.FeetPerMm,
                        0.4 - 4 * SheetLayoutRules.FeetPerMm, 0.15 - 19 * SheetLayoutRules.FeetPerMm };
            };

            var result = Execute(s.app, "{operation:'set_title',viewport_id:3,label_offset:[0,-12]}");
            Assert.Equal(labelFollowsOffset, result.Success);
            Assert.True(committedRequestedOffset);
            if (!labelFollowsOffset) Assert.Contains("title_moved_with_offset", result.Error);
            JObject row = ReadViewport(s.app, "{operation:'get_viewport_box',viewport_id:3}");
            Assert.Equal(labelFollowsOffset ? new[] { 0.0, -12 } : new[] { 4.0, 7 }, row["label_offset"].Values<double>());
        }

        [Fact]
        public void Viewport_box_explains_an_unreadable_title_offset()
        {
            var s = Scene();
            s.vp.UnreadableLabelOffset = true;
            JObject row = ReadViewport(s.app, "{operation:'get_viewport_box',viewport_id:3}");
            Assert.Null(row["label_offset_mm"]);
            Assert.Contains("title offset unavailable", (string)row["label_offset_warning"]);
            Assert.NotNull(row["box"]);
        }

        private static JObject ReadViewport(UIApplication app, string request)
        {
            var result = new SheetLayoutCommand().Execute(app, request);
            Assert.True(result.Success, result.Error);
            return Assert.IsType<JObject>(Assert.Single((JArray)((JObject)result.Data)["viewports"]));
        }
    }
}
