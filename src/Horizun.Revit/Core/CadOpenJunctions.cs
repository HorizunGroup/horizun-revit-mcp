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
// the bridge's rule is that a write is consented, not inferred.
//
// WHAT IT REPORTS, and why the scope is what it is. At every POINT where this
// call has an end - of something it created, re-shaped or released - it names
// every end sitting there that holds nothing. Two readings of "this call's work"
// were tried and measured before this one:
//
//   ends of touched elements only   missed the loose end of a NEIGHBOUR that was
//                                   never written to, left hanging because the
//                                   fitting between them was released to let the
//                                   re-shape happen. That is collateral of this
//                                   call and belongs in its reply.
//   both ends free                  missed a declared junction where one side is
//                                   still held by something and the other is not.
//                                   What makes an end worth reporting is that IT
//                                   holds nothing; what its neighbour is doing is
//                                   not the test.
//
// It stays deliberately narrow in the other direction. Ends far apart are not its
// business (a gap is a design question), an end that holds something is not
// reported, and a loose end elsewhere in the model is somebody else's - that is
// what horizun_audit_cad_model is for.
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

            // EVERY END, AND WHETHER IT HOLDS ANYTHING.
            //
            // MEASURED: the first version collected only the FREE ends and paired them with each other,
            // so it saw a junction where BOTH sides were loose and missed one where the drawing declares
            // a junction, one side is still held by a neighbour and the other is loose. The acceptance
            // caught that third case and this did not. What makes an end worth reporting is that IT
            // holds nothing while another end sits on it - what the other end is doing is not the test.
            var ends = new List<Tuple<MEPCurve, Connector, bool>>();
            foreach (MEPCurve c in all)
            {
                ConnectorManager m = MepFacts.ManagerOf(c);
                if (m == null) continue;
                foreach (Connector k in m.Connectors)
                {
                    try
                    {
                        if (k.ConnectorType != ConnectorType.End) continue;
                        ends.Add(Tuple.Create(c, k, k.IsConnected));
                    }
                    catch { }
                }
            }
            var openEnds = ends.Where(e => !e.Item3).ToList();

            // THE POINTS THIS CALL WORKED AT.
            //
            // MEASURED: scoping the report to ENDS OF TOUCHED ELEMENTS missed the third loose end of a
            // release - the one belonging to a neighbour this call never wrote to, left hanging because
            // the fitting between them was released to let the re-shape happen. It is collateral of THIS
            // call and it belongs in its reply.
            //
            // The scope is still this call's work, just stated as places instead of elements: the points
            // where something it touched has an end. A loose end somewhere else in the model is somebody
            // else's business and stays out - that is what horizun_audit_cad_model is for.
            var worksites = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in ends)
                if (ids.Contains(Rid.Value(e.Item1.Id)))
                {
                    try { if (e.Item2.Origin != null) worksites.Add(Key(e.Item2.Origin)); }
                    catch { }
                }

            var reported = new HashSet<string>();
            foreach (var mine in openEnds)
            {
                long mineId = Rid.Value(mine.Item1.Id);
                bool atAWorksite;
                try { atAWorksite = mine.Item2.Origin != null && worksites.Contains(Key(mine.Item2.Origin)); }
                catch { atAWorksite = false; }
                if (!ids.Contains(mineId) && !atAWorksite) continue;
                foreach (var theirs in ends)
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
                        ["this_call_touched_the_other_one"] = ids.Contains(theirId),
                        ["this_call_touched_this_one"] = ids.Contains(mineId),
                        ["the_other_end_holds_something"] = theirs.Item3,
                        ["means"] = theirs.Item3
                            ? "this end holds nothing and sits on an end that IS held by something else - " +
                              "the two do not meet each other, whatever the drawing says about this point."
                            : "both ends sit on this point and neither holds anything."
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

        /// <summary>A point, rounded to the same tolerance two ends are judged coincident by.</summary>
        private static string Key(XYZ p)
        {
            return Math.Round(p.X / SamePointFt) + ":" + Math.Round(p.Y / SamePointFt) + ":" +
                   Math.Round(p.Z / SamePointFt);
        }

        private static JToken SafeId(Connector c)
        {
            try { return c.Id; }
            catch { return null; }
        }
    }
}
