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
// shape has failed repeatedly, a person may quarantine it: further calls matching
// that shape are REFUSED, with the evidence that led to the quarantine and the
// exact way to lift it. It is not automatic, it is not permanent, and it never
// hides: a refusal that did not say it came from a quarantine would be
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

        public static Dictionary<string, RepairEntry> Load(string path)
        {
            var entries = new Dictionary<string, RepairEntry>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path)) return entries;
                JObject root = JObject.Parse(File.ReadAllText(path));
                JArray rows = root["entries"] as JArray;
                if (rows == null) return entries;
                foreach (JToken token in rows)
                {
                    var row = token as JObject;
                    if (row == null) continue;
                    RepairEntry entry = FromJson(row);
                    if (entry?.Shape != null) entries[entry.Shape] = entry;
                }
            }
            catch { /* an unreadable memory is an empty one; it never blocks a command */ }
            return entries;
        }

        public static void Save(string path, IEnumerable<RepairEntry> entries)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Newest first, bounded. A memory that grows forever is a file nobody reads
            // and a startup cost everybody pays.
            List<RepairEntry> ordered = entries
                .OrderByDescending(e => e.LastSeenUtc ?? "")
                .Take(MaxEntries)
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
            File.WriteAllText(path, document.ToString(Newtonsoft.Json.Formatting.Indented),
                              new UTF8Encoding(false));
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
            ["quarantined_by"] = e.QuarantinedBy == null ? (JToken)JValue.CreateNull() : e.QuarantinedBy
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
            QuarantinedBy = row.Value<string>("quarantined_by")
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
            else if (entry.Occurrences >= SuggestAfterOccurrences)
            {
                advice["suggestion"] =
                    "this exact failure shape has been seen " + entry.Occurrences + " times and nobody has " +
                    "recorded a remedy. It is worth someone's attention rather than another retry.";
            }
            return advice;
        }

        /// <summary>
        /// May a call of this shape run? The refusal, or null.
        ///
        /// A quarantine REFUSES and says so in those words, with the evidence and the
        /// way out. A refusal that hid its origin would be indistinguishable from a
        /// broken tool, which is the failure mode a quarantine must never create.
        /// </summary>
        public static string QuarantineRefusal(Dictionary<string, RepairEntry> entries, string tool,
                                               string failureClass, string message)
        {
            RepairEntry entry;
            if (!entries.TryGetValue(ShapeOf(tool, failureClass, message), out entry)) return null;
            if (entry.State != StateQuarantined) return null;

            return "This call matches a QUARANTINED failure shape (" + entry.Shape + "). It was quarantined on " +
                   (entry.QuarantinedAtUtc ?? "an unrecorded date") +
                   (string.IsNullOrWhiteSpace(entry.QuarantinedBy) ? "" : " by " + entry.QuarantinedBy) +
                   " after " + entry.Occurrences + " occurrence(s), because: " +
                   (entry.QuarantineReason ?? "no reason was recorded") + ". Nothing was run. The quarantine is " +
                   "REVERSIBLE and this is how: lift it from the repair memory and the call proceeds normally. " +
                   "It is not automatic, it does not expire silently, and it applies only to this exact shape.";
        }

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
