// -----------------------------------------------------------------------------
// Horizun Revit MCP - railings: on a stair or ramp, or along a sketched path.
//
// Two routes, both Railing.Create (2023-2027 unchanged):
//   host_id  -> Railing.Create(doc, stairsOrRamp, type, placement): Revit places one
//               railing per side it decides, so the reply lists every id created and
//               each one's host is re-read.
//   path     -> Railing.Create(doc, CurveLoop, type, level): an OPEN polyline is a
//               valid railing path. GetPath() is re-read and compared with the sketch
//               segment by segment in plan (x,y); its z is reported, not judged.
// The railing's height belongs to its TYPE; the instance takes base_offset.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class CreateRailingCommand : ICommand
    {
        public string Name => "horizun_create_railing";
        public string Description => "Create a railing on a stair/ramp or along a sketched path and re-read its type, host, path and base offset.";

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: create railing", ArchitecturalEditRules.ValidateRailing, Plan);

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            RailingType type = ModelEditRunner.Need<RailingType>(doc, r, "type_id");
            long typeId = Rid.Value(type.Id);
            double? baseOffset = r["base_offset"] == null ? (double?)null : r.Value<double>("base_offset") * scale;
            var edit = new ArchModelEdit();
            var created = new List<long>();
            long hostId = -1; List<XYZ> pts = null; long levelId = -1;
            if (r["host_id"] != null)
            {
                Element host = ModelEditRunner.Need<Element>(doc, r, "host_id");
                if (!Railing.IsValidHostForNewRailing(doc, host.Id))
                    throw new ArgumentException("host_id " + Rid.Value(host.Id) + " (" + host.GetType().Name + ") cannot host a new railing: Revit accepts a stair or ramp without one on that side.");
                hostId = Rid.Value(host.Id);
                edit.Planned.Add(ModelEditRunner.Planned(host, PlannedAction.Modify, r));
            }
            else
            {
                Level level = ModelEditRunner.Need<Level>(doc, r, "level_id");
                levelId = Rid.Value(level.Id);
                pts = ((JArray)r["path"]).Select((t, i) => ModelEditRunner.Point(t, scale, "path[" + i + "]")).ToList();
                for (int i = 1; i < pts.Count; i++)
                    if (pts[i].DistanceTo(pts[i - 1]) < 1e-6) throw new ArgumentException("path points " + (i - 1) + " and " + i + " coincide.");
                if (!Railing.IsValidPathForRailing(Loop(pts)))
                    throw new ArgumentException("Revit refuses this path for a railing (Railing.IsValidPathForRailing is false): it must be one continuous, non-self-intersecting chain in a horizontal plane.");
            }
            var placement = (r.Value<string>("placement") ?? "treads").ToLowerInvariant() == "stringer" ? RailingPlacementPosition.Stringer : RailingPlacementPosition.Treads;
            edit.Summary["type_id"] = typeId;
            edit.Summary["route"] = hostId > 0 ? "host" : "path";
            if (hostId > 0) edit.Summary["host_id"] = hostId; else edit.Summary["segments"] = pts.Count - 1;
            edit.Apply = d =>
            {
                created.Clear();
                if (hostId > 0)
                    foreach (ElementId id in Railing.Create(d, Rid.Make(hostId), Rid.Make(typeId), placement) ?? new List<ElementId>()) created.Add(Rid.Value(id));
                else
                {
                    Railing made = Railing.Create(d, Loop(pts), Rid.Make(typeId), Rid.Make(levelId));
                    if (made != null) created.Add(Rid.Value(made.Id));
                }
                if (created.Count == 0) throw new InvalidOperationException("Revit created no railing and gave no reason.");
                if (baseOffset.HasValue)
                    foreach (long id in created)
                    {
                        Parameter p = d.GetElement(Rid.Make(id)).get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT_OFFSET);
                        if (p == null || p.IsReadOnly) throw new InvalidOperationException("railing " + id + " has no writable base offset (STAIRS_RAILING_HEIGHT_OFFSET).");
                        p.Set(baseOffset.Value);
                    }
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "created", "type_id" };
                if (hostId > 0) req.Add("host_id"); else req.Add("path");
                if (baseOffset.HasValue) req.Add("base_offset");
                var check = new PostconditionCheck(req.ToArray());
                var rails = created.Select(id => d.GetElement(Rid.Make(id)) as Railing).ToList();
                check.Compare("created", created.Count, rails.Count(x => x != null));
                check.Compare("type_id", created.Count, rails.Count(x => x != null && Rid.Value(x.GetTypeId()) == typeId));
                var rows = new JArray();
                foreach (Railing x in rails.Where(x => x != null))
                {
                    var row = new JObject { ["element_id"] = Rid.Value(x.Id), ["host_id"] = x.HasHost ? (JToken)Rid.Value(x.HostId) : JValue.CreateNull() };
                    try { row["path"] = new JArray(x.GetPath().Select(c => new JArray(ModelEditRunner.Arr(c.GetEndPoint(0), scale), ModelEditRunner.Arr(c.GetEndPoint(1), scale)))); } catch { }
                    try { row["type_height"] = Math.Round((((RailingType)d.GetElement(x.GetTypeId())).get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT)?.AsDouble() ?? double.NaN) / scale, 4); } catch { }
                    rows.Add(row);
                }
                edit.Evidence["created_ids"] = new JArray(created);
                edit.Evidence["railings"] = rows;
                if (hostId > 0)
                    check.Compare("host_id", created.Count, rails.Count(x => x != null && x.HasHost && Rid.Value(x.HostId) == hostId));
                else
                {
                    Railing x = rails.FirstOrDefault();
                    IList<Curve> path = null; string why = null;
                    try { path = x?.GetPath(); } catch (Exception ex) { why = ex.Message; }
                    if (path == null) check.Unreadable("path", pts.Count - 1, why ?? "the railing has no readable path");
                    else check.Record("path", pts.Count - 1, path.Count, PathMatches(path, pts));
                }
                if (baseOffset.HasValue)
                {
                    var values = rails.Where(x => x != null).Select(x => x.get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT_OFFSET)?.AsDouble()).ToList();
                    if (values.Any(v => !v.HasValue)) check.Unreadable("base_offset", baseOffset.Value * 304.8, "base offset unreadable on a created railing");
                    else check.Measure("base_offset", baseOffset.Value * 304.8, values.Select(v => v.Value * 304.8).OrderBy(v => Math.Abs(v - baseOffset.Value * 304.8)).Last(), 0.5, "mm", "worst STAIRS_RAILING_HEIGHT_OFFSET re-read");
                }
                return check;
            };
            return edit;
        }

        private static CurveLoop Loop(List<XYZ> pts)
        {
            var loop = new CurveLoop();
            for (int i = 1; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i - 1], pts[i]));
            return loop;
        }

        /// <summary>Same number of segments, each segment's ends where the sketch put them in plan (either direction).</summary>
        private static bool PathMatches(IList<Curve> path, List<XYZ> pts)
        {
            if (path.Count != pts.Count - 1) return false;
            double tol = ArchitecturalEditRules.PositionToleranceFeet;
            bool Near(XYZ a, XYZ b) => Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol;
            bool forward = true, backward = true;
            for (int i = 0; i < path.Count; i++)
            {
                XYZ s = path[i].GetEndPoint(0), e = path[i].GetEndPoint(1);
                forward &= Near(s, pts[i]) && Near(e, pts[i + 1]);
                int j = path.Count - 1 - i;
                backward &= Near(s, pts[j + 1]) && Near(e, pts[j]);
            }
            return forward || backward;
        }
    }
}
