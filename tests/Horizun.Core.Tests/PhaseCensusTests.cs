// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Phases, proved by running the rules. Two properties carry this area:
//
//   a category that has no phase is not_applicable, never "no phase"
//   demolished-before-created is a contradiction in the MODEL, so it needs no
//   profile - and an unknown order is not an invalid one
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PhaseCensusTests
    {
        private static PhasedElementFact E(string category = "Walls", string created = "New Construction",
                                           int? createdSeq = 1, string demolished = null, int? demoSeq = null,
                                           bool supports = true)
        {
            return new PhasedElementFact
            {
                ElementId = 1,
                Category = category,
                SupportsPhases = supports,
                CreatedPhase = created,
                CreatedSequence = createdSeq,
                DemolishedPhase = demolished,
                DemolishedSequence = demoSeq
            };
        }

        // ------------------------------------------------------ applicability

        [Fact]
        public void A_category_without_phases_is_not_applicable_and_never_no_phase()
        {
            // THE ONE THAT MATTERS. Levels, grids, views and sheets have no phase,
            // and counting them as "no phase" buries the elements that really lack one.
            Assert.Equal(PhaseState.NotApplicable,
                PhaseCensusRules.StateOf(E("Levels", created: null, createdSeq: null, supports: false)));

            Assert.Equal(PhaseState.NoPhase,
                PhaseCensusRules.StateOf(E("Walls", created: null, createdSeq: null, supports: true)));
        }

        [Fact]
        public void The_two_are_counted_apart_in_the_tally()
        {
            JObject t = PhaseCensusRules.Tally(new[]
            {
                E("Levels", created: null, createdSeq: null, supports: false),
                E("Walls", created: null, createdSeq: null),
                E("Walls")
            });
            Assert.Equal(1, t.Value<int>(PhaseState.NotApplicable));
            Assert.Equal(1, t.Value<int>(PhaseState.NoPhase));
            Assert.Equal(1, t.Value<int>(PhaseState.Created));
            Assert.Contains("compiled-in list", PhaseCensusRules.ApplicabilityMeans);
        }

        [Fact]
        public void The_no_phase_breakdown_names_the_categories_so_a_reader_can_act()
        {
            JObject t = PhaseCensusRules.Tally(new[]
            {
                E("Walls", created: null, createdSeq: null),
                E("Walls", created: null, createdSeq: null),
                E("Doors", created: null, createdSeq: null)
            });
            JArray rows = (JArray)t["no_phase_by_category"];
            Assert.Equal("Walls", rows[0].Value<string>("category"));
            Assert.Equal(2, rows[0].Value<long>("elements"));
        }

        // -------------------------------------------------------- contradiction

        [Fact]
        public void An_element_demolished_before_it_was_created_is_a_contradiction()
        {
            // Reported with no profile: this is the model disagreeing with itself,
            // not a deviation from somebody's standard.
            PhasedElementFact bad = E(created: "Phase 2", createdSeq: 2, demolished: "Phase 1", demoSeq: 1);
            Assert.True(bad.DemolishedBeforeCreated);
            Assert.Single(PhaseCensusRules.Contradictions(new[] { bad }));
            Assert.Contains("contradiction inside the model", PhaseCensusRules.InvalidMeans);
        }

        [Fact]
        public void A_normal_demolition_is_not_a_contradiction()
        {
            PhasedElementFact ok = E(created: "Phase 1", createdSeq: 1, demolished: "Phase 2", demoSeq: 2);
            Assert.False(ok.DemolishedBeforeCreated);
            Assert.Empty(PhaseCensusRules.Contradictions(new[] { ok }));
        }

        [Fact]
        public void An_unknown_order_is_not_an_invalid_one()
        {
            // A sequence that could not be read makes the comparison unknown. False
            // would say the model was checked and found consistent; true would
            // invent a contradiction.
            PhasedElementFact unknown = E(created: "Phase 1", createdSeq: null,
                                          demolished: "Phase 2", demoSeq: 2);
            Assert.Null(unknown.DemolishedBeforeCreated);
            Assert.Empty(PhaseCensusRules.Contradictions(new[] { unknown }));

            JObject t = PhaseCensusRules.Tally(new[] { unknown });
            Assert.Equal(0, t.Value<int>("demolished_before_created"));
            Assert.Equal(1, t.Value<int>("order_unknown"));
        }

        // ------------------------------------------------------------ ordering

        [Fact]
        public void Phases_are_ordered_by_sequence_and_never_alphabetically()
        {
            // "Phase 10" sorts before "Phase 2" as text, and every before/after
            // question in this area would then be wrong.
            var phases = new[]
            {
                new PhaseFact { ElementId = 3, Name = "Phase 10", Sequence = 10 },
                new PhaseFact { ElementId = 1, Name = "Phase 2", Sequence = 2 },
                new PhaseFact { ElementId = 2, Name = "Existing", Sequence = 1 }
            };
            List<PhaseFact> ordered = PhaseCensusRules.InSequence(phases);
            Assert.Equal("Existing", ordered[0].Name);
            Assert.Equal("Phase 2", ordered[1].Name);
            Assert.Equal("Phase 10", ordered[2].Name);
        }

        // ------------------------------------------------------------ coverage

        [Fact]
        public void An_unreadable_element_is_its_own_state_and_makes_the_counts_inexact()
        {
            PhasedElementFact bad = E();
            bad.Readable = false;
            Assert.Equal(PhaseState.Unreadable, PhaseCensusRules.StateOf(bad));

            JObject t = PhaseCensusRules.Tally(new[] { E(), bad });
            Assert.Equal(1, t.Value<int>(PhaseState.Unreadable));
            Assert.False(t.Value<bool>("counts_are_exact"));
        }

        [Fact]
        public void Every_state_appears_so_a_missing_key_never_has_to_be_guessed()
        {
            JObject t = PhaseCensusRules.Tally(new PhasedElementFact[0]);
            foreach (string s in PhaseState.All) Assert.NotNull(t[s]);
            Assert.Equal(0, t.Value<int>("examined"));
            Assert.True(t.Value<bool>("counts_are_exact"));
        }

        [Fact]
        public void The_number_of_categories_examined_is_reported_beside_the_counts()
        {
            // Zero findings over zero categories is not a clean model.
            JObject t = PhaseCensusRules.Tally(new[] { E("Walls"), E("Walls"), E("Doors") });
            Assert.Equal(2, t.Value<int>("categories_examined"));
        }
    }
}
