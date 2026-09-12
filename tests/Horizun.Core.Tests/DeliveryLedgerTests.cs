// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// The delivery ledger. The properties worth proving are the refusals and the
// cascades: a stage cannot run ahead of its dependency, a completed write is
// never handed out again, an invalidation reaches everything built on the
// invalidated stage, and the publish gate stays shut on any of it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DeliveryLedgerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 8, 1, 0, 0, DateTimeKind.Utc);

        private static JObject Plan()
        {
            JObject profile = JObject.Parse(@"{
              ""id"": ""arch"", ""version"": ""1"", ""units"": ""mm"",
              ""views"": [{""view_id"": 101, ""tags"": {""element_ids"": [501]}},
                          {""view_id"": 102, ""dimension_sets"": [{""role"": ""general"", ""operation"": ""intent_dimension"",
                              ""element_ids"": [601, 602], ""offset"": 10, ""side"": ""positive"", ""dimension_type_id"": 301}]}],
              ""packing"": {""sheets"": [{""sheet_id"": 201, ""usable_rect"": [10, 10, 700, 500]}],
                            ""items"": [{""key"": ""plan"", ""view_id"": 101}]},
              ""publication"": {""format"": ""pdf"", ""view_ids"": [201], ""output_path"": ""C:/Out/delivery.pdf""},
              ""requirement_set"": {""requirement_set"": {""id"": ""s"", ""version"": ""1""},
                                    ""rules"": [{""id"": ""number"", ""entity"": ""sheet"", ""selector"": {""applies_to_all"": true},
                                                 ""assertion"": {""field"": ""sheet_number"", ""operator"": ""not_empty""}}]}
            }");
            return DeliveryPlan.Build(profile, "HZ_WRITE");
        }

        private static JObject Open(string id = "d1")
        {
            return DeliveryLedger.Open(Plan(), new JObject { ["title"] = "HZ_WRITE", ["fingerprint"] = "fp-1" },
                                       new JObject { ["version"] = "1.2.1", ["commit"] = "abc" }, id, T0);
        }

        private static void Go(JObject record, string key, string to, JObject facts = null)
        {
            string why;
            Assert.True(DeliveryLedger.TryTransition(record, key, to, facts, T0, out why), key + " -> " + to + ": " + why);
        }

        private static string Status(JObject record, string key) => DeliveryLedger.Stage(record, key).Value<string>("status");

        [Fact]
        public void OpeningDerivesKindsAndDependenciesFromThePlan()
        {
            JObject r = Open();
            string[] keys = ((JArray)r["stages"]).Select(s => s.Value<string>("key")).ToArray();
            Assert.Equal(new[] { "view_101", "tags_101", "capture_view_101", "view_102", "dimensions_102", "capture_view_102",
                                 "pack", "audit", "capture_sheet_201", "publish" }, keys);
            Assert.Equal(DeliveryLedger.KindWrite, DeliveryLedger.Stage(r, "tags_101").Value<string>("kind"));
            Assert.Equal(DeliveryLedger.KindApprovalCapture, DeliveryLedger.Stage(r, "capture_sheet_201").Value<string>("kind"));
            Assert.Equal(new[] { "view_101" }, DeliveryLedger.Stage(r, "tags_101")["depends_on"].Values<string>());
            Assert.Equal(new[] { "capture_view_101", "capture_view_102" }, DeliveryLedger.Stage(r, "pack")["depends_on"].Values<string>());
            Assert.Equal(new[] { "audit", "capture_sheet_201" }, DeliveryLedger.Stage(r, "publish")["depends_on"].Values<string>());
            Assert.All(((JArray)r["stages"]).OfType<JObject>(), s => Assert.Equal(DeliveryLedger.Pending, s.Value<string>("status")));
            Assert.Equal("fp-1", r["document"].Value<string>("fingerprint"));
        }

        [Fact]
        public void AStageCannotStartAheadOfItsDependency()
        {
            JObject r = Open();
            string why;
            Assert.False(DeliveryLedger.TryTransition(r, "tags_101", DeliveryLedger.InProgress, null, T0, out why));
            Assert.Contains("depends on 'view_101'", why);
            Go(r, "view_101", DeliveryLedger.InProgress);
            Go(r, "view_101", DeliveryLedger.Completed);
            Go(r, "tags_101", DeliveryLedger.InProgress);
        }

        [Fact]
        public void ACompletedWriteMustNameItsIdempotencyKey()
        {
            JObject r = Open();
            Go(r, "view_101", DeliveryLedger.InProgress); Go(r, "view_101", DeliveryLedger.Completed);
            Go(r, "tags_101", DeliveryLedger.InProgress);
            string why;
            Assert.False(DeliveryLedger.TryTransition(r, "tags_101", DeliveryLedger.Completed, new JObject { ["element_ids"] = new JArray(9) }, T0, out why));
            Assert.Contains("idempotency_key", why);
            Go(r, "tags_101", DeliveryLedger.Completed, new JObject { ["idempotency_key"] = "k1", ["element_ids"] = new JArray(9) });
        }

        [Fact]
        public void IllegalTransitionsAreRefusedByName()
        {
            JObject r = Open();
            string why;
            Assert.False(DeliveryLedger.TryTransition(r, "view_101", DeliveryLedger.Completed, null, T0, out why));   // pending -> completed
            Assert.Contains("cannot go from pending to completed", why);
            Assert.False(DeliveryLedger.TryTransition(r, "view_101", DeliveryLedger.Approved, null, T0, out why));    // navigation never approved
            Assert.False(DeliveryLedger.TryTransition(r, "nope", DeliveryLedger.InProgress, null, T0, out why));
            Assert.Contains("not in this delivery", why);
            Assert.False(DeliveryLedger.TryTransition(r, "view_101", "done", null, T0, out why));
        }

        private static JObject RunToPublishGate(out JObject record)
        {
            JObject r = Open();
            Go(r, "view_101", DeliveryLedger.InProgress); Go(r, "view_101", DeliveryLedger.Completed);
            Go(r, "tags_101", DeliveryLedger.InProgress);
            Go(r, "tags_101", DeliveryLedger.Completed, new JObject { ["idempotency_key"] = "k-tags", ["element_ids"] = new JArray(9001),
                ["scope"] = new JArray(new JObject { ["element_id"] = 9001, ["version_guid"] = "v1" }) });
            Go(r, "capture_view_101", DeliveryLedger.InProgress); Go(r, "capture_view_101", DeliveryLedger.Completed);
            Go(r, "view_102", DeliveryLedger.InProgress); Go(r, "view_102", DeliveryLedger.Completed);
            Go(r, "dimensions_102", DeliveryLedger.InProgress);
            Go(r, "dimensions_102", DeliveryLedger.Completed, new JObject { ["idempotency_key"] = "k-dims", ["element_ids"] = new JArray(9002) });
            Go(r, "capture_view_102", DeliveryLedger.InProgress); Go(r, "capture_view_102", DeliveryLedger.Completed);
            Go(r, "pack", DeliveryLedger.InProgress);
            Go(r, "pack", DeliveryLedger.Completed, new JObject { ["idempotency_key"] = "k-pack", ["element_ids"] = new JArray(9003) });
            Go(r, "audit", DeliveryLedger.InProgress);
            Go(r, "audit", DeliveryLedger.Completed, new JObject { ["no_blocking_findings"] = true });
            Go(r, "capture_sheet_201", DeliveryLedger.InProgress);
            Go(r, "capture_sheet_201", DeliveryLedger.AwaitingApproval);
            record = r;
            return DeliveryLedger.PublishGate(r);
        }

        [Fact]
        public void TheGateStaysShutUntilEverySheetIsApproved()
        {
            JObject r;
            JObject gate = RunToPublishGate(out r);
            Assert.False(gate.Value<bool>("open"));
            Assert.Contains(gate["reasons"].Values<string>(), s => s.Contains("capture_sheet_201") && s.Contains("awaiting_approval"));

            string why;
            Assert.False(DeliveryLedger.TryTransition(r, "capture_sheet_201", DeliveryLedger.Approved, new JObject(), T0, out why));
            Assert.Contains("identity", why);
            Go(r, "capture_sheet_201", DeliveryLedger.Approved, new JObject { ["identity"] = "reviewer", ["scope"] = new JArray() });
            Assert.True(DeliveryLedger.PublishGate(r).Value<bool>("open"));
        }

        [Fact]
        public void AnAuditWithBlockingFindingsNeverOpensTheGate()
        {
            JObject r = Open();
            foreach (string k in new[] { "view_101", "tags_101", "capture_view_101", "view_102", "dimensions_102", "capture_view_102", "pack" })
            {
                Go(r, k, DeliveryLedger.InProgress);
                Go(r, k, DeliveryLedger.Completed, new JObject { ["idempotency_key"] = "k-" + k });
            }
            Go(r, "audit", DeliveryLedger.InProgress);
            string why;
            Assert.False(DeliveryLedger.TryTransition(r, "audit", DeliveryLedger.Completed, new JObject(), T0, out why));
            Assert.Contains("no_blocking_findings", why);
            Go(r, "audit", DeliveryLedger.Completed, new JObject { ["no_blocking_findings"] = false });
            Go(r, "capture_sheet_201", DeliveryLedger.InProgress); Go(r, "capture_sheet_201", DeliveryLedger.AwaitingApproval);
            Go(r, "capture_sheet_201", DeliveryLedger.Approved, new JObject { ["identity"] = "reviewer" });
            JObject gate = DeliveryLedger.PublishGate(r);
            Assert.False(gate.Value<bool>("open"));
            Assert.Contains(gate["reasons"].Values<string>(), s => s.Contains("blocking findings"));
        }

        [Fact]
        public void InvalidatingACompletedWriteCascadesToEverythingBuiltOnIt()
        {
            JObject r;
            RunToPublishGate(out r);
            Go(r, "capture_sheet_201", DeliveryLedger.Approved, new JObject { ["identity"] = "reviewer" });
            Assert.True(DeliveryLedger.PublishGate(r).Value<bool>("open"));

            List<string> hit = DeliveryLedger.Invalidate(r, "tags_101", "element 9001 changed (VersionGuid v1 -> v2)", T0);
            Assert.Equal(new[] { "tags_101", "capture_view_101", "pack", "audit", "capture_sheet_201" }, hit);
            Assert.Equal(DeliveryLedger.Invalidated, Status(r, "tags_101"));
            Assert.Equal(DeliveryLedger.Invalidated, Status(r, "capture_sheet_201"));
            // The other view's stages are untouched: they did not depend on tags_101.
            Assert.Equal(DeliveryLedger.Completed, Status(r, "dimensions_102"));
            Assert.Equal(DeliveryLedger.Completed, Status(r, "view_101"));
            Assert.False(DeliveryLedger.PublishGate(r).Value<bool>("open"));
            Assert.Equal("element 9001 changed (VersionGuid v1 -> v2)", DeliveryLedger.Stage(r, "pack")["invalidated_facts"].Value<string>("reason"));
            Assert.Equal("tags_101", DeliveryLedger.Stage(r, "pack")["invalidated_facts"].Value<string>("cascaded_from"));
        }

        [Fact]
        public void ResumeNamesTheNextStageAndNeverHandsOutACompletedWrite()
        {
            JObject r = Open();
            Go(r, "view_101", DeliveryLedger.InProgress); Go(r, "view_101", DeliveryLedger.Completed);
            Go(r, "tags_101", DeliveryLedger.InProgress);
            Go(r, "tags_101", DeliveryLedger.Completed, new JObject { ["idempotency_key"] = "k-tags", ["element_ids"] = new JArray(9001),
                ["scope"] = new JArray(new JObject { ["element_id"] = 9001, ["version_guid"] = "v1" }) });
            JObject resume = DeliveryLedger.Resume(r);
            Assert.Equal("capture_view_101", resume["next_stage"].Value<string>("key"));
            JObject write = ((JArray)resume["completed_writes"]).Cast<JObject>().Single();
            Assert.Equal("tags_101", write.Value<string>("key"));
            Assert.Equal("k-tags", write.Value<string>("idempotency_key"));
            Assert.True(write.Value<bool>("never_replay"));
            Assert.Equal("tags_101", ((JArray)resume["needs_reverification"]).Cast<JObject>().Single().Value<string>("key"));
            Assert.False(resume["publish_gate"].Value<bool>("open"));
        }

        [Fact]
        public void ResumeAfterAFailureReportsItAndOffersNothingPastIt()
        {
            JObject r = Open();
            Go(r, "view_101", DeliveryLedger.InProgress); Go(r, "view_101", DeliveryLedger.Completed);
            Go(r, "tags_101", DeliveryLedger.InProgress);
            Go(r, "tags_101", DeliveryLedger.Failed, new JObject { ["reason"] = "coverage incomplete: 1455410" });
            JObject resume = DeliveryLedger.Resume(r);
            // The other view can proceed independently; the failed chain is reported.
            Assert.Equal("view_102", resume["next_stage"].Value<string>("key"));
            JObject attention = ((JArray)resume["needs_attention"]).Cast<JObject>().Single();
            Assert.Equal("tags_101", attention.Value<string>("key"));
            Assert.Contains("1455410", attention.Value<string>("reason"));
            // Re-arming is explicit, and it clears the stale facts.
            Go(r, "tags_101", DeliveryLedger.Pending);
            Assert.Null(DeliveryLedger.Stage(r, "tags_101")["failed_facts"]);
        }

        [Fact]
        public void AStageLeftInProgressByACrashIsInDoubtNotOfferedAndNotAssumed()
        {
            JObject r = Open();
            Go(r, "view_101", DeliveryLedger.InProgress); Go(r, "view_101", DeliveryLedger.Completed);
            Go(r, "tags_101", DeliveryLedger.InProgress);
            // ...and the process died here. Replaying the events yields exactly this record.
            JObject resume = DeliveryLedger.Resume(r);
            JObject doubt = ((JArray)resume["in_doubt"]).Cast<JObject>().Single();
            Assert.Equal("tags_101", doubt.Value<string>("key"));
            Assert.Contains("idempotency_key", doubt.Value<string>("resolution"));
            // Not offered again: the next stage is the other view's, never the one in doubt.
            Assert.Equal("view_102", resume["next_stage"].Value<string>("key"));
            Assert.Empty((JArray)resume["completed_writes"]);
            Assert.False(resume["publish_gate"].Value<bool>("open"));
            Assert.Contains(resume["publish_gate"]["reasons"].Values<string>(), s => s.Contains("tags_101") && s.Contains("in_progress"));
            // Resolution is explicit: completed with its key (the host re-reads ids) or failed.
            string why;
            Assert.False(DeliveryLedger.TryTransition(r, "tags_101", DeliveryLedger.Completed, null, T0, out why));
            Go(r, "tags_101", DeliveryLedger.Failed, new JObject { ["reason"] = "the writer died mid-stage; nothing re-read" });
            Assert.Empty((JArray)DeliveryLedger.Resume(r)["in_doubt"]);
        }

        [Fact]
        public void AnInvalidatedApprovalClosesAnOpenGateUntilANewApprovalIsGiven()
        {
            JObject r;
            RunToPublishGate(out r);
            Go(r, "capture_sheet_201", DeliveryLedger.Approved, new JObject { ["identity"] = "reviewer",
                ["scope"] = new JArray(new JObject { ["element_id"] = 201, ["version_guid"] = "s1" }, new JObject { ["element_id"] = 9003, ["version_guid"] = "vp1" }) });
            Assert.True(DeliveryLedger.PublishGate(r).Value<bool>("open"));
            // The host re-read the scope and found the viewport changed: only the approval is hit.
            List<string> hit = DeliveryLedger.Invalidate(r, "capture_sheet_201", "element 9003 changed (VersionGuid vp1 -> vp2)", T0);
            Assert.Equal(new[] { "capture_sheet_201" }, hit);
            Assert.Equal(DeliveryLedger.Invalidated, Status(r, "capture_sheet_201"));
            Assert.Equal(DeliveryLedger.Completed, Status(r, "audit"));
            JObject gate = DeliveryLedger.PublishGate(r);
            Assert.False(gate.Value<bool>("open"));
            Assert.Contains(gate["reasons"].Values<string>(), s => s.Contains("capture_sheet_201") && s.Contains("invalidated"));
            // A stale approval can never be reused: the stage is re-armed and approved afresh.
            string why;
            Assert.False(DeliveryLedger.TryTransition(r, "capture_sheet_201", DeliveryLedger.Approved, new JObject { ["identity"] = "reviewer" }, T0, out why));
            Go(r, "capture_sheet_201", DeliveryLedger.Pending);
            Go(r, "capture_sheet_201", DeliveryLedger.InProgress);
            Go(r, "capture_sheet_201", DeliveryLedger.AwaitingApproval);
            Go(r, "capture_sheet_201", DeliveryLedger.Approved, new JObject { ["identity"] = "reviewer", ["scope"] = new JArray() });
            Assert.True(DeliveryLedger.PublishGate(r).Value<bool>("open"));
            // And the history keeps both decisions.
            Assert.True(((JArray)DeliveryLedger.Stage(r, "capture_sheet_201")["history"]).Count >= 6);
        }

        [Fact]
        public void ReplayAfterACrashReproducesTheInDoubtStage()
        {
            JObject r = Open();
            var lines = new List<string> { DeliveryLedger.OpenedEvent(r).ToString(Newtonsoft.Json.Formatting.None) };
            Go(r, "view_101", DeliveryLedger.InProgress); lines.Add(DeliveryLedger.TransitionEvent("view_101", DeliveryLedger.InProgress, null, T0).ToString(Newtonsoft.Json.Formatting.None));
            Go(r, "view_101", DeliveryLedger.Completed); lines.Add(DeliveryLedger.TransitionEvent("view_101", DeliveryLedger.Completed, null, T0).ToString(Newtonsoft.Json.Formatting.None));
            Go(r, "tags_101", DeliveryLedger.InProgress); lines.Add(DeliveryLedger.TransitionEvent("tags_101", DeliveryLedger.InProgress, null, T0).ToString(Newtonsoft.Json.Formatting.None));
            JObject replayed = DeliveryLedger.Replay(lines);
            Assert.Equal("tags_101", ((JArray)DeliveryLedger.Resume(replayed)["in_doubt"]).Cast<JObject>().Single().Value<string>("key"));
            Assert.Empty((JArray)replayed["replay_problems"]);
        }

        [Fact]
        public void ASecondPublicationClosesTheGate()
        {
            JObject r;
            RunToPublishGate(out r);
            Go(r, "capture_sheet_201", DeliveryLedger.Approved, new JObject { ["identity"] = "reviewer" });
            Go(r, "publish", DeliveryLedger.InProgress);
            Go(r, "publish", DeliveryLedger.Completed, new JObject { ["files"] = new JArray() });
            JObject gate = DeliveryLedger.PublishGate(r);
            Assert.False(gate.Value<bool>("open"));
            Assert.Contains(gate["reasons"].Values<string>(), s => s.Contains("already completed"));
        }

        [Fact]
        public void EventsReplayIntoTheSameRecordAndProblemsAreReportedNotSkipped()
        {
            JObject r = Open();
            var lines = new List<string> { DeliveryLedger.OpenedEvent(r).ToString(Newtonsoft.Json.Formatting.None) };
            Go(r, "view_101", DeliveryLedger.InProgress);
            lines.Add(DeliveryLedger.TransitionEvent("view_101", DeliveryLedger.InProgress, null, T0).ToString(Newtonsoft.Json.Formatting.None));
            Go(r, "view_101", DeliveryLedger.Completed);
            lines.Add(DeliveryLedger.TransitionEvent("view_101", DeliveryLedger.Completed, null, T0).ToString(Newtonsoft.Json.Formatting.None));
            lines.Add("{not json");
            lines.Add(DeliveryLedger.TransitionEvent("tags_101", DeliveryLedger.Completed, null, T0).ToString(Newtonsoft.Json.Formatting.None)); // illegal
            JObject replayed = DeliveryLedger.Replay(lines);
            Assert.Equal(DeliveryLedger.Completed, Status(replayed, "view_101"));
            Assert.Equal(DeliveryLedger.Pending, Status(replayed, "tags_101"));
            JArray problems = (JArray)replayed["replay_problems"];
            Assert.Equal(2, problems.Count);
            Assert.Contains("unparseable", problems[0].Value<string>("problem"));
            Assert.Contains("cannot go from pending to completed", problems[1].Value<string>("problem"));
            Assert.Equal(5, replayed.Value<int>("replayed_lines"));
        }

        [Fact]
        public void PersistenceGoesThroughTheSinkAndComesBack()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz-ledger-" + Guid.NewGuid().ToString("N"));
            try
            {
                JObject r = Open("d-persist");
                DeliveryLedger.AppendEvent(FileJobSink.Instance, dir, "d-persist", DeliveryLedger.OpenedEvent(r));
                DeliveryLedger.AppendEvent(FileJobSink.Instance, dir, "d-persist", DeliveryLedger.TransitionEvent("view_101", DeliveryLedger.InProgress, null, T0));
                JObject back = DeliveryLedger.Load(dir, "d-persist");
                Assert.Equal(DeliveryLedger.InProgress, Status(back, "view_101"));
                Assert.Empty((JArray)back["replay_problems"]);
                Assert.Null(DeliveryLedger.Load(dir, "never-opened"));
            }
            finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        }

        [Fact]
        public void DeliveryIdsAreValidatedAndDerivable()
        {
            Assert.Throws<ArgumentException>(() => DeliveryLedger.Open(Plan(), null, null, "../escape", T0));
            Assert.Throws<ArgumentException>(() => DeliveryLedger.Open(Plan(), null, null, "", T0));
            string a = DeliveryLedger.DeriveId("sha-a", "fp-1"), b = DeliveryLedger.DeriveId("sha-a", "fp-1"), c = DeliveryLedger.DeriveId("sha-b", "fp-1");
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.StartsWith("delivery-", a);
        }
    }
}
