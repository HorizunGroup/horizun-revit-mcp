// -----------------------------------------------------------------------------
// Horizun Revit MCP - compact, typed authoring surface for common BIM elements.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class CreateElementsCommand : ICommand
    {
        public string Name => "horizun_create_elements";
        public string Description => "Create common BIM elements atomically, then re-read every created id.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray input = request["elements"] as JArray;
            if (input == null || input.Count == 0) return CommandResult.Fail("elements is required and must be non-empty.");
            if (input.Count > 2000) return CommandResult.Fail("elements exceeds the 2000 item atomic-batch limit.");
            double scale;
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!Scale(units, out scale)) return CommandResult.Fail("units must be mm, m or feet.");

            var plans = new List<Plan>();
            var errors = new JArray();
            for (int i = 0; i < input.Count; i++)
            {
                JObject item = input[i] as JObject;
                string error = null;
                Plan plan = item == null ? null : PlanItem(doc, i, item, scale, out error);
                if (plan == null) errors.Add(new JObject { ["index"] = i, ["error"] = item == null ? "entry is not an object" : error });
                else plans.Add(plan);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "elements");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["requested"] = input.Count,
                    ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors,
                    ["plan"] = new JArray(plans.Select(p => p.Summary)),
                    ["note"] = "Nothing was created and no transaction was opened. Correct every invalid row before apply."
                };
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds this ordered heterogeneous batch and its units" :
                    "no usable confirmation is issued while any row is invalid");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0)
                return CommandResult.Fail(errors.Count + " element plan(s) are invalid. Nothing was created: " + errors.ToString(Formatting.None));
            CommandResult confirmation = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (confirmation != null) return confirmation;
            CommandResult moved = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (moved != null) return moved;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: create elements";
            var created = new List<Created>();
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    foreach (Plan plan in plans)
                    {
                        Element element = Create(doc, plan);
                        if (element == null) throw new InvalidOperationException("item " + plan.Index + " returned no element");
                        created.Add(new Created { Index = plan.Index, Kind = plan.Kind, Id = element.Id });
                    }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    return CommandResult.Fail("Atomic creation failed: " + ex.Message + ". The transaction was rolled back; nothing in this batch was kept.");
                }
            }

            var rows = new JArray();
            int verified = 0;
            foreach (Created made in created)
            {
                Element element = doc.GetElement(made.Id);
                bool kindMatches = element != null && KindMatches(element, made.Kind);
                if (kindMatches) verified++;
                rows.Add(new JObject
                {
                    ["index"] = made.Index, ["kind"] = made.Kind, ["element_id"] = Rid.Value(made.Id),
                    ["present_after_commit"] = element != null, ["kind_verified"] = kindMatches,
                    ["actual_class"] = element?.GetType().Name, ["actual_category"] = Safe(() => element?.Category?.Name)
                });
            }
            if (verified != created.Count)
                return CommandResult.Fail("The transaction committed, but only " + verified + " of " + created.Count +
                    " created ids were re-read as the requested kinds. Inspect the model; success is not claimed. Verification: " +
                    rows.ToString(Formatting.None));

            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["requested"] = input.Count, ["created_verified"] = verified,
                ["verification"] = new JObject { ["intended"] = plans.Count, ["actual"] = verified, ["verified"] = verified == plans.Count },
                ["rows"] = rows
            });
        }

        private static Plan PlanItem(Document doc, int index, JObject item, double scale, out string error)
        {
            error = null;
            string kind = (item.Value<string>("kind") ?? "").ToLowerInvariant();
            var p = new Plan { Index = index, Kind = kind, Input = item, Scale = scale };
            try
            {
                switch (kind)
                {
                    case "level":
                        if (item["elevation"] == null) throw new ArgumentException("elevation is required");
                        p.Elevation = item.Value<double>("elevation") * scale;
                        break;
                    case "grid":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true);
                        NonZero(p.Start, p.End);
                        break;
                    case "wall":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Level = Need<Level>(doc, item, "level_id");
                        p.Type = Optional<WallType>(doc, item, "type_id") ??
                            new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                                .FirstOrDefault(w => w.Kind == WallKind.Basic);
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no Basic WallType default");
                        if (item["height"] == null) throw new ArgumentException("height is required for wall");
                        p.Height = item.Value<double>("height") * scale;
                        if (p.Height <= 0) throw new ArgumentException("height must be positive");
                        break;
                    case "floor":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<FloorType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = doc.GetElement(Floor.GetDefaultFloorType(doc, false)) as FloorType;
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and Revit reports no default architectural FloorType");
                        p.Loops = Loops(item["profile"] as JArray, scale);
                        break;
                    case "room":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Start = Point(item["point"], scale, false);
                        break;
                    case "family_instance":
                        p.Type = Need<FamilySymbol>(doc, item, "type_id"); p.Start = Point(item["point"], scale, true);
                        p.Level = Optional<Level>(doc, item, "level_id");
                        StructuralType parsed;
                        if (!Enum.TryParse(item.Value<string>("structural_type") ?? "NonStructural", true, out parsed))
                            throw new ArgumentException("structural_type is invalid");
                        p.StructuralType = parsed;
                        break;
                    case "duct":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<DuctType>(doc, item, "type_id");
                        p.SystemType = Need<MechanicalSystemType>(doc, item, "system_type_id");
                        break;
                    case "pipe":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<PipeType>(doc, item, "type_id");
                        p.SystemType = Need<PipingSystemType>(doc, item, "system_type_id");
                        break;
                    case "conduit":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<ConduitType>(doc, item, "type_id");
                        break;
                    default: throw new ArgumentException("unsupported kind '" + kind + "'");
                }
                p.Summary = new JObject { ["index"] = index, ["kind"] = kind, ["references_resolved"] = true };
                return p;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private static Element Create(Document doc, Plan p)
        {
            switch (p.Kind)
            {
                case "level":
                    Level level = Level.Create(doc, p.Elevation);
                    if (!string.IsNullOrWhiteSpace(p.Input.Value<string>("name"))) level.Name = p.Input.Value<string>("name");
                    return level;
                case "grid":
                    Grid grid = Grid.Create(doc, Line.CreateBound(p.Start, p.End));
                    if (!string.IsNullOrWhiteSpace(p.Input.Value<string>("name"))) grid.Name = p.Input.Value<string>("name");
                    return grid;
                case "wall":
                    return Wall.Create(doc, Line.CreateBound(p.Start, p.End), p.Type.Id, p.Level.Id, p.Height,
                        (p.Input.Value<double?>("offset") ?? 0) * p.Scale,
                        p.Input.Value<bool?>("flip") == true, p.Input.Value<bool?>("structural") == true);
                case "floor":
                    return Floor.Create(doc, p.Loops, p.Type.Id, p.Level.Id);
                case "room":
                    return doc.Create.NewRoom(p.Level, new UV(p.Start.X, p.Start.Y));
                case "family_instance":
                    FamilySymbol symbol = (FamilySymbol)p.Type;
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    return p.Level == null
                        ? doc.Create.NewFamilyInstance(p.Start, symbol, p.StructuralType)
                        : doc.Create.NewFamilyInstance(p.Start, symbol, p.Level, p.StructuralType);
                case "duct": return Duct.Create(doc, p.SystemType.Id, p.Type.Id, p.Level.Id, p.Start, p.End);
                case "pipe": return Pipe.Create(doc, p.SystemType.Id, p.Type.Id, p.Level.Id, p.Start, p.End);
                case "conduit": return Conduit.Create(doc, p.Type.Id, p.Start, p.End, p.Level.Id);
                default: throw new InvalidOperationException("unsupported kind '" + p.Kind + "'");
            }
        }

        private static bool KindMatches(Element e, string kind)
        {
            switch (kind)
            {
                case "level": return e is Level; case "grid": return e is Grid; case "wall": return e is Wall;
                case "floor": return e is Floor; case "room": return e is SpatialElement;
                case "family_instance": return e is FamilyInstance; case "duct": return e is Duct;
                case "pipe": return e is Pipe; case "conduit": return e is Conduit; default: return false;
            }
        }

        private static T Need<T>(Document doc, JObject o, string field) where T : Element
        {
            T value = Optional<T>(doc, o, field);
            if (value == null) throw new ArgumentException(field + " is required and must identify a " + typeof(T).Name);
            return value;
        }
        private static T Optional<T>(Document doc, JObject o, string field) where T : Element
        {
            if (o[field] == null) return null;
            long raw = o.Value<long>(field);
            if (!Rid.CanRepresent(raw)) throw new ArgumentException(field + " is outside ElementId range");
            T value = doc.GetElement(Rid.Make(raw)) as T;
            if (value == null) throw new ArgumentException(field + "=" + raw + " does not identify a " + typeof(T).Name);
            return value;
        }
        private static XYZ Point(JToken token, double scale, bool requireZ)
        {
            JArray a = token as JArray;
            int minimum = requireZ ? 3 : 2;
            if (a == null || a.Count < minimum || a.Count > 3) throw new ArgumentException("point/start/end must contain " + minimum + " XYZ coordinates");
            return new XYZ(a[0].Value<double>() * scale, a[1].Value<double>() * scale,
                (a.Count > 2 ? a[2].Value<double>() : 0) * scale);
        }
        private static IList<CurveLoop> Loops(JArray profile, double scale)
        {
            if (profile == null || profile.Count == 0) throw new ArgumentException("profile requires at least one loop");
            var result = new List<CurveLoop>();
            foreach (JArray loopToken in profile.OfType<JArray>())
            {
                if (loopToken.Count < 3) throw new ArgumentException("every profile loop needs at least three points");
                List<XYZ> points = loopToken.Select(t => Point(t, scale, true)).ToList();
                var loop = new CurveLoop();
                for (int i = 0; i < points.Count; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
                result.Add(loop);
            }
            if (result.Count != profile.Count) throw new ArgumentException("every profile entry must be an array of XYZ points");
            return result;
        }
        private static void NonZero(XYZ a, XYZ b) { if (a.DistanceTo(b) < 1e-9) throw new ArgumentException("start and end must differ"); }
        private static bool Scale(string units, out double scale)
        { if (units == "feet") { scale = 1; return true; } if (units == "m") { scale = 1 / 0.3048; return true; } if (units == "mm") { scale = 1 / 304.8; return true; } scale = 0; return false; }
        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }

        private sealed class Plan
        {
            public int Index; public string Kind; public JObject Input; public double Scale, Elevation, Height;
            public XYZ Start, End; public Level Level; public Element Type, SystemType; public IList<CurveLoop> Loops;
            public StructuralType StructuralType; public JObject Summary;
        }
        private sealed class Created { public int Index; public string Kind; public ElementId Id; }
    }
}
