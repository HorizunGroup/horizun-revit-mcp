// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// FEDERATION AND FOREIGN CONTENT.
//
// Two things a scan misses that make a model heavy and a delivery wrong:
//
//   A LINK INSIDE A LINK. Nobody opens the nested one, so nobody sees it, and
//   a circular reference - A links B links A - is invisible until Revit spends
//   ten minutes opening a file somebody thought was small. Cycles are found
//   here as a graph property, provable at a desk.
//
//   CONTENT THAT IS NOT REVIT. A four-gigabyte point cloud, a linked image, a
//   texture nobody can resolve: a model carrying all three scans as clean
//   because nothing looked. They are counted, and their PATHS are checked for
//   whether they still resolve.
//
// AN UNRESOLVED PATH IS NOT AN ABSENT ONE. A texture whose file has moved is a
// different problem from a material that never had one - the first breaks a
// render on somebody else's machine, the second is a modelling choice - and a
// count that merges them tells you to fix the wrong thing.
//
// DECALS ARE A DECLARED GAP. There is no DecalType, no DecalElement and no
// OST_Decals in the API of any supported year - checked by reflection over
// 2023 and 2027 rather than assumed - so decals are reported as NOT OBSERVABLE
// rather than as zero. Zero would be a count; this is an absence of a way to
// count.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class LinkFederationFact
    {
        public long ElementId;
        public string Name;
        public bool NameReadable = true;
        /// <summary>Attachment or overlay. Null when the model would not say.</summary>
        public string AttachmentType;
        public string WorksetName;
        public bool? IsRoomBounding;
        public bool? IsLoaded;
        /// <summary>Names of links loaded INSIDE this one. Empty is a real answer; null is not.</summary>
        public List<string> NestedLinkNames = new List<string>();
        public bool NestedReadable = true;
        /// <summary>True when this link is itself nested inside another.</summary>
        public bool? IsNested;
    }

    public sealed class ExternalPathFact
    {
        public string Kind;
        public long ElementId;
        public string Name;
        /// <summary>Null when this kind carries no path at all.</summary>
        public string Path;
        /// <summary>True/false when a path exists and was checked; null when there is none to check.</summary>
        public bool? Resolves;
    }

    public static class FederationContentRules
    {
        public const string DecalsMean =
            "decals are NOT OBSERVABLE through the API of any supported Revit year - there is no DecalType, no " +
            "DecalElement and no OST_Decals, checked by reflection rather than assumed. They are reported as " +
            "unobservable rather than as zero, because zero is a count and this is the absence of a way to count.";

        public const string PathsMean =
            "an unresolved path is not an absent one. A texture whose file has moved breaks a render on " +
            "somebody else's machine; a material that never had one is a modelling choice. They are counted " +
            "apart, because a merged number sends you to fix the wrong thing.";

        public const string NestingMeans =
            "a link inside a link is loaded by Revit and seen by nobody, and a circular reference - A links B " +
            "links A - stays invisible until an ordinary open takes ten minutes. Cycles are reported by name " +
            "so the loop can be broken at whichever end is wrong.";

        /// <summary>
        /// Cycles in the link graph, each as the names that form the loop. A cycle is
        /// reported once, starting from its lowest name, so two runs of one model
        /// produce the same list rather than a rotation of it.
        /// </summary>
        public static List<List<string>> CircularReferences(IDictionary<string, List<string>> graph)
        {
            var found = new List<List<string>>();
            if (graph == null) return found;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string start in graph.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var path = new List<string>();
                var onPath = new HashSet<string>(StringComparer.Ordinal);
                Walk(graph, start, path, onPath, found, seen);
            }
            return found;
        }

        private static void Walk(IDictionary<string, List<string>> graph, string node,
                                 List<string> path, HashSet<string> onPath,
                                 List<List<string>> found, HashSet<string> seen)
        {
            if (onPath.Contains(node))
            {
                // The loop is the tail of the path from the first sighting of node.
                int at = path.IndexOf(node);
                if (at < 0) return;
                List<string> cycle = path.Skip(at).ToList();
                string key = Canonical(cycle);
                if (seen.Add(key)) found.Add(Rotate(cycle));
                return;
            }
            List<string> next;
            if (!graph.TryGetValue(node, out next) || next == null) return;
            if (path.Count > 64) return;      // a guard, not a limit anybody should hit

            path.Add(node);
            onPath.Add(node);
            foreach (string child in next.OrderBy(x => x, StringComparer.Ordinal))
                Walk(graph, child, path, onPath, found, seen);
            path.RemoveAt(path.Count - 1);
            onPath.Remove(node);
        }

        /// <summary>Starts a cycle at its lowest name so rotations compare equal.</summary>
        private static List<string> Rotate(List<string> cycle)
        {
            if (cycle.Count == 0) return cycle;
            int lowest = 0;
            for (int i = 1; i < cycle.Count; i++)
                if (string.CompareOrdinal(cycle[i], cycle[lowest]) < 0) lowest = i;
            return cycle.Skip(lowest).Concat(cycle.Take(lowest)).ToList();
        }

        private static string Canonical(List<string> cycle)
        {
            return string.Join(">", Rotate(cycle));
        }

        public static JObject PathTally(IEnumerable<ExternalPathFact> paths)
        {
            List<ExternalPathFact> all =
                (paths ?? Enumerable.Empty<ExternalPathFact>()).Where(p => p != null).ToList();

            var byKind = new JObject();
            foreach (IGrouping<string, ExternalPathFact> g in all.GroupBy(p => p.Kind ?? "(unknown)")
                                                                 .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                byKind[g.Key] = new JObject
                {
                    ["total"] = g.Count(),
                    // THREE ANSWERS, not two.
                    ["with_path_resolving"] = g.Count(p => p.Resolves == true),
                    ["with_path_not_resolving"] = g.Count(p => p.Resolves == false),
                    ["without_a_path"] = g.Count(p => p.Resolves == null)
                };
            }

            return new JObject
            {
                ["by_kind"] = byKind,
                ["unresolved_total"] = all.Count(p => p.Resolves == false),
                ["paths_mean"] = PathsMean
            };
        }

        public static JObject ToJson(LinkFederationFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["link_id"] = f.ElementId,
                ["name"] = f.Name,
                ["name_readable"] = f.NameReadable,
                ["attachment_type"] = f.AttachmentType,
                ["workset"] = f.WorksetName,
                ["is_room_bounding"] = f.IsRoomBounding,
                ["is_loaded"] = f.IsLoaded,
                ["nested_link_count"] = f.NestedReadable ? (JToken)f.NestedLinkNames.Count : null,
                ["nested_links"] = new JArray(f.NestedLinkNames.Select(x => (JToken)x)),
                ["nested_readable"] = f.NestedReadable,
                ["is_nested_link"] = f.IsNested
            };
        }

        public static JObject ToJson(ExternalPathFact p)
        {
            if (p == null) return null;
            return new JObject
            {
                ["kind"] = p.Kind,
                ["element_id"] = p.ElementId,
                ["name"] = p.Name,
                ["path"] = p.Path,
                ["resolves"] = p.Resolves
            };
        }
    }
}
