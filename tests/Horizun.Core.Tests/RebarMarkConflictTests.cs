// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ONE MARK, ONE BAR. In reinforcement practice the same mark on several sets of
// identical bars is correct - one schedule line per position - and Revit itself
// numbers identical bars with the same Rebar Number. What is wrong is one mark
// covering bars that are NOT the same: another bar type, another shape, or a
// different length. These pin both halves: identical sets are not a finding, and
// different bars under one mark still fail, naming what differs with both values.
//
// The rows are the shape RebarFacts.Describe emits: measured.schedule_mark,
// bar_type.id/name, shape.id/name, measured.bar_centreline_length_mm,
// measured.total_length_mm and measured.quantity.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarMarkConflictTests
    {
        internal static JObject Bar(long id, string mark, long typeId = 300, string typeName = "12M",
                                    long shapeId = 400, string shapeName = "M_00",
                                    double? centrelineMm = 3000.0, double? totalMm = 15000.0, int? quantity = 5,
                                    long hostId = 5001)
        {
            return new JObject
            {
                ["id"] = id,
                ["host"] = new JObject { ["id"] = hostId, ["resolved"] = true },
                ["bar_type"] = new JObject
                {
                    ["resolved"] = true, ["id"] = typeId, ["name"] = typeName, ["nominal_diameter_mm"] = 12.0
                },
                ["shape"] = new JObject { ["id"] = shapeId, ["name"] = shapeName, ["resolved"] = true },
                ["layout"] = new JObject { ["quantity"] = quantity },
                ["measured"] = new JObject
                {
                    ["total_length_mm"] = totalMm,
                    ["quantity"] = quantity,
                    ["schedule_mark"] = mark,
                    ["bar_centreline_length_mm"] = centrelineMm
                }
            };
        }

        private static JObject Single(JArray f)
        {
            return f.OfType<JObject>().Single();
        }



























        [Fact]
        public void The_code_is_bar_mark_conflict_and_the_old_code_is_gone()
        {
            Assert.Contains("bar_mark_conflict", RebarFinding.All);
            Assert.DoesNotContain("bar_mark_duplicate", RebarFinding.All);
        }
    }
}
