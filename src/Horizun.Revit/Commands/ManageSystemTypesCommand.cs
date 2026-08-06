// -----------------------------------------------------------------------------
// Horizun Revit MCP - verified authoring of project-resident system-family types.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ManageSystemTypesCommand : ICommand
    {
        public string Name => "horizun_manage_system_types";
        public string Description => "Duplicate project-resident system-family ElementTypes, write their parameters atomically and verify every new type and value after commit.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            if (doc.IsFamilyDocument) return CommandResult.Fail("System-family types live in a project document, not in an RFA.");
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!TryScale(units, out double scale)) return CommandResult.Fail("units must be mm, m or feet.");
            JArray actions = request["actions"] as JArray;
            if (actions == null || actions.Count < 1 || actions.Count > 500)
                return CommandResult.Fail("actions must contain 1..500 entries.");

            var plans = new List<Plan>(); var errors = new JArray();
            var targetNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                try
                {
                    if (!(actions[i] is JObject action)) throw new ArgumentException("action is not an object");
                    long raw = action.Value<long?>("source_type_id") ?? -1;
                    if (!Rid.CanRepresent(raw) || !(doc.GetElement(Rid.Make(raw)) is ElementType source))
                        throw new ArgumentException("source_type_id must identify an ElementType");
                    if (source is FamilySymbol)
                        throw new ArgumentException("source_type_id is a loadable FamilySymbol; use horizun_create_family/family tools, not system-type duplication");
                    string name = action.Value<string>("new_name");
                    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("new_name is required");
                    name = name.Trim();
                    string uniqueness = source.GetType().FullName + "\n" + name;
                    if (!targetNames.Add(uniqueness)) throw new ArgumentException("new_name '" + name + "' is duplicated for " + source.GetType().Name);
                    if (new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                        .Any(x => x.GetType() == source.GetType() && string.Equals(x.Name, name, StringComparison.Ordinal)))
                        throw new ArgumentException(source.GetType().Name + " named '" + name + "' already exists");
                    JObject values = action["values"] as JObject ?? new JObject();
                    var writes = new List<Write>();
                    foreach (JProperty property in values.Properties())
                    {
                        Parameter parameter = ResolveParameter(source, property.Name, out string why);
                        if (parameter == null) throw new ArgumentException("parameter '" + property.Name + "': " + why);
                        if (parameter.IsReadOnly) throw new ArgumentException("parameter '" + property.Name + "' is read-only on the source type");
                        ValidateValue(parameter, property.Value);
                        writes.Add(new Write { Spec = property.Name, Requested = property.Value.DeepClone() });
                    }
                    CompoundPlan compound = BuildCompoundPlan(doc, action["compound_structure"], scale);
                    if (compound != null && !(source is HostObjAttributes))
                        throw new ArgumentException("compound_structure is only valid for a HostObjAttributes system type such as WallType, FloorType, RoofType or CeilingType");
                    if (compound != null) ValidateCompound((HostObjAttributes)source, compound);
                    plans.Add(new Plan { Index = i, Source = source, NewName = name, Writes = writes, Compound = compound });
                }
                catch (Exception ex) { errors.Add(new JObject { ["index"] = i, ["error"] = ex.Message }); }
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "actions");

            // ---- The MATERIALISED plan: the SOURCE each duplicate starts from. ----------
            // planHash binds the REQUEST - the actions as written. The duplicate INHERITS
            // everything the caller did not override, which is why the source's state is
            // half of what gets approved and none of it is in the request:
            //
            //   * A SOURCE PARAMETER MOVED. Duplicating "Muro 200" and setting two values
            //     copies every OTHER value as it stands. The rehearsal showed a type with
            //     45mm insulation; if somebody changes it to 90 before the token is spent,
            //     the same request mints a different wall. So each parameter the caller is
            //     ABOUT to override carries what it reads on the source NOW - drift in the
            //     inherited remainder is deliberately out of scope, and the fingerprint
            //     covering name + parameters keeps the check honest without freezing the
            //     whole type.
            //   * THE SOURCE WAS RENAMED OR SWAPPED. source_type_id is a number; the NAME
            //     is what the person approved duplicating. A renamed source is a different
            //     rehearsal.
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
                    UniqueId = SafePlanUniqueId(planned.Source),
                    Category = planned.Source.GetType().Name,
                    TypeName = SafePlanName(planned.Source),
                    Action = PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "new_name", planned.NewName },
                        { "compound", planned.Compound == null ? "" : Canon(planned.Compound.Summary()) }
                    }
                };
                foreach (Write w in planned.Writes)
                    row.BeforeValues["param:" + w.Spec] = SafePlanParamNow(planned.Source, w.Spec);
                resolvedPlan.Elements.Add(row);
            }

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["requested"] = actions.Count,
                    ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors,
                    ["plan"] = new JArray(plans.Select(x => new JObject
                    {
                        ["index"] = x.Index, ["source_type_id"] = Rid.Value(x.Source.Id), ["source_class"] = x.Source.GetType().Name,
                        ["source_name"] = x.Source.Name, ["new_name"] = x.NewName, ["parameters"] = new JArray(x.Writes.Select(w => w.Spec)),
                        ["compound_structure"] = x.Compound?.Summary()
                    })),
                    ["note"] = "No type was duplicated and no transaction was opened."
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0
                        ? "the token binds every source type, new name and requested parameter value, AND the value " +
                          "each named parameter reads on its source right now - a source renamed or edited under the " +
                          "rehearsal refuses as a stale plan. Parameters you did not name are inherited as they stand " +
                          "at apply time and are NOT frozen by this token."
                        : "no usable token is issued while any action is invalid");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0) return CommandResult.Fail("Invalid system-type plan; nothing ran: " + errors.ToString(Formatting.None));
            // Recomputed by THIS call from the sources as they stand. The rehearsed plan
            // does not travel in the token, only its fingerprint, so a stale refusal names
            // the drift generically - still refused, nothing duplicated.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name") ?? "Horizun: manage system family types";
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    foreach (Plan plan in plans)
                    {
                        plan.CreatedId = plan.Source.Duplicate(plan.NewName).Id;
                        ElementType created = doc.GetElement(plan.CreatedId) as ElementType;
                        if (created == null) throw new InvalidOperationException("Duplicate returned no ElementType for action " + plan.Index);
                        foreach (Write write in plan.Writes)
                        {
                            Parameter parameter = ResolveParameter(created, write.Spec, out string why);
                            if (parameter == null) throw new InvalidOperationException("duplicated type lost parameter '" + write.Spec + "': " + why);
                            Apply(parameter, write);
                        }
                        if (plan.Compound != null)
                            ApplyCompound((HostObjAttributes)created, plan.Compound);
                    }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Atomic system-type creation failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "no duplicate in this batch was kept"));
                }
            }

            var rows = new JArray(); int verified = 0;
            foreach (Plan plan in plans)
            {
                ElementType fresh = doc.GetElement(plan.CreatedId) as ElementType;
                bool typeOk = fresh != null && fresh.GetType() == plan.Source.GetType() && string.Equals(fresh.Name, plan.NewName, StringComparison.Ordinal);
                var parameters = new JArray(); bool valuesOk = true;
                foreach (Write write in plan.Writes)
                {
                    Parameter parameter = fresh == null ? null : ResolveParameter(fresh, write.Spec, out string _);
                    JToken actual = Read(parameter); bool ok = parameter != null && JToken.DeepEquals(actual, write.Expected);
                    if (!ok) valuesOk = false;
                    parameters.Add(new JObject
                    {
                        ["parameter"] = write.Spec, ["requested"] = write.Requested,
                        ["stored_expected"] = write.Expected, ["read_after_commit"] = actual,
                        ["verified"] = ok, ["intent_verified"] = !write.ParsedByRevit
                    });
                }
                JObject compound = plan.Compound == null ? null : VerifyCompound(fresh as HostObjAttributes, plan.Compound);
                bool compoundOk = compound == null || compound.Value<bool>("verified");
                bool okAll = typeOk && valuesOk && compoundOk; if (okAll) verified++;
                rows.Add(new JObject
                {
                    ["index"] = plan.Index, ["source_type_id"] = Rid.Value(plan.Source.Id), ["new_type_id"] = Rid.Value(plan.CreatedId),
                    ["class"] = fresh?.GetType().Name, ["name"] = fresh?.Name, ["type_verified"] = typeOk,
                    ["parameters_verified"] = valuesOk, ["compound_structure_verified"] = compoundOk,
                    ["verified"] = okAll, ["parameters"] = parameters, ["compound_structure"] = compound
                });
            }
            if (verified != plans.Count)
                return CommandResult.Fail("The transaction committed, but only " + verified + " of " + plans.Count +
                    " system types passed post-commit verification. Inspect the model: " + rows.ToString(Formatting.None));
            return CommandResult.Ok(new JObject
            {
                ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["created_verified"] = verified, ["rows"] = rows
            });
        }

        private static Parameter ResolveParameter(Element element, string spec, out string why)
        {
            why = null;
            if (Enum.TryParse(spec, true, out BuiltInParameter bip) && Enum.IsDefined(typeof(BuiltInParameter), bip))
            {
                Parameter parameter = element.get_Parameter(bip);
                if (parameter == null) why = "BuiltInParameter exists but is not present on this type";
                return parameter;
            }
            if (Guid.TryParse(spec, out Guid guid))
            {
                Parameter parameter = element.get_Parameter(guid);
                if (parameter == null) why = "shared-parameter GUID is not present on this type";
                return parameter;
            }
            IList<Parameter> hits = element.GetParameters(spec);
            if (hits.Count == 1) return hits[0];
            why = hits.Count == 0 ? "no exact parameter name matched" : hits.Count + " parameters share that name; use BuiltInParameter or GUID";
            return null;
        }

        private static CompoundPlan BuildCompoundPlan(Document doc, JToken token, double scale)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JObject spec)) throw new ArgumentException("compound_structure must be an object");
            JArray rows = spec["layers"] as JArray;
            if (rows == null || rows.Count < 1 || rows.Count > 100)
                throw new ArgumentException("compound_structure.layers must contain 1..100 layers ordered exterior to interior");
            var plan = new CompoundPlan
            {
                ExteriorShells = spec.Value<int?>("exterior_shell_layers") ?? 0,
                InteriorShells = spec.Value<int?>("interior_shell_layers") ?? 0,
                StructuralIndex = spec.Value<int?>("structural_layer_index") ?? -1,
                VariableIndex = spec.Value<int?>("variable_layer_index") ?? -1
            };
            if (plan.ExteriorShells < 0 || plan.InteriorShells < 0 || plan.ExteriorShells + plan.InteriorShells > rows.Count)
                throw new ArgumentException("exterior_shell_layers and interior_shell_layers must be non-negative and leave at least zero core layers");
            if (plan.StructuralIndex < -1 || plan.StructuralIndex >= rows.Count)
                throw new ArgumentException("structural_layer_index is outside compound_structure.layers");
            if (plan.VariableIndex < -1 || plan.VariableIndex >= rows.Count)
                throw new ArgumentException("variable_layer_index is outside compound_structure.layers");
            if (!TryEnum(spec.Value<string>("end_cap"), out EndCapCondition endCap))
                throw new ArgumentException("end_cap must be None, Exterior, Interior or NoEndCap");
            if (!TryEnum(spec.Value<string>("opening_wrapping"), out OpeningWrappingCondition opening))
                throw new ArgumentException("opening_wrapping must be None, Exterior, Interior or ExteriorAndInterior");
            plan.EndCap = endCap; plan.OpeningWrapping = opening;

            for (int i = 0; i < rows.Count; i++)
            {
                if (!(rows[i] is JObject row)) throw new ArgumentException("every compound_structure layer must be an object");
                string rawFunction = row.Value<string>("function") ?? "None";
                if (string.Equals(rawFunction, "thermal_or_air", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rawFunction, "thermal_or_air_layer", StringComparison.OrdinalIgnoreCase)) rawFunction = "Insulation";
                if (!Enum.TryParse(rawFunction, true, out MaterialFunctionAssignment function) ||
                    !Enum.IsDefined(typeof(MaterialFunctionAssignment), function) || function == MaterialFunctionAssignment.None)
                    throw new ArgumentException("layer " + i + " function must be Structure, Substrate, Insulation, Finish1, Finish2, Membrane or StructuralDeck");
                double requestedWidth = Finite(row.Value<double?>("width") ?? (function == MaterialFunctionAssignment.Membrane ? 0 : -1), "layer " + i + " width");
                if (function == MaterialFunctionAssignment.Membrane ? Math.Abs(requestedWidth) > 1e-12 : requestedWidth <= 0)
                    throw new ArgumentException("layer " + i + (function == MaterialFunctionAssignment.Membrane ? " membrane width must be 0" : " width must be positive"));
                long materialRaw = row.Value<long?>("material_id") ?? -1;
                if (!Rid.CanRepresent(materialRaw) || (materialRaw != -1 && !(doc.GetElement(Rid.Make(materialRaw)) is Material)))
                    throw new ArgumentException("layer " + i + " material_id must identify a Material or be -1");
                long deckRaw = row.Value<long?>("deck_profile_id") ?? -1;
                if (!Rid.CanRepresent(deckRaw) || (deckRaw != -1 && !(doc.GetElement(Rid.Make(deckRaw)) is FamilySymbol)))
                    throw new ArgumentException("layer " + i + " deck_profile_id must identify a loadable profile FamilySymbol or be -1");
                if (!TryEnum(row.Value<string>("deck_embedding"), out StructDeckEmbeddingType embedding))
                    throw new ArgumentException("layer " + i + " deck_embedding must be MergeWithLayerAbove or Standalone");
                if ((deckRaw != -1 || row["deck_embedding"] != null) && function != MaterialFunctionAssignment.StructuralDeck)
                    throw new ArgumentException("layer " + i + " deck settings require function=StructuralDeck");
                plan.Layers.Add(new LayerPlan
                {
                    Function = function, Width = requestedWidth * scale, MaterialId = Rid.Make(materialRaw),
                    Wraps = row.Value<bool?>("wraps") == true, DeckProfileId = Rid.Make(deckRaw), DeckEmbedding = embedding
                });
            }
            if (plan.StructuralIndex >= 0 && plan.Layers[plan.StructuralIndex].Function != MaterialFunctionAssignment.Structure &&
                plan.Layers[plan.StructuralIndex].Function != MaterialFunctionAssignment.StructuralDeck)
                throw new ArgumentException("structural_layer_index must point to a Structure or StructuralDeck layer");
            if (plan.VariableIndex >= 0 && plan.Layers[plan.VariableIndex].Function == MaterialFunctionAssignment.Membrane)
                throw new ArgumentException("variable_layer_index cannot point to a zero-width Membrane layer");
            return plan;
        }

        private static void ApplyCompound(HostObjAttributes type, CompoundPlan plan)
        {
            CompoundStructure structure = MakeCompound(plan);
            AssertValidCompound(type, structure);
            type.SetCompoundStructure(structure);
        }

        private static void ValidateCompound(HostObjAttributes type, CompoundPlan plan)
        {
            CompoundStructure structure = MakeCompound(plan);
            AssertValidCompound(type, structure);
        }

        private static CompoundStructure MakeCompound(CompoundPlan plan)
        {
            var layers = plan.Layers.Select(x => new CompoundStructureLayer(x.Width, x.Function, x.MaterialId)).ToList();
            CompoundStructure structure = CompoundStructure.CreateSimpleCompoundStructure(layers);
            structure.SetNumberOfShellLayers(ShellLayerType.Exterior, plan.ExteriorShells);
            structure.SetNumberOfShellLayers(ShellLayerType.Interior, plan.InteriorShells);
            structure.StructuralMaterialIndex = plan.StructuralIndex;
            structure.VariableLayerIndex = plan.VariableIndex;
            structure.EndCap = plan.EndCap;
            structure.OpeningWrapping = plan.OpeningWrapping;
            for (int i = 0; i < plan.Layers.Count; i++)
            {
                LayerPlan layer = plan.Layers[i];
                structure.SetParticipatesInWrapping(i, layer.Wraps);
                if (layer.Function == MaterialFunctionAssignment.StructuralDeck)
                {
                    structure.SetDeckProfileId(i, layer.DeckProfileId);
                    structure.SetDeckEmbeddingType(i, layer.DeckEmbedding);
                }
            }
            return structure;
        }

        private static void AssertValidCompound(HostObjAttributes type, CompoundStructure structure)
        {
            if (!structure.IsValid(type.Document, out IDictionary<int, CompoundStructureError> errors,
                out IDictionary<int, int> errorMap))
                throw new InvalidOperationException("Revit rejected compound structure: " + string.Join("; ", errors.Select(x => x.Key + "=" + x.Value)));
        }

        private static JObject VerifyCompound(HostObjAttributes type, CompoundPlan plan)
        {
            CompoundStructure structure = type?.GetCompoundStructure();
            IList<CompoundStructureLayer> layers = structure?.GetLayers();
            bool ok = layers != null && layers.Count == plan.Layers.Count;
            var rows = new JArray();
            for (int i = 0; i < plan.Layers.Count; i++)
            {
                LayerPlan wanted = plan.Layers[i]; CompoundStructureLayer actual = layers != null && i < layers.Count ? layers[i] : null;
                bool layerOk = actual != null && actual.Function == wanted.Function &&
                    Math.Abs(actual.Width - wanted.Width) <= 1e-9 && actual.MaterialId == wanted.MaterialId &&
                    structure.ParticipatesInWrapping(i) == wanted.Wraps;
                if (actual != null && wanted.Function == MaterialFunctionAssignment.StructuralDeck)
                    layerOk = layerOk && actual.DeckProfileId == wanted.DeckProfileId && actual.DeckEmbeddingType == wanted.DeckEmbedding;
                ok = ok && layerOk;
                rows.Add(new JObject
                {
                    ["index"] = i, ["function"] = actual?.Function.ToString(), ["width_internal"] = actual?.Width,
                    ["material_id"] = actual == null ? JValue.CreateNull() : new JValue(Rid.Value(actual.MaterialId)),
                    ["wraps"] = actual == null ? JValue.CreateNull() : new JValue(structure.ParticipatesInWrapping(i)), ["verified"] = layerOk
                });
            }
            bool settingsOk = structure != null &&
                structure.GetNumberOfShellLayers(ShellLayerType.Exterior) == plan.ExteriorShells &&
                structure.GetNumberOfShellLayers(ShellLayerType.Interior) == plan.InteriorShells &&
                structure.StructuralMaterialIndex == plan.StructuralIndex && structure.VariableLayerIndex == plan.VariableIndex &&
                structure.EndCap == plan.EndCap && structure.OpeningWrapping == plan.OpeningWrapping;
            ok = ok && settingsOk;
            return new JObject
            {
                ["verified"] = ok, ["settings_verified"] = settingsOk, ["layers"] = rows,
                ["exterior_shell_layers"] = structure?.GetNumberOfShellLayers(ShellLayerType.Exterior),
                ["interior_shell_layers"] = structure?.GetNumberOfShellLayers(ShellLayerType.Interior),
                ["structural_layer_index"] = structure?.StructuralMaterialIndex,
                ["variable_layer_index"] = structure?.VariableLayerIndex,
                ["end_cap"] = structure?.EndCap.ToString(), ["opening_wrapping"] = structure?.OpeningWrapping.ToString()
            };
        }

        private static bool TryEnum<T>(string raw, out T value) where T : struct
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                string fallback = typeof(T) == typeof(EndCapCondition) ? "None" :
                    typeof(T) == typeof(OpeningWrappingCondition) ? "None" : "Standalone";
                return Enum.TryParse(fallback, true, out value) && Enum.IsDefined(typeof(T), value);
            }
            return Enum.TryParse(raw, true, out value) && Enum.IsDefined(typeof(T), value);
        }

        private static bool TryScale(string units, out double scale)
        {
            if (units == "feet") { scale = 1; return true; }
            if (units == "m") { scale = 1 / 0.3048; return true; }
            if (units == "mm") { scale = 1 / 304.8; return true; }
            scale = 0; return false;
        }
        private static double Finite(double value, string field)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite"); return value; }
        private static void ValidateValue(Parameter parameter, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                if (parameter.StorageType != StorageType.ElementId) throw new ArgumentException("null is only accepted to clear ElementId storage");
                return;
            }
            switch (parameter.StorageType)
            {
                case StorageType.String: return;
                case StorageType.Integer:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Boolean && value.Type != JTokenType.String)
                        throw new ArgumentException("Integer storage requires integer, boolean or unit-aware string"); return;
                case StorageType.Double:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float && value.Type != JTokenType.String)
                        throw new ArgumentException("Double storage requires number or unit-aware string");
                    if (value.Type != JTokenType.String) Finite(value.Value<double>(), "Double parameter value");
                    return;
                case StorageType.ElementId:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.String)
                        throw new ArgumentException("ElementId storage requires an integer id, numeric string or null"); return;
                default: throw new ArgumentException("unsupported storage type " + parameter.StorageType);
            }
        }
        private static void Apply(Parameter parameter, Write write)
        {
            JToken value = write.Requested;
            bool accepted;
            switch (parameter.StorageType)
            {
                case StorageType.String: accepted = parameter.Set(value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None)); break;
                case StorageType.Integer:
                    if (value.Type == JTokenType.String) { accepted = parameter.SetValueString(value.Value<string>()); write.ParsedByRevit = true; }
                    else accepted = parameter.Set(value.Type == JTokenType.Boolean ? (value.Value<bool>() ? 1 : 0) : value.Value<int>()); break;
                case StorageType.Double:
                    if (value.Type == JTokenType.String) { accepted = parameter.SetValueString(value.Value<string>()); write.ParsedByRevit = true; }
                    else accepted = parameter.Set(value.Value<double>()); break;
                case StorageType.ElementId:
                    long raw = value == null || value.Type == JTokenType.Null ? -1 : value.Value<long>();
                    if (!Rid.CanRepresent(raw)) throw new InvalidOperationException("ElementId value is outside range");
                    accepted = parameter.Set(Rid.Make(raw)); break;
                default: throw new InvalidOperationException("unsupported storage type " + parameter.StorageType);
            }
            if (!accepted) throw new InvalidOperationException("Revit rejected Set for parameter '" + write.Spec + "'");
            write.Expected = Read(parameter);
        }
        private static JToken Read(Parameter parameter)
        {
            if (parameter == null) return JValue.CreateNull();
            switch (parameter.StorageType)
            {
                case StorageType.String: return new JValue(parameter.AsString());
                case StorageType.Integer: return new JValue(parameter.AsInteger());
                case StorageType.Double: return new JValue(parameter.AsDouble());
                case StorageType.ElementId: return new JValue(Rid.Value(parameter.AsElementId()));
                default: return JValue.CreateNull();
            }
        }
        /// <summary>Identity for the plan, guarded: measuring must never be what fails.</summary>
        private static string SafePlanUniqueId(Element e)
        {
            try { return e == null ? null : e.UniqueId; } catch { return null; }
        }

        private static string SafePlanName(Element e)
        {
            try { return e == null ? null : e.Name; } catch { return "<unreadable>"; }
        }

        /// <summary>Stable JSON: Formatting.None so whitespace is never the difference.</summary>
        private static string Canon(JToken t)
        {
            try { return t == null ? "" : t.ToString(Formatting.None); } catch { return "<unreadable>"; }
        }

        /// <summary>
        /// What the named parameter reads on the SOURCE right now - the value the caller
        /// saw in the rehearsal and decided to override. AsValueString first so a length
        /// reads in the document's units the way the person read it; falls back to the raw
        /// string. "&lt;unreadable&gt;" stays distinct from "": an unreadable value must
        /// not compare equal to an empty one, or it drifts past the check.
        /// </summary>
        private static string SafePlanParamNow(ElementType source, string spec)
        {
            try
            {
                Parameter q = ResolveParameter(source, spec, out _);
                if (q == null) return "<unresolved>";
                try { string v = q.AsValueString(); if (v != null) return v; } catch { }
                try { return q.AsString() ?? ""; } catch { return "<unreadable>"; }
            }
            catch { return "<unreadable>"; }
        }

        private sealed class Plan { public int Index; public ElementType Source; public string NewName; public List<Write> Writes; public CompoundPlan Compound; public ElementId CreatedId; }
        private sealed class Write { public string Spec; public JToken Requested, Expected; public bool ParsedByRevit; }
        private sealed class CompoundPlan
        {
            public readonly List<LayerPlan> Layers = new List<LayerPlan>();
            public int ExteriorShells, InteriorShells, StructuralIndex, VariableIndex;
            public EndCapCondition EndCap; public OpeningWrappingCondition OpeningWrapping;
            public JObject Summary() => new JObject
            {
                ["layers"] = new JArray(Layers.Select((x, i) => new JObject
                {
                    ["index"] = i, ["function"] = x.Function.ToString(), ["width_internal"] = x.Width,
                    ["material_id"] = Rid.Value(x.MaterialId), ["wraps"] = x.Wraps
                })),
                ["exterior_shell_layers"] = ExteriorShells, ["interior_shell_layers"] = InteriorShells,
                ["structural_layer_index"] = StructuralIndex, ["variable_layer_index"] = VariableIndex,
                ["end_cap"] = EndCap.ToString(), ["opening_wrapping"] = OpeningWrapping.ToString()
            };
        }
        private sealed class LayerPlan
        {
            public MaterialFunctionAssignment Function; public double Width; public ElementId MaterialId, DeckProfileId;
            public bool Wraps; public StructDeckEmbeddingType DeckEmbedding;
        }
    }
}
