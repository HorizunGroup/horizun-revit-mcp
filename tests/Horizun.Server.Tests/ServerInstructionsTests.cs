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

        /// <summary>
        /// The fallback policy: a missing typed capability is a reason to generate and
        /// RUN Python, not to answer "not supported". Stated here because this is the
        /// only channel that reaches every client, and trimming it would silently
        /// revert the product decision.
        /// </summary>
        [Fact]
        public void The_instructions_state_typed_first_python_fallback()
        {
            string s = Instructions();
            Assert.Contains("TYPED FIRST, PYTHON AS THE FALLBACK", s);
            Assert.Contains("do not answer 'not supported'", s);
            Assert.Contains("horizun_execute_python", s);
            Assert.Contains("__output__", s);
            Assert.Contains("self_reported_verified|completed_unverified|partial|failed", s);
        }

        /// <summary>
        /// The two boundaries that keep the fallback from doing damage: no Python retry
        /// of a typed write that may have partially written, and no stopping to ask for
        /// approval when the objective is already unambiguous.
        /// </summary>
        [Fact]
        public void The_instructions_bound_the_fallback_in_both_directions()
        {
            string s = Instructions();
            Assert.Contains("write_started=true is never accompanied by", s);
            Assert.Contains("second write, not a recovery", s);
            Assert.Contains("do not stop to ask", s);
            // Python is under the same document discipline as every typed write.
            Assert.Contains("target_document and the active-document check apply to Python", s);
        }

        /// <summary>
        /// The fallback has to be DECIDABLE, not inferred from prose. The instructions
        /// must name the structured block and state that its absence forbids the
        /// fallback - otherwise a client is back to matching error wording, which is the
        /// fragile arrangement the block exists to replace.
        /// </summary>
        [Fact]
        public void The_instructions_make_the_fallback_decidable_from_a_structured_block()
        {
            string s = Instructions();
            Assert.Contains("fallback.allowed", s);
            Assert.Contains("structuredContent", s);
            Assert.Contains("recommended_tool", s);
            Assert.Contains("write_started", s);
            // Absence is an answer, and it is the safe one.
            Assert.Contains("the absence of the block is itself the answer", s);
            Assert.Contains("NOT ON THE WORDING OF AN ERROR", s);
            Assert.Contains("If the block is ABSENT, or allowed=false, DO NOT fall back", s);
        }

        /// <summary>
        /// The honesty boundary a client must repeat to its user: typed writes are
        /// host-verified, Python results are the script's own testimony.
        /// </summary>
        [Fact]
        public void The_instructions_state_that_python_results_are_self_reported()
        {
            string s = Instructions();
            Assert.Contains("SELF-REPORTED, NOT HOST-VERIFIED", s);
            Assert.Contains("self_reported_verified", s);
            Assert.Contains("host_verified is always false", s);
            // "verified" must never be offered as a state this path returns. Checked as
            // the state list it would appear in, since self_reported_verified legitimately
            // ends in the same characters.
            Assert.Contains("there is no 'verified'", s);
            Assert.DoesNotContain("states are verified|", s);
            Assert.DoesNotContain("status verified|", s);
        }
    }
}
