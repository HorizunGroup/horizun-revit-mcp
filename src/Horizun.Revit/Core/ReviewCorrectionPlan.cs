// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free and net48-safe.
//
// THE REVIEW -> CORRECTION -> RE-REVIEW CYCLE, the half that lives in this repo.
//
// horizun-review (another MCP server) reviews a PDF set and, with
// plan_correcciones_revit, proposes steps that a PARAMETER fixes in the model:
// each step names a typed tool of this bridge and its arguments, always with
// dry_run=true, sometimes with "<...>" placeholders for ids it cannot know. It
// never executes anything.
//
// This file decides, without a Revit:
//
//   Check     whether a plan may be run at all: every step names a published,
//             typed tool (never Python), still rehearses (dry_run=true), and every
//             placeholder is listed with what resolves it - a step with one left is
//             requires_input, never "ready".
//   Reverify  after the person approved and each step was applied with its token:
//             re-derive, step by step, that the model now carries what the PLAN
//             asked - by the plan's own targets and values, against the rows the
//             typed tool re-read after its commit. Not a count of calls made.
//             corrected / confirmed_by_reread_only / persistent / not_applied /
//             not_verifiable, rolled up in the honesty vocabulary.
//
// The re-review itself - re-export the sheets and run horizun-review again - is
// the other server's, and only its verdict closes a PDF finding. The reply says
// so, and lists the rules to watch in that delta.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ReviewCorrectionPlan
    {
        /// <summary>The tools whose applied reply this file knows how to reconcile against the plan.</summary>
        public static readonly string[] JudgedTools = { "horizun_write_params_verified", "horizun_manage_revisions" };

        /// <summary>Never reachable from an external plan, published or not.</summary>
        public static readonly string[] Forbidden = { "horizun_execute_python", "horizun_request_python_access" };

        public const string Ready = "ready";
        public const string RequiresInput = "requires_input";
        public const string NotPermitted = "not_permitted";

        public const string Corrected = "corrected";
        public const string ConfirmedByRereadOnly = "confirmed_by_reread_only";
        public const string Persistent = "persistent";
        public const string NotApplied = "not_applied";
        public const string NotVerifiable = "not_verifiable";

        private static readonly Regex Placeholder = new Regex(@"^<[^<>]+>$", RegexOptions.CultureInvariant);

        // ---- Check: may this plan be run at all? -----------------------------------

        public static JObject Check(JObject plan, Func<string, bool> isPublished)
        {
            var steps = new JArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject step in Steps(plan))
            {
                string tool = (string)step["herramienta"];
                JObject args = step["argumentos"] as JObject;
                var row = new JObject
                {
                    ["step_id"] = (string)step["id"],
                    ["rule"] = (string)step["motivo"],
                    ["tool"] = tool
                };
                string refusal = null;
                if (string.IsNullOrWhiteSpace((string)step["id"]) || !ids.Add((string)step["id"])) refusal = "step id is missing or duplicated.";
                else if (string.IsNullOrWhiteSpace(tool)) refusal = "the step names no tool.";
                else if (Forbidden.Contains(tool))
                    refusal = tool + " is never reachable from an external plan: a correction runs through a typed tool that rehearses and re-reads.";
                else if (isPublished != null && !isPublished(tool))
                    refusal = tool + " is not published by this build.";
                else if (!JudgedTools.Contains(tool)) refusal = "no model re-read rule exists for " + tool + ".";
                else if (args == null) refusal = "the step carries no argumentos object.";
                else if ((bool?)args["dry_run"] != true)
                    refusal = "a plan step must rehearse (dry_run=true); the apply is a separate call a person approves, with the rehearsal's confirmation_token.";
                else if (tool == "horizun_write_params_verified" &&
                    (!(args["writes"] is JArray writes) || writes.Count == 0 || args["tabular_source"] != null || args["mode"] != null && (string)args["mode"] != "writes"))
                    refusal = "review corrections require explicit writes (no tabular source or sequence).";
                else if (tool == "horizun_manage_revisions" &&
                    (!(args["actions"] is JArray actions) || actions.Count == 0 || actions.Any(a => a["clouds"] != null)))
                    refusal = "review corrections require revision actions without clouds; cloud geometry needs a separate typed call.";
                if (refusal != null)
                {
                    row["state"] = NotPermitted;
                    row["reason"] = refusal;
                    steps.Add(row);
                    continue;
                }
                var holes = new JArray();
                FindPlaceholders(args, "/argumentos", holes);
                row["state"] = holes.Count == 0 ? Ready : RequiresInput;
                if (holes.Count > 0)
                {
                    row["placeholders"] = holes;
                    if (step["resolver"] != null) row["resolver"] = step["resolver"].DeepClone();
                    row["reason"] = "Resolve every placeholder in the model (each says what it stands for), then rehearse the step with the resolved arguments.";
                }
                row["reconciliation"] = JudgedTools.Contains(tool)
                    ? "reverify reconciles this step's reply against the plan's targets and values"
                    : "no reconciliation rule for this tool: only its own re-read verification will speak for it";
                steps.Add(row);
            }
            int manual = (plan?["manual"] as JArray)?.Count ?? 0;
            bool runnable = steps.Count > 0 && steps.Count <= 100 && steps.Count == ((plan?["pasos"] as JArray)?.Count ?? 0) && steps.All(s => (string)s["state"] == Ready);
            return new JObject
            {
                ["steps"] = steps,
                ["counts"] = new JObject
                {
                    [Ready] = steps.Count(s => (string)s["state"] == Ready),
                    [RequiresInput] = steps.Count(s => (string)s["state"] == RequiresInput),
                    [NotPermitted] = steps.Count(s => (string)s["state"] == NotPermitted)
                },
                ["manual_count"] = manual,
                ["runnable"] = runnable,
                ["next"] = "horizun_health first; resolve placeholders; rehearse each step (dry_run=true), show the rehearsal to " +
                           "a person, apply only with their approval (same arguments, dry_run=false, the confirmation_token, a new " +
                           "idempotency_key); then reverify with every applied step's arguments and reply."
            };
        }

        private static void FindPlaceholders(JToken t, string pointer, JArray into)
        {
            if (t is JObject o)
                foreach (JProperty p in o.Properties()) FindPlaceholders(p.Value, pointer + "/" + p.Name.Replace("~", "~0").Replace("/", "~1"), into);
            else if (t is JArray a)
                for (int i = 0; i < a.Count; i++) FindPlaceholders(a[i], pointer + "/" + i.ToString(CultureInfo.InvariantCulture), into);
            else if (t != null && t.Type == JTokenType.String && Placeholder.IsMatch(((string)t).Trim()))
                into.Add(new JObject { ["pointer"] = pointer, ["stands_for"] = (string)t });
        }

        // ---- Reverify: does the model now carry what the PLAN asked? -----------------

        public static JObject Reverify(JObject plan, JArray applied, bool hostRead = false)
        {
            var byStep = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (JObject a in (applied ?? new JArray()).OfType<JObject>())
            {
                string id = (string)a["step_id"];
                if (id != null && !byStep.ContainsKey(id)) byStep[id] = a;
            }
            var steps = new JArray();
            var rules = new SortedSet<string>(StringComparer.Ordinal);
            var planIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject step in Steps(plan))
            {
                string id = (string)step["id"], tool = (string)step["herramienta"];
                if (id != null) planIds.Add(id);
                if (!string.IsNullOrWhiteSpace((string)step["motivo"])) rules.Add((string)step["motivo"]);
                JObject a;
                byStep.TryGetValue(id ?? "", out a);
                JArray targets;
                if (a == null) targets = new JArray(Target(id, NotVerifiable, "no applied reply was given for this step, so nothing proves the model carries it."));
                else if (tool == "horizun_write_params_verified") targets = ParameterTargets(step, a);
                else if (tool == "horizun_manage_revisions") targets = RevisionTargets(step, a);
                else targets = new JArray(Target(id, NotVerifiable, "no reconciliation rule for " + tool + "; read that tool's own verification."));
                steps.Add(new JObject
                {
                    ["step_id"] = id, ["rule"] = (string)step["motivo"], ["tool"] = tool,
                    ["state"] = RollUp(targets.OfType<JObject>().Select(t => (string)t["outcome"])),
                    ["targets"] = targets
                });
            }
            var unknown = byStep.Keys.Where(k => !planIds.Contains(k)).ToList();
            var result = new JObject
            {
                ["state"] = steps.Count == 0 ? ExchangeManifest.Failed
                                             : ExchangeManifest.Weakest(steps.OfType<JObject>().Select(s => (string)s["state"])),
                ["method"] = "plan_vs_reread_reply",
                ["host_verified"] = false,
                ["steps"] = steps,
                ["manual_not_covered"] = (plan?["manual"] as JArray)?.Count ?? 0,
                ["re_review"] = new JObject
                {
                    ["required"] = true,
                    ["server"] = "horizun-review",
                    ["tool"] = "revisar_set",
                    ["rules"] = new JArray(rules),
                    ["how"] = "Re-export the affected sheets and run the review again on the new set; only its delta closes a " +
                              "PDF finding. This reconciliation proves the MODEL carries what the plan asked, not that the printed set does."
                },
                ["caveats"] = new JArray(
                    "Each reply was re-read by the bridge inside Revit when it was produced; this judgement re-derives the plan " +
                    "from those replies as relayed by the client. It does not read the model again.",
                    "Steps listed under 'manual' in the plan are not covered here.")
            };
            if (unknown.Count > 0) result["unknown_step_ids"] = new JArray(unknown);
            if (!hostRead)
            {
                foreach (JObject s in steps.OfType<JObject>())
                    if ((string)s["state"] == ExchangeManifest.Verified) s["state"] = ExchangeManifest.SelfReported;
                if ((string)result["state"] == ExchangeManifest.Verified) result["state"] = ExchangeManifest.SelfReported;
            }
            result["host_verified"] = hostRead && (string)result["state"] == ExchangeManifest.Verified;
            if (hostRead)
            {
                result["method"] = "plan_vs_fresh_model_read";
                result["caveats"] = new JArray("Manual steps and the printed PDF are not verified here; re-review remains required.");
            }
            return result;
        }

        /// <summary>Apply every step, then re-fetch ALL targets. A later step can invalidate an earlier one.</summary>
        public static JObject ApplyAndReRead(JObject plan, Func<JObject, JObject> apply,
                                             Func<JObject, JObject, JObject> read)
        {
            var applied = new JArray();
            foreach (JObject step in Steps(plan))
            {
                JObject reply;
                try { reply = apply(step); }
                catch (Exception ex) { reply = new JObject { ["error"] = ex.Message }; }
                var args = (JObject)step["argumentos"].DeepClone();
                args["dry_run"] = false;
                applied.Add(new JObject { ["step_id"] = step["id"], ["arguments"] = args, ["reply"] = reply });
            }
            int i = 0;
            foreach (JObject step in Steps(plan))
            {
                JObject item = (JObject)applied[i++];
                item["apply_reply"] = item["reply"].DeepClone();
                try { item["reply"] = read(step, (JObject)item["reply"]); }
                catch (Exception ex) { item["reply"] = new JObject { ["error"] = ex.Message }; }
            }
            JObject result = Reverify(plan, applied, true);
            result["applied"] = applied;
            return result;
        }

        private static string RollUp(IEnumerable<string> outcomes)
        {
            var list = outcomes.ToList();
            if (list.Count == 0) return ExchangeManifest.Failed;
            int corrected = list.Count(o => o == Corrected), reread = list.Count(o => o == ConfirmedByRereadOnly);
            if (corrected == list.Count) return ExchangeManifest.Verified;
            if (corrected + reread == list.Count) return ExchangeManifest.SelfReported;
            if (corrected + reread > 0) return ExchangeManifest.Partial;
            return ExchangeManifest.Failed;
        }

        private static JArray ParameterTargets(JObject step, JObject applied)
        {
            var targets = new JArray();
            JObject planArgs = step["argumentos"] as JObject ?? new JObject();
            JObject args = applied["arguments"] as JObject;
            JObject reply = Unwrap(applied["reply"]);
            string id = (string)step["id"];
            bool rehearsal = args == null || (bool?)args["dry_run"] != false;
            var planWrites = (planArgs["writes"] as JArray)?.OfType<JObject>().ToList();
            if (planWrites == null)
            {
                // tabular_source: the plan's values live in a CSV this reconciliation does not read.
                bool toolVerified = !rehearsal && (bool?)reply?["verification"]?["verified"] == true;
                targets.Add(Target(id, rehearsal ? NotApplied : toolVerified ? ConfirmedByRereadOnly : NotVerifiable,
                    rehearsal ? "the arguments sent were a rehearsal (dry_run is not false); nothing was applied."
                    : toolVerified ? "the plan's values live in its CSV; the tool re-read every row against it, which this check cannot re-derive."
                                   : "the plan's values live in its CSV and the reply does not show every row verified."));
                return targets;
            }
            var appliedWrites = (args?["writes"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var rows = (reply?["rows"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            bool truncated = (bool?)reply?["rows_truncated"] == true;
            for (int i = 0; i < planWrites.Count; i++)
            {
                JObject pw = planWrites[i];
                string parameter = (string)pw["parameter"];
                string planValue = Text(pw["value"]);
                JObject aw = i < appliedWrites.Count ? appliedWrites[i] : null;
                string label = id + "#" + i.ToString(CultureInfo.InvariantCulture);
                if (rehearsal) { targets.Add(Target(label, NotApplied, "the arguments sent were a rehearsal (dry_run is not false); nothing was applied.", parameter)); continue; }
                if (aw == null) { targets.Add(Target(label, NotVerifiable, "the applied arguments carry no write #" + i + ".", parameter)); continue; }
                string drift = Drift(pw, aw);
                if (drift != null) { targets.Add(Target(label, Persistent, "the applied write is not the plan's: " + drift, parameter)); continue; }
                bool projectInfo = string.Equals((string)aw["target"], "project_info", StringComparison.OrdinalIgnoreCase) || aw["target_id"] == null;
                string targetId = projectInfo ? null : Text(aw["target_id"]);
                JObject row = rows.FirstOrDefault(r => string.Equals((string)r["parameter"], parameter, StringComparison.Ordinal) &&
                    (projectInfo ? (string)r["target_kind"] == "project_info" : Text(r["target_id"]) == targetId));
                JObject t = Target(label, null, null, parameter);
                t["target_id"] = targetId;
                if (row == null)
                {
                    t["outcome"] = NotVerifiable;
                    t["reason"] = truncated ? "the reply was truncated (rows_truncated) and does not show this row; re-read it with a narrower call."
                                            : "the reply has no row for this target and parameter.";
                }
                else
                {
                    t["value_read_back"] = row["value_read_back"];
                    JToken confirmed = row["confirmed"];
                    string against = (string)row["confirmed_against"];
                    if (confirmed == null || confirmed.Type == JTokenType.Null) { t["outcome"] = NotApplied; t["reason"] = "the row was rehearsed, not written."; }
                    else if (!(row["value_read_back"] is JObject rv) || (bool?)rv["readable"] != true)
                    { t["outcome"] = NotVerifiable; t["reason"] = "no readable model value was supplied."; }
                    else if ((bool)confirmed && against == "your_value" && ReadMatches(row["value_read_back"] as JObject, pw["value"])) t["outcome"] = Corrected;
                    else if ((bool)confirmed && against == "your_value")
                    { t["outcome"] = Persistent; t["reason"] = "the model reads '" + Text(row["value_read_back"]) + "', not the plan's '" + planValue + "'."; }
                    else if ((bool)confirmed)
                    { t["outcome"] = ConfirmedByRereadOnly; t["reason"] = "confirmed only against a re-read of itself (Revit parsed the string's units), not against the plan's value."; }
                    else if ((string)row["outcome"] == "unknown")
                    { t["outcome"] = NotVerifiable; t["reason"] = "the tool could not read the row back; its fate is unknown."; }
                    else { t["outcome"] = Persistent; t["reason"] = "the model was read back and does not carry the plan's value (" + (string)row["outcome"] + ")."; }
                }
                targets.Add(t);
            }
            return targets;
        }

        private static JArray RevisionTargets(JObject step, JObject applied)
        {
            var targets = new JArray();
            JObject args = applied["arguments"] as JObject;
            JObject reply = Unwrap(applied["reply"]);
            bool rehearsal = args == null || (bool?)args["dry_run"] != false;
            var appliedActions = (args?["actions"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var rows = (reply?["rows"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            bool hostVerified = (bool?)reply?["host_verified"] == true && (string)reply?["state"] == "committed_verified";
            foreach (JObject pa in (step["argumentos"]?["actions"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string key = (string)pa["key"];
                JObject t = Target((string)step["id"] + "#" + key, null, null);
                t["key"] = key;
                JObject aa = appliedActions.FirstOrDefault(x => (string)x["key"] == key);
                JObject row = rows.FirstOrDefault(x => (string)x["key"] == key);
                string drift = aa == null ? null : Drift(pa, aa);
                if (rehearsal) { t["outcome"] = NotApplied; t["reason"] = "the arguments sent were a rehearsal (dry_run is not false)."; }
                else if (aa == null) { t["outcome"] = NotVerifiable; t["reason"] = "the applied arguments carry no action '" + key + "'."; }
                else if (drift != null) { t["outcome"] = Persistent; t["reason"] = "the applied action is not the plan's: " + drift; }
                else if (row == null || !hostVerified || (bool?)row["verified"] != true)
                { t["outcome"] = NotVerifiable; t["reason"] = "the reply does not show action '" + key + "' committed and verified by the host."; }
                else
                {
                    var wanted = ((aa["sheet_ids"] as JArray) ?? new JArray()).Select(Text).ToList();
                    var got = new HashSet<string>(((row["sheet_ids"] as JArray) ?? new JArray()).Select(Text), StringComparer.Ordinal);
                    var missing = wanted.Where(w => !got.Contains(w)).ToList();
                    t["revision_id"] = row["revision_id"];
                    if (missing.Count == 0) t["outcome"] = Corrected;
                    else { t["outcome"] = Persistent; t["reason"] = "the revision is not on sheet(s) " + string.Join(", ", missing) + "."; }
                }
                targets.Add(t);
            }
            if (targets.Count == 0) targets.Add(Target((string)step["id"], NotVerifiable, "the plan step carries no actions."));
            return targets;
        }

        /// <summary>
        /// Where the applied object departs from the plan's: every plan field that is not a
        /// placeholder must arrive unchanged. dry_run is the one field allowed to differ.
        /// </summary>
        private static string Drift(JObject planned, JObject sent)
        {
            foreach (JProperty p in planned.Properties())
            {
                if (p.Name == "dry_run") continue;
                if (ContainsPlaceholder(p.Value)) continue;
                JToken other = sent[p.Name];
                if (other == null || !JToken.DeepEquals(Normal(p.Value), Normal(other)))
                    return p.Name + " is '" + (other == null ? "(absent)" : Text(other)) + "' where the plan says '" + Text(p.Value) + "'.";
            }
            return null;
        }

        private static bool ContainsPlaceholder(JToken t)
        {
            var holes = new JArray();
            FindPlaceholders(t, "", holes);
            return holes.Count > 0;
        }

        private static JToken Normal(JToken t) => t.Type == JTokenType.Integer || t.Type == JTokenType.Float ? new JValue(Text(t)) : t;

        private static JObject Unwrap(JToken reply)
        {
            JObject o = reply as JObject;
            if (o?["structuredContent"] is JObject sc) o = sc;
            if (o?["result"] is JObject inner && o["ok"] != null) o = inner;
            return o;
        }

        private static IEnumerable<JObject> Steps(JObject plan) => (plan?["pasos"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();

        private static JObject Target(string label, string outcome, string reason, string parameter = null)
        {
            var t = new JObject { ["target"] = label, ["outcome"] = outcome };
            if (parameter != null) t["parameter"] = parameter;
            if (reason != null) t["reason"] = reason;
            return t;
        }

        private static string Text(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            if (t.Type == JTokenType.Integer) return ((long)t).ToString(CultureInfo.InvariantCulture);
            if (t.Type == JTokenType.Float) return ((double)t).ToString("R", CultureInfo.InvariantCulture);
            return t.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool ReadMatches(JObject read, JToken requested)
        {
            JToken value = read?["value"];
            if ((string)read?["storage"] == "Integer" && requested?.Type == JTokenType.Boolean)
                return Text(value) == ((bool)requested ? "1" : "0");
            if ((string)read?["storage"] == "Double" && requested != null &&
                (requested.Type == JTokenType.Integer || requested.Type == JTokenType.Float))
            {
                if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) return false;
                double a = (double)value, b = (double)requested;
                return Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
            }
            return Text(value) == Text(requested);
        }
    }
}
