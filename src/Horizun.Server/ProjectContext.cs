// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_project_context: the ISO 19650 project context and its intake.
//
// A HOST-RESIDENT tool. It answers in this process and never touches Revit. It
// knows ONE thing that is not caller data: the shape of project-context.json,
// schema_version 1 (schemas/project-context.v1.schema.json, embedded in this
// binary so an installed server does not depend on a source checkout). ISO 19650
// is an international standard, not an organisation's, so modelling its concepts
// here keeps the bridge organisation-neutral: every code, pattern, path and name
// inside a project context is the PROJECT's data.
//
// Four operations:
//
//   schema     the JSON Schema, byte for byte what is embedded.
//   validate   a file against it. THREE different verdicts are kept apart, because
//              they call for three different next steps: INVALID (the file breaks
//              the schema - fix it), INCONSISTENT (valid, but it contradicts itself:
//              a deliverable whose name does not follow the declared naming), and
//              INCOMPLETE (valid and coherent, but ISO 19650 questions are still
//              unanswered - ask them). Nothing is inferred to fill a gap.
//   questions  the ORDERED intake questions for what is still missing, each with
//              the JSON pointer its answer lands on, its type, its options and why
//              it matters, in Spanish and English.
//   draft      answers {pointer: value} applied onto the existing file (or onto an
//              empty context), validated, and returned. dry_run defaults to true.
//              With dry_run=false it writes, then READS THE FILE BACK and compares
//              bytes and content, because a write that was not re-read is a claim.
//              It never replaces an existing file without overwrite=true, and it
//              never writes a context that is invalid or carries a credential.
//
// Writing is the only thing here that reaches outside this process, and only when a
// call asks for it: the contract classifies the tool ExternalSideEffectOnRequest, so
// every profile admits the reading and the write itself asks
// Settings.AllowsExternalSideEffect before a byte leaves.
//
// The schema validator below is deliberately SMALL: it implements exactly the
// keywords the embedded schema uses, and a test walks the schema and fails if a
// keyword appears that the validator does not know. That is what makes "valid"
// here mean valid, without taking a JSON Schema dependency into the server.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ProjectContext
    {
        public const string SchemaId = "https://horizunhub.com/schemas/project-context/v1";
        public const string SchemaResourceUri = "horizun://schemas/project-context/v1";
        internal const string SchemaResourceName = "Horizun.Schemas.project-context.v1.schema.json";

        private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

        // ---- the embedded schema ---------------------------------------------------

        private static readonly Lazy<string> _schemaText = new Lazy<string>(LoadSchemaText);
        private static readonly Lazy<JObject> _schema = new Lazy<JObject>(() => JObject.Parse(_schemaText.Value));

        /// <summary>The schema exactly as embedded (UTF-8, no BOM).</summary>
        public static string SchemaText => _schemaText.Value;

        public static JObject Schema => (JObject)_schema.Value.DeepClone();

        private static string LoadSchemaText()
        {
            Assembly asm = typeof(ProjectContext).Assembly;
            using (Stream s = asm.GetManifestResourceStream(SchemaResourceName))
            {
                if (s == null)
                    throw new InvalidOperationException(
                        "The project-context schema is not embedded in " + asm.GetName().Name + " (resource '" +
                        SchemaResourceName + "'). The build is incomplete; nothing can be validated against a " +
                        "schema this process does not carry.");
                using (var r = new StreamReader(s, new UTF8Encoding(false), true))
                    return r.ReadToEnd();
            }
        }

        // ---- the host handler --------------------------------------------------------

        internal static JObject Handle(JObject args) => Handle(args, CancellationToken.None);

        internal static JObject Handle(JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();
            string operation = (string)args["operation"];
            switch (operation)
            {
                case "schema": return SchemaOperation();
                case "validate": return ValidateOperation(args, ct);
                case "questions": return QuestionsOperation(args, ct);
                case "draft": return DraftOperation(args, ct);
                case null:
                    throw new ToolRefusal("operation is required: schema, validate, questions or draft. Nothing was read.");
                default:
                    throw new ToolRefusal("Unknown operation '" + operation + "'. Use schema, validate, questions or draft. " +
                                          "Nothing was read.");
            }
        }

        private static JObject SchemaOperation()
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(SchemaText);
            return new JObject
            {
                ["operation"] = "schema",
                ["schema_id"] = SchemaId,
                ["resource_uri"] = SchemaResourceUri,
                ["sha256"] = Sha256Hex(bytes),
                ["schema"] = Schema
            };
        }

        private static JObject ValidateOperation(JObject args, CancellationToken ct)
        {
            string path = RequirePath(args, "validate");
            if (!File.Exists(path))
                throw new ToolRefusal("No file at '" + path + "'. validate reads an existing project-context.json; to start " +
                                      "one, use operation=questions and then operation=draft.");
            byte[] bytes = File.ReadAllBytes(path);
            ct.ThrowIfCancellationRequested();
            JObject result = new JObject
            {
                ["operation"] = "validate",
                ["path"] = path,
                ["bytes"] = bytes.LongLength,
                ["sha256"] = Sha256Hex(bytes),
                ["schema_id"] = SchemaId
            };

            JToken parsed;
            string parseError;
            if (!TryParse(bytes, out parsed, out parseError))
            {
                JObject invalid = UnparseableReport(parseError);
                foreach (JProperty p in invalid.Properties()) result[p.Name] = p.Value;
                return result;
            }
            foreach (JProperty p in Evaluate(parsed).Properties()) result[p.Name] = p.Value;
            return result;
        }

        private static JObject QuestionsOperation(JObject args, CancellationToken ct)
        {
            string path = OptionalPath(args);
            bool includeAnswered = (bool?)args["include_answered"] ?? false;
            JObject doc = new JObject();
            string source = "empty";
            if (path != null)
            {
                if (File.Exists(path))
                {
                    JToken parsed;
                    string parseError;
                    if (!TryParse(File.ReadAllBytes(path), out parsed, out parseError) || !(parsed is JObject))
                        throw new ToolRefusal("'" + path + "' is not a JSON object (" + (parseError ?? "top level is not an object") +
                                              "). Run operation=validate to see why; the questions are only computed " +
                                              "against a context that can be read.");
                    doc = (JObject)parsed;
                    source = "file";
                }
                else source = "file_not_found";
            }
            ct.ThrowIfCancellationRequested();

            var rows = new JArray();
            int answered = 0, skipped = 0, pending = 0;
            foreach (Question q in Questions)
            {
                string state = StateOf(q, doc);
                if (state == "answered") answered++;
                else if (state == "not_applicable") skipped++;
                else pending++;
                if (state != "pending" && !includeAnswered) continue;
                JObject row = q.ToJson(Questions.IndexOf(q) + 1);
                row["state"] = state;
                JToken current = Resolve(doc, q.Pointer);
                if (current != null && state == "answered") row["current_value"] = current.DeepClone();
                rows.Add(row);
            }
            JToken first = rows.FirstOrDefault(r => (string)r["state"] == "pending");
            return new JObject
            {
                ["operation"] = "questions",
                ["path"] = path,
                ["source"] = source,
                ["total"] = Questions.Count,
                ["answered"] = answered,
                ["not_applicable"] = skipped,
                ["pending"] = pending,
                ["next_question_id"] = first == null ? null : first["id"],
                ["missing_topics"] = new JArray(MissingTopics(doc)),
                ["how_to_answer"] =
                    "Ask the person, one topic at a time, offering the options when a question has them. Never " +
                    "answer a question yourself: an unknown stays out of the file and is listed in intake.missing. " +
                    "Then send the answers to operation=draft as {\"<pointer>\": value}.",
                ["questions"] = rows
            };
        }

        private static JObject DraftOperation(JObject args, CancellationToken ct)
        {
            JToken answersToken = args["answers"];
            if (answersToken == null || answersToken.Type != JTokenType.Object)
                throw new ToolRefusal("draft needs 'answers': an object of {\"<JSON pointer>\": value}, e.g. " +
                                      "{\"/project/code\": \"P01\", \"/appointment/role\": \"appointed_party\"}. Nothing was written.");
            JObject answers = (JObject)answersToken;
            bool dryRun = (bool?)args["dry_run"] ?? true;
            bool overwrite = (bool?)args["overwrite"] ?? false;
            string path = OptionalPath(args);
            if (!dryRun && path == null)
                throw new ToolRefusal("dry_run=false needs 'path': where the project-context.json is written. Nothing was written.");
            if (path != null && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new ToolRefusal("path must name a .json file; '" + path + "' does not. Nothing was written.");

            // THE BASE. An existing file is the starting point, so a second intake round
            // fills gaps instead of wiping the first round's answers.
            JObject doc = new JObject();
            bool exists = path != null && File.Exists(path);
            string baseSha = null;
            if (exists)
            {
                byte[] existing = File.ReadAllBytes(path);
                baseSha = Sha256Hex(existing);
                JToken parsed;
                string parseError;
                if (!TryParse(existing, out parsed, out parseError) || !(parsed is JObject))
                    throw new ToolRefusal("'" + path + "' exists and is not a JSON object (" +
                                          (parseError ?? "top level is not an object") + "). draft builds on the existing " +
                                          "file and will not guess what it was meant to say. Nothing was written.");
                doc = (JObject)parsed;
            }
            ct.ThrowIfCancellationRequested();

            // Every pointer is checked before any is applied: a bad one refuses the call
            // whole, rather than returning a document that silently dropped an answer.
            var rejected = new List<string>();
            foreach (JProperty p in answers.Properties())
            {
                string why = CheckPointer(p.Name);
                if (why != null) rejected.Add("'" + p.Name + "': " + why);
            }
            if (rejected.Count > 0)
                throw new ToolRefusal("These answers name no usable location, so none was applied and nothing was written: " +
                                      string.Join("; ", rejected) + ".");

            var applied = new JArray();
            foreach (JProperty p in answers.Properties())
            {
                string why = SetPointer(doc, p.Name, p.Value.DeepClone());
                if (why != null)
                    throw new ToolRefusal("The answer for '" + p.Name + "' cannot be placed: " + why +
                                          ". Nothing was written.");
                applied.Add(p.Name);
            }
            if (doc["schema_version"] == null) doc.AddFirst(new JProperty("schema_version", 1));

            // intake.missing is DERIVED unless the caller set it: it lists the topics still
            // unanswered, which is exactly the rule "never invent, list what is missing".
            bool callerSetMissing = answers.Properties().Any(p =>
                p.Name == "/intake/missing" || p.Name.StartsWith("/intake/missing/", StringComparison.Ordinal) ||
                p.Name == "/intake");
            if (!callerSetMissing)
            {
                // An intake that is present but not an object is the caller's content, invalid
                // or not; it is reported by the schema check, never replaced here.
                if (doc["intake"] == null) doc["intake"] = new JObject();
                if (doc["intake"] is JObject intake) intake["missing"] = new JArray(MissingTopics(doc));
            }

            JObject evaluation = Evaluate(doc);
            var result = new JObject
            {
                ["operation"] = "draft",
                ["dry_run"] = dryRun,
                ["path"] = path,
                ["base"] = exists ? "existing_file" : "empty",
                ["base_sha256"] = baseSha,
                ["applied"] = applied,
                ["document"] = doc.DeepClone()
            };
            foreach (JProperty p in evaluation.Properties()) result[p.Name] = p.Value;

            if (dryRun)
            {
                result["written"] = false;
                result["would_refuse"] = WriteRefusal(evaluation, path, exists, overwrite);
                result["note"] = "Rehearsal: nothing was written. Send the same answers with dry_run=false to write; " +
                                 "the file is then read back and compared before this reports it written.";
                return result;
            }

            string refusal = WriteRefusal(evaluation, path, exists, overwrite);
            if (refusal != null) throw new ToolRefusal(refusal);

            string profileRefusal;
            if (!Settings.AllowsExternalSideEffect(out profileRefusal))
                throw new ToolRefusal("Writing the project context is a file outside the model, and that is what needs the " +
                                      "profile: " + profileRefusal + " The drafted document is still available: send the " +
                                      "same call with dry_run=true and save its 'document' yourself.");

            byte[] bytes = Serialize(doc);
            WriteAtomically(path, bytes, overwrite);

            // RE-READ. A write is only reported after the bytes on disk are the bytes meant
            // and they parse back to the same document.
            byte[] onDisk = File.ReadAllBytes(path);
            string wantSha = Sha256Hex(bytes), gotSha = Sha256Hex(onDisk);
            JToken reread;
            string rereadError;
            bool parsedBack = TryParse(onDisk, out reread, out rereadError);
            bool same = wantSha == gotSha && parsedBack && JToken.DeepEquals(reread, doc);
            if (!same)
                throw new InvalidOperationException(
                    "'" + path + "' was written but reading it back does not return what was written (sha256 written " +
                    wantSha + ", read " + gotSha + (parsedBack ? "" : ", and it no longer parses: " + rereadError) +
                    "). Treat the file as unverified; nothing is reported written.");

            result["written"] = true;
            result["verification"] = new JObject
            {
                ["reread"] = true,
                ["bytes"] = onDisk.LongLength,
                ["sha256"] = gotSha,
                ["content_equal"] = true
            };
            result.Remove("would_refuse");
            return result;
        }

        private static string WriteRefusal(JObject evaluation, string path, bool exists, bool overwrite)
        {
            if (path == null) return "no path given, so there is nowhere to write; the rehearsal is the whole answer.";
            if (!(bool)evaluation["valid"])
                return "the drafted context breaks the schema (" + ((JArray)evaluation["errors"]).Count +
                       " error(s), listed in 'errors'). An invalid file is not written; fix the answers. Nothing was written.";
            if (((JArray)evaluation["coherence"]).Any(f => (string)f["rule"] == "credential_like"))
                return "the drafted context carries what looks like a credential (see coherence rule credential_like). A " +
                       "project context never holds credentials or tokens. Nothing was written.";
            if (exists && !overwrite)
                return "'" + path + "' already exists and overwrite is false. The draft was built ON TOP of it (base=" +
                       "existing_file); send overwrite=true to replace it. Nothing was written.";
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return "the folder '" + dir + "' does not exist. This tool does not create project folders; create it " +
                       "deliberately and send the call again. Nothing was written.";
            return null;
        }

        private static void WriteAtomically(string path, byte[] bytes, bool overwrite)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temp, bytes);
                // overwrite=false: File.Move refuses if the target appeared meanwhile, so the
                // "never replace without overwrite" rule holds even against a race.
                File.Move(temp, path, overwrite);
            }
            catch (IOException ex) when (!overwrite && File.Exists(path))
            {
                throw new ToolRefusal("'" + path + "' appeared while this call was writing and overwrite is false; it was " +
                                      "left untouched (" + ex.Message + "). Nothing was written.");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        internal static byte[] Serialize(JObject doc)
            => new UTF8Encoding(false).GetBytes(doc.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n");

        // ---- evaluation: invalid / inconsistent / incomplete / complete ---------------

        /// <summary>
        /// Everything validate reports about a parsed document. Pure: no I/O.
        /// </summary>
        internal static JObject Evaluate(JToken doc)
        {
            var errors = new List<JObject>();
            ValidateAgainst(doc, _schema.Value, _schema.Value, "", errors);

            JObject obj = doc as JObject;
            var coherence = obj == null ? new List<JObject>() : Coherence(obj);
            var missing = new JArray();
            if (obj != null)
                foreach (Question q in Questions)
                    if (StateOf(q, obj) == "pending")
                        missing.Add(new JObject
                        {
                            ["id"] = q.Id,
                            ["topic"] = q.Topic,
                            ["pointer"] = q.Pointer,
                            ["why"] = new JObject { ["es"] = q.WhyEs, ["en"] = q.WhyEn }
                        });

            bool valid = errors.Count == 0;
            bool coherent = !coherence.Any(f => (string)f["severity"] == "error");
            bool complete = missing.Count == 0;
            string state = !valid ? "invalid" : !coherent ? "inconsistent" : !complete ? "incomplete" : "complete";

            var declaredMissing = new JArray();
            if (obj?["intake"]?["missing"] is JArray dm) foreach (JToken t in dm) declaredMissing.Add(t.DeepClone());
            var documentsMissing = new JArray();
            if (obj?["documents"] is JObject docs)
                foreach (JProperty p in docs.Properties())
                    if (p.Value is JObject d && (string)d["status"] == "missing") documentsMissing.Add(p.Name);

            return new JObject
            {
                ["state"] = state,
                ["valid"] = valid,
                ["coherent"] = coherent,
                ["complete"] = complete,
                ["errors"] = new JArray(errors),
                ["coherence"] = new JArray(coherence),
                ["missing"] = missing,
                ["declared_missing"] = declaredMissing,
                ["documents_declared_missing"] = documentsMissing,
                ["state_means"] = StateMeaning(state)
            };
        }

        private static string StateMeaning(string state)
        {
            switch (state)
            {
                case "invalid": return "The file breaks the schema; 'errors' says where, by JSON pointer. Fix those first.";
                case "inconsistent": return "The file follows the schema but contradicts itself; 'coherence' lists each contradiction.";
                case "incomplete": return "Valid and coherent, but ISO 19650 intake questions are unanswered; 'missing' lists them in order. Ask them - do not fill them in.";
                default: return "Valid, coherent, and every intake question has an answer.";
            }
        }

        private static JObject UnparseableReport(string parseError) => new JObject
        {
            ["state"] = "invalid",
            ["valid"] = false,
            ["coherent"] = false,
            ["complete"] = false,
            ["errors"] = new JArray(Error("", "json", "The file is not JSON: " + parseError)),
            ["coherence"] = new JArray(),
            ["missing"] = new JArray(),
            ["declared_missing"] = new JArray(),
            ["documents_declared_missing"] = new JArray(),
            ["state_means"] = StateMeaning("invalid")
        };

        // ---- coherence ---------------------------------------------------------------

        private static readonly string[] CdeStates = { "wip", "shared", "published", "archived" };

        private static readonly Regex CredentialLike = new Regex(
            @"(access[_-]?token|refresh[_-]?token|client[_-]?secret|password|passwd|api[_-]?key|bearer\s+[A-Za-z0-9\-._~+/]{8,}|[?&](sig|token|code|sv)=|://[^/\s:@]+:[^/\s@]+@)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        internal static List<JObject> Coherence(JObject doc)
        {
            var findings = new List<JObject>();

            // Credentials, anywhere, in a key or a value.
            foreach (JToken t in doc.DescendantsAndSelf())
            {
                if (t is JProperty prop && CredentialLike.IsMatch(prop.Name))
                    findings.Add(Finding(PointerOf(prop.Value), "credential_like", "error",
                        "A key named '" + prop.Name + "' looks like a credential. A project context never holds credentials or tokens."));
                else if (t is JValue v && v.Type == JTokenType.String && CredentialLike.IsMatch((string)v))
                    findings.Add(Finding(PointerOf(v), "credential_like", "error",
                        "This value looks like it carries a credential or token. Keep only the reference, never the secret."));
            }

            // Naming: which fields, and do the declared codes follow them?
            JObject naming = doc["naming"] as JObject;
            JArray fields = naming?["fields"] as JArray;
            string separator = naming?["separator"] is JValue sv && sv.Type == JTokenType.String ? (string)sv : null;
            var fieldList = new List<KeyValuePair<string, string>>();
            if (fields != null)
                foreach (JToken f in fields)
                    if (f is JObject fo && fo["name"] is JValue fn && fn.Type == JTokenType.String)
                        fieldList.Add(new KeyValuePair<string, string>((string)fn,
                            fo["pattern"] is JValue fp && fp.Type == JTokenType.String ? (string)fp : null));

            string projectCode = doc["project"]?["code"] is JValue pc && pc.Type == JTokenType.String ? (string)pc : null;
            string orgCode = doc["appointment"]?["organisation_code"] is JValue oc && oc.Type == JTokenType.String ? (string)oc : null;
            CheckFieldValue(fieldList, "project", projectCode, "/project/code", findings);
            CheckFieldValue(fieldList, "originator", orgCode, "/appointment/organisation_code", findings);

            var statusCodes = naming?["status_codes"] as JObject;
            var teamCodes = new HashSet<string>(StringComparer.Ordinal);
            bool teamsDeclared = doc["appointment"]?["task_teams"] is JArray teams && teams.Count > 0;
            if (teamsDeclared)
                foreach (JToken tt in (JArray)doc["appointment"]["task_teams"])
                    if (tt?["role_code"] is JValue rc && rc.Type == JTokenType.String) teamCodes.Add((string)rc);

            if (doc["deliverables"] is JArray deliverables)
            {
                bool namingCheckable = fieldList.Count > 0 && !string.IsNullOrEmpty(separator);
                if (!namingCheckable && deliverables.Count > 0)
                    findings.Add(Finding("/naming", "naming_not_checkable", "warning",
                        "Deliverables are declared but naming.fields and naming.separator are not both present, so no container name could be checked."));
                for (int i = 0; i < deliverables.Count; i++)
                {
                    if (!(deliverables[i] is JObject d)) continue;
                    string at = "/deliverables/" + i;
                    string container = d["container"] is JValue cv && cv.Type == JTokenType.String ? (string)cv : null;
                    if (namingCheckable && container != null)
                        CheckContainer(container, separator, fieldList, projectCode, at + "/container", findings);
                    string status = d["required_status"] is JValue st && st.Type == JTokenType.String ? (string)st : null;
                    if (status != null && statusCodes != null && statusCodes.Count > 0 && statusCodes[status] == null)
                        findings.Add(Finding(at + "/required_status", "unknown_status_code", "error",
                            "required_status '" + status + "' is not a key of naming.status_codes."));
                    string team = d["task_team"] is JValue tv && tv.Type == JTokenType.String ? (string)tv : null;
                    if (team != null && teamsDeclared && !teamCodes.Contains(team))
                        findings.Add(Finding(at + "/task_team", "unknown_task_team", "error",
                            "task_team '" + team + "' is not a role_code of appointment.task_teams."));
                }
            }

            // Documents: BEP kind, TIDP teams, status vs path.
            if (doc["documents"] is JObject documents)
            {
                if (documents["bep"] is JObject bep && bep["kind"] == null)
                    findings.Add(Finding("/documents/bep/kind", "bep_kind_absent", "warning",
                        "A BEP is declared without its kind. ISO 19650-2 distinguishes the pre-appointment BEP (tender) from the post-appointment BEP (delivery); they commit to different things."));
                if (documents["tidp"] is JArray tidps && teamsDeclared)
                    for (int i = 0; i < tidps.Count; i++)
                        if (tidps[i]?["task_team"] is JValue tv && tv.Type == JTokenType.String && !teamCodes.Contains((string)tv))
                            findings.Add(Finding("/documents/tidp/" + i + "/task_team", "unknown_task_team", "error",
                                "TIDP task_team '" + (string)tv + "' is not a role_code of appointment.task_teams."));
                foreach (JToken node in documents.Descendants().OfType<JObject>())
                {
                    string status = node["status"] is JValue s && s.Type == JTokenType.String ? (string)s : null;
                    bool hasPath = node["path"] is JValue p && p.Type == JTokenType.String && ((string)p).Length > 0;
                    if ((status == "received" || status == "approved") && !hasPath)
                        findings.Add(Finding(PointerOf(node), "document_without_path", "warning",
                            "The document is declared " + status + " but no path says where it is, so nothing can read it."));
                    if (status == "missing" && hasPath)
                        findings.Add(Finding(PointerOf(node), "missing_document_has_path", "warning",
                            "The document is declared missing but carries a path. One of the two is out of date."));
                }
            }

            // CDE: a state the team uses needs a folder.
            if (doc["cde"] is JObject cde)
            {
                JObject states = cde["states"] as JObject;
                if (states != null && states.Count > 0)
                    foreach (string s in CdeStates)
                        if (states[s] == null)
                            findings.Add(Finding("/cde/states/" + s, "cde_state_without_folder", "warning",
                                "The CDE declares state folders but none for '" + s + "'. Every ISO 19650 container state needs a place, or a transition into it has nowhere to land."));
                string working = cde["working_state"] is JValue wv && wv.Type == JTokenType.String ? (string)wv : null;
                if (working != null && (states == null || states.Count == 0))
                    findings.Add(Finding("/cde/working_state", "working_state_without_folder", "warning",
                        "working_state is '" + working + "' but no folder is declared for that state."));
                if (cde["approvals"] is JObject approvals && (states == null || states.Count == 0) && approvals.Count > 0)
                    findings.Add(Finding("/cde/approvals", "approvals_without_states", "warning",
                        "Approvers are named for CDE transitions but no state folders are declared."));
            }

            // intake.missing that is no longer missing.
            if (doc["intake"]?["missing"] is JArray declared)
            {
                var stillMissing = new HashSet<string>(MissingTopics(doc), StringComparer.Ordinal);
                var knownTopics = new HashSet<string>(Questions.Select(q => q.Topic), StringComparer.Ordinal);
                for (int i = 0; i < declared.Count; i++)
                    if (declared[i] is JValue mv && mv.Type == JTokenType.String && knownTopics.Contains((string)mv) &&
                        !stillMissing.Contains((string)mv))
                        findings.Add(Finding("/intake/missing/" + i, "stale_intake_missing", "warning",
                            "intake.missing lists '" + (string)mv + "' but that topic is answered in the file."));
            }

            return findings;
        }

        private static void CheckFieldValue(List<KeyValuePair<string, string>> fields, string fieldName, string value,
                                            string pointer, List<JObject> findings)
        {
            if (value == null) return;
            foreach (var f in fields)
            {
                if (!string.Equals(f.Key, fieldName, StringComparison.Ordinal) || f.Value == null) continue;
                bool? ok = SafeMatch(value, f.Value);
                if (ok == false)
                    findings.Add(Finding(pointer, "naming_field_mismatch", "error",
                        "'" + value + "' does not match the naming field '" + fieldName + "' pattern " + f.Value + "."));
            }
        }

        private static void CheckContainer(string container, string separator, List<KeyValuePair<string, string>> fields,
                                           string projectCode, string pointer, List<JObject> findings)
        {
            string[] parts = container.Split(new[] { separator }, StringSplitOptions.None);
            if (parts.Length != fields.Count)
            {
                findings.Add(Finding(pointer, "container_field_count", "error",
                    "'" + container + "' has " + parts.Length + " field(s) separated by '" + separator + "'; naming.fields declares " +
                    fields.Count + " (" + string.Join(separator, fields.Select(f => f.Key)) + ")."));
                return;
            }
            for (int i = 0; i < parts.Length; i++)
            {
                string name = fields[i].Key, pattern = fields[i].Value;
                if (pattern != null && SafeMatch(parts[i], pattern) == false)
                    findings.Add(Finding(pointer, "container_field_mismatch", "error",
                        "Field '" + name + "' of '" + container + "' is '" + parts[i] + "', which does not match " + pattern + "."));
                if (name == "project" && projectCode != null && !string.Equals(parts[i], projectCode, StringComparison.Ordinal))
                    findings.Add(Finding(pointer, "container_project_mismatch", "error",
                        "Field 'project' of '" + container + "' is '" + parts[i] + "' but project.code is '" + projectCode + "'."));
            }
        }

        /// <summary>true/false, or null when the pattern cannot be evaluated (reported by the schema check).</summary>
        private static bool? SafeMatch(string value, string pattern)
        {
            try { return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, RegexBudget); }
            catch (ArgumentException) { return null; }
            catch (RegexMatchTimeoutException) { return null; }
        }

        private static JObject Finding(string pointer, string rule, string severity, string message) => new JObject
        {
            ["pointer"] = pointer,
            ["rule"] = rule,
            ["severity"] = severity,
            ["message"] = message
        };

        // ---- the intake questions ----------------------------------------------------

        internal sealed class Option
        {
            public string Value, Es, En;
            public Option(string value, string es, string en) { Value = value; Es = es; En = en; }
        }

        internal sealed class Question
        {
            public string Id, Topic, Section, Pointer, Type;
            public Option[] Options = new Option[0];
            public bool AllowOther;
            public string TextEs, TextEn, WhyEs, WhyEn;
            public string DependsOnPointer;       // skipped when that pointer holds one of DependsOnNotIn / not DependsOnIn
            public string[] SkipWhenValueIn;
            public string[] OnlyWhenValueIn;

            public JObject ToJson(int order)
            {
                var o = new JObject
                {
                    ["order"] = order,
                    ["id"] = Id,
                    ["topic"] = Topic,
                    ["section"] = Section,
                    ["pointer"] = Pointer,
                    ["type"] = Type,
                    ["text"] = new JObject { ["es"] = TextEs, ["en"] = TextEn },
                    ["why"] = new JObject { ["es"] = WhyEs, ["en"] = WhyEn }
                };
                if (Options.Length > 0)
                {
                    o["options"] = new JArray(Options.Select(op => new JObject
                    {
                        ["value"] = Type == "integer" && int.TryParse(op.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                            ? (JToken)n : op.Value,
                        ["label"] = new JObject { ["es"] = op.Es, ["en"] = op.En }
                    }));
                    o["allow_other"] = AllowOther;
                }
                if (DependsOnPointer != null)
                {
                    var dep = new JObject { ["pointer"] = DependsOnPointer };
                    if (SkipWhenValueIn != null) dep["skipped_when_value_in"] = new JArray(SkipWhenValueIn);
                    if (OnlyWhenValueIn != null) dep["asked_only_when_value_in"] = new JArray(OnlyWhenValueIn);
                    o["depends_on"] = dep;
                }
                return o;
            }
        }

        private static Question Q(string id, string topic, string section, string pointer, string type,
                                  string textEs, string textEn, string whyEs, string whyEn,
                                  Option[] options = null, bool allowOther = false)
            => new Question
            {
                Id = id, Topic = topic, Section = section, Pointer = pointer, Type = type,
                TextEs = textEs, TextEn = textEn, WhyEs = whyEs, WhyEn = whyEn,
                Options = options ?? new Option[0], AllowOther = allowOther
            };

        private static Question SkipIf(this Question q, string pointer, params string[] values)
        { q.DependsOnPointer = pointer; q.SkipWhenValueIn = values; return q; }

        private static Question OnlyIf(this Question q, string pointer, params string[] values)
        { q.DependsOnPointer = pointer; q.OnlyWhenValueIn = values; return q; }

        private static readonly Option[] DocumentStatus =
        {
            new Option("missing", "No existe / no lo tenemos", "Does not exist / we do not have it"),
            new Option("draft", "En borrador", "Draft"),
            new Option("received", "Recibido, sin aprobar", "Received, not approved"),
            new Option("approved", "Aprobado", "Approved")
        };

        private static Question DocStatus(string key, string topic, string section, string nameEs, string nameEn,
                                          string whyEs, string whyEn)
            => Q(key + "_status", topic, section, "/documents/" + key + "/status", "enum",
                 "¿Existe el " + nameEs + " del proyecto y en qué estado está?",
                 "Does the project's " + nameEn + " exist, and what is its status?",
                 whyEs, whyEn, DocumentStatus);

        private static Question DocPath(string key, string topic, string section, string nameEs, string nameEn)
            => Q(key + "_path", topic, section, "/documents/" + key + "/path", "path",
                 "¿Dónde está el " + nameEs + "? (ruta o referencia en el CDE)",
                 "Where is the " + nameEn + "? (path or CDE reference)",
                 "Sin la ruta nadie puede leerlo ni verificar contra él.",
                 "Without the path nobody can read it or check against it.")
               .SkipIf("/documents/" + key + "/status", "missing");

        /// <summary>
        /// THE ORDER IS THE INTAKE. Project identity first (the schema requires
        /// project.code), then: role in the appointment, stage, EIR, BEP, MIDP/TIDP,
        /// responsibility matrix, CDE, naming, classification, LOIN/IDS, georeference,
        /// IFC delivery, Revit version. A test pins this order.
        /// </summary>
        internal static readonly List<Question> Questions = new List<Question>
        {
            Q("project_code", "project", "project", "/project/code", "string",
              "¿Cuál es el código del proyecto?", "What is the project code?",
              "Es el primer campo del nombre de cada contenedor de información y lo único obligatorio del archivo.",
              "It is the first field of every information container name and the only mandatory value in the file."),
            Q("project_name", "project", "project", "/project/name", "string",
              "¿Cuál es el nombre del proyecto?", "What is the project name?",
              "Identifica el proyecto para las personas; el código lo identifica para las máquinas.",
              "It identifies the project to people; the code identifies it to machines."),

            Q("appointment_role", "appointment", "appointment", "/appointment/role", "enum",
              "¿Qué papel tiene tu organización en la designación (ISO 19650-2)?",
              "What role does your organisation have in the appointment (ISO 19650-2)?",
              "El papel decide qué documentos produces y cuáles recibes: la parte que designa emite el EIR, la parte designada principal el BEP y el MIDP.",
              "The role decides which documents you produce and which you receive: the appointing party issues the EIR, the lead appointed party the BEP and MIDP.",
              new[]
              {
                  new Option("appointing_party", "Parte que designa (cliente / propietario)", "Appointing party (client / owner)"),
                  new Option("lead_appointed_party", "Parte designada principal (lidera la entrega)", "Lead appointed party (leads delivery)"),
                  new Option("appointed_party", "Parte designada (equipo de tarea)", "Appointed party (task team)")
              }),
            Q("organisation_code", "appointment", "appointment", "/appointment/organisation_code", "string",
              "¿Qué código de organización (originador) usa tu empresa en los nombres de archivo?",
              "Which organisation (originator) code does your company use in file names?",
              "Es el campo 'originator' del nombre del contenedor; sin él no se puede nombrar ni verificar un entregable propio.",
              "It is the container name's 'originator' field; without it no deliverable of yours can be named or checked."),
            Q("task_teams", "appointment", "appointment", "/appointment/task_teams", "array",
              "¿Qué equipos de tarea participan? (código de rol, disciplina, organización, responsable)",
              "Which task teams take part? (role code, discipline, organisation, lead)",
              "Cada TIDP y cada entregable pertenece a un equipo de tarea; el código de rol suele ser un campo del nombre.",
              "Every TIDP and deliverable belongs to a task team; the role code is usually a field of the name."),

            Q("stage", "stage", "stage", "/appointment/stage", "string",
              "¿En qué fase está el proyecto hoy?", "Which stage is the project in today?",
              "La fase define qué nivel de información se exige y qué hitos del MIDP están vigentes.",
              "The stage defines the level of information required and which MIDP milestones apply."),
            Q("stage_scheme", "stage", "stage", "/appointment/stage_scheme", "enum",
              "¿Con qué esquema de fases se describe?", "Which stage scheme describes it?",
              "Sin esquema, 'fase 3' significa cosas distintas para cada equipo.",
              "Without a scheme, 'stage 3' means different things to each team.",
              new[]
              {
                  new Option("riba2020", "RIBA Plan of Work 2020", "RIBA Plan of Work 2020"),
                  new Option("iso22263", "ISO 22263", "ISO 22263"),
                  new Option("custom", "Propio del proyecto", "Project-specific")
              }),

            DocStatus("eir", "eir", "eir", "EIR (requisitos de información de intercambio)", "EIR (exchange information requirements)",
                      "El EIR dice qué información pide la parte que designa; todo lo demás (BEP, MIDP, LOIN) responde a él.",
                      "The EIR states what information the appointing party requires; everything else (BEP, MIDP, LOIN) answers it."),
            DocPath("eir", "eir", "eir", "EIR", "EIR"),

            Q("bep_kind", "bep", "bep", "/documents/bep/kind", "enum",
              "¿El BEP es previo a la designación (oferta) o posterior (entrega)?",
              "Is the BEP pre-appointment (tender) or post-appointment (delivery)?",
              "ISO 19650-2 los distingue: el previo propone, el posterior compromete al equipo de entrega.",
              "ISO 19650-2 distinguishes them: the pre-appointment BEP proposes, the post-appointment one commits the delivery team.",
              new[]
              {
                  new Option("pre_appointment", "Previo a la designación (oferta)", "Pre-appointment (tender)"),
                  new Option("post_appointment", "Posterior a la designación (entrega)", "Post-appointment (delivery)")
              }),
            DocStatus("bep", "bep", "bep", "BEP (plan de ejecución BIM)", "BEP (BIM execution plan)",
                      "El BEP fija cómo se cumplirá el EIR: estándares, métodos, CDE y responsabilidades.",
                      "The BEP fixes how the EIR will be met: standards, methods, CDE and responsibilities."),
            DocPath("bep", "bep", "bep", "BEP", "BEP"),

            DocStatus("midp", "midp", "midp_tidp", "MIDP (plan maestro de entrega de información)", "MIDP (master information delivery plan)",
                      "El MIDP lista qué contenedores se entregan, cuándo y con qué estado; es contra lo que se mide el avance.",
                      "The MIDP lists which containers are delivered, when and at which status; progress is measured against it."),
            DocPath("midp", "midp", "midp_tidp", "MIDP", "MIDP"),
            Q("tidp", "tidp", "midp_tidp", "/documents/tidp", "array",
              "¿Qué TIDP (planes por equipo de tarea) existen? (equipo, ruta, estado)",
              "Which TIDPs (task team plans) exist? (team, path, status)",
              "El MIDP se arma a partir de los TIDP; un equipo sin TIDP no tiene compromisos de entrega verificables.",
              "The MIDP is assembled from the TIDPs; a team without one has no verifiable delivery commitments."),

            DocStatus("responsibility_matrix", "responsibility_matrix", "responsibility_matrix",
                      "matriz de responsabilidades", "responsibility matrix",
                      "Dice quién produce, revisa y aprueba cada información; sin ella las aprobaciones del CDE no tienen dueño.",
                      "It says who produces, reviews and approves each piece of information; without it CDE approvals have no owner."),
            DocPath("responsibility_matrix", "responsibility_matrix", "responsibility_matrix",
                    "matriz de responsabilidades", "responsibility matrix"),

            Q("cde_platform", "cde", "cde", "/cde/platform", "enum",
              "¿En qué plataforma está el entorno común de datos (CDE)?",
              "Which platform hosts the common data environment (CDE)?",
              "Determina cómo se leen y publican los contenedores y qué se puede verificar localmente.",
              "It determines how containers are read and published and what can be verified locally.",
              new[]
              {
                  new Option("acc", "Autodesk Construction Cloud (Docs)", "Autodesk Construction Cloud (Docs)"),
                  new Option("bim360", "BIM 360", "BIM 360"),
                  new Option("sharepoint", "SharePoint / OneDrive", "SharePoint / OneDrive"),
                  new Option("trimble_connect", "Trimble Connect", "Trimble Connect"),
                  new Option("projectwise", "ProjectWise", "ProjectWise"),
                  new Option("local", "Servidor o carpeta local", "Local server or folder"),
                  new Option("other", "Otra", "Other")
              }),
            Q("cde_project_ref", "cde", "cde", "/cde/project_ref", "string",
              "¿Cuál es la referencia del proyecto en el CDE (id o URL, sin credenciales)?",
              "What is the project's reference in the CDE (id or URL, no credentials)?",
              "Permite volver al mismo proyecto sin adivinar entre proyectos con nombres parecidos.",
              "It lets anyone return to the same project without guessing between similarly named ones.")
              .SkipIf("/cde/platform", "local"),
            Q("cde_root", "cde", "cde", "/cde/root", "path",
              "¿Hay una carpeta sincronizada localmente (Desktop Connector, OneDrive...)? ¿Cuál es su ruta?",
              "Is there a locally synchronised folder (Desktop Connector, OneDrive...)? What is its path?",
              "Las carpetas de estado se resuelven respecto a esta raíz; sin ella solo se puede trabajar en la nube.",
              "State folders resolve against this root; without it work can only happen in the cloud."),
            Q("cde_state_wip", "cde_states", "cde", "/cde/states/wip", "path",
              "¿Qué carpeta corresponde al estado Trabajo en curso (WIP)?", "Which folder is the Work in progress (WIP) state?",
              "Cada estado ISO 19650 necesita su lugar; una transición sin carpeta destino no puede ocurrir.",
              "Every ISO 19650 state needs its place; a transition with no destination folder cannot happen."),
            Q("cde_state_shared", "cde_states", "cde", "/cde/states/shared", "path",
              "¿Qué carpeta corresponde al estado Compartido?", "Which folder is the Shared state?",
              "Lo Compartido es lo que otros equipos pueden usar para coordinar.",
              "Shared is what other teams may rely on for coordination."),
            Q("cde_state_published", "cde_states", "cde", "/cde/states/published", "path",
              "¿Qué carpeta corresponde al estado Publicado?", "Which folder is the Published state?",
              "Lo Publicado es lo autorizado para su uso (construcción, contrato).",
              "Published is what is authorised for use (construction, contract)."),
            Q("cde_state_archived", "cde_states", "cde", "/cde/states/archived", "path",
              "¿Qué carpeta corresponde al estado Archivado?", "Which folder is the Archived state?",
              "El Archivo conserva el historial y la trazabilidad de lo que se publicó.",
              "The Archive keeps the history and traceability of what was published."),
            Q("cde_working_state", "cde", "cde", "/cde/working_state", "enum",
              "¿En qué estado se trabaja HOY en el día a día?", "Which state does the team work in TODAY?",
              "Si hoy se trabaja directamente en Compartido, eso es un hallazgo: nada pasa por revisión antes de que otros lo usen.",
              "If work happens directly in Shared today, that is a finding: nothing is reviewed before others use it.",
              new[]
              {
                  new Option("wip", "Trabajo en curso (WIP)", "Work in progress (WIP)"),
                  new Option("shared", "Compartido", "Shared"),
                  new Option("published", "Publicado", "Published"),
                  new Option("archived", "Archivado", "Archived")
              }),
            Q("cde_approval_wip_to_shared", "cde_approvals", "cde", "/cde/approvals/wip_to_shared", "string",
              "¿Quién autoriza pasar de WIP a Compartido?", "Who authorises WIP to Shared?",
              "ISO 19650 exige una revisión y aprobación en cada transición; alguien concreto debe firmarla.",
              "ISO 19650 requires a review and approval at every transition; a named person must sign it."),
            Q("cde_approval_shared_to_published", "cde_approvals", "cde", "/cde/approvals/shared_to_published", "string",
              "¿Quién autoriza pasar de Compartido a Publicado?", "Who authorises Shared to Published?",
              "Publicar es autorizar el uso; normalmente lo aprueba la parte designada principal o la que designa.",
              "Publishing authorises use; it is normally approved by the lead appointed party or the appointing party."),
            Q("cde_approval_published_to_archived", "cde_approvals", "cde", "/cde/approvals/published_to_archived", "string",
              "¿Quién autoriza archivar lo Publicado?", "Who authorises archiving Published information?",
              "Archivar cierra una versión; sin responsable, versiones viejas siguen pareciendo vigentes.",
              "Archiving closes a version; without an owner, old versions keep looking current."),

            Q("naming_scheme", "naming", "naming", "/naming/scheme", "enum",
              "¿Qué esquema de nomenclatura de contenedores se usa?", "Which container naming scheme is used?",
              "Sin nomenclatura acordada no se puede verificar ni un solo nombre de entregable.",
              "Without an agreed naming scheme not a single deliverable name can be checked.",
              new[]
              {
                  new Option("iso19650-2", "ISO 19650-2 (campos genéricos)", "ISO 19650-2 (generic fields)"),
                  new Option("iso19650-2-uk-annex", "ISO 19650-2 con anexo nacional del Reino Unido", "ISO 19650-2 with the UK National Annex"),
                  new Option("custom", "Propio del proyecto", "Project-specific")
              }),
            Q("naming_separator", "naming", "naming", "/naming/separator", "string",
              "¿Qué separador se usa entre campos del nombre?", "Which separator is used between name fields?",
              "Es lo que permite partir un nombre en sus campos para verificarlo.",
              "It is what lets a name be split into its fields to be checked.",
              new[] { new Option("-", "Guion (-)", "Hyphen (-)"), new Option("_", "Guion bajo (_)", "Underscore (_)") },
              allowOther: true),
            Q("naming_fields", "naming", "naming", "/naming/fields", "array",
              "¿Qué campos lleva el nombre, en orden, y qué patrón cumple cada uno?",
              "Which fields make up the name, in order, and which pattern does each follow?",
              "Cada campo con su patrón convierte la nomenclatura en una regla verificable, no en una costumbre.",
              "A pattern per field turns naming into a checkable rule instead of a habit."),
            Q("naming_status_codes", "naming", "naming", "/naming/status_codes", "object",
              "¿Qué códigos de estado de idoneidad se usan (S0, S1... A1...) y qué significa cada uno?",
              "Which suitability status codes are used (S0, S1... A1...) and what does each mean?",
              "El estado dice para qué se puede usar un contenedor; un código no declarado no se puede verificar.",
              "The status says what a container may be used for; an undeclared code cannot be checked."),
            Q("naming_revision", "naming", "naming", "/naming/revision", "object",
              "¿Cómo se escriben las revisiones preliminares y contractuales (p. ej. P01, C01)?",
              "How are preliminary and contractual revisions written (e.g. P01, C01)?",
              "Separar preliminares de contractuales evita entregar como contractual algo que aún no lo es.",
              "Separating preliminary from contractual revisions stops a draft from being delivered as contractual."),

            Q("classification_system", "classification", "classification", "/classification/system", "enum",
              "¿Qué sistema de clasificación usa el proyecto?", "Which classification system does the project use?",
              "La clasificación une el modelo con cantidades, presupuesto y especificaciones.",
              "Classification links the model to quantities, cost and specifications.",
              new[]
              {
                  new Option("uniclass2015", "Uniclass 2015", "Uniclass 2015"),
                  new Option("omniclass", "OmniClass", "OmniClass"),
                  new Option("custom", "Catálogo propio", "Own catalogue")
              }),
            Q("classification_name", "classification", "classification", "/classification/name", "string",
              "¿Cómo se llama el sistema de clasificación propio?", "What is the own classification system called?",
              "Un catálogo propio sin nombre no se puede citar ni versionar.",
              "An unnamed own catalogue cannot be cited or versioned.")
              .OnlyIf("/classification/system", "custom"),
            Q("classification_catalog_path", "classification", "classification", "/classification/catalog_path", "path",
              "¿Dónde está el catálogo de códigos?", "Where is the code catalogue?",
              "Sin el catálogo no se puede verificar que un código exista ni que sea de último nivel.",
              "Without the catalogue nobody can check that a code exists or is a last-level code."),
            Q("classification_type_parameter", "classification", "classification", "/classification/type_parameter", "string",
              "¿En qué parámetro del tipo vive el código (p. ej. Keynote)?", "Which type parameter holds the code (e.g. Keynote)?",
              "Es donde las herramientas leen y escriben el código; adivinarlo lleva a escribir en el lugar equivocado.",
              "It is where tools read and write the code; guessing it writes to the wrong place."),

            DocStatus("loin", "loin", "loin_ids", "LOIN (nivel de necesidad de información)", "LOIN (level of information need)",
                      "El LOIN dice cuánta geometría, datos y documentación hace falta por entregable y fase.",
                      "The LOIN says how much geometry, data and documentation each deliverable needs at each stage."),
            DocPath("loin", "loin", "loin_ids", "LOIN", "LOIN"),
            Q("ids", "ids", "loin_ids", "/documents/ids", "array",
              "¿Hay archivos IDS (especificación de entrega de información) y a qué aplican?",
              "Are there IDS (information delivery specification) files, and what do they apply to?",
              "Un IDS convierte los requisitos en algo que una máquina puede comprobar sobre el IFC entregado.",
              "An IDS turns requirements into something a machine can check against the delivered IFC."),

            Q("georeference_crs", "georeference", "georeference", "/georeference/crs", "string",
              "¿Qué sistema de referencia de coordenadas (CRS) usa el proyecto? (p. ej. EPSG:xxxx)",
              "Which coordinate reference system (CRS) does the project use? (e.g. EPSG:xxxx)",
              "Sin CRS los modelos de distintas disciplinas y el terreno no coinciden en el espacio.",
              "Without a CRS, discipline models and the site do not line up in space."),
            Q("georeference_survey_point", "georeference", "georeference", "/georeference/survey_point", "object",
              "¿Cuáles son las coordenadas del punto de levantamiento (N, E, Z)?",
              "What are the survey point coordinates (N, E, Z)?",
              "Es el ancla que liga el modelo al CRS; debe ser la misma en todos los modelos del proyecto.",
              "It is the anchor tying the model to the CRS; it must be the same in every model of the project."),

            Q("ifc_version", "ifc", "ifc", "/delivery/ifc/version", "string",
              "¿En qué versión de IFC se entrega?", "Which IFC version is delivered?",
              "La versión fija qué entidades y propiedades existen en el archivo entregado.",
              "The version fixes which entities and properties exist in the delivered file.",
              new[]
              {
                  new Option("IFC2x3", "IFC2x3", "IFC2x3"),
                  new Option("IFC4", "IFC4", "IFC4"),
                  new Option("IFC4x3", "IFC4x3", "IFC4x3")
              }, allowOther: true),
            Q("ifc_mvd", "ifc", "ifc", "/delivery/ifc/mvd", "string",
              "¿Qué vista de modelo (MVD) se exige?", "Which model view definition (MVD) is required?",
              "La MVD decide si el IFC es para coordinar (referencia) o para transferir diseño editable.",
              "The MVD decides whether the IFC is for coordination (reference) or for editable design transfer.",
              new[]
              {
                  new Option("RV", "Reference View (coordinación)", "Reference View (coordination)"),
                  new Option("DTV", "Design Transfer View", "Design Transfer View"),
                  new Option("CV2", "Coordination View 2.0 (IFC2x3)", "Coordination View 2.0 (IFC2x3)")
              }, allowOther: true),
            Q("ifc_pset_mapping_path", "ifc", "ifc", "/delivery/ifc/pset_mapping_path", "path",
              "¿Dónde está el archivo de mapeo de parámetros a Psets?", "Where is the parameter-to-Pset mapping file?",
              "Sin mapeo, los datos del modelo no llegan a las propiedades que el EIR o el IDS piden.",
              "Without a mapping, model data does not reach the properties the EIR or IDS ask for."),
            Q("ifc_site_placement", "ifc", "ifc", "/delivery/ifc/site_placement", "enum",
              "¿Con qué coordenadas se ubica el IFC exportado?", "Which coordinates place the exported IFC?",
              "Coordenadas internas y compartidas producen archivos que no se superponen entre sí.",
              "Internal and shared coordinates produce files that do not overlay each other.",
              new[]
              {
                  new Option("shared", "Coordenadas compartidas", "Shared coordinates"),
                  new Option("internal", "Origen interno", "Internal origin"),
                  new Option("survey", "Punto de levantamiento", "Survey point")
              }),

            Q("revit_year", "software", "software", "/software/revit_year", "integer",
              "¿Con qué versión de Revit se trabaja el proyecto?", "Which Revit version is the project authored in?",
              "Un modelo guardado en una versión no abre en una anterior; todo el equipo debe usar la misma.",
              "A model saved in one version does not open in an earlier one; the whole team must use the same.",
              new[]
              {
                  new Option("2023", "Revit 2023", "Revit 2023"), new Option("2024", "Revit 2024", "Revit 2024"),
                  new Option("2025", "Revit 2025", "Revit 2025"), new Option("2026", "Revit 2026", "Revit 2026"),
                  new Option("2027", "Revit 2027", "Revit 2027")
              }, allowOther: true)
        };

        /// <summary>answered | pending | not_applicable.</summary>
        internal static string StateOf(Question q, JObject doc)
        {
            if (q.DependsOnPointer != null)
            {
                JToken dep = Resolve(doc, q.DependsOnPointer);
                string depValue = dep is JValue dv && dv.Type == JTokenType.String ? (string)dv : null;
                if (q.SkipWhenValueIn != null && depValue != null && q.SkipWhenValueIn.Contains(depValue))
                    return "not_applicable";
                if (q.OnlyWhenValueIn != null && (depValue == null || !q.OnlyWhenValueIn.Contains(depValue)))
                    return "not_applicable";
            }
            return IsAnswered(Resolve(doc, q.Pointer)) ? "answered" : "pending";
        }

        private static bool IsAnswered(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return false;
            if (t.Type == JTokenType.String) return !string.IsNullOrWhiteSpace((string)t);
            if (t is JContainer c) return c.HasValues;
            return true;
        }

        /// <summary>Distinct topics of pending questions, in question order.</summary>
        internal static List<string> MissingTopics(JObject doc)
        {
            var topics = new List<string>();
            foreach (Question q in Questions)
                if (StateOf(q, doc) == "pending" && !topics.Contains(q.Topic)) topics.Add(q.Topic);
            return topics;
        }

        // ---- JSON pointers (RFC 6901) ---------------------------------------------------

        private static List<string> Segments(string pointer)
            => pointer.Substring(1).Split('/').Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToList();

        internal static JToken Resolve(JToken root, string pointer)
        {
            if (pointer == "") return root;
            JToken cur = root;
            foreach (string seg in Segments(pointer))
            {
                if (cur is JObject o) cur = o[seg];
                else if (cur is JArray a && int.TryParse(seg, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < a.Count) cur = a[i];
                else return null;
                if (cur == null) return null;
            }
            return cur;
        }

        private static string CheckPointer(string pointer)
        {
            if (string.IsNullOrEmpty(pointer)) return "the empty pointer would replace the whole document";
            if (pointer[0] != '/') return "a JSON pointer starts with '/'";
            foreach (string seg in Segments(pointer))
                if (seg.Length == 0) return "it has an empty segment";
            return null;
        }

        /// <summary>Set a value at a pointer, creating objects (or arrays for numeric / '-' segments) on the way.</summary>
        internal static string SetPointer(JObject root, string pointer, JToken value)
        {
            List<string> segs = Segments(pointer);
            JToken cur = root;
            for (int k = 0; k < segs.Count; k++)
            {
                string seg = segs[k];
                bool last = k == segs.Count - 1;
                bool nextIsIndex = !last && (segs[k + 1] == "-" || IsIndex(segs[k + 1]));
                if (cur is JObject o)
                {
                    if (last) { o[seg] = value; return null; }
                    JToken next = o[seg];
                    if (next == null || next.Type == JTokenType.Null)
                    {
                        next = nextIsIndex ? (JToken)new JArray() : new JObject();
                        o[seg] = next;
                    }
                    cur = next;
                }
                else if (cur is JArray a)
                {
                    int index;
                    if (seg == "-") index = a.Count;
                    else if (!IsIndex(seg)) return "'" + seg + "' is not an array index";
                    else index = int.Parse(seg, CultureInfo.InvariantCulture);
                    if (index > a.Count) return "index " + index + " is past the end of an array of " + a.Count;
                    if (last)
                    {
                        if (index == a.Count) a.Add(value); else a[index] = value;
                        return null;
                    }
                    if (index == a.Count) a.Add(nextIsIndex ? (JToken)new JArray() : new JObject());
                    cur = a[index];
                }
                else return "the path runs through a value that is neither an object nor an array";
            }
            return null;
        }

        private static bool IsIndex(string s)
            => s.Length > 0 && s.All(char.IsDigit) && (s == "0" || s[0] != '0') && s.Length < 9;

        private static string PointerOf(JToken t)
        {
            var parts = new List<string>();
            JToken cur = t;
            while (cur != null && cur.Parent != null)
            {
                JContainer parent = cur.Parent;
                if (parent is JProperty prop)
                {
                    parts.Add(prop.Name.Replace("~", "~0").Replace("/", "~1"));
                    cur = prop.Parent;
                    continue;
                }
                if (parent is JArray arr) parts.Add(arr.IndexOf(cur).ToString(CultureInfo.InvariantCulture));
                cur = parent;
            }
            parts.Reverse();
            return parts.Count == 0 ? "" : "/" + string.Join("/", parts);
        }

        // ---- the schema subset validator ---------------------------------------------

        /// <summary>
        /// Every keyword ValidateAgainst understands. The embedded schema must use no
        /// other (a test walks it); annotation-only keywords are listed so they are
        /// known rather than ignored by accident.
        /// </summary>
        internal static readonly HashSet<string> SupportedKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            // annotations
            "$schema", "$id", "title", "description",
            // structure
            "$defs", "$ref", "type", "const", "enum", "properties", "required", "additionalProperties", "items",
            // values
            "minLength", "pattern", "format", "minimum", "maximum"
        };

        internal static void ValidateAgainst(JToken value, JObject schema, JObject root, string pointer, List<JObject> errors)
        {
            if (schema["$ref"] is JValue refValue)
            {
                JObject target = ResolveRef((string)refValue, root);
                if (target == null) { errors.Add(Error(pointer, "$ref", "The schema reference " + (string)refValue + " does not resolve.")); return; }
                ValidateAgainst(value, target, root, pointer, errors);
                return;
            }

            if (schema["const"] != null && !JToken.DeepEquals(schema["const"], value))
            {
                errors.Add(Error(pointer, "const", "Must be " + schema["const"].ToString(Formatting.None) + "."));
                return;
            }
            if (schema["enum"] is JArray allowed && !allowed.Any(a => JToken.DeepEquals(a, value)))
            {
                errors.Add(Error(pointer, "enum", "Must be one of " + string.Join(", ", allowed.Select(a => a.ToString(Formatting.None))) +
                                                  "; got " + value.ToString(Formatting.None) + "."));
                return;
            }
            if (schema["type"] is JValue typeValue && !HasType(value, (string)typeValue))
            {
                errors.Add(Error(pointer, "type", "Must be " + (string)typeValue + "; got " + Describe(value) + "."));
                return;
            }

            switch (value.Type)
            {
                case JTokenType.String:
                    string s = (string)value;
                    if (schema["minLength"] != null && s.Length < (int)schema["minLength"])
                        errors.Add(Error(pointer, "minLength", "Must have at least " + (int)schema["minLength"] + " character(s)."));
                    if (schema["pattern"] is JValue pattern && SafeMatch(s, (string)pattern) != true)
                        errors.Add(Error(pointer, "pattern", "'" + s + "' does not match " + (string)pattern + "."));
                    string format = (string)schema["format"];
                    if (format == "date" && !DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        errors.Add(Error(pointer, "format", "'" + s + "' is not an ISO 8601 date (YYYY-MM-DD)."));
                    if (format == "regex")
                    {
                        try { new Regex(s, RegexOptions.None, RegexBudget); }
                        catch (ArgumentException ex) { errors.Add(Error(pointer, "format", "'" + s + "' is not a valid regular expression: " + ex.Message)); }
                    }
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    double d = (double)value;
                    if (schema["minimum"] != null && d < (double)schema["minimum"])
                        errors.Add(Error(pointer, "minimum", "Must be at least " + schema["minimum"] + "."));
                    if (schema["maximum"] != null && d > (double)schema["maximum"])
                        errors.Add(Error(pointer, "maximum", "Must be at most " + schema["maximum"] + "."));
                    break;
                case JTokenType.Object:
                    var obj = (JObject)value;
                    JObject props = schema["properties"] as JObject;
                    if (schema["required"] is JArray required)
                        foreach (JToken r in required)
                            if (obj[(string)r] == null)
                                errors.Add(Error(pointer + "/" + Escape((string)r), "required", "'" + (string)r + "' is required."));
                    foreach (JProperty p in obj.Properties())
                    {
                        string childPointer = pointer + "/" + Escape(p.Name);
                        if (props?[p.Name] is JObject childSchema)
                            ValidateAgainst(p.Value, childSchema, root, childPointer, errors);
                        else if (schema["additionalProperties"] is JValue ap && ap.Type == JTokenType.Boolean && !(bool)ap)
                            errors.Add(Error(childPointer, "additionalProperties", "'" + p.Name + "' is not a property this schema defines."));
                        else if (schema["additionalProperties"] is JObject apSchema)
                            ValidateAgainst(p.Value, apSchema, root, childPointer, errors);
                    }
                    break;
                case JTokenType.Array:
                    if (schema["items"] is JObject itemSchema)
                    {
                        var arr = (JArray)value;
                        for (int i = 0; i < arr.Count; i++)
                            ValidateAgainst(arr[i], itemSchema, root, pointer + "/" + i, errors);
                    }
                    break;
            }
        }

        private static JObject ResolveRef(string reference, JObject root)
            => reference != null && reference.StartsWith("#", StringComparison.Ordinal)
                ? Resolve(root, reference.Substring(1)) as JObject
                : null;

        private static bool HasType(JToken v, string type)
        {
            switch (type)
            {
                case "object": return v.Type == JTokenType.Object;
                case "array": return v.Type == JTokenType.Array;
                case "string": return v.Type == JTokenType.String;
                case "boolean": return v.Type == JTokenType.Boolean;
                case "null": return v.Type == JTokenType.Null;
                case "number": return v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
                case "integer":
                    return v.Type == JTokenType.Integer ||
                           (v.Type == JTokenType.Float && Math.Floor((double)v) == (double)v && !double.IsInfinity((double)v));
                default: return false;
            }
        }

        private static string Describe(JToken v)
        {
            switch (v.Type)
            {
                case JTokenType.Integer: case JTokenType.Float: return "number " + v.ToString(Formatting.None);
                case JTokenType.String: return "string " + v.ToString(Formatting.None);
                default: return v.Type.ToString().ToLowerInvariant();
            }
        }

        private static string Escape(string name) => name.Replace("~", "~0").Replace("/", "~1");

        private static JObject Error(string pointer, string keyword, string message) => new JObject
        {
            ["pointer"] = pointer,
            ["keyword"] = keyword,
            ["message"] = message
        };

        // ---- small helpers -------------------------------------------------------------

        private static bool TryParse(byte[] bytes, out JToken parsed, out string error)
        {
            parsed = null;
            error = null;
            try
            {
                string text = new UTF8Encoding(false, true).GetString(bytes);
                if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    parsed = JToken.ReadFrom(reader);
                    if (reader.Read() && reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException("additional content after the JSON value.");
                }
                return true;
            }
            catch (DecoderFallbackException) { error = "the bytes are not valid UTF-8."; return false; }
            catch (JsonException ex) { error = ex.Message; return false; }
        }

        private static string RequirePath(JObject args, string operation)
        {
            string path = OptionalPath(args);
            if (path == null)
                throw new ToolRefusal(operation + " needs 'path': the absolute path of a project-context.json. Nothing was read.");
            return path;
        }

        private static string OptionalPath(JObject args)
        {
            JToken t = args["path"];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))
                throw new ToolRefusal("path must be a non-empty string. Nothing was read.");
            string path = (string)t;
            if (!Path.IsPathRooted(path))
                throw new ToolRefusal("path must be absolute; '" + path + "' is relative, and this server's working folder " +
                                      "is not the project's. Nothing was read.");
            return Path.GetFullPath(path);
        }

        internal static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
