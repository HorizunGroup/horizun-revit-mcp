// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Per-section budgets and cursors, proved by BEHAVIOUR.
//
// Two properties are the point of the whole feature and both are demonstrated
// here rather than asserted about:
//
//   * a large section does not eat another section's budget;
//   * paging twice returns exactly what one unpaged call returns - same rows,
//     same order, no duplicate, nothing lost.
//
// Everything else is the ways it must REFUSE. A cursor that cannot be trusted
// is refused by name; it is never quietly read as "start again", because a
// caller paging through ninety thousand rows cannot tell page one from page
// nine when both arrive looking identical.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionBudgetTests
    {
        private static readonly string[] Sections =
        {
            "document", "categories", "cleanliness", "naming", "documentation",
            "project_info", "health", "links", "worksets", "design_options", "lines", "types"
        };

        private static List<KeyedRow> Rows(int n, string prefix = "e")
        {
            var rows = new List<KeyedRow>();
            for (int i = 0; i < n; i++)
            {
                // Zero-padded so ordinal order is numeric order. Unpadded ids are
                // exactly how "10" comes before "9" and a page boundary lands in
                // the wrong place.
                string key = prefix + i.ToString("D6");
                rows.Add(new KeyedRow(key, new JObject { ["id"] = key, ["n"] = i }));
            }
            return rows;
        }

        // ---------------------------------------------------------------- budgets

        [Fact]
        public void No_budget_means_every_section_gets_the_default()
        {
            BudgetPlan p = SectionBudgets.Parse(null, Sections, 50);
            Assert.True(p.Ok);
            Assert.Equal(50, p.LimitFor("categories", "by_category"));
            Assert.Equal(50, p.LimitFor("links", "anything"));
        }

        [Fact]
        public void One_section_can_be_raised_without_raising_any_other()
        {
            // THE POINT OF THE FEATURE. Forty-one warnings and ninety thousand
            // lines cannot share one number.
            var limits = JObject.Parse(@"{ ""cleanliness"": 500, ""lines"": 5 }");
            BudgetPlan p = SectionBudgets.Parse(limits, Sections, 50);
            Assert.True(p.Ok);

            Assert.Equal(500, p.LimitFor("cleanliness", "warnings"));
            Assert.Equal(5, p.LimitFor("lines", "by_style"));
            Assert.Equal(50, p.LimitFor("categories", "by_category"));   // untouched
            Assert.Equal(50, p.LimitFor("types", "unused"));             // untouched
        }

        [Fact]
        public void A_big_section_does_not_consume_another_sections_budget()
        {
            // Demonstrated on real pages, not on the plan object: the sizes that
            // come back are the sizes each section was given, whatever the others did.
            var limits = JObject.Parse(@"{ ""lines"": 3, ""cleanliness"": 200 }");
            BudgetPlan p = SectionBudgets.Parse(limits, Sections, 10);

            BucketPage lines = Paging.Page(Rows(9000), p.LimitFor("lines", "by_style"), null, "doc", "lines", "by_style");
            BucketPage warns = Paging.Page(Rows(41), p.LimitFor("cleanliness", "warnings"), null, "doc", "cleanliness", "warnings");
            BucketPage cats = Paging.Page(Rows(30), p.LimitFor("categories", "by_category"), null, "doc", "categories", "by_category");

            Assert.Equal(3, lines.Returned);
            Assert.Equal(9000, lines.Total);      // the total is the population, not the page
            Assert.True(lines.Truncated);

            Assert.Equal(41, warns.Returned);     // all of them: its own budget was 200
            Assert.False(warns.Truncated);

            Assert.Equal(10, cats.Returned);      // the default, untouched by either
            Assert.True(cats.Truncated);
        }

        [Fact]
        public void A_bucket_can_be_budgeted_inside_a_section()
        {
            var limits = JObject.Parse(@"{ ""cleanliness"": { ""limit"": 20, ""buckets"": { ""warnings"": 400 } } }");
            BudgetPlan p = SectionBudgets.Parse(limits, Sections, 50);
            Assert.True(p.Ok);
            Assert.Equal(400, p.LimitFor("cleanliness", "warnings"));   // the bucket wins
            Assert.Equal(20, p.LimitFor("cleanliness", "anything_else"));  // the section
            Assert.Equal(50, p.LimitFor("naming", "whatever"));          // the default
        }

        // ------------------------------------------------------------- refusals

        [Fact]
        public void A_section_that_does_not_exist_is_refused_and_the_real_ones_are_named()
        {
            BudgetPlan p = SectionBudgets.Parse(JObject.Parse(@"{ ""warning"": 10 }"), Sections, 50);
            Assert.False(p.Ok);
            Assert.Equal(BudgetCodes.UnknownSection, p.Code);
            Assert.Contains("warning", p.Message);
            Assert.Contains("cleanliness", p.Message);   // the list is offered
        }

        [Fact]
        public void One_bad_key_refuses_the_whole_request_rather_than_half_honouring_it()
        {
            // Honouring the good half would hand back a default-sized bucket for
            // the misspelled one, which reads exactly like a section with nothing
            // in it.
            BudgetPlan p = SectionBudgets.Parse(
                JObject.Parse(@"{ ""links"": 5, ""lnks"": 500 }"), Sections, 50);
            Assert.False(p.Ok);
            Assert.Empty(p.BySection);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-9999)]
        public void A_limit_below_one_is_refused(int n)
        {
            BudgetPlan p = SectionBudgets.Parse(
                JObject.Parse("{ \"links\": " + n + " }"), Sections, 50);
            Assert.False(p.Ok);
            Assert.Equal(BudgetCodes.InvalidLimit, p.Code);
        }

        [Fact]
        public void A_limit_above_the_ceiling_is_refused_and_the_ceiling_is_named()
        {
            BudgetPlan p = SectionBudgets.Parse(
                JObject.Parse("{ \"links\": " + (SectionBudgets.MaxLimit + 1) + " }"), Sections, 50);
            Assert.False(p.Ok);
            Assert.Equal(BudgetCodes.LimitTooLarge, p.Code);
            Assert.Contains(SectionBudgets.MaxLimit.ToString(), p.Message);
            Assert.Contains("cursor", p.Message);   // it says what to do instead
        }

        [Fact]
        public void An_unknown_key_inside_a_section_budget_is_refused()
        {
            BudgetPlan p = SectionBudgets.Parse(
                JObject.Parse(@"{ ""links"": { ""limitt"": 5 } }"), Sections, 50);
            Assert.False(p.Ok);
            Assert.Equal(BudgetCodes.UnknownBudgetKey, p.Code);
        }

        [Fact]
        public void A_default_limit_that_is_itself_invalid_is_refused()
        {
            Assert.False(SectionBudgets.Parse(null, Sections, 0).Ok);
            Assert.False(SectionBudgets.Parse(null, Sections, SectionBudgets.MaxLimit + 1).Ok);
        }

        [Fact]
        public void Section_limits_that_are_not_an_object_are_refused()
        {
            Assert.False(SectionBudgets.Parse(JToken.Parse("5"), Sections, 50).Ok);
            Assert.False(SectionBudgets.Parse(JToken.Parse("[1,2]"), Sections, 50).Ok);
            Assert.False(SectionBudgets.Parse(JToken.Parse("\"x\""), Sections, 50).Ok);
        }

        // -------------------------------------------------------------- paging

        [Fact]
        public void Paging_twice_returns_exactly_what_one_call_returns()
        {
            // THE OTHER POINT OF THE FEATURE, demonstrated by reconstruction.
            List<KeyedRow> all = Rows(250);

            BucketPage whole = Paging.Page(all, 250, null, "doc", "lines", "by_style");
            Assert.Equal(250, whole.Returned);
            Assert.False(whole.Truncated);

            BucketPage one = Paging.Page(all, 100, null, "doc", "lines", "by_style");
            Assert.True(one.Truncated);
            Assert.NotNull(one.NextCursor);

            CursorRead c1 = SectionCursor.Decode(one.NextCursor, "doc", "lines", "by_style");
            Assert.True(c1.Ok);
            BucketPage two = Paging.Page(all, 100, c1.AfterKey, "doc", "lines", "by_style");
            Assert.True(two.Truncated);

            CursorRead c2 = SectionCursor.Decode(two.NextCursor, "doc", "lines", "by_style");
            BucketPage three = Paging.Page(all, 100, c2.AfterKey, "doc", "lines", "by_style");
            Assert.False(three.Truncated);
            Assert.Null(three.NextCursor);

            List<string> paged = one.Items.Concat(two.Items).Concat(three.Items)
                .Select(t => t.Value<string>("id")).ToList();
            List<string> once = whole.Items.Select(t => t.Value<string>("id")).ToList();

            Assert.Equal(250, paged.Count);
            Assert.Equal(once, paged);                       // same rows, same order
            Assert.Equal(paged.Count, paged.Distinct().Count());  // and no duplicates
        }

        [Fact]
        public void Every_page_reports_the_whole_population_not_what_is_left()
        {
            // A second page that said "50 total" would read like a small bucket.
            List<KeyedRow> all = Rows(120);
            BucketPage one = Paging.Page(all, 100, null, "d", "s", "b");
            CursorRead c = SectionCursor.Decode(one.NextCursor, "d", "s", "b");
            BucketPage two = Paging.Page(all, 100, c.AfterKey, "d", "s", "b");

            Assert.Equal(120, one.Total);
            Assert.Equal(120, two.Total);
            Assert.Equal(20, two.Returned);
        }

        [Fact]
        public void Order_is_ordinal_so_two_machines_page_a_model_the_same_way()
        {
            // Culture-aware ordering would page differently under a Turkish locale.
            var rows = new List<KeyedRow>
            {
                new KeyedRow("I", new JObject { ["id"] = "I" }),
                new KeyedRow("i", new JObject { ["id"] = "i" }),
                new KeyedRow("A", new JObject { ["id"] = "A" }),
                new KeyedRow("a", new JObject { ["id"] = "a" }),
            };
            BucketPage p = Paging.Page(rows, 10, null, "d", "s", "b");
            Assert.Equal(new[] { "A", "I", "a", "i" }, p.Items.Select(t => t.Value<string>("id")).ToArray());
        }

        [Fact]
        public void A_complete_page_offers_no_cursor()
        {
            BucketPage p = Paging.Page(Rows(5), 50, null, "d", "s", "b");
            Assert.False(p.Truncated);
            Assert.Null(p.NextCursor);
            Assert.False(p.ToJson().ContainsKey("next_cursor"));
        }

        [Fact]
        public void An_empty_population_is_not_a_truncated_one()
        {
            // Zero rows and "there is more" are different answers.
            BucketPage p = Paging.Page(new List<KeyedRow>(), 50, null, "d", "s", "b");
            Assert.Equal(0, p.Total);
            Assert.Equal(0, p.Returned);
            Assert.False(p.Truncated);
            Assert.Null(p.NextCursor);
        }

        // ------------------------------------------------------------- cursors

        [Fact]
        public void No_cursor_means_the_first_page_and_says_so()
        {
            CursorRead c = SectionCursor.Decode(null, "d", "s", "b");
            Assert.True(c.Ok);
            Assert.True(c.FromStart);
            Assert.Null(c.AfterKey);
        }

        [Fact]
        public void A_cursor_from_another_document_is_refused_not_restarted()
        {
            string cur = SectionCursor.Encode("doc-A", "lines", "by_style", "e000099");
            CursorRead c = SectionCursor.Decode(cur, "doc-B", "lines", "by_style");
            Assert.False(c.Ok);
            Assert.Equal(BudgetCodes.CursorWrongDocument, c.Code);
            Assert.False(c.FromStart);
        }

        [Fact]
        public void A_cursor_from_another_section_or_bucket_is_refused()
        {
            string cur = SectionCursor.Encode("d", "lines", "by_style", "k");
            Assert.Equal(BudgetCodes.CursorWrongSection,
                SectionCursor.Decode(cur, "d", "types", "by_style").Code);
            Assert.Equal(BudgetCodes.CursorWrongBucket,
                SectionCursor.Decode(cur, "d", "lines", "unused").Code);
        }

        [Fact]
        public void A_cursor_from_another_contract_version_is_refused()
        {
            string cur = SectionCursor.Encode("d", "s", "b", "k");
            byte[] raw = Convert.FromBase64String(Pad(cur.Replace('-', '+').Replace('_', '/')));
            string text = System.Text.Encoding.UTF8.GetString(raw).Replace(SectionCursor.Version, "hzc0");
            string older = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            CursorRead c = SectionCursor.Decode(older, "d", "s", "b");
            Assert.False(c.Ok);
            Assert.Equal(BudgetCodes.CursorWrongVersion, c.Code);
        }

        [Theory]
        [InlineData("not-base64-!!!")]
        [InlineData("YWJj")]              // decodes, but has none of the fields
        [InlineData("a")]                 // impossible base64 length
        public void A_corrupt_cursor_is_refused_and_never_read_as_the_start(string bad)
        {
            CursorRead c = SectionCursor.Decode(bad, "d", "s", "b");
            Assert.False(c.Ok);
            Assert.False(c.FromStart);
            Assert.Contains(c.Code, new[] { BudgetCodes.CursorMalformed, BudgetCodes.CursorWrongVersion });
        }

        [Fact]
        public void A_cursor_with_the_right_version_but_the_wrong_shape_is_refused()
        {
            // Isolates the FIELD-COUNT check. The corrupt-cursor cases above all fail
            // the version check first, so removing the length guard left them still
            // refusing - for a different reason - and the mutation did not bite.
            // This one carries the current version and only three fields.
            string raw = SectionCursor.Version + "" + "d" + "" + "s";
            string cur = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            CursorRead c = SectionCursor.Decode(cur, "d", "s", "b");
            Assert.False(c.Ok);
            Assert.Equal(BudgetCodes.CursorMalformed, c.Code);
            Assert.False(c.FromStart);
        }

        [Fact]
        public void A_key_containing_unusual_characters_survives_a_round_trip()
        {
            // Names come out of somebody's model; they contain accents, CJK and
            // spaces, and a delimiter collision would silently truncate a cursor.
            foreach (string key in new[] { "Muro Exterior — 01", "階段 A", "a|b\\c\"d", "e/f+g=h" })
            {
                string cur = SectionCursor.Encode("d", "s", "b", key);
                CursorRead c = SectionCursor.Decode(cur, "d", "s", "b");
                Assert.True(c.Ok, key);
                Assert.Equal(key, c.AfterKey);
            }
        }

        [Fact]
        public void Resuming_after_the_population_shrank_returns_each_survivor_at_most_once()
        {
            // Somebody may be modelling while the audit runs. Key-based resumption
            // cannot promise the whole set, but it can promise no duplicates - which
            // an offset cannot.
            List<KeyedRow> before = Rows(100);
            BucketPage one = Paging.Page(before, 40, null, "d", "s", "b");
            CursorRead c = SectionCursor.Decode(one.NextCursor, "d", "s", "b");

            List<KeyedRow> after = before.Where((r, i) => i % 3 != 0).ToList();
            BucketPage two = Paging.Page(after, 40, c.AfterKey, "d", "s", "b");

            List<string> seen = one.Items.Concat(two.Items).Select(t => t.Value<string>("id")).ToList();
            Assert.Equal(seen.Count, seen.Distinct().Count());
            Assert.All(two.Items, t => Assert.True(
                string.CompareOrdinal(t.Value<string>("id"), c.AfterKey) > 0,
                "a row at or before the cursor came back a second time"));
        }

        private static string Pad(string s)
        {
            switch (s.Length % 4) { case 2: return s + "=="; case 3: return s + "="; default: return s; }
        }
    }
}
