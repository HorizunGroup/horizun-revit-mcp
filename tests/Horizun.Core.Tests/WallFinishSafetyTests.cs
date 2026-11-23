using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class WallFinishSafetyTests
    {
        [Fact]
        public void Original_ids_are_captured_before_delete_and_checked_by_lookup_afterward()
        {
            var originals = new Dictionary<long, bool> { [11] = true, [12] = true, [13] = true, [14] = true };
            var reads = new List<long>();
            long ReadId(long id)
            {
                if (!originals[id]) throw new Autodesk.Revit.Exceptions.InvalidObjectException("The referenced object is not valid.");
                reads.Add(id);
                return id;
            }
            var plan = new WallFinishRules.OriginalWalls(originals.Keys.ToArray().Select(ReadId));
            plan.Delete(ids =>
            {
                Assert.Equal(ids, reads);
                foreach (long id in ids) originals[id] = false;
                return ids;
            });
            Assert.Equal(4, plan.CountRemoved(id => originals[id]));
            Assert.Equal(4, reads.Count);
        }

        [Fact]
        public void Removing_originals_rolls_back_the_command_when_Revit_deletes_a_dependent()
        {
            var doc = new Document { Title = "Test", TestConfirmationStore = new ConfirmationStore() };
            Wall AddWall(Document owner, long id)
            {
                var wall = new Wall
                {
                    Id = new ElementId(id), Document = owner, LevelId = new ElementId(4), Width = 0.5,
                    Location = new LocationCurve { Curve = Line.CreateBound(new XYZ(0, 0, 0), new XYZ(4, 0, 0)) },
                    BoundingBox = new BoundingBoxXYZ { Min = new XYZ(0, -0.25, 0), Max = new XYZ(4, 0.25, 8) }
                };
                wall.BuiltInParams[BuiltInParameter.WALL_USER_HEIGHT_PARAM] = new Parameter { Value = 8.0 };
                owner.Elements.Add(id, wall);
                return wall;
            }

            Wall original = AddWall(doc, 1);
            var linked = new Document();
            AddWall(linked, 5);
            doc.Elements.Add(2, new RevitLinkInstance { Id = new ElementId(2), Linked = linked });
            doc.Elements.Add(3, new WallType { Id = new ElementId(3), Width = 0.05 });
            doc.Elements.Add(4, new Level { Id = new ElementId(4) });
            var dependent = new Element { Id = new ElementId(99) };
            doc.Elements.Add(99, dependent);
            int deleteCalls = 0;
            doc.TestWallFinishDeleting = ids =>
            {
                deleteCalls++;
                Assert.Equal(1, Assert.Single(ids).Value);
                Assert.NotNull(doc.GetElement(new ElementId(100)));
                ids.Add(dependent.Id);
            };

            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new WallFinishCommand();
            var request = JObject.Parse("{'operation':'create_finish_layers','target_document':'Test','wall_ids':[1],'link_instance_id':2," +
                "'interior_type_id':3,'original_wall':'delete_if_unhosted'}");
            CommandResult preview = command.Execute(app, request.ToString());
            Assert.True(preview.Success, preview.Error);
            request["confirmation_token"] = Assert.IsType<JObject>(preview.Data)["confirmation_token"];
            request["idempotency_key"] = "wall-finish-cascade";
            request["dry_run"] = false;

            CommandResult result = command.Execute(app, request.ToString());

            Assert.False(result.Success);
            Assert.Contains("99", result.Error);
            Assert.Equal(1, deleteCalls);
            Assert.Equal(1, doc.TransactionGroupRollbacks);
            Assert.False(doc.Assimilated);
            Assert.Equal("RolledBack", result.Detail.Value<string>("rollback_status"));
            Assert.Equal(false, result.Detail.Value<bool?>("changes_applied"));
            Assert.Same(original, doc.GetElement(new ElementId(1)));
            Assert.Same(dependent, doc.GetElement(new ElementId(99)));
            Assert.Null(doc.GetElement(new ElementId(100)));
        }


        [Theory]
        [InlineData("match_core")]
        [InlineData("create_finish_layers")]
        public void Wall_finish_native_plan_failures_cannot_be_classified_as_schema_errors(string operation)
        {
            var doc = new Document { Title = "Test" };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            string fields = operation == "match_core" ? "" : ",'interior_type_id':3,'original_wall':'delete_if_unhosted'";
            string args = "{'operation':'" + operation + "','target_document':'Test','wall_ids':[1],'link_instance_id':2" + fields + "}";
            CommandResult Run(string json) => ModelEditRunner.Run(app, json, "horizun_wall_finish", "Finish",
                WallFinishRules.Validate, (_, _, _) => throw new Autodesk.Revit.Exceptions.InvalidObjectException(
                    "The referenced object is not valid, possibly because it has been deleted."), readOperation: "match_core");

            var result = Run(args);
            Assert.Equal(ErrorCodes.ApiRevitExcepcion, ErrorCodes.Classify(result.Error, result.Detail).Code);
            Assert.Equal("InvalidObjectException", result.Detail.Value<string>(ErrorCodes.ExceptionTypeField));
            Assert.False(result.Detail.Value<bool>("write_started"));

            var invalid = Run(args.Replace("'wall_ids':[1]", "'wall_ids':[]"));
            Assert.Equal(ErrorCodes.EsquemaInvalido, ErrorCodes.Classify(invalid.Error, invalid.Detail).Code);
        }

        private static string ConversionSource()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, "src/Horizun.Revit/Commands/WallFinishCommand.Convert.cs"));
        }

        [Fact]
        public void Native_errors_roll_back_the_wall_before_the_failure_dialog_and_keep_structured_evidence()
        {
            string source = ConversionSource();
            Assert.Contains("FailureProcessingResult.ProceedWithRollBack", source);
            Assert.Contains("SetClearAfterRollback(true)", source);
            Assert.Contains("[\"native_failures\"]", source);
            Assert.Contains("GetFailingElementIds()", source);
            Assert.Contains("GetFailureDefinitionId()", source);
        }

        [Fact]
        public void Partial_core_coverage_is_refused_before_mutating_a_full_wall_or_its_hosts()
        {
            string source = ConversionSource();
            int coverage = source.IndexOf("WallFinishRules.ConversionCoverageRefusal(", StringComparison.Ordinal);
            int candidate = source.IndexOf("candidates.Add(c)", StringComparison.Ordinal);
            Assert.True(coverage >= 0 && coverage < candidate, "Coverage of the whole retained wall must be checked before it becomes a conversion candidate.");
            Assert.Contains("[\"reason_code\"]", source);
            Assert.Contains("[\"stage\"]", source);
            Assert.Contains("[\"rollback_verified\"]", source);
        }

        [Theory]
        [InlineData(0, 4, 0, 3, 0, 3, null)]
        [InlineData(0, 4, 0, 3, -1, 4, null)]
        [InlineData(0, 3, 0, 3, 0, 3, "core_partial_span")]
        [InlineData(1, 4, 0, 3, 0, 3, "core_partial_span")]
        [InlineData(0, 4, 0, 3, 0.1, 3, "core_partial_height")]
        [InlineData(0, 4, 0, 3, 0, 2.9, "core_partial_height")]
        [InlineData(0, 4, 0, 3, double.NaN, 3, "height_unreadable")]
        [InlineData(0, 4, 0, 3, 3, 3, "height_unreadable")]
        [InlineData(0, 0, 0, 3, 0, 3, "core_coverage_unreadable")]
        public void Whole_wall_conversion_requires_measured_core_coverage_of_its_entire_span_and_height(
            double from, double to, double bottom, double top, double coreBottom, double coreTop, string expected)
        {
            Assert.Equal(expected, WallFinishRules.ConversionCoverageRefusal(4, from, to, bottom, top, coreBottom, coreTop, 0.0001));
        }

        [Fact]
        public void Sub_tolerance_face_noise_does_not_require_a_split_but_real_uncovered_length_does()
        {
            const double tolerance = 0.1 / 304.8;
            Assert.Null(WallFinishRules.ConversionCoverageRefusal(4, tolerance / 2, 4 - tolerance / 2,
                0, 3, tolerance / 2, 3 - tolerance / 2, tolerance));
            Assert.Equal("core_partial_span", WallFinishRules.ConversionCoverageRefusal(4, tolerance * 2, 4,
                0, 3, 0, 3, tolerance));
        }

        [Fact]
        public void A_matched_core_and_a_valid_full_wall_insert_do_not_authorize_converting_the_uncovered_tail()
        {
            var core = new CoreSegment { Id = 7, A = new[] { 0.0, 0.0 }, B = new[] { 3.0, 0.0 }, ZMin = 0, ZMax = 3 };
            CoreMatch match = WallFinishRules.Match(new[] { 0.0, 0.0 }, new[] { 4.0, 0.0 }, new[] { 0.0, 1.0 },
                new[] { core }, 0.5, 0.6, 0, 3);
            Assert.Null(match.Refusal);
            Assert.Equal(0.75, match.Ratio);
            // The opening fits the existing host but crosses the end of its core.
            Assert.True(WallFinishRules.OpeningFits(3, 1, 4, 0, 2, 0, 3));
            Assert.Equal("core_partial_span", WallFinishRules.ConversionCoverageRefusal(4, match.From, match.To,
                0, 3, core.ZMin, core.ZMax, 0.1 / 304.8));
        }
    }
}
