// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Story 5.1 is being wired one command at a time, which means the repository will
// spend a long while in a MIXED state: some commands bind their confirmation token
// to the elements they resolved, the rest bind only the request. A mixed state is
// fine. A mixed state nobody can see is not - and the two ways it goes wrong are
// both silent:
//
//   * A command RECORDS a plan on its rehearsal and then never COMPARES one on its
//     apply. The reply grows a plan_resolved block and a sentence promising the
//     token is bound to those elements, and nothing checks it. That is worse than
//     not wiring the command at all, because the promise is now false in writing.
//   * Somebody deletes the disclosure sentence in DocumentGate while commands are
//     still unwired. Then eleven commands stop admitting the limit, and a caller
//     reads a guarantee that was never there.
//
// These tests read the SOURCE, because that is where the mistake would be made. It
// is a coarse instrument - it matches text, not behaviour - and it is deliberately
// coarse: it has to keep working for the eleven commands that have no test harness
// of their own, since they cannot be instantiated without Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanWiringTests
    {
        /// <summary>
        /// Walks up from the test binary to the repository root. Anchored on a file that
        /// cannot move without the repository being restructured.
        /// </summary>
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        private static Dictionary<string, string> CommandSources()
        {
            string dir = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            return Directory.GetFiles(dir, "*.cs")
                            .ToDictionary(Path.GetFileName, File.ReadAllText);
        }

        /// <summary>
        /// The premise of every test below: this really is the set of commands that issue
        /// a confirmation token. If that set ever empties, these tests would pass by
        /// examining nothing, which is the failure mode the rollback probe already taught
        /// this repository to check for explicitly.
        /// </summary>
        [Fact]
        public void There_are_commands_that_issue_confirmation_tokens()
        {
            var stamping = CommandSources().Where(kv => kv.Value.Contains("StampConfirmation")).ToList();
            Assert.True(stamping.Count >= 10,
                "expected the write commands to issue tokens; found " + stamping.Count);
        }

        /// <summary>
        /// A plan recorded on the rehearsal and never compared on the apply is theatre: the
        /// reply promises the token is bound to those elements and nothing enforces it.
        /// Recording and comparing are two halves of one mechanism and neither is useful
        /// alone.
        /// </summary>
        [Fact]
        public void A_command_that_records_a_plan_also_compares_one()
        {
            var offenders = new List<string>();
            foreach (var kv in CommandSources())
            {
                if (!kv.Value.Contains("RecordResolvedPlan")) continue;

                // The plan-aware overload takes two extra arguments after planHash. Match
                // the call across line breaks - every wired command wraps it.
                bool comparesPlan = Regex.IsMatch(
                    kv.Value,
                    @"RequireConfirmation\s*\([^;]*?planHash\s*,\s*[\r\n\s]*[A-Za-z_][A-Za-z0-9_]*\s*,",
                    RegexOptions.Singleline);
                if (!comparesPlan) offenders.Add(kv.Key);
            }
            Assert.True(offenders.Count == 0,
                "these commands record a resolved plan but never compare one at apply, so the " +
                "plan_resolved block and its promise are unenforced: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// The other half. While ANY command still stamps a token without materialising a
        /// plan, the gate must keep saying so in the reply - a guarantee nobody mentions
        /// reads exactly like one that held. When the last command is wired this test stops
        /// requiring the sentence, which is the correct time for it to be deleted.
        /// </summary>
        [Fact]
        public void While_any_command_is_unwired_the_gate_still_admits_the_limit()
        {
            var unwired = CommandSources()
                .Where(kv => kv.Value.Contains("StampConfirmation") &&
                             !kv.Value.Contains("RecordResolvedPlan"))
                .Select(kv => kv.Key)
                .ToList();
            if (unwired.Count == 0) return;   // fully wired: the disclosure may go

            string gate = File.ReadAllText(Path.Combine(
                RepoRoot(), "src", "Horizun.Revit", "Core", "DocumentGate.cs"));
            Assert.Contains("bound to the REQUEST, not to the resolved element set", gate);
            // Asserted separately: in the source the sentence is split across a line break
            // by string concatenation, so the joined phrase does not appear in the file.
            Assert.Contains("materialise its plan yet", gate);
        }
    }
}
