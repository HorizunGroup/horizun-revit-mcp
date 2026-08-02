// -----------------------------------------------------------------------------
// Horizun Revit MCP — how much of the host + linked model set was loaded.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class FederatedVisibility
    {
        public static JObject Measure(Document host, bool includeLinks)
        {
            DocumentVisibilityCoverage hostCoverage = DocumentVisibility.Measure(host);
            var sources = new JArray
            {
                Source("host", host.Title, null, hostCoverage)
            };
            var unavailable = new JArray();
            int total = 0;
            int loaded = 0;
            bool complete = hostCoverage.CoverageComplete;

            if (includeLinks)
            {
                foreach (RevitLinkInstance link in new FilteredElementCollector(host)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    total++;
                    Document linked = null;
                    try { linked = link.GetLinkDocument(); }
                    catch (Exception ex)
                    {
                        complete = false;
                        unavailable.Add(new JObject
                        {
                            ["link_instance_id"] = Rid.Value(link.Id),
                            ["name"] = link.Name,
                            ["reason"] = ex.Message
                        });
                        continue;
                    }
                    if (linked == null)
                    {
                        complete = false;
                        unavailable.Add(new JObject
                        {
                            ["link_instance_id"] = Rid.Value(link.Id),
                            ["name"] = link.Name,
                            ["reason"] = "link is unloaded or its document is unavailable"
                        });
                        continue;
                    }

                    loaded++;
                    DocumentVisibilityCoverage coverage = DocumentVisibility.Measure(linked);
                    if (!coverage.CoverageComplete) complete = false;
                    sources.Add(Source("link", linked.Title, Rid.Value(link.Id), coverage));
                }
            }

            return new JObject
            {
                ["coverage_complete"] = complete,
                ["include_links"] = includeLinks,
                ["link_instances_total"] = total,
                ["link_instances_loaded"] = loaded,
                ["link_instances_unavailable"] = total - loaded,
                ["sources"] = sources,
                ["unavailable_links"] = unavailable,
                ["note"] = complete
                    ? "Every considered source was loaded with all user worksets open."
                    : "INCOMPLETE COVERAGE: at least one considered host/link source is unavailable or has closed/unreadable worksets. Counts are lower bounds and absence is unproven."
            };
        }

        private static JObject Source(string kind, string title, long? linkId, DocumentVisibilityCoverage coverage) =>
            new JObject
            {
                ["source_kind"] = kind,
                ["source_model"] = title,
                ["link_instance_id"] = linkId,
                ["visibility_coverage"] = coverage.ToJson()
            };
    }
}
