using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class R9CActivationIdentityTests
    {
        private sealed class Wrapper
        {
            public int Instance;
            public string Path;
            public override bool Equals(object other) => other is Wrapper w && w.Instance == Instance;
            public override int GetHashCode() => Instance;
        }

        [Theory]
        [InlineData(null)]
        [InlineData("C:/example/model.rvt")]
        public void Rehearsal_and_apply_use_the_target_instance_even_with_new_wrappers(string path)
        {
            var keys = new ActivationSessionIdentity<Wrapper>();
            var confirmations = new ConfirmationStore();
            var rehearsal = new Wrapper { Instance = 1, Path = path };
            var apply = new Wrapper { Instance = 1, Path = path };
            string hash = ConfirmationStore.HashPlan("choice=save_as", "target=c:/example/new.rvt");
            var token = confirmations.Issue("activate", keys.Key(rehearsal, "Project1"), hash);
            var check = confirmations.Validate(token.Token, "activate", keys.Key(apply, "Project1"), hash);
            Assert.True(check.Ok, check.Message);
        }

        [Fact]
        public void Destination_and_title_are_bound_to_the_rehearsal()
        {
            var keys = new ActivationSessionIdentity<Wrapper>();
            var confirmations = new ConfirmationStore();
            var doc = new Wrapper { Instance = 1 };
            string key = keys.Key(doc, "Project1");
            var token = confirmations.Issue("activate", key, ConfirmationStore.HashPlan("target=c:/example/a.rvt"));
            var changedDestination = confirmations.Validate(token.Token, "activate", key, ConfirmationStore.HashPlan("target=c:/example/b.rvt"));
            Assert.Equal(ConfirmationState.PlanChanged, changedDestination.State);
            var changedTitle = confirmations.Validate(token.Token, "activate", keys.Key(doc, "Project2"), ConfirmationStore.HashPlan("target=c:/example/a.rvt"));
            Assert.Equal(ConfirmationState.DocumentChanged, changedTitle.State);
        }
        [Fact]
        public void Different_instance_with_same_title_is_refused()
        {
            var keys = new ActivationSessionIdentity<Wrapper>();
            var confirmations = new ConfirmationStore();
            var token = confirmations.Issue("activate", keys.Key(new Wrapper { Instance = 1 }, "Project1"), "plan");
            var check = confirmations.Validate(token.Token, "activate", keys.Key(new Wrapper { Instance = 2 }, "Project1"), "plan");
            Assert.Equal(ConfirmationState.DocumentChanged, check.State);
            Assert.Contains("DIFFERENT document", check.Message);
        }
    }
}
