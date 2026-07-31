using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// The two-step gate, checked END TO END rather than in halves.
    ///
    /// ConfirmationTests already proves Issue and Validate agree with each other. That
    /// passed for months while THREE of the five destructive commands could not be
    /// executed at all - they demanded a token their own rehearsal never minted - and a
    /// fourth performed its write during the rehearsal. Both were found by driving a
    /// real Revit, not by any test here, because every test stopped at the seam.
    ///
    /// These read the command sources. That is unusual for a unit test and deliberate:
    /// the handlers need a UIApplication, so the only way to assert this invariant
    /// without Revit is over the code that ships. A test that cannot fail on the real
    /// defect is decoration.
    /// </summary>
    public class ConfirmationRoundTripTests
    {
        private static string CommandsDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate src/Horizun.Revit/Commands from " + AppContext.BaseDirectory);
            return Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands");
        }

        private static IEnumerable<(string Name, string Text)> CommandSources() =>
            Directory.EnumerateFiles(CommandsDir(), "*Command.cs")
                     .Select(p => (Path.GetFileName(p), File.ReadAllText(p)));

        /// <summary>
        /// Demanding a token without ever issuing one is not a strict gate, it is a
        /// command that can never run. Three shipped that way.
        /// </summary>
        [Fact]
        public void Every_command_that_demands_a_token_also_issues_one()
        {
            var broken = new List<string>();
            foreach (var (name, text) in CommandSources())
            {
                bool demands = text.Contains("RequireConfirmation");
                bool issues = text.Contains("StampConfirmation") || text.Contains("Confirmations.Issue");
                if (demands && !issues) broken.Add(name);
            }

            Assert.True(broken.Count == 0,
                "These commands REQUIRE a confirmation token but never issue one, so dry_run=false can never " +
                "succeed and the command is unreachable: " + string.Join(", ", broken));
        }

        /// <summary>
        /// The inverse, which is the dangerous direction: a rehearsal that falls through
        /// into the write. bind_shared_param did exactly this - dry_run was read, used
        /// once to SKIP the gate, and never again, so the default call bound the
        /// parameter while calling itself a rehearsal.
        ///
        /// WHAT THIS ASKS FOR, and why it changed. The first version counted USES of the
        /// flag and demanded at least three: a declaration, the guard that skips the
        /// confirmation, and one more that stops before the write. That is a proxy for
        /// the property, and it went wrong in the safe direction the moment a command was
        /// written in a different shape - document_session's close needs no
        /// skip-the-confirmation branch, because its rehearsal returns before the
        /// confirmation is ever reached, so it used the flag twice and failed a test that
        /// exists to catch the opposite mistake.
        ///
        /// So it asks for the property itself: somewhere there is a branch taken WHEN
        /// dry_run is true that RETURNS. A rehearsal that returns cannot fall through into
        /// a write. bind_shared_param's defect still fails this - it had no such branch at
        /// all - and a handler that stops before the write still passes it however it is
        /// arranged. The window is bounded rather than brace-matched: this is a test
        /// reading source text, and it should be obvious about how far it looks.
        /// </summary>
        [Fact]
        public void Every_command_that_reads_dry_run_also_returns_on_it()
        {
            var broken = new List<string>();
            foreach (var (name, text) in CommandSources())
            {
                // The local the handler keeps its dry_run decision in.
                Match decl = Regex.Match(text, @"bool\s+(\w*[Dd]ry\w*)\s*=");
                if (!decl.Success) continue;

                string flag = Regex.Escape(decl.Groups[1].Value);

                // A POSITIVE branch on the flag - `if (dryRun)`, not `if (!dryRun)` - with
                // a return in it. Singleline so the body may wrap over as many lines as it
                // needs; the bounded window keeps it from finding a return three methods
                // away and calling that a rehearsal that stopped.
                bool stops = Regex.IsMatch(
                    text, @"if\s*\(\s*" + flag + @"\s*\)[\s\S]{0,4000}?\breturn\b",
                    RegexOptions.Singleline);

                if (!stops) broken.Add(name + " ('" + decl.Groups[1].Value + "')");
            }

            Assert.True(broken.Count == 0,
                "These commands read a dry_run flag but have no branch on it that RETURNS, so a REHEARSAL falls " +
                "through into the write: " + string.Join(", ", broken));
        }

        /// <summary>
        /// A token minted by a rehearsal has to open the execution of that same request.
        /// Both halves exist; this asserts they meet - the plan hash the rehearsal signs
        /// is the one execution presents.
        /// </summary>
        [Fact]
        public void A_token_issued_for_a_plan_opens_that_plan_and_nothing_else()
        {
            var store = new ConfirmationStore();
            const string doc = "doc-fingerprint";

            Confirmation issued = store.Issue("horizun_family_apply", doc, "plan-A");

            Assert.False(store.Validate(issued.Token, "horizun_delete_verified", doc, "plan-A").Ok);
            Assert.False(store.Validate(issued.Token, "horizun_family_apply", "other-doc", "plan-A").Ok);
            Assert.False(store.Validate(issued.Token, "horizun_family_apply", doc, "plan-B").Ok);

            Assert.True(store.Validate(issued.Token, "horizun_family_apply", doc, "plan-A").Ok);
            Assert.False(store.Validate(issued.Token, "horizun_family_apply", doc, "plan-A").Ok);
        }
    }
}
