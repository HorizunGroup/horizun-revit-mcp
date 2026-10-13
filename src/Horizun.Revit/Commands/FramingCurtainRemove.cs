// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing, curtain method: remove restores the carrier,
// and the carrier's delete cascade is measured before a token can authorise it.
// Original Horizun code.
//
// REMOVE. The curtain method changed the carrier: trimmed to its one opening and set
// to the placeholder type, kept full length with the placeholder type, or deleted once
// the pieces existed. Deleting the pieces alone would leave a gap where the partition
// was, so remove restores the carrier FROM THE RECORD its pieces carry
// (FramingCurtainStore): a trimmed or kept carrier gets its original type and location
// line back; a deleted one is created again with its type, line, level, base offset,
// top constraint (or unconnected height), location-line reference, flip and structural
// flag. Revit cannot bring a deleted element back, so the recreated carrier has a NEW
// id, named in the plan and the evidence, and what the record does not hold (mark,
// comments, phase, workset, other instance parameters) is named as not restored.
// A carrier someone changed after the apply (type or line not as the apply left it) is
// NOT overwritten: its restore is refused by name and only its pieces go.
// The token binds every restore (carrier, action, original type, original line to
// 0.1 mm) and the committed model is re-read: type, line within 1 mm, base and top,
// location-line reference, flip, structural flag, and the carrier's inserts as they were when
// the remove was planned (an edit made to a door since the apply is the user's), still hosted
// by it. What a deleted carrier took with it does not come back: the record names it, and so do
// the plan and the evidence (deleted_with_carrier_not_restored).
//
// CASCADE. Deleting a carrier takes whatever Revit hosts on it or ties to it (tags,
// dimensions, face-hosted families, sweeps). The plan measures that set in a
// rolled-back transaction (MeasureRemoveCascade's rule), shows it by category, and the
// token binds it; the apply compares what the delete really took. Elements the apply
// itself created (the pieces and their grid elements: ids from the first piece on, as
// Revit hands out ids in increasing order) are judged by the piece checks instead.
//
// REVIT API, confirmed in RevitAPI.xml 2023 and 2026: Wall.Create(Document, Curve,
// ElementId, ElementId, Double, Double, Boolean, Boolean); Wall.Flipped;
// Element.ChangeTypeId(ElementId); LocationCurve.Curve (set); BuiltInParameter
// WALL_KEY_REF_PARAM, WALL_HEIGHT_TYPE, WALL_TOP_OFFSET, WALL_STRUCTURAL_SIGNIFICANT.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>One carrier a remove restores from its pieces' record.</summary>
    internal sealed class CurtainRestore
    {
        public long CarrierId;
        public CurtainSourceState State;
        public string Action;
        /// <summary>Why this carrier is not restored (only its pieces go), or null.</summary>
        public string Refusal;
        /// <summary>The recreated carrier's id (a deleted carrier comes back under a new id).</summary>
        public long RecreatedId = -1;
        /// <summary>Insert id -> "type|x,y,z|host" when the remove is planned (CurtainInsertState).</summary>
        public readonly Dictionary<long, string> Inserts = new Dictionary<long, string>();

        public bool Recreates => Action == CurtainFramingRoles.CarrierDelete;

        /// <summary>What the token binds for this restore.</summary>
        public string Key()
        {
            string id = CarrierId.ToString(CultureInfo.InvariantCulture);
            if (Refusal != null) return id + ":not_restored";
            Func<XYZ, string> p = q => string.Join(",", new[] { q.X, q.Y, q.Z }.Select(v => Math.Round(v * 304.8, 1).ToString("0.0", CultureInfo.InvariantCulture)));
            return id + ":" + Action + ":" + State.OriginalTypeId.ToString(CultureInfo.InvariantCulture) + ":" + p(State.OriginalStart) + ";" + p(State.OriginalEnd);
        }
    }

    public sealed partial class FramingCommand
    {
        private static Element RestoreLookup(Document doc, long id) => Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;

        /// <summary>The height a recreated carrier is created with when it has no top level.</summary>
        private static double RecreateHeightFt(CurtainSourceState s) => s.UnconnectedFt > 1e-6 ? s.UnconnectedFt : s.HeightMm / 304.8;

        // ---- remove: plan -----------------------------------------------------------------

        /// <summary>
        /// The carriers of the curtain pieces being removed, each with its restore or the reason it
        /// has none. Read BEFORE the remove's cascade is measured: that rolled-back delete may leave
        /// the pieces' wrappers stale, and a stale wrapper reads no record.
        /// </summary>
        private static List<CurtainRestore> PlanCurtainRestores(Document doc, List<KeyValuePair<Element, FramingMark>> toRemove)
        {
            var restores = new List<CurtainRestore>();
            foreach (IGrouping<long, KeyValuePair<Element, FramingMark>> g in toRemove.GroupBy(x => x.Value.SourceId).OrderBy(x => x.Key))
            {
                JObject record = g.Select(x => FramingCurtainStore.Read(x.Key)).FirstOrDefault(r => r != null);
                if (record == null) continue;   // the member method: its source was never changed
                CurtainSourceState s = CurtainSourceState.FromRecord(record);
                var restore = new CurtainRestore { CarrierId = g.Key, State = s, Action = s.Plan.Carrier.Action };
                // Snapshot NOW (type, position, host): the remove must leave them as they are, and an edit
                // made to a door since the apply is the user's, not a reason to fail the restore.
                var insertIds = new HashSet<long>();
                if (record["inserts"] is JObject inserts)
                    foreach (JProperty kv in inserts.Properties()) insertIds.Add(long.Parse(kv.Name, CultureInfo.InvariantCulture));
                if (RestoreLookup(doc, g.Key) is Wall carrierNow)
                    foreach (ElementId id in carrierNow.FindInserts(true, false, false, false)) insertIds.Add(Rid.Value(id));
                foreach (long id in insertIds.OrderBy(i => i)) restore.Inserts[id] = CurtainInsertState(doc, id);
                restore.Refusal = RestoreRefusal(doc, restore);
                restores.Add(restore);
            }
            return restores;
        }

        /// <summary>Why the carrier cannot be restored as recorded, or null.</summary>
        private static string RestoreRefusal(Document doc, CurtainRestore r)
        {
            CurtainSourceState s = r.State;
            if (!(RestoreLookup(doc, s.OriginalTypeId) is WallType)) return "its original type " + s.OriginalTypeId + " is no longer a wall type of this document";
            if (r.Recreates)
            {
                if (!(RestoreLookup(doc, s.LevelId) is Level)) return "its level " + s.LevelId + " is gone";
                if (s.TopLevelId > 0 && !(RestoreLookup(doc, s.TopLevelId) is Level)) return "its top level " + s.TopLevelId + " is gone";
                return null;
            }
            if (!(RestoreLookup(doc, r.CarrierId) is Wall w) || !(w.Location is LocationCurve lc) || !(lc.Curve is Line l))
                return "the carrier wall is gone or no longer straight";
            // Someone's later edit is not undone by a remove: the carrier must still be as the apply left it.
            string left = s.Plan.Carrier.TypeKey;
            if (Rid.Value(w.GetTypeId()).ToString(CultureInfo.InvariantCulture) != left)
                return "its type changed after the apply (now " + Rid.Value(w.GetTypeId()) + ", the apply left " + left + ")";
            XYZ a = s.NewStart ?? s.OriginalStart, b = s.NewEnd ?? s.OriginalEnd;
            double dev = Math.Max(Flat(l.GetEndPoint(0)).DistanceTo(Flat(a)), Flat(l.GetEndPoint(1)).DistanceTo(Flat(b))) * 304.8;
            return dev > EndpointToleranceMm ? "its location line moved " + Math.Round(dev, 1).ToString(CultureInfo.InvariantCulture) + " mm after the apply" : null;
        }

        /// <summary>What the remove's token binds for the restores ("" when there are none).</summary>
        private static string CurtainRestoreKey(List<CurtainRestore> restores) =>
            restores == null || restores.Count == 0 ? "" : "|restore:" + string.Join(";", restores.Select(r => r.Key()));

        private static JArray CurtainRestoreSummary(List<CurtainRestore> restores)
        {
            Func<XYZ, JArray> mm = q => ModelEditRunner.Arr(q, 1 / 304.8);
            return new JArray(restores.Select(r => new JObject
            {
                ["carrier_id"] = r.CarrierId, ["action_at_apply"] = r.Action,
                ["restore"] = r.Refusal != null ? "none: only the pieces are removed"
                            : r.Recreates ? "recreate the carrier from the record (a new element id)"
                            : "set the carrier back to its original type" + (r.Action == CurtainFramingRoles.CarrierTrim ? " and location line" : ""),
                ["not_restored_because"] = r.Refusal,
                ["original_type_id"] = r.State.OriginalTypeId,
                ["original_line_mm"] = new JArray(mm(r.State.OriginalStart), mm(r.State.OriginalEnd)),
                ["inserts"] = r.Inserts.Count,
                ["not_in_record"] = r.Recreates && r.Refusal == null
                    ? "mark, comments, phase, workset and other instance parameters of the deleted carrier are not restored" : null,
                ["deleted_with_carrier_not_restored"] = r.Recreates && r.Refusal == null ? (r.State.DeletedWithCarrier?.DeepClone() ?? new JObject { ["ids"] = new JArray() }) : null,
            }));
        }

        // ---- remove: write ----------------------------------------------------------------

        /// <summary>Inside the remove's transaction, after the pieces are deleted (nothing then overlaps the carrier).</summary>
        private static void RestoreCarriers(Document doc, List<CurtainRestore> restores)
        {
            if (restores == null) return;
            foreach (CurtainRestore r in restores.Where(x => x.Refusal == null))
            {
                CurtainSourceState s = r.State;
                Line original = Line.CreateBound(s.OriginalStart, s.OriginalEnd);
                ElementId typeId = Rid.Make(s.OriginalTypeId);
                if (!r.Recreates)
                {
                    Wall w = (Wall)doc.GetElement(Rid.Make(r.CarrierId));
                    // The apply left it on its centre plane with the wall centreline as its location line: the
                    // original type widens back about that plane, the full span returns on it, then the
                    // recorded reference and line are restored - whichever of the two Revit moves when the
                    // reference is set, the pair ends as recorded.
                    w.ChangeTypeId(typeId);
                    XYZ centre = s.Normal * s.CentreOffsetFt;
                    ((LocationCurve)w.Location).Curve = Line.CreateBound(s.OriginalStart + centre, s.OriginalEnd + centre);
                    Parameter refParam = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                    if (refParam != null && !refParam.IsReadOnly && refParam.AsInteger() != s.KeyRef) refParam.Set(s.KeyRef);
                    ((LocationCurve)w.Location).Curve = original;
                    continue;
                }
                Wall made;
                try { made = Wall.Create(doc, original, typeId, Rid.Make(s.LevelId), RecreateHeightFt(s), s.BaseOffsetFt, s.Flipped, s.Structural); }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                { throw new InvalidOperationException("recreating carrier " + r.CarrierId + ": " + ex.Message, ex); }
                if (made == null) throw new InvalidOperationException("recreating carrier " + r.CarrierId + ": Revit returned no wall.");
                if (s.TopLevelId > 0)
                {
                    made.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(Rid.Make(s.TopLevelId));
                    made.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(s.TopOffsetFt);
                }
                // The recorded line lies at the ORIGINAL location-line reference. Set the reference,
                // then put the line back: whichever of the two Revit moved, the pair ends as recorded.
                Parameter key = made.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (key != null && !key.IsReadOnly && key.AsInteger() != s.KeyRef)
                {
                    key.Set(s.KeyRef);
                    ((LocationCurve)made.Location).Curve = original;
                }
                r.RecreatedId = Rid.Value(made.Id);
            }
        }

        // ---- remove: verify ---------------------------------------------------------------

        /// <summary>Every restore re-read from the committed model; problems counted per carrier, inserts apart.</summary>
        private static void VerifyCurtainRestores(Document doc, List<CurtainRestore> restores, out int problems, out int insertsChanged, JObject evidence)
        {
            problems = 0; insertsChanged = 0;
            var rows = new JArray();
            foreach (CurtainRestore r in restores)
            {
                var row = new JObject { ["carrier_id"] = r.CarrierId, ["action_at_apply"] = r.Action };
                rows.Add(row);
                if (r.Refusal != null) { row["restored"] = false; row["not_restored_because"] = r.Refusal; continue; }
                CurtainSourceState s = r.State;
                long id = r.Recreates ? r.RecreatedId : r.CarrierId;
                row["wall_id"] = id;
                if (r.Recreates) row["recreated_with_new_id"] = true;
                if (!(RestoreLookup(doc, id) is Wall w) || !(w.Location is LocationCurve lc) || !(lc.Curve is Line l))
                { problems++; row["restored"] = false; row["found"] = false; continue; }
                var bad = new List<string>();
                if (Rid.Value(w.GetTypeId()) != s.OriginalTypeId) bad.Add("type " + Rid.Value(w.GetTypeId()));
                double dev = Math.Max(Flat(l.GetEndPoint(0)).DistanceTo(Flat(s.OriginalStart)), Flat(l.GetEndPoint(1)).DistanceTo(Flat(s.OriginalEnd))) * 304.8;
                row["line_deviation_mm"] = Math.Round(dev, 3);
                if (dev > EndpointToleranceMm) bad.Add("location line");
                if (r.Recreates)
                {
                    Level level = RestoreLookup(doc, s.LevelId) as Level;
                    ElementId baseId = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? w.LevelId;
                    if (baseId == ElementId.InvalidElementId || Rid.Value(baseId) != s.LevelId) bad.Add("base level");
                    double baseDev = level == null ? double.PositiveInfinity : Math.Abs(BaseElevation(doc, w) - (level.ProjectElevation + s.BaseOffsetFt)) * 304.8;
                    double expectedTop;
                    if (s.TopLevelId > 0)
                    {
                        ElementId topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
                        if (topId == ElementId.InvalidElementId || Rid.Value(topId) != s.TopLevelId) bad.Add("top constraint");
                        expectedTop = RestoreLookup(doc, s.TopLevelId) is Level top ? top.ProjectElevation + s.TopOffsetFt : double.NaN;
                    }
                    else expectedTop = level == null ? double.NaN : level.ProjectElevation + s.BaseOffsetFt + RecreateHeightFt(s);
                    double topDev = double.IsNaN(expectedTop) ? double.PositiveInfinity : Math.Abs(TopElevation(doc, w) - expectedTop) * 304.8;
                    row["base_deviation_mm"] = Math.Round(baseDev, 3);
                    row["top_deviation_mm"] = Math.Round(topDev, 3);
                    if (baseDev > EndpointToleranceMm) bad.Add("base elevation");
                    if (topDev > EndpointToleranceMm) bad.Add("top elevation");
                    int keyRef = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? -1;
                    if (keyRef != s.KeyRef) bad.Add("location line reference " + keyRef + " (recorded " + s.KeyRef + ")");
                    if (w.Flipped != s.Flipped) bad.Add("flip");
                    bool structural = (w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() ?? 0) == 1;
                    if (structural != s.Structural) bad.Add("structural");
                    row["not_in_record"] = "mark, comments, phase, workset and other instance parameters";
                    row["deleted_with_carrier_not_restored"] = s.DeletedWithCarrier?.DeepClone() ?? new JObject { ["ids"] = new JArray() };
                }
                else
                {
                    // Against the snapshot taken when the remove was planned: type, position and host.
                    int changed = r.Inserts.Count(kv => CurtainInsertState(doc, kv.Key) != kv.Value);
                    insertsChanged += changed;
                    int keyRefNow = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? -1;
                    if (keyRefNow != s.KeyRef) bad.Add("location line reference " + keyRefNow + " (recorded " + s.KeyRef + ")");
                    row["inserts_checked"] = r.Inserts.Count;
                    row["inserts_changed"] = changed;
                }
                if (bad.Count > 0) { problems++; row["disagrees"] = new JArray(bad.ToArray()); }
                row["restored"] = bad.Count == 0;
            }
            evidence["carrier_restores"] = rows;
        }

        // ---- apply: the carrier's delete cascade ------------------------------------------

        /// <summary>What Revit deletes along with the carrier (MeasureRemoveCascade's rule), in a rolled-back transaction.</summary>
        private static List<long> MeasureCarrierCascade(Document doc, Wall carrier)
        {
            var cascade = new List<long>();
            long cid = Rid.Value(carrier.Id);
            using (var tx = new Transaction(doc, "Horizun: measure carrier delete (rolled back)"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new ArgumentException("wall " + cid + ": the carrier's delete cascade could not be measured (no transaction could start).");
                try
                {
                    foreach (ElementId id in doc.Delete(carrier.Id))
                    {
                        long v = Rid.Value(id);
                        if (v != cid && doc.GetElement(id) == null) cascade.Add(v);
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ArgumentException("wall " + cid + ": the carrier's delete cascade could not be measured (" + ex.Message + "); it is not deleted unmeasured.", ex);
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                }
            }
            cascade.Sort();
            return cascade;
        }

        /// <summary>What the apply's token binds for the carriers it deletes ("" when none).</summary>
        private static string CurtainCascadeKey(List<FramingSourcePlan> plans)
        {
            // A deleted carrier binds its delete's cascade (">"); a kept or trimmed one what its change takes ("~").
            List<string> parts = plans.Where(p => p.Curtain != null && !p.AlreadyApplied)
                .Select(p => p.Curtain.CarrierId.ToString(CultureInfo.InvariantCulture) + (p.Curtain.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete
                    ? ">" + string.Join(",", p.Curtain.CarrierDeleteMeasured.Select(id => id.ToString(CultureInfo.InvariantCulture)))
                    : "~" + string.Join(",", p.Curtain.ChangeLostMeasured.OrderBy(kv => kv.Key).Select(kv => kv.Key.ToString(CultureInfo.InvariantCulture) + ":" + kv.Value))))
                .ToList();
            return parts.Count == 0 ? "" : "|carrier_cascade:" + string.Join(";", parts);
        }

        private static bool DeletesCarrier(FramingSourcePlan p) => p.Curtain != null && !p.AlreadyApplied && p.Curtain.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete;

        /// <summary>The resolved plan's action for a framing source: a curtain carrier the apply deletes is a Delete, not a Modify.</summary>
        private static PlannedAction CurtainSourceAction(FramingSourcePlan p) => DeletesCarrier(p) ? PlannedAction.Delete : PlannedAction.Modify;

        /// <summary>Delete rows for what the carrier's delete takes along (as measured and bound), so plan_resolved counts them.</summary>
        private static IEnumerable<PlannedElement> CurtainCascadeRows(Document doc, FramingSourcePlan p, JObject request)
        {
            if (p.Curtain == null || p.AlreadyApplied) yield break;
            if (DeletesCarrier(p))
            {
                foreach (long id in p.Curtain.CarrierDeleteMeasured)
                    if (RestoreLookup(doc, id) is Element gone) yield return ModelEditRunner.Planned(gone, PlannedAction.Delete, request);
                yield break;
            }
            // A kept or trimmed carrier: what its change deletes, and what it un-hosts (modified).
            foreach (KeyValuePair<long, string> kv in p.Curtain.ChangeLostMeasured)
                if (RestoreLookup(doc, kv.Key) is Element lost) yield return ModelEditRunner.Planned(lost, kv.Value == "deleted" ? PlannedAction.Delete : PlannedAction.Modify, request);
        }

        /// <summary>The restores' resolved-plan rows: a kept or trimmed carrier is modified, a deleted one is created again.</summary>
        private static IEnumerable<PlannedElement> CurtainRestoreRows(Document doc, List<CurtainRestore> restores, JObject request)
        {
            foreach (CurtainRestore r in restores.Where(x => x.Refusal == null))
            {
                if (!r.Recreates) { yield return ModelEditRunner.Planned(doc.GetElement(Rid.Make(r.CarrierId)), PlannedAction.Modify, request); continue; }
                yield return new PlannedElement
                {
                    UniqueId = "recreate_carrier:" + r.CarrierId.ToString(CultureInfo.InvariantCulture), Category = "OST_Walls",
                    TypeName = r.State.OriginalTypeId.ToString(CultureInfo.InvariantCulture), Action = PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>(), ProposedValues = new Dictionary<string, string> { ["restore"] = r.Key() },
                };
            }
        }

        // ---- read -------------------------------------------------------------------------

        /// <summary>A curtain-method source's carrier record for operation=read, or null for the member method.</summary>
        private static JObject CurtainReadRow(Document doc, IEnumerable<Element> members)
        {
            JObject record = members.Select(FramingCurtainStore.Read).FirstOrDefault(r => r != null);
            if (record == null) return null;
            CurtainSourceState s = CurtainSourceState.FromRecord(record);
            Element carrier = RestoreLookup(doc, s.CarrierId);
            Func<XYZ, JArray> mm = q => ModelEditRunner.Arr(q, 1 / 304.8);
            return new JObject
            {
                ["method"] = "curtain",
                ["planned_pieces"] = s.Plan.Pieces.Count,
                ["carrier_action"] = s.Plan.Carrier.Action,
                ["carrier_exists"] = carrier != null,
                ["carrier_type_id"] = carrier == null ? null : (JToken)Rid.Value(carrier.GetTypeId()),
                ["original_type_id"] = s.OriginalTypeId,
                ["original_line_mm"] = new JArray(mm(s.OriginalStart), mm(s.OriginalEnd)),
                ["line_after_apply_mm"] = s.NewStart == null ? null : new JArray(mm(s.NewStart), mm(s.NewEnd)),
                ["inserts_recorded"] = (record["inserts"] as JObject)?.Count ?? 0,
                ["remove_restores"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete
                    ? "recreates the carrier from this record (a new element id)"
                    : "sets the carrier back to its original type" + (s.Plan.Carrier.Action == CurtainFramingRoles.CarrierTrim ? " and location line" : ""),
            };
        }
    }
}
