// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_styles - object styles, subcategories, line styles, line
// patterns and fill patterns, read and written the verified way.
//
// Reads answer from the document. Every write is a single subject run through
// VerifiedModelEdit: rehearsed in a rolled-back transaction, applied inside a
// TransactionGroup, and every requested value (weight, colour, pattern,
// material, segments, grids) RE-READ from the committed model.
//
// STATED LIMITS:
//   * nothing here deletes a style. A subcategory, line pattern or fill pattern
//     is removed with horizun_delete_verified (their ids are published), and a
//     built-in category cannot be deleted at all.
//   * cut weights exist only on cuttable categories; asking for one elsewhere is
//     refused by name before any transaction.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ManageStylesCommand : ICommand
    {
        public const string Tool = "horizun_manage_styles";
        public string Name => "horizun_manage_styles";
        public string Description => "Object styles, subcategories, line styles, line patterns and fill patterns: list, set and create, re-read after commit.";

        private const string Known = "list_object_styles, set_object_style, create_subcategory, list_line_styles, " +
                                     "create_line_style, list_line_patterns, create_line_pattern, list_fill_patterns, create_fill_pattern";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request = VerifiedModelEdit.Parse(paramsJson, out CommandResult parseError);
            if (request == null) return parseError;
            string op = VerifiedModelEdit.Operation(request);
            switch (op)
            {
                case "list_object_styles":
                case "list_line_styles":
                case "list_line_patterns":
                case "list_fill_patterns":
                {
                    Document doc = app.ActiveUIDocument?.Document;
                    if (doc == null) return CommandResult.Fail("No document is open.");
                    CommandResult guard = DocumentGate.ReadGuard(doc, request, Tool);
                    if (guard != null) return guard;
                    if (op == "list_line_patterns") return VerifiedModelEdit.ReadReply(ListLinePatterns(doc));
                    if (op == "list_fill_patterns") return VerifiedModelEdit.ReadReply(ListFillPatterns(doc));
                    string category = op == "list_line_styles" ? "OST_Lines" : request.Value<string>("category");
                    return ListObjectStyles(doc, category);
                }
                case "set_object_style":
                case "create_subcategory":
                case "create_line_style":
                case "create_line_pattern":
                case "create_fill_pattern":
                {
                    GateResult gate = DocumentGate.ForMutation(app, request, Tool);
                    if (!gate.Ok) return gate.Refusal;
                    ModelEdit edit;
                    string error;
                    if (op == "set_object_style") edit = PlanSetStyle(gate.Document, request, out error);
                    else if (op == "create_line_pattern") edit = PlanLinePattern(gate.Document, request, out error);
                    else if (op == "create_fill_pattern") edit = PlanFillPattern(gate.Document, request, out error);
                    else edit = PlanSubcategory(gate.Document, request, op == "create_line_style", out error);
                    if (edit == null) return CommandResult.Fail(error + " Nothing was written.");
                    edit.Tool = Tool; edit.Operation = op;
                    return VerifiedModelEdit.Run(app, gate, request, edit,
                        "category", "subcategory", "name", "projection_weight", "cut_weight", "color", "line_pattern",
                        "material", "segments", "target", "fill", "angle", "spacing", "spacing2", "units");
                }
                default:
                    return VerifiedModelEdit.UnknownOperation(Tool, op, Known);
            }
        }

        // ================================================================== reads

        private static CommandResult ListObjectStyles(Document doc, string categoryName)
        {
            var rows = new JArray();
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                foreach (Category c in doc.Settings.Categories.Cast<Category>()
                             .Where(c => SafeType(c) == CategoryType.Model || SafeType(c) == CategoryType.Annotation)
                             .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    JObject row = StyleRow(doc, c);
                    row["subcategory_count"] = SafeCount(c);
                    rows.Add(row);
                }
                return VerifiedModelEdit.ReadReply(new JObject
                {
                    ["document"] = doc.Title, ["count"] = rows.Count, ["categories"] = rows,
                    ["note"] = "Top-level model and annotation categories. Pass category to list one with its subcategories."
                });
            }
            Category parent = ResolveCategory(doc, categoryName, out string error);
            if (parent == null) return CommandResult.Fail(error);
            JObject head = StyleRow(doc, parent);
            var subs = new JArray();
            foreach (Category s in parent.SubCategories.Cast<Category>().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                subs.Add(StyleRow(doc, s));
            head["subcategories"] = subs;
            return VerifiedModelEdit.ReadReply(new JObject { ["document"] = doc.Title, ["category"] = head, ["count"] = subs.Count });
        }

        private static JObject StyleRow(Document doc, Category c)
        {
            long id = Rid.Value(c.Id);
            var row = new JObject
            {
                ["id"] = id,
                ["name"] = VerifiedModelEdit.Safe(() => c.Name),
                ["built_in"] = id < 0 ? VerifiedModelEdit.Safe(() => ((BuiltInCategory)(int)id).ToString()) : null,
                ["parent"] = VerifiedModelEdit.Safe(() => c.Parent?.Name),
                ["type"] = SafeType(c)?.ToString(),
                ["cuttable"] = SafeBool(() => c.IsCuttable),
                ["projection_weight"] = Weight(c, GraphicsStyleType.Projection),
                ["cut_weight"] = SafeBool(() => c.IsCuttable) == true ? Weight(c, GraphicsStyleType.Cut) : null,
                ["color"] = VerifiedModelEdit.Hex(SafeColor(c)),
                ["line_pattern"] = PatternName(doc, SafeId(() => c.GetLinePatternId(GraphicsStyleType.Projection))),
                ["material"] = VerifiedModelEdit.Safe(() => c.Material?.Name),
                ["graphics_style_id"] = SafeId(() => c.GetGraphicsStyle(GraphicsStyleType.Projection)?.Id) is ElementId g ? (JToken)Rid.Value(g) : null
            };
            return row;
        }

        private static JObject ListLinePatterns(Document doc)
        {
            var rows = new JArray();
            foreach (LinePatternElement e in new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement))
                         .Cast<LinePatternElement>().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var segs = new JArray();
                try
                {
                    foreach (LinePatternSegment s in e.GetLinePattern().GetSegments())
                        segs.Add(new JObject { ["type"] = s.Type.ToString().ToLowerInvariant(), ["length_mm"] = Math.Round(s.Length * 304.8, 4) });
                }
                catch (Exception ex) { segs.Add(new JObject { ["error"] = ex.Message }); }
                rows.Add(new JObject { ["id"] = Rid.Value(e.Id), ["name"] = e.Name, ["segments"] = segs });
            }
            return new JObject
            {
                ["document"] = doc.Title, ["count"] = rows.Count, ["line_patterns"] = rows,
                ["solid_pattern_id"] = Rid.Value(LinePatternElement.GetSolidPatternId()),
                ["note"] = "'Solid' is not an element; it is the reserved solid pattern id."
            };
        }

        private static JObject ListFillPatterns(Document doc)
        {
            var rows = new JArray();
            foreach (FillPatternElement e in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                         .Cast<FillPatternElement>().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var row = new JObject { ["id"] = Rid.Value(e.Id), ["name"] = e.Name };
                try
                {
                    FillPattern p = e.GetFillPattern();
                    row["target"] = p.Target.ToString().ToLowerInvariant();
                    row["solid"] = p.IsSolidFill;
                    var grids = new JArray();
                    foreach (FillGrid g in p.GetFillGrids())
                        grids.Add(new JObject { ["angle_deg"] = Math.Round(g.Angle * 180 / Math.PI, 6), ["spacing_mm"] = Math.Round(g.Offset * 304.8, 4),
                                                ["shift_mm"] = Math.Round(g.Shift * 304.8, 4), ["segments"] = g.GetSegments().Count });
                    row["grids"] = grids;
                }
                catch (Exception ex) { row["error"] = ex.Message; }
                rows.Add(row);
            }
            return new JObject { ["document"] = doc.Title, ["count"] = rows.Count, ["fill_patterns"] = rows };
        }

        // ================================================================== writes

        private sealed class StyleAsk
        {
            public int? Projection, Cut; public Color Color; public string ColorHex;
            public ElementId Pattern; public string PatternName; public Material Material; public bool Any;
        }

        /// <summary>The optional style fields shared by set_object_style and create_subcategory.</summary>
        private static StyleAsk ReadStyle(Document doc, JObject r, out string error)
        {
            error = null; var a = new StyleAsk();
            if (r["projection_weight"] != null)
            {
                a.Projection = r.Value<int>("projection_weight");
                if (a.Projection < 1 || a.Projection > 16) { error = "projection_weight must be 1..16."; return null; }
                a.Any = true;
            }
            if (r["cut_weight"] != null)
            {
                a.Cut = r.Value<int>("cut_weight");
                if (a.Cut < 1 || a.Cut > 16) { error = "cut_weight must be 1..16."; return null; }
                a.Any = true;
            }
            if (r["color"] != null)
            {
                if (!VerifiedModelEdit.TryParseColor(r.Value<string>("color"), out a.Color)) { error = "color must be #RRGGBB."; return null; }
                a.ColorHex = VerifiedModelEdit.Hex(a.Color); a.Any = true;
            }
            if (r["line_pattern"] != null)
            {
                string n = r.Value<string>("line_pattern") ?? "";
                if (n.Equals("solid", StringComparison.OrdinalIgnoreCase)) a.Pattern = LinePatternElement.GetSolidPatternId();
                else a.Pattern = LinePatternElement.GetLinePatternElementByName(doc, n)?.Id;
                if (a.Pattern == null)
                {
                    error = "line_pattern '" + n + "' is not in the document (operation=list_line_patterns lists them; 'Solid' is the solid one).";
                    return null;
                }
                a.PatternName = PatternName(doc, a.Pattern); a.Any = true;
            }
            if (r["material"] != null)
            {
                string n = r.Value<string>("material") ?? "";
                var found = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                    .Where(m => string.Equals(m.Name, n, StringComparison.Ordinal)).ToList();
                if (found.Count != 1) { error = "material '" + n + "' matches " + found.Count + " materials; exactly one is required."; return null; }
                a.Material = found[0]; a.Any = true;
            }
            return a;
        }

        private static void ApplyStyle(Category c, StyleAsk a)
        {
            if (a.Projection.HasValue) c.SetLineWeight(a.Projection.Value, GraphicsStyleType.Projection);
            if (a.Cut.HasValue) c.SetLineWeight(a.Cut.Value, GraphicsStyleType.Cut);
            if (a.Color != null) c.LineColor = a.Color;
            if (a.Pattern != null) c.SetLinePatternId(a.Pattern, GraphicsStyleType.Projection);
            if (a.Material != null) c.Material = a.Material;
        }

        private static string[] Required(StyleAsk a, params string[] extra)
        {
            var r = new List<string>(extra);
            if (a.Projection.HasValue) r.Add("projection_weight");
            if (a.Cut.HasValue) r.Add("cut_weight");
            if (a.Color != null) r.Add("color");
            if (a.Pattern != null) r.Add("line_pattern");
            if (a.Material != null) r.Add("material");
            return r.ToArray();
        }

        private static void CheckStyle(Document doc, PostconditionCheck check, Category c, StyleAsk a)
        {
            if (a.Projection.HasValue) Read(check, "projection_weight", a.Projection.Value, () => Weight(c, GraphicsStyleType.Projection));
            if (a.Cut.HasValue) Read(check, "cut_weight", a.Cut.Value, () => Weight(c, GraphicsStyleType.Cut));
            if (a.Color != null) ReadS(check, "color", a.ColorHex, () => VerifiedModelEdit.Hex(c.LineColor));
            if (a.Pattern != null) ReadS(check, "line_pattern", a.PatternName, () => PatternName(doc, c.GetLinePatternId(GraphicsStyleType.Projection)));
            if (a.Material != null) ReadS(check, "material", a.Material.Name, () => c.Material?.Name);
        }

        private static ModelEdit PlanSetStyle(Document doc, JObject r, out string error)
        {
            Category c = ResolveTarget(doc, r, out error);
            if (c == null) return null;
            StyleAsk a = ReadStyle(doc, r, out error);
            if (a == null) return null;
            if (!a.Any) { error = "set_object_style changes nothing: pass projection_weight, cut_weight, color, line_pattern and/or material."; return null; }
            if (a.Cut.HasValue && SafeBool(() => c.IsCuttable) != true)
            { error = "'" + c.Name + "' is not cuttable, so it has no cut line weight."; return null; }
            ElementId catId = c.Id;
            var edit = new ModelEdit { Subject = "category:" + Rid.Value(catId), Category = "object_style" };
            JObject before = StyleRow(doc, c);
            foreach (var p in before.Properties())
                if (p.Value.Type != JTokenType.Null && p.Value.Type != JTokenType.Object) edit.Before[p.Name] = p.Value.ToString();
            edit.Plan = new JObject { ["category_id"] = Rid.Value(catId), ["name"] = c.Name, ["set"] = Echo(r, a) };
            edit.Apply = d => ApplyStyle(Category.GetCategory(d, catId), a);
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(Required(a));
                Category now = Category.GetCategory(d, catId);
                CheckStyle(d, check, now, a);
                return check;
            };
            return edit;
        }

        private static ModelEdit PlanSubcategory(Document doc, JObject r, bool lineStyle, out string error)
        {
            error = null;
            string parentName = lineStyle ? "OST_Lines" : r.Value<string>("category");
            Category parent = ResolveCategory(doc, parentName, out error);
            if (parent == null) return null;
            string name = (r.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) { error = "name is required for the new subcategory."; return null; }
            if (parent.SubCategories.Cast<Category>().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            { error = "'" + parent.Name + "' already has a subcategory named '" + name + "'."; return null; }
            bool canAdd;
            try { canAdd = parent.CanAddSubcategory; } catch { canAdd = false; }
            if (!canAdd) { error = "Revit does not allow subcategories under '" + parent.Name + "'."; return null; }
            StyleAsk a = ReadStyle(doc, r, out error);
            if (a == null) return null;
            if (a.Cut.HasValue && SafeBool(() => parent.IsCuttable) != true)
            { error = "'" + parent.Name + "' is not cuttable, so its subcategories have no cut line weight."; return null; }
            ElementId parentId = parent.Id; ElementId created = null;
            var edit = new ModelEdit
            {
                Subject = "subcategory:" + Rid.Value(parentId) + "/" + name, Category = "subcategory", Action = PlannedAction.Create
            };
            edit.Before["parent"] = parent.Name;
            edit.Before["existing_subcategories"] = SafeCount(parent).ToString();
            edit.Plan = new JObject { ["parent_id"] = Rid.Value(parentId), ["parent"] = parent.Name, ["name"] = name, ["set"] = Echo(r, a) };
            edit.Apply = d =>
            {
                Category p = Category.GetCategory(d, parentId);
                Category sub = d.Settings.Categories.NewSubcategory(p, name);
                created = sub.Id;
                ApplyStyle(sub, a);
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(Required(a, "parent", "name"));
                Category sub = created == null ? null : Category.GetCategory(d, created);
                if (sub == null)
                {
                    check.Unreadable("parent", parentName, "the new subcategory does not re-read");
                    check.Unreadable("name", name, "the new subcategory does not re-read");
                    foreach (string f in Required(a)) check.Unreadable(f, null, "the new subcategory does not re-read");
                    return check;
                }
                ReadL(check, "parent", Rid.Value(parentId), () => Rid.Value(sub.Parent.Id));
                ReadS(check, "name", name, () => sub.Name);
                CheckStyle(d, check, sub, a);
                return check;
            };
            edit.Result = d =>
            {
                Category sub = created == null ? null : Category.GetCategory(d, created);
                return sub == null ? new JObject() : StyleRow(d, sub);
            };
            return edit;
        }

        private static ModelEdit PlanLinePattern(Document doc, JObject r, out string error)
        {
            error = null;
            string name = (r.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) { error = "name is required."; return null; }
            if (LinePatternElement.GetLinePatternElementByName(doc, name) != null) { error = "A line pattern named '" + name + "' already exists."; return null; }
            if (!Scale(r, out double scale, out error)) return null;
            JArray raw = r["segments"] as JArray;
            if (raw == null || raw.Count < 2 || raw.Count > 100) { error = "segments must hold 2..100 entries, e.g. [{\"type\":\"dash\",\"length\":6},{\"type\":\"space\",\"length\":3}]."; return null; }
            var segs = new List<LinePatternSegment>(); var wanted = new JArray();
            for (int i = 0; i < raw.Count; i++)
            {
                JObject s = raw[i] as JObject;
                string t = (s?.Value<string>("type") ?? "").ToLowerInvariant();
                LinePatternSegmentType type;
                if (t == "dash") type = LinePatternSegmentType.Dash;
                else if (t == "space") type = LinePatternSegmentType.Space;
                else if (t == "dot") type = LinePatternSegmentType.Dot;
                else { error = "segments[" + i + "].type must be dash, space or dot."; return null; }
                double len = type == LinePatternSegmentType.Dot ? 0 : (s.Value<double?>("length") ?? -1) * scale;
                if (type != LinePatternSegmentType.Dot && !(len > 0)) { error = "segments[" + i + "].length must be > 0."; return null; }
                segs.Add(new LinePatternSegment(type, len));
                wanted.Add(t + ":" + Math.Round(len * 304.8, 4));
            }
            ElementId created = null;
            var edit = new ModelEdit { Subject = "line_pattern:" + name, Category = "line_pattern", Action = PlannedAction.Create };
            edit.Before["name_free"] = "true";
            edit.Plan = new JObject { ["name"] = name, ["segments_mm"] = wanted };
            edit.Apply = d =>
            {
                var lp = new LinePattern(name);
                lp.SetSegments(segs);
                created = LinePatternElement.Create(d, lp).Id;
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("name", "segments");
                var e = created == null ? null : d.GetElement(created) as LinePatternElement;
                if (e == null) { check.Unreadable("name", name, "no element re-reads"); check.Unreadable("segments", wanted, "no element re-reads"); return check; }
                ReadS(check, "name", name, () => e.Name);
                try
                {
                    var found = new JArray(e.GetLinePattern().GetSegments().Select(s => s.Type.ToString().ToLowerInvariant() + ":" + Math.Round(s.Length * 304.8, 4)));
                    check.Record("segments", wanted, found, JToken.DeepEquals(wanted, found));
                }
                catch (Exception ex) { check.Unreadable("segments", wanted, ex.Message); }
                return check;
            };
            edit.Result = d => new JObject { ["line_pattern_id"] = created == null ? null : (JToken)Rid.Value(created) };
            return edit;
        }

        private static ModelEdit PlanFillPattern(Document doc, JObject r, out string error)
        {
            error = null;
            string name = (r.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) { error = "name is required."; return null; }
            string targetName = (r.Value<string>("target") ?? "drafting").ToLowerInvariant();
            FillPatternTarget target;
            if (targetName == "drafting") target = FillPatternTarget.Drafting;
            else if (targetName == "model") target = FillPatternTarget.Model;
            else { error = "target must be drafting or model."; return null; }
            if (FillPatternElement.GetFillPatternElementByName(doc, target, name) != null)
            { error = "A " + targetName + " fill pattern named '" + name + "' already exists."; return null; }
            string fill = (r.Value<string>("fill") ?? "hatch").ToLowerInvariant();
            if (fill != "solid" && fill != "hatch" && fill != "crosshatch") { error = "fill must be solid, hatch or crosshatch."; return null; }
            if (!Scale(r, out double scale, out error)) return null;
            double angle = (r.Value<double?>("angle") ?? 0) * Math.PI / 180;
            double s1 = (r.Value<double?>("spacing") ?? 0) * scale, s2 = (r.Value<double?>("spacing2") ?? r.Value<double?>("spacing") ?? 0) * scale;
            if (fill != "solid" && !(s1 > 0)) { error = "spacing must be > 0 for a " + fill + " pattern."; return null; }
            if (fill == "crosshatch" && !(s2 > 0)) { error = "spacing2 must be > 0."; return null; }
            int grids = fill == "solid" ? 0 : fill == "hatch" ? 1 : 2;
            ElementId created = null;
            var edit = new ModelEdit { Subject = "fill_pattern:" + targetName + ":" + name, Category = "fill_pattern", Action = PlannedAction.Create };
            edit.Before["name_free"] = "true";
            edit.Plan = new JObject { ["name"] = name, ["target"] = targetName, ["fill"] = fill, ["angle_deg"] = r.Value<double?>("angle") ?? 0,
                                      ["spacing_mm"] = Math.Round(s1 * 304.8, 4), ["spacing2_mm"] = fill == "crosshatch" ? (JToken)Math.Round(s2 * 304.8, 4) : null };
            edit.Apply = d =>
            {
                FillPattern p = fill == "solid" ? new FillPattern(name, target, FillPatternHostOrientation.ToView)
                              : fill == "hatch" ? new FillPattern(name, target, FillPatternHostOrientation.ToView, angle, s1)
                              : new FillPattern(name, target, FillPatternHostOrientation.ToView, angle, s1, s2);
                created = FillPatternElement.Create(d, p).Id;
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "name", "target", "solid", "grid_count" };
                if (grids > 0) { req.Add("angle"); req.Add("spacing"); }
                var check = new PostconditionCheck(req.ToArray());
                var e = created == null ? null : d.GetElement(created) as FillPatternElement;
                FillPattern p = null; try { p = e?.GetFillPattern(); } catch { }
                if (p == null) { foreach (string f in req) check.Unreadable(f, null, "no fill pattern re-reads"); return check; }
                ReadS(check, "name", name, () => e.Name);
                ReadS(check, "target", target.ToString(), () => p.Target.ToString());
                try { check.Compare("solid", fill == "solid", p.IsSolidFill); } catch (Exception ex) { check.Unreadable("solid", fill == "solid", ex.Message); }
                ReadL(check, "grid_count", grids, () => p.GetFillGrids().Count);
                if (grids > 0)
                {
                    try
                    {
                        FillGrid g = p.GetFillGrids()[0];
                        check.Measure("angle", angle, g.Angle, 1e-9, "rad", "FillGrid.Angle of grid 0");
                        check.Measure("spacing", s1, g.Offset, 1e-9, "ft", "FillGrid.Offset of grid 0");
                    }
                    catch (Exception ex) { check.Unreadable("angle", angle, ex.Message); check.Unreadable("spacing", s1, ex.Message); }
                }
                return check;
            };
            edit.Result = d => new JObject { ["fill_pattern_id"] = created == null ? null : (JToken)Rid.Value(created) };
            return edit;
        }

        // ================================================================== helpers

        /// <summary>A category (by OST_ name, id or name) or, with subcategory, one of its subcategories.</summary>
        private static Category ResolveTarget(Document doc, JObject r, out string error)
        {
            Category parent = ResolveCategory(doc, r.Value<string>("category"), out error);
            if (parent == null) return null;
            string sub = r.Value<string>("subcategory");
            if (string.IsNullOrWhiteSpace(sub)) return parent;
            var hits = parent.SubCategories.Cast<Category>().Where(s => string.Equals(s.Name, sub, StringComparison.Ordinal)).ToList();
            if (hits.Count == 1) return hits[0];
            error = "'" + parent.Name + "' has no subcategory named '" + sub + "' (operation=list_object_styles with category lists them).";
            return null;
        }

        private static Category ResolveCategory(Document doc, string key, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(key)) { error = "category is required (OST_ name, id or name)."; return null; }
            key = key.Trim();
            try
            {
                if (key.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(key, true, out BuiltInCategory bic))
                {
                    Category c = Category.GetCategory(doc, bic);
                    if (c != null) return c;
                }
                if (long.TryParse(key, out long id) && Rid.CanRepresent(id))
                {
                    Category c = Category.GetCategory(doc, Rid.Make(id));
                    if (c != null) return c;
                }
                var hits = doc.Settings.Categories.Cast<Category>().Where(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 1) return hits[0];
                error = hits.Count == 0 ? "No category matches '" + key + "'." : "'" + key + "' names " + hits.Count + " categories; pass its OST_ name or id.";
            }
            catch (Exception ex) { error = "category '" + key + "' could not be resolved: " + ex.Message; }
            return null;
        }

        private static JObject Echo(JObject r, StyleAsk a)
        {
            var o = new JObject();
            if (a.Projection.HasValue) o["projection_weight"] = a.Projection.Value;
            if (a.Cut.HasValue) o["cut_weight"] = a.Cut.Value;
            if (a.Color != null) o["color"] = a.ColorHex;
            if (a.Pattern != null) o["line_pattern"] = a.PatternName;
            if (a.Material != null) o["material"] = a.Material.Name;
            return o;
        }

        private static bool Scale(JObject r, out double scale, out string error)
        {
            error = null;
            string units = (r.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (DimensionPlanRules.UnitScale(units, out scale)) return true;
            error = "units must be mm, m or feet."; return false;
        }

        private static void Read(PostconditionCheck check, string what, long wanted, Func<int?> read)
        {
            try
            {
                int? v = read();
                if (v.HasValue) check.Compare(what, wanted, v.Value);
                else check.Unreadable(what, wanted, "Revit returned no value");
            }
            catch (Exception ex) { check.Unreadable(what, wanted, ex.Message); }
        }

        private static void ReadL(PostconditionCheck check, string what, long wanted, Func<long> read)
        {
            try { check.Compare(what, wanted, read()); }
            catch (Exception ex) { check.Unreadable(what, wanted, ex.Message); }
        }

        private static void ReadS(PostconditionCheck check, string what, string wanted, Func<string> read)
        {
            try { check.Compare(what, wanted, read()); }
            catch (Exception ex) { check.Unreadable(what, wanted, ex.Message); }
        }

        private static int? Weight(Category c, GraphicsStyleType t) { try { return c.GetLineWeight(t); } catch { return null; } }
        private static CategoryType? SafeType(Category c) { try { return c.CategoryType; } catch { return null; } }
        private static bool? SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
        private static Color SafeColor(Category c) { try { return c.LineColor; } catch { return null; } }
        private static ElementId SafeId(Func<ElementId> f) { try { return f(); } catch { return null; } }
        private static int SafeCount(Category c) { try { return c.SubCategories.Size; } catch { return -1; } }

        private static string PatternName(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            if (id == LinePatternElement.GetSolidPatternId()) return "Solid";
            return VerifiedModelEdit.Safe(() => doc.GetElement(id)?.Name);
        }
    }
}
