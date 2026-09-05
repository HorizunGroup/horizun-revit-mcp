// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// GROUPS AND DESIGN OPTIONS - two areas that share one confusion each.
//
// GROUPS: a group TYPE with no instances is not an "empty group". An empty
// group is a group whose MEMBERS are none; an unplaced type is a definition
// carrying its full geometry in the file that nothing draws. They are different
// problems with different fixes - purge the second, investigate the first - and
// a census that prints one number called "empty groups" cannot tell a reader
// which they have.
//
// DESIGN OPTIONS: a document with NO option sets has not passed a design-option
// check. It has no design options, which is a different statement, and
// reporting it as a pass tells a team their options are tidy in a file that
// never had any.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class GroupTypeFact
    {
        public long ElementId;
        public string Name;
        public bool NameReadable = true;
        /// <summary>How many instances of this type are placed. Zero is a real answer.</summary>
        public int InstanceCount;
        /// <summary>How many members the DEFINITION holds. Null when it could not be read.</summary>
        public int? MemberCount;
        /// <summary>Categories the members belong to, largest first when reported.</summary>
        public Dictionary<string, long> MemberCategories = new Dictionary<string, long>(StringComparer.Ordinal);
        public bool MembersReadable = true;

        /// <summary>Placed nowhere. NOT the same as holding no members.</summary>
        public bool Unplaced { get { return InstanceCount == 0; } }

        /// <summary>Holds nothing. Only knowable when the members could be read.</summary>
        public bool? Empty { get { return MemberCount.HasValue ? (bool?)(MemberCount.Value == 0) : null; } }
    }

    public sealed class GroupInstanceFact
    {
        public long ElementId;
        public long TypeId;
        public string TypeName;
        public string LevelName;
        public string WorksetName;
        /// <summary>True when this instance sits inside another group.</summary>
        public bool? IsNested;
        public long? ParentGroupId;
        /// <summary>Members excluded in this instance, where the model reports them.</summary>
        public int? ExcludedMemberCount;
        public bool Readable = true;
    }

    public sealed class DesignOptionFact
    {
        public long ElementId;
        public string Name;
        public string SetName;
        public bool? IsPrimary;
        public long ElementCount;
        public bool Readable = true;
    }

    public static class GroupOptionRules
    {
        public const string GroupsMean =
            "a group TYPE with no instances is NOT an empty group. Unplaced means nothing draws it while it " +
            "carries its full geometry in the file - a purge candidate. Empty means the definition holds no " +
            "members - a different problem with a different fix. The two are reported as separate fields and " +
            "an unreadable member list leaves 'empty' null rather than guessing either way.";

        public const string OptionsMean =
            "a document with no option sets has not PASSED a design-option check; it has no design options. " +
            "Reporting that as clean tells a team their options are tidy in a file that never had any. The " +
            "section answers not_applicable instead.";

        /// <summary>
        /// The design-option answer for a document that has none. Not a pass, and
        /// not a count of zero problems.
        /// </summary>
        public static JObject NoDesignOptions()
        {
            return new JObject
            {
                ["status"] = "not_applicable",
                ["reason"] = "this document defines no design option sets, so there is nothing to report about " +
                             "design options.",
                ["means"] = OptionsMean
            };
        }

        public static JObject GroupTotals(IEnumerable<GroupTypeFact> types, IEnumerable<GroupInstanceFact> instances)
        {
            List<GroupTypeFact> t = (types ?? Enumerable.Empty<GroupTypeFact>()).Where(x => x != null).ToList();
            List<GroupInstanceFact> i =
                (instances ?? Enumerable.Empty<GroupInstanceFact>()).Where(x => x != null).ToList();

            return new JObject
            {
                ["group_types"] = t.Count,
                ["group_instances"] = i.Count,
                // THE TWO NUMBERS THE WHOLE FILE EXISTS TO KEEP APART.
                ["types_with_no_instances"] = t.Count(x => x.Unplaced),
                ["types_with_no_members"] = t.Count(x => x.Empty == true),
                ["types_whose_members_are_unreadable"] = t.Count(x => x.Empty == null),
                ["nested_instances"] = i.Count(x => x.IsNested == true),
                ["nesting_unreadable"] = i.Count(x => x.IsNested == null),
                ["instances_unreadable"] = i.Count(x => !x.Readable),
                ["means"] = GroupsMean
            };
        }

        public static JObject ToJson(GroupTypeFact f)
        {
            if (f == null) return null;
            var cats = new JArray();
            foreach (KeyValuePair<string, long> kv in Ranked(f.MemberCategories))
                cats.Add(new JObject { ["category"] = kv.Key, ["members"] = kv.Value });

            return new JObject
            {
                ["group_type_id"] = f.ElementId,
                ["name"] = f.Name,
                ["name_readable"] = f.NameReadable,
                ["instance_count"] = f.InstanceCount,
                ["member_count"] = f.MemberCount,
                ["members_readable"] = f.MembersReadable,
                ["unplaced"] = f.Unplaced,
                ["empty"] = f.Empty,
                ["dominant_categories"] = cats
            };
        }

        public static JObject ToJson(GroupInstanceFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["group_id"] = f.ElementId,
                ["group_type_id"] = f.TypeId,
                ["type_name"] = f.TypeName,
                ["level"] = f.LevelName,
                ["workset"] = f.WorksetName,
                ["is_nested"] = f.IsNested,
                ["parent_group_id"] = f.ParentGroupId,
                ["excluded_member_count"] = f.ExcludedMemberCount,
                ["readable"] = f.Readable
            };
        }

        public static JObject ToJson(DesignOptionFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["option_id"] = f.ElementId,
                ["name"] = f.Name,
                ["option_set"] = f.SetName,
                ["is_primary"] = f.IsPrimary,
                ["element_count"] = f.ElementCount,
                ["readable"] = f.Readable
            };
        }

        public static List<KeyValuePair<string, long>> Ranked(Dictionary<string, long> d)
        {
            var rows = new List<KeyValuePair<string, long>>();
            if (d == null) return rows;
            rows.AddRange(d);
            rows.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            return rows;
        }
    }
}
