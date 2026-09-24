// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_manage_groups AND horizun_manage_worksets.
//
// Revit has no API for "edit group". Redefining a group type's membership is done
// the way the UI's own history shows it: the reference instance is ungrouped, the
// member set is changed, a NEW group type is made from it, and - only when the
// caller said so - every other instance is swapped onto the new type, the old type
// is deleted and the new one takes its name.
//
// Two decisions in that sequence cannot be left to the command's mood, and both
// live here where a test can pin them:
//
//   * WHICH instances change. With other instances of the same type in the model,
//     "add this wall to the group" reads two ways - this instance only, or the
//     type (every instance). Neither is a default: the call names its scope or
//     it is refused before anything is written.
//
//   * WHERE a swapped instance's content lands. A new type's origin is wherever
//     Revit puts it for the new member set, so a swapped instance can come back
//     displaced. The displacement is MEASURED - from a member whose (category,
//     type) is unique before and after - never computed from rotations the API
//     does not publish for groups (a mirrored instance has none), and the result
//     is then checked member by member against what the instance held before.
//
// Worksets: the classification of an element that was asked to move. A borrowed
// element is reported and left where it is - the bridge does not take another
// user's element - and one whose workset parameter is read-only (a group member,
// a hosted sub-element) is named instead of being counted as moved.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A member's identity for matching: category, type and axis-aligned box, in feet.</summary>
    public sealed class MemberSignature
    {
        public long Category;
        public long Type;
        public double[] Min = new double[3];
        public double[] Max = new double[3];
        /// <summary>False when Revit publishes no bounding box: compared by category and type only, never measured from.</summary>
        public bool HasBox = true;

        public MemberSignature() { }

        public MemberSignature(long category, long type, double[] min, double[] max)
        {
            Category = category; Type = type;
            Min = min ?? new double[3]; Max = max ?? new double[3];
        }

        public string Key => Category + ":" + Type;

        public double[] Center => new[] { (Min[0] + Max[0]) / 2, (Min[1] + Max[1]) / 2, (Min[2] + Max[2]) / 2 };

        public bool SameAs(MemberSignature other, double tolerance)
        {
            if (other == null || other.Category != Category || other.Type != Type || other.HasBox != HasBox) return false;
            if (!HasBox) return true;
            for (int i = 0; i < 3; i++)
                if (Math.Abs(Min[i] - other.Min[i]) > tolerance || Math.Abs(Max[i] - other.Max[i]) > tolerance) return false;
            return true;
        }
    }

    public sealed class RetainedMatch
    {
        public bool Held;
        public int Before, After, Expected, Unmatched;
        public string Why;
    }

    public static class GroupRedefinitionRules
    {
        public const string ScopeAll = "all_instances";
        public const string ScopeThis = "this_instance";

        /// <summary>Tolerance, in feet, for a member's box after a swap: about 0.3 mm.</summary>
        public const double BoxTolerance = 0.001;

        /// <summary>
        /// The scope a membership change runs with, or null and the reason. A type with
        /// no other instance has one reading; a type with others has two, and the caller
        /// must pick.
        /// </summary>
        public static string ResolveScope(string requested, int otherInstances, out string error)
        {
            error = null;
            string s = (requested ?? "").Trim().ToLowerInvariant();
            if (s.Length > 0 && s != ScopeAll && s != ScopeThis)
            { error = "scope must be " + ScopeAll + " or " + ScopeThis + "."; return null; }
            if (otherInstances <= 0) return s.Length == 0 ? ScopeAll : s;
            if (s.Length == 0)
            {
                error = "the group type has " + otherInstances + " other instance(s), so this change reads two ways: " +
                        "scope=" + ScopeAll + " redefines the TYPE (every instance changes and the other instances' " +
                        "member ids are replaced; members removed from them are deleted, not left loose), or scope=" +
                        ScopeThis + " moves only this instance to a NEW type named by 'name' and leaves the others " +
                        "untouched. Neither is assumed.";
                return null;
            }
            return s;
        }

        /// <summary>
        /// The translation a swapped instance's content came back with, measured from a
        /// (category, type) that occurs exactly once before and once after. Null when no
        /// such member exists - the caller must then refuse rather than guess.
        /// </summary>
        public static double[] MeasureShift(IList<MemberSignature> before, IList<MemberSignature> after, out string why)
        {
            why = null;
            before = before ?? new MemberSignature[0]; after = after ?? new MemberSignature[0];
            var b = before.GroupBy(s => s.Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            var a = after.GroupBy(s => s.Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            foreach (MemberSignature one in before)
            {
                MemberSignature was, now;
                if (!b.TryGetValue(one.Key, out was) || !a.TryGetValue(one.Key, out now)) continue;
                if (!was.HasBox || !now.HasBox) continue;
                double[] c0 = was.Center, c1 = now.Center;
                return new[] { c1[0] - c0[0], c1[1] - c0[1], c1[2] - c0[2] };
            }
            why = "no member has a (category, type) that occurs exactly once in the instance before and after the " +
                  "swap, so the displacement of its content cannot be measured without guessing.";
            return null;
        }

        /// <summary>
        /// Did the swapped instance keep what it had? For an addition every member it held
        /// must still be there, in place; for a removal every member it holds now must be
        /// one it held. Counts are checked too: the change is `changed` members.
        /// </summary>
        public static RetainedMatch Match(IList<MemberSignature> before, IList<MemberSignature> after,
                                          bool addition, int changed, double tolerance)
        {
            before = before ?? new MemberSignature[0]; after = after ?? new MemberSignature[0];
            var r = new RetainedMatch { Before = before.Count, After = after.Count };
            r.Expected = addition ? before.Count + changed : before.Count - changed;
            IList<MemberSignature> must = addition ? before : after;
            IList<MemberSignature> pool = addition ? after : before;
            var used = new bool[pool.Count];
            foreach (MemberSignature s in must)
            {
                int hit = -1;
                for (int i = 0; i < pool.Count; i++)
                    if (!used[i] && s.SameAs(pool[i], tolerance)) { hit = i; break; }
                if (hit < 0) r.Unmatched++; else used[hit] = true;
            }
            if (r.After != r.Expected)
                r.Why = "the instance holds " + r.After + " member(s); " + r.Expected + " were expected.";
            else if (r.Unmatched > 0)
                r.Why = r.Unmatched + " member(s) did not come back where they were.";
            else if (must.Count == 0)
                r.Why = "nothing was compared.";
            r.Held = r.Why == null;
            return r;
        }
    }

    /// <summary>One former member as re-read after the ungroup. GroupIdAfter is -1 for no group.</summary>
    public sealed class UngroupMemberState
    {
        public long Id;
        public bool Exists;
        public long GroupIdAfter = -1;
    }

    /// <summary>
    /// The postcondition of operation=ungroup, Revit-free. Ungrouping DELETES the instance
    /// by design, so nothing may be read through it afterwards: the members are the ids
    /// Revit's UngroupMembers returned (the members read before are the fallback when it
    /// returned none), each must still exist and belong to no group - or to the parent
    /// group when the instance was nested - and the group TYPE must survive with its
    /// other instances untouched. A type with no other instance may be kept or purged:
    /// both are Revit's business, neither is the ungroup's failure.
    /// </summary>
    public static class UngroupRules
    {
        public const long NoGroup = -1;

        public static List<long> MembersToCheck(IEnumerable<long> released, IEnumerable<long> readBefore)
        {
            var r = (released ?? Enumerable.Empty<long>()).Distinct().ToList();
            return r.Count > 0 ? r : (readBefore ?? Enumerable.Empty<long>()).Distinct().ToList();
        }

        /// <summary>The ids that did NOT come out loose (missing, or still in some group other than the parent).</summary>
        public static List<long> NotReleased(IEnumerable<UngroupMemberState> members, long parentGroupId)
        {
            long want = parentGroupId < 0 ? NoGroup : parentGroupId;
            return (members ?? Enumerable.Empty<UngroupMemberState>())
                .Where(m => m == null || !m.Exists || (m.GroupIdAfter < 0 ? NoGroup : m.GroupIdAfter) != want)
                .Select(m => m == null ? NoGroup : m.Id).ToList();
        }

        public static bool MembersReleased(IList<UngroupMemberState> members, long parentGroupId)
            => members != null && members.Count > 0 && NotReleased(members, parentGroupId).Count == 0;

        /// <summary>What the type must look like after ungrouping `ungrouped` of its `instancesBefore` instances.</summary>
        public static bool TypeHeld(int instancesBefore, int ungrouped, bool typeExistsAfter, int instancesAfter, out string expectation)
        {
            int others = instancesBefore - ungrouped;
            if (others > 0)
            {
                expectation = "kept with " + others + " instance(s)";
                return typeExistsAfter && instancesAfter == others;
            }
            expectation = "no instance left (kept or purged, either is Revit's)";
            return !typeExistsAfter || instancesAfter == 0;
        }
    }

    public static class WorksetEditRules
    {
        public const string Movable = "movable";
        public const string Borrowed = "borrowed_by_other";
        public const string ReadOnlyWorkset = "workset_not_editable";
        public const string AlreadyThere = "already_in_target";
        public const string Missing = "not_found";

        private static readonly char[] Forbidden = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>Why a workset or group-type name cannot be used, or null.</summary>
        public static string NameProblem(string name, IEnumerable<string> existing)
        {
            if (string.IsNullOrWhiteSpace(name)) return "the name is empty.";
            if (name.Trim() != name) return "the name starts or ends with blanks.";
            if (name.IndexOfAny(Forbidden) >= 0) return "the name holds a character Revit refuses in names (\\ : { } [ ] | ; < > ? ` ~).";
            if (existing != null && existing.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                return "the name '" + name + "' is already used.";
            return null;
        }

        /// <summary>
        /// One element's verdict for move_elements. A borrowed element is NEVER forced:
        /// it is reported with its owner and left where it is.
        /// </summary>
        public static string Classify(bool exists, bool ownedByOther, bool worksetParameterEditable, bool alreadyInTarget)
        {
            if (!exists) return Missing;
            if (alreadyInTarget) return AlreadyThere;
            if (ownedByOther) return Borrowed;
            if (!worksetParameterEditable) return ReadOnlyWorkset;
            return Movable;
        }

        public static readonly string[] Visibilities = { "visible", "hidden", "use_global" };
    }
}
