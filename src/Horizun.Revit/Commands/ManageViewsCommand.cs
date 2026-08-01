// -----------------------------------------------------------------------------
// Horizun Revit MCP - views and sheets as one dependency-aware atomic batch.
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
    public sealed class ManageViewsCommand : ICommand
    {
        public string Name => "horizun_manage_views";
        public string Description => "Create views/sheets and place views/schedules in one verified transaction.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            JArray input = request["actions"] as JArray;
            if (input == null || input.Count == 0 || input.Count > 500)
                return CommandResult.Fail("actions must contain 1..500 entries.");
            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            var knownKeys = new HashSet<string>(StringComparer.Ordinal);
            var errors = new JArray();
            var plans = new List<JObject>();
            for (int i = 0; i < input.Count; i++)
            {
                JObject a = input[i] as JObject;
                string error = Validate(doc, a, knownKeys);
                if (error != null) errors.Add(new JObject { ["index"] = i, ["error"] = error });
                else
                {
                    string key = a.Value<string>("key");
                    if (!string.IsNullOrWhiteSpace(key)) knownKeys.Add(key);
                    plans.Add(new JObject { ["index"] = i, ["operation"] = a.Value<string>("operation"), ["key"] = key });
                }
            }
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "actions");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["actions"] = input.Count,
                    ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors, ["plan"] = new JArray(plans),
                    ["note"] = "Nothing was created or changed. Aliases are resolved in action order."
                };
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds the ordered dependency graph" : "no usable token is issued while an action is invalid");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0) return CommandResult.Fail("Invalid action graph; nothing ran: " + errors.ToString(Formatting.None));
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: manage views and sheets";
            var aliases = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            var applied = new List<Applied>();
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    for (int i = 0; i < input.Count; i++)
                    {
                        JObject action = (JObject)input[i];
                        Element result = Apply(doc, action, aliases, scale);
                        string key = action.Value<string>("key");
                        if (!string.IsNullOrWhiteSpace(key) && result != null) aliases[key] = result.Id;
                        applied.Add(new Applied { Index = i, Operation = action.Value<string>("operation"), Id = result?.Id,
                            TargetId = TargetId(doc, action, aliases) });
                    }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    return CommandResult.Fail("Atomic views/sheets batch failed: " + ex.Message + ". Everything in it was rolled back.");
                }
            }

            var rows = new JArray(); int verified = 0;
            foreach (Applied a in applied)
            {
                Element e = a.Id == null ? null : doc.GetElement(a.Id);
                bool ok = Verify(doc, a, e);
                if (ok) verified++;
                rows.Add(new JObject
                {
                    ["index"] = a.Index, ["operation"] = a.Operation,
                    ["element_id"] = a.Id == null ? JValue.CreateNull() : new JValue(Rid.Value(a.Id)),
                    ["present_after_commit"] = e != null, ["verified"] = ok, ["actual_class"] = e?.GetType().Name
                });
            }
            if (verified != applied.Count)
                return CommandResult.Fail("The transaction committed, but only " + verified + " of " + applied.Count +
                    " actions passed post-commit verification. Inspect the model: " + rows.ToString(Formatting.None));
            return CommandResult.Ok(new JObject
            {
                ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["actions_verified"] = verified,
                ["aliases"] = new JObject(aliases.Select(kv => new JProperty(kv.Key, Rid.Value(kv.Value)))),
                ["rows"] = rows
            });
        }

        private static string Validate(Document doc, JObject a, HashSet<string> known)
        {
            if (a == null) return "action is not an object";
            string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
            string key = a.Value<string>("key");
            if (!string.IsNullOrWhiteSpace(key) && known.Contains(key)) return "key '" + key + "' is duplicated";
            try
            {
                switch (op)
                {
                    case "create_floor_plan": Need<Level>(doc, a, "level_id"); OptionalViewFamilyType(doc, a, ViewFamily.FloorPlan); break;
                    case "create_3d": OptionalViewFamilyType(doc, a, ViewFamily.ThreeDimensional); break;
                    case "duplicate_view": Reference<View>(doc, a, "source_view_id", "source_view_key", known); break;
                    case "apply_template":
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        View template = Need<View>(doc, a, "template_view_id"); if (!template.IsTemplate) throw new ArgumentException("template_view_id is not a view template"); break;
                    case "create_sheet": if (a["title_block_type_id"] != null) Need<FamilySymbol>(doc, a, "title_block_type_id"); break;
                    case "place_view": Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known); Reference<View>(doc, a, "view_id", "view_key", known); Point(a["point"]); break;
                    case "place_schedule": Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known); Reference<ViewSchedule>(doc, a, "schedule_id", "schedule_key", known); Point(a["point"]); break;
                    default: return "unsupported operation '" + op + "'";
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static Element Apply(Document doc, JObject a, Dictionary<string, ElementId> aliases, double scale)
        {
            string op = a.Value<string>("operation").ToLowerInvariant();
            if (op == "create_floor_plan")
            {
                Level level = Need<Level>(doc, a, "level_id"); ViewFamilyType type = ResolveVft(doc, a, ViewFamily.FloorPlan);
                ViewPlan view = ViewPlan.Create(doc, type.Id, level.Id); SetName(view, a); return view;
            }
            if (op == "create_3d") { View3D view = View3D.CreateIsometric(doc, ResolveVft(doc, a, ViewFamily.ThreeDimensional).Id); SetName(view, a); return view; }
            if (op == "duplicate_view")
            {
                View source = Resolve<View>(doc, a, "source_view_id", "source_view_key", aliases);
                ViewDuplicateOption option;
                if (!Enum.TryParse(a.Value<string>("duplicate_option") ?? "Duplicate", true, out option)) throw new ArgumentException("invalid duplicate_option");
                View copy = doc.GetElement(source.Duplicate(option)) as View; SetName(copy, a); return copy;
            }
            if (op == "apply_template")
            {
                View view = Resolve<View>(doc, a, "view_id", "view_key", aliases); View template = Need<View>(doc, a, "template_view_id");
                view.ViewTemplateId = template.Id; return view;
            }
            if (op == "create_sheet")
            {
                ElementId title = ElementId.InvalidElementId;
                if (a["title_block_type_id"] != null) title = Need<FamilySymbol>(doc, a, "title_block_type_id").Id;
                ViewSheet sheet = ViewSheet.Create(doc, title);
                if (!string.IsNullOrWhiteSpace(a.Value<string>("name"))) sheet.Name = a.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(a.Value<string>("number"))) sheet.SheetNumber = a.Value<string>("number");
                return sheet;
            }
            ViewSheet targetSheet = Resolve<ViewSheet>(doc, a, "sheet_id", "sheet_key", aliases);
            XYZ point = Point(a["point"]) * scale;
            if (op == "place_view") return Viewport.Create(doc, targetSheet.Id, Resolve<View>(doc, a, "view_id", "view_key", aliases).Id, point);
            if (op == "place_schedule") return ScheduleSheetInstance.Create(doc, targetSheet.Id, Resolve<ViewSchedule>(doc, a, "schedule_id", "schedule_key", aliases).Id, point);
            throw new InvalidOperationException("unsupported operation");
        }

        private static bool Verify(Document doc, Applied a, Element e)
        {
            if (e == null) return false;
            switch (a.Operation.ToLowerInvariant())
            {
                case "create_floor_plan": return e is ViewPlan;
                case "create_3d": return e is View3D;
                case "duplicate_view": return e is View;
                case "apply_template": return e is View && a.TargetId != null && ((View)e).ViewTemplateId == a.TargetId;
                case "create_sheet": return e is ViewSheet;
                case "place_view": return e is Viewport;
                case "place_schedule": return e is ScheduleSheetInstance;
                default: return false;
            }
        }

        private static ElementId TargetId(Document d, JObject a, Dictionary<string, ElementId> aliases)
        {
            try
            {
                return string.Equals(a.Value<string>("operation"), "apply_template", StringComparison.OrdinalIgnoreCase)
                    ? Need<View>(d, a, "template_view_id").Id : null;
            }
            catch { return null; }
        }
        private static void SetName(View view, JObject a) { string name = a.Value<string>("name"); if (!string.IsNullOrWhiteSpace(name)) view.Name = name; }
        private static ViewFamilyType ResolveVft(Document doc, JObject a, ViewFamily family) => a["view_family_type_id"] != null
            ? Need<ViewFamilyType>(doc, a, "view_family_type_id")
            : new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == family)
              ?? throw new ArgumentException("no ViewFamilyType for " + family);
        private static void OptionalViewFamilyType(Document doc, JObject a, ViewFamily family) { ViewFamilyType t = ResolveVft(doc, a, family); if (t.ViewFamily != family) throw new ArgumentException("view_family_type_id is " + t.ViewFamily + ", not " + family); }
        private static T Need<T>(Document doc, JObject a, string field) where T : Element
        { long id = a.Value<long?>(field) ?? -1; if (!Rid.CanRepresent(id) || !(doc.GetElement(Rid.Make(id)) is T value)) throw new ArgumentException(field + " must identify a " + typeof(T).Name); return value; }
        private static void Reference<T>(Document doc, JObject a, string idField, string keyField, HashSet<string> known) where T : Element
        { string key = a.Value<string>(keyField); if (!string.IsNullOrWhiteSpace(key)) { if (!known.Contains(key)) throw new ArgumentException(keyField + " references unknown/prior key '" + key + "'"); return; } Need<T>(doc, a, idField); }
        private static T Resolve<T>(Document doc, JObject a, string idField, string keyField, Dictionary<string, ElementId> aliases) where T : Element
        { string key = a.Value<string>(keyField); if (!string.IsNullOrWhiteSpace(key)) { if (!aliases.TryGetValue(key, out ElementId id) || !(doc.GetElement(id) is T byKey)) throw new ArgumentException(keyField + " did not resolve to " + typeof(T).Name); return byKey; } return Need<T>(doc, a, idField); }
        private static XYZ Point(JToken token) { JArray p = token as JArray; if (p == null || p.Count < 2 || p.Count > 3) throw new ArgumentException("point needs XY or XYZ"); return new XYZ(p[0].Value<double>(), p[1].Value<double>(), p.Count > 2 ? p[2].Value<double>() : 0); }
        private static bool Scale(string u, out double s) { if (u == "feet") { s = 1; return true; } if (u == "m") { s = 1 / 0.3048; return true; } if (u == "mm") { s = 1 / 304.8; return true; } s = 0; return false; }
        private sealed class Applied { public int Index; public string Operation; public ElementId Id, TargetId; }
    }
}
