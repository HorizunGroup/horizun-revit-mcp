// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A 96 MB NETWORK ANSWER (measured, campaign 6) is bounded by paging the LISTING,
// never by shrinking the ANALYSIS: the summary counts everything, every page says
// what it is, and the pages together are exactly the whole list.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadNetworkPagingTests
    {
        private static CadNetwork Net(int n)
        {
            var segs = new List<CadSegment>();
            for (int i = 0; i < n; i++)
                segs.Add(new CadSegment(new CadPoint(0, i * 1000), new CadPoint(5000, i * 1000), "M", CadCurveKind.Line, 0, "s" + i));
            return CadNetworkRules.Build(segs, new CadNetworkOptions
            { ConnectToleranceMm = 25.4, IdentityToleranceMm = 25.4, CollinearToleranceDegrees = 2, GapReviewDistanceMm = 250, ThroughToleranceDegrees = 15 });
        }

        [Fact]
        public void Pages_are_declared_the_summary_is_whole_and_pages_add_up()
        {
            CadNetwork net = Net(23);
            var seen = new List<string>();
            int? offset = 0;
            while (offset.HasValue)
            {
                JObject page = CadNetworkPaging.Page(net.ToJson(), new List<string> { "runs" }, offset.Value, 10);
                Assert.Equal(23, page["summary"].Value<int>("runs"));                 // the analysis is whole
                Assert.True(page.Value<bool>("analysis_complete"));
                JObject info = (JObject)page["listing"]["runs"];
                Assert.Equal(23, info.Value<int>("total"));
                seen.AddRange(((JArray)page["runs"]).Select(r => r.Value<string>("id")));
                Assert.False(page.Value<bool>("listing_complete"));                 // a page is never the whole
                offset = info["next_offset"].Type == JTokenType.Null ? (int?)null : info.Value<int>("next_offset");
            }
            Assert.Equal(23, seen.Count);
            Assert.Equal(seen.Count, seen.Distinct().Count());
            Assert.Equal(net.Runs.Select(r => r.Id), seen);                            // deterministic order
        }

        [Fact]
        public void A_small_network_fits_in_one_page_and_says_it_is_complete()
        {
            JObject page = CadNetworkPaging.Page(Net(3).ToJson(), null, 0, 500);
            Assert.True(page.Value<bool>("listing_complete"));
            Assert.Equal(3, ((JArray)page["runs"]).Count);
        }

        [Fact]
        public void Lists_not_asked_for_are_counted_and_marked_not_listed()
        {
            JObject page = CadNetworkPaging.Page(Net(5).ToJson(), new List<string> { "junctions" }, 0, 500);
            Assert.Empty((JArray)page["runs"]);
            Assert.False(page["listing"]["runs"].Value<bool>("listed"));
            Assert.Equal(5, page["listing"]["runs"].Value<int>("total"));
            Assert.True(page["listing"]["runs"].Value<bool>("truncated"));
        }
    }
}
