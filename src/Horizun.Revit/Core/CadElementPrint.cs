// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// "IS THIS ELEMENT STILL AS IT WAS WHEN THE PLAN WAS MADE?"
//
// Short, cheap and about the things an update would take away: where the element
// is, what type it is, and - for a duct or a pipe - the section it carries. A
// person who moved a run, resized it or changed its type between a plan and its
// apply has done work, and an apply that re-shapes it on the plan's say-so
// destroys that work without ever mentioning it.
//
// Not a checksum of the element: it names the four things this bridge's update
// actions actually change. A parameter somebody edited that no action here touches
// does not make a plan stale, and calling it stale would train people to ignore
// the refusal.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadElementPrint
    {
        /// <summary>The element as the update cares about it, or null when it cannot be read.</summary>
        public static string Of(Document doc, long elementId)
        {
            try
            {
                if (doc == null || !Rid.CanRepresent(elementId)) return null;
                Element e = doc.GetElement(Rid.Make(elementId));
                if (e == null) return null;
                var parts = new List<string> { "t=" + Rid.Value(e.GetTypeId()).ToString(CultureInfo.InvariantCulture) };
                var lc = e.Location as LocationCurve;
                if (lc?.Curve != null)
                {
                    XYZ a = lc.Curve.GetEndPoint(0), b = lc.Curve.GetEndPoint(1);
                    parts.Add(Pt(a) + ">" + Pt(b));
                }
                else if (e.Location is LocationPoint lp && lp.Point != null)
                    parts.Add(Pt(lp.Point));
                foreach (Connector c in MepConnect.ConnectorsOf(e).Take(1))
                {
                    try { parts.Add("w=" + Mm(c.Width) + ",h=" + Mm(c.Height)); }
                    catch { try { parts.Add("d=" + Mm(c.Radius * 2)); } catch { } }
                }
                return "el:" + CadIdentity.Sha256Hex(string.Join("|", parts)).Substring(0, 16);
            }
            catch { return null; }
        }

        /// <summary>Every element these actions would re-shape, delete or write to, with its print now.</summary>
        public static JArray TouchedBy(Document doc, JArray actions)
        {
            var seen = new List<long>();
            foreach (JObject a in (actions ?? new JArray()).OfType<JObject>())
            {
                var args = a["arguments"] as JObject;
                if (args == null) continue;
                foreach (JObject op in (args["operations"] as JArray ?? new JArray()).OfType<JObject>())
                    foreach (JToken id in (op["element_ids"] as JArray ?? new JArray()))
                        Remember(seen, id);
                foreach (JToken id in (args["ids"] as JArray ?? new JArray()))
                    Remember(seen, id);
                foreach (JObject row in (args["elements"] as JArray ?? new JArray()).OfType<JObject>())
                    Remember(seen, row["element_id"]);
            }
            var rows = new JArray();
            foreach (long id in seen)
            {
                string print = Of(doc, id);
                if (print == null) continue;
                rows.Add(new JObject { ["element_id"] = id, ["fingerprint"] = print });
            }
            return rows;
        }

        /// <summary>The same elements, read NOW, for the apply to compare against.</summary>
        public static Dictionary<long, string> ReadAgain(Document doc, JArray touched)
        {
            var map = new Dictionary<long, string>();
            foreach (JObject t in (touched ?? new JArray()).OfType<JObject>())
            {
                long id = t.Value<long?>("element_id") ?? -1;
                string print = Of(doc, id);
                if (print != null) map[id] = print;
            }
            return map;
        }

        private static void Remember(List<long> seen, JToken id)
        {
            long v;
            if (id == null || !long.TryParse(id.ToString(), out v)) return;
            if (!seen.Contains(v)) seen.Add(v);
        }

        private static string Pt(XYZ p) => Mm(p.X) + "," + Mm(p.Y) + "," + Mm(p.Z);

        private static string Mm(double feet) =>
            Math.Round(CadUnits.FeetToMm(feet), 2).ToString("0.##", CultureInfo.InvariantCulture);
    }
}
