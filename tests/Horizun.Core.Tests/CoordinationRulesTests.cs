// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The finding state machine and the merge. Every branch is a coordination
// meeting somebody sat through: the clash that came back, the partial run
// that must not close anything, the human verdict detection may not forge.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CoordinationRulesTests
    {
        private static CoordinationDetected Hit(string a = "host||u1", string b = "MEP||u2",
                                                string ca = "Walls", string cb = "Pipes")
            => new CoordinationDetected { SideA = a, SideB = b, CategoryA = ca, CategoryB = cb, PointMm = new[] { 1.0, 2.0, 3.0 } };

        [Fact]
        public void The_same_pair_has_one_identity_whichever_side_found_it()
        {
            Assert.Equal(
                CoordinationRules.FindingId("host||u1", "MEP||u2"),
                CoordinationRules.FindingId("MEP||u2", "host||u1"));
        }

        [Fact]
        public void Different_pairs_have_different_identities()
        {
            Assert.NotEqual(
                CoordinationRules.FindingId("host||u1", "MEP||u2"),
                CoordinationRules.FindingId("host||u1", "MEP||u3"));
        }

        [Fact]
        public void A_new_hit_opens_a_finding_and_a_second_run_persists_it()
        {
            var ledger = new Dictionary<string, CoordinationFinding>();
            var first = CoordinationRules.Merge(ledger, new[] { Hit() }, "t1", runComplete: true, scopeKey: "s1");
            Assert.Equal(1, first.New);
            var second = CoordinationRules.Merge(ledger, new[] { Hit() }, "t2", runComplete: true, scopeKey: "s1");
            Assert.Equal(1, second.Persisting);
            CoordinationFinding f = ledger[CoordinationRules.FindingId("host||u1", "MEP||u2")];
            Assert.Equal(2, f.TimesSeen);
            Assert.Equal("t1", f.FirstSeenUtc);
            Assert.Equal("t2", f.LastSeenUtc);
        }

        [Fact]
        public void A_complete_run_without_the_pair_resolves_it_by_model()
        {
            var ledger = new Dictionary<string, CoordinationFinding>();
            CoordinationRules.Merge(ledger, new[] { Hit() }, "t1", true, "s1");
            var outcome = CoordinationRules.Merge(ledger, new CoordinationDetected[0], "t2", true, "s1");
            Assert.Equal(1, outcome.ResolvedByModel);
            CoordinationFinding f = ledger[CoordinationRules.FindingId("host||u1", "MEP||u2")];
            Assert.Equal(CoordinationRules.StatusResolvedByModel, f.Status);
            Assert.Equal("t2", f.ResolvedUtc);
        }

        [Fact]
        public void A_partial_run_resolves_nothing_and_says_so()
        {
            var ledger = new Dictionary<string, CoordinationFinding>();
            CoordinationRules.Merge(ledger, new[] { Hit() }, "t1", true, "s1");
            var outcome = CoordinationRules.Merge(ledger, new CoordinationDetected[0], "t2", runComplete: false, scopeKey: "s1");
            Assert.Equal(0, outcome.ResolvedByModel);
            Assert.True(outcome.ResolutionSkippedBecausePartial);
            Assert.Equal(CoordinationRules.StatusOpen, ledger[CoordinationRules.FindingId("host||u1", "MEP||u2")].Status);
        }

        [Fact]
        public void A_resolved_finding_that_returns_is_a_regression()
        {
            var ledger = new Dictionary<string, CoordinationFinding>();
            CoordinationRules.Merge(ledger, new[] { Hit() }, "t1", true, "s1");
            CoordinationRules.Merge(ledger, new CoordinationDetected[0], "t2", true, "s1");
            var back = CoordinationRules.Merge(ledger, new[] { Hit() }, "t3", true, "s1");
            Assert.Equal(1, back.Regressions);
            CoordinationFinding f = ledger[CoordinationRules.FindingId("host||u1", "MEP||u2")];
            Assert.Equal(CoordinationRules.StatusOpen, f.Status);
            Assert.True(f.Regression);
            Assert.Null(f.ResolvedUtc);
        }

        [Fact]
        public void Human_states_survive_the_clash_still_being_there()
        {
            var ledger = new Dictionary<string, CoordinationFinding>();
            CoordinationRules.Merge(ledger, new[] { Hit() }, "t1", true, "s1");
            string id = CoordinationRules.FindingId("host||u1", "MEP||u2");
            ledger[id].Status = CoordinationRules.StatusAssigned;
            ledger[id].Assignee = "estructural";
            CoordinationRules.Merge(ledger, new[] { Hit() }, "t2", true, "s1");
            Assert.Equal(CoordinationRules.StatusAssigned, ledger[id].Status);
            Assert.Equal("estructural", ledger[id].Assignee);
        }

        [Fact]
        public void A_complete_run_of_ANOTHER_scope_resolves_nothing_here()
        {
            // The pipes-vs-walls run is complete; the finding belongs to the
            // ducts-vs-floors scope. Its absence from this run is not evidence.
            var ledger = new Dictionary<string, CoordinationFinding>();
            CoordinationRules.Merge(ledger, new[] { Hit() }, "t1", true, "scope-ducts");
            var outcome = CoordinationRules.Merge(ledger, new CoordinationDetected[0], "t2", true, "scope-pipes");
            Assert.Equal(0, outcome.ResolvedByModel);
            Assert.Equal(CoordinationRules.StatusOpen,
                ledger[CoordinationRules.FindingId("host||u1", "MEP||u2")].Status);
        }

        [Fact]
        public void The_scope_key_ignores_category_order_and_nothing_else()
        {
            string one = CoordinationRules.ScopeKey(new[] { "OST_Walls", "OST_Floors" }, new[] { "OST_PipeCurves" }, 0, true);
            string two = CoordinationRules.ScopeKey(new[] { "OST_Floors", "OST_Walls" }, new[] { "OST_PipeCurves" }, 0, true);
            Assert.Equal(one, two);
            Assert.NotEqual(one, CoordinationRules.ScopeKey(new[] { "OST_Walls", "OST_Floors" }, new[] { "OST_PipeCurves" }, 5, true));
            Assert.NotEqual(one, CoordinationRules.ScopeKey(new[] { "OST_Walls", "OST_Floors" }, new[] { "OST_PipeCurves" }, 0, false));
        }

        [Fact]
        public void Nobody_asserts_resolved_by_model_by_hand()
        {
            Assert.False(CoordinationRules.CanTransition(
                CoordinationRules.StatusOpen, CoordinationRules.StatusResolvedByModel, out string reason));
            Assert.Contains("MEASURED", reason);
        }

        [Fact]
        public void An_unknown_status_names_the_real_ones()
        {
            Assert.False(CoordinationRules.CanTransition("open", "fixed", out string reason));
            Assert.Contains("closed_by_decision", reason);
        }

        [Fact]
        public void A_no_op_transition_is_refused_as_already_there()
        {
            Assert.False(CoordinationRules.CanTransition("open", "open", out string reason));
            Assert.Contains("already", reason);
        }

        [Fact]
        public void Reopening_a_human_closure_is_allowed()
        {
            Assert.True(CoordinationRules.CanTransition(
                CoordinationRules.StatusClosedByDecision, CoordinationRules.StatusOpen, out _));
        }

        [Fact]
        public void Csv_cells_with_commas_quotes_and_newlines_stay_one_cell()
        {
            var f = new CoordinationFinding
            {
                Id = "abc", Status = "open",
                Note = "cruza la viga, dijo \"moverlo\"\nal eje B",
                SideA = "host||u1", SideB = "MEP||u2", TimesSeen = 1
            };
            string row = CoordinationRules.CsvRow(f);
            Assert.Contains("\"cruza la viga, dijo \"\"moverlo\"\"\nal eje B\"", row);
            // The header and the row agree on the column count.
            Assert.Equal(CoordinationRules.CsvHeader.Length, SplitCsv(row));
        }

        private static int SplitCsv(string row)
        {
            int cells = 1; bool quoted = false;
            for (int i = 0; i < row.Length; i++)
            {
                char c = row[i];
                if (c == '"') quoted = !quoted;
                else if (c == ',' && !quoted) cells++;
            }
            return cells;
        }

        // ---- history and BCF -------------------------------------------------

        [Fact]
        public void The_history_is_append_only_and_capped()
        {
            var f = new CoordinationFinding { Id = new string('a', 32) };
            for (int i = 0; i < CoordinationRules.MaxHistoryEvents + 10; i++)
                CoordinationRules.AppendEvent(f, "comment", "c" + i, "t");
            Assert.Equal(CoordinationRules.MaxHistoryEvents, f.History.Count);
            Assert.Equal("c" + (CoordinationRules.MaxHistoryEvents + 9), f.History[f.History.Count - 1].Text);
        }

        [Fact]
        public void The_topic_guid_is_the_finding_id_and_stable()
        {
            string id = "0123456789abcdef0123456789abcdef";
            string guid = CoordinationRules.BcfTopicGuid(id);
            Assert.Equal(guid, CoordinationRules.BcfTopicGuid(id));
            Assert.NotEqual(guid, CoordinationRules.BcfTopicGuid("f" + id.Substring(1)));
            Assert.True(System.Guid.TryParse(guid, out _));
        }

        [Fact]
        public void The_markup_escapes_and_carries_the_history_as_comments()
        {
            var f = new CoordinationFinding
            {
                Id = new string('b', 32), Status = CoordinationRules.StatusAssigned,
                CategoryA = "Pipes <&>", CategoryB = "Walls", Assignee = "ana",
                FirstSeenUtc = "2026-08-26T00:00:00Z", PointMm = new[] { 1.0, 2.0, 3.0 }
            };
            CoordinationRules.AppendEvent(f, "comment", "see \"detail\" 5", "2026-08-26T01:00:00Z");
            CoordinationRules.AppendEvent(f, "status", "open -> assigned", "2026-08-26T02:00:00Z");
            string xml = CoordinationRules.BcfMarkupXml(f, "HZ_WRITE");
            Assert.Contains("Pipes &lt;&amp;&gt;", xml);
            Assert.Contains("TopicStatus=\"Open\"", xml);           // assigned is still open work
            Assert.Contains("<AssignedTo>ana</AssignedTo>", xml);
            Assert.Contains("[comment] see &quot;detail&quot; 5", xml);
            Assert.Contains("[status] open -&gt; assigned", xml);
            var parsed = new System.Xml.XmlDocument();
            parsed.LoadXml(xml);
            Assert.Equal(2, parsed.DocumentElement.SelectNodes("Comment").Count);
        }

        [Fact]
        public void Closed_states_map_to_the_bcf_vocabulary()
        {
            var f = new CoordinationFinding { Id = new string('c', 32), Status = CoordinationRules.StatusClosedByDecision };
            Assert.Contains("TopicStatus=\"Closed\"", CoordinationRules.BcfMarkupXml(f, "d"));
            f.Status = CoordinationRules.StatusResolvedByModel;
            Assert.Contains("TopicStatus=\"Closed\"", CoordinationRules.BcfMarkupXml(f, "d"));
        }
    }
}