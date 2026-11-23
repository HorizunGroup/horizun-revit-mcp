using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

// Only the host-dependent surfaces are substituted; the options parser and budget
// implementation under test are the production sources linked into this project.
namespace Autodesk.Revit.DB
{
    public partial class Document
    {
        public string Title { get; set; }
        public bool IsValidObject { get; set; } = true;
        public bool IsFamilyDocument { get; set; }
        public bool IsWorkshared { get; set; }
        public bool IsModified { get; set; }
        public string PathName { get; set; }
        public System.Collections.Generic.List<RevitLinkInstance> TestLinkDocuments { get; } = new System.Collections.Generic.List<RevitLinkInstance>();
        public int TestElements { get; set; }
        public int TestTypes { get; set; }
        public int TestLinks { get; set; }
        public int TestWorksets { get; set; }
        public int TestNativeCounts { get; set; }
        public bool TestCountThrows { get; set; }
    }
}

namespace Horizun.Revit.Core
{
    public static class QueryCacheLifecycle
    {
        public static bool Ready { get; set; }
        public static long TestEpoch { get; set; }
        public static long ModelEpoch { get; set; }
        internal static event System.Action<Autodesk.Revit.DB.Document> DocumentEditObserved;
        internal static void ObserveFixtureEdit(Autodesk.Revit.DB.Document document) => DocumentEditObserved?.Invoke(document);
        public static TestCache Cache => new TestCache();
        public sealed class TestCache
        {
            public long Epoch => TestEpoch;
            public bool TryGet(string key, long epoch, out JObject result) => throw new System.NotSupportedException();
            public void Store(string key, long epoch, JObject result) => throw new System.NotSupportedException();
        }
    }
}

namespace Horizun.Core.Tests
{
    [CollectionDefinition("CooperativeRead global state", DisableParallelization = true)]
    public class CooperativeReadGlobalStateCollection { }

    [Collection("CooperativeRead global state")]
    public class CooperativeOptionsResponseTests
    {
        [Fact]
        public void Cursor_requires_same_session_and_observed_unchanged_document()
        {
            try
            {
                QueryCacheLifecycle.Ready = true;
                QueryCacheLifecycle.TestEpoch = 41;
                string cursor = CooperativeOptions.MakeCursor(7, "test");
                var request = new JObject { ["cooperative"] = new JObject { ["cursor"] = cursor } };
                Assert.Equal(7, CooperativeOptions.Read(request, "test").StartAt);
                QueryCacheLifecycle.TestEpoch++;
                Assert.NotNull(CooperativeOptions.Read(request, "test").Refusal);
                QueryCacheLifecycle.TestEpoch--;
                QueryCacheLifecycle.Ready = false;
                Assert.NotNull(CooperativeOptions.Read(request, "test").Refusal);
                QueryCacheLifecycle.Ready = true;
                string raw = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(cursor));
                string[] parts = raw.Split('|');
                parts[3] = "different-process";
                request["cooperative"]["cursor"] = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts)));
                Assert.NotNull(CooperativeOptions.Read(request, "test").Refusal);
                request["cooperative"]["cursor"] = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("7|test|41"));
                Assert.NotNull(CooperativeOptions.Read(request, "test").Refusal);
            }
            finally { QueryCacheLifecycle.Ready = false; QueryCacheLifecycle.TestEpoch = 0; }
        }
        [Fact]
        public void Interrupted_collection_cannot_report_complete_coverage_or_resumption()
        {
            var result = JObject.Parse("{\"coverage_complete\":true,\"cooperative\":{\"cursor_means\":\"continue\"}}");
            QueryResponseOptions.ApplyCollectionCoverage(result, false);
            Assert.False((bool)result["coverage_complete"]);
            Assert.True((bool)result["partial"]);
            Assert.False((bool)result["cooperative"]["collection_continuation_available"]);
            Assert.Contains("cannot resume", (string)result["cooperative"]["cursor_means"]);
        }

        [Fact]
        public void Abandoned_read_does_not_count_an_unexamined_unit()
        {
            var scope = CooperativeRead.Begin("synthetic", 60000);
            CooperativeRead.Abandoned = () => true;
            try
            {
                Assert.False(scope.ContinueWithin(10));
                Assert.Equal(0, scope.Done);
                Assert.Equal("client_abandoned", (string)scope.Report()["stopped_because"]);
                Assert.False(scope.ContinueWithin(10));
                Assert.Equal(0, scope.Done);
            }
            finally { CooperativeRead.Abandoned = null; }
        }

        [Fact]
        public void Unit_limit_marks_partial_only_when_another_unit_exists()
        {
            var scope = CooperativeRead.Begin("synthetic", 60000);
            int examined = 0;
            for (int i = 0; i < 100000; i++)
            {
                if (!scope.ContinueWithin(3)) break;
                examined++;
            }
            Assert.Equal(3, examined);
            Assert.Equal(3, scope.Done);
            Assert.False(scope.Complete);
            Assert.Equal("max_units_exhausted", (string)scope.Report()["stopped_because"]);
            Assert.Contains("unit limit", (string)scope.Report()["means"]);

            var exact = CooperativeRead.Begin("exact", 60000);
            for (int i = 0; i < 3; i++) Assert.True(exact.ContinueWithin(3));
            Assert.True(exact.Complete);
        }

        [Fact]
        public void First_page_honours_requested_response_limit()
        {
            var request = JObject.Parse("{\"cooperative\":{\"max_units\":1000},\"max_response_bytes\":40}");
            var options = CooperativeOptions.Read(request, "test");
            Assert.Equal(40, options.ResponseCeiling);
            Assert.True(options.ResponseTooLarge(250, new JArray(new string('x', 40)), out int bytes));
            Assert.True(bytes >= 40);
        }

        [Fact]
        public void Limit_counts_utf8_bytes_and_clamps_large_values()
        {
            var request = JObject.Parse("{\"cooperative\":{},\"max_response_bytes\":8}");
            var options = CooperativeOptions.Read(request, "test");
            Assert.True(options.ResponseTooLarge(250, new JValue("ééé"), out int bytes));
            Assert.Equal(8, bytes);
            request["max_response_bytes"] = int.MaxValue;
            Assert.Equal(CooperativeOptions.HardMaxResponseBytes, CooperativeOptions.Read(request, "test").ResponseCeiling);
        }
    }
}
