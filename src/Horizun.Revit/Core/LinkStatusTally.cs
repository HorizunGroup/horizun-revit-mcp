// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Counting link statuses without turning "I could not read it" into "it is broken".
//
// This exists because both places that counted links got it wrong, in two
// different ways, and a real cloud-hosted model was the only thing that showed it:
//
//   * ModelScan stored an unreadable status as JSON null and then tested
//     `row["load_status"] != null`. Newtonsoft stores a JSON null as a JValue of
//     type Null, which is NOT a C# null reference - so the test passed, the cast
//     to string gave null, `null != "Loaded"` was true, and every link it could
//     not interrogate was reported as NOT LOADED. The comment above the line said
//     the opposite of what the line did.
//
//   * AuditModel wrote `catch { return true; }` around the same read, which counts
//     a failed read as an unloaded link on purpose.
//
// Both produced a fabricated defect on a delivery model: two links reported as
// not loaded while both linked documents were open in Revit. The API call each
// used differs (RevitLinkType.GetLinkedFileStatus works for a cloud link,
// GetExternalFileReference throws on one), which is why only the real project
// triggered it - every lab model links a local file.
//
// The rule, in one place, with one meaning: a status we did not read is UNKNOWN.
// It is not loaded, it is not unloaded, and while any exist the tally is not
// complete. No `using Autodesk.*` here, so the rule is provable without Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// The outcome of counting link statuses. `Loaded + NotLoaded + Unknown == Total`
    /// always holds: nothing is double counted and nothing is dropped.
    /// </summary>
    public sealed class LinkTally
    {
        public int Total { get; internal set; }
        public int Loaded { get; internal set; }
        public int NotLoaded { get; internal set; }

        /// <summary>Links whose status could not be read. Neither loaded nor unloaded.</summary>
        public int Unknown { get; internal set; }

        /// <summary>
        /// True only when every link answered. False means the loaded/not-loaded split
        /// describes a SUBSET, so "all links are loaded" may not be said.
        /// </summary>
        public bool Complete => Unknown == 0;

        /// <summary>
        /// The sentence to publish. It never claims a clean bill of health it cannot
        /// support, and it never calls an unread link broken.
        ///
        /// `unit` names WHAT is being counted, because the two callers count different
        /// things and neither used to say so: audit_model tallies link TYPES and
        /// model_scan tallies link INSTANCES, and on the same model - one where several
        /// links were loaded 3-4 times each - they published "1 of 8" against "4 of 22".
        /// Both were right, and anyone reading both reports together would conclude one
        /// of them was broken. A count whose unit is unstated is a count the reader
        /// supplies a unit for, and they will supply the wrong one.
        /// </summary>
        public string Summary(string unit = "link")
        {
            if (Total == 0) return "No Revit links.";

            string u = unit + "(s)";

            if (Complete)
                return NotLoaded == 0
                    ? "All " + Total + " " + u + " are loaded."
                    : NotLoaded + " of " + Total + " " + u + " are NOT loaded. Anything coordinated against them - " +
                      "clash results, dimensions, copy/monitor - is currently checking against nothing.";

            string head = Unknown == Total
                ? "None of the " + Total + " " + u + " would report their status, so whether they are loaded is UNKNOWN."
                : NotLoaded + " of " + Total + " " + u + " are NOT loaded, and " + Unknown + " more would not report " +
                  "their status at all, so their state is UNKNOWN.";

            return head + " This is PARTIAL coverage: an unread status is not evidence of a healthy link and not " +
                   "evidence of a broken one. " + Loaded + " " + u + " did confirm they are loaded.";
        }
    }

    public static class LinkStatusTally
    {
        /// <summary>
        /// Tally one status string per link. A null or empty string means the status could
        /// not be read and is counted as UNKNOWN - never as unloaded. Comparison is
        /// case-insensitive because the value comes from an enum's ToString().
        /// </summary>
        public static LinkTally Of(IEnumerable<string> statuses)
        {
            var tally = new LinkTally();
            if (statuses == null) return tally;

            foreach (string s in statuses)
            {
                tally.Total++;
                if (string.IsNullOrEmpty(s)) tally.Unknown++;
                else if (string.Equals(s, "Loaded", StringComparison.OrdinalIgnoreCase)) tally.Loaded++;
                else tally.NotLoaded++;
            }
            return tally;
        }
    }
}
