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
        public string Description => "Create architectural, structural and MEP BIM elements atomically, then re-read every created id.";

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
            // Every action's outcome, so the FALLBACK decision is made once, centrally,
            // over the whole batch - a mixed batch must not inherit one entry's
            // capability gap as permission for the request. See FallbackDecision.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < input.Count; i++)
            {
                JObject item = input[i] as JObject;
                string error = null, reason = null;
                Plan plan = item == null ? null : PlanItem(doc, i, item, scale, out error, out reason);
                if (plan == null)
                {
                    string message = item == null ? "entry is not an object" : error;
                    errors.Add(new JObject { ["index"] = i, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else plans.Add(plan);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "elements");

            // ---- The MATERIALISED plan: what the request's NAMES resolved to. -----------
            // planHash binds the batch as written, and for a creation command the batch is
            // names and ids: a wall type called "Muro 200", a level called "N.E 10", a
            // piping system picked by name. None of those meanings is frozen by the
            // request. Between the rehearsal and the apply somebody can rename a type,
            // swap what a name resolves to, or move a level's elevation - and the same
            // batch then creates different elements in different places. The plan records
            // each row's RESOLVED references: the type's UniqueId and name, the level's
            // UniqueId and its elevation as measured now. A level that moved 50mm is a
            // different plan even though its name still matches.
            //
            // Elements created do not exist at plan time, so what is fingerprinted is what
            // the caller actually approved: the recipe plus the resolved ingredients.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Plan planned in plans)
            {
                var row = new PlannedElement
                {
                    UniqueId = "create:" + planned.Index,
                    Category = planned.Kind,
                    TypeName = SafePlanName(planned.Type),
                    Action = PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "type_uid", SafePlanUid(planned.Type) },
                        { "system_type", SafePlanName(planned.SystemType) },
                        { "system_type_uid", SafePlanUid(planned.SystemType) },
                        { "level", SafePlanName(planned.Level) },
                        { "level_uid", SafePlanUid(planned.Level) },
                        // The level's measured elevation, to the tenth of a millimetre.
                        // "Create on N.E 10" approved a HEIGHT, not a name: a level that
                        // moved is a different creation wearing the same words.
                        { "level_elev_mm", SafePlanElevation(planned.Level) }
                    }
                };
                resolvedPlan.Elements.Add(row);
            }

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["requested"] = input.Count,
                    ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors,
                    ["plan"] = new JArray(plans.Select(p => p.Summary)),
                    ["note"] = "Nothing was created and no transaction was opened. Correct every invalid row before apply."
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so
                // this is the first thing a caller sends; without the block here they
                // got success=true, invalid=1 and no way to tell a capability gap from
                // a typo except by sending an apply they had no reason to send.
                // Invalid entries make this a partial rehearsal, not a clean one: the token
                // below is already withheld for them, and a plan must read the same fact.
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                // Stamp before constructing CommandResult. The previous order happened to
                // work only because Ok retained the mutable JObject reference.
                CommandResult rehearsal = FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0
                        ? "the token binds this ordered heterogeneous batch, its units, AND what its names resolved " +
                          "to right now - the types, system types and levels, including each level's measured " +
                          "elevation. A type renamed, a name re-pointed or a level moved before you apply refuses " +
                          "as a stale plan instead of creating something else under the approved words."
                        : "no usable confirmation is issued while any row is invalid");
                return rehearsal;
            }
            if (errors.Count > 0)
            {
                string why = errors.Count + " element plan(s) are invalid. Nothing was created: " +
                             errors.ToString(Formatting.None);
                // Nothing has been written - no transaction is open at this point - so the
                // decision is entirely about WHAT failed, and it is made centrally.
                return FallbackDecision.Refuse(why, FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            // Recomputed above by this call's own PlanItem resolution. The rehearsed plan
            // does not travel in the token, only its fingerprint, so a stale refusal names
            // the drift generically - still refused, nothing created.
            CommandResult confirmation = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                          resolvedPlan, null);
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
                        created.Add(new Created
                        {
                            Index = plan.Index, Kind = plan.Kind, Id = element.Id,
                            ExpectedTypeId = plan.Type?.Id,
                            ExpectedStructuralType = plan.Kind == "family_instance" || plan.Kind == "structural_framing" || plan.Kind == "structural_column"
                                ? (StructuralType?)plan.StructuralType : null
                        });
                    }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Atomic creation failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing in this batch was kept"));
                }
            }

            var rows = new JArray();
            int verified = 0;
            foreach (Created made in created)
            {
                Element element = doc.GetElement(made.Id);
                bool kindMatches = element != null && KindMatches(element, made.Kind);
                bool typeMatches = made.ExpectedTypeId == null || (element != null && element.GetTypeId() == made.ExpectedTypeId);
                bool structuralTypeMatches = made.ExpectedStructuralType == null ||
                    (element is FamilyInstance instance && instance.StructuralType == made.ExpectedStructuralType.Value);
                bool rowVerified = kindMatches && typeMatches && structuralTypeMatches;
                if (rowVerified) verified++;
                rows.Add(new JObject
                {
                    ["index"] = made.Index, ["kind"] = made.Kind, ["element_id"] = Rid.Value(made.Id),
                    ["present_after_commit"] = element != null, ["kind_verified"] = kindMatches,
                    ["type_verified"] = typeMatches, ["structural_type_verified"] = structuralTypeMatches,
                    ["verified"] = rowVerified,
                    ["actual_class"] = element?.GetType().Name, ["actual_category"] = Safe(() => element?.Category?.Name)
                });
            }
            if (verified != created.Count)
                return CommandResult.Fail("The transaction committed, but only " + verified + " of " + created.Count +
                    " created ids were re-read as the requested kinds. Inspect the model; success is not claimed. Verification: " +
                    rows.ToString(Formatting.None));

            var ceResult = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["requested"] = input.Count, ["created_verified"] = verified,
                ["verification"] = new JObject { ["intended"] = plans.Count, ["actual"] = verified, ["verified"] = verified == plans.Count },
                ["rows"] = rows
            };
            // Entries that never became a plan are unresolved: they were asked for and no
            // element was created for them, which is not the same as a creation that failed.
            ApplicationOutcome.StampApplied(ceResult, ApplicationOutcome.Committed, input.Count, verified,
                                            verified, input.Count - plans.Count, 0, 0);
            return CommandResult.Ok(ceResult);
        }

        private static Plan PlanItem(Document doc, int index, JObject item, double scale, out string error,
                                     out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            string kind = (item.Value<string>("kind") ?? "").ToLowerInvariant();
            var p = new Plan { Index = index, Kind = kind, Input = item, Scale = scale };
            try
            {
                switch (kind)
                {
                    case "level":
                        if (item["elevation"] == null) throw new ArgumentException("elevation is required");
                        p.Elevation = Finite(item.Value<double>("elevation"), "elevation") * scale;
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
                        p.Height = Finite(item.Value<double>("height"), "height") * scale;
                        if (p.Height <= 0) throw new ArgumentException("height must be positive");
                        p.Offset = Finite(item.Value<double?>("offset") ?? 0, "offset") * scale;
                        break;
                    case "floor":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<FloorType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = doc.GetElement(Floor.GetDefaultFloorType(doc, false)) as FloorType;
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and Revit reports no default architectural FloorType");
                        p.Loops = Loops(item["profile"] as JArray, scale);
                        RequireHorizontal(p.Loops, "floor");
                        break;
                    case "ceiling":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<CeilingType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>().FirstOrDefault();
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no CeilingType");
                        p.Loops = Loops(item["profile"] as JArray, scale);
                        RequireHorizontal(p.Loops, "ceiling");
                        break;
                    case "roof":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<RoofType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault();
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no RoofType");
                        p.Loops = Loops(item["profile"] as JArray, scale);
                        RequireHorizontal(p.Loops, "roof");
                        if (p.Loops.Count != 1) throw new ArgumentException("roof currently requires exactly one closed footprint loop");
                        p.SlopeRadians = Finite(item.Value<double?>("slope_degrees") ?? 0, "slope_degrees") * Math.PI / 180.0;
                        if (p.SlopeRadians < 0 || p.SlopeRadians >= Math.PI / 2)
                            throw new ArgumentException("slope_degrees must be at least 0 and less than 90");
                        break;
                    case "room":
                        p.Level = Need<Level>(doc, item, "level_id"); p.Start = Point(item["point"], scale, false);
                        break;
                    case "family_instance":
                        p.Type = Need<FamilySymbol>(doc, item, "type_id"); p.Start = Point(item["point"], scale, true);
                        p.Level = Optional<Level>(doc, item, "level_id");
                        StructuralType parsed;
                        if (!Enum.TryParse(item.Value<string>("structural_type") ?? "NonStructural", true, out parsed) ||
                            !Enum.IsDefined(typeof(StructuralType), parsed))
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
                    case "cable_tray":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Optional<CableTrayType>(doc, item, "type_id");
                        if (p.Type == null) p.Type = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>().FirstOrDefault();
                        if (p.Type == null) throw new ArgumentException("type_id was omitted and the document has no CableTrayType");
                        break;
                    case "structural_framing":
                        p.Start = Point(item["start"], scale, true); p.End = Point(item["end"], scale, true); NonZero(p.Start, p.End);
                        p.Level = Need<Level>(doc, item, "level_id"); p.Type = Need<FamilySymbol>(doc, item, "type_id");
                        if (!InCategory(p.Type, BuiltInCategory.OST_StructuralFraming))
                            throw new ArgumentException("structural_framing type_id must identify a FamilySymbol in OST_StructuralFraming");
                        if (!Enum.TryParse(item.Value<string>("structural_type") ?? "Beam", true, out StructuralType framingType) ||
                            (framingType != StructuralType.Beam && framingType != StructuralType.Brace))
                            throw new ArgumentException("structural_framing structural_type must be Beam or Brace");
                        p.StructuralType = framingType;
                        break;
                    case "structural_column":
                        p.Start = Point(item["point"], scale, true); p.Level = Need<Level>(doc, item, "level_id");
                        p.Type = Need<FamilySymbol>(doc, item, "type_id"); p.StructuralType = StructuralType.Column;
                        if (!InCategory(p.Type, BuiltInCategory.OST_StructuralColumns))
                            throw new ArgumentException("structural_column type_id must identify a FamilySymbol in OST_StructuralColumns");
                        break;
                    default: throw new UnsupportedCapability(
                        "unsupported kind '" + kind + "' - horizun_create_elements implements a fixed set of " +
                        "element kinds and this is not one of them. Nothing was created.",
                        FallbackSignal.ReasonUnsupportedKind);
                }
                p.Summary = new JObject { ["index"] = index, ["kind"] = kind, ["references_resolved"] = true };
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
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
                        p.Offset,
                        p.Input.Value<bool?>("flip") == true, p.Input.Value<bool?>("structural") == true);
                case "floor":
                    return Floor.Create(doc, p.Loops, p.Type.Id, p.Level.Id);
                case "ceiling":
                    return Ceiling.Create(doc, p.Loops, p.Type.Id, p.Level.Id);
                case "roof":
                    var footprint = new CurveArray();
                    foreach (Curve curve in p.Loops[0]) footprint.Append(curve);
                    ModelCurveArray boundaries;
                    FootPrintRoof roof = doc.Create.NewFootPrintRoof(footprint, p.Level, (RoofType)p.Type, out boundaries);
                    foreach (ModelCurve edge in boundaries)
                    {
                        bool definesSlope = p.SlopeRadians > 0;
                        roof.set_DefinesSlope(edge, definesSlope);
                        if (definesSlope) roof.set_SlopeAngle(edge, p.SlopeRadians);
                    }
                    return roof;
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
                case "cable_tray": return CableTray.Create(doc, p.Type.Id, p.Start, p.End, p.Level.Id);
                case "structural_framing":
                    FamilySymbol framing = (FamilySymbol)p.Type;
                    if (!framing.IsActive) { framing.Activate(); doc.Regenerate(); }
                    return doc.Create.NewFamilyInstance(Line.CreateBound(p.Start, p.End), framing, p.Level, p.StructuralType);
                case "structural_column":
                    FamilySymbol column = (FamilySymbol)p.Type;
                    if (!column.IsActive) { column.Activate(); doc.Regenerate(); }
                    return doc.Create.NewFamilyInstance(p.Start, column, p.Level, StructuralType.Column);
                default: throw new InvalidOperationException("unsupported kind '" + p.Kind + "'");
            }
        }

        private static bool KindMatches(Element e, string kind)
        {
            switch (kind)
            {
                case "level": return e is Level; case "grid": return e is Grid; case "wall": return e is Wall;
                case "floor": return e is Floor; case "ceiling": return e is Ceiling; case "roof": return e is FootPrintRoof;
                case "room": return e is Autodesk.Revit.DB.Architecture.Room;
                case "family_instance": return e is FamilyInstance; case "duct": return e is Duct;
                case "pipe": return e is Pipe; case "conduit": return e is Conduit; case "cable_tray": return e is CableTray;
                case "structural_framing": return e is FamilyInstance && InCategory(e, BuiltInCategory.OST_StructuralFraming);
                case "structural_column": return e is FamilyInstance && InCategory(e, BuiltInCategory.OST_StructuralColumns);
                default: return false;
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
            return new XYZ(Finite(a[0].Value<double>(), "X") * scale, Finite(a[1].Value<double>(), "Y") * scale,
                Finite(a.Count > 2 ? a[2].Value<double>() : 0, "Z") * scale);
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
        private static void RequireHorizontal(IEnumerable<CurveLoop> loops, string kind)
        {
            double? commonZ = null;
            foreach (CurveLoop loop in loops)
            {
                List<Curve> curves = loop.ToList();
                double z = curves[0].GetEndPoint(0).Z;
                if (curves.Any(c => Math.Abs(c.GetEndPoint(0).Z - z) > 1e-7 || Math.Abs(c.GetEndPoint(1).Z - z) > 1e-7))
                    throw new ArgumentException(kind + " profile loops must be horizontal and coplanar");
                if (commonZ != null && Math.Abs(z - commonZ.Value) > 1e-7)
                    throw new ArgumentException(kind + " profile loops must share one horizontal plane");
                commonZ = z;
            }
        }
        private static bool InCategory(Element element, BuiltInCategory category)
        { return element?.Category != null && Rid.Value(element.Category.Id) == (long)category; }
        private static bool Scale(string units, out double scale)
        { if (units == "feet") { scale = 1; return true; } if (units == "m") { scale = 1 / 0.3048; return true; } if (units == "mm") { scale = 1 / 304.8; return true; } scale = 0; return false; }
        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static double Finite(double value, string field)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite"); return value; }

        /// <summary>Guarded reads for the plan: measuring must never be what fails.</summary>
        private static string SafePlanName(Element e)
        {
            try { return e == null ? "" : (e.Name ?? ""); } catch { return "<unreadable>"; }
        }

        private static string SafePlanUid(Element e)
        {
            try { return e == null ? "" : (e.UniqueId ?? ""); } catch { return "<unreadable>"; }
        }

        /// <summary>
        /// The level's elevation in tenths of a millimetre. Rounded because Revit's own
        /// regeneration jitters the last digits, and a fingerprint that changes on its own
        /// would refuse every apply - the same lesson the transform wiring paid for.
        /// </summary>
        private static string SafePlanElevation(Level level)
        {
            try
            {
                if (level == null) return "";
                return System.Math.Round(level.Elevation * 304.8, 1)
                             .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return "<unreadable>"; }
        }

        private sealed class Plan
        {
            public int Index; public string Kind; public JObject Input; public double Scale, Elevation, Height, Offset, SlopeRadians;
            public XYZ Start, End; public Level Level; public Element Type, SystemType; public IList<CurveLoop> Loops;
            public StructuralType StructuralType; public JObject Summary;
        }
        private sealed class Created
        {
            public int Index; public string Kind; public ElementId Id, ExpectedTypeId;
            public StructuralType? ExpectedStructuralType;
        }
    }
}
