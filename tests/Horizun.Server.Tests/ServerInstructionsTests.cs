// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The `instructions` string in the initialize reply is the ONLY channel MCP gives
// a server for telling a client agent how to behave. A bridge cannot force an
// agent to ask before it writes; it can only say so here, where every client
// reads it once per session. That makes this string a contract surface, not
// documentation - and an untested contract surface is one somebody trims to fit.
// -----------------------------------------------------------------------------
using Xunit;

namespace Horizun.Server.Tests
{
    public class ServerInstructionsTests
    {
        private static string Instructions() => Horizun.Server.ServerInstructions.Text;

        /// <summary>
        /// The three questions a caller must be able to answer before the first write.
        /// Written out so that trimming the section fails a test instead of quietly
        /// removing the only place the expectation is stated to a client.
        /// </summary>
        [Fact]
        public void The_instructions_require_understanding_the_objective_before_writing()
        {
            string s = Instructions();
            Assert.Contains("UNDERSTAND THE OBJECTIVE BEFORE YOU WRITE", s);
            Assert.Contains("WHAT outcome", s);
            Assert.Contains("WHICH elements", s);
            Assert.Contains("HOW the result will be recognised", s);
        }

        /// <summary>
        /// Asking is only useful if it comes with alternatives: an open question hands the
        /// work back, a question with options moves the decision forward.
        /// </summary>
        [Fact]
        public void The_instructions_ask_for_options_not_open_questions()
        {
            string s = Instructions();
            Assert.Contains("ASK", s);
            Assert.Contains("OPTIONS", s);
            Assert.Contains("trade-off", s);
        }

        /// <summary>
        /// The exemption that keeps this from breaking every unattended path: a scheduled
        /// run cannot answer a question, so there it must refuse instead of asking. Without
        /// this clause, "always ask" would deadlock the write-probe tier and the release
        /// gate - turning trust on would turn verification off.
        /// </summary>
        [Fact]
        public void The_instructions_say_to_refuse_rather_than_ask_when_unattended()
        {
            string s = Instructions();
            Assert.Contains("WHEN NOBODY IS AT THE KEYBOARD", s);
            Assert.Contains("REFUSE RATHER THAN ASK", s);
        }

        /// <summary>The pre-existing promises must survive any edit to this block.</summary>
        [Fact]
        public void The_instructions_still_carry_the_house_contract_and_health_first()
        {
            string s = Instructions();
            Assert.Contains("never reports work it did not verify", s);
            Assert.Contains("Call horizun_health FIRST", s);
            Assert.Contains("organisation-neutral", s);
        }
    }
}
