// -----------------------------------------------------------------------------
// Horizun Revit MCP - the shared discipline of one verified model edit.
//
// horizun_manage_curtain, horizun_slab_shape and horizun_create_railing each do
// ONE edit per call. The sequence around that edit is the same for all three and
// is the shape TransformElementsCommand set: gate the document, resolve the plan
// without a transaction, rehearse by default, spend a single-use confirmation,
// write inside a TransactionGroup, re-read the committed model through a
// PostconditionCheck and ROLL BACK when it disagrees, then re-read once more after
// the group assimilated. A read operation takes the read guard and never opens a
// transaction.
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
    /// <summary>What a planner resolved: either a read's answer, or an edit to apply and verify.</summary>
    internal sealed class ArchModelEdit
    {
        /// <summary>Set for a read: returned as is, nothing is written.</summary>
        public JObject ReadResult;
        /// <summary>What the rehearsal shows.</summary>
        public JObject Summary = new JObject();
        /// <summary>The elements the plan touches, fingerprinted into the confirmation.</summary>
        public readonly List<PlannedElement> Planned = new List<PlannedElement>();
        /// <summary>The write, inside the open transaction.</summary>
        public Action<Document> Apply;
        /// <summary>The re-read of the committed model; its AllVerified is the verdict.</summary>
        public Func<Document, PostconditionCheck> Verify;
        /// <summary>Facts the reply carries beside the checklist (created ids, measured values).</summary>
        public JObject Evidence = new JObject();
    }

    internal static class ModelEditRunner
    {
        /// <summary>Which postconditions failed, with expected and measured values, and a short evidence extract -
        /// in the TEXT, because that is what a probe or a person reads first.</summary>
        internal static string FailedText(PostconditionCheck check, JObject evidence)
        {
            try
            {
                var props = check?.ToJson()?["properties"] as JArray;
                var failed = props == null ? new System.Collections.Generic.List<string>() : props.OfType<JObject>()
                    .Where(x => x.Value<bool?>("matches") != true)
                    .Select(x => x.Value<string>("property") + " (requested " + x["requested"]?.ToString(Newtonsoft.Json.Formatting.None) +
                                 ", found " + x["found_in_committed_model"]?.ToString(Newtonsoft.Json.Formatting.None) + ")").ToList();
                string ev = evidence == null ? "" : evidence.ToString(Newtonsoft.Json.Formatting.None);
                if (ev.Length > 500) ev = ev.Substring(0, 500) + "...";
                return (failed.Count > 0 ? "Failed: " + string.Join("; ", failed) + "." : "No postcondition named the failure.") +
                       (ev.Length > 0 ? " Evidence: " + ev : "");
            }
            catch { return ""; }
        }


        public static CommandResult Run(UIApplication app, string paramsJson, string name, string defaultTransaction,
            Func<JObject, string> validate, Func<Document, JObject, double, ArchModelEdit> plan)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string invalid = validate(request);
            if (invalid != null) return CommandResult.Fail(invalid);
            double scale;
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (units == "feet") scale = 1; else if (units == "m") scale = 1 / 0.3048; else if (units == "mm") scale = 1 / 304.8;
            else return CommandResult.Fail("units must be mm, m or feet.");

            bool read = string.Equals(request.Value<string>("operation"), "read", StringComparison.OrdinalIgnoreCase);
            if (read)
            {
                Document active = null;
                try { active = app?.ActiveUIDocument?.Document; } catch { active = null; }
                if (active == null) return CommandResult.Fail("No document is active in Revit; there is nothing to read.");
                CommandResult refused = DocumentGate.ReadGuard(active, request, name);
                if (refused != null) return refused;
                try
                {
                    ArchModelEdit r = plan(active, request, scale);
                    return CommandResult.Ok(r.ReadResult ?? new JObject());
                }
                catch (Exception ex) { return CommandResult.Fail(ex.Message + " Nothing was read."); }
            }

            GateResult gate = DocumentGate.ForMutation(app, request, name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            ArchModelEdit edit;
            try { edit = plan(doc, request, scale); }
            catch (Exception ex)
            {
                var outcome = new ActionOutcome { Index = 0, Error = ex.Message, UnsupportedReason = UnsupportedCapability.ReasonOf(ex) };
                return FallbackDecision.Refuse(ex.Message + " Nothing was written.",
                    FallbackDecision.Decide(new[] { outcome }, writeStarted: false));
            }

            var resolved = new ResolvedPlan
            {
                Command = name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolved.Elements.AddRange(edit.Planned);
            string hash = DocumentGate.PlanHash(request, "units", "operation", "element_id", "grid_index", "direction",
                "offset", "point", "grid_line_id", "mode", "mullion_type_id", "segment_index", "panel_ids", "type_id",
                "points", "start", "end", "host_id", "placement", "path", "level_id", "base_offset");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["plan"] = edit.Summary,
                    ["note"] = "Nothing was written; no transaction was opened. The apply re-reads the committed model and rolls back on any disagreement."
                };
                DocumentGate.RecordResolvedPlan(resolved);
                ApplicationOutcome.StampRehearsal(result, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, name, hash, true,
                    "the token binds the operation, its arguments and the current state of every element it touches");
                return CommandResult.Ok(result);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, name, hash, resolved, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = defaultTransaction;
            PostconditionCheck check = null;
            using (var group = new TransactionGroup(doc, txName))
            {
                bool started = false;
                string said = "";
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction group did not start");
                    using (var tx = new Transaction(doc, txName))
                    {
                        RevitErrorRecorder recorder = RevitErrorRecorder.On(tx);
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                        started = true;
                        try { edit.Apply(doc); doc.Regenerate(); Guard.Commit(tx, txName); }
                        catch { said = recorder.Said(); throw; }
                    }
                    check = edit.Verify(doc);
                    if (!check.AllVerified)
                    {
                        var rolled = Guard.RollBack(group);
                        return CommandResult.FailWithDetail(
                            "The committed model disagreed with the request, so the whole edit was rolled back. " + FailedText(check, edit.Evidence),
                            new JObject
                            {
                                ["code"] = "postcondition_failed", ["write_started"] = true,
                                ["changes_applied"] = rolled.Confirmed ? (JToken)false : JValue.CreateNull(),
                                ["rollback_status"] = rolled.StatusName, ["postconditions"] = check.ToJson(),
                                ["evidence"] = edit.Evidence
                            });
                    }
                    Guard.Assimilate(group, txName);
                }
                catch (Exception ex)
                {
                    string rb = "not_attempted";
                    try { if (group.GetStatus() == TransactionStatus.Started) rb = Guard.RollBack(group).StatusName; }
                    catch (Exception e2) { rb = "failed: " + e2.Message; }
                    return CommandResult.FailWithDetail(name + " failed: " + ex.Message + said, new JObject
                    {
                        ["code"] = "revit_edit_failed", ["write_started"] = started,
                        ["changes_applied"] = !started ? (JToken)false : (rb == "RolledBack" ? (JToken)false : JValue.CreateNull()),
                        ["rollback_status"] = rb
                    });
                }
            }
            // Fresh re-read after the group assimilated: a disagreement now cannot be
            // undone by this call, so it is reported as committed and unverified.
            check = edit.Verify(doc);
            if (!check.AllVerified)
                return CommandResult.FailWithDetail("Committed, but the re-read after the group assimilated disagrees; inspect the model.",
                    new JObject { ["code"] = "postcommit_verification_failed", ["write_started"] = true, ["changes_applied"] = true,
                                  ["postconditions"] = check.ToJson(), ["evidence"] = edit.Evidence });
            var done = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["postconditions"] = check.ToJson(), ["evidence"] = edit.Evidence
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0);
            return CommandResult.Ok(done);
        }

        // ---- small shared readers ----------------------------------------------------

        public static XYZ Point(JToken token, double scale, string name, int dims = 3)
        {
            JArray a = token as JArray;
            if (a == null || a.Count != dims) throw new ArgumentException(name + " must contain " + dims + " coordinates");
            return new XYZ(a[0].Value<double>() * scale, a[1].Value<double>() * scale, dims == 3 ? a[2].Value<double>() * scale : 0);
        }

        public static T Need<T>(Document doc, JObject request, string field) where T : Element
        {
            long raw = request.Value<long?>(field) ?? -1;
            Element e = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) : null;
            if (e is T t) return t;
            throw new ArgumentException(field + " " + raw + " is " + (e == null ? "not an element of this document" : "a " + e.GetType().Name + ", not a " + typeof(T).Name) + ".");
        }

        public static PlannedElement Planned(Element e, PlannedAction action, JObject request)
        {
            string uid = null, geometry = null;
            try { uid = e.UniqueId; } catch { }
            try
            {
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                if (b != null)
                    geometry = string.Join(",", new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }
                        .Select(v => Math.Round(v * 304.8).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch { }
            return new PlannedElement
            {
                UniqueId = uid, ElementId = Rid.Value(e.Id), Category = e.Category?.Name, Action = action,
                GeometryFingerprint = geometry,
                BeforeValues = new Dictionary<string, string> { { "type_id", Rid.Value(e.GetTypeId()).ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                // NOT request.ToString(): the apply differs from its rehearsal by dry_run and
                // confirmation_token by construction, so hashing them made every apply of a
                // plan with a listed element refuse as stale (measured on Revit 2026,
                // 2026-09-24; the railing path route passed only because it lists none).
                ProposedValues = new Dictionary<string, string> { { "request", ArchitecturalEditRules.ProposedRequest(request) } }
            };
        }

        public static JArray Arr(XYZ p, double scale) => new JArray(Math.Round(p.X / scale, 4), Math.Round(p.Y / scale, 4), Math.Round(p.Z / scale, 4));
    }
}
