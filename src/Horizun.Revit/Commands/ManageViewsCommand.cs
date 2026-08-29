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
                    Action = ModifyingOperations.Contains(op) ? PlannedAction.Modify : PlannedAction.Create,
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
                        if (result == null)
                            throw new InvalidOperationException(
                                "operation '" + action.Value<string>("operation") + "' returned no element. " +
                                "The whole batch is being rolled back before commit.");
                        string key = action.Value<string>("key");
                        if (!string.IsNullOrWhiteSpace(key)) aliases[key] = result.Id;
                        applied.Add(new Applied { Index = i, Operation = action.Value<string>("operation"), Id = result.Id,
                            TargetId = TargetId(doc, action, aliases), Action = action, Scale = scale,
                            Batch = input });
                    }
                    doc.Regenerate();
                    foreach (Applied action in applied)
                    {
                        Element reread = doc.GetElement(action.Id);
                        if (!Verify(doc, action, reread))
                            throw new InvalidOperationException(
                                "operation '" + action.Operation + "' failed verification while the transaction " +
                                "was still reversible. The whole batch is being rolled back before commit.");
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
        /// The operations that CHANGE an existing object rather than minting a new one.
        /// The distinction keeps the modify/create counts a person approves honest.
        /// place_view/place_schedule mint a Viewport/instance but their meaning to the
        /// approver is "this sheet changes", so they classify as modifications.
        /// </summary>
        private static readonly HashSet<string> ModifyingOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "apply_template", "place_view", "place_schedule",
            "convert_placeholder_sheet", "set_phase", "assign_scope_box", "set_view_range",
            "set_crop", "set_annotation_crop", "set_viewport_type", "align_viewports"
        };

        /// <summary>
        /// Every request field that points at an EXISTING element. Kept as one list so the
        /// plan builder and the schema cannot quietly disagree about what is a reference.
        /// </summary>
        private static readonly string[] RefIdFields =
        {
            "level_id", "view_family_type_id", "plan_view_id", "source_view_id", "view_id",
            "template_view_id", "title_block_type_id", "sheet_id", "schedule_id",
            "parent_view_id", "area_scheme_id", "phase_id", "scope_box_id", "viewport_id",
            "source_sheet_id", "viewport_type_id", "anchor_viewport_id",
            "cut_level_id", "top_level_id", "bottom_level_id", "view_depth_level_id"
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
                        if (a["rotation"] != null)
                        {
                            double rot = Finite(a.Value<double>("rotation"), "rotation");
                            if (rot <= -360 || rot >= 360) throw new ArgumentException("rotation must be between -360 and 360 degrees exclusive");
                        }
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
                        // The number stays OPTIONAL here - Revit auto-numbers a sheet
                        // created without one - but a number the caller DID choose gets
                        // the same document-and-batch uniqueness check the placeholder
                        // and duplicate operations run. Run 2 measured the gap: a
                        // duplicate number sailed through this case's validation and
                        // would have died mid-transaction in Revit's own words.
                        if (!string.IsNullOrWhiteSpace(a.Value<string>("number")))
                            RequireUnusedSheetNumber(doc, a, known);
                        break;
                    case "place_view": Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known); ReferenceViewportView(doc, a, known); Point(a["point"]); break;
                    case "place_schedule": Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known); Reference<ViewSchedule>(doc, a, "schedule_id", "schedule_key", known); Point(a["point"]); break;
                    case "create_area_plan":
                        Need<Level>(doc, a, "level_id");
                        if (!(Need<Element>(doc, a, "area_scheme_id") is AreaScheme))
                            throw new ArgumentException("area_scheme_id must identify an AreaScheme");
                        break;
                    case "create_callout":
                        Reference<View>(doc, a, "parent_view_id", "parent_view_key", known);
                        Point(a["start"]); Point(a["end"]);
                        if (a["view_family_type_id"] != null) Need<ViewFamilyType>(doc, a, "view_family_type_id");
                        break;
                    case "create_placeholder_sheet":
                        RequireUnusedSheetNumber(doc, a, known);
                        break;
                    case "convert_placeholder_sheet":
                    {
                        FamilySymbol convTitle = Need<FamilySymbol>(doc, a, "title_block_type_id");
                        if (!InCategory(convTitle, BuiltInCategory.OST_TitleBlocks))
                            throw new ArgumentException("title_block_type_id must identify a title-block FamilySymbol");
                        // A key may name a placeholder created earlier in THIS batch, whose
                        // IsPlaceholder cannot be asked yet; an id must resolve to one now.
                        string convKey = a.Value<string>("sheet_key");
                        if (string.IsNullOrWhiteSpace(convKey))
                        {
                            ViewSheet placeholder = Need<ViewSheet>(doc, a, "sheet_id");
                            if (!placeholder.IsPlaceholder)
                                throw new ArgumentException("sheet_id " + a.Value<long>("sheet_id") + " is not a " +
                                    "placeholder sheet; converting a real sheet is not an operation");
                        }
                        else Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known);
                        break;
                    }
                    case "duplicate_sheet":
                    {
                        ViewSheet dupSource = Need<ViewSheet>(doc, a, "source_sheet_id");
                        if (dupSource.IsPlaceholder)
                            throw new ArgumentException("source_sheet_id is a placeholder; duplicate a real sheet");
                        RequireUnusedSheetNumber(doc, a, known);
                        bool withContent = a.Value<bool?>("with_content") ?? false;
                        if (withContent)
                        {
                            var schedules = new FilteredElementCollector(doc, dupSource.Id)
                                .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                                .Where(ssi => { try { return !ssi.IsTitleblockRevisionSchedule; } catch { return true; } })
                                .Select(ssi => Rid.Value(ssi.Id)).ToList();
                            if (schedules.Count > 0)
                                throw new ArgumentException("source sheet carries " + schedules.Count + " placed " +
                                    "schedule(s) (" + string.Join(", ", schedules) + "). Revit does not permit one " +
                                    "schedule on two sheets, and duplicating the schedules silently would double " +
                                    "the model's schedule census. Duplicate without content and place schedule " +
                                    "duplicates deliberately, or remove the placements first.");
                            foreach (Viewport vp in dupSource.GetAllViewports()
                                     .Select(id => doc.GetElement(id)).OfType<Viewport>())
                            {
                                View source = doc.GetElement(vp.ViewId) as View;
                                if (source == null || !source.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
                                    throw new ArgumentException("view '" + (source == null ? vp.ViewId.ToString() : source.Name) +
                                        "' on the source sheet cannot be duplicated WithDetailing, so the sheet " +
                                        "cannot be duplicated with content. Duplicate without content instead.");
                            }
                        }
                        break;
                    }
                    case "set_phase":
                    {
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        Element phase = Need<Element>(doc, a, "phase_id");
                        if (!(phase is Phase)) throw new ArgumentException("phase_id must identify a Phase");
                        break;
                    }
                    case "assign_scope_box":
                    {
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        Element box = Need<Element>(doc, a, "scope_box_id");
                        if (!InCategory(box, BuiltInCategory.OST_VolumeOfInterest))
                            throw new ArgumentException("scope_box_id must identify a scope box (Volume of Interest)");
                        break;
                    }
                    case "set_view_range":
                    {
                        string planKey = a.Value<string>("view_key");
                        if (string.IsNullOrWhiteSpace(planKey)) Need<ViewPlan>(doc, a, "view_id");
                        else
                        {
                            // MEASURED live (2026-08-26): duplicate_view declares its
                            // result as View in the key table, and a duplicated plan IS
                            // a ViewPlan at run time. Requiring the declared type to be
                            // ViewPlan refused the one composition this operation exists
                            // for; a View-typed key is accepted here and Apply's cast
                            // rolls the batch back by name if the runtime object is not
                            // actually a plan.
                            if (!known.TryGetValue(planKey, out Type actualKeyType))
                                throw new ArgumentException("view_key references unknown/prior key '" + planKey + "'");
                            if (!typeof(View).IsAssignableFrom(actualKeyType))
                                throw new ArgumentException("view_key references key '" + planKey +
                                    "' whose result is " + actualKeyType.Name + ", not a View");
                        }
                        bool any = false;
                        foreach (string plane in ViewRangePlanes)
                        {
                            bool hasLevel = a[plane + "_level_id"] != null, hasOffset = a[plane + "_offset"] != null;
                            if (hasLevel) { Need<Level>(doc, a, plane + "_level_id"); any = true; }
                            if (hasOffset) { Finite(a.Value<double>(plane + "_offset"), plane + "_offset"); any = true; }
                        }
                        if (!any)
                            throw new ArgumentException("set_view_range changes nothing: pass at least one of " +
                                "cut/top/bottom/view_depth _level_id or _offset. A no-op action in a verified " +
                                "batch can only be a mistake.");
                        break;
                    }
                    case "set_crop":
                    {
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        JArray cropBox = a["box"] as JArray;
                        if (cropBox == null || cropBox.Count != 4 ||
                            cropBox.Any(t => t.Type != JTokenType.Float && t.Type != JTokenType.Integer))
                            throw new ArgumentException("box must be [min_x, min_y, max_x, max_y] in the view's " +
                                "own right/up plane, in the request's units");
                        if (cropBox[2].Value<double>() <= cropBox[0].Value<double>() ||
                            cropBox[3].Value<double>() <= cropBox[1].Value<double>())
                            throw new ArgumentException("box max must exceed box min on both axes");
                        break;
                    }
                    case "set_annotation_crop":
                    {
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        if (a["active"] == null || a["active"].Type != JTokenType.Boolean)
                            throw new ArgumentException("active (boolean) is required");
                        if (a["annotation_offset"] != null)
                        {
                            double off = Finite(a.Value<double>("annotation_offset"), "annotation_offset");
                            if (off < 0) throw new ArgumentException("annotation_offset must be zero or greater");
                        }
                        break;
                    }
                    case "set_viewport_type":
                    {
                        Viewport typed = Need<Viewport>(doc, a, "viewport_id");
                        ElementType wanted = Need<ElementType>(doc, a, "viewport_type_id");
                        if (!typed.GetValidTypes().Contains(wanted.Id))
                            throw new ArgumentException("viewport_type_id " + Rid.Value(wanted.Id) + " is not a " +
                                "valid type for viewport " + Rid.Value(typed.Id) + " - Revit's own GetValidTypes " +
                                "does not offer it");
                        break;
                    }
                    case "align_viewports":
                    {
                        JArray ids = a["viewport_ids"] as JArray;
                        if (ids == null || ids.Count < 1 || ids.Any(t => t.Type != JTokenType.Integer))
                            throw new ArgumentException("viewport_ids must be a non-empty array of viewport ids");
                        var seenVp = new HashSet<long>();
                        foreach (JToken t in ids)
                        {
                            long vid = t.Value<long>();
                            if (!seenVp.Add(vid)) throw new ArgumentException("viewport_ids repeats " + vid);
                            if (!Rid.CanRepresent(vid) || !(doc.GetElement(Rid.Make(vid)) is Viewport))
                                throw new ArgumentException("viewport_ids entry " + vid + " is not a viewport");
                        }
                        string mode = (a.Value<string>("mode") ?? "").ToLowerInvariant();
                        if (Array.IndexOf(AlignModes, mode) < 0)
                            throw new ArgumentException("mode must be one of: " + string.Join(", ", AlignModes));
                        Viewport anchor = Need<Viewport>(doc, a, "anchor_viewport_id");
                        if (seenVp.Contains(Rid.Value(anchor.Id)))
                            throw new ArgumentException("anchor_viewport_id must not appear in viewport_ids: the " +
                                "anchor holds still and everything else moves to it");
                        break;
                    }
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
                double? rotation = a.Value<double?>("rotation");
                if (rotation != null && Math.Abs(rotation.Value) > 1e-9)
                {
                    // Orienting to a principal wall: the MARKER rotates about the vertical
                    // axis through its own point, and the elevation's view direction turns
                    // with it. The pre-rotation direction is recorded on the action so the
                    // post-commit check can verify the TURN rather than assume which way
                    // Revit points a fresh index.
                    a["__direction_before_rotation"] = new JArray(view.ViewDirection.X, view.ViewDirection.Y,
                                                                  view.ViewDirection.Z);
                    var axis = Line.CreateBound(elevationPoint, elevationPoint.Add(XYZ.BasisZ));
                    ElementTransformUtils.RotateElement(doc, marker.Id, axis,
                                                        rotation.Value * Math.PI / 180.0);
                }
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
            if (op == "create_area_plan")
            {
                ViewPlan view = ViewPlan.CreateAreaPlan(doc, Need<Element>(doc, a, "area_scheme_id").Id,
                                                        Need<Level>(doc, a, "level_id").Id);
                SetName(view, a); return view;
            }
            if (op == "create_callout")
            {
                View parent = Resolve<View>(doc, a, "parent_view_id", "parent_view_key", aliases);
                ViewFamilyType vft = a["view_family_type_id"] != null
                    ? Need<ViewFamilyType>(doc, a, "view_family_type_id")
                    : ResolveVft(doc, a, ViewFamily.Detail);
                View callout = ViewSection.CreateCallout(doc, parent.Id, vft.Id,
                                                         Point(a["start"]) * scale, Point(a["end"]) * scale);
                SetName(callout, a); return callout;
            }
            if (op == "create_placeholder_sheet")
            {
                ViewSheet placeholder = ViewSheet.CreatePlaceholder(doc);
                if (!string.IsNullOrWhiteSpace(a.Value<string>("name"))) placeholder.Name = a.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(a.Value<string>("number"))) placeholder.SheetNumber = a.Value<string>("number");
                return placeholder;
            }
            if (op == "convert_placeholder_sheet")
            {
                ViewSheet placeholder = Resolve<ViewSheet>(doc, a, "sheet_id", "sheet_key", aliases);
                if (!placeholder.IsPlaceholder)
                    throw new InvalidOperationException("sheet '" + placeholder.SheetNumber + "' is not a " +
                        "placeholder (a key may have resolved to a real sheet); the batch is rolling back");
                placeholder.ConvertToRealSheet(Need<FamilySymbol>(doc, a, "title_block_type_id").Id);
                return placeholder;
            }
            if (op == "duplicate_sheet")
            {
                ViewSheet source = Need<ViewSheet>(doc, a, "source_sheet_id");
                // The new sheet takes the SOURCE's title block type: a duplicate that
                // silently swapped title blocks would not be a duplicate.
                ElementId titleType = ElementId.InvalidElementId;
                FamilyInstance titleInstance = new FilteredElementCollector(doc, source.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>().FirstOrDefault();
                if (titleInstance != null) titleType = titleInstance.GetTypeId();
                ViewSheet copy = ViewSheet.Create(doc, titleType);
                copy.SheetNumber = a.Value<string>("number");
                copy.Name = string.IsNullOrWhiteSpace(a.Value<string>("name")) ? source.Name : a.Value<string>("name");
                if (a.Value<bool?>("with_content") ?? false)
                {
                    foreach (Viewport vp in source.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>()
                             .OrderBy(v => Rid.Value(v.Id)))
                    {
                        View sourceView = (View)doc.GetElement(vp.ViewId);
                        var duplicated = (View)doc.GetElement(sourceView.Duplicate(ViewDuplicateOption.WithDetailing));
                        Viewport placed = Viewport.Create(doc, copy.Id, duplicated.Id, vp.GetBoxCenter());
                        try { if (placed.GetTypeId() != vp.GetTypeId()) placed.ChangeTypeId(vp.GetTypeId()); }
                        catch { /* a viewport type invalid for the copy keeps the default; the box center is verified */ }
                    }
                }
                return copy;
            }
            if (op == "set_phase")
            {
                View phaseView = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                Parameter phaseParam = phaseView.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (phaseParam == null || phaseParam.IsReadOnly)
                    throw new InvalidOperationException("view '" + phaseView.Name + "' does not expose a writable " +
                        "Phase parameter; the batch is rolling back");
                phaseParam.Set(Need<Element>(doc, a, "phase_id").Id);
                return phaseView;
            }
            if (op == "assign_scope_box")
            {
                View scopedView = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                Parameter scopeParam = scopedView.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                if (scopeParam == null || scopeParam.IsReadOnly)
                    throw new InvalidOperationException("view '" + scopedView.Name + "' does not accept a scope " +
                        "box; the batch is rolling back");
                scopeParam.Set(Need<Element>(doc, a, "scope_box_id").Id);
                return scopedView;
            }
            if (op == "set_view_range")
            {
                ViewPlan plan = Resolve<ViewPlan>(doc, a, "view_id", "view_key", aliases);
                PlanViewRange range = plan.GetViewRange();
                foreach (string planeName in ViewRangePlanes)
                {
                    PlanViewPlane plane = PlaneOf(planeName);
                    long? levelId = a.Value<long?>(planeName + "_level_id");
                    if (levelId != null) range.SetLevelId(plane, Rid.Make(levelId.Value));
                    double? offset = a.Value<double?>(planeName + "_offset");
                    if (offset != null) range.SetOffset(plane, offset.Value * scale);
                }
                plan.SetViewRange(range);
                return plan;
            }
            if (op == "set_crop")
            {
                View cropView = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                JArray b = (JArray)a["box"];
                // The request's rectangle lives in the VIEW's right/up plane, anchored at
                // the view origin - the frame a caller can actually reason about. The
                // CropBox has its OWN transform, whose origin and basis Revit chooses,
                // so the corners are carried view-plane -> model -> crop-local rather
                // than written into Min/Max as if the two frames were the same. They
                // often are; "often" is not a property.
                BoundingBoxXYZ crop = cropView.CropBox;
                XYZ localA = CropLocal(cropView, crop, b[0].Value<double>() * scale, b[1].Value<double>() * scale);
                XYZ localB = CropLocal(cropView, crop, b[2].Value<double>() * scale, b[3].Value<double>() * scale);
                crop.Min = new XYZ(Math.Min(localA.X, localB.X), Math.Min(localA.Y, localB.Y), crop.Min.Z);
                crop.Max = new XYZ(Math.Max(localA.X, localB.X), Math.Max(localA.Y, localB.Y), crop.Max.Z);
                cropView.CropBox = crop;
                cropView.CropBoxActive = true;
                return cropView;
            }
            if (op == "set_annotation_crop")
            {
                View annView = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                Parameter annParam = annView.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (annParam == null || annParam.IsReadOnly)
                    throw new InvalidOperationException("view '" + annView.Name + "' does not expose the annotation " +
                        "crop; the batch is rolling back");
                annParam.Set(a.Value<bool>("active") ? 1 : 0);
                double? annOffset = a.Value<double?>("annotation_offset");
                if (annOffset != null)
                {
                    ViewCropRegionShapeManager manager = annView.GetCropRegionShapeManager();
                    double feet = annOffset.Value * scale;
                    manager.BottomAnnotationCropOffset = feet;
                    manager.TopAnnotationCropOffset = feet;
                    manager.LeftAnnotationCropOffset = feet;
                    manager.RightAnnotationCropOffset = feet;
                }
                return annView;
            }
            if (op == "set_viewport_type")
            {
                Viewport typed = Need<Viewport>(doc, a, "viewport_id");
                typed.ChangeTypeId(Need<ElementType>(doc, a, "viewport_type_id").Id);
                return typed;
            }
            if (op == "align_viewports")
            {
                Viewport anchor = Need<Viewport>(doc, a, "anchor_viewport_id");
                string mode = a.Value<string>("mode").ToLowerInvariant();
                Outline anchorOutline = anchor.GetBoxOutline();
                XYZ anchorCenter = anchor.GetBoxCenter();
                foreach (JToken t in (JArray)a["viewport_ids"])
                {
                    var vp = (Viewport)doc.GetElement(Rid.Make(t.Value<long>()));
                    Outline o = vp.GetBoxOutline();
                    XYZ c = vp.GetBoxCenter();
                    double dx = 0, dy = 0;
                    switch (mode)
                    {
                        case "center_x": dx = anchorCenter.X - c.X; break;
                        case "center_y": dy = anchorCenter.Y - c.Y; break;
                        case "center": dx = anchorCenter.X - c.X; dy = anchorCenter.Y - c.Y; break;
                        case "left": dx = anchorOutline.MinimumPoint.X - o.MinimumPoint.X; break;
                        case "right": dx = anchorOutline.MaximumPoint.X - o.MaximumPoint.X; break;
                        case "top": dy = anchorOutline.MaximumPoint.Y - o.MaximumPoint.Y; break;
                        case "bottom": dy = anchorOutline.MinimumPoint.Y - o.MinimumPoint.Y; break;
                    }
                    vp.SetBoxCenter(new XYZ(c.X + dx, c.Y + dy, c.Z));
                }
                return anchor;
            }
            ViewSheet targetSheet = Resolve<ViewSheet>(doc, a, "sheet_id", "sheet_key", aliases);
            XYZ point = Point(a["point"]) * scale;
            if (op == "place_view") return Viewport.Create(doc, targetSheet.Id, Resolve<View>(doc, a, "view_id", "view_key", aliases).Id, point);
            if (op == "place_schedule") return ScheduleSheetInstance.Create(doc, targetSheet.Id, Resolve<ViewSchedule>(doc, a, "schedule_id", "schedule_key", aliases).Id, point);
            throw new InvalidOperationException("unsupported operation");
        }

        /// <summary>
        /// A point given in the view's right/up plane (anchored at the view origin),
        /// expressed in the crop box's own local frame.
        /// </summary>
        private static XYZ CropLocal(View view, BoundingBoxXYZ crop, double viewX, double viewY)
        {
            XYZ model = view.Origin.Add(view.RightDirection.Multiply(viewX)).Add(view.UpDirection.Multiply(viewY));
            return crop.Transform.Inverse.OfPoint(model);
        }

        /// <summary>The four planes set_view_range can move, in the request's field-prefix spelling.</summary>
        private static readonly string[] ViewRangePlanes = { "cut", "top", "bottom", "view_depth" };

        private static PlanViewPlane PlaneOf(string prefix)
        {
            switch (prefix)
            {
                case "cut": return PlanViewPlane.CutPlane;
                case "top": return PlanViewPlane.TopClipPlane;
                case "bottom": return PlanViewPlane.BottomClipPlane;
                default: return PlanViewPlane.ViewDepthPlane;
            }
        }

        /// <summary>The alignment vocabulary, closed. The anchor never moves.</summary>
        private static readonly string[] AlignModes =
            { "center", "center_x", "center_y", "left", "right", "top", "bottom" };

        /// <summary>
        /// A sheet number must be unique across the DOCUMENT and across the BATCH.
        /// Revit would refuse the second copy anyway - but mid-transaction, taking the
        /// whole batch down with an exception whose wording nobody chose. Checked here
        /// so the refusal happens before anything runs and names the collision.
        /// </summary>
        private static void RequireUnusedSheetNumber(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string number = a.Value<string>("number");
            if (string.IsNullOrWhiteSpace(number))
                throw new ArgumentException("number is required: a sheet without a number cannot enter a drawing " +
                                            "register, and Revit would invent one nobody chose.");
            foreach (ViewSheet existing in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                string existingNumber;
                try { existingNumber = existing.SheetNumber; } catch { continue; }
                if (string.Equals(existingNumber, number, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("sheet number '" + number + "' is already used by sheet " +
                        Rid.Value(existing.Id) + "; sheet numbers are unique in a Revit document.");
            }
            // The batch's own numbers: recorded in `known` under a reserved prefix so
            // two creates in one batch cannot collide either.
            string reserved = "sheet-number:" + number.ToLowerInvariant();
            if (known.ContainsKey(reserved))
                throw new ArgumentException("sheet number '" + number + "' appears twice in this batch.");
            known.Add(reserved, typeof(ViewSheet));
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
                case "create_elevation":
                {
                    if (!(e is ViewSection elevation) || elevation.ViewType != ViewType.Elevation) return false;
                    JArray before = a.Action["__direction_before_rotation"] as JArray;
                    double? rotation = a.Action.Value<double?>("rotation");
                    if (before == null || rotation == null) return true;
                    double angle = rotation.Value * Math.PI / 180.0;
                    double cos = Math.Cos(angle), sin = Math.Sin(angle);
                    double bx = before[0].Value<double>(), by = before[1].Value<double>();
                    var want = new XYZ(bx * cos - by * sin, bx * sin + by * cos, before[2].Value<double>());
                    XYZ got;
                    try { got = elevation.ViewDirection; } catch { return false; }
                    return got.DistanceTo(want) <= 1e-6;
                }
                case "duplicate_view": return e is View;
                case "apply_template": return e is View && a.TargetId != null && ((View)e).ViewTemplateId == a.TargetId;
                case "create_sheet": return e is ViewSheet;
                case "place_view": return e is Viewport;
                case "place_schedule": return e is ScheduleSheetInstance;
                case "create_area_plan": return e is ViewPlan area && area.ViewType == ViewType.AreaPlan;
                case "create_callout":
                    // A callout of a plan is a Detail view; of a section, a Section or
                    // Detail. What is being verified is that a real graphical view came
                    // back, not which of Revit's spellings it wears.
                    return e is View calloutView && !(e is ViewSheet) && !(e is ViewSchedule) &&
                           !calloutView.IsTemplate;
                case "create_placeholder_sheet":
                {
                    if (!(e is ViewSheet ph) || !NumberMatches(ph, a.Action)) return false;
                    if (ph.IsPlaceholder) return true;
                    // MEASURED live (2026-08-26): a convert_placeholder_sheet LATER in
                    // the same batch flips IsPlaceholder before this verifier runs, and
                    // the first version of this check took the whole committed batch
                    // down for it. A conversion this batch itself asked for is the
                    // promised state, not a drift - so it is accepted, and ONLY it.
                    string myKey = a.Action.Value<string>("key");
                    if (a.Batch == null) return false;
                    for (int later = a.Index + 1; later < a.Batch.Count; later++)
                    {
                        var other = a.Batch[later] as JObject;
                        if (other == null) continue;
                        if (!string.Equals(other.Value<string>("operation"), "convert_placeholder_sheet",
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(myKey) &&
                            string.Equals(other.Value<string>("sheet_key"), myKey, StringComparison.Ordinal))
                            return true;
                        long? sheetId = other.Value<long?>("sheet_id");
                        if (sheetId != null && Rid.Value(e.Id) == sheetId.Value) return true;
                    }
                    return false;
                }
                case "convert_placeholder_sheet":
                    if (!(e is ViewSheet converted) || converted.IsPlaceholder) return false;
                    // The conversion's whole promise is the title block: re-read it.
                    return new FilteredElementCollector(doc, converted.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .Any(t => Rid.Value(t.GetTypeId()) == (a.Action.Value<long?>("title_block_type_id") ?? -1));
                case "duplicate_sheet":
                {
                    if (!(e is ViewSheet copy) || copy.IsPlaceholder) return false;
                    if (!NumberMatches(copy, a.Action)) return false;
                    if (!(a.Action.Value<bool?>("with_content") ?? false)) return true;
                    ViewSheet dupSource = doc.GetElement(Rid.Make(a.Action.Value<long>("source_sheet_id"))) as ViewSheet;
                    if (dupSource == null) return false;
                    var sourcePorts = dupSource.GetAllViewports().Select(id => doc.GetElement(id))
                        .OfType<Viewport>().OrderBy(v => Rid.Value(v.Id)).ToList();
                    var copyPorts = copy.GetAllViewports().Select(id => doc.GetElement(id))
                        .OfType<Viewport>().ToList();
                    if (copyPorts.Count != sourcePorts.Count) return false;
                    // Every source center must be matched by a copy center within the
                    // paper tolerance - the promise was "the same arrangement".
                    const double tolerance = 1e-6;
                    foreach (Viewport sp in sourcePorts)
                    {
                        XYZ want = sp.GetBoxCenter();
                        if (!copyPorts.Any(cp =>
                        {
                            XYZ got = cp.GetBoxCenter();
                            return Math.Abs(got.X - want.X) <= tolerance && Math.Abs(got.Y - want.Y) <= tolerance;
                        })) return false;
                    }
                    return true;
                }
                case "set_phase":
                {
                    Parameter phase = (e as View)?.get_Parameter(BuiltInParameter.VIEW_PHASE);
                    return phase != null && Rid.Value(phase.AsElementId()) == (a.Action.Value<long?>("phase_id") ?? -1);
                }
                case "assign_scope_box":
                {
                    Parameter scope = (e as View)?.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    return scope != null &&
                           Rid.Value(scope.AsElementId()) == (a.Action.Value<long?>("scope_box_id") ?? -1);
                }
                case "set_view_range":
                {
                    if (!(e is ViewPlan plan)) return false;
                    PlanViewRange range;
                    try { range = plan.GetViewRange(); } catch { return false; }
                    foreach (string planeName in ViewRangePlanes)
                    {
                        PlanViewPlane plane = PlaneOf(planeName);
                        long? levelId = a.Action.Value<long?>(planeName + "_level_id");
                        if (levelId != null && Rid.Value(range.GetLevelId(plane)) != levelId.Value) return false;
                        double? offset = a.Action.Value<double?>(planeName + "_offset");
                        if (offset != null && Math.Abs(range.GetOffset(plane) - offset.Value * a.Scale) > 1e-6)
                            return false;
                    }
                    return true;
                }
                case "set_crop":
                {
                    if (!(e is View cropped) || !cropped.CropBoxActive) return false;
                    JArray b = (JArray)a.Action["box"];
                    BoundingBoxXYZ crop;
                    try { crop = cropped.CropBox; } catch { return false; }
                    XYZ localA = CropLocal(cropped, crop, b[0].Value<double>() * a.Scale,
                                           b[1].Value<double>() * a.Scale);
                    XYZ localB = CropLocal(cropped, crop, b[2].Value<double>() * a.Scale,
                                           b[3].Value<double>() * a.Scale);
                    // The mm tolerance rather than 1e-6 ft: Revit is free to snap a crop
                    // to its own internal grid, and the caller's claim is "this rectangle",
                    // not "these exact doubles".
                    const double cropTolerance = 1.0 / 304.8;
                    return Math.Abs(crop.Min.X - Math.Min(localA.X, localB.X)) <= cropTolerance &&
                           Math.Abs(crop.Min.Y - Math.Min(localA.Y, localB.Y)) <= cropTolerance &&
                           Math.Abs(crop.Max.X - Math.Max(localA.X, localB.X)) <= cropTolerance &&
                           Math.Abs(crop.Max.Y - Math.Max(localA.Y, localB.Y)) <= cropTolerance;
                }
                case "set_annotation_crop":
                {
                    Parameter ann = (e as View)?.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                    if (ann == null || (ann.AsInteger() == 1) != a.Action.Value<bool>("active")) return false;
                    double? offset = a.Action.Value<double?>("annotation_offset");
                    if (offset == null) return true;
                    try
                    {
                        ViewCropRegionShapeManager manager = ((View)e).GetCropRegionShapeManager();
                        double want = offset.Value * a.Scale;
                        return Math.Abs(manager.BottomAnnotationCropOffset - want) <= 1e-6 &&
                               Math.Abs(manager.TopAnnotationCropOffset - want) <= 1e-6 &&
                               Math.Abs(manager.LeftAnnotationCropOffset - want) <= 1e-6 &&
                               Math.Abs(manager.RightAnnotationCropOffset - want) <= 1e-6;
                    }
                    catch { return false; }
                }
                case "set_viewport_type":
                    return e is Viewport typed &&
                           Rid.Value(typed.GetTypeId()) == (a.Action.Value<long?>("viewport_type_id") ?? -1);
                case "align_viewports":
                {
                    if (!(e is Viewport anchor)) return false;
                    Outline anchorOutline; XYZ anchorCenter;
                    try { anchorOutline = anchor.GetBoxOutline(); anchorCenter = anchor.GetBoxCenter(); }
                    catch { return false; }
                    string mode = a.Action.Value<string>("mode").ToLowerInvariant();
                    const double tolerance = 1e-6;
                    foreach (JToken t in (JArray)a.Action["viewport_ids"])
                    {
                        if (!(doc.GetElement(Rid.Make(t.Value<long>())) is Viewport vp)) return false;
                        Outline o; XYZ c;
                        try { o = vp.GetBoxOutline(); c = vp.GetBoxCenter(); } catch { return false; }
                        bool ok;
                        switch (mode)
                        {
                            case "center_x": ok = Math.Abs(c.X - anchorCenter.X) <= tolerance; break;
                            case "center_y": ok = Math.Abs(c.Y - anchorCenter.Y) <= tolerance; break;
                            case "center": ok = Math.Abs(c.X - anchorCenter.X) <= tolerance &&
                                                Math.Abs(c.Y - anchorCenter.Y) <= tolerance; break;
                            case "left": ok = Math.Abs(o.MinimumPoint.X - anchorOutline.MinimumPoint.X) <= tolerance; break;
                            case "right": ok = Math.Abs(o.MaximumPoint.X - anchorOutline.MaximumPoint.X) <= tolerance; break;
                            case "top": ok = Math.Abs(o.MaximumPoint.Y - anchorOutline.MaximumPoint.Y) <= tolerance; break;
                            case "bottom": ok = Math.Abs(o.MinimumPoint.Y - anchorOutline.MinimumPoint.Y) <= tolerance; break;
                            default: ok = false; break;
                        }
                        if (!ok) return false;
                    }
                    return true;
                }
                default: return false;
            }
        }

        private static bool NumberMatches(ViewSheet sheet, JObject action)
        {
            string wanted = action.Value<string>("number");
            if (string.IsNullOrWhiteSpace(wanted)) return true;
            try { return string.Equals(sheet.SheetNumber, wanted, StringComparison.Ordinal); }
            catch { return false; }
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
                case "create_area_plan": return typeof(ViewPlan);
                case "create_callout": return typeof(View);
                case "create_placeholder_sheet": case "convert_placeholder_sheet": case "duplicate_sheet":
                    return typeof(ViewSheet);
                case "set_phase": case "assign_scope_box": case "set_crop": case "set_annotation_crop":
                    return typeof(View);
                case "set_view_range": return typeof(ViewPlan);
                case "set_viewport_type": case "align_viewports": return typeof(Viewport);
                default: return typeof(Element);
            }
        }
        private static bool InCategory(Element element, BuiltInCategory category)
        { return element?.Category != null && Rid.Value(element.Category.Id) == (long)category; }
        private sealed class Applied
        {
            public int Index; public string Operation; public ElementId Id, TargetId;
            public JObject Action; public double Scale;
            /// <summary>The whole batch, so a verifier can see what LATER actions did to its subject.</summary>
            public JArray Batch;
        }
    }
}
