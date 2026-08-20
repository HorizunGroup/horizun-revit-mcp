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
        public string Description => "Create plans, sections, elevations, drafting/3D views and sheets in one verified transaction.";

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

            var knownKeys = new Dictionary<string, Type>(StringComparer.Ordinal);
            var errors = new JArray();
            var plans = new List<JObject>();
            // Every action's outcome, so the fallback is decided once over the whole
            // batch rather than granted because one entry was uncovered.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < input.Count; i++)
            {
                JObject a = input[i] as JObject;
                string reason = null;
                string error = Validate(doc, a, knownKeys, out reason);
                if (error != null)
                {
                    errors.Add(new JObject { ["index"] = i, ["error"] = error });
                    outcomes.Add(new ActionOutcome { Index = i, Error = error, UnsupportedReason = reason });
                }
                else
                {
                    string key = a.Value<string>("key");
                    if (!string.IsNullOrWhiteSpace(key)) knownKeys.Add(key, ResultType(a.Value<string>("operation")));
                    plans.Add(new JObject { ["index"] = i, ["operation"] = a.Value<string>("operation"), ["key"] = key });
                }
            }
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "actions");

            // ---- The MATERIALISED plan: what the batch's ID REFERENCES resolve to. ------
            // planHash binds the graph as written. But this batch builds documentation on
            // top of existing elements it names by id - a source view to duplicate, a
            // template to apply, a titleblock, a level for a new plan - and an id is a
            // pointer, not a meaning. Between rehearsal and apply the template can be
            // edited, the source view cropped, the titleblock swapped for another type
            // under the same id family. The plan records each referenced element's
            // UniqueId AND NAME as resolved now: the name is what the person read when
            // they approved, so a renamed reference refuses as stale even though the id
            // still resolves. In-batch aliases (keys) are not ambient state and carry no
            // drift; they are deliberately absent.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            for (int i = 0; i < input.Count; i++)
            {
                JObject a = input[i] as JObject;
                if (a == null) continue;
                string op = a.Value<string>("operation") ?? "";
                var row = new PlannedElement
                {
                    UniqueId = "action:" + i,
                    Category = op,
                    // apply_template and place_* CHANGE an existing object; the rest mint
                    // new ones. The distinction keeps the counts a person reads honest.
                    Action = (op == "apply_template" || op == "place_view" || op == "place_schedule")
                        ? PlannedAction.Modify : PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>()
                };
                foreach (string field in RefIdFields)
                {
                    long? id = a.Value<long?>(field);
                    if (id == null) continue;
                    row.BeforeValues[field] = SafePlanRef(doc, id.Value);
                }
                resolvedPlan.Elements.Add(row);
            }

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["actions"] = input.Count,
                    ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors, ["plan"] = new JArray(plans),
                    ["note"] = "Nothing was created or changed. Aliases are resolved in action order."
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                // Invalid entries make this a partial rehearsal, not a clean one: the token
                // below is already withheld for them, and a plan must read the same fact.
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0
                        ? "the token binds the ordered dependency graph AND what every referenced id resolves to " +
                          "right now, by identity and by name - a template edited into a different template, a " +
                          "renamed source view or a swapped titleblock refuses as a stale plan."
                        : "no usable token is issued while an action is invalid");
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so this
                // is the first call a caller makes; without the block here they got
                // success=true with invalid rows and no way to tell a capability gap
                // from a typo except by sending an apply they had no reason to send.
                return FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
                return FallbackDecision.Refuse(
                    "Invalid action graph; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            // Recomputed by THIS call's own resolution of the same references.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     resolvedPlan, null);
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
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Atomic views/sheets batch failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing in it was kept"));
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
            var mvResult = new JObject
            {
                ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["actions_verified"] = verified,
                ["aliases"] = new JObject(aliases.Select(kv => new JProperty(kv.Key, Rid.Value(kv.Value)))),
                ["rows"] = rows
            };
            // Reached only when every applied action passed its post-commit check.
            ApplicationOutcome.StampApplied(mvResult, ApplicationOutcome.Committed,
                                            applied.Count, verified, verified, 0, 0, 0);
            return CommandResult.Ok(mvResult);
        }

        /// <summary>
        /// Every request field that points at an EXISTING element. Kept as one list so the
        /// plan builder and the schema cannot quietly disagree about what is a reference.
        /// </summary>
        private static readonly string[] RefIdFields =
        {
            "level_id", "view_family_type_id", "plan_view_id", "source_view_id", "view_id",
            "template_view_id", "title_block_type_id", "sheet_id", "schedule_id"
        };

        /// <summary>
        /// Identity and name of a referenced element, guarded: a plan must never fail
        /// while MEASURING. A dangling id reads as "unresolved" - the validator has its
        /// own, louder opinion about those; the plan just has to be stable about them.
        /// </summary>
        private static string SafePlanRef(Document doc, long id)
        {
            try
            {
                if (!Rid.CanRepresent(id)) return "unresolved";
                Element e = doc.GetElement(Rid.Make(id));
                if (e == null) return "unresolved";
                string uid; try { uid = e.UniqueId ?? ""; } catch { uid = "<unreadable>"; }
                string name; try { name = e.Name ?? ""; } catch { name = "<unreadable>"; }
                return uid + "|" + name;
            }
            catch { return "<unreadable>"; }
        }

        private static string Validate(Document doc, JObject a, Dictionary<string, Type> known,
                                       out string unsupportedReason)
        {
            unsupportedReason = null;
            if (a == null) return "action is not an object";
            string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
            string key = a.Value<string>("key");
            if (!string.IsNullOrWhiteSpace(key) && known.ContainsKey(key)) return "key '" + key + "' is duplicated";
            try
            {
                switch (op)
                {
                    case "create_floor_plan": Need<Level>(doc, a, "level_id"); OptionalViewFamilyType(doc, a, ViewFamily.FloorPlan); break;
                    case "create_ceiling_plan": Need<Level>(doc, a, "level_id"); OptionalViewFamilyType(doc, a, ViewFamily.CeilingPlan); break;
                    case "create_structural_plan": Need<Level>(doc, a, "level_id"); OptionalViewFamilyType(doc, a, ViewFamily.StructuralPlan); break;
                    case "create_3d": OptionalViewFamilyType(doc, a, ViewFamily.ThreeDimensional); break;
                    case "create_drafting": OptionalViewFamilyType(doc, a, ViewFamily.Drafting); break;
                    case "create_section": OptionalViewFamilyType(doc, a, ViewFamily.Section); SectionBox(a, 1); break;
                    case "create_elevation":
                        OptionalViewFamilyType(doc, a, ViewFamily.Elevation); Need<ViewPlan>(doc, a, "plan_view_id"); Point(a["point"]);
                        int index = a.Value<int?>("elevation_index") ?? 0;
                        if (index < 0 || index > 3) throw new ArgumentException("elevation_index must be 0..3");
                        int markerScale = a.Value<int?>("marker_scale") ?? 100;
                        if (markerScale < 1 || markerScale > 24000) throw new ArgumentException("marker_scale must be 1..24000");
                        break;
                    case "duplicate_view":
                        Reference<View>(doc, a, "source_view_id", "source_view_key", known);
                        if (!Enum.TryParse(a.Value<string>("duplicate_option") ?? "Duplicate", true, out ViewDuplicateOption duplicate) ||
                            !Enum.IsDefined(typeof(ViewDuplicateOption), duplicate))
                            throw new ArgumentException("duplicate_option is invalid");
                        if (string.IsNullOrWhiteSpace(a.Value<string>("source_view_key")) &&
                            !Need<View>(doc, a, "source_view_id").CanViewBeDuplicated(duplicate))
                            throw new ArgumentException("source_view_id cannot be duplicated with " + duplicate);
                        break;
                    case "apply_template":
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        View template = Need<View>(doc, a, "template_view_id"); if (!template.IsTemplate) throw new ArgumentException("template_view_id is not a view template"); break;
                    case "create_sheet":
                        if (a["title_block_type_id"] != null)
                        {
                            FamilySymbol title = Need<FamilySymbol>(doc, a, "title_block_type_id");
                            if (!InCategory(title, BuiltInCategory.OST_TitleBlocks))
                                throw new ArgumentException("title_block_type_id must identify a title-block FamilySymbol");
                        }
                        break;
                    case "place_view": Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known); ReferenceViewportView(doc, a, known); Point(a["point"]); break;
                    case "place_schedule": Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known); Reference<ViewSchedule>(doc, a, "schedule_id", "schedule_key", known); Point(a["point"]); break;
                    default:
                        // A capability gap, not a fixable argument: this command implements a
                        // fixed set of documentation operations and this is not one of them.
                        unsupportedReason = FallbackSignal.ReasonUnsupportedOperation;
                        return "unsupported operation '" + op + "' - horizun_manage_views implements a fixed " +
                               "set of view, sheet and viewport operations. Nothing was written.";
                }
                return null;
            }
            catch (Exception ex)
            {
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return ex.Message;
            }
        }

        private static Element Apply(Document doc, JObject a, Dictionary<string, ElementId> aliases, double scale)
        {
            string op = a.Value<string>("operation").ToLowerInvariant();
            if (op == "create_floor_plan" || op == "create_ceiling_plan" || op == "create_structural_plan")
            {
                ViewFamily family = op == "create_floor_plan" ? ViewFamily.FloorPlan :
                    op == "create_ceiling_plan" ? ViewFamily.CeilingPlan : ViewFamily.StructuralPlan;
                Level level = Need<Level>(doc, a, "level_id"); ViewFamilyType type = ResolveVft(doc, a, family);
                ViewPlan view = ViewPlan.Create(doc, type.Id, level.Id); SetName(view, a); return view;
            }
            if (op == "create_3d") { View3D view = View3D.CreateIsometric(doc, ResolveVft(doc, a, ViewFamily.ThreeDimensional).Id); SetName(view, a); return view; }
            if (op == "create_drafting") { ViewDrafting view = ViewDrafting.Create(doc, ResolveVft(doc, a, ViewFamily.Drafting).Id); SetName(view, a); return view; }
            if (op == "create_section")
            {
                ViewSection view = ViewSection.CreateSection(doc, ResolveVft(doc, a, ViewFamily.Section).Id, SectionBox(a, scale));
                SetName(view, a); return view;
            }
            if (op == "create_elevation")
            {
                ViewFamilyType type = ResolveVft(doc, a, ViewFamily.Elevation);
                XYZ elevationPoint = Point(a["point"]) * scale;
                ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, type.Id, elevationPoint, a.Value<int?>("marker_scale") ?? 100);
                ViewSection view = marker.CreateElevation(doc, Need<ViewPlan>(doc, a, "plan_view_id").Id,
                    a.Value<int?>("elevation_index") ?? 0);
                SetName(view, a); return view;
            }
            if (op == "duplicate_view")
            {
                View source = Resolve<View>(doc, a, "source_view_id", "source_view_key", aliases);
                ViewDuplicateOption option;
                if (!Enum.TryParse(a.Value<string>("duplicate_option") ?? "Duplicate", true, out option) ||
                    !Enum.IsDefined(typeof(ViewDuplicateOption), option)) throw new ArgumentException("invalid duplicate_option");
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
                case "create_floor_plan": return e is ViewPlan floor && floor.ViewType == ViewType.FloorPlan;
                case "create_ceiling_plan": return e is ViewPlan ceiling && ceiling.ViewType == ViewType.CeilingPlan;
                case "create_structural_plan": return e is ViewPlan structural && structural.ViewType == ViewType.EngineeringPlan;
                case "create_3d": return e is View3D threeD && threeD.ViewType == ViewType.ThreeD;
                case "create_drafting": return e is ViewDrafting drafting && drafting.ViewType == ViewType.DraftingView;
                case "create_section": return e is ViewSection section && section.ViewType == ViewType.Section;
                case "create_elevation": return e is ViewSection elevation && elevation.ViewType == ViewType.Elevation;
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
        private static BoundingBoxXYZ SectionBox(JObject a, double scale)
        {
            XYZ start = Point(a["start"]) * scale;
            XYZ end = Point(a["end"]) * scale;
            XYZ horizontal = new XYZ(end.X - start.X, end.Y - start.Y, 0);
            if (horizontal.GetLength() < 1e-9) throw new ArgumentException("section start/end must define a non-zero horizontal line");
            if (Math.Abs(end.Z - start.Z) > 1e-6) throw new ArgumentException("section start/end must have the same Z elevation");
            double bottom = Finite(a.Value<double?>("bottom_offset") ?? -1000, "bottom_offset") * scale;
            double top = Finite(a.Value<double?>("top_offset") ?? 3000, "top_offset") * scale;
            double depth = Finite(a.Value<double?>("depth") ?? 5000, "depth") * scale;
            if (top <= bottom) throw new ArgumentException("top_offset must be greater than bottom_offset");
            if (depth <= 0) throw new ArgumentException("depth must be positive");
            XYZ x = horizontal.Normalize();
            XYZ y = XYZ.BasisZ;
            XYZ z = x.CrossProduct(y).Normalize();
            double half = horizontal.GetLength() / 2.0;
            var box = new BoundingBoxXYZ
            {
                Transform = new Transform(Transform.Identity)
                {
                    Origin = (start + end) / 2.0,
                    BasisX = x,
                    BasisY = y,
                    BasisZ = z
                },
                Min = new XYZ(-half, bottom, 0),
                Max = new XYZ(half, top, depth)
            };
            return box;
        }
        private static ViewFamilyType ResolveVft(Document doc, JObject a, ViewFamily family) => a["view_family_type_id"] != null
            ? Need<ViewFamilyType>(doc, a, "view_family_type_id")
            : new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == family)
              ?? throw new ArgumentException("no ViewFamilyType for " + family);
        private static void OptionalViewFamilyType(Document doc, JObject a, ViewFamily family) { ViewFamilyType t = ResolveVft(doc, a, family); if (t.ViewFamily != family) throw new ArgumentException("view_family_type_id is " + t.ViewFamily + ", not " + family); }
        private static T Need<T>(Document doc, JObject a, string field) where T : Element
        { long id = a.Value<long?>(field) ?? -1; if (!Rid.CanRepresent(id) || !(doc.GetElement(Rid.Make(id)) is T value)) throw new ArgumentException(field + " must identify a " + typeof(T).Name); return value; }
        private static void Reference<T>(Document doc, JObject a, string idField, string keyField, Dictionary<string, Type> known) where T : Element
        {
            string key = a.Value<string>(keyField);
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!known.TryGetValue(key, out Type actual)) throw new ArgumentException(keyField + " references unknown/prior key '" + key + "'");
                if (!typeof(T).IsAssignableFrom(actual))
                    throw new ArgumentException(keyField + " references key '" + key + "' whose result is " + actual.Name + ", not " + typeof(T).Name);
                return;
            }
            Need<T>(doc, a, idField);
        }
        private static void ReferenceViewportView(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string key = a.Value<string>("view_key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!known.TryGetValue(key, out Type actual)) throw new ArgumentException("view_key references unknown/prior key '" + key + "'");
                if (!typeof(View).IsAssignableFrom(actual) || typeof(ViewSchedule).IsAssignableFrom(actual) || typeof(ViewSheet).IsAssignableFrom(actual))
                    throw new ArgumentException("view_key must resolve to a non-schedule, non-sheet View suitable for a Viewport");
                return;
            }
            View view = Need<View>(doc, a, "view_id");
            if (view is ViewSchedule) throw new ArgumentException("view_id is a schedule; use place_schedule");
            if (view is ViewSheet) throw new ArgumentException("view_id is a sheet and cannot be placed in a Viewport");
        }
        private static T Resolve<T>(Document doc, JObject a, string idField, string keyField, Dictionary<string, ElementId> aliases) where T : Element
        { string key = a.Value<string>(keyField); if (!string.IsNullOrWhiteSpace(key)) { if (!aliases.TryGetValue(key, out ElementId id) || !(doc.GetElement(id) is T byKey)) throw new ArgumentException(keyField + " did not resolve to " + typeof(T).Name); return byKey; } return Need<T>(doc, a, idField); }
        private static XYZ Point(JToken token)
        {
            JArray p = token as JArray; if (p == null || p.Count < 2 || p.Count > 3) throw new ArgumentException("point needs XY or XYZ");
            return new XYZ(Finite(p[0].Value<double>(), "X"), Finite(p[1].Value<double>(), "Y"),
                Finite(p.Count > 2 ? p[2].Value<double>() : 0, "Z"));
        }
        private static bool Scale(string u, out double s) { if (u == "feet") { s = 1; return true; } if (u == "m") { s = 1 / 0.3048; return true; } if (u == "mm") { s = 1 / 304.8; return true; } s = 0; return false; }
        private static double Finite(double value, string field)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite"); return value; }
        private static Type ResultType(string operation)
        {
            switch ((operation ?? "").ToLowerInvariant())
            {
                case "create_floor_plan": case "create_ceiling_plan": case "create_structural_plan": return typeof(ViewPlan);
                case "create_3d": return typeof(View3D);
                case "create_drafting": return typeof(ViewDrafting);
                case "create_section": case "create_elevation": return typeof(ViewSection);
                case "duplicate_view": case "apply_template": return typeof(View);
                case "create_sheet": return typeof(ViewSheet);
                case "place_view": return typeof(Viewport);
                case "place_schedule": return typeof(ScheduleSheetInstance);
                default: return typeof(Element);
            }
        }
        private static bool InCategory(Element element, BuiltInCategory category)
        { return element?.Category != null && Rid.Value(element.Category.Id) == (long)category; }
        private sealed class Applied { public int Index; public string Operation; public ElementId Id, TargetId; }
    }
}
