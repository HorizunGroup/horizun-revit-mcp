// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// An unset text parameter and one holding "" are the same state for undo
// (measured live 2026-10-08 while probing audit finding 6.9).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class UndoTextStateTests
    {
        private static JObject Text(string v) => new JObject { ["storage"] = "String", ["value"] = v };

        [Fact]
        public void An_unset_text_and_an_empty_text_are_one_state()
        {
            Assert.True(UndoRules.StatesMatch(Text(null), Text("")));
            Assert.True(UndoRules.StatesMatch(Text(""), Text(null)));
        }

        [Fact]
        public void Nothing_else_is_loosened()
        {
            Assert.False(UndoRules.StatesMatch(Text(null), Text("one")));
            Assert.False(UndoRules.StatesMatch(new JObject { ["storage"] = "Integer", ["value"] = null },
                                               new JObject { ["storage"] = "Integer", ["value"] = 0 }));
            Assert.False(UndoRules.StatesMatch(new JObject { ["storage"] = "String", ["value"] = null, ["extra"] = 1 },
                                               new JObject { ["storage"] = "String", ["value"] = "", ["extra"] = 2 }));
        }
    }
}
