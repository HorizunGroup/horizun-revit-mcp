// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_apply_cad_update - carry out an incremental plan AND remember it.
//
// The update could be sent through horizun_execute_plan, and for one run it would
// work. The second run is the problem: elements created that way carry no
// provenance, so the next update reads them as things the drawing asks for and
// nothing has built, and creates them AGAIN. Measured live, 2026-08-27 - two
// walls where the drawing shows one.
//
// So this command exists for the same reason horizun_apply_cad_plan does. It
// creates nothing itself: creates go through the typed horizun_create_elements
// and re-shapes through the typed horizun_transform_elements, both of which
// rehearse, confirm and re-read their own work. What it adds is the memory - a
// provenance stamp on everything it touches, carrying the entity, the rule, the
// requirement set, and the geometry AS BUILT, read back off the element after
// the commit rather than off the request.
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
    public sealed class ApplyCadUpdateCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        public ApplyCadUpdateCommand(Func<string, ICommand> resolve) { _resolve = resolve; }

        public string Name => "horizun_apply_cad_update";

        public string Description =>
            "Carry out an incremental CAD plan through the typed commands, and stamp what it touched.";

        public CommandResult Execute(UIApplication uiApp, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("The arguments are not valid JSON: " + ex.Message); }

            // THE SHARED GATE, not a hand-rolled title check.
            //
            // A repo guard caught this one: every command that opens a transaction
            // must resolve its document through DocumentGate.ForMutation, or it
            // writes to whatever window is in front. The hand-rolled version here
            // compared titles and looked equivalent - it is not, because the gate
            // is where the required target_document, the active-document rule and
            // the wording of the refusal all live, in ONE place that stays right
            // when any of them changes.
            GateResult gate = DocumentGate.ForMutation(uiApp, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = uiApp?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            string title = SafeTitle(doc);

            JToken actionsToken = request["actions"];
            JArray actions = actionsToken as JArray;
            if (actionsToken != null && actionsToken.Type != JTokenType.Null && actions == null)
                return CommandResult.Fail(
                    "actions_not_a_list: 'actions' arrived as " + actionsToken.Type.ToString().ToLowerInvariant() +
                    " and it must be the LIST horizun_plan_cad_update emitted. Nothing was written.");
            if (actions == null || actions.Count == 0)
                return CommandResult.Fail(
                    "actions is required and must carry at least one entry. An update plan with nothing " +
                    "automatic in it is a plan that is waiting for a person, and running it would write nothing " +
                    "while reporting success.");

            JObject provenanceTemplate = request["provenance"] as JObject;
            if (provenanceTemplate == null)
                return CommandResult.Fail(
                    "provenance is required: which drawing, which rules, which plan. Without it this command " +
                    "would create elements that remember nothing, and the NEXT update would build them again. " +
                    "Copy it from horizun_plan_cad_update's reply.");

            JArray index = request["candidate_index"] as JArray ?? new JArray();
            bool dryRun = request.Value<bool?>("dry_run") ?? true;

            // ------------------------------------------------------- rehearse
            var rehearsal = new JArray();
            var tokens = new JObject();
            bool clean = true;
            foreach (JObject action in actions.OfType<JObject>())
            {
                string key = action.Value<string>("key") ?? "";
                string tool = action.Value<string>("tool");
                ICommand child = tool == null ? null : _resolve(tool);
                if (child == null)
                {
                    rehearsal.Add(new JObject { ["key"] = key, ["tool"] = tool, ["ok"] = false,
                                                ["error"] = "no such command" });
                    clean = false;
                    continue;
                }
                JObject args = (JObject)(action["arguments"] ?? new JObject()).DeepClone();
                args["target_document"] = title;
                args["dry_run"] = true;
                CommandResult r = child.Execute(uiApp, args.ToString(Formatting.None));
                rehearsal.Add(new JObject
                {
                    ["key"] = key, ["tool"] = tool, ["ok"] = r.Success,
                    ["error"] = r.Success ? null : r.Error
                });
                if (!r.Success) { clean = false; continue; }
                string t = (r.Data as JObject)?.Value<string>("confirmation_token");
                if (t != null) tokens[key] = t;
            }

            // AN EXPLICIT BRANCH ON dry_run THAT RETURNS. A repo guard reads for
            // exactly this shape, because a rehearsal that falls through into the
            // write is the failure nobody notices until it has happened.
            if (dryRun || !clean)
            {
                var rehearsed = new JObject
                {
                    ["document"] = title,
                    ["dry_run"] = true,
                    ["wrote_nothing"] = true,
                    ["rehearsed_cleanly"] = clean,
                    ["rehearsal"] = rehearsal,
                    ["tokens_by_key"] = tokens,
                    ["means"] = clean
                        ? "every action rehearsed cleanly and nothing was written. Send the same actions with " +
                          "dry_run=false and each action's token from tokens_by_key."
                        : "at least one action would not rehearse, so NOTHING was written and no token was " +
                          "issued for the ones that did. Fix the failure and rehearse again."
                };
                if (dryRun) return CommandResult.Ok(rehearsed);
                return CommandResult.FailWithDetail(
                    "rehearsal_failed: at least one action in this update would not rehearse, so nothing " +
                    "was written. A partly-applied incremental update is the worst of both worlds: the " +
                    "model matches neither revision.", rehearsed);
            }

            // ---------------------------------------------------------- apply
            var applied = new JArray();
            var touched = new List<Touched>();
            int failures = 0;
            foreach (JObject action in actions.OfType<JObject>())
            {
                string key = action.Value<string>("key") ?? "";
                string tool = action.Value<string>("tool");
                ICommand child = _resolve(tool);
                JObject args = (JObject)(action["arguments"] ?? new JObject()).DeepClone();
                args["target_document"] = title;
                args["dry_run"] = false;
                if (tokens[key] != null) args["confirmation_token"] = tokens[key];
                args["idempotency_key"] = (request.Value<string>("idempotency_key") ?? "cad-update") + "-" + key;

                CommandResult r = child.Execute(uiApp, args.ToString(Formatting.None));
                var row = new JObject
                {
                    ["key"] = key, ["tool"] = tool, ["ok"] = r.Success,
                    ["error"] = r.Success ? null : r.Error
                };
                if (r.Success)
                {
                    // THE ROW INDEX, not the position in the reply. A create that
                    // skipped a row would otherwise stamp every following element
                    // with somebody else's origin - the same reasoning as
                    // horizun_apply_cad_plan, and the same cost if it is wrong.
                    foreach (Touched t in CreatedElements(r.Data, key)) touched.Add(t);
                    foreach (long id in TargetIds(args))
                        touched.Add(new Touched { Key = key, ElementId = id, RowIndex = null });
                    row["elements"] = new JArray(touched.Where(x => x.Key == key)
                                                        .Select(x => (JToken)x.ElementId));
                }
                else failures++;
                applied.Add(row);
                if (!r.Success) break;   // stop at the first failure; a half-updated model is nobody's revision
            }

            // ----------------------------------------------------- provenance
            var stamps = new JArray();
            int written = 0, anonymous = 0;
            using (var t = new Transaction(doc, "Horizun: record CAD update provenance"))
            {
                t.Start();
                foreach (Touched pair in touched)
                {
                    Element e = null;
                    try { if (Rid.CanRepresent(pair.ElementId)) e = doc.GetElement(Rid.Make(pair.ElementId)); } catch { }
                    if (e == null) continue;

                    JObject entry = index.OfType<JObject>().FirstOrDefault(x =>
                        string.Equals(x.Value<string>("key"), pair.Key, StringComparison.Ordinal) &&
                        (pair.RowIndex.HasValue
                            ? x.Value<int?>("element_index") == pair.RowIndex.Value
                            : x.Value<long?>("element_id") == pair.ElementId));
                    if (entry == null)
                    {
                        anonymous++;
                        stamps.Add(new JObject
                        {
                            ["element_id"] = pair.ElementId, ["key"] = pair.Key, ["written"] = false,
                            ["means"] = "candidate_index has no entry for this action, so nothing could be " +
                                        "stamped. The element exists and is ANONYMOUS: the next update will " +
                                        "build it again."
                        });
                        continue;
                    }

                    var p = new CadProvenance
                    {
                        SchemaVersion = CadProvenanceStore.CurrentVersion,
                        CandidateId = entry.Value<string>("candidate_id"),
                        GeometryId = entry.Value<string>("geometry_id"),
                        SemanticId = entry.Value<string>("semantic_id"),
                        RuleId = entry.Value<string>("rule_id"),
                        Layer = entry.Value<string>("layer"),
                        RequirementSetId = provenanceTemplate.Value<string>("requirement_set_id"),
                        RequirementSetVersion = provenanceTemplate.Value<string>("requirement_set_version"),
                        RequirementSetSha256 = provenanceTemplate.Value<string>("requirement_set_sha256"),
                        SourceFingerprint = provenanceTemplate.Value<string>("source_fingerprint"),
                        SourceFileSha256 = provenanceTemplate.Value<string>("source_file_sha256"),
                        PlanFingerprint = provenanceTemplate.Value<string>("plan_fingerprint"),
                        Confidence = entry.Value<double?>("confidence") ?? 0,
                        WrittenUtc = DateTime.UtcNow.ToString("o"),
                        BuiltGeometry = CadUpdateRules.Encode(PlanGeometry(e))
                    };

                    string why;
                    bool ok = CadProvenanceStore.Write(e, p, out why);
                    if (ok) written++; else anonymous++;
                    stamps.Add(new JObject
                    {
                        ["element_id"] = pair.ElementId,
                        ["key"] = pair.Key,
                        ["candidate_id"] = p.CandidateId,
                        ["semantic_id"] = p.SemanticId,
                        ["written"] = ok,
                        ["means"] = ok
                            ? "this element now remembers the entity in THIS revision that it stands for, so the " +
                              "next update recognises it instead of building it again"
                            : "the provenance entity did not land, so this element is ANONYMOUS and the next " +
                              "update will build it again. Revit said: " + (why ?? "(nothing)")
                    });
                }
                t.Commit();
            }

            var result = new JObject
            {
                ["document"] = title,
                ["dry_run"] = false,
                ["actions_attempted"] = applied.Count,
                ["actions_failed"] = failures,
                ["actions"] = applied,
                ["elements_touched"] = touched.Count,
                ["provenance_written"] = written,
                ["elements_left_anonymous"] = anonymous,
                ["provenance"] = stamps,
                ["state"] = failures == 0 ? "applied" : "partial",
                ["atomicity"] = "PER ACTION, not whole. Each typed command is atomic and verified in itself; the " +
                                "actions commit separately, and the run STOPS at the first failure rather than " +
                                "carrying on into a model that matches neither revision.",
                ["partial_means"] = failures == 0
                    ? null
                    : "an action failed and the ones after it did not run. What landed IS in the model and is " +
                      "stamped. Re-plan from the current drawing rather than re-sending this plan: the model has " +
                      "moved since it was made.",
                ["re_plan_note"] = "the elements stamped above now carry THIS revision, so planning the next " +
                                   "update against this drawing will read them as built rather than missing."
            };
            return CommandResult.Ok(result);
        }

        /// <summary>One element this run touched, and how to find the candidate that explains it.</summary>
        private sealed class Touched
        {
            public string Key;
            public long ElementId;
            /// <summary>The row index inside the create request; null for an element that already existed.</summary>
            public int? RowIndex;
        }

        /// <summary>
        /// What horizun_create_elements says it created: 'rows', each carrying the
        /// INDEX of the request row it came from. The first version read a
        /// 'created' array that does not exist, so nothing was ever stamped and
        /// the reply reported zero elements touched while the walls stood there.
        /// </summary>
        private static IEnumerable<Touched> CreatedElements(object data, string key)
        {
            var o = data as JObject;
            JArray rows = o?["rows"] as JArray;
            if (rows == null) yield break;
            foreach (JObject row in rows.OfType<JObject>())
            {
                long? id = row.Value<long?>("element_id");
                if (!id.HasValue) continue;
                yield return new Touched { Key = key, ElementId = id.Value, RowIndex = row.Value<int?>("index") };
            }
        }

        /// <summary>The elements a transform aimed at: a set_curve re-shapes one that already exists.</summary>
        private static IEnumerable<long> TargetIds(JObject args)
        {
            JArray ops = args["operations"] as JArray;
            if (ops == null) yield break;
            foreach (JObject op in ops.OfType<JObject>())
            {
                if (!string.Equals(op.Value<string>("operation"), "set_curve", StringComparison.Ordinal)) continue;
                foreach (JToken id in op["element_ids"] as JArray ?? new JArray())
                {
                    long value;
                    if (long.TryParse(id.ToString(), out value)) yield return value;
                }
            }
        }

        private static List<CadPoint> PlanGeometry(Element e)
        {
            var points = new List<CadPoint>();
            try
            {
                var curve = e.Location as LocationCurve;
                if (curve?.Curve != null)
                {
                    points.Add(Mm(curve.Curve.GetEndPoint(0)));
                    points.Add(Mm(curve.Curve.GetEndPoint(1)));
                    return points;
                }
                var point = e.Location as LocationPoint;
                if (point?.Point != null) points.Add(Mm(point.Point));
            }
            catch { }
            return points;
        }

        private static CadPoint Mm(XYZ p) =>
            new CadPoint(CadUnits.FeetToMm(p.X), CadUnits.FeetToMm(p.Y), CadUnits.FeetToMm(p.Z));

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
    }
}
