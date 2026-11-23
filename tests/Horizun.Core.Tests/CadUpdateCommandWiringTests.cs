// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// PLACEMENT IDENTITY, GUARDED IN SOURCE where it cannot be executed.
//
// The decisions live in CadPlacementRules and CadUpdateRules and are exercised
// directly by CadPlacementScopeTests. What those tests cannot see is whether the
// two Revit-bound commands actually CALL them, read the new arguments, and put
// the results in the reply - and that seam is exactly where the geometry_id
// defect lived: the apply read a field the plan never emitted, so every element
// an incremental run created was stamped with GeometryId null, and no test could
// fail because the rules were right and the wiring was not.
//
// So these guards read the command sources and assert the seams. A guard on
// text is a weak test; it is the strongest one available for a file that needs
// a UIApplication to run, and it is the pattern this repository uses for that.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadUpdateCommandWiringTests
    {
        private static DirectoryInfo Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d;
        }

        private static string Source(params string[] parts)
        {
            return File.ReadAllText(Path.Combine(Root().FullName, Path.Combine(parts)));
        }

        private static string Plan() => Source("src", "Horizun.Revit", "Commands", "PlanCadUpdateCommand.cs");
        private static string Apply() => Source("src", "Horizun.Revit", "Commands", "ApplyCadUpdateCommand.cs");
        private static string Store() => Source("src", "Horizun.Revit", "Core", "CadProvenanceStore.cs");
        private static string Contract() => Source("src", "Horizun.Contracts", "Contract.cs");

        [Fact]
        public void A_placed_DWG_is_sampled_by_its_total_transform_so_a_move_can_be_verified()
        {
            // Measured 2026-09-03: the typed move refused an ImportInstance as
            // unsampleable, and an incremental update cannot detect a placement it
            // cannot move under test. Instance covers ImportInstance and links alike.
            string src = Source("src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs");
            Assert.Contains("var instance = e as Instance;", src);
            Assert.Contains("instance.GetTotalTransform()", src);
        }

        [Fact]
        public void A_hosted_instance_keeps_its_host_or_the_transform_is_rolled_back_and_a_turn_turns_it()
        {
            // MEASURED on a face-hosted receptacle: a 300 mm move along its wall left
            // the face, Revit kept the device with NO host, and the point check
            // verified the move. And a turn about an axis through the element's own
            // point moves no point, so the point check alone verifies any turn.
            string src = Source("src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs");
            int apply = src.IndexOf("foreach (Plan p in plans) { reached = p.Index; Apply(doc, p); }", StringComparison.Ordinal);
            int guard = src.IndexOf("foreach (Plan p in plans) { reached = p.Index; GuardHosts(doc, p); }", StringComparison.Ordinal);
            int commit = src.IndexOf("Guard.Commit(tx, txName);", StringComparison.Ordinal);
            Assert.True(apply > 0 && guard > apply && commit > guard, "the host guard runs after the change and before the commit");
            Assert.Contains("\"host_changed: \" + p.Operation", src);
            Assert.Contains("double? off = OffItsFaceMm(element);", src);
            Assert.Contains("private const double FaceToleranceMm = 0.5;", src);
            Assert.Contains("if (host.HasValue) p.HostBefore[raw] = host.Value;", src);
            Assert.Contains("if (op == \"rotate\" || op == \"mirror\") p.Axes[raw] = Axes(element);", src);
            Assert.Contains("case \"mirror\": ElementTransformUtils.MirrorElements(doc, p.Ids, p.MirrorPlane, false); break;", src);
            Assert.Contains("p.Rotation.OfVector(axesBefore[1]).IsAlmostEqualTo(axesAfter[1], 1e-6)", src);
            Assert.Contains("detail[\"orientation_not_turned\"] = why;", src);
            Assert.Contains("rolled back whole with host_changed", Contract());
        }

        // ------------------------------------------------------- geometry_id

        // ------------------------------------------------------- scope

        // ------------------------------------------------------- transform

        // ------------------------------------------------------- migration

        // ------------------------------------------------------- retries

        // ------------------------------------------------------- contract

    }
}
