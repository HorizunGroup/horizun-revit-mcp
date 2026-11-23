using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class R9AContractParityTests
    {
        [Fact]
        public void V6a_unplaced_views_arguments_reach_the_real_mode_parser()
        {
            var request = JObject.Parse("{'mode':'unplaced_views','target_document':'Model A','max_rows':5}");
            Assert.Null(PlanimetryInventoryArgs.ParseMode(request, out string mode));
            Assert.Equal("unplaced_views", mode);
        }

        [Fact]
        public void V6a_warnings_arguments_report_the_unrecognized_top()
        {
            var request = JObject.Parse("{'operation':'warnings','target_document':'Model A','top':5}");
            Assert.Equal("warnings: unknown argument top", WarningDetailRules.ParseRequest(request, "Model A", out _, out _, out _));
            request.Remove("top");
            Assert.Null(WarningDetailRules.ParseRequest(request, "Model A", out int offset, out int size, out int budget));
            Assert.Equal((0, 100, 65536), (offset, size, budget));
        }

        [Fact]
        public void V6a_summary_arguments_reach_the_real_geometry_parser()
        {
            // The document name is deliberately anonymized; the remaining values are from V6a.
            var request = JObject.Parse("{'target_document':'Model A','ids':[631418,631497,631587,631685],'measures':['summary'],'unit':'mm'}");
            Assert.True(GeometryMeasureRules.TryParse(request, out var parsed, out var error), error);
            Assert.Equal(new long[] { 631418, 631497, 631587, 631685 }, parsed.Ids);
            Assert.Equal(new[] { "summary" }, parsed.Measures);
            Assert.Equal("mm", parsed.Unit);
        }

        [Fact]
        public void Geometry_measure_enum_matches_the_addin_parser()
        {
            var declared = Contract.Find("horizun_measure_geometry").InputSchema["properties"]["measures"]["items"]["enum"].Values<string>();
            Assert.Equal(MeasureKinds.All.OrderBy(x => x), declared.OrderBy(x => x));
        }

        [Fact]
        public void Planimetry_modes_match_the_addin_dispatch_table()
        {
            var declared = Contract.Find("horizun_query_planimetry").InputSchema["properties"]["mode"]["enum"].Values<string>();
            Assert.Equal(PlanimetryInventoryArgs.Modes.OrderBy(x => x), declared.OrderBy(x => x));
        }
    }
}
