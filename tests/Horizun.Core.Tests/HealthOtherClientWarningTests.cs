// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_health warns when a second MCP client is connected to the SAME Revit
// (feedback §9). The warning is a single top-level sentence so detail=compact keeps it.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HealthOtherClientWarningTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Alone_on_the_revit_there_is_no_warning()
        {
            var p = new ClientPresence();
            p.Seen(100, T0);
            Assert.Null(ConcurrentClientRule.HealthWarning(p.Take(T0), pid => true));
        }

        [Fact]
        public void A_second_live_client_is_named_with_its_pid_and_the_advice()
        {
            var p = new ClientPresence();
            p.Seen(200, T0.AddSeconds(-30));
            p.Seen(100, T0);                                   // the caller: most recent
            string w = ConcurrentClientRule.HealthWarning(p.Take(T0), pid => true);
            Assert.NotNull(w);
            Assert.Contains("Another MCP client is connected to this same Revit", w);
            Assert.Contains("200", w);
            Assert.DoesNotContain("pid 100", w);                       // the caller is not listed as "the other"
            Assert.Contains("target_document", w);
        }

        [Fact]
        public void A_client_whose_process_is_gone_does_not_raise_it_but_an_unknown_one_does()
        {
            var p = new ClientPresence();
            p.Seen(200, T0.AddSeconds(-30));
            p.Seen(100, T0);
            Assert.Null(ConcurrentClientRule.HealthWarning(p.Take(T0), pid => false));
            Assert.NotNull(ConcurrentClientRule.HealthWarning(p.Take(T0), pid => null));
        }

        [Fact]
        public void A_client_outside_the_ten_minute_window_is_forgotten()
        {
            var p = new ClientPresence();
            p.Seen(200, T0.AddMinutes(-11));
            p.Seen(100, T0);
            Assert.Null(ConcurrentClientRule.HealthWarning(p.Take(T0), pid => true));
        }
    }
}
