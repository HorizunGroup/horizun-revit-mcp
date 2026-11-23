// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_split_by_level split_at=openings: split ONE straight basic wall at the jambs
// of its doors and windows - side pieces between openings, one opening piece over each
// opening span (Core/GeometryWhatIfRules OpeningSplitRules plans them).
//
// Field data (2026-10): "split walls at lintel, sill and sides around openings" was one of
// the operations agents kept writing in Python. What is implemented, and what is not:
//
//   * IMPLEMENTED - sides. The ORIGINAL wall becomes the first piece, so it keeps its
//     ElementId, and the doors/windows inside that piece keep theirs. Every other piece is a
//     copy of the original wall (all its parameters, constraints and location line) trimmed
//     to its span; the doors/windows in it are the COPIES Revit made with the wall (same
//     type, same instance parameters) and the originals are deleted with the old span. The
//     reply maps every original insert id to the id that now stands in its place.
//   * NOT IMPLEMENTED - separate lintel and sill pieces. A door or window needs a host
//     wall covering its whole span; cutting the wall above and below it would leave the
//     insert without a host. The lintel and the sill stay part of the opening piece.
//
// Verified before commit and re-read after it: the pieces are contiguous and their lengths
// add up to the original within 1 mm; every insert exists, is hosted by the piece that
// covers it, has its original type, location (1 mm), sill height, facing and hand.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class SplitByLevelCommand
    {
        private const double MmPerFoot = 304.8;
        private const double OneMm = 1.0 / 304.8;

        private sealed class InsertFact
        {
            public long Id;
            public ElementId SymbolId;
            public XYZ Point;
            public double Along;
            public double Width;
            public bool Facing, Hand;
            public double? Sill;
        }

        private CommandResult SplitAtOpenings(UIApplication app, JObject request, GateResult gate, Document doc, Element source, long raw)
        {
            var wall = source as Wall;
            if (wall == null) return CommandResult.Fail("split_at=openings takes one wall. Nothing was written.");
            string refusal = ValidateForOpenings(doc, wall);
            if (refusal != null) return CommandResult.Fail(refusal + " Nothing was written.");
            var line = (Line)((LocationCurve)wall.Location).Curve;
            XYZ start = line.GetEndPoint(0), dir = line.Direction;
            double length = line.Length;
            var facts = new List<InsertFact>();
            foreach (ElementId id in wall.FindInserts(true, true, true, true))
            {
                var fi = (FamilyInstance)doc.GetElement(id);
                double width = OpeningWidth(fi);
                XYZ p = ((LocationPoint)fi.Location).Point;
                var f = new InsertFact
                {
                    Id = Rid.Value(id), SymbolId = fi.Symbol.Id, Point = p,
                    Along = (p - start).DotProduct(dir), Width = width,
                    Facing = fi.FacingFlipped, Hand = fi.HandFlipped, Sill = Sill(fi)
                };
                string centred = CentredOnOrigin(fi, start, dir, f.Along);
                if (centred != null) return CommandResult.Fail(centred + " Nothing was written.");
                facts.Add(f);
            }
            OpeningSplitPlan plan = OpeningSplitRules.Plan(length,
                facts.Select(f => new OpeningSpan { Id = f.Id, Center = f.Along, Width = f.Width }).ToList());
            if (!plan.Ok) return CommandResult.Fail("Wall " + raw + " cannot be split at its openings: " + plan.Refusal + ". Nothing was written.");

            JArray pieces = PieceRows(plan.Pieces);
            string state = wall.UniqueId + ":" + wall.GetTypeId() + ":" + start + ":" + line.GetEndPoint(1) + ":" +
                string.Join(",", facts.OrderBy(f => f.Id).Select(f => f.Id + "@" + Mm(f.Along) + "/" + Mm(f.Width)));
            string planHash = DocumentGate.PlanHash(new JObject { ["element_id"] = raw, ["split_at"] = "openings", ["source_state"] = state },
                "element_id", "split_at", "source_state");
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["split_at"] = "openings",
                    ["element_id"] = raw, ["wall_length_mm"] = Math.Round(length * MmPerFoot, 1),
                    ["pieces"] = pieces, ["would_create"] = plan.Pieces.Count - 1,
                    ["hosted_inserts"] = new JArray(facts.Select(f => f.Id)),
                    ["notes"] = new JArray(plan.Notes),
                    ["note"] = "The original wall becomes piece 0 and keeps its id, as do the inserts in it. The inserts of the other " +
                               "pieces are replaced by copies (same type and parameters, new ids, mapped in the reply). Lintel and sill " +
                               "stay in the opening piece: a door or window needs a host covering its span."
                };
                ApplicationOutcome.StampRehearsal(rehearsal, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(rehearsal, gate, Name, planHash, true,
                    "bound to the wall's identity, type, curve and the position and width of each hosted insert");
                return CommandResult.Ok(rehearsal);
            }
            CommandResult denied = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (denied != null) return denied;
            denied = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (denied != null) return denied;

            var pieceIds = new List<ElementId>();
            var insertMap = new Dictionary<long, ElementId>();   // original insert -> the insert standing in its place
            var capture = new SplitFailures();
            string verifyError = null;
            using (var tx = new Transaction(doc, "Horizun: split wall at openings"))
            {
                FailureHandlingOptions options = tx.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(capture);
                tx.SetFailureHandlingOptions(options);
                if (tx.Start() != TransactionStatus.Started) return CommandResult.Fail("The transaction did not start. Nothing was written.");
                try
                {
                    capture.Ours.Add(raw);
                    capture.Ours.UnionWith(facts.Select(f => f.Id));
                    // The wall's end joins are released before anything moves: trimming the
                    // original away from a corner it is joined at made Revit fail the whole
                    // transaction with "Can't keep elements joined" (measured live, Revit 2025,
                    // 2026-10-27). Copies inherit the released state; the outer ends of the
                    // first and last pieces get their joins back below, where they were allowed.
                    bool joinStartAllowed = WallUtils.IsWallJoinAllowedAtEnd(wall, 0);
                    bool joinEndAllowed = WallUtils.IsWallJoinAllowedAtEnd(wall, 1);
                    if (joinStartAllowed) WallUtils.DisallowWallJoinAtEnd(wall, 0);
                    if (joinEndAllowed) WallUtils.DisallowWallJoinAtEnd(wall, 1);
                    // Walls that END on this one (T junctions) lose the span they are joined to
                    // when the original is trimmed - the same "Can't keep elements joined" (live,
                    // Revit 2025: an interior wall meeting the split wall mid-span). Their end is
                    // released here and allowed again after the pieces exist, so it rejoins
                    // whichever piece now covers the junction.
                    var tJoins = TJoinedEnds(doc, wall);
                    foreach (var t in tJoins) WallUtils.DisallowWallJoinAtEnd(t.Key, t.Value);
                    doc.Regenerate();
                    // Copies first, from the untouched original: one per piece after the first.
                    var copies = new List<Wall>();
                    for (int i = 1; i < plan.Pieces.Count; i++)
                    {
                        ICollection<ElementId> made = ElementTransformUtils.CopyElement(doc, wall.Id, XYZ.Zero);
                        Wall copy = made.Select(id => doc.GetElement(id)).OfType<Wall>().SingleOrDefault();
                        if (copy == null) throw new InvalidOperationException("Revit returned no wall for piece " + i + ".");
                        copies.Add(copy);
                        capture.Ours.UnionWith(made.Select(Rid.Value));
                        var copiedInserts = new HashSet<ElementId>(made.Where(id => id != copy.Id));
                        copiedInserts.UnionWith(copy.FindInserts(true, true, true, true));
                        capture.Ours.UnionWith(copiedInserts.Select(Rid.Value));
                        KeepInserts(doc, copiedInserts, facts, plan.Pieces[i], insertMap);
                        ((LocationCurve)copy.Location).Curve = Segment(start, dir, plan.Pieces[i]);
                    }
                    // Then the original: keep only its piece-0 inserts and trim it.
                    foreach (InsertFact f in facts)
                    {
                        if (plan.Pieces[0].OpeningIds.Contains(f.Id))
                            insertMap[f.Id] = Rid.Make(f.Id);
                        else doc.Delete(Rid.Make(f.Id));
                    }
                    ((LocationCurve)wall.Location).Curve = Segment(start, dir, plan.Pieces[0]);
                    pieceIds.Add(wall.Id);
                    pieceIds.AddRange(copies.Select(c => c.Id));
                    doc.Regenerate();
                    // Outer ends rejoin their neighbours as the original did; the cuts between
                    // pieces stay unjoined so the pieces remain separate walls.
                    Wall lastPiece = copies.Count > 0 ? copies[copies.Count - 1] : wall;
                    if (joinStartAllowed) WallUtils.AllowWallJoinAtEnd(wall, 0);
                    if (joinEndAllowed) WallUtils.AllowWallJoinAtEnd(lastPiece, 1);
                    foreach (var t in tJoins) WallUtils.AllowWallJoinAtEnd(t.Key, t.Value);
                    doc.Regenerate();
                    // An insert that slid along its new host is put back where it was.
                    foreach (InsertFact f in facts)
                    {
                        var fi = insertMap.TryGetValue(f.Id, out ElementId now) ? doc.GetElement(now) as FamilyInstance : null;
                        if (fi == null) continue;
                        XYZ delta = f.Point - ((LocationPoint)fi.Location).Point;
                        if (delta.GetLength() > OneMm / 2) ElementTransformUtils.MoveElement(doc, fi.Id, delta);
                    }
                    doc.Regenerate();
                    verifyError = Verify(doc, start, dir, length, plan.Pieces, pieceIds, facts, insertMap);
                    if (verifyError != null) throw new InvalidOperationException(verifyError);
                    TransactionStatus committed = tx.Commit();
                    if (committed != TransactionStatus.Committed)
                        return CommandResult.Fail("The split did not commit (" + committed + "); no verified result is claimed.");
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                    {
                        Guard.RollbackResult rollback = Guard.RollBack(tx);
                        return CommandResult.Fail((rollback.Confirmed ? "Split at openings rolled back, nothing kept: " :
                            "Split state uncertain after rollback returned " + rollback.StatusName + ": ") + ex.Message);
                    }
                    return CommandResult.Fail("Split state uncertain: " + ex.Message);
                }
            }
            // The commit is not the proof: read the pieces and inserts again.
            string reread = Verify(doc, start, dir, length, plan.Pieces, pieceIds, facts, insertMap);
            bool verified = reread == null && capture.Unexpected.Count == 0;
            var created = new JArray();
            for (int i = 0; i < pieceIds.Count; i++)
            {
                var piece = doc.GetElement(pieceIds[i]) as Wall;
                Line l = (piece?.Location as LocationCurve)?.Curve as Line;
                created.Add(new JObject
                {
                    ["element_id"] = Rid.Value(pieceIds[i]), ["exists"] = piece != null, ["original"] = i == 0,
                    ["kind"] = plan.Pieces[i].IsOpening ? "opening" : "side",
                    ["length_mm"] = l == null ? null : (JToken)Math.Round(l.Length * MmPerFoot, 1),
                    ["inserts"] = new JArray(plan.Pieces[i].OpeningIds.Select(o => insertMap.TryGetValue(o, out ElementId n) ? Rid.Value(n) : (long?)null))
                });
            }
            var map = new JArray(facts.Select(f => new JObject
            {
                ["original_id"] = f.Id,
                ["now_id"] = insertMap.TryGetValue(f.Id, out ElementId n) ? Rid.Value(n) : (long?)null,
                ["id_kept"] = insertMap.TryGetValue(f.Id, out ElementId m) && Rid.Value(m) == f.Id
            }));
            var result = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["split_at"] = "openings",
                ["original_element_id"] = raw, ["pieces"] = created, ["inserts"] = map,
                ["verification_error"] = reread,
                ["unexpected_warnings"] = new JArray(capture.Unexpected),
                ["notes"] = new JArray(plan.Notes),
                ["all_verified"] = verified
            };
            ApplicationOutcome.StampApplied(result, "Committed", 1, 1, verified ? 1 : 0, 0, verified ? 0 : 1, 0);
            return CommandResult.Ok(result);
        }

        private static string ValidateForOpenings(Document doc, Wall wall)
        {
            if (wall.GroupId != ElementId.InvalidElementId) return "A grouped wall cannot be split independently.";
            if (wall.WallType.Kind != WallKind.Basic) return "Only a basic wall can be split at its openings (curtain and stacked walls are refused).";
            if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line)) return "The wall needs a straight location line.";
            if (wall.SketchId != ElementId.InvalidElementId) return "A wall with an edited profile cannot be split by its location line alone.";
            foreach (ElementId id in wall.FindInserts(true, true, true, true))
            {
                Element insert = doc.GetElement(id);
                if (!(insert is FamilyInstance fi) || (Rid.Value(fi.Category.Id) != (long)BuiltInCategory.OST_Doors &&
                    Rid.Value(fi.Category.Id) != (long)BuiltInCategory.OST_Windows))
                    return "The wall has an insert other than a door or window (" + id + "); its opening cannot be guaranteed.";
                if (!(fi.Location is LocationPoint)) return "Insert " + id + " has no location point.";
                if (fi.GroupId != ElementId.InvalidElementId) return "Insert " + id + " is in a group.";
                if (OpeningWidth(fi) <= 0) return "Insert " + id + " has no readable width (rough width, width).";
            }
            return null;
        }

        /// <summary>Rough width when the family has one, else its width, instance before type. 0 when unreadable.</summary>
        private static double OpeningWidth(FamilyInstance fi)
        {
            foreach (BuiltInParameter p in new[] { BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH,
                                                   BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
                foreach (Element owner in new Element[] { fi, fi.Symbol })
                {
                    Parameter par = owner?.get_Parameter(p);
                    if (par != null && par.HasValue && par.StorageType == StorageType.Double && par.AsDouble() > 1e-6) return par.AsDouble();
                }
            return 0;
        }

        private static double? Sill(FamilyInstance fi)
        {
            Parameter p = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? p.AsDouble() : (double?)null;
        }

        /// <summary>
        /// The span is taken as the insert's origin +- half its width, which holds for families
        /// whose origin is the centre of the opening (Revit's own templates). Refuses an insert
        /// whose bounding box is not centred on its origin along the wall (150 mm).
        /// </summary>
        private static string CentredOnOrigin(FamilyInstance fi, XYZ start, XYZ dir, double along)
        {
            BoundingBoxXYZ bb = fi.get_BoundingBox(null);
            if (bb == null) return null;
            double a = double.MaxValue, b = double.MinValue;
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
                foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                {
                    double t = (new XYZ(x, y, start.Z) - start).DotProduct(dir);
                    a = Math.Min(a, t); b = Math.Max(b, t);
                }
            // An axis-aligned box of a rotated wall is wider than the insert; only its middle is compared.
            double mid = (a + b) / 2;
            if (Math.Abs(mid - along) > 150 * OneMm)
                return "Insert " + Rid.Value(fi.Id) + "'s geometry is not centred on its origin along the wall (" +
                       Mm(mid - along) + " mm off), so its jambs cannot be located from its width.";
            return null;
        }

        private static string Mm(double feet) => (feet * MmPerFoot).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        private static Line Segment(XYZ start, XYZ dir, WallPiece piece) =>
            Line.CreateBound(start + dir * piece.Start, start + dir * piece.End);

        /// <summary>
        /// Other walls whose end is joined to this wall (a T junction or a corner), with the end
        /// index, read from Revit's own join record. Only ends whose join is allowed are returned.
        /// </summary>
        private static List<KeyValuePair<Wall, int>> TJoinedEnds(Document doc, Wall wall)
        {
            var found = new List<KeyValuePair<Wall, int>>();
            BoundingBoxXYZ box = wall.get_BoundingBox(null);
            if (box == null) return found;
            var outline = new Outline(box.Min - new XYZ(OneMm, OneMm, OneMm), box.Max + new XYZ(OneMm, OneMm, OneMm));
            foreach (Wall other in new FilteredElementCollector(doc).OfClass(typeof(Wall))
                         .WherePasses(new BoundingBoxIntersectsFilter(outline)).Cast<Wall>())
            {
                if (other.Id == wall.Id || !(other.Location is LocationCurve lc)) continue;
                for (int end = 0; end < 2; end++)
                {
                    if (!WallUtils.IsWallJoinAllowedAtEnd(other, end)) continue;
                    ElementArray at;
                    try { at = lc.get_ElementsAtJoin(end); } catch { continue; }
                    if (at == null) continue;
                    foreach (Element e in at)
                        if (e != null && e.Id == wall.Id) { found.Add(new KeyValuePair<Wall, int>(other, end)); break; }
                }
            }
            return found;
        }

        /// <summary>Of the inserts a wall copy brought with it, keep those of this piece (mapped to their originals) and delete the rest.</summary>
        private static void KeepInserts(Document doc, IEnumerable<ElementId> copiedInserts, List<InsertFact> facts,
            WallPiece piece, Dictionary<long, ElementId> map)
        {
            foreach (ElementId id in copiedInserts.ToList())
            {
                var fi = doc.GetElement(id) as FamilyInstance;
                if (fi == null) continue;
                if (fi.SuperComponent != null) continue;   // nested in a copied insert: goes with its parent
                XYZ p = (fi.Location as LocationPoint)?.Point;
                InsertFact original = p == null ? null : facts
                    .Where(f => f.SymbolId == fi.Symbol.Id && f.Point.DistanceTo(p) < OneMm)
                    .FirstOrDefault(f => !map.ContainsKey(f.Id) || map[f.Id] == null);
                if (original != null && piece.OpeningIds.Contains(original.Id)) map[original.Id] = id;
                else doc.Delete(id);
            }
        }

        private static string Verify(Document doc, XYZ start, XYZ dir, double length, List<WallPiece> plan,
            List<ElementId> pieceIds, List<InsertFact> facts, Dictionary<long, ElementId> map)
        {
            double total = 0;
            for (int i = 0; i < pieceIds.Count; i++)
            {
                var piece = doc.GetElement(pieceIds[i]) as Wall;
                var l = (piece?.Location as LocationCurve)?.Curve as Line;
                if (l == null) return "piece " + i + " is missing or not straight";
                double a = (l.GetEndPoint(0) - start).DotProduct(dir), b = (l.GetEndPoint(1) - start).DotProduct(dir);
                if (Math.Abs(a - plan[i].Start) > OneMm || Math.Abs(b - plan[i].End) > OneMm)
                    return "piece " + i + " runs " + Mm(a) + ".." + Mm(b) + " mm, planned " +
                           Mm(plan[i].Start) + ".." + Mm(plan[i].End);
                total += l.Length;
            }
            if (Math.Abs(total - length) > OneMm)
                return "the pieces add up to " + Mm(total) + " mm, the wall was " + Mm(length);
            foreach (InsertFact f in facts)
            {
                int index = plan.FindIndex(p => p.OpeningIds.Contains(f.Id));
                var fi = map.TryGetValue(f.Id, out ElementId now) && now != null ? doc.GetElement(now) as FamilyInstance : null;
                if (fi == null) return "insert " + f.Id + " has no standing replacement";
                if (index < 0 || fi.Host?.Id != pieceIds[index]) return "insert " + f.Id + " is not hosted by the piece covering it";
                if (fi.Symbol.Id != f.SymbolId) return "insert " + f.Id + " changed type";
                if (((LocationPoint)fi.Location).Point.DistanceTo(f.Point) > OneMm) return "insert " + f.Id + " moved";
                if (fi.FacingFlipped != f.Facing || fi.HandFlipped != f.Hand) return "insert " + f.Id + " changed facing or hand";
                double? sill = Sill(fi);
                if (f.Sill.HasValue && (!sill.HasValue || Math.Abs(sill.Value - f.Sill.Value) > OneMm)) return "insert " + f.Id + " changed sill height";
            }
            return null;
        }

        private static JArray PieceRows(List<WallPiece> pieces) => new JArray(pieces.Select((p, i) => new JObject
        {
            ["index"] = i, ["kind"] = p.IsOpening ? "opening" : "side",
            ["start_mm"] = Math.Round(p.Start * MmPerFoot, 1), ["end_mm"] = Math.Round(p.End * MmPerFoot, 1),
            ["length_mm"] = Math.Round(p.Length * MmPerFoot, 1), ["opening_ids"] = new JArray(p.OpeningIds)
        }));

        /// <summary>
        /// Walls overlap and identical instances are transient by construction here (a copy
        /// sits on its original until both are trimmed) and are deleted ONLY when every element
        /// they name is one this operation made or trimmed. Every other warning is kept, counted
        /// and takes all_verified down.
        /// </summary>
        private sealed class SplitFailures : IFailuresPreprocessor
        {
            public readonly HashSet<long> Ours = new HashSet<long>();
            public readonly List<string> Unexpected = new List<string>();
            private static readonly HashSet<FailureDefinitionId> Transient = new HashSet<FailureDefinitionId>
            {
                BuiltInFailures.OverlapFailures.WallsOverlap,
                BuiltInFailures.OverlapFailures.DuplicateInstances
            };

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (FailureMessageAccessor m in accessor.GetFailureMessages())
                {
                    if (m.GetSeverity() != FailureSeverity.Warning) continue;
                    bool ours = false;
                    try { ours = m.GetFailingElementIds().All(id => Ours.Contains(Rid.Value(id))); } catch { }
                    if (ours && Transient.Contains(m.GetFailureDefinitionId())) { accessor.DeleteWarning(m); continue; }
                    try { Unexpected.Add(m.GetDescriptionText()); } catch { Unexpected.Add("<a warning whose text could not be read>"); }
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
