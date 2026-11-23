// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of curtain grids, railings, slab shape and arrays.
//
// The commands read and write the model; what is decided here is which fields an
// operation accepts, where each member of an array must land, and how a slab-shape
// vertex elevation is read back. Kept apart so the arithmetic that decides a pass
// or a failure is tested without Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ArchitecturalEditRules
    {
        public static JObject FlatSlabContour(IEnumerable<IEnumerable<double[]>> loops, double scale)
        {
            var boundaries = new JArray();
            var vertices = new JArray();
            bool complete = true;
            foreach (IEnumerable<double[]> loop in loops)
            {
                var points = new JArray();
                foreach (double[] xyz in loop)
                {
                    var point = new JArray(xyz.Select(value => Math.Round(value / scale, 4)));
                    point.Add("Boundary");
                    points.Add(point);
                }
                if (points.Count < 3) { complete = false; continue; }
                boundaries.Add(points);
                foreach (JToken point in points) vertices.Add(point.DeepClone());
            }
            return new JObject
            {
                ["enabled"] = false,
                ["vertex_source"] = "top_face_boundary",
                ["z_note"] = "boundary z is the elevation of the top face in model coordinates",
                ["vertices"] = vertices,
                ["boundary_loops"] = boundaries,
                ["creases"] = new JArray(),
                ["boundary_read_complete"] = complete && boundaries.Count > 0
            };
        }

        public enum SlabEndpoint { Outside, Interior, Boundary }

        public static TEditor RefreshSlabEditor<TEditor>(Action regenerate, Func<TEditor> read, Func<TEditor, bool> enabled,
            Action<TEditor> enable, out bool wasEnabled)
        {
            regenerate();
            TEditor editor = read();
            wasEnabled = enabled(editor);
            if (wasEnabled) return editor;
            enable(editor);
            regenerate();
            return read();
        }

        // Each edit can invalidate wrappers over the slab's native vertices.
        public static void InsertSlabSplit<TPoint, TEditor, TVertex>(TPoint start, TPoint end,
            Func<TEditor> refresh, Func<TEditor, TPoint, TVertex> findVertex,
            Func<TPoint, SlabEndpoint> locate, Action<TEditor, TPoint> addVertex,
            Action<TEditor, TVertex, TVertex, bool> insert, Action<TEditor> splitAlongBoundaryChord,
            bool canAddBoundaryVertex = false, Action<TEditor> beforeTrace = null, Action<TEditor> afterTrace = null)
            where TVertex : class
        {
            TEditor editor = refresh();
            bool missingStart = findVertex(editor, start) == null, missingEnd = findVertex(editor, end) == null;
            SlabEndpoint startLocation = locate(start), endLocation = locate(end);
            if ((missingStart && startLocation == SlabEndpoint.Outside) || (missingEnd && endLocation == SlabEndpoint.Outside))
                throw new ArgumentException("A split endpoint is outside the readable slab top face.");
            bool needsBoundaryVertex = (missingStart && startLocation == SlabEndpoint.Boundary) ||
                (missingEnd && endLocation == SlabEndpoint.Boundary);
            bool edgeToEdge = startLocation == SlabEndpoint.Boundary && endLocation == SlabEndpoint.Boundary;
            if (needsBoundaryVertex && edgeToEdge && !canAddBoundaryVertex)
            {
                beforeTrace?.Invoke(editor);
                splitAlongBoundaryChord(editor);
                if (afterTrace != null) afterTrace(refresh());
                return;
            }
            if (needsBoundaryVertex && !canAddBoundaryVertex)
                throw new ArgumentException("Revit cannot insert a point on the slab boundary with AddPoint. Use an existing edge vertex or a flat floor split from edge to edge.");
            foreach (TPoint endPoint in new[] { start, end })
            {
                if (findVertex(editor, endPoint) != null) continue;
                addVertex(editor, endPoint);
                editor = refresh();
                if (findVertex(editor, endPoint) == null)
                    throw new ArgumentException("Revit did not create a split endpoint. Use an existing boundary vertex or a point inside the slab.");
            }
            TVertex a = findVertex(editor, start), b = findVertex(editor, end);
            if (a == null || b == null) throw new InvalidOperationException("A split endpoint disappeared after regeneration.");
            beforeTrace?.Invoke(editor);
            insert(editor, a, b, needsBoundaryVertex);
            if (afterTrace != null) afterTrace(refresh());
        }

        internal static double NewCommittedSplitCoverage(double[][] before, double[][] after,
            double[][] committed, double start, double end)
        {
            var retained = from traced in after
                           from read in committed
                           let lo = Math.Max(Math.Min(traced[0], traced[1]), Math.Min(read[0], read[1]))
                           let hi = Math.Min(Math.Max(traced[0], traced[1]), Math.Max(read[0], read[1]))
                           where hi > lo
                           select new[] { lo, hi };
            return CoveredLength(before.Concat(retained), start, end) - CoveredLength(before, start, end);
        }

        public static bool FlatSlabAt(IEnumerable<double> faceElevations, double target, double tolerance)
        {
            double[] measured = faceElevations.ToArray();
            return measured.Length > 0 && measured.All(z => Math.Abs(z - target) <= tolerance);
        }

        public static double? FlatFloorSupportElevation(double[] tops, double[] bottoms, double tolerance) =>
            tops.Length > 0 && bottoms.Length > 0 && tops[0] - bottoms[0] > tolerance &&
            FlatSlabAt(tops, tops[0], tolerance) && FlatSlabAt(bottoms, bottoms[0], tolerance) ? bottoms[0] : (double?)null;
        /// <summary>0.5 mm in feet: "the model came back where we put it".</summary>
        public const double PositionToleranceFeet = 0.5 / 304.8;

        private static readonly string[] Common =
            { "target_document", "units", "operation", "dry_run", "confirmation_token", "transaction_name", "idempotency_key" };

        public static readonly Dictionary<string, string[]> CurtainFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["read"] = new[] { "element_id", "grid_index" },
            ["add_grid_line"] = new[] { "element_id", "grid_index", "direction", "offset", "point" },
            ["remove_grid_line"] = new[] { "element_id", "grid_index", "grid_line_id" },
            ["set_mullions"] = new[] { "element_id", "grid_index", "grid_line_id", "mode", "mullion_type_id", "segment_index" },
            ["set_panel_type"] = new[] { "element_id", "grid_index", "panel_ids", "point", "type_id" }
        };

        public static readonly Dictionary<string, string[]> SlabShapeFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["read"] = new[] { "element_id" },
            ["add_point"] = new[] { "element_id", "points" },
            ["add_split_line"] = new[] { "element_id", "start", "end" },
            ["modify_subelement"] = new[] { "element_id", "points", "start", "end", "offset" },
            ["reset_shape"] = new[] { "element_id" }
        };

        private static string Fields(JObject request, Dictionary<string, string[]> table, string tool)
        {
            if (request == null) return "parameters must be a JSON object.";
            string op = (request.Value<string>("operation") ?? "").ToLowerInvariant();
            string[] own;
            if (!table.TryGetValue(op, out own))
                return "operation must be one of " + string.Join(", ", table.Keys) + " for " + tool + ".";
            var allowed = new HashSet<string>(Common.Concat(own), StringComparer.Ordinal);
            foreach (JProperty p in request.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + op + "'. Nothing ran.";
            if (request["element_id"] == null) return "element_id is required.";
            return null;
        }

        /// <summary>Null when the curtain request is well formed for its operation; otherwise why not.</summary>
        public static string ValidateCurtain(JObject request)
        {
            string e = Fields(request, CurtainFields, "horizun_manage_curtain");
            if (e != null) return e;
            string op = request.Value<string>("operation").ToLowerInvariant();
            if (op == "add_grid_line")
            {
                string d = (request.Value<string>("direction") ?? "").ToLowerInvariant();
                if (d != "u" && d != "v") return "direction must be u (horizontal line) or v (vertical line).";
                if ((request["offset"] == null) == (request["point"] == null))
                    return "add_grid_line takes exactly one of offset (walls: v = along the wall from its start, u = height above its base) or point.";
            }
            if ((op == "remove_grid_line" || op == "set_mullions") && request["grid_line_id"] == null)
                return "grid_line_id is required for " + op + ".";
            if (op == "set_mullions")
            {
                string m = (request.Value<string>("mode") ?? "").ToLowerInvariant();
                if (m != "add" && m != "remove") return "mode must be add or remove.";
                if (m == "add" && request["mullion_type_id"] == null) return "mode=add requires mullion_type_id.";
                if (m == "remove" && request["mullion_type_id"] != null) return "mode=remove takes no mullion_type_id.";
            }
            if (op == "set_panel_type")
            {
                if (request["type_id"] == null) return "type_id is required for set_panel_type.";
                if ((request["panel_ids"] == null) == (request["point"] == null))
                    return "set_panel_type takes exactly one of panel_ids or point (the panel whose box contains it).";
            }
            return null;
        }

        /// <summary>Null when the slab-shape request is well formed for its operation.</summary>
        public static string ValidateSlabShape(JObject request)
        {
            string e = Fields(request, SlabShapeFields, "horizun_slab_shape");
            if (e != null) return e;
            string op = request.Value<string>("operation").ToLowerInvariant();
            if (op == "add_point" && !(request["points"] is JArray pts && pts.Count > 0))
                return "add_point requires points: [[x, y, offset], ...].";
            if (op == "add_split_line" && (request["start"] == null || request["end"] == null))
                return "add_split_line requires start and end ([x, y]).";
            if (op == "modify_subelement")
            {
                bool vertices = request["points"] != null;
                bool crease = request["start"] != null || request["end"] != null || request["offset"] != null;
                if (vertices == crease)
                    return "modify_subelement takes points [[x, y, offset]] for vertices OR start+end+offset for one crease.";
                if (crease && (request["start"] == null || request["end"] == null || request["offset"] == null))
                    return "a crease is named by start and end ([x, y]) and takes an offset.";
            }
            return null;
        }

        /// <summary>Null when the railing request names exactly one placement route.</summary>
        /// <summary>
        /// infill: the railing gets a NEW type, duplicated from type_id and named new_type_name, whose
        /// baluster pattern is one baluster or panel family (baluster_type_id) every spacing; posts
        /// optional. The source type is never edited, so its other railings keep their look.
        /// </summary>
        public static string ValidateRailingInfill(JToken token)
        {
            if (token == null) return null;
            if (!(token is JObject infill)) return "infill must be an object { new_type_name, baluster_type_id, spacing, post_type_id? }.";
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "new_type_name", "baluster_type_id", "spacing", "post_type_id" };
            foreach (JProperty p in infill.Properties())
                if (!allowed.Contains(p.Name)) return "infill." + p.Name + " is not a field of infill (new_type_name, baluster_type_id, spacing, post_type_id). Nothing ran.";
            if (string.IsNullOrWhiteSpace(infill.Value<string>("new_type_name")))
                return "infill.new_type_name is required: the infill goes on a NEW railing type, never on the one other railings use.";
            if (infill["baluster_type_id"] == null || infill["baluster_type_id"].Type != JTokenType.Integer)
                return "infill.baluster_type_id (a baluster or baluster-panel family type) is required.";
            if (infill["spacing"] == null || (infill["spacing"].Type != JTokenType.Integer && infill["spacing"].Type != JTokenType.Float))
                return "infill.spacing (centre to centre, in units) is required.";
            if (infill.Value<double>("spacing") <= 0) return "infill.spacing must be greater than zero.";
            if (infill["post_type_id"] != null && infill["post_type_id"].Type != JTokenType.Integer)
                return "infill.post_type_id must be an integer type id.";
            return null;
        }

        public static string ValidateRailing(JObject request)
        {
            if (request == null) return "parameters must be a JSON object.";
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "target_document", "units", "dry_run", "confirmation_token", "transaction_name", "idempotency_key",
                "type_id", "host_id", "placement", "path", "level_id", "base_offset", "infill"
            };
            foreach (JProperty p in request.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to horizun_create_railing. Nothing ran.";
            if (request["type_id"] == null) return "type_id (a railing type) is required.";
            string infillProblem = ValidateRailingInfill(request["infill"]);
            if (infillProblem != null) return infillProblem;
            bool host = request["host_id"] != null, path = request["path"] != null;
            if (host == path) return "give host_id (a stair or ramp) OR path (a sketched polyline), not both and not neither.";
            if (host && request["level_id"] != null) return "level_id applies to a sketched path; a hosted railing takes its host's level.";
            if (path && request["placement"] != null) return "placement applies to a hosted railing only.";
            if (path)
            {
                if (request["level_id"] == null) return "a sketched path requires level_id.";
                if (!(request["path"] is JArray a) || a.Count < 2) return "path needs at least two points.";
            }
            if (request["placement"] != null)
            {
                string p = (request.Value<string>("placement") ?? "").ToLowerInvariant();
                if (p != "treads" && p != "stringer") return "placement must be treads or stringer.";
            }
            return null;
        }

        /// <summary>
        /// Fields that steer the APPROVAL, not the edit. The rehearsal carries dry_run=true
        /// and no token; the apply carries dry_run=false and the token the rehearsal issued.
        /// MEASURED on Revit 2026 (2026-09-24): the resolved plan hashed the raw request, so
        /// these two fields alone made every apply of add_grid_line, add_point and
        /// reset_shape refuse as "THE MODEL MOVED AFTER THE DRY RUN" with nobody touching
        /// the model. They are the only fields that MUST differ between the two calls.
        /// </summary>
        private static readonly HashSet<string> ApprovalOnlyFields = new HashSet<string>(StringComparer.Ordinal)
            { "dry_run", "confirmation_token", "idempotency_key", "transaction_name" };

        /// <summary>
        /// The request as the plan's proposed values: every field that shapes the edit,
        /// canonically (keys sorted, nested values compact), without the approval-only
        /// fields. A rehearsal and its apply render identically; a different offset,
        /// point, type or target still does not.
        /// </summary>
        public static string ProposedRequest(JObject request)
        {
            var canonical = new JObject();
            if (request == null) return canonical.ToString(Newtonsoft.Json.Formatting.None);
            var keys = request.Properties().Select(p => p.Name).Where(n => !ApprovalOnlyFields.Contains(n)).ToList();
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys) canonical[k] = request[k]?.DeepClone();
            return canonical.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Whether a type can take the place of a curtain panel, decided from what the type
        /// IS rather than from Element.IsValidType on the panel. MEASURED on Revit 2026
        /// (2026-09-24): IsValidType refused a type the Type Selector accepts for the same
        /// panel, so the refusal said "a wall type is valid" while rejecting one. Revit's rule:
        /// a curtain panel is replaced by a curtain panel type (a system PanelType or a
        /// family of the Curtain Panels category), by a door or window whose family is a
        /// curtain-panel family, or by a BASIC wall type. A curtain or stacked wall type can
        /// never be a panel. Null when acceptable; otherwise the reason, naming what it is.
        /// </summary>
        /// <param name="typeClass">The API class: WallType, PanelType, FamilySymbol or anything else.</param>
        /// <param name="wallKind">WallType.Kind as text (Basic, Curtain, Stacked); ignored otherwise.</param>
        /// <param name="categoryKey">The type's BuiltInCategory name (OST_CurtainWallPanels, OST_Doors...).</param>
        /// <param name="curtainPanelFamily">Family.IsCurtainPanelFamily for a family symbol.</param>
        public static string CurtainPanelTypeRefusal(string typeClass, string wallKind, string categoryKey, bool curtainPanelFamily)
        {
            switch (typeClass ?? "")
            {
                case "WallType":
                    if (string.Equals(wallKind, "Basic", StringComparison.OrdinalIgnoreCase)) return null;
                    return "it is a " + (string.IsNullOrEmpty(wallKind) ? "non-basic" : wallKind.ToLowerInvariant()) +
                           " wall type; only a BASIC wall type can replace a curtain panel (a curtain or stacked wall cannot be a panel).";
                case "PanelType":
                    return null;
                case "FamilySymbol":
                    if (categoryKey == "OST_CurtainWallPanels") return null;
                    if (categoryKey == "OST_Doors" || categoryKey == "OST_Windows")
                        return curtainPanelFamily ? null :
                            "it is a " + (categoryKey == "OST_Doors" ? "door" : "window") + " type from a wall-hosted family; only a " +
                            "curtain-wall door or window (a curtain-panel family) can replace a curtain panel.";
                    return "it is a family type of category " + (categoryKey ?? "(none)") + "; a curtain panel takes a curtain panel " +
                           "type, a curtain-wall door or window type, or a basic wall type.";
                default:
                    return "it is a " + (string.IsNullOrEmpty(typeClass) ? "(unknown)" : typeClass) + "; a curtain panel takes a " +
                           "curtain panel type, a curtain-wall door or window type, or a basic wall type.";
            }
        }

        /// <summary>Linear arrays take 2..200 members, radial 3..200 - Revit's own ranges, refused before Revit throws.</summary>
        public static string ValidateArrayCount(bool radial, int count)
        {
            int min = radial ? 3 : 2;
            if (count < min || count > 200)
                return (radial ? "array_radial" : "array_linear") + " count must be " + min + "..200 (members including the original).";
            return null;
        }

        /// <summary>
        /// The displacement between consecutive members. anchor=second: the vector IS the
        /// step; anchor=last: the vector reaches the last member, so it is split count-1 ways.
        /// </summary>
        public static double[] ArrayStepVector(double[] vector, int count, bool anchorLast)
        {
            if (vector == null || vector.Length != 3) throw new ArgumentException("vector must have three components");
            double d = anchorLast ? count - 1 : 1;
            return new[] { vector[0] / d, vector[1] / d, vector[2] / d };
        }

        /// <summary>
        /// The turn between consecutive members, radians. anchor=last with a full turn puts
        /// the last member back on the first, so a full circle is split count ways, not count-1.
        /// </summary>
        public static double ArrayStepAngle(double angle, int count, bool anchorLast)
        {
            if (!anchorLast) return angle;
            bool fullTurn = Math.Abs(Math.Abs(angle) - 2 * Math.PI) < 1e-9;
            return angle / (fullTurn ? count : count - 1);
        }

        /// <summary>
        /// Which reading of SlabShapeVertex.Position.Z the committed vertex agrees with:
        /// "absolute" (the slab top plus the offset) or "offset" (the offset itself). The API
        /// documents neither, so both are measured and the one that held is reported; null
        /// when the vertex is at neither height.
        /// </summary>
        public static string SlabVertexConvention(double z, double top, double offset, double tolerance)
        {
            if (Math.Abs(z - (top + offset)) <= tolerance) return "absolute";
            if (Math.Abs(z - offset) <= tolerance) return "offset";
            return null;
        }

        /// <summary>
        /// Position is an XYZ in model coordinates, not the ModifySubElement offset.
        /// Never infer its convention from agreement with the requested result: even a single
        /// unchanged vertex could then choose the offset reading and pass (backlog #20).
        /// </summary>
        public static string SlabEditConvention(IList<double?> z, double top, IList<double> offsets, double tolerance)
        {
            return "absolute";
        }

        /// <summary>
        /// The height an unmodified slab-shape vertex sits at. A floor's height offset already names
        /// its TOP face; a roof's base offset names its BASE, and its shape vertices sit on the top
        /// face, one thickness higher. Read without the thickness, every add_point on a flat roof
        /// measured +thickness and the verification rolled the edit back.
        /// </summary>
        public static double SlabShapeTop(double levelElevation, double offset, bool isRoof, double? roofThickness) =>
            levelElevation + offset + (isRoof ? (roofThickness ?? 0) : 0);

        /// <summary>A vertex's offset under the edit's convention (absolute when none held).</summary>
        public static double SlabOffsetRead(double z, double top, string convention) =>
            convention == "offset" ? z : z - top;

        /// <summary>How much of [lo, hi] the union of the intervals covers.</summary>
        public static double CoveredLength(IEnumerable<double[]> intervals, double lo, double hi)
        {
            var clipped = (intervals ?? Enumerable.Empty<double[]>())
                .Select(i => new[] { Math.Max(lo, Math.Min(i[0], i[1])), Math.Min(hi, Math.Max(i[0], i[1])) })
                .Where(i => i[1] > i[0]).OrderBy(i => i[0]).ToList();
            double total = 0, end = double.NegativeInfinity;
            foreach (double[] i in clipped)
            {
                double start = Math.Max(i[0], end);
                if (i[1] > start) { total += i[1] - start; end = i[1]; }
            }
            return total;
        }
    }
}
