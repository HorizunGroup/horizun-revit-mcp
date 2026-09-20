// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// ENDS THAT MEET AND HOLD NOTHING.
//
// MEASURED (campaign 9, the both-ends-connected case): a revision that divides a
// run into two builds both pieces correctly - right layer, right size, right
// place - and joins neither. The reply said `state: applied, actions_failed: 0`,
// every duct count was right, and the two new ends sat at the same point holding
// nothing. Six open terminals became eight and nobody said so.
//
// That is not a bug in the writes. An update builds geometry; JOINING is a
// separate, consented step (horizun_cad_connect), and it ran before the revision
// rather than after it. The defect is that the reply did not SAY what it had
// left: a caller reading `applied` has no way to know the model now has a
// junction that exists on paper and not in the model.
//
// So this reports it, and does not fix it. Connecting here would be a write
// nobody asked for, made at the moment the caller is least able to see it - and
// the bridge's rule is that a write is consented, not inferred. What it does is
// name every end of what this call created or re-shaped that sits ON another
// duct's end with nothing between them. That condition needs no knowledge of the
// drawing: two ends at the same point, unjoined, is precisely what the connect
// step joins.
//
// It is deliberately narrow. Ends far apart are not its business (a gap is a
// design question), and an end already joined is not reported at all.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadOpenJunctions
    {
        /// <summary>One foot is 304.8 mm; a millimetre is the tolerance a connector point deserves.</summary>
        private const double SamePointFt = 1.0 / 304.8;

        /// <summary>
        /// Every free end of <paramref name="touched"/> that coincides with a free end of another MEP
        /// curve. Returns an empty array when there is nothing to say, which is the normal case.
        /// </summary>
        public static JArray Find(Document doc, IEnumerable<long> touched)
        {
            var found = new JArray();
            if (doc == null || touched == null) return found;

            var ids = new HashSet<long>(touched);
            if (ids.Count == 0) return found;

            // Every MEP curve in the document, read once. A junction has two sides and the other side is
            // usually NOT something this call touched - the piece that was already built is the whole
            // point of an update.
            List<MEPCurve> all;
            try
            {
                all = new FilteredElementCollector(doc)
                    .OfClass(typeof(MEPCurve)).WhereElementIsNotElementType()
                    .Cast<MEPCurve>().ToList();
            }
            catch { return found; }

            var openEnds = new List<Tuple<MEPCurve, Connector>>();
            foreach (MEPCurve c in all)
            {
                ConnectorManager m = MepFacts.ManagerOf(c);
                if (m == null) continue;
                foreach (Connector k in m.Connectors)
                {
                    try
                    {
                        if (k.ConnectorType != ConnectorType.End) continue;
                        if (k.IsConnected) continue;
                        openEnds.Add(Tuple.Create(c, k));
                    }
                    catch { }
                }
            }

            var reported = new HashSet<string>();
            foreach (var mine in openEnds)
            {
                long mineId = Rid.Value(mine.Item1.Id);
                if (!ids.Contains(mineId)) continue;
                foreach (var theirs in openEnds)
                {
                    long theirId = Rid.Value(theirs.Item1.Id);
                    if (theirId == mineId) continue;
                    XYZ a, b;
                    try { a = mine.Item2.Origin; b = theirs.Item2.Origin; }
                    catch { continue; }
                    if (a == null || b == null || a.DistanceTo(b) > SamePointFt) continue;

                    string pair = Math.Min(mineId, theirId) + ":" + Math.Max(mineId, theirId) + ":" +
                                  Math.Round(a.X, 4) + "," + Math.Round(a.Y, 4) + "," + Math.Round(a.Z, 4);
                    if (!reported.Add(pair)) continue;

                    found.Add(new JObject
                    {
                        ["element_id"] = mineId,
                        ["connector"] = SafeId(mine.Item2),
                        ["meets_element_id"] = theirId,
                        ["meets_connector"] = SafeId(theirs.Item2),
                        ["at_mm"] = new JArray(Math.Round(a.X * 304.8, 1), Math.Round(a.Y * 304.8, 1),
                                               Math.Round(a.Z * 304.8, 1)),
                        ["this_call_touched_the_other_one"] = ids.Contains(theirId)
                    });
                }
            }
            return found;
        }

        /// <summary>The sentence a reply carries beside the list, or null when there is nothing to say.</summary>
        public static string Means(JArray open)
        {
            if (open == null || open.Count == 0) return null;
            return open.Count + " end(s) of what this update built or re-shaped sit at the same point as " +
                   "another duct's end and hold NOTHING. The update writes geometry; it does not join it, " +
                   "and joining is its own consented step. Until horizun_cad_connect runs over these, the " +
                   "model has a junction that exists on the drawing and not in the model - which every " +
                   "count of elements, sizes and positions will report as correct.";
        }

        private static JToken SafeId(Connector c)
        {
            try { return c.Id; }
            catch { return null; }
        }
    }
}
