// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE ONE COMMAND THAT WAS OUTSIDE THE POLICY.
//
// "Every mutation validates the document" was recorded as met, on evidence from
// seven typed commands. execute_python was not one of them - and it is the
// command that can do everything the other seven can, plus everything they
// cannot. It took whatever document happened to be in front. So the sentence was
// true of the commands that were checked and false of the surface as a whole,
// which is the more expensive kind of wrong: it reads as a guarantee.
//
// These read the shipped source, the same technique as ConfirmationRoundTripTests
// and for the same reason: the handler needs a UIApplication, so the only way to
// assert this without a Revit in the room is over the code that deploys. A test
// that cannot fail on the real defect is decoration.
//
// ORDER IS ASSERTED, not just presence. A gate that runs after the script has
// already been queued is not a gate.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExecutePythonGateTests
    {
        private static string CommandsDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate src/Horizun.Revit/Commands from " + AppContext.BaseDirectory);
            return Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands");
        }

        private static string Source(string file) => File.ReadAllText(Path.Combine(CommandsDir(), file));

        private const string File_ = "ExecutePythonCommand.cs";

        [Fact]
        public void It_resolves_its_document_through_the_shared_gate()
        {
            string text = Source(File_);
            Assert.Contains("DocumentGate.ForMutation", text);
        }

        [Fact]
        public void The_gate_runs_before_the_script_does()
        {
            string text = Source(File_);

            int gate = text.IndexOf("DocumentGate.ForMutation", StringComparison.Ordinal);
            // Where the caller's Python is actually handed to the engine.
            int run = text.IndexOf("source.Execute(scope)", StringComparison.Ordinal);

            Assert.True(gate >= 0, "the gate must be called");
            Assert.True(run >= 0, "the script execution site moved; this test needs updating");
            Assert.True(gate < run,
                "the document gate must run BEFORE the caller's script. A check that happens afterwards has " +
                "already let an arbitrary write land in whatever document was in front.");
        }

        [Fact]
        public void The_gate_runs_before_anything_is_queued()
        {
            string text = Source(File_);

            int gate = text.IndexOf("DocumentGate.ForMutation", StringComparison.Ordinal);
            int queue = text.IndexOf("AsyncQueue.TryAdd", StringComparison.Ordinal);

            // Asserted explicitly, because IndexOf returns -1 for absent and -1 is less
            // than every real offset. Without this line the test PASSES when the gate
            // has been deleted outright, which is the one case it exists to catch -
            // measured by running it against the previous commit, where it passed.
            Assert.True(gate >= 0, "the gate must be called at all");
            Assert.True(queue >= 0, "the queueing site moved; this test needs updating");
            Assert.True(gate < queue,
                "the document gate must run BEFORE the script is queued. Queued work is claimed and run with " +
                "nobody waiting, so a gate after the queue never stops anything.");
        }

        [Fact]
        public void A_refused_gate_returns_instead_of_carrying_on()
        {
            string text = Source(File_);
            // The gate hands back a refusal rather than throwing, so a handler that
            // ignores it compiles perfectly and runs the script anyway.
            Assert.Contains("if (!gate.Ok) return gate.Refusal;", text);
        }

        [Fact]
        public void Run_async_demands_an_idempotency_key_before_it_queues()
        {
            string text = Source(File_);

            int demand = text.IndexOf("string.IsNullOrWhiteSpace(idempotencyKey)", StringComparison.Ordinal);
            int queue = text.IndexOf("AsyncQueue.TryAdd", StringComparison.Ordinal);

            Assert.True(demand >= 0, "run_async must require an idempotency_key");
            Assert.True(demand < queue,
                "the key must be demanded BEFORE the work is queued, or the first request of a retry pair is " +
                "already on the queue by the time anything notices.");
        }

        [Fact]
        public void The_claim_is_taken_before_the_work_is_queued()
        {
            string text = Source(File_);

            int claim = text.IndexOf("AsyncClaims.Claim", StringComparison.Ordinal);
            int queue = text.IndexOf("AsyncQueue.TryAdd", StringComparison.Ordinal);

            Assert.True(claim >= 0, "the ledger must be consulted");
            Assert.True(claim < queue,
                "the key must be claimed BEFORE the entry is queued. The other order queues the work and then " +
                "discovers it was a duplicate, which is precisely the second execution being prevented.");
        }

        /// <summary>
        /// The queued copy carries its own target. Without it the deferred run reaches
        /// the gate with no target_document and refuses itself - a job that can never
        /// succeed, discovered only by reading its record.
        /// </summary>
        [Fact]
        public void The_queued_copy_carries_the_target_document()
        {
            string text = Source(File_);
            int queuedObject = text.IndexOf("var queued = new JObject", StringComparison.Ordinal);
            Assert.True(queuedObject >= 0, "the queued parameter object moved; this test needs updating");

            string tail = text.Substring(queuedObject, Math.Min(700, text.Length - queuedObject));
            Assert.Contains("[\"target_document\"]", tail);
            Assert.Contains("[\"run_async\"] = false", tail);
        }

        /// <summary>
        /// The universal dispatcher now stores synchronous results durably, so the old
        /// command-local refusal would make synchronous Python impossible: admission
        /// requires the key and the command would then reject it.
        /// </summary>
        [Fact]
        public void Synchronous_python_uses_the_dispatchers_durable_key_instead_of_rejecting_it()
        {
            string text = Source(File_);
            Assert.DoesNotContain("'idempotency_key' was supplied without run_async=true", text);

            string dispatcher = File.ReadAllText(Path.GetFullPath(
                Path.Combine(CommandsDir(), "..", "Core", "Dispatcher.cs")));
            int claim = dispatcher.IndexOf("_idempotency.Claim", StringComparison.Ordinal);
            int execute = dispatcher.IndexOf("cmd.Execute(app, req.ParamsJson)", StringComparison.Ordinal);
            Assert.True(claim >= 0 && execute >= 0 && claim < execute,
                "the durable claim must be established before any command, including Python, runs");
        }

        /// <summary>
        /// The surface must stop CLAIMING a policy it does not satisfy. execute_python
        /// now passes the document gate, and it still has no dry run, no plan and no
        /// confirmation token - so it is a privileged bypass that is document-scoped,
        /// which is a different sentence from "it complies".
        /// </summary>
        [Fact]
        public void The_description_states_that_it_is_still_a_privileged_bypass()
        {
            string text = Source(File_);
            int desc = text.IndexOf("public string Description", StringComparison.Ordinal);
            Assert.True(desc >= 0);
            string description = text.Substring(desc, Math.Min(2600, text.Length - desc));

            Assert.Contains("target_document", description);
            Assert.Contains("idempotency_key", description);
            // Named as a risk in the tool's own description, where a caller reads it,
            // not only in a document nobody opens.
            Assert.True(description.Contains("PRIVILEGED BYPASS") || description.Contains("privileged bypass"),
                "the description must say it is still a privileged bypass");
        }

        /// <summary>
        /// The claim in reverse: no OTHER command may quietly become a second way in.
        /// Anything that opens a Revit Transaction is a mutation and must be
        /// document-gated - which is what "one mutation policy" has to mean to be worth
        /// asserting at all.
        /// </summary>
        [Fact]
        public void Every_command_that_opens_a_transaction_is_document_gated()
        {
            var ungated = new List<string>();

            foreach (string path in Directory.EnumerateFiles(CommandsDir(), "*Command.cs"))
            {
                string text = File.ReadAllText(path);
                bool opensTransaction = text.Contains("new Transaction(") || text.Contains("new TransactionGroup(");
                if (!opensTransaction) continue;

                bool gated = text.Contains("DocumentGate.ForMutation");
                if (!gated) ungated.Add(Path.GetFileName(path));
            }

            Assert.True(ungated.Count == 0,
                "These commands open a Revit transaction without resolving their document through " +
                "DocumentGate.ForMutation, so they write to whatever window is in front: " +
                string.Join(", ", ungated));
        }

        /// <summary>
        /// A caller that reaches for execute_python to do exactly what a typed command
        /// already performs and verifies gets redirected, not a silent unverified
        /// duplicate. Reported case: a script calling ViewSheet.Create instead of
        /// horizun_manage_views(create_sheet).
        /// </summary>
        [Fact]
        public void Typed_overlaps_are_refused_before_the_gate_and_before_queueing()
        {
            string text = Source(File_);

            int overlapCheck = text.IndexOf("RefuseIfTypedOverlap(code)", StringComparison.Ordinal);
            int gate = text.IndexOf("DocumentGate.ForMutation", StringComparison.Ordinal);
            int queue = text.IndexOf("AsyncQueue.TryAdd", StringComparison.Ordinal);

            Assert.True(overlapCheck >= 0, "the typed-overlap check must be called");
            Assert.True(overlapCheck < gate,
                "the overlap check must run BEFORE the document gate - a script redirected to a typed command " +
                "should never reach far enough to touch a document at all.");
            Assert.True(overlapCheck < queue,
                "the overlap check must run BEFORE anything is queued, or run_async=true bypasses it.");
        }

        [Theory]
        [InlineData("ViewSheet", "Create", "horizun_manage_views")]
        [InlineData("Viewport", "Create", "horizun_manage_views")]
        [InlineData("ScheduleSheetInstance", "Create", "horizun_manage_views")]
        [InlineData("ViewSchedule", "CreateSchedule", "horizun_create_schedule")]
        [InlineData("ElementTransformUtils", "Move", "horizun_transform_elements")]
        [InlineData("ElementTransformUtils", "Rotate", "horizun_transform_elements")]
        [InlineData("ElementTransformUtils", "Mirror", "horizun_transform_elements")]
        [InlineData("doc", "Delete", "horizun_delete_verified")]
        public void Each_typed_overlap_names_its_replacement_command(string apiType, string apiMethod, string typedCommand)
        {
            string text = Source(File_);
            int table = text.IndexOf("TypedOverlaps =", StringComparison.Ordinal);
            Assert.True(table >= 0, "the typed-overlap table moved; this test needs updating");

            // Every entry lives between the table and the refusal method that reads it.
            int helperEnd = text.IndexOf("private static CommandResult RefuseIfTypedOverlap", StringComparison.Ordinal);
            string tableText = text.Substring(table, helperEnd - table);

            // The table stores REGEX SOURCE (backslashes and all), not the API call
            // itself - so this checks for the type/method names appearing near each
            // other rather than matching the regex literally against its own source.
            Assert.Contains(apiType, tableText);
            Assert.Contains(apiMethod, tableText);
            Assert.Contains(typedCommand, tableText);
        }
    }
}
