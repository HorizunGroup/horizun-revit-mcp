// -----------------------------------------------------------------------------
// Horizun Revit MCP - promoting a script, and versioning what it becomes.
// Original Horizun code.
//
// G17 ("promover solo después de pruebas y revisión; firma, permisos, versiones y
// trazabilidad del código") and G16 ("activar generación compatible sin reinicio y
// conservar anterior al fallar") of the 2026-09-14 competitive inventory, answered
// by ONE mechanism because they are one problem seen twice: a script somebody
// wrote has to become a named, versioned, reviewable thing, and replacing it must
// be reversible.
//
// THE SENTENCE THAT SHAPES ALL OF IT, from the inventory itself:
//
//   "Nunca conviertas estos mecanismos en una vía para eludir la autorización del
//    dueño para Python."
//
// So a promoted script is NOT a new tool with new rights. It is Python, it runs
// through horizun_execute_python, and it needs the machine owner's persistent
// Python grant exactly as any other script does. Promotion adds provenance, review
// state and versioning; it adds no permission whatsoever, and there is no code
// path here that runs anything. A registry that could execute would be a second
// permission model with no owner behind it.
//
// WHAT CAN AND CANNOT BE RELOADED, said plainly because G16's wording invites the
// wrong answer. A .NET assembly loaded into Revit CANNOT be unloaded - not on
// net48, and not on the .NET that Revit 2025+ hosts it in, because the add-in
// lands in the default load context. "Hot-swapping a compiled command" is
// therefore not a feature anybody can build here, and a mechanism that appeared
// to do it would be lying about which code just ran. What CAN be swapped is what
// is READ FROM DISK PER CALL: Python. So generations are generations of scripts,
// activation is a pointer write, and the previous generation survives a failed
// activation because it was never deleted.
//
// ACTIVATION IS ATOMIC OR IT DID NOT HAPPEN. The pointer is written to a temp file
// and moved over the old one, so a process that dies mid-write leaves the previous
// generation active rather than a half-written pointer nobody can parse.
//
// Revit-free: states, transitions and the pointer are arithmetic over files.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One generation of one promoted script: the bytes, and who vouched for them.</summary>
    public sealed class ScriptGeneration
    {
        public int Number;
        public string Sha256;
        public int Bytes;
        public string CreatedUtc;
        public string Author;

        /// <summary>proposed | reviewed | approved</summary>
        public string State = ScriptPromotion.StateProposed;

        public string ReviewedBy, ReviewedUtc, ReviewNote;
        public string ApprovedBy, ApprovedUtc;

        /// <summary>What the author says this was tested against. Required to leave `proposed`.</summary>
        public string Evidence;

        /// <summary>
        /// What this generation TAKES and what it RETURNS, plus the minimum permission a
        /// caller needs. Declared at propose time and refused without it.
        ///
        /// PER GENERATION, not per script, and that is the point. Generation 3 taking an
        /// argument generation 2 did not is exactly the kind of change a version exists to
        /// record; one contract for the whole script would make every caller guess which
        /// generation it was written against.
        /// </summary>
        public JObject Contract;
    }

    public sealed class PromotedScript
    {
        public string Id;
        public string Title;
        public string Description;
        public string CreatedUtc;
        public int ActiveGeneration;          // 0 = nothing is published

        /// <summary>
        /// When this script was WITHDRAWN, or null. Distinct from ActiveGeneration == 0
        /// with no deactivation record, which means nothing was ever published.
        ///
        /// Both end with nothing being served, and they are not the same event: one is a
        /// script that never got through review, the other is a script somebody pulled
        /// after it did. A caller told the wrong one goes looking for the wrong thing.
        /// </summary>
        public string DeactivatedUtc;

        public string DeactivatedBy;

        /// <summary>Why it was withdrawn. Required - a withdrawal nobody explained is one nobody can undo safely.</summary>
        public string DeactivationReason;

        /// <summary>The generation that was live when it was withdrawn, for the record.</summary>
        public int DeactivatedFromGeneration;

        public bool IsDeactivated => !string.IsNullOrEmpty(DeactivatedUtc);

        public readonly List<ScriptGeneration> Generations = new List<ScriptGeneration>();

        public ScriptGeneration Active =>
            Generations.FirstOrDefault(g => g.Number == ActiveGeneration);

        public ScriptGeneration Latest =>
            Generations.OrderByDescending(g => g.Number).FirstOrDefault();
    }

    public static class ScriptPromotion
    {
        public const string StateProposed = "proposed";
        public const string StateReviewed = "reviewed";
        public const string StateApproved = "approved";

        public const int MaxGenerations = 20;
        public const int MaxScriptBytes = 512 * 1024;

        public static string Root() => Path.Combine(HorizunPaths.DataRoot(), "promotions");

        public static string DirectoryFor(string id) => Path.Combine(Root(), Safe(id));

        public static string ManifestFor(string id) => Path.Combine(DirectoryFor(id), "manifest.json");

        public static string SourceFor(string id, int generation) =>
            Path.Combine(DirectoryFor(id), "g" + generation.ToString(CultureInfo.InvariantCulture) + ".py");

        /// <summary>
        /// An id that is safe as a folder name and stable as an identifier. Anything else
        /// is refused rather than sanitised: a caller whose id silently became a different
        /// id would later look for a promotion that is not where they put it.
        /// </summary>
        public static string ValidateId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "id is required.";
            if (id.Length > 64) return "id must be at most 64 characters.";
            foreach (char c in id)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                    return "id may hold only letters, digits, '-' and '_'; '" + id + "' does not. It is refused " +
                           "rather than cleaned up, because an id that silently became another id is a " +
                           "promotion nobody can find again.";
            return null;
        }

        private static string Safe(string id) => id;   // ValidateId has already refused anything else

        public static string Sha256Of(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text ?? ""));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // =====================================================================
        // Reading and writing
        // =====================================================================

        public static List<string> Ids()
        {
            var ids = new List<string>();
            try
            {
                if (!Directory.Exists(Root())) return ids;
                foreach (string directory in Directory.GetDirectories(Root()))
                    if (File.Exists(Path.Combine(directory, "manifest.json")))
                        ids.Add(Path.GetFileName(directory));
            }
            catch { }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        public static PromotedScript Load(string id)
        {
            try
            {
                string path = ManifestFor(id);
                if (!File.Exists(path)) return null;
                JObject json = JObject.Parse(File.ReadAllText(path));
                var script = new PromotedScript
                {
                    Id = json.Value<string>("id"),
                    Title = json.Value<string>("title"),
                    Description = json.Value<string>("description"),
                    CreatedUtc = json.Value<string>("created_utc"),
                    ActiveGeneration = json.Value<int?>("active_generation") ?? 0,
                    DeactivatedUtc = json.Value<string>("deactivated_utc"),
                    DeactivatedBy = json.Value<string>("deactivated_by"),
                    DeactivationReason = json.Value<string>("deactivation_reason"),
                    DeactivatedFromGeneration = json.Value<int?>("deactivated_from_generation") ?? 0
                };
                foreach (JToken token in json["generations"] as JArray ?? new JArray())
                {
                    var row = token as JObject;
                    if (row == null) continue;
                    script.Generations.Add(new ScriptGeneration
                    {
                        Number = row.Value<int?>("number") ?? 0,
                        Sha256 = row.Value<string>("sha256"),
                        Bytes = row.Value<int?>("bytes") ?? 0,
                        CreatedUtc = row.Value<string>("created_utc"),
                        Author = row.Value<string>("author"),
                        State = row.Value<string>("state") ?? StateProposed,
                        ReviewedBy = row.Value<string>("reviewed_by"),
                        ReviewedUtc = row.Value<string>("reviewed_utc"),
                        ReviewNote = row.Value<string>("review_note"),
                        ApprovedBy = row.Value<string>("approved_by"),
                        ApprovedUtc = row.Value<string>("approved_utc"),
                        Evidence = row.Value<string>("evidence"),
                        Contract = row["contract"] as JObject
                    });
                }
                return script;
            }
            catch { return null; }
        }

        /// <summary>
        /// Write the manifest ATOMICALLY: to a temporary file, then moved over the old
        /// one. A process that dies mid-write leaves the previous manifest intact, which
        /// is what "the previous generation survives a failed activation" actually means
        /// at the filesystem level.
        /// </summary>
        public static void Save(PromotedScript script)
        {
            Directory.CreateDirectory(DirectoryFor(script.Id));
            var json = new JObject
            {
                ["schema"] = "horizun.script-promotion/1",
                ["id"] = script.Id,
                ["title"] = script.Title,
                ["description"] = script.Description,
                ["created_utc"] = script.CreatedUtc,
                ["active_generation"] = script.ActiveGeneration,
                ["deactivated_utc"] = script.DeactivatedUtc == null
                    ? (JToken)JValue.CreateNull() : script.DeactivatedUtc,
                ["deactivated_by"] = script.DeactivatedBy == null
                    ? (JToken)JValue.CreateNull() : script.DeactivatedBy,
                ["deactivation_reason"] = script.DeactivationReason == null
                    ? (JToken)JValue.CreateNull() : script.DeactivationReason,
                ["deactivated_from_generation"] = script.DeactivatedFromGeneration,
                ["generations"] = new JArray(script.Generations
                    .OrderBy(g => g.Number)
                    .Select(g => new JObject
                    {
                        ["number"] = g.Number,
                        ["sha256"] = g.Sha256,
                        ["bytes"] = g.Bytes,
                        ["created_utc"] = g.CreatedUtc,
                        ["author"] = g.Author,
                        ["state"] = g.State,
                        ["reviewed_by"] = g.ReviewedBy == null ? (JToken)JValue.CreateNull() : g.ReviewedBy,
                        ["reviewed_utc"] = g.ReviewedUtc == null ? (JToken)JValue.CreateNull() : g.ReviewedUtc,
                        ["review_note"] = g.ReviewNote == null ? (JToken)JValue.CreateNull() : g.ReviewNote,
                        ["approved_by"] = g.ApprovedBy == null ? (JToken)JValue.CreateNull() : g.ApprovedBy,
                        ["approved_utc"] = g.ApprovedUtc == null ? (JToken)JValue.CreateNull() : g.ApprovedUtc,
                        ["evidence"] = g.Evidence == null ? (JToken)JValue.CreateNull() : g.Evidence,
                        ["contract"] = g.Contract == null ? (JToken)JValue.CreateNull() : g.Contract
                    })),
                ["means"] =
                    "A promoted script is PYTHON, and it runs through horizun_execute_python under the machine " +
                    "owner's Python grant exactly as any other script does. Promotion adds provenance, review " +
                    "and versions. It adds no permission, and nothing here executes anything."
            };

            string target = ManifestFor(script.Id);
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, json.ToString(Newtonsoft.Json.Formatting.Indented),
                              new UTF8Encoding(false));

            // REPLACE, NOT DELETE-THEN-MOVE. The two are not equivalent and the difference
            // is the whole property this method claims: a delete followed by a move has a
            // window in which the manifest does not exist at all, and a process that dies
            // inside it loses the promotion, its review history and its pointer. File.Replace
            // is the single filesystem operation that swaps one file for another, so the
            // reader either sees the old manifest or the new one.
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }

        // =====================================================================
        // The state machine
        // =====================================================================

        /// <summary>
        /// Add a generation. It always arrives as `proposed`: nothing is born approved,
        /// including a correction to something that was.
        /// </summary>
        public static string AddGeneration(PromotedScript script, string source, string author,
                                           string evidence, string nowUtc, out ScriptGeneration added)
        {
            return AddGeneration(script, source, author, evidence, null, nowUtc, out added);
        }

        /// <summary>
        /// Add a generation WITH its declared contract.
        ///
        /// The contract is not optional, and refusing here rather than at invocation time is
        /// deliberate: a generation that reaches `approved` without one is a generation two
        /// people have signed off on and nobody can call.
        /// </summary>
        public static string AddGeneration(PromotedScript script, string source, string author,
                                           string evidence, JObject contract, string nowUtc,
                                           out ScriptGeneration added)
        {
            added = null;
            if (string.IsNullOrWhiteSpace(source)) return "the script source is required.";
            string contractProblem = ScriptInvocation.Validate(ScriptContract.FromJson(contract));
            if (contractProblem != null) return "the contract is refused: " + contractProblem;
            if (source.Length > MaxScriptBytes)
                return "the script is " + source.Length + " characters; the bound is " + MaxScriptBytes + ".";
            if (string.IsNullOrWhiteSpace(author)) return "an author is required. Nothing promotes itself.";
            if (script.Generations.Count >= MaxGenerations)
                return "this promotion already holds " + MaxGenerations + " generations. Retire it and start " +
                       "another rather than losing the history that made the current one trustworthy.";

            string sha = Sha256Of(source);
            ScriptGeneration identical = script.Generations.FirstOrDefault(g => g.Sha256 == sha);
            if (identical != null)
                return "generation " + identical.Number + " already holds exactly these bytes (sha256 " + sha +
                       "). Adding it again would create a second version of one thing and two review histories " +
                       "for it.";

            int number = script.Generations.Count == 0 ? 1 : script.Generations.Max(g => g.Number) + 1;
            added = new ScriptGeneration
            {
                Number = number,
                Sha256 = sha,
                Bytes = new UTF8Encoding(false).GetByteCount(source),
                CreatedUtc = nowUtc,
                Author = author,
                Evidence = evidence,
                Contract = contract,
                State = StateProposed
            };
            script.Generations.Add(added);

            File.WriteAllText(SourceFor(script.Id, number), source, new UTF8Encoding(false));
            return null;
        }

        /// <summary>
        /// Review: a second person says they read it. The author may not review their own
        /// work, which is the entire value of the step.
        /// </summary>
        public static string Review(ScriptGeneration generation, string reviewer, string note, string nowUtc)
        {
            if (generation == null) return "no such generation.";
            if (generation.State != StateProposed)
                return "generation " + generation.Number + " is '" + generation.State + "', not '" +
                       StateProposed + "'.";
            if (string.IsNullOrWhiteSpace(reviewer)) return "a reviewer is required.";
            if (string.Equals(reviewer, generation.Author, StringComparison.OrdinalIgnoreCase))
                return "the author cannot review their own generation. A review by the person who wrote it is " +
                       "the step happening on paper and not in fact.";
            if (string.IsNullOrWhiteSpace(generation.Evidence))
                return "generation " + generation.Number + " records no evidence of having been tested. " +
                       "Promotion after review means after review OF SOMETHING; add the evidence first.";
            generation.State = StateReviewed;
            generation.ReviewedBy = reviewer;
            generation.ReviewedUtc = nowUtc;
            generation.ReviewNote = note;
            return null;
        }

        /// <summary>Approve a reviewed generation. Approval is a separate act from review, by name.</summary>
        public static string Approve(ScriptGeneration generation, string approver, string nowUtc)
        {
            if (generation == null) return "no such generation.";
            if (generation.State != StateReviewed)
                return "generation " + generation.Number + " is '" + generation.State + "'; only a reviewed " +
                       "generation can be approved.";
            if (string.IsNullOrWhiteSpace(approver)) return "an approver is required.";
            generation.State = StateApproved;
            generation.ApprovedBy = approver;
            generation.ApprovedUtc = nowUtc;
            return null;
        }

        /// <summary>
        /// Make an approved generation the active one.
        ///
        /// THE PREVIOUS GENERATION IS NOT DELETED and the pointer moves atomically, so a
        /// failure at any point leaves the old one active. That is the whole of G16's
        /// "conservar anterior al fallar" - not a rollback procedure, but a design in
        /// which there is nothing to roll back.
        /// </summary>
        public static string Activate(PromotedScript script, int generation, out int previous)
        {
            previous = script.ActiveGeneration;
            ScriptGeneration target = script.Generations.FirstOrDefault(g => g.Number == generation);
            if (target == null) return "this promotion has no generation " + generation + ".";
            if (target.State != StateApproved)
                return "generation " + generation + " is '" + target.State + "'. Only an APPROVED generation " +
                       "can be activated; generation " + previous + " stays active.";

            string source = SourceFor(script.Id, generation);
            if (!File.Exists(source))
                return "generation " + generation + "'s source file is missing from disk. Generation " +
                       previous + " stays active - a pointer to bytes that are not there would be the one " +
                       "failure this design exists to make impossible.";

            string onDisk;
            try { onDisk = File.ReadAllText(source); }
            catch (Exception ex)
            {
                return "generation " + generation + "'s source could not be read (" + ex.Message +
                       "). Generation " + previous + " stays active.";
            }
            if (Sha256Of(onDisk) != target.Sha256)
                return "generation " + generation + "'s source no longer hashes to what was reviewed (" +
                       target.Sha256 + "). It was edited after approval. Generation " + previous +
                       " stays active, and the edited generation must be added and reviewed as a new one.";

            script.ActiveGeneration = generation;

            // Activating LIFTS a withdrawal, because that is what re-publishing means, and
            // the source was re-hashed against what was reviewed three lines up. The record
            // of the withdrawal is cleared rather than kept: a script that is live and also
            // carries 'withdrawn on the 3rd' reads as both at once.
            script.DeactivatedUtc = null;
            script.DeactivatedBy = null;
            script.DeactivationReason = null;
            script.DeactivatedFromGeneration = 0;
            return null;
        }

        /// <summary>
        /// WITHDRAW a promoted script: stop serving any generation, keep all of them.
        ///
        /// The answer to "this is doing damage, stop it" was previously to activate some
        /// OTHER generation - which is publishing code in response to deciding that
        /// publishing code was the problem. This stops serving without deleting anything,
        /// so it is undone by activating a generation again.
        ///
        /// A REASON IS REQUIRED. A withdrawal with no reason leaves the next person a
        /// choice between re-publishing something that was pulled for a cause they cannot
        /// see, and leaving a working script withdrawn forever because nobody dares.
        /// </summary>
        public static string Deactivate(PromotedScript script, string by, string reason, string nowUtc,
                                        out int previous)
        {
            previous = script == null ? 0 : script.ActiveGeneration;
            if (script == null) return "no such promotion.";
            if (string.IsNullOrWhiteSpace(by)) return "deactivate requires 'by': who is withdrawing this.";
            if (string.IsNullOrWhiteSpace(reason))
                return "deactivate requires 'reason'. A withdrawal nobody explained cannot be safely undone, " +
                       "and the next person either republishes something that was pulled for a cause they " +
                       "cannot see or leaves it withdrawn forever.";

            if (script.IsDeactivated)
                return "this promotion was already withdrawn on " + script.DeactivatedUtc + " by " +
                       (script.DeactivatedBy ?? "an unrecorded person") + ". Nothing changed. To put it back " +
                       "in service, activate a generation.";

            if (previous == 0)
                return "this promotion has no active generation, so there is nothing to withdraw. Nothing has " +
                       "been approved and activated yet, which is not the same as having been pulled.";

            script.ActiveGeneration = 0;
            script.DeactivatedUtc = nowUtc;
            script.DeactivatedBy = by.Trim();
            script.DeactivationReason = reason.Trim();
            script.DeactivatedFromGeneration = previous;
            return null;
        }

        /// <summary>
        /// The active source, verified against the hash that was reviewed, or null with a
        /// reason. Nothing RUNS it: the caller hands it to horizun_execute_python, which
        /// applies the owner's Python grant exactly as it does to any other script.
        /// </summary>
        public static string ActiveSource(PromotedScript script, out string reason)
        {
            reason = null;
            if (script == null) { reason = "no such promotion."; return null; }
            if (script.IsDeactivated)
            {
                reason = "this promotion was WITHDRAWN on " + script.DeactivatedUtc + " by " +
                         (script.DeactivatedBy ?? "an unrecorded person") + ": " +
                         (script.DeactivationReason ?? "no reason was recorded") + ". Generation " +
                         script.DeactivatedFromGeneration + " was live until then and is still on disk. " +
                         "Nothing is returned. If the reason no longer holds, activate a generation again - " +
                         "which re-hashes it against what was reviewed, exactly as the first activation did.";
                return null;
            }

            ScriptGeneration active = script.Active;
            if (active == null)
            {
                reason = "this promotion has no active generation. Nothing has been approved and activated " +
                         "yet - which is not the same as having been withdrawn, and this one has not been.";
                return null;
            }
            string path = SourceFor(script.Id, active.Number);
            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception ex) { reason = "the active source could not be read: " + ex.Message; return null; }

            if (Sha256Of(source) != active.Sha256)
            {
                reason = "the active source no longer hashes to what was reviewed and approved (" +
                         active.Sha256 + "). It has been edited on disk since. Nothing is returned: handing " +
                         "back code that nobody reviewed, from a registry whose whole purpose is that somebody " +
                         "did, would be worse than having no registry.";
                return null;
            }
            return source;
        }
    }
}
