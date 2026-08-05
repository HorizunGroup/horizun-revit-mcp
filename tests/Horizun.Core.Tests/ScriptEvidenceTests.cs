// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE HONESTY BOUNDARY BETWEEN A TYPED WRITE AND A SCRIPT.
//
// A typed command is verified BY THE HOST: the bridge re-reads the model after
// the commit, in code this repository owns. Arbitrary Python is not re-read by
// anything. An earlier version of the classifier returned "verified" whenever a
// script said so and attached a non-empty list, which let
// {"status":"verified","verification":{"checked":true,"evidence":["ok"]}} come
// back indistinguishable from a real host verification. The word was doing work
// it had not earned.
//
// So the ceiling for arbitrary code is self_reported_verified, and these tests
// exist to keep it there. The negative cases are the point: "ok" and a document
// title are exactly the kind of thing a plausible-looking script returns.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScriptEvidenceTests
    {
        private static JObject Structured(string status, bool check = false, params string[] evidence)
        {
            var ev = new JArray();
            foreach (string e in evidence) ev.Add(e);
            return new JObject
            {
                ["status"] = status,
                ["summary"] = "s",
                ["created_ids"] = new JArray(),
                ["modified_ids"] = new JArray(),
                ["deleted_ids"] = new JArray(),
                ["verification"] = new JObject { ["checked"] = check, ["evidence"] = ev },
                ["warnings"] = new JArray()
            };
        }

        /// <summary>
        /// THE RULE. No input, however well-formed, produces "verified" - that word
        /// belongs to the typed commands, which the host re-reads.
        /// </summary>
        [Fact]
        public void No_input_ever_produces_the_word_verified_as_a_state()
        {
            foreach (JToken candidate in new JToken[]
            {
                Structured("verified", true, "re-read id 42: Comments='x'"),
                Structured("verified", true, "ok"),
                Structured("verified"),
                Structured("partial"),
                Structured("failed"),
                new JValue("done"),
                JValue.CreateNull()
            })
            {
                Assert.NotEqual("verified", ScriptEvidence.Classify(candidate).Status);
            }
        }

        [Fact]
        public void A_verified_claim_with_evidence_is_self_reported_not_host_verified()
        {
            EvidenceReport r = ScriptEvidence.Classify(
                Structured("verified", true, "re-read id 42: Comments='checked-by-fallback'"));

            Assert.Equal("self_reported_verified", r.Status);
            Assert.True(r.Structured);
            Assert.False(r.HostVerified);
            // What the script said is kept, so classifying never destroys information.
            Assert.Equal("verified", r.ScriptReportedStatus);
            // And the response says, in words, that the host confirmed nothing.
            Assert.Contains(r.Warnings, w => ((string)w).Contains("did not re-read the model"));
        }

        /// <summary>
        /// The negative case the reviewer named: a script can write any words it likes
        /// into evidence. ["ok"] demonstrates nothing about the model, and must not be
        /// presented as host verification.
        /// </summary>
        [Fact]
        public void Evidence_of_ok_is_not_presented_as_host_verification()
        {
            EvidenceReport r = ScriptEvidence.Classify(Structured("verified", true, "ok"));

            Assert.Equal("self_reported_verified", r.Status);
            Assert_it_is_not_host_verified(r);
        }

        /// <summary>
        /// The other named case: echoing doc.Title proves the script ran, not that
        /// anything was written or re-read.
        /// </summary>
        [Fact]
        public void Evidence_of_a_document_title_is_not_presented_as_host_verification()
        {
            EvidenceReport r = ScriptEvidence.Classify(Structured("verified", true, "HZ_LIVE_A"));

            Assert.Equal("self_reported_verified", r.Status);
            Assert_it_is_not_host_verified(r);
        }

        /// <summary>
        /// A well-formed evidence payload and one with nothing to show must remain
        /// TELLABLE APART - the classifier narrows what may be claimed, it does not
        /// flatten every Python result into one indistinguishable state.
        /// </summary>
        [Fact]
        public void A_structured_claim_stays_distinguishable_from_one_without_evidence()
        {
            EvidenceReport withEvidence = ScriptEvidence.Classify(Structured("verified", true, "re-read id 7"));
            EvidenceReport withoutEvidence = ScriptEvidence.Classify(Structured("verified", true));
            EvidenceReport unstructured = ScriptEvidence.Classify(new JValue("done"));

            Assert.Equal("self_reported_verified", withEvidence.Status);
            Assert.Equal("completed_unverified", withoutEvidence.Status);
            Assert.Equal("completed_unverified", unstructured.Status);

            // ...and the two completed_unverified results are still not the same result.
            Assert.True(withoutEvidence.Structured);
            Assert.False(unstructured.Structured);
            Assert.Equal("verified", withoutEvidence.ScriptReportedStatus);
            Assert.Null(unstructured.ScriptReportedStatus);
        }

        [Fact]
        public void A_verified_claim_without_evidence_is_downgraded()
        {
            // checked=true but empty evidence
            Assert.Equal("completed_unverified", ScriptEvidence.Classify(Structured("verified", true)).Status);
            // evidence present but checked=false
            Assert.Equal("completed_unverified",
                ScriptEvidence.Classify(Structured("verified", false, "claim")).Status);
            // no verification object at all
            var bare = new JObject { ["status"] = "verified", ["summary"] = "s" };
            EvidenceReport r = ScriptEvidence.Classify(bare);
            Assert.Equal("completed_unverified", r.Status);
            Assert.Contains(r.Warnings, w => ((string)w).Contains("DOWNGRADED"));
        }

        [Fact]
        public void Partial_and_failed_are_preserved_exactly_as_the_script_states_them()
        {
            EvidenceReport partial = ScriptEvidence.Classify(Structured("partial"));
            Assert.Equal("partial", partial.Status);
            Assert.Equal("partial", partial.ScriptReportedStatus);

            EvidenceReport failed = ScriptEvidence.Classify(Structured("failed"));
            Assert.Equal("failed", failed.Status);
            Assert.Equal("failed", failed.ScriptReportedStatus);

            Assert.Equal("completed_unverified",
                ScriptEvidence.Classify(Structured("completed_unverified")).Status);
        }

        [Fact]
        public void Unstructured_output_is_completed_unverified_with_an_explanation()
        {
            foreach (JToken t in new JToken[] { JValue.CreateNull(), new JValue(42), new JValue("done"),
                                                new JArray { 1, 2, 3 } })
            {
                EvidenceReport r = ScriptEvidence.Classify(t);
                Assert.Equal("completed_unverified", r.Status);
                Assert.False(r.Structured);
                Assert.NotEmpty(r.Warnings);
            }
        }

        [Fact]
        public void An_object_without_status_or_with_an_unknown_status_never_reads_as_success()
        {
            EvidenceReport noStatus = ScriptEvidence.Classify(new JObject { ["result"] = "ok" });
            Assert.Equal("completed_unverified", noStatus.Status);
            Assert.False(noStatus.Structured);
            Assert.Null(noStatus.ScriptReportedStatus);

            EvidenceReport unknown = ScriptEvidence.Classify(Structured("great_success", true, "e"));
            Assert.Equal("completed_unverified", unknown.Status);
            Assert.Equal("great_success", unknown.ScriptReportedStatus);
            Assert.NotEmpty(unknown.Warnings);
        }

        [Fact]
        public void Status_matching_is_case_and_whitespace_tolerant_but_nothing_else()
        {
            Assert.Equal("self_reported_verified",
                ScriptEvidence.Classify(Structured(" VERIFIED ", true, "e")).Status);
            Assert.Equal("failed", ScriptEvidence.Classify(Structured("Failed")).Status);
        }

        [Fact]
        public void The_scripts_own_warnings_ride_along()
        {
            JObject o = Structured("partial");
            ((JArray)o["warnings"]).Add("two floors could not be matched");
            EvidenceReport r = ScriptEvidence.Classify(o);
            Assert.Contains(r.Warnings, w => ((string)w).Contains("two floors could not be matched"));
        }

        [Fact]
        public void The_summary_is_surfaced_when_the_script_gave_one()
        {
            JObject o = Structured("verified", true, "e");
            o["summary"] = "3 walls re-read";
            Assert.Equal("3 walls re-read", ScriptEvidence.Classify(o).Summary);
        }

        [Fact]
        public void The_disclaimer_states_the_difference_from_a_typed_write()
        {
            Assert.Contains("NOT HOST-VERIFIED", ScriptEvidence.HostVerificationDisclaimer);
            Assert.Contains("Typed commands are re-read", ScriptEvidence.HostVerificationDisclaimer);
        }

        private static void Assert_it_is_not_host_verified(EvidenceReport r)
        {
            Assert.False(r.HostVerified);
            Assert.NotEqual("verified", r.Status);
            Assert.Contains(r.Warnings, w => ((string)w).Contains("did not re-read the model"));
        }
    }
}
