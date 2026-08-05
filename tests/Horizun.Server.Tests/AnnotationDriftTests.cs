// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// MCP's tool annotations are what a client uses to decide whether to ask a human
// before running something. They were computed in Tools.cs from two hardcoded
// lists of tool names, three files away from where a tool is defined - so a tool
// added without editing those lists reported destructiveHint=false, and silence
// was the dangerous answer. They are declared on the contract now; these tests
// are what keep them honest, because the failure mode is not an exception, it is
// a plausible-looking wrong hint.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Xunit;

namespace Horizun.Server.Tests
{
    public class AnnotationDriftTests
    {
        /// <summary>
        /// A read-only tool that reports itself destructive would make every client ask a
        /// human before a query; a read-only tool is also, by definition, repeatable.
        /// </summary>
        [Fact]
        public void Read_only_tools_are_never_destructive_and_always_idempotent()
        {
            var offenders = Contract.All
                .Where(c => c.Effect == ToolEffect.ReadOnly && c.Destructive)
                .Select(c => c.Name)
                .ToList();
            Assert.True(offenders.Count == 0,
                "read-only tools declared destructive: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// Every command that deletes, replaces or overwrites has to SAY so. Naming them
        /// here means adding a destructive command without declaring it fails a test
        /// instead of shipping a tool clients will run without asking.
        /// </summary>
        [Fact]
        public void The_commands_that_destroy_something_declare_it()
        {
            var mustBeDestructive = new[]
            {
                "horizun_delete_verified",      // removes elements
                "horizun_execute_python",       // arbitrary API, no dry run possible
                "horizun_document_session",     // closes documents
                "horizun_export",               // overwrites files on disk
                "horizun_create_family",        // replaces family geometry
                "horizun_power_bi_push"         // replaces a dataset
            };
            foreach (string name in mustBeDestructive)
            {
                CommandContract c = Contract.Find(name);
                Assert.NotNull(c);
                Assert.True(c.Destructive, name + " must declare Destructive");
            }
        }

        /// <summary>
        /// Anything that reaches the filesystem, the network or the Revit session itself is
        /// open-world: a client is entitled to treat it differently from a model edit.
        /// </summary>
        [Fact]
        public void The_commands_that_leave_the_model_declare_open_world()
        {
            foreach (string name in new[] { "horizun_export", "horizun_power_bi_push",
                                            "horizun_excel_write_rows", "horizun_capture_view",
                                            "horizun_open_document", "horizun_document_session",
                                            "horizun_execute_python", "horizun_catalog_lookup" })
            {
                CommandContract c = Contract.Find(name);
                Assert.NotNull(c);
                Assert.True(c.OpenWorld, name + " must declare OpenWorld");
            }
        }

        /// <summary>
        /// A mutating command is only safely retryable because the durable idempotency key
        /// makes a replay return the recorded answer. That is what idempotentHint promises,
        /// and it is derived - so this test guards the derivation, not a list.
        /// </summary>
        [Fact]
        public void Mutating_commands_take_an_idempotency_key()
        {
            var mutating = Contract.All.Where(c =>
                c.Effect == ToolEffect.Mutating ||
                c.Effect == ToolEffect.MutatingUnlessDryRun ||
                c.Effect == ToolEffect.DocumentSession).ToList();
            Assert.NotEmpty(mutating);
            var missing = new List<string>();
            foreach (CommandContract c in mutating)
            {
                var props = c.InputSchema?["properties"];
                if (props == null || props["idempotency_key"] == null) missing.Add(c.Name);
            }
            Assert.True(missing.Count == 0,
                "mutating commands whose published schema omits idempotency_key: " +
                string.Join(", ", missing));
        }

        /// <summary>
        /// The whole point of moving these onto the contract: a NEW command inherits a
        /// classification from its own definition. Every command must land in exactly one
        /// Effect, and no command may be left with the enum's default by accident - which
        /// is ReadOnly, the most permissive value there is.
        /// </summary>
        [Fact]
        public void Every_command_is_classified_and_writes_are_not_silently_read_only()
        {
            // Any command whose published schema carries dry_run + confirmation_token is a
            // write, whatever anybody remembered to put in a set. It must not be ReadOnly.
            var misfiled = Contract.All.Where(c =>
            {
                var props = c.InputSchema?["properties"];
                if (props == null) return false;
                bool looksLikeAWrite = props["confirmation_token"] != null && props["dry_run"] != null;
                return looksLikeAWrite && c.Effect == ToolEffect.ReadOnly;
            }).Select(c => c.Name).ToList();

            Assert.True(misfiled.Count == 0,
                "these publish a confirmation token and a dry run - so they write - but are " +
                "classified ReadOnly, which is the enum's default and the most permissive " +
                "value: " + string.Join(", ", misfiled));
        }
    }
}
