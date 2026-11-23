// Horizun Revit MCP — in-place conversion; no Wall.Create or Document.Delete.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class WallFinishCommand
    {
        private sealed class InsertSnapshot
        {
            public long Id, TypeId;
            public XYZ Point;
            public double Width, Height;
        }
        private sealed class Conversion
        {
            public Pair Pair;
            public string UniqueId;
            public long OriginalTypeId;
            public XYZ OriginalFrom, OriginalTo;
            public XYZ From, To, Delta;
            public double Before;
            public bool Applied;
            public bool OriginalJoinStart, OriginalJoinEnd;
            public FloorWallOverlapGuard.Snapshot FloorOverlap;
            public readonly List<InsertSnapshot> Inserts = new List<InsertSnapshot>();
            public readonly Dictionary<BuiltInParameter, double> Constraints = new Dictionary<BuiltInParameter, double>();
        }

        private sealed class ConversionRefusal : InvalidOperationException
        {
            public readonly string Code;
            public ConversionRefusal(string code, string reason) : base(reason) { Code = code; }
        }

        // A deferred native error must roll back this wall before Revit raises a
        // failure dialog. Never resolve it by deleting a floor, sketch or insert.
        private sealed class ConversionFailures : IFailuresPreprocessor
        {
            private readonly Document _doc;
            private readonly RevitErrorRecorder _recorder = new RevitErrorRecorder();
            public readonly JArray Facts = new JArray();
            public ConversionFailures(Document doc) { _doc = doc; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool error = false;
                try
                {
                    _recorder.PreprocessFailures(accessor);
                    foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
                    {
                        FailureSeverity severity = message.GetSeverity();
                        bool isError = severity != FailureSeverity.None && severity != FailureSeverity.Warning;
                        error |= isError;
                        var elements = new JArray();
                        foreach (ElementId id in message.GetFailingElementIds().Concat(message.GetAdditionalElementIds()).Distinct())
                        {
                            Element element = _doc.GetElement(id);
                            elements.Add(new JObject { ["id"] = Rid.Value(id), ["class"] = element?.GetType().Name,
                                ["category_id"] = element?.Category == null ? null : (JToken)Rid.Value(element.Category.Id) });
                        }
                        Facts.Add(new JObject { ["failure_id"] = message.GetFailureDefinitionId().Guid.ToString(),
                            ["severity"] = severity.ToString(), ["is_error"] = isError,
                            ["description"] = message.GetDescriptionText(), ["elements"] = elements });
                    }
                }
                catch (Exception ex)
                {
                    error = true;
                    Facts.Add(new JObject { ["is_error"] = true, ["observation_complete"] = false,
                        ["description"] = "Native failure data could not be read; wall transaction must roll back.", ["observer_error"] = ex.Message });
                }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
            public string Said() => _recorder.Said();
        }

        private static ArchModelEdit PlanConversion(Document doc, JObject r, List<Pair> pairs, JArray matches, Document linked, Transform tf)
        {
            long requestedTypeId = r.Value<long>("finish_type_id");
            var type = Rid.CanRepresent(requestedTypeId) ? doc.GetElement(Rid.Make(requestedTypeId)) as WallType : null;
            double thickness = r.Value<double>("thickness_mm");
            if (type == null || type.Kind != WallKind.Basic || !WallFinishRules.ThicknessMatches(type.Width, thickness))
                throw new ArgumentException("finish_type_id must be a basic wall type whose width equals thickness_mm (0.1 mm tolerance).");
            // Existing libraries may use Structure for a single plaster layer. Width is
            // explicit and checked; never mutate the type or infer material from its name.
            if (type.GetCompoundStructure()?.GetLayers().Count != 1)
                throw new ArgumentException("convert_to_finish requires a single-layer finish type; compound cores are not converted implicitly.");
            ElementId typeId = type.Id;
            double typeWidth = type.Width;
            int side = r.Value<string>("side") == "exterior" ? 1 : -1;
            var candidates = new List<Conversion>();
            var skipped = new JArray();
            foreach (Pair pair in pairs)
            {
                if (pair.Refusal != null) { SkipConversion(skipped, pair.ArqId, pair.Refusal, "core_match_ineligible"); continue; }
                var c = new Conversion { Pair = pair, UniqueId = pair.Arq.UniqueId, OriginalTypeId = Rid.Value(pair.Arq.GetTypeId()) };
                try
                {
                    if (pair.Arq.Pinned || pair.Arq.GroupId != ElementId.InvalidElementId)
                        throw new ConversionRefusal("wall_locked", "Pinned/grouped wall: resolve ownership and grouping in a separate plan before conversion.");
                    if (pair.Arq.WallType.Kind != WallKind.Basic)
                        throw new ConversionRefusal("wall_type_unsupported", "Only a basic source wall can be converted in place; stacked/curtain walls need a separate plan.");
                    if (pair.Arq.SketchId != ElementId.InvalidElementId)
                        throw new ConversionRefusal("profile_edited", "The source wall has an edited profile; preserve its sketch and prepare a separate profile-aware conversion.");
                    c.OriginalJoinStart = WallUtils.IsWallJoinAllowedAtEnd(pair.Arq, 0);
                    c.OriginalJoinEnd = WallUtils.IsWallJoinAllowedAtEnd(pair.Arq, 1);
                    var curve = ((LocationCurve)pair.Arq.Location).Curve;
                    c.OriginalFrom = curve.GetEndPoint(0); c.OriginalTo = curve.GetEndPoint(1);
                    double[] z = VerticalRange(pair.Arq, Transform.Identity), coreZ = VerticalRange(pair.Core, tf);
                    string coverage = WallFinishRules.ConversionCoverageRefusal(curve.Length, pair.M.From, pair.M.To,
                        z[0], z[1], coreZ[0], coreZ[1], 0.1 * Mm);
                    if (coverage != null)
                        throw new ConversionRefusal(coverage, coverage == "core_partial_span"
                            ? "The core covers only part of the retained wall. Keep the exterior portion unchanged; splitting/rehosting inserts requires a separate verified plan."
                            : "The core does not demonstrably cover the whole wall height. Read base/top constraints and the core's actual vertical extent before conversion.");
                    foreach (BuiltInParameter key in new[] { BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.WALL_HEIGHT_TYPE,
                        BuiltInParameter.WALL_BASE_OFFSET, BuiltInParameter.WALL_TOP_OFFSET, BuiltInParameter.WALL_USER_HEIGHT_PARAM })
                    {
                        Parameter parameter = pair.Arq.get_Parameter(key);
                        if (parameter?.StorageType == StorageType.Double) c.Constraints[key] = parameter.AsDouble();
                        else if (parameter?.StorageType == StorageType.ElementId) c.Constraints[key] = Rid.Value(parameter.AsElementId());
                    }
                    if (pair.Core.CrossSection != WallCrossSection.Vertical || pair.Arq.CrossSection != WallCrossSection.Vertical)
                        throw new ConversionRefusal("section_unsupported", "Conversion requires vertical walls of constant section; slanted/tapered hosts need a separate plan.");
                    Curve coreAxis = ((LocationCurve)pair.Core.Location).Curve;
                    XYZ coreDirection = tf.OfVector((coreAxis.GetEndPoint(1) - coreAxis.GetEndPoint(0)).Normalize()).Normalize();
                    // Matching has a 1 degree search tolerance. In-place conversion
                    // cannot rotate hosts: require actual face parallelism over the
                    // full retained axis, not merely a match or zero shared volume.
                    if (Math.Abs(coreDirection.DotProduct(pair.N)) * curve.Length > 0.1 * Mm)
                        throw new ConversionRefusal("core_not_parallel", "In-place conversion requires parallel faces within 0.1 mm along the whole wall; rotation would move hosted openings.");
                    double shift = pair.M.OffsetFeet + side * WallFinishRules.FinishOffset(pair.CoreWidth, type.Width);
                    c.Delta = pair.N * shift;
                    // Keep the full axis and vertical constraints. Only translate normal
                    // to it; shortening to core overlap would displace hosted openings.
                    c.From = curve.GetEndPoint(0) + c.Delta;
                    c.To = curve.GetEndPoint(1) + c.Delta;
                    double? before = SharedWithCore(pair.Arq, pair.Core, tf);
                    if (!before.HasValue) throw new ConversionRefusal("volume_unmeasurable", "Shared volume before conversion is not measurable; no safe conversion can be verified.");
                    c.Before = before.Value;
                    var insertIds = pair.Arq.FindInserts(true, false, true, true).Select(Rid.Value).OrderBy(x => x).ToList();
                    foreach (long id in insertIds)
                    {
                        var insert = doc.GetElement(Rid.Make(id)) as FamilyInstance;
                        if (insert == null || !(insert.Location is LocationPoint lp) || insert.Host?.Id != pair.Arq.Id)
                            throw new ConversionRefusal("hosted_insert_unreadable", "Insert " + id + " is not a measurable hosted door/window; preserve this host.");
                        long category = insert.Category == null ? 0 : Rid.Value(insert.Category.Id);
                        if (category != (long)BuiltInCategory.OST_Doors && category != (long)BuiltInCategory.OST_Windows)
                            throw new ConversionRefusal("hosted_insert_unsupported", "Insert " + id + " is not a door/window; a separate host-preservation plan is required.");
                        double width = InsertDimension(insert, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH);
                        double height = InsertDimension(insert, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT);
                        var snapshot = new InsertSnapshot { Id = id, TypeId = Rid.Value(insert.GetTypeId()), Point = lp.Point, Width = width, Height = height };
                        double along = (lp.Point - pair.A).DotProduct(pair.U);
                        if (!WallFinishRules.OpeningFits(along, width, curve.Length, lp.Point.Z, height, z[0], z[1]))
                            throw new ConversionRefusal("opening_outside_retained_host", "Opening " + id + " does not fit inside the retained wall length/height; do not split or rehost it implicitly.");
                        // Optional family restrictions are data, not a universal inference
                        // from frame depth: a deep jamb can legitimately span a thin wall.
                        foreach (string name in new[] { "Minimum Host Thickness", "Maximum Host Thickness" })
                        {
                            Parameter bound = insert.LookupParameter(name) ?? insert.Symbol.LookupParameter(name);
                            if (bound == null || bound.StorageType != StorageType.Double || bound.AsDouble() <= 0) continue;
                            if (name.StartsWith("Minimum", StringComparison.Ordinal) ? type.Width < bound.AsDouble() - 1e-6 : type.Width > bound.AsDouble() + 1e-6)
                                throw new ConversionRefusal("host_thickness_restriction", "Opening " + id + " refuses requested thickness: " + name + ". Select a compatible finish/host plan.");
                        }
                        if (!OpeningClear(pair.Arq, snapshot, lp.Point))
                            throw new ConversionRefusal("opening_clearance_unmeasured", "Opening " + id + " has no measurable clear cut through the original host; preserve it for review.");
                        c.Inserts.Add(snapshot);
                    }
                    c.FloorOverlap = FloorWallOverlapGuard.Capture(doc, new[] { pair.Arq.Id });
                    candidates.Add(c);
                }
                catch (Exception ex) { SkipConversion(skipped, pair.ArqId, ex.Message, (ex as ConversionRefusal)?.Code ?? "preflight_unreadable"); }
            }
            if (candidates.Count == 0)
                return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["operation"] = "convert_to_finish", ["converted"] = 0, ["skipped"] = skipped,
                    ["note"] = "No compatible wall; nothing was written. Inspect skipped walls and hosted openings." } };
            var edit = new ArchModelEdit();
            foreach (Conversion c in candidates)
            {
                edit.Planned.Add(ModelEditRunner.Planned(c.Pair.Arq, PlannedAction.Modify, r));
                foreach (InsertSnapshot i in c.Inserts) edit.Planned.Add(ModelEditRunner.Planned(doc.GetElement(Rid.Make(i.Id)), PlannedAction.Modify, r));
            }
            var linkPlan = ModelEditRunner.Planned(doc.GetElement(Rid.Make(r.Value<long>("link_instance_id"))), PlannedAction.Read, r);
            // A link's global box is not a revision fingerprint: an interior core can
            // change without moving that box. Bind exact core/input/output facts.
            linkPlan.BeforeValues["conversion_dependencies"] = new JObject
            {
                ["transform"] = new JArray(Exact(tf.Origin), Exact(tf.BasisX), Exact(tf.BasisY), Exact(tf.BasisZ)),
                ["walls"] = new JArray(candidates.Select(c => new JObject
                {
                    ["wall_uid"] = c.UniqueId, ["core_uid"] = c.Pair.Core.UniqueId, ["core_width"] = c.Pair.CoreWidth,
                    ["core_axis"] = new JArray(Exact(((LocationCurve)c.Pair.Core.Location).Curve.GetEndPoint(0)), Exact(((LocationCurve)c.Pair.Core.Location).Curve.GetEndPoint(1))),
                    ["core_geometry"] = new JArray(JoinGeometryCommand.SolidsOf(c.Pair.Core).SelectMany(s => s.Edges.Cast<Edge>())
                        .SelectMany(e => e.Tessellate()).Select(p => Exact(p).ToString(Newtonsoft.Json.Formatting.None)).Distinct().OrderBy(x => x, StringComparer.Ordinal)),
                    ["proposed_axis"] = new JArray(Exact(c.From), Exact(c.To)),
                    ["inserts"] = new JArray(c.Inserts.Select(i => new JObject { ["id"] = i.Id, ["type_id"] = i.TypeId,
                        ["host_uid"] = c.UniqueId, ["point"] = Exact(i.Point), ["width"] = i.Width, ["height"] = i.Height }))
                }))
            }.ToString(Newtonsoft.Json.Formatting.None);
            edit.Planned.Add(linkPlan);
            var typePlan = ModelEditRunner.Planned(type, PlannedAction.Read, r);
            typePlan.BeforeValues["width_feet"] = type.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            edit.Planned.Add(typePlan);
            edit.Summary = new JObject { ["operation"] = "convert_to_finish", ["walls"] = matches, ["eligible"] = candidates.Count,
                ["finish_type_id"] = Rid.Value(type.Id), ["thickness_mm"] = thickness, ["side"] = r["side"], ["skipped"] = skipped,
                ["creates"] = 0, ["deletes"] = 0, ["planned"] = new JArray(candidates.Select(c => new JObject { ["wall_id"] = c.Pair.ArqId,
                    ["core_id"] = c.Pair.M.CoreId, ["shared_before_m3"] = c.Before, ["translation_mm"] = ModelEditRunner.Arr(c.Delta, Mm),
                    ["hosted_ids"] = new JArray(c.Inserts.Select(i => i.Id)) })) };
            edit.ApplyWithTransactions = d =>
            {
                foreach (Conversion c in candidates)
                {
                    var failures = new ConversionFailures(d);
                    string stage = "native_commit";
                    using (var unit = new TransactionGroup(d, "Horizun: convert wall " + c.Pair.ArqId))
                    {
                        if (unit.Start() != TransactionStatus.Started) throw new InvalidOperationException("wall transaction group did not start");
                        try
                        {
                            using (var tx = new Transaction(d, "Horizun: convert wall " + c.Pair.ArqId))
                            {
                                FailureHandlingOptions options = tx.GetFailureHandlingOptions();
                                options.SetFailuresPreprocessor(failures);
                                options.SetClearAfterRollback(true);
                                tx.SetFailureHandlingOptions(options);
                                if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("wall transaction did not start");
                                try
                                {
                                    Wall w = (Wall)d.GetElement(Rid.Make(c.Pair.ArqId));
                                    // Prevent automatic end joins from moving adjacent walls.
                                    WallUtils.DisallowWallJoinAtEnd(w, 0); WallUtils.DisallowWallJoinAtEnd(w, 1);
                                    w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(0);
                                    ElementId wallId = Rid.Make(c.Pair.ArqId);
                                    if (w.ChangeTypeId(typeId) != ElementId.InvalidElementId)
                                        throw new InvalidOperationException("Changing type replaced the original wall; conversion must preserve identity.");
                                    ElementTransformUtils.MoveElement(d, wallId, c.Delta);
                                    d.Regenerate();
                                    Guard.Commit(tx, "convert wall " + c.Pair.ArqId);
                                }
                                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                                catch (Exception ex) { throw new InvalidOperationException(ModelEditRunner.FailureDiagnosis(r, ex) + failures.Said(), ex); }
                            }
                            stage = "floor_wall_overlap";
                            FloorWallOverlapGuard.ThrowIfIncreased(d, c.FloorOverlap);
                            stage = "postconditions";
                            string invalid = ConversionFailure(d, c, typeId, typeWidth, linked, tf, out JObject row);
                            if (invalid != null) throw new InvalidOperationException(invalid);
                            Guard.Assimilate(unit, "convert wall " + c.Pair.ArqId);
                            c.Applied = true;
                        }
                        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                        catch (Exception ex)
                        {
                            if (unit.GetStatus() == TransactionStatus.Started && !Guard.RollBack(unit).Confirmed)
                                throw new InvalidOperationException("wall rollback not confirmed", ex);
                            var original = d.GetElement(Rid.Make(c.Pair.ArqId)) as Wall;
                            Curve restored = (original?.Location as LocationCurve)?.Curve;
                            if (original == null || original.UniqueId != c.UniqueId || Rid.Value(original.GetTypeId()) != c.OriginalTypeId ||
                                restored == null || restored.GetEndPoint(0).DistanceTo(c.OriginalFrom) > 1e-8 || restored.GetEndPoint(1).DistanceTo(c.OriginalTo) > 1e-8 ||
                                !original.FindInserts(true, false, true, true).Select(Rid.Value).OrderBy(x => x).SequenceEqual(c.Inserts.Select(i => i.Id).OrderBy(x => x)) ||
                                WallUtils.IsWallJoinAllowedAtEnd(original, 0) != c.OriginalJoinStart ||
                                WallUtils.IsWallJoinAllowedAtEnd(original, 1) != c.OriginalJoinEnd ||
                                OriginalConstraintsFailure(original, c) != null || !OriginalInsertsRestored(d, original, c))
                                throw new InvalidOperationException("skipped wall rollback did not restore identity/type/axis/hosts", ex);
                            SkipConversion(skipped, c.Pair.ArqId, ex.Message,
                                failures.Facts.Any(f => f.Value<bool>("is_error")) ? "native_constraint_error" :
                                    stage == "native_commit" ? "native_api_error" :
                                    stage == "floor_wall_overlap" ? "floor_wall_overlap_increased_or_unmeasured" : "conversion_verification_failed",
                                stage, true, failures.Facts);
                        }
                    }
                }
            };
            edit.Verify = d =>
            {
                var output = new JArray(); int good = 0;
                foreach (Conversion c in candidates.Where(x => x.Applied))
                {
                    string failure = ConversionFailure(d, c, typeId, typeWidth, linked, tf, out JObject row);
                    row["failure"] = failure; output.Add(row); if (failure == null) good++;
                }
                edit.Evidence["operation"] = "convert_to_finish"; edit.Evidence["walls"] = output; edit.Evidence["skipped"] = skipped;
                edit.Evidence["converted"] = good; edit.Evidence["creates"] = 0; edit.Evidence["deletes"] = 0;
                edit.Evidence["shared_before_m3"] = candidates.Where(c => c.Applied).Sum(c => c.Before);
                edit.Evidence["shared_after_m3"] = output.Sum(x => x.Value<double?>("shared_after_m3") ?? 0);
                edit.Evidence["opening_check"] = "IDs, host, type, translated insertion point; nine through-wall aperture rays per insert (sampled clearance, not full void topology)";
                var check = new PostconditionCheck("converted_wall_identity_type_location_openings_volume");
                check.Compare("converted_wall_identity_type_location_openings_volume", candidates.Count(c => c.Applied), good);
                return check;
            };
            edit.VerifiedItems = () => candidates.Count(c => c.Applied);
            edit.PartialFailures = () => pairs.Count - candidates.Count(c => c.Applied);
            return edit;
        }

        private static void SkipConversion(JArray skipped, long id, string reason, string code,
            string stage = "preflight", bool? rollbackVerified = null, JArray nativeFailures = null) => skipped.Add(new JObject
            {
                ["wall_id"] = id, ["reason"] = reason, ["reason_code"] = code, ["stage"] = stage,
                ["applied"] = false, ["rollback_verified"] = rollbackVerified.HasValue ? (JToken)rollbackVerified.Value : null,
                ["native_failures"] = nativeFailures ?? new JArray()
            });
        private static JArray Exact(XYZ point) => new JArray(point.X, point.Y, point.Z);

        private static string OriginalConstraintsFailure(Wall wall, Conversion c)
        {
            foreach (var constraint in c.Constraints)
            {
                Parameter p = wall.get_Parameter(constraint.Key);
                double actual = p?.StorageType == StorageType.Double ? p.AsDouble() :
                    (p?.StorageType == StorageType.ElementId ? Rid.Value(p.AsElementId()) : double.NaN);
                if (double.IsNaN(actual) || Math.Abs(actual - constraint.Value) > 1e-8) return "vertical constraint changed: " + constraint.Key;
            }
            return null;
        }

        private static bool OriginalInsertsRestored(Document doc, Wall wall, Conversion c)
        {
            foreach (InsertSnapshot snapshot in c.Inserts)
            {
                var insert = doc.GetElement(Rid.Make(snapshot.Id)) as FamilyInstance;
                if (insert == null || insert.Host?.Id != wall.Id || Rid.Value(insert.GetTypeId()) != snapshot.TypeId ||
                    !(insert.Location is LocationPoint point) || point.Point.DistanceTo(snapshot.Point) > 1e-8)
                    return false;
            }
            return true;
        }

        private static double InsertDimension(FamilyInstance insert, BuiltInParameter door, BuiltInParameter window)
        {
            foreach (BuiltInParameter p in new[] { door, window })
                foreach (Element element in new Element[] { insert, insert.Symbol })
                { Parameter value = element.get_Parameter(p); if (value?.StorageType == StorageType.Double && value.AsDouble() > 0) return value.AsDouble(); }
            return double.NaN;
        }

        private static string ConversionFailure(Document doc, Conversion c, ElementId typeId, double typeWidth,
            Document linked, Transform tf, out JObject row)
        {
            row = new JObject { ["wall_id"] = c.Pair.ArqId, ["core_id"] = c.Pair.M.CoreId, ["shared_before_m3"] = c.Before };
            try { FloorWallOverlapGuard.ThrowIfIncreased(doc, c.FloorOverlap); }
            catch (Exception ex) { return "Floor/wall overlap was increased or could not be measured: " + ex.Message; }
            row["floor_wall_overlap_increase_checked"] = true;
            var w = doc.GetElement(Rid.Make(c.Pair.ArqId)) as Wall;
            if (w == null || w.UniqueId != c.UniqueId || w.GetTypeId() != typeId || Math.Abs(w.Width - typeWidth) > 0.1 * Mm)
                return "wall identity/type/thickness disagrees";
            Curve axis = (w.Location as LocationCurve)?.Curve;
            if (axis == null || axis.GetEndPoint(0).DistanceTo(c.From) > Mm || axis.GetEndPoint(1).DistanceTo(c.To) > Mm)
                return "wall axis disagrees with requested core-face location";
            row["type_id"] = Rid.Value(w.GetTypeId()); row["thickness_mm"] = w.Width / Mm;
            string constraintsFailure = OriginalConstraintsFailure(w, c);
            if (constraintsFailure != null) return constraintsFailure;
            row["axis_mm"] = new JArray(ModelEditRunner.Arr(axis.GetEndPoint(0), Mm), ModelEditRunner.Arr(axis.GetEndPoint(1), Mm));
            var core = linked.GetElement(Rid.Make(c.Pair.M.CoreId)) as Wall;
            if (!(core?.Location is LocationCurve coreLocation)) return "linked core geometry could not be re-read";
            Curve coreAxis = coreLocation.Curve;
            XYZ coreA = tf.OfPoint(coreAxis.GetEndPoint(0));
            XYZ coreU = tf.OfVector((coreAxis.GetEndPoint(1) - coreAxis.GetEndPoint(0)).Normalize()).Normalize();
            XYZ coreN = XYZ.BasisZ.CrossProduct(coreU).Normalize();
            double requiredDistance = WallFinishRules.FinishOffset(c.Pair.CoreWidth, typeWidth);
            var offsets = new JArray();
            foreach (XYZ endpoint in new[] { axis.GetEndPoint(0), axis.GetEndPoint(1) })
            {
                double distance = Math.Abs((endpoint - coreA).DotProduct(coreN));
                offsets.Add(distance / Mm);
                if (Math.Abs(distance - requiredDistance) > Mm) return "finish axis does not follow the linked core face at both ends";
            }
            row["axis_distance_from_core_mm"] = offsets;
            row["core_overlap_ratio"] = c.Pair.M.Ratio;
            if (!ConversionFaceContact(w, c, core, tf)) return "nine inner-face samples over the matched overlap do not contact a bounded vertical core face";
            row["sampled_core_face_contact"] = true;
            var actualIds = w.FindInserts(true, false, true, true).Select(Rid.Value).OrderBy(x => x).ToList();
            if (!actualIds.SequenceEqual(c.Inserts.Select(i => i.Id).OrderBy(x => x))) return "hosted insert set changed";
            var inserts = new JArray(); row["inserts"] = inserts;
            foreach (InsertSnapshot snapshot in c.Inserts)
            {
                var i = doc.GetElement(Rid.Make(snapshot.Id)) as FamilyInstance;
                if (i == null || i.Host?.Id != w.Id || Rid.Value(i.GetTypeId()) != snapshot.TypeId || !(i.Location is LocationPoint lp))
                    return "opening " + snapshot.Id + " lost its host/type/location";
                if (lp.Point.DistanceTo(snapshot.Point + c.Delta) > Mm) return "opening " + snapshot.Id + " was not translated with its host";
                if (!OpeningClear(w, snapshot, lp.Point)) return "opening " + snapshot.Id + " does not cut through the new thickness; wall rolled back";
                inserts.Add(new JObject { ["id"] = snapshot.Id, ["host_id"] = Rid.Value(i.Host.Id), ["type_id"] = snapshot.TypeId, ["sampled_clearance"] = true });
            }
            double? shared = SharedWithCore(w, core, tf);
            row["shared_after_m3"] = shared.HasValue ? (JToken)shared.Value : null;
            row["volume_coverage_complete"] = shared.HasValue;
            if (!shared.HasValue) return "shared volume after conversion is not measurable";
            if (shared.Value > JoinGeometryRules.OverlapToleranceM3) return "finish still shares volume with linked core";
            return null;
        }

        private static bool OpeningClear(Wall wall, InsertSnapshot insert, XYZ location)
        {
            List<Solid> solids = JoinGeometryCommand.SolidsOf(wall);
            if (solids.Count == 0) return false;
            XYZ normal = wall.Orientation.Normalize();
            var line = ((LocationCurve)wall.Location).Curve;
            XYZ along = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
            double reach = wall.Width + 10 * Mm;
            using (var options = new SolidCurveIntersectionOptions())
            {
                options.ResultType = SolidCurveIntersectionMode.CurveSegmentsInside;
                foreach (double x in new[] { -0.25, 0.0, 0.25 })
                    foreach (double z in new[] { 0.25, 0.5, 0.75 })
                    {
                        XYZ p = location + along * (insert.Width * x) + XYZ.BasisZ * (insert.Height * z);
                        // Insertion points may be on either reference face. Anchor every
                        // ray to the actual wall axis, while preserving along/Z position.
                        XYZ a = line.GetEndPoint(0);
                        p -= normal * ((p - a).DotProduct(normal));
                        using (Line ray = Line.CreateBound(p - normal * reach, p + normal * reach))
                            foreach (Solid solid in solids)
                                using (SolidCurveIntersection cut = solid.IntersectWithCurve(ray, options))
                                    for (int k = 0; k < cut.SegmentCount; k++)
                                        if (cut.GetCurveSegment(k).Length > 0.1 * Mm) return false;
                    }
            }
            return true;
        }

        private static bool ConversionFaceContact(Wall wall, Conversion c, Wall core, Transform tf)
        {
            double[] hostZ = VerticalRange(wall, Transform.Identity), coreZ = VerticalRange(core, tf);
            double bottom = Math.Max(hostZ[0], coreZ[0]), top = Math.Min(hostZ[1], coreZ[1]);
            if (!(top > bottom)) return false;
            XYZ axisStart = ((LocationCurve)wall.Location).Curve.GetEndPoint(0);
            XYZ coreA = tf.OfPoint(((LocationCurve)core.Location).Curve.GetEndPoint(0));
            double sign = Math.Sign((axisStart - coreA).DotProduct(c.Pair.N));
            XYZ inner = axisStart - c.Pair.N * (sign * wall.Width / 2);
            Transform inverse = tf.Inverse;
            foreach (Solid solid in JoinGeometryCommand.SolidsOf(core))
                foreach (Face face in solid.Faces)
                {
                    if (!(face is PlanarFace plane)) continue;
                    XYZ n = tf.OfVector(plane.FaceNormal).Normalize();
                    if (Math.Abs(n.Z) > .001 || Math.Abs(n.DotProduct(c.Pair.N)) < .999999) continue;
                    bool fits = true;
                    foreach (double along in new[] { .1, .5, .9 })
                        foreach (double height in new[] { .1, .5, .9 })
                        {
                            XYZ p = inner + c.Pair.U * (c.Pair.M.From + (c.Pair.M.To - c.Pair.M.From) * along);
                            p = new XYZ(p.X, p.Y, bottom + (top - bottom) * height);
                            IntersectionResult hit = plane.Project(inverse.OfPoint(p));
                            if (hit == null || hit.Distance > Mm || !plane.IsInside(hit.UVPoint)) fits = false;
                        }
                    if (fits) return true;
                }
            return false;
        }
    }
}
