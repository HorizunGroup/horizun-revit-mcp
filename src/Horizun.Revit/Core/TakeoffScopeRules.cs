// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// WHICH DOCUMENTS A LINKED TAKEOFF MEASURES, AND UNDER WHOSE NAME.
//
// THE DEFECT THIS FILE EXISTS FOR. horizun_quantities mode='takeoff' with
// include_links=true swept the RevitLinkInstances of the host and kept the link
// each measured document belonged to in a Dictionary<Document, RevitLinkInstance>.
//
// Revit loads a linked FILE once. Two RevitLinkInstances of the same file - two
// towers, a mirrored wing, a podium placed twice - both answer GetLinkDocument()
// with the SAME Document object. So the dictionary collapsed: the second instance
// overwrote the first, and every row measured through either placement came back
// stamped with the LAST instance's id. The elements were measured twice, which is
// correct (the building is there twice), and both copies were attributed to one
// placement, which is not. A reader tracing a quantity back to the model would
// have found half of it in a link instance that never produced it.
//
// The provenance is the whole point of include_links: a takeoff you cannot trace
// is a number somebody has to take on faith.
//
// So placement, not document, is the unit. This file is the arithmetic of that -
// deliberately Revit-free, so the numbering and the declaration can be proved at
// a desk while the command keeps only the part that needs a Document.
//
// AND IT DECLARES THE REPETITION RATHER THAN HIDING IT. A file placed twice
// contributes its quantities twice, which is what the model says and what a
// schedule of linked elements would report. That is easy to mistake for double
// counting, so the reply names every repeated document, its placements and their
// link instance ids, and says in words that the totals include it once per
// placement. A number that surprises somebody is fine; a number that surprises
// them with no way to check it is not.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// One loaded Revit link instance, as the takeoff sees it before any element is
    /// read. DocumentKey identifies the linked FILE (two instances of one file share
    /// it); LinkInstanceId identifies the PLACEMENT and is never shared.
    /// </summary>
    public sealed class TakeoffLinkFact
    {
        public string LinkInstanceId;
        public string DocumentKey;
        public string Title;
        public string Path;
    }

    /// <summary>One placement to measure: a link instance, and where it sits among the placements of its own file.</summary>
    public sealed class TakeoffPlacement
    {
        public TakeoffLinkFact Link;
        public int Occurrence;               // 1-based, in link instance order
        public int OccurrencesOfDocument;    // how many placements this file has in the host

        public bool IsRepeated { get { return OccurrencesOfDocument > 1; } }
    }

    /// <summary>The placements, and what has to be said about the ones that repeat.</summary>
    public sealed class TakeoffScope
    {
        public List<TakeoffPlacement> Placements = new List<TakeoffPlacement>();

        /// <summary>One entry per linked file placed more than once. Empty when none is.</summary>
        public JArray RepeatedDocuments = new JArray();

        public bool HasRepeatedDocuments { get { return RepeatedDocuments.Count > 0; } }
    }

    public static class TakeoffScopeRules
    {
        /// <summary>
        /// Number the placements and describe the repeated files. Order is preserved
        /// exactly as given - the command hands them over in link instance id order, and
        /// a takeoff that reordered its own scope would produce a different row order for
        /// the same model.
        ///
        /// A fact with no LinkInstanceId is refused rather than numbered: the instance id
        /// IS the identity this whole file exists to keep, and a placement without one
        /// could not be told from the next.
        /// </summary>
        public static TakeoffScope Resolve(IEnumerable<TakeoffLinkFact> links)
        {
            var scope = new TakeoffScope();
            if (links == null) return scope;

            var order = new List<TakeoffLinkFact>();
            foreach (TakeoffLinkFact f in links)
            {
                if (f == null) continue;
                if (string.IsNullOrWhiteSpace(f.LinkInstanceId))
                    throw new ArgumentException("A link placement was offered with no link_instance_id. The instance " +
                                                "id is the only thing that tells two placements of the same file " +
                                                "apart, and a takeoff without it cannot say where a quantity came from.");
                order.Add(f);
            }

            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (TakeoffLinkFact f in order)
            {
                string key = KeyOf(f);
                int n;
                totals[key] = totals.TryGetValue(key, out n) ? n + 1 : 1;
            }

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (TakeoffLinkFact f in order)
            {
                string key = KeyOf(f);
                int n;
                seen[key] = n = (seen.TryGetValue(key, out n) ? n : 0) + 1;
                scope.Placements.Add(new TakeoffPlacement
                {
                    Link = f,
                    Occurrence = n,
                    OccurrencesOfDocument = totals[key]
                });
            }

            // The declaration, in the order the repeated files were first met.
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (TakeoffPlacement p in scope.Placements)
            {
                string key = KeyOf(p.Link);
                if (p.OccurrencesOfDocument < 2 || !declared.Add(key)) continue;
                var ids = new JArray();
                foreach (TakeoffPlacement q in scope.Placements)
                    if (KeyOf(q.Link) == key) ids.Add(q.Link.LinkInstanceId);
                scope.RepeatedDocuments.Add(new JObject
                {
                    ["document"] = p.Link.Title,
                    ["path"] = p.Link.Path,
                    ["placements"] = p.OccurrencesOfDocument,
                    ["link_instance_ids"] = ids,
                    ["means"] = "this linked file is placed " + p.OccurrencesOfDocument + " times in the host, so its " +
                                "elements are measured once PER PLACEMENT and the totals include them " +
                                p.OccurrencesOfDocument + " times. That is what the model says, not a double count - " +
                                "every row carries the link_instance_id it was measured through, so each copy can be " +
                                "traced to its own placement."
                });
            }
            return scope;
        }

        /// <summary>
        /// The provenance fields a document entry carries. Kept here so the row and the
        /// documents block cannot describe the same placement two different ways.
        /// </summary>
        public static JObject PlacementJson(TakeoffPlacement placement)
        {
            if (placement == null) return null;
            return new JObject
            {
                ["link_instance_id"] = placement.Link.LinkInstanceId,
                ["placement"] = placement.Occurrence,
                ["placements_of_this_document"] = placement.OccurrencesOfDocument
            };
        }

        /// <summary>
        /// A file's identity for counting placements. The path when there is one - two
        /// instances of one file share it - and the title otherwise, because an
        /// unsaved or otherwise path-less linked document still has to be counted as
        /// something rather than as nothing.
        /// </summary>
        private static string KeyOf(TakeoffLinkFact f)
        {
            if (!string.IsNullOrWhiteSpace(f.DocumentKey)) return f.DocumentKey;
            if (!string.IsNullOrWhiteSpace(f.Path)) return f.Path;
            if (!string.IsNullOrWhiteSpace(f.Title)) return "title:" + f.Title;
            // No file identity at all: treat the placement as its own file rather than
            // merging every anonymous link into one imaginary document.
            return "instance:" + f.LinkInstanceId;
        }
    }
}
