// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A PAGE IS CUT FROM THE ANALYSIS IT NAMES, NOT FROM A LOOK-ALIKE.
//
// MEASURED (campaign 7, M106): each page of horizun_cad_networks re-read the
// drawing and re-ran the analysis, 1.5-2.2 s for four rows. The cache key is
// what decides whether a page may be cut from a kept analysis: the paging
// arguments must not change it, and everything the analysis depends on must.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadNetworkCacheTests
    {
        private static JObject Req(string extra = "") => JObject.Parse(
            "{ \"instance_id\": 977974, \"target_document\": \"HZ\", \"connect_tolerance_mm\": 25.4, " +
            "\"requirement_set\": { \"id\": \"m106\", \"version\": \"2.1.0\" }" + extra + " }");

        private static string Key(JObject r, string sha = "abc", string transform = "t1", string path = @"C:\m.rvt") =>
            CadNetworkCache.Key(path, "HZ", 977974, transform, sha, r);

        [Fact]
        public void The_paging_arguments_do_not_change_which_analysis_a_page_belongs_to()
        {
            string first = Key(Req());
            Assert.StartsWith("netc:", first);
            Assert.Equal(first, Key(Req(", \"page_offset\": 8, \"page_limit\": 4, \"lists\": [\"runs\"], " +
                                        "\"expect_analysis_fingerprint\": \"net:1\", \"idempotency_key\": \"k\"")));
        }

        [Fact]
        public void Everything_the_analysis_depends_on_does()
        {
            string first = Key(Req());
            Assert.NotEqual(first, Key(Req(), sha: "abd"));                              // the file's bytes
            Assert.NotEqual(first, Key(Req(), transform: "t2"));                         // where the link sits
            Assert.NotEqual(first, Key(Req(), path: @"C:\other.rvt"));                   // the document
            Assert.NotEqual(first, CadNetworkCache.Key(@"C:\m.rvt", "HZ", 977974, "t1", "abc", Req(), 1));  // any model change since
            Assert.NotEqual(first, Key(Req(", \"view_id\": 32")));                       // a view-dependent read
            JObject otherRules = Req();
            otherRules["requirement_set"]["version"] = "2.2.0";
            Assert.NotEqual(first, Key(otherRules));                                     // the rules
            JObject otherTolerance = Req();
            otherTolerance["connect_tolerance_mm"] = 30;
            Assert.NotEqual(first, Key(otherTolerance));                                 // any other argument
        }

        [Fact]
        public void No_key_without_the_file_or_the_placement_so_nothing_is_cut_blind()
        {
            Assert.Null(Key(Req(), sha: null));
            Assert.Null(Key(Req(), transform: null));
            Assert.Null(CadNetworkCache.Key(@"C:\m.rvt", "HZ", 1, "t", "s", null));
            // a continued snapshot says exactly that, and says it did NOT re-read the sources
            JObject hit = CadNetworkCache.HitBlock("netc:x", "2026-09-20T00:00:00Z");
            Assert.Equal("continued_snapshot", hit.Value<string>("state"));
            Assert.False(hit.Value<bool>("sources_checked"));
            Assert.Equal("continue_the_named_snapshot", hit.Value<string>("contract"));
            Assert.Equal("2026-09-20T00:00:00Z", hit.Value<string>("snapshot_taken_utc"));
            Assert.Contains("no external reference was checked", hit.Value<string>("means"));
        }
    }
}
