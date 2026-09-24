// -----------------------------------------------------------------------------
// Horizun Revit MCP - MEP routing preferences, size catalogs and resizing.
// Original Horizun code.
//
// WHY THIS IS ITS OWN TOOL. 114 field scripts reached for RoutingPreferenceManager,
// PipeSegment sizes, ConduitSizeSettings and diameter writes through Python, because
// nothing typed covered them. horizun_manage_system_types was the tempting home and
// the wrong one: it DUPLICATES types and writes their parameters, and its verifier
// compares parameter values. A routing rule is not a parameter, a size catalog is
// not an element type, and a resize is an instance write whose side effects land on
// OTHER elements (the fittings Revit swaps or inserts). Folding three new shapes of
// verification into a duplicate-and-set tool would have made both harder to read.
//
// WHAT EACH OPERATION PROVES, re-read from the model after the commit:
//
//   set_rules    the rule list of every touched group equals the list computed from
//                the list read before plus the ordered edits (MepRoutingRules.Simulate),
//                and every group NOT touched still reads exactly as it did.
//   add_sizes    each size is in the catalog with the requested inner/outer/bend.
//   remove_sizes each size is gone. A size an element still uses is refused before
//                any write: removing it would orphan a run, and which run is not ours.
//                Every other size of the catalog re-reads unchanged, for both.
//   resize       each element's size parameters equal the request, the request was a
//                size of that element's own catalog, and every connector connected
//                before is still connected. The fittings Revit replaced, retyped or
//                inserted (transitions) are REPORTED, not hidden - they are the part of
//                a resize the caller did not write.
//
// size_by_flow writes nothing: it proposes, per element, the smallest catalog size
// that carries the element's flow at or below a velocity limit. Velocity is the only
// criterion it claims; Revit's own sizing dialog (friction, static regain) has no
// public API, so a friction-based size would be a guess this bridge will not label
// as Revit's.
//
// YEARS. RoutingPreferenceManager, RoutingPreferenceRule, PrimarySizeCriterion,
// Segment/PipeSegment sizes, DuctSizeSettings, ConduitSizeSettings and CableTraySizes
// read identically in the RevitAPI.xml of 2023, 2024, 2025, 2026 and 2027 (131
// members compared, no difference), so there is no per-year branch here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class MepRoutingCommand : ICommand
    {
        public string Name => "horizun_mep_routing";
        public string Description =>
            "Read and edit MEP routing preferences and size catalogs, resize runs to catalog sizes, and propose sizes by flow.";

        private const int MaxElements = 500;
        private const int MaxListed = 200;

        private static readonly RoutingPreferenceRuleGroupType[] Groups =
            Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)).Cast<RoutingPreferenceRuleGroupType>()
                .Where(g => g != RoutingPreferenceRuleGroupType.Undefined).ToArray();

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            double toFeet;
            if (!MepRoutingRules.TryUnitScale(request.Value<string>("units"), out toFeet))
                return CommandResult.Fail("units must be mm, in or feet.");
            var u = new Units(toFeet);

            if (op == "read" || op == "size_by_flow")
            {
                Document active = null;
                try { active = app?.ActiveUIDocument?.Document; } catch { }
                if (active == null) return CommandResult.Fail("No document is active in Revit; nothing was read.");
                CommandResult guard = DocumentGate.ReadGuard(active, request, Name);
                if (guard != null) return guard;
                try { return op == "read" ? Read(active, request, u) : SizeByFlow(active, request, u); }
                catch (Exception ex) { return CommandResult.Fail(op + " failed: " + ex.Message + " Nothing was written."); }
            }

            if (op != "set_rules" && op != "add_sizes" && op != "remove_sizes" && op != "resize")
                return CommandResult.Fail("operation must be read, set_rules, add_sizes, remove_sizes, resize or size_by_flow.");

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            WritePlan plan; string error; CommandResult refusal = null;
            try
            {
                if (op == "set_rules") plan = RulesPlan.Build(doc, request, u, out error);
                else if (op == "resize") plan = ResizePlan.Build(doc, request, u, out error, out refusal);
                else plan = SizesPlan.Build(doc, request, u, op == "add_sizes", out error);
            }
            catch (Exception ex) { plan = null; error = ex.Message; }
            if (refusal != null) return refusal;
            if (plan == null) return CommandResult.Fail(error + " Nothing was written.");

            string hash = DocumentGate.PlanHash(request, "operation", "units", "type_id", "segment_id", "catalog",
                "conduit_standard", "rules", "junction", "sizes", "element_ids", "system_id", "diameter", "width", "height");
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            ResolvedPlan resolved = plan.Resolved(gate, app, Name);

            if (dry)
            {
                Rehearsal r = Rehearse(doc, plan);
                if (!r.RollbackConfirmed)
                    return CommandResult.FailWithDetail("The rehearsal's rollback was not confirmed; the model state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["rollback_status"] = r.RollbackStatus, ["write_started"] = true });
                if (!r.Verified)
                    return CommandResult.FailWithDetail("The rehearsal could not verify the change" +
                        (r.Error == null ? "" : ": " + r.Error) + ". Nothing was committed.",
                        new JObject { ["state"] = "refused", ["rehearsal"] = r.Json });
                DocumentGate.RecordResolvedPlan(resolved);
                var result = new JObject { ["dry_run"] = true, ["operation"] = op, ["plan"] = plan.Describe(u), ["rehearsal"] = r.Json };
                ApplicationOutcome.StampRehearsal(result, plan.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, true,
                    "the token binds the request and the state read before the rehearsal (rule lists, catalog sizes, element sizes); apply re-plans against the model as it is then.");
                return CommandResult.Ok(result);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            string txName = "Horizun: mep routing " + op;
            PostconditionCheck final;
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started) return CommandResult.Fail("Could not start the TransactionGroup. Nothing was written.");
                var tx = new Transaction(doc, txName); TransactionStatus txStatus = TransactionStatus.Uninitialized;
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                    plan.Apply(doc);
                    doc.Regenerate();
                    PostconditionCheck before = plan.Verify(doc);
                    if (!before.AllVerified) throw new VerificationFailed("a postcondition failed while the change was reversible", before);
                    txStatus = tx.Commit();
                    if (txStatus != TransactionStatus.Committed) throw new InvalidOperationException("the transaction returned " + txStatus);
                    PostconditionCheck after = plan.Verify(doc);
                    if (!after.AllVerified) throw new VerificationFailed("a postcondition failed after the commit", after);
                    TransactionStatus gs = group.Assimilate();
                    if (gs != TransactionStatus.Committed)
                        return CommandResult.FailWithDetail("The TransactionGroup did not assimilate; the state is uncertain.",
                            new JObject { ["state"] = "uncertain", ["transaction_group_status"] = gs.ToString() });
                }
                catch (Exception ex)
                {
                    Guard.RollbackResult? txRollback = null;
                    try { if (tx.GetStatus() == TransactionStatus.Started) txRollback = Guard.RollBack(tx); } catch { }
                    Guard.RollbackResult? groupRollback;
                    try { groupRollback = Guard.RollBack(group); } catch { groupRollback = null; }
                    var detail = new JObject
                    {
                        ["state"] = groupRollback.HasValue && groupRollback.Value.Confirmed ? "rolled_back" : "uncertain",
                        ["transaction_status"] = txRollback.HasValue ? txRollback.Value.StatusName : txStatus.ToString(),
                        ["transaction_group_status"] = groupRollback.HasValue ? groupRollback.Value.StatusName : "Error"
                    };
                    if (ex is VerificationFailed vf) detail["postconditions"] = vf.Check.ToJson();
                    return CommandResult.FailWithDetail("The " + op + " change failed and was rolled back: " + ex.Message, detail);
                }
            }
            final = plan.Verify(doc);
            if (!final.AllVerified)
                return CommandResult.FailWithDetail("A postcondition contradicted the reversible verification after the group assimilated; the state is uncertain.",
                    new JObject { ["state"] = "uncertain", ["host_verified"] = false, ["postconditions"] = final.ToJson() });
            var done = new JObject
            {
                ["state"] = "committed_verified", ["operation"] = op, ["host_verified"] = true,
                ["postconditions"] = final.ToJson(), ["result"] = plan.Report(doc, u)
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, plan.Count, plan.Count, plan.Count, 0, 0, 0);
            DocumentGate.StampConfirmation(done, gate, Name, hash, false);
            return CommandResult.Ok(done);
        }

        // ---- the shared rehearsal ------------------------------------------------------

        private static Rehearsal Rehearse(Document doc, WritePlan plan)
        {
            var r = new Rehearsal(); PostconditionCheck check = null; JToken report = null;
            using (var tx = new Transaction(doc, "Horizun: rehearse mep routing"))
            {
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the rehearsal transaction did not start");
                    plan.Apply(doc); doc.Regenerate();
                    check = plan.Verify(doc); r.Verified = check.AllVerified;
                    report = plan.Report(doc, null);
                }
                catch (Exception ex) { r.Error = ex.Message; r.Verified = false; }
                try { Guard.RollbackResult rb = Guard.RollBack(tx); r.RollbackStatus = rb.StatusName; r.RollbackConfirmed = rb.Confirmed; }
                catch (Exception ex) { r.RollbackStatus = "exception: " + ex.Message; r.RollbackConfirmed = false; }
            }
            plan.ResetAfterRehearsal();
            r.Json = new JObject
            {
                ["constructible_and_verified"] = r.Verified, ["rollback_status"] = r.RollbackStatus,
                ["error"] = r.Error == null ? (JToken)JValue.CreateNull() : r.Error,
                ["postconditions"] = check == null ? (JToken)JValue.CreateNull() : check.ToJson(),
                ["observed"] = report ?? JValue.CreateNull()
            };
            return r;
        }

        private sealed class Rehearsal { public bool Verified, RollbackConfirmed; public string Error, RollbackStatus; public JObject Json; }

        private sealed class VerificationFailed : Exception
        {
            public readonly PostconditionCheck Check;
            public VerificationFailed(string message, PostconditionCheck check) : base(message) { Check = check; }
        }

        private abstract class WritePlan
        {
            public abstract int Count { get; }
            public abstract void Apply(Document doc);
            public abstract PostconditionCheck Verify(Document doc);
            public abstract JObject Describe(Units u);
            /// <summary>What the model shows after Apply; u null = the rehearsal (units of the request are applied by Describe).</summary>
            public virtual JToken Report(Document doc, Units u) => JValue.CreateNull();
            public virtual void ResetAfterRehearsal() { }
            public abstract ResolvedPlan Resolved(GateResult gate, UIApplication app, string command);

            protected static ResolvedPlan NewResolved(GateResult gate, UIApplication app, string command)
                => new ResolvedPlan { Command = command, DocumentKey = gate.Fingerprint, RevitVersion = app.Application.VersionNumber, DocumentFingerprint = gate.Identity.FingerprintDigest() };
        }

        private sealed class Units
        {
            public readonly double ToFeet;
            public Units(double toFeet) { ToFeet = toFeet; }
            public double Out(double feet) => Math.Round(feet / ToFeet, 4);
        }

        // ---- read ----------------------------------------------------------------------

        private CommandResult Read(Document doc, JObject request, Units u)
        {
            var result = new JObject { ["operation"] = "read", ["units"] = UnitName(request) };
            long typeId = request.Value<long?>("type_id") ?? -1, segmentId = request.Value<long?>("segment_id") ?? -1;
            if (typeId >= 0)
            {
                var type = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as MEPCurveType : null;
                if (type == null) return CommandResult.Fail("type_id " + typeId + " is not a pipe, duct, conduit or cable-tray type.");
                result["type"] = TypeJson(doc, type, u, true);
            }
            if (segmentId >= 0)
            {
                var segment = Rid.CanRepresent(segmentId) ? doc.GetElement(Rid.Make(segmentId)) as Segment : null;
                if (segment == null) return CommandResult.Fail("segment_id " + segmentId + " is not a pipe segment.");
                result["segment"] = SegmentJson(doc, segment, u, true);
            }
            JArray elementIds = request["element_ids"] as JArray;
            if (elementIds != null && elementIds.Count > 0)
            {
                if (elementIds.Count > MaxElements) return CommandResult.Fail("at most " + MaxElements + " element_ids per read.");
                var rows = new JArray();
                foreach (JToken t in elementIds)
                {
                    long id = t.Value<long>();
                    var row = new JObject { ["element_id"] = id };
                    var e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as MEPCurve : null;
                    string why = null;
                    Run r = e == null ? null : Classify(e, out why);
                    if (r == null) { row["reason"] = e == null ? "not a pipe, duct, conduit or cable tray" : why; rows.Add(row); continue; }
                    List<double> catalog = CatalogFor(doc, r, out string catalogName);
                    row["kind"] = r.Kind; row["size"] = r.Size(u); row["catalog"] = catalogName;
                    row["size_in_catalog"] = r.Before.All(v => MepRoutingRules.CatalogHas(catalog, v));
                    rows.Add(row);
                }
                result["elements"] = rows;
            }
            else if (typeId < 0 && segmentId < 0)
            {
                result["types"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(MEPCurveType)).Cast<MEPCurveType>()
                    .OrderBy(t => Rid.Value(t.Id)).Take(MaxListed).Select(t => TypeJson(doc, t, u, false)));
                result["segments"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(Segment)).Cast<Segment>()
                    .OrderBy(s => Rid.Value(s.Id)).Take(MaxListed).Select(s => SegmentJson(doc, s, u, false)));
                var duct = new JObject();
                foreach (DuctShape shape in new[] { DuctShape.Round, DuctShape.Rectangular, DuctShape.Oval })
                    duct[shape.ToString().ToLowerInvariant()] = SizesJson(DuctSizes(doc, shape), u);
                result["duct_sizes"] = duct;
                var conduit = new JObject();
                foreach (KeyValuePair<string, List<ConduitRow>> kv in ConduitStandards(doc))
                    conduit[kv.Key] = new JArray(kv.Value.Select(c => c.Json(u)));
                result["conduit_standards"] = conduit;
                result["cable_tray_sizes"] = SizesJson(CableTrayList(doc), u);
            }
            return CommandResult.Ok(result);
        }

        private static string UnitName(JObject request) => (request.Value<string>("units") ?? "mm").Trim().ToLowerInvariant();

        private static JObject TypeJson(Document doc, MEPCurveType type, Units u, bool detail)
        {
            var o = new JObject { ["id"] = Rid.Value(type.Id), ["name"] = type.Name, ["class"] = type.GetType().Name };
            try { o["shape"] = type.Shape.ToString(); } catch { }
            RoutingPreferenceManager rpm = Rpm(type);
            if (type is ConduitType ct) o["conduit_standard"] = ConduitStandardOf(ct);
            if (!detail) { o["has_routing_preferences"] = rpm != null; return o; }
            if (rpm == null)
            {
                o["routing_preferences"] = JValue.CreateNull();
                o["routing_preferences_note"] = "this type class carries no routing preferences; its fittings are the type's own elbow/tee/cross/transition/union.";
                var fit = new JObject();
                fit["elbow"] = PartRef(doc, Safe(() => type.Elbow)?.Id); fit["tee"] = PartRef(doc, Safe(() => type.Tee)?.Id);
                fit["cross"] = PartRef(doc, Safe(() => type.Cross)?.Id); fit["transition"] = PartRef(doc, Safe(() => type.Transition)?.Id);
                fit["union"] = PartRef(doc, Safe(() => type.Union)?.Id);
                o["fittings"] = fit;
                return o;
            }
            o["preferred_junction"] = Safe(() => rpm.PreferredJunctionType.ToString());
            var groups = new JObject();
            foreach (RoutingPreferenceRuleGroupType g in Groups)
            {
                List<RuleRead> rules = ReadGroup(rpm, g, out string why);
                if (rules == null) { groups[g.ToString()] = new JObject { ["unavailable"] = why }; continue; }
                groups[g.ToString()] = new JArray(rules.Select(r => r.Json(doc, u)));
            }
            o["rule_groups"] = groups;
            // What a rule can name: the fittings of the type's domain, and every pipe segment.
            BuiltInCategory cat = type is PipeType ? BuiltInCategory.OST_PipeFitting : BuiltInCategory.OST_DuctFitting;
            List<FamilySymbol> parts = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(cat)
                .Cast<FamilySymbol>().OrderBy(s => Rid.Value(s.Id)).ToList();
            o["available_fittings_total"] = parts.Count;
            o["available_fittings"] = new JArray(parts.Take(MaxListed).Select(s => new JObject { ["id"] = Rid.Value(s.Id), ["name"] = s.FamilyName + ": " + s.Name }));
            return o;
        }

        private static JObject SegmentJson(Document doc, Segment s, Units u, bool detail)
        {
            var o = new JObject { ["id"] = Rid.Value(s.Id), ["name"] = s.Name };
            o["material"] = NameOf(doc, Safe(() => s.MaterialId));
            if (s is PipeSegment ps) o["schedule"] = NameOf(doc, Safe(() => ps.ScheduleTypeId));
            o["roughness"] = Safe(() => (double?)s.Roughness);
            List<MEPSize> sizes = Safe(() => s.GetSizes().ToList()) ?? new List<MEPSize>();
            o["size_count"] = sizes.Count;
            if (detail) o["sizes"] = SizesJson(sizes, u);
            return o;
        }

        private static JArray SizesJson(IEnumerable<MEPSize> sizes, Units u)
            => new JArray((sizes ?? Enumerable.Empty<MEPSize>()).OrderBy(s => s.NominalDiameter).Select(s => new JObject
            {
                ["nominal"] = u.Out(s.NominalDiameter), ["inner"] = u.Out(s.InnerDiameter), ["outer"] = u.Out(s.OuterDiameter),
                ["used_in_size_lists"] = s.UsedInSizeLists, ["used_in_sizing"] = s.UsedInSizing
            }));

        private static JToken PartRef(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return JValue.CreateNull();
            Element e = doc.GetElement(id);
            string name = e is FamilySymbol fs ? fs.FamilyName + ": " + fs.Name : e?.Name;
            return new JObject { ["id"] = Rid.Value(id), ["name"] = name };
        }

        private static string NameOf(Document doc, ElementId id)
            => id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;

        // ---- catalogs --------------------------------------------------------------------

        private static RoutingPreferenceManager Rpm(MEPCurveType type)
        {
            if (!(type is PipeType) && !(type is DuctType)) return null;
            try { return type.RoutingPreferenceManager; } catch { return null; }
        }

        private static List<MEPSize> DuctSizes(Document doc, DuctShape shape)
        {
            var list = new List<MEPSize>();
            DuctSizeSettings settings = DuctSizeSettings.GetDuctSizeSettings(doc);
            if (settings == null) return list;
            DuctSizes sizes = settings[shape];
            if (sizes == null) return list;
            foreach (MEPSize s in sizes) list.Add(s);
            return list;
        }

        private static List<MEPSize> CableTrayList(Document doc)
        {
            var list = new List<MEPSize>();
            CableTraySizes sizes = CableTraySizes.GetCableTraySizes(doc);
            if (sizes == null) return list;
            foreach (MEPSize s in sizes) list.Add(s);
            return list;
        }

        private sealed class ConduitRow
        {
            public double Nominal, Inner, Outer, Bend; public bool Lists, Sizing;
            public JObject Json(Units u) => new JObject
            {
                ["nominal"] = u.Out(Nominal), ["inner"] = u.Out(Inner), ["outer"] = u.Out(Outer), ["bend_radius"] = u.Out(Bend),
                ["used_in_size_lists"] = Lists, ["used_in_sizing"] = Sizing
            };
        }

        private static SortedDictionary<string, List<ConduitRow>> ConduitStandards(Document doc)
        {
            var result = new SortedDictionary<string, List<ConduitRow>>(StringComparer.Ordinal);
            ConduitSizeSettings settings = ConduitSizeSettings.GetConduitSizeSettings(doc);
            if (settings == null) return result;
            foreach (KeyValuePair<string, ConduitSizes> kv in settings)
            {
                var rows = new List<ConduitRow>();
                foreach (ConduitSize c in kv.Value)
                    rows.Add(new ConduitRow { Nominal = c.NominalDiameter, Inner = c.InnerDiameter, Outer = c.OuterDiameter, Bend = c.BendRadius, Lists = c.UsedInSizeLists, Sizing = c.UsedInSizing });
                result[kv.Key] = rows.OrderBy(r => r.Nominal).ToList();
            }
            return result;
        }

        private static string ConduitStandardOf(ConduitType type)
        {
            Parameter p = Safe(() => type.get_Parameter(BuiltInParameter.CONDUIT_STANDARD_TYPE_PARAM));
            if (p == null) return null;
            return Safe(() => p.StorageType == StorageType.String ? p.AsString() : p.AsValueString());
        }

        // ---- rules -----------------------------------------------------------------------

        private sealed class RuleRead
        {
            public ElementId PartId; public string Description; public List<double[]> Criteria = new List<double[]>(); public string Other;
            public string Signature => Rid.Value(PartId) + "|" + (Description ?? "") + "|" +
                (Other ?? string.Join(";", Criteria.Select(c => R(c[0]) + "~" + R(c[1]))));
            public JObject Json(Document doc, Units u)
            {
                var o = new JObject { ["part"] = PartRef(doc, PartId), ["description"] = Description ?? "" };
                o["size_ranges"] = Other != null ? (JToken)Other
                    : new JArray(Criteria.Select(c => new JObject { ["min"] = u.Out(c[0]), ["max"] = u.Out(c[1]) }));
                return o;
            }
        }

        internal static string R(double v) => Math.Round(v, 9).ToString("R", CultureInfo.InvariantCulture);

        private static RuleRead ReadRule(RoutingPreferenceRule rule)
        {
            var r = new RuleRead { PartId = rule.MEPPartId, Description = rule.Description };
            for (int i = 0; i < rule.NumberOfCriteria; i++)
            {
                RoutingCriterionBase c = rule.GetCriterion(i);
                if (c is PrimarySizeCriterion p) r.Criteria.Add(new[] { p.MinimumSize, p.MaximumSize });
                else r.Other = "criterion " + i + " is a " + (c?.GetType().Name ?? "null") + ", which this tool does not copy";
            }
            return r;
        }

        private static List<RuleRead> ReadGroup(RoutingPreferenceManager rpm, RoutingPreferenceRuleGroupType g, out string why)
        {
            why = null;
            try
            {
                int n = rpm.GetNumberOfRules(g);
                var list = new List<RuleRead>();
                for (int i = 0; i < n; i++) list.Add(ReadRule(rpm.GetRule(g, i)));
                return list;
            }
            catch (Exception ex) { why = ex.Message; return null; }
        }

        private sealed class RulesPlan : WritePlan
        {
            private MEPCurveType _type;
            private string _junction, _junctionBefore;
            private readonly Dictionary<RoutingPreferenceRuleGroupType, List<string>> _before = new Dictionary<RoutingPreferenceRuleGroupType, List<string>>();
            private readonly Dictionary<RoutingPreferenceRuleGroupType, List<string>> _expected = new Dictionary<RoutingPreferenceRuleGroupType, List<string>>();
            private readonly List<Edit> _edits = new List<Edit>();


            private sealed class Edit
            {
                public RoutingPreferenceRuleGroupType Group; public string Action; public int? Index, ToIndex;
                public ElementId Part; public string Description; public double? Min, Max; public string Signature;
            }

            public override int Count => _edits.Count + (_junction != null ? 1 : 0);

            public static WritePlan Build(Document doc, JObject request, Units u, out string error)
            {
                error = null;
                long typeId = request.Value<long?>("type_id") ?? -1;
                var p = new RulesPlan();
                p._type = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as MEPCurveType : null;
                if (p._type == null) { error = "set_rules needs type_id naming a pipe or duct type."; return null; }
                RoutingPreferenceManager rpm = Rpm(p._type);
                if (rpm == null) { error = "type " + typeId + " (" + p._type.GetType().Name + ") has no routing preferences; conduit and cable-tray fittings are type parameters (horizun_manage_system_types)."; return null; }
                p._junctionBefore = rpm.PreferredJunctionType.ToString();
                string junction = request.Value<string>("junction");
                if (junction != null)
                {
                    if (!Enum.TryParse(junction, false, out PreferredJunctionType _)) { error = "junction must be Tee or Tap."; return null; }
                    p._junction = junction;
                }
                foreach (RoutingPreferenceRuleGroupType g in Groups)
                {
                    List<RuleRead> rules = ReadGroup(rpm, g, out string _);
                    if (rules != null) p._before[g] = rules.Select(r => r.Signature).ToList();
                }
                PrimarySizeCriterion all = PrimarySizeCriterion.All();
                JArray raw = request["rules"] as JArray;
                if ((raw == null || raw.Count == 0) && junction == null) { error = "set_rules needs rules and/or junction."; return null; }
                var perGroup = new Dictionary<RoutingPreferenceRuleGroupType, List<MepRoutingRules.RuleEdit>>();
                for (int i = 0; i < (raw?.Count ?? 0); i++)
                {
                    var a = raw[i] as JObject; string at = "rules[" + i + "]";
                    if (a == null) { error = at + " is not an object."; return null; }
                    if (!Enum.TryParse(a.Value<string>("group") ?? "", false, out RoutingPreferenceRuleGroupType g) || g == RoutingPreferenceRuleGroupType.Undefined)
                    { error = at + ".group must be one of " + string.Join(", ", Groups) + "."; return null; }
                    if (!p._before.ContainsKey(g)) { error = at + ": group " + g + " cannot be read on this type."; return null; }
                    var e = new Edit { Group = g, Action = (a.Value<string>("action") ?? "").ToLowerInvariant(), Index = a.Value<int?>("index"), ToIndex = a.Value<int?>("to_index") };
                    if (e.Action == "add")
                    {
                        long pid = a.Value<long?>("part_id") ?? -1;
                        Element part = Rid.CanRepresent(pid) ? doc.GetElement(Rid.Make(pid)) : null;
                        if (g == RoutingPreferenceRuleGroupType.Segments ? !(part is Segment) : !(part is FamilySymbol))
                        { error = at + ".part_id " + pid + " must name a " + (g == RoutingPreferenceRuleGroupType.Segments ? "pipe segment" : "fitting type (FamilySymbol)") + " for group " + g + "."; return null; }
                        e.Part = part.Id; e.Description = a.Value<string>("description") ?? "";
                        double? min = a.Value<double?>("min_size"), max = a.Value<double?>("max_size");
                        e.Min = min.HasValue ? min.Value * u.ToFeet : all.MinimumSize;
                        e.Max = max.HasValue ? max.Value * u.ToFeet : all.MaximumSize;
                        if (e.Min < 0 || e.Max < e.Min) { error = at + ": min_size must be >= 0 and <= max_size."; return null; }
                        e.Signature = Rid.Value(e.Part) + "|" + e.Description + "|" + R(e.Min.Value) + "~" + R(e.Max.Value);
                    }
                    else if (e.Action != "remove" && e.Action != "move") { error = at + ".action must be add, remove or move."; return null; }
                    p._edits.Add(e);
                    if (!perGroup.TryGetValue(g, out var list)) perGroup[g] = list = new List<MepRoutingRules.RuleEdit>();
                    list.Add(new MepRoutingRules.RuleEdit { Action = e.Action, Index = e.Index, ToIndex = e.ToIndex, Added = e.Signature });
                }
                foreach (var kv in perGroup)
                {
                    List<string> expected = MepRoutingRules.Simulate(p._before[kv.Key], kv.Value, out string why);
                    if (expected == null) { error = "group " + kv.Key + ": " + why; return null; }
                    // A moved rule is re-created from what was read; a criterion this tool cannot copy is refused, not dropped.
                    if (kv.Value.Any(x => x.Action == "move") && p._before[kv.Key].Any(s => s.Contains("which this tool does not copy")))
                    { error = "group " + kv.Key + " holds a rule with a criterion other than a size range; moving rules there is refused rather than dropping it."; return null; }
                    p._expected[kv.Key] = expected;
                }
                return p;
            }

            public override void Apply(Document doc)
            {
                RoutingPreferenceManager rpm = Rpm(_type) ?? throw new InvalidOperationException("the routing preferences are no longer readable");
                foreach (Edit e in _edits)
                {
                    if (e.Action == "add")
                    {
                        var rule = new RoutingPreferenceRule(e.Part, e.Description);
                        rule.AddCriterion(new PrimarySizeCriterion(e.Min.Value, e.Max.Value));
                        if (e.Index.HasValue) rpm.AddRule(e.Group, rule, e.Index.Value); else rpm.AddRule(e.Group, rule);
                    }
                    else if (e.Action == "remove") rpm.RemoveRule(e.Group, e.Index.Value);
                    else
                    {
                        RoutingPreferenceRule old = rpm.GetRule(e.Group, e.Index.Value);
                        RuleRead read = ReadRule(old);
                        var copy = new RoutingPreferenceRule(read.PartId, read.Description ?? "");
                        foreach (double[] c in read.Criteria) copy.AddCriterion(new PrimarySizeCriterion(c[0], c[1]));
                        rpm.RemoveRule(e.Group, e.Index.Value);
                        rpm.AddRule(e.Group, copy, e.ToIndex.Value);
                    }
                }
                if (_junction != null) rpm.PreferredJunctionType = (PreferredJunctionType)Enum.Parse(typeof(PreferredJunctionType), _junction);
            }

            public override PostconditionCheck Verify(Document doc)
            {
                var required = _expected.Keys.Select(g => "group:" + g).ToList();
                required.Add("untouched_groups");
                if (_junction != null) required.Add("junction");
                var check = new PostconditionCheck(required.ToArray());
                var type = doc.GetElement(_type.Id) as MEPCurveType;
                RoutingPreferenceManager rpm = type == null ? null : Rpm(type);
                foreach (var kv in _expected)
                {
                    List<RuleRead> now = rpm == null ? null : ReadGroup(rpm, kv.Key, out string _);
                    if (now == null) { check.Unreadable("group:" + kv.Key, new JArray(kv.Value), "the group could not be re-read"); continue; }
                    var found = now.Select(r => r.Signature).ToList();
                    check.Record("group:" + kv.Key, new JArray(kv.Value), new JArray(found), found.SequenceEqual(kv.Value, StringComparer.Ordinal));
                }
                var untouchedBefore = new JObject(); var untouchedNow = new JObject(); bool same = rpm != null;
                foreach (var kv in _before.Where(k => !_expected.ContainsKey(k.Key)))
                {
                    untouchedBefore[kv.Key.ToString()] = kv.Value.Count;
                    List<RuleRead> now = rpm == null ? null : ReadGroup(rpm, kv.Key, out string _);
                    untouchedNow[kv.Key.ToString()] = now == null ? (JToken)JValue.CreateNull() : now.Count;
                    if (now == null || !now.Select(r => r.Signature).SequenceEqual(kv.Value, StringComparer.Ordinal)) same = false;
                }
                check.Record("untouched_groups", untouchedBefore, untouchedNow, same);
                if (_junction != null)
                {
                    string now = rpm == null ? null : Safe(() => rpm.PreferredJunctionType.ToString());
                    if (now == null) check.Unreadable("junction", _junction, "the preferred junction could not be re-read");
                    else check.Compare("junction", _junction, now);
                }
                return check;
            }

            public override JObject Describe(Units u) => new JObject
            {
                ["type_id"] = Rid.Value(_type.Id), ["type"] = _type.Name,
                ["junction_before"] = _junctionBefore, ["junction"] = _junction,
                ["groups"] = new JObject(_expected.Select(kv => new JProperty(kv.Key.ToString(), new JObject
                {
                    ["before"] = new JArray(_before[kv.Key]), ["after"] = new JArray(kv.Value)
                }))),
                ["rule_signature"] = "part_id|description|min~max in feet"
            };

            public override ResolvedPlan Resolved(GateResult gate, UIApplication app, string command)
            {
                var rp = NewResolved(gate, app, command);
                var before = new Dictionary<string, string> { ["junction"] = _junctionBefore };
                foreach (var kv in _before) before[kv.Key.ToString()] = string.Join("/", kv.Value);
                rp.Elements.Add(new PlannedElement { UniqueId = _type.UniqueId, ElementId = Rid.Value(_type.Id), Category = "mep_curve_type", TypeName = _type.Name, Action = PlannedAction.Modify, BeforeValues = before });
                return rp;
            }
        }

        // ---- sizes -----------------------------------------------------------------------

        private sealed class SizesPlan : WritePlan
        {
            private bool _add; private string _catalog, _standard; private Segment _segment;
            private readonly List<double[]> _sizes = new List<double[]>(); // nominal, inner, outer, bend (feet)
            private List<double> _beforeNominals;
            public override int Count => _sizes.Count;

            public static WritePlan Build(Document doc, JObject request, Units u, bool add, out string error)
            {
                error = null;
                var p = new SizesPlan { _add = add, _catalog = (request.Value<string>("catalog") ?? "").ToLowerInvariant() };
                string[] catalogs = { "segment", "conduit", "duct_round", "duct_rectangular", "duct_oval", "cable_tray" };
                if (!catalogs.Contains(p._catalog)) { error = "catalog must be one of " + string.Join(", ", catalogs) + "."; return null; }
                if (p._catalog == "segment")
                {
                    long sid = request.Value<long?>("segment_id") ?? -1;
                    p._segment = Rid.CanRepresent(sid) ? doc.GetElement(Rid.Make(sid)) as Segment : null;
                    if (p._segment == null) { error = "catalog=segment needs segment_id naming a pipe segment."; return null; }
                }
                if (p._catalog == "conduit")
                {
                    p._standard = request.Value<string>("conduit_standard");
                    if (string.IsNullOrWhiteSpace(p._standard) || !ConduitStandards(doc).ContainsKey(p._standard))
                    { error = "catalog=conduit needs conduit_standard, one of: " + string.Join(", ", ConduitStandards(doc).Keys) + "."; return null; }
                }
                p._beforeNominals = p.Nominals(doc);
                JArray raw = request["sizes"] as JArray;
                if (raw == null || raw.Count < 1 || raw.Count > 100) { error = "sizes must hold 1..100 entries."; return null; }
                for (int i = 0; i < raw.Count; i++)
                {
                    var s = raw[i] as JObject; string at = "sizes[" + i + "]";
                    double? nominal = s?.Value<double?>("nominal");
                    if (nominal == null || !(nominal > 0)) { error = at + ".nominal must be a positive size."; return null; }
                    double n = nominal.Value * u.ToFeet;
                    if (p._sizes.Any(x => Math.Abs(x[0] - n) <= MepRoutingRules.SizeToleranceFeet)) { error = at + " repeats nominal " + nominal + "."; return null; }
                    bool present = MepRoutingRules.CatalogHas(p._beforeNominals, n);
                    if (add && present) { error = at + ": nominal " + nominal + " is already in the catalog; remove it first to change it."; return null; }
                    if (!add && !present) { error = at + ": nominal " + nominal + " is not in the catalog."; return null; }
                    double inner = n, outer = n, bend = 0;
                    if (add && (p._catalog == "segment" || p._catalog == "conduit"))
                    {
                        double? i2 = s.Value<double?>("inner"), o2 = s.Value<double?>("outer");
                        if (i2 == null || o2 == null || !(i2 > 0) || o2 < i2) { error = at + " needs inner and outer (outer >= inner > 0) for a " + p._catalog + " size."; return null; }
                        inner = i2.Value * u.ToFeet; outer = o2.Value * u.ToFeet;
                        if (p._catalog == "conduit")
                        {
                            double? b = s.Value<double?>("bend_radius");
                            if (b == null || !(b > 0)) { error = at + " needs a positive bend_radius for a conduit size."; return null; }
                            bend = b.Value * u.ToFeet;
                        }
                    }
                    if (!add)
                    {
                        List<long> users = p.UsersOf(doc, n);
                        if (users.Count > 0)
                        { error = at + ": nominal " + nominal + " is used by " + users.Count + " element(s) (" + string.Join(", ", users.Take(10)) + (users.Count > 10 ? ", ..." : "") + "); resize them first."; return null; }
                    }
                    p._sizes.Add(new[] { n, inner, outer, bend });
                }
                return p;
            }

            private List<double> Nominals(Document doc) => Rows(doc).Select(r => r[0]).ToList();

            /// <summary>Every size of the catalog: nominal, inner, outer, bend (feet).</summary>
            private List<double[]> Rows(Document doc)
            {
                switch (_catalog)
                {
                    case "segment":
                        var seg = doc.GetElement(_segment.Id) as Segment;
                        return seg == null ? null : seg.GetSizes().Select(s => new[] { s.NominalDiameter, s.InnerDiameter, s.OuterDiameter, 0.0 }).ToList();
                    case "conduit":
                        return ConduitStandards(doc).TryGetValue(_standard, out var rows) ? rows.Select(c => new[] { c.Nominal, c.Inner, c.Outer, c.Bend }).ToList() : null;
                    case "cable_tray":
                        return CableTrayList(doc).Select(s => new[] { s.NominalDiameter, s.InnerDiameter, s.OuterDiameter, 0.0 }).ToList();
                    default:
                        return DuctSizes(doc, Shape).Select(s => new[] { s.NominalDiameter, s.InnerDiameter, s.OuterDiameter, 0.0 }).ToList();
                }
            }

            private DuctShape Shape => _catalog == "duct_round" ? DuctShape.Round : _catalog == "duct_oval" ? DuctShape.Oval : DuctShape.Rectangular;

            private List<long> UsersOf(Document doc, double nominal)
            {
                var ids = new List<long>();
                bool Eq(Element e, BuiltInParameter bip)
                {
                    Parameter p = e.get_Parameter(bip);
                    return p != null && p.HasValue && Math.Abs(p.AsDouble() - nominal) <= MepRoutingRules.SizeToleranceFeet;
                }
                switch (_catalog)
                {
                    case "segment":
                        foreach (Pipe pipe in new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>())
                            if (pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM)?.AsElementId() == _segment.Id && Eq(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)) ids.Add(Rid.Value(pipe.Id));
                        break;
                    case "conduit":
                        foreach (Conduit c in new FilteredElementCollector(doc).OfClass(typeof(Conduit)).Cast<Conduit>())
                            if (ConduitStandardOf(doc.GetElement(c.GetTypeId()) as ConduitType) == _standard && Eq(c, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)) ids.Add(Rid.Value(c.Id));
                        break;
                    case "cable_tray":
                        foreach (CableTray t in new FilteredElementCollector(doc).OfClass(typeof(CableTray)).Cast<CableTray>())
                            if (Eq(t, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM) || Eq(t, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)) ids.Add(Rid.Value(t.Id));
                        break;
                    default:
                        ConnectorProfileType profile = Shape == DuctShape.Round ? ConnectorProfileType.Round : Shape == DuctShape.Oval ? ConnectorProfileType.Oval : ConnectorProfileType.Rectangular;
                        foreach (Duct d in new FilteredElementCollector(doc).OfClass(typeof(Duct)).Cast<Duct>())
                        {
                            if (Safe(() => (ConnectorProfileType?)d.DuctType.Shape) != profile) continue;
                            if (profile == ConnectorProfileType.Round ? Eq(d, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                                : Eq(d, BuiltInParameter.RBS_CURVE_WIDTH_PARAM) || Eq(d, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)) ids.Add(Rid.Value(d.Id));
                        }
                        break;
                }
                return ids;
            }

            public override void Apply(Document doc)
            {
                foreach (double[] s in _sizes)
                {
                    switch (_catalog)
                    {
                        case "segment":
                            if (_add) _segment.AddSize(new MEPSize(s[0], s[1], s[2], true, true)); else _segment.RemoveSize(s[0]);
                            break;
                        case "conduit":
                            ConduitSizeSettings cs = ConduitSizeSettings.GetConduitSizeSettings(doc);
                            if (_add) cs.AddSize(_standard, new ConduitSize(s[0], s[1], s[2], s[3], true, true)); else cs.RemoveSize(_standard, s[0]);
                            break;
                        case "cable_tray":
                            CableTraySizes ct = CableTraySizes.GetCableTraySizes(doc);
                            if (_add) ct.AddSize(new MEPSize(s[0], s[1], s[2], true, true));
                            else
                            {
                                MEPSize hit = CableTrayList(doc).FirstOrDefault(x => Math.Abs(x.NominalDiameter - s[0]) <= MepRoutingRules.SizeToleranceFeet)
                                              ?? throw new InvalidOperationException("the cable-tray size is no longer in the catalog");
                                ct.RemoveSize(hit);
                            }
                            break;
                        default:
                            DuctSizeSettings ds = DuctSizeSettings.GetDuctSizeSettings(doc);
                            if (_add) ds.AddSize(Shape, new MEPSize(s[0], s[1], s[2], true, true)); else ds.RemoveSize(Shape, s[0]);
                            break;
                    }
                }
            }

            public override PostconditionCheck Verify(Document doc)
            {
                var required = _sizes.Select(s => "size:" + R(s[0])).ToList(); required.Add("other_sizes_unchanged");
                var check = new PostconditionCheck(required.ToArray());
                List<double[]> rows = Rows(doc);
                foreach (double[] s in _sizes)
                {
                    string key = "size:" + R(s[0]);
                    if (rows == null) { check.Unreadable(key, R(s[0]), "the catalog could not be re-read"); continue; }
                    double[] hit = rows.FirstOrDefault(r => Math.Abs(r[0] - s[0]) <= MepRoutingRules.SizeToleranceFeet);
                    if (!_add) { check.Record(key, "absent", hit == null ? "absent" : "present", hit == null); continue; }
                    bool checkBore = _catalog == "segment" || _catalog == "conduit";
                    bool ok = hit != null && (!checkBore || (Near(hit[1], s[1]) && Near(hit[2], s[2]))) && (_catalog != "conduit" || Near(hit[3], s[3]));
                    check.Record(key, Row(s), hit == null ? (JToken)"absent" : Row(hit), ok);
                }
                List<double> expectedOthers = _beforeNominals.Where(n => !_sizes.Any(s => Math.Abs(s[0] - n) <= MepRoutingRules.SizeToleranceFeet)).OrderBy(n => n).ToList();
                if (rows == null) check.Unreadable("other_sizes_unchanged", expectedOthers.Count, "the catalog could not be re-read");
                else
                {
                    List<double> nowOthers = rows.Select(r => r[0]).Where(n => !_sizes.Any(s => Math.Abs(s[0] - n) <= MepRoutingRules.SizeToleranceFeet)).OrderBy(n => n).ToList();
                    bool same = nowOthers.Count == expectedOthers.Count && nowOthers.Zip(expectedOthers, (a, b) => Near(a, b)).All(x => x);
                    check.Record("other_sizes_unchanged", expectedOthers.Count, nowOthers.Count, same);
                }
                return check;
            }

            private static bool Near(double a, double b) => Math.Abs(a - b) <= MepRoutingRules.SizeToleranceFeet;
            private static JObject Row(double[] r) => new JObject { ["nominal_ft"] = R(r[0]), ["inner_ft"] = R(r[1]), ["outer_ft"] = R(r[2]), ["bend_radius_ft"] = R(r[3]) };

            public override JObject Describe(Units u) => new JObject
            {
                ["catalog"] = _catalog, ["segment_id"] = _segment == null ? (JToken)JValue.CreateNull() : Rid.Value(_segment.Id),
                ["conduit_standard"] = _standard, ["action"] = _add ? "add" : "remove",
                ["sizes"] = new JArray(_sizes.Select(s => new JObject { ["nominal"] = u.Out(s[0]), ["inner"] = u.Out(s[1]), ["outer"] = u.Out(s[2]), ["bend_radius"] = u.Out(s[3]) })),
                ["catalog_count_before"] = _beforeNominals.Count
            };

            public override ResolvedPlan Resolved(GateResult gate, UIApplication app, string command)
            {
                var rp = NewResolved(gate, app, command);
                string uid = _segment != null ? _segment.UniqueId : "catalog:" + _catalog + ":" + (_standard ?? "");
                rp.Elements.Add(new PlannedElement
                {
                    UniqueId = uid, ElementId = _segment == null ? (long?)null : Rid.Value(_segment.Id), Category = "size_catalog", Action = PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string> { ["nominals_ft"] = string.Join(",", _beforeNominals.OrderBy(n => n).Select(R)) },
                    ProposedValues = new Dictionary<string, string> { [_add ? "add" : "remove"] = string.Join(",", _sizes.Select(s => R(s[0]))) }
                });
                return rp;
            }
        }

        // ---- elements: resize and size_by_flow -------------------------------------------------

        private sealed class Run
        {
            public MEPCurve Element; public string Kind; // pipe | duct_round | duct_rectangular | duct_oval | conduit | cable_tray
            public double[] Before; // diameter or width,height (feet)
            public BuiltInParameter[] Params;
            public JObject Size(Units u) => Params.Length == 1 ? new JObject { ["diameter"] = u.Out(Before[0]) }
                : new JObject { ["width"] = u.Out(Before[0]), ["height"] = u.Out(Before[1]) };
        }

        private static Run Classify(MEPCurve e, out string why)
        {
            why = null; var r = new Run { Element = e };
            if (e is Pipe) { r.Kind = "pipe"; r.Params = new[] { BuiltInParameter.RBS_PIPE_DIAMETER_PARAM }; }
            else if (e is Conduit) { r.Kind = "conduit"; r.Params = new[] { BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM }; }
            else if (e is CableTray) { r.Kind = "cable_tray"; r.Params = new[] { BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM }; }
            else if (e is Duct d)
            {
                ConnectorProfileType shape = Safe(() => (ConnectorProfileType?)d.DuctType.Shape) ?? ConnectorProfileType.Invalid;
                if (shape == ConnectorProfileType.Round) { r.Kind = "duct_round"; r.Params = new[] { BuiltInParameter.RBS_CURVE_DIAMETER_PARAM }; }
                else if (shape == ConnectorProfileType.Rectangular || shape == ConnectorProfileType.Oval)
                { r.Kind = shape == ConnectorProfileType.Oval ? "duct_oval" : "duct_rectangular"; r.Params = new[] { BuiltInParameter.RBS_CURVE_WIDTH_PARAM, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM }; }
                else { why = "duct " + Rid.Value(e.Id) + " has no readable shape"; return null; }
            }
            else { why = "element " + Rid.Value(e.Id) + " is a " + e.GetType().Name + "; resize covers pipes, ducts, conduits and cable trays"; return null; }
            r.Before = new double[r.Params.Length];
            for (int i = 0; i < r.Params.Length; i++)
            {
                Parameter p = e.get_Parameter(r.Params[i]);
                if (p == null || !p.HasValue) { why = "element " + Rid.Value(e.Id) + " has no readable " + r.Params[i]; return null; }
                r.Before[i] = p.AsDouble();
            }
            return r;
        }

        /// <summary>The catalog the element's own size must come from, in feet.</summary>
        private static List<double> CatalogFor(Document doc, Run r, out string name)
        {
            switch (r.Kind)
            {
                case "pipe":
                    var seg = doc.GetElement(r.Element.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Segment;
                    name = seg == null ? "(no segment)" : "segment " + seg.Name;
                    return seg == null ? new List<double>() : seg.GetSizes().Select(s => s.NominalDiameter).ToList();
                case "conduit":
                    string std = ConduitStandardOf(doc.GetElement(r.Element.GetTypeId()) as ConduitType);
                    name = "conduit standard " + (std ?? "(unreadable)");
                    return std != null && ConduitStandards(doc).TryGetValue(std, out var rows) ? rows.Select(c => c.Nominal).ToList() : new List<double>();
                case "cable_tray": name = "cable-tray sizes"; return CableTrayList(doc).Select(s => s.NominalDiameter).ToList();
                case "duct_round": name = "round duct sizes"; return DuctSizes(doc, DuctShape.Round).Select(s => s.NominalDiameter).ToList();
                case "duct_oval": name = "oval duct sizes"; return DuctSizes(doc, DuctShape.Oval).Select(s => s.NominalDiameter).ToList();
                default: name = "rectangular duct sizes"; return DuctSizes(doc, DuctShape.Rectangular).Select(s => s.NominalDiameter).ToList();
            }
        }

        private static List<MEPCurve> Targets(Document doc, JObject request, out string error)
        {
            error = null; var list = new List<MEPCurve>();
            JArray ids = request["element_ids"] as JArray; long systemId = request.Value<long?>("system_id") ?? -1;
            if ((ids == null || ids.Count == 0) == (systemId < 0)) { error = "name the runs with element_ids OR system_id (exactly one)."; return null; }
            if (ids != null && ids.Count > 0)
            {
                foreach (JToken t in ids)
                {
                    long id = t.Value<long>(); Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (!(e is MEPCurve c)) { error = "element_ids " + id + " is not a pipe, duct, conduit or cable tray."; return null; }
                    if (list.Any(x => x.Id == c.Id)) { error = "element_ids repeats " + id + "."; return null; }
                    list.Add(c);
                }
            }
            else
            {
                var system = Rid.CanRepresent(systemId) ? doc.GetElement(Rid.Make(systemId)) as MEPSystem : null;
                ElementSet net = system is PipingSystem ps ? ps.PipingNetwork : system is MechanicalSystem ms ? ms.DuctNetwork : null;
                if (net == null) { error = "system_id " + systemId + " is not a piping or duct system with a readable network."; return null; }
                foreach (Element e in net) if (e is MEPCurve c && !(e is FlexPipe) && !(e is FlexDuct)) list.Add(c);
                if (list.Count == 0) { error = "system " + systemId + " holds no pipe or duct runs."; return null; }
            }
            if (list.Count > MaxElements) { error = "at most " + MaxElements + " runs per call."; return null; }
            return list.OrderBy(c => Rid.Value(c.Id)).ToList();
        }

        private sealed class Neighbor { public long Id; public long TypeId; public string Category; }

        private static Dictionary<long, Neighbor> NeighborsOf(Document doc, IEnumerable<ElementId> runIds)
        {
            var result = new Dictionary<long, Neighbor>();
            var runs = new HashSet<long>(runIds.Select(Rid.Value));
            foreach (ElementId id in runIds)
            {
                var run = doc.GetElement(id) as MEPCurve; if (run == null) continue;
                foreach (Connector c in MepFacts.Ordered(run.ConnectorManager))
                {
                    if (!Safe(() => (bool?)c.IsConnected).GetValueOrDefault()) continue;
                    foreach (Connector o in c.AllRefs)
                    {
                        Element owner = o.Owner; if (owner == null || runs.Contains(Rid.Value(owner.Id)) || !(owner is FamilyInstance)) continue;
                        long oid = Rid.Value(owner.Id);
                        if (!result.ContainsKey(oid)) result[oid] = new Neighbor { Id = oid, TypeId = Rid.Value(owner.GetTypeId()), Category = owner.Category?.Name };
                    }
                }
            }
            return result;
        }

        private static int ConnectedCount(MEPCurve run)
            => MepFacts.Ordered(run.ConnectorManager).Count(c => Safe(() => (bool?)c.IsConnected).GetValueOrDefault());

        private sealed class ResizePlan : WritePlan
        {
            private readonly List<Run> _runs = new List<Run>();
            private readonly Dictionary<long, int> _connectedBefore = new Dictionary<long, int>();
            private Dictionary<long, Neighbor> _neighborsBefore;
            private double[] _target;
            private readonly List<JObject> _unchanged = new List<JObject>();
            public override int Count => _runs.Count;

            public static WritePlan Build(Document doc, JObject request, Units u, out string error, out CommandResult refusal)
            {
                error = null; refusal = null;
                List<MEPCurve> targets = Targets(doc, request, out error);
                if (targets == null) return null;
                double? dia = request.Value<double?>("diameter"), w = request.Value<double?>("width"), h = request.Value<double?>("height");
                bool round = dia != null;
                if (round == (w != null || h != null) || (!round && (w == null || h == null)))
                { error = "resize needs diameter, or width AND height - not both."; return null; }
                var p = new ResizePlan { _target = round ? new[] { dia.Value * u.ToFeet } : new[] { w.Value * u.ToFeet, h.Value * u.ToFeet } };
                if (p._target.Any(x => !(x > 0) || double.IsInfinity(x))) { error = "sizes must be positive."; return null; }
                // Every element is judged before anything is refused, so the fallback verdict sees
                // the whole batch: an uncovered element (a flex run) beside a fixable one (a size
                // outside its catalog) must not license Python for the request.
                var outcomes = new List<ActionOutcome>();
                for (int i = 0; i < targets.Count; i++)
                {
                    MEPCurve e = targets[i];
                    var outcome = new ActionOutcome { Index = i };
                    outcomes.Add(outcome);
                    Run r = Classify(e, out string why);
                    if (r == null) { outcome.Error = why; continue; }
                    if (e is FlexPipe || e is FlexDuct) { outcome.UnsupportedReason = FallbackSignal.ReasonUnsupportedKind; outcome.Error += " - flex runs are unsupported by resize"; }
                    if ((r.Params.Length == 1) != round) { outcome.Error = "element " + Rid.Value(e.Id) + " is " + r.Kind + ", which is sized by " + (round ? "width and height" : "diameter"); continue; }
                    List<double> catalog = CatalogFor(doc, r, out string catalogName);
                    double missing = p._target.FirstOrDefault(v => !MepRoutingRules.CatalogHas(catalog, v));
                    if (missing > 0) { outcome.Error = "element " + Rid.Value(e.Id) + ": " + u.Out(missing) + " is not a size of its " + catalogName + " (" + catalog.Count + " sizes; read lists them)"; continue; }
                    if (r.Before.Zip(p._target, (a, b) => Math.Abs(a - b) <= MepRoutingRules.SizeToleranceFeet).All(x => x))
                    { p._unchanged.Add(new JObject { ["element_id"] = Rid.Value(e.Id), ["already"] = r.Size(u) }); continue; }
                    p._runs.Add(r);
                    p._connectedBefore[Rid.Value(e.Id)] = ConnectedCount(e);
                }
                if (outcomes.Any(o => o.Failed))
                {
                    refusal = FallbackDecision.Refuse(string.Join("; ", outcomes.Where(o => o.Failed).Select(o => o.Error)) + ". Nothing was written.",
                        FallbackDecision.Decide(outcomes, writeStarted: false));
                    return null;
                }
                if (p._runs.Count == 0) { error = "every named run is already that size; nothing would change."; return null; }
                p._neighborsBefore = NeighborsOf(doc, p._runs.Select(r => r.Element.Id));
                return p;
            }

            public override void Apply(Document doc)
            {
                foreach (Run r in _runs)
                    for (int i = 0; i < r.Params.Length; i++)
                    {
                        Parameter p = r.Element.get_Parameter(r.Params[i]);
                        if (p == null || p.IsReadOnly) throw new InvalidOperationException("element " + Rid.Value(r.Element.Id) + ": " + r.Params[i] + " is read-only");
                        if (!p.Set(_target[i])) throw new InvalidOperationException("element " + Rid.Value(r.Element.Id) + ": Revit refused " + r.Params[i]);
                    }
            }

            public override PostconditionCheck Verify(Document doc)
            {
                var required = new List<string>();
                foreach (Run r in _runs) { required.Add("size:" + Rid.Value(r.Element.Id)); required.Add("connections:" + Rid.Value(r.Element.Id)); }
                var check = new PostconditionCheck(required.ToArray());
                foreach (Run r in _runs)
                {
                    long id = Rid.Value(r.Element.Id);
                    var e = doc.GetElement(r.Element.Id) as MEPCurve;
                    if (e == null) { check.Unreadable("size:" + id, Arr(_target), "the run no longer exists"); check.Unreadable("connections:" + id, _connectedBefore[id], "the run no longer exists"); continue; }
                    var found = new double[r.Params.Length]; bool readable = true;
                    for (int i = 0; i < r.Params.Length; i++) { Parameter p = e.get_Parameter(r.Params[i]); if (p == null || !p.HasValue) readable = false; else found[i] = p.AsDouble(); }
                    if (!readable) check.Unreadable("size:" + id, Arr(_target), "a size parameter did not re-read");
                    else check.Record("size:" + id, Arr(_target), Arr(found), found.Zip(_target, (a, b) => Math.Abs(a - b) <= MepRoutingRules.SizeToleranceFeet).All(x => x));
                    int now = ConnectedCount(e);
                    check.Record("connections:" + id, _connectedBefore[id], now, now >= _connectedBefore[id]);
                }
                return check;
            }

            private static JArray Arr(double[] v) => new JArray(v.Select(R));

            public override JToken Report(Document doc, Units u)
            {
                Dictionary<long, Neighbor> after = NeighborsOf(doc, _runs.Select(r => r.Element.Id));
                var replaced = new JArray(); var retyped = new JArray(); var added = new JArray();
                foreach (Neighbor b in _neighborsBefore.Values)
                {
                    Element still = doc.GetElement(Rid.Make(b.Id));
                    if (still == null) replaced.Add(new JObject { ["element_id"] = b.Id, ["type_id"] = b.TypeId, ["category"] = b.Category });
                    else if (Rid.Value(still.GetTypeId()) != b.TypeId) retyped.Add(new JObject { ["element_id"] = b.Id, ["type_before"] = b.TypeId, ["type_after"] = Rid.Value(still.GetTypeId()) });
                }
                foreach (Neighbor a in after.Values.Where(a => !_neighborsBefore.ContainsKey(a.Id)))
                    added.Add(new JObject { ["element_id"] = a.Id, ["type_id"] = a.TypeId, ["category"] = a.Category, ["type"] = NameOf(doc, Rid.Make(a.TypeId)) });
                return new JObject
                {
                    ["fittings_removed_or_replaced"] = replaced, ["fittings_retyped"] = retyped, ["fittings_added"] = added,
                    ["note"] = "Revit resizes, swaps or inserts fittings (transitions) at the runs' ends by the type's routing preferences; these are the elements the request did not name."
                };
            }

            public override JObject Describe(Units u) => new JObject
            {
                ["runs"] = new JArray(_runs.Select(r => new JObject { ["element_id"] = Rid.Value(r.Element.Id), ["kind"] = r.Kind, ["before"] = r.Size(u) })),
                ["target"] = _target.Length == 1 ? new JObject { ["diameter"] = u.Out(_target[0]) } : new JObject { ["width"] = u.Out(_target[0]), ["height"] = u.Out(_target[1]) },
                ["skipped_already_that_size"] = new JArray(_unchanged),
                ["adjacent_fittings_before"] = _neighborsBefore.Count
            };

            public override ResolvedPlan Resolved(GateResult gate, UIApplication app, string command)
            {
                var rp = NewResolved(gate, app, command);
                foreach (Run r in _runs)
                    rp.Elements.Add(new PlannedElement
                    {
                        UniqueId = r.Element.UniqueId, ElementId = Rid.Value(r.Element.Id), Category = r.Kind, Action = PlannedAction.Modify,
                        BeforeValues = new Dictionary<string, string> { ["size_ft"] = string.Join("x", r.Before.Select(R)) },
                        ProposedValues = new Dictionary<string, string> { ["size_ft"] = string.Join("x", _target.Select(R)) }
                    });
                return rp;
            }
        }

        private CommandResult SizeByFlow(Document doc, JObject request, Units u)
        {
            double? vmax = request.Value<double?>("max_velocity");
            if (vmax == null || !(vmax > 0)) return CommandResult.Fail("size_by_flow needs max_velocity (m/s), a positive number.");
            double vFps = vmax.Value * MepRoutingRules.FeetPerSecondPerMetrePerSecond;
            double? flowOverride = request.Value<double?>("flow"), heightIn = request.Value<double?>("height");
            List<MEPCurve> targets = Targets(doc, request, out string error);
            if (targets == null) return CommandResult.Fail(error);
            var rows = new JArray(); var groups = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (MEPCurve e in targets)
            {
                var row = new JObject { ["element_id"] = Rid.Value(e.Id) };
                Run r = Classify(e, out string why);
                if (r == null || (r.Kind != "pipe" && r.Kind != "duct_round" && r.Kind != "duct_rectangular"))
                { row["reason"] = why ?? (r.Kind + " is not sized by flow here: only pipes, round and rectangular ducts."); rows.Add(row); continue; }
                row["kind"] = r.Kind; row["current"] = r.Size(u);
                double flowCfs = flowOverride.HasValue ? flowOverride.Value * MepRoutingRules.CubicFeetPerSecondPerLitrePerSecond
                    : (e.get_Parameter(r.Kind == "pipe" ? BuiltInParameter.RBS_PIPE_FLOW_PARAM : BuiltInParameter.RBS_DUCT_FLOW_PARAM)?.AsDouble() ?? double.NaN);
                row["flow_lps"] = double.IsNaN(flowCfs) ? (JToken)JValue.CreateNull() : Math.Round(flowCfs / MepRoutingRules.CubicFeetPerSecondPerLitrePerSecond, 4);
                row["flow_source"] = flowOverride.HasValue ? "request" : "model";
                MepRoutingRules.SizingResult s; JObject proposed;
                if (r.Kind == "pipe")
                {
                    var seg = doc.GetElement(e.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Segment;
                    var options = seg == null ? new List<MepRoutingRules.SizeOption>() : seg.GetSizes().Where(z => z.UsedInSizing)
                        .Select(z => new MepRoutingRules.SizeOption { Nominal = z.NominalDiameter, Bore = z.InnerDiameter }).ToList();
                    s = MepRoutingRules.SmallestRound(flowCfs, vFps, options);
                    proposed = s.Found ? new JObject { ["diameter"] = u.Out(s.Nominal) } : null;
                    row["area_basis"] = "segment inner diameter";
                }
                else if (r.Kind == "duct_round")
                {
                    s = MepRoutingRules.SmallestRound(flowCfs, vFps, DuctSizes(doc, DuctShape.Round).Where(z => z.UsedInSizing)
                        .Select(z => new MepRoutingRules.SizeOption { Nominal = z.NominalDiameter, Bore = z.NominalDiameter }));
                    proposed = s.Found ? new JObject { ["diameter"] = u.Out(s.Nominal) } : null;
                    row["area_basis"] = "nominal diameter";
                }
                else
                {
                    double hFeet = heightIn.HasValue ? heightIn.Value * u.ToFeet : r.Before[1];
                    s = MepRoutingRules.NarrowestRectangle(flowCfs, vFps, hFeet, DuctSizes(doc, DuctShape.Rectangular).Where(z => z.UsedInSizing).Select(z => z.NominalDiameter));
                    proposed = s.Found ? new JObject { ["width"] = u.Out(s.Nominal), ["height"] = u.Out(hFeet) } : null;
                    row["area_basis"] = "width x height held at " + u.Out(hFeet) + (heightIn.HasValue ? " (request)" : " (current)");
                }
                if (!s.Found) { row["reason"] = s.Reason; rows.Add(row); continue; }
                row["proposed"] = proposed;
                row["velocity_mps"] = Math.Round(s.VelocityFeetPerSecond / MepRoutingRules.FeetPerSecondPerMetrePerSecond, 3);
                bool changes = !r.Before.Zip(r.Params.Length == 1 ? new[] { s.Nominal } : new[] { s.Nominal, heightIn.HasValue ? heightIn.Value * u.ToFeet : r.Before[1] },
                    (a, b) => Math.Abs(a - b) <= MepRoutingRules.SizeToleranceFeet).All(x => x);
                row["changes"] = changes;
                rows.Add(row);
                if (!changes) continue;
                string key = proposed.ToString(Newtonsoft.Json.Formatting.None);
                if (!groups.TryGetValue(key, out JObject g))
                {
                    g = new JObject { ["operation"] = "resize", ["units"] = UnitName(request), ["element_ids"] = new JArray() };
                    foreach (var prop in proposed.Properties()) g[prop.Name] = prop.Value;
                    groups[key] = g;
                }
                ((JArray)g["element_ids"]).Add(Rid.Value(e.Id));
            }
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "size_by_flow", ["writes"] = false, ["max_velocity_mps"] = vmax.Value, ["rows"] = rows,
                ["resize_calls"] = new JArray(groups.Values),
                ["method"] = "smallest catalog size (used_in_sizing) whose free area carries the flow at or below max_velocity. Velocity only: no friction or pressure drop - Revit's sizing dialog has no public API. Apply a proposal with operation=resize."
            });
        }

        private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default(T); } }
    }
}
