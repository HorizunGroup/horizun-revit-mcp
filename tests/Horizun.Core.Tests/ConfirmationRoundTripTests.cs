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
                // needs.
                //
                // BOUNDED BY THE MEMBER, not by a character count. It was 4000 characters,
                // which was always a proxy for "still inside this method" - and the proxy
                // failed the moment family_apply's rehearsal grew to 4045: that handler
                // emits a plan, a parameter-schema baseline and a geometry baseline before
                // returning, all of it legitimately, and the test reported a rehearsal
                // falling through into a write that does not exist. A test that fires on
                // the length of a JSON block teaches people to shorten their evidence.
                //
                // So the window is now the thing the count approximated: everything up to
                // the next member declaration. A return found after one would be in another
                // method, which is exactly the mistake the bound exists to prevent. The
                // 20000 cap is only a runaway guard on the regex.
                if (!RehearsalStops(text, flag) && !RehearsalIsDelegated(text, flag))
                    broken.Add(name + " ('" + decl.Groups[1].Value + "')");
            }

            Assert.True(broken.Count == 0,
                "These commands read a dry_run flag but have no branch on it that RETURNS, so a REHEARSAL falls " +
                "through into the write: " + string.Join(", ", broken));
        }

        /// <summary>The detection above, aimed at one source text. `flag` arrives escaped.</summary>
        /// <summary>
        /// THE OTHER SHAPE THAT IS SAFE, and it needs proving rather than assuming.
        ///
        /// A command that writes NOTHING ITSELF and delegates to typed commands that
        /// do - horizun_cad_connect joins through horizun_connect_mep and builds
        /// fittings through horizun_create_elements - has no write of its own to
        /// guard, so it has no rehearsal branch to return from. Forwarding is how it
        /// honours the flag, and the first version of this guard reported it as a
        /// rehearsal falling through into a write that does not exist.
        ///
        /// Two things are required together, and neither alone is enough:
        ///
        ///   the file opens no transaction of its own - a forwarding command that
        ///   also writes is exactly the defect this guard exists for;
        ///
        ///   and every request it hands to a child command sets dry_run FROM THIS
        ///   FLAG, so a rehearsal cannot reach a child as an apply.
        ///
        /// A command that dispatches to a child without passing the flag fails here,
        /// which is the case worth catching: the child's own default would decide,
        /// and horizun_create_elements defaults dry_run to true while a caller who
        /// sent dry_run=false is owed an apply.
        /// </summary>
        private static bool RehearsalIsDelegated(string text, string flag)
        {
            bool writesItself = Regex.IsMatch(text, @"\bnew\s+Transaction\s*\(") ||
                                Regex.IsMatch(text, @"\bnew\s+TransactionGroup\s*\(");
            if (writesItself) return false;

            int dispatches = Regex.Matches(text, @"\.Execute\s*\(\s*app\b").Count;
            if (dispatches == 0) return false;

            int forwards = Regex.Matches(text, @"\[""dry_run""\]\s*=\s*" + flag + @"\b").Count;

            // A DISPATCH THAT IS ALWAYS A REHEARSAL IS ALSO SAFE, and it is the
            // shape a command needs when its delegate demands a confirmation
            // token: rehearse the child to earn the token, then send the apply
            // guarded by the flag. horizun_cad_connect has to do exactly that -
            // horizun_create_elements refuses a fitting without a token issued by
            // its own dry run - and counting one forwarded flag per dispatch
            // reported that as a rehearsal falling through into a write.
            //
            // So a dispatch is accounted for by a forwarded flag OR by a negative
            // branch on the flag, which is what stops an apply being reached during
            // a rehearsal. A command that dispatches with neither still fails,
            // which is the case this guard exists for.
            int guardedApplies = Regex.Matches(text, @"if\s*\(\s*!" + flag + @"\b").Count;
            return forwards + guardedApplies >= dispatches;
        }

        private static bool RehearsalStops(string text, string flag)
        {
            return Regex.IsMatch(
                text,
                @"if\s*\(\s*" + flag + @"\s*\)" +
                @"(?:(?!\n\s{4,8}(?:private|public|internal|protected)\s)[\s\S]){0,20000}?\breturn\b",
                RegexOptions.Singleline);
        }

        /// <summary>
        /// The guard above was LOOSENED - its bound went from 4000 characters to "up to the
        /// next member declaration" - so it has to be shown to still reject what it was
        /// written for. A weakened test that passes everything is worse than no test: the
        /// green tick becomes evidence of nothing, and this repository has already paid once
        /// for a probe that passed by examining zero verdicts.
        ///
        /// Fabricated sources, deliberately. The real tree is expected to be clean, so it
        /// cannot demonstrate that a defect WOULD be caught - only that none is present.
        /// </summary>
        [Fact]
        public void The_rehearsal_guard_still_rejects_a_rehearsal_that_falls_through()
        {
            // bind_shared_param's actual defect: the flag is read, used once to SKIP the
            // confirmation, and never again. No positive branch exists, so the default call
            // bound the parameter while calling itself a rehearsal.
            const string fellThrough = @"        public CommandResult Run(JObject request)
        {
            bool dryRun = request.Value<bool>(""dry_run"");
            if (!dryRun) { Gate(); }
            Write();
            return CommandResult.Ok();
        }
";
            Assert.False(RehearsalStops(fellThrough, "dryRun"),
                "a handler with no positive branch on the flag must still be reported");

            // The opposite shape, and the reason the bound changed: a rehearsal that DOES
            // return, having first emitted far more than 4000 characters of evidence.
            // family_apply is this shape - a plan, a schema baseline, a geometry baseline -
            // and a test that fires on the LENGTH of a JSON block teaches people to publish
            // less evidence, which is the opposite of what this repository wants.
            string longButCorrect = @"        public CommandResult Run(JObject request)
        {
            bool dryRun = request.Value<bool>(""dry_run"");
            if (dryRun)
            {
                " + new string('x', 9000) + @"
                return CommandResult.Ok(dryResult);
            }
            Write();
            return CommandResult.Ok();
        }
";
            Assert.True(RehearsalStops(longButCorrect, "dryRun"),
                "a rehearsal that returns must pass however much evidence it emits first");

            // And the mistake the bound exists to prevent, which the character count also
            // prevented: the return belongs to a DIFFERENT member, so it is not this
            // rehearsal stopping.
            const string returnsInAnotherMethod = @"        public CommandResult Run(JObject request)
        {
            bool dryRun = request.Value<bool>(""dry_run"");
            if (dryRun) { Log(); }
            Write();
        }

        private static int Other()
        {
            return 1;
        }
";
            Assert.False(RehearsalStops(returnsInAnotherMethod, "dryRun"),
                "a return in the next method is not this rehearsal stopping");
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
