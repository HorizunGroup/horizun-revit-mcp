// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// EVERY FIELD THIS CAPABILITY CAPTURES IS CONSUMED.
//
// "Captured and never used" is the shape of the two worst defects in this
// capability's history, and it shipped at two different levels:
//
//   * the pyRevit port captured five facts about a door and rebuilt it from
//     them, so sill heights, phases, worksets and every project parameter were
//     lost without a word;
//   * the first rewrite captured ElementsAtEnd0, ElementsAtEnd1 and the cut
//     order for every wall and compared none of them, so the end joins were
//     lost exactly as before - one level up.
//
// Neither was findable by reading the code that captured. Both were findable by
// asking "who reads this?" - which is what this file does, mechanically.
//
// The field lists are ENUMERATED FROM THE SOURCE rather than written out here,
// so a field added tomorrow is covered by these tests the moment it exists. A
// field that genuinely has no consumer must be named in the exemption table
// below WITH ITS REASON: an exemption is a decision somebody made, not a gap
// nobody noticed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallCapturedFieldsAreConsumedTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>
        /// The public fields declared on one class in a source file. Parsed rather than
        /// reflected because these types need a Revit Document to load.
        /// </summary>
        private static List<string> FieldsOf(string source, string className)
        {
            int start = source.IndexOf("class " + className, StringComparison.Ordinal);
            Assert.True(start >= 0, "class " + className + " was not found - this test is looking at the wrong file");

            int open = source.IndexOf('{', start);
            int depth = 0;
            int end = open;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }

            string body = source.Substring(open, end - open);

            // `public <type> Name;` and `public <type> Name = ...;` - fields, not properties.
            var matches = Regex.Matches(body, @"public\s+(?!static|const)[\w<>,\[\]\.\? ]+?\s+(\w+)\s*(?:=|;)");
            var fields = new List<string>();
            foreach (Match match in matches)
            {
                string name = match.Groups[1].Value;
                if (!fields.Contains(name)) fields.Add(name);
            }

            Assert.NotEmpty(fields);
            return fields;
        }

        // ---- the exemption tables --------------------------------------------------
        //
        // A field here is one nothing reads BY DECISION. Each carries the reason, so the
        // next person can disagree with the decision rather than discover the gap.

        private static readonly Dictionary<string, string> JoinExemptions =
            new Dictionary<string, string>(StringComparer.Ordinal);   // deliberately empty

        private static readonly Dictionary<string, string> SnapshotExemptions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Kind"] = "not a fact to preserve: it SELECTS which verifier runs, and the dispatch is " +
                           "pinned separately by Every_registered_dependency_kind_is_dispatched_by_the_verifier",
                ["Insert"] = "a container, not a fact. Its own fields are enumerated and checked below",
                ["RebarDescription"] = "the RAW RebarFacts.Describe reply, kept as evidence for the report. The " +
                                       "facts that have to be COMPARED are lifted out of it into named fields " +
                                       "beside this one - digesting the whole JObject would make the fingerprint " +
                                       "depend on every future change to that reply's prose",
            };

        private static readonly Dictionary<string, string> InsertExemptions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ElementId"] = "the identity the whole check is keyed BY - every comparison starts from it",
                ["SubComponentCount"] = "compared through the subcomponent identity check, which is strictly stronger",
                ["HostId"] = "copied onto the enclosing DependencySnapshot.HostId, which IS fingerprinted; the " +
                             "verifier compares the live host against the carrier directly, which is stronger " +
                             "than comparing it against a remembered id",
            };

        // ---- joins -------------------------------------------------------------------

        [Fact]
        public void Every_field_WallJoinFacts_captures_is_read_by_the_verifier()
        {
            // The exact defect that shipped: four fields captured, none compared.
            string verifier = Source("src/Horizun.Revit/Commands/WallSplitVerifier.cs");
            List<string> fields = FieldsOf(Source("src/Horizun.Revit/Commands/WallSplitFacts.cs"), "WallJoinFacts");

            var unread = fields
                .Where(f => !JoinExemptions.ContainsKey(f))
                .Where(f => !verifier.Contains("before." + f, StringComparison.Ordinal))
                .ToList();

            Assert.True(unread.Count == 0,
                "WallJoinFacts captures these and the verifier never reads them: " + string.Join(", ", unread) +
                ". Either compare them or add them to JoinExemptions with the reason.");
        }

        [Fact]
        public void Every_field_WallJoinFacts_captures_enters_its_fingerprint()
        {
            // ...and the token has to move when any of them changes, or a join broken
            // between the dry run and the apply is written over.
            string facts = Source("src/Horizun.Revit/Commands/WallSplitFacts.cs");
            int start = facts.IndexOf("public static string FingerprintOf(WallJoinFacts joins)", StringComparison.Ordinal);
            Assert.True(start >= 0, "the join fingerprint builder was not found");
            int end = facts.IndexOf("public static string WallStateFingerprint", start, StringComparison.Ordinal);
            string builder = facts.Substring(start, end - start);

            List<string> fields = FieldsOf(facts, "WallJoinFacts");
            var missing = fields
                .Where(f => !JoinExemptions.ContainsKey(f))
                .Where(f => !builder.Contains("joins." + f, StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                "these join facts are captured but do not enter the fingerprint: " + string.Join(", ", missing));
        }

        // ---- dependency snapshots -------------------------------------------------

        [Fact]
        public void Every_field_DependencySnapshot_captures_enters_its_fingerprint()
        {
            string facts = Source("src/Horizun.Revit/Commands/WallSplitFacts.cs");
            int start = facts.IndexOf("public static string FingerprintOf(DependencySnapshot snapshot)",
                                      StringComparison.Ordinal);
            Assert.True(start >= 0, "the dependency fingerprint builder was not found");
            int end = facts.IndexOf("public static string FingerprintOf(WallJoinFacts", start, StringComparison.Ordinal);
            string builder = facts.Substring(start, end - start);

            List<string> fields = FieldsOf(facts, "DependencySnapshot");
            var missing = fields
                .Where(f => !SnapshotExemptions.ContainsKey(f))
                .Where(f => !builder.Contains("snapshot." + f, StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                "these dependency facts are captured but do not enter the fingerprint, so a change to them " +
                "between the dry run and the apply would go unnoticed: " + string.Join(", ", missing));
        }

        [Fact]
        public void Every_field_InsertSnapshot_captures_enters_the_fingerprint_or_the_verifier()
        {
            // A door's state is checked in two places for two different reasons: the
            // fingerprint refuses a STALE plan, the verifier refuses a BROKEN conversion.
            // A field is allowed to serve either, but not neither.
            string facts = Source("src/Horizun.Revit/Commands/WallSplitFacts.cs");
            string verifier = Source("src/Horizun.Revit/Commands/WallSplitVerifier.cs");

            List<string> fields = FieldsOf(facts, "InsertSnapshot");
            var missing = fields
                .Where(f => !InsertExemptions.ContainsKey(f))
                .Where(f => !facts.Contains("insert." + f, StringComparison.Ordinal))
                .Where(f => !verifier.Contains("before." + f, StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                "these insert facts are captured and nothing reads them: " + string.Join(", ", missing));
        }

        // ---- the exemptions themselves --------------------------------------------

        [Fact]
        public void Every_exemption_names_a_field_that_still_exists()
        {
            // An exemption for a field somebody deleted is stale reasoning that hides the
            // next real gap.
            string facts = Source("src/Horizun.Revit/Commands/WallSplitFacts.cs");

            foreach (string field in SnapshotExemptions.Keys)
                Assert.Contains(field, FieldsOf(facts, "DependencySnapshot"));
            foreach (string field in InsertExemptions.Keys)
                Assert.Contains(field, FieldsOf(facts, "InsertSnapshot"));
            foreach (string field in JoinExemptions.Keys)
                Assert.Contains(field, FieldsOf(facts, "WallJoinFacts"));
        }

        [Fact]
        public void Every_exemption_carries_a_reason()
        {
            foreach (var table in new[] { SnapshotExemptions, InsertExemptions, JoinExemptions })
                foreach (KeyValuePair<string, string> exemption in table)
                    Assert.True(exemption.Value != null && exemption.Value.Length > 30,
                        exemption.Key + " is exempted without a reason worth reading");
        }

        [Fact]
        public void The_field_parser_actually_finds_fields()
        {
            // If the parser silently matched nothing, every test above would pass vacuously.
            // This is the guard against that - the mistake this suite has already made twice.
            string facts = Source("src/Horizun.Revit/Commands/WallSplitFacts.cs");

            Assert.True(FieldsOf(facts, "WallJoinFacts").Count >= 8);
            Assert.True(FieldsOf(facts, "DependencySnapshot").Count >= 25);
            Assert.True(FieldsOf(facts, "InsertSnapshot").Count >= 18);

            Assert.Contains("ElementsAtEnd0", FieldsOf(facts, "WallJoinFacts"));
            Assert.Contains("CutByOther", FieldsOf(facts, "WallJoinFacts"));
            Assert.Contains("TaggedElementIds", FieldsOf(facts, "DependencySnapshot"));
            Assert.Contains("Rotation", FieldsOf(facts, "InsertSnapshot"));
        }
    }
}
