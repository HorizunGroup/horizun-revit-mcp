// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// WHAT A DIVISION COSTS, SAID IN ONE PLACE.
//
// A split and a merge are spread across the plan: an orphan here, two creates
// there, evidence keys naming each other. Everything needed to judge one is in
// that plan and nothing assembles it, so the reader has to reconstruct - for each
// change - which element survives, which ids appear and disappear, what is joined
// to the ends that move, and what they are being asked to decide.
//
// This assembles it. One row per division, naming:
//
//   origin          the element, the drawing entity it was built from, the issue
//                   of the drawing it was built under, and the line it was built to
//   pieces          each piece with its geometry, and WHICH one keeps the element
//   keeps/creates/  the element ids that survive, the pieces that become new
//   removes         elements, and what a decision would remove
//   id_substitutions   empty when an id survives. An operation that must replace an
//                   element says so here rather than promising an id it cannot keep
//   fittings        every fitting joined to the ends that move, by element and type
//   connections     the connectors that must survive the operation
//   decisions       what is being asked, and what answering it does
//   geometry        the line before, and the lines after
//
// It reports; it decides nothing. A row appears for a division the plan HOLDS and
// for one a caller has accepted, and says which it is.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadRevisionShapes
    {
        /// <summary>One row per split or merge in this plan, held or accepted.</summary>
        public static JArray Describe(Document doc, CadUpdate update, IList<CadAuditSubject> subjects)
        {
            var rows = new JArray();
            if (update == null) return rows;
            Func<long, CadAuditSubject> subjectOf = id =>
                subjects == null ? null : subjects.FirstOrDefault(s => s.ElementId == id);

            // ---- splits: the element is the origin, the pieces are the creates ----
            foreach (CadUpdateAction a in update.Actions)
            {
                var into = a.Evidence["may_have_been_split_into"] as JArray;
                var companions = a.Evidence["split_companions"] as JArray;
                if (into == null && companions == null) continue;
                bool accepted = companions != null;
                long originId = a.ElementId ?? -1;
                var pieceIds = new List<string>();
                if (into != null) pieceIds.AddRange(into.Select(x => (string)x));
                if (companions != null)
                {
                    pieceIds.AddRange(companions.Select(x => (string)x));
                    if (a.CandidateId != null) pieceIds.Insert(0, a.CandidateId);
                }
                rows.Add(Row(doc, update, subjectOf(originId), "split", originId, pieceIds, accepted,
                             a.Evidence.Value<string>("paired_on")));
            }

            // ---- merges: the create is the origin of the description, and the
            //      elements it covers are the parts ---------------------------------
            foreach (CadUpdateAction a in update.Actions)
            {
                var parts = a.Evidence["may_be_the_merge_of"] as JArray;
                if (parts == null) continue;
                long keeps = a.Evidence.Value<long?>("would_keep_element") ?? -1;
                bool accepted = a.Kind == "set_curve";
                var row = Row(doc, update, subjectOf(keeps), "merge", keeps,
                              new List<string> { a.CandidateId }, accepted, a.Evidence.Value<string>("paired_on"));
                var others = new JArray();
                foreach (long id in parts.Select(x => (long)x))
                {
                    if (id == keeps) continue;
                    CadUpdateAction part = update.Actions.FirstOrDefault(x => x.ElementId == id);
                    others.Add(new JObject
                    {
                        ["element_id"] = id,
                        ["still_stands"] = part == null || part.Kind != "delete",
                        ["decided"] = part == null ? null : part.Kind,
                        ["means"] = "a part the merged run now covers. Accepting the merge does NOT remove it: " +
                                    "that is a delete decision of its own, and the run would otherwise stand " +
                                    "inside the merged one."
                    });
                }
                row["parts_it_absorbs"] = others;
                row["removes"] = new JArray(others.OfType<JObject>()
                    .Where(x => x.Value<string>("decided") == "delete").Select(x => x["element_id"]));
                rows.Add(row);
            }
            return rows;
        }

        private static JObject Row(Document doc, CadUpdate update, CadAuditSubject origin, string what,
                                   long originId, IList<string> pieceIds, bool accepted, string why)
        {
            var pieces = new JArray();
            var creates = new JArray();
            foreach (string cid in pieceIds.Distinct(StringComparer.Ordinal))
            {
                CadUpdateAction p = update.Actions.FirstOrDefault(x =>
                    string.Equals(x.CandidateId, cid, StringComparison.Ordinal));
                if (p == null) continue;
                bool keepsIt = p.Kind == "set_curve" || p.Evidence["split_keeps_the_element"] != null;
                pieces.Add(new JObject
                {
                    ["candidate_id"] = cid,
                    ["geometry_mm"] = Line(p.Geometry),
                    ["length_mm"] = Length(p.Geometry),
                    ["keeps_the_element"] = keepsIt,
                    ["element_id"] = keepsIt ? (JToken)originId : JValue.CreateNull(),
                    ["automatic"] = p.Automatic,
                    ["held_because"] = p.Evidence.Value<string>("held_because")
                });
                if (!keepsIt) creates.Add(cid);
            }

            var row = new JObject
            {
                ["what"] = what,
                ["state"] = accepted ? "accepted" : "held",
                ["why"] = why,
                ["origin"] = OriginJson(origin, originId),
                ["pieces"] = pieces,
                ["keeps"] = new JArray(originId),
                ["creates"] = creates,
                ["removes"] = new JArray(),
                // AN ID IS NEVER PROMISED WHERE IT CANNOT BE KEPT. Both operations re-shape an element
                // that is already in the model, so no substitution arises here; the field exists so that
                // an operation which DOES have to replace one has somewhere to say so, and a reader
                // never has to infer it from silence.
                ["id_substitutions"] = new JArray(),
                ["id_substitutions_mean"] = "empty: this operation re-shapes an element that stands, so every " +
                                            "id in 'keeps' is the id it had. A substitution would be listed here.",
                ["geometry_before_mm"] = origin != null ? Line(AsBuilt(origin)) : null,
                ["geometry_after_mm"] = new JArray(pieces.OfType<JObject>().Select(x => x["geometry_mm"]))
            };

            JObject joined = JoinedTo(doc, originId);
            row["fittings_affected"] = joined["fittings"];
            row["connections_protected"] = joined["connections"];
            row["decisions_required"] = Decisions(what, accepted, originId, pieces);
            return row;
        }

        private static JObject OriginJson(CadAuditSubject s, long id) => new JObject
        {
            ["element_id"] = id,
            ["category"] = s?.Category,
            ["type"] = s?.TypeName,
            ["built_from_entity"] = s?.Provenance?.SemanticId,
            ["geometry_id"] = s?.Provenance?.GeometryId,
            ["rule_id"] = s?.Provenance?.RuleId,
            ["layer"] = s?.Provenance?.Layer,
            ["built_under"] = s?.Provenance == null ? null : new JObject
            {
                ["source_file_sha256"] = s.Provenance.SourceFileSha256,
                ["source_set_sha256"] = s.Provenance.SourceSetSha256,
                ["requirement_set_sha256"] = s.Provenance.RequirementSetSha256,
                ["means"] = "the issue of the drawing, and the rules, this element was built under. A division " +
                            "planned against a different issue is a different question."
            }
        };

        /// <summary>Every fitting joined to this element, and the connectors that must survive.</summary>
        private static JObject JoinedTo(Document doc, long elementId)
        {
            var fittings = new JArray();
            var connections = new JArray();
            try
            {
                Element e = doc == null || !Rid.CanRepresent(elementId) ? null : doc.GetElement(Rid.Make(elementId));
                if (e != null)
                {
                    foreach (Connector c in MepConnect.ConnectorsOf(e))
                    {
                        XYZ o;
                        try { o = c.Origin; } catch { continue; }
                        bool connected;
                        try { connected = c.IsConnected; } catch { connected = false; }
                        var holders = new JArray();
                        if (connected)
                        {
                            try
                            {
                                foreach (Connector r in c.AllRefs)
                                {
                                    if (r?.Owner == null || r.Owner.Id == e.Id) continue;
                                    var sym = doc.GetElement(r.Owner.GetTypeId()) as ElementType;
                                    holders.Add(Rid.Value(r.Owner.Id));
                                    fittings.Add(new JObject
                                    {
                                        ["element_id"] = Rid.Value(r.Owner.Id),
                                        ["what"] = sym == null ? r.Owner.Name : sym.FamilyName + ": " + sym.Name,
                                        ["at_mm"] = Point(o),
                                        ["why"] = "joined to an end of the element this division re-shapes: whatever " +
                                                  "moves that end moves this fitting or breaks this join."
                                    });
                                }
                            }
                            catch { }
                        }
                        connections.Add(new JObject
                        {
                            ["at_mm"] = Point(o),
                            ["connected"] = connected,
                            ["held_by"] = holders,
                            ["must_survive"] = connected,
                            ["means"] = connected
                                ? "this join exists now and the operation must leave it standing, or say it broke it."
                                : "a free end: nothing to protect here."
                        });
                    }
                }
            }
            catch { }
            return new JObject { ["fittings"] = fittings, ["connections"] = connections };
        }

        private static JArray Decisions(string what, bool accepted, long originId, JArray pieces)
        {
            var d = new JArray();
            if (!accepted)
            {
                d.Add(new JObject
                {
                    ["what"] = what == "split" ? "is this element the same run, divided?" : "are these elements one run now?",
                    ["how"] = "accept_pairings: [{ element_id: " + originId.ToString(CultureInfo.InvariantCulture) +
                              ", candidate_id: <the piece that keeps it> }]",
                    ["if_you_do"] = what == "split"
                        ? "the element is re-shaped to that piece and keeps its id; the other pieces are built new"
                        : "the element is re-shaped to the whole line and keeps its id; the other parts still stand",
                    ["if_you_do_not"] = "nothing is written: the pieces stay held, because building one now would " +
                                        "put a duct inside the element that stands"
                });
            }
            foreach (JObject p in pieces.OfType<JObject>())
            {
                string held = p.Value<string>("held_because");
                if (held == null || p.Value<bool?>("automatic") == true) continue;
                d.Add(new JObject
                {
                    ["what"] = "a piece is held: " + held,
                    ["how"] = "decide the element it stands on, or reject the pairing to say this piece is new",
                    ["candidate_id"] = p["candidate_id"]
                });
            }
            return d;
        }

        /// <summary>
        /// The line the element was BUILT to, from its own record, and where it is now when the record
        /// does not say. The two differ exactly when somebody moved it, which is the case this row has
        /// to make visible rather than smooth over.
        /// </summary>
        private static List<CadPoint> AsBuilt(CadAuditSubject s)
        {
            try
            {
                List<CadPoint> was = CadUpdateRules.AsBuiltOf(s?.Provenance);
                return was != null && was.Count >= 2 ? was : s?.Geometry;
            }
            catch { return s?.Geometry; }
        }

        private static JToken Line(IList<CadPoint> g) =>
            g == null || g.Count < 2 ? (JToken)JValue.CreateNull()
                : new JArray(new JArray(Math.Round(g[0].X, 1), Math.Round(g[0].Y, 1)),
                             new JArray(Math.Round(g[g.Count - 1].X, 1), Math.Round(g[g.Count - 1].Y, 1)));

        private static JToken Length(IList<CadPoint> g) =>
            g == null || g.Count < 2 ? (JToken)JValue.CreateNull()
                : Math.Round(g[0].PlanDistanceTo(g[g.Count - 1]), 1);

        private static JArray Point(XYZ p) =>
            new JArray(Math.Round(CadUnits.FeetToMm(p.X), 1), Math.Round(CadUnits.FeetToMm(p.Y), 1));
    }
}
