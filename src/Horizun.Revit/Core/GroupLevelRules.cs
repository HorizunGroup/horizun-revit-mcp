using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class GroupMemberBounds
    {
        public long Id;
        public string Key;
        public double[] Min, Max;
        // Two directed family basis vectors or one undirected curve chord.
        // Null means bounds-only; an empty array means an unreadable orientation.
        public double[][] Axes;
        public bool? Reflected;
        // Ordered endpoints for a line, or five equally spaced angular samples for an arc.
        // Null means no LocationCurve; an empty array means the path could not be verified.
        public double[][] CurvePath;
        public double[] CurveArcCenter;
        public double CurveArcSweep;
        public Func<IList<double[]>> ReadGeometryPoints;
        public IList<double[]> CapturedGeometryPoints;
    }

    public sealed class GroupPlacementSnapshot
    {
        public double[] Origin;
        public IList<GroupMemberBounds> Members;

        public JObject ToJson() => new JObject
        {
            ["origin"] = Origin == null ? null : new JArray(Origin),
            ["members"] = MemberRows(Members)
        };

        public static JArray MemberRows(IEnumerable<GroupMemberBounds> members) => new JArray(members.Select(m => new JObject
        {
            ["id"] = m.Id, ["key"] = m.Key, ["reflected"] = m.Reflected,
            ["axes"] = m.Axes == null ? null : new JArray(m.Axes.Select(axis => axis == null ? JValue.CreateNull() : (JToken)new JArray(axis))),
            ["curve_path"] = m.CurvePath == null ? null : new JArray(m.CurvePath.Select(point => point == null ? JValue.CreateNull() : (JToken)new JArray(point))),
            ["curve_arc_center"] = m.CurveArcCenter == null ? null : new JArray(m.CurveArcCenter),
            ["curve_arc_sweep"] = m.CurveArcCenter == null ? (double?)null : m.CurveArcSweep,
            ["geometry_points"] = m.CapturedGeometryPoints == null ? null : new JArray(m.CapturedGeometryPoints.Select(point => new JArray(point))),
            ["min"] = m.Min == null ? null : new JArray(m.Min), ["max"] = m.Max == null ? null : new JArray(m.Max)
        }));
    }

    public static class GroupLevelRules
    {
        public const string MepGroupLevelCode = "grupo_mep_cambio_de_nivel";

        /// <summary>
        /// A group that holds connected MEP elements (pipes, ducts, conduits, cable trays, their fittings and
        /// connectable equipment) does not change level through GROUP_LEVEL: in the field (2026-11) writing that
        /// parameter on such a group closed Revit. Null when the group holds none; otherwise the typed refusal,
        /// naming the way that is known to work. Nothing is written either way: this only decides.
        /// </summary>
        public static string MepLevelChangeRefusal(int mepMembers, IList<long> exampleIds)
        {
            if (mepMembers <= 0) return null;
            string examples = exampleIds == null || exampleIds.Count == 0 ? "" :
                " (for instance " + string.Join(", ", exampleIds.Take(5)) + ")";
            return MepGroupLevelCode + ": the group holds " + mepMembers + " MEP element(s)" + examples + ". Changing the level of a group that " +
                   "carries connected pipes, ducts, conduits or their fittings has closed Revit in the field, so it is refused before anything is " +
                   "written. The route that works: horizun_manage_groups operation=ungroup, then horizun_mep_fittings (Pro edition) operation=move_run with the " +
                   "vector between the two levels (it keeps the connections), then horizun_manage_groups operation=create over the same elements. " +
                   "Nothing was written.";
        }

        public static string ValidateCopyOrVariation(JObject r)
        {
            string op = r?.Value<string>("operation");
            if (op != "copy_to_levels" && op != "check_variation") return "operation must be copy_to_levels or check_variation.";
            string[] common = { "operation", "target_document", "group_ids", "dry_run", "confirmation_token", "transaction_name", "idempotency_key", "units" };
            foreach (JProperty property in r.Properties())
                if (!common.Contains(property.Name) && !(op == "copy_to_levels" ? property.Name == "level_ids" :
                    property.Name == "tolerance_mm" || property.Name == "relative_rotations_degrees"))
                    return property.Name + " is not applicable to " + op + ".";
            if (op == "copy_to_levels" && string.IsNullOrWhiteSpace(r.Value<string>("target_document"))) return "target_document is required.";
            int min = op == "copy_to_levels" ? 1 : 2, max = op == "copy_to_levels" ? 1 : 50;
            if (!(r["group_ids"] is JArray ids) || ids.Count < min || ids.Count > max || ids.Any(t => t.Type != JTokenType.Integer || t.Value<long>() <= 0))
                return "group_ids must list " + min + ".." + max + " positive integer group ids.";
            if (ids.Values<long>().Distinct().Count() != ids.Count) return "group_ids must be distinct.";
            if (op == "copy_to_levels")
            {
                if (!(r["level_ids"] is JArray levels) || levels.Count < 1 || levels.Count > 50 || levels.Any(t => t.Type != JTokenType.Integer || t.Value<long>() <= 0))
                    return "level_ids must list 1..50 positive integer Level ids.";
                if (levels.Values<long>().Distinct().Count() != levels.Count) return "level_ids must be distinct.";
            }
            else
            {
                if (r["tolerance_mm"] != null && (!ReadableNumber(r["tolerance_mm"]) || r.Value<double>("tolerance_mm") <= 0))
                    return "tolerance_mm must be finite and positive.";
                if (r["relative_rotations_degrees"] != null && (!(r["relative_rotations_degrees"] is JArray rotations) ||
                    rotations.Count != ids.Count - 1 || rotations.Any(t => !ReadableNumber(t) || Math.Abs(t.Value<double>()) > 360)))
                    return "relative_rotations_degrees must contain one angle in [-360, 360] for each group after the first in group_ids.";
            }
            return null;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool ReadableNumber(JToken value) =>
            (value.Type == JTokenType.Float || value.Type == JTokenType.Integer) && Finite(value.Value<double>());

        public static string ValidateAdditionRotations(JToken angles, IList<long> orderedCopyIds)
        {
            if (orderedCopyIds.Count == 0)
                return angles == null ? null : "relative_rotations_degrees applies only when add_members updates other instances.";
            if (angles is JArray values && values.Count == orderedCopyIds.Count &&
                values.All(t => ReadableNumber(t) && Math.Abs(t.Value<double>()) <= 360))
                return null;
            return "add_members requires known relative_rotations_degrees in [-360, 360] for the same-handed copies " +
                "in this group id order: " + string.Join(", ", orderedCopyIds) + ". Member directions do not establish the group pose.";
        }

        // The API guarantees the same member order across instances of a type. Track the
        // retained ids in the reference group across the rebuild to link the old and new orders.
        public static double[] MeasureRetainedTranslation(IList<long> oldReferenceIds, IList<long> newReferenceIds,
            IList<GroupMemberBounds> before, IList<GroupMemberBounds> after, double toleranceFeet)
        {
            if (oldReferenceIds.Count != before.Count || newReferenceIds.Count != after.Count) return null;
            var newSlots = newReferenceIds.Select((id, slot) => new { id, slot }).ToDictionary(row => row.id, row => row.slot);
            double[] shift = null;
            for (int i = 0; i < oldReferenceIds.Count; i++)
            {
                if (!newSlots.TryGetValue(oldReferenceIds[i], out int slot)) return null;
                GroupMemberBounds oldMember = before[i], newMember = after[slot];
                if (oldMember.Key == null || oldMember.Key != newMember.Key) return null;
                if (!BoundsReadable(oldMember) || !BoundsReadable(newMember)) continue;
                shift = shift ?? Enumerable.Range(0, 3).Select(axis => newMember.Min[axis] - oldMember.Min[axis]).ToArray();
                if (Enumerable.Range(0, 3).Any(axis =>
                    Math.Abs(newMember.Min[axis] - oldMember.Min[axis] - shift[axis]) > toleranceFeet ||
                    Math.Abs(newMember.Max[axis] - oldMember.Max[axis] - shift[axis]) > toleranceFeet)) return null;
            }
            return shift;
        }

        private static bool AxisReadable(GroupMemberBounds row, int axis) => row?.Min?.Length == 3 && row.Max?.Length == 3 &&
            Finite(row.Min[axis]) && Finite(row.Max[axis]) && row.Max[axis] >= row.Min[axis];

        private static bool BoundsReadable(GroupMemberBounds row) => row?.Key != null &&
            Enumerable.Range(0, 3).All(axis => AxisReadable(row, axis));

        // A renumbered member needs geometric evidence; an id or a type alone does not
        // identify it. Limit substitution to members with a readable location curve.
        public static bool SameRecreatedCurveMember(GroupMemberBounds before, GroupMemberBounds after)
        {
            double tolerance = GroupRedefinitionRules.BoxTolerance;
            var origin = new double[3];
            return before.Key != null && before.Key == after.Key &&
                before.CurvePath?.Length > 0 && after.CurvePath?.Length > 0 &&
                BoundsReadable(before) && BoundsReadable(after) &&
                BoundsAxesHeld(before, after, origin, tolerance, 3) &&
                CurvePathHeld(before, after, origin, origin, 0, tolerance) == true &&
                OrientationHeld(before, after, 0) == true;
        }

        public static JArray AddedMembersInCopy(IList<GroupMemberBounds> reference, IList<GroupMemberBounds> copy,
            IEnumerable<long> addedReferenceIds, IEnumerable<long> includedDependentIds,
            GroupPlacementSnapshot referenceBefore, GroupPlacementSnapshot copyBefore,
            IList<GroupMemberBounds> looseBefore, double declaredDegrees, out JObject placement)
        {
            placement = null;
            var expected = new HashSet<long>(addedReferenceIds.Concat(includedDependentIds));
            if (expected.Count == 0 || reference.Count != copy.Count || referenceBefore.Members.Count != copyBefore.Members.Count)
                return null;
            var previous = new HashSet<long>(copyBefore.Members.Select(m => m.Id));
            var currentSlots = reference.Select((member, i) => new { member.Id, Index = i }).ToDictionary(row => row.Id, row => row.Index);
            var looseById = looseBefore.ToDictionary(member => member.Id);
            var expectedMembers = new List<GroupMemberBounds>();
            var actualMembers = new List<GroupMemberBounds>();
            var ids = new JArray();
            for (int i = 0; i < reference.Count; i++)
            {
                if (!expected.Contains(reference[i].Id))
                    continue;
                if (previous.Contains(copy[i].Id) || !looseById.TryGetValue(reference[i].Id, out GroupMemberBounds original)) return null;
                expectedMembers.Add(original);
                actualMembers.Add(copy[i]);
                ids.Add(copy[i].Id);
            }
            if (ids.Count != expected.Count) return null;
            double toleranceFeet = GroupRedefinitionRules.BoxTolerance;
            double toleranceMm = toleranceFeet * 304.8;
            double angle = Radians(declaredDegrees);
            placement = ComparePlacedBounds(expectedMembers, actualMembers, referenceBefore.Origin, copyBefore.Origin, angle, toleranceMm, null);
            placement["rotation_source"] = "declared";
            placement["rotation_degrees"] = angle * 180 / Math.PI;
            for (int i = 0; i < referenceBefore.Members.Count; i++)
            {
                GroupMemberBounds a = referenceBefore.Members[i], b = copyBefore.Members[i];
                if (a.Reflected.HasValue && b.Reflected.HasValue && a.Reflected != b.Reflected)
                {
                    placement["matches"] = false;
                    placement["reason"] = "The retained family transforms have different reflection states, contrary to the declared same handedness.";
                    return null;
                }
                bool hasSlot = currentSlots.TryGetValue(a.Id, out int slot);
                bool? referenceOrientation = hasSlot ? OrientationHeld(a, reference[slot], 0) : null;
                bool? copyOrientation = hasSlot ? OrientationHeld(b, copy[slot], 0) : null;
                bool? referencePath = hasSlot ? CurvePathHeld(a, reference[slot], referenceBefore.Origin, referenceBefore.Origin, 0, toleranceFeet) : null;
                bool? copyPath = hasSlot ? CurvePathHeld(b, copy[slot], copyBefore.Origin, copyBefore.Origin, 0, toleranceFeet) : null;
                if (hasSlot && (referencePath != true || copyPath != true))
                {
                    FailCurvePath(placement, "retained", a.Id, referencePath, copyPath);
                    return null;
                }
                if (referenceOrientation != true || copyOrientation != true)
                {
                    if (!referenceOrientation.HasValue || !copyOrientation.HasValue) placement["coverage_complete"] = false;
                    placement["matches"] = false;
                    placement["reason"] = "A retained member's orientation could not be confirmed against the pre-regeneration reading.";
                    return null;
                }
            }
            for (int i = 0; i < expectedMembers.Count; i++)
            {
                GroupMemberBounds original = expectedMembers[i], retained = reference[currentSlots[original.Id]];
                bool? referenceOrientation = OrientationHeld(original, retained, 0);
                bool? copyOrientation = OrientationHeld(original, actualMembers[i], angle);
                if (original.Axes != null)
                    placement["members"][i]["orientation_matches"] = copyOrientation;
                bool? referencePath = CurvePathHeld(original, retained, referenceBefore.Origin, referenceBefore.Origin, 0, toleranceFeet);
                bool? copyPath = CurvePathHeld(original, actualMembers[i], referenceBefore.Origin, copyBefore.Origin, angle, toleranceFeet);
                if (original.CurvePath != null || retained.CurvePath != null || actualMembers[i].CurvePath != null)
                {
                    placement["members"][i]["reference_curve_path_matches"] = referencePath;
                    placement["members"][i]["curve_path_matches"] = copyPath;
                }
                if (referencePath != true || copyPath != true)
                {
                    FailCurvePath(placement, "added", original.Id, referencePath, copyPath);
                    return null;
                }
                if (!BoundsReadable(original) || !BoundsReadable(retained) ||
                    !BoundsAxesHeld(original, retained, new double[3], toleranceFeet, 3) ||
                    referenceOrientation != true || copyOrientation != true)
                {
                    if (!referenceOrientation.HasValue || !copyOrientation.HasValue) placement["coverage_complete"] = false;
                    placement["matches"] = false;
                    placement["reason"] = "An added member's position or orientation could not be confirmed against the pre-regeneration reading.";
                    placement["unverified_source_member_id"] = original.Id;
                    return null;
                }
            }
            return placement.Value<bool>("coverage_complete") && placement.Value<bool?>("matches") == true ? ids : null;
        }

        private static void FailCurvePath(JObject report, string role, long sourceId, bool? referencePath, bool? copyPath)
        {
            bool measured = referencePath.HasValue && copyPath.HasValue;
            if (!measured) report["coverage_complete"] = false;
            report["matches"] = false;
            report["unverified_source_member_id"] = sourceId;
            report["reason"] = measured
                ? "A " + role + " member's curve path differs from its expected placement."
                : "A " + role + " member's full curve path could not be verified within the tolerance.";
        }

        public static JObject ComparePlacedMembers(IList<GroupMemberBounds> reference, IList<GroupMemberBounds> copy,
            double[] referenceOrigin, double[] copyOrigin, double toleranceMm, double? declaredDegrees)
        {
            // Agreement between member directions can also be caused by a variation in all of them.
            double? angle = declaredDegrees.HasValue ? (double?)Radians(declaredDegrees.Value) : null;
            JObject report = ComparePlacedBounds(reference, copy, referenceOrigin, copyOrigin, angle, toleranceMm,
                "The Revit API does not expose the model group's rotation and reflection. Supply a known relative rotation between groups with the same handedness to compare plan geometry.");
            if (angle.HasValue)
            {
                report["rotation_source"] = "declared";
                bool complete = report.Value<bool>("coverage_complete");
                bool differs = report.Value<bool?>("matches") == false;
                for (int i = 0; i < Math.Min(reference.Count, copy.Count); i++)
                {
                    if (reference[i].CurvePath == null && copy[i].CurvePath == null) continue;
                    bool? matches = CurvePathHeld(reference[i], copy[i], referenceOrigin, copyOrigin, angle.Value, toleranceMm / 304.8);
                    report["members"][i]["curve_path_matches"] = matches;
                    if (!matches.HasValue)
                    {
                        complete = false;
                        if (report["reason"] == null) report["reason"] = "Some full member curve paths could not be verified within the tolerance.";
                    }
                    differs |= matches == false;
                }
                report["coverage_complete"] = complete;
                report["matches"] = differs ? (JToken)false : complete ? (JToken)true : JValue.CreateNull();
            }
            return report;
        }

        private static bool PointReadable(double[] point) => point?.Length == 3 && point.All(Finite);

        private static bool? CurvePathHeld(GroupMemberBounds before, GroupMemberBounds after,
            double[] from, double[] to, double angle, double toleranceFeet)
        {
            double[][] a = before.CurvePath, b = after.CurvePath;
            if (a == null && b == null) return true;
            bool Readable(double[][] path) => path != null && (path.Length == 2 || path.Length == 5) && path.All(PointReadable);
            if (!Readable(a) || !Readable(b) || !PointReadable(from) || !PointReadable(to) ||
                !Finite(toleranceFeet) || toleranceFeet < 0) return null;
            if (a.Length != b.Length) return false;

            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double[] Place(double[] point)
            {
                double x = point[0] - from[0], y = point[1] - from[1];
                return new[] { to[0] + cos * x - sin * y, to[1] + sin * x + cos * y, to[2] + point[2] - from[2] };
            }
            double[][] expected = a.Select(Place).ToArray();
            if (!expected.All(PointReadable)) return null;
            bool arc = a.Length == 5;
            double[] expectedCenter = arc && PointReadable(before.CurveArcCenter) ? Place(before.CurveArcCenter) : null;
            bool? Matches(bool reverse)
            {
                double[] Observed(int i) => b[reverse ? b.Length - 1 - i : i];
                for (int i = 0; i < expected.Length; i++)
                    if (VectorLength(Difference(expected[i], Observed(i))) > toleranceFeet) return false;
                if (!arc) return true;
                if (!PointReadable(expectedCenter) || !PointReadable(after.CurveArcCenter) ||
                    !ArcSweepReadable(before.CurveArcSweep) || !ArcSweepReadable(after.CurveArcSweep)) return null;

                double[] radial = Difference(expected[2], expectedCenter);
                double[] otherRadial = Difference(Observed(2), after.CurveArcCenter);
                double[] tangent = ArcMidpointTangent(expected[1], expected[3], before.CurveArcSweep);
                double[] otherTangent = ArcMidpointTangent(Observed(1), Observed(3), after.CurveArcSweep);

                // A circle is center + radial*cos(u) + tangent*sin(u), with u measured from its midpoint.
                // These two in-plane vectors include the radius. The parameter error moves a point along
                // the second circle by at most radius * half the difference between the two sweeps.
                // The triangle inequality bounds the separation at every parameter, including between samples.
                double upperBound = VectorLength(Difference(expectedCenter, after.CurveArcCenter)) +
                    VectorLength(Difference(radial, otherRadial)) + VectorLength(Difference(tangent, otherTangent)) +
                    VectorLength(otherRadial) * Math.Abs(before.CurveArcSweep - after.CurveArcSweep) / 2;
                return upperBound <= toleranceFeet ? (bool?)true : null;
            }

            bool? forward = Matches(false);
            if (forward == true) return true;
            bool? reversed = Matches(true);
            if (reversed == true) return true;
            return forward == false && reversed == false ? (bool?)false : null;
        }

        private static bool ArcSweepReadable(double sweep) => sweep > 0 && sweep <= 2 * Math.PI + 1e-9;

        private static double[] ArcMidpointTangent(double[] firstQuarter, double[] thirdQuarter, double sweep)
        {
            double scale = 1 / (2 * Math.Sin(sweep / 4));
            return Difference(thirdQuarter, firstQuarter).Select(component => component * scale).ToArray();
        }

        private static double[] Difference(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        private static double VectorLength(double[] vector) => Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);

        private static bool? OrientationHeld(GroupMemberBounds before, GroupMemberBounds after, double angle)
        {
            if (before.Key == null || after.Key == null) return null;
            if (before.Key != after.Key) return false;
            if (before.Axes == null) return after.Axes == null;
            if (before.Axes.Length == 0 || after.Axes == null || after.Axes.Length == 0) return null;
            if (after.Axes.Length != before.Axes.Length) return false;
            if (before.Axes.Length == 2 && (!before.Reflected.HasValue || !after.Reflected.HasValue)) return null;
            if (before.Reflected != after.Reflected) return false;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            for (int i = 0; i < before.Axes.Length; i++)
            {
                double[] a = before.Axes[i], b = after.Axes[i];
                if (!PointReadable(a) || !PointReadable(b)) return null;
                var expected = new[] { cos * a[0] - sin * a[1], sin * a[0] + cos * a[1], a[2] };
                bool SameAxis(int sign) => Enumerable.Range(0, 3).All(axis => Math.Abs(expected[axis] - sign * b[axis]) <= 1e-6);
                if (!SameAxis(1) && !(before.Axes.Length == 1 && SameAxis(-1))) return false;
            }
            return true;
        }

        private static double Radians(double degrees)
        {
            double radians = degrees * Math.PI / 180;
            return Math.Atan2(Math.Sin(radians), Math.Cos(radians));
        }
        public static bool NeedsPlanGeometry(double degrees) => Oblique(Radians(degrees));
        private static bool Oblique(double angle) => Math.Abs(Math.Sin(angle)) > 1e-10 && Math.Abs(Math.Cos(angle)) > 1e-10;

        private static bool BoundsAxesHeld(GroupMemberBounds before, GroupMemberBounds after, double[] shift, double toleranceFeet, int axes) =>
            Enumerable.Range(0, axes).All(axis => Math.Abs(after.Min[axis] - before.Min[axis] - shift[axis]) <= toleranceFeet &&
                Math.Abs(after.Max[axis] - before.Max[axis] - shift[axis]) <= toleranceFeet);

        private static JObject ComparePlacedBounds(IList<GroupMemberBounds> reference, IList<GroupMemberBounds> copy,
            double[] referenceOrigin, double[] copyOrigin, double? angle, double toleranceMm, string reason)
        {
            double[] shift = PointReadable(referenceOrigin) && PointReadable(copyOrigin)
                ? Enumerable.Range(0, 3).Select(i => copyOrigin[i] - referenceOrigin[i]).ToArray() : null;
            if (shift == null || angle == null)
            {
                JObject incomplete = CompareMemberBounds(reference, copy, shift, toleranceMm, comparePlan: false);
                incomplete["reason"] = shift == null ? "Group origin is unreadable." : reason;
                return incomplete;
            }
            if (Math.Abs(angle.Value) <= 1e-10) return CompareMemberBounds(reference, copy, shift, toleranceMm);

            double cos = Math.Cos(angle.Value), sin = Math.Sin(angle.Value);
            // A quarter turn permutes the box axes. For other angles, turning a world
            // AABB overestimates irregular geometry, so measure the underlying points.
            bool needsGeometry = Oblique(angle.Value);
            var expected = reference.Select(member => PlacedBounds(member, referenceOrigin, copyOrigin, cos, sin, needsGeometry)).ToList();
            var measured = needsGeometry
                ? copy.Select(member => PlacedBounds(member, copyOrigin, copyOrigin, 1, 0, true)).ToList() : copy;
            JObject report = CompareMemberBounds(expected, measured, new double[3], toleranceMm);
            report["rotation_degrees"] = angle.Value * 180 / Math.PI;
            if (needsGeometry && report.Value<bool>("coverage_complete") == false)
                report["reason"] = "Some member geometry could not be measured in the rotated placement.";
            return report;
        }

        private static GroupMemberBounds PlacedBounds(GroupMemberBounds member, double[] from, double[] to,
            double cos, double sin, bool useGeometry)
        {
            var bounds = new GroupMemberBounds { Id = member.Id, Key = member.Key,
                Min = new[] { double.NaN, double.NaN, double.NaN }, Max = new[] { double.NaN, double.NaN, double.NaN } };
            if (AxisReadable(member, 2))
            {
                bounds.Min[2] = member.Min[2] + to[2] - from[2];
                bounds.Max[2] = member.Max[2] + to[2] - from[2];
            }
            if (!useGeometry && (!AxisReadable(member, 0) || !AxisReadable(member, 1))) return bounds;
            IList<double[]> points = useGeometry ? member.CapturedGeometryPoints ?? member.ReadGeometryPoints?.Invoke() : new[]
            {
                member.Min, member.Max,
                new[] { member.Min[0], member.Max[1], member.Min[2] },
                new[] { member.Max[0], member.Min[1], member.Max[2] }
            };
            if (points == null || points.Count == 0 || !points.All(PointReadable)) return bounds;
            bounds.Min[0] = bounds.Min[1] = double.PositiveInfinity;
            bounds.Max[0] = bounds.Max[1] = double.NegativeInfinity;
            foreach (double[] p in points)
            {
                double x = p[0] - from[0], y = p[1] - from[1];
                double rotatedX = to[0] + cos * x - sin * y, rotatedY = to[1] + sin * x + cos * y;
                bounds.Min[0] = Math.Min(bounds.Min[0], rotatedX);
                bounds.Min[1] = Math.Min(bounds.Min[1], rotatedY);
                bounds.Max[0] = Math.Max(bounds.Max[0], rotatedX);
                bounds.Max[1] = Math.Max(bounds.Max[1], rotatedY);
            }
            return bounds;
        }

        /// <summary>Read back additions observed in the transaction, excluding the primary group and provisional ids.</summary>
        public static JArray AlsoCreated(long groupId, long typeId, IEnumerable<long> addedIds,
            IEnumerable<long> expectedNewMembers, Func<long, string> readUniqueId)
        {
            var observed = new HashSet<long>(addedIds);
            if (!observed.SetEquals(expectedNewMembers.Concat(new[] { groupId, typeId }))) return null;
            var rows = new JArray();
            foreach (long id in observed.OrderBy(id => id))
            {
                string uid = readUniqueId(id);
                if (string.IsNullOrEmpty(uid)) return null;
                if (id != groupId) rows.Add(new JObject { ["element_id"] = id, ["unique_id"] = uid });
            }
            return rows;
        }

        public static JObject VariationResult(long referenceGroupId, long typeId, JArray instances)
        {
            var rows = instances.OfType<JObject>().ToList();
            bool complete = rows.Count > 0 && rows.All(row => row.Value<bool>("coverage_complete"));
            int variations = rows.Count(row => row.Value<bool?>("matches") == false);
            return new JObject
            {
                ["operation"] = "check_variation", ["read_only"] = true,
                ["reference_group_id"] = referenceGroupId, ["type_id"] = typeId, ["instances"] = instances,
                ["coverage_complete"] = complete,
                ["variation_count"] = complete ? (JToken)variations : JValue.CreateNull(),
                ["has_variation"] = variations > 0 ? (JToken)true : complete ? (JToken)false : JValue.CreateNull(),
                ["method"] = "Member count, types, bounds and readable curve paths in Revit's GetMemberIds order. Plan geometry requires declared rotation between groups with the same handedness. Parameters are outside this check."
            };
        }

        public static JObject CompareMemberBounds(IList<GroupMemberBounds> before, IList<GroupMemberBounds> after,
            double[] shiftFeet, double toleranceMm, bool comparePlan = true)
        {
            var rows = new JArray();
            bool shiftReadable = shiftFeet?.Length == 3 && shiftFeet.All(Finite) && Finite(toleranceMm) && toleranceMm >= 0;
            bool measured = comparePlan && shiftReadable && before.Count > 0 && before.All(BoundsReadable) && after.All(BoundsReadable);
            bool differs = before.Count != after.Count;
            for (int i = 0; i < Math.Max(before.Count, after.Count); i++)
            {
                GroupMemberBounds a = i < before.Count ? before[i] : null, b = i < after.Count ? after[i] : null;
                bool readable = shiftReadable && AxisReadable(a, 2) && AxisReadable(b, 2);
                bool height = readable && MemberHeightHeld(a.Min[2] * 304.8, a.Max[2] * 304.8,
                    b.Min[2] * 304.8, b.Max[2] * 304.8, shiftFeet[2] * 304.8, toleranceMm);
                bool? sameType = a?.Key == null || b?.Key == null ? (bool?)null : a.Key == b.Key;
                bool planMeasured = comparePlan && shiftReadable && BoundsReadable(a) && BoundsReadable(b);
                bool match = planMeasured && sameType == true && height && BoundsAxesHeld(a, b, shiftFeet, toleranceMm / 304.8, 2);
                differs |= sameType == false || (readable && !height) || (planMeasured && !match);
                rows.Add(new JObject { ["member_index"] = i, ["source_member_id"] = a == null ? null : (JToken)a.Id,
                    ["member_id"] = b == null ? null : (JToken)b.Id, ["measured"] = planMeasured, ["type_matches"] = sameType,
                    ["expected_bottom_mm"] = !shiftReadable || !AxisReadable(a, 2) ? null : (JToken)((a.Min[2] + shiftFeet[2]) * 304.8),
                    ["expected_top_mm"] = !shiftReadable || !AxisReadable(a, 2) ? null : (JToken)((a.Max[2] + shiftFeet[2]) * 304.8),
                    ["bottom_mm"] = !AxisReadable(b, 2) ? null : (JToken)(b.Min[2] * 304.8),
                    ["top_mm"] = !AxisReadable(b, 2) ? null : (JToken)(b.Max[2] * 304.8),
                    ["height_matches"] = readable ? (JToken)height : null,
                    ["geometry_matches"] = planMeasured ? (JToken)match : null });
            }
            return new JObject { ["coverage_complete"] = measured,
                ["matches"] = differs ? (JToken)false : measured ? (JToken)true : JValue.CreateNull(), ["members"] = rows };
        }

        /// <summary>Each member's bottom AND top must move by the level elevation difference.</summary>
        public static bool MemberHeightHeld(double? beforeMinMm, double? beforeMaxMm, double? afterMinMm, double? afterMaxMm,
            double levelShiftMm, double toleranceMm)
        {
            double?[] readings = { beforeMinMm, beforeMaxMm, afterMinMm, afterMaxMm };
            return readings.All(v => v.HasValue && Finite(v.Value)) && Finite(levelShiftMm) && Finite(toleranceMm) && toleranceMm >= 0 &&
                beforeMaxMm.Value >= beforeMinMm.Value && afterMaxMm.Value >= afterMinMm.Value &&
                Math.Abs(afterMinMm.Value - beforeMinMm.Value - levelShiftMm) <= toleranceMm &&
                Math.Abs(afterMaxMm.Value - beforeMaxMm.Value - levelShiftMm) <= toleranceMm;
        }

        public static double OffsetMmForWorldZ(double worldZMm, double newLevelElevationMm) =>
            worldZMm - newLevelElevationMm;

        public static bool PositionHeld(double beforeX, double beforeY, double beforeZ,
            double afterX, double afterY, double afterZ, double toleranceMm)
        {
            double dx = afterX-beforeX, dy = afterY-beforeY, dz = afterZ-beforeZ;
            return Math.Sqrt(dx*dx + dy*dy + dz*dz) <= toleranceMm;
        }
    }
}
