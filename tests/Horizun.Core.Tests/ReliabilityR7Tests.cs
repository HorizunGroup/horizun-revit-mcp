using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReliabilityR7Tests
    {


        [Fact]
        public void An_unreadable_category_makes_the_view_and_batch_partial()
        {
            var good = new JObject { ["view_id"] = 300, ["errors"] = new JArray(), ["category_warnings"] = new JArray() };
            var failedCategory = new JObject
            {
                ["view_id"] = 301, ["errors"] = new JArray(), ["category_warnings"] = new JArray("Category 7: unreadable override")
            };
            Assert.True(ViewVisibilityRules.StampGraphicsCoverage(new JArray(good)));
            Assert.True(good.Value<bool>("complete"));
            Assert.False(ViewVisibilityRules.StampGraphicsCoverage(new JArray(good, failedCategory)));
            Assert.True(good.Value<bool>("complete"));
            Assert.False(failedCategory.Value<bool>("complete"));
            Assert.Single((JArray)failedCategory["category_warnings"]);
        }

    }
}
