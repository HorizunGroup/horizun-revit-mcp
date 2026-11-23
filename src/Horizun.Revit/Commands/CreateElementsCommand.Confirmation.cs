using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            CommandResult preparationFailure = PrepareCreation(doc, request, out CreationBatch batch);
            if (preparationFailure != null) return preparationFailure;

            // Confirm the resolved model inputs along with the request. The same
            // freshly prepared rows are handed to confirmation and to the write.
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            var confirmation = new CreationConfirmation(app, gate, Name, CreationPlanHash(request),
                batch.Errors.Count == 0 ? batch.Plans : null, batch.Tabular);
            if (dryRun)
            {
                CommandResult preview = RehearseCreation(doc, request, batch, out JArray provisionalGeometry);
                if (preview.Success && preview.Data is JObject result) confirmation.Stamp(result, provisionalGeometry);
                return preview;
            }
            if (batch.Errors.Count > 0) return InvalidCreation(batch);
            CommandResult refusal = confirmation.Require(request);
            if (refusal != null) return refusal;
            CommandResult moved = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            return moved ?? ApplyCreation(doc, request, batch, confirmation);
        }

        private sealed class CreationBatch
        {
            public List<Plan> Plans;
            public JObject Tabular;
            public JArray Errors, Enclosed, Architecture, MepGraphWarnings;
            public List<ActionOutcome> Outcomes;
            public int InputCount;
            public string ValidationMode;
        }

        private sealed class CreationConfirmation
        {
            private const string WyeTypeKey = "wye_type_unique_id";
            private readonly UIApplication _app;
            private readonly GateResult _gate;
            private readonly string _commandName;
            private readonly string _hash;
            private readonly ResolvedPlan _plan;
            private readonly List<Plan> _plans;
            private readonly bool _externalSource;
            private string _approvedFingerprint;

            public CreationConfirmation(UIApplication app, GateResult gate, string commandName, string hash, List<Plan> plans,
                JObject tabularBlock)
            {
                // An atomic child reopens its external source (CSV, LandXML) after the parent graph recheck.
                _externalSource = tabularBlock != null || (plans?.Any(p => p.TopoSource != null) ?? false);
                _app = app;
                _gate = gate;
                _commandName = commandName;
                _hash = hash;
                _plans = plans;
                _plan = plans == null ? null : BuildConfirmationPlan(gate, app, commandName, plans, tabularBlock);
            }

            public void Stamp(JObject result, JArray provisionalGeometry)
            {
                if (_plan != null && provisionalGeometry != null)
                    foreach (JObject geometry in provisionalGeometry.OfType<JObject>())
                    {
                        int index = geometry.Value<int>("index");
                        PlannedElement target = _plan.Elements.FirstOrDefault(x => x.UniqueId == "create:" + index);
                        if (target != null && geometry["values"] is JObject values)
                            target.PreviewValues = values.Properties().ToDictionary(x => x.Name, x => x.Value.ToString(), StringComparer.Ordinal);
                    }
                if (_plan != null)
                {
                    RecordBuiltWyes();
                    DocumentGate.RecordResolvedPlan(_plan);
                }
                DocumentGate.StampConfirmation(result, _gate, _commandName, _hash, _plan != null,
                    _plan != null
                        ? "the token binds this ordered heterogeneous batch, its units, AND what its names resolved " +
                          "to right now - the types, system types and levels, including each level's measured " +
                          "elevation and the wye type actually built, when applicable. A type renamed, a name re-pointed or a level moved before you apply refuses " +
                          "as a stale plan instead of creating something else under the approved words."
                        : "no usable confirmation is issued while any row is invalid");
            }

            public CommandResult Require(JObject request)
            {
                if (DocumentGate.InConfirmedAtomicPlan)
                    _approvedFingerprint = request.Value<string>("__expected_plan_fingerprint");
                else
                {
                    ResolvedPlan rehearsal = DocumentGate.Confirmations.ReadRehearsedPlan(
                        request.Value<string>("confirmation_token"), _commandName, _gate.Fingerprint, _hash);
                    if (rehearsal != null)
                    {
                        var approvedRows = rehearsal.Elements.ToDictionary(row => row.UniqueId, StringComparer.Ordinal);
                        foreach (PlannedElement row in _plan.Elements)
                            if (approvedRows.TryGetValue(row.UniqueId, out PlannedElement approved))
                                row.ProposedValues = approved.ProposedValues;
                    }
                    _approvedFingerprint = _plan.Fingerprint();
                }
                string expectedChildPlan = _externalSource ? request.Value<string>("__expected_plan_fingerprint") : null;
                return _plan.RefuseChangedChild(expectedChildPlan, "creation") ??
                    DocumentGate.RequireConfirmation(_app, _gate, request, _commandName, _hash, _plan, null);
            }

            private void RecordBuiltWyes()
            {
                // Batches and resolved rows are built in the same order. Only stable type
                // identities go into the fingerprint, never the IDs of provisional fittings.
                for (int i = 0; i < _plans.Count; i++)
                {
                    string uid = _plans[i].BuiltWyeTypeUniqueId;
                    _plan.Elements[i].ProposedValues = uid == null ? null : new Dictionary<string, string> { [WyeTypeKey] = uid };
                }
            }

            public string BuiltPlanMismatch()
            {
                if (!_plans.Any(p => p.FittingSubtype == "tee")) return null;
                if (_approvedFingerprint == null && !_plans.Any(p => p.BuiltWyeTypeUniqueId != null)) return null;
                RecordBuiltWyes();
                return _plan.Fingerprint() == _approvedFingerprint ? null :
                    "The constructed pipe junction plan differs from the approved rehearsal, including any wye type. Run the dry run again.";
            }
        }

        private static ResolvedPlan BuildConfirmationPlan(GateResult gate, UIApplication app, string commandName, IEnumerable<Plan> plans,
            JObject tabularBlock)
        {
            var resolvedPlan = ResolveCreationPlan(commandName, gate.Fingerprint, app?.Application?.VersionNumber,
                gate.Identity?.FingerprintDigest(), tabularBlock);
            foreach (Plan planned in plans)
            {
                var facts = new Dictionary<string, string>
                {
                    { "type_uid", SafePlanUid(planned.Type) },
                    { "system_type", SafePlanName(planned.SystemType) },
                    { "system_type_uid", SafePlanUid(planned.SystemType) },
                    { "level", SafePlanName(planned.Level) },
                    { "level_uid", SafePlanUid(planned.Level) },
                    { "level_elev_mm", SafePlanElevation(planned.Level) },
                    { "host_uid", SafePlanUid(planned.Host) }
                };
                if (planned.ExtraPlanFacts != null)
                    foreach (KeyValuePair<string, string> fact in planned.ExtraPlanFacts)
                        facts[fact.Key] = fact.Value;
                var row = ArchitectureCreationRules.CreationPlanRow(planned.Index, planned.Kind, SafePlanName(planned.Type),
                    facts, SafePlanUid(planned.TopLevel), planned.TopLevel?.ProjectElevation, planned.Height);

                if (planned.Kind == "wall_foundation")
                {
                    row.BeforeValues["host_uid"] = SafePlanUid(planned.FoundationWall);
                    if (planned.FoundationWall?.Location is LocationCurve wallLocation && wallLocation.Curve != null)
                    {
                        row.BeforeValues["host_start_mm"] = SafePlanPoint(wallLocation.Curve.GetEndPoint(0));
                        row.BeforeValues["host_end_mm"] = SafePlanPoint(wallLocation.Curve.GetEndPoint(1));
                    }
                    Parameter baseOffset = planned.FoundationWall?.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                    row.BeforeValues["host_base_offset_mm"] = baseOffset?.StorageType == StorageType.Double
                        ? Math.Round(baseOffset.AsDouble() * 304.8, 1).ToString(CultureInfo.InvariantCulture) : "<unreadable>";
                }
                // WHERE, not only WHAT. A tabular_source batch is expanded from the CSV on
                // every call, and the request carries only the file's path: without the
                // placement in the plan, a file whose rows kept their type and level but
                // moved was applied with the rehearsal's token (backlog P0 #5).
                row.BeforeValues["start_mm"] = SafePlanPoint(planned.Start);
                row.BeforeValues["end_mm"] = SafePlanPoint(planned.End);
                row.BeforeValues["rotation_deg"] = System.Math.Round(planned.RotationRadians * 180.0 / System.Math.PI, 4)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (planned.DisplacedState != null) row.BeforeValues["displacement_source"] = planned.DisplacedState.ToString(Formatting.None);
                resolvedPlan.Elements.Add(row);
            }
            return resolvedPlan;
        }

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
        private static string SafePlanPoint(XYZ point)
        {
            if (point == null) return "";
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            return System.Math.Round(point.X * 304.8, 1).ToString(inv) + "," +
                   System.Math.Round(point.Y * 304.8, 1).ToString(inv) + "," +
                   System.Math.Round(point.Z * 304.8, 1).ToString(inv);
        }

        private static string SafePlanElevation(Level level)
        {
            try
            {
                if (level == null) return "";
                return System.Math.Round(level.ProjectElevation * 304.8, 1)
                             .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return "<unreadable>"; }
        }
    }
}
