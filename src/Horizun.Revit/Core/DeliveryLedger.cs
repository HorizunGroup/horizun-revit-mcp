// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// THE BOOK A DELIVERY KEEPS. DeliveryPlan.Build hands a client an ordered list
// of stages with status "pending"; this file is what makes that list a
// delivery instead of a to-do: durable identity, one explicit state per stage,
// the transitions each state allows, invalidation when the model moves under a
// completed stage, and a publish gate that opens only on a complete, audited,
// currently-approved run.
//
// WHAT IT IS NOT. It is not a transaction spanning the delivery. Navigation,
// Revit transactions and external files cannot share one, so the atomicity on
// offer is exactly: one typed write is atomic; the ledger records which writes
// happened and refuses to hand a completed write out again. And an audit that
// passed is not a permission to write anything - it is one fact about one
// moment, bound to the elements it looked at.
//
// SHAPE ON DISK. One append-only JSONL file per delivery, one event per line,
// replayed into the record on every read. A crash keeps every event that was
// flushed; the next reader folds them and sees exactly how far the run got.
//
// CHANGE DETECTION. A completed write stage stores the VersionGuid of every
// element in its scope (Revit changes it whenever the element changes). The
// host re-reads those at resume and at the gate; a difference invalidates the
// stage and everything that depended on it. The ledger does the arithmetic;
// the host does the reading.
//
// Revit-free on purpose: every transition, every cascade and the gate are
// unit-tested at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class DeliveryLedger
    {
        public const string Schema = "horizun.delivery-ledger/1";

        // ---- stage statuses -------------------------------------------------------
        public const string Pending = "pending";
        public const string InProgress = "in_progress";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Blocked = "blocked";
        public const string Invalidated = "invalidated";
        public const string AwaitingApproval = "awaiting_approval";
        public const string Approved = "approved";
        public const string Rejected = "rejected";

        // ---- stage kinds ------------------------------------------------------------
        public const string KindNavigate = "navigate";
        public const string KindWrite = "write";
        public const string KindCapture = "capture";
        public const string KindApprovalCapture = "approval_capture";
        public const string KindAudit = "audit";
        public const string KindPublish = "publish";

        private static readonly string[] AllStatuses =
        {
            Pending, InProgress, Completed, Failed, Blocked, Invalidated, AwaitingApproval, Approved, Rejected
        };

        /// <summary>The transitions a stage may take, by kind. Anything else is refused by name.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { Pending + ">" + InProgress, new[] { KindNavigate, KindWrite, KindCapture, KindApprovalCapture, KindAudit, KindPublish } },
            { Pending + ">" + Blocked, new[] { KindNavigate, KindWrite, KindCapture, KindApprovalCapture, KindAudit, KindPublish } },
            { InProgress + ">" + Completed, new[] { KindNavigate, KindWrite, KindCapture, KindAudit, KindPublish } },
            { InProgress + ">" + AwaitingApproval, new[] { KindApprovalCapture } },
            { InProgress + ">" + Failed, new[] { KindNavigate, KindWrite, KindCapture, KindApprovalCapture, KindAudit, KindPublish } },
            { InProgress + ">" + Blocked, new[] { KindNavigate, KindWrite, KindCapture, KindApprovalCapture, KindAudit, KindPublish } },
            { AwaitingApproval + ">" + Approved, new[] { KindApprovalCapture } },
            { AwaitingApproval + ">" + Rejected, new[] { KindApprovalCapture } },
            { Completed + ">" + Invalidated, new[] { KindNavigate, KindWrite, KindCapture, KindAudit, KindPublish } },
            { AwaitingApproval + ">" + Invalidated, new[] { KindApprovalCapture } },
            { Approved + ">" + Invalidated, new[] { KindApprovalCapture } },
            { Failed + ">" + Pending, new[] { KindNavigate, KindWrite, KindCapture, KindApprovalCapture, KindAudit, KindPublish } },
            { Blocked + ">" + Pending, new[] { KindNavigate, KindWrite, KindCapture, KindApprovalCapture, KindAudit, KindPublish } },
            { Invalidated + ">" + Pending, new[] { KindNavigate, KindWrite, KindCapture, KindApprovalCapture, KindAudit, KindPublish } },
            { Rejected + ">" + Pending, new[] { KindApprovalCapture } }
        };

        // =====================================================================
        // Opening.
        // =====================================================================

        /// <summary>The kind of a stage, from its plan key.</summary>
        public static string KindOf(string key, string tool)
        {
            if (key == "publish") return KindPublish;
            if (key == "audit") return KindAudit;
            if (key == "pack") return KindWrite;
            if (key.StartsWith("capture_sheet_", StringComparison.Ordinal)) return KindApprovalCapture;
            if (key.StartsWith("capture_view_", StringComparison.Ordinal)) return KindCapture;
            if (key.StartsWith("view_", StringComparison.Ordinal)) return KindNavigate;
            if (key.StartsWith("dimensions_", StringComparison.Ordinal) || key.StartsWith("tags_", StringComparison.Ordinal)) return KindWrite;
            return tool == "horizun_capture_view" ? KindCapture : KindWrite;
        }

        /// <summary>
        /// Dependencies derived from the plan's own stage keys: a view's stages chain in
        /// order, packing waits for every annotation capture, the audit for packing,
        /// every sheet capture for the audit, and publication for the audit plus every
        /// sheet approval.
        /// </summary>
        public static Dictionary<string, List<string>> Dependencies(IList<string> orderedKeys)
        {
            var deps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string lastOfView = null; string currentView = null;
            var viewCaptures = new List<string>();
            var sheetCaptures = new List<string>();
            foreach (string key in orderedKeys)
            {
                deps[key] = new List<string>();
                if (key.StartsWith("view_", StringComparison.Ordinal))
                {
                    currentView = key.Substring(5); lastOfView = key;
                }
                else if (key.StartsWith("dimensions_", StringComparison.Ordinal) || key.StartsWith("tags_", StringComparison.Ordinal) ||
                         key.StartsWith("capture_view_", StringComparison.Ordinal))
                {
                    string viewOf = key.Substring(key.LastIndexOf('_') + 1);
                    if (viewOf == currentView && lastOfView != null) deps[key].Add(lastOfView);
                    lastOfView = key;
                    if (key.StartsWith("capture_view_", StringComparison.Ordinal)) viewCaptures.Add(key);
                }
                else if (key == "pack") deps[key].AddRange(viewCaptures);
                else if (key == "audit") { if (orderedKeys.Contains("pack")) deps[key].Add("pack"); }
                else if (key.StartsWith("capture_sheet_", StringComparison.Ordinal))
                {
                    if (orderedKeys.Contains("audit")) deps[key].Add("audit");
                    sheetCaptures.Add(key);
                }
                else if (key == "publish")
                {
                    if (orderedKeys.Contains("audit")) deps[key].Add("audit");
                    deps[key].AddRange(sheetCaptures);
                }
            }
            return deps;
        }

        /// <summary>
        /// Open a ledger record from a plan. document/addin are the identities the host
        /// measured; deliveryId is the caller's name for the delivery or a stable
        /// derivation of profile hash and document fingerprint.
        /// </summary>
        public static JObject Open(JObject plan, JObject document, JObject addin, string deliveryId, DateTime utc)
        {
            if (plan == null || !(plan["stages"] is JArray stages) || stages.Count == 0)
                throw new ArgumentException("A delivery ledger needs a plan with stages.");
            if (string.IsNullOrWhiteSpace(deliveryId) || deliveryId.Length > 120 ||
                deliveryId.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')))
                throw new ArgumentException("delivery_id must be 1..120 characters of letters, digits, '-', '_' or '.'.");
            List<string> keys = stages.Select(s => s.Value<string>("key")).ToList();
            if (keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
                throw new ArgumentException("Plan stage keys must be nonempty and unique.");
            Dictionary<string, List<string>> deps = Dependencies(keys);
            var rows = new JArray();
            int order = 0;
            foreach (JObject s in stages)
            {
                string key = s.Value<string>("key");
                rows.Add(new JObject
                {
                    ["key"] = key, ["order"] = order++, ["tool"] = s.Value<string>("tool"),
                    ["kind"] = KindOf(key, s.Value<string>("tool")),
                    ["depends_on"] = new JArray(deps[key]),
                    ["status"] = Pending, ["updated_utc"] = utc.ToString("o", CultureInfo.InvariantCulture),
                    ["arguments"] = s["arguments"]?.DeepClone() ?? new JObject(),
                    ["acceptance"] = s.Value<string>("acceptance"),
                    ["history"] = new JArray()
                });
            }
            return new JObject
            {
                ["schema"] = Schema,
                ["delivery_id"] = deliveryId,
                ["opened_utc"] = utc.ToString("o", CultureInfo.InvariantCulture),
                ["profile"] = new JObject
                {
                    ["id"] = plan["profile_id"]?.DeepClone(), ["version"] = plan["profile_version"]?.DeepClone(),
                    ["sha256"] = plan["profile_sha256"]?.DeepClone()
                },
                ["document"] = document?.DeepClone() ?? new JObject(),
                ["addin"] = addin?.DeepClone() ?? new JObject(),
                ["stages"] = rows,
                ["events"] = 1,
                ["atomicity"] = "one typed write at a time; navigation, captures and external files share no transaction with model writes",
                ["authority"] = "a passed audit is a fact about the elements it measured at that moment, never a permission to write or publish anything else"
            };
        }

        // =====================================================================
        // Transitions.
        // =====================================================================

        public static JObject Stage(JObject record, string key)
        {
            return (record["stages"] as JArray)?.OfType<JObject>().FirstOrDefault(s => s.Value<string>("key") == key);
        }

        /// <summary>
        /// Move one stage to a new status, or refuse with the reason. Order is enforced
        /// on entry to in_progress: every dependency must be completed (or approved).
        /// facts are recorded verbatim on the stage under the status they arrived with.
        /// </summary>
        public static bool TryTransition(JObject record, string key, string toStatus, JObject facts, DateTime utc, out string refusal)
        {
            refusal = null;
            JObject stage = Stage(record, key);
            if (stage == null) { refusal = "stage '" + key + "' is not in this delivery"; return false; }
            if (!AllStatuses.Contains(toStatus)) { refusal = "'" + toStatus + "' is not a stage status"; return false; }
            string from = stage.Value<string>("status"), kind = stage.Value<string>("kind");
            string[] kinds;
            if (!Transitions.TryGetValue(from + ">" + toStatus, out kinds) || !kinds.Contains(kind))
            {
                refusal = "stage '" + key + "' (" + kind + ") cannot go from " + from + " to " + toStatus +
                          "; allowed from " + from + ": " + string.Join(", ", AllowedFrom(from, kind));
                return false;
            }
            if (toStatus == InProgress)
            {
                foreach (string dep in stage["depends_on"].Values<string>())
                {
                    string depStatus = Stage(record, dep)?.Value<string>("status");
                    if (depStatus != Completed && depStatus != Approved)
                    {
                        refusal = "stage '" + key + "' depends on '" + dep + "', which is " + (depStatus ?? "missing") +
                                  "; complete it first. Stages run in dependency order, never ahead of one.";
                        return false;
                    }
                }
            }
            if (toStatus == Completed && kind == KindWrite && (facts == null || string.IsNullOrWhiteSpace(facts.Value<string>("idempotency_key"))))
            {
                refusal = "a completed write stage must name the idempotency_key its write ran under; that key is how the write is never replayed";
                return false;
            }
            if (toStatus == Completed && kind == KindAudit && (facts == null || facts["no_blocking_findings"]?.Type != JTokenType.Boolean))
            {
                refusal = "a completed audit stage must state no_blocking_findings as a boolean; an audit without a verdict opens no gate";
                return false;
            }
            if (toStatus == Approved && (facts == null || string.IsNullOrWhiteSpace(facts.Value<string>("identity"))))
            {
                refusal = "an approval names who approved (identity); an anonymous approval is not one";
                return false;
            }
            Apply(record, stage, toStatus, facts, utc);
            return true;
        }

        private static IEnumerable<string> AllowedFrom(string from, string kind)
        {
            return Transitions.Where(t => t.Key.StartsWith(from + ">", StringComparison.Ordinal) && t.Value.Contains(kind))
                              .Select(t => t.Key.Substring(from.Length + 1));
        }

        private static void Apply(JObject record, JObject stage, string toStatus, JObject facts, DateTime utc)
        {
            string from = stage.Value<string>("status");
            stage["status"] = toStatus;
            stage["updated_utc"] = utc.ToString("o", CultureInfo.InvariantCulture);
            if (facts != null) stage[toStatus + "_facts"] = facts.DeepClone();
            ((JArray)stage["history"]).Add(new JObject
            {
                ["utc"] = utc.ToString("o", CultureInfo.InvariantCulture), ["from"] = from, ["to"] = toStatus,
                ["facts"] = facts?.DeepClone() ?? JValue.CreateNull()
            });
            if (toStatus == Pending)
                foreach (string s in new[] { Completed, Approved, AwaitingApproval, Failed, Blocked, Invalidated, Rejected })
                    stage.Remove(s + "_facts");
            record["events"] = (record.Value<int?>("events") ?? 0) + 1;
        }

        /// <summary>
        /// Invalidate a stage and, transitively, everything that depends on it. Stages
        /// that had not run are left alone. Returns the keys invalidated, in order.
        /// </summary>
        public static List<string> Invalidate(JObject record, string key, string reason, DateTime utc)
        {
            var done = new List<string>();
            var queue = new Queue<string>(); queue.Enqueue(key);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                string k = queue.Dequeue();
                if (!seen.Add(k)) continue;
                JObject stage = Stage(record, k);
                if (stage == null) continue;
                string status = stage.Value<string>("status");
                if (status == Completed || status == Approved || status == AwaitingApproval)
                {
                    Apply(record, stage, Invalidated, new JObject { ["reason"] = reason, ["cascaded_from"] = k == key ? JValue.CreateNull() : (JToken)key }, utc);
                    done.Add(k);
                }
                foreach (JObject other in ((JArray)record["stages"]).OfType<JObject>())
                    if (other["depends_on"].Values<string>().Contains(k)) queue.Enqueue(other.Value<string>("key"));
            }
            return done;
        }

        // =====================================================================
        // Resume and the gate.
        // =====================================================================

        /// <summary>
        /// What to do next. Completed writes are listed with their keys so nobody
        /// replays them; stages whose scope the host must re-read are listed so the
        /// host can confirm or invalidate; the next runnable stage is the first
        /// pending one whose dependencies are done.
        /// </summary>
        public static JObject Resume(JObject record)
        {
            var stages = ((JArray)record["stages"]).OfType<JObject>().ToList();
            JObject next = stages.FirstOrDefault(s => s.Value<string>("status") == Pending &&
                s["depends_on"].Values<string>().All(d => { string ds = Stage(record, d)?.Value<string>("status"); return ds == Completed || ds == Approved; }));
            var completedWrites = stages.Where(s => s.Value<string>("kind") == KindWrite && s.Value<string>("status") == Completed)
                .Select(s => new JObject
                {
                    ["key"] = s["key"].DeepClone(), ["idempotency_key"] = s["completed_facts"]?["idempotency_key"]?.DeepClone(),
                    ["element_ids"] = s["completed_facts"]?["element_ids"]?.DeepClone() ?? new JArray(),
                    ["never_replay"] = true
                });
            var reverify = stages.Where(s => (s.Value<string>("status") == Completed || s.Value<string>("status") == Approved) &&
                                             s[s.Value<string>("status") + "_facts"]?["scope"] is JArray)
                .Select(s => new JObject { ["key"] = s["key"].DeepClone(), ["status"] = s["status"].DeepClone(), ["scope"] = s[s.Value<string>("status") + "_facts"]["scope"].DeepClone() });
            var attention = stages.Where(s => s.Value<string>("status") == Failed || s.Value<string>("status") == Blocked ||
                                              s.Value<string>("status") == Invalidated || s.Value<string>("status") == Rejected)
                .Select(s => new JObject { ["key"] = s["key"].DeepClone(), ["status"] = s["status"].DeepClone(),
                                           ["reason"] = s[s.Value<string>("status") + "_facts"]?["reason"]?.DeepClone() ?? JValue.CreateNull() });
            // IN DOUBT: a stage that was started and never reached a terminal record.
            // After a crash nobody knows whether its write landed, so it is neither
            // offered again nor assumed done - it is named, and only an explicit
            // completed (with the ids the host can re-read) or failed resolves it.
            var inDoubt = stages.Where(s => s.Value<string>("status") == InProgress)
                .Select(s => new JObject
                {
                    ["key"] = s["key"].DeepClone(), ["kind"] = s["kind"].DeepClone(), ["since_utc"] = s["updated_utc"].DeepClone(),
                    ["resolution"] = s.Value<string>("kind") == KindWrite
                        ? "re-read the model: if the write landed, record completed with its idempotency_key and element_ids (the host verifies them); if not, record failed and re-arm"
                        : "record completed with its facts, or failed"
                });
            return new JObject
            {
                ["delivery_id"] = record["delivery_id"].DeepClone(),
                ["next_stage"] = next == null ? (JToken)JValue.CreateNull() : new JObject { ["key"] = next["key"].DeepClone(), ["tool"] = next["tool"].DeepClone(), ["arguments"] = next["arguments"].DeepClone(), ["acceptance"] = next["acceptance"].DeepClone() },
                ["completed_writes"] = new JArray(completedWrites),
                ["needs_reverification"] = new JArray(reverify),
                ["needs_attention"] = new JArray(attention),
                ["in_doubt"] = new JArray(inDoubt),
                ["counts"] = Counts(record),
                ["publish_gate"] = PublishGate(record),
                ["policy"] = "completed writes are never replayed; a stage whose scope changed is invalidated, not repeated blindly; " +
                             "re-verify scopes with the host before resuming, then run next_stage with a fresh rehearsal"
            };
        }

        public static JObject Counts(JObject record)
        {
            var counts = new JObject();
            var stages = ((JArray)record["stages"]).OfType<JObject>().ToList();
            foreach (string s in AllStatuses) counts[s] = stages.Count(x => x.Value<string>("status") == s);
            counts["total"] = stages.Count;
            return counts;
        }

        /// <summary>
        /// May the publication run? Only when every other stage is completed or
        /// approved, the audit reported no blocking findings, and nothing is
        /// invalidated, failed, blocked, rejected or still awaiting approval.
        /// </summary>
        public static JObject PublishGate(JObject record)
        {
            var reasons = new List<string>();
            var stages = ((JArray)record["stages"]).OfType<JObject>().ToList();
            JObject publish = stages.FirstOrDefault(s => s.Value<string>("kind") == KindPublish);
            foreach (JObject s in stages)
            {
                if (s == publish) continue;
                string status = s.Value<string>("status"), kind = s.Value<string>("kind");
                bool ok = status == Completed || (kind == KindApprovalCapture && status == Approved);
                if (!ok) reasons.Add("stage '" + s.Value<string>("key") + "' is " + status);
            }
            JObject audit = stages.FirstOrDefault(s => s.Value<string>("kind") == KindAudit);
            if (audit == null) reasons.Add("the plan has no audit stage");
            else if (audit.Value<string>("status") == Completed && audit["completed_facts"]?.Value<bool?>("no_blocking_findings") != true)
                reasons.Add("the audit completed with blocking findings (or without a verdict)");
            if (publish != null && (publish.Value<string>("status") == Completed))
                reasons.Add("publication already completed for this delivery; a second publication is a new delivery or an explicit reopen");
            return new JObject
            {
                ["open"] = reasons.Count == 0,
                ["reasons"] = new JArray(reasons),
                ["means"] = "open only on a complete, audited run whose every sheet approval is current; any invalidation closes it again"
            };
        }

        // =====================================================================
        // Persistence: append-only events, replayed on read.
        // =====================================================================

        public static string FileFor(string directory, string deliveryId)
        {
            return Path.Combine(directory, deliveryId + ".jsonl");
        }

        public static void AppendEvent(IJobSink sink, string directory, string deliveryId, JObject evt)
        {
            sink.EnsureDirectory(directory);
            sink.Append(FileFor(directory, deliveryId), evt.ToString(Formatting.None));
        }

        public static JObject OpenedEvent(JObject record) { return new JObject { ["type"] = "opened", ["record"] = record.DeepClone() }; }
        public static JObject TransitionEvent(string key, string toStatus, JObject facts, DateTime utc)
        {
            return new JObject { ["type"] = "transition", ["utc"] = utc.ToString("o", CultureInfo.InvariantCulture), ["key"] = key, ["to"] = toStatus, ["facts"] = facts?.DeepClone() ?? JValue.CreateNull() };
        }
        public static JObject InvalidationEvent(string key, string reason, DateTime utc)
        {
            return new JObject { ["type"] = "invalidate", ["utc"] = utc.ToString("o", CultureInfo.InvariantCulture), ["key"] = key, ["reason"] = reason };
        }

        /// <summary>
        /// Fold the event lines back into a record. A line that cannot be applied is
        /// reported in replay_problems and never silently skipped; the record still
        /// reflects every line before it.
        /// </summary>
        public static JObject Replay(IEnumerable<string> lines)
        {
            JObject record = null;
            var problems = new JArray();
            int n = 0;
            foreach (string raw in lines)
            {
                n++;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                JObject evt;
                try { evt = JObject.Parse(raw); }
                catch (Exception ex) { problems.Add(new JObject { ["line"] = n, ["problem"] = "unparseable: " + ex.Message }); continue; }
                string type = evt.Value<string>("type");
                DateTime utc = DateTime.TryParse(evt.Value<string>("utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed : DateTime.MinValue;
                if (type == "opened")
                {
                    if (record != null) { problems.Add(new JObject { ["line"] = n, ["problem"] = "a second opened event; ignored" }); continue; }
                    record = evt["record"] as JObject;
                    if (record == null) problems.Add(new JObject { ["line"] = n, ["problem"] = "opened event without a record" });
                    continue;
                }
                if (record == null) { problems.Add(new JObject { ["line"] = n, ["problem"] = type + " before opened; ignored" }); continue; }
                if (type == "transition")
                {
                    string refusal;
                    if (!TryTransition(record, evt.Value<string>("key"), evt.Value<string>("to"), evt["facts"] as JObject, utc, out refusal))
                        problems.Add(new JObject { ["line"] = n, ["problem"] = refusal });
                }
                else if (type == "invalidate")
                    Invalidate(record, evt.Value<string>("key"), evt.Value<string>("reason"), utc);
                else problems.Add(new JObject { ["line"] = n, ["problem"] = "unknown event type '" + type + "'" });
            }
            if (record == null) return null;
            record["replay_problems"] = problems;
            record["replayed_lines"] = n;
            return record;
        }

        public static JObject Load(string directory, string deliveryId)
        {
            string path = FileFor(directory, deliveryId);
            if (!File.Exists(path)) return null;
            return Replay(File.ReadAllLines(path, Encoding.UTF8));
        }

        /// <summary>A deterministic delivery id when the caller gives none.</summary>
        public static string DeriveId(string profileSha256, string documentFingerprint)
        {
            string material = (profileSha256 ?? "") + "|" + (documentFingerprint ?? "");
            string hash = RequestFingerprint.Sha256Hex(material);
            return "delivery-" + hash.Substring(0, 16);
        }
    }
}
