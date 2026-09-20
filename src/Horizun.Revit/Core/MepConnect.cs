// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// WHICH CONNECTOR DOES THE DRAWING MEAN?
//
// This file used to be bigger and most of it should not have existed. Joining two
// MEP connectors — checking the domain, the profile, the size, the coincidence and
// whether the end is already taken, then re-reading the result from the model —
// is `horizun_connect_mep`, which has done all of that since before this campaign
// and does it with a rehearsal, a rollback and a verified re-read. A second
// implementation would have been a second set of rules about when a connection is
// legitimate, and the two would have drifted.
//
// What was genuinely missing is the step before it, and it is the only thing left
// here: `horizun_connect_mep` takes an element and a CONNECTOR INDEX, and a
// drawing gives a POINT. Turning one into the other is where a conversion route
// goes wrong quietly.
//
// WHY "THE NEAREST" IS NOT ENOUGH. Nearest is fine until two connectors are
// equally near — a tee whose branch and run outlets sit a few millimetres apart on
// a small fitting — and then the answer is whichever floating-point comparison
// won. Joining the wrong outlet of a tee builds a model that looks right in every
// view and flows wrong. So nearness is MEASURED, a second candidate inside the
// ambiguity band is a REFUSAL naming both distances, and ties are broken by the
// connector's own id so that the answer never depends on the order Revit happened
// to enumerate.
//
// AND NEARNESS IS IN PLAN, which is not a simplification — it is the only reading
// that is true. The point comes from a DRAWING, and a plan drawing's junctions
// carry the drawing's own Z, which is zero; the element built from one sits at
// whatever height its rule gave it. Ranking in three dimensions put every run at
// +2400 mm two and a half metres from its own junction and refused the lot.
//
// THE RISER IS WHAT MAKES THAT MORE THAN A ONE-WORD CHANGE. A vertical pipe has
// both connectors at the same place in plan and different heights, so in plan they
// are exactly equally near and the ambiguity band refuses — which is CORRECT, and
// is the honest answer: a plan cannot say which end of a riser its junction means.
// The refusal names both heights so that the reader sees the question is vertical.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One connector this bridge picked out, with the evidence for the pick.</summary>
    public sealed class MepConnectorPick
    {
        public Connector Connector;

        /// <summary>The connector's own index, which is what horizun_connect_mep takes.</summary>
        public int ConnectorId = -1;

        /// <summary>Distance IN PLAN from the point the drawing named. See <see cref="MepConnect.Nearest"/>.</summary>
        public double DistanceMm;

        /// <summary>
        /// How far the chosen connector sits above or below the point the drawing
        /// named. Not part of the match - a plan drawing carries no height - but
        /// reported, because a connector 2400 mm up is the right one and a
        /// connector 2400 mm up when the rule said 400 is a finding.
        /// </summary>
        public double HeightDifferenceMm;

        /// <summary>The runner-up's distance, when there was one. Null when this was the only candidate.</summary>
        public double? RunnerUpMm;
        public string Refusal;
        public JObject RefusalDetail;

        public bool Found => Connector != null && Refusal == null;
    }

    public static class MepConnect
    {
        /// <summary>
        /// How much closer the winner must be than the runner-up before the pick
        /// is unambiguous, as a fraction of the tolerance. A second connector
        /// inside this band makes the pick a refusal.
        /// </summary>
        public const double AmbiguityBand = 0.5;

        /// <summary>Every connector an element exposes, whatever kind of element it is.</summary>
        public static List<Connector> ConnectorsOf(Element e)
        {
            var found = new List<Connector>();
            if (e == null) return found;

            ConnectorManager manager = null;
            var curve = e as MEPCurve;
            if (curve != null) { try { manager = curve.ConnectorManager; } catch { } }

            if (manager == null)
            {
                var fi = e as FamilyInstance;
                if (fi != null)
                {
                    try
                    {
                        MEPModel model = fi.MEPModel;
                        if (model != null) manager = model.ConnectorManager;
                    }
                    catch { }
                }
            }

            if (manager == null) return found;
            try
            {
                foreach (Connector c in manager.Connectors) if (c != null) found.Add(c);
            }
            catch { }
            return found;
        }

        /// <summary>
        /// The connector of <paramref name="e"/> nearest <paramref name="nearMm"/>
        /// IN PLAN, or a refusal that says why there isn't one.
        ///
        /// In plan, because <paramref name="nearMm"/> comes from a DRAWING: its
        /// junctions carry the drawing's own Z and the element built from one sits
        /// at the height its rule gave it. The height difference is reported and
        /// never matched on.
        ///
        /// Reads only. What to DO with the pick is horizun_connect_mep's business.
        /// </summary>
        public static MepConnectorPick Nearest(Element e, CadPoint nearMm, double toleranceMm)
        {
            var pick = new MepConnectorPick();
            List<Connector> all = ConnectorsOf(e);
            if (all.Count == 0)
            {
                pick.Refusal = "element_has_no_connectors";
                pick.RefusalDetail = new JObject
                {
                    ["element"] = e == null ? (JToken)JValue.CreateNull() : Rid.Value(e.Id),
                    ["means"] = "this element exposes no connector manager at all. A pipe or duct always has " +
                                "one; a family instance only has one when its family was built with " +
                                "connectors, and a generic model drawn to look like a fixture has none."
                };
                return pick;
            }

            // RANKED IN PLAN, because the point comes from a DRAWING and a drawing
            // is a plan: its junctions carry the drawing's own Z - zero - while the
            // element built from one sits at the height its rule gave it. Ranking in
            // three dimensions put every run at +2400 out of tolerance from its own
            // junction, and refused the lot.
            var ranked = new List<KeyValuePair<Connector, double>>();
            var heightOf = new Dictionary<Connector, double>();
            foreach (Connector c in all)
            {
                XYZ origin;
                try { origin = c.Origin; } catch { continue; }
                if (origin == null) continue;
                double dx = CadUnits.FeetToMm(origin.X) - nearMm.X;
                double dy = CadUnits.FeetToMm(origin.Y) - nearMm.Y;
                ranked.Add(new KeyValuePair<Connector, double>(c, Math.Sqrt(dx * dx + dy * dy)));
                heightOf[c] = CadUnits.FeetToMm(origin.Z) - nearMm.Z;
            }
            // Ties are broken by the connector's own id, so the answer does not
            // depend on the order Revit happened to enumerate.
            ranked = ranked.OrderBy(k => k.Value).ThenBy(k => SafeId(k.Key)).ToList();

            if (ranked.Count == 0 || ranked[0].Value > toleranceMm)
            {
                pick.Refusal = "no_connector_within_tolerance";
                pick.RefusalDetail = new JObject
                {
                    ["element"] = Rid.Value(e.Id),
                    ["nearest_in_plan_mm"] = ranked.Count == 0
                        ? (JToken)JValue.CreateNull()
                        : Math.Round(ranked[0].Value, 3, MidpointRounding.AwayFromZero),
                    ["tolerance_mm"] = toleranceMm,
                    ["connector_count"] = all.Count,
                    ["means"] = "the element is not where the plan expected its end to be, or its nearest " +
                                "connector belongs to the other end. Nothing was joined."
                };
                return pick;
            }

            pick.Connector = ranked[0].Key;
            pick.DistanceMm = ranked[0].Value;
            pick.HeightDifferenceMm = heightOf[ranked[0].Key];
            pick.ConnectorId = SafeIdValue(ranked[0].Key);
            if (ranked.Count > 1) pick.RunnerUpMm = ranked[1].Value;

            // TWO EQUALLY NEAR IS A REFUSAL, not a coin toss.
            if (ranked.Count > 1 && ranked[1].Value - ranked[0].Value < toleranceMm * AmbiguityBand)
            {
                pick.Refusal = "ambiguous_connector";
                double heightApart = Math.Abs(heightOf[ranked[0].Key] - heightOf[ranked[1].Key]);
                pick.RefusalDetail = new JObject
                {
                    ["element"] = Rid.Value(e.Id),
                    ["winner_in_plan_mm"] = Math.Round(ranked[0].Value, 3, MidpointRounding.AwayFromZero),
                    ["runner_up_in_plan_mm"] = Math.Round(ranked[1].Value, 3, MidpointRounding.AwayFromZero),
                    ["band_mm"] = Math.Round(toleranceMm * AmbiguityBand, 3, MidpointRounding.AwayFromZero),
                    ["height_apart_mm"] = Math.Round(heightApart, 3, MidpointRounding.AwayFromZero),
                    ["means"] = heightApart > toleranceMm
                        ? "two of this element's connectors are at the same place IN PLAN and " +
                          heightApart.ToString("0.#", CultureInfo.InvariantCulture) + " mm apart vertically. " +
                          "That is a RISER, and a plan drawing genuinely cannot say which of its two ends a " +
                          "junction means - so the question is vertical and nobody can answer it from this " +
                          "drawing. Name the connector, or read the height off a riser diagram."
                        : "two of this element's connectors are almost the same distance from the point. " +
                          "On a fitting that is the run outlet and the branch outlet, and joining the " +
                          "wrong one builds a network that looks right and flows wrong. Nothing was joined."
                };
                pick.Connector = null;
                pick.ConnectorId = -1;
            }
            return pick;
        }

        private static string SafeId(Connector c)
        {
            try { return c.Id.ToString("D6", CultureInfo.InvariantCulture); }
            catch { return ""; }
        }

        private static int SafeIdValue(Connector c)
        {
            try { return c.Id; }
            catch { return -1; }
        }
    }
}
