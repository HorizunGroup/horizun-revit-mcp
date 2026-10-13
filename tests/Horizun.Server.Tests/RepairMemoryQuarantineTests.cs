// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// WHAT A QUARANTINE IS, SAID TRUTHFULLY (backlog P0 #4, 2026-10-10). The tool and its
// contract said "calls matching this exact shape are now REFUSED", but nothing ever
// refused anything: a failure SHAPE is the tool, the class and the normalised error
// MESSAGE, so it is only known once a call has failed - there is nothing to match a
// call against before it runs. A quarantine is a recorded instruction not to retry,
// answered by `advice` with the reason, the author and the way to lift it. These tests
// pin that wording, and that the 500-entry bound never drops a quarantine or a
// validated remedy (it kept the 500 most recently SEEN shapes, whatever their state).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class RepairMemoryQuarantineTests
    {
        private const string Tool = "horizun_write_params_verified";
        private const string Message = "The transaction could not be committed because the element is pinned";

        private static T WithDataRoot<T>(Func<T> body)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string root = Path.Combine(Path.GetTempPath(), "hz-repair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, root);
                return body();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static JObject Call(object args) =>
            RepairMemoryTool.Handle(JObject.FromObject(args), System.Threading.CancellationToken.None);

        [Fact]
        public void A_quarantine_never_claims_to_refuse_calls_it_cannot_see_in_advance()
        {
            JObject reply = WithDataRoot(() =>
            {
                JObject seen = Call(new { operation = "observe", tool = Tool, message = Message });
                return Call(new { operation = "quarantine", shape = (string)seen["shape"], reason = "damages hosted doors", author = "owner" });
            });
            string means = (string)reply["means"];
            Assert.DoesNotContain("REFUSED", means);
            Assert.Contains("cannot refuse", means, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RepairMemory.StateQuarantined, (string)reply["state"]);
        }

        [Fact]
        public void Advice_on_a_quarantined_shape_says_so_with_reason_author_and_the_way_out()
        {
            JObject advice = WithDataRoot(() =>
            {
                JObject seen = Call(new { operation = "observe", tool = Tool, message = Message });
                Call(new { operation = "quarantine", shape = (string)seen["shape"], reason = "damages hosted doors", author = "owner" });
                return Call(new { operation = "advice", tool = Tool, message = Message });
            });
            JObject a = (JObject)advice["advice"];
            Assert.Equal(RepairMemory.StateQuarantined, (string)a["state"]);
            JObject q = (JObject)a["quarantine"];
            Assert.NotNull(q);
            Assert.Equal("damages hosted doors", (string)q["reason"]);
            Assert.Equal("owner", (string)q["by"]);
            Assert.Contains("operation=release", (string)q["means"]);
            Assert.Contains("do not retry", (string)q["means"], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_bound_never_drops_a_quarantine_or_a_validated_remedy()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-repair-bound-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var entries = new List<RepairEntry>
                {
                    new RepairEntry { Shape = "q-old", Tool = Tool, State = RepairMemory.StateQuarantined, LastSeenUtc = "2020-01-01T00:00:00Z", Occurrences = 1 },
                    new RepairEntry { Shape = "v-old", Tool = Tool, State = RepairMemory.StateValidated, Remedy = "unpin first", LastSeenUtc = "2020-01-01T00:00:00Z", Occurrences = 1 }
                };
                for (int i = 0; i < RepairMemory.MaxEntries + 100; i++)
                    entries.Add(new RepairEntry { Shape = "o" + i, Tool = Tool, LastSeenUtc = "2026-10-10T00:00:" + (i % 60).ToString("00") + "Z", Occurrences = 1 });
                RepairMemory.Save(path, entries);
                Dictionary<string, RepairEntry> back = RepairMemory.Load(path);
                Assert.True(back.ContainsKey("q-old"), "a quarantine was dropped by the 500-entry bound");
                Assert.True(back.ContainsKey("v-old"), "a validated remedy was dropped by the 500-entry bound");
                Assert.True(back.Count <= RepairMemory.MaxEntries, "the bound no longer bounds");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Newer_validated_remedies_cannot_evict_an_older_quarantine()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-repair-priority-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var entries = new List<RepairEntry>
                {
                    new RepairEntry { Shape = "q-old", Tool = Tool, State = RepairMemory.StateQuarantined,
                        LastSeenUtc = "2020-01-01T00:00:00Z", Occurrences = 1 }
                };
                for (int i = 0; i < RepairMemory.MaxEntries + 1; i++)
                    entries.Add(new RepairEntry { Shape = "v" + i, Tool = Tool,
                        State = RepairMemory.StateValidated, Remedy = "checked",
                        LastSeenUtc = "2026-10-10T00:00:00Z", Occurrences = 1 });

                RepairMemory.Save(path, entries);
                Dictionary<string, RepairEntry> back = RepairMemory.Load(path);
                Assert.True(back.ContainsKey("q-old"), "a validated remedy evicted an active quarantine");
                Assert.Equal(entries.Count, back.Count);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void The_contract_does_not_promise_a_refusal()
        {
            string description = Horizun.Contracts.Contract.All.Single(c => c.Name == "horizun_repair_memory").Description;
            Assert.DoesNotContain("refuses only that exact shape", description);
            Assert.Contains("cannot refuse", description, StringComparison.OrdinalIgnoreCase);
        }
    }
}
