using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FieldCallBehaviorTests
    {
        [Fact]
        public void Cloud_open_defaults_reach_central_refusal_with_detach_retry()
        {
            var request = JObject.Parse(@"{'cloud_project_guid':'11111111-1111-1111-1111-111111111111','cloud_model_guid':'22222222-2222-2222-2222-222222222222','cloud_region':'US','on_open_dialog':'cancel','idempotency_key':'33333333-3333-3333-3333-333333333333'}");
            var dialog = OpenDialogPolicy.Parse(request.Value<string>("on_open_dialog"), out var error);
            var intent = new OpenIntent { Detach = request.Value<bool?>("detach") ?? false, OpenCentral = request.Value<bool?>("open_central") ?? false };
            Assert.Null(error);
            Assert.Null(OpenRecoveryPolicy.OpenRefusal(dialog == DialogAnswer.ResolveInMemory, intent.Detach));
            var facts = new OpenFacts { IsCloud = true, IsCentral = true, HostVersion = "2026" };
            var verdict = OpenDecision.Decide(facts, intent);
            Assert.False(verdict.Ok);
            Assert.True(FieldCallRules.CloudOpenRetry(facts, intent, verdict).Value<bool>("detach"));
        }

        [Fact]
        public void Activate_target_document_resolves_title_and_path_and_rejects_ambiguity()
        {
            string title = new string('a', 32);
            var docs = new List<DocIdentity> { new DocIdentity { Title = title, Path = @"C:\Models\model.rvt" }, new DocIdentity { Title = "other", Path = @"C:\Models\other.rvt" } };
            foreach (string target in new[] { title, @"C:\Models\model.rvt" })
            {
                var request = new JObject { ["operation"] = "activate", ["target_document"] = target };
                var match = DocumentMatcher.FindTarget(docs, request.Value<string>("target_document"));
                Assert.Equal(DocMatchOutcome.Matched, match.Outcome);
                Assert.Equal(0, match.Index);
            }
            docs.Add(new DocIdentity { Title = title, Path = @"C:\Other\model.rvt" });
            Assert.Equal(DocMatchOutcome.Ambiguous, DocumentMatcher.FindTarget(docs, title).Outcome);
        }

        [Fact]
        public void Save_without_operation_defaults_local()
        {
            var targeted = new JObject { ["target_document"] = "model" };
            Assert.Equal("save_local", FieldCallRules.SaveOperation(targeted));
            var compact = new JObject { ["compact"] = true };
            Assert.Equal("save_local", FieldCallRules.SaveOperation(compact));
        }

        [Fact]
        public void Capture_non_raster_schedule_offers_unique_sheet()
        {
            var request = new JObject { ["view_id"] = 42 };
            Assert.Equal  (42, request.Value<int>("view_id"));
            Assert.Equal(77, FieldCallRules.CaptureSheetRetry(new long[] { 77, 77 }).Value<long>("view_id"));
            Assert.Null(FieldCallRules.CaptureSheetRetry(new long[] { 77, 78 }));
            Assert.Null(FieldCallRules.CaptureSheetRetry(new long[0]));
        }
    }
}
