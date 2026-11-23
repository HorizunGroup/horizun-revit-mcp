using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReadDocumentRulesTests
    {
        [Fact]
        public void MissingTargetListsOpenDocumentsAndUsesDocumentCode()
        {
            var detail = ReadDocumentRules.MissingTarget("Missing", new[] { new DocIdentity { Title = "Model A" }, new DocIdentity { Title = "Model B" } });
            Assert.Equal("documento_objetivo_ambiguo", (string)detail["error_code"]);
            Assert.Contains("Model A", (string)detail["message"]);
            Assert.Contains("Model B", (string)detail["message"]);
            Assert.Equal(new[] { "Model A", "Model B" }, detail["open_documents"].Values<string>());
            var writeDetail = ReadDocumentRules.MissingTarget("Missing", new[] { new DocIdentity { Title = "Model A" } }, isWrite: true);
            Assert.Equal("documento_objetivo_ambiguo", (string)writeDetail["error_code"]);
            Assert.Contains("Nothing was changed", (string)writeDetail["message"]);
            Assert.Equal("Model A", (string)writeDetail["open_documents"][0]);
        }
        [Fact]
        public void Synthetic_equal_titles_cannot_distinguish_two_paths()
        {
            // This establishes the rule's limitation, not that Revit can open both
            // documents in a real session. Paths are intentionally not inputs to Allows.
            var first = new { Title = "Model", Path = @"C:\A\Model.rvt" };
            var second = new { Title = "Model", Path = @"C:\B\Model.rvt" };
            Assert.NotEqual(first.Path, second.Path);
            Assert.True(ReadDocumentRules.Allows("Model", first.Title));
            Assert.True(ReadDocumentRules.Allows("Model", second.Title));
        }
        [Fact]
        public void Full_path_identifies_active_document_even_with_duplicate_titles()
        {
            var method = typeof(ReadDocumentRules).GetMethod("Allows", new[] { typeof(string), typeof(string), typeof(string) });
            Assert.NotNull(method);
            Assert.True((bool)method.Invoke(null, new object[] { @"c:/a/model.RVT", "Model", @"C:\A\Model.rvt" }));
            Assert.False((bool)method.Invoke(null, new object[] { @"c:/a/model.RVT", "Model", @"C:\B\Model.rvt" }));
        }
        [Theory]
        [InlineData("target_document")]
        [InlineData("target_document_title")]
        [InlineData("expected_document")]
        public void Every_accepted_document_alias_refuses_a_different_active_document(string key)
        {
            var args = new JObject { [key] = "Example" };
            string wanted = ReadDocumentRules.Target(args);
            Assert.Equal("Example", wanted);
            Assert.False(ReadDocumentRules.Allows(wanted, "Different"));
            Assert.True(ReadDocumentRules.Allows(wanted, "example.rvt"));
            Assert.False(ReadDocumentRules.Allows(wanted, null));
        }

        [Fact]
        public void Omitted_guard_remains_optional_and_primary_title_wins()
        {
            Assert.True(ReadDocumentRules.Allows(ReadDocumentRules.Target(new JObject()), "Example"));
            var args = JObject.Parse("{target_document:'Example',expected_document:'Different'}");
            Assert.Equal("Example", ReadDocumentRules.Target(args));
        }
    }
}
