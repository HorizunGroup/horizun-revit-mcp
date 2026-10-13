// A read-only translation plan. The writer is horizun_detail_2d, whose own
// rehearsal and post-commit reread remain mandatory. Counts here describe
// SOURCE entities, not the number of curve segments a writer happens to create.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CadDetailSource
    {
        public string Id, Layer;
        public List<string> BlockPath = new List<string>();
    }

    public sealed class CadDetailGap
    {
        public string Id, Layer, Reason;
    }

    public sealed class CadDetailPlanResult
    {
        public int SourceEntities, PlannedEntities, Offset, PageEntities;
        public readonly List<JObject> Actions = new List<JObject>();
        public readonly List<CadDetailSource> ActionSources = new List<CadDetailSource>();
        public readonly List<CadDetailGap> Gaps = new List<CadDetailGap>();
        public bool Complete => Offset == 0 && PageEntities == SourceEntities &&
                                PlannedEntities == SourceEntities && Gaps.Count == 0;

        public JObject ToJson()
        {
            var sources = new JArray();
            foreach (var s in ActionSources) sources.Add(new JObject {
                ["id"] = s.Id, ["layer"] = s.Layer,
                ["block_path"] = new JArray(s.BlockPath.Select(x => (JToken)x)) });
            var gaps = new JArray();
            foreach (var g in Gaps) gaps.Add(new JObject { ["id"] = g.Id,
                ["layer"] = g.Layer, ["reason"] = g.Reason });
            return new JObject {
                ["source_entities_read_by_revit"] = SourceEntities,
                ["planned_source_entities_on_page"] = PlannedEntities,
                ["page"] = new JObject { ["offset"] = Offset, ["returned"] = PageEntities,
                    ["total"] = SourceEntities, ["complete"] = Offset + PageEntities >= SourceEntities },
                ["actions_for_horizun_detail_2d"] = new JArray(Actions),
                ["action_sources_in_same_order"] = sources,
                ["gaps_on_page"] = gaps,
                ["source_coverage_complete"] = Complete,
                ["coverage_meaning"] = "Counts are Revit-readable source entities. This plan has not written anything. " +
                    "The source DWG may contain more entities, including text and hatches; do not call the " +
                    "native detail complete until the source-file census, every action commit and a reread agree."
            };
        }
    }

    public static class CadDetailPlanRules
    {
        private static double Dot(CadPoint a, CadPoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static CadPoint Sub(CadPoint a, CadPoint b) => new CadPoint(a.X-b.X, a.Y-b.Y, a.Z-b.Z);
        private static JArray P(CadPoint p, CadPoint origin, CadPoint right, CadPoint up) =>
            new JArray(Math.Round(Dot(Sub(p, origin), right), 4),
                       Math.Round(Dot(Sub(p, origin), up), 4));
        private static bool OnPlane(CadPoint p, CadPoint origin, CadPoint normal) =>
            Math.Abs(Dot(Sub(p, origin), normal)) <= 0.5;

        public static CadDetailPlanResult Build(IEnumerable<CadIrEntity> source, long viewId,
            CadPoint originMm, CadPoint right, CadPoint up, CadPoint normal,
            int offset, int maxEntities, IDictionary<string, long> lineStylesByLayer = null)
        {
            var all = source.ToList();
            var result = new CadDetailPlanResult { SourceEntities = all.Count,
                Offset = Math.Max(0, offset) };
            var page = all.Skip(result.Offset).Take(Math.Max(1, Math.Min(500, maxEntities))).ToList();
            result.PageEntities = page.Count;
            foreach (var e in page)
            {
                JObject action = null;
                string reason = null;
                if (e.Kind == CadEntityKind.Line && e.Points.Count == 2)
                {
                    if (e.Points.All(p => OnPlane(p, originMm, normal)))
                        action = new JObject { ["operation"] = "create_detail_line", ["view_id"] = viewId,
                            ["start"] = P(e.Points[0], originMm, right, up),
                            ["end"] = P(e.Points[1], originMm, right, up) };
                    else reason = "out_of_view_plane";
                }
                else if (e.Kind == CadEntityKind.Arc && e.Arc != null)
                {
                    var a = e.Arc;
                    if (new[] { a.Start, a.Middle, a.End }.All(p => OnPlane(p, originMm, normal)))
                        action = new JObject { ["operation"] = "create_detail_arc", ["view_id"] = viewId,
                            ["start"] = P(a.Start, originMm, right, up),
                            ["point_on_arc"] = P(a.Middle, originMm, right, up),
                            ["end"] = P(a.End, originMm, right, up) };
                    else reason = "out_of_view_plane";
                }
                else if (e.Kind == CadEntityKind.Polyline && e.Points.Count >= 2 && e.Points.Count <= 200)
                {
                    if (e.Points.All(p => OnPlane(p, originMm, normal)))
                        action = new JObject { ["operation"] = "create_detail_polyline", ["view_id"] = viewId,
                            ["closed"] = e.Closed,
                            ["points"] = new JArray(e.Points.Select(p => (JToken)P(p, originMm, right, up))) };
                    else reason = "out_of_view_plane";
                }
                else reason = e.Kind == CadEntityKind.Hatch ? "unconverted_hatch" :
                    e.Kind == CadEntityKind.Text ? "text_requires_source_dwg_and_text_type" :
                    "unsupported_or_invalid_" + e.Kind;
                if (action == null)
                    result.Gaps.Add(new CadDetailGap { Id = e.Id, Layer = e.Layer, Reason = reason });
                else
                {
                    long styleId;
                    if (e.Layer != null && lineStylesByLayer != null &&
                        lineStylesByLayer.TryGetValue(e.Layer, out styleId))
                        action["line_style_id"] = styleId;
                    else
                        result.Gaps.Add(new CadDetailGap { Id = e.Id, Layer = e.Layer,
                            Reason = "line_style_unmapped" });
                    result.PlannedEntities++;
                    result.Actions.Add(action);
                    result.ActionSources.Add(new CadDetailSource { Id = e.Id, Layer = e.Layer,
                        BlockPath = e.BlockPath == null ? new List<string>() : new List<string>(e.BlockPath) });
                }
            }
            return result;
        }
    }
}
