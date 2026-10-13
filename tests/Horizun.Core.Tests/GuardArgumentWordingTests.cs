// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE GUARD ARGUMENTS SAY WHAT THE TOOL DOES. write_params_verified, set_keynote and
// family_apply carried horizun_delete_verified's target_document and confirmation_token
// text word for word: "the document to delete from", "nothing is deleted" - on tools
// that write. A model reads those words when it decides what a call will do. Found
// 2026-10-10 measuring the tools/list budget; this keeps the copy from coming back.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GuardArgumentWordingTests
    {
        private static readonly string[] GuardArguments = { "target_document", "confirmation_token" };
        private static readonly string[] DeletePhrases = { "document to delete from", "nothing is deleted", "a delete aimed at" };

        [Fact]
        public void Only_a_deleting_tool_describes_its_guard_arguments_as_a_delete()
        {
            var offenders = new List<string>();
            foreach (CommandContract c in Contract.All.Where(c => c.InputSchema != null && c.Name != "horizun_delete_verified"))
                foreach (string arg in GuardArguments)
                {
                    string text = (string)c.InputSchema["properties"]?[arg]?["description"];
                    if (text == null) continue;
                    foreach (string phrase in DeletePhrases)
                        if (text.IndexOf(phrase, System.StringComparison.OrdinalIgnoreCase) >= 0)
                            offenders.Add(c.Name + "." + arg + ": '" + phrase + "'");
                }
            Assert.True(offenders.Count == 0, "guard arguments worded as a delete on tools that do not delete:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void The_deleting_tool_keeps_its_own_wording()
        {
            CommandContract delete = Contract.All.Single(c => c.Name == "horizun_delete_verified");
            Assert.Contains("document to delete from", (string)delete.InputSchema["properties"]["target_document"]["description"]);
            Assert.Contains("nothing is deleted", (string)delete.InputSchema["properties"]["confirmation_token"]["description"]);
        }
    }
}
