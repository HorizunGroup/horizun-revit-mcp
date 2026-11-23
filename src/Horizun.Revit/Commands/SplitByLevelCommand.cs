using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>One source per atomic, verified operation. A refusal never leaves a partial split.</summary>
    public sealed partial class SplitByLevelCommand : ICommand
    {
        public string Name => "horizun_split_by_level";
        public string Description => "Split one wall, column or vertical MEP run at crossed levels, preserving wall inserts and MEP connectivity; dry run by default.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            long raw = request.Value<long?>("element_id") ?? -1;
            if (!Rid.CanRepresent(raw)) return CommandResult.Fail("element_id must identify one element.");
            Element source = doc.GetElement(Rid.Make(raw));
            if (source == null) return CommandResult.Fail("element_id does not exist in the active document.");
            ElementId sourceId = source.Id;
            if (source.Pinned) return CommandResult.Fail("Unpin the source deliberately before splitting it.");
            string splitAt = (request.Value<string>("split_at") ?? "levels").Trim().ToLowerInvariant();
            if (splitAt == "openings") return SplitAtOpenings(app, request, gate, doc, source, raw);
            if (splitAt != "levels") return CommandResult.Fail("split_at must be levels or openings. Nothing was written.");
            BoundingBoxXYZ box = source.get_BoundingBox(null);
            if (box == null) return CommandResult.Fail("The source has no readable vertical range.");
            double[] sourceBounds = LevelSpanBounds.Read(box);
            double bottom = sourceBounds[0], top = sourceBounds[1];
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ToList();
            var cuts = levels.Where(l => LevelSpanRules.Crossed(bottom * 304.8, top * 304.8,
                new[] { l.ProjectElevation * 304.8 }).Count != 0).ToList();
            if (cuts.Count == 0) return CommandResult.Fail("The element crosses no level by more than 50 mm; nothing to split.");
            if (!(source is Wall) && !IsColumn(source) && !(source is MEPCurve))
                return CommandResult.Fail("Only basic walls, architectural/structural columns and vertical pipe, duct, conduit or cable tray runs can be split.");
            string refusal = Validate(doc, source, cuts);
            if (refusal != null) return CommandResult.Fail(refusal + " Nothing was written.");
            List<Interval> intervals;
            try { intervals = Intervals(doc, bottom, top, cuts, levels); }
            catch (Exception ex) { return CommandResult.Fail(ex.Message + " Nothing was written."); }
            var insertIds = source is Wall w ? w.FindInserts(true, true, true, true).Select(Rid.Value).OrderBy(x => x) : Enumerable.Empty<long>();
            string state = source.UniqueId + ":" + source.GetTypeId() + ":" + bottom.ToString("R") + ":" +
                top.ToString("R") + ":" + string.Join(",", cuts.Select(l => Rid.Value(l.Id))) + ":" +
                string.Join(",", insertIds);
            string planHash = DocumentGate.PlanHash(new JObject { ["element_id"] = raw, ["source_state"] = state },
                "element_id", "source_state");
            var plan = new JArray(cuts.Select(l => new JObject {
                ["level_id"] = Rid.Value(l.Id), ["name"] = l.Name,
                ["elevation_mm"] = Math.Round(l.ProjectElevation * 304.8, 1) }));
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            if (dryRun)
            {
                var rehearsal = new JObject {
                    ["dry_run"] = true, ["transaction_status"] = "not_started",
                    ["element_id"] = raw, ["source_category"] = source.Category?.Name,
                    ["crossed_levels"] = plan, ["would_create"] = cuts.Count + 1,
                    ["hosted_inserts"] = new JArray(insertIds),
                    ["note"] = "The source and its hosted inserts remain untouched until a confirmed apply." };
                ApplicationOutcome.StampRehearsal(rehearsal, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(rehearsal, gate, Name, planHash, true,
                    "bound to the source identity, type, vertical range, crossed levels and hosted inserts");
                return CommandResult.Ok(rehearsal);
            }
            CommandResult denied = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (denied != null) return denied;
            denied = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (denied != null) return denied;
            var created = new List<ElementId>();
            var inserts = new List<ElementId>();
            using (var tx = new Transaction(doc, "Horizun: split by level"))
            {
                if (tx.Start() != TransactionStatus.Started) return CommandResult.Fail("The transaction did not start.");
                try
                {
                    if (source is Wall wall) SplitWall(doc, wall, intervals, created, inserts);
                    else if (source is MEPCurve run) SplitMep(doc, run, intervals, created);
                    else SplitColumn(doc, (FamilyInstance)source, intervals, created);
                    doc.Regenerate();
                    if (doc.GetElement(sourceId) != null) throw new InvalidOperationException("The original element remains.");
                    if (created.Count != intervals.Count) throw new InvalidOperationException("Piece count differs from plan.");
                    for (int i = 0; i < created.Count; i++)
                    {
                        Element piece = doc.GetElement(created[i]);
                        BoundingBoxXYZ b = piece?.get_BoundingBox(null);
                        double[] pieceBounds = LevelSpanBounds.Read(b);
                        if (pieceBounds == null || pieceBounds[0] < intervals[i].Bottom - 0.05 ||
                            pieceBounds[1] > intervals[i].Top + 0.05)
                            throw new InvalidOperationException("Piece " + i + " still crosses its assigned interval.");
                    }
                    if (source is Wall && inserts.Count != insertIds.Count())
                        throw new InvalidOperationException("A hosted door/window was not recreated.");
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("The split did not commit; no verified result is claimed.");
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                    {
                        Guard.RollbackResult rollback = Guard.RollBack(tx);
                        return CommandResult.Fail((rollback.Confirmed ? "Split rolled back: " :
                            "Split state uncertain after rollback returned " + rollback.StatusName + ": ") + ex.Message);
                    }
                    return CommandResult.Fail("Split state uncertain: " + ex.Message);
                }
            }
            // The commit is not the proof: read each resulting element again outside the transaction.
            var rows = new JArray(created.Select(id => new JObject {
                ["element_id"] = Rid.Value(id), ["exists"] = doc.GetElement(id) != null,
                ["category"] = doc.GetElement(id)?.Category?.Name, ["built_in_category"] = CategoryKey.Of(doc.GetElement(id)) }));
            bool verified = doc.GetElement(sourceId) == null && rows.All(r => r.Value<bool>("exists"));
            for (int i = 0; i < created.Count; i++)
            {
                double[] measured = LevelSpanBounds.Read(doc.GetElement(created[i])?.get_BoundingBox(null));
                if (measured == null || measured[0] < intervals[i].Bottom - 0.05 ||
                    measured[1] > intervals[i].Top + 0.05) verified = false;
            }
            if (source is Wall)
                verified &= inserts.All(id => doc.GetElement(id) is FamilyInstance fi &&
                    created.Contains(fi.Host?.Id));
            if (source is MEPCurve)
                for (int i = 0; i < created.Count - 1; i++)
                {
                    try {
                        var one = doc.GetElement(created[i]) as MEPCurve;
                        var two = doc.GetElement(created[i + 1]) as MEPCurve;
                        verified &= one != null && two != null && End(one, true).IsConnectedTo(End(two, false));
                    } catch { verified = false; }
                }
            var result = new JObject {
                ["dry_run"] = false, ["transaction_status"] = "Committed",
                ["original_element_id"] = raw, ["created"] = rows,
                ["hosted_inserts"] = new JArray(inserts.Select(Rid.Value)),
                ["all_verified"] = verified };
            ApplicationOutcome.StampApplied(result, "Committed", 1, 1, verified ? 1 : 0, 0, verified ? 0 : 1, 0);
            return CommandResult.Ok(result);
        }

        private sealed class Interval { public double Bottom, Top; public Level Level; }

        private static List<Interval> Intervals(Document doc, double bottom, double top,
            List<Level> cuts, List<Level> levels)
        {
            var marks = new List<double> { bottom };
            marks.AddRange(cuts.Select(l => l.ProjectElevation)); marks.Add(top);
            var result = new List<Interval>();
            for (int i = 0; i < marks.Count - 1; i++)
            {
                Level level = levels.LastOrDefault(l => l.ProjectElevation <= marks[i] + 0.05);
                if (level == null) throw new InvalidOperationException("No level exists at or below piece " + i + ".");
                result.Add(new Interval { Bottom = marks[i], Top = marks[i + 1], Level = level });
            }
            return result;
        }

        private static bool IsColumn(Element e) => e is FamilyInstance &&
            (Rid.Value(e.Category.Id) == (long)BuiltInCategory.OST_Columns ||
             Rid.Value(e.Category.Id) == (long)BuiltInCategory.OST_StructuralColumns);

        private static string Validate(Document doc, Element source, List<Level> cuts)
        {
            if (source.GroupId != ElementId.InvalidElementId) return "A grouped element cannot be split independently.";
            if (source is Wall wall)
            {
                if (wall.WallType.Kind != WallKind.Basic) return "Only a basic wall can be recreated safely; keep curtain walls as documented exceptions.";
                if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line)) return "The wall needs a straight location line.";
                if (wall.SketchId != ElementId.InvalidElementId) return "An edited wall profile cannot be reconstructed from a height alone.";
                foreach (ElementId id in wall.FindInserts(true, true, true, true))
                {
                    Element insert = doc.GetElement(id);
                    if (!(insert is FamilyInstance fi) || (Rid.Value(fi.Category.Id) != (long)BuiltInCategory.OST_Doors &&
                        Rid.Value(fi.Category.Id) != (long)BuiltInCategory.OST_Windows))
                        return "The wall has an insert other than a door or window; its opening cannot be guaranteed.";
                }
            }
            else if (source is MEPCurve run)
            {
                if (!(run is Pipe) && !(run is Duct) && !(run is Conduit) && !(run is CableTray))
                    return "Only rigid pipe, duct, conduit and cable tray runs are supported.";
                if (!(run.Location is LocationCurve lc) || !(lc.Curve is Line line)) return "The MEP run needs a straight line.";
                if (run.ConnectorManager.Connectors.Cast<Connector>().Count() != 2)
                    return "The MEP run has branch or extra connectors; splitting it could disconnect them.";
                XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                if (Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) > 0.05)
                    return "The MEP run is not vertical within 15 mm.";
            }
            else if (IsColumn(source))
            {
                var fi = (FamilyInstance)source;
                if (fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.IsReadOnly != false ||
                    fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.IsReadOnly != false)
                    return "The column does not expose writable base and top level constraints.";
            }
            return null;
        }
    }
}
