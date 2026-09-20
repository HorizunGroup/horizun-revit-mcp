// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THREE THINGS GET CALLED "RETRY", AND THEY ARE NOT THE SAME.
//
//   REPEAT    the same work, already finished. Nothing runs; the caller is handed
//             the reply that run produced.
//   CONTINUE  the same work, half done. What is still pending runs, and ONLY
//             that, under the decisions the first call was given.
//   A NEW PLAN  the world moved. The old decisions do not carry - they were
//             answers to a question that has changed.
//
// The in-session ledger covered the first. These cover the record that makes the
// second possible and keeps it honest: what is confirmed, what is pending, what
// each action created, and what the caller was authorised to do.
//
// The record is a FILE, and that is the point being tested here as much as the
// logic: "save the model, close Revit, open a new session and continue" is a case
// a caller actually has, and a ledger that lives in one process cannot serve it.
// Every test below writes and reads it through HorizunPaths, in its own root.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class CadUpdateOperationsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _savedRoot;

        public CadUpdateOperationsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hz-ops-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_root, true); } catch { }
        }

        private static JArray Actions(params string[] keys) =>
            new JArray(keys.Select(k => (JToken)new JObject { ["key"] = k, ["tool"] = "horizun_create_elements" }));

        private static JObject Binding(string fingerprint) =>
            new JObject { ["actions_fingerprint"] = fingerprint, ["target_document"] = "HZ" };

        private static JObject Decisions() => new JObject
        {
            ["accept_pairings"] = new JArray(new JObject { ["element_id"] = 101L, ["candidate_id"] = "cadrev:a" }),
            ["release_fittings"] = new JArray(202L)
        };

        [Fact]
        public void A_fresh_operation_has_every_action_pending_and_nothing_confirmed()
        {
            JObject o = CadUpdateOperations.Begin("op-1", "HZ", "pl-1", Binding("f1"), Decisions(),
                                                  Actions("a", "b", "c"));

            Assert.Equal(new[] { "a", "b", "c" }, CadUpdateOperations.PendingKeys(o).ToArray());
            JObject said = CadUpdateOperations.Describe(o, "fresh");
            Assert.Empty(said["confirmed"] as JArray);
            Assert.Equal(3, (said["pending"] as JArray).Count);
            Assert.Equal("open", said.Value<string>("state"));
        }

        [Fact]
        public void An_action_that_landed_is_recorded_the_moment_it_lands_with_what_it_created()
        {
            CadUpdateOperations.Begin("op-2", "HZ", null, Binding("f1"), null, Actions("a", "b"));
            CadUpdateOperations.MarkAction("op-2", "a", CadUpdateOperations.Confirmed,
                                           new long[] { 900, 901 }, null, null);

            // READ BACK FROM DISK, not from the object the caller happened to hold: a crash between the
            // write and the reply is precisely the case this record exists for.
            JObject reread = CadUpdateOperations.Read("op-2");
            Assert.Equal(new[] { "b" }, CadUpdateOperations.PendingKeys(reread).ToArray());
            JObject said = CadUpdateOperations.Describe(reread, "continued");
            Assert.Equal(new JArray("a"), said["confirmed"]);
            Assert.Equal(new JArray(900L, 901L), said["created"]);
        }

        [Fact]
        public void A_failed_action_is_neither_confirmed_nor_pending_and_keeps_its_reason()
        {
            CadUpdateOperations.Begin("op-3", "HZ", null, Binding("f1"), null, Actions("a", "b"));
            CadUpdateOperations.MarkAction("op-3", "a", CadUpdateOperations.Confirmed, new long[] { 5 }, null, null);
            CadUpdateOperations.MarkAction("op-3", "b", CadUpdateOperations.Failed, null, null,
                                           "the host was not created");

            JObject said = CadUpdateOperations.Describe(CadUpdateOperations.Read("op-3"), "fresh");
            Assert.Equal(new JArray("a"), said["confirmed"]);
            Assert.Empty(said["pending"] as JArray);
            Assert.Equal(new JArray("b"), said["failed"]);
            // AND THE REPLY SAYS SO IN ONE PLACE. Measured on the first live run: the reply listed
            // "pending" as empty while a failed action was still to be carried out, which reads as
            // "nothing left" to anybody who is not holding all three lists at once.
            Assert.Equal(new JArray("b"), said["still_to_do"]);
            // AND IT IS STILL SOMETHING TO DO. A failure is not a completion: PendingKeys is what a
            // continuation runs, and an action that failed has not been carried out.
            Assert.Equal(new[] { "b" }, CadUpdateOperations.PendingKeys(CadUpdateOperations.Read("op-3")).ToArray());
        }

        [Fact]
        public void Beginning_the_same_operation_twice_returns_the_record_as_it_stands()
        {
            CadUpdateOperations.Begin("op-4", "HZ", null, Binding("f1"), null, Actions("a", "b"));
            CadUpdateOperations.MarkAction("op-4", "a", CadUpdateOperations.Confirmed, new long[] { 7 }, null, null);

            // A SECOND Begin IS WHAT A RETRY LOOKS LIKE FROM HERE. If it reset the rows, a continuation
            // would run the confirmed actions again - which is how a retry builds the revision twice.
            JObject again = CadUpdateOperations.Begin("op-4", "HZ", null, Binding("f1"), null, Actions("a", "b"));
            Assert.Equal(new[] { "b" }, CadUpdateOperations.PendingKeys(again).ToArray());
        }

        [Fact]
        public void The_record_carries_the_decisions_the_first_call_was_authorised_to_make()
        {
            CadUpdateOperations.Begin("op-5", "HZ", "pl", Binding("f1"), Decisions(), Actions("a"));
            JObject said = CadUpdateOperations.Describe(CadUpdateOperations.Read("op-5"), "continued");

            JObject authorised = said["decisions_authorised"] as JObject;
            Assert.NotNull(authorised);
            Assert.Equal(101L, (authorised["accept_pairings"] as JArray)[0].Value<long>("element_id"));
            Assert.Equal(new JArray(202L), authorised["release_fittings"]);
        }

        [Fact]
        public void An_operation_nobody_opened_reads_as_nothing_rather_than_as_an_empty_one()
        {
            // The difference matters at the call site: an unknown id is refused, an OPEN id with no
            // pending actions is finished. Returning an empty record for both would make a continuation
            // on the wrong machine look like a completed one.
            Assert.Null(CadUpdateOperations.Read("op-that-was-never-opened"));
            Assert.Equal("nothing-opened", CadUpdateOperations.Describe(null, "nothing-opened").Value<string>("shape"));
        }

        [Fact]
        public void Finishing_says_whether_the_work_ended_whole_or_partial()
        {
            CadUpdateOperations.Begin("op-6", "HZ", null, Binding("f1"), null, Actions("a", "b"));
            CadUpdateOperations.MarkAction("op-6", "a", CadUpdateOperations.Confirmed, new long[] { 1 }, null, null);
            CadUpdateOperations.Finish("op-6", "partial");

            JObject said = CadUpdateOperations.Describe(CadUpdateOperations.Read("op-6"), "fresh");
            Assert.Equal("partial", said.Value<string>("state"));
            // AND THE PENDING WORK SURVIVES THE CLOSE. "Partial" is not "over": it is the state a
            // continuation is for, and the row it has to run is still named.
            Assert.Equal(new JArray("b"), said["pending"]);
        }

        [Fact]
        public void Two_operations_do_not_share_a_file()
        {
            CadUpdateOperations.Begin("op-7", "HZ", null, Binding("f1"), null, Actions("a"));
            CadUpdateOperations.Begin("op-8", "HZ", null, Binding("f2"), null, Actions("z"));
            CadUpdateOperations.MarkAction("op-7", "a", CadUpdateOperations.Confirmed, null, null, null);

            Assert.Equal(new[] { "z" }, CadUpdateOperations.PendingKeys(CadUpdateOperations.Read("op-8")).ToArray());
            Assert.NotEqual(CadUpdateOperations.PathFor("op-7"), CadUpdateOperations.PathFor("op-8"));
        }

        [Fact]
        public void What_a_reply_says_names_the_shape_and_explains_it_in_words()
        {
            CadUpdateOperations.Begin("op-9", "HZ", null, Binding("f1"), null, Actions("a"));
            JObject record = CadUpdateOperations.Read("op-9");

            Assert.Contains("already finished", CadUpdateOperations.Describe(record, "replayed").Value<string>("means"));
            Assert.Contains("left pending", CadUpdateOperations.Describe(record, "continued").Value<string>("means"));
            Assert.Contains("fresh operation", CadUpdateOperations.Describe(record, "fresh").Value<string>("means"));
        }

        [Fact]
        public void A_record_written_by_one_process_is_readable_by_the_next()
        {
            // THE WHOLE POINT OF IT BEING A FILE. Nothing here shares an object with the writer: this is
            // the same path a NEW Revit session takes when a caller saves, closes and continues.
            CadUpdateOperations.Begin("op-10", "HZ", "pl", Binding("f1"), Decisions(), Actions("a", "b"));
            CadUpdateOperations.MarkAction("op-10", "a", CadUpdateOperations.Confirmed, new long[] { 42 }, null, null);

            string path = CadUpdateOperations.PathFor("op-10");
            Assert.True(File.Exists(path));
            JObject fromDisk = JObject.Parse(File.ReadAllText(path));
            Assert.Equal("HZ", fromDisk.Value<string>("document"));
            Assert.Equal("f1", (fromDisk["binding"] as JObject).Value<string>("actions_fingerprint"));
            Assert.Equal(new[] { "b" }, CadUpdateOperations.PendingKeys(fromDisk).ToArray());
        }

        [Fact]
        public void What_an_action_removed_is_recorded_as_removed_not_as_created()
        {
            // A SUBSTITUTION IS TWO FACTS. An update that replaces an element creates one and deletes
            // another; a record that only counted creations would leave a continuation unable to say
            // whether the old one is still standing.
            CadUpdateOperations.Begin("op-11", "HZ", null, Binding("f1"), null, Actions("swap"));
            CadUpdateOperations.MarkAction("op-11", "swap", CadUpdateOperations.Confirmed,
                                           new long[] { 500 }, new long[] { 499 }, null);

            JObject said = CadUpdateOperations.Describe(CadUpdateOperations.Read("op-11"), "fresh");
            Assert.Equal(new JArray(500L), said["created"]);
            Assert.Equal(new JArray(499L), said["removed"]);
        }
    }
}
