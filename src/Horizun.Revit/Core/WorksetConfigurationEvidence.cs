// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Post-open evidence for a requested workset configuration.  The Revit-facing
// command gathers names and IsOpen values; this class decides whether those
// observations prove the request actually landed.  Keeping the decision free of
// Revit makes the dangerous negative cases executable in ordinary unit tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class WorksetOpenObservation
    {
        public string Name { get; set; }
        public bool IsOpen { get; set; }
    }

    public sealed class WorksetConfigurationEvidence
    {
        public bool Requested { get; private set; }
        public bool Applied { get; private set; }
        public string Error { get; private set; }
        public IList<string> OpenNames { get; private set; } = new List<string>();
        public IList<string> ClosedNames { get; private set; } = new List<string>();

        public static WorksetConfigurationEvidence Unreadable(bool requested, string error)
        {
            return new WorksetConfigurationEvidence
            {
                Requested = requested,
                Applied = false,
                Error = "The opened document's worksets could not be measured: " + error
            };
        }

        public static WorksetConfigurationEvidence Verify(IList<string> closeNames, bool openAll,
                                                            IEnumerable<WorksetOpenObservation> observations)
        {
            closeNames = closeNames ?? new List<string>();
            bool requested = openAll || closeNames.Count > 0;
            var result = new WorksetConfigurationEvidence { Requested = requested, Applied = false };
            if (!requested) return result;
            if (openAll && closeNames.Count > 0)
            {
                result.Error = "open_all_worksets and close_workset_names contradict each other.";
                return result;
            }

            var rows = (observations ?? Enumerable.Empty<WorksetOpenObservation>()).ToList();
            result.OpenNames = rows.Where(x => x != null && x.IsOpen).Select(x => x.Name).ToList();
            result.ClosedNames = rows.Where(x => x != null && !x.IsOpen).Select(x => x.Name).ToList();

            if (openAll)
            {
                if (result.ClosedNames.Count == 0)
                {
                    result.Applied = true; // includes a non-workshared model: zero user worksets, all zero are open
                    return result;
                }
                result.Error = "open_all_worksets was requested, but these user worksets are still closed: " +
                               string.Join(", ", result.ClosedNames);
                return result;
            }

            var wanted = new HashSet<string>(closeNames, StringComparer.OrdinalIgnoreCase);
            foreach (string name in closeNames)
            {
                var hits = rows.Where(x => x != null &&
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count != 1)
                {
                    result.Error = hits.Count == 0
                        ? "The requested user workset '" + name + "' was not present after opening."
                        : "The requested user workset '" + name + "' was ambiguous after opening.";
                    return result;
                }
                if (hits[0].IsOpen)
                {
                    result.Error = "The requested user workset '" + name + "' is OPEN after opening.";
                    return result;
                }
            }

            var unexpectedlyClosed = rows.Where(x => x != null && !x.IsOpen && !wanted.Contains(x.Name))
                                         .Select(x => x.Name).ToList();
            if (unexpectedlyClosed.Count > 0)
            {
                result.Error = "These unrequested user worksets are also closed: " +
                               string.Join(", ", unexpectedlyClosed);
                return result;
            }

            result.Applied = true;
            return result;
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["requested"] = Requested,
                ["applied"] = Applied,
                ["measurement_error"] = Error,
                ["worksets_total"] = OpenNames.Count + ClosedNames.Count,
                ["worksets_open"] = OpenNames.Count,
                ["worksets_closed"] = ClosedNames.Count,
                ["open_workset_names"] = new JArray(OpenNames),
                ["closed_workset_names"] = new JArray(ClosedNames)
            };
        }
    }
}
