// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ClientPresence (story 5.16): the registry behind other_clients_connected.
// The rules worth pinning are the ones a live two-agent machine will not
// reproduce on demand: the minus-one never going negative, the window pruning
// yesterday's session, and an unreadable pid staying visible as a lower-bound
// warning instead of vanishing.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class ClientPresenceTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void The_caller_alone_reads_as_zero_others()
        {
            var p = new ClientPresence();
            p.Seen(100, T0);
            var snap = p.Take(T0);
            Assert.Single(snap.Clients);
            Assert.Equal(0, snap.OtherThanCaller);
        }

        [Fact]
        public void A_second_client_is_the_field_reports_measured_case()
        {
            var p = new ClientPresence();
            p.Seen(100, T0);                                   // the asking client
            p.Seen(200, T0.AddSeconds(-30));                   // the other agent
            var snap = p.Take(T0);
            Assert.Equal(2, snap.Clients.Count);
            Assert.Equal(1, snap.OtherThanCaller);
        }

        [Fact]
        public void Repeat_requests_from_one_pid_stay_one_client()
        {
            var p = new ClientPresence();
            for (int i = 0; i < 50; i++) p.Seen(100, T0.AddSeconds(i));
            Assert.Single(p.Take(T0.AddSeconds(50)).Clients);
        }

        [Fact]
        public void An_empty_registry_never_answers_minus_one()
        {
            // Health can only run over a recorded connection, but the floor is a
            // property of the rule, not of the one caller we know about today.
            var snap = new ClientPresence().Take(T0);
            Assert.Empty(snap.Clients);
            Assert.Equal(0, snap.OtherThanCaller);
        }

        [Fact]
        public void Yesterdays_session_is_pruned_by_the_window()
        {
            var p = new ClientPresence();
            p.Seen(100, T0 - ClientPresence.Window - TimeSpan.FromSeconds(1));
            p.Seen(200, T0);
            var snap = p.Take(T0);
            Assert.Single(snap.Clients);
            Assert.Equal(200, snap.Clients[0].Pid);
            Assert.Equal(0, snap.OtherThanCaller);
        }

        [Fact]
        public void A_client_exactly_at_the_window_edge_is_kept()
        {
            var p = new ClientPresence();
            p.Seen(100, T0 - ClientPresence.Window);
            Assert.Single(p.Take(T0).Clients);
        }

        [Fact]
        public void Most_recent_client_comes_first()
        {
            var p = new ClientPresence();
            p.Seen(100, T0.AddSeconds(-60));
            p.Seen(200, T0);
            var snap = p.Take(T0);
            Assert.Equal(200, snap.Clients[0].Pid);
            Assert.Equal(100, snap.Clients[1].Pid);
        }

        [Fact]
        public void An_unreadable_pid_is_counted_and_makes_the_note_say_lower_bound()
        {
            var p = new ClientPresence();
            p.Seen(100, T0);
            p.SeenUnidentified(T0);
            var snap = p.Take(T0);
            Assert.Equal(1, snap.UnidentifiedInWindow);
            Assert.Contains("LOWER BOUND", snap.UnidentifiedNote());
        }

        [Fact]
        public void With_every_pid_readable_the_note_is_silent()
        {
            var p = new ClientPresence();
            p.Seen(100, T0);
            Assert.Equal("", p.Take(T0).UnidentifiedNote());
        }

        [Fact]
        public void Unidentified_connections_age_out_with_the_same_window()
        {
            var p = new ClientPresence();
            p.SeenUnidentified(T0 - ClientPresence.Window - TimeSpan.FromSeconds(1));
            Assert.Equal(0, p.Take(T0).UnidentifiedInWindow);
        }
    }
}
