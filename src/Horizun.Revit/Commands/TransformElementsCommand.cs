// -----------------------------------------------------------------------------
// Horizun Revit MCP - generic transforms with post-commit location verification.
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
    public sealed class TransformElementsCommand : ICommand
    {
        public string Name => "horizun_transform_elements";
        public string Description => "Move, copy, rotate, pin or change type atomically and verify from the committed model.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray input = request["operations"] as JArray;
            if (input == null || input.Count == 0) return CommandResult.Fail("operations is required and must be non-empty.");
            if (input.Count > 500) return CommandResult.Fail("operations exceeds 500 entries.");
            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            var plans = new List<Plan>();
            var errors = new JArray();
            var claimed = new HashSet<long>();
            // Every action's outcome, so the fallback is decided once over the whole
            // batch rather than granted because one entry was uncovered.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < input.Count; i++)
            {
                string error = null, reason = null;
                Plan plan = PlanOperation(doc, i, input[i] as JObject, scale, claimed, out error, out reason);
                if (plan == null)
                {
                    string message = error ?? "entry is not an object";
                    errors.Add(new JObject { ["index"] = i, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else plans.Add(plan);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "operations");

            // ---- The MATERIALISED plan: the ELEMENTS this resolves to, right now. ----
            // planHash above binds the REQUEST - the operations as written. It cannot see
            // that the model moved: an operation naming a filter, or a type whose instances
            // changed, resolves to a different set of elements without a single character of
            // the request changing. Built identically here on both paths, so the dry run's
            // fingerprint rides in the token and the apply recomputes it.
            //
            // CONTRIBUTING points new commands at this file as the shape to copy, which is
            // exactly why the plan belongs here: whatever this command does, the next ten
            // will do.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Plan p in plans)
            {
                foreach (ElementId id in (p.Ids ?? new List<ElementId>()))
                {
                    Element e = doc.GetElement(id);
                    if (e == null) continue;
                    var planned = new PlannedElement
                    {
                        UniqueId = SafePlanUniqueId(e),
                        Category = e.Category == null ? null : e.Category.Name,
                        TypeName = SafePlanTypeName(doc, e),
                        // change_type REPLACES the type; move/rotate/copy do not. Both are
                        // modifications of this element, and 'copy' additionally creates -
                        // but the created ids do not exist yet at plan time, so what is
                        // fingerprinted is the SOURCE set and the operation, which is what
                        // the caller actually approved.
                        Action = PlannedAction.Modify,
                        // The geometry the operation is ABOUT. A move approved against an
                        // element somebody has since moved is a different move, and only a
                        // position read can show that.
                        GeometryFingerprint = SafePlanGeometry(e),
                        BeforeValues = new Dictionary<string, string>
                        {
                            { "operation", p.Operation ?? "" },
                            { "type_id", p.TypeId == null ? "" : Rid.Value(p.TypeId).ToString() }
                        }
                    };
                    resolvedPlan.Elements.Add(planned);
                }
            }

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["operations"] = input.Count,
                    ["targets"] = claimed.Count, ["valid_operations"] = plans.Count, ["invalid_operations"] = errors.Count,
                    ["errors"] = errors, ["plan"] = new JArray(plans.Select(p => p.Summary)),
                    ["note"] = "Nothing was transformed; no transaction was opened."
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds the ordered operations, targets, vectors, axes and types" :
                    "no usable confirmation is issued while any operation is invalid");
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so this
                // is the first call a caller makes; without the block here they got
                // success=true with invalid rows and no way to tell a capability gap
                // from a typo except by sending an apply they had no reason to send.
                return FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
                return FallbackDecision.Refuse(
                    "Invalid operations; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            // The rehearsed fingerprint travels in the token; the rehearsed PLAN does not,
            // so a stale refusal names the drift generically rather than per element.
            // Still refused, still nothing written.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                    resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: transform elements";
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    foreach (Plan p in plans) Apply(doc, p);
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Atomic transform failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing was transformed"));
                }
            }

            var verification = new JArray();
            int verified = 0;
            foreach (Plan p in plans)
            {
                bool ok = Verify(doc, p, out JObject detail);
                detail["index"] = p.Index; detail["operation"] = p.Operation; detail["verified"] = ok;
                verification.Add(detail); if (ok) verified++;
            }
            if (verified != plans.Count)
                return CommandResult.Fail("The transaction committed, but " + (plans.Count - verified) +
                    " operation(s) failed post-commit verification. Inspect the model. " + verification.ToString(Formatting.None));
            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["operations_verified"] = verified, ["targets"] = claimed.Count, ["rows"] = verification
            });
        }

        private static Plan PlanOperation(Document doc, int index, JObject o, double scale, HashSet<long> claimed,
                                          out string error, out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            if (o == null) { error = "entry is not an object"; return null; }
            try
            {
                string op = (o.Value<string>("operation") ?? "").ToLowerInvariant();
                if (op != "move" && op != "copy" && op != "rotate" && op != "pin" && op != "unpin" && op != "change_type")
                    throw new UnsupportedCapability(
                        "unsupported operation '" + op + "' - horizun_transform_elements does move, copy, " +
                        "rotate, pin, unpin and change_type only. Nothing was written.",
                        FallbackSignal.ReasonUnsupportedOperation);
                JArray ids = o["element_ids"] as JArray;
                if (ids == null || ids.Count == 0 || ids.Count > 2000) throw new ArgumentException("element_ids must contain 1..2000 ids");
                var p = new Plan { Index = index, Operation = op, Ids = new List<ElementId>(), Samples = new Dictionary<long, List<XYZ>>() };
                foreach (JToken token in ids)
                {
                    long raw = token.Value<long>();
                    if (!Rid.CanRepresent(raw)) throw new ArgumentException("ElementId " + raw + " is outside the supported range");
                    Element element = doc.GetElement(Rid.Make(raw));
                    if (element == null) throw new ArgumentException("ElementId " + raw + " does not exist in the host document");
                    if (!claimed.Add(raw)) throw new ArgumentException("ElementId " + raw + " appears in more than one operation; sequential transforms are ambiguous, combine them deliberately first");
                    p.Ids.Add(element.Id);
                    if (op == "move" || op == "rotate")
                    {
                        List<XYZ> sample = Samples(element);
                        if (sample.Count == 0) throw new ArgumentException("ElementId " + raw + " has no LocationPoint/LocationCurve sample, so " + op + " cannot be verified");
                        p.Samples[raw] = sample;
                    }
                }
                if (op == "move" || op == "copy") p.Vector = Point(o["vector"], scale, "vector");
                if (op == "rotate")
                {
                    XYZ a = Point(o["axis_start"], scale, "axis_start"), b = Point(o["axis_end"], scale, "axis_end");
                    if (a.DistanceTo(b) < 1e-9) throw new ArgumentException("rotation axis endpoints must differ");
                    p.Axis = Line.CreateBound(a, b);
                    if (o["angle_degrees"] == null) throw new ArgumentException("angle_degrees is required for rotate");
                    p.Angle = o.Value<double>("angle_degrees") * Math.PI / 180.0;
                    p.Rotation = Transform.CreateRotationAtPoint((b - a).Normalize(), p.Angle, a);
                }
                if (op == "change_type")
                {
                    long typeId = o.Value<long?>("type_id") ?? -1;
                    if (!Rid.CanRepresent(typeId) || !(doc.GetElement(Rid.Make(typeId)) is ElementType))
                        throw new ArgumentException("type_id must identify an ElementType");
                    p.TypeId = Rid.Make(typeId);
                    foreach (ElementId id in p.Ids)
                        if (!doc.GetElement(id).IsValidType(p.TypeId))
                            throw new ArgumentException("type_id " + typeId + " is not valid for ElementId " + Rid.Value(id));
                }
                p.Summary = new JObject { ["index"] = index, ["operation"] = op, ["targets"] = p.Ids.Count, ["verifiable"] = true };
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }

        private static void Apply(Document doc, Plan p)
        {
            switch (p.Operation)
            {
                case "move": ElementTransformUtils.MoveElements(doc, p.Ids, p.Vector); break;
                case "copy": p.Created = ElementTransformUtils.CopyElements(doc, p.Ids, p.Vector).ToList(); break;
                case "rotate": ElementTransformUtils.RotateElements(doc, p.Ids, p.Axis, p.Angle); break;
                case "pin": foreach (ElementId id in p.Ids) doc.GetElement(id).Pinned = true; break;
                case "unpin": foreach (ElementId id in p.Ids) doc.GetElement(id).Pinned = false; break;
                case "change_type": foreach (ElementId id in p.Ids) doc.GetElement(id).ChangeTypeId(p.TypeId); break;
            }
        }

        private static bool Verify(Document doc, Plan p, out JObject detail)
        {
            detail = new JObject { ["targets"] = p.Ids.Count };
            if (p.Operation == "copy")
            {
                int present = p.Created == null ? 0 : p.Created.Count(id => doc.GetElement(id) != null);
                detail["copies_expected"] = p.Ids.Count; detail["copies_present"] = present;
                detail["created_ids"] = new JArray((p.Created ?? new List<ElementId>()).Select(id => (JToken)Rid.Value(id)));
                return present == p.Ids.Count;
            }
            int good = 0;
            foreach (ElementId id in p.Ids)
            {
                Element e = doc.GetElement(id); if (e == null) continue;
                if (p.Operation == "pin" && e.Pinned) good++;
                else if (p.Operation == "unpin" && !e.Pinned) good++;
                else if (p.Operation == "change_type" && e.GetTypeId() == p.TypeId) good++;
                else if (p.Operation == "move" || p.Operation == "rotate")
                {
                    List<XYZ> after = Samples(e), before = p.Samples[Rid.Value(id)];
                    if (after.Count != before.Count) continue;
                    bool same = MatchesSamples(before, after, p, false);
                    // Revit may normalize a LocationCurve by reversing its endpoint
                    // order. That is the same committed geometry, not a failed move.
                    if (!same && before.Count == 2) same = MatchesSamples(before, after, p, true);
                    if (same) good++;
                }
            }
            detail["targets_verified"] = good;
            return good == p.Ids.Count;
        }

        private static bool MatchesSamples(List<XYZ> before, List<XYZ> after, Plan p, bool reverse)
        {
            for (int i = 0; i < before.Count; i++)
            {
                XYZ expected = p.Operation == "move" ? before[i] + p.Vector : p.Rotation.OfPoint(before[i]);
                int actualIndex = reverse ? after.Count - 1 - i : i;
                if (expected.DistanceTo(after[actualIndex]) > 1e-6) return false;
            }
            return true;
        }

        private static List<XYZ> Samples(Element e)
        {
            var result = new List<XYZ>();
            LocationPoint point = e.Location as LocationPoint;
            if (point != null) { result.Add(point.Point); return result; }
            LocationCurve curve = e.Location as LocationCurve;
            if (curve != null && curve.Curve != null)
            { result.Add(curve.Curve.GetEndPoint(0)); result.Add(curve.Curve.GetEndPoint(1)); }
            return result;
        }
        private static XYZ Point(JToken token, double scale, string name)
        {
            JArray a = token as JArray;
            if (a == null || a.Count != 3) throw new ArgumentException(name + " must contain three coordinates");
            return new XYZ(a[0].Value<double>() * scale, a[1].Value<double>() * scale, a[2].Value<double>() * scale);
        }
        private static bool Scale(string units, out double scale)
        { if (units == "feet") { scale = 1; return true; } if (units == "m") { scale = 1 / 0.3048; return true; } if (units == "mm") { scale = 1 / 304.8; return true; } scale = 0; return false; }

        /// <summary>
        /// Identity for the plan. Every read here is guarded: a plan that throws while
        /// being MEASURED would turn a safety feature into a new way to fail.
        /// </summary>
        private static string SafePlanUniqueId(Element e)
        {
            try { return e.UniqueId; } catch { return null; }
        }

        private static string SafePlanTypeName(Document doc, Element e)
        {
            try
            {
                ElementId tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) return null;
                Element t = doc.GetElement(tid);
                return t == null ? null : t.Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// A rounded bounding box: enough to notice that an element moved or changed shape
        /// between the rehearsal and the apply, cheap enough to take for every element in a
        /// batch. Rounded to a millimetre because Revit's own regeneration jitters the last
        /// digits, and a fingerprint that changes on its own would refuse every apply.
        /// </summary>
        private static string SafePlanGeometry(Element e)
        {
            try
            {
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                if (b == null) return null;
                const double mm = 304.8;   // feet -> mm
                return string.Join(",", new[]
                {
                    Math.Round(b.Min.X * mm), Math.Round(b.Min.Y * mm), Math.Round(b.Min.Z * mm),
                    Math.Round(b.Max.X * mm), Math.Round(b.Max.Y * mm), Math.Round(b.Max.Z * mm)
                }.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            }
            catch { return null; }
        }

        private sealed class Plan
        {
            public int Index; public string Operation; public List<ElementId> Ids, Created; public XYZ Vector;
            public Line Axis; public double Angle; public Transform Rotation; public ElementId TypeId;
            public Dictionary<long, List<XYZ>> Samples; public JObject Summary;
        }
    }
}
