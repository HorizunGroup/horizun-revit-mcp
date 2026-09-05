// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH FINDING IS THIS, and WHICH AUDIT RUN did it come from.
//
// A correction has to cite the finding it corrects, and an apply has to prove
// that the finding it was approved against is the finding still in the model.
// Neither works on a check name alone: "unpinned_links" names the same check
// on every model on every day, and a caller who approved four links on Monday
// must not spend that approval on the nine links Tuesday's audit found.
//
// So a finding carries a FINDING ID - a hash over the check, the elements it
// named and the `top` it was listed under - and an audit reply carries a
// FINDING SET FINGERPRINT that folds the document's own fingerprint, the same
// `top`, and every finding id in the run. Two audits of an unchanged model at
// the same `top` reproduce both. An audit at another `top` does NOT, and the
// refusal downstream says so by name, because a legitimate refusal that reads
// like a bug is a refusal people learn to work around.
//
// THE DOCUMENT HALF IS THE SNAPSHOT'S. DocumentGate.IdentityOf(doc).
// FingerprintDigest() is what the diagnostics snapshot keys its file on, and it
// is what this folds in - one notion of "which model was this", not a second one
// invented for corrections that could disagree with the first.
//
// WHY ONLY ID-SHAPED FIELDS ARE HASHED. A finding's items carry prose beside
// their ids: a warning's localized description, a summary sentence, a triage
// label from a caller-supplied profile. Hashing all of it would change every
// finding id when a session's language changed or a profile was passed, and
// "the model moved" would be reported about a model that did not. The identity
// is what a correction acts on: the ids, and the few typed codes findings use
// where they have no id to give.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class FindingIdentity
    {
        public const string FindingPrefix = "f:";
        public const string SetPrefix = "fs:";

        public const string TopMeans =
            "finding ids hash over the items a finding LISTED, and the list is cut at 'top' - so two audits at " +
            "different top values name the same defect with different ids, and neither can cite the other. The " +
            "finding set fingerprint folds top in for the same reason. Re-run the audit at the top the " +
            "correction was rehearsed against.";

        /// <summary>
        /// The keys an item may carry that name an element: `id`, anything ending
        /// in `_id` or `_ids`. Plus the three typed codes findings use where they
        /// have nothing to identify by id - a readiness role, a datum collision
        /// code, an in-place family name - and the two typed statuses a recipe
        /// filters on. NEVER a description, a summary or a triage label.
        /// </summary>
        public static bool IsIdentityKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key == "id" || key == "ids") return true;
            if (key.EndsWith("_id", StringComparison.Ordinal) || key.EndsWith("_ids", StringComparison.Ordinal))
                return true;
            return key == "role" || key == "code" || key == "family" || key == "status" || key == "problem_code";
        }

        /// <summary>
        /// One item, canonically: its identity keys sorted, values rendered compact.
        /// Control characters separate the fields so a name containing '=' or ','
        /// cannot forge a boundary.
        /// </summary>
        public static string CanonicalItem(JObject item)
        {
            if (item == null) return "";
            var keys = item.Properties().Select(p => p.Name).Where(IsIdentityKey).ToList();
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string k in keys)
            {
                JToken v = item[k];
                sb.Append(k).Append('=');
                sb.Append(v == null || v.Type == JTokenType.Null ? "null" : v.ToString(Newtonsoft.Json.Formatting.None));
                sb.Append((char)30);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The finding id. Order-independent over the items: Revit may enumerate a
        /// collector differently between two calls, and that is not a change to the
        /// finding. A different element in the list is.
        /// </summary>
        public static string IdOf(string check, JArray items, int top, long total)
        {
            var lines = new List<string>();
            foreach (JToken t in items ?? new JArray())
                lines.Add(CanonicalItem(t as JObject));
            lines.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("check=").Append(check ?? "").Append((char)31);
            sb.Append("top=").Append(top.ToString(CultureInfo.InvariantCulture)).Append((char)31);
            sb.Append("total=").Append(total.ToString(CultureInfo.InvariantCulture)).Append((char)31);
            sb.Append("n=").Append(lines.Count.ToString(CultureInfo.InvariantCulture)).Append((char)31);
            foreach (string l in lines) sb.Append(l).Append((char)31);
            return FindingPrefix + Hash(sb.ToString(), 8);
        }

        /// <summary>
        /// The whole run: the document, the top, and every finding id, sorted so the
        /// order the checks ran in does not matter.
        /// </summary>
        public static string SetFingerprint(string documentFingerprint, int top, IEnumerable<string> findingIds)
        {
            var ids = new List<string>(findingIds ?? Enumerable.Empty<string>());
            ids.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append("doc=").Append(documentFingerprint ?? "").Append((char)31);
            sb.Append("top=").Append(top.ToString(CultureInfo.InvariantCulture)).Append((char)31);
            sb.Append("n=").Append(ids.Count.ToString(CultureInfo.InvariantCulture)).Append((char)31);
            foreach (string id in ids) sb.Append(id).Append((char)31);
            return SetPrefix + Hash(sb.ToString(), 8);
        }

        /// <summary>
        /// The element ids a finding's items name, in item order. Reads the three
        /// keys the audit's findings use for the element a correction would act on:
        /// `element_id`, `id`, `group_type_id`. Numeric strings count - the audit
        /// renders ElementId.ToString() into several of them. Anything else is not
        /// an element this surface will touch.
        /// </summary>
        public static List<long> ElementIdsOf(JArray items)
        {
            var ids = new List<long>();
            foreach (JToken t in items ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) continue;
                long id;
                if (TryElementId(o, out id)) ids.Add(id);
            }
            return ids;
        }

        public static bool TryElementId(JObject item, out long id)
        {
            id = 0;
            if (item == null) return false;
            foreach (string key in new[] { "element_id", "id", "group_type_id" })
            {
                JToken v = item[key];
                if (v == null || v.Type == JTokenType.Null) continue;
                if (v.Type == JTokenType.Integer) { id = v.Value<long>(); return true; }
                if (v.Type == JTokenType.String &&
                    long.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The items whose typed code is one of the values a recipe accepts. A recipe
        /// that only deletes UNPLACED rooms filters on `problem_code`, never on the
        /// sentence beside it: a sentence is how a report becomes a command.
        /// </summary>
        public static JArray ItemsWhere(JArray items, string field, IEnumerable<string> values)
        {
            if (string.IsNullOrEmpty(field) || values == null) return items ?? new JArray();
            var accepted = new HashSet<string>(values, StringComparer.Ordinal);
            var kept = new JArray();
            foreach (JToken t in items ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) continue;
                JToken v = o[field];
                if (v != null && v.Type == JTokenType.String && accepted.Contains((string)v)) kept.Add(o);
            }
            return kept;
        }

        private static string Hash(string text, int bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(h, 0, bytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
