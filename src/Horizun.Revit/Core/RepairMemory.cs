// -----------------------------------------------------------------------------
// Horizun Revit MCP - the repair memory, and the quarantine. Original Horizun code.
//
// G18 of the 2026-09-14 competitive inventory: "guardar forma de error -> solución
// validada sin datos de cliente; cuarentena explícita reversible con evidencia".
//
// THE SHAPE OF AN ERROR, NOT THE ERROR. This is the whole design. A Revit failure
// message carries the model's own words - element ids, family names, file paths,
// a client's project number - and a memory that stored those would be a file full
// of somebody else's data, sitting on a consultant's laptop, surviving the
// project. So what is stored is a NORMALISED SHAPE: the tool, the failure class,
// and the message with every number, path, quoted name and identifier replaced by
// a token. Two failures with the same shape are the same problem; nothing about
// either model can be reconstructed from the record.
//
// NOTHING IS LEARNED WITHOUT A HUMAN. An entry starts as `observed`. It becomes a
// `remedy` only when a person records one, and it is never applied automatically
// by anything: the memory ANSWERS a question ("has this shape been seen, and did
// anything work?") and never acts. A memory that acted would be a second decision
// maker with no permission model.
//
// THE QUARANTINE IS THE OTHER HALF, AND IT IS EXPLICIT AND REVERSIBLE. When a
// shape has failed repeatedly, a person may quarantine it. A shape is the failure's
// own message, known only after a call has failed, so the shape cannot refuse
// anything by itself; what CAN be recognised before a call runs is the call: the
// tool and its arguments. `observe` and `quarantine` take the failing call's
// arguments and keep a HASH of their normalised form (CallFingerprint - never the
// values), and while the shape is quarantined the server refuses a call with that
// fingerprint before it reaches the add-in, with the evidence and the exact way to
// lift it (code call_quarantined). It is not automatic, it is not permanent, and it
// never hides: a refusal that did not say it came from a quarantine would be
// indistinguishable from the tool being broken.
//
// REVIT-FREE ON PURPOSE. The normalisation, the matching and the quarantine rules
// are arithmetic over strings, and arithmetic is provable without a building.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One remembered failure shape and whatever is known about fixing it.</summary>
    public sealed class RepairEntry
    {
        public string Shape;              // the hash: what identifies this problem
        public string Tool;
        public string FailureClass;
        public string NormalizedMessage;  // the redacted message, which IS the shape's text
        public int Occurrences;
        public string FirstSeenUtc, LastSeenUtc;

        /// <summary>observed | remedy_proposed | remedy_validated | quarantined</summary>
        public string State = RepairMemory.StateObserved;

        public string Remedy;             // what a person recorded as the fix
        public string RemedyAuthor;
        public string RemedyAtUtc;
        public int RemedyConfirmations;   // how many times a person said it worked again

        public string QuarantineReason;
        public string QuarantinedAtUtc;
        public string QuarantinedBy;

        /// <summary>
        /// Hashes of the CALLS that failed with this shape (tool + normalised arguments,
        /// see RepairMemory.CallFingerprint) - never the arguments themselves. While the
        /// shape is quarantined, the server refuses a call whose fingerprint is here.
        /// </summary>
        public List<string> ArgumentFingerprints = new List<string>();
    }

    public static class RepairMemory
    {
        public const string StateObserved = "observed";
        public const string StateProposed = "remedy_proposed";
        public const string StateValidated = "remedy_validated";
        public const string StateQuarantined = "quarantined";

        /// <summary>
        /// How many identical shapes it takes before the memory SUGGESTS that somebody
        /// look. It suggests; it never acts, and it never quarantines on its own.
        /// </summary>
        public const int SuggestAfterOccurrences = 3;

        public const int MaxEntries = 500;

        /// <summary>How many distinct failing calls one shape remembers; the oldest goes first.</summary>
        public const int MaxFingerprintsPerShape = 50;

        /// <summary>The code a quarantined call is refused with.</summary>
        public const string CallQuarantinedCode = "call_quarantined";

        // =====================================================================
        // The call's fingerprint - what CAN be recognised before a call runs
        // =====================================================================

        /// <summary>
        /// Arguments that make a retry look new without making it a different call. A
        /// fresh idempotency_key is exactly what a client sends when it tries the same
        /// thing again, and a confirmation token is minted per rehearsal. Everything else
        /// - dry_run, target_document, every value - is part of what the call does.
        /// </summary>
        private static readonly HashSet<string> RetryEnvelope =
            new HashSet<string>(StringComparer.Ordinal) { "idempotency_key", "confirmation_token", "_meta" };

        /// <summary>
        /// The identity of a CALL: the tool and its arguments in a canonical form (object
        /// keys in ordinal order at every depth, the retry envelope removed, an integral
        /// number written as an integer), hashed. Only the hash is ever stored: arguments
        /// carry the model's own words, and this memory stores none of them.
        /// </summary>
        public static string CallFingerprint(string tool, JObject arguments)
        {
            var canonical = new StringBuilder("horizun.call/1\n");
            canonical.Append(tool ?? "").Append('\n');
            var trimmed = new JObject();
            if (arguments != null)
                foreach (JProperty p in arguments.Properties())
                    if (!RetryEnvelope.Contains(p.Name)) trimmed[p.Name] = p.Value;
            WriteCanonical(trimmed, canonical);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static void WriteCanonical(JToken token, StringBuilder sb)
        {
            switch (token?.Type ?? JTokenType.Null)
            {
                case JTokenType.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (JProperty p in ((JObject)token).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(Newtonsoft.Json.JsonConvert.ToString(p.Name)).Append(':');
                        WriteCanonical(p.Value, sb);
                    }
                    sb.Append('}');
                    break;
                case JTokenType.Array:
                    sb.Append('[');
                    int n = 0;
                    foreach (JToken item in (JArray)token)
                    {
                        if (n++ > 0) sb.Append(',');
                        WriteCanonical(item, sb);
                    }
                    sb.Append(']');
                    break;
                case JTokenType.Float:
                    double d = token.Value<double>();
                    if (!double.IsInfinity(d) && !double.IsNaN(d) && Math.Floor(d) == d && Math.Abs(d) < 1e15)
                        sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                    else
                        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JTokenType.Integer:
                    sb.Append(Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture));
                    break;
                case JTokenType.Null:
                case JTokenType.Undefined:
                    sb.Append("null");
                    break;
                case JTokenType.Boolean:
                    sb.Append(token.Value<bool>() ? "true" : "false");
                    break;
                default:
                    // Strings, and anything else a parser kept as a value: its exact text, quoted.
                    sb.Append(Newtonsoft.Json.JsonConvert.ToString(
                        Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture)));
                    break;
            }
        }

        /// <summary>Remember that this call failed with this shape. Idempotent and bounded.</summary>
        public static bool AttachCall(RepairEntry entry, string fingerprint)
        {
            if (entry == null || string.IsNullOrEmpty(fingerprint)) return false;
            if (entry.ArgumentFingerprints == null) entry.ArgumentFingerprints = new List<string>();
            if (entry.ArgumentFingerprints.Contains(fingerprint)) return false;
            entry.ArgumentFingerprints.Add(fingerprint);
            while (entry.ArgumentFingerprints.Count > MaxFingerprintsPerShape) entry.ArgumentFingerprints.RemoveAt(0);
            return true;
        }

        /// <summary>
        /// The quarantined entry this call matches, or null. Matched on the tool and the
        /// call fingerprint - the only thing knowable before the call runs.
        /// </summary>
        public static RepairEntry QuarantinedCall(IEnumerable<RepairEntry> entries, string tool, JObject arguments)
        {
            if (entries == null || string.IsNullOrEmpty(tool)) return null;
            string fingerprint = null;
            foreach (RepairEntry e in entries)
            {
                if (e == null || e.State != StateQuarantined) continue;
                if (!string.Equals(e.Tool, tool, StringComparison.Ordinal)) continue;
                if (e.ArgumentFingerprints == null || e.ArgumentFingerprints.Count == 0) continue;
                fingerprint = fingerprint ?? CallFingerprint(tool, arguments);
                if (e.ArgumentFingerprints.Contains(fingerprint)) return e;
            }
            return null;
        }

        /// <summary>
        /// The memory as the call gate reads it: no lock, no side effect. Unlike Load it
        /// never moves a damaged file aside - that is the memory tool's decision, taken
        /// under its mutex. A file it cannot read answers empty, with the problem named.
        /// </summary>
        public static List<RepairEntry> ReadForGate(string path, out string problem)
        {
            problem = null;
            var list = new List<RepairEntry>();
            if (!File.Exists(path)) return list;
            try
            {
                JArray rows = JsonText.ParseObject(File.ReadAllText(path))["entries"] as JArray;
                if (rows == null) return list;
                foreach (JToken token in rows)
                {
                    var row = token as JObject;
                    if (row == null) continue;
                    RepairEntry e = FromJson(row);
                    if (e?.Shape != null) list.Add(e);
                }
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                list.Clear();
            }
            return list;
        }

        // =====================================================================
        // Normalisation - where the client's data stops existing
        // =====================================================================

        private static readonly Regex[] Redactions =
        {
            // Windows and UNC paths, before anything else: a path contains a user name.
            new Regex(@"(?i)\b[a-z]:\\[^\s""'<>|]+", RegexOptions.Compiled),
            new Regex(@"\\\\[^\s""'<>|]+", RegexOptions.Compiled),
            // Anything quoted: family names, type names, document titles, sheet numbers.
            new Regex("\"[^\"]*\"", RegexOptions.Compiled),
            new Regex("'[^']*'", RegexOptions.Compiled),
            // GUIDs and Revit unique ids.
            new Regex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(-[0-9a-f]{8})?\b",
                      RegexOptions.Compiled),
            // Numbers: element ids, counts, measurements. A shape is about the KIND of
            // failure, and "12 of 15 failed" and "3 of 4 failed" are the same kind.
            new Regex(@"-?\b\d+(\.\d+)?\b", RegexOptions.Compiled)
        };

        private static readonly string[] Tokens = { "<path>", "<path>", "<name>", "<name>", "<guid>", "<n>" };

        /// <summary>
        /// The message with everything specific removed. This is what is stored, and it
        /// is the only thing that is: the original never reaches the file.
        /// </summary>
        public static string Normalize(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            string working = message.Replace("\r", " ").Replace("\n", " ");
            for (int i = 0; i < Redactions.Length; i++)
                working = Redactions[i].Replace(working, Tokens[i]);
            working = Regex.Replace(working, @"\s+", " ").Trim();
            // A bound, so one enormous Revit message cannot become the file.
            return working.Length <= 400 ? working : working.Substring(0, 400) + "…";
        }

        /// <summary>
        /// The identity of a failure: tool, class and normalised text, hashed. Two
        /// failures that hash the same are the same problem as far as anything here can
        /// tell, and that is exactly the claim the memory makes.
        /// </summary>
        public static string ShapeOf(string tool, string failureClass, string message)
        {
            string canonical = (tool ?? "") + "\0" + (failureClass ?? "") + "\0" + Normalize(message);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Would this normalised message still carry something specific?
        ///
        /// The redaction list is deliberately broad, and this is the assertion that keeps
        /// it honest: anything that still looks like a path, a quoted name or a long
        /// digit run means the normaliser missed something, and the caller must NOT
        /// store it. Failing closed here costs one remembered failure; failing open puts
        /// a client's project number in a file that outlives the project.
        /// </summary>
        public static bool LooksRedacted(string normalized)
        {
            if (string.IsNullOrEmpty(normalized)) return true;
            if (normalized.IndexOf('\\') >= 0) return false;
            if (normalized.IndexOf('"') >= 0 || normalized.IndexOf('\'') >= 0) return false;
            if (Regex.IsMatch(normalized, @"\d{3,}")) return false;
            if (Regex.IsMatch(normalized, @"(?i)\b[a-z]:")) return false;
            return true;
        }

        // =====================================================================
        // Storage
        // =====================================================================

        public static string Path() =>
            System.IO.Path.Combine(HorizunPaths.DataRoot(), "repair-memory.json");

        public static Dictionary<string, RepairEntry> Load(string path) => Load(path, out _);

        /// <summary>
        /// The memory, or an empty one with <paramref name="problem"/> naming why.
        ///
        /// A file that does not parse used to read as EMPTY - and the next Save wrote
        /// that emptiness over it, losing every recorded remedy without a word (audit
        /// 2026-10-08). It is now MOVED ASIDE to "&lt;file&gt;.corrupt-&lt;utc&gt;", kept, and
        /// named in <paramref name="problem"/>; only then does the memory start empty.
        /// If it cannot be moved aside, this throws rather than let a Save destroy it.
        /// </summary>
        public static Dictionary<string, RepairEntry> Load(string path, out string problem)
        {
            problem = null;
            var entries = new Dictionary<string, RepairEntry>(StringComparer.Ordinal);
            if (!File.Exists(path)) return entries;
            JArray rows;
            try { rows = JsonText.ParseObject(File.ReadAllText(path))["entries"] as JArray; }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is InvalidCastException)
            {
                string aside = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
                try { File.Move(path, aside); }
                catch (Exception move)
                {
                    throw new IOException("The repair memory at " + path + " does not parse (" + ex.Message + ") and could " +
                                          "not be moved aside (" + move.Message + "). Nothing was written, so it is not " +
                                          "overwritten; move or repair it by hand.", move);
                }
                problem = "The repair memory did not parse (" + ex.Message + "). It was kept, unchanged, as " +
                          System.IO.Path.GetFileName(aside) + " beside the new one, and this memory starts empty.";
                return entries;
            }
            if (rows == null) return entries;
            foreach (JToken token in rows)
            {
                var row = token as JObject;
                if (row == null) continue;
                RepairEntry entry = FromJson(row);
                if (entry?.Shape != null) entries[entry.Shape] = entry;
            }
            return entries;
        }

        /// <summary>
        /// The name of the mutex every process takes around a load-modify-save of this
        /// memory. Claude's and Codex's MCP servers are separate processes; an
        /// in-process lock in each let their cycles interleave and one overwrite the other.
        /// </summary>
        public static string MutexName(string path)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch { full = path ?? ""; }
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
                var sb = new StringBuilder("Local\\HorizunRepairMemory-");
                for (int i = 0; i < 10; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static void Save(string path, IEnumerable<RepairEntry> entries)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Owner decisions must not expire because later entries arrived. Bound only
            // observations and proposals; if there are more than MaxEntries owner decisions,
            // preserve them all rather than silently lifting a quarantine or losing a remedy.
            List<RepairEntry> all = entries.Where(e => e != null).ToList();
            List<RepairEntry> protectedEntries = all
                .Where(e => e.State == StateQuarantined || e.State == StateValidated)
                .OrderByDescending(e => e.State == StateQuarantined)
                .ThenByDescending(e => e.LastSeenUtc ?? "", JsonText.ByInstant)
                .ToList();
            List<RepairEntry> ordered = protectedEntries.Concat(all
                .Where(e => e.State != StateQuarantined && e.State != StateValidated)
                .OrderByDescending(e => e.LastSeenUtc ?? "", JsonText.ByInstant)
                .Take(Math.Max(0, MaxEntries - protectedEntries.Count)))
                .ToList();

            var document = new JObject
            {
                ["schema"] = "horizun.repair-memory/1",
                ["written_utc"] = DateTime.UtcNow.ToString("o"),
                ["means"] =
                    "Failure SHAPES, never failure messages. Every path, quoted name, identifier and number is " +
                    "replaced before anything is written, so nothing about any model can be reconstructed from " +
                    "this file. A remedy is recorded by a person and applied by nobody.",
                ["entries"] = new JArray(ordered.Select(ToJson))
            };
            // Whole or not at all: a crash mid-write used to leave half a file, which the
            // next Load read as an empty memory.
            AtomicFile.WriteAllText(path, document.ToString(Newtonsoft.Json.Formatting.Indented));
        }

        public static JObject ToJson(RepairEntry e) => new JObject
        {
            ["shape"] = e.Shape,
            ["tool"] = e.Tool,
            ["failure_class"] = e.FailureClass,
            ["normalized_message"] = e.NormalizedMessage,
            ["occurrences"] = e.Occurrences,
            ["first_seen_utc"] = e.FirstSeenUtc,
            ["last_seen_utc"] = e.LastSeenUtc,
            ["state"] = e.State,
            ["remedy"] = e.Remedy == null ? (JToken)JValue.CreateNull() : e.Remedy,
            ["remedy_author"] = e.RemedyAuthor == null ? (JToken)JValue.CreateNull() : e.RemedyAuthor,
            ["remedy_at_utc"] = e.RemedyAtUtc == null ? (JToken)JValue.CreateNull() : e.RemedyAtUtc,
            ["remedy_confirmations"] = e.RemedyConfirmations,
            ["quarantine_reason"] = e.QuarantineReason == null ? (JToken)JValue.CreateNull() : e.QuarantineReason,
            ["quarantined_at_utc"] = e.QuarantinedAtUtc == null ? (JToken)JValue.CreateNull() : e.QuarantinedAtUtc,
            ["quarantined_by"] = e.QuarantinedBy == null ? (JToken)JValue.CreateNull() : e.QuarantinedBy,
            ["argument_fingerprints"] = new JArray((e.ArgumentFingerprints ?? new List<string>()).Cast<object>().ToArray())
        };

        public static RepairEntry FromJson(JObject row) => new RepairEntry
        {
            Shape = row.Value<string>("shape"),
            Tool = row.Value<string>("tool"),
            FailureClass = row.Value<string>("failure_class"),
            NormalizedMessage = row.Value<string>("normalized_message"),
            Occurrences = row.Value<int?>("occurrences") ?? 0,
            FirstSeenUtc = row.Value<string>("first_seen_utc"),
            LastSeenUtc = row.Value<string>("last_seen_utc"),
            State = row.Value<string>("state") ?? StateObserved,
            Remedy = row.Value<string>("remedy"),
            RemedyAuthor = row.Value<string>("remedy_author"),
            RemedyAtUtc = row.Value<string>("remedy_at_utc"),
            RemedyConfirmations = row.Value<int?>("remedy_confirmations") ?? 0,
            QuarantineReason = row.Value<string>("quarantine_reason"),
            QuarantinedAtUtc = row.Value<string>("quarantined_at_utc"),
            QuarantinedBy = row.Value<string>("quarantined_by"),
            // Only well-formed hashes are read back: this list makes calls refuse, so a
            // hand-edited value that is not a fingerprint must not become one.
            ArgumentFingerprints = (row["argument_fingerprints"] as JArray)?
                .Where(t => t.Type == JTokenType.String && Regex.IsMatch((string)t, "^[0-9a-f]{32}$"))
                .Select(t => (string)t).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>()
        };

        // =====================================================================
        // The two things it does
        // =====================================================================

        /// <summary>
        /// Record that a failure of this shape happened.
        ///
        /// Returns the entry, or NULL when the message could not be normalised safely -
        /// in which case nothing is stored at all. That is the fail-closed half of the
        /// privacy promise: a message the redactor could not clean is a message that
        /// does not enter the file.
        /// </summary>
        public static RepairEntry Observe(Dictionary<string, RepairEntry> entries, string tool,
                                          string failureClass, string message, string nowUtc)
        {
            string normalized = Normalize(message);
            if (!LooksRedacted(normalized)) return null;

            string shape = ShapeOf(tool, failureClass, message);
            RepairEntry entry;
            if (!entries.TryGetValue(shape, out entry))
            {
                entry = new RepairEntry
                {
                    Shape = shape,
                    Tool = tool,
                    FailureClass = failureClass,
                    NormalizedMessage = normalized,
                    FirstSeenUtc = nowUtc,
                    Occurrences = 0,
                    State = StateObserved
                };
                entries[shape] = entry;
            }
            entry.Occurrences++;
            entry.LastSeenUtc = nowUtc;
            return entry;
        }

        /// <summary>
        /// What this shape's history says, for a caller about to try again - or null
        /// when the memory has nothing to offer.
        ///
        /// ADVICE, NEVER ACTION. The caller decides; this hands over what a person
        /// previously wrote down and how many times it has been seen.
        /// </summary>
        public static JObject Advice(Dictionary<string, RepairEntry> entries, string tool,
                                     string failureClass, string message)
        {
            RepairEntry entry;
            if (!entries.TryGetValue(ShapeOf(tool, failureClass, message), out entry)) return null;

            var advice = new JObject
            {
                ["shape"] = entry.Shape,
                ["occurrences"] = entry.Occurrences,
                ["state"] = entry.State,
                ["first_seen_utc"] = entry.FirstSeenUtc
            };
            if (!string.IsNullOrWhiteSpace(entry.Remedy))
            {
                advice["remedy"] = entry.Remedy;
                advice["remedy_confirmations"] = entry.RemedyConfirmations;
                advice["remedy_means"] =
                    "a person recorded this, and nothing applies it for you. It is what worked before on a " +
                    "failure of the same SHAPE - which is not proof that it is the same cause.";
            }
            if (entry.State == StateQuarantined)
            {
                advice["quarantine"] = new JObject
                {
                    ["reason"] = entry.QuarantineReason,
                    ["by"] = entry.QuarantinedBy,
                    ["at_utc"] = entry.QuarantinedAtUtc,
                    ["means"] =
                        "QUARANTINED: a person decided calls that fail this way must not be retried. Do not retry; " +
                        "ask the owner. The bridge refuses (code " + CallQuarantinedCode + ") only the calls " +
                        "recorded against this shape with call_arguments - " + (entry.ArgumentFingerprints?.Count ?? 0) +
                        " of them: a shape alone is known only once a call has failed. Lift it with operation=release.",
                    ["refuses_calls"] = entry.ArgumentFingerprints?.Count ?? 0
                };
            }
            else if (string.IsNullOrWhiteSpace(entry.Remedy) && entry.Occurrences >= SuggestAfterOccurrences)
            {
                advice["suggestion"] =
                    "this exact failure shape has been seen " + entry.Occurrences + " times and nobody has " +
                    "recorded a remedy. It is worth someone's attention rather than another retry.";
            }
            return advice;
        }

        /// <summary>
        /// The refusal for a call that matched a quarantine (see QuarantinedCall), or null.
        ///
        /// A quarantine REFUSES and says so in those words, with the evidence and the
        /// way out. A refusal that hid its origin would be indistinguishable from a
        /// broken tool, which is the failure mode a quarantine must never create.
        /// </summary>
        public static string QuarantineRefusal(RepairEntry entry)
        {
            if (entry == null) return null;
            return "This call is QUARANTINED: the same tool with the same arguments already failed with shape " +
                   entry.Shape + ", and it was quarantined on " + (entry.QuarantinedAtUtc ?? "an unrecorded date") +
                   (string.IsNullOrWhiteSpace(entry.QuarantinedBy) ? "" : " by " + entry.QuarantinedBy) +
                   " after " + entry.Occurrences + " occurrence(s), because: " +
                   (entry.QuarantineReason ?? "no reason was recorded") + ". Nothing was run and nothing was sent " +
                   "to Revit. Do not retry it as it is; ask the owner. The quarantine is REVERSIBLE: " +
                   ReleaseHint(entry) + " It does not expire silently and it refuses only these exact calls - " +
                   "an argument changed (other than idempotency_key or confirmation_token) is a different call.";
        }

        /// <summary>The exact way to lift a quarantine.</summary>
        public static string ReleaseHint(RepairEntry entry) =>
            "horizun_repair_memory operation=release shape=" + (entry?.Shape ?? "<shape>") + " lifts it.";

        /// <summary>
        /// Quarantine a shape. Requires a reason and an author: a quarantine with neither
        /// is a refusal nobody can argue with later.
        /// </summary>
        public static string Quarantine(RepairEntry entry, string reason, string author, string nowUtc)
        {
            if (entry == null) return "there is no remembered shape with that id.";
            if (string.IsNullOrWhiteSpace(reason))
                return "a quarantine needs a reason: it makes a call refuse, and a refusal nobody can argue " +
                       "with later is worse than the failure it was meant to stop.";
            if (string.IsNullOrWhiteSpace(author))
                return "a quarantine needs an author. Nothing here quarantines by itself.";
            entry.State = StateQuarantined;
            entry.QuarantineReason = reason;
            entry.QuarantinedBy = author;
            entry.QuarantinedAtUtc = nowUtc;
            return null;
        }

        /// <summary>Lift a quarantine, back to whatever the shape's remedy state was.</summary>
        public static string Release(RepairEntry entry)
        {
            if (entry == null) return "there is no remembered shape with that id.";
            if (entry.State != StateQuarantined) return "that shape is not quarantined.";
            entry.State = string.IsNullOrWhiteSpace(entry.Remedy)
                ? StateObserved
                : (entry.RemedyConfirmations > 0 ? StateValidated : StateProposed);
            entry.QuarantineReason = null;
            entry.QuarantinedAtUtc = null;
            entry.QuarantinedBy = null;
            return null;
        }

        /// <summary>
        /// Record what a person found worked. Recording it twice for the same shape
        /// counts as a confirmation, which is the only thing that moves a remedy from
        /// proposed to validated - the memory never decides that on its own.
        /// </summary>
        public static string RecordRemedy(RepairEntry entry, string remedy, string author, string nowUtc)
        {
            if (entry == null) return "there is no remembered shape with that id.";
            if (string.IsNullOrWhiteSpace(remedy)) return "a remedy needs text: what actually worked.";
            if (string.IsNullOrWhiteSpace(author)) return "a remedy needs an author.";
            if (!LooksRedacted(Normalize(remedy)))
                return "that remedy text contains something specific - a path, a quoted name or a long number. " +
                       "This memory stores shapes, not models, and nothing that could identify a project is " +
                       "written to it. Rewrite the remedy in general terms.";

            if (string.Equals(entry.Remedy, remedy, StringComparison.Ordinal))
            {
                entry.RemedyConfirmations++;
                entry.State = StateValidated;
            }
            else
            {
                entry.Remedy = remedy;
                entry.RemedyConfirmations = 0;
                entry.State = StateProposed;
            }
            entry.RemedyAuthor = author;
            entry.RemedyAtUtc = nowUtc;
            return null;
        }
    }
}
