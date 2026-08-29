// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The fitting pair choice. A fitting lands in somebody's deliverable, so the
// choice is either unique and measured or it is a refusal that names why -
// every branch here is a case a live model would make expensive to stage.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MepRulesTests
    {
        private static ConnectorFact C(int id, double xMm, double yMm, bool connected = false,
                                       string domain = "piping", double dx = 1, double dy = 0)
        {
            return new ConnectorFact
            {
                Id = id, X = xMm / 304.8, Y = yMm / 304.8, Z = 0,
                IsConnected = connected, Domain = domain, DirX = dx, DirY = dy, DirZ = 0
            };
        }

        private static bool Select(IList<ConnectorFact> a, IList<ConnectorFact> b,
                                   out ConnectorFact ca, out ConnectorFact cb, out string code, out string reason,
                                   int? namedA = null, int? namedB = null)
            => MepRules.SelectPair(a, b, namedA, namedB, out ca, out cb, out code, out reason);

        [Fact]
        public void The_unique_coincident_open_pair_is_chosen()
        {
            var a = new[] { C(1, 0, 0), C(2, 6000, 0, connected: true) };
            var b = new[] { C(7, 0, 0.2), C(8, 9000, 0) };
            Assert.True(Select(a, b, out var ca, out var cb, out _, out _));
            Assert.Equal(1, ca.Id);
            Assert.Equal(7, cb.Id);
        }

        [Fact]
        public void A_connected_connector_is_never_a_candidate()
        {
            // The only coincident pair involves a CONNECTED connector; choosing it
            // would stack a second fitting onto a joint that already has one.
            var a = new[] { C(1, 0, 0, connected: true), C(2, 6000, 0) };
            var b = new[] { C(7, 0, 0) };
            Assert.False(Select(a, b, out _, out _, out string code, out string reason));
            Assert.Equal(MepRules.CodeNotCoincident, code);
            Assert.Contains("mm apart", reason);
        }

        [Fact]
        public void No_open_connector_names_the_side_and_the_count()
        {
            var a = new[] { C(1, 0, 0, connected: true) };
            var b = new[] { C(7, 0, 0) };
            Assert.False(Select(a, b, out _, out _, out string code, out string reason));
            Assert.Equal(MepRules.CodeNoOpenConnector, code);
            Assert.Contains("first element", reason);
            Assert.Contains("1 connector(s)", reason);
        }

        [Fact]
        public void Two_pairs_within_one_tolerance_refuse_as_ambiguous()
        {
            // Both of B's open ends sit essentially on A's end: distance ties.
            var a = new[] { C(1, 0, 0) };
            var b = new[] { C(7, 0.3, 0), C(8, 0, 0.3) };
            Assert.False(Select(a, b, out _, out _, out string code, out string reason));
            Assert.Equal(MepRules.CodeAmbiguousPair, code);
            Assert.Contains("Name the connectors", reason);
        }

        [Fact]
        public void A_clear_winner_beats_a_distant_runner_up()
        {
            var a = new[] { C(1, 0, 0) };
            var b = new[] { C(7, 0.2, 0), C(8, 5000, 0) };
            Assert.True(Select(a, b, out var ca, out var cb, out _, out _));
            Assert.Equal(7, cb.Id);
        }

        [Fact]
        public void Different_domains_refuse_by_name()
        {
            var a = new[] { C(1, 0, 0, domain: "piping") };
            var b = new[] { C(7, 0, 0, domain: "cable_tray") };
            Assert.False(Select(a, b, out _, out _, out string code, out string reason));
            Assert.Equal(MepRules.CodeDomainMismatch, code);
            Assert.Contains("piping", reason);
            Assert.Contains("cable_tray", reason);
        }

        [Fact]
        public void Distant_open_ends_refuse_naming_the_measured_millimetres()
        {
            var a = new[] { C(1, 0, 0) };
            var b = new[] { C(7, 250, 0) };
            Assert.False(Select(a, b, out _, out _, out string code, out string reason));
            Assert.Equal(MepRules.CodeNotCoincident, code);
            Assert.Contains("250.0 mm", reason);
        }

        [Fact]
        public void Naming_a_connector_narrows_that_side()
        {
            // Without names this would be ambiguous; naming B's connector decides it.
            var a = new[] { C(1, 0, 0) };
            var b = new[] { C(7, 0.3, 0), C(8, 0, 0.3) };
            Assert.True(Select(a, b, out _, out var cb, out _, out _, namedB: 8));
            Assert.Equal(8, cb.Id);
        }

        [Fact]
        public void Naming_a_connected_connector_is_a_refusal_not_a_reconnection()
        {
            var a = new[] { C(1, 0, 0) };
            var b = new[] { C(7, 0, 0, connected: true) };
            Assert.False(Select(a, b, out _, out _, out string code, out string reason, namedB: 7));
            Assert.Equal(MepRules.CodeNoOpenConnector, code);
            Assert.Contains("id 7", reason);
        }

        [Fact]
        public void The_turn_angle_is_measured_between_flows_not_between_outward_bases()
        {
            // Two curves meeting head-on: outward directions antiparallel -> 0 degree turn.
            var straightA = C(1, 0, 0, dx: 1, dy: 0);
            var straightB = C(2, 0, 0, dx: -1, dy: 0);
            Assert.Equal(0.0, MepRules.AngleDegrees(straightA, straightB), 6);

            // A right-angle corner: outward directions at 90 -> a 90 degree turn.
            var cornerA = C(1, 0, 0, dx: 1, dy: 0);
            var cornerB = C(2, 0, 0, dx: 0, dy: 1);
            Assert.Equal(90.0, MepRules.AngleDegrees(cornerA, cornerB), 6);
        }
    }
}
