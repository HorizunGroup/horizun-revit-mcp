using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// AN EMPTY PARAMETER IS NOT AN ABSENT INTEGRATION.
    ///
    /// A model where every element has a blank "Cost Code" is in a completely
    /// different state from one where the parameter does not exist. The first has
    /// been set up and not filled in - a day's work away from ready. The second
    /// has not been set up - a decision away from ready. A tool that reports both
    /// as "no 5D connection" has destroyed the only thing the reader needed.
    ///
    /// Half these tests are about that one distinction, and the other half are
    /// about the difference between "nothing carries a value" and "nothing could
    /// be read", which is the same mistake wearing a different hat.
    /// </summary>
    public class ReadinessRulesTests
    {
        private static ReadinessRole Role(string id, string dim, params string[] aliases)
        {
            return new ReadinessRole { Id = id, Dimension = dim, Aliases = aliases.ToList() };
        }

        // ------------------------------------------------------- the declaration

        [Fact]
        public void An_empty_declaration_is_refused_rather_than_answered_not_ready()
        {
            List<string> codes;
            string refusal = ReadinessRules.Validate(new List<ReadinessRole>(), out codes);
            Assert.NotNull(refusal);
            Assert.Contains(ReadinessRules.CodeNoRoles, codes);
            Assert.Contains("inventing a standard", refusal);
        }

        [Fact]
        public void A_role_with_no_parameter_names_is_refused_because_nothing_is_compiled_in()
        {
            List<string> codes;
            var roles = new List<ReadinessRole> { new ReadinessRole { Id = "cost", Dimension = "5d" } };
            Assert.NotNull(ReadinessRules.Validate(roles, out codes));
            Assert.Contains(ReadinessRules.CodeNoAliases, codes);
        }

        [Fact]
        public void A_duplicate_role_id_and_an_unknown_dimension_are_both_refused()
        {
            List<string> codes;
            Assert.NotNull(ReadinessRules.Validate(
                new[] { Role("a", "4d", "X"), Role("a", "5d", "Y") }, out codes));
            Assert.Contains(ReadinessRules.CodeDuplicateRole, codes);

            Assert.NotNull(ReadinessRules.Validate(new[] { Role("a", "6d", "X") }, out codes));
            Assert.Contains(ReadinessRules.CodeUnknownDimension, codes);
        }

        [Fact]
        public void A_well_formed_declaration_is_accepted()
        {
            List<string> codes;
            Assert.Null(ReadinessRules.Validate(
                new[] { Role("task", "4d", "Task ID", "Activity ID"), Role("cost", "5d", "Cost Code") },
                out codes));
            Assert.Empty(codes);
        }

        // ------------------------------------------------------ the distinction

        [Fact]
        public void A_parameter_that_EXISTS_and_is_blank_is_not_the_same_as_one_that_does_not_exist()
        {
            var setUpButEmpty = ReadinessRules.Judge(new RoleMeasurement
            {
                RoleId = "cost", Dimension = "5d", ParameterExists = true, MatchedAlias = "Cost Code",
                ElementsInScope = 100, ElementsCarryingValue = 0
            });
            var neverSetUp = ReadinessRules.Judge(new RoleMeasurement
            {
                RoleId = "cost", Dimension = "5d", ParameterExists = false,
                ElementsInScope = 100, ElementsCarryingValue = 0
            });

            // Both are `absent` as a state - the model is not ready either way - but
            // the two reasons must be distinguishable, because they are a day's work
            // apart.
            Assert.Equal(ReadinessState.Absent, setUpButEmpty.State);
            Assert.Equal(ReadinessState.Absent, neverSetUp.State);

            Assert.Contains("EXISTS", setUpButEmpty.Why);
            Assert.Contains("set up for this role and not", setUpButEmpty.Why);
            Assert.Contains("has not been set up", neverSetUp.Why);
            Assert.Contains("different state", neverSetUp.Why);
        }

        [Fact]
        public void Nothing_readable_is_not_assessable_and_never_zero_coverage()
        {
            var v = ReadinessRules.Judge(new RoleMeasurement
            {
                RoleId = "cost", Dimension = "5d", ParameterExists = true,
                ElementsInScope = 40, ElementsUnreadable = 40, ElementsCarryingValue = 0
            });
            Assert.Equal(ReadinessState.NotAssessable, v.State);
            Assert.Null(v.Coverage);
            Assert.Contains("Unreadable is not the same as empty", v.Why);
        }

        [Fact]
        public void An_empty_scope_is_a_fact_about_the_scope_not_about_the_model()
        {
            var v = ReadinessRules.Judge(new RoleMeasurement { RoleId = "t", Dimension = "4d", ElementsInScope = 0 });
            Assert.Equal(ReadinessState.NotAssessable, v.State);
            Assert.Contains("fact about the scope", v.Why);
        }

        [Fact]
        public void Coverage_is_computed_over_the_READABLE_elements_only()
        {
            var v = ReadinessRules.Judge(new RoleMeasurement
            {
                RoleId = "cost", Dimension = "5d", ParameterExists = true, MatchedAlias = "Cost Code",
                ElementsInScope = 100, ElementsUnreadable = 20, ElementsCarryingValue = 40
            });
            Assert.Equal(ReadinessState.Partial, v.State);
            // 40 of the 80 that could be read, not 40 of 100.
            Assert.Equal(0.5, v.Coverage.Value, 6);
            Assert.Contains("40 of 80", v.Why);
            Assert.Contains("not counted either way", v.Why);
        }

        [Fact]
        public void Complete_means_every_READABLE_element_and_says_what_it_left_out()
        {
            var v = ReadinessRules.Judge(new RoleMeasurement
            {
                RoleId = "cost", Dimension = "5d", ParameterExists = true, MatchedAlias = "Cost Code",
                ElementsInScope = 100, ElementsUnreadable = 10, ElementsCarryingValue = 90
            });
            Assert.Equal(ReadinessState.Complete, v.State);
            Assert.Contains("10 could not be read", v.Why);
        }

        [Fact]
        public void The_alias_that_matched_is_reported_so_a_reader_knows_which_name_the_model_uses()
        {
            var v = ReadinessRules.Judge(new RoleMeasurement
            {
                RoleId = "task", Dimension = "4d", ParameterExists = true, MatchedAlias = "Activity ID",
                ElementsInScope = 10, ElementsCarryingValue = 10
            });
            Assert.Equal("Activity ID", v.MatchedAlias);
            Assert.Contains("Activity ID", v.Why);
        }

        // ---------------------------------------------------------- the rollup

        [Fact]
        public void A_dimension_with_no_declared_role_is_not_assessable_rather_than_absent()
        {
            var s = ReadinessRules.Score("5d", new List<RoleVerdict>());
            Assert.Equal(ReadinessState.NotAssessable, s.State);
            Assert.Equal(0, s.RolesDeclared);
        }

        [Fact]
        public void A_dimension_rolls_up_from_its_own_roles_only()
        {
            var verdicts = new List<RoleVerdict>
            {
                new RoleVerdict { RoleId = "a", Dimension = "4d", State = ReadinessState.Complete, Coverage = 1.0 },
                new RoleVerdict { RoleId = "b", Dimension = "4d", State = ReadinessState.Partial, Coverage = 0.5 },
                new RoleVerdict { RoleId = "c", Dimension = "5d", State = ReadinessState.Absent },
            };
            var four = ReadinessRules.Score("4d", verdicts);
            Assert.Equal(2, four.RolesDeclared);
            Assert.Equal(ReadinessState.Partial, four.State);
            Assert.Equal(0.75, four.Coverage.Value, 6);

            var five = ReadinessRules.Score("5d", verdicts);
            Assert.Equal(1, five.RolesDeclared);
            Assert.Equal(ReadinessState.Absent, five.State);
        }

        [Fact]
        public void A_dimension_where_nothing_could_be_measured_is_not_reported_as_absent()
        {
            var s = ReadinessRules.Score("5d", new[]
            {
                new RoleVerdict { RoleId = "a", Dimension = "5d", State = ReadinessState.NotAssessable },
                new RoleVerdict { RoleId = "b", Dimension = "5d", State = ReadinessState.NotAssessable },
            });
            Assert.Equal(ReadinessState.NotAssessable, s.State);
            Assert.Contains("none of the 2 declared role(s) could be measured", s.Why);
        }

        [Fact]
        public void Roles_that_could_not_be_measured_are_named_beside_a_verdict_rather_than_hidden_by_it()
        {
            var s = ReadinessRules.Score("4d", new[]
            {
                new RoleVerdict { RoleId = "a", Dimension = "4d", State = ReadinessState.Complete, Coverage = 1.0 },
                new RoleVerdict { RoleId = "b", Dimension = "4d", State = ReadinessState.NotAssessable },
            });
            Assert.Equal(ReadinessState.Partial, s.State);
            Assert.Equal(1, s.RolesNotAssessable);
            Assert.Contains("1 role(s) could not be measured", s.Why);
        }

        [Fact]
        public void The_published_explanation_refuses_the_claim_this_could_be_mistaken_for()
        {
            Assert.Contains("not a connection to a programme", ReadinessRules.Means);
            Assert.Contains("nothing here prices or sequences", ReadinessRules.Means);
        }
    }
}
