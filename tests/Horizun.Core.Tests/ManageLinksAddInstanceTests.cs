// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE DEAD END. horizun_manage_links operation=add refuses a path already linked
// and tells the caller what to do instead: "place another instance of that type,
// or change_path it." There was no operation that placed another instance. The
// advice named a capability the command did not have, so the only way to reach a
// perfectly ordinary Revit state - one linked file placed twice - was arbitrary
// Python, which is off by default and verifies nothing.
//
// It was found by trying to BUILD the fixture for the linked-takeoff harness: a
// takeoff reports one row per element per placement, told apart by
// link_instance_id, and nothing typed could create the second placement to prove
// it. A capability whose evidence cannot be produced is a claim.
//
// These are contract and wiring facts. Whether Revit places the instance is a
// live question and is answered by scripts/live/verify-quantities-budget.ps1.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class ManageLinksAddInstanceTests
    {
        private static CommandContract Links() => Contract.Find("horizun_manage_links");

        private static string Source()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", "ManageLinksCommand.cs"));
        }

        [Fact]
        public void The_published_operations_include_placing_another_instance()
        {
            JToken operations = Links().InputSchema["properties"]["operation"]["enum"];
            Assert.Contains("add_instance", operations.Select(t => (string)t));
        }

        /// <summary>
        /// The advice `add` gives and the operation that carries it out must exist in the
        /// same build. This is the pair that was broken: one shipped, the other did not.
        /// </summary>
        [Fact]
        public void The_refusal_that_sends_a_caller_to_another_instance_has_somewhere_to_send_them()
        {
            string src = Source();
            Assert.Contains("place another instance of that type", src);
            Assert.Contains("if (operation == \"add_instance\") return AddInstance(app, request);", src);
        }

        [Fact]
        public void The_description_says_what_add_instance_does_and_what_it_re_reads()
        {
            string description = Links().Description;
            Assert.Contains("add_instance", description);
            Assert.Contains("belongs to the type asked for", description);
        }

        /// <summary>
        /// The contract this bridge does not bend: a write is re-read from the model. Both
        /// halves - the instance is there, and it is an instance of the type that was
        /// asked for. A placement of the WRONG link is worse than a failure, because it
        /// looks like success.
        /// </summary>
        [Fact]
        public void The_placement_is_verified_by_re_reading_the_instance_and_its_type()
        {
            string src = Source();
            int create = src.IndexOf("RevitLinkInstance.Create(doc, type.Id)", StringComparison.Ordinal);
            int reread = src.IndexOf("var reread = doc.GetElement(created.Id) as RevitLinkInstance;", StringComparison.Ordinal);
            int ofType = src.IndexOf("reread.GetTypeId() == type.Id", StringComparison.Ordinal);
            Assert.True(create > 0 && reread > create && ofType > create,
                "the instance must be created, then re-read, then checked to belong to the type asked for");
            Assert.Contains("after.Count != before.Count + 1", src);
            Assert.Contains("Success is not claimed.", src);
        }

        /// <summary>
        /// An unloaded link has no elements, so an instance of it is a placement of
        /// nothing. Refused by name rather than created and then explained.
        /// </summary>
        [Fact]
        public void An_instance_of_a_link_that_is_not_loaded_is_refused()
        {
            string src = Source();
            Assert.Contains("An instance of a \" +\n                    \"link that is not loaded would be a placement of nothing", src.Replace("\r\n", "\n"));
        }

        /// <summary>
        /// It is a mutation, so it goes through the gate, the plan hash and the token,
        /// like every other write on this command.
        /// </summary>
        [Fact]
        public void It_rehearses_through_the_document_gate_and_spends_a_token()
        {
            string src = Source();
            int at = src.IndexOf("private static CommandResult AddInstance", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at);
            Assert.Contains("DocumentGate.ForMutation(app, request, \"horizun_manage_links\")", body);
            Assert.Contains("DocumentGate.PlanHash(request, \"operation\", \"link_type_id\")", body);
            Assert.Contains("DocumentGate.StampConfirmation(preview, gate, \"horizun_manage_links\", hash, true)", body);
            Assert.Contains("DocumentGate.RequireConfirmation(app, gate, request, \"horizun_manage_links\", hash)", body);
            // And the dry run says which kind of rehearsal it was, as the rest of this
            // command does: creating an instance and rolling it back would BE the write
            // whose count the token binds.
            Assert.Contains("[\"mode\"] = \"measured_preview\"", body);
        }
    }
}
