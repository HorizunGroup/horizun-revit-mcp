// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// PER-SECTION BUDGETS AND STABLE CURSORS for the model diagnostics.
//
// The scan has one knob today: `top`, applied to every bucket in every section.
// It is honest - each bucket reports an exact `total`, a `returned` and a
// `truncated` - but it cannot express the thing a caller actually wants, which
// is "give me all forty-one warnings and only five of the ninety thousand
// lines". Raising `top` to see the warnings drags every other bucket up with it
// and the reply stops being consumable; leaving it low hides the warnings. One
// number cannot serve two populations of different sizes.
//
// So: a budget per section, optionally per bucket inside it, and a cursor to
// come back for the rest.
//
// WHAT MAKES A CURSOR SAFE TO HAND OUT. A cursor is a promise that resuming
// returns the rest of the same answer. That promise has four ways to break, and
// each is refused by name rather than papered over:
//
//   * it is replayed against a DIFFERENT DOCUMENT, where the ids mean other
//     things;
//   * it is replayed against a different SECTION or BUCKET, where the ordering
//     is a different ordering;
//   * it was minted by a different CONTRACT VERSION whose encoding differed;
//   * it is corrupt.
//
// A cursor that cannot be validated is refused. It is never treated as "start
// from the beginning", because a caller paging through ninety thousand lines
// would silently receive page one again and have no way to tell.
//
// RESUMPTION IS BY KEY, NOT BY OFFSET. An offset is only correct if the
// population is identical between calls, and nothing here can promise that -
// somebody may be modelling while the audit runs. Every pageable row carries a
// stable ordering key, the page ends by naming the last key it returned, and the
// next page takes what sorts strictly after it. If the population is unchanged
// the two halves reconstruct the whole exactly; if it changed, the caller gets
// each surviving row at most once instead of a shifted window with duplicates
// and holes.
//
// Ordering is ORDINAL on that key. Not culture-aware: a model whose element
// names sort differently under a Turkish locale would paginate differently for
// two people looking at the same file.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Why a budget or a cursor was refused. Closed set.</summary>
    public static class BudgetCodes
    {
        public const string UnknownSection = "unknown_section";
        public const string UnknownBudgetKey = "unknown_budget_key";
        public const string InvalidLimit = "invalid_limit";
        public const string LimitTooLarge = "limit_too_large";
        public const string CursorMalformed = "cursor_malformed";
        public const string CursorWrongDocument = "cursor_wrong_document";
        public const string CursorWrongSection = "cursor_wrong_section";
        public const string CursorWrongBucket = "cursor_wrong_bucket";
        public const string CursorWrongVersion = "cursor_wrong_version";

        public static readonly string[] All =
        {
            UnknownSection, UnknownBudgetKey, InvalidLimit, LimitTooLarge,
            CursorMalformed, CursorWrongDocument, CursorWrongSection,
            CursorWrongBucket, CursorWrongVersion
        };
    }

    /// <summary>The budget for one section: a section-wide limit and optional per-bucket ones.</summary>
    public sealed class SectionBudget
    {
        public int? Limit;
        public readonly Dictionary<string, int> BucketLimits =
            new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>
    /// A parsed budget request, or a named refusal. Never a partial success: a
    /// request with one bad key is refused whole, because silently dropping the
    /// misspelled half of what somebody asked for is how a caller comes to
    /// believe a section was empty.
    /// </summary>
    public sealed class BudgetPlan
    {
        public bool Ok;
        public string Code;
        public string Message;

        /// <summary>Applied where no section says otherwise.</summary>
        public int DefaultLimit;

        public readonly Dictionary<string, SectionBudget> BySection =
            new Dictionary<string, SectionBudget>(StringComparer.Ordinal);

        /// <summary>
        /// The limit for one bucket: its own budget, else its section's, else the
        /// default. A section that names no budget is NOT thereby unlimited.
        /// </summary>
        public int LimitFor(string section, string bucket)
        {
            if (section != null && BySection.TryGetValue(section, out SectionBudget s))
            {
                if (bucket != null && s.BucketLimits.TryGetValue(bucket, out int b)) return b;
                if (s.Limit.HasValue) return s.Limit.Value;
            }
            return DefaultLimit;
        }

        public JObject ToJson()
        {
            var sections = new JObject();
            foreach (KeyValuePair<string, SectionBudget> kv in BySection.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var one = new JObject();
                if (kv.Value.Limit.HasValue) one["limit"] = kv.Value.Limit.Value;
                if (kv.Value.BucketLimits.Count > 0)
                {
                    var buckets = new JObject();
                    foreach (KeyValuePair<string, int> b in kv.Value.BucketLimits.OrderBy(k => k.Key, StringComparer.Ordinal))
                        buckets[b.Key] = b.Value;
                    one["buckets"] = buckets;
                }
                sections[kv.Key] = one;
            }
            return new JObject
            {
                ["default_limit"] = DefaultLimit,
                ["sections"] = sections
            };
        }

        public static BudgetPlan Refused(string code, string message) =>
            new BudgetPlan { Ok = false, Code = code, Message = message };
    }

    public static class SectionBudgets
    {
        /// <summary>
        /// A defensive ceiling. Not a judgement about what a caller needs - a reply
        /// large enough to exhaust the transport is a failure with no useful error,
        /// so the refusal happens here where it can name the number.
        /// </summary>
        public const int MaxLimit = 5000;

        /// <summary>The keys a per-section budget object may carry.</summary>
        private static readonly HashSet<string> SectionKeys =
            new HashSet<string>(StringComparer.Ordinal) { "limit", "buckets" };

        /// <summary>
        /// Parse `section_limits` against the sections that actually exist.
        ///
        /// Unknown section names are REFUSED and the refusal lists what is
        /// available. Silently ignoring `warning` when the section is `warnings`
        /// would hand back a default-sized bucket that looks exactly like a
        /// section with nothing in it.
        /// </summary>
        public static BudgetPlan Parse(JToken sectionLimits, IReadOnlyCollection<string> knownSections, int defaultLimit)
        {
            var plan = new BudgetPlan { Ok = true, DefaultLimit = defaultLimit };

            string bad = ValidateLimit(defaultLimit, "top");
            if (bad != null)
                return BudgetPlan.Refused(defaultLimit > MaxLimit ? BudgetCodes.LimitTooLarge : BudgetCodes.InvalidLimit, bad);

            if (sectionLimits == null || sectionLimits.Type == JTokenType.Null) return plan;

            if (sectionLimits.Type != JTokenType.Object)
                return BudgetPlan.Refused(BudgetCodes.UnknownBudgetKey,
                    "section_limits must be an object mapping a section name to its budget, and this is a " +
                    sectionLimits.Type.ToString().ToLowerInvariant() + ".");

            var known = new HashSet<string>(knownSections ?? new string[0], StringComparer.Ordinal);

            foreach (JProperty prop in ((JObject)sectionLimits).Properties())
            {
                if (!known.Contains(prop.Name))
                    return BudgetPlan.Refused(BudgetCodes.UnknownSection,
                        "there is no section called '" + prop.Name + "'. The sections are: " +
                        string.Join(", ", known.OrderBy(k => k, StringComparer.Ordinal)) +
                        ". Nothing was budgeted, because a budget silently dropped reads as an empty section.");

                var budget = new SectionBudget();

                if (prop.Value.Type == JTokenType.Integer)
                {
                    // The short form: "categories": 10
                    int n = prop.Value.Value<int>();
                    string why = ValidateLimit(n, prop.Name);
                    if (why != null)
                        return BudgetPlan.Refused(n > MaxLimit ? BudgetCodes.LimitTooLarge : BudgetCodes.InvalidLimit, why);
                    budget.Limit = n;
                }
                else if (prop.Value.Type == JTokenType.Object)
                {
                    var obj = (JObject)prop.Value;
                    foreach (JProperty inner in obj.Properties())
                        if (!SectionKeys.Contains(inner.Name))
                            return BudgetPlan.Refused(BudgetCodes.UnknownBudgetKey,
                                "'" + prop.Name + "." + inner.Name + "' is not a budget key. A section budget takes " +
                                "'limit' and 'buckets'.");

                    JToken limit = obj["limit"];
                    if (limit != null && limit.Type != JTokenType.Null)
                    {
                        if (limit.Type != JTokenType.Integer)
                            return BudgetPlan.Refused(BudgetCodes.InvalidLimit,
                                "'" + prop.Name + ".limit' must be a whole number.");
                        int n = limit.Value<int>();
                        string why = ValidateLimit(n, prop.Name + ".limit");
                        if (why != null)
                            return BudgetPlan.Refused(n > MaxLimit ? BudgetCodes.LimitTooLarge : BudgetCodes.InvalidLimit, why);
                        budget.Limit = n;
                    }

                    JToken buckets = obj["buckets"];
                    if (buckets != null && buckets.Type != JTokenType.Null)
                    {
                        if (buckets.Type != JTokenType.Object)
                            return BudgetPlan.Refused(BudgetCodes.UnknownBudgetKey,
                                "'" + prop.Name + ".buckets' must be an object mapping a bucket name to a limit.");
                        foreach (JProperty b in ((JObject)buckets).Properties())
                        {
                            if (b.Value.Type != JTokenType.Integer)
                                return BudgetPlan.Refused(BudgetCodes.InvalidLimit,
                                    "'" + prop.Name + ".buckets." + b.Name + "' must be a whole number.");
                            int n = b.Value.Value<int>();
                            string why = ValidateLimit(n, prop.Name + ".buckets." + b.Name);
                            if (why != null)
                                return BudgetPlan.Refused(n > MaxLimit ? BudgetCodes.LimitTooLarge : BudgetCodes.InvalidLimit, why);
                            budget.BucketLimits[b.Name] = n;
                        }
                    }
                }
                else
                {
                    return BudgetPlan.Refused(BudgetCodes.InvalidLimit,
                        "'" + prop.Name + "' must be a whole number or a budget object.");
                }

                plan.BySection[prop.Name] = budget;
            }

            return plan;
        }

        private static string ValidateLimit(int n, string where)
        {
            if (n < 1)
                return "'" + where + "' is " + n.ToString(CultureInfo.InvariantCulture) +
                       ". A limit below 1 cannot be honoured: zero rows and a truncated flag would be " +
                       "indistinguishable from a population that is genuinely empty.";
            if (n > MaxLimit)
                return "'" + where + "' is " + n.ToString(CultureInfo.InvariantCulture) + " and the ceiling is " +
                       MaxLimit.ToString(CultureInfo.InvariantCulture) + ". Page through it with the cursor instead.";
            return null;
        }
    }

    /// <summary>What a cursor decoded to, or why it was refused.</summary>
    public sealed class CursorRead
    {
        public bool Ok;
        public string Code;
        public string Message;

        /// <summary>Rows sorting at or before this key were already returned.</summary>
        public string AfterKey;

        /// <summary>True when no cursor was supplied at all - the first page.</summary>
        public bool FromStart;

        public static CursorRead Start() => new CursorRead { Ok = true, FromStart = true, AfterKey = null };
        public static CursorRead Refused(string code, string message) =>
            new CursorRead { Ok = false, Code = code, Message = message };
    }

    /// <summary>
    /// The cursor itself. Opaque to the caller by design - it is not a promise
    /// about the encoding - but every field it carries is checked when it comes
    /// back, so replaying one somewhere it does not belong is an error rather
    /// than a wrong answer.
    /// </summary>
    public static class SectionCursor
    {
        public const string Version = "hzc1";
        private const char Sep = '\u001F';   // unit separator: cannot occur in the fields

        public static string Encode(string documentFingerprint, string section, string bucket, string lastKey)
        {
            if (lastKey == null) return null;
            string raw = string.Join(Sep.ToString(),
                Version, documentFingerprint ?? "", section ?? "", bucket ?? "", lastKey);
            return Base64Url(Encoding.UTF8.GetBytes(raw));
        }

        public static CursorRead Decode(string cursor, string documentFingerprint, string section, string bucket)
        {
            if (string.IsNullOrEmpty(cursor)) return CursorRead.Start();

            string raw;
            try { raw = Encoding.UTF8.GetString(FromBase64Url(cursor)); }
            catch
            {
                return CursorRead.Refused(BudgetCodes.CursorMalformed,
                    "this cursor could not be decoded. It is refused rather than treated as the start of the " +
                    "list: a caller paging through a large section would silently receive page one again.");
            }

            string[] parts = raw.Split(Sep);
            if (parts.Length != 5)
                return CursorRead.Refused(BudgetCodes.CursorMalformed,
                    "this cursor does not have the shape this version writes.");

            if (!string.Equals(parts[0], Version, StringComparison.Ordinal))
                return CursorRead.Refused(BudgetCodes.CursorWrongVersion,
                    "this cursor was minted by contract version '" + parts[0] + "' and this is '" + Version +
                    "'. Ordering may differ between versions, so resuming across one would skip or repeat rows.");

            if (!string.Equals(parts[1], documentFingerprint ?? "", StringComparison.Ordinal))
                return CursorRead.Refused(BudgetCodes.CursorWrongDocument,
                    "this cursor was minted against a different document. Element ids mean different things in " +
                    "different models, so resuming here would page through the wrong list.");

            if (!string.Equals(parts[2], section ?? "", StringComparison.Ordinal))
                return CursorRead.Refused(BudgetCodes.CursorWrongSection,
                    "this cursor belongs to section '" + parts[2] + "' and was presented to '" + section + "'.");

            if (!string.Equals(parts[3], bucket ?? "", StringComparison.Ordinal))
                return CursorRead.Refused(BudgetCodes.CursorWrongBucket,
                    "this cursor belongs to bucket '" + parts[3] + "' and was presented to '" + bucket + "'.");

            return new CursorRead { Ok = true, FromStart = false, AfterKey = parts[4] };
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string s)
        {
            string t = s.Replace('-', '+').Replace('_', '/');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
                case 1: throw new FormatException("not base64url");
            }
            return Convert.FromBase64String(t);
        }
    }

    /// <summary>One row, with the key that fixes its place in the order.</summary>
    public sealed class KeyedRow
    {
        public readonly string Key;
        public readonly JToken Value;
        public KeyedRow(string key, JToken value) { Key = key; Value = value; }
    }

    /// <summary>One page of one bucket.</summary>
    public sealed class BucketPage
    {
        public List<JToken> Items = new List<JToken>();
        public int Total;
        public int Returned;
        public bool Truncated;
        public string NextCursor;
        public string LastKey;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["total"] = Total,
                ["returned"] = Returned,
                ["truncated"] = Truncated,
                ["items"] = new JArray(Items)
            };
            // Only when there IS more. A cursor on a complete page invites a
            // second call that returns nothing and looks like the end of a list
            // the caller has already seen the end of.
            if (Truncated && NextCursor != null) o["next_cursor"] = NextCursor;
            return o;
        }
    }

    public static class Paging
    {
        /// <summary>
        /// One page, ordered and resumable.
        ///
        /// Rows are sorted ORDINALLY by key - not by culture, which would make two
        /// people with different locales page the same model differently - and the
        /// page begins strictly after the key the previous page ended on.
        ///
        /// `Total` is the size of the WHOLE bucket, not of what survived the
        /// cursor. A caller resuming at row 900 of 1000 needs to be told there are
        /// a thousand, or the second page reads like a hundred-row bucket.
        /// </summary>
        public static BucketPage Page(IEnumerable<KeyedRow> rows, int limit, string afterKey,
                                      string documentFingerprint, string section, string bucket)
        {
            List<KeyedRow> all = (rows ?? Enumerable.Empty<KeyedRow>())
                .Where(r => r != null)
                .OrderBy(r => r.Key, StringComparer.Ordinal)
                .ToList();

            var page = new BucketPage { Total = all.Count };

            IEnumerable<KeyedRow> remaining = afterKey == null
                ? all
                : all.Where(r => string.CompareOrdinal(r.Key, afterKey) > 0);

            List<KeyedRow> taken = remaining.Take(Math.Max(1, limit)).ToList();

            page.Items = taken.Select(r => r.Value).ToList();
            page.Returned = taken.Count;
            page.LastKey = taken.Count > 0 ? taken[taken.Count - 1].Key : afterKey;

            int consumed = afterKey == null
                ? taken.Count
                : all.Count(r => string.CompareOrdinal(r.Key, afterKey) <= 0) + taken.Count;
            page.Truncated = consumed < all.Count;

            if (page.Truncated && page.LastKey != null)
                page.NextCursor = SectionCursor.Encode(documentFingerprint, section, bucket, page.LastKey);

            return page;
        }
    }
}
