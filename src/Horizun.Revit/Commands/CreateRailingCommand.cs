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
            InfillPlan infill = InfillPlan.From(doc, r["infill"] as JObject, scale);
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
            if (infill != null) edit.Summary["infill"] = infill.Describe(scale);
            edit.Summary["route"] = hostId > 0 ? "host" : "path";
            if (hostId > 0) edit.Summary["host_id"] = hostId; else edit.Summary["segments"] = pts.Count - 1;
            edit.Apply = d =>
            {
                created.Clear();
                if (infill != null) typeId = infill.CreateType(d, Rid.Make(Rid.Value(type.Id)));
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
                if (infill != null) req.Add("infill");
                var check = new PostconditionCheck(req.ToArray());
                if (infill != null)
                {
                    string why = infill.Reread(d, typeId, out JObject read);
                    if (why != null) read["mismatch"] = why;
                    edit.Evidence["infill"] = read;
                    check.Record("infill", infill.Describe(scale), read, why == null);
                }
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

        /// <summary>The new railing type's infill: one baluster/panel family every spacing, optional posts.</summary>
        private sealed class InfillPlan
        {
            public string Name; public long BalusterTypeId; public long? PostTypeId; public double SpacingFeet;

            public static InfillPlan From(Document doc, JObject infill, double scale)
            {
                if (infill == null) return null;
                var plan = new InfillPlan
                {
                    Name = infill.Value<string>("new_type_name").Trim(),
                    BalusterTypeId = infill.Value<long>("baluster_type_id"),
                    PostTypeId = infill["post_type_id"] == null ? (long?)null : infill.Value<long>("post_type_id"),
                    SpacingFeet = infill.Value<double>("spacing") * scale
                };
                CheckBaluster(doc, plan.BalusterTypeId, "infill.baluster_type_id");
                if (plan.PostTypeId.HasValue) CheckBaluster(doc, plan.PostTypeId.Value, "infill.post_type_id");
                bool taken = new FilteredElementCollector(doc).OfClass(typeof(RailingType)).Cast<RailingType>()
                    .Any(t => string.Equals(t.Name, plan.Name, StringComparison.Ordinal));
                if (taken) throw new ArgumentException("infill.new_type_name '" + plan.Name + "' is already a railing type: choose a new name. Nothing was written.");
                return plan;
            }

            private static void CheckBaluster(Document doc, long id, string field)
            {
                var symbol = doc.GetElement(Rid.Make(id)) as FamilySymbol;
                long cat = symbol?.Category == null ? 0 : Rid.Value(symbol.Category.Id);
                if (symbol == null || cat != (long)BuiltInCategory.OST_StairsRailingBaluster)
                    throw new ArgumentException(field + " " + id + " is not a baluster or baluster-panel family type (category Balusters). Nothing was written.");
            }

            public JObject Describe(double scale) => new JObject
            {
                ["new_type_name"] = Name, ["baluster_type_id"] = BalusterTypeId,
                ["post_type_id"] = PostTypeId.HasValue ? (JToken)PostTypeId.Value : null,
                ["spacing"] = Math.Round(SpacingFeet / scale, 4)
            };

            public long CreateType(Document d, ElementId sourceTypeId)
            {
                var source = (RailingType)d.GetElement(sourceTypeId);
                var made = source.Duplicate(Name) as RailingType;
                if (made == null) throw new InvalidOperationException("Revit did not duplicate railing type " + Rid.Value(sourceTypeId) + ".");
                BalusterPattern pattern = made.BalusterPlacement?.BalusterPattern;
                if (pattern == null || pattern.GetBalusterCount() == 0)
                    throw new InvalidOperationException("the duplicated type has no baluster pattern entry to fill.");
                while (pattern.GetBalusterCount() > 1) pattern.RemoveBaluster(pattern.GetBalusterCount() - 1);
                BalusterInfo b = pattern.GetBaluster(0);
                b.BalusterFamilyId = Rid.Make(BalusterTypeId);
                b.DistanceFromPreviousOrSpace = SpacingFeet;
                if (PostTypeId.HasValue)
                {
                    PostPattern posts = made.BalusterPlacement.PostPattern;
                    posts.StartPost.BalusterFamilyId = Rid.Make(PostTypeId.Value);
                    posts.EndPost.BalusterFamilyId = Rid.Make(PostTypeId.Value);
                    posts.CornerPost.BalusterFamilyId = Rid.Make(PostTypeId.Value);
                }
                return Rid.Value(made.Id);
            }

            /// <summary>null when the type reads back as planned; else what differs.</summary>
            public string Reread(Document d, long typeId, out JObject read)
            {
                read = new JObject { ["type_id"] = typeId };
                var t = d.GetElement(Rid.Make(typeId)) as RailingType;
                if (t == null) return "the new railing type is not in the model";
                read["type_name"] = t.Name;
                BalusterPattern pattern = t.BalusterPlacement?.BalusterPattern;
                if (pattern == null || pattern.GetBalusterCount() != 1) return "the pattern does not hold exactly one baluster";
                BalusterInfo b = pattern.GetBaluster(0);
                read["baluster_type_id"] = Rid.Value(b.BalusterFamilyId);
                read["spacing_feet"] = b.DistanceFromPreviousOrSpace;
                if (!string.Equals(t.Name, Name, StringComparison.Ordinal)) return "type name differs";
                if (Rid.Value(b.BalusterFamilyId) != BalusterTypeId) return "baluster family differs";
                if (Math.Abs(b.DistanceFromPreviousOrSpace - SpacingFeet) > 1e-6) return "spacing differs";
                if (PostTypeId.HasValue)
                {
                    PostPattern posts = t.BalusterPlacement.PostPattern;
                    read["post_type_ids"] = new JArray(Rid.Value(posts.StartPost.BalusterFamilyId), Rid.Value(posts.EndPost.BalusterFamilyId), Rid.Value(posts.CornerPost.BalusterFamilyId));
                    if (Rid.Value(posts.StartPost.BalusterFamilyId) != PostTypeId.Value || Rid.Value(posts.EndPost.BalusterFamilyId) != PostTypeId.Value ||
                        Rid.Value(posts.CornerPost.BalusterFamilyId) != PostTypeId.Value) return "post family differs";
                }
                return null;
            }
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
