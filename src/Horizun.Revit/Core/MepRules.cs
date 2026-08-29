// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// MEP arithmetic without Revit: which pair of open connectors a fitting joins,
// and why a pair is refused. The Revit half hands this file plain facts
// (origins, open flags, domains) and takes back a decision it can act on.
//
// THE SELECTION IS DETERMINISTIC OR IT IS A REFUSAL. "The nearest open pair"
// is only an answer when it is unique: two candidate pairs within the same
// tolerance are an ambiguity the caller must resolve by naming connectors -
// a bridge that picks one silently has chosen where a fitting goes in
// somebody's deliverable.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>One connector as plain facts. Feet, like every internal length.</summary>
    public sealed class ConnectorFact
    {
        public int Id;                 // Revit's own connector id, the stable name.
        public double X, Y, Z;         // origin, feet
        public bool IsConnected;
        public string Domain;          // canonical: piping/hvac/electrical/cable_tray/conduit/undefined
        public double DirX, DirY, DirZ; // outward basis Z, unit
    }

    public static class MepRules
    {
        /// <summary>Two connectors count as meeting when their origins agree within this. 1 mm.</summary>
        public const double CoincidenceToleranceFeet = 1.0 / 304.8;

        /// <summary>
        /// A second candidate pair within this of the best is an ambiguity, not a
        /// runner-up. Same grid as the coincidence tolerance.
        /// </summary>
        public const double AmbiguityToleranceFeet = 1.0 / 304.8;

        public const string CodeNoOpenConnector = "no_open_connector";
        public const string CodeAmbiguousPair = "ambiguous_connector_pair";
        public const string CodeDomainMismatch = "connector_domain_mismatch";
        public const string CodeNotCoincident = "connectors_not_coincident";

        public static double Distance(ConnectorFact a, ConnectorFact b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Choose the one open pair (one connector from each side) a fitting joins.
        /// Null return plus a code/reason is a refusal; a successful choice is the
        /// unique open pair with the smallest origin distance, which must itself be
        /// within the coincidence tolerance. An explicitly named connector id on
        /// either side narrows that side to the named connector - named or not, the
        /// same rules decide.
        /// </summary>
        public static bool SelectPair(IList<ConnectorFact> sideA, IList<ConnectorFact> sideB,
                                      int? namedA, int? namedB,
                                      out ConnectorFact chosenA, out ConnectorFact chosenB,
                                      out string code, out string reason)
        {
            chosenA = null; chosenB = null; code = null; reason = null;

            List<ConnectorFact> a = Narrow(sideA, namedA, "first", out reason);
            if (reason != null) { code = CodeNoOpenConnector; return false; }
            List<ConnectorFact> b = Narrow(sideB, namedB, "second", out reason);
            if (reason != null) { code = CodeNoOpenConnector; return false; }

            double best = double.MaxValue, second = double.MaxValue;
            foreach (ConnectorFact ca in a)
                foreach (ConnectorFact cb in b)
                {
                    double d = Distance(ca, cb);
                    if (d < best) { second = best; best = d; chosenA = ca; chosenB = cb; }
                    else if (d < second) second = d;
                }

            if (chosenA == null)
            {
                code = CodeNoOpenConnector;
                reason = "no open connector pair exists between the two elements.";
                return false;
            }
            if (second - best <= AmbiguityToleranceFeet && second != double.MaxValue)
            {
                code = CodeAmbiguousPair;
                reason = "two candidate connector pairs are " + Mm(best) + " and " + Mm(second) +
                         " apart - within one tolerance of each other. Name the connectors " +
                         "(connector ids per side) instead of letting distance decide.";
                chosenA = null; chosenB = null;
                return false;
            }
            if (!string.Equals(chosenA.Domain, chosenB.Domain, StringComparison.Ordinal))
            {
                code = CodeDomainMismatch;
                reason = "the chosen connectors live in different domains (" + chosenA.Domain + " vs " +
                         chosenB.Domain + "); a fitting cannot join them.";
                chosenA = null; chosenB = null;
                return false;
            }
            if (best > CoincidenceToleranceFeet)
            {
                code = CodeNotCoincident;
                reason = "the nearest open connectors are " + Mm(best) + " apart; a fitting joins " +
                         "connectors that MEET (within " + Mm(CoincidenceToleranceFeet) + "). Move the " +
                         "curves so their ends coincide, then create the fitting.";
                chosenA = null; chosenB = null;
                return false;
            }
            return true;
        }

        private static List<ConnectorFact> Narrow(IList<ConnectorFact> side, int? named, string label, out string problem)
        {
            problem = null;
            var open = new List<ConnectorFact>();
            if (side != null)
                foreach (ConnectorFact c in side)
                    if (c != null && !c.IsConnected && (named == null || c.Id == named.Value)) open.Add(c);
            if (open.Count > 0) return open;
            problem = named == null
                ? "the " + label + " element has no OPEN connector (" + Count(side) + " connector(s), all connected or none at all)."
                : "the " + label + " element has no OPEN connector with id " + named.Value + ".";
            return open;
        }

        private static int Count(IList<ConnectorFact> side) => side == null ? 0 : side.Count;

        public static string Mm(double feet) =>
            (feet * 304.8).ToString("0.0", CultureInfo.InvariantCulture) + " mm";

        /// <summary>
        /// The elbow/union split is a measured geometric fact: collinear open ends
        /// take a union/coupling, ends at an angle take an elbow. Revit enforces it
        /// with an exception AFTER a transaction opened; naming it before is cheaper
        /// and states the measured angle.
        /// </summary>
        public static double AngleDegrees(ConnectorFact a, ConnectorFact b)
        {
            double dot = a.DirX * b.DirX + a.DirY * b.DirY + a.DirZ * b.DirZ;
            if (dot > 1.0) dot = 1.0; else if (dot < -1.0) dot = -1.0;
            // Connector bases point OUT of each element; two curves meeting head-on
            // have antiparallel outward directions. The turn the fitting makes is
            // measured between the flow directions: 180 - angle(out, out).
            return 180.0 - (Math.Acos(dot) * 180.0 / Math.PI);
        }
    }
}
