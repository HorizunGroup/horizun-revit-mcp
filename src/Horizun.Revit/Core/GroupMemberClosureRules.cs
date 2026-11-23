// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE CLOSURE OF A GROUP OF MEP ELEMENTS: what Revit adds by itself, said in advance.
//
// NewGroup over pipes, ducts or conduits does not hand back exactly the ids it was
// given. Revit adds the "Center Line" companion of every curve (a dependent, not
// something the caller selected) and, when a tee reduces on its own, the fittings it
// put there. The exact comparison horizun_manage_groups create made - the members
// the group holds must equal the elements asked for - therefore refused a perfectly
// valid group, and a person had to read the ids to see that the "extra" members were
// the network's own representation.
//
// The rule now is a CLOSURE, not an equality:
//
//   members found = elements asked for  +  members Revit is known to add
//
// Every extra is classified by WHY it is there (center_line: a dependent of an
// element asked for; auto_fitting: an MEP fitting joined to an element asked for).
// An extra that is neither is a real surprise and still refuses. The counts are kept
// apart - physical elements (pipes, fittings) and representations (center lines) -
// because "29 members" that is 14 pipes and 15 lines is not 29 pipes.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class GroupClosure
    {
        public bool Holds;
        public List<long> Missing = new List<long>();
        public List<long> Unexpected = new List<long>();
        public List<long> AcceptedExtras = new List<long>();
        public int PhysicalMembers;
        public int RepresentationalMembers;

        public JObject ToJson(IDictionary<long, string> kinds) => new JObject
        {
            ["holds"] = Holds,
            ["missing_ids"] = new JArray(Missing),
            ["unexpected_ids"] = new JArray(Unexpected),
            ["accepted_extras"] = new JArray(AcceptedExtras.Select(id => new JObject
            {
                ["element_id"] = id,
                ["why"] = kinds != null && kinds.TryGetValue(id, out string k) ? k : "accepted"
            })),
            ["physical_members"] = PhysicalMembers,
            ["representational_members"] = RepresentationalMembers
        };
    }

    public static class GroupMemberClosureRules
    {
        public const string KindCenterLine = "center_line";
        public const string KindAutoFitting = "auto_fitting";

        /// <summary>
        /// Judge the members a new group really holds. `acceptable` names, by id, the extras that may be there and
        /// why; `representationalIds` are the ids (asked for or extra) that are a representation, not a physical
        /// element. A requested element missing from the group, or an extra nobody accepted, makes it not hold.
        /// </summary>
        public static GroupClosure Evaluate(IEnumerable<long> requested, IEnumerable<long> found,
                                            IDictionary<long, string> acceptable, ISet<long> representationalIds)
        {
            var result = new GroupClosure();
            var asked = new HashSet<long>(requested ?? Enumerable.Empty<long>());
            var held = new HashSet<long>(found ?? Enumerable.Empty<long>());
            result.Missing = asked.Where(id => !held.Contains(id)).OrderBy(id => id).ToList();
            foreach (long id in held.Where(id => !asked.Contains(id)).OrderBy(id => id))
            {
                if (acceptable != null && acceptable.ContainsKey(id)) result.AcceptedExtras.Add(id);
                else result.Unexpected.Add(id);
            }
            result.RepresentationalMembers = held.Count(id => representationalIds != null && representationalIds.Contains(id));
            result.PhysicalMembers = held.Count - result.RepresentationalMembers;
            result.Holds = asked.Count > 0 && held.Count > 0 && result.Missing.Count == 0 && result.Unexpected.Count == 0;
            return result;
        }

        /// <summary>The sentence a refused closure gives: what is missing, what nobody expected.</summary>
        public static string Refusal(GroupClosure c)
        {
            if (c == null || c.Holds) return null;
            var parts = new List<string>();
            if (c.Missing.Count > 0) parts.Add("missing from the group: " + string.Join(", ", c.Missing.Take(20)));
            if (c.Unexpected.Count > 0) parts.Add("members nobody asked for and that are not the network's own center line or fitting: " +
                                                  string.Join(", ", c.Unexpected.Take(20)));
            if (parts.Count == 0) parts.Add("the group holds nothing");
            return "NewGroup did not take the planned members (" + string.Join("; ", parts) + ").";
        }
    }
}
