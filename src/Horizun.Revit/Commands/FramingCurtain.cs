// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing, spec.wall.method = 'curtain': the Revit half.
// Original Horizun code.
//
// WHAT IT BUILDS. The partition core as Curtain Walls of the caller's type (its grid
// and mullions ARE the studs and tracks), cut around the carrier's openings, with a
// header curtain wall above each opening and a sill curtain wall below each window
// (Core/CurtainFramingRules.cs plans them). The pieces run on the carrier's CORE
// CENTRELINE (the core's two faces, WALL_KEY_REF_PARAM-aware, the same arithmetic
// ReadWall uses for a layer), in the carrier's direction so a grid justified at the
// beginning starts where the wall starts.
//
// REVIT API, confirmed in RevitAPI.xml 2023 and 2026 (identical entries):
//   Wall.Create(Document, Curve, ElementId, ElementId, Double, Double, Boolean, Boolean)
//   WallUtils.DisallowWallJoinAtEnd(Wall, Int32)   - a piece keeps its planned ends
//   Element.ChangeTypeId(ElementId); LocationCurve.Curve (set)   - the carrier
//   Wall.CurtainGrid; CurtainGrid.GetUGridLineIds / GetVGridLineIds / GetMullionIds;
//   CurtainGridLine.FullCurve; Mullion.MullionType; Mullion.LocationCurve
//   BuiltInParameter SPACING_LAYOUT_VERT, SPACING_LENGTH_VERT, AUTO_MULLION_* (type)
// A curtain wall's grid is laid out FROM ITS TYPE when it is created (the type's
// layout, spacing and justification per direction, and the AUTO_MULLION_* types on
// the interior and border lines); nothing here edits a grid. The grid is read back
// after doc.Regenerate(). The numeric value of SPACING_LAYOUT_VERT is not documented
// by the API: 1 is read as Fixed Distance (the order of the type dialog's list) and
// the value string is reported beside it, so the live probe confirms the mapping.
//
// THE CARRIER keeps its identity and its inserts (no public API re-hosts a door):
// trimmed to its one opening's span and set to the placeholder type, deleted when it
// had no opening (after the pieces exist), or - several openings, the default
// multi_opening = keep_carrier - kept full length under the pieces, which then
// overlap it: carrier.overlap says so in the plan and in the verified result.
// A kept or trimmed carrier stays on its own CENTRE PLANE, where its doors and windows sit: its
// location line is set to the wall centreline before the type change (a type change keeps the
// location line), so the thinner placeholder does not move them. A carrier with an embedded wall
// (a storefront) is refused: nothing here can verify it stays embedded in the placeholder.
// An insert whose span cannot be measured, or an edited wall profile, refuses the
// wall: trimming or deleting it could take an element nobody planned for with it.
//
// THE RECORD. Each piece carries the FramingMarker (role, index, spec hash, plan
// signature) AND, in its own schema, the RESOLVED PLAN as JSON: the frame, every
// piece, the carrier action, and the carrier's ORIGINAL type, curve, level and
// constraints. A second apply of the same spec re-verifies from that record (the
// carrier it would re-read has already been trimmed or deleted), and remove restores
// the carrier from it. The FramingMarker field list is frozen, hence the new schema.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>The curtain method's resolved plan for one carrier (feet internally, mm in the plan).</summary>
    internal sealed class CurtainSourceState
    {
        public long CarrierId;
        public string CarrierUniqueId;
        public CurtainWallPlan Plan;
        public XYZ Origin, Dir, Normal;
        public double CoreOffsetFt, BaseZFt, BaseOffsetFt, LengthMm, HeightMm;
        public long LevelId, TopLevelId = -1;
        public double TopOffsetFt, UnconnectedFt;
        public long OriginalTypeId;
        public XYZ OriginalStart, OriginalEnd, NewStart, NewEnd;
        public bool Flipped;
        public int KeyRef;
        public List<WallOpeningSpan> Openings = new List<WallOpeningSpan>();
        public List<long> CarrierDeleteCascade = new List<long>();
        /// <summary>The delete's cascade as the plan measured it (the token binds it), and what the delete took that this apply had created.</summary>
        public readonly List<long> CarrierDeleteMeasured = new List<long>(), CarrierDeleteCreated = new List<long>();
        public bool Structural;
        /// <summary>The carrier's centre plane from its location line along Normal (feet): where the placeholder stays.</summary>
        public double CentreOffsetFt;
        /// <summary>What deleting the carrier takes along (ids, by category) as the plan measured it: remove names them as not restored.</summary>
        public JObject DeletedWithCarrier;
        /// <summary>A kept or trimmed carrier's dependents before its change (id -> host id), and what the rehearsed change deleted or un-hosted (id -> deleted | unhosted).</summary>
        public Dictionary<long, long> DependentsBefore;
        public readonly Dictionary<long, string> ChangeLostMeasured = new Dictionary<long, string>();

        /// <summary>A point of the frame on the core centreline, at the base level's elevation.</summary>
        public XYZ At(double xMm, double levelZ) => new XYZ(Origin.X, Origin.Y, levelZ) + Dir * (xMm / 304.8) + Normal * CoreOffsetFt;

        public JObject ToRecord(string specHash, string signature, IDictionary<long, string> inserts) => new JObject
        {
            ["v"] = 1, ["carrier_id"] = CarrierId, ["carrier_uid"] = CarrierUniqueId, ["spec_hash"] = specHash, ["signature"] = signature,
            ["origin"] = P(Origin), ["dir"] = P(Dir), ["normal"] = P(Normal), ["core_offset_ft"] = CoreOffsetFt, ["base_z_ft"] = BaseZFt,
            ["base_offset_ft"] = BaseOffsetFt, ["length_mm"] = LengthMm, ["height_mm"] = HeightMm, ["level_id"] = LevelId,
            ["top_level_id"] = TopLevelId, ["top_offset_ft"] = TopOffsetFt, ["unconnected_ft"] = UnconnectedFt,
            ["original_type_id"] = OriginalTypeId, ["original_curve"] = new JArray(P(OriginalStart), P(OriginalEnd)),
            ["new_curve"] = NewStart == null ? null : new JArray(P(NewStart), P(NewEnd)), ["flipped"] = Flipped, ["key_ref"] = KeyRef, ["structural"] = Structural,
            ["centre_offset_ft"] = CentreOffsetFt, ["deleted_with_carrier"] = DeletedWithCarrier?.DeepClone(),
            ["pieces"] = new JArray(Plan.Pieces.Select(m => new JObject { ["role"] = m.Role, ["type"] = m.TypeKey, ["x0"] = m.X0, ["x1"] = m.X1, ["z0"] = m.Z0, ["z1"] = m.Z1, ["src"] = m.Source })),
            ["carrier"] = new JObject { ["action"] = Plan.Carrier.Action, ["x0"] = Plan.Carrier.X0, ["x1"] = Plan.Carrier.X1, ["type"] = Plan.Carrier.TypeKey, ["opening"] = Plan.Carrier.OpeningId },
            ["inserts"] = new JObject(inserts.Select(kv => new JProperty(kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value))),
        };

        public static CurtainSourceState FromRecord(JObject r)
        {
            var plan = new CurtainWallPlan();
            foreach (JObject m in r["pieces"] as JArray ?? new JArray())
                plan.Pieces.Add(new FramingMember { Role = (string)m["role"], TypeKey = (string)m["type"], X0 = (double)m["x0"], X1 = (double)m["x1"], Z0 = (double)m["z0"], Z1 = (double)m["z1"], Source = (int)m["src"] });
            JObject c = (JObject)r["carrier"];
            plan.Carrier = new CurtainCarrierAction { Action = (string)c["action"], X0 = (double)c["x0"], X1 = (double)c["x1"], TypeKey = (string)c["type"], OpeningId = (string)c["opening"] };
            JArray nc = r["new_curve"] as JArray;
            return new CurtainSourceState
            {
                CarrierId = (long)r["carrier_id"], CarrierUniqueId = (string)r["carrier_uid"], Plan = plan,
                Origin = X(r["origin"]), Dir = X(r["dir"]), Normal = X(r["normal"]), CoreOffsetFt = (double)r["core_offset_ft"], BaseZFt = (double)r["base_z_ft"],
                BaseOffsetFt = (double)r["base_offset_ft"], LengthMm = (double)r["length_mm"], HeightMm = (double)r["height_mm"], LevelId = (long)r["level_id"],
                TopLevelId = (long)r["top_level_id"], TopOffsetFt = (double)r["top_offset_ft"], UnconnectedFt = (double)r["unconnected_ft"],
                OriginalTypeId = (long)r["original_type_id"], OriginalStart = X(r["original_curve"][0]), OriginalEnd = X(r["original_curve"][1]),
                NewStart = nc == null ? null : X(nc[0]), NewEnd = nc == null ? null : X(nc[1]), Flipped = (bool)r["flipped"], KeyRef = (int)r["key_ref"], Structural = (bool?)r["structural"] ?? false,
                CentreOffsetFt = (double?)r["centre_offset_ft"] ?? 0, DeletedWithCarrier = r["deleted_with_carrier"] as JObject,
            };
        }

        private static JArray P(XYZ p) => new JArray(p.X, p.Y, p.Z);
        private static XYZ X(JToken t) => new XYZ((double)t[0], (double)t[1], (double)t[2]);
    }

    /// <summary>The resolved curtain plan on every piece (see the header): read by idempotence and remove.</summary>
    internal static class FramingCurtainStore
    {
        public static readonly Guid SchemaGuid = new Guid("5e0c7a93-2d41-4b8f-a6c3-8f19d2b07e54");
        public const string SchemaName = "HorizunFramingCurtainV1";
        private const string FVersion = "SchemaVersion", FCarrier = "CarrierId", FRecord = "Record", FMemberUid = "MemberUniqueId";
        private static Schema _cached;

        private static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return _cached; }
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.SetVendorId(CadProvenanceStore.VendorId);
            b.SetDocumentation("Horizun framing (curtain method): the resolved plan and the carrier's original state, for re-verification and remove.");
            b.AddSimpleField(FVersion, typeof(int));
            foreach (string f in new[] { FCarrier, FRecord, FMemberUid }) b.AddSimpleField(f, typeof(string));
            _cached = b.Finish();
            return _cached;
        }

        public static void Write(Element e, long carrierId, JObject record)
        {
            var entity = new Entity(GetOrCreate());
            entity.Set(FVersion, 1);
            entity.Set(FCarrier, carrierId.ToString(CultureInfo.InvariantCulture));
            entity.Set(FRecord, record.ToString(Newtonsoft.Json.Formatting.None));
            entity.Set(FMemberUid, e.UniqueId ?? "");
            e.SetEntity(entity);
        }

        /// <summary>The record on an element that is ours (its own UniqueId matches), else null.</summary>
        public static JObject Read(Element e)
        {
            try
            {
                Schema s = Schema.Lookup(SchemaGuid);
                if (s == null || e == null) return null;
                Entity en = e.GetEntity(s);
                if (en == null || !en.IsValid() || !string.Equals(en.Get<string>(FMemberUid), e.UniqueId, StringComparison.Ordinal)) return null;
                return JObject.Parse(en.Get<string>(FRecord));
            }
            catch { return null; }
        }
    }

    public sealed partial class FramingCommand
    {
        private const int MaxCurtainPiecesTotal = 2000;

        private static List<FramingSourcePlan> PlanCurtainWalls(Document doc, JObject request, CurtainWallFramingSpec spec, string specHash, List<string> skipped)
        {
            // By ROLE, not by distinct id: an id given for two roles is checked as both (the parser
            // already refuses a curtain, header or sill type equal to the placeholder).
            var roles = new List<KeyValuePair<string, long>>
            {
                new KeyValuePair<string, long>("curtain_type_id", spec.CurtainTypeId), new KeyValuePair<string, long>("placeholder_type_id", spec.PlaceholderTypeId)
            };
            if (spec.HeaderTypeId.HasValue) roles.Add(new KeyValuePair<string, long>("header_type_id", spec.HeaderTypeId.Value));
            if (spec.SillTypeId.HasValue) roles.Add(new KeyValuePair<string, long>("sill_type_id", spec.SillTypeId.Value));
            foreach (KeyValuePair<string, long> role in roles)
            {
                long id = role.Value;
                WallType t = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as WallType : null;
                bool placeholder = role.Key == "placeholder_type_id";
                if (t == null) throw new ArgumentException("spec.wall." + role.Key + " " + id + " is not a wall type of this document.");
                if (placeholder && t.Kind != WallKind.Basic) throw new ArgumentException("placeholder_type_id " + id + " is a " + t.Kind + " wall type; the placeholder is a Basic wall type.");
                if (!placeholder && t.Kind != WallKind.Curtain) throw new ArgumentException(role.Key + " " + id + " is a " + t.Kind + " wall type; curtain, header and sill types are Curtain Wall types.");
            }
            var plans = new List<FramingSourcePlan>();
            int total = 0;
            HashSet<long> ids = SourceIds(request);
            var walls = new List<Wall>();
            if (ids != null)
            {
                foreach (long id in ids.OrderBy(i => i))
                {
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (e is Wall w) { walls.Add(w); continue; }
                    // A carrier the curtain method deleted: its pieces carry the plan it was replaced by.
                    FramingSourcePlan fromRecord = e == null ? CurtainFromRecord(doc, id, null, specHash) : null;
                    if (fromRecord == null) throw new ArgumentException("element " + id + " is " + (e == null ? "not an element of this document" : "not a wall") + ".");
                    plans.Add(fromRecord);
                }
            }
            else walls = Sources<Wall>(doc, request, "wall", WallOutOfScope, skipped);

            foreach (Wall wall in walls)
            {
                long sid = Rid.Value(wall.Id);
                FramingSourcePlan existing = CurtainFromRecord(doc, sid, wall, specHash);
                if (existing != null) { plans.Add(existing); continue; }
                string who = "wall " + sid;
                if (wall.SketchId != ElementId.InvalidElementId) throw new ArgumentException(who + " has an edited profile; the curtain method trims or deletes the carrier and refuses a profile it cannot keep.");
                FramedWall fw = ReadWall(doc, wall, new WallFramingSpec(), out string refusal);
                if (fw == null) throw new ArgumentException(refusal);
                List<long> embeddedWalls = fw.InsertIds.Where(i => doc.GetElement(Rid.Make(i)) is Wall).ToList();
                if (embeddedWalls.Count > 0)
                    throw new ArgumentException(who + " hosts embedded wall(s) " + string.Join(", ", embeddedWalls) + " (a storefront): the placeholder keeps doors, windows and openings, "
                                                + "but nothing here can verify an embedded wall stays embedded in it. Split the wall at the embedded wall, or unembed it, first.");
                if (fw.InsertIds.Count != fw.OpeningsMm.Count)
                    throw new ArgumentException(who + " hosts " + fw.InsertIds.Count + " insert(s) but only " + fw.OpeningsMm.Count + " have a measurable span; the carrier cannot be trimmed or deleted safely.");
                var s = new CurtainSourceState
                {
                    CarrierId = sid, CarrierUniqueId = wall.UniqueId, Origin = fw.Origin, Dir = fw.Dir, Normal = fw.Normal,
                    BaseZFt = fw.BaseZ, LengthMm = fw.LengthMm, HeightMm = fw.HeightMm, LevelId = Rid.Value(fw.Level.Id),
                    BaseOffsetFt = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0,
                    OriginalTypeId = Rid.Value(wall.GetTypeId()), Flipped = wall.Flipped,
                    KeyRef = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0,
                    Structural = (wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() ?? 0) == 1,
                    UnconnectedFt = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0,
                    Openings = fw.OpeningsMm.ToList(),
                };
                Line original = (Line)((LocationCurve)wall.Location).Curve;
                s.OriginalStart = original.GetEndPoint(0);
                s.OriginalEnd = original.GetEndPoint(1);
                ElementId topId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
                if (topId != ElementId.InvalidElementId && doc.GetElement(topId) is Level)
                {
                    s.TopLevelId = Rid.Value(topId);
                    s.TopOffsetFt = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0;
                }
                var p = new FramingSourcePlan { Source = wall, Operation = "wall", Wall = fw, SpecHash = specHash, Curtain = s };
                p.Warnings.AddRange(fw.Warnings);
                double? core = CoreCentreOffset(wall, fw.CentreFromCurve);
                if (core.HasValue) s.CoreOffsetFt = core.Value;
                else { s.CoreOffsetFt = fw.LayerOffset; p.Warnings.Add(who + ": the type has no core; the pieces run on the thickest layer's centre"); }

                CurtainWallPlan plan = CurtainFramingRules.PlanWall(spec.ToInput(fw.LengthMm, fw.HeightMm, fw.OpeningsMm));
                if (!string.IsNullOrEmpty(plan.Refusal)) throw new ArgumentException(who + ": " + plan.Refusal);
                s.Plan = plan;
                if (plan.Carrier.Action != CurtainFramingRoles.CarrierDelete)
                {
                    // The placeholder stays centred where the carrier is centred - its doors and windows sit
                    // on that plane: the apply sets the location line to the wall centreline BEFORE the type
                    // change (a type change keeps the location line), then asserts this line on that plane.
                    Parameter keyRef = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                    if (s.KeyRef != (int)WallLocationLine.WallCenterline && (keyRef == null || keyRef.IsReadOnly))
                        throw new ArgumentException(who + ": its location line cannot be set to the wall centreline, so the placeholder could not keep its inserts where they are.");
                    // Measured on the side faces (FramingWall.MeasuredCentreFromCurve), not deduced from
                    // the location line parameter the curve may no longer sit on.
                    s.CentreOffsetFt = fw.CentreFromCurve;
                    XYZ centre = fw.Normal * s.CentreOffsetFt;
                    if (plan.Carrier.Action == CurtainFramingRoles.CarrierTrim)
                    {
                        XYZ o = new XYZ(fw.Origin.X, fw.Origin.Y, s.OriginalStart.Z) + centre;
                        s.NewStart = o + fw.Dir * (plan.Carrier.X0 / 304.8);
                        s.NewEnd = o + fw.Dir * (plan.Carrier.X1 / 304.8);
                    }
                    else { s.NewStart = s.OriginalStart + centre; s.NewEnd = s.OriginalEnd + centre; }
                    double apart = Math.Abs(s.CoreOffsetFt - s.CentreOffsetFt) * 304.8;
                    if (apart > EndpointToleranceMm)
                        p.Warnings.Add(who + ": the placeholder stays on the carrier's centre plane (its inserts sit there), " + Math.Round(apart, 1).ToString(CultureInfo.InvariantCulture)
                                       + " mm from the pieces' core centreline (carrier.placeholder_offset_from_pieces_mm)");
                }
                if (!spec.HeaderTypeId.HasValue && plan.Pieces.Any(m => m.Role == CurtainFramingRoles.Header)) p.Warnings.Add("header_type_id not given: headers use the curtain type");
                if (!spec.SillTypeId.HasValue && plan.Pieces.Any(m => m.Role == CurtainFramingRoles.Sill)) p.Warnings.Add("sill_type_id not given: sills use the curtain type");
                if (plan.Carrier.Action != CurtainFramingRoles.CarrierDelete)
                    foreach (string key in plan.Pieces.Select(m => m.TypeKey).Distinct())
                        if ((doc.GetElement(Rid.Make(long.Parse(key, CultureInfo.InvariantCulture))) as WallType)?.get_Parameter(BuiltInParameter.ALLOW_AUTO_EMBED)?.AsInteger() == 1)
                            p.Warnings.Add("type " + key + " has Automatically Embed on: Revit may embed a piece in the carrier it overlaps, and the apply then rolls back (pieces_not_embedded); turn it off on the type");
                p.Warnings.AddRange(plan.Warnings);
                p.Members = plan.Pieces;
                p.Signature = plan.Signature();
                total += p.Members.Count;
                if (total > MaxCurtainPiecesTotal) throw new ArgumentException("the plan exceeds " + MaxCurtainPiecesTotal + " curtain walls; frame fewer walls per call.");
                foreach (long insert in fw.InsertIds) p.InsertsBefore[insert] = CurtainInsertState(doc, insert);
                if (plan.Carrier.Action != CurtainFramingRoles.CarrierDelete)
                {
                    // What the change (location line, placeholder type, trimmed line) deletes or un-hosts among
                    // the carrier's dependents, rehearsed and rolled back; measured last like the delete's
                    // cascade, and the source read again afterwards.
                    s.DependentsBefore = CarrierDependents(doc, wall, p.InsertsBefore.Keys);
                    foreach (KeyValuePair<long, string> kv in MeasureCarrierChange(doc, wall, s)) s.ChangeLostMeasured[kv.Key] = kv.Value;
                    p.Source = doc.GetElement(Rid.Make(sid)) ?? p.Source;
                    if (s.ChangeLostMeasured.Count > 0)
                        p.Warnings.Add("changing the carrier (" + plan.Carrier.Action + ") deletes or un-hosts " + s.ChangeLostMeasured.Count + " element(s) that depend on it (carrier.change_takes); the token binds them");
                }
                if (plan.Carrier.Action == CurtainFramingRoles.CarrierDelete)
                {
                    // Measured last (FramingCurtainRemove.cs): the rolled-back delete may leave this
                    // wall's wrapper stale, so the source is read again afterwards.
                    s.CarrierDeleteMeasured.AddRange(MeasureCarrierCascade(doc, wall));
                    p.Source = doc.GetElement(Rid.Make(sid)) ?? p.Source;
                    s.DeletedWithCarrier = new JObject
                    {
                        ["ids"] = new JArray(s.CarrierDeleteMeasured),
                        ["by_category"] = JObject.FromObject(s.CarrierDeleteMeasured.GroupBy(id => CategoryLabel(doc, id)).ToDictionary(g => g.Key, g => g.Count())),
                    };
                    if (s.CarrierDeleteMeasured.Count > 0)
                        p.Warnings.Add("deleting the carrier also deletes " + s.CarrierDeleteMeasured.Count + " element(s) Revit hosts on or ties to it (carrier.deleted_with_it); the token binds them");
                }
                plans.Add(p);
            }
            foreach (FramingSourcePlan p in plans)
                if (p.Curtain.Plan.Skipped.Count > 0) skipped.AddRange(p.Curtain.Plan.Skipped.Select(x => "wall " + p.Curtain.CarrierId + ": " + x));
            return plans;
        }

        /// <summary>The core's centre from the location curve along Wall.Orientation (feet), or null when the type has no core. <paramref name="centreFromCurve"/> is the measured centre plane.</summary>
        private static double? CoreCentreOffset(Wall wall, double centreFromCurve)
        {
            CompoundStructure cs = wall.WallType?.GetCompoundStructure();
            IList<CompoundStructureLayer> layers = cs?.GetLayers();
            if (layers == null || layers.Count == 0) return null;
            int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
            if (first < 0 || last < first || last >= layers.Count) return null;
            double total = cs.GetWidth();
            Func<int, double> faceAfter = k => total / 2 - layers.Take(k).Sum(l => l.Width);
            double coreExt = faceAfter(first), coreInt = faceAfter(last + 1);
            return centreFromCurve + (coreExt + coreInt) / 2;
        }

        /// <summary>An insert's type, position AND host, as the curtain method snapshots it and re-reads it.</summary>
        private static string CurtainInsertState(Document doc, long id)
        {
            Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
            long host = e is FamilyInstance fi && fi.Host != null ? Rid.Value(fi.Host.Id) : e is Opening o && o.Host != null ? Rid.Value(o.Host.Id) : -1;
            return InsertState(doc, id) + "|host " + host.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Earlier curtain framing of this carrier: the same spec re-verifies from the record its
        /// pieces carry (already_applied); another spec, or pieces missing, refuses - remove first.
        /// Null when the carrier carries none.
        /// </summary>
        private static FramingSourcePlan CurtainFromRecord(Document doc, long carrierId, Wall carrier, string specHash)
        {
            List<KeyValuePair<Element, FramingMark>> members = FramingMarker.Find(doc, new HashSet<long> { carrierId })
                .Where(x => x.Value.Role != FramingMarker.WorkPlaneRole).ToList();
            if (members.Count == 0) return null;
            JObject record = members.Select(x => FramingCurtainStore.Read(x.Key)).FirstOrDefault(r => r != null);
            if (record == null || members.Any(x => x.Value.SpecHash != specHash) || (string)record["spec_hash"] != specHash)
                throw new ArgumentException("wall " + carrierId + " already carries " + members.Count + " horizun_framing member(s) from another spec or method; run operation=remove for it first.");
            CurtainSourceState s = CurtainSourceState.FromRecord(record);
            string signature = (string)record["signature"];
            if (members.Any(x => x.Value.PlanSignature != signature) || members.Count != s.Plan.Pieces.Count || members.Select(x => x.Value.Index).Distinct().Count() != members.Count)
                throw new ArgumentException("wall " + carrierId + " carries " + members.Count + " curtain piece(s) of this spec where its plan placed " + s.Plan.Pieces.Count + " (pieces were deleted or copied since); run operation=remove for it first.");
            var p = new FramingSourcePlan
            {
                Source = (Element)carrier ?? members[0].Key, Operation = "wall", SpecHash = specHash, Curtain = s,
                Members = s.Plan.Pieces, Signature = signature, AlreadyApplied = true
            };
            foreach (KeyValuePair<Element, FramingMark> x in members) p.MemberIds[x.Value.Index] = Rid.Value(x.Key.Id);
            // Snapshot NOW: a re-apply writes nothing, and an edit made to a door since the first apply is
            // the user's, not a disagreement with this plan (the record keeps the state before the first apply).
            if (record["inserts"] is JObject inserts)
                foreach (JProperty kv in inserts.Properties()) { long id = long.Parse(kv.Name, CultureInfo.InvariantCulture); p.InsertsBefore[id] = CurtainInsertState(doc, id); }
            return p;
        }

        // ---- writing --------------------------------------------------------------------

        private static void PlaceCurtainSource(Document doc, FramingSourcePlan p)
        {
            CurtainSourceState s = p.Curtain;
            Level level = doc.GetElement(Rid.Make(s.LevelId)) as Level ?? throw new InvalidOperationException("the carrier's level is gone");
            JObject record = s.ToRecord(p.SpecHash, p.Signature, p.InsertsBefore);
            for (int i = 0; i < p.Members.Count; i++)
            {
                FramingMember m = p.Members[i];
                Line line = Line.CreateBound(s.At(m.X0, level.ProjectElevation), s.At(m.X1, level.ProjectElevation));
                Wall w;
                try { w = Wall.Create(doc, line, Rid.Make(long.Parse(m.TypeKey, CultureInfo.InvariantCulture)), level.Id, (m.Z1 - m.Z0) / 304.8, s.BaseOffsetFt + m.Z0 / 304.8, false, false); }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                { throw new InvalidOperationException(m.Role + " " + i + " (type " + m.TypeKey + ", x " + Math.Round(m.X0, 1).ToString(CultureInfo.InvariantCulture) + " -> " + Math.Round(m.X1, 1).ToString(CultureInfo.InvariantCulture) + " mm): " + ex.Message, ex); }
                if (w == null) throw new InvalidOperationException(m.Role + " " + i + ": Revit returned no wall.");
                // Segments and headers end where the carrier ends: its top constraint, not a copied number.
                if (m.Role != CurtainFramingRoles.Sill && s.TopLevelId > 0)
                {
                    w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(Rid.Make(s.TopLevelId));
                    w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(s.TopOffsetFt);
                }
                WallUtils.DisallowWallJoinAtEnd(w, 0);
                WallUtils.DisallowWallJoinAtEnd(w, 1);
                FramingMarker.Write(w, new FramingMark { SourceId = s.CarrierId, SourceUniqueId = s.CarrierUniqueId, Role = m.Role, Index = i, SpecHash = p.SpecHash, PlanSignature = p.Signature, Operation = "wall" });
                FramingCurtainStore.Write(w, s.CarrierId, record);
                p.MemberIds[i] = Rid.Value(w.Id);
            }
            Wall carrier = doc.GetElement(Rid.Make(s.CarrierId)) as Wall ?? throw new InvalidOperationException("the carrier wall is gone");
            string action = s.Plan.Carrier.Action;
            if (action == CurtainFramingRoles.CarrierDelete)
            {
                // What the delete took, split: elements that existed before this apply (compared with
                // the cascade the plan measured and the token bound) and elements this apply created
                // (ids from the first piece on - Revit hands out ids in increasing order), which the
                // piece checks judge.
                long firstCreated = p.MemberIds.Count > 0 ? p.MemberIds.Values.Min() : long.MaxValue;
                s.CarrierDeleteCascade.Clear();
                s.CarrierDeleteCreated.Clear();
                foreach (long id in doc.Delete(carrier.Id).Select(Rid.Value).Where(id => id != s.CarrierId && doc.GetElement(Rid.Make(id)) == null).OrderBy(id => id))
                    (id >= firstCreated ? s.CarrierDeleteCreated : s.CarrierDeleteCascade).Add(id);
                return;
            }
            // Location line to the wall centreline FIRST: a type change keeps the location line, so the
            // placeholder then narrows about the carrier's centre plane, where its doors and windows sit.
            // Then the planned line (on that plane) is asserted: whether setting the reference moved the
            // curve or the wall, the pair ends as planned (WallSplitExecutor's rule).
            ChangeCarrier(carrier, s);
        }

        /// <summary>The kept or trimmed carrier's change: the same in the apply and in its rolled-back rehearsal (MeasureCarrierChange).</summary>
        private static void ChangeCarrier(Wall carrier, CurtainSourceState s)
        {
            Parameter keyRef = carrier.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
            if (keyRef != null && keyRef.AsInteger() != (int)WallLocationLine.WallCenterline) keyRef.Set((int)WallLocationLine.WallCenterline);
            carrier.ChangeTypeId(Rid.Make(long.Parse(s.Plan.Carrier.TypeKey, CultureInfo.InvariantCulture)));
            ((LocationCurve)carrier.Location).Curve = Line.CreateBound(s.NewStart, s.NewEnd);
        }

        /// <summary>
        /// What the carrier's planned change deletes or un-hosts among its dependents
        /// (Element.GetDependentElements, its inserts apart): hosted or face-based families on the length
        /// a trim removes or on faces a thinner type moves, sweeps, reveals. A rolled-back transaction.
        /// </summary>
        private static Dictionary<long, string> MeasureCarrierChange(Document doc, Wall carrier, CurtainSourceState s)
        {
            long cid = Rid.Value(carrier.Id);
            using (var tx = new Transaction(doc, "Horizun: measure carrier change (rolled back)"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new ArgumentException("wall " + cid + ": the carrier's change could not be measured (no transaction could start).");
                try
                {
                    ChangeCarrier(carrier, s);
                    doc.Regenerate();
                    return LostDependents(doc, cid, s.DependentsBefore);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ArgumentException("wall " + cid + ": the carrier's change could not be rehearsed (" + ex.Message + "); it is not changed unmeasured.", ex);
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                }
            }
        }

        /// <summary>The carrier's dependents other than itself and its inserts: id -> the id hosting it (-1: none).</summary>
        private static Dictionary<long, long> CarrierDependents(Document doc, Wall carrier, ICollection<long> inserts)
        {
            long cid = Rid.Value(carrier.Id);
            var map = new Dictionary<long, long>();
            foreach (ElementId id in carrier.GetDependentElements(null))
            {
                long v = Rid.Value(id);
                if (v != cid && !inserts.Contains(v)) map[v] = DependentHost(doc.GetElement(id));
            }
            return map;
        }

        private static long DependentHost(Element e) => e is FamilyInstance fi && fi.Host != null ? Rid.Value(fi.Host.Id) : -1;

        /// <summary>Dependents gone (deleted), or no longer hosted by the carrier that hosted them (unhosted).</summary>
        private static Dictionary<long, string> LostDependents(Document doc, long carrierId, Dictionary<long, long> before)
        {
            var lost = new Dictionary<long, string>();
            foreach (KeyValuePair<long, long> kv in before ?? new Dictionary<long, long>())
            {
                Element e = doc.GetElement(Rid.Make(kv.Key));
                if (e == null) lost[kv.Key] = "deleted";
                else if (kv.Value == carrierId && DependentHost(e) != carrierId) lost[kv.Key] = "unhosted";
            }
            return lost;
        }

        // ---- verifying ------------------------------------------------------------------

        private static PostconditionCheck VerifyCurtainWalls(Document doc, List<FramingSourcePlan> plans, JObject evidence)
        {
            var check = new PostconditionCheck("curtain_wall_count", "curtain_types", "curtain_location", "curtain_base_top", "grid_spacing", "mullion_types", "carrier", "inserts_untouched", "carrier_cascade_as_measured", "pieces_not_embedded", "carrier_change_as_measured");
            int planned = 0, found = 0, wrongType = 0, gridProblems = 0, mullionProblems = 0, carrierProblems = 0, insertsChanged = 0, cascadeDiffers = 0, embeddedWalls = 0, changeDiffers = 0;
            double maxLoc = 0, maxBaseTop = 0;
            var rows = new JArray();
            foreach (FramingSourcePlan p in plans)
            {
                CurtainSourceState s = p.Curtain;
                var byIndex = FramingMarker.Find(doc, new HashSet<long> { s.CarrierId })
                    .Where(x => x.Value.SpecHash == p.SpecHash && x.Value.PlanSignature == p.Signature)
                    .GroupBy(x => x.Value.Index).ToDictionary(g => g.Key, g => g.ToList());
                Level level = doc.GetElement(Rid.Make(s.LevelId)) as Level;
                var pieces = new JArray();
                for (int i = 0; i < p.Members.Count; i++)
                {
                    FramingMember m = p.Members[i];
                    planned++;
                    if (!byIndex.TryGetValue(i, out var list) || list.Count != 1 || list[0].Value.Role != m.Role || !(list[0].Key is Wall w)) continue;
                    found++;
                    var row = new JObject { ["i"] = i, ["role"] = m.Role, ["id"] = Rid.Value(w.Id) };
                    if (Rid.Value(w.GetTypeId()).ToString(CultureInfo.InvariantCulture) != m.TypeKey || w.WallType?.Kind != WallKind.Curtain) wrongType++;
                    if (level != null && w.Location is LocationCurve lc && lc.Curve is Line l)
                    {
                        XYZ a = s.At(m.X0, 0), b = s.At(m.X1, 0);
                        XYZ ra = Flat(l.GetEndPoint(0)), rb = Flat(l.GetEndPoint(1));
                        double dev = Math.Max(ra.DistanceTo(Flat(a)), rb.DistanceTo(Flat(b))) * 304.8;
                        maxLoc = Math.Max(maxLoc, dev);
                        row["location_deviation_mm"] = Math.Round(dev, 3);
                        double baseDev = Math.Abs(BaseElevation(doc, w) - (s.BaseZFt + m.Z0 / 304.8)) * 304.8;
                        double topDev = Math.Abs(TopElevation(doc, w) - (s.BaseZFt + m.Z1 / 304.8)) * 304.8;
                        maxBaseTop = Math.Max(maxBaseTop, Math.Max(baseDev, topDev));
                        row["base_deviation_mm"] = Math.Round(baseDev, 3);
                        row["top_deviation_mm"] = Math.Round(topDev, 3);
                        JObject grid = ReadCurtainWallGrid(doc, w, l, out int gp, out int mp);
                        gridProblems += gp; mullionProblems += mp;
                        row["grid"] = grid;
                    }
                    else { maxLoc = double.PositiveInfinity; row["location"] = "unreadable"; }
                    pieces.Add(row);
                }
                // The carrier: gone, or its planned type and curve, its inserts still its own.
                Element carrierElement = doc.GetElement(Rid.Make(s.CarrierId));
                var carrierRow = new JObject { ["id"] = s.CarrierId, ["action"] = s.Plan.Carrier.Action };
                if (s.Plan.Carrier.Action == CurtainFramingRoles.CarrierKeep)
                {
                    // Never silent: the kept carrier and the pieces occupy the same wall line.
                    carrierRow["overlap"] = CurtainFramingRules.OverlapNote();
                    carrierRow["overlapped_by"] = new JArray(p.MemberIds.OrderBy(kv => kv.Key).Select(kv => kv.Value));
                }
                int changed = 0;
                if (s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete)
                {
                    carrierRow["deleted"] = carrierElement == null;
                    if (carrierElement != null) carrierProblems++;
                    if (!p.AlreadyApplied)
                    {
                        // Revit must have taken exactly the pre-existing elements the token bound.
                        var measured = new HashSet<long>(s.CarrierDeleteMeasured);
                        var took = new HashSet<long>(s.CarrierDeleteCascade);
                        cascadeDiffers += took.Count(id => !measured.Contains(id)) + measured.Count(id => !took.Contains(id));
                        carrierRow["deleted_with_it"] = new JArray(s.CarrierDeleteCascade);
                        carrierRow["deleted_with_it_measured"] = new JArray(s.CarrierDeleteMeasured);
                        if (s.CarrierDeleteCreated.Count > 0) carrierRow["deleted_with_it_created_by_this_apply"] = new JArray(s.CarrierDeleteCreated);
                    }
                }
                else if (!(carrierElement is Wall carrier) || !(carrier.Location is LocationCurve clc) || !(clc.Curve is Line cl)) { carrierProblems++; carrierRow["found"] = false; }
                else
                {
                    bool typeOk = Rid.Value(carrier.GetTypeId()).ToString(CultureInfo.InvariantCulture) == s.Plan.Carrier.TypeKey;
                    XYZ ea = s.NewStart ?? s.OriginalStart, eb = s.NewEnd ?? s.OriginalEnd;
                    double dev = Math.Max(Flat(cl.GetEndPoint(0)).DistanceTo(Flat(ea)), Flat(cl.GetEndPoint(1)).DistanceTo(Flat(eb))) * 304.8;
                    carrierRow["type_ok"] = typeOk;
                    carrierRow["curve_deviation_mm"] = Math.Round(dev, 3);
                    if (!typeOk || dev > EndpointToleranceMm) carrierProblems++;
                    // Each insert against its snapshot - type, position and host: taken before this apply, or
                    // at this call for a re-apply (CurtainFromRecord).
                    changed = p.InsertsBefore.Count(kv => CurtainInsertState(doc, kv.Key) != kv.Value);
                    int keyNow = carrier.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? -1;
                    carrierRow["location_line"] = keyNow;
                    if (keyNow != (int)WallLocationLine.WallCenterline) carrierProblems++;
                    carrierRow["placeholder_offset_from_pieces_mm"] = Math.Round((s.CoreOffsetFt - s.CentreOffsetFt) * 304.8, 1);
                    // Automatically Embed cuts a piece into the wall it overlaps; the carrier was to stay whole
                    // (the plan refuses a carrier that had an embedded wall, so every one found here is new).
                    List<long> embedded = carrier.FindInserts(false, false, true, false).Where(id => doc.GetElement(id) is Wall).Select(Rid.Value).OrderBy(id => id).ToList();
                    carrierRow["embedded_walls"] = new JArray(embedded);
                    embeddedWalls += embedded.Count;
                    if (!p.AlreadyApplied && s.DependentsBefore != null)
                    {
                        // The change must have taken exactly what its rehearsal measured and the token bound.
                        Dictionary<long, string> took = LostDependents(doc, s.CarrierId, s.DependentsBefore);
                        changeDiffers += took.Count(kv => !s.ChangeLostMeasured.TryGetValue(kv.Key, out string was) || was != kv.Value) + s.ChangeLostMeasured.Keys.Count(id => !took.ContainsKey(id));
                        carrierRow["change_took"] = JObject.FromObject(took);
                        carrierRow["change_took_measured"] = JObject.FromObject(s.ChangeLostMeasured);
                    }
                }
                insertsChanged += changed;
                carrierRow["inserts_checked"] = p.InsertsBefore.Count;
                carrierRow["inserts_changed"] = changed;
                rows.Add(new JObject
                {
                    ["source_id"] = s.CarrierId, ["already_applied"] = p.AlreadyApplied, ["pieces"] = pieces, ["carrier"] = carrierRow,
                    ["piece_ids"] = new JArray(p.MemberIds.OrderBy(kv => kv.Key).Select(kv => kv.Value))
                });
            }
            check.Compare("curtain_wall_count", planned, found);
            check.Compare("curtain_types", 0, wrongType);
            check.Measure("curtain_location", 0, maxLoc, EndpointToleranceMm, "mm", "max over pieces of an end's horizontal distance to the planned core-centreline end");
            check.Measure("curtain_base_top", 0, maxBaseTop, EndpointToleranceMm, "mm", "max over pieces of |base or top elevation - plan|, read from the level and offset parameters");
            check.Compare("grid_spacing", 0, gridProblems);
            check.Compare("mullion_types", 0, mullionProblems);
            check.Compare("carrier", 0, carrierProblems);
            check.Compare("inserts_untouched", 0, insertsChanged);
            check.Compare("carrier_cascade_as_measured", 0, cascadeDiffers);
            check.Compare("pieces_not_embedded", 0, embeddedWalls);
            check.Compare("carrier_change_as_measured", 0, changeDiffers);
            evidence["sources"] = rows;
            return check;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        private static double BaseElevation(Document doc, Wall w)
        {
            ElementId baseId = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? w.LevelId;
            double z = (doc.GetElement(baseId) as Level ?? doc.GetElement(w.LevelId) as Level)?.ProjectElevation ?? 0;
            return z + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
        }

        private static double TopElevation(Document doc, Wall w)
        {
            ElementId topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
            if (topId != ElementId.InvalidElementId && doc.GetElement(topId) is Level top)
                return top.ProjectElevation + (w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0);
            return BaseElevation(doc, w) + (w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0);
        }

        /// <summary>
        /// A curtain wall's grid and mullions read back: vertical line positions along the wall,
        /// the fixed-distance check when the type's vertical layout is Fixed Distance, and every
        /// mullion's type against the type's AUTO_MULLION_* for its role (interior / border,
        /// vertical / horizontal). With no horizontal grid line each vertical line carries one
        /// interior mullion, so that count is checked too; other counts are reported.
        /// </summary>
        private static JObject ReadCurtainWallGrid(Document doc, Wall w, Line axis, out int gridProblems, out int mullionProblems)
        {
            gridProblems = 0; mullionProblems = 0;
            var result = new JObject();
            CurtainGrid grid = w.CurtainGrid;
            if (grid == null) { gridProblems++; result["read"] = "no curtain grid"; return result; }
            XYZ start = Flat(axis.GetEndPoint(0));
            XYZ dir = Flat(axis.GetEndPoint(1)) - start;
            double hostMm = dir.GetLength() * 304.8;
            dir = dir.Normalize();
            var vertical = new List<double>();
            int horizontal = 0;
            foreach (ElementId id in grid.GetVGridLineIds().Concat(grid.GetUGridLineIds()))
            {
                Curve c = (doc.GetElement(id) as CurtainGridLine)?.FullCurve;
                if (c == null) continue;
                XYZ d = c.GetEndPoint(1) - c.GetEndPoint(0);
                if (Math.Abs(d.Z) > 0.99 * d.GetLength()) vertical.Add((Flat(c.Evaluate(0.5, true)) - start).DotProduct(dir) * 304.8);
                else horizontal++;
            }
            vertical.Sort();
            ElementType type = doc.GetElement(w.GetTypeId()) as ElementType;
            int layout = type?.get_Parameter(BuiltInParameter.SPACING_LAYOUT_VERT)?.AsInteger() ?? -1;
            double spacingMm = (type?.get_Parameter(BuiltInParameter.SPACING_LENGTH_VERT)?.AsDouble() ?? 0) * 304.8;
            result["vertical_lines"] = vertical.Count;
            result["horizontal_lines"] = horizontal;
            result["layout_vert"] = layout;
            result["layout_vert_text"] = type?.get_Parameter(BuiltInParameter.SPACING_LAYOUT_VERT)?.AsValueString();
            if (vertical.Count > 0) { result["first_mm"] = Math.Round(vertical[0], 1); result["last_mm"] = Math.Round(vertical[vertical.Count - 1], 1); }
            if (layout == 1)
            {
                List<string> problems = CurtainFramingRules.CheckFixedSpacing(vertical, hostMm, spacingMm, EndpointToleranceMm);
                result["spacing_mm"] = Math.Round(spacingMm, 2);
                result["spacing_problems"] = new JArray(problems.Take(10).ToArray());
                gridProblems += problems.Count;
            }
            else result["spacing_check"] = "not fixed distance: count and first/last reported only";

            // Mullions by role, against the type's automatic mullion per role.
            Func<BuiltInParameter, long> auto = bip => { ElementId t = type?.get_Parameter(bip)?.AsElementId(); return t == null || t == ElementId.InvalidElementId ? -1 : Rid.Value(t); };
            var expected = new Dictionary<string, long>
            {
                ["vertical_interior"] = auto(BuiltInParameter.AUTO_MULLION_INTERIOR_VERT),
                ["vertical_border"] = -2,   // border 1 / 2 may differ; both are accepted
                ["horizontal_interior"] = auto(BuiltInParameter.AUTO_MULLION_INTERIOR_HORIZ),
                ["horizontal_border"] = -2,
            };
            var borderV = new HashSet<long> { auto(BuiltInParameter.AUTO_MULLION_BORDER1_VERT), auto(BuiltInParameter.AUTO_MULLION_BORDER2_VERT) };
            var borderH = new HashSet<long> { auto(BuiltInParameter.AUTO_MULLION_BORDER1_HORIZ), auto(BuiltInParameter.AUTO_MULLION_BORDER2_HORIZ) };
            var counts = new JObject();
            double baseZ = BaseElevation(doc, w), topZ = TopElevation(doc, w);
            foreach (ElementId id in grid.GetMullionIds())
            {
                if (!(doc.GetElement(id) is Mullion mu) || !(mu.LocationCurve is Curve mc)) { mullionProblems++; continue; }
                XYZ d = mc.GetEndPoint(1) - mc.GetEndPoint(0);
                bool isVertical = Math.Abs(d.Z) > 0.99 * d.GetLength();
                string role;
                if (isVertical)
                {
                    double x = (Flat(mc.Evaluate(0.5, true)) - start).DotProduct(dir) * 304.8;
                    role = x < EndpointToleranceMm || x > hostMm - EndpointToleranceMm ? "vertical_border" : "vertical_interior";
                }
                else
                {
                    double z = mc.Evaluate(0.5, true).Z;
                    role = Math.Abs(z - baseZ) * 304.8 < EndpointToleranceMm || Math.Abs(z - topZ) * 304.8 < EndpointToleranceMm ? "horizontal_border" : "horizontal_interior";
                }
                counts[role] = (counts.Value<int?>(role) ?? 0) + 1;
                long mt = mu.MullionType == null ? -1 : Rid.Value(mu.MullionType.Id);
                bool ok = role == "vertical_border" ? borderV.Contains(mt) : role == "horizontal_border" ? borderH.Contains(mt) : expected[role] == mt;
                if (!ok) mullionProblems++;
            }
            result["mullions_by_role"] = counts;
            if (horizontal == 0 && expected["vertical_interior"] > 0 && (counts.Value<int?>("vertical_interior") ?? 0) != vertical.Count)
            {
                mullionProblems++;
                result["interior_mullion_count_problem"] = "one interior vertical mullion per vertical line expected with no horizontal line";
            }
            return result;
        }

        private static JObject CurtainWallSummary(Document doc, List<FramingSourcePlan> plans)
        {
            var rows = new JArray();
            foreach (FramingSourcePlan p in plans)
            {
                CurtainSourceState s = p.Curtain;
                rows.Add(new JObject
                {
                    ["source_id"] = s.CarrierId, ["status"] = p.AlreadyApplied ? "already_applied" : "planned", ["method"] = "curtain",
                    ["length_mm"] = Math.Round(s.LengthMm, 1), ["height_mm"] = Math.Round(s.HeightMm, 1),
                    ["core_offset_mm"] = Math.Round(s.CoreOffsetFt * 304.8, 1),
                    ["openings"] = new JArray(s.Openings.Select(o => new JObject
                    {
                        ["id"] = o.Id, ["start"] = Math.Round(o.Start, 1), ["end"] = Math.Round(o.End, 1), ["sill"] = Math.Round(o.Sill, 1), ["head"] = Math.Round(o.Head, 1)
                    })),
                    ["pieces"] = new JArray(p.Members.Select((m, i) => new JObject
                    {
                        ["i"] = i, ["role"] = m.Role, ["type_id"] = long.Parse(m.TypeKey, CultureInfo.InvariantCulture),
                        ["from"] = new JArray(Math.Round(m.X0, 1), Math.Round(m.Z0, 1)), ["to"] = new JArray(Math.Round(m.X1, 1), Math.Round(m.Z1, 1))
                    })),
                    ["skipped"] = new JArray(s.Plan.Skipped.ToArray()),
                    ["carrier"] = new JObject
                    {
                        ["action"] = s.Plan.Carrier.Action, ["original_type_id"] = s.OriginalTypeId,
                        ["type_id"] = s.Plan.Carrier.TypeKey == null ? null : (JToken)long.Parse(s.Plan.Carrier.TypeKey, CultureInfo.InvariantCulture),
                        ["span"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete ? null : new JArray(Math.Round(s.Plan.Carrier.X0, 1), Math.Round(s.Plan.Carrier.X1, 1)),
                        ["opening_id"] = s.Plan.Carrier.OpeningId,
                        ["location_line"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete ? null : "wall_centreline: set before the type change, so the placeholder stays centred where the carrier was",
                        ["placeholder_offset_from_pieces_mm"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete ? null : (JToken)Math.Round((s.CoreOffsetFt - s.CentreOffsetFt) * 304.8, 1),
                        ["change_takes"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete || p.AlreadyApplied ? null : new JObject
                        {
                            ["count"] = s.ChangeLostMeasured.Count,
                            ["by_kind"] = JObject.FromObject(s.ChangeLostMeasured.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Count())),
                            ["by_category"] = JObject.FromObject(s.ChangeLostMeasured.Keys.GroupBy(id => CategoryLabel(doc, id)).ToDictionary(g => g.Key, g => g.Count())),
                            ["ids"] = new JArray(s.ChangeLostMeasured.Keys.OrderBy(id => id).Take(SummaryMemberCap)),
                        },
                        ["replaced_by"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierDelete ? "every curtain_segment piece" : null,
                        ["overlap"] = s.Plan.Carrier.Action == CurtainFramingRoles.CarrierKeep ? CurtainFramingRules.OverlapNote() : null,
                        ["deleted_with_it"] = s.Plan.Carrier.Action != CurtainFramingRoles.CarrierDelete || p.AlreadyApplied ? null : new JObject
                        {
                            ["count"] = s.CarrierDeleteMeasured.Count,
                            ["by_category"] = JObject.FromObject(s.CarrierDeleteMeasured.GroupBy(id => CategoryLabel(doc, id)).ToDictionary(g => g.Key, g => g.Count())),
                            ["ids"] = new JArray(s.CarrierDeleteMeasured.Take(SummaryMemberCap)),
                        },
                    },
                    ["count_by_role"] = JObject.FromObject(FramingPlanSignature.CountByRole(p.Members)),
                    ["plan_signature"] = p.Signature, ["spec_hash"] = p.SpecHash,
                    ["warnings"] = new JArray(p.Warnings.ToArray()),
                    ["pieces_frame"] = "x along the carrier from its start, z up from its base, mm; on the core centreline"
                });
            }
            return new JObject { ["method"] = "curtain", ["sources"] = rows, ["member_count"] = plans.Sum(p => p.Members.Count) };
        }
    }
}
