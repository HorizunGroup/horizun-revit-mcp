// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE TOOLS/LIST BUDGET, AS A CEILING THAT A LEDGER REFRESH DOES NOT MOVE (owner,
// 2026-10-10: "un test de presupuesto que impida volver a engordar"). The per-tool
// ledger fails on ANY change and is refreshed by setting a variable; that names the
// tool that grew, but it does not stop the list from growing. These tests do:
//
//   - the worst-case list (every contract row) stays within MeasuredBytes + 3 %;
//   - a long argument description repeated across three or more tools is advertised
//     through a short form, so the same paragraph is never paid for once per tool;
//   - a short form keeps every argument name and the REQUIRED word its full text
//     carries, so shortening never hides an argument a caller must send.
//
// Only the ADVERTISED copy changes; the contract, its hash and
// horizun://contract/tools keep every word (AdvertisedSchemaEquivalenceTests).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ToolsListBudgetTests
    {
        // MEASURED 2026-10-10 on feat/tools-list-budget: Tools.Publish(t, true) over
        // Tools.All, Formatting.None, UTF-8 - the same measurement as the ledger's "all".
        // Before the short forms of this change: 475,647 B. Re-measured 2026-10-10 (close) on the
        // 2.2.0 candidate with its own guard-argument wording: 475,800 B before, 473,188 B after.
        internal const int MeasuredBytes = 473_188;
        internal const double Allowance = 0.03;

        internal const int RepeatedTextMinLength = 100;
        internal const int RepeatedTextMinTools = 3;

        [Fact]
        public void Worst_case_list_stays_within_its_measured_budget()
        {
            int bytes = Encoding.UTF8.GetByteCount(
                new JArray(Tools.All.Select(t => (JToken)Tools.Publish(t, true))).ToString(Formatting.None));
            int limit = (int)Math.Floor(MeasuredBytes * (1 + Allowance));
            Assert.True(bytes <= limit,
                "tools/list (every contract row) is " + bytes + " B, over its budget of " + limit + " B (" + MeasuredBytes +
                " B measured + " + (Allowance * 100) + " %). Shorten what grew (see tools-list-ledger.json for which tool), " +
                "or raise MeasuredBytes as a deliberate, reviewed decision - never as a side effect of a refresh.");
        }

        [Fact]
        public void No_long_description_repeats_across_tools_without_a_short_form()
        {
            var tools = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (ToolDef t in Tools.All)
            {
                JToken schema = Tools.Publish(t, true)["inputSchema"];
                foreach (string d in Descriptions(schema).Where(d => d.Length >= RepeatedTextMinLength))
                {
                    if (!tools.TryGetValue(d, out SortedSet<string> set)) tools[d] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.Add(t.Name);
                }
            }
            var shortForms = new HashSet<string>(Tools.AdvertisedShortForms.Values, StringComparer.Ordinal);
            string[] offenders = tools.Where(kv => kv.Value.Count >= RepeatedTextMinTools && !shortForms.Contains(kv.Key))
                                      .Select(kv => kv.Value.Count + " tools (" + string.Join(", ", kv.Value.Take(3)) + "...): " + kv.Key)
                                      .ToArray();
            Assert.True(offenders.Length == 0,
                "these argument descriptions are advertised verbatim in " + RepeatedTextMinTools + "+ tools; give each a short " +
                "form in Tools.AdvertisedShortForms:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void A_short_form_keeps_every_argument_name_and_the_required_word()
        {
            var problems = new List<string>();
            foreach (var kv in Tools.AdvertisedShortForms)
            {
                if (kv.Value.Length >= kv.Key.Length) problems.Add("not shorter: " + kv.Value);
                if (kv.Key.StartsWith("REQUIRED", StringComparison.OrdinalIgnoreCase) &&
                    !kv.Value.StartsWith("REQUIRED", StringComparison.OrdinalIgnoreCase))
                    problems.Add("lost REQUIRED: " + kv.Value);
                foreach (string name in ArgumentNames(kv.Key))
                    if (kv.Value.IndexOf(name, StringComparison.Ordinal) < 0)
                        problems.Add("lost '" + name + "': " + kv.Value);
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void Argument_names_are_read_from_the_text()
        {
            Assert.Equal(new[] { "dry_run=false", "expected_document", "target_document_title" },
                ArgumentNames("REQUIRED when dry_run=false. Aliases: expected_document, target_document_title. Revit's UI.").ToArray());
        }

        /// <summary>snake_case identifiers (with an optional =value) - the names a caller types.</summary>
        internal static IEnumerable<string> ArgumentNames(string text) =>
            Regex.Matches(text, @"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+(?:=[a-z0-9_]+)?\b").Cast<Match>().Select(m => m.Value).Distinct();

        private static IEnumerable<string> Descriptions(JToken node)
        {
            if (node is JObject o)
            {
                if (o["description"] is JValue d && d.Type == JTokenType.String) yield return (string)d;
                foreach (JProperty p in o.Properties())
                    if (p.Name != "description")
                        foreach (string s in Descriptions(p.Value)) yield return s;
            }
            else if (node is JArray a)
                foreach (JToken item in a)
                    foreach (string s in Descriptions(item)) yield return s;
        }
    }
}
