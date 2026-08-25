// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE tools/list DESCRIPTION BUDGET, pinned at the boundary rather than sampled.
//
// Tools.CompactDescription promises a 900-character cap, and it could exceed it.
// The ellipsis it appends was not counted against the budget, so a description
// whose sentence boundary fell exactly on the limit came back at 901, and the
// `cut += 1` branch could reach 902. Nothing detected it: the only coverage was
// the aggregate assertion over the descriptions that HAPPEN to exist, and every
// one of them cut earlier. It surfaced when horizun_fix_planimetry's description
// landed on the boundary - which is to say, it was waiting for the next tool.
//
// So the property is asserted over every length across the boundary, not over
// the current tool table: a truncation that overshoots by one is caught by the
// input that produces it, whether or not any shipped tool is that length today.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ToolDescriptionBudgetTests
    {
        private const int Max = 900;
        private const string Suffix = " Full installed contract: horizun://contract/tools";

        /// <summary>
        /// Every length from comfortably under the cap to well past it. The sentence
        /// boundaries are what the truncation searches for, so the input carries them
        /// at every position - which is how the exact-boundary case is reached without
        /// anyone having to compute where it is.
        /// </summary>
        [Fact]
        public void No_input_length_can_push_a_description_over_the_cap()
        {
            var over = new System.Collections.Generic.List<string>();
            for (int length = 700; length <= 1400; length++)
            {
                string description = SentenceText(length);
                string compacted = Tools.CompactDescription(description);
                if (compacted.Length > Max)
                    over.Add("length " + length + " -> " + compacted.Length);
            }
            Assert.True(over.Count == 0,
                "CompactDescription exceeded its own " + Max + "-character cap for these inputs: " +
                string.Join(", ", over.Take(10)) +
                ". The ellipsis and the suffix are both part of the budget.");
        }

        /// <summary>
        /// Truncating must not cost the pointer to the full contract - that sentence is
        /// the whole reason a short description is acceptable.
        /// </summary>
        [Fact]
        public void Every_compacted_description_still_names_the_contract_resource()
        {
            for (int length = 700; length <= 1400; length += 7)
                Assert.Contains("horizun://contract/tools", Tools.CompactDescription(SentenceText(length)));
        }

        /// <summary>
        /// A description that fits is handed over whole. The cap is a ceiling, not a
        /// target, and truncating something that already fits would lose text for nothing.
        /// </summary>
        [Fact]
        public void A_description_that_fits_is_not_truncated()
        {
            string description = SentenceText(Max - Suffix.Length);
            string compacted = Tools.CompactDescription(description);
            Assert.Equal(description + Suffix, compacted);
            Assert.DoesNotContain("…", compacted);
        }

        /// <summary>
        /// And the real table, which is what a client actually receives. Kept beside the
        /// synthetic sweep because the two fail for different reasons: this one catches a
        /// description somebody made longer, the sweep catches the arithmetic.
        /// </summary>
        [Fact]
        public void Every_shipped_tool_description_is_within_the_budget()
        {
            foreach (Horizun.Contracts.CommandContract contract in Horizun.Contracts.Contract.All)
            {
                string compacted = Tools.CompactDescription(contract.Description);
                Assert.True(compacted.Length <= Max,
                    contract.Name + " compacts to " + compacted.Length + " characters, over the " + Max + " cap");
            }
        }

        /// <summary>
        /// Text of an exact length whose sentence boundaries fall every few characters,
        /// so the "last '. ' at or before the limit" search has somewhere to land at
        /// every offset the sweep walks past.
        /// </summary>
        private static string SentenceText(int length)
        {
            var sb = new System.Text.StringBuilder(length + 16);
            int word = 0;
            while (sb.Length < length) sb.Append("Sentence ").Append(word++ % 10).Append(". ");
            char[] text = sb.ToString(0, length).ToCharArray();
            // The function trims its input, so a trailing space would make the effective
            // length one short and quietly skip the very offset being swept.
            if (char.IsWhiteSpace(text[length - 1])) text[length - 1] = 'x';
            return new string(text);
        }
    }
}
