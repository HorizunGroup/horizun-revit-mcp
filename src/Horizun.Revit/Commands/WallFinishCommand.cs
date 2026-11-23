// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_wall_finish: an ARQ wall as finish layers on the EST core of a LINK,
// with no overlap, through ModelEditRunner (rehearse, token, TransactionGroup,
// re-read, roll back).
//
//   match_core            read-only: for each ARQ wall, the EST wall in the link it
//                         stands on (Core/WallFinishRules.Match), its offset, overlap
//                         and widths; unmatched walls carry the reason.
//   create_finish_layers  on each matched core, a finish wall of exterior_type_id on
//                         the core face toward the ARQ wall's exterior and/or one of
//                         interior_type_id on the other face, over the overlap only,
//                         on the ARQ wall's base level, base offset and height. The ARQ
//                         wall is kept or deleted (original_wall, said, never defaulted).
//                         An ARQ wall that hosts doors or windows is SKIPPED with the
//                         reason: a finish layer over the core would close its openings.
//                         Re-read: each finish wall present, of its type, parallel to the
//                         core, its axis core/2 + finish/2 off the core's (1 mm), its length
//                         the overlap (1 mm), sharing no volume with the core (<= 1 cm3);
//                         deleted originals gone.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class WallFinishCommand : ICommand
    {
        public string Name => "horizun_wall_finish";
        public string Description => "ARQ walls as finish layers on the EST core of a link: match each wall to its core, place finish walls on the core faces with no overlap, re-read after commit.";

        private const double Mm = 1.0 / 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: wall finish", ValidateOperation, Plan, readOperation: "match_core");

        private static string ValidateOperation(JObject request) =>
            string.Equals((string)request?["operation"], "paint_faces", StringComparison.OrdinalIgnoreCase)
                ? PaintFacesRules.Validate(request) : WallFinishRules.Validate(request);

        private sealed class Pair
        {
            public Wall Arq; public long ArqId; public Wall Core; public CoreMatch M; public XYZ A, U, N; public double CoreWidth; public int Inserts;
            public string Refusal;
        }

        private sealed class Finish
        {
            public Pair Pair;
            public int Side;
            public long TypeId, LevelId;
            public double Width, Height, BaseOffset;
            public XYZ From, To;
            public long Created = -1;
        }

        private static double[] P2(XYZ p) => new[] { p.X, p.Y };

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            if (string.Equals((string)r["operation"], "paint_faces", StringComparison.OrdinalIgnoreCase)) return PlanPaintFaces(doc, r);
            var link = (Rid.CanRepresent(r.Value<long>("link_instance_id")) ? doc.GetElement(Rid.Make(r.Value<long>("link_instance_id"))) : null) as RevitLinkInstance;
            if (link == null) throw new ArgumentException("link_instance_id is not a Revit link instance of this document. Nothing was written.");
            Document est = link.GetLinkDocument() ?? throw new ArgumentException("the EST link is not loaded; its walls cannot be read. Nothing was written.");
            Transform tf = link.GetTotalTransform();
            double maxOffset = (r.Value<double?>("max_offset_mm") ?? 150) * Mm, minOverlap = r.Value<double?>("min_overlap") ?? 0.6;
            var coreWalls = new FilteredElementCollector(est).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve lc && lc.Curve is Line).ToList();
            var cores = coreWalls.Select(w => {
                Curve c = ((LocationCurve)w.Location).Curve;
                double[] z = VerticalRange(w, tf);
                return new CoreSegment { Id = Rid.Value(w.Id), A = P2(tf.OfPoint(c.GetEndPoint(0))), B = P2(tf.OfPoint(c.GetEndPoint(1))), Width = w.Width,
                    ZMin = z[0], ZMax = z[1] };
            }).ToList();
            var coreById = coreWalls.ToDictionary(w => Rid.Value(w.Id));

            var pairs = new List<Pair>();
            foreach (JToken t in (JArray)r["wall_ids"])
            {
                long id = t.Value<long>();
                var w = (Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null) as Wall;
                if (w == null) throw new ArgumentException("wall_ids: " + id + " is not a wall of this document. Nothing was written.");
                var p = new Pair { Arq = w, ArqId = id };
                pairs.Add(p);
                if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line line)) { p.Refusal = "not a straight wall"; continue; }
                XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                p.A = a; p.U = new XYZ(b.X - a.X, b.Y - a.Y, 0).Normalize(); p.N = new XYZ(w.Orientation.X, w.Orientation.Y, 0).Normalize();
                double[] z = VerticalRange(w, Transform.Identity);
                p.M = WallFinishRules.Match(P2(a), P2(b), P2(p.N), cores, maxOffset, minOverlap, z[0], z[1]);
                if (p.M.Refusal != null) { p.Refusal = p.M.Refusal; continue; }
                p.Core = coreById[p.M.CoreId];
                p.CoreWidth = p.Core.Width;
                try { p.Inserts = w.FindInserts(true, false, false, false).Count; } catch { p.Inserts = -1; }
            }
            JArray rows = new JArray(pairs.Select(p => new JObject
            {
                ["wall_id"] = p.ArqId, ["core_id"] = p.M?.Refusal == null && p.M != null ? (JToken)p.M.CoreId : null,
                ["offset_mm"] = p.Refusal == null ? (JToken)Math.Round(p.M.OffsetFeet / Mm, 1) : null,
                ["overlap"] = p.Refusal == null ? (JToken)Math.Round(p.M.Ratio, 3) : null,
                ["wall_width_mm"] = Math.Round(p.Arq.Width / Mm, 1), ["core_width_mm"] = p.Refusal == null ? (JToken)Math.Round(p.CoreWidth / Mm, 1) : null,
                ["hosted_inserts"] = p.Inserts, ["reason"] = p.Refusal
            }));
            string op = r.Value<string>("operation").Trim().ToLowerInvariant();
            if (op == "match_core")
                return new ArchModelEdit { ReadResult = new JObject { ["read_only"] = true, ["link"] = est.Title, ["walls"] = rows,
                    ["matched"] = pairs.Count(p => p.Refusal == null), ["unmatched"] = pairs.Count(p => p.Refusal != null) } };

            if (op == "convert_to_finish") return PlanConversion(doc, r, pairs, rows, est, tf);

            bool deleteOriginal = r.Value<string>("original_wall") == "delete_if_unhosted";
            var finishes = new List<Finish>();
            var skipped = new JArray(pairs.Where(p => p.Refusal != null).Select(p => new JObject { ["wall_id"] = p.ArqId, ["reason"] = p.Refusal }));
            foreach (Pair p in pairs.Where(x => x.Refusal == null))
            {
                if (p.Inserts != 0)
                {
                    skipped.Add(new JObject { ["wall_id"] = p.ArqId, ["reason"] = p.Inserts < 0 ? "its hosted inserts could not be read" : "hosts " + p.Inserts + " door/window insert(s): a finish layer on the core would close the opening" });
                    continue;
                }
                foreach (var side in new[] { new { K = 1, F = "exterior_type_id" }, new { K = -1, F = "interior_type_id" } })
                {
                    if (r[side.F] == null) continue;
                    long typeId = r.Value<long>(side.F);
                    var type = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as WallType : null;
                    if (type == null || type.Kind != WallKind.Basic) throw new ArgumentException(side.F + " is not a basic wall type. Nothing was written.");
                    double off = p.M.OffsetFeet + side.K * WallFinishRules.FinishOffset(p.CoreWidth, type.Width);
                    double z = (doc.GetElement(p.Arq.LevelId) as Level)?.Elevation ?? p.A.Z;
                    XYZ from = new XYZ(p.A.X + p.U.X * p.M.From + p.N.X * off, p.A.Y + p.U.Y * p.M.From + p.N.Y * off, z);
                    XYZ to = new XYZ(p.A.X + p.U.X * p.M.To + p.N.X * off, p.A.Y + p.U.Y * p.M.To + p.N.Y * off, z);
                    if (from.DistanceTo(to) < 50 * Mm) { skipped.Add(new JObject { ["wall_id"] = p.ArqId, ["reason"] = "the overlap is under 50 mm" }); continue; }
                    finishes.Add(new Finish { Pair = p, Side = side.K, TypeId = typeId, Width = type.Width, From = from, To = to,
                        LevelId = Rid.Value(p.Arq.LevelId),
                        BaseOffset = p.Arq.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0,
                        Height = p.Arq.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0 });
                }
            }
            if (finishes.Count == 0)
                return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["walls"] = rows, ["skipped"] = skipped, ["note"] = "No finish layer to create; nothing was written." } };
            var used = finishes.Select(f => f.Pair).Distinct().ToList();
            var originals = deleteOriginal ? new WallFinishRules.OriginalWalls(used.Select(p => p.ArqId)) : null;
            var edit = new ArchModelEdit();
            foreach (Pair p in used) edit.Planned.Add(ModelEditRunner.Planned(p.Arq, deleteOriginal ? PlannedAction.Delete : PlannedAction.Modify, r));
            edit.Summary["operation"] = "create_finish_layers";
            edit.Summary["finish_walls"] = finishes.Count;
            edit.Summary["original_wall"] = deleteOriginal ? "delete_if_unhosted" : "keep";
            edit.Summary["deletes"] = originals == null ? new JArray() : new JArray(originals.Ids);
            edit.Summary["skipped"] = skipped;
            edit.Summary["walls"] = rows;

            edit.Apply = d =>
            {
                foreach (Finish f in finishes)
                {
                    if (f.Height <= 0) throw new ArgumentException("the height of ARQ wall " + f.Pair.ArqId + " could not be read.");
                    Wall w = Wall.Create(d, Line.CreateBound(f.From, f.To), Rid.Make(f.TypeId), Rid.Make(f.LevelId), f.Height, f.BaseOffset, false, false);
                    w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set(0);   // wall centerline
                    WallUtils.DisallowWallJoinAtEnd(w, 0);
                    WallUtils.DisallowWallJoinAtEnd(w, 1);
                    f.Created = Rid.Value(w.Id);
                }
                originals?.Delete(ids => d.Delete(ids.Select(Rid.Make).ToList()).Select(Rid.Value));
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "created", "type_id", "parallel", "offset", "length", "no_overlap" };
                if (deleteOriginal) req.Add("originals_removed");
                var check = new PostconditionCheck(req.ToArray());
                int created = 0, typed = 0, parallel = 0, offset = 0, length = 0, clean = 0, unread = 0;
                var outRows = new JArray();
                foreach (Finish f in finishes)
                {
                    var w = f.Created > 0 ? d.GetElement(Rid.Make(f.Created)) as Wall : null;
                    var row = new JObject { ["wall_id"] = f.Pair.ArqId, ["core_id"] = f.Pair.M.CoreId, ["side"] = f.Side > 0 ? "exterior" : "interior", ["finish_id"] = w == null ? null : (JToken)f.Created };
                    outRows.Add(row);
                    if (w == null) continue;
                    created++;
                    if (Rid.Value(w.GetTypeId()) == f.TypeId) typed++;
                    Curve c = ((LocationCurve)w.Location).Curve;
                    XYZ c0 = c.GetEndPoint(0), c1 = c.GetEndPoint(1);
                    XYZ dir = new XYZ(c1.X - c0.X, c1.Y - c0.Y, 0).Normalize();
                    if (Math.Abs(dir.DotProduct(f.Pair.U)) >= WallFinishRules.ParallelCos) parallel++;
                    XYZ mid = (c0 + c1) / 2;
                    double measured = (mid - f.Pair.A).DotProduct(f.Pair.N) - f.Pair.M.OffsetFeet;   // finish axis off the core axis, along n
                    double expected = f.Side * WallFinishRules.FinishOffset(f.Pair.CoreWidth, f.Width);
                    row["offset_from_core_mm"] = Math.Round(measured / Mm, 2);
                    if (Math.Abs(measured - expected) <= 1 * Mm) offset++;
                    if (Math.Abs(c0.DistanceTo(c1) - f.From.DistanceTo(f.To)) <= 1 * Mm) length++;
                    var core = est.GetElement(Rid.Make(f.Pair.M.CoreId)) as Wall;
                    double? shared = core == null ? (double?)null : SharedWithCore(w, core, tf);
                    row["shared_with_core_m3"] = shared.HasValue ? (JToken)Math.Round(shared.Value, 6) : null;
                    if (shared == null) unread++; else if (shared.Value <= JoinGeometryRules.OverlapToleranceM3) clean++;
                }
                int n = finishes.Count;
                check.Compare("created", n, created);
                check.Compare("type_id", n, typed);
                check.Compare("parallel", n, parallel);
                check.Compare("offset", n, offset);
                check.Compare("length", n, length);
                if (unread > 0) check.Unreadable("no_overlap", n, unread + " finish wall(s) whose volume shared with the core could not be measured");
                else check.Compare("no_overlap", n, clean);
                if (originals != null)
                    check.Compare("originals_removed", originals.Ids.Count, originals.CountRemoved(id => d.GetElement(Rid.Make(id)) != null));
                edit.Evidence["also_created"] = new JArray(finishes.Select(f => f.Created));
                edit.Evidence["finishes"] = outRows;
                return check;
            };
            return edit;
        }

        /// <summary>The volume a host wall shares with a linked core, the core's solids moved into host coordinates.</summary>
        private static double[] VerticalRange(Wall wall, Transform linkTransform)
        {
            try
            {
                BoundingBoxXYZ box = wall.get_BoundingBox(null);
                if (box != null)
                {
                    var z = new List<double>();
                    foreach (double x in new[] { box.Min.X, box.Max.X })
                        foreach (double y in new[] { box.Min.Y, box.Max.Y })
                            foreach (double height in new[] { box.Min.Z, box.Max.Z })
                                z.Add(linkTransform.OfPoint(box.Transform.OfPoint(new XYZ(x, y, height))).Z);
                    return new[] { z.Min(), z.Max() };
                }
            }
            catch { }
            return new[] { double.NaN, double.NaN };
        }

        private static double? SharedWithCore(Wall finish, Wall core, Transform tf)
        {
            List<Solid> a = JoinGeometryCommand.SolidsOf(finish);
            List<Solid> b = JoinGeometryCommand.SolidsOf(core).Select(s => SolidUtils.CreateTransformed(s, tf)).ToList();
            if (a.Count == 0 || b.Count == 0) return null;
            double total = 0;
            foreach (Solid x in a)
                foreach (Solid y in b)
                {
                    if (!ClashBooleanRetry.Try(
                            () => BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect),
                            () => BooleanOperationsUtils.ExecuteBooleanOperation(y, x, BooleanOperationsType.Intersect),
                            out Solid inter, out bool recovered, out string error))
                        return null;
                    if (inter != null) total += inter.Volume;
                }
            return total * 0.028316846592;
        }
    }
}
