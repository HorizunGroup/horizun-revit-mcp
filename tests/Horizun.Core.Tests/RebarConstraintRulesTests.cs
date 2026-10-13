using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    // ACERO 3.0, stage 1c: the constraints Revit put on a bar's handles, read and typed (query_structure
    // mode=rebar include_constraints). MEASURED on the anonymized regression case (Revit 2024): a column bar declared 6.7 mm inside
    // the cover of the beam it runs into came back 11.45 mm off its plan. Which element Revit pinned the handle
    // to is the evidence, so the read names every handle whose constraint targets ANOTHER element than the host.
    public class RebarConstraintRulesTests
    {
        private static RebarConstraintRules.Handle H(string name, string type, long? target, bool toCover = false,
            double? hostFaceMm = null, double? coverMm = null)
            => new RebarConstraintRules.Handle { Name = name, ConstraintType = type, TargetElementId = target,
                ToCover = toCover, DistanceToHostFaceMm = hostFaceMm, DistanceToCoverMm = coverMm };

        [Fact]
        public void A_handle_pinned_to_another_element_than_the_host_is_named()
        {
            var handles = new List<RebarConstraintRules.Handle>
            {
                H("Edge 0", "ToCover", 794950, true, coverMm: 0),
                H("Edge 2", "ToCover", 841673, true, coverMm: 0),
                H("Start of Bar", "FixedDistanceToHostFace", 794950, hostFaceMm: 52.4)
            };
            RebarConstraintRules.Summary s = RebarConstraintRules.Summarise(794950, handles);
            Assert.Equal(3, s.Handles);
            Assert.Equal(3, s.Constrained);
            Assert.Equal(new long[] { 841673 }, s.OtherTargets);
            Assert.Equal(new[] { "Edge 2" }, s.HandlesOnOtherTargets);
            Assert.Equal(2, s.ToCover);
        }

        [Fact]
        public void A_handle_without_a_constraint_is_counted_as_unconstrained_never_as_on_the_host()
        {
            var handles = new List<RebarConstraintRules.Handle> { H("Edge 1", null, null), H("Edge 0", "ToCover", 7, true) };
            RebarConstraintRules.Summary s = RebarConstraintRules.Summarise(7, handles);
            Assert.Equal(2, s.Handles);
            Assert.Equal(1, s.Constrained);
            Assert.Equal(new[] { "Edge 1" }, s.Unconstrained);
            Assert.Empty(s.OtherTargets);
        }

        [Fact]
        public void The_distance_reported_is_the_one_the_constraint_type_owns()
        {
            Assert.Equal(12.5, RebarConstraintRules.Distance(H("a", "ToCover", 1, true, 99, 12.5)));
            Assert.Equal(99, RebarConstraintRules.Distance(H("a", "FixedDistanceToHostFace", 1, false, 99, 12.5)));
            Assert.Null(RebarConstraintRules.Distance(H("a", "ToOtherRebar", 1)));
            Assert.Null(RebarConstraintRules.Distance(H("a", null, null)));
        }

        [Fact]
        public void Feet_become_millimetres_rounded_to_a_thousandth()
        {
            Assert.Equal(304.8, RebarConstraintRules.FeetToMm(1.0));
            Assert.Equal(11.452, RebarConstraintRules.FeetToMm(11.4521 / 304.8));
            Assert.Null(RebarConstraintRules.FeetToMm(null));
        }
    }
}
