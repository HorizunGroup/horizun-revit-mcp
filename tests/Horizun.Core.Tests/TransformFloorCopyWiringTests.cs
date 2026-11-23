// The Revit API requires a running Revit host for geometric integration tests.
// These checks pin the offline command wiring; multi-year compilation covers API signatures.
using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TransformFloorCopyWiringTests
    {
        private static string Source()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string path = Path.Combine(directory.FullName, "src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs");
                if (File.Exists(path)) return File.ReadAllText(path);
                directory = directory.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        [Fact]
        public void Floor_copy_has_a_sketch_based_plan_and_committed_readback()
        {
            string source = Source();
            Assert.Contains("p.FloorCopies[raw] = FloorCopySnapshot.Read(doc, floor)", source);
            Assert.Contains("FloorCopySnapshot.Matches(doc, floorBefore, e as Floor, p.Vector)", source);
            Assert.Contains("Sketch sketch = floor.SketchId", source);
            Assert.Contains("floor_copies_verified", source);
        }

        [Fact]
        public void Floor_copy_checks_holes_elevation_and_type()
        {
            string source = Source();
            Assert.Contains("SameLoops(source.Loops, target.Loops, vector)", source);
            Assert.Contains("SameLoop(loop, candidate, vector)", source);
            Assert.Contains("hosted_openings = openings.Select(FingerprintLoops)", source);
            Assert.Contains("SameOpeningSets(source.HostedOpenings, target.HostedOpenings, vector)", source);
            Assert.Contains("(e as Opening)?.Host", source);
            Assert.Contains("target.AbsoluteElevation - source.AbsoluteElevation - vector.Z", source);
            Assert.Contains("copy.GetTypeId() != source.TypeId", source);
            Assert.Contains("{ \"floor_sketch\", p.FloorCopies.TryGetValue", source);
        }

        [Fact]
        public void Floor_copy_refuses_shapes_variable_thickness_and_slope_before_copying()
        {
            string source = Source();
            Assert.Contains("p.FloorCopies[raw] = FloorCopySnapshot.Read(doc, floor)", source);
            Assert.Contains("GuardSimpleFloor(doc, floor);", source);
            Assert.Contains("shape.IsEnabled", source);
            Assert.Contains("structure.VariableLayerIndex >= 0", source);
            Assert.Contains("HostObjectUtils.GetTopFaces(floor)", source);
            Assert.Contains("face.FaceNormal.DotProduct(XYZ.BasisZ) < 1 - 1e-6", source);
        }

        [Fact]
        public void Failed_floor_copy_reports_each_elevation_and_sketch_read()
        {
            string source = Source();
            Assert.Contains("detail[\"floor_copy_diagnostics\"] = floorDiagnostics", source);
            Assert.Contains("candidate_level_elevation_feet", source);
            Assert.Contains("candidate_offset_feet", source);
            Assert.Contains("candidate_absolute_elevation_feet", source);
            Assert.Contains("sketch_matches_full_vector", source);
            Assert.Contains("sketch_matches_xy_without_z_shift", source);
            Assert.Contains("candidate_sketch_z_range_feet", source);
            Assert.Contains("hosted_openings_match_full_vector", source);
        }

        [Fact]
        public void Local_sketch_plane_requires_translated_physical_body_and_absolute_elevation()
        {
            string source = Source();
            int elevation = source.IndexOf("Math.Abs(target.AbsoluteElevation - source.AbsoluteElevation - vector.Z) > 1e-5", StringComparison.Ordinal);
            int body = source.IndexOf("!PhysicalBodyMatches(source, target, vector)", elevation, StringComparison.Ordinal);
            int local = source.IndexOf("SameLoops(source.Loops, target.Loops, xyOnly)", body, StringComparison.Ordinal);
            Assert.True(elevation >= 0 && body > elevation && local > body);
            Assert.Contains("(source.PhysicalMin + vector).DistanceTo(target.PhysicalMin)", source);
            Assert.Contains("(source.PhysicalMax + vector).DistanceTo(target.PhysicalMax)", source);
            Assert.Contains("Math.Abs(source.SolidVolume - target.SolidVolume)", source);
        }
        [Fact]
        public void Level_transform_names_the_typed_elevation_route_before_sampling()
        {
            string source = Source();
            int guard = source.IndexOf("if (element is Level)", StringComparison.Ordinal);
            int sample = source.IndexOf("List<XYZ> sample = Samples(element)", guard, StringComparison.Ordinal);
            Assert.True(guard >= 0 && sample > guard);
            Assert.Contains("horizun_write_params_verified on parameter Elevation (include units)", source);
        }

        [Fact]
        public void Every_copy_is_verified_before_commit_and_names_the_api_return_mapping()
        {
            string source = Source();
            Assert.Contains("plans.Any(p => p.Operation == \"copy\")", source);
            Assert.Contains("detail[\"source_to_copy\"] = sourceToCopy", source);
            Assert.Contains("CopyRepresentationRules.Translate(p.CopyGeometry", source);
            Assert.Contains("FirstZ(target.Loops) - FirstZ(source.Loops)", source);
            Assert.Contains("Guard.RollBack(copyGroup)", source);
            Assert.Contains("Guard.Assimilate(copyGroup", source);
        }

    }
}
