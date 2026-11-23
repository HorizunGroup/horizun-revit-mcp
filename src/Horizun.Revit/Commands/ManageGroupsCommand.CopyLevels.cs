using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using HeightMember = Horizun.Revit.Core.GroupMemberBounds;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageGroupsCommand
    {
        private CommandResult ExecuteCopyOrVariation(UIApplication app, JObject request) =>
            ModelEditRunner.Run(app, request.ToString(Newtonsoft.Json.Formatting.None), Name, "Horizun: copy groups to levels",
                GroupLevelRules.ValidateCopyOrVariation, PlanCopyOrVariation, readOperation: "check_variation");

        private static List<HeightMember> HeightMembers(Document doc, Group group, bool readPlacement = false) =>
            ReadMemberBounds(doc, group.GetMemberIds(), readPlacement);

        private static List<HeightMember> ReadMemberBounds(Document doc, IEnumerable<ElementId> ids, bool readPlacement)
        {
            var rows = new List<HeightMember>();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                BoundingBoxXYZ b = Safe(() => e?.get_BoundingBox(null));
                var orientation = readPlacement ? MemberOrientation(e) : null;
                var row = new HeightMember { Id = Rid.Value(id), Key = e == null ? null :
                    (e.Category == null ? "none" : Rid.Value(e.Category.Id).ToString()) + ":" + Rid.Value(e.GetTypeId()),
                    Min = Coordinates(b?.Min), Max = Coordinates(b?.Max),
                    Axes = orientation?.Item1, Reflected = orientation?.Item2,
                    ReadGeometryPoints = readPlacement && e != null
                        ? (Func<IList<double[]>>)(() => Safe(() => GeometryPoints(e.get_Geometry(new Options())).Select(Coordinates).ToList()))
                        : null };
                if (readPlacement) row.CurvePath = MemberCurvePath(e, row);
                rows.Add(row);
            }
            return rows;
        }

        private static double[] Coordinates(XYZ point) => point == null ? null : new[] { point.X, point.Y, point.Z };

        private static double[][] MemberCurvePath(Element element, HeightMember row)
        {
            // Absence of a location curve and inability to read one have different coverage.
            var location = Safe(() => new { Path = element?.Location as LocationCurve });
            if (location == null) return Array.Empty<double[]>();
            if (location.Path == null) return null;
            return Safe(() =>
            {
                Curve curve = location.Path.Curve;
                if (!(curve is Line || curve is Arc) || !curve.IsBound) return null;
                XYZ start = curve.GetEndPoint(0), end = curve.GetEndPoint(1);
                if (start.DistanceTo(end) <= 1e-9) return null;
                if (!(curve is Arc arc)) return new[] { Coordinates(start), Coordinates(end) };
                var points = new[] { Coordinates(start), Coordinates(arc.Evaluate(0.25, true)),
                    Coordinates(arc.Evaluate(0.5, true)), Coordinates(arc.Evaluate(0.75, true)), Coordinates(end) };
                double sweep = arc.Length / arc.Radius;
                row.CurveArcCenter = Coordinates(arc.Center);
                row.CurveArcSweep = sweep;
                return points;
            }) ?? Array.Empty<double[]>();
        }

        private static Tuple<double[][], bool?> MemberOrientation(Element element)
        {
            var unreadable = Tuple.Create(Array.Empty<double[]>(), (bool?)null);
            if (element is FamilyInstance family)
            {
                return Safe(() =>
                {
                    Transform transform = family.GetTransform();
                    return Tuple.Create(new[] { Coordinates(transform.BasisX), Coordinates(transform.BasisY) }, (bool?)transform.HasReflection);
                }) ?? unreadable;
            }
            if (!(Safe(() => element?.Location) is LocationCurve path)) return null;
            return Safe(() =>
            {
                Curve curve = path.Curve;
                if (!curve.IsBound) return unreadable;
                XYZ chord = curve.GetEndPoint(1) - curve.GetEndPoint(0);
                double length = Math.Sqrt(chord.X * chord.X + chord.Y * chord.Y + chord.Z * chord.Z);
                return length <= 1e-9 ? unreadable : Tuple.Create(new[] { new[] { chord.X / length, chord.Y / length, chord.Z / length } }, (bool?)null);
            }) ?? unreadable;
        }

        private static IEnumerable<XYZ> GeometryPoints(GeometryElement geometry)
        {
            if (geometry == null) yield break;
            foreach (GeometryObject item in geometry)
            {
                if (item is GeometryInstance instance)
                    foreach (XYZ point in GeometryPoints(instance.GetInstanceGeometry())) yield return point;
                else if (item is Solid solid)
                    foreach (Edge edge in solid.Edges)
                        foreach (XYZ point in edge.Tessellate()) yield return point;
                else if (item is Curve curve)
                    foreach (XYZ point in curve.Tessellate()) yield return point;
                else if (item is Mesh mesh)
                    foreach (XYZ point in mesh.Vertices) yield return point;
                else if (item is PolyLine polyline)
                    foreach (XYZ point in polyline.GetCoordinates()) yield return point;
            }
        }

        private static GroupPlacementSnapshot CaptureGroupPlacement(Document doc, Group group) => new GroupPlacementSnapshot
        {
            Origin = Coordinates(Safe(() => (group.Location as LocationPoint)?.Point)),
            Members = HeightMembers(doc, group, readPlacement: true)
        };

        private static JArray VerifyAddedMembersInCopy(Document doc, IList<HeightMember> referenceMembers,
            Group copy, IEnumerable<long> addedIds, IEnumerable<long> includedIds, GroupPlacementSnapshot referenceBefore,
            GroupPlacementSnapshot copyBefore, IList<HeightMember> looseBefore, double rotation, out JObject placement) =>
            GroupLevelRules.AddedMembersInCopy(referenceMembers, HeightMembers(doc, copy, readPlacement: true),
                addedIds, includedIds, referenceBefore, copyBefore, looseBefore, rotation, out placement);

        private static JObject CompareHeights(IList<HeightMember> before, IList<HeightMember> after, XYZ shift, double toleranceMm)
            => GroupLevelRules.CompareMemberBounds(before, after, new[] { shift.X, shift.Y, shift.Z }, toleranceMm);

        private static ArchModelEdit PlanCopyOrVariation(Document doc, JObject r, double scale)
        {
            var groups = ((JArray)r["group_ids"]).Values<long>().Select(id => Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as Group : null).ToList();
            if (groups.Any(g => g == null || Kind(g.GroupType) != "model" || g.GroupId != ElementId.InvalidElementId))
                throw new ArgumentException("group_ids must identify top-level model groups in this document.");
            Group source = groups[0];
            XYZ sourceOrigin = Safe(() => (source.Location as LocationPoint)?.Point);
            if (r.Value<string>("operation") == "check_variation")
            {
                if (groups.Any(g => g.GroupType.Id != source.GroupType.Id))
                    throw new ArgumentException("check_variation compares instances of the same group type.");
                double tolerance = r.Value<double?>("tolerance_mm") ?? 1;
                var rotations = (JArray)r["relative_rotations_degrees"];
                var referenceMembers = HeightMembers(doc, source, readPlacement: rotations != null);
                var rows = new JArray();
                for (int i = 1; i < groups.Count; i++)
                {
                    Group other = groups[i];
                    XYZ origin = Safe(() => (other.Location as LocationPoint)?.Point);
                    JObject row = GroupLevelRules.ComparePlacedMembers(referenceMembers,
                        HeightMembers(doc, other, readPlacement: rotations != null), Coordinates(sourceOrigin), Coordinates(origin),
                        tolerance, rotations?[i - 1].Value<double>());
                    row["group_id"] = Rid.Value(other.Id); rows.Add(row);
                }
                return new ArchModelEdit { ReadResult = GroupLevelRules.VariationResult(Rid.Value(source.Id), Rid.Value(source.GroupType.Id), rows) };
            }

            if (sourceOrigin == null) throw new ArgumentException("The source group's origin is unreadable.");
            List<HeightMember> before = HeightMembers(doc, source);
            if (before.Count == 0 || before.Any(m => m.Min == null || m.Max == null))
                throw new ArgumentException("Every source group member must have readable geometry to verify its height.");
            ElementId sourceLevelId = source.get_Parameter(BuiltInParameter.GROUP_LEVEL)?.AsElementId();
            Level sourceLevel = sourceLevelId == null ? null : doc.GetElement(sourceLevelId) as Level;
            double? offset = source.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL)?.AsDouble();
            if (sourceLevel == null || offset == null) throw new ArgumentException("Source group level or offset is unreadable.");
            var targets = ((JArray)r["level_ids"]).Values<long>().Select(id => Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as Level : null).ToList();
            if (targets.Any(level => level == null || level.Id == sourceLevel.Id))
                throw new ArgumentException("level_ids must name existing target Levels, distinct from the source level.");
            var shifts = targets.Select(level => new XYZ(0, 0, level.Elevation - sourceLevel.Elevation)).ToList();
            if (shifts.Any(shift => Math.Abs(shift.Z) < 1e-9)) throw new ArgumentException("A target Level has the same elevation as the source; copying would create a duplicate in place.");
            var edit = new ArchModelEdit();
            edit.Planned.Add(ModelEditRunner.Planned(source, PlannedAction.Read, r));
            foreach (ElementId id in source.GetMemberIds()) edit.Planned.Add(ModelEditRunner.Planned(doc.GetElement(id), PlannedAction.Read, r));
            foreach (Level target in targets) edit.Planned.Add(ModelEditRunner.Planned(target, PlannedAction.Read, r));
            edit.Summary["operation"] = "copy_to_levels"; edit.Summary["source_group_id"] = Rid.Value(source.Id);
            edit.Summary["targets"] = new JArray(targets.Select((level, i) => new JObject { ["level_id"] = Rid.Value(level.Id),
                ["translation_mm"] = shifts[i].Z * 304.8, ["expected_offset_mm"] = offset.Value * 304.8 }));
            var created = new List<ElementId>();
            edit.Apply = d =>
            {
                created.Clear();
                for (int i = 0; i < targets.Count; i++)
                {
                    var copied = ElementTransformUtils.CopyElement(d, source.Id, shifts[i]);
                    var copies = copied.Select(d.GetElement).OfType<Group>().Where(g => g.GroupId == ElementId.InvalidElementId).ToList();
                    if (copies.Count != 1) throw new InvalidOperationException("Copy did not produce exactly one top-level group.");
                    Group copy = copies[0]; created.Add(copy.Id);
                    Parameter level = copy.get_Parameter(BuiltInParameter.GROUP_LEVEL), off = copy.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL);
                    if (level == null || level.IsReadOnly || off == null || off.IsReadOnly || !level.Set(targets[i].Id) || !off.Set(offset.Value))
                        throw new InvalidOperationException("The copied group's level or offset could not be set.");
                }
            };
            edit.Verify = d =>
            {
                var required = new List<string> { "source_unchanged", "copy_count" };
                for (int i = 0; i < targets.Count; i++) required.AddRange(new[] { "type_" + i, "level_" + i, "offset_" + i, "origin_" + i, "members_" + i });
                var check = new PostconditionCheck(required.ToArray());
                Group currentSource = d.GetElement(source.Id) as Group;
                JObject sourceGeometry = currentSource == null ? null : CompareHeights(before, HeightMembers(d, currentSource), XYZ.Zero, 1);
                if (sourceGeometry?.Value<bool?>("coverage_complete") != true)
                    check.Unreadable("source_unchanged", true, "The source group's member geometry cannot be re-read completely.");
                else check.Record("source_unchanged", true, sourceGeometry, sourceGeometry.Value<bool?>("matches") == true);
                check.Compare("copy_count", targets.Count, created.Count);
                var rows = new JArray();
                for (int i = 0; i < targets.Count; i++)
                {
                    Group copy = i < created.Count ? d.GetElement(created[i]) as Group : null;
                    check.Compare("type_" + i, Rid.Value(source.GroupType.Id), copy == null ? -1 : Rid.Value(copy.GroupType.Id));
                    ElementId level = copy?.get_Parameter(BuiltInParameter.GROUP_LEVEL)?.AsElementId();
                    check.Compare("level_" + i, Rid.Value(targets[i].Id), level == null ? -1 : Rid.Value(level));
                    double? readOffset = copy?.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL)?.AsDouble();
                    if (readOffset == null) check.Unreadable("offset_" + i, offset.Value * 304.8, "Copied group offset is unreadable.");
                    else check.Measure("offset_" + i, offset.Value * 304.8, readOffset.Value * 304.8, 1, "mm", "GROUP_OFFSET_FROM_LEVEL");
                    XYZ origin = (copy?.Location as LocationPoint)?.Point;
                    XYZ wanted = sourceOrigin + shifts[i];
                    if (origin == null) check.Unreadable("origin_" + i, new JArray(wanted.X, wanted.Y, wanted.Z), "Copied group origin is unreadable.");
                    else check.Measure("origin_" + i, 0, origin.DistanceTo(wanted) * 304.8, 1, "mm", "LocationPoint displacement");
                    JObject heights = copy == null ? null : CompareHeights(before, HeightMembers(d, copy), shifts[i], 1);
                    if (heights == null || heights.Value<bool>("coverage_complete") != true)
                        check.Unreadable("members_" + i, before.Count, "One or more member bounds could not be re-read.");
                    else check.Record("members_" + i, "Every member's type and translated bounds, including bottom and top", heights, heights.Value<bool>("matches"));
                    rows.Add(new JObject { ["group_id"] = copy == null ? null : (JToken)Rid.Value(copy.Id), ["target_level_id"] = Rid.Value(targets[i].Id), ["members"] = heights });
                }
                edit.Evidence["copies"] = rows;
                return check;
            };
            return edit;
        }
    }
}
