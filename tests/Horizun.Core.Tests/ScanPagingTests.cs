// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The paging context the twelve emitters hold, and the invariant that every one
// of them actually holds it.
//
// The behaviour is proved by running it. The WIRING is proved by reading the
// source, and that is a deliberate second line rather than a substitute: the
// emitters need a Revit Document to call, so a mutation that swaps one section's
// budget for another's has to be caught by an invariant over the call sites.
// Each such mutation is in the ledger and each breaks a named test here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScanPagingTests
    {
        private static readonly string[] Sections =
        {
            "document", "categories", "cleanliness", "naming", "documentation",
            "project_info", "health", "links", "worksets", "design_options", "lines", "types"
        };

        private static ScanPagingContext Ctx(string limitsJson = null, string cursor = null, int top = 50)
        {
            BudgetPlan plan = SectionBudgets.Parse(
                limitsJson == null ? null : JToken.Parse(limitsJson), Sections, top);
            Assert.True(plan.Ok, plan.Message);
            return new ScanPagingContext { Plan = plan, DocumentFingerprint = "fp-A", RawCursor = cursor };
        }

        private static List<JToken> Rows(int n) =>
            Enumerable.Range(0, n).Select(i => (JToken)new JObject { ["id"] = i, ["name"] = "row" + i }).ToList();

        // ------------------------------------------------------------ behaviour

        [Fact]
        public void Each_bucket_gets_its_own_budget_and_the_others_are_untouched()
        {
            ScanPagingContext c = Ctx(@"{ ""cleanliness"": { ""buckets"": { ""warnings"": 400 }, ""limit"": 7 },
                                          ""lines"": 3 }", top: 10);

            Assert.Equal(400, c.LimitFor("cleanliness", "warnings"));
            Assert.Equal(7, c.LimitFor("cleanliness", "cad_imported"));
            Assert.Equal(3, c.LimitFor("lines", "anything"));
            Assert.Equal(10, c.LimitFor("types", "types"));

            Assert.Equal(7, c.Bucket(Rows(500), "cleanliness", "cad_imported").Value<int>("returned"));
            Assert.Equal(10, c.Bucket(Rows(500), "types", "types").Value<int>("returned"));
        }

        [Fact]
        public void A_bucket_reports_limit_total_returned_and_truncated()
        {
            JObject b = Ctx(@"{ ""links"": 5 }").Bucket(Rows(120), "links", "rvt_links");
            Assert.Equal(5, b.Value<int>("limit"));
            Assert.Equal(120, b.Value<int>("total"));
            Assert.Equal(5, b.Value<int>("returned"));
            Assert.True(b.Value<bool>("truncated"));
            // A PLAIN BUCKET DOES NOT CLAIM EXACTNESS. It knows the rows it was
            // handed; only the section knows whether it could read the whole
            // population, and it says so by calling BucketLowerBound. This used to
            // assert `true` - the assumption that made total_is_exact unfalsifiable
            // for all 68 buckets, including the ones whose own prose calls their
            // list an upper bound.
            Assert.Null(b["total_is_exact"]);
            Assert.NotNull(b["next_cursor"]);
        }

        [Fact]
        public void Two_hundred_and_fifty_rows_come_back_whole_in_three_pages()
        {
            List<JToken> all = Rows(250);

            JObject whole = Ctx(@"{ ""types"": 250 }").Bucket(all, "types", "types");
            List<int> once = whole["items"].Select(t => t.Value<int>("id")).ToList();
            Assert.Equal(250, once.Count);

            var seen = new List<int>();
            string cursor = null;
            for (int page = 0; page < 3; page++)
            {
                JObject b = Ctx(@"{ ""types"": 100 }", cursor).Bucket(all, "types", "types");
                seen.AddRange(b["items"].Select(t => t.Value<int>("id")));
                Assert.Equal(250, b.Value<int>("total"));      // always the population
                cursor = b["next_cursor"]?.ToString();
                if (page < 2) Assert.NotNull(cursor);
            }
            Assert.Null(cursor);
            Assert.Equal(once, seen);
            Assert.Equal(seen.Count, seen.Distinct().Count());
        }

        [Fact]
        public void A_cursor_only_resumes_the_bucket_it_was_minted_for()
        {
            JObject first = Ctx(@"{ ""types"": 10 }").Bucket(Rows(40), "types", "types");
            string cur = first.Value<string>("next_cursor");

            // Offered to the SAME bucket: resumes.
            JObject same = Ctx(@"{ ""types"": 10 }", cur).Bucket(Rows(40), "types", "types");
            Assert.True(same.Value<bool>("resumed"));
            Assert.DoesNotContain(same["items"].Select(t => t.Value<int>("id")),
                                  id => first["items"].Select(x => x.Value<int>("id")).Contains(id));

            // Offered to a DIFFERENT bucket: that bucket starts at the beginning and
            // this is not an error - it is simply somebody else's cursor.
            ScanPagingContext c = Ctx(@"{ ""links"": 10 }", cur);
            JObject other = c.Bucket(Rows(40), "links", "rvt_links");
            Assert.False(other.Value<bool>("resumed"));
            Assert.Empty(c.CursorProblems);
        }

        [Fact]
        public void A_cursor_from_another_document_is_reported_not_silently_restarted()
        {
            string cur = SectionCursor.Encode("fp-OTHER", "types", "types", "k");
            ScanPagingContext c = Ctx(@"{ ""types"": 10 }", cur);
            JObject b = c.Bucket(Rows(40), "types", "types");

            Assert.False(b.Value<bool>("resumed"));
            Assert.Single(c.CursorProblems);
            Assert.Equal(BudgetCodes.CursorWrongDocument, c.CursorProblems[0].Value<string>("code"));
        }

        [Fact]
        public void A_corrupt_cursor_is_reported_once_not_once_per_bucket()
        {
            ScanPagingContext c = Ctx(@"{ ""types"": 10 }", "!!!not-a-cursor!!!");
            c.Bucket(Rows(5), "types", "types");
            c.Bucket(Rows(5), "links", "rvt_links");
            c.Bucket(Rows(5), "naming", "views");
            Assert.Single(c.CursorProblems);
        }

        [Fact]
        public void A_bucket_that_never_established_its_readability_is_not_counted_as_exact()
        {
            // The whole point of the flag. It used to be set to true by default in
            // Bucket(), and BucketPage.ToJson never set it, so the condition was
            // always taken and the answer was always "exact" - for all 68 buckets,
            // with BucketLowerBound having no production caller at all.
            //
            // Absent must therefore mean UNKNOWN, and a consumer must read unknown
            // as a lower bound: only the other reading can produce a false clean.
            JObject undeclared = Ctx().Bucket(Rows(40), "cleanliness", "cad_imported");
            Assert.Null(undeclared["total_is_exact"]);

            // And a section that DID look says so, either way.
            JObject clean = Ctx().BucketLowerBound(Rows(40), "cleanliness", "cad_imported", 0, null);
            Assert.True(clean.Value<bool>("total_is_exact"));

            JObject partial = Ctx().BucketLowerBound(Rows(40), "cleanliness", "cad_imported", 7, "seven threw");
            Assert.False(partial.Value<bool>("total_is_exact"));
        }

        [Fact]
        public void An_unreadable_population_makes_its_total_a_lower_bound()
        {
            // "I found 40" and "there are 40" are different claims.
            JObject b = Ctx().BucketLowerBound(Rows(40), "cleanliness", "cad_imported", 7, "seven threw");
            Assert.False(b.Value<bool>("total_is_exact"));
            Assert.Equal(7, b.Value<int>("unreadable"));
            Assert.Contains("lower bound", b.Value<string>("total_note"));

            JObject clean = Ctx().BucketLowerBound(Rows(40), "cleanliness", "cad_imported", 0, null);
            Assert.True(clean.Value<bool>("total_is_exact"));
        }

        [Fact]
        public void An_empty_bucket_is_not_truncated_and_offers_no_cursor()
        {
            JObject b = Ctx().Bucket(new List<JToken>(), "worksets", "worksets");
            Assert.Equal(0, b.Value<int>("total"));
            Assert.False(b.Value<bool>("truncated"));
            Assert.Null(b["next_cursor"]);
        }

        [Fact]
        public void Rows_with_no_id_and_a_shared_name_still_have_a_total_order()
        {
            // Two views can share a name. A colliding key makes a page boundary
            // ambiguous, so the key carries a hash of the row's content too.
            var twins = new List<JToken>
            {
                new JObject { ["name"] = "Level 1", ["scale"] = 100 },
                new JObject { ["name"] = "Level 1", ["scale"] = 50 },
            };
            Assert.NotEqual(ScanPagingContext.KeyOf(twins[0]), ScanPagingContext.KeyOf(twins[1]));

            JObject page1 = Ctx(@"{ ""naming"": 1 }").Bucket(twins, "naming", "views");
            string cur = page1.Value<string>("next_cursor");
            JObject page2 = Ctx(@"{ ""naming"": 1 }", cur).Bucket(twins, "naming", "views");

            Assert.Equal(1, page1.Value<int>("returned"));
            Assert.Equal(1, page2.Value<int>("returned"));
            Assert.NotEqual(page1["items"][0].Value<int>("scale"), page2["items"][0].Value<int>("scale"));
        }

        [Fact]
        public void The_key_does_not_depend_on_property_order()
        {
            JToken a = JToken.Parse(@"{ ""name"": ""x"", ""scale"": 1 }");
            JToken b = JToken.Parse(@"{ ""scale"": 1, ""name"": ""x"" }");
            Assert.Equal(ScanPagingContext.KeyOf(a), ScanPagingContext.KeyOf(b));
        }

        [Fact]
        public void Numeric_ids_order_numerically_not_as_text()
        {
            // Unpadded, "10" sorts before "9" and the page boundary lands wrong.
            JObject b = Ctx(@"{ ""types"": 100 }").Bucket(Rows(12), "types", "types");
            Assert.Equal(Enumerable.Range(0, 12), b["items"].Select(t => t.Value<int>("id")));
        }

        [Fact]
        public void Every_paged_bucket_is_reported_so_an_ignored_budget_is_visible()
        {
            ScanPagingContext c = Ctx(@"{ ""links"": 2 }");
            c.Bucket(Rows(9), "links", "rvt_links");
            JObject row = (JObject)c.Paged["links.rvt_links"];
            Assert.NotNull(row);
            Assert.Equal(2, row.Value<int>("limit"));
            Assert.Equal(9, row.Value<int>("total"));
            Assert.True(row.Value<bool>("truncated"));
        }

        // ------------------------------------------------- the wiring invariant

        private static string ScanSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName,
                "src", "Horizun.Revit", "Commands", "ModelScanCommand.cs"));
        }

        /// <summary>Method name -> the section key its buckets must be filed under.</summary>
        private static readonly (string Method, string Section)[] Emitters =
        {
            ("CategoriesSection", "categories"),
            ("CleanlinessSection", "cleanliness"),
            ("NamingSection", "naming"),
            ("DocumentationSection", "documentation"),
            ("HealthSection", "health"),
            ("SpatialBucket", "health"),
            ("LinksSection", "links"),
            ("WorksetsSection", "worksets"),
            ("DesignOptionsSection", "design_options"),
            ("TypesSection", "types"),
            ("CoordinatesSection", "coordinates"),
            ("DatumsSection", "datums"),
            ("LevelAssociationSection", "level_association"),
            ("WorksharingSection", "worksharing"),
            ("FamiliesSection", "families"),
            ("ViewsSection", "views"),
            ("SheetsSection", "sheets"),
            ("AnnotationsSection", "annotations"),
            ("ParametersSection", "parameters"),
            ("SpatialSection", "spatial"),
            ("GroupsSection", "groups"),
            ("DesignOptionsCensus", "design_options_census"),
            ("PhasesSection", "phases"),
            ("MepSection", "mep"),
            ("StructureSection", "structure"),
            ("FederationSection", "federation"),
            ("ExternalContentSection", "external_content"),
            ("DocumentaryContextSection", "documentary_context"),
            ("DeliveryReadinessSection", "delivery_readiness"),
        };

        [Fact]
        public void No_emitter_still_uses_the_old_global_limit()
        {
            string src = ScanSource();
            Assert.DoesNotContain("Bucket(items, int top)", src);
            Assert.False(Regex.IsMatch(src, @"[^.]\bBucket\([A-Za-z_][A-Za-z0-9_]*, top\)"),
                "an emitter still calls the old Bucket(x, top)");
        }

        /// <summary>
        /// One paging.Bucket call, found STRUCTURALLY rather than by line.
        ///
        /// The line-based walk this replaces could not see a call wrapped across
        /// two lines: it simply did not match, the call was attributed to no
        /// section, and only a floor assertion on the total noticed. A bucket
        /// nobody attributes is a bucket drawing a budget nobody checks, so the
        /// weakness was in exactly the guard meant to catch that.
        ///
        /// This balances parentheses from the call site, so the argument list may
        /// span any number of lines, and it reads the published key by walking
        /// BACK to the start of the statement rather than expecting it on the same
        /// line.
        /// </summary>
        private sealed class BucketCall
        {
            public int Offset;
            public string Section;
            public string Bucket;
            public string PublishedKey;
        }

        /// <summary>Splits an argument list on top-level commas, respecting strings and nesting.</summary>
        private static List<string> SplitArgs(string args)
        {
            var parts = new List<string>();
            int depth = 0;
            bool inString = false;
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < args.Length) { sb.Append(args[++i]); continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; sb.Append(c); continue; }
                if (c == '(' || c == '[' || c == '{') depth++;
                if (c == ')' || c == ']' || c == '}') depth--;
                if (c == ',' && depth == 0) { parts.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) parts.Add(sb.ToString().Trim());
            return parts;
        }

        private static string StringLiteral(string arg)
        {
            if (arg == null) return null;
            arg = arg.Trim();
            if (arg.Length < 2 || arg[0] != '"' || arg[arg.Length - 1] != '"') return null;
            return arg.Substring(1, arg.Length - 2);
        }

        /// <summary>
        /// Blanks comments while KEEPING every offset, so a paging.Bucket() written
        /// in prose is not mistaken for one written in code. The line-based walk
        /// this replaced never hit that, because it demanded the arguments too; the
        /// structural one reads real calls AND sentences about them, so the
        /// sentences have to go first.
        ///
        /// The quote and backslash are written as character codes on purpose. This
        /// routine is about escaping, and spelling its own delimiters as escapes
        /// makes it the hardest thing in the file to read correctly.
        /// </summary>
        private static string BlankComments(string src)
        {
            const char Quote = (char)34;      // "
            const char Apos = (char)39;       // '
            const char Backslash = (char)92;  // \

            var sb = new StringBuilder(src);
            bool inString = false, inChar = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (inString)
                {
                    if (c == Backslash) { i++; continue; }
                    if (c == Quote) inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == Backslash) { i++; continue; }
                    if (c == Apos) inChar = false;
                    continue;
                }
                if (c == Quote) { inString = true; continue; }
                if (c == Apos) { inChar = true; continue; }

                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != (char)10) { sb[i] = ' '; i++; }
                    continue;
                }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                    {
                        if (src[i] != (char)10) sb[i] = ' ';
                        i++;
                    }
                    if (i + 1 < src.Length) { sb[i] = ' '; sb[i + 1] = ' '; i++; }
                    continue;
                }
            }
            return sb.ToString();
        }

        private static List<BucketCall> BucketCalls(string src)
        {
            src = BlankComments(src);
            var found = new List<BucketCall>();
            int at = 0;
            while ((at = src.IndexOf("paging.Bucket", at, StringComparison.Ordinal)) >= 0)
            {
                int open = src.IndexOf('(', at);
                if (open < 0) break;

                int depth = 0, i = open;
                bool inString = false;
                for (; i < src.Length; i++)
                {
                    char c = src[i];
                    if (inString)
                    {
                        if (c == '\\') { i++; continue; }
                        if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '"') { inString = true; continue; }
                    if (c == '(') depth++;
                    else if (c == ')') { depth--; if (depth == 0) break; }
                }
                if (i >= src.Length) break;

                List<string> args = SplitArgs(src.Substring(open + 1, i - open - 1));
                var call = new BucketCall
                {
                    Offset = at,
                    Section = args.Count > 1 ? StringLiteral(args[1]) : null,
                    Bucket = args.Count > 2 ? StringLiteral(args[2]) : null
                };

                // The published key, read by walking BACK to the start of the
                // statement - it is on the same line today, and need not be.
                int stmt = at;
                while (stmt > 0 && src[stmt - 1] != ';' && src[stmt - 1] != '{' && src[stmt - 1] != '}') stmt--;
                Match key = Regex.Match(src.Substring(stmt, at - stmt), @"\[""([a-z_]+)""\]\s*=\s*$");
                call.PublishedKey = key.Success ? key.Groups[1].Value : null;

                found.Add(call);
                at = i;
            }
            return found;
        }

        [Fact]
        public void Every_emitter_files_its_buckets_under_its_own_section()
        {
            // THE MUTATION THIS CATCHES: swapping one section's name for another's,
            // which would silently draw a different section's budget - whether the
            // call sits on one line or on five.
            string src = ScanSource();

            var starts = new List<(int Offset, string Method, string Section)>();
            foreach ((string method, string section) in Emitters)
            {
                Match m = Regex.Match(src, @"JObject " + method + @"\(");
                Assert.True(m.Success, "emitter not found: " + method);
                starts.Add((m.Index, method, section));
            }
            starts = starts.OrderBy(s => s.Offset).ToList();

            var seenSections = new HashSet<string>(StringComparer.Ordinal);
            var misfiled = new List<string>();
            List<BucketCall> calls = BucketCalls(src);

            foreach (BucketCall c in calls)
            {
                Assert.False(string.IsNullOrEmpty(c.Section),
                    "a paging.Bucket call at offset " + c.Offset + " has no readable section argument");

                (int Offset, string Method, string Section) owner = starts.Last(s => s.Offset <= c.Offset);
                Assert.True(c.Section == owner.Section,
                    owner.Method + " files a bucket under '" + c.Section + "' but belongs to section '" +
                    owner.Section + "' - it would draw another section's budget");
                seenSections.Add(owner.Section);

                // AND THE BUCKET NAME MUST BE THE KEY IT IS PUBLISHED UNDER. Filing
                // a bucket under some other name draws the SECTION's budget instead
                // of that bucket's, and nothing outside the reply would show it.
                if (c.PublishedKey != null && c.Bucket != null && c.PublishedKey != c.Bucket)
                    misfiled.Add("offset " + c.Offset + ": published as '" + c.PublishedKey +
                                 "' but budgeted as '" + c.Bucket + "'");
            }

            Assert.True(misfiled.Count == 0, string.Join("; ", misfiled));
            // A floor, not the count: new buckets are expected, deletions are not.
            Assert.True(calls.Count >= 67,
                "expected every emitter's buckets to be wired; found " + calls.Count);
            foreach ((string method, string section) in Emitters)
                Assert.Contains(section, seenSections);
        }

        [Fact]
        public void The_emitter_guard_reads_a_call_split_across_lines()
        {
            // The weakness this guard used to have, pinned as its own test: a
            // wrapped call was invisible to it, so a bucket could be filed under
            // the wrong section on two lines and pass.
            const string wrapped =
                "private static JObject FakeSection(Document doc, ScanPagingContext paging)\n" +
                "{\n" +
                "    return new JObject\n" +
                "    {\n" +
                "        [\"rows\"] = paging.Bucket(\n" +
                "            rows,\n" +
                "            \"health\",\n" +
                "            \"rows\")\n" +
                "    };\n" +
                "}\n";

            BucketCall only = Assert.Single(BucketCalls(wrapped));
            Assert.Equal("health", only.Section);
            Assert.Equal("rows", only.Bucket);
            Assert.Equal("rows", only.PublishedKey);
        }

        [Fact]
        public void The_sections_with_no_list_take_no_budget_at_all()
        {
            // document, project_info and lines return counts only. Accepting a budget
            // and ignoring it is the whole defect this change is about, so they do
            // not accept one.
            string src = ScanSource();
            Assert.Contains("private static JObject LinesSection(Document doc)", src);
            Assert.Contains("private static JObject DocumentSection(Document doc, UIApplication app)", src);
            Assert.Contains("private static JObject ProjectInfoSection(Document doc)", src);
        }

        [Fact]
        public void The_command_builds_the_budget_and_reports_what_each_bucket_got()
        {
            string src = ScanSource();
            Assert.Contains("SectionBudgets.Parse(request[\"section_limits\"], AllSections, top)", src);
            Assert.Contains("if (!budget.Ok) return CommandResult.Fail(", src);
            Assert.Contains("RawCursor = request.Value<string>(\"cursor\")", src);
            Assert.Contains("[\"paged\"] = paging.Paged", src);
            Assert.Contains("[\"cursor_problems\"] = paging.CursorProblems", src);
        }

        [Fact]
        public void Top_survives_only_as_the_default_for_unnamed_buckets()
        {
            // It cannot contradict section_limits because it never wins against it:
            // it is passed in as the plan's DefaultLimit and nothing else.
            ScanPagingContext c = Ctx(@"{ ""links"": 3 }", top: 25);
            Assert.Equal(3, c.LimitFor("links", "rvt_links"));    // section_limits wins
            Assert.Equal(25, c.LimitFor("naming", "views"));      // top is the fallback
        }
    }
}
