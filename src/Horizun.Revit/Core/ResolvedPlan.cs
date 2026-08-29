// -----------------------------------------------------------------------------
// Horizun Revit MCP - the MATERIALISED PLAN. Original Horizun code.
//
// WHAT THIS FIXES, in the words the old code used about itself:
//
//   "WHAT THIS DOES NOT TELL YOU: this hash is computed from the request, not from
//    the model, so it cannot detect that the model moved underneath you."
//
// That was true and it was the gap. A confirmation token bound the REQUEST - its
// own fields - so a caller approved a QUESTION. Ask "delete everything matching
// this filter", rehearse it against 12 elements, let somebody else save a change,
// and the same question now resolves to 15. The token still matched, because the
// question had not changed. The answer had.
//
// So a dry run now records WHAT IT RESOLVED - the elements themselves and the
// values it read off them - and the apply recomputes that fingerprint and refuses
// with a stale_plan when it differs. The person approves the operation that runs.
//
// WHY A FINGERPRINT AND NOT THE ELEMENTS. A plan over 100k elements cannot be
// held in a token, and a token is not a place to keep a copy of the model. The
// fingerprint is a hash over a canonical, ORDER-INDEPENDENT rendering of each
// element's identity and the facts the plan depends on. Two runs that resolved
// the same elements with the same values hash the same however Revit happened to
// enumerate them; anything else does not.
//
// Everything here is deliberately Revit-free: no `using Autodesk.*`. The facts
// need Revit, the arithmetic does not - the same split as OpenDecision and
// SprinklerTargets - which is why the cases that matter can be proved in unit
// tests rather than hoped for against a model somebody has to build first.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// One element as the plan resolved it: who it is, and the facts the plan leans on.
    /// </summary>
    public sealed class PlannedElement
    {
        /// <summary>
        /// Revit's UniqueId, not the ElementId. An ElementId is a per-document integer that
        /// another element can inherit after a delete; a UniqueId identifies the element
        /// across saves and across the copies of a workshared model, which is what "the same
        /// element I showed you" has to mean.
        /// </summary>
        public string UniqueId;

        /// <summary>Category and type, so a type swap between rehearsal and apply is caught.</summary>
        public string Category;
        public string TypeName;

        /// <summary>Level and host, so an element that was re-hosted is not silently accepted.</summary>
        public string Level;
        public string HostUniqueId;

        /// <summary>
        /// The values this plan depends on, BEFORE the write. A plan that sets a parameter
        /// has to notice that somebody else already set it to something else - otherwise the
        /// apply overwrites a change nobody in this conversation ever saw.
        /// </summary>
        public IDictionary<string, string> BeforeValues;

        /// <summary>
        /// Geometry fingerprint where the plan depends on shape or position - a rounded
        /// bounding box is enough and is cheap. Null when the plan does not care.
        /// </summary>
        public string GeometryFingerprint;

        /// <summary>What the plan intends to do to this element, for the counts below.</summary>
        public PlannedAction Action;
    }

    public enum PlannedAction { Create, Modify, Delete, Read }

    /// <summary>
    /// The plan as a whole. Counts are part of the fingerprint on purpose: "12 deletions"
    /// approved and 15 attempted is the failure this exists to catch, and the counts catch
    /// it even when the extra elements are ones the rehearsal never saw.
    /// </summary>
    public sealed class ResolvedPlan
    {
        public string Command;
        public string DocumentKey;

        /// <summary>Revit's own version, so a plan is never carried across an upgrade.</summary>
        public string RevitVersion;

        /// <summary>
        /// The document's own fingerprint as the gate already computes it. Included so the
        /// plan is bound to the state of the document, not only to its identity.
        /// </summary>
        public string DocumentFingerprint;

        public List<PlannedElement> Elements = new List<PlannedElement>();

        /// <summary>
        /// Effects the plan expects BEYOND the elements it listed - a delete that cascades
        /// into hosted elements, a type change that regenerates dependents. Declared, so
        /// that a cascade nobody predicted shows up as a changed count.
        /// </summary>
        public int ExpectedCascadeCount;

        /// <summary>
        /// State the plan DEPENDS ON without it being one of the elements listed. Some
        /// commands are only correct relative to something ambient: family_apply measures
        /// the shape of the ACTIVE family type and reports the others as not verified, so a
        /// rehearsal taken with one type active approved a check of THAT type - if the active
        /// type changes before the apply, the plan is about a different shape even though
        /// every parameter row is identical.
        ///
        /// A free-form canonical string, hashed as-is. Kept OUT of Elements deliberately:
        /// putting it there would inflate create/modify/delete, and those counts are the
        /// numbers a person reads before saying yes.
        /// </summary>
        public string ContextFingerprint;

        public int CreateCount { get { return Count(PlannedAction.Create); } }
        public int ModifyCount { get { return Count(PlannedAction.Modify); } }
        public int DeleteCount { get { return Count(PlannedAction.Delete); } }

        private int Count(PlannedAction a)
        {
            int n = 0;
            foreach (PlannedElement e in Elements) if (e.Action == a) n++;
            return n;
        }

        /// <summary>
        /// The fingerprint. Order-independent by construction: each element renders to one
        /// canonical line, the lines are sorted, and the sorted set is hashed. Revit is free
        /// to enumerate a collector in whatever order it likes between two calls, and that
        /// is not a change to the plan - but an element appearing, disappearing or changing
        /// any fact the plan read IS.
        /// </summary>
        public string Fingerprint()
        {
            var lines = new List<string>(Elements.Count);
            foreach (PlannedElement e in Elements) lines.Add(Render(e));
            lines.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("cmd=").Append(Command ?? "").Append('\n');
            sb.Append("doc=").Append(DocumentKey ?? "").Append('\n');
            sb.Append("docfp=").Append(DocumentFingerprint ?? "").Append('\n');
            sb.Append("revit=").Append(RevitVersion ?? "").Append('\n');
            // The counts are hashed SEPARATELY from the lines. Two different plans could in
            // principle render the same set of lines with different intents; the counts make
            // that impossible, and they are also the number a human actually approved.
            sb.Append("create=").Append(CreateCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("modify=").Append(ModifyCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("delete=").Append(DeleteCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("cascade=").Append(ExpectedCascadeCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("ctx=").Append(ContextFingerprint ?? "").Append('\n');
            sb.Append("n=").Append(lines.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (string l in lines) sb.Append(l).Append('\n');

            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(h.Length * 2);
                foreach (byte b in h) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        /// <summary>
        /// One element, canonically. Field separators are control characters: Revit type and
        /// parameter names contain quotes, brackets, commas and colons - 'Tee 3" x 1 1/2"' is
        /// a real type name - so a printable separator could be forged by a name that
        /// contained it, and two different plans could hash the same.
        /// </summary>
        private static string Render(PlannedElement e)
        {
            const char F = (char)31;   // unit separator, between fields
            const char R = (char)30;   // record separator, between before-values

            var sb = new StringBuilder();
            sb.Append(e.Action.ToString()).Append(F);
            sb.Append(e.UniqueId ?? "").Append(F);
            sb.Append(e.Category ?? "").Append(F);
            sb.Append(e.TypeName ?? "").Append(F);
            sb.Append(e.Level ?? "").Append(F);
            sb.Append(e.HostUniqueId ?? "").Append(F);
            sb.Append(e.GeometryFingerprint ?? "").Append(F);

            if (e.BeforeValues != null && e.BeforeValues.Count > 0)
            {
                // Sorted, because a dictionary's order is not a fact about the model.
                var keys = new List<string>(e.BeforeValues.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys)
                {
                    string v;
                    if (!e.BeforeValues.TryGetValue(k, out v)) v = null;
                    sb.Append(k).Append('=').Append(v ?? " null").Append(R);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// What changed between two plans, in a sentence a person can act on. A refusal that
        /// says only "the model moved" sends somebody to diff two runs by hand.
        /// </summary>
        public static string DescribeDrift(ResolvedPlan approved, ResolvedPlan now)
        {
            if (approved == null || now == null) return "one of the two plans is missing.";

            var was = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlannedElement e in approved.Elements) if (e.UniqueId != null) was.Add(e.UniqueId);
            var isNow = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlannedElement e in now.Elements) if (e.UniqueId != null) isNow.Add(e.UniqueId);

            int appeared = 0, vanished = 0;
            foreach (string u in isNow) if (!was.Contains(u)) appeared++;
            foreach (string u in was) if (!isNow.Contains(u)) vanished++;

            var parts = new List<string>();
            if (appeared > 0) parts.Add(appeared + " element(s) now match that did not during the dry run");
            if (vanished > 0) parts.Add(vanished + " element(s) that were rehearsed are gone or no longer match");
            if (approved.DeleteCount != now.DeleteCount)
                parts.Add("the deletion count moved from " + approved.DeleteCount + " to " + now.DeleteCount);
            if (approved.ModifyCount != now.ModifyCount)
                parts.Add("the modification count moved from " + approved.ModifyCount + " to " + now.ModifyCount);
            if (approved.CreateCount != now.CreateCount)
                parts.Add("the creation count moved from " + approved.CreateCount + " to " + now.CreateCount);
            if (!string.Equals(approved.DocumentFingerprint, now.DocumentFingerprint, StringComparison.Ordinal))
                parts.Add("the document itself changed");

            if (parts.Count == 0)
            {
                // Same elements, same counts - so a VALUE the plan read is different.
                // That is the quiet one, and "a value changed" is not enough to act on:
                // name the fields, because a moved link, a swapped type and a
                // re-measured length all land here and all have different fixes.
                List<string> fields = ChangedFields(approved, now);
                if (fields.Count == 0)
                    return "the same elements match and the counts are unchanged, so a value this plan " +
                           "depends on was edited after the dry run - somebody else may have already " +
                           "changed what this was about to change.";
                return "the same elements match and the counts are unchanged, but " + fields.Count +
                       " resolved value(s) differ: " + string.Join("; ", fields.ToArray()) + ".";
            }
            return string.Join("; ", parts.ToArray()) + ".";
        }

        /// <summary>How many changed values a drift description will name before it stops.</summary>
        private const int MaxNamedDriftFields = 8;

        /// <summary>
        /// The BeforeValues keys whose values moved between the two plans, rendered so a
        /// reader can act. A key ending in ".link" is decoded rather than printed: the
        /// four fields it packs are the link instance, the linked document, the linked
        /// element and the placement, and LinkedReferenceRules names which of them
        /// moved with the same structured code the discovery surface uses.
        /// </summary>
        private static List<string> ChangedFields(ResolvedPlan approved, ResolvedPlan now)
        {
            var result = new List<string>();
            var current = new Dictionary<string, PlannedElement>(StringComparer.Ordinal);
            foreach (PlannedElement e in now.Elements)
                if (e.UniqueId != null && !current.ContainsKey(e.UniqueId)) current[e.UniqueId] = e;

            foreach (PlannedElement was in approved.Elements)
            {
                PlannedElement isNow;
                if (was.UniqueId == null || !current.TryGetValue(was.UniqueId, out isNow)) continue;
                if (was.BeforeValues == null || isNow.BeforeValues == null) continue;
                foreach (KeyValuePair<string, string> pair in was.BeforeValues)
                {
                    string after;
                    if (!isNow.BeforeValues.TryGetValue(pair.Key, out after)) after = null;
                    if (string.Equals(pair.Value ?? "", after ?? "", StringComparison.Ordinal)) continue;
                    if (result.Count >= MaxNamedDriftFields)
                    {
                        result.Add("(more differences were found and not listed)");
                        return result;
                    }
                    result.Add(DescribeFieldDrift(was.UniqueId, pair.Key, pair.Value, after));
                }
            }
            return result;
        }

        private static string DescribeFieldDrift(string owner, string field, string was, string now)
        {
            if (field != null && field.EndsWith(".link", StringComparison.Ordinal))
            {
                LinkBinding before = ParseLinkBinding(was);
                LinkBinding after = ParseLinkBinding(now);
                string code = LinkedReferenceRules.DetectDrift(before, after);
                if (code != null)
                    return owner + " " + field + ": " + code;
            }
            return owner + " " + field + ": '" + Elide(was) + "' -> '" + Elide(now) + "'";
        }

        /// <summary>
        /// The inverse of how the annotation plan packs a link binding into one value:
        /// instance unique id, document identity, linked element unique id, transform
        /// fingerprint. MEASURED on Revit 2026 (2026-08-26): the document identity is
        /// itself "title|path-hash" - LinkedReferenceRules.DocumentIdentity packs a pipe
        /// of its own, and a linked title may carry more. So the parse anchors on the
        /// ENDS, where the fields are pipe-free by construction (Revit unique ids and
        /// the transform fingerprint), and everything between the first and the last two
        /// separators is the identity. Exactly four segments was the shape this required
        /// before, and every real value has at least five - which is why the live stale
        /// refusal printed two elided hashes instead of link_transform_moved.
        /// A value with too few segments returns null and the caller falls back to
        /// printing both sides, which is still true and still useful.
        /// </summary>
        private static LinkBinding ParseLinkBinding(string packed)
        {
            if (string.IsNullOrEmpty(packed)) return null;
            string[] fields = packed.Split('|');
            if (fields.Length < 4) return null;
            int n = fields.Length;
            return new LinkBinding
            {
                InstanceUniqueId = fields[0],
                DocumentIdentity = string.Join("|", fields, 1, n - 3),
                LinkedElementUniqueId = fields[n - 2],
                TransformFingerprint = fields[n - 1]
            };
        }

        private static string Elide(string value)
        {
            if (value == null) return "(absent)";
            return value.Length <= 48 ? value : value.Substring(0, 48) + "...";
        }
    }
}
