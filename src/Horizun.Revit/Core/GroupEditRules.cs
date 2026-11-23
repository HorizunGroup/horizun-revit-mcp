// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF EDITING A GROUP'S MEMBERS.
//
// Measured on Revit 2025 against a disposable plumbing model (a model group of a
// pipe and two elbows, 29 instances, each elbow connected to a downpipe OUTSIDE
// the group):
//
//   * ElementTransformUtils.MoveElement on a member of a group with several
//     instances returns WITHOUT an exception and moves nothing; the transaction
//     then fails at commit with "Can't make change to Group ... with more than one
//     instance". A caller that does not read the commit status believes it moved.
//   * Swapping an instance onto a redefined type regenerates EVERY member with a
//     new id at the next Regenerate, and the regenerated members come back with
//     their connectors OPEN: every connection to an element outside the group is
//     lost. The external element is not touched.
//   * Removing a pipe but keeping its "Center Line" companion element in the group
//     makes the swap fail at commit with the same "more than one instance" error.
//     The companion is a dependent of the pipe and has to leave with it.
//   * Ungrouping the reference instance keeps every member id; moving its elbows
//     there drags the connected member pipe AND stretches the connected external
//     downpipes, which stay connected. NewGroup over the same ids keeps them.
//
// Everything below is the arithmetic those facts need: which refusal to give, how
// to read an instance's placement relative to the reference, how to pair members,
// and when an external connection can be put back.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    /// <summary>One element asked to move that Revit will not move on its own: it is a group member.</summary>
    public sealed class GroupMemberTarget
    {
        public long ElementId;
        public long GroupId;
        public string GroupTypeName;
        public int TypeInstances;
        public string Operation;
    }

    public static class GroupMemberGuard
    {
        public const string Code = "miembro_de_grupo";

        /// <summary>Operations that change where an element is, which Revit refuses for a group member.</summary>
        public static readonly string[] GeometricOperations =
            { "move", "move_preserve_pin", "rotate", "mirror", "set_curve", "array_linear", "array_radial" };

        public static bool IsGeometric(string operation)
            => operation != null && Array.IndexOf(GeometricOperations, operation.Trim().ToLowerInvariant()) >= 0;

        /// <summary>The refusal text for one or more group members, naming each and the ways through.</summary>
        public static string Message(IList<GroupMemberTarget> targets)
        {
            if (targets == null || targets.Count == 0) return null;
            var rows = targets.Take(20).Select(t =>
                t.ElementId.ToString(CultureInfo.InvariantCulture) + " (" + t.Operation + ") is a member of group " +
                t.GroupId.ToString(CultureInfo.InvariantCulture) +
                (string.IsNullOrEmpty(t.GroupTypeName) ? "" : " '" + t.GroupTypeName + "'") +
                ", whose type has " + t.TypeInstances.ToString(CultureInfo.InvariantCulture) + " instance(s)");
            string list = string.Join("; ", rows) + (targets.Count > 20 ? "; and " + (targets.Count - 20) + " more" : "");
            bool shared = targets.Any(t => t.TypeInstances > 1);
            return Code + ": " + list + ". Revit does not move a group member on its own: MoveElement returns without " +
                   "error and leaves it where it was" + (shared ? ", and the commit then fails with \"Can't make change to " +
                   "Group with more than one instance\"" : "") + ". Nothing was written. Ways through: " +
                   "horizun_manage_groups operation=edit_type_members (group_id of any instance, the same move/set_curve " +
                   "on its members, scope=type - every instance changes, like Edit Group, and is verified one by one); " +
                   "or horizun_manage_groups operation=ungroup first, when only this copy should change and it may stop " +
                   "being a group. A Python script hits the same wall.";
        }
    }

    /// <summary>Did a set_curve land? The line may be trimmed by a join; it must not be the line it already was.</summary>
    public static class CurveChangeRules
    {
        public const string Verified = "verified";
        public const string OffLine = "off_the_requested_line";
        public const string Unchanged = "unchanged";

        private static double Dist(double[] a, double[] b)
            => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

        private static double OffLineDistance(double[] p, double[] a, double[] b)
        {
            double[] d = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double len = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            if (len < 1e-12) return Dist(p, a);
            double[] v = { p[0] - a[0], p[1] - a[1], p[2] - a[2] };
            double cx = v[1] * d[2] - v[2] * d[1], cy = v[2] * d[0] - v[0] * d[2], cz = v[0] * d[1] - v[1] * d[0];
            return Math.Sqrt(cx * cx + cy * cy + cz * cz) / len;
        }

        private static bool SameEnds(double[] a0, double[] a1, double[] b0, double[] b1, double tol)
            => (Dist(a0, b0) <= tol && Dist(a1, b1) <= tol) || (Dist(a0, b1) <= tol && Dist(a1, b0) <= tol);

        /// <summary>
        /// verified: both re-read ends lie on the requested line AND the curve is not the one it
        /// was before (unless the request asked for exactly that one, which the caller refuses
        /// earlier as a no-op). unchanged: the element still has its old ends - Revit kept it
        /// where it was, whatever it said. off_the_requested_line: it moved somewhere else.
        /// </summary>
        public static string Judge(double[] before0, double[] before1, double[] want0, double[] want1,
                                   double[] got0, double[] got1, double tolerance)
        {
            bool askedForChange = !SameEnds(before0, before1, want0, want1, tolerance);
            if (askedForChange && SameEnds(before0, before1, got0, got1, tolerance)) return Unchanged;
            if (OffLineDistance(got0, want0, want1) > tolerance || OffLineDistance(got1, want0, want1) > tolerance) return OffLine;
            return Verified;
        }
    }

    /// <summary>A rigid placement in plan: rotation about Z (optionally mirrored), then a translation.</summary>
    public sealed class PlanarPlacement
    {
        public double Cos = 1, Sin = 0;
        public bool Mirrored;
        public double Tx, Ty, Tz;
        public double ResidualFeet;

        public double[] Apply(double[] p)
        {
            double x = p[0], y = Mirrored ? -p[1] : p[1];
            return new[] { Cos * x - Sin * y + Tx, Sin * x + Cos * y + Ty, p[2] + Tz };
        }

        /// <summary>A direction (no translation).</summary>
        public double[] ApplyVector(double[] v)
        {
            double x = v[0], y = Mirrored ? -v[1] : v[1];
            return new[] { Cos * x - Sin * y, Sin * x + Cos * y, v[2] };
        }

        public double AngleDegrees => Math.Atan2(Sin, Cos) * 180.0 / Math.PI;
    }

    public static class PlanarFit
    {
        /// <summary>
        /// The placement that carries the reference points onto the instance points (paired by
        /// index), by least squares in plan and the mean in Z. Tries the direct and the mirrored
        /// fit and keeps the better one. Null when fewer than one pair is given.
        /// </summary>
        public static PlanarPlacement Fit(IList<double[]> reference, IList<double[]> instance)
        {
            if (reference == null || instance == null || reference.Count == 0 || reference.Count != instance.Count) return null;
            PlanarPlacement a = FitOne(reference, instance, false), b = FitOne(reference, instance, true);
            return b.ResidualFeet + 1e-9 < a.ResidualFeet ? b : a;
        }

        private static PlanarPlacement FitOne(IList<double[]> r, IList<double[]> s, bool mirrored)
        {
            int n = r.Count;
            double rx = 0, ry = 0, rz = 0, sx = 0, sy = 0, sz = 0;
            for (int i = 0; i < n; i++)
            {
                double y = mirrored ? -r[i][1] : r[i][1];
                rx += r[i][0]; ry += y; rz += r[i][2]; sx += s[i][0]; sy += s[i][1]; sz += s[i][2];
            }
            rx /= n; ry /= n; rz /= n; sx /= n; sy /= n; sz /= n;
            double num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                double ax = r[i][0] - rx, ay = (mirrored ? -r[i][1] : r[i][1]) - ry;
                double bx = s[i][0] - sx, by = s[i][1] - sy;
                num += ax * by - ay * bx; den += ax * bx + ay * by;
            }
            double ang = (Math.Abs(num) < 1e-15 && Math.Abs(den) < 1e-15) ? 0 : Math.Atan2(num, den);
            var p = new PlanarPlacement { Cos = Math.Cos(ang), Sin = Math.Sin(ang), Mirrored = mirrored };
            p.Tx = sx - (p.Cos * rx - p.Sin * ry);
            p.Ty = sy - (p.Sin * rx + p.Cos * ry);
            p.Tz = sz - rz;
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                double[] q = p.Apply(r[i]);
                double d = Math.Sqrt((q[0] - s[i][0]) * (q[0] - s[i][0]) + (q[1] - s[i][1]) * (q[1] - s[i][1]) + (q[2] - s[i][2]) * (q[2] - s[i][2]));
                if (d > worst) worst = d;
            }
            p.ResidualFeet = worst;
            return p;
        }
    }

    /// <summary>A member as the edit reads it: identity, and the points that place it (location point, curve ends, or box centre).</summary>
    public sealed class EditMember
    {
        public long Id;
        public string Key;            // category:type
        public List<double[]> Points = new List<double[]>();
    }

    public static class GroupEditMatching
    {
        /// <summary>
        /// Pairs the instance's members with the reference's BY POSITION IN GetMemberIds (the
        /// order Revit keeps for every instance of a type - measured), and checks that each pair
        /// has the same category/type and the same number of placing points. Null and the reason
        /// otherwise: a pairing that has to be guessed is not one.
        /// </summary>
        public static List<int> ByOrder(IList<EditMember> reference, IList<EditMember> instance, out string why)
        {
            why = null;
            if (reference.Count != instance.Count)
            { why = "it holds " + instance.Count + " member(s) and the reference holds " + reference.Count; return null; }
            for (int i = 0; i < reference.Count; i++)
                if (reference[i].Key != instance[i].Key || reference[i].Points.Count != instance[i].Points.Count)
                { why = "member " + instance[i].Id + " (position " + i + ") is " + instance[i].Key + " where the reference has " + reference[i].Key; return null; }
            return Enumerable.Range(0, reference.Count).ToList();
        }

        /// <summary>All placing points of a member list, in order, for a fit.</summary>
        public static List<double[]> Flatten(IEnumerable<EditMember> members)
            => members.SelectMany(m => m.Points).ToList();

        /// <summary>
        /// The greedy match of expected members (already carried into the instance's frame) to the
        /// members the instance holds now: same key, nearest points, within tolerance. Returns
        /// expected index -> actual index; unmatched expected entries are absent.
        /// </summary>
        public static Dictionary<int, int> ByGeometry(IList<EditMember> expected, IList<EditMember> actual, double tolerance)
        {
            var map = new Dictionary<int, int>();
            var used = new HashSet<int>();
            var candidates = new List<Tuple<double, int, int>>();
            for (int e = 0; e < expected.Count; e++)
                for (int a = 0; a < actual.Count; a++)
                {
                    if (expected[e].Key != actual[a].Key) continue;
                    double d = Distance(expected[e], actual[a]);
                    if (d <= tolerance) candidates.Add(Tuple.Create(d, e, a));
                }
            foreach (var c in candidates.OrderBy(c => c.Item1))
            {
                if (map.ContainsKey(c.Item2) || used.Contains(c.Item3)) continue;
                map[c.Item2] = c.Item3; used.Add(c.Item3);
            }
            return map;
        }

        /// <summary>Largest point distance between two members with the same point count (a reversed curve counts as the same curve); infinity otherwise.</summary>
        public static double Distance(EditMember a, EditMember b)
        {
            if (a.Points.Count != b.Points.Count) return double.PositiveInfinity;
            if (a.Points.Count == 0) return 0;
            double straight = 0, reversed = 0;
            int n = a.Points.Count;
            for (int i = 0; i < n; i++)
            {
                straight = Math.Max(straight, D(a.Points[i], b.Points[i]));
                reversed = Math.Max(reversed, D(a.Points[i], b.Points[n - 1 - i]));
            }
            return Math.Min(straight, reversed);
        }

        public static double D(double[] a, double[] b)
            => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

        /// <summary>The mean offset actual - expected over matched pairs (the swap's displacement of a regenerated instance).</summary>
        public static double[] MeanOffset(IList<EditMember> expected, IList<EditMember> actual, IDictionary<int, int> map)
        {
            double x = 0, y = 0, z = 0; int n = 0;
            foreach (var kv in map)
            {
                var e = expected[kv.Key]; var a = actual[kv.Value];
                for (int i = 0; i < e.Points.Count && i < a.Points.Count; i++)
                { x += a.Points[i][0] - e.Points[i][0]; y += a.Points[i][1] - e.Points[i][1]; z += a.Points[i][2] - e.Points[i][2]; n++; }
            }
            return n == 0 ? new double[3] : new[] { x / n, y / n, z / n };
        }
    }

    /// <summary>One connection from a member to an element outside its group, captured before the edit.</summary>
    public sealed class ExternalConnection
    {
        public long GroupId;
        public int MemberIndex;
        public long MemberId;
        public long ExternalId;
        public bool ExternalIsCurve;
        /// <summary>The external connector is one of the two ends of a pipe/duct/conduit.</summary>
        public bool ExternalEndOfCurve;
        public double[] Point;        // where both connectors met, feet
        public bool MemberRemoved;    // the member leaves the group in this edit
        /// <summary>The external connector's origin before the edit (feet); null when not captured.</summary>
        public double[] ExternalPoint;
        /// <summary>Distance between the two connector origins before the edit: Revit keeps some connections "connected" while apart.</summary>
        public double GapBeforeFeet;
    }

    /// <summary>
    /// Putting an external connection back WITHOUT absorbing an offset it already had.
    ///
    /// Field 2026-10-27: 27 connections that were connected with a 0.702 mm offset came
    /// back stretched by that offset (moving unrelated risers), and two connected 8.3 mm
    /// apart were lost (beyond the 5 mm stretch limit) although their member never moved.
    /// Reconnecting to the member's connector closes the gap; the external end should
    /// follow only by what the MEMBER moved. Measured on Revit 2025: ConnectTo accepts
    /// two connectors that are apart and leaves them where they are.
    /// </summary>
    public static class PreservingReconnect
    {
        /// <summary>Where the external connector belongs: where it was, moved by what the member's connector moved.</summary>
        public static double[] Desired(double[] externalBefore, double[] memberBefore, double[] memberNow)
            => new[] { externalBefore[0] + memberNow[0] - memberBefore[0], externalBefore[1] + memberNow[1] - memberBefore[1], externalBefore[2] + memberNow[2] - memberBefore[2] };

        /// <summary>
        /// residualFeet: how far the external connector is from where it belongs. connect when it
        /// is there (the prior offset, if any, is kept); stretch a pipe/duct end that can follow;
        /// lost otherwise.
        /// </summary>
        public static string Decide(ExternalConnection c, double? residualFeet, double stretchLimitFeet, double toleranceFeet, out string why)
        {
            why = null;
            if (c.MemberRemoved) { why = "the member left the group, so the connection goes with it"; return ExternalReconnectRules.RemovedWithMember; }
            if (!residualFeet.HasValue) { why = "the regenerated member or the external connector could not be read"; return ExternalReconnectRules.Lost; }
            if (residualFeet.Value <= toleranceFeet) return ExternalReconnectRules.Connect;
            if (c.ExternalIsCurve && c.ExternalEndOfCurve && residualFeet.Value <= stretchLimitFeet) return ExternalReconnectRules.StretchAndConnect;
            why = c.ExternalIsCurve && c.ExternalEndOfCurve
                ? "the external end would have to move " + Mm(residualFeet.Value) + " mm, beyond the stretch limit of " + Mm(stretchLimitFeet) + " mm"
                : "the external element is not a pipe/duct end that can follow, and it would have to move " + Mm(residualFeet.Value) + " mm";
            return ExternalReconnectRules.Lost;
        }

        private static string Mm(double feet) => Math.Round(feet * 304.8, 2).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>How big an edit is, and whether a synchronous call may carry it.</summary>
    public static class GroupEditBudget
    {
        /// <summary>
        /// Above this many member-instances (instances x members) a synchronous call is refused
        /// and the edit goes through horizun_submit_job. Measured: 29 x 1,022 = 29,638 took ~21 min
        /// on a cloud workshared model (Revit 2024, field); 29 x 164 = 4,756 took ~60 s on a local
        /// model (Revit 2025). The 600 s synchronous limit is crossed somewhere between, so the
        /// line sits where the slow rate still fits: ~6,000 x 0.045 s = 270 s.
        /// </summary>
        public const int SyncMemberInstanceLimit = 6000;
        public const double FastSecondsPerMemberInstance = 0.0126;
        public const double SlowSecondsPerMemberInstance = 0.0425;

        public static long MemberInstances(int instances, int membersPerInstance) => (long)Math.Max(0, instances) * Math.Max(0, membersPerInstance);

        public static bool NeedsAsync(int instances, int membersPerInstance, bool runningAsJob)
            => !runningAsJob && MemberInstances(instances, membersPerInstance) > SyncMemberInstanceLimit;

        public static int[] EstimatedSeconds(int instances, int membersPerInstance)
        {
            long n = MemberInstances(instances, membersPerInstance);
            return new[] { (int)Math.Ceiling(n * FastSecondsPerMemberInstance), (int)Math.Ceiling(n * SlowSecondsPerMemberInstance) };
        }
    }

    /// <summary>Slope of a pipe as rise over horizontal run, in percent; and whether a change matters.</summary>
    public static class RunSlopeRules
    {
        /// <summary>Pipes steeper than this (percent) are risers/drops, not "horizontal" runs.</summary>
        public const double HorizontalMaxPercent = 20;
        /// <summary>A change smaller than this (percentage points) is read noise.</summary>
        public const double ChangeTolerancePercent = 0.001;

        public static double? Percent(double[] a, double[] b)
        {
            double run = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
            if (run < 1e-6) return null;
            return Math.Abs(b[2] - a[2]) / run * 100.0;
        }

        public static bool IsHorizontalRun(double? percent) => percent.HasValue && percent.Value <= HorizontalMaxPercent;

        public static bool Changed(double? before, double? after)
            => before.HasValue != after.HasValue || (before.HasValue && Math.Abs(before.Value - after.Value) > ChangeTolerancePercent);
    }

    /// <summary>Members that moved although nobody named them: Revit drags what is connected to a moved fitting.</summary>
    public static class DragRules
    {
        /// <summary>Per-point displacement after - before, feet; null when the two cannot be compared.</summary>
        public static List<double[]> Displacement(EditMember before, EditMember after)
        {
            if (before == null || after == null || before.Points.Count != after.Points.Count) return null;
            var d = new List<double[]>();
            for (int i = 0; i < before.Points.Count; i++)
                d.Add(new[] { after.Points[i][0] - before.Points[i][0], after.Points[i][1] - before.Points[i][1], after.Points[i][2] - before.Points[i][2] });
            return d;
        }

        public static double Largest(List<double[]> d)
            => d == null ? double.PositiveInfinity : d.Select(v => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])).DefaultIfEmpty(0).Max();

        /// <summary>Rigid when every point moved by the same vector (a dragged fitting or a pipe carried whole) - else it was stretched or tilted.</summary>
        public static bool Rigid(List<double[]> d, double tolerance)
        {
            if (d == null || d.Count == 0) return true;
            return d.All(v => GroupEditMatching.D(v, d[0]) <= tolerance);
        }
    }

    /// <summary>
    /// Every physical connection around a group type, keyed so it can be found again after the
    /// edit gave the members new ids: a member is (instance, member index, connector id), an
    /// element outside the groups is (element id, connector id). Internal (member-member) and
    /// neighbour (outside-outside, one hop from a member) connections are checked after the
    /// edit; Revit's failure resolution and the regeneration are not trusted to have kept them.
    /// </summary>
    public sealed class ConnectionLedger
    {
        public sealed class End
        {
            public int Instance = -1;   // -1: not a member
            public int Member = -1;
            public long ElementId;      // outside elements only
            public int Connector;
            public bool IsMember => Instance >= 0;
            public string Key => IsMember ? "m" + Instance + ":" + Member + ":" + Connector : "x" + ElementId + ":" + Connector;
        }

        public sealed class Link
        {
            public End A, B;
            public string Kind;          // internal | external | neighbour
            public double GapBeforeFeet;
            public string Key => string.CompareOrdinal(A.Key, B.Key) <= 0 ? A.Key + "|" + B.Key : B.Key + "|" + A.Key;
        }

        private readonly Dictionary<string, Link> _links = new Dictionary<string, Link>();
        public IEnumerable<Link> Links => _links.Values;
        public int Count => _links.Count;

        public static string KindOf(End a, End b)
            => a.IsMember && b.IsMember ? (a.Instance == b.Instance ? "internal" : "between_instances") : (a.IsMember || b.IsMember ? "external" : "neighbour");

        /// <summary>Adds a connection once, whichever end it was seen from.</summary>
        public bool Add(End a, End b, double gapFeet)
        {
            var l = new Link { A = a, B = b, Kind = KindOf(a, b), GapBeforeFeet = gapFeet };
            if (_links.ContainsKey(l.Key)) return false;
            _links[l.Key] = l;
            return true;
        }

        /// <summary>
        /// Re-check every captured connection. `connected` answers for one link now (null: an end
        /// is gone); `gapNow` gives the distance now. A link is lost when not connected; it is
        /// pulled apart when it is still connected but its gap grew beyond the tolerance.
        /// </summary>
        public JObjectLike Verify(Func<Link, bool?> connected, Func<Link, double?> gapNow, double toleranceFeet, int maxRows = 40)
        {
            var result = new JObjectLike();
            foreach (Link l in _links.Values)
            {
                Tally t = result.Of(l.Kind);
                t.Captured++;
                bool? c = connected(l);
                if (c != true) { t.Lost++; if (t.LostRows.Count < maxRows) t.LostRows.Add(l); continue; }
                double? g = gapNow(l);
                if (g.HasValue && g.Value > l.GapBeforeFeet + toleranceFeet) { t.PulledApart++; if (t.PulledRows.Count < maxRows) t.PulledRows.Add(Tuple.Create(l, g.Value)); }
            }
            return result;
        }

        public sealed class Tally
        {
            public int Captured, Lost, PulledApart;
            public readonly List<Link> LostRows = new List<Link>();
            public readonly List<Tuple<Link, double>> PulledRows = new List<Tuple<Link, double>>();
        }

        /// <summary>A tiny result holder so this file stays free of a JSON dependency.</summary>
        public sealed class JObjectLike
        {
            public readonly Dictionary<string, Tally> ByKind = new Dictionary<string, Tally>();
            public Tally Of(string kind) { Tally t; if (!ByKind.TryGetValue(kind, out t)) ByKind[kind] = t = new Tally(); return t; }
            public bool Holds(params string[] kinds) => kinds.All(k => !ByKind.ContainsKey(k) || (ByKind[k].Lost == 0 && ByKind[k].PulledApart == 0));
        }
    }

    public static class ExternalReconnectRules
    {
        public const string Connect = "connect";
        public const string StretchAndConnect = "stretch_and_connect";
        public const string Lost = "lost";
        public const string RemovedWithMember = "removed_with_member";

        /// <summary>
        /// What to do with one captured connection once the member is back: the external
        /// connector is where it was; the member's free connector is `gapFeet` away. Coincident:
        /// connect. A pipe/duct end within the stretch limit: move that end and connect (that is
        /// what Edit Group does to a connected run). Anything else is reported lost.
        /// </summary>
        public static string Decide(ExternalConnection c, double? gapFeet, double stretchLimitFeet, double coincidenceFeet, out string why)
        {
            why = null;
            if (c.MemberRemoved) { why = "the member left the group, so the connection goes with it"; return RemovedWithMember; }
            if (!gapFeet.HasValue) { why = "no free connector of the regenerated member was found near the connection"; return Lost; }
            if (gapFeet.Value <= coincidenceFeet) return Connect;
            if (c.ExternalIsCurve && c.ExternalEndOfCurve && gapFeet.Value <= stretchLimitFeet) return StretchAndConnect;
            why = c.ExternalIsCurve && c.ExternalEndOfCurve
                ? "the member's connector is " + Mm(gapFeet.Value) + " mm away, beyond the stretch limit of " + Mm(stretchLimitFeet) + " mm"
                : "the external element is not a pipe/duct end that can follow, and the member's connector is " + Mm(gapFeet.Value) + " mm away";
            return Lost;
        }

        private static string Mm(double feet) => Math.Round(feet * 304.8, 2).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>execute_python: a script that moves elements, and the group members it may have meant.</summary>
    public static class PythonGroupMoveScan
    {
        private static readonly Regex MoveCall = new Regex(
            @"\bMoveElements?\s*\(|\bRotateElements?\s*\(|\bMirrorElements?\s*\(|\.Location\s*\.\s*Move\s*\(|\.Move\s*\(|\.Point\s*=(?!=)|\.Curve\s*=(?!=)|\bSetLocation\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex ElementIdLiteral = new Regex(@"ElementId\s*\(\s*(?:System\.)?(?:Int64\s*\(\s*)?(\d{1,18})", RegexOptions.Compiled);

        public static bool CallsMove(string code) => !string.IsNullOrEmpty(code) && MoveCall.IsMatch(code);

        /// <summary>Element ids written into the source as ElementId(n).</summary>
        public static HashSet<long> LiteralIds(string code)
        {
            var ids = new HashSet<long>();
            if (string.IsNullOrEmpty(code)) return ids;
            foreach (Match m in ElementIdLiteral.Matches(code))
                if (long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long v)) ids.Add(v);
            return ids;
        }

        /// <summary>
        /// The group members a script referenced (literal ids, or declared modified/moved ids) whose
        /// placing points did not change. `before`/`after` map id -> flattened points in feet.
        /// </summary>
        public static List<long> Unmoved(IEnumerable<long> referenced, IDictionary<long, double[]> before,
                                         IDictionary<long, double[]> after, double tolerance)
        {
            var rows = new List<long>();
            foreach (long id in referenced.Distinct().OrderBy(x => x))
            {
                if (!before.TryGetValue(id, out double[] b) || !after.TryGetValue(id, out double[] a)) continue;
                if (b.Length != a.Length) continue;
                bool same = true;
                for (int i = 0; i < b.Length; i++) if (Math.Abs(b[i] - a[i]) > tolerance) { same = false; break; }
                if (same) rows.Add(id);
            }
            return rows;
        }
    }
}
