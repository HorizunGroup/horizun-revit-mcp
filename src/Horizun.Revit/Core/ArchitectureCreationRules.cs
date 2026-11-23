using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Pure authoring decisions, shared with tests; distances are internal feet.</summary>
    public static class ArchitectureCreationRules
    {
        public static string RoomStatus(double? area, bool boundaryClosed = true, bool redundant = false)
        {
            if (redundant) return "redundant";
            if (!area.HasValue || double.IsNaN(area.Value) || double.IsInfinity(area.Value)) return "unreadable";
            return area.Value > 0 && boundaryClosed ? "enclosed" : "not_enclosed";
        }

        public static JObject RoomPlacement(double? area, bool boundaryClosed)
        {
            string status = RoomStatus(area, boundaryClosed);
            bool measured = status != "unreadable";
            var response = new JObject
            {
                ["room_status"] = status,
                ["boundary_closed"] = boundaryClosed,
                ["area_sqft"] = measured ? (JToken)Math.Round(area.Value, 4) : JValue.CreateNull(),
                ["area_enclosed"] = measured ? (JToken)(status == "enclosed") : JValue.CreateNull(),
                ["area_means"] = !measured ? "The Area property could not be read." :
                    area.Value <= 0 ? "Area is zero; the room placement exists." :
                    boundaryClosed ? "Revit measured positive area and the boundary loops close." :
                    "Revit measured positive area, but closure of the boundary loops could not be verified."
            };
            if (status == "not_enclosed") AssumptionNotes.Stamp(response, new[] {
                AssumptionNotes.Row("room_status = not_enclosed", response.Value<string>("area_means"), "Close the room boundaries before placing the room.") });
            return response;
        }

        public static long? RedundantCreatedRoom(IEnumerable<long> createdRoomIds, IEnumerable<long> regionWarningIds)
        {
            var namedByRevit = new HashSet<long>(regionWarningIds);
            return createdRoomIds.Where(namedByRevit.Contains).Select(id => (long?)id).FirstOrDefault();
        }

        public static PlannedElement CreationPlanRow(int index, string kind, string typeName, IDictionary<string, string> facts,
            string topLevelUid, double? topLevelElevationFeet, double effectiveHeightFeet)
        {
            facts["top_level_uid"] = topLevelUid;
            facts["top_level_elev_mm"] = topLevelElevationFeet.HasValue
                ? Math.Round(topLevelElevationFeet.Value * 304.8, 1).ToString(CultureInfo.InvariantCulture) : "";
            if (kind == "wall") facts["effective_height_feet"] = Math.Round(effectiveHeightFeet, 6).ToString("0.######", CultureInfo.InvariantCulture);
            return new PlannedElement { UniqueId = "create:" + index, Category = kind, TypeName = typeName,
                Action = PlannedAction.Create, BeforeValues = facts };
        }

        public static CommandResult CreationResponse(CommandResult response, IEnumerable<(int index, JArray notes)> rows,
            JArray expansion, string failurePrefix)
        {
            var recipesByRow = expansion.OfType<JObject>().Where(row => row["index"] != null)
                .ToDictionary(row => row.Value<int>("index"), row => row.Value<int>("elements_index"));
            var seen = new HashSet<(bool recipe, int index, string note)>();
            var notes = new List<JObject>();
            void Add(bool recipe, int index, JArray source)
            {
                if (source == null) return;
                foreach (JObject note in source.OfType<JObject>())
                    if (seen.Add((recipe, index, note.ToString(Formatting.None)))) notes.Add((JObject)note.DeepClone());
            }
            foreach (var row in rows)
            {
                bool recipe = recipesByRow.TryGetValue(row.index, out int original);
                Add(recipe, recipe ? original : row.index, row.notes);
            }
            foreach (JObject row in expansion.OfType<JObject>())
                Add(true, row.Value<int>("elements_index"), row["assumptions"] as JArray);

            if (response.Success)
            {
                if (response.Data is JObject payload) payload["architectural_expansion"] = expansion;
                return AssumptionNotes.StampResult(response, notes);
            }
            response = AssumptionNotes.MarkFailure(response, failurePrefix, notes);
            if (expansion.Count > 0)
            {
                var detail = response.Detail ?? new JObject();
                detail["architectural_expansion"] = expansion;
                response.ReplaceDetail(detail);
            }
            return response;
        }

        public static double WallHeight(double baseZ, double? topZ)
        {
            double height = topZ.HasValue ? topZ.Value - baseZ : 3000 / 304.8;
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
                throw new ArgumentException("the wall top must be above its base");
            return height;
        }

        public static JObject Assumption(string field, JToken value, string reason) =>
            AssumptionNotes.Row(field + " = " + value, reason, "Supply " + field + " explicitly.");

        public sealed class TypeCandidate
        {
            public long Id;
            public string Name, Family;
            public bool Preferred = true;
            public string QualifiedName => string.IsNullOrEmpty(Family) ? Name : Family + ": " + Name;
        }

        public static long SelectType(IEnumerable<TypeCandidate> available, long? id, string name,
                                      long? defaultId, JArray assumptions)
        {
            var types = available.ToList();
            if (id.HasValue)
            {
                var exactId = types.FirstOrDefault(t => t.Id == id.Value);
                if (exactId != null) return exactId.Id;
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("type_id " + id + " does not identify a compatible loaded type; supply a fresh type_id or type_name");
            }
            string requested = (name ?? "").Trim();
            var exact = types.Where(t => string.Equals(requested, t.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requested, t.QualifiedName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (requested.Length > 0 && exact.Count > 1)
                throw new ArgumentException("ambiguous type_name; candidates: " + string.Join(", ", exact.OrderBy(t => t.Id).Select(t => t.QualifiedName + " (" + t.Id + ")")));
            TypeCandidate chosen = exact.Count == 1 ? exact[0] : null;
            if (chosen == null && requested.Length == 0 && defaultId.HasValue)
                chosen = types.FirstOrDefault(t => t.Id == defaultId.Value);
            if (chosen == null)
            {
                var reasonable = types.Where(t => t.Preferred).ToList();
                if (reasonable.Count == 0) reasonable = types;
                chosen = reasonable.OrderBy(t => requested.Length == 0 ? 0 : Math.Min(NameDistance(requested, t.Name), NameDistance(requested, t.QualifiedName)))
                    .ThenBy(t => t.QualifiedName, StringComparer.Ordinal).ThenBy(t => t.Id).FirstOrDefault();
            }
            if (chosen == null) throw new ArgumentException("the document has no compatible loaded architectural type");
            if (exact.Count == 0 || id.HasValue)
                assumptions.Add(AssumptionNotes.Row("type requested: name=" + (name ?? "<omitted>") + ", id=" + (id?.ToString() ?? "<omitted>") +
                    "; selected: " + chosen.QualifiedName + " (" + chosen.Id + ")",
                    requested.Length == 0 && chosen.Id == defaultId ? "document default type" : "deterministic compatible loaded type",
                    "Supply type_id or an unambiguous type_name (Family: Type)."));
            return chosen.Id;
        }

        public static JObject ResolveWallHeight(double baseZ, double? topZ, bool explicitTop, double scale, string topName)
        {
            bool bind = explicitTop || BindInferredTop(baseZ, topZ);
            double height = WallHeight(baseZ, bind ? topZ : null);
            var response = new JObject { ["height_feet"] = height, ["bind_top"] = bind };
            if (!explicitTop)
                AssumptionNotes.Stamp(response, new[] { Assumption("height", height / scale,
                    bind ? "bound to next upper level: " + topName : "3000 mm default; no upper level at least 2000 mm above base") });
            return response;
        }

        public static JObject ResolveHostedDefaults(JObject source, long hostLevelId)
        {
            var row = (JObject)source.DeepClone();
            var notes = new List<JObject>();
            if (row["level_id"] == null)
            {
                row["level_id"] = hostLevelId;
                notes.Add(Assumption("level_id", hostLevelId, "host wall base level"));
            }
            if (row["coordinate_mode"] == null)
            {
                row["coordinate_mode"] = "level_offset";
                notes.Add(Assumption("coordinate_mode", "level_offset", "Z is an offset above the host wall base level"));
            }
            return AssumptionNotes.Stamp(new JObject { ["element"] = row }, notes);
        }

        public static JObject ResolveType(IEnumerable<TypeCandidate> types, long? id, string name, long? defaultId)
        {
            var notes = new JArray();
            long selected = SelectType(types, id, name, defaultId, notes);
            return AssumptionNotes.Stamp(new JObject { ["type_id"] = selected }, notes.OfType<JObject>());
        }

        public static bool BindInferredTop(double baseZ, double? topZ) => topZ.HasValue && topZ.Value - baseZ >= 2000 / 304.8;

        public static string ExpansionConflict(JArray input)
        {
            if (input.OfType<JObject>().Any(x => x.Value<string>("placement") == "all_enclosed"))
                return "architectural recipes cannot share a batch with placement=all_enclosed; use separate batches";
            if (input.OfType<JObject>().Any(x => string.Equals(x.Value<string>("kind"), "wall_chain", StringComparison.OrdinalIgnoreCase)) && input.SelectTokens("$..batch_index").Any())
                return "wall_chain cannot share a batch with batch_index references; create the walls in a separate batch";
            return null;
        }

        // Stable edit distance: collector order must never decide the type an agent approves.
        public static int NameDistance(string requested, string candidate)
        {
            string a = (requested ?? "").Trim().ToUpperInvariant();
            string b = (candidate ?? "").Trim().ToUpperInvariant();
            int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++)
            {
                int[] next = new int[b.Length + 1]; next[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    next[j] = Math.Min(Math.Min(next[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = next;
            }
            return previous[b.Length];
        }

        public static List<JObject> WallChain(JObject item, double shortTolerance = 1e-6) =>
            WallChain(item, shortTolerance, out _);

        public static List<JObject> WallChain(JObject item, double shortTolerance, out List<int> skipped)
        {
            skipped = new List<int>();
            var points = item["points"] as JArray;
            if (points == null || points.Count < 2 || points.Count > 100)
                throw new ArgumentException("wall_chain.points requires 2 to 100 XYZ points");
            foreach (JToken point in points)
            {
                var xyz = point as JArray;
                if (xyz == null || xyz.Count != 3) throw new ArgumentException("wall_chain needs XYZ points");
                foreach (JToken coordinate in xyz)
                    if ((coordinate.Type != JTokenType.Integer && coordinate.Type != JTokenType.Float) ||
                        double.IsNaN(coordinate.Value<double>()) || double.IsInfinity(coordinate.Value<double>()))
                        throw new ArgumentException("wall_chain coordinates must be finite numbers");
            }
            if (item["closed"] != null && item["closed"].Type != JTokenType.Boolean)
                throw new ArgumentException("wall_chain.closed must be boolean");
            bool close = item.Value<bool?>("closed") ?? false;
            if (close && points.Count < 3) throw new ArgumentException("a closed wall_chain needs at least 3 points");
            Vec3[] verticesInModel = points.Select(p => new Vec3(p[0].Value<double>(), p[1].Value<double>(), p[2].Value<double>())).ToArray();
            bool Near(Vec3 a, Vec3 b) => (a - b).Length <= shortTolerance;
            bool alreadyClosed = Near(verticesInModel[0], verticesInModel[points.Count - 1]);
            int vertices = points.Count - (alreadyClosed ? 1 : 0);
            int count = points.Count - 1 + (close && !alreadyClosed ? 1 : 0);
            var rows = new List<JObject>();
            var spans = new List<(int Index, Vec3 Start, Vec3 End)>();
            for (int i = 0; i < count; i++)
            {
                int next = (i + 1) % points.Count;
                Vec3 start = verticesInModel[i], end = verticesInModel[next];
                if (Near(start, end)) { skipped.Add(i); continue; }
                foreach (var span in spans)
                    if (OverlapAlongAxis(span.Start, span.End, start, end, shortTolerance))
                        throw new ArgumentException("wall_chain segment " + i + " overlaps an earlier segment (" + span.Index + ")");
                spans.Add((i, start, end));
                var row = (JObject)item.DeepClone();
                row.Remove("points"); row.Remove("closed"); row["kind"] = "wall";
                row["_segment_index"] = i;
                row["start"] = points[i].DeepClone(); row["end"] = points[next].DeepClone();
                rows.Add(row);
            }
            if (rows.Count > 0 && close && vertices < 3)
                throw new ArgumentException("closed wall_chain needs at least 3 distinct vertices; overlapping return walls are refused");
            return rows;
        }

        private static bool OverlapAlongAxis(Vec3 firstStart, Vec3 firstEnd, Vec3 secondStart, Vec3 secondEnd, double tolerance)
        {
            Vec3 axis = firstEnd - firstStart;
            double length = axis.Length;
            Vec3 second = secondEnd - secondStart;
            double secondLength = second.Length;
            if (secondLength > length)
                return OverlapAlongAxis(secondStart, secondEnd, firstStart, firstEnd, tolerance);
            axis *= 1 / length;
            // Curve tolerance measures distance, not direction. A shallow crossing can
            // lie entirely inside that distance while the segments meet at only one point.
            Vec3 direction = second * (1 / secondLength);
            if ((direction - axis * direction.Dot(axis)).Length > 1e-10) return false;
            if (((firstStart - secondStart).Length <= tolerance && (firstEnd - secondEnd).Length <= tolerance) ||
                ((firstStart - secondEnd).Length <= tolerance && (firstEnd - secondStart).Length <= tolerance))
                return true;
            Vec3 from = secondStart - firstStart, to = secondEnd - firstStart;
            double fromAlong = from.Dot(axis), toAlong = to.Dot(axis);
            if ((from - axis * fromAlong).Length > tolerance || (to - axis * toAlong).Length > tolerance)
                return false;
            double overlapStart = Math.Max(0, Math.Min(fromAlong, toAlong));
            double overlapEnd = Math.Min(length, Math.Max(fromAlong, toAlong));
            return overlapEnd - overlapStart > tolerance;
        }

        public static JArray Expand(JArray input, double shortTolerance,
            Func<JObject, string, JArray, JObject> resolveHosted, out JArray evidence)
        {
            evidence = new JArray();
            // The expansion's own bookkeeping: a caller's row carrying it would be reported against another row.
            int carried = input == null ? -1 : input.ToList().FindIndex(row => row is JObject o && o[SourceKey] != null);
            if (carried >= 0) throw new ArgumentException("elements[" + carried + "]: " + SourceKey + " is written by the architectural expansion, not by a caller.");
            if (input == null || !input.OfType<JObject>().Any(row => RecipeKind(row) != null)) return input;
            string conflict = ExpansionConflict(input);
            if (conflict != null) throw new ArgumentException(conflict);
            var expanded = new JArray();
            for (int i = 0; i < input.Count; i++)
            {
                var source = input[i] as JObject;
                string kind = RecipeKind(source);
                if (kind == null)
                {
                    // Rows after a recipe move; each keeps the caller's index so its errors name the row the caller sent.
                    JToken copy = input[i].DeepClone();
                    if (copy is JObject passed) passed[SourceKey] = i;
                    expanded.Add(copy);
                    continue;
                }
                string invalid = Horizun.Contracts.ToolInputRules.ValidateCreation(source, kind);
                if (invalid != null) throw new ArgumentException("elements[" + i + "]: " + invalid);
                var notes = new JArray();
                List<JObject> rows;
                if (kind == "wall_chain")
                {
                    rows = WallChain(source, shortTolerance, out List<int> skipped);
                    if (skipped.Count > 0)
                    {
                        var omitted = new JObject { ["elements_index"] = i, ["kind"] = kind, ["state"] = "segments_omitted",
                            ["skipped_segment_indices"] = new JArray(skipped) };
                        if (rows.Count == 0) omitted["state"] = "recipe_omitted";
                        AssumptionNotes.Stamp(omitted, new[] { AssumptionNotes.Row("wall_chain skipped segments: " + string.Join(", ", skipped),
                            "Segments at or below Revit curve tolerance were omitted.", "Supply points separated by more than the curve tolerance.") });
                        evidence.Add(omitted);
                    }
                }
                else rows = new List<JObject> { resolveHosted((JObject)source.DeepClone(), kind, notes) };
                for (int segment = 0; segment < rows.Count; segment++)
                {
                    JObject row = rows[segment];
                    var mapped = new JObject { ["index"] = expanded.Count, ["elements_index"] = i,
                        ["kind"] = kind, ["segment_index"] = row.Value<int?>("_segment_index") ?? segment };
                    row.Remove("_segment_index");
                    row[SourceKey] = i;
                    if (segment == 0) AssumptionNotes.Stamp(mapped, notes.OfType<JObject>());
                    evidence.Add(mapped);
                    expanded.Add(row);
                }
                int limit = Horizun.Contracts.Contract.CreateElementsBatchLimit;
                if (expanded.Count > limit) throw new ArgumentException("architectural expansion exceeds the " + limit + " item atomic-batch limit");
            }
            return expanded;
        }

        // Caller index of an expanded row, read by CreationBatch to report errors against elements[i].
        internal const string SourceKey = ExpansionKeys.RecipeFrom;

        private static string RecipeKind(JObject row)
        {
            string kind = row?.Value<string>("kind")?.ToLowerInvariant();
            return kind == "wall_chain" || kind == "door" || kind == "window" ? kind : null;
        }
    }
}
