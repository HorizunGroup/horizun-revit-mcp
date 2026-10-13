// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE QUARANTINE THAT REFUSES (backlog P0 #4, finished in 3.0.0 stage 3). 2.1.7 made
// the wording honest: a failure SHAPE is known only once a call has failed, so the
// shape alone can refuse nothing. What CAN be recognised before a call runs is the
// call itself - the tool and its arguments. `observe` and `quarantine` now take the
// failing call's arguments (`call_arguments`), keep only a hash of their normalised
// form, and the tools/call path refuses a later call with the same fingerprint while
// its shape is quarantined, before anything reaches the add-in.
//
// These tests pin: the refusal, its code and remedy; what does NOT count as a
// different call (key order, idempotency_key, confirmation_token); what does; that
// release lifts it; that no argument value is written to the file; that the 500
// bound keeps the fingerprints; that the memory's own tool is never refused; and
// that a damaged memory refuses nothing and is left exactly where it was.
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
    public class RepairMemoryCallGateTests
    {
        private const string Tool = "horizun_write_params_verified";
        private const string Message = "The transaction could not be committed because the element is pinned";
        private const string Secret = "Proyecto-Confidencial-Torre-Norte";

        private static void WithDataRoot(Action<string> body)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string root = Path.Combine(Path.GetTempPath(), "hz-repair-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, root);
                QuarantineGate.ResetCache();
                body(root);
            }
            finally
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                QuarantineGate.ResetCache();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static JObject Call(object args) =>
            RepairMemoryTool.Handle(JObject.FromObject(args), System.Threading.CancellationToken.None);

        private static JObject FailingArgs() => JObject.Parse(@"{
            ""element_ids"": [101, 102],
            ""parameters"": { ""Comments"": """ + Secret + @""", ""Mark"": ""A-1"" },
            ""dry_run"": false,
            ""idempotency_key"": ""k-1"",
            ""confirmation_token"": ""tok-1""
        }");

        private static string QuarantineFailingCall()
        {
            JObject seen = Call(new { operation = "observe", tool = Tool, message = Message, call_arguments = FailingArgs() });
            Call(new { operation = "quarantine", shape = (string)seen["shape"], reason = "damages hosted doors", author = "owner" });
            return (string)seen["shape"];
        }

        [Fact]
        public void A_quarantined_call_is_refused_before_it_runs_with_a_code_and_a_remedy()
        {
            WithDataRoot(_ =>
            {
                string shape = QuarantineFailingCall();
                JObject refusal = QuarantineGate.Check(Tool, FailingArgs());
                Assert.NotNull(refusal);
                Assert.Equal(QuarantineGate.Code, (string)refusal["code"]);
                Assert.Equal("call_quarantined", (string)refusal["code"]);
                Assert.Equal(shape, (string)refusal["shape"]);
                Assert.Equal("damages hosted doors", (string)refusal["reason"]);
                Assert.Equal("owner", (string)refusal["quarantined_by"]);
                Assert.False((bool)refusal["ran"]);
                Assert.Contains("operation=release", (string)refusal["remedy"]);
                Assert.Contains(shape, (string)refusal["remedy"]);
                Assert.Contains("QUARANTINED", (string)refusal["message"]);
            });
        }

        [Fact]
        public void Key_order_idempotency_key_and_confirmation_token_do_not_make_a_different_call()
        {
            WithDataRoot(_ =>
            {
                QuarantineFailingCall();
                JObject retried = JObject.Parse(@"{
                    ""confirmation_token"": ""tok-2"",
                    ""idempotency_key"": ""k-2"",
                    ""dry_run"": false,
                    ""parameters"": { ""Mark"": ""A-1"", ""Comments"": """ + Secret + @""" },
                    ""element_ids"": [101, 102]
                }");
                Assert.NotNull(QuarantineGate.Check(Tool, retried));
            });
        }

        [Fact]
        public void A_different_argument_value_or_another_tool_is_not_refused()
        {
            WithDataRoot(_ =>
            {
                QuarantineFailingCall();
                JObject other = FailingArgs();
                other["element_ids"] = new JArray(101, 103);
                Assert.Null(QuarantineGate.Check(Tool, other));
                JObject rehearsal = FailingArgs();
                rehearsal["dry_run"] = true;
                Assert.Null(QuarantineGate.Check(Tool, rehearsal));
                Assert.Null(QuarantineGate.Check("horizun_set_parameters", FailingArgs()));
            });
        }

        [Fact]
        public void An_observed_call_that_is_not_quarantined_is_not_refused()
        {
            WithDataRoot(_ =>
            {
                Call(new { operation = "observe", tool = Tool, message = Message, call_arguments = FailingArgs() });
                Assert.Null(QuarantineGate.Check(Tool, FailingArgs()));
            });
        }

        [Fact]
        public void Release_lifts_the_refusal()
        {
            WithDataRoot(_ =>
            {
                string shape = QuarantineFailingCall();
                Assert.NotNull(QuarantineGate.Check(Tool, FailingArgs()));
                Call(new { operation = "release", shape });
                Assert.Null(QuarantineGate.Check(Tool, FailingArgs()));
            });
        }

        [Fact]
        public void The_memory_s_own_tool_is_never_refused()
        {
            WithDataRoot(_ =>
            {
                JObject args = JObject.FromObject(new { operation = "list" });
                Assert.Throws<ToolRefusal>(() =>
                    Call(new { operation = "observe", tool = RepairMemoryTool.Name, message = Message, call_arguments = args }));

                // Even a hand-made entry that names it cannot lock the way out.
                var entry = new RepairEntry
                {
                    Shape = "self", Tool = RepairMemoryTool.Name, State = RepairMemory.StateQuarantined,
                    LastSeenUtc = "2026-10-12T00:00:00Z", Occurrences = 1, QuarantineReason = "r", QuarantinedBy = "owner"
                };
                RepairMemory.AttachCall(entry, RepairMemory.CallFingerprint(RepairMemoryTool.Name, args));
                RepairMemory.Save(RepairMemory.Path(), new[] { entry });
                QuarantineGate.ResetCache();
                Assert.Null(QuarantineGate.Check(RepairMemoryTool.Name, args));
            });
        }

        [Fact]
        public void No_argument_value_is_written_to_the_memory()
        {
            WithDataRoot(root =>
            {
                QuarantineFailingCall();
                string text = File.ReadAllText(RepairMemory.Path());
                Assert.DoesNotContain(Secret, text);
                Assert.DoesNotContain("A-1", text);
                Assert.DoesNotContain("element_ids", text);
                Assert.Contains("argument_fingerprints", text);
            });
        }

        [Fact]
        public void A_quarantine_attaches_the_call_it_is_given_and_says_how_many_calls_it_refuses()
        {
            WithDataRoot(_ =>
            {
                JObject seen = Call(new { operation = "observe", tool = Tool, message = Message });
                JObject bare = Call(new { operation = "quarantine", shape = (string)seen["shape"], reason = "r", author = "owner" });
                Assert.Equal(0, (int)bare["refuses_calls"]);
                Assert.Contains("no call", (string)bare["means"], StringComparison.OrdinalIgnoreCase);
                Assert.Null(QuarantineGate.Check(Tool, FailingArgs()));

                JObject armed = Call(new { operation = "quarantine", shape = (string)seen["shape"], reason = "r", author = "owner", call_arguments = FailingArgs() });
                Assert.Equal(1, (int)armed["refuses_calls"]);
                Assert.NotNull(QuarantineGate.Check(Tool, FailingArgs()));
            });
        }

        [Fact]
        public void The_bound_keeps_a_quarantine_s_fingerprints_and_it_still_refuses()
        {
            WithDataRoot(_ =>
            {
                QuarantineFailingCall();
                string path = RepairMemory.Path();
                Dictionary<string, RepairEntry> entries = RepairMemory.Load(path);
                for (int i = 0; i < RepairMemory.MaxEntries + 50; i++)
                    entries["o" + i] = new RepairEntry { Shape = "o" + i, Tool = Tool, LastSeenUtc = "2099-01-01T00:00:" + (i % 60).ToString("00") + "Z", Occurrences = 1 };
                RepairMemory.Save(path, entries.Values);
                QuarantineGate.ResetCache();
                Assert.True(RepairMemory.Load(path).Count <= RepairMemory.MaxEntries);
                Assert.NotNull(QuarantineGate.Check(Tool, FailingArgs()));
            });
        }

        [Fact]
        public void A_damaged_memory_refuses_nothing_and_is_left_where_it_was()
        {
            WithDataRoot(root =>
            {
                string path = RepairMemory.Path();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{ not json");
                Assert.Null(QuarantineGate.Check(Tool, FailingArgs()));
                Assert.True(File.Exists(path), "the read-only gate moved the memory aside");
                Assert.Equal("{ not json", File.ReadAllText(path));
            });
        }

        [Fact]
        public void The_fingerprint_is_a_hash_and_ignores_only_the_retry_envelope()
        {
            string a = RepairMemory.CallFingerprint(Tool, FailingArgs());
            Assert.Matches("^[0-9a-f]{32}$", a);
            JObject b = FailingArgs();
            b.Remove("idempotency_key");
            b.Remove("confirmation_token");
            Assert.Equal(a, RepairMemory.CallFingerprint(Tool, b));
            JObject c = FailingArgs();
            c["target_document"] = "Model B";
            Assert.NotEqual(a, RepairMemory.CallFingerprint(Tool, c));
            Assert.NotEqual(a, RepairMemory.CallFingerprint("horizun_other", FailingArgs()));
        }

        [Fact]
        public void The_contract_says_what_is_refused()
        {
            var contract = Horizun.Contracts.Contract.All.Single(c => c.Name == "horizun_repair_memory");
            Assert.NotNull(contract.InputSchema["properties"]?["call_arguments"]);
            Assert.Contains("call_quarantined", contract.Description);
        }
    }
}
