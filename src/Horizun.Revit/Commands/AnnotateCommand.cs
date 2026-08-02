// -----------------------------------------------------------------------------
// Horizun Revit MCP - text, tags and dimensions with explicit references.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class AnnotateCommand : ICommand
    {
        public string Name => "horizun_annotate";
        public string Description => "Create text, tags and dimensions atomically and verify the committed annotations.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document; JArray actions = request["actions"] as JArray;
            if (actions == null || actions.Count == 0 || actions.Count > 1000) return CommandResult.Fail("actions must contain 1..1000 entries.");
            double scale; if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale)) return CommandResult.Fail("units must be mm, m or feet.");

            var plans = new List<Plan>(); var errors = new JArray();
            for (int i = 0; i < actions.Count; i++)
            {
                string error = null; Plan p = PlanAction(doc, i, actions[i] as JObject, scale, out error);
                if (p == null) errors.Add(new JObject { ["index"] = i, ["error"] = error ?? "entry is not an object" }); else plans.Add(p);
            }
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string hash = DocumentGate.PlanHash(request, "units", "actions");
            if (dry)
            {
                var result = new JObject { ["dry_run"] = true, ["valid"] = plans.Count, ["invalid"] = errors.Count,
                    ["errors"] = errors, ["plan"] = new JArray(plans.Select(p => new JObject { ["index"] = p.Index, ["operation"] = p.Operation, ["references"] = p.References?.Size })) };
                DocumentGate.StampConfirmation(result, gate, Name, hash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds views, geometry, text, targets and stable references" : "no usable token while invalid");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0) return CommandResult.Fail("Invalid annotations; nothing ran: " + errors.ToString(Formatting.None));
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash); if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name); if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name"); if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: annotate";
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try { foreach (Plan p in plans) p.Created = Create(doc, p)?.Id; Guard.Commit(tx, txName); }
                catch (Exception ex) { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); return CommandResult.Fail("Atomic annotation failed: " + ex.Message + ". Everything was rolled back."); }
            }
            var rows = new JArray(); int verified = 0;
            foreach (Plan p in plans)
            {
                Element e = p.Created == null ? null : doc.GetElement(p.Created); bool ok = Verify(p, e); if (ok) verified++;
                rows.Add(new JObject { ["index"] = p.Index, ["operation"] = p.Operation, ["element_id"] = p.Created == null ? JValue.CreateNull() : new JValue(Rid.Value(p.Created)), ["verified"] = ok });
            }
            if (verified != plans.Count) return CommandResult.Fail("Committed, but annotation verification failed: " + rows.ToString(Formatting.None));
            return CommandResult.Ok(new JObject { ["transaction_status"] = "Committed", ["annotations_verified"] = verified, ["rows"] = rows });
        }

        private static Plan PlanAction(Document doc, int index, JObject a, double scale, out string error)
        {
            error = null; if (a == null) { error = "entry is not an object"; return null; }
            try
            {
                string op = (a.Value<string>("operation") ?? "").ToLowerInvariant(); View view = Need<View>(doc, a, "view_id");
                if (view.IsTemplate) throw new ArgumentException("view_id is a template");
                var p = new Plan { Index = index, Operation = op, View = view, Input = a, Scale = scale };
                if (op == "text")
                {
                    p.Point = Point(a["point"], scale, false); p.Text = a.Value<string>("text");
                    if (string.IsNullOrEmpty(p.Text)) throw new ArgumentException("text is required");
                    p.Type = Need<TextNoteType>(doc, a, "text_type_id");
                }
                else if (op == "tag")
                {
                    p.Point = Point(a["point"], scale, false); p.Target = Need<Element>(doc, a, "element_id");
                }
                else if (op == "dimension")
                {
                    XYZ x = Point(a["line_start"], scale, true), y = Point(a["line_end"], scale, true);
                    if (x.DistanceTo(y) < 1e-9) throw new ArgumentException("dimension line endpoints must differ"); p.Line = Line.CreateBound(x, y);
                    JArray refs = a["references"] as JArray; if (refs == null || refs.Count < 2) throw new ArgumentException("references needs at least two stable reference strings");
                    p.References = new ReferenceArray();
                    foreach (JToken token in refs) p.References.Append(Reference.ParseFromStableRepresentation(doc, token.Value<string>()));
                    if (a["dimension_type_id"] != null) p.Type = Need<DimensionType>(doc, a, "dimension_type_id");
                }
                else throw new ArgumentException("unsupported operation '" + op + "'");
                return p;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }
        private static Element Create(Document doc, Plan p)
        {
            if (p.Operation == "text") return TextNote.Create(doc, p.View.Id, p.Point, p.Text, p.Type.Id);
            if (p.Operation == "tag")
            {
                TagMode mode = TagMode.TM_ADDBY_CATEGORY; string m = (p.Input.Value<string>("tag_mode") ?? "by_category").ToLowerInvariant();
                if (m == "multi_category") mode = TagMode.TM_ADDBY_MULTICATEGORY; else if (m == "material") mode = TagMode.TM_ADDBY_MATERIAL;
                TagOrientation orientation = (p.Input.Value<string>("orientation") ?? "horizontal").ToLowerInvariant() == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
                return IndependentTag.Create(doc, p.View.Id, new Reference(p.Target), p.Input.Value<bool?>("add_leader") == true, mode, orientation, p.Point);
            }
            DimensionType type = p.Type as DimensionType;
            return type == null ? doc.Create.NewDimension(p.View, p.Line, p.References) : doc.Create.NewDimension(p.View, p.Line, p.References, type);
        }
        private static bool Verify(Plan p, Element e)
        {
            if (p.Operation == "text") return e is TextNote note && note.Text == p.Text;
            if (p.Operation == "tag") return e is IndependentTag tag && tag.GetTaggedLocalElementIds().Any(id => id == p.Target.Id);
            return e is Dimension dimension && dimension.References != null && dimension.References.Size == p.References.Size;
        }
        private static T Need<T>(Document d, JObject a, string f) where T : Element { long id = a.Value<long?>(f) ?? -1; if (!Rid.CanRepresent(id) || !(d.GetElement(Rid.Make(id)) is T e)) throw new ArgumentException(f + " must identify " + typeof(T).Name); return e; }
        private static XYZ Point(JToken t, double s, bool z) { JArray a = t as JArray; if (a == null || a.Count < (z ? 3 : 2) || a.Count > 3) throw new ArgumentException("point/line coordinate has wrong length"); return new XYZ(a[0].Value<double>() * s, a[1].Value<double>() * s, (a.Count > 2 ? a[2].Value<double>() : 0) * s); }
        private static bool Scale(string u, out double s) { if (u == "feet") { s = 1; return true; } if (u == "m") { s = 1 / 0.3048; return true; } if (u == "mm") { s = 1 / 304.8; return true; } s = 0; return false; }
        private sealed class Plan { public int Index; public string Operation, Text; public View View; public JObject Input; public double Scale; public XYZ Point; public Element Target, Type; public Line Line; public ReferenceArray References; public ElementId Created; }
    }
}
