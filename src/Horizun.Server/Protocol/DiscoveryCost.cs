// -----------------------------------------------------------------------------
// Horizun MCP server - what discovery COSTS, measured. Original Horizun code.
//
// G03 of the competitive inventory asks for discipline-focused discovery that
// "conserva resultados y reduce >=40% bytes de descubrimiento en fixture común".
// Tool packs already do the reducing (ToolPacks.cs). What nothing did was MEASURE
// it: the acceptance criterion is a percentage, and a percentage that comes from
// an estimate has not been met, it has been asserted.
//
// So the server can say, about itself, right now: how many bytes its tools/list
// answer is with the selection in force, how many it would be with every pack
// active, and therefore what the selection saved. Both numbers are produced by
// serialising the SAME list builder the server answers with - not a model of it -
// because a measurement of a reimplementation measures the reimplementation.
//
// THE PERMISSION FILTER IS HELD CONSTANT. A tool hidden because the owner set
// read_only is not a saving attributable to packs, so the baseline is "the same
// permission posture, no pack restriction". Otherwise a locked-down machine would
// report a spectacular pack saving it never made.
// -----------------------------------------------------------------------------
using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal static class DiscoveryCost
    {
        /// <summary>
        /// Measure the current tools/list answer against the unrestricted one.
        ///
        /// <paramref name="advertiseTaskSupport"/> must be the same value the live
        /// answer uses, or the two numbers are of two different documents.
        /// </summary>
        public static JObject Measure(bool advertiseTaskSupport)
        {
            var o = new JObject
            {
                ["measured"] = false,
                ["means"] =
                    "bytes of the UTF-8 JSON this server would return for tools/list, with and without the " +
                    "active tool-pack selection, at the same permission profile. Produced by serialising the " +
                    "same builder the server answers with. A null field could not be measured and is not a zero."
            };

            try
            {
                Horizun.Revit.Core.ToolPacks.Resolution packs =
                    Horizun.Revit.Core.Settings.ActivePackResolution();

                JArray restricted = Tools.List(advertiseTaskSupport);
                long restrictedBytes = Bytes(restricted);

                o["active_selection"] = Describe(packs);
                o["tool_count"] = restricted.Count;
                o["bytes"] = restrictedBytes;

                if (!packs.Restricting)
                {
                    // Nothing is being restricted, so there is no saving to claim. Saying
                    // 0% here is the honest answer and the one a fixture should read.
                    o["baseline_tool_count"] = restricted.Count;
                    o["baseline_bytes"] = restrictedBytes;
                    o["bytes_saved"] = 0;
                    o["reduction_percent"] = 0.0;
                    o["measured"] = true;
                    return o;
                }

                JArray unrestricted = Tools.ListIgnoringPacks(advertiseTaskSupport);
                long baselineBytes = Bytes(unrestricted);
                o["baseline_tool_count"] = unrestricted.Count;
                o["baseline_bytes"] = baselineBytes;
                o["bytes_saved"] = Math.Max(0, baselineBytes - restrictedBytes);
                o["reduction_percent"] = baselineBytes <= 0
                    ? 0.0
                    : Math.Round(100.0 * (baselineBytes - restrictedBytes) / baselineBytes, 2);
                o["measured"] = true;
            }
            catch (Exception ex)
            {
                // A measurement that failed must not look like a measurement of zero.
                o["error"] = ex.Message;
            }
            return o;
        }

        private static JObject Describe(Horizun.Revit.Core.ToolPacks.Resolution packs)
        {
            var o = new JObject
            {
                ["source"] = packs.Source.ToString().ToLowerInvariant(),
                ["restricting"] = packs.Restricting,
                ["problem"] = packs.Problem == null ? (JToken)JValue.CreateNull() : packs.Problem
            };
            o["active_packs"] = packs.ActivePacks == null
                ? (JToken)JValue.CreateNull()
                : new JArray(packs.ActivePacks);
            o["chosen_packs"] = packs.ChosenPacks == null
                ? (JToken)JValue.CreateNull()
                : new JArray(packs.ChosenPacks);
            o["added_by_dependency"] = packs.AddedByDependency == null
                ? (JToken)JValue.CreateNull()
                : new JArray(packs.AddedByDependency);
            return o;
        }

        private static long Bytes(JArray tools)
        {
            // The wire form, not the indented one: what the client actually pays for.
            string json = new JObject { ["tools"] = tools }.ToString(Formatting.None);
            return Encoding.UTF8.GetByteCount(json);
        }

        /// <summary>
        /// The discipline map a client reads to choose a pack: pack name, what it is for,
        /// which packs come with it, and how many tools it contributes. This is the part
        /// of G03 that server/discover publishes - the selection itself remains an owner
        /// decision made in settings, never something a client can change by asking.
        /// </summary>
        public static JArray Disciplines()
        {
            var arr = new JArray();
            foreach (string pack in SortedPackNames())
            {
                var row = new JObject
                {
                    ["pack"] = pack,
                    ["tools"] = Horizun.Revit.Core.ToolPacks.MembersOf(pack).Count,
                    ["requires"] = new JArray(Horizun.Revit.Core.ToolPacks.DependenciesOf(pack))
                };
                arr.Add(row);
            }
            return arr;
        }

        private static System.Collections.Generic.List<string> SortedPackNames()
        {
            var names = new System.Collections.Generic.List<string>(Horizun.Revit.Core.ToolPacks.KnownPacks);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
