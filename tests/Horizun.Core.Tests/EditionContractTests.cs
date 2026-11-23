using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Xunit;

namespace Horizun.Core.Tests
{
    public class EditionContractTests
    {

        [Fact]
        public void The_pro_build_keeps_every_tool()
        {
            var all = Contract.All.ToList();
            Assert.Equal(all.Count, Edition.Filter(all).Count);
        }

        [Fact]
        public void The_community_frontier_keeps_the_community_uses()
        {
            // Queries, quantities, sheets and annotation, simple architecture and structure without reinforcement.
            foreach (string kept in new[]
                     {
                         "horizun_query_model", "horizun_quantities", "horizun_create_schedule", "horizun_manage_views",
                         "horizun_annotate", "horizun_query_planimetry", "horizun_sheet_layout", "horizun_export",
                         "horizun_create_elements", "horizun_transform_elements", "horizun_plan_structure",
                         "horizun_query_structure", "horizun_room_finishes", "horizun_write_params_verified"
                     })
                Assert.DoesNotContain(kept, Edition.ProOnlyTools);
        }
    }
}
