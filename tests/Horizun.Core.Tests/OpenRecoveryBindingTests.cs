using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OpenRecoveryBindingTests
    {
        [Fact]
        public void Candidate_must_match_first_document_before_another_resolution_is_attempted()
        {
            var binding = new OpenRecoveryBinding<DocumentStub>();
            var first = new DocumentStub { Identity = 1 };
            Assert.True(binding.TryBind(first));
            Assert.True(binding.TryBind(new DocumentStub { Identity = 1 }));
            Assert.False(binding.TryBind(new DocumentStub { Identity = 2 }));
            Assert.False(binding.TryBind(null));
            first.ThrowOnEquals = true;
            Assert.False(binding.TryBind(new DocumentStub { Identity = 1 }));
        }
        private sealed class DocumentStub
        {
            public int Identity;
            public bool ThrowOnEquals;
            public override int GetHashCode() => 1;
            public override bool Equals(object obj)
            {
                if (ThrowOnEquals) throw new System.InvalidOperationException();
                return obj is DocumentStub other && other.Identity == Identity;
            }
        }

        [Fact]
        public void Hash_collision_is_not_identity_and_unreadable_identity_fails_closed()
        {
            var binding = new OpenRecoveryBinding<DocumentStub>();
            var attempted = new DocumentStub { Identity = 1 };
            binding.Record(attempted);
            Assert.True(binding.Matches(new DocumentStub { Identity = 1 }));
            Assert.False(binding.Matches(new DocumentStub { Identity = 2 }));
            attempted.ThrowOnEquals = true;
            Assert.False(binding.Matches(new DocumentStub { Identity = 1 }));
        }

        [Fact]
        public void Evidence_requires_each_attempt_to_belong_to_returned_document()
        {
            var binding = new OpenRecoveryBinding<string>();
            binding.Record("opened");
            binding.Record("opened");
            Assert.True(binding.Matches("opened"));
            Assert.False(binding.Matches("other"));
            binding.Record("other");
            Assert.False(binding.Matches("opened"));
            Assert.False(binding.Matches(null));
        }

        [Fact]
        public void No_attempt_cannot_establish_document_identity()
        {
            Assert.False(new OpenRecoveryBinding<string>().Matches("opened"));
        }
    }
}
