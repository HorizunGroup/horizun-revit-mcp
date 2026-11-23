// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_styles - colour fill schemes of rooms, areas and spaces: list,
// create (a copy of an existing scheme: Revit has no empty-scheme constructor),
// edit (parameter, entries with colours, ranges, legend title), apply to a view
// and place the colour legend. Every write goes through VerifiedModelEdit like
// the rest of the tool: rehearsed in a rolled-back transaction, applied inside a
// TransactionGroup, and every requested value RE-READ from the committed model.
//
// STATED LIMITS (paper, not yet measured in Revit):
//   * numeric entry values are converted from value_units to Revit internal units (feet, square feet): the
//     scheme stores the raw parameter value, and so does this tool;
//   * Revit keeps an entry that elements still use when it is left out of
//     entries; the reply lists those as extra_entries_kept instead of failing;
//   * IsByRange is switched before the entries are written, as the API requires
//     entries Revit can read as ranges; a refusal is Revit's own sentence.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class ColorSchemeEdits
    {
        // =====================================================================
        // Reads
        // =====================================================================

        internal static CommandResult List(Document doc, JObject r)
        {
            ElementId categoryId = null;
            if (r["category"] != null)
            {
                string error;
                categoryId = CategoryOf(doc, r.Value<string>("category"), out error);
                if (categoryId == null) return CommandResult.Fail(error);
            }
            long? only = r.Value<long?>("scheme_id");
            var rows = new JArray();
            foreach (ColorFillScheme scheme in Schemes(doc))
            {
                if (categoryId != null && Rid.Value(scheme.CategoryId) != Rid.Value(categoryId)) continue;
                if (only != null && Rid.Value(scheme.Id) != only.Value) continue;
                JObject row = Row(doc, scheme);
                if (only != null) row["supported_parameters"] = SupportedParameters(doc, scheme);
                rows.Add(row);
            }
            return VerifiedModelEdit.ReadReply(new JObject
            {
                ["document"] = doc.Title, ["count"] = rows.Count, ["color_schemes"] = rows,
                ["note"] = "Numeric entry values on a length/area scheme need value_units (mm, m, feet, m2, ft2 or internal); they are stored in Revit internal units (feet, square feet). Pass scheme_id to also list the parameters a scheme can colour by."
            });
        }

        // =====================================================================
        // Writes
        // =====================================================================

        internal static ModelEdit Plan(Document doc, JObject r, string op, out string error)
        {
            error = null;
            switch (op)
            {
                case ColorSchemeRules.OpCreate: return PlanCreate(doc, r, out error);
                case ColorSchemeRules.OpEdit: return PlanEdit(doc, r, out error);
                case ColorSchemeRules.OpApply: return PlanApply(doc, r, out error);
                default: return PlanLegend(doc, r, out error);
            }
        }

        // ---- create ---------------------------------------------------------------

        private static ModelEdit PlanCreate(Document doc, JObject r, out string error)
        {
            List<ColorEntrySpec> entries;
            error = ColorSchemeRules.ValidateSettings(r, true, out entries);
            if (error != null) return null;
            string name = (r.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) { error = "name is required for the new scheme."; return null; }
            ColorFillScheme source = SourceScheme(doc, r, out error);
            if (source == null) return null;
            if (!source.IsValidSchemeName(name))
            { error = "'" + name + "' is not a valid name for a new '" + CategoryName(doc, source.CategoryId) + "' scheme (it may already exist)."; return null; }
            ElementId parameterId; error = ResolveParameter(doc, source, r.Value<string>("parameter"), out parameterId);
            if (error != null) return null;
            ElementId sourceId = source.Id, created = null;
            long sourceCategory = Rid.Value(source.CategoryId);
            var edit = new ModelEdit { Subject = "color_scheme:new:" + name, Category = "color_scheme", Action = PlannedAction.Create };
            edit.Before["source"] = Rid.Value(sourceId).ToString(CultureInfo.InvariantCulture);
            edit.Before["source_state"] = Fingerprint(doc, source);
            edit.Plan = new JObject
            {
                ["source_scheme_id"] = Rid.Value(sourceId), ["name"] = name,
                ["category"] = CategoryName(doc, source.CategoryId), ["settings"] = Echo(r, entries)
            };
            edit.Apply = d =>
            {
                var src = (ColorFillScheme)d.GetElement(sourceId);
                created = src.Duplicate(name);
                ApplySettings(d, (ColorFillScheme)d.GetElement(created), r, parameterId, entries);
            };
            edit.Verify = d =>
            {
                ColorFillScheme scheme = created == null ? null : d.GetElement(created) as ColorFillScheme;
                var check = new PostconditionCheck(RequiredFor(r, true));
                if (scheme == null)
                {
                    foreach (string f in RequiredFor(r, true)) check.Unreadable(f, null, "the new scheme does not re-read");
                    return check;
                }
                Compare(check, "name", name, () => scheme.Name);
                Compare(check, "category", sourceCategory, () => Rid.Value(scheme.CategoryId));
                CheckSettings(d, check, scheme, r, parameterId, entries);
                return check;
            };
            edit.Result = d =>
            {
                ColorFillScheme scheme = created == null ? null : d.GetElement(created) as ColorFillScheme;
                JObject result = scheme == null ? new JObject() : Row(d, scheme);
                if (scheme != null && entries != null) result["extra_entries_kept"] = ExtraEntries(d, scheme, entries);
                return result;
            };
            return edit;
        }

        // ---- edit -----------------------------------------------------------------

        private static ModelEdit PlanEdit(Document doc, JObject r, out string error)
        {
            List<ColorEntrySpec> entries;
            error = ColorSchemeRules.ValidateSettings(r, false, out entries);
            if (error != null) return null;
            ColorFillScheme scheme = NeedScheme(doc, r, out error);
            if (scheme == null) return null;
            ElementId parameterId; error = ResolveParameter(doc, scheme, r.Value<string>("parameter"), out parameterId);
            if (error != null) return null;
            ElementId id = scheme.Id;
            var edit = new ModelEdit { Subject = "color_scheme:" + Rid.Value(id), Category = "color_scheme" };
            edit.Before["scheme"] = Rid.Value(id).ToString(CultureInfo.InvariantCulture);
            edit.Before["state"] = Fingerprint(doc, scheme);
            edit.Plan = new JObject
            {
                ["scheme_id"] = Rid.Value(id), ["name"] = SafeName(scheme), ["category"] = CategoryName(doc, scheme.CategoryId),
                ["settings"] = Echo(r, entries)
            };
            edit.Apply = d => ApplySettings(d, (ColorFillScheme)d.GetElement(id), r, parameterId, entries);
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(RequiredFor(r, false));
                var now = d.GetElement(id) as ColorFillScheme;
                if (now == null)
                {
                    foreach (string f in RequiredFor(r, false)) check.Unreadable(f, null, "the scheme does not re-read");
                    return check;
                }
                CheckSettings(d, check, now, r, parameterId, entries);
                return check;
            };
            edit.Result = d =>
            {
                var now = d.GetElement(id) as ColorFillScheme;
                JObject result = now == null ? new JObject() : Row(d, now);
                if (now != null && entries != null) result["extra_entries_kept"] = ExtraEntries(d, now, entries);
                return result;
            };
            return edit;
        }

        private static string[] RequiredFor(JObject r, bool create)
        {
            var names = new List<string>();
            if (create) { names.Add("name"); names.Add("category"); }
            if (r["title"] != null) names.Add("title");
            if (r["parameter"] != null) names.Add("parameter");
            if (r["by_range"] != null) names.Add("by_range");
            if (r["include_links"] != null) names.Add("include_links");
            if (r["entries"] != null) names.Add("entries");
            return names.ToArray();
        }

        // ---- apply to a view ---------------------------------------------------------

        private static ModelEdit PlanApply(Document doc, JObject r, out string error)
        {
            error = ColorSchemeRules.ValidateApply(r);
            if (error != null) return null;
            View view = NeedView(doc, r, out error);
            if (view == null) return null;
            ColorFillScheme scheme = null;
            ElementId categoryId;
            if (r["scheme_id"] != null)
            {
                scheme = NeedScheme(doc, r, out error);
                if (scheme == null) return null;
                categoryId = scheme.CategoryId;
            }
            else
            {
                categoryId = CategoryOf(doc, r.Value<string>("category"), out error);
                if (categoryId == null) return null;
            }
            ElementId schemeId = scheme == null ? ElementId.InvalidElementId : scheme.Id;
            if (!view.CanApplyColorFillScheme(categoryId, schemeId))
            {
                error = scheme == null
                    ? "view '" + view.Name + "' cannot take a colour scheme for " + CategoryName(doc, categoryId) + " (Revit's own answer)."
                    : "Revit refuses scheme '" + SafeName(scheme) + "' for view '" + view.Name + "': the view type does not take " +
                      CategoryName(doc, categoryId) + " schemes, or (area plans) the scheme belongs to another area scheme.";
                return null;
            }
            ElementId viewId = view.Id;
            var edit = new ModelEdit { Subject = "color_scheme_view:" + Rid.Value(viewId) + ":" + Rid.Value(categoryId), Category = "color_scheme_view" };
            ElementId current = AppliedScheme(view, categoryId);
            edit.Before["view"] = Rid.Value(viewId).ToString(CultureInfo.InvariantCulture);
            edit.Before["current_scheme_id"] = Rid.Value(current).ToString(CultureInfo.InvariantCulture);
            edit.Plan = new JObject
            {
                ["view_id"] = Rid.Value(viewId), ["view"] = view.Name, ["category"] = CategoryName(doc, categoryId),
                ["scheme_id"] = scheme == null ? null : (JToken)Rid.Value(schemeId), ["clears_scheme"] = scheme == null
            };
            edit.Apply = d => ((View)d.GetElement(viewId)).SetColorFillSchemeId(categoryId, schemeId);
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("scheme");
                Compare(check, "scheme", Rid.Value(schemeId), () => Rid.Value(((View)d.GetElement(viewId)).GetColorFillSchemeId(categoryId)));
                return check;
            };
            return edit;
        }

        // ---- legend -------------------------------------------------------------------

        private static ModelEdit PlanLegend(Document doc, JObject r, out string error)
        {
            error = ColorSchemeRules.ValidateLegend(r);
            if (error != null) return null;
            View view = NeedView(doc, r, out error);
            if (view == null) return null;
            ColorFillScheme scheme = null;
            ElementId categoryId;
            if (r["scheme_id"] != null)
            {
                scheme = NeedScheme(doc, r, out error);
                if (scheme == null) return null;
                categoryId = scheme.CategoryId;
            }
            else
            {
                categoryId = CategoryOf(doc, r.Value<string>("category"), out error);
                if (categoryId == null) return null;
            }
            ElementId applied = AppliedScheme(view, categoryId);
            ElementId schemeId = scheme != null ? scheme.Id : applied;
            if (schemeId == null || schemeId == ElementId.InvalidElementId)
            {
                error = "view '" + view.Name + "' has no " + CategoryName(doc, categoryId) + " colour scheme, so a legend would explain nothing. " +
                        "Pass scheme_id to apply one in the same call, or run apply_color_scheme first.";
                return null;
            }
            bool needsApply = Rid.Value(applied) != Rid.Value(schemeId);
            if (needsApply && !view.CanApplyColorFillScheme(categoryId, schemeId))
            { error = "Revit refuses that scheme for view '" + view.Name + "', so the legend cannot be placed there."; return null; }
            foreach (Element existing in new FilteredElementCollector(doc, view.Id).OfClass(typeof(ColorFillLegend)))
            {
                ColorFillLegend legend = existing as ColorFillLegend;
                if (legend != null && Rid.Value(legend.ColorFillCategoryId) == Rid.Value(categoryId))
                {
                    error = "view '" + view.Name + "' already has a colour legend for " + CategoryName(doc, categoryId) +
                            " (id " + Rid.Value(existing.Id) + "); one view takes one. Delete it with horizun_delete_verified to replace it.";
                    return null;
                }
            }
            double scale;
            if (!Scale(r, out scale, out error)) return null;
            JArray p = (JArray)r["point"];
            var origin = new XYZ(p[0].Value<double>() * scale, p[1].Value<double>() * scale, (p.Count > 2 ? p[2].Value<double>() : 0) * scale);
            // ColorFillLegend.Create needs the origin ON the view plane (live 5.2, Revit 2025: point [0,0] on a plan whose
            // level is not at z=0 was refused "The origin is not on the view plane"). The point is projected onto that plane
            // along the view direction, which keeps the x/y a plan caller gave, and the plan says when it moved.
            XYZ requested = origin;
            try
            {
                XYZ n = view.ViewDirection.Normalize();
                // In a section or elevation the view plane is vertical: a point without z would land at z=0, likely outside
                // the crop, and the legend would verify while nobody can see it. Ask for the third coordinate there.
                if (p.Count < 3 && Math.Abs(n.Z) < 0.99)
                {
                    error = "place_color_legend: view '" + view.Name + "' is not a plan, so point needs three coordinates [x, y, z] " +
                            "(a point [x, y] would be placed at z=0, likely outside what the view shows). Nothing was written.";
                    return null;
                }
                origin = origin - n.Multiply((origin - view.Origin).DotProduct(n));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { origin = requested; }
            bool projected = !origin.IsAlmostEqualTo(requested);
            ElementId viewId = view.Id, legendId = null;
            var edit = new ModelEdit { Subject = "color_legend:" + Rid.Value(viewId) + ":" + Rid.Value(categoryId), Category = "color_legend", Action = PlannedAction.Create };
            edit.Before["view"] = Rid.Value(viewId).ToString(CultureInfo.InvariantCulture);
            edit.Before["applied_scheme_id"] = Rid.Value(applied).ToString(CultureInfo.InvariantCulture);
            edit.Plan = new JObject
            {
                ["view_id"] = Rid.Value(viewId), ["view"] = view.Name, ["category"] = CategoryName(doc, categoryId),
                ["scheme_id"] = Rid.Value(schemeId), ["applies_scheme_first"] = needsApply,
                ["origin_mm"] = new JArray(Math.Round(origin.X * 304.8, 3), Math.Round(origin.Y * 304.8, 3), Math.Round(origin.Z * 304.8, 3)),
                ["origin_projected_to_view_plane"] = projected
            };
            edit.Apply = d =>
            {
                var v = (View)d.GetElement(viewId);
                if (needsApply) v.SetColorFillSchemeId(categoryId, schemeId);
                legendId = ColorFillLegend.Create(d, viewId, categoryId, origin).Id;
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("legend", "view", "category", "origin", "scheme");
                var legend = legendId == null ? null : d.GetElement(legendId) as ColorFillLegend;
                if (legend == null)
                {
                    foreach (string f in new[] { "legend", "view", "category", "origin", "scheme" }) check.Unreadable(f, null, "the legend does not re-read");
                    return check;
                }
                check.Compare("legend", true, true);
                Compare(check, "view", Rid.Value(viewId), () => Rid.Value(legend.OwnerViewId));
                Compare(check, "category", Rid.Value(categoryId), () => Rid.Value(legend.ColorFillCategoryId));
                try
                {
                    XYZ got = legend.Origin;
                    check.Measure("origin", 0.0, got.DistanceTo(origin), 1e-6, "ft", "distance between the requested and the read origin");
                }
                catch (Exception ex) { check.Unreadable("origin", null, ex.Message); }
                Compare(check, "scheme", Rid.Value(schemeId), () => Rid.Value(((View)d.GetElement(viewId)).GetColorFillSchemeId(categoryId)));
                return check;
            };
            edit.Result = d => new JObject { ["legend_id"] = legendId == null ? null : (JToken)Rid.Value(legendId) };
            return edit;
        }

        // =====================================================================
        // Writing a scheme
        // =====================================================================

        private static void ApplySettings(Document doc, ColorFillScheme scheme, JObject r, ElementId parameterId,
                                          List<ColorEntrySpec> entries)
        {
            if (r["title"] != null) scheme.Title = r.Value<string>("title");
            if (r["include_links"] != null) scheme.IsLinkedFilesIncluded = r.Value<bool>("include_links");
            // Changing the parameter removes every entry (and may clear by_range): it goes first.
            if (parameterId != null && Rid.Value(scheme.ParameterDefinition) != Rid.Value(parameterId))
                scheme.ParameterDefinition = parameterId;
            if (r["by_range"] != null && scheme.IsByRange != r.Value<bool>("by_range"))
                scheme.IsByRange = r.Value<bool>("by_range");
            if (entries == null) return;

            if (scheme.StorageType == StorageType.Double)
            {
                string unitsError = ColorSchemeRules.NumericToInternal(entries, r.Value<string>("value_units"));
                if (unitsError != null) throw new ArgumentException(unitsError);
            }
            var built = new List<ColorFillSchemeEntry>();
            IList<ColorFillSchemeEntry> existing = scheme.GetEntries();
            ElementId solid = SolidDraftingPattern(doc);
            foreach (ColorEntrySpec spec in entries)
            {
                ColorFillSchemeEntry entry = new ColorFillSchemeEntry(scheme.StorageType);
                SetValue(entry, scheme.StorageType, spec.Value);
                ColorFillSchemeEntry old = existing.FirstOrDefault(e => ValueKey(e) == spec.Key);
                ElementId pattern;
                string patternError = ResolvePattern(doc, spec.Pattern, old != null ? old.FillPatternId : solid, out pattern);
                if (patternError != null) throw new ArgumentException(patternError);
                entry.FillPatternId = pattern;
                entry.Color = ToColor(spec.Color);
                if (spec.Visible != null) entry.IsVisible = spec.Visible.Value;
                if (spec.Caption != null)
                {
                    if (!scheme.IsByRange)
                        throw new ArgumentException("caption only means something in a by_range scheme (it is the legend text of a range).");
                    entry.Caption = spec.Caption;
                }
                built.Add(entry);
            }
            scheme.SetEntries(built);
        }

        private static void SetValue(ColorFillSchemeEntry entry, StorageType storage, JToken value)
        {
            switch (storage)
            {
                case StorageType.String:
                    entry.SetStringValue(value.Type == JTokenType.String ? value.Value<string>() : value.ToString());
                    break;
                case StorageType.Integer:
                {
                    if (value.Type != JTokenType.Integer && !(value.Type == JTokenType.Float && value.Value<double>() == Math.Floor(value.Value<double>())))
                        throw new ArgumentException("this scheme colours an integer parameter; entry value '" + value + "' is not a whole number.");
                    entry.SetIntegerValue(value.Value<int>());
                    break;
                }
                case StorageType.Double:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                        throw new ArgumentException("this scheme colours a numeric parameter; entry value '" + value + "' is not a number.");
                    entry.SetDoubleValue(value.Value<double>());
                    break;
                case StorageType.ElementId:
                {
                    if (value.Type != JTokenType.Integer || !Rid.CanRepresent(value.Value<long>()))
                        throw new ArgumentException("this scheme colours an element parameter; entry value '" + value + "' must be an element id.");
                    entry.SetElementIdValue(Rid.Make(value.Value<long>()));
                    break;
                }
                default:
                    throw new ArgumentException("this scheme's parameter has a storage type (" + storage + ") the tool cannot write entries for.");
            }
        }

        private static string ValueKey(ColorFillSchemeEntry entry)
        {
            try
            {
                switch (entry.StorageType)
                {
                    case StorageType.String: return "s:" + entry.GetStringValue();
                    case StorageType.Integer: return "n:" + entry.GetIntegerValue().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double: return ColorSchemeRules.ValueKey(new JValue(entry.GetDoubleValue()));
                    case StorageType.ElementId: return "n:" + Rid.Value(entry.GetElementIdValue()).ToString(CultureInfo.InvariantCulture);
                    default: return null;
                }
            }
            catch { return null; }
        }

        private static JToken ValueToken(ColorFillSchemeEntry entry)
        {
            try
            {
                switch (entry.StorageType)
                {
                    case StorageType.String: return entry.GetStringValue();
                    case StorageType.Integer: return entry.GetIntegerValue();
                    case StorageType.Double: return entry.GetDoubleValue();
                    case StorageType.ElementId: return Rid.Value(entry.GetElementIdValue());
                    default: return null;
                }
            }
            catch { return null; }
        }

        private static string ResolvePattern(Document doc, JToken wanted, ElementId fallback, out ElementId id)
        {
            id = fallback;
            if (wanted == null) return null;
            if (wanted.Type == JTokenType.Integer)
            {
                long number = wanted.Value<long>();
                var byId = Rid.CanRepresent(number) ? doc.GetElement(Rid.Make(number)) as FillPatternElement : null;
                if (byId == null) return "fill pattern id " + number + " is not a fill pattern.";
                id = byId.Id;
            }
            else
            {
                string name = wanted.Value<string>();
                FillPatternElement byName = FillPatternElement.GetFillPatternElementByName(doc, FillPatternTarget.Drafting, name);
                if (byName == null)
                    return "drafting fill pattern '" + name + "' is not in the document (horizun_manage_styles operation=list_fill_patterns lists them; a colour scheme takes drafting patterns).";
                id = byName.Id;
            }
            return null;
        }

        private static ElementId SolidDraftingPattern(Document doc)
        {
            foreach (FillPatternElement e in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
                try
                {
                    FillPattern p = e.GetFillPattern();
                    if (p.IsSolidFill && p.Target == FillPatternTarget.Drafting) return e.Id;
                }
                catch { }
            return ElementId.InvalidElementId;
        }

        private static Color ToColor(string hex)
        {
            byte r, g, b;
            ScheduleAppearanceRules.TryParseColor(hex, out r, out g, out b);
            return new Color(r, g, b);
        }

        // =====================================================================
        // Re-reading a scheme
        // =====================================================================

        private static void CheckSettings(Document doc, PostconditionCheck check, ColorFillScheme scheme, JObject r,
                                          ElementId parameterId, List<ColorEntrySpec> entries)
        {
            if (r["title"] != null) Compare(check, "title", r.Value<string>("title"), () => scheme.Title);
            if (r["parameter"] != null)
                Compare(check, "parameter", Rid.Value(parameterId), () => Rid.Value(scheme.ParameterDefinition));
            if (r["by_range"] != null) Compare(check, "by_range", r.Value<bool>("by_range"), () => scheme.IsByRange);
            if (r["include_links"] != null) Compare(check, "include_links", r.Value<bool>("include_links"), () => scheme.IsLinkedFilesIncluded);
            if (entries == null) return;
            try
            {
                IList<ColorFillSchemeEntry> got = scheme.GetEntries();
                var problems = new JArray();
                foreach (ColorEntrySpec spec in entries)
                {
                    ColorFillSchemeEntry entry = got.FirstOrDefault(e => ValueKey(e) == spec.Key);
                    if (entry == null) { problems.Add("value " + spec.Value + " is not in the scheme"); continue; }
                    string hex = null;
                    try { hex = ScheduleAppearanceRules.Hex(entry.Color.Red, entry.Color.Green, entry.Color.Blue); } catch { }
                    if (!string.Equals(hex, spec.Color, StringComparison.OrdinalIgnoreCase))
                        problems.Add("value " + spec.Value + " has colour " + (hex ?? "unreadable") + ", asked " + spec.Color);
                    if (spec.Visible != null && entry.IsVisible != spec.Visible.Value)
                        problems.Add("value " + spec.Value + " visibility did not stick");
                    if (spec.Caption != null && !string.Equals(entry.Caption, spec.Caption, StringComparison.Ordinal))
                        problems.Add("value " + spec.Value + " caption did not stick");
                    if (spec.Pattern != null)
                    {
                        ElementId wanted;
                        string patternError = ResolvePattern(doc, spec.Pattern, ElementId.InvalidElementId, out wanted);
                        if (patternError != null || Rid.Value(entry.FillPatternId) != Rid.Value(wanted))
                            problems.Add("value " + spec.Value + " fill pattern did not stick");
                    }
                }
                check.Record("entries", new JValue(entries.Count + " entries"),
                             new JValue(problems.Count == 0 ? "all present" : string.Join("; ", problems.Select(p => (string)p))),
                             problems.Count == 0);
            }
            catch (Exception ex) { check.Unreadable("entries", null, ex.Message); }
        }

        /// <summary>Entries the scheme holds that the request did not name: Revit keeps those that elements use.</summary>
        private static JArray ExtraEntries(Document doc, ColorFillScheme scheme, List<ColorEntrySpec> requested)
        {
            var keys = new HashSet<string>(requested.Select(s => s.Key), StringComparer.Ordinal);
            var extra = new JArray();
            foreach (ColorFillSchemeEntry e in scheme.GetEntries())
                if (!keys.Contains(ValueKey(e)))
                    extra.Add(new JObject { ["value"] = ValueToken(e), ["in_use"] = SafeBool(() => e.IsInUse) });
            return extra;
        }

        internal static JObject Row(Document doc, ColorFillScheme scheme)
        {
            var row = new JObject
            {
                ["id"] = Rid.Value(scheme.Id), ["name"] = SafeName(scheme),
                ["title"] = VerifiedModelEdit.Safe(() => scheme.Title),
                ["category"] = CategoryName(doc, scheme.CategoryId),
                ["category_id"] = Rid.Value(scheme.CategoryId),
                ["by_range"] = SafeBool(() => scheme.IsByRange),
                ["include_links"] = SafeBool(() => scheme.IsLinkedFilesIncluded),
                ["storage"] = VerifiedModelEdit.Safe(() => scheme.StorageType.ToString())
            };
            try
            {
                ElementId parameter = scheme.ParameterDefinition;
                row["parameter"] = new JObject { ["id"] = Rid.Value(parameter), ["name"] = ParameterName(doc, parameter) };
            }
            catch { row["parameter"] = null; }
            try
            {
                if (Rid.Value(scheme.CategoryId) == (long)BuiltInCategory.OST_Areas) row["area_scheme_id"] = Rid.Value(scheme.AreaSchemeId);
            }
            catch { }
            var entries = new JArray();
            try
            {
                foreach (ColorFillSchemeEntry e in scheme.GetEntries())
                {
                    string hex = null;
                    try { hex = ScheduleAppearanceRules.Hex(e.Color.Red, e.Color.Green, e.Color.Blue); } catch { }
                    var item = new JObject
                    {
                        ["value"] = ValueToken(e), ["color"] = hex, ["visible"] = SafeBool(() => e.IsVisible),
                        ["in_use"] = SafeBool(() => e.IsInUse), ["pattern_id"] = SafeId(() => e.FillPatternId)
                    };
                    string caption = VerifiedModelEdit.Safe(() => e.Caption);
                    if (!string.IsNullOrEmpty(caption)) item["caption"] = caption;
                    entries.Add(item);
                }
            }
            catch (Exception ex) { entries.Add(new JObject { ["error"] = ex.Message }); }
            row["entries"] = entries;
            return row;
        }

        private static string Fingerprint(Document doc, ColorFillScheme scheme)
        {
            JObject row = Row(doc, scheme);
            return RequestFingerprint.Sha256Hex(row.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static JObject Echo(JObject r, List<ColorEntrySpec> entries)
        {
            var o = new JObject();
            foreach (string key in new[] { "title", "parameter", "by_range", "include_links" })
                if (r[key] != null) o[key] = r[key].DeepClone();
            if (entries != null)
                o["entries"] = new JArray(entries.Select(e => new JObject { ["value"] = e.Value.DeepClone(), ["color"] = e.Color }));
            return o;
        }

        // =====================================================================
        // Resolution
        // =====================================================================

        /// <summary>The scheme a view has for a category, or InvalidElementId when the view cannot have one.</summary>
        private static ElementId AppliedScheme(View view, ElementId categoryId)
        {
            try { return view.GetColorFillSchemeId(categoryId) ?? ElementId.InvalidElementId; }
            catch { return ElementId.InvalidElementId; }
        }

        private static IEnumerable<ColorFillScheme> Schemes(Document doc)
            => new FilteredElementCollector(doc).OfClass(typeof(ColorFillScheme)).Cast<ColorFillScheme>().OrderBy(s => Rid.Value(s.Id));

        private static ColorFillScheme NeedScheme(Document doc, JObject r, out string error)
        {
            error = null;
            long id = r.Value<long?>("scheme_id") ?? -1;
            var scheme = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as ColorFillScheme : null;
            if (scheme == null)
                error = "scheme_id " + id + " is not a colour fill scheme (operation=list_color_schemes lists them).";
            return scheme;
        }

        private static View NeedView(Document doc, JObject r, out string error)
        {
            error = null;
            long id = r.Value<long?>("view_id") ?? -1;
            var view = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as View : null;
            if (view == null || view.IsTemplate) error = "view_id " + id + " is not a view (a plan, an area plan or a legend; not a view template).";
            return view;
        }

        /// <summary>The scheme a create copies: the one named, else the first of the category.</summary>
        private static ColorFillScheme SourceScheme(Document doc, JObject r, out string error)
        {
            error = null;
            if (r["scheme_id"] != null) return NeedScheme(doc, r, out error);
            if (r["category"] == null)
            { error = "create_color_scheme copies an existing scheme: pass scheme_id (the one to copy) or category (rooms, areas or spaces)."; return null; }
            ElementId categoryId = CategoryOf(doc, r.Value<string>("category"), out error);
            if (categoryId == null) return null;
            ColorFillScheme source = Schemes(doc).FirstOrDefault(s => Rid.Value(s.CategoryId) == Rid.Value(categoryId));
            if (source == null)
                error = "the document has no colour scheme for " + CategoryName(doc, categoryId) + " to copy; create one once in Revit (Architecture > Room & Area > Color Scheme).";
            return source;
        }

        private static ElementId CategoryOf(Document doc, string text, out string error)
        {
            error = null;
            string alias = ColorSchemeRules.CategoryAlias(text);
            string key = alias ?? text;
            Category category = ManageStylesCommand.ResolveCategoryPublic(doc, key, out error);
            return category == null ? null : category.Id;
        }

        private static string CategoryName(Document doc, ElementId id)
        {
            try { return Category.GetCategory(doc, id)?.Name; } catch { return null; }
        }

        private static string ResolveParameter(Document doc, ColorFillScheme scheme, string text, out ElementId id)
        {
            id = null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            IList<ElementId> supported = scheme.GetSupportedParameterIds();
            long number;
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                id = supported.FirstOrDefault(p => Rid.Value(p) == number);
                if (id != null) return null;
            }
            else
            {
                var hits = supported.Where(p => string.Equals(ParameterName(doc, p), text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 1) { id = hits[0]; return null; }
                if (hits.Count > 1)
                    return "parameter '" + text + "' names " + hits.Count + " parameters (" +
                           string.Join(", ", hits.Select(p => Rid.Value(p).ToString(CultureInfo.InvariantCulture))) + "); pass its id.";
            }
            return "parameter '" + text + "' cannot colour this scheme. It can colour by: " +
                   string.Join("; ", supported.Take(40).Select(p => ParameterName(doc, p) + " (" + Rid.Value(p) + ")")) +
                   (supported.Count > 40 ? "; ... (" + supported.Count + " in all, operation=list_color_schemes with scheme_id lists them)" : "") + ".";
        }

        private static JArray SupportedParameters(Document doc, ColorFillScheme scheme)
        {
            var list = new JArray();
            try
            {
                foreach (ElementId p in scheme.GetSupportedParameterIds())
                    list.Add(new JObject { ["id"] = Rid.Value(p), ["name"] = ParameterName(doc, p) });
            }
            catch { }
            return list;
        }

        private static string ParameterName(Document doc, ElementId id)
        {
            try
            {
                long v = Rid.Value(id);
                if (v < 0 && Enum.IsDefined(typeof(BuiltInParameter), (BuiltInParameter)v))
                    return LabelUtils.GetLabelFor((BuiltInParameter)v);
                return doc.GetElement(id)?.Name;
            }
            catch { return null; }
        }

        private static bool Scale(JObject r, out double scale, out string error)
        {
            error = null;
            string units = (r.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (DimensionPlanRules.UnitScale(units, out scale)) return true;
            error = "units must be mm, m or feet."; return false;
        }

        // ---- small safe readers ---------------------------------------------------------

        private static string SafeName(Element e) { try { return e.Name; } catch { return null; } }
        private static bool? SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
        private static JToken SafeId(Func<ElementId> f) { try { return Rid.Value(f()); } catch { return null; } }

        private static void Compare(PostconditionCheck check, string what, string wanted, Func<string> read)
        {
            try { check.Compare(what, wanted, read()); }
            catch (Exception ex) { check.Unreadable(what, wanted, ex.Message); }
        }

        private static void Compare(PostconditionCheck check, string what, long wanted, Func<long> read)
        {
            try { check.Compare(what, wanted, read()); }
            catch (Exception ex) { check.Unreadable(what, wanted, ex.Message); }
        }

        private static void Compare(PostconditionCheck check, string what, bool wanted, Func<bool> read)
        {
            try { check.Compare(what, wanted, read()); }
            catch (Exception ex) { check.Unreadable(what, wanted, ex.Message); }
        }
    }
}
