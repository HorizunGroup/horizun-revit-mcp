using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CropShapeAndImportReferenceTests
    {
        [Theory]
        [InlineData("[[0,0],[400,0],[200,100],[0,400],[0,0]]", null)]
        [InlineData("[[0,0],[400,0],[400,400],[0,400]]", "close")]
        [InlineData("[[0,0],[400,400],[0,400],[400,0],[0,0]]", "self_intersection")]
        [InlineData("[[0,0],[400,0],[200,0],[0,400],[0,0]]", "self_intersection")]
        [InlineData("[[0,0],[400,0],[400,0],[0,400],[0,0]]", "degenerate")]
        public void PolygonRequiresExplicitClosureAndASimpleBoundary(string json, string expectedError)
        {
            string error = ViewCropGeometry.ReadPolygon(JArray.Parse(json), out double[][] polygon);
            if (expectedError == null)
            {
                Assert.Null(error);
                Assert.Equal(4, polygon.Length);
            }
            else
            {
                Assert.Contains(expectedError, error);
                Assert.Null(polygon);
            }
        }

        [Fact]
        public void PolygonValidationBoundsTheWorkBeforeInspectingVertices()
        {
            var boundary = new JArray();
            int count = ViewCropGeometry.MaxPolygonVertices;
            for (int i = 0; i < count; i++)
            {
                double angle = 2 * Math.PI * i / count;
                boundary.Add(new JArray(1000 * Math.Cos(angle), 1000 * Math.Sin(angle)));
            }
            boundary.Add(boundary[0].DeepClone());
            Assert.Null(ViewCropGeometry.ReadPolygon(boundary, out double[][] polygon));
            Assert.Equal(count, polygon.Length);

            boundary.Add(JValue.CreateNull());
            Assert.Contains("at most " + count, ViewCropGeometry.ReadPolygon(boundary, out polygon));
            Assert.Null(polygon);
        }

        [Theory]
        [InlineData(true, false, true, true)]
        [InlineData(false, false, true, false)]
        [InlineData(true, true, true, false)]
        [InlineData(true, false, false, false)]
        public void MatchingRectangleVerticesStillRequireAnActiveUnsplitSketch(bool shapeSet, bool split, bool active, bool verified)
        {
            double[][] rectangle = { new[] { 0d, 0d }, new[] { 4d, 0d }, new[] { 4d, 3d }, new[] { 0d, 3d } };
            Assert.Equal(verified, ViewCropGeometry.VerifyPolygon(rectangle, rectangle, shapeSet, split, active).AllVerified);
        }

        [Fact]
        public void CleanupRequiresExactlyTheNewDimensionIds()
        {
            var generated = new long[] { 91, 92 };
            PlanimetryFixRules.RequireCropCleanupMatches(generated, new long[] { 92, 91 });
            var cascade = Assert.Throws<InvalidOperationException>(() =>
                PlanimetryFixRules.RequireCropCleanupMatches(generated, new long[] { 91, 92, 50 }));
            Assert.Contains("Unexpected deleted ids: [50]", cascade.Message);

            var incomplete = Assert.Throws<InvalidOperationException>(() =>
                PlanimetryFixRules.RequireCropCleanupMatches(generated, new long[] { 91 }));
            Assert.Contains("Generated ids not deleted: [92]", incomplete.Message);
        }

        [Fact]
        public void ReadbackAllowsWindingAndStartChangesButChecksEdgeOrderAndTolerance()
        {
            double[] a = { 0, 0 }, b = { 400, 0 }, c = { 200, 100 }, d = { 0, 400 };
            double[][] requested = { a, b, c, d };
            Assert.True(ViewCropGeometry.PolygonMatches(requested, new[] { c, b, a, d }, 1));
            Assert.False(ViewCropGeometry.PolygonMatches(requested, new[] { c, a, b, d }, 1));
            Assert.False(ViewCropGeometry.PolygonMatches(requested, new[] { a, b, new[] { 202d, 100d }, d }, 1));
        }

        [Fact]
        public void OnlyConfirmedImportReferencesQualifyAndTheirIndicesSurvive()
        {
            var references = JArray.Parse(@"[
                {'index':0,'is_import':false},
                {'index':1,'is_import':true,'linked':false},
                {'index':2,'is_import':null,'linked':true},
                {'index':3,'is_import':true,'linked':true}
            ]");
            Assert.True(DimensionImportReferences.IncludesDimension(true, references, false, out JArray indices, out string reason));
            Assert.Equal(new[] { 1, 3 }, indices.Values<int>());
            Assert.Null(reason);
            references[1]["is_import"] = false;
            references[3]["is_import"] = false;
            Assert.False(DimensionImportReferences.IncludesDimension(true, references, true, out indices, out reason));
            Assert.Empty(indices);
            Assert.NotNull(reason);
        }

        [Fact]
        public void AnUnreadableLinkKeepsTheLegacyBooleanAndOnlyQualifiesWithoutFiltering()
        {
            JObject reference = DimensionImportReferences.ReferenceRow(0, null);
            Assert.Equal(JTokenType.Boolean, reference["linked"].Type);
            Assert.False(reference.Value<bool>("linked"));
            Assert.Equal(JTokenType.Null, reference["is_import"].Type);
            var references = new JArray(reference);
            Assert.True(DimensionImportReferences.IncludesDimension(false, references, true, out JArray indices, out string reason));
            Assert.Empty(indices);
            Assert.Null(reason);
            Assert.False(DimensionImportReferences.IncludesDimension(true, references, true, out indices, out reason));
            Assert.NotNull(reason);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(null, true)]
        public void EmptyReferenceArrayCanOnlyExcludeAReadableDimension(bool? available, bool unreadable)
        {
            Assert.False(DimensionImportReferences.IncludesDimension(true, new JArray(), available, out JArray indices, out string reason));
            Assert.Empty(indices);
            Assert.Equal(unreadable, reason != null);
        }
    }
}
