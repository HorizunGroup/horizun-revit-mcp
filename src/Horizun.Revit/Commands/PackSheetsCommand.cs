// -----------------------------------------------------------------------------
// Horizun Revit MCP - autonomous, deterministic packing of one sheet.
//
// The caller chooses the sheet, ordered content, margin and gap. The command
// chooses the coordinates. It measures existing placements directly and
// unplaced views/schedules by creating their real placement provisionally and
// rolling it back, runs the pure upper-left packer, then materialises the
// complete proposal during dry-run. Confirmation binds the stable source state,
// sheet and fixed obstacles; on apply it is spent BEFORE the provisional size
// measurement transaction opens.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class PackSheetsCommand : ICommand
    {
        public string Name => "horizun_pack_sheets";
        public string Description => "Pack ordered views and schedules on a sheet with verified margins and gaps.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            if (doc.IsReadOnly) return CommandResult.Fail("The document is read-only; sheet packing cannot run.");

            double toFeet, fromFeet;
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!PlanimetryGeometry.TryScaleToFeet(units, out toFeet) ||
                !PlanimetryGeometry.TryScaleFromFeet(units, out fromFeet))
                return CommandResult.Fail("units must be mm, m or feet.");

            ViewSheet sheet;
            string error;
            if (!TryElement(doc, request["sheet_id"], out sheet, out error)) return CommandResult.Fail(error);
            if (sheet.IsPlaceholder) return CommandResult.Fail("sheet_id is a placeholder sheet; it cannot host placements.");
            JArray rawItems = request["items"] as JArray;
            if (rawItems == null || rawItems.Count < 1 || rawItems.Count > 100)
                return CommandResult.Fail("items must contain 1..100 ordered placements.");

            double margin = (request.Value<double?>("margin") ?? 10.0) * toFeet;
            double gap = (request.Value<double?>("gap") ?? 10.0) * toFeet;
            double tolerance = (request.Value<double?>("tolerance") ?? 0.1) * toFeet;
            if (!FiniteNonNegative(margin) || !FiniteNonNegative(gap) || !FinitePositive(tolerance))
                return CommandResult.Fail("margin/gap must be finite and non-negative; tolerance must be finite and positive.");

            List<Item> items;
            if (!ResolveItems(doc, sheet, rawItems, out items, out error)) return CommandResult.Fail(error);
            List<Obstacle> fixedObstacles;
            if (!FixedObstacles(doc, sheet, items, out fixedObstacles, out error)) return CommandResult.Fail(error);

            PlanBox sheetBox = SheetBox(sheet);
            if (!sheetBox.Valid) return CommandResult.Fail("Packing refused: sheet extent is unreadable. Nothing was written.");
            string planHash = DocumentGate.PlanHash(request, "sheet_id", "units", "margin", "gap", "tolerance", "items");
            ResolvedPlan resolved = Resolved(doc, gate, app, sheet, items, fixedObstacles, sheetBox);
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");

            // An unplaced view's View.Outline is legitimately empty in some Revit
            // releases even when the view contains detail geometry. The only
            // authoritative paper size includes the real viewport label/schedule
            // box, so measure by provisional placement and confirmed rollback.
            // On APPLY this transaction is strictly after the single-use approval.
            if (!dry)
            {
                CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolved, null);
                if (refusal != null) return refusal;
                refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
                if (refusal != null) return refusal;
            }
            if (!MeasureItems(doc, sheet, items, out string measurementRollback, out error))
                return CommandResult.Fail("Packing measurement refused: " + error +
                    " Measurement rollback status: " + measurementRollback + ". Nothing was written.");

            PackingResult packed = PlanimetryPackingRules.Pack(
                sheetBox, fixedObstacles.Select(o => o.Box),
                items.Select(i => new PackingItem { Key = i.Key, Width = i.Width, Height = i.Height }),
                margin, gap, tolerance);
            if (!packed.Ok) return CommandResult.Fail("Packing refused: " + packed.Error + ". Nothing was written.");
            foreach (PackingPlacement p in packed.Placements)
            {
                Item item = items.Single(i => i.Key == p.Key);
                item.Planned = p.Box;
            }

            if (dry)
            {
                Rehearsal rehearsal = Rehearse(doc, sheet, items, fixedObstacles, packed.Usable, gap, tolerance);
                JObject result = ResultBase(true, sheet, units, margin, gap, tolerance, fromFeet, items,
                                            fixedObstacles, rehearsal.Rows);
                result["measurement_rollback_status"] = measurementRollback;
                result["rehearsal_rollback_status"] = rehearsal.RollbackStatus;
                result["constructible"] = rehearsal.Ok;
                if (rehearsal.Ok && rehearsal.RollbackConfirmed) DocumentGate.RecordResolvedPlan(resolved);
                ApplicationOutcome.StampRehearsal(result, items.Count, 0,
                    rehearsal.Ok ? 0 : items.Count, rehearsal.RollbackConfirmed ? 0 : items.Count);
                DocumentGate.StampConfirmation(result, gate, Name, planHash,
                    rehearsal.Ok && rehearsal.RollbackConfirmed,
                    rehearsal.Ok && rehearsal.RollbackConfirmed
                        ? "the token binds the sheet, ordered source state, every existing obstacle, margin and gap; after approval the authoritative paper extents are remeasured by a rolled-back provisional placement"
                        : "no token: the complete arrangement was not constructible and cleanly rolled back");
                if (!(rehearsal.Ok && rehearsal.RollbackConfirmed))
                    result["confirmation_note"] = "NO token was issued; inspect rehearsal rows and rollback status.";
                return CommandResult.Ok(result);
            }

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: pack sheet " + sheet.SheetNumber;
            JArray reversibleRows;
            using (var group = new TransactionGroup(doc, txName))
            {
                group.Start();
                using (var tx = new Transaction(doc, txName))
                {
                    tx.Start();
                    try
                    {
                        Apply(doc, sheet, items);
                        doc.Regenerate();
                        reversibleRows = Verify(doc, sheet, items, fixedObstacles, packed.Usable, gap, tolerance,
                                                out int failures);
                        if (failures > 0)
                        {
                            Guard.RollbackResult rbTx = Guard.RollBack(tx);
                            Guard.RollbackResult rbGroup = Guard.RollBack(group);
                            return CommandResult.FailWithDetail(
                                failures + " packed placement(s) failed a reversible postcondition; the whole " +
                                "arrangement was rolled back.",
                                FailureDetail(items.Count, rbTx.StatusName, rbGroup.StatusName, reversibleRows));
                        }
                        Guard.Commit(tx, txName);
                    }
                    catch (Exception ex)
                    {
                        string rbTx = tx.GetStatus() == TransactionStatus.Started
                            ? Guard.RollBack(tx).StatusName : tx.GetStatus().ToString();
                        string rbGroup = Guard.RollBack(group).StatusName;
                        return CommandResult.FailWithDetail(
                            "Atomic sheet packing failed: " + ex.Message,
                            FailureDetail(items.Count, rbTx, rbGroup, new JArray()));
                    }
                }

                // Re-read with the group still reversible: committed transaction
                // geometry is the reliable source for viewport and label outlines.
                reversibleRows = Verify(doc, sheet, items, fixedObstacles, packed.Usable, gap, tolerance,
                                        out int groupFailures);
                if (groupFailures > 0)
                {
                    string rb = Guard.RollBack(group).StatusName;
                    return CommandResult.FailWithDetail(
                        groupFailures + " packed placement(s) changed after transaction commit; the whole group " +
                        "was rolled back.", FailureDetail(items.Count, ApplicationOutcome.Committed, rb, reversibleRows));
                }
                try { Guard.Assimilate(group, txName); }
                catch (Exception ex)
                {
                    string rb;
                    try { rb = Guard.RollBack(group).StatusName; } catch { rb = "unconfirmed"; }
                    return CommandResult.FailWithDetail("The verified packing could not be assimilated: " + ex.Message,
                        FailureDetail(items.Count, ApplicationOutcome.Committed, rb, reversibleRows));
                }
            }

            JArray rows = Verify(doc, sheet, items, fixedObstacles, packed.Usable, gap, tolerance,
                                 out int finalFailures);
            if (finalFailures > 0)
            {
                JObject uncertain = FailureDetail(items.Count, ApplicationOutcome.Committed, "Assimilated", rows);
                uncertain["state"] = "uncertain";
                return CommandResult.FailWithDetail(
                    finalFailures + " post-assimilate re-read(s) contradict the reversible verification. Inspect " +
                    "the sheet before retrying; the command does not claim a clean rollback after assimilation.", uncertain);
            }

            JObject done = ResultBase(false, sheet, units, margin, gap, tolerance, fromFeet, items,
                                      fixedObstacles, rows);
            done["measurement_rollback_status"] = measurementRollback;
            done["transaction_status"] = ApplicationOutcome.Committed;
            done["transaction_group_status"] = "Assimilated";
            done["state"] = "committed_verified";
            done["host_verified"] = true;
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, items.Count, items.Count,
                                            items.Count, 0, 0, 0);
            return CommandResult.Ok(done);
        }

        private static bool ResolveItems(Document doc, ViewSheet sheet, JArray input,
                                         out List<Item> items, out string error)
        {
            items = new List<Item>(); error = null;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < input.Count; index++)
            {
                JObject row = input[index] as JObject;
                if (row == null) { error = "items[" + index + "] is not an object."; return false; }
                string key = row.Value<string>("key");
                if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                { error = "items[" + index + "].key is missing or duplicated."; return false; }
                string[] fields = { "view_id", "schedule_id", "viewport_id", "schedule_instance_id" };
                string[] present = fields.Where(f => row[f] != null).ToArray();
                if (present.Length != 1)
                { error = "items[" + index + "] must carry exactly one of " + string.Join(", ", fields) + "."; return false; }
                string field = present[0];
                long raw = row.Value<long>(field);
                if (!Rid.CanRepresent(raw) || !ids.Add(field + ":" + raw.ToString(CultureInfo.InvariantCulture)))
                { error = "items[" + index + "]." + field + " is invalid or duplicated."; return false; }

                var item = new Item { Index = index, Key = key, SourceField = field, SourceId = Rid.Make(raw) };
                if (field == "viewport_id")
                {
                    item.Existing = doc.GetElement(item.SourceId) as Viewport;
                    Viewport vp = item.Existing as Viewport;
                    if (vp == null || vp.SheetId != sheet.Id)
                    { error = field + " " + raw + " is not a viewport on sheet " + sheet.SheetNumber + "."; return false; }
                    item.Source = doc.GetElement(vp.ViewId);
                    item.Kind = "viewport";
                    item.Estimated = ViewportBox(vp);
                    SetMeasurement(item, item.Estimated, SafeViewportCenter(vp));
                }
                else if (field == "schedule_instance_id")
                {
                    item.Existing = doc.GetElement(item.SourceId) as ScheduleSheetInstance;
                    ScheduleSheetInstance si = item.Existing as ScheduleSheetInstance;
                    if (si == null || si.OwnerViewId != sheet.Id)
                    { error = field + " " + raw + " is not a schedule instance on sheet " + sheet.SheetNumber + "."; return false; }
                    item.Source = doc.GetElement(si.ScheduleId);
                    item.Kind = "schedule_instance";
                    item.Estimated = ScheduleBox(si, sheet);
                    SetMeasurement(item, item.Estimated, SafeSchedulePoint(si));
                }
                else if (field == "view_id")
                {
                    View view = doc.GetElement(item.SourceId) as View;
                    if (view == null || view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                    { error = "view_id " + raw + " must identify an unplaced graphical view."; return false; }
                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    { error = "view_id " + raw + " cannot be added to sheet " + sheet.SheetNumber + "."; return false; }
                    item.Source = view; item.Kind = "view"; item.Estimated = OutlineBox(view);
                }
                else
                {
                    ViewSchedule schedule = doc.GetElement(item.SourceId) as ViewSchedule;
                    if (schedule == null || schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule)
                    { error = "schedule_id " + raw + " must identify a non-revision schedule."; return false; }
                    item.Source = schedule; item.Kind = "schedule"; item.Estimated = OutlineBox(schedule);
                }
                items.Add(item);
            }
            return true;
        }

        /// <summary>
        /// Revit does not guarantee a positive View.Outline before placement (a
        /// drafting view with valid detail geometry returns an empty outline in
        /// Revit 2023). Measure every unplaced source through the exact API object
        /// that will be committed, including the viewport label, and prove the
        /// measurement transaction rolled back. Existing placements were already
        /// measured in ResolveItems and never need a provisional duplicate.
        /// </summary>
        private static bool MeasureItems(Document doc, ViewSheet sheet, List<Item> items,
                                         out string rollbackStatus, out string error)
        {
            rollbackStatus = "not_needed"; error = null;
            List<Item> unplaced = items.Where(i => i.Existing == null).ToList();
            if (unplaced.Count > 0)
            {
                using (var tx = new Transaction(doc, "Horizun: measure unplaced sheet content"))
                {
                    try
                    {
                        if (tx.Start() != TransactionStatus.Started)
                        { error = "the provisional measurement transaction did not start"; rollbackStatus = tx.GetStatus().ToString(); return false; }
                        PlanBox paper = SheetBox(sheet);
                        XYZ seed = new XYZ(paper.CenterX, paper.CenterY, 0);
                        foreach (Item item in unplaced)
                        {
                            Element placement;
                            if (item.SourceField == "view_id")
                                placement = Viewport.Create(doc, sheet.Id, item.Source.Id, seed);
                            else if (item.SourceField == "schedule_id")
                                placement = ScheduleSheetInstance.Create(doc, sheet.Id, item.Source.Id, seed);
                            else
                                throw new InvalidOperationException("item '" + item.Key + "' has no provisional measurement path");
                            if (placement == null)
                                throw new InvalidOperationException("Revit returned null while measuring item '" + item.Key + "'");

                            doc.Regenerate();
                            PlanBox measured = placement is Viewport vp ? ViewportBox(vp) :
                                               placement is ScheduleSheetInstance si ? ScheduleBox(si, sheet) :
                                               PlanBox.Unreadable;
                            XYZ anchor = placement is Viewport vp2 ? SafeViewportCenter(vp2) :
                                         placement is ScheduleSheetInstance si2 ? SafeSchedulePoint(si2) : null;
                            if (!SetMeasurement(item, measured, anchor))
                                throw new InvalidOperationException("item '" + item.Key +
                                    "' has no readable positive paper extent after real provisional placement");
                            doc.Delete(placement.Id);
                            doc.Regenerate();
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    try
                    {
                        Guard.RollbackResult rb = Guard.RollBack(tx);
                        rollbackStatus = rb.StatusName;
                        if (!rb.Confirmed && error == null) error = "Revit did not confirm the measurement rollback";
                    }
                    catch (Exception ex)
                    {
                        rollbackStatus = "exception: " + ex.Message;
                        if (error == null) error = "the measurement rollback threw: " + ex.Message;
                    }
                }
                if (error != null) return false;
            }

            foreach (Item item in items)
            {
                if (!item.Estimated.Valid || !FinitePositive(item.Width) || !FinitePositive(item.Height))
                { error = "item '" + item.Key + "' has no readable positive paper extent; no size was guessed"; return false; }
            }
            return true;
        }

        private static bool SetMeasurement(Item item, PlanBox box, XYZ anchor)
        {
            if (item == null || !box.Valid || box.Width <= 0 || box.Height <= 0 || anchor == null) return false;
            item.Estimated = box;
            item.Width = box.Width; item.Height = box.Height;
            item.AnchorOffsetX = box.CenterX - anchor.X;
            item.AnchorOffsetY = box.CenterY - anchor.Y;
            return true;
        }

        private static XYZ SafeViewportCenter(Viewport viewport)
        { try { return viewport?.GetBoxCenter(); } catch { return null; } }

        private static XYZ SafeSchedulePoint(ScheduleSheetInstance schedule)
        { try { return schedule?.Point; } catch { return null; } }

        private static bool FixedObstacles(Document doc, ViewSheet sheet, List<Item> items,
                                           out List<Obstacle> obstacles, out string error)
        {
            obstacles = new List<Obstacle>(); error = null;
            var selected = new HashSet<long>(items.Where(i => i.Existing != null)
                .Select(i => Rid.Value(i.Existing.Id)));
            foreach (ElementId id in sheet.GetAllViewports())
            {
                if (selected.Contains(Rid.Value(id))) continue;
                Viewport vp = doc.GetElement(id) as Viewport;
                PlanBox box = ViewportBox(vp);
                if (!box.Valid) { error = "fixed viewport " + Rid.Value(id) + " has unreadable extent."; return false; }
                obstacles.Add(new Obstacle { Id = id, Element = vp, Box = box });
            }
            foreach (ScheduleSheetInstance si in new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                if (selected.Contains(Rid.Value(si.Id))) continue;
                PlanBox box = ScheduleBox(si, sheet);
                if (!box.Valid) { error = "fixed schedule instance " + Rid.Value(si.Id) + " has unreadable extent."; return false; }
                obstacles.Add(new Obstacle { Id = si.Id, Element = si, Box = box });
            }
            obstacles = obstacles.OrderBy(o => Rid.Value(o.Id)).ToList();
            return true;
        }

        private static ResolvedPlan Resolved(Document doc, GateResult gate, UIApplication app, ViewSheet sheet,
                                             List<Item> items, List<Obstacle> obstacles, PlanBox sheetBox)
        {
            var plan = new ResolvedPlan
            {
                Command = "horizun_pack_sheets", DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ContextFingerprint = "sheet:" + SafeId(sheet) + "|extent:" + PlanimetryGeometry.Signature(sheetBox) +
                    "|default_viewport_type:" + DefaultViewportType(doc)
            };
            foreach (Item item in items)
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = "item:" + item.Index,
                    Category = item.Kind,
                    Action = item.Existing == null ? PlannedAction.Create : PlannedAction.Modify,
                    GeometryFingerprint = SourceState(doc, item),
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "source", SafeId(item.Source) },
                        { "existing", SafeId(item.Existing) }
                    }
                });
            foreach (Obstacle obstacle in obstacles)
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = "obstacle:" + Rid.Value(obstacle.Id), Category = "fixed_obstacle",
                    Action = PlannedAction.Read,
                    GeometryFingerprint = PlanimetryGeometry.Signature(obstacle.Box),
                    BeforeValues = new Dictionary<string, string> { { "element", SafeId(obstacle.Element) } }
                });
            return plan;
        }

        private static string SourceState(Document doc, Item item)
        {
            if (item?.Existing is Viewport vp)
                return "viewport|" + PlanimetryGeometry.Signature(ViewportBox(vp));
            if (item?.Existing is ScheduleSheetInstance si)
                return "schedule_instance|" + PlanimetryGeometry.Signature(ScheduleBox(si, doc.GetElement(si.OwnerViewId) as ViewSheet));
            if (item?.Source is View view)
            {
                var parts = new List<string>
                {
                    SafeId(view), "type=" + view.ViewType, "scale=" + SafeViewScale(view),
                    "outline=" + PlanimetryGeometry.Signature(OutlineBox(view)),
                    "crop=" + SafeCropState(view)
                };
                // OwnedByView is a document-wide owner filter, not the unreliable
                // graphics-populated FilteredElementCollector(doc, viewId) path.
                try
                {
                    foreach (Element element in new FilteredElementCollector(doc).OwnedByView(view.Id)
                        .WhereElementIsNotElementType().ToElements().OrderBy(e => Rid.Value(e.Id)))
                        parts.Add(ElementState(element, view));
                }
                catch (Exception ex) { parts.Add("owned_unreadable=" + ex.GetType().Name); }
                return string.Join("|", parts);
            }
            return SafeId(item?.Source);
        }

        private static string ElementState(Element element, View view)
        {
            string box = "unreadable";
            try
            {
                BoundingBoxXYZ b = element?.get_BoundingBox(view);
                if (b != null)
                    box = Canon(b.Min.X) + "," + Canon(b.Min.Y) + "," + Canon(b.Min.Z) + ">" +
                          Canon(b.Max.X) + "," + Canon(b.Max.Y) + "," + Canon(b.Max.Z);
            }
            catch { }
            return SafeId(element) + "@" + box;
        }

        private static string SafeCropState(View view)
        {
            try
            {
                BoundingBoxXYZ b = view.CropBox;
                return view.CropBoxActive + ":" + (b == null ? "none" :
                    Canon(b.Min.X) + "," + Canon(b.Min.Y) + "," + Canon(b.Min.Z) + ">" +
                    Canon(b.Max.X) + "," + Canon(b.Max.Y) + "," + Canon(b.Max.Z));
            }
            catch { return "unreadable"; }
        }

        private static string SafeViewScale(View view)
        { try { return view.Scale.ToString(CultureInfo.InvariantCulture); } catch { return "unreadable"; } }

        private static string DefaultViewportType(Document doc)
        {
            try
            {
                ElementId id = doc.GetDefaultElementTypeId(ElementTypeGroup.ViewportType);
                return Rid.Value(id).ToString(CultureInfo.InvariantCulture) + "|" + SafeId(doc.GetElement(id));
            }
            catch { return "unreadable"; }
        }

        private static Rehearsal Rehearse(Document doc, ViewSheet sheet, List<Item> items,
                                          List<Obstacle> obstacles, PlanBox usable, double gap, double tolerance)
        {
            var r = new Rehearsal();
            using (var tx = new Transaction(doc, "Horizun: rehearse sheet packing"))
            {
                tx.Start();
                try
                {
                    Apply(doc, sheet, items);
                    doc.Regenerate();
                    r.Rows = Verify(doc, sheet, items, obstacles, usable, gap, tolerance, out int failures);
                    r.Ok = failures == 0;
                }
                catch (Exception ex)
                {
                    r.Ok = false;
                    r.Rows = new JArray(new JObject { ["error"] = ex.Message, ["verified"] = false });
                }
                Guard.RollbackResult rb = Guard.RollBack(tx);
                r.RollbackStatus = rb.StatusName;
                r.RollbackConfirmed = rb.Confirmed;
            }
            return r;
        }

        private static void Apply(Document doc, ViewSheet sheet, List<Item> items)
        {
            foreach (Item item in items)
            {
                // The packed rectangle describes the union of viewport box and
                // label (or the schedule box). Its centre is not necessarily the
                // API placement anchor, so preserve the measured offset instead
                // of pretending they coincide.
                XYZ point = new XYZ(item.Planned.CenterX - item.AnchorOffsetX,
                                    item.Planned.CenterY - item.AnchorOffsetY, 0);
                if (item.SourceField == "view_id")
                {
                    Viewport vp = Viewport.Create(doc, sheet.Id, item.Source.Id, point);
                    if (vp == null) throw new InvalidOperationException("Viewport.Create returned null for item '" + item.Key + "'.");
                    item.CreatedId = vp.Id;
                }
                else if (item.SourceField == "schedule_id")
                {
                    ScheduleSheetInstance si = ScheduleSheetInstance.Create(doc, sheet.Id, item.Source.Id, point);
                    if (si == null) throw new InvalidOperationException("ScheduleSheetInstance.Create returned null for item '" + item.Key + "'.");
                    item.CreatedId = si.Id;
                }
                else if (item.Existing is Viewport existingViewport)
                {
                    existingViewport.SetBoxCenter(point); item.CreatedId = existingViewport.Id;
                }
                else if (item.Existing is ScheduleSheetInstance existingSchedule)
                {
                    existingSchedule.Point = point; item.CreatedId = existingSchedule.Id;
                }
                else throw new InvalidOperationException("item '" + item.Key + "' has no placement path.");
            }
        }

        private static JArray Verify(Document doc, ViewSheet sheet, List<Item> items, List<Obstacle> obstacles,
                                     PlanBox usable, double gap, double tolerance, out int failures)
        {
            failures = 0;
            var actual = new Dictionary<string, PlanBox>(StringComparer.Ordinal);
            var centres = new Dictionary<string, XYZ>(StringComparer.Ordinal);
            foreach (Item item in items)
            {
                Element e = item.CreatedId == null ? null : doc.GetElement(item.CreatedId);
                PlanBox box = e is Viewport vp ? ViewportBox(vp) :
                              e is ScheduleSheetInstance si ? ScheduleBox(si, sheet) : PlanBox.Unreadable;
                XYZ anchor = e is Viewport vp2 ? SafeViewportCenter(vp2) :
                             e is ScheduleSheetInstance si2 ? SafeSchedulePoint(si2) : null;
                actual[item.Key] = box; centres[item.Key] = anchor;
            }

            var rows = new JArray();
            foreach (Item item in items)
            {
                PlanBox box = actual[item.Key]; XYZ centre = centres[item.Key];
                bool present = box.Valid && centre != null;
                bool centerMatch = present && Distance2D(box.CenterX, box.CenterY,
                                                         item.Planned.CenterX, item.Planned.CenterY) <= tolerance;
                bool inside = present && PlanimetryGeometry.Contains(usable, box, tolerance);
                var conflicts = new JArray();
                if (present)
                {
                    foreach (Obstacle obstacle in obstacles)
                        if (Conflict(box, obstacle.Box, gap, tolerance)) conflicts.Add(Rid.Value(obstacle.Id));
                    foreach (Item other in items.Where(i => string.CompareOrdinal(i.Key, item.Key) < 0))
                        if (actual[other.Key].Valid && Conflict(box, actual[other.Key], gap, tolerance))
                            conflicts.Add(other.Key);
                }
                bool ok = present && centerMatch && inside && conflicts.Count == 0;
                if (!ok) failures++;
                rows.Add(new JObject
                {
                    ["key"] = item.Key, ["kind"] = item.Kind,
                    ["element_id"] = item.CreatedId == null ? JValue.CreateNull() : new JValue(Rid.Value(item.CreatedId)),
                    ["present"] = present, ["center_match"] = centerMatch, ["inside_usable_extent"] = inside,
                    ["conflicts"] = conflicts, ["verified"] = ok,
                    ["actual_extent_feet"] = BoxJson(box, 1.0)
                });
            }
            return rows;
        }

        private static bool Conflict(PlanBox a, PlanBox b, double gap, double tolerance)
        {
            if (PlanimetryGeometry.Overlaps(a, b, tolerance)) return true;
            if (gap <= tolerance) return false;
            double separation = PlanimetryGeometry.Separation(a, b);
            return double.IsNaN(separation) || separation + tolerance < gap;
        }

        private static JObject ResultBase(bool dry, ViewSheet sheet, string units,
                                          double margin, double gap, double tolerance, double fromFeet,
                                          List<Item> items, List<Obstacle> obstacles, JArray rows)
        {
            return new JObject
            {
                ["dry_run"] = dry, ["sheet_id"] = Rid.Value(sheet.Id), ["sheet_number"] = sheet.SheetNumber,
                ["units"] = units, ["margin"] = margin * fromFeet, ["gap"] = gap * fromFeet,
                ["tolerance"] = tolerance * fromFeet, ["items"] = items.Count,
                ["fixed_obstacles"] = obstacles.Count,
                ["plan"] = new JArray(items.Select(i => new JObject
                {
                    ["key"] = i.Key, ["kind"] = i.Kind,
                    ["source_id"] = Rid.Value(i.Source.Id),
                    ["existing_placement_id"] = i.Existing == null ? JValue.CreateNull() : new JValue(Rid.Value(i.Existing.Id)),
                    ["center"] = new JArray(i.Planned.CenterX * fromFeet, i.Planned.CenterY * fromFeet),
                    ["estimated_extent"] = BoxJson(i.Planned, fromFeet)
                })),
                ["rows"] = rows
            };
        }

        private static JObject FailureDetail(int total, string tx, string group, JArray rows)
        {
            var o = new JObject
            {
                ["state"] = PlanFailure.IsConfirmedRollback(group) ? "rolled_back" : "uncertain",
                ["transaction_status"] = tx, ["transaction_group_status"] = group,
                ["write_started"] = true, ["rows"] = rows
            };
            ApplicationOutcome.StampApplied(o, group, total, 0, 0, 0,
                PlanFailure.IsConfirmedRollback(group) ? total : 0,
                PlanFailure.IsConfirmedRollback(group) ? 0 : total);
            return o;
        }

        private static PlanBox SheetBox(ViewSheet sheet)
        {
            try
            {
                BoundingBoxUV b = sheet.Outline;
                return b == null ? PlanBox.Unreadable : PlanBox.FromCorners(b.Min.U, b.Min.V, b.Max.U, b.Max.V);
            }
            catch { return PlanBox.Unreadable; }
        }

        private static PlanBox OutlineBox(View view)
        {
            try
            {
                BoundingBoxUV b = view.Outline;
                return b == null ? PlanBox.Unreadable : PlanBox.FromCorners(b.Min.U, b.Min.V, b.Max.U, b.Max.V);
            }
            catch { return PlanBox.Unreadable; }
        }

        private static PlanBox ViewportBox(Viewport viewport)
        {
            if (viewport == null) return PlanBox.Unreadable;
            try
            {
                Outline b = viewport.GetBoxOutline();
                PlanBox box = PlanBox.FromCorners(b.MinimumPoint.X, b.MinimumPoint.Y,
                                                  b.MaximumPoint.X, b.MaximumPoint.Y);
                try
                {
                    Outline label = viewport.GetLabelOutline();
                    if (label != null)
                        box = PlanimetryGeometry.Union(box, PlanBox.FromCorners(
                            label.MinimumPoint.X, label.MinimumPoint.Y, label.MaximumPoint.X, label.MaximumPoint.Y));
                }
                catch { }
                return box;
            }
            catch { return PlanBox.Unreadable; }
        }

        private static PlanBox ScheduleBox(ScheduleSheetInstance schedule, ViewSheet sheet)
        {
            if (schedule == null) return PlanBox.Unreadable;
            try
            {
                BoundingBoxXYZ b = schedule.get_BoundingBox(sheet);
                return b == null ? PlanBox.Unreadable : PlanBox.FromCorners(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y);
            }
            catch { return PlanBox.Unreadable; }
        }

        private static JObject BoxJson(PlanBox box, double scale)
            => box.Valid ? new JObject
            {
                ["min"] = new JArray(box.MinX * scale, box.MinY * scale),
                ["max"] = new JArray(box.MaxX * scale, box.MaxY * scale)
            } : null;

        private static double Distance2D(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool TryElement<T>(Document doc, JToken token, out T value, out string error) where T : Element
        {
            value = null; error = null;
            if (token == null || token.Type != JTokenType.Integer || !Rid.CanRepresent(token.Value<long>()))
            { error = typeof(T).Name + " id is required and must be a valid integer ElementId."; return false; }
            value = doc.GetElement(Rid.Make(token.Value<long>())) as T;
            if (value == null) { error = "Element " + token + " is not a " + typeof(T).Name + "."; return false; }
            return true;
        }

        private static string SafeId(Element e)
        {
            if (e == null) return "none";
            string uid; try { uid = e.UniqueId; } catch { uid = "unreadable"; }
            string name; try { name = e.Name; } catch { name = "unreadable"; }
            return Rid.Value(e.Id) + "|" + uid + "|" + name;
        }

        private static string Canon(double value)
            => Math.Round(value, 9).ToString("R", CultureInfo.InvariantCulture);

        private static bool FinitePositive(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0;
        private static bool FiniteNonNegative(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

        private sealed class Item
        {
            public int Index; public string Key, Kind, SourceField;
            public ElementId SourceId, CreatedId;
            public Element Source, Existing;
            public PlanBox Estimated, Planned;
            public double Width, Height, AnchorOffsetX, AnchorOffsetY;
        }

        private sealed class Obstacle { public ElementId Id; public Element Element; public PlanBox Box; }
        private sealed class Rehearsal
        {
            public bool Ok, RollbackConfirmed;
            public string RollbackStatus;
            public JArray Rows = new JArray();
        }
    }
}
