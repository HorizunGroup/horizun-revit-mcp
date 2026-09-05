// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// 4D and 5D readiness, proved by running the rules. Three sentences this file
// exists to make impossible:
//
//   "this model is not ready for 4D"   - with no profile, nobody declared what
//                                        ready means
//   "78% of the model carries it"      - the average that hides a discipline
//   "this code is fine, it exists"     - a group code exists and cannot be
//                                        priced
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DeliveryReadinessTests
    {
        private static RoleCategoryMeasurement M(string category, long population, long evaluated,
                                                 long complete, long unreadable = 0)
        {
            return new RoleCategoryMeasurement
            {
                RoleId = "activity_id",
                Category = category,
                Population = population,
                Evaluated = evaluated,
                Complete = complete,
                Incomplete = evaluated - complete,
                Unreadable = unreadable
            };
        }

        private static RoleVerdictByCategory J(RoleCategoryMeasurement m, bool required = true)
        {
            RoleVerdictByCategory v = DeliveryReadinessRules.Judge(m, required);
            v.Dimension = DeliveryDimension.FourD;
            return v;
        }

        // ------------------------------------------------------- per category

        [Fact]
        public void A_discipline_with_nothing_is_visible_beside_one_that_is_complete()
        {
            // THE AVERAGE THAT HIDES A DISCIPLINE. Walls complete, pipes empty; a
            // model-wide percentage would report this as mostly ready.
            RoleVerdictByCategory walls = J(M("Walls", 400, 400, 400));
            RoleVerdictByCategory pipes = J(M("Pipes", 100, 100, 0));

            Assert.Equal(RoleState.Complete, walls.State);
            Assert.Equal(RoleState.Absent, pipes.State);
            Assert.Contains("hides the pipes", DeliveryReadinessRules.PerCategoryMeans);
        }

        [Fact]
        public void Coverage_is_null_when_nothing_was_evaluated_and_never_zero()
        {
            Assert.Null(M("Walls", 10, 0, 0).Coverage);
            Assert.Equal(50.0, M("Walls", 10, 10, 5).Coverage);
        }

        [Fact]
        public void A_category_with_no_elements_is_not_assessable_rather_than_absent()
        {
            // There is nothing to carry the parameter. Calling that absent invents
            // a gap in a discipline the project does not have.
            RoleVerdictByCategory v = J(M("Ducts", 0, 0, 0));
            Assert.Equal(RoleState.NotAssessable, v.State);
            Assert.Contains("no element of this category", v.Why);
        }

        [Fact]
        public void Complete_requires_that_nothing_was_unreadable()
        {
            // A hundred per cent of what we could read is not a hundred per cent.
            RoleVerdictByCategory v = J(M("Walls", 100, 90, 90, unreadable: 10));
            Assert.Equal(RoleState.Partial, v.State);
            Assert.Contains("lower bound", v.Why);

            Assert.Equal(RoleState.Complete, J(M("Walls", 90, 90, 90)).State);
        }

        [Fact]
        public void A_population_that_could_not_be_read_is_unreadable_and_not_absent()
        {
            RoleVerdictByCategory v = J(M("Walls", 50, 0, 0, unreadable: 50));
            Assert.Equal(RoleState.Unreadable, v.State);
            Assert.Contains("nothing is known here", v.Why);
        }

        [Fact]
        public void A_role_nobody_required_is_not_required_and_never_absent()
        {
            Assert.Equal(RoleState.NotRequired, J(M("Walls", 100, 100, 0), required: false).State);
        }

        // ---------------------------------------------------------- roll-up

        [Fact]
        public void Found_is_weaker_than_complete_and_says_only_that_something_carries_it()
        {
            // Partial anywhere means partial overall; nothing rolls up to complete
            // unless every judged category is complete.
            Assert.Equal(RoleState.Partial, DeliveryReadinessRules.RollUp(new[]
            {
                J(M("Walls", 10, 10, 10)), J(M("Pipes", 10, 10, 0))
            }));
            Assert.Equal(RoleState.Complete, DeliveryReadinessRules.RollUp(new[]
            {
                J(M("Walls", 10, 10, 10)), J(M("Pipes", 10, 10, 10))
            }));
            Assert.Equal(RoleState.Absent, DeliveryReadinessRules.RollUp(new[]
            {
                J(M("Walls", 10, 10, 0)), J(M("Pipes", 10, 10, 0))
            }));
        }

        [Fact]
        public void Categories_that_are_not_assessable_do_not_drag_a_role_down()
        {
            // An empty category is not evidence against the role.
            Assert.Equal(RoleState.Complete, DeliveryReadinessRules.RollUp(new[]
            {
                J(M("Walls", 10, 10, 10)), J(M("Ducts", 0, 0, 0))
            }));
        }

        [Fact]
        public void A_role_with_no_categories_judged_is_not_assessable()
        {
            Assert.Equal(RoleState.NotAssessable,
                DeliveryReadinessRules.RollUp(new RoleVerdictByCategory[0]));
        }

        // -------------------------------------------------------- dimension

        [Fact]
        public void A_dimension_publishes_no_score_because_readiness_is_not_a_scalar()
        {
            var roles = new List<DeliveryRole>
            {
                new DeliveryRole { Id = "activity_id", Dimension = DeliveryDimension.FourD }
            };
            JObject d = DeliveryReadinessRules.Dimension(
                DeliveryDimension.FourD, new[] { J(M("Walls", 10, 10, 10)) }, roles);

            Assert.Null(d["score"].Value<double?>());
            Assert.Contains("not commensurable", d.Value<string>("score_means"));
            Assert.Equal(1, d.Value<int>("roles_declared"));
        }

        [Fact]
        public void With_no_roles_declared_a_dimension_says_nothing_about_the_model()
        {
            JObject d = DeliveryReadinessRules.Dimension(DeliveryDimension.FourD, null, null);
            Assert.Equal(0, d.Value<int>("roles_declared"));
            Assert.Contains("not a verdict", d.Value<string>("evidence_means"));
        }

        [Fact]
        public void The_reply_refuses_to_claim_an_integration()
        {
            JObject d = DeliveryReadinessRules.Dimension(DeliveryDimension.FiveD, null, null);
            string m = d.Value<string>("not_an_integration_means");
            Assert.Contains("not a connection to a schedule or a budget", m);
            Assert.Contains("looks like an activity id is not proof", m);
        }
    }
}
