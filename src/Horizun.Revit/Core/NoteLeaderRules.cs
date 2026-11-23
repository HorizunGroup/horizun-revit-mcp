// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of the 4.2 notes-and-leaders front (N2): the request
// validation and the arithmetic of
//
//   move_note              (planimetry_ops)  notes moved, flipped, widened or re-texted
//                          while every leader end stays where it was (< 0.1 mm)
//   note_from_model        (planimetry_ops)  a note whose text is the layer stack or the
//                          material of the element its leader points at, with the id
//                          of that element stored on the note
//   clone_tag              (planimetry_ops)  tags cloned from a model tag, heads set in
//                          ONE regeneration and read back (never at the origin)
//   leader_targets         (query_planimetry) what each leader end points at
//   annotation_staleness   (query_planimetry) notes whose element changed or vanished
//   sheet_conflicts        (query_planimetry) overlaps in SHEET coordinates, real geometry
//   free_spot              (query_planimetry) the nearest free place for a box, computed
//                          from geometry without regenerating anything
//
// Directions may be given in the SHEET frame; a rotated viewport turns them into the
// VIEW frame here (QuarterTurns / SheetToView), so a RCP rotated 90 degrees does not
// send a note the wrong way.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class NoteLeaderRules
    {
        public const double LeaderEndToleranceMm = 0.1;

        public static string NormalizeNoteText(string text)
        {
            if (text == null) return null;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Replace('\u00a0', ' ').Replace('\u2009', ' ').Replace('\u200a', ' ').Replace('\u202f', ' ');
            return string.Join("\n", text.Split('\n').Select(line => line.TrimEnd(' '))).TrimEnd('\n');
        }

        public static bool TextsMatch(string expected, string reread) =>
            string.Equals(NormalizeNoteText(expected), NormalizeNoteText(reread), StringComparison.Ordinal);

        private static string EscapeNoteText(string text)
        {
            if (text == null) return null;
            var result = new StringBuilder();
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (char.IsControl(c) || c == '\u00a0' || c == '\u2009' || c == '\u200a' || c == '\u202f')
                            result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else result.Append(c);
                        break;
                }
            }
            return result.ToString();
        }

        // The offset is zero-based in the normalized UTF-16 strings; a missing read has no offset.
        public static JObject TextDifference(string expected, string reread)
        {
            string a = NormalizeNoteText(expected), b = NormalizeNoteText(reread);
            int? offset = null;
            if (a != null && b != null && !string.Equals(a, b, StringComparison.Ordinal))
            {
                int i = 0;
                while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
                offset = i;
            }
            return new JObject { ["expected_text"] = EscapeNoteText(expected), ["reread_text"] = EscapeNoteText(reread),
                ["first_difference"] = offset, ["difference_index_basis"] = "normalized_utf16_zero_based" };
        }

        public static PlannedElement PlannedCreation(string kind, int index, IDictionary<string, string> inputs)
        {
            return new PlannedElement { UniqueId = "planned:" + kind + ":" + index.ToString(CultureInfo.InvariantCulture),
                Category = kind, Action = PlannedAction.Create, BeforeValues = new Dictionary<string, string>(inputs) };
        }

        public static JObject ExistingNoteOutcome(long elementId, bool current) => new JObject {
            ["element_id"] = elementId, ["status"] = "not_applicable",
            ["reason"] = current ? "A current note already exists." : "A stale note already exists; update it instead of creating a duplicate." };

        public sealed class TagBatchResult { public int Regenerations; }

        public static TagBatchResult RunTagBatch<T>(IEnumerable<T> items, Action<T> apply, Action regenerate)
        {
            foreach (T item in items) apply(item);
            regenerate();
            return new TagBatchResult { Regenerations = 1 };
        }

        public static readonly string[] WriteOperations = { "move_note", "note_from_model", "clone_tag", "arrange_column", "set_leader_end", "reseat_leaders", "required_notes", "replace_text", "sync_sheet_references" };
        public static readonly string[] ReadModes = { "leader_targets", "annotation_staleness", "sheet_conflicts", "free_spot", "view_ray", "section_mark_diagnosis" };

        public static string KeynoteWarning(bool isKeynoteTag, bool readable, string keynote)
        {
            if (!isKeynoteTag) return null;
            if (!readable) return "destination keynote could not be read; tag text is uncertain";
            return string.IsNullOrWhiteSpace(keynote) ? "destination has no keynote: the tag will show empty" : null;
        }
        public static bool NoteReadFieldAllowed(string mode, string field)
        {
            if (!ReadModes.Contains(mode)) return false;
            if (new[] { "target_document", "mode", "units", "max_rows", "gap_mm", "step_mm", "max_radius_mm", "include_linked_symbols" }.Contains(field)) return true;
            string[] own = mode == "leader_targets" ? new[] { "view_ids", "note_ids" }
                : mode == "annotation_staleness" ? new[] { "view_ids", "sheet_ids", "changed_unique_ids", "deleted_unique_ids" }
                : mode == "sheet_conflicts" ? new[] { "sheet_id" } : mode == "view_ray" ? new[] { "view_id", "points_mm" } : mode == "section_mark_diagnosis" ? new[] { "view_id", "section_view_id" } : new[] { "view_id", "size_mm", "near_mm" };
            return own.Contains(field);
        }
        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["move_note"] = new[] { "note_ids", "delta_mm", "frame", "keep_leader_ends", "elbow", "side", "width_mm", "text", "fit_crop_margin_mm" },
            ["note_from_model"] = new[] { "view_id", "notes", "source", "schedule_id", "text_note_type_id", "width_mm" },
            ["clone_tag"] = new[] { "model_tag_id", "targets", "sheet_horizontal" },
            ["arrange_column"] = new[] { "view_id", "note_ids", "side", "width_mm", "gap_mm" },
            ["set_leader_end"] = new[] { "note_ids", "leader_index", "end_mm", "delta_mm", "frame", "elbow" },
            ["reseat_leaders"] = new[] { "view_id", "note_ids", "max_shift_mm" },
            ["required_notes"] = new[] { "view_id", "notes", "source", "schedule_id", "text_note_type_id", "width_mm" },
            ["replace_text"] = new[] { "note_ids", "old", "new" },
            ["sync_sheet_references"] = new[] { "view_id", "pattern", "map" }
        };

        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

        private static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) && Finite(t.Value<double>());

        /// <summary>Validates the write operations of this front. Null when the request is well formed.</summary>
        public static string ValidateWrite(string op, JObject r)
        {
            switch (op)
            {
                case "move_note":
                {
                    if (!(r["note_ids"] is JArray ids) || ids.Count == 0 || ids.Count > 200 || ids.Any(t => t.Type != JTokenType.Integer))
                        return "note_ids must list 1..200 text note ids.";
                    if (r["delta_mm"] != null && !Pair(r["delta_mm"], 5000)) return "delta_mm must be [dx, dy] in paper millimetres, each within +-5000.";
                    string frame = r.Value<string>("frame") ?? "view";
                    if (frame != "view" && frame != "sheet") return "frame must be view or sheet.";
                    if (r["keep_leader_ends"] != null && r["keep_leader_ends"].Type != JTokenType.Boolean) return "keep_leader_ends must be true or false.";
                    string elbow = r.Value<string>("elbow") ?? "keep";
                    if (elbow != "keep" && elbow != "horizontal" && elbow != "L") return "elbow must be keep, horizontal or L.";
                    string side = r.Value<string>("side") ?? "keep";
                    if (side != "keep" && side != "auto" && side != "left" && side != "right") return "side must be keep, auto, left or right.";
                    if (r["width_mm"] != null && (!IsNumber(r["width_mm"]) || r.Value<double>("width_mm") < 5 || r.Value<double>("width_mm") > 2000)) return "width_mm must be between 5 and 2000 paper mm.";
                    if (r["fit_crop_margin_mm"] != null && (!IsNumber(r["fit_crop_margin_mm"]) || r.Value<double>("fit_crop_margin_mm") < 0 || r.Value<double>("fit_crop_margin_mm") > 200)) return "fit_crop_margin_mm must be between 0 and 200 paper mm.";
                    if (r["text"] != null && (r["text"].Type != JTokenType.String || string.IsNullOrEmpty(r.Value<string>("text")) || r.Value<string>("text").Length > 4000)) return "text must be a non-empty string of at most 4000 characters.";
                    bool keep = r.Value<bool?>("keep_leader_ends") ?? true;
                    if (!keep && (elbow != "keep" || side != "keep")) return "elbow and side only make sense with keep_leader_ends=true: without it the leaders travel with the note.";
                    return null;
                }
                case "arrange_column":
                {
                    if (r["view_id"]?.Type != JTokenType.Integer) return "view_id is required.";
                    if (r["note_ids"] != null && (!(r["note_ids"] is JArray ai) || ai.Count == 0 || ai.Count > 200 || ai.Any(t => t.Type != JTokenType.Integer))) return "note_ids must list 1..200 text note ids.";
                    string cs = r.Value<string>("side");
                    if (cs != "left" && cs != "right") return "side must be left or right: the side of the drawing where the column goes.";
                    if (r["width_mm"] != null && (!IsNumber(r["width_mm"]) || r.Value<double>("width_mm") < 5 || r.Value<double>("width_mm") > 2000)) return "width_mm must be between 5 and 2000 paper mm.";
                    return null;
                }
                case "set_leader_end":
                {
                    if (!(r["note_ids"] is JArray one) || one.Count != 1 || one[0].Type != JTokenType.Integer) return "note_ids must hold exactly one text note id.";
                    if (r["leader_index"] != null && (r["leader_index"].Type != JTokenType.Integer || r.Value<int>("leader_index") < 0)) return "leader_index must be an integer >= 0.";
                    if ((r["end_mm"] == null) == (r["delta_mm"] == null)) return "give exactly one of end_mm (a model point) or delta_mm (a shift of the end).";
                    if (r["end_mm"] != null && !Triple(r["end_mm"], 1e6)) return "end_mm must be [x, y, z] in model millimetres.";
                    if (r["delta_mm"] != null && !Pair(r["delta_mm"], 5000)) return "delta_mm must be [dx, dy] in paper millimetres.";
                    string fr = r.Value<string>("frame") ?? "view";
                    if (fr != "view" && fr != "sheet") return "frame must be view or sheet.";
                    string el = r.Value<string>("elbow") ?? "keep";
                    if (el != "keep" && el != "horizontal" && el != "L") return "elbow must be keep, horizontal or L.";
                    return null;
                }
                case "reseat_leaders":
                {
                    bool hasView = r["view_id"]?.Type == JTokenType.Integer, hasNotes = r["note_ids"] is JArray ni && ni.Count > 0 && ni.Count <= 200 && ni.All(t => t.Type == JTokenType.Integer);
                    if (!hasView && !hasNotes) return "reseat_leaders needs view_id or note_ids (1..200).";
                    if (r["max_shift_mm"] != null && (!IsNumber(r["max_shift_mm"]) || r.Value<double>("max_shift_mm") <= 0 || r.Value<double>("max_shift_mm") > 500)) return "max_shift_mm must be > 0 and <= 500 paper mm.";
                    return null;
                }
                case "replace_text":
                {
                    if (!(r["note_ids"] is JArray rn) || rn.Count == 0 || rn.Count > 200 || rn.Any(t => t.Type != JTokenType.Integer)) return "note_ids must list 1..200 text note ids.";
                    if (r["old"]?.Type != JTokenType.String || string.IsNullOrEmpty(r.Value<string>("old"))) return "old (the text to find) is required.";
                    if (r["new"]?.Type != JTokenType.String) return "new (the replacement, may be empty) is required.";
                    return null;
                }
                case "sync_sheet_references":
                {
                    if (r["view_id"]?.Type != JTokenType.Integer) return "view_id is required.";
                    if (r["pattern"] != null && (r["pattern"].Type != JTokenType.String || r.Value<string>("pattern").Length > 300)) return "pattern must be a regular expression string with ONE group for the sheet number.";
                    if (r["map"] != null && (!(r["map"] is JObject mo) || mo.Count == 0 || mo.Count > 200 || mo.Properties().Any(x => x.Value.Type != JTokenType.String))) return "map must be {old sheet number: new sheet number}, 1..200 entries.";
                    return null;
                }
                case "required_notes":
                case "note_from_model":
                {
                    if (r["view_id"]?.Type != JTokenType.Integer) return "view_id is required.";
                    if (!(r["notes"] is JArray notes) || notes.Count == 0 || notes.Count > 200 || notes.Any(t => t.Type != JTokenType.Object)) return "notes must list 1..200 objects.";
                    string source = r.Value<string>("source") ?? "layers";
                    if (source != "layers" && source != "material" && source != "type_name" && source != "schedule") return "source must be layers, material, type_name or schedule.";
                    if (source == "schedule" && op == "required_notes") return "required_notes cannot update schedule notes; use note_from_model.";
                    if (source == "schedule" && r["schedule_id"]?.Type != JTokenType.Integer) return "schedule_id is required for source=schedule.";
                    if (source != "schedule" && r["schedule_id"] != null) return "schedule_id requires source=schedule.";
                    foreach (JObject n in notes.OfType<JObject>())
                    {
                        foreach (JProperty p in n.Properties())
                            if (p.Name != "element_id" && p.Name != "link_instance_id" && p.Name != "end_mm" && p.Name != "note_mm")
                                return p.Name + " is not a field of a notes entry (element_id, link_instance_id, end_mm, note_mm).";
                        if (n["element_id"]?.Type != JTokenType.Integer) return "every notes entry needs element_id (the element in the link when link_instance_id is given).";
                        if (n["link_instance_id"] != null && n["link_instance_id"].Type != JTokenType.Integer) return "link_instance_id must be an integer.";
                        if (!Triple(n["end_mm"], 1e6) || !Triple(n["note_mm"], 1e6)) return "end_mm and note_mm must be [x, y, z] points in model millimetres.";
                    }
                    if (r["width_mm"] != null && (!IsNumber(r["width_mm"]) || r.Value<double>("width_mm") < 5 || r.Value<double>("width_mm") > 2000)) return "width_mm must be between 5 and 2000 paper mm.";
                    if (r["text_note_type_id"] != null && r["text_note_type_id"].Type != JTokenType.Integer) return "text_note_type_id must be an integer.";
                    return null;
                }
                case "clone_tag":
                {
                    if (r["model_tag_id"]?.Type != JTokenType.Integer) return "model_tag_id (an existing tag, already well formed, to clone) is required.";
                    if (!(r["targets"] is JArray t) || t.Count == 0 || t.Count > 200 || t.Any(x => x.Type != JTokenType.Object)) return "targets must list 1..200 objects.";
                    foreach (JObject n in t.OfType<JObject>())
                    {
                        foreach (JProperty p in n.Properties())
                            if (p.Name != "element_id" && p.Name != "head_mm")
                                return p.Name + " is not a field of a targets entry (element_id, head_mm).";
                        if (n["element_id"]?.Type != JTokenType.Integer) return "every targets entry needs element_id.";
                        if (!Triple(n["head_mm"], 1e6)) return "head_mm must be an [x, y, z] point in model millimetres.";
                    }
                    if (r["sheet_horizontal"] != null && r["sheet_horizontal"].Type != JTokenType.Boolean) return "sheet_horizontal must be true or false.";
                    return null;
                }
            }
            return "unknown operation " + op + ".";
        }

        /// <summary>Validates the read modes of this front (the common fields are checked by the caller).</summary>
        public static string ValidateRead(string mode, JObject r)
        {
            switch (mode)
            {
                case "leader_targets":
                    if (!HasScope(r)) return "leader_targets needs view_ids (1..20) or note_ids (1..500).";
                    break;
                case "annotation_staleness":
                    if (!(r["view_ids"] is JArray v) || v.Count == 0 || v.Count > 20 || v.Any(t => t.Type != JTokenType.Integer)) return "annotation_staleness needs view_ids (1..20).";
                    if (r["sheet_ids"] != null && (!(r["sheet_ids"] is JArray sheets) || sheets.Count == 0 || sheets.Count > 2000 || sheets.Any(t => t.Type != JTokenType.Integer))) return "sheet_ids must list 1..2000 sheet ids.";
                    foreach (string k in new[] { "changed_unique_ids", "deleted_unique_ids" })
                        if (r[k] != null && (!(r[k] is JArray a) || a.Count > 20000 || a.Any(t => t.Type != JTokenType.String))) return k + " must be a list of UniqueId strings (max 20000).";
                    break;
                case "sheet_conflicts":
                    if (r["sheet_id"]?.Type != JTokenType.Integer) return "sheet_conflicts needs sheet_id.";
                    break;
                case "view_ray":
                    if (r["view_id"]?.Type != JTokenType.Integer) return "view_ray needs view_id.";
                    if (!(r["points_mm"] is JArray pts) || pts.Count == 0 || pts.Count > 200 || pts.Any(t => !Triple(t, 1e6))) return "view_ray needs points_mm: 1..200 model points [x, y, z] in mm, taken on the view plane.";
                    break;
                case "section_mark_diagnosis":
                    if (r["view_id"]?.Type != JTokenType.Integer) return "section_mark_diagnosis needs view_id: the view where the mark is expected.";
                    if (r["section_view_id"] != null && r["section_view_id"].Type != JTokenType.Integer) return "section_view_id must be the id of one section view.";
                    if (r["section_view_id"] != null && r["view_id"].Value<long>() == r["section_view_id"].Value<long>()) return "section_view_id is the same view as view_id: name the section and the view where its mark is expected.";
                    break;
                case "free_spot":
                    if (r["view_id"]?.Type != JTokenType.Integer) return "free_spot needs view_id.";
                    if (!Pair(r["size_mm"], 2000) || r["size_mm"][0].Value<double>() <= 0 || r["size_mm"][1].Value<double>() <= 0) return "free_spot needs size_mm [width, height] in paper millimetres, both > 0.";
                    if (!Pair(r["near_mm"], 20000)) return "free_spot needs near_mm [x, y]: the point in the SHEET frame (paper millimetres) closest to which the place is wanted.";
                    break;
            }
            foreach (string k in new[] { "gap_mm", "step_mm", "max_radius_mm" })
                if (r[k] != null && (!IsNumber(r[k]) || r.Value<double>(k) < 0 || r.Value<double>(k) > 2000)) return k + " must be between 0 and 2000 paper mm.";
            if (r["include_linked_symbols"] != null && r["include_linked_symbols"].Type != JTokenType.Boolean) return "include_linked_symbols must be true or false.";
            return null;
        }

        private static bool HasScope(JObject r)
        {
            bool views = r["view_ids"] is JArray v && v.Count >= 1 && v.Count <= 20 && v.All(t => t.Type == JTokenType.Integer);
            bool notes = r["note_ids"] is JArray n && n.Count >= 1 && n.Count <= 500 && n.All(t => t.Type == JTokenType.Integer);
            return views || notes;
        }

        private static bool Pair(JToken t, double max) => t is JArray a && a.Count == 2 && a.All(IsNumber) && a.All(x => Math.Abs(x.Value<double>()) <= max);
        private static bool Triple(JToken t, double max) => t is JArray a && a.Count == 3 && a.All(IsNumber) && a.All(x => Math.Abs(x.Value<double>()) <= max);

        // ---- frames -------------------------------------------------------------------------

        /// <summary>Quarter turns (clockwise positive) of a viewport: None 0, Clockwise 1, Counterclockwise 3.</summary>
        public static int QuarterTurns(string rotationName)
        {
            switch (rotationName)
            {
                case "None": return 0;
                case "Clockwise": return 1;
                case "Counterclockwise": return 3;
                default: throw new ArgumentException("Unrecognized viewport rotation '" + rotationName + "'; the sheet direction cannot be converted. Nothing was written.");
            }
        }

        /// <summary>A direction on the sheet expressed as a direction in the view. A viewport rotated clockwise
        /// shows the view's up axis pointing to the sheet's right, so a sheet vector is turned back counterclockwise.</summary>
        public static double[] SheetToView(double sx, double sy, int quarterTurns)
        {
            int q = ((quarterTurns % 4) + 4) % 4;
            switch (q)
            {
                case 1: return new[] { -sy, sx };
                case 2: return new[] { -sx, -sy };
                case 3: return new[] { sy, -sx };
                default: return new[] { sx, sy };
            }
        }

        /// <summary>The inverse: a view direction as it appears on the sheet.</summary>
        public static double[] ViewToSheet(double vx, double vy, int quarterTurns) => SheetToView(vx, vy, -quarterTurns);

        /// <summary>The tag orientation that reads horizontally on the SHEET: a viewport turned a quarter turn
        /// shows a horizontal tag vertically, so it is the other orientation that reads horizontally.</summary>
        public static string TagOrientationForSheet(bool horizontalOnSheet, int quarterTurns)
        {
            bool turned = ((quarterTurns % 2) + 2) % 2 == 1;
            bool viewHorizontal = horizontalOnSheet ^ turned;
            return viewHorizontal ? "horizontal" : "vertical";
        }

        // ---- leaders --------------------------------------------------------------------------

        /// <summary>The elbow of a leader from <paramref name="anchor"/> to a fixed <paramref name="end"/> in the (right, up) frame.
        /// horizontal: the shoulder runs level with the anchor, half way to the end; L: level with the anchor under the end.</summary>
        public static double[] Elbow(string mode, double[] anchor, double[] end)
        {
            if (mode == "L") return new[] { end[0], anchor[1] };
            return new[] { (anchor[0] + end[0]) / 2.0, anchor[1] };
        }

        /// <summary>The side of the note a leader leaves from so that it points away from the text, never across it.</summary>
        public static string ChooseSide(double noteLeft, double noteRight, double endRight)
            => endRight < (noteLeft + noteRight) / 2.0 ? "left" : "right";

        public static bool SamePoint(double[] a, double[] b, double tol)
            => a != null && b != null && a.Length == b.Length && tol > 0 && Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum()) < tol;

        public sealed class LeaderGeometryDecision
        {
            public bool Accept;
            public double MaxDeviationMm;
        }

        public static LeaderGeometryDecision DecideLeaderGeometry(IEnumerable<double> deviationsMm, double toleranceMm)
        {
            var values = deviationsMm.ToArray();
            double max = values.Length == 0 ? 0 : values.Max();
            return new LeaderGeometryDecision { MaxDeviationMm = max,
                Accept = Finite(toleranceMm) && toleranceMm > 0 && values.All(d => Finite(d) && d >= 0 && d < toleranceMm) };
        }

        /// <summary>The smallest shift that brings a note box inside the crop less a margin; zero when it fits or cannot fit.</summary>
        public static double[] ShiftInside(double[] box, double[] crop, double margin)
        {
            double minX = crop[0] + margin, minY = crop[1] + margin, maxX = crop[2] - margin, maxY = crop[3] - margin;
            double w = box[2] - box[0], h = box[3] - box[1];
            double dx = 0, dy = 0;
            if (w <= maxX - minX) dx = box[0] < minX ? minX - box[0] : box[2] > maxX ? maxX - box[2] : 0;
            if (h <= maxY - minY) dy = box[1] < minY ? minY - box[1] : box[3] > maxY ? maxY - box[3] : 0;
            return new[] { dx, dy };
        }

        // ---- text from the model ------------------------------------------------------------

        public sealed class Layer
        {
            public string Material;
            public double ThicknessMm;
            public string Function;
        }

        /// <summary>The note text for the layer stack (outside first), one layer per line.</summary>
        public static string MaterialFromMatchingHit(string sourceUid, string sourceLinkUid, string hitUid, string hitLinkUid, string layerMaterial, string structuralMaterial)
        {
            if (string.IsNullOrEmpty(sourceUid) || sourceUid != hitUid || sourceLinkUid != hitLinkUid) return null;
            return layerMaterial ?? structuralMaterial;
        }
        public static string LayersText(IList<Layer> layers, string typeName)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(typeName)) sb.Append(typeName);
            for (int i = 0; i < layers.Count; i++)
            {
                if (sb.Length > 0) sb.Append('\r');
                sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ")
                  .Append(string.IsNullOrWhiteSpace(layers[i].Material) ? "(sin material)" : layers[i].Material)
                  .Append(" ").Append(layers[i].ThicknessMm.ToString("0.#", CultureInfo.InvariantCulture)).Append(" mm");
            }
            return sb.ToString();
        }

        /// <summary>Index of the layer reached at <paramref name="depthMm"/> from the first (outside) face; -1 when beyond the stack.</summary>
        public static int LayerAtDepth(IList<double> thicknessMm, double depthMm)
        {
            if (depthMm < -1e-6) return -1;
            double cum = 0;
            for (int i = 0; i < thicknessMm.Count; i++)
            {
                cum += thicknessMm[i];
                if (depthMm <= cum + 1e-6) return i;
            }
            return -1;
        }

        /// <summary>A short fingerprint of what a note says and where it came from, stored with it.</summary>
        public static string Signature(string sourceUniqueId, string source, string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((sourceUniqueId ?? "") + "|" + (source ?? "") + "|" + (text ?? "")));
                return string.Concat(h.Take(8).Select(b => b.ToString("x2")));
            }
        }

        // ---- staleness -------------------------------------------------------------------------

        public static string CompareNoteSource(string storedUid, string storedLayer, string storedValue,
            bool? originExists, string currentUid, string currentLayer, string currentValue)
        {
            if (originExists == false) return "origen_borrado";
            if (originExists != true || string.IsNullOrEmpty(storedUid) || storedUid != currentUid ||
                storedLayer == null || currentLayer == null || storedValue == null || currentValue == null) return "sin_medir";
            if (storedLayer != currentLayer) return "cambio_capa";
            return storedValue == currentValue ? "vigente" : "cambio_valor";
        }

        /// <summary>Why a note is out of date. Classify free text separately from notes whose origin or leader could not be measured.</summary>
        public static List<string> StaleReasons(string originUid, bool? originExists, string storedSignature, string currentSignature,
            string hitUid, bool hitSeen, ISet<string> changed, ISet<string> deleted, out string verdict, bool hasLeader = false)
        {
            var reasons = new List<string>();
            if (originUid != null)
            {
                if (originExists == false || (deleted != null && deleted.Contains(originUid))) reasons.Add("origin_deleted");
                else if (changed != null && changed.Contains(originUid)) reasons.Add("origin_changed");
                if (originExists != false && storedSignature != null && currentSignature != null && storedSignature != currentSignature) reasons.Add("text_out_of_date");
            }
            if (hitUid != null)
            {
                if (deleted != null && deleted.Contains(hitUid) && !reasons.Contains("origin_deleted")) reasons.Add("target_deleted");
                else if (changed != null && changed.Contains(hitUid) && !reasons.Contains("origin_changed")) reasons.Add("target_changed");
            }
            if (reasons.Count > 0) verdict = "stale";
            else if (originUid != null && originExists == null) verdict = "unknown_origin_not_measured";
            else if (originUid != null && (storedSignature == null || currentSignature == null)) verdict = "unknown_not_measured";
            else if (hasLeader && hitUid == null) verdict = hitSeen ? "unknown_no_target" : "unknown_not_measured";
            else if (originUid != null || hitUid != null) verdict = "current";
            else if (hitSeen) verdict = "unknown_no_target";
            else { verdict = "untracked"; reasons.Add("nota de texto libre: sin origen ni directriz, no se puede desactualizar"); }
            return reasons;
        }

        // ---- boxes: conflicts and free places ---------------------------------------------------

        public static bool Overlap(double[] a, double[] b, double gap)
            => a[0] < b[2] + gap && b[0] < a[2] + gap && a[1] < b[3] + gap && b[1] < a[3] + gap;

        /// <summary>Does the segment p-q touch the box grown by gap? (Liang-Barsky.) Used for leaders and dimension lines.</summary>
        public static bool SegmentHitsBox(double[] p, double[] q, double[] box, double gap)
        {
            double x0 = box[0] - gap, y0 = box[1] - gap, x1 = box[2] + gap, y1 = box[3] + gap;
            double dx = q[0] - p[0], dy = q[1] - p[1], t0 = 0, t1 = 1;
            double[] pp = { -dx, dx, -dy, dy }, qq = { p[0] - x0, x1 - p[0], p[1] - y0, y1 - p[1] };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(pp[i]) < 1e-12) { if (qq[i] < 0) return false; continue; }
                double t = qq[i] / pp[i];
                if (pp[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
            return true;
        }

        public static List<int[]> Conflicts(IList<double[]> boxes, double gap)
        {
            var pairs = new List<int[]>();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                    if (Overlap(boxes[i], boxes[j], gap)) pairs.Add(new[] { i, j });
            return pairs;
        }

        /// <summary>The centre of the free place nearest to <paramref name="near"/> for a w x h box, searched on a square
        /// ring grid of <paramref name="step"/> out to <paramref name="maxRadius"/>; null when none exists inside
        /// <paramref name="bounds"/> [minX, minY, maxX, maxY]. Ties break by angle so the answer is deterministic.</summary>
        public static double[] FreeSpot(IList<double[]> obstacles, double w, double h, double[] near, double[] bounds, double gap, double step, double maxRadius)
        {
            if (step <= 0) throw new ArgumentException("step must be > 0.");
            Func<double, double, bool> free = (cx, cy) =>
            {
                double[] box = { cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2 };
                if (bounds != null && (box[0] < bounds[0] || box[1] < bounds[1] || box[2] > bounds[2] || box[3] > bounds[3])) return false;
                foreach (double[] o in obstacles) if (Overlap(box, o, gap)) return false;
                return true;
            };
            if (free(near[0], near[1])) return new[] { near[0], near[1] };
            int rings = (int)Math.Ceiling(maxRadius / step);
            double[] best = null; double bestD = double.MaxValue;
            for (int k = 1; k <= rings; k++)
            {
                for (int i = -k; i <= k; i++)
                    for (int j = -k; j <= k; j++)
                    {
                        if (Math.Max(Math.Abs(i), Math.Abs(j)) != k) continue;
                        double x = near[0] + i * step, y = near[1] + j * step;
                        double d = Math.Sqrt((x - near[0]) * (x - near[0]) + (y - near[1]) * (y - near[1]));
                        if (d > maxRadius + 1e-9) continue;
                        if (d < bestD - 1e-9 || (Math.Abs(d - bestD) <= 1e-9 && best != null && (Math.Atan2(j, i) < Math.Atan2(best[1] - near[1], best[0] - near[0]))))
                        {
                            if (free(x, y)) { best = new[] { x, y }; bestD = d; }
                        }
                    }
                // Rings are walked outward; the nearest candidate of this ring beats every later ring only
                // when it is closer than the inner edge of the next ring.
                if (best != null && bestD <= k * step) return best;
            }
            return best;
        }

        // ---- sync_sheet_references: ONE pass, by position (INT-6 M-1; word edges of the MATCH, INT-7 m-f) -----------------

        /// <summary>One replacement of a sheet number inside a note's plain text: where it is, how long it is, what replaces it.</summary>
        public sealed class SheetRefEdit
        {
            public int Start, Length;
            public string Number, NewNumber;
            public int End => Start + Length;
        }

        /// <summary>One reference the pattern found in a note, and what the map does with it (the row the reply shows).</summary>
        public sealed class SheetRefMatch
        {
            public string Reference;      // the whole match
            public string Number;         // capture group 1
            public int Start, Length;     // where the number sits in the plain text
            /// <summary>The new number when this reference WILL be renamed; null when the map has nothing for it or its edges are not a word's.</summary>
            public string RenamedTo;
        }

        /// <summary>
        /// A reference is renamed as a whole word: the MATCH of the pattern must not touch a letter or a digit on either side.
        /// The edges are the match's and not the number's (INT-7 m-f): a pattern that keeps its prefix in the match, like
        /// S(\d+) over "S12" or P(\d{3}) over "P101", has a letter right before its number and is still a reference; what is
        /// refused is a match glued to more text, like A1 inside A10 or S12 inside AS12.
        /// </summary>
        private static bool WholeWord(string plain, Match m)
        {
            if (m.Index > 0 && char.IsLetterOrDigit(plain[m.Index - 1])) return false;
            int after = m.Index + m.Length;
            return !(after < plain.Length && char.IsLetterOrDigit(plain[after]));
        }

        /// <summary>
        /// Every match of <paramref name="rx"/> in <paramref name="plain"/> with its sheet number (capture group 1) and, ONLY when
        /// the map really renames it, the new number. The reply's `renamed_to` is read from here, so it announces what will be
        /// renamed and nothing else (INT-7 m-f).
        /// </summary>
        public static List<SheetRefMatch> ListSheetReferences(string plain, Regex rx, IDictionary<string, string> map)
        {
            var rows = new List<SheetRefMatch>();
            if (string.IsNullOrEmpty(plain) || rx == null) return rows;
            foreach (Match m in rx.Matches(plain))
            {
                Group g = m.Groups[1];
                if (!g.Success || g.Length == 0) continue;
                string to = null;
                if (map != null && map.Count > 0 && map.TryGetValue(g.Value, out string mapped) && mapped != null && mapped != g.Value && WholeWord(plain, m))
                    to = mapped;
                rows.Add(new SheetRefMatch { Reference = m.Value, Number = g.Value, Start = g.Index, Length = g.Length, RenamedTo = to });
            }
            return rows;
        }

        /// <summary>
        /// The sheet number of every match of <paramref name="rx"/> that <paramref name="map"/> renames, BY POSITION in
        /// <paramref name="plain"/>. The map is looked up once per match against the number as it stands in the note, never
        /// against a text another replacement already changed: {A1:A2, A2:A3} turns "A1 y A2" into "A2 y A3", not "A3 y A3".
        /// </summary>
        public static List<SheetRefEdit> PlanSheetReferenceEdits(string plain, Regex rx, IDictionary<string, string> map)
        {
            var edits = new List<SheetRefEdit>();
            foreach (SheetRefMatch r in ListSheetReferences(plain, rx, map))
                if (r.RenamedTo != null)
                    edits.Add(new SheetRefEdit { Start = r.Start, Length = r.Length, Number = r.Number, NewNumber = r.RenamedTo });
            return edits;
        }

        /// <summary>
        /// What the note must read after the edits, computed WITHOUT the edit list: the regex replaces the capture inside
        /// each match with an evaluator of its own, so a wrong edit list cannot vouch for itself in the re-read.
        /// </summary>
        public static string ExpectedAfterSheetReferences(string plain, Regex rx, IDictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(plain) || rx == null || map == null || map.Count == 0) return plain;
            return rx.Replace(plain, m =>
            {
                Group g = m.Groups[1];
                if (!g.Success || g.Length == 0 || !map.TryGetValue(g.Value, out string mapped) || mapped == null) return m.Value;
                if (!WholeWord(plain, m)) return m.Value;
                return m.Value.Substring(0, g.Index - m.Index) + mapped + m.Value.Substring(g.Index - m.Index + g.Length);
            });
        }

        /// <summary>
        /// Apply the edits to a string from the LAST to the first, the order the formatted text is edited in (each edit leaves
        /// the positions before it where they were). The Revit half does the same on the FormattedText; a test holds the
        /// two readings to each other.
        /// </summary>
        public static string ApplySheetReferenceEdits(string plain, IEnumerable<SheetRefEdit> edits)
        {
            string text = plain ?? "";
            foreach (SheetRefEdit e in (edits ?? Enumerable.Empty<SheetRefEdit>()).OrderByDescending(x => x.Start))
                text = text.Substring(0, e.Start) + e.NewNumber + text.Substring(e.End);
            return text;
        }
    }
}
