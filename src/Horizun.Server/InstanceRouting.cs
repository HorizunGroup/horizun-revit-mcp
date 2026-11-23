// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// ONE RULE FOR SEVERAL REVIT INSTANCES, THE SAME FOR EVERY TOOL.
//
// Field report (Revit 2024 and 2026 open at once): horizun_health and
// get_document_info answered while horizun_request_python_access demanded choosing an
// instance. The agent could not tell which Revit had answered the first two, nor why
// the third was different. The rule now, for every tool that reaches Revit, reads
// and writes alike:
//
//   1. ONE instance is meant - it is the only live one, or horizun_target /
//      HORIZUN_REVIT_PID / HORIZUN_REVIT_YEAR names exactly one - and the call goes
//      there. Every reply then NAMES it: answered_by {pid, year, addin_version,
//      selected_by, other_live_instances}.
//   2. More than one live instance and nothing names one: EVERY tool refuses the same
//      way - error_code documento_objetivo_ambiguo, target_required=true, and the
//      candidate list instances[] with each instance's open documents (pid, year, title,
//      path, active). The requested read/write command is not sent; only bounded,
//      read-only health probes gather candidate documents. A read is not exempt: the
//      right answer from the wrong model is the failure nobody notices.
//      EXCEPTION (owner decision, 2026-11-13): target_document open in exactly one instance goes
//      there (a write keeps its own rehearsal and token); open in several, or in none,
//      is refused naming where it is or what is open - MatchDocument / DocumentRefusal.
//   3. horizun_health is the one tool that ANSWERS in case 2, because it is how the
//      agent learns what to choose: estado=varias_instancias with the same code, the
//      same target_required and the same instances[] (each also probed for its build
//      and active document). It never routes the question to one of them.
//
// Server-side tools that never reach Revit (horizun_target, horizun_job_status read
// from disk, the facade's list/describe) are outside the rule.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal sealed class RoutingDecision
    {
        /// <summary>The instance the call goes to; null when refused or none is reachable.</summary>
        public Discovered Instance;
        /// <summary>The refusal sentence (ambiguity or a pinned pid that published nothing); null otherwise.</summary>
        public string Refusal;
        /// <summary>True when the refusal is rule 2: several live instances, none named.</summary>
        public bool Ambiguous;
        /// <summary>For an ambiguous refusal: the live candidates, the same rows for every tool.</summary>
        public JArray Candidates;
    }

    internal static class InstanceRouting
    {
        /// <summary>Decide where a call goes. Independent of the tool on purpose: the rule is the same for all.</summary>
        public static RoutingDecision Decide(TargetSelection target, string envYear)
        {
            target = target ?? TargetSelection.Automatic;
            // INT-6 m-10: a pinned document that no instance shows is a refusal for every tool, reads included.
            if (target.Unresolved != null)
                return new RoutingDecision { Refusal = target.Unresolved, Ambiguous = true, Candidates = Candidates(PipeClient.ListAll()) };
            string year = target.Year ?? (target.Pid != null ? null : envYear);
            Discovered d = PipeClient.Resolve(year, target.Pid, out string refusal);
            var decision = new RoutingDecision { Instance = refusal == null ? d : null, Refusal = refusal };
            if (refusal != null && !refusal.StartsWith("No Revit with process id", System.StringComparison.Ordinal))
            {
                decision.Ambiguous = true;
                decision.Candidates = Candidates(PipeClient.ListAll());
            }
            return decision;
        }

        /// <summary>
        /// Candidate documents reported by the bounded health probes. A row represents one
        /// open document, so the caller can distinguish two processes and two documents in
        /// the same process without this router selecting either one.
        /// </summary>
        public static JArray Candidates(JArray instances)
        {
            var rows = new JArray();
            foreach (JObject instance in (instances ?? new JArray()).OfType<JObject>()
                         .OrderBy(x => (string)x["year"], System.StringComparer.Ordinal)
                         .ThenBy(x => (int?)x["pid"]))
            {
                JArray documents = instance["open_documents"] as JArray;
                if (documents != null)
                    documents = new JArray(documents.OfType<JObject>().Where(d => (bool?)d["is_linked"] != true));
                if (documents == null || documents.Count == 0)
                {
                    JObject active = instance["active_document"] as JObject;
                    rows.Add(Candidate(instance, active, active != null ? (JToken)true :
                        (bool?)instance["no_active_document"] == true ? (JToken)false : JValue.CreateNull()));
                    continue;
                }

                foreach (JObject document in documents.OfType<JObject>())
                    rows.Add(Candidate(instance, document, document["is_active"]));
            }
            return rows;
        }

        /// <summary>Discovery-only fallback used before the health snapshot is available.</summary>
        public static JArray Candidates(IEnumerable<Discovered> instances) => new JArray(
            (instances ?? Enumerable.Empty<Discovered>())
                .Where(d => d.ProcessAlive)
                .OrderBy(d => d.Year).ThenBy(d => d.Pid)
                .Select(d => Candidate(new JObject
                {
                    ["pid"] = d.Pid,
                    ["year"] = d.Year,
                    ["addin_version"] = d.AddinVersion
                }, null, null)));

        /// <summary>Enrich an ambiguity response with a bounded health snapshot; retain discovery rows if probes fail.</summary>
        public static JArray LiveCandidates()
        {
            try { return Candidates(HealthAnswer.ListInstances()); }
            catch
            {
                var rows = new JArray();
                foreach (Discovered d in PipeClient.ListAll().Where(x => x.ProcessAlive).OrderBy(x => x.Year).ThenBy(x => x.Pid))
                    rows.Add(Candidate(new JObject { ["pid"] = d.Pid, ["year"] = d.Year, ["addin_version"] = d.AddinVersion }, null, null));
                return rows;
            }
        }

        /// <summary>Small target list for a pinned process that has exited.</summary>
        public static JArray LiveTargets()
        {
            try
            {
                return new JArray(PipeClient.ListAll().Where(x => x.ProcessAlive)
                    .OrderBy(x => x.Year, System.StringComparer.Ordinal).ThenBy(x => x.Pid)
                    .Select(x => new JObject
                    {
                        ["pid"] = x.Pid,
                        ["year"] = x.Year,
                        ["addin_version"] = x.AddinVersion,
                        ["select_with"] = new JObject
                        {
                            ["tool"] = "horizun_target",
                            ["arguments"] = new JObject { ["pid"] = x.Pid }
                        }
                    }));
            }
            catch { return new JArray(); }
        }

        private static JObject Candidate(JObject instance, JObject document, JToken active)
        {
            string title = (string)document?["title"];
            string path = (string)document?["path"];
            string targetDocument = !string.IsNullOrWhiteSpace(path) ? path : title;
            int? pid = (int?)instance["pid"];
            var row = new JObject
            {
                ["pid"] = pid,
                ["year"] = instance["year"]?.DeepClone(),
                ["title"] = title,
                ["path"] = path,
                ["active"] = active?.DeepClone() ?? JValue.CreateNull(),
                ["addin_version"] = instance["addin_version"]?.DeepClone(),
                ["started_utc"] = instance["started_utc"]?.DeepClone(),
                ["choose_with"] = pid.HasValue ? "horizun_target" : null,
                ["select_instance"] = pid.HasValue ? new JObject
                {
                    ["tool"] = "horizun_target",
                    ["arguments"] = new JObject { ["pid"] = pid.Value }
                } : null,
                ["target_document"] = targetDocument
            };
            return row;
        }

        /// <summary>The structured half of an ambiguous refusal; identical for every tool.</summary>
        public static JObject AmbiguityExtra(JArray candidates) => new JObject
        {
            ["write_started"] = false,
            ["changes_applied"] = false,
            ["target_required"] = true,
            ["instances"] = candidates ?? new JArray(),
            ["retry_with"] = new JArray((candidates ?? new JArray()).OfType<JObject>().Select(c => new JObject { ["tool"] = "horizun_target", ["arguments"] = new JObject { ["pid"] = c["pid"]?.DeepClone() } })),
            ["rule"] = "several_instances_refuse_uniformly",
            ["example"] = new JObject
            {
                ["select_instance"] = candidates?.FirstOrDefault()?["select_instance"]?.DeepClone(),
                ["repeat_original_tool_arguments"] = new JObject
                {
                    ["target_document"] = candidates?.FirstOrDefault()?["target_document"]?.DeepClone() ?? JValue.CreateNull()
                }
            },
            ["que_hacer"] = "Choose the intended candidate; call horizun_target with its select_instance.arguments, then repeat " +
                            "the original tool with target_document set to that candidate's title or full path. The PID selects " +
                            "the Revit instance; target_document must match its active document for a write. The requested " +
                            "command was not sent; only read-only health probes were run."
        };

        internal static Discovered MatchOpenDocument(string asked, Discovered[] all, JArray probes) =>
            MatchDocument(asked, all, probes)?.Instance;

        /// <summary>Where <paramref name="asked"/> is open among the live instances.</summary>
        internal sealed class DocumentMatch
        {
            /// <summary>The one instance holding it; null when it is open in none or in several.</summary>
            public Discovered Instance;
            public JObject Document;
            /// <summary>One row per instance holding it: year, pid, title, path, is_modified.</summary>
            public JArray OpenIn = new JArray();
            /// <summary>Every open (non-linked) document of every instance, same row shape.</summary>
            public JArray AllOpen = new JArray();
        }

        /// <summary>
        /// Null when a probe could not list every open document of every instance (unknown is not "not open":
        /// the caller then refuses with the generic ambiguity). Otherwise where the document is open.
        /// </summary>
        internal static DocumentMatch MatchDocument(string asked, Discovered[] all, JArray probes)
        {
            if (string.IsNullOrWhiteSpace(asked) || all == null || probes == null || probes.Count != all.Length) return null;
            bool path = asked.IndexOfAny(new[] { (char)92, (char)47 }) >= 0;
            var match = new DocumentMatch();
            foreach (var d in all.OrderBy(x => x.Year, System.StringComparer.Ordinal).ThenBy(x => x.Pid))
            {
                JObject row = probes.OfType<JObject>().FirstOrDefault(x => (int?)x["pid"] == d.Pid);
                if (row == null || row["probe_error"] != null || (bool?)row["open_documents_complete"] != true || !(row["open_documents"] is JArray docs)) return null;
                JObject hit = null;
                foreach (JObject doc in docs.OfType<JObject>().Where(x => (bool?)x["is_linked"] != true))
                {
                    match.AllOpen.Add(Row(d, doc));
                    bool same = path
                        ? string.Equals(((string)doc["path"] ?? "").Trim(), asked.Trim(), System.StringComparison.OrdinalIgnoreCase)
                        : string.Equals(DocumentTarget.Normalize((string)doc["title"]), DocumentTarget.Normalize(asked), System.StringComparison.OrdinalIgnoreCase);
                    if (same && hit == null) hit = doc;
                }
                if (hit == null) continue;
                match.OpenIn.Add(Row(d, hit));
                if (match.OpenIn.Count == 1) { match.Instance = d; match.Document = hit; }
                else { match.Instance = null; match.Document = null; }
            }
            return match;
        }

        private static JObject Row(Discovered d, JObject doc) => new JObject
        {
            ["year"] = d.Year,
            ["pid"] = d.Pid,
            ["title"] = doc["title"]?.DeepClone(),
            ["path"] = doc["path"]?.DeepClone(),
            ["is_modified"] = doc["is_modified"]?.DeepClone() ?? JValue.CreateNull()
        };

        private static string Describe(JToken r) =>
            "Revit " + (string)r["year"] + " pid " + (int?)r["pid"] + " (" + ((string)r["path"] ?? (string)r["title"] ?? "?") +
            ((bool?)r["is_modified"] == true ? ", unsaved changes" : "") + ")";

        /// <summary>
        /// The refusal for a target_document that does not name exactly one instance (owner decision, 2026-11-13: the same
        /// model open twice is refused; a model open nowhere is refused with what IS open). Null when it names one.
        /// </summary>
        internal static JObject DocumentRefusal(string asked, DocumentMatch match)
        {
            if (match == null || match.Instance != null) return null;
            bool several = match.OpenIn.Count > 1;
            string text = several
                ? "'" + asked + "' is open in " + match.OpenIn.Count + " Revit instances: " +
                  string.Join("; ", match.OpenIn.Select(Describe)) + ". Refusing to pick: the same model open twice " +
                  "means an edit in one is not in the other. Close the one you do not mean (save first if it has " +
                  "unsaved changes), or call horizun_target with the pid you mean, and repeat the call. Nothing was run."
                : "'" + asked + "' is not open in any running Revit. Open documents: " +
                  (match.AllOpen.Count == 0 ? "none" : string.Join("; ", match.AllOpen.Select(Describe))) +
                  ". Send target_document as one of those titles or full paths, or open the model first. Nothing was run.";
            var extra = new JObject
            {
                ["write_started"] = false,
                ["changes_applied"] = false,
                ["target_document"] = asked,
                ["target_document_state"] = several ? "open_in_several_instances" : "not_open",
                ["open_in"] = match.OpenIn,
                ["open_documents"] = match.AllOpen,
                ["retry_with"] = new JArray((several ? match.OpenIn : match.AllOpen).OfType<JObject>()
                    .Select(r => (int?)r["pid"]).Distinct()
                    .Select(pid => new JObject { ["tool"] = "horizun_target", ["arguments"] = new JObject { ["pid"] = pid } }))
            };
            return new JObject { ["text"] = text, ["extra"] = extra };
        }

        /// <summary>Which instance answered, and why that one. Stamped on every routed reply.</summary>
        public static JObject AnsweredBy(Discovered d, TargetSelection target, string envYear, int liveInstances)
        {
            if (d == null) return null;
            target = target ?? TargetSelection.Automatic;
            string selectedBy =
                target.Document != null ? (target.Origin ?? "horizun_target") + " document '" + target.Document + "'"
              : target.Pid != null ? (target.Origin ?? "horizun_target") + " pid"
              : target.Year != null ? (target.Origin ?? "horizun_target") + " year"
              : !string.IsNullOrEmpty(envYear) ? "HORIZUN_REVIT_YEAR"
              : "sole_instance";
            return new JObject
            {
                ["pid"] = d.Pid,
                ["year"] = d.Year,
                ["addin_version"] = d.AddinVersion,
                ["selected_by"] = selectedBy,
                ["other_live_instances"] = System.Math.Max(0, liveInstances - 1)
            };
        }

        /// <summary>Adds answered_by to an object payload that does not already carry one. Other shapes are left alone.</summary>
        public static void Stamp(JToken payload, JObject answeredBy)
        {
            if (answeredBy == null || !(payload is JObject o) || o["answered_by"] != null) return;
            o["answered_by"] = answeredBy;
        }

        public static int LiveCount()
        {
            try { return PipeClient.ListAll().Count(x => x.ProcessAlive); } catch { return 0; }
        }
    }
}
