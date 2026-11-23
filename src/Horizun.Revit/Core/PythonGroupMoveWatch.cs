// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// execute_python: a script that MOVES things, watched for the one move Revit
// swallows. MoveElement on a member of a multi-instance group returns without an
// exception and leaves the member where it was (measured, Revit 2025); the script
// then reports success. Only when the source calls a move/rotate/location API is
// every group member's placement read before and after the script, and the
// members the script referenced (ElementId(n) literals, or modified_ids/moved_ids
// it declared) that did not move are reported with code miembro_de_grupo. It is a
// warning about a script's own testimony, not a verification of it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PythonGroupMoveWatch
    {
        private const int Cap = 20000;
        private readonly Dictionary<long, double[]> _before = new Dictionary<long, double[]>();
        private readonly Dictionary<long, long> _groupOf = new Dictionary<long, long>();
        private Dictionary<long, double[]> _after;
        private bool _truncated;
        private string _error;

        private PythonGroupMoveWatch() { }

        /// <summary>Null when the source calls no move API (nothing is read then).</summary>
        public static PythonGroupMoveWatch Start(Document doc, string code)
        {
            if (doc == null || !PythonGroupMoveScan.CallsMove(code)) return null;
            var w = new PythonGroupMoveWatch();
            try
            {
                foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    if (e.GroupId == ElementId.InvalidElementId || e is Group) continue;
                    double[] pts = Points(e);
                    if (pts == null) continue;
                    if (w._before.Count >= Cap) { w._truncated = true; break; }
                    long id = Rid.Value(e.Id);
                    w._before[id] = pts; w._groupOf[id] = Rid.Value(e.GroupId);
                }
            }
            catch (Exception ex) { w._error = ex.Message; }
            return w;
        }

        /// <summary>Read the same members again, while a rehearsal's changes are still in the model.</summary>
        public void Capture(Document doc)
        {
            _after = new Dictionary<long, double[]>();
            try
            {
                if (doc == null || !doc.IsValidObject) return;
                foreach (long id in _before.Keys)
                {
                    Element e = doc.GetElement(Rid.Make(id));
                    double[] pts = e == null ? null : Points(e);
                    if (pts != null) _after[id] = pts;
                }
            }
            catch (Exception ex) { _error = ex.Message; }
        }

        public JObject Report(string code, JToken output)
        {
            var referenced = new HashSet<long>(PythonGroupMoveScan.LiteralIds(code));
            if (output is JObject o)
                foreach (string field in new[] { "modified_ids", "moved_ids" })
                    foreach (JToken t in (o[field] as JArray) ?? new JArray())
                        if (long.TryParse(t.ToString(), out long v)) referenced.Add(v);
            var members = referenced.Where(_before.ContainsKey).ToList();
            List<long> unmoved = _after == null ? new List<long>() : PythonGroupMoveScan.Unmoved(members, _before, _after, 1e-6);
            var r = new JObject
            {
                ["checked"] = true,
                ["group_members_read"] = _before.Count,
                ["truncated"] = _truncated,
                ["referenced_group_members"] = new JArray(members.OrderBy(x => x).Take(200)),
                ["unmoved"] = new JArray(unmoved.Take(200).Select(id => new JObject { ["element_id"] = id, ["group_id"] = _groupOf[id] }))
            };
            if (_error != null) r["error"] = _error;
            if (unmoved.Count > 0)
            {
                r["code"] = GroupMemberGuard.Code;
                r["warning"] = unmoved.Count + " group member(s) the script referenced did not move. Revit does not move a group " +
                    "member on its own (MoveElement returns without error). Use horizun_manage_groups operation=edit_type_members, or ungroup first.";
            }
            return r;
        }

        private static double[] Points(Element e)
        {
            try
            {
                if (e.Location is LocationPoint lp && lp.Point != null) return new[] { lp.Point.X, lp.Point.Y, lp.Point.Z };
                if (e.Location is LocationCurve lc && lc.Curve != null)
                {
                    XYZ a = lc.Curve.GetEndPoint(0), b = lc.Curve.GetEndPoint(1);
                    return new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z };
                }
            }
            catch { }
            return null;
        }
    }
}
