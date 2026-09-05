// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT EVERY BUCKET IN THE SCAN IS ALLOWED TO RETURN, and where it resumes.
//
// SectionBudgetRules decides the arithmetic; this is what the twelve section
// emitters actually hold, so that a budget cannot be accepted at the door and
// then ignored by the code that builds the reply. That gap is the whole reason
// this file exists: `section_limits` was parsed, validated and reported while
// every emitter still called Bucket(items, top) with the one global number.
//
// THE ORDERING KEY. Rows arrive as JObjects of a dozen different shapes, and a
// cursor is only meaningful over a TOTAL order. The key is derived from the row
// itself by a fixed rule:
//
//     id (zero-padded when numeric)  ->  else name  ->  else nothing
//     followed always by a hash of the row's canonical JSON
//
// The hash is not decoration. Two views can share a name, two rows can carry no
// id at all, and a key that collides makes a page boundary ambiguous - the
// second page would either repeat the twin or skip it. Appending the hash makes
// the order total whatever the rows look like, and it is stable across runs
// because it is computed from the content rather than from enumeration order.
//
// WHAT HAPPENS IF THE MODEL CHANGES BETWEEN PAGES. Nothing here can stop
// somebody modelling while an audit runs, and an offset would silently return a
// shifted window with duplicates and holes. Resumption is by key, so the promise
// is narrower and keepable: every surviving row is returned AT MOST ONCE. Rows
// added before the cursor are missed, and the reply says so rather than
// pretending the set was frozen.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ScanPagingContext
    {
        public BudgetPlan Plan;
        public string DocumentFingerprint = "";

        /// <summary>The one cursor the caller supplied, if any.</summary>
        public string RawCursor;

        /// <summary>Every bucket that was paged, and how. Reported beside the sections.</summary>
        public readonly JObject Paged = new JObject();

        /// <summary>Cursor refusals, so a bad cursor is never silently a first page.</summary>
        public readonly JArray CursorProblems = new JArray();

        /// <summary>True once the supplied cursor has been consumed by its bucket.</summary>
        public bool CursorUsed;

        public static ScanPagingContext Default(int top) => new ScanPagingContext
        {
            Plan = new BudgetPlan { Ok = true, DefaultLimit = Math.Max(1, top) },
        };

        public int LimitFor(string section, string bucket) =>
            Plan == null ? 50 : Plan.LimitFor(section, bucket);

        /// <summary>
        /// One bucket, budgeted and resumable.
        ///
        /// The cursor is only offered to the bucket it was minted for. Presenting
        /// it to any other is a refusal, recorded and reported - a cursor read as
        /// "start again" would hand a caller page one while they believed they
        /// were on page nine.
        /// </summary>
        public JObject Bucket(IEnumerable<JToken> items, string section, string bucket)
        {
            List<KeyedRow> rows = (items ?? Enumerable.Empty<JToken>())
                .Where(t => t != null)
                .Select(t => new KeyedRow(KeyOf(t), t))
                .ToList();

            string afterKey = null;
            if (!string.IsNullOrEmpty(RawCursor))
            {
                CursorRead read = SectionCursor.Decode(RawCursor, DocumentFingerprint, section, bucket);
                if (read.Ok && !read.FromStart)
                {
                    afterKey = read.AfterKey;
                    CursorUsed = true;
                }
                else if (!read.Ok &&
                         (read.Code == BudgetCodes.CursorWrongDocument ||
                          read.Code == BudgetCodes.CursorWrongVersion ||
                          read.Code == BudgetCodes.CursorMalformed))
                {
                    // Wrong document, wrong version or corrupt is wrong for EVERY
                    // bucket, so it is reported once rather than twelve times.
                    if (!CursorProblems.Any(p => (string)p["code"] == read.Code))
                        CursorProblems.Add(new JObject
                        {
                            ["code"] = read.Code,
                            ["message"] = read.Message,
                        });
                }
                // wrong_section / wrong_bucket are NOT problems: that is simply a
                // cursor for a different bucket than this one, which is normal.
            }

            int limit = LimitFor(section, bucket);
            BucketPage page = Paging.Page(rows, limit, afterKey, DocumentFingerprint, section, bucket);

            JObject json = page.ToJson();
            json["limit"] = limit;
            json["resumed"] = afterKey != null;
            // NOBODY SAYS EXACT BY DEFAULT, and this line used to.
            //
            // It read `if (json["total_is_exact"] == null) json["total_is_exact"] = true;`
            // and BucketPage.ToJson never sets that key, so the condition was
            // always true and the answer was always "exact" - for all 68 buckets,
            // including the ones whose own prose calls their list an UPPER BOUND.
            // BucketLowerBound, the only thing that would have set it false, had
            // no production caller at all.
            //
            // This method knows the rows it was HANDED. It cannot know whether the
            // section could read the whole population; only the section knows that,
            // and it says so by calling BucketLowerBound. Absent means nobody
            // established it, which is not the same as exact - and a reader who
            // treats it as exact is doing what the consumer used to do.

            Paged[section + "." + bucket] = new JObject
            {
                ["limit"] = limit,
                ["total"] = page.Total,
                ["returned"] = page.Returned,
                ["truncated"] = page.Truncated,
                ["resumed"] = afterKey != null,
            };

            return json;
        }

        /// <summary>
        /// A bucket whose population could not be fully read. Same page, but the
        /// total is declared a LOWER BOUND - "I found 40" and "there are 40" are
        /// different claims and a reader cannot tell them apart otherwise.
        /// </summary>
        public JObject BucketLowerBound(IEnumerable<JToken> items, string section, string bucket,
                                        int unreadable, string limitation)
        {
            JObject o = Bucket(items, section, bucket);
            // EITHER ANSWER IS A DECLARATION. This used to speak only when the news
            // was bad, and silence now means "nobody established it" - so a caller
            // that looked and found nothing unreadable would have been ranked a
            // lower bound alongside the sections that never looked at all.
            o["total_is_exact"] = unreadable <= 0;
            if (unreadable > 0)
            {
                o["unreadable"] = unreadable;
                o["total_note"] = "a lower bound: " + unreadable.ToString(CultureInfo.InvariantCulture) +
                                  " of this population could not be read" +
                                  (string.IsNullOrWhiteSpace(limitation) ? "" : " (" + limitation + ")") + ".";
            }
            return o;
        }

        /// <summary>
        /// The total order. Content-derived, so two runs over an unchanged model
        /// agree and a cursor minted by one is honoured by the other.
        /// </summary>
        public static string KeyOf(JToken row)
        {
            string primary = "";
            if (row is JObject o)
            {
                JToken id = o["id"] ?? o["element_id"] ?? o["ElementId"];
                if (id != null && id.Type != JTokenType.Null)
                {
                    if (id.Type == JTokenType.Integer) primary = id.Value<long>().ToString("D18", CultureInfo.InvariantCulture);
                    else primary = id.ToString();
                }
                else
                {
                    JToken name = o["name"] ?? o["title"] ?? o["kind"];
                    if (name != null && name.Type != JTokenType.Null) primary = name.ToString();
                }
            }
            else primary = row.ToString();

            return primary + "\u001E" + ShortHash(Canonical(row));
        }

        /// <summary>Property order must not change the key, so the JSON is sorted first.</summary>
        private static string Canonical(JToken t)
        {
            if (t is JObject o)
            {
                var sorted = new JObject();
                foreach (JProperty p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    sorted[p.Name] = Canonical(p.Value);
                return sorted.ToString(Formatting.None);
            }
            if (t is JArray a) return new JArray(a.Select(Canonical).Select(JToken.Parse)).ToString(Formatting.None);
            return t == null ? "null" : t.ToString(Formatting.None);
        }

        private static string ShortHash(string s)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")))
                    .Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }
    }
}
