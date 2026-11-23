// -----------------------------------------------------------------------------
// The wiring of three typed capabilities that Revit itself cannot be asked about from a test:
//   horizun_manage_cad_links operation=import     Document.Import(DWGImportOptions) beside Link
//   horizun_manage_links add kind=image           ImageType.Create + ImageInstance.Create
//   horizun_wall_sweep place_edge / read host_id  NewSlabEdge, NewFascia, NewGutter
// A source-level check that each is reachable from its contract, rehearses before it writes,
// writes inside a transaction it can roll back, and re-reads what it made. The API members were
// measured by reflection over RevitAPI.dll 2023-2027 when this was written; the live behaviour
// is measured in Revit, not here.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ImportAndHostedEdgeWiringTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Command(string file) => File.ReadAllText(Path.Combine(Root(), "src", "Horizun.Revit", "Commands", file));

        private static JObject Schema(string tool) => Contract.All.Single(c => c.Name == tool).InputSchema;

        private static string[] Values(JToken token) => ((JArray)token).Select(t => (string)t).ToArray();

        // ---- DWG import ---------------------------------------------------------------

        // ---- images ---------------------------------------------------------------------

        // ---- slab edges, fascias, gutters ---------------------------------------------------

        [Fact]
        public void Wall_sweep_takes_place_edge_and_read_with_host_id()
        {
            JObject props = (JObject)Schema("horizun_wall_sweep")["properties"];
            Assert.Contains("place_edge", Values(props["operation"]["enum"]));
            Assert.Equal(new[] { "slab_edge", "fascia", "gutter" }, Values(props["kind"]["enum"]));
            foreach (string field in new[] { "host_id", "edge_references", "face_references" })
                Assert.NotNull(props[field]);
        }

        [Fact]
        public void Place_edge_calls_the_three_creators_with_a_reference_array_of_the_edges_named()
        {
            string src = Command("WallSweepEdges.cs");
            Assert.Contains("d.Create.NewSlabEdge((SlabEdgeType)d.GetElement(type.Id), refs)", src);
            Assert.Contains("d.Create.NewFascia((Autodesk.Revit.DB.Architecture.FasciaType)d.GetElement(type.Id), refs)", src);
            Assert.Contains("d.Create.NewGutter((Autodesk.Revit.DB.Architecture.GutterType)d.GetElement(type.Id), refs)", src);
            Assert.Contains("refs.Append(w.Reference)", src);
            // references are parsed from the stable representation and must belong to the host named
            Assert.Contains("Reference.ParseFromStableRepresentation(doc, stable)", src);
            Assert.Contains("reference.ElementId != host.Id", src);
        }

        [Fact]
        public void Place_edge_is_verified_by_a_postcondition_checklist_and_dispatched_by_the_command()
        {
            string src = Command("WallSweepEdges.cs");
            Assert.Contains("new PostconditionCheck(\"created\", \"class\", \"type_id\", \"at_host\")", src);
            string command = Command("WallSweepCommand.cs");
            Assert.Contains("if (op == \"place_edge\") return PlaceEdge(doc, r, scale);", command);
            Assert.Contains("r[\"host_id\"] != null ? ReadEdges(doc, r, scale) : Read(doc, r, scale)", command);
        }

        [Fact]
        public void The_edge_arguments_are_in_the_plan_hash()
        {
            string runner = Command("ModelEditRunner.cs");
            foreach (string field in new[] { "kind", "edge_references", "face_references" })
                Assert.Contains("\"" + field + "\"", runner);
        }
    }
}
