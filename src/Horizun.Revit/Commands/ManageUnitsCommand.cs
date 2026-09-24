// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_units - project units, Project Information and the two base
// points, read and written the verified way (ForgeTypeId API, Revit 2023-2027).
//
//   read                 Units: per spec the unit, accuracy, symbol, zero/space
//                        suppression, digit grouping, plus the document's decimal
//                        and grouping symbols.
//   set                  FormatOptions for one spec (unit, accuracy, symbol, ...)
//                        and/or the decimal/grouping symbols; re-read from
//                        doc.GetUnits() after the commit.
//   project_information  values omitted: every Project Information parameter.
//                        values given: text/integer parameters written by name,
//                        each re-read after the commit.
//   base_points          project_position omitted: Project Base Point and Survey
//                        Point positions (internal and shared) and the angle to
//                        true north. project_position given: the SHARED
//                        COORDINATES of the project base point are re-specified
//                        (ProjectLocation.SetProjectPosition) - this re-positions
//                        the whole model against every linked model and survey
//                        that relies on shared coordinates, so it additionally
//                        requires confirm_shared_coordinates=true.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ManageUnitsCommand : ICommand
    {
        public const string Tool = "horizun_manage_units";
        public string Name => "horizun_manage_units";
        public string Description => "Project units, Project Information and base points: read and verified write.";

        private const string Known = "read, set, project_information, base_points";

        /// <summary>The specs `read` reports when none are named.</summary>
        private static readonly string[] DefaultSpecs =
        {
            "autodesk.spec.aec:length", "autodesk.spec.aec:area", "autodesk.spec.aec:volume",
            "autodesk.spec.aec:angle", "autodesk.spec.aec:slope",
            "autodesk.spec.aec.hvac:airFlow", "autodesk.spec.aec.piping:flow",
            "autodesk.spec.aec.electrical:current", "autodesk.spec.aec.electrical:electricalPotential",
            "autodesk.spec.aec.electrical:electricalPower"
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request = VerifiedModelEdit.Parse(paramsJson, out CommandResult parseError);
            if (request == null) return parseError;
            string op = VerifiedModelEdit.Operation(request);
            if (op != "read" && op != "set" && op != "project_information" && op != "base_points")
                return VerifiedModelEdit.UnknownOperation(Tool, op, Known);

            bool write = op == "set" ||
                         (op == "project_information" && request["values"] is JObject) ||
                         (op == "base_points" && request["project_position"] is JObject);
            if (!write)
            {
                Document doc = app.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("No document is open.");
                CommandResult guard = DocumentGate.ReadGuard(doc, request, Tool);
                if (guard != null) return guard;
                if (op == "read") return ReadUnits(doc, request);
                if (op == "project_information") return VerifiedModelEdit.ReadReply(ReadProjectInfo(doc));
                return VerifiedModelEdit.ReadReply(ReadBasePoints(doc));
            }

            GateResult gate = DocumentGate.ForMutation(app, request, Tool);
            if (!gate.Ok) return gate.Refusal;
            string error;
            ModelEdit edit = op == "set" ? PlanSet(gate.Document, request, out error)
                           : op == "project_information" ? PlanProjectInfo(gate.Document, request, out error)
                           : PlanBasePoint(gate.Document, request, out error);
            if (edit == null) return CommandResult.Fail(error + " Nothing was written.");
            edit.Tool = Tool; edit.Operation = op;
            return VerifiedModelEdit.Run(app, gate, request, edit,
                "spec", "unit", "accuracy", "symbol", "suppress_trailing_zeros", "suppress_leading_zeros",
                "suppress_spaces", "use_digit_grouping", "decimal_symbol", "digit_grouping_symbol",
                "values", "project_position", "units");
        }

        // ================================================================== units

        private static CommandResult ReadUnits(Document doc, JObject r)
        {
            Units units = doc.GetUnits();
            var specs = new List<ForgeTypeId>();
            JArray asked = r["specs"] as JArray;
            if (r.Value<bool?>("all") == true) specs.AddRange(Units.GetModifiableSpecs());
            else if (asked != null)
            {
                foreach (JToken t in asked)
                {
                    ForgeTypeId s = ResolveSpec(t.Value<string>(), out string error);
                    if (s == null) return CommandResult.Fail(error);
                    specs.Add(s);
                }
            }
            // Unversioned ids, resolved against THIS Revit: spec versions differ between years.
            else specs.AddRange(DefaultSpecs.Select(s => ResolveSpec(s, out string _)).Where(s => s != null));
            var rows = new JArray(specs.Select(s => FormatRow(units, s)));
            return VerifiedModelEdit.ReadReply(new JObject
            {
                ["document"] = doc.Title,
                ["decimal_symbol"] = VerifiedModelEdit.Safe(() => units.DecimalSymbol.ToString()),
                ["digit_grouping_symbol"] = VerifiedModelEdit.Safe(() => units.DigitGroupingSymbol.ToString()),
                ["digit_grouping_amount"] = VerifiedModelEdit.Safe(() => units.DigitGroupingAmount.ToString()),
                ["count"] = rows.Count,
                ["specs"] = rows,
                ["note"] = asked == null && r.Value<bool?>("all") != true
                    ? "Common specs only; pass specs=[...] or all=true for every modifiable spec." : null
            });
        }

        private static JObject FormatRow(Units units, ForgeTypeId spec)
        {
            var row = new JObject { ["spec"] = spec.TypeId, ["label"] = VerifiedModelEdit.Safe(() => LabelUtils.GetLabelForSpec(spec)) };
            try
            {
                FormatOptions fo = units.GetFormatOptions(spec);
                ForgeTypeId unit = fo.GetUnitTypeId();
                ForgeTypeId symbol = SafeSymbol(fo);
                row["unit"] = unit.TypeId;
                row["unit_label"] = VerifiedModelEdit.Safe(() => LabelUtils.GetLabelForUnit(unit));
                row["accuracy"] = fo.Accuracy;
                row["symbol"] = symbol == null || symbol.Empty() ? null : symbol.TypeId;
                row["symbol_label"] = symbol == null || symbol.Empty() ? null : VerifiedModelEdit.Safe(() => LabelUtils.GetLabelForSymbol(symbol));
                row["suppress_trailing_zeros"] = B(() => fo.SuppressTrailingZeros);
                row["suppress_leading_zeros"] = B(() => fo.SuppressLeadingZeros);
                row["suppress_spaces"] = B(() => fo.SuppressSpaces);
                row["use_digit_grouping"] = B(() => fo.UseDigitGrouping);
                row["use_plus_prefix"] = B(() => fo.UsePlusPrefix);
            }
            catch (Exception ex) { row["error"] = ex.Message; }
            return row;
        }

        private static ModelEdit PlanSet(Document doc, JObject r, out string error)
        {
            error = null;
            Units units = doc.GetUnits();
            string specKey = r.Value<string>("spec");
            ForgeTypeId spec = null; FormatOptions fo = null; JObject before = null;
            var required = new List<string>();
            if (!string.IsNullOrWhiteSpace(specKey))
            {
                spec = ResolveSpec(specKey, out error);
                if (spec == null) return null;
                fo = new FormatOptions(units.GetFormatOptions(spec));
                before = FormatRow(units, spec);
                if (r["unit"] != null)
                {
                    ForgeTypeId unit = ResolveUnit(spec, r.Value<string>("unit"), out error);
                    if (unit == null) return null;
                    fo.SetUnitTypeId(unit);
                    // A symbol belongs to a unit; keep the old one only when it is still valid.
                    try { if (!fo.IsValidSymbol(SafeSymbol(fo) ?? new ForgeTypeId())) fo.SetSymbolTypeId(new ForgeTypeId()); } catch { }
                    required.Add("unit");
                }
                if (r["accuracy"] != null)
                {
                    double acc = r.Value<double>("accuracy");
                    if (!fo.IsValidAccuracy(acc)) { error = "accuracy " + acc.ToString(CultureInfo.InvariantCulture) + " is not valid for " + fo.GetUnitTypeId().TypeId + " (e.g. 1, 0.1, 0.01 for decimal units)."; return null; }
                    fo.Accuracy = acc; required.Add("accuracy");
                }
                if (r["symbol"] != null)
                {
                    string want = r.Value<string>("symbol") ?? "";
                    ForgeTypeId sym = null;
                    if (want.Length == 0 || want.Equals("none", StringComparison.OrdinalIgnoreCase)) sym = new ForgeTypeId();
                    else
                    {
                        var valid = fo.GetValidSymbols().Where(x => !x.Empty()).ToList();
                        sym = valid.FirstOrDefault(x => x.TypeId == want || ShortName(x.TypeId) == want ||
                              string.Equals(VerifiedModelEdit.Safe(() => LabelUtils.GetLabelForSymbol(x)), want, StringComparison.Ordinal));
                        if (sym == null) { error = "symbol '" + want + "' is not valid for this unit. Valid: " + string.Join(", ", valid.Select(x => ShortName(x.TypeId) + " (" + VerifiedModelEdit.Safe(() => LabelUtils.GetLabelForSymbol(x)) + ")")) + ", none."; return null; }
                    }
                    fo.SetSymbolTypeId(sym); required.Add("symbol");
                }
                foreach (string flag in new[] { "suppress_trailing_zeros", "suppress_leading_zeros", "suppress_spaces", "use_digit_grouping" })
                {
                    if (r[flag] == null) continue;
                    bool v = r.Value<bool>(flag);
                    try
                    {
                        if (flag == "suppress_trailing_zeros") fo.SuppressTrailingZeros = v;
                        else if (flag == "suppress_leading_zeros") fo.SuppressLeadingZeros = v;
                        else if (flag == "suppress_spaces") fo.SuppressSpaces = v;
                        else fo.UseDigitGrouping = v;
                    }
                    catch (Exception ex) { error = flag + " cannot be set for this unit: " + ex.Message; return null; }
                    required.Add(flag);
                }
                if (required.Count == 0) { error = "spec was named but nothing to change: pass unit, accuracy, symbol or a suppress_*/use_digit_grouping flag."; return null; }
            }
            DecimalSymbol? dec = null; DigitGroupingSymbol? grp = null;
            if (r["decimal_symbol"] != null)
            {
                if (!Enum.TryParse(r.Value<string>("decimal_symbol"), true, out DecimalSymbol d)) { error = "decimal_symbol must be one of " + string.Join(", ", Enum.GetNames(typeof(DecimalSymbol))) + "."; return null; }
                dec = d; required.Add("decimal_symbol");
            }
            if (r["digit_grouping_symbol"] != null)
            {
                if (!Enum.TryParse(r.Value<string>("digit_grouping_symbol"), true, out DigitGroupingSymbol g)) { error = "digit_grouping_symbol must be one of " + string.Join(", ", Enum.GetNames(typeof(DigitGroupingSymbol))) + "."; return null; }
                grp = g; required.Add("digit_grouping_symbol");
            }
            if (required.Count == 0) { error = "set changes nothing: pass spec with unit/accuracy/symbol/flags, and/or decimal_symbol / digit_grouping_symbol."; return null; }

            FormatOptions wanted = fo;
            var edit = new ModelEdit { Subject = "units:" + (spec?.TypeId ?? "symbols"), Category = "units" };
            if (before != null)
                foreach (var p in before.Properties())
                    if (p.Value.Type != JTokenType.Null) edit.Before[p.Name] = p.Value.ToString();
            edit.Before["decimal_symbol"] = units.DecimalSymbol.ToString();
            edit.Before["digit_grouping_symbol"] = units.DigitGroupingSymbol.ToString();
            edit.Plan = new JObject { ["spec"] = spec?.TypeId, ["change"] = new JArray(required) };
            if (wanted != null)
            {
                edit.Plan["unit"] = wanted.GetUnitTypeId().TypeId;
                edit.Plan["accuracy"] = wanted.Accuracy;
            }
            edit.Apply = d =>
            {
                Units u = d.GetUnits();
                if (spec != null) u.SetFormatOptions(spec, wanted);
                if (dec.HasValue) u.DecimalSymbol = dec.Value;
                if (grp.HasValue) u.DigitGroupingSymbol = grp.Value;
                d.SetUnits(u);
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(required.ToArray());
                Units u = d.GetUnits();
                FormatOptions now = null;
                if (spec != null) { try { now = u.GetFormatOptions(spec); } catch { } }
                foreach (string f in required)
                {
                    try
                    {
                        switch (f)
                        {
                            case "unit": check.Compare(f, wanted.GetUnitTypeId().TypeId, now.GetUnitTypeId().TypeId); break;
                            case "accuracy": check.Measure(f, wanted.Accuracy, now.Accuracy, 1e-12, "display unit", "FormatOptions.Accuracy"); break;
                            case "symbol": check.Compare(f, SymbolId(wanted), SymbolId(now)); break;
                            case "suppress_trailing_zeros": check.Compare(f, wanted.SuppressTrailingZeros, now.SuppressTrailingZeros); break;
                            case "suppress_leading_zeros": check.Compare(f, wanted.SuppressLeadingZeros, now.SuppressLeadingZeros); break;
                            case "suppress_spaces": check.Compare(f, wanted.SuppressSpaces, now.SuppressSpaces); break;
                            case "use_digit_grouping": check.Compare(f, wanted.UseDigitGrouping, now.UseDigitGrouping); break;
                            case "decimal_symbol": check.Compare(f, dec.Value.ToString(), u.DecimalSymbol.ToString()); break;
                            case "digit_grouping_symbol": check.Compare(f, grp.Value.ToString(), u.DigitGroupingSymbol.ToString()); break;
                        }
                    }
                    catch (Exception ex) { check.Unreadable(f, null, ex.Message); }
                }
                return check;
            };
            edit.Result = d => spec == null ? new JObject() : FormatRow(d.GetUnits(), spec);
            return edit;
        }

        // ====================================================== project information

        private static readonly Dictionary<string, BuiltInParameter> InfoFields = new Dictionary<string, BuiltInParameter>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = BuiltInParameter.PROJECT_NAME,
            ["number"] = BuiltInParameter.PROJECT_NUMBER,
            ["client"] = BuiltInParameter.CLIENT_NAME,
            ["address"] = BuiltInParameter.PROJECT_ADDRESS,
            ["status"] = BuiltInParameter.PROJECT_STATUS,
            ["issue_date"] = BuiltInParameter.PROJECT_ISSUE_DATE,
            ["author"] = BuiltInParameter.PROJECT_AUTHOR,
            ["building_name"] = BuiltInParameter.PROJECT_BUILDING_NAME,
            ["organization_name"] = BuiltInParameter.PROJECT_ORGANIZATION_NAME,
            ["organization_description"] = BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION
        };

        private static JObject ReadProjectInfo(Document doc)
        {
            ProjectInfo info = doc.ProjectInformation;
            var fields = new JObject();
            foreach (var f in InfoFields)
                fields[f.Key] = VerifiedModelEdit.Safe(() => info.get_Parameter(f.Value)?.AsString());
            var parameters = new JArray();
            foreach (Parameter p in info.Parameters.Cast<Parameter>().OrderBy(p => VerifiedModelEdit.Safe(() => p.Definition.Name), StringComparer.OrdinalIgnoreCase))
                parameters.Add(new JObject
                {
                    ["name"] = VerifiedModelEdit.Safe(() => p.Definition.Name),
                    ["storage"] = VerifiedModelEdit.Safe(() => p.StorageType.ToString()),
                    ["value"] = VerifiedModelEdit.Safe(() => p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()),
                    ["read_only"] = B(() => p.IsReadOnly),
                    ["shared"] = B(() => p.IsShared),
                    ["built_in"] = (p.Definition as InternalDefinition)?.BuiltInParameter is BuiltInParameter bip && bip != BuiltInParameter.INVALID ? bip.ToString() : null
                });
            return new JObject { ["document"] = doc.Title, ["element_id"] = Rid.Value(info.Id), ["fields"] = fields, ["parameters"] = parameters, ["count"] = parameters.Count };
        }

        private static ModelEdit PlanProjectInfo(Document doc, JObject r, out string error)
        {
            error = null;
            ProjectInfo info = doc.ProjectInformation;
            JObject values = (JObject)r["values"];
            if (values.Count == 0 || values.Count > 100) { error = "values must hold 1..100 entries."; return null; }
            var targets = new List<Tuple<string, Parameter, JToken>>();
            foreach (var kv in values.Properties())
            {
                Parameter p = null;
                if (InfoFields.TryGetValue(kv.Name, out BuiltInParameter bip)) p = info.get_Parameter(bip);
                else
                {
                    var hits = info.Parameters.Cast<Parameter>().Where(x => string.Equals(VerifiedModelEdit.Safe(() => x.Definition.Name), kv.Name, StringComparison.Ordinal)).ToList();
                    if (hits.Count > 1) { error = "'" + kv.Name + "' names " + hits.Count + " Project Information parameters; which one is meant is not guessable."; return null; }
                    p = hits.FirstOrDefault();
                }
                if (p == null) { error = "Project Information has no parameter '" + kv.Name + "' (operation=project_information without values lists them)."; return null; }
                if (p.IsReadOnly) { error = "'" + kv.Name + "' is read-only."; return null; }
                if (p.StorageType != StorageType.String && p.StorageType != StorageType.Integer)
                { error = "'" + kv.Name + "' stores " + p.StorageType + "; this operation writes text and integer parameters only (use horizun_write_params_verified on element " + Rid.Value(info.Id) + ")."; return null; }
                if (p.StorageType == StorageType.Integer && kv.Value.Type != JTokenType.Integer && !(kv.Value.Type == JTokenType.Boolean))
                { error = "'" + kv.Name + "' is an integer parameter; pass a number."; return null; }
                targets.Add(Tuple.Create(kv.Name, p, kv.Value));
            }
            ElementId id = info.Id;
            var edit = new ModelEdit { Subject = VerifiedModelEdit.Safe(() => info.UniqueId) ?? "project_information", Category = "ProjectInformation" };
            foreach (var t in targets) edit.Before[t.Item1] = Current(t.Item2) ?? "<empty>";
            edit.Plan = new JObject { ["element_id"] = Rid.Value(id), ["values"] = values.DeepClone() };
            edit.Apply = d =>
            {
                foreach (var t in targets)
                {
                    Parameter p = Find(d, t.Item1);
                    bool ok = p.StorageType == StorageType.String ? p.Set(t.Item3.Type == JTokenType.Null ? "" : t.Item3.ToString())
                                                                  : p.Set(t.Item3.Type == JTokenType.Boolean ? (t.Item3.Value<bool>() ? 1 : 0) : t.Item3.Value<int>());
                    if (!ok) throw new InvalidOperationException("Revit refused to set '" + t.Item1 + "'");
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(targets.Select(t => t.Item1).ToArray());
                foreach (var t in targets)
                {
                    try
                    {
                        Parameter p = Find(d, t.Item1);
                        if (p.StorageType == StorageType.String) check.Compare(t.Item1, t.Item3.Type == JTokenType.Null ? "" : t.Item3.ToString(), p.AsString() ?? "");
                        else check.Compare(t.Item1, (long)(t.Item3.Type == JTokenType.Boolean ? (t.Item3.Value<bool>() ? 1 : 0) : t.Item3.Value<int>()), (long)p.AsInteger());
                    }
                    catch (Exception ex) { check.Unreadable(t.Item1, t.Item3, ex.Message); }
                }
                return check;
            };
            return edit;
        }

        private static Parameter Find(Document d, string key)
        {
            ProjectInfo info = d.ProjectInformation;
            if (InfoFields.TryGetValue(key, out BuiltInParameter bip)) return info.get_Parameter(bip);
            return info.Parameters.Cast<Parameter>().Single(x => string.Equals(x.Definition.Name, key, StringComparison.Ordinal));
        }

        private static string Current(Parameter p)
        {
            try { return p.StorageType == StorageType.String ? p.AsString() : p.AsInteger().ToString(CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        // ======================================================== base points

        private static JObject ReadBasePoints(Document doc)
        {
            ProjectLocation loc = doc.ActiveProjectLocation;
            BasePoint pbp = BasePoint.GetProjectBasePoint(doc), sp = BasePoint.GetSurveyPoint(doc);
            JObject Point(BasePoint b)
            {
                if (b == null) return null;
                return new JObject
                {
                    ["id"] = Rid.Value(b.Id),
                    ["position_mm"] = Xyz(() => b.Position),
                    ["shared_position_mm"] = Xyz(() => b.SharedPosition),
                    ["pinned"] = B(() => b.Pinned)
                };
            }
            JObject shared = null;
            try
            {
                ProjectPosition p = loc.GetProjectPosition(pbp.Position);
                shared = PositionJson(p);
            }
            catch (Exception ex) { shared = new JObject { ["error"] = ex.Message }; }
            return new JObject
            {
                ["document"] = doc.Title,
                ["active_location"] = VerifiedModelEdit.Safe(() => loc.Name),
                ["project_base_point"] = Point(pbp),
                ["survey_point"] = Point(sp),
                ["project_position_at_base_point"] = shared,
                ["note"] = "Lengths in mm. project_position is the shared coordinate of the project base point: " +
                           "east_west / north_south / elevation, and the angle from project north to true north."
            };
        }

        private static JObject PositionJson(ProjectPosition p) => new JObject
        {
            ["east_west_mm"] = Math.Round(p.EastWest * 304.8, 4),
            ["north_south_mm"] = Math.Round(p.NorthSouth * 304.8, 4),
            ["elevation_mm"] = Math.Round(p.Elevation * 304.8, 4),
            ["angle_to_true_north_deg"] = Math.Round(p.Angle * 180 / Math.PI, 9)
        };

        private static ModelEdit PlanBasePoint(Document doc, JObject r, out string error)
        {
            error = null;
            if (r.Value<bool?>("confirm_shared_coordinates") != true)
            {
                error = "base_points with project_position RE-SPECIFIES THE SHARED COORDINATES of this model: every linked " +
                        "model, coordinate-based export and survey tie that relies on them moves with it, and in a " +
                        "workshared model it needs the shared-coordinates ownership. Pass confirm_shared_coordinates=true " +
                        "only when that is what the project coordinator decided.";
                return null;
            }
            JObject pp = (JObject)r["project_position"];
            string units = (r.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!DimensionPlanRules.UnitScale(units, out double scale)) { error = "units must be mm, m or feet."; return null; }
            ProjectLocation loc = doc.ActiveProjectLocation;
            BasePoint pbp = BasePoint.GetProjectBasePoint(doc);
            if (loc == null || pbp == null) { error = "The document has no active project location or project base point."; return null; }
            XYZ at = pbp.Position;
            ProjectPosition now = loc.GetProjectPosition(at);
            double ew = pp["east_west"] != null ? pp.Value<double>("east_west") * scale : now.EastWest;
            double ns = pp["north_south"] != null ? pp.Value<double>("north_south") * scale : now.NorthSouth;
            double el = pp["elevation"] != null ? pp.Value<double>("elevation") * scale : now.Elevation;
            double an = pp["angle_to_true_north"] != null ? pp.Value<double>("angle_to_true_north") * Math.PI / 180 : now.Angle;
            if (pp["east_west"] == null && pp["north_south"] == null && pp["elevation"] == null && pp["angle_to_true_north"] == null)
            { error = "project_position changes nothing: pass east_west, north_south, elevation and/or angle_to_true_north (degrees)."; return null; }
            ElementId locId = loc.Id;
            var edit = new ModelEdit { Subject = VerifiedModelEdit.Safe(() => loc.UniqueId) ?? "project_location", Category = "ProjectLocation" };
            JObject before = PositionJson(now);
            foreach (var p in before.Properties()) edit.Before[p.Name] = p.Value.ToString();
            edit.Plan = new JObject { ["location"] = loc.Name, ["at_project_base_point_mm"] = Xyz(() => at),
                                      ["before"] = before, ["after"] = PositionJson(new ProjectPosition(ew, ns, el, an)) };
            edit.Warning = "Shared coordinates are re-specified at the project base point. Linked models positioned by " +
                           "shared coordinates, coordinate-based IFC/DWG exports and survey ties all follow this change.";
            edit.TokenNote = "the token binds the project position measured at the project base point before the change.";
            edit.Apply = d => ((ProjectLocation)d.GetElement(locId)).SetProjectPosition(at, new ProjectPosition(ew, ns, el, an));
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("east_west", "north_south", "elevation", "angle_to_true_north");
                try
                {
                    ProjectPosition p = ((ProjectLocation)d.GetElement(locId)).GetProjectPosition(at);
                    check.Measure("east_west", ew, p.EastWest, 1e-6, "ft", "ProjectLocation.GetProjectPosition(project base point)");
                    check.Measure("north_south", ns, p.NorthSouth, 1e-6, "ft", "ProjectLocation.GetProjectPosition(project base point)");
                    check.Measure("elevation", el, p.Elevation, 1e-6, "ft", "ProjectLocation.GetProjectPosition(project base point)");
                    check.Measure("angle_to_true_north", an, p.Angle, 1e-9, "rad", "ProjectLocation.GetProjectPosition(project base point)");
                }
                catch (Exception ex)
                {
                    foreach (string f in new[] { "east_west", "north_south", "elevation", "angle_to_true_north" }) check.Unreadable(f, null, ex.Message);
                }
                return check;
            };
            edit.Result = d => ReadBasePoints(d);
            return edit;
        }

        // ================================================================== helpers

        private static ForgeTypeId ResolveSpec(string key, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(key)) { error = "spec is required (e.g. length, area, autodesk.spec.aec:length-2.0.0)."; return null; }
            key = key.Trim();
            IList<ForgeTypeId> all = Units.GetModifiableSpecs();
            var hits = all.Where(s => s.TypeId == key || Unversioned(s.TypeId) == key).ToList();
            if (hits.Count == 0) hits = all.Where(s => string.Equals(ShortName(s.TypeId), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) hits = all.Where(s => string.Equals(VerifiedModelEdit.Safe(() => LabelUtils.GetLabelForSpec(s)), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            error = hits.Count == 0
                ? "No modifiable spec matches '" + key + "'. operation=read with all=true lists them."
                : "'" + key + "' matches " + hits.Count + " specs: " + string.Join(", ", hits.Select(h => h.TypeId)) + ". Pass the full id.";
            return null;
        }

        private static ForgeTypeId ResolveUnit(ForgeTypeId spec, string key, out string error)
        {
            error = null;
            IList<ForgeTypeId> valid = UnitUtils.GetValidUnits(spec);
            key = (key ?? "").Trim();
            var hits = valid.Where(u => u.TypeId == key || Unversioned(u.TypeId) == key ||
                                        string.Equals(ShortName(u.TypeId), key, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(VerifiedModelEdit.Safe(() => LabelUtils.GetLabelForUnit(u)), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            error = "unit '" + key + "' is not " + (hits.Count > 1 ? "unambiguous" : "valid") + " for " + spec.TypeId + ". Valid: " +
                    string.Join(", ", valid.Select(u => ShortName(u.TypeId))) + ".";
            return null;
        }

        /// <summary>'autodesk.spec.aec:length-2.0.0' -> 'autodesk.spec.aec:length'.</summary>
        private static string Unversioned(string typeId)
        {
            int dash = typeId.LastIndexOf('-');
            return dash > 0 ? typeId.Substring(0, dash) : typeId;
        }

        /// <summary>'autodesk.spec.aec:length-2.0.0' -> 'length'.</summary>
        private static string ShortName(string typeId)
        {
            string u = Unversioned(typeId);
            int colon = u.LastIndexOf(':');
            return colon >= 0 ? u.Substring(colon + 1) : u;
        }

        private static ForgeTypeId SafeSymbol(FormatOptions fo) { try { return fo.GetSymbolTypeId(); } catch { return null; } }
        private static string SymbolId(FormatOptions fo) { ForgeTypeId s = SafeSymbol(fo); return s == null || s.Empty() ? "" : s.TypeId; }
        private static JToken B(Func<bool> f) { try { return f(); } catch { return null; } }

        private static JToken Xyz(Func<XYZ> f)
        {
            try
            {
                XYZ p = f();
                return new JArray(Math.Round(p.X * 304.8, 4), Math.Round(p.Y * 304.8, 4), Math.Round(p.Z * 304.8, 4));
            }
            catch { return null; }
        }
    }
}
