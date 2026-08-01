// -----------------------------------------------------------------------------
// Horizun Revit MCP - one composable query instead of a tool per question.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class QueryModelCommand : ICommand
    {
        public string Name => "horizun_query_model";
        public string Description => "Composable, federated, paginated model query with explicit coverage.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document host = app.ActiveUIDocument?.Document;
            if (host == null) return CommandResult.Fail("No active Revit document.");

            bool includeLinks = request["include_links"] == null || request.Value<bool>("include_links");
            string scope = (request.Value<string>("scope") ?? "model").ToLowerInvariant();
            if (scope != "model" && scope != "current_view" && scope != "view")
                return CommandResult.Fail("scope must be model, current_view or view.");
            if (includeLinks && scope != "model")
                return CommandResult.Fail(
                    "View-scoped queries and include_links=true cannot be combined honestly: a host ViewId is not " +
                    "a view in a linked document. Use scope=model with links, or include_links=false for a host view.");

            ElementId viewId = null;
            if (scope == "current_view") viewId = app.ActiveUIDocument?.ActiveView?.Id;
            else if (scope == "view")
            {
                long raw = request.Value<long?>("view_id") ?? -1;
                if (!Rid.CanRepresent(raw)) return CommandResult.Fail("view_id is required and must be a valid ElementId for scope=view.");
                View view = host.GetElement(Rid.Make(raw)) as View;
                if (view == null) return CommandResult.Fail("view_id does not identify a view in the active document.");
                viewId = view.Id;
            }

            List<string> categories = Strings(request["categories"] as JArray);
            List<string> returnParameters = Strings(request["return_parameters"] as JArray);
            JArray predicates = request["parameters"] as JArray ?? new JArray();
            foreach (JToken token in predicates)
            {
                JObject p = token as JObject;
                if (p == null || string.IsNullOrWhiteSpace(p.Value<string>("name")) ||
                    string.IsNullOrWhiteSpace(p.Value<string>("operator")))
                    return CommandResult.Fail("Every parameters entry must be an object with name and operator.");
                string op = p.Value<string>("operator").ToLowerInvariant();
                if (op != "exists" && op != "not_exists" && p["value"] == null)
                    return CommandResult.Fail("Parameter predicate '" + p.Value<string>("name") + "' with operator '" + op + "' requires value.");
            }

            Box queryBox;
            string boxError;
            if (!TryReadBox(request["bounding_box"] as JObject, out queryBox, out boxError))
                return CommandResult.Fail(boxError);

            string coordinateUnits = (request.Value<string>("coordinate_units") ?? "mm").ToLowerInvariant();
            double coordinateScale;
            if (!TryScaleFromFeet(coordinateUnits, out coordinateScale))
                return CommandResult.Fail("coordinate_units must be mm, m or feet.");
            bool includeBox = request.Value<bool?>("include_bounding_box") == true;
            bool includeTypes = request.Value<bool?>("include_types") == true;

            int maxRows = Math.Max(1, Math.Min(500, request.Value<int?>("max_rows") ?? 100));
            var matched = new List<Row>();
            var unreadable = new JArray();
            int unreadableTotal = 0;

            Collect(host, "host", host.Title, null, Transform.Identity, viewId, categories, request,
                    predicates, returnParameters, queryBox, includeBox, coordinateScale, includeTypes,
                    matched, unreadable, ref unreadableTotal);

            if (includeLinks)
            {
                foreach (RevitLinkInstance link in new FilteredElementCollector(host)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document linked = null;
                    try { linked = link.GetLinkDocument(); }
                    catch (Exception ex)
                    {
                        AddUnreadable(unreadable, ref unreadableTotal, new JObject
                        {
                            ["link_instance_id"] = Rid.Value(link.Id), ["source_model"] = link.Name,
                            ["reason"] = "link document could not be read: " + ex.Message
                        });
                        continue;
                    }
                    if (linked == null) continue; // FederatedVisibility names every unloaded link below.
                    Transform transform;
                    try { transform = link.GetTotalTransform() ?? Transform.Identity; }
                    catch (Exception ex)
                    {
                        AddUnreadable(unreadable, ref unreadableTotal, new JObject
                        {
                            ["link_instance_id"] = Rid.Value(link.Id), ["source_model"] = linked.Title,
                            ["reason"] = "link transform could not be read: " + ex.Message
                        });
                        continue;
                    }
                    Collect(linked, "link", linked.Title, Rid.Value(link.Id), transform, null, categories, request,
                            predicates, returnParameters, queryBox, includeBox, coordinateScale, includeTypes,
                            matched, unreadable, ref unreadableTotal);
                }
            }

            matched = matched.OrderBy(r => r.SourceKind, StringComparer.Ordinal)
                             .ThenBy(r => r.SourceModel, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(r => r.LinkInstanceId ?? -1)
                             .ThenBy(r => r.Id).ToList();

            string queryHash = QueryHash(request);
            string setHash = ResultSetHash(matched);
            int offset = 0;
            string cursor = request.Value<string>("cursor");
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                string cursorError;
                if (!TryCursor(cursor, queryHash, setHash, out offset, out cursorError))
                    return CommandResult.Fail(cursorError);
            }
            if (offset > matched.Count)
                return CommandResult.Fail("The cursor starts beyond the current result set. Re-run without cursor.");

            List<Row> page = matched.Skip(offset).Take(maxRows).ToList();
            int nextOffset = offset + page.Count;
            string nextCursor = nextOffset < matched.Count ? MakeCursor(nextOffset, queryHash, setHash) : null;

            JObject coverage = FederatedVisibility.Measure(host, includeLinks);
            bool coverageComplete = coverage.Value<bool>("coverage_complete") && unreadableTotal == 0;
            return CommandResult.Ok(new JObject
            {
                ["document"] = host.Title,
                ["scope"] = scope,
                ["view_id"] = viewId == null ? JValue.CreateNull() : new JValue(Rid.Value(viewId)),
                ["include_links"] = includeLinks,
                ["matched_total"] = matched.Count,
                ["returned"] = page.Count,
                ["offset"] = offset,
                ["truncated"] = nextCursor != null,
                ["next_cursor"] = nextCursor == null ? JValue.CreateNull() : new JValue(nextCursor),
                ["result_set_fingerprint"] = setHash.Substring(0, 16),
                ["coverage_complete"] = coverageComplete,
                ["unreadable_total"] = unreadableTotal,
                ["unreadable_shown"] = unreadable.Count,
                ["unreadable_truncated"] = unreadableTotal > unreadable.Count,
                ["unreadable"] = unreadable,
                ["federated_coverage"] = coverage,
                ["summary"] = Summary(matched),
                ["rows"] = new JArray(page.Select(r => r.Json))
            });
        }

        private static void Collect(Document source, string sourceKind, string sourceName, long? linkId,
                                    Transform transform, ElementId viewId, List<string> categories, JObject request,
                                    JArray predicates, List<string> returnParameters, Box queryBox, bool includeBox,
                                    double coordinateScale, bool includeTypes, List<Row> rows, JArray unreadable,
                                    ref int unreadableTotal)
        {
            HashSet<long> categoryIds = ResolveCategories(source, categories, unreadable, ref unreadableTotal, sourceName);
            FilteredElementCollector collector = viewId == null
                ? new FilteredElementCollector(source)
                : new FilteredElementCollector(source, viewId);
            // Revit refuses extraction from a collector with no native filter, even
            // though the LINQ Cast/Where compiles. Apply a real ElementFilter before
            // iteration. The OR is an explicit pass over types + instances when the
            // caller requested both.
            IEnumerable<Element> candidates = includeTypes
                ? collector.WherePasses(new LogicalOrFilter(
                    new ElementIsElementTypeFilter(false),
                    new ElementIsElementTypeFilter(true))).Cast<Element>()
                : collector.WhereElementIsNotElementType().Cast<Element>();

            foreach (Element element in candidates)
            {
                long id = Rid.Value(element.Id);
                try
                {
                    if (categoryIds != null)
                    {
                        long? categoryId = element.Category == null ? (long?)null : Rid.Value(element.Category.Id);
                        if (categoryId == null || !categoryIds.Contains(categoryId.Value)) continue;
                    }

                    Element type = element is ElementType ? element : source.GetElement(element.GetTypeId());
                    string elementName = Safe(() => element.Name);
                    string family = type is ElementType et ? Safe(() => et.FamilyName) : null;
                    string typeName = type == null ? null : Safe(() => type.Name);
                    string level = LevelName(source, element);

                    if (!Contains(elementName, request.Value<string>("name")) ||
                        !Contains(family, request.Value<string>("family")) ||
                        !Contains(typeName, request.Value<string>("type")) ||
                        !Contains(level, request.Value<string>("level"))) continue;

                    bool predicateUnknown;
                    string predicateError;
                    if (!PredicatesMatch(source, element, type, predicates, out predicateUnknown, out predicateError))
                    {
                        if (predicateUnknown)
                            AddUnreadable(unreadable, ref unreadableTotal, Error(sourceName, linkId, id, predicateError));
                        continue;
                    }

                    Box elementBox = ElementBox(element, transform);
                    if (queryBox != null)
                    {
                        if (elementBox == null)
                        {
                            AddUnreadable(unreadable, ref unreadableTotal,
                                Error(sourceName, linkId, id, "bounding box is unavailable, so intersection is unknown"));
                            continue;
                        }
                        if (!elementBox.Intersects(queryBox)) continue;
                    }

                    var json = new JObject
                    {
                        ["element_id"] = id,
                        ["unique_id"] = Safe(() => element.UniqueId),
                        ["category"] = Safe(() => element.Category?.Name),
                        ["name"] = elementName,
                        ["family"] = family,
                        ["type"] = typeName,
                        ["type_id"] = type == null ? JValue.CreateNull() : new JValue(Rid.Value(type.Id)),
                        ["level"] = level,
                        ["is_element_type"] = element is ElementType,
                        ["source_kind"] = sourceKind,
                        ["source_model"] = sourceName,
                        ["link_instance_id"] = linkId == null ? JValue.CreateNull() : new JValue(linkId.Value)
                    };
                    if (includeBox) json["bounding_box"] = BoxJson(elementBox, coordinateScale);
                    if (returnParameters.Count > 0)
                    {
                        List<string> projectionErrors;
                        json["parameters"] = ProjectParameters(source, element, type, returnParameters, out projectionErrors);
                        foreach (string projectionError in projectionErrors)
                            AddUnreadable(unreadable, ref unreadableTotal,
                                Error(sourceName, linkId, id, projectionError));
                    }

                    rows.Add(new Row
                    {
                        Id = id, SourceKind = sourceKind, SourceModel = sourceName,
                        LinkInstanceId = linkId, Category = (string)json["category"], Level = level, Json = json
                    });
                }
                catch (Exception ex)
                {
                    AddUnreadable(unreadable, ref unreadableTotal, Error(sourceName, linkId, id, ex.Message));
                }
            }
        }

        private static HashSet<long> ResolveCategories(Document doc, List<string> names, JArray errors,
                                                       ref int errorTotal, string source)
        {
            if (names.Count == 0) return null;
            var ids = new HashSet<long>();
            foreach (string name in names)
            {
                Category category = null;
                BuiltInCategory bic;
                if (Enum.TryParse(name, true, out bic))
                    try { category = Category.GetCategory(doc, bic); } catch { }
                if (category == null)
                    try
                    {
                        foreach (Category c in doc.Settings.Categories)
                            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) { category = c; break; }
                    }
                    catch (Exception ex)
                    {
                        AddUnreadable(errors, ref errorTotal, new JObject
                        { ["source_model"] = source, ["reason"] = "category table unreadable: " + ex.Message });
                    }
                if (category != null) ids.Add(Rid.Value(category.Id));
            }
            return ids;
        }

        private static bool PredicatesMatch(Document doc, Element element, Element type, JArray predicates,
                                            out bool unknown, out string error)
        {
            unknown = false; error = null;
            foreach (JObject predicate in predicates.OfType<JObject>())
            {
                string name = predicate.Value<string>("name");
                string op = predicate.Value<string>("operator").ToLowerInvariant();
                string scope;
                Parameter p = ResolveParameter(element, type, name, out scope, out error);
                if (error != null) { unknown = true; return false; }
                if (op == "exists") { if (p == null) return false; continue; }
                if (op == "not_exists") { if (p != null) return false; continue; }
                if (p == null) return false;
                if (!Compare(p, op, predicate["value"], out error))
                {
                    if (error != null) unknown = true;
                    return false;
                }
            }
            return true;
        }

        private static Parameter ResolveParameter(Element element, Element type, string spec,
                                                   out string scope, out string error)
        {
            scope = null; error = null;
            Parameter p = ResolveOn(element, spec, out error);
            if (error != null) return null;
            if (p != null) { scope = "instance"; return p; }
            if (type != null && type.Id != element.Id)
            {
                p = ResolveOn(type, spec, out error);
                if (p != null) scope = "type";
            }
            return p;
        }

        private static Parameter ResolveOn(Element element, string spec, out string error)
        {
            error = null;
            if (element == null || string.IsNullOrWhiteSpace(spec)) return null;
            BuiltInParameter bip;
            if (Enum.TryParse(spec, true, out bip))
                try { return element.get_Parameter(bip); }
                catch (Exception ex) { error = "BuiltInParameter '" + spec + "' could not be read: " + ex.Message; return null; }
            Guid guid;
            if (Guid.TryParse(spec, out guid))
                try { return element.get_Parameter(guid); }
                catch (Exception ex) { error = "shared parameter '" + spec + "' could not be read: " + ex.Message; return null; }
            try
            {
                IList<Parameter> found = element.GetParameters(spec);
                if (found.Count > 1)
                {
                    error = "parameter name '" + spec + "' is ambiguous on element " + Rid.Value(element.Id) +
                            " (" + found.Count + " parameters share it); use a BuiltInParameter token or GUID";
                    return null;
                }
                return found.Count == 1 ? found[0] : null;
            }
            catch (Exception ex) { error = "parameter '" + spec + "' could not be read: " + ex.Message; return null; }
        }

        private static bool Compare(Parameter p, string op, JToken expected, out string error)
        {
            error = null;
            try
            {
                if (expected != null && (expected.Type == JTokenType.Integer || expected.Type == JTokenType.Float))
                {
                    double have;
                    if (p.StorageType == StorageType.Double) have = p.AsDouble();
                    else if (p.StorageType == StorageType.Integer) have = p.AsInteger();
                    else if (p.StorageType == StorageType.ElementId) have = Rid.Value(p.AsElementId());
                    else { error = "numeric comparison requested for non-numeric parameter '" + p.Definition?.Name + "'"; return false; }
                    double want = expected.Value<double>();
                    switch (op)
                    {
                        case "equals": return Math.Abs(have - want) <= 1e-9;
                        case "not_equals": return Math.Abs(have - want) > 1e-9;
                        case "gt": return have > want;
                        case "gte": return have >= want;
                        case "lt": return have < want;
                        case "lte": return have <= want;
                        default: error = "operator '" + op + "' is not valid for a numeric value"; return false;
                    }
                }

                string haveText = ParameterText(p) ?? "";
                string wantText = expected == null || expected.Type == JTokenType.Null ? "" :
                    expected.Type == JTokenType.String ? expected.Value<string>() : expected.ToString(Formatting.None);
                switch (op)
                {
                    case "equals": return string.Equals(haveText, wantText, StringComparison.OrdinalIgnoreCase);
                    case "not_equals": return !string.Equals(haveText, wantText, StringComparison.OrdinalIgnoreCase);
                    case "contains": return haveText.IndexOf(wantText, StringComparison.OrdinalIgnoreCase) >= 0;
                    case "starts_with": return haveText.StartsWith(wantText, StringComparison.OrdinalIgnoreCase);
                    case "ends_with": return haveText.EndsWith(wantText, StringComparison.OrdinalIgnoreCase);
                    default: error = "operator '" + op + "' requires a numeric JSON value for ordering"; return false;
                }
            }
            catch (Exception ex) { error = "parameter comparison failed: " + ex.Message; return false; }
        }

        private static JObject ProjectParameters(Document doc, Element element, Element type, List<string> specs,
                                                  out List<string> errors)
        {
            errors = new List<string>();
            var result = new JObject();
            foreach (string spec in specs)
            {
                string scope, error;
                Parameter p = ResolveParameter(element, type, spec, out scope, out error);
                if (error != null)
                {
                    errors.Add(error);
                    result[spec] = new JObject { ["read_error"] = error };
                }
                else if (p == null) result[spec] = new JObject { ["exists"] = false };
                else result[spec] = new JObject
                {
                    ["exists"] = true, ["scope"] = scope, ["storage_type"] = p.StorageType.ToString(),
                    ["raw"] = Raw(p), ["display"] = Safe(() => p.AsValueString())
                };
            }
            return result;
        }

        private static JToken Raw(Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString();
                    case StorageType.Integer: return p.AsInteger();
                    case StorageType.Double: return p.AsDouble();
                    case StorageType.ElementId: return Rid.Value(p.AsElementId());
                    default: return JValue.CreateNull();
                }
            }
            catch (Exception ex) { return new JObject { ["read_error"] = ex.Message }; }
        }

        private static string ParameterText(Parameter p)
        {
            if (p.StorageType == StorageType.String) return p.AsString();
            string displayed = p.AsValueString();
            if (!string.IsNullOrEmpty(displayed)) return displayed;
            JToken raw = Raw(p);
            return raw?.Type == JTokenType.Null ? null : raw?.ToString(Formatting.None);
        }

        private static Box ElementBox(Element element, Transform transform)
        {
            BoundingBoxXYZ b = element.get_BoundingBox(null);
            if (b == null) return null;
            Transform own = b.Transform ?? Transform.Identity;
            var points = new List<XYZ>();
            foreach (double x in new[] { b.Min.X, b.Max.X })
                foreach (double y in new[] { b.Min.Y, b.Max.Y })
                    foreach (double z in new[] { b.Min.Z, b.Max.Z })
                    {
                        XYZ p = own.OfPoint(new XYZ(x, y, z));
                        points.Add((transform ?? Transform.Identity).OfPoint(p));
                    }
            return new Box(
                new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
        }

        private static bool TryReadBox(JObject o, out Box box, out string error)
        {
            box = null; error = null;
            if (o == null) return true;
            JArray min = o["min"] as JArray, max = o["max"] as JArray;
            if (min == null || max == null || min.Count != 3 || max.Count != 3)
            { error = "bounding_box.min and max must each contain exactly three numbers."; return false; }
            double feetPerUnit;
            if (!TryScaleToFeet((o.Value<string>("units") ?? "mm").ToLowerInvariant(), out feetPerUnit))
            { error = "bounding_box.units must be mm, m or feet."; return false; }
            try
            {
                var lo = new XYZ(min[0].Value<double>() * feetPerUnit, min[1].Value<double>() * feetPerUnit, min[2].Value<double>() * feetPerUnit);
                var hi = new XYZ(max[0].Value<double>() * feetPerUnit, max[1].Value<double>() * feetPerUnit, max[2].Value<double>() * feetPerUnit);
                if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z)
                { error = "bounding_box.min must be <= max on every axis."; return false; }
                box = new Box(lo, hi); return true;
            }
            catch (Exception ex) { error = "bounding_box coordinates are invalid: " + ex.Message; return false; }
        }

        private static bool TryScaleToFeet(string units, out double scale)
        {
            if (units == "feet") { scale = 1; return true; }
            if (units == "m") { scale = 1.0 / 0.3048; return true; }
            if (units == "mm") { scale = 1.0 / 304.8; return true; }
            scale = 0; return false;
        }

        private static bool TryScaleFromFeet(string units, out double scale)
        {
            if (units == "feet") { scale = 1; return true; }
            if (units == "m") { scale = 0.3048; return true; }
            if (units == "mm") { scale = 304.8; return true; }
            scale = 0; return false;
        }

        private static JToken BoxJson(Box b, double scale)
        {
            if (b == null) return JValue.CreateNull();
            return new JObject
            {
                ["min"] = new JArray(b.Min.X * scale, b.Min.Y * scale, b.Min.Z * scale),
                ["max"] = new JArray(b.Max.X * scale, b.Max.Y * scale, b.Max.Z * scale)
            };
        }

        private static JObject Summary(List<Row> rows)
        {
            return new JObject
            {
                ["by_category"] = Counts(rows, r => r.Category ?? "(no category)"),
                ["by_level"] = Counts(rows, r => r.Level ?? "(no level)"),
                ["by_source"] = Counts(rows, r => r.SourceKind + ":" + (r.SourceModel ?? "(unknown)"))
            };
        }

        private static JObject Counts(IEnumerable<Row> rows, Func<Row, string> key)
            => JsonObjectKey.SummaryCounts(rows.Select(key));

        private static string QueryHash(JObject request)
        {
            JObject copy = (JObject)request.DeepClone();
            copy.Remove("cursor"); copy.Remove("max_rows");
            return RequestFingerprint.Sha256Hex(RequestFingerprint.Canonical(copy));
        }

        private static string ResultSetHash(IEnumerable<Row> rows) => RequestFingerprint.Sha256Hex(
            string.Join("\n", rows.Select(r => RequestFingerprint.Canonical(r.Json))));

        private static string MakeCursor(int offset, string queryHash, string setHash) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture) + "\n" + queryHash + "\n" + setHash));

        private static bool TryCursor(string cursor, string queryHash, string setHash, out int offset, out string error)
        {
            offset = 0; error = null;
            try
            {
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
                if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                    throw new FormatException("cursor payload has the wrong shape");
                if (parts[1] != queryHash)
                { error = "The cursor belongs to different query arguments. Re-run without cursor."; return false; }
                if (parts[2] != setHash)
                { error = "The model result set changed since the previous page. The cursor is stale; re-run from the first page."; return false; }
                return true;
            }
            catch (Exception ex) { error = "cursor is invalid: " + ex.Message + ". Re-run without cursor."; return false; }
        }

        private static string LevelName(Document doc, Element element)
        {
            Parameter p = element.get_Parameter(BuiltInParameter.LEVEL_PARAM) ??
                          element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
                          element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (p == null) return null;
            if (p.StorageType == StorageType.ElementId)
            {
                Element level = doc.GetElement(p.AsElementId());
                if (level != null) return Safe(() => level.Name);
            }
            return Safe(() => p.AsValueString());
        }

        private static bool Contains(string have, string want) =>
            string.IsNullOrWhiteSpace(want) || (have != null && have.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
        private static List<string> Strings(JArray a) => a == null ? new List<string>() :
            a.Where(x => x.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)x)).Select(x => (string)x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static JObject Error(string source, long? linkId, long id, string reason) => new JObject
        { ["source_model"] = source, ["link_instance_id"] = linkId == null ? JValue.CreateNull() : new JValue(linkId.Value), ["element_id"] = id, ["reason"] = reason };
        private static void AddUnreadable(JArray shown, ref int total, JObject error)
        { total++; if (shown.Count < 100) shown.Add(error); }

        private sealed class Row
        {
            public long Id; public string SourceKind; public string SourceModel; public long? LinkInstanceId;
            public string Category; public string Level; public JObject Json;
        }

        private sealed class Box
        {
            public readonly XYZ Min, Max;
            public Box(XYZ min, XYZ max) { Min = min; Max = max; }
            public bool Intersects(Box other) =>
                Min.X <= other.Max.X && Max.X >= other.Min.X &&
                Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
                Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
        }
    }
}
