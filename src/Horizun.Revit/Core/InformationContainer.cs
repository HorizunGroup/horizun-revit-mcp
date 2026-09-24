// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// INFORMATION CONTAINERS (ISO 19650). Revit-free on purpose: it is linked into the
// MCP server (horizun_information_container), compiled into the add-in
// (horizun_export with information_container) and tested without a building open.
// It must compile for net48 as well as net8/net10, so it uses nothing newer than
// .NET Framework 4.8 offers.
//
// ISO 19650 is an international standard, not any organisation's standard, so its
// CONCEPTS live here: a container name composed from ordered fields, a suitability
// status, a revision, and the CDE states. What stays an ARGUMENT is every concrete
// rule a project chooses - which fields, in what order, which patterns, which status
// codes. The defaults below are the ISO 19650-2 shape and are reported as defaults
// whenever they are used, never passed off as the caller's rules.
//
// THE SIDECAR. "<file>.container.json" beside the file records the name, fields,
// status, revision and the SHA-256 of the bytes it describes. It is written through
// a temporary file and a no-overwrite move, then READ BACK and compared with what was
// intended, and the file is re-hashed afterwards: a sidecar is testimony about a
// file, and testimony nobody re-read is not evidence. An existing sidecar is never
// overwritten.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A container argument that cannot be used as given. Nothing was written.</summary>
    public sealed class ContainerRuleException : Exception
    {
        public ContainerRuleException(string message) : base(message) { }
    }

    /// <summary>The naming RULES, without any particular container's values.</summary>
    public sealed class ContainerNaming
    {
        public List<string> FieldOrder = new List<string>();
        public string Separator = "-";
        public Dictionary<string, string> FieldPatterns = new Dictionary<string, string>(StringComparer.Ordinal);
        public string FieldPatternsSource = InformationContainer.SourceDefault;
        public List<KeyValuePair<string, string>> StatusCodes = new List<KeyValuePair<string, string>>();
        public string StatusCodesSource = InformationContainer.SourceDefault;
        public List<KeyValuePair<string, string>> RevisionPatterns = new List<KeyValuePair<string, string>>();
        public string RevisionPatternsSource = InformationContainer.SourceDefault;

        /// <summary>"name" (default) or "name_status_revision".</summary>
        public string FileNameMode = InformationContainer.FileNameName;

        /// <summary>True when field_order was not given and the ISO order was assumed.</summary>
        public bool FieldOrderDefaulted;

        public JObject Describe()
        {
            var patterns = new JObject();
            foreach (string f in FieldOrder)
            {
                string p;
                patterns[f] = FieldPatterns.TryGetValue(f, out p) ? (JToken)p : JValue.CreateNull();
            }
            var codes = new JObject();
            foreach (var kv in StatusCodes) codes[kv.Key] = kv.Value;
            var revs = new JObject();
            foreach (var kv in RevisionPatterns) revs[kv.Key] = kv.Value;
            return new JObject
            {
                ["field_order"] = new JArray(FieldOrder.Cast<object>().ToArray()),
                ["field_order_source"] = FieldOrderDefaulted ? InformationContainer.SourceDefault : InformationContainer.SourceArgument,
                ["separator"] = Separator,
                ["file_name"] = FileNameMode,
                ["field_patterns"] = patterns,
                ["field_patterns_source"] = FieldPatternsSource,
                ["status_codes"] = codes,
                ["status_codes_source"] = StatusCodesSource,
                ["revision_patterns"] = revs,
                ["revision_patterns_source"] = RevisionPatternsSource
            };
        }
    }

    /// <summary>One container: the rules plus this container's values.</summary>
    public sealed class ContainerSpec
    {
        public ContainerNaming Naming = new ContainerNaming();
        public Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
        public string Status;
        public string Revision;
        public string Title;
    }

    public sealed class ContainerValidation
    {
        public bool Valid;
        public string Name;
        public string FileStem;
        public string RevisionKind;
        public string StatusDescription;
        public string StatusState;
        public JArray Problems = new JArray();
        public JArray Warnings = new JArray();

        public JObject ToJson()
        {
            return new JObject
            {
                ["valid"] = Valid,
                ["name"] = Name,
                ["file_stem"] = FileStem,
                ["revision_kind"] = RevisionKind,
                ["status_description"] = StatusDescription,
                ["status_state"] = StatusState,
                ["problems"] = Problems,
                ["warnings"] = Warnings
            };
        }
    }

    public static class InformationContainer
    {
        public const string SidecarSchema = "horizun.container/v1";
        public const string SidecarSuffix = ".container.json";
        public const string SourceDefault = "default_iso19650_2";
        public const string SourceArgument = "argument";
        public const string SourceMerged = "argument_over_default_iso19650_2";
        public const string FileNameName = "name";
        public const string FileNameWithStatusRevision = "name_status_revision";

        public const string StateWip = "wip";
        public const string StateShared = "shared";
        public const string StatePublished = "published";
        public const string StateArchived = "archived";
        public static readonly string[] States = { StateWip, StateShared, StatePublished, StateArchived };

        /// <summary>The ISO 19650-2 field order used when none is given.</summary>
        public static readonly string[] DefaultFieldOrder =
            { "project", "originator", "volume", "level", "type", "role", "number" };

        private static readonly string[][] DefaultFieldPatterns =
        {
            new[] { "project", "^[A-Z0-9]{2,6}$" },
            new[] { "originator", "^[A-Z0-9]{3,6}$" },
            new[] { "volume", "^[A-Z0-9]{2}$" },
            new[] { "level", "^[A-Z0-9]{2}$" },
            new[] { "type", "^[A-Z0-9]{2}$" },
            new[] { "role", "^[A-Z0-9]{1,2}$" },
            new[] { "number", "^[0-9]{4,6}$" }
        };

        // ORDER MATTERS: the position of a code is its rank (S0 < S1 < ... < CR), which is
        // how "has the deliverable reached the status it needs" is answered.
        private static readonly string[][] DefaultStatusCodes =
        {
            new[] { "S0", "Work in progress" },
            new[] { "S1", "Suitable for coordination" },
            new[] { "S2", "Suitable for information" },
            new[] { "S3", "Suitable for review and comment" },
            new[] { "S4", "Suitable for stage approval" },
            new[] { "A1", "Authorized and accepted" },
            new[] { "B1", "Partially accepted, with comments" },
            new[] { "CR", "As constructed record" }
        };

        private static readonly string[][] DefaultRevisionPatterns =
        {
            new[] { "preliminary", "^P[0-9]{2}(\\.[0-9]{2})?$" },
            new[] { "contractual", "^C[0-9]{2}$" }
        };

        private static readonly HashSet<string> SpecKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "fields", "field_order", "separator", "field_patterns", "status", "revision", "title",
            "status_codes", "revision_patterns", "file_name"
        };

        private static readonly char[] InvalidNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

        // ---- parsing ---------------------------------------------------------------

        /// <summary>
        /// Read an information_container object. Unknown keys are REFUSED by name: a
        /// misspelled "revison" silently ignored would stamp a container with no
        /// revision while the caller believes it has one.
        /// </summary>
        public static ContainerSpec ParseSpec(JToken token)
        {
            var o = token as JObject;
            if (o == null) throw new ContainerRuleException("information_container must be an object.");
            foreach (JProperty p in o.Properties())
                if (!SpecKeys.Contains(p.Name))
                    throw new ContainerRuleException("information_container has an unknown key '" + p.Name +
                        "'. Accepted: " + string.Join(", ", SpecKeys.OrderBy(k => k, StringComparer.Ordinal)) + ".");

            var spec = new ContainerSpec();
            var fields = o["fields"] as JObject;
            if (fields == null || !fields.Properties().Any())
                throw new ContainerRuleException("information_container.fields is required: an object of field name -> code.");
            foreach (JProperty p in fields.Properties())
            {
                if (p.Value.Type != JTokenType.String)
                    throw new ContainerRuleException("information_container.fields." + p.Name + " must be a string.");
                spec.Fields[p.Name] = (string)p.Value;
            }
            spec.Naming = ParseNaming(o, spec.Fields.Keys);
            spec.Status = OptionalString(o, "status");
            spec.Revision = OptionalString(o, "revision");
            spec.Title = OptionalString(o, "title");
            return spec;
        }

        /// <summary>
        /// The rules part of a container object (or of a bare naming object: the same keys
        /// minus fields/status/revision/title). fieldNames, when given, supplies the field
        /// set used to decide whether the ISO order can be assumed.
        /// </summary>
        public static ContainerNaming ParseNaming(JObject o, IEnumerable<string> fieldNames)
        {
            var n = new ContainerNaming();
            o = o ?? new JObject();

            string sep = OptionalString(o, "separator");
            if (sep != null)
            {
                if (sep.Length == 0 || sep.Length > 3) throw new ContainerRuleException("separator must be 1 to 3 characters.");
                if (sep.IndexOfAny(InvalidNameChars) >= 0) throw new ContainerRuleException("separator cannot contain a character Windows forbids in file names.");
                n.Separator = sep;
            }

            if (o["field_order"] != null && o["field_order"].Type != JTokenType.Null)
            {
                var arr = o["field_order"] as JArray;
                if (arr == null || arr.Count == 0) throw new ContainerRuleException("field_order must be a non-empty array of field names.");
                foreach (JToken t in arr)
                {
                    if (t.Type != JTokenType.String || string.IsNullOrEmpty((string)t))
                        throw new ContainerRuleException("field_order entries must be non-empty strings.");
                    if (n.FieldOrder.Contains((string)t))
                        throw new ContainerRuleException("field_order names '" + (string)t + "' twice.");
                    n.FieldOrder.Add((string)t);
                }
            }
            else
            {
                List<string> names = fieldNames == null ? null : fieldNames.ToList();
                if (names != null && names.Count > 0 &&
                    !(names.Count == DefaultFieldOrder.Length && DefaultFieldOrder.All(names.Contains)))
                    throw new ContainerRuleException("field_order is required when the fields are not exactly the seven " +
                        "ISO 19650-2 fields (" + string.Join(", ", DefaultFieldOrder) + "). The order of a name is never guessed.");
                n.FieldOrder.AddRange(DefaultFieldOrder);
                n.FieldOrderDefaulted = true;
            }

            foreach (string[] d in DefaultFieldPatterns) n.FieldPatterns[d[0]] = d[1];
            n.FieldPatternsSource = SourceDefault;
            if (o["field_patterns"] != null && o["field_patterns"].Type != JTokenType.Null)
            {
                var fp = o["field_patterns"] as JObject;
                if (fp == null) throw new ContainerRuleException("field_patterns must be an object of field name -> regular expression.");
                foreach (JProperty p in fp.Properties())
                {
                    if (p.Value.Type != JTokenType.String) throw new ContainerRuleException("field_patterns." + p.Name + " must be a string.");
                    CompileOrRefuse("field_patterns." + p.Name, (string)p.Value);
                    n.FieldPatterns[p.Name] = (string)p.Value;
                }
                n.FieldPatternsSource = SourceMerged;
            }

            if (o["status_codes"] != null && o["status_codes"].Type != JTokenType.Null)
            {
                var sc = o["status_codes"] as JObject;
                if (sc == null || !sc.Properties().Any()) throw new ContainerRuleException("status_codes must be a non-empty object of code -> description.");
                foreach (JProperty p in sc.Properties())
                    n.StatusCodes.Add(new KeyValuePair<string, string>(p.Name, p.Value.Type == JTokenType.Null ? "" : p.Value.ToString()));
                n.StatusCodesSource = SourceArgument;
            }
            else foreach (string[] d in DefaultStatusCodes) n.StatusCodes.Add(new KeyValuePair<string, string>(d[0], d[1]));

            if (o["revision_patterns"] != null && o["revision_patterns"].Type != JTokenType.Null)
            {
                var rp = o["revision_patterns"] as JObject;
                if (rp == null || !rp.Properties().Any()) throw new ContainerRuleException("revision_patterns must be a non-empty object of kind -> regular expression.");
                foreach (JProperty p in rp.Properties())
                {
                    if (p.Value.Type != JTokenType.String) throw new ContainerRuleException("revision_patterns." + p.Name + " must be a string.");
                    CompileOrRefuse("revision_patterns." + p.Name, (string)p.Value);
                    n.RevisionPatterns.Add(new KeyValuePair<string, string>(p.Name, (string)p.Value));
                }
                n.RevisionPatternsSource = SourceArgument;
            }
            else foreach (string[] d in DefaultRevisionPatterns) n.RevisionPatterns.Add(new KeyValuePair<string, string>(d[0], d[1]));

            string mode = OptionalString(o, "file_name");
            if (mode != null)
            {
                if (mode != FileNameName && mode != FileNameWithStatusRevision)
                    throw new ContainerRuleException("file_name must be '" + FileNameName + "' or '" + FileNameWithStatusRevision + "'.");
                n.FileNameMode = mode;
            }
            return n;
        }

        /// <summary>
        /// The naming rules of a project-context.json (schema_version 1, section "naming":
        /// fields [{name, pattern}], separator, status_codes, revision {preliminary_pattern,
        /// contractual_pattern}). Absent parts fall back to the ISO defaults and say so.
        /// </summary>
        public static ContainerNaming NamingFromProjectContext(JObject context)
        {
            var naming = context?["naming"] as JObject;
            var o = new JObject();
            if (naming == null) return ParseNaming(o, null);
            if (naming["separator"] != null) o["separator"] = naming["separator"];
            if (naming["file_name"] != null) o["file_name"] = naming["file_name"];
            if (naming["fields"] is JArray fields && fields.Count > 0)
            {
                var order = new JArray();
                var patterns = new JObject();
                foreach (JToken f in fields)
                {
                    string name = f?["name"]?.Type == JTokenType.String ? (string)f["name"] : null;
                    if (string.IsNullOrEmpty(name)) throw new ContainerRuleException("project-context naming.fields entries need a 'name'.");
                    order.Add(name);
                    if (f["pattern"] != null && f["pattern"].Type == JTokenType.String) patterns[name] = f["pattern"];
                }
                o["field_order"] = order;
                if (patterns.Properties().Any()) o["field_patterns"] = patterns;
            }
            if (naming["status_codes"] is JObject sc && sc.Properties().Any()) o["status_codes"] = sc;
            if (naming["revision"] is JObject rev)
            {
                var rp = new JObject();
                if (rev["preliminary_pattern"] != null && rev["preliminary_pattern"].Type == JTokenType.String) rp["preliminary"] = rev["preliminary_pattern"];
                if (rev["contractual_pattern"] != null && rev["contractual_pattern"].Type == JTokenType.String) rp["contractual"] = rev["contractual_pattern"];
                if (rp.Properties().Any()) o["revision_patterns"] = rp;
            }
            return ParseNaming(o, null);
        }

        // ---- validation --------------------------------------------------------------

        /// <summary>
        /// Compose and judge. Every problem is collected, not just the first, so one reply
        /// is enough to fix a name. requireStatusRevision: a container being STAMPED needs
        /// both; composing a name alone does not.
        /// </summary>
        public static ContainerValidation Validate(ContainerSpec spec, bool requireStatusRevision)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            ContainerNaming n = spec.Naming;
            var v = new ContainerValidation();

            foreach (string f in spec.Fields.Keys)
                if (!n.FieldOrder.Contains(f))
                    Problem(v, f, spec.Fields[f], "field_not_in_order",
                        "field '" + f + "' is not in field_order, so it would be silently left out of the name");

            var parts = new List<string>();
            foreach (string f in n.FieldOrder)
            {
                string value;
                if (!spec.Fields.TryGetValue(f, out value) || string.IsNullOrEmpty(value))
                {
                    Problem(v, f, value, "field_missing", "field '" + f + "' is named in field_order but has no value");
                    continue;
                }
                parts.Add(value);
                CheckFieldValue(n, f, value, v);
            }
            v.Name = string.Join(n.Separator, parts);

            if (spec.Status != null) CheckStatus(n, spec.Status, v);
            else if (requireStatusRevision) Problem(v, "status", null, "status_missing", "a stamped container needs a suitability status");
            if (spec.Revision != null) CheckRevision(n, spec.Revision, v);
            else if (requireStatusRevision) Problem(v, "revision", null, "revision_missing", "a stamped container needs a revision");

            if (spec.Status != null && spec.Revision != null) CheckCoherence(v);

            if (n.FileNameMode == FileNameWithStatusRevision)
            {
                if (spec.Status == null || spec.Revision == null)
                    Problem(v, "file_name", n.FileNameMode, "file_name_needs_status_revision",
                        "file_name=name_status_revision needs both status and revision");
                v.FileStem = v.Name + n.Separator + (spec.Status ?? "") + n.Separator + (spec.Revision ?? "");
            }
            else v.FileStem = v.Name;

            v.Valid = v.Problems.Count == 0;
            return v;
        }

        private static void CheckFieldValue(ContainerNaming n, string f, string value, ContainerValidation v)
        {
            if (value.IndexOf(n.Separator, StringComparison.Ordinal) >= 0)
                Problem(v, f, value, "contains_separator", "value contains the separator '" + n.Separator + "', so the name could not be read back");
            if (value.IndexOfAny(InvalidNameChars) >= 0 || value.Trim().Length != value.Length)
                Problem(v, f, value, "invalid_characters", "value has leading/trailing spaces or a character Windows forbids in file names");
            string pattern;
            if (n.FieldPatterns.TryGetValue(f, out pattern))
            {
                if (!FullMatch(pattern, value))
                    Problem(v, f, value, "pattern_mismatch", "value does not match " + pattern);
            }
            else Warn(v, f, "no_pattern", "no pattern is declared for field '" + f + "'; only emptiness and separators were checked");
        }

        private static void CheckStatus(ContainerNaming n, string status, ContainerValidation v)
        {
            foreach (var kv in n.StatusCodes)
                if (string.Equals(kv.Key, status, StringComparison.Ordinal))
                {
                    v.StatusDescription = kv.Value;
                    v.StatusState = StateOfStatus(status);
                    return;
                }
            Problem(v, "status", status, "status_unknown",
                "status is not one of the declared codes (" + string.Join(", ", n.StatusCodes.Select(k => k.Key)) + ")");
        }

        private static void CheckRevision(ContainerNaming n, string revision, ContainerValidation v)
        {
            foreach (var kv in n.RevisionPatterns)
                if (FullMatch(kv.Value, revision)) { v.RevisionKind = kv.Key; return; }
            Problem(v, "revision", revision, "revision_pattern_mismatch",
                "revision matches none of " + string.Join(", ", n.RevisionPatterns.Select(k => k.Key + "=" + k.Value)));
        }

        /// <summary>
        /// ISO 19650-2 practice: preliminary revisions travel with S-statuses, contractual
        /// ones with A/B/CR. A mismatch is a WARNING, never a refusal - a project may have
        /// agreed otherwise, and that is its call.
        /// </summary>
        private static void CheckCoherence(ContainerValidation v)
        {
            if (v.StatusState == null || v.RevisionKind == null) return;
            if (v.RevisionKind == "preliminary" && v.StatusState == StatePublished)
                Warn(v, "revision", "revision_status_mismatch", "a preliminary revision with a published (A/B/CR) status is unusual under ISO 19650-2");
            if (v.RevisionKind == "contractual" && (v.StatusState == StateWip || v.StatusState == StateShared))
                Warn(v, "revision", "revision_status_mismatch", "a contractual revision with a WIP/shared (S) status is unusual under ISO 19650-2");
        }

        /// <summary>
        /// Read a file stem back into a container. The inverse of Validate's composition,
        /// with the same checks: a name that cannot be split into the declared fields is
        /// reported as such, never "mostly" parsed.
        /// </summary>
        public static ContainerValidation CheckStem(ContainerNaming n, string stem, out ContainerSpec parsed)
        {
            parsed = null;
            var v = new ContainerValidation { FileStem = stem };
            string[] parts = (stem ?? "").Split(new[] { n.Separator }, StringSplitOptions.None);
            int expected = n.FieldOrder.Count + (n.FileNameMode == FileNameWithStatusRevision ? 2 : 0);
            if (parts.Length != expected)
            {
                Problem(v, "name", stem, "field_count",
                    "splits into " + parts.Length + " parts on '" + n.Separator + "', the rules expect " + expected);
                v.Valid = false;
                return v;
            }
            var spec = new ContainerSpec { Naming = n };
            for (int i = 0; i < n.FieldOrder.Count; i++) spec.Fields[n.FieldOrder[i]] = parts[i];
            if (n.FileNameMode == FileNameWithStatusRevision)
            {
                spec.Status = parts[n.FieldOrder.Count];
                spec.Revision = parts[n.FieldOrder.Count + 1];
            }
            ContainerValidation full = Validate(spec, false);
            full.FileStem = stem;
            parsed = spec;
            return full;
        }

        // ---- CDE states, statuses, revisions ------------------------------------------

        /// <summary>
        /// The CDE state an ISO 19650-2 status belongs to, or null for a code this rule
        /// does not know (a custom code: its state is not assessed rather than guessed).
        /// </summary>
        public static string StateOfStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return null;
            if (status == "S0") return StateWip;
            if (status == "CR") return StatePublished;
            if (status.Length >= 2 && char.IsDigit(status[1]))
            {
                char c = status[0];
                if (c == 'S' || c == 'D') return StateShared;
                if (c == 'A' || c == 'B') return StatePublished;
            }
            return null;
        }

        public static int StateRank(string state)
        {
            for (int i = 0; i < States.Length; i++) if (States[i] == state) return i;
            return -1;
        }

        /// <summary>Position of a status in the declared list, or -1.</summary>
        public static int StatusRank(ContainerNaming n, string status)
        {
            for (int i = 0; i < n.StatusCodes.Count; i++)
                if (string.Equals(n.StatusCodes[i].Key, status, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>
        /// Compare two revisions of the SAME kind (P01 vs P02, P01.01 vs P01.02, C01 vs
        /// C02). Returns null when they are of different kinds or unreadable: a P and a C
        /// are not ordered against each other, and pretending otherwise would invent a
        /// finding.
        /// </summary>
        public static int? CompareRevisions(string a, string b)
        {
            string pa, pb; int[] na, nb;
            if (!SplitRevision(a, out pa, out na) || !SplitRevision(b, out pb, out nb)) return null;
            if (!string.Equals(pa, pb, StringComparison.Ordinal)) return null;
            for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
            {
                int x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static bool SplitRevision(string r, out string prefix, out int[] numbers)
        {
            prefix = null; numbers = null;
            if (string.IsNullOrEmpty(r)) return false;
            int i = 0;
            while (i < r.Length && char.IsLetter(r[i])) i++;
            if (i == r.Length) return false;
            prefix = r.Substring(0, i);
            string[] chunks = r.Substring(i).Split('.');
            var list = new List<int>();
            foreach (string c in chunks)
            {
                int value;
                if (!int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
                list.Add(value);
            }
            numbers = list.ToArray();
            return true;
        }

        // ---- files, hashes, sidecars ----------------------------------------------------

        public static string SidecarPath(string file) => file + SidecarSuffix;

        public static bool IsSidecar(string path) =>
            path != null && path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>Lowercase hex SHA-256 of a file, streamed; also returns its length.</summary>
        public static string Sha256File(string path, out long bytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                bytes = stream.Length;
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// The sidecar document for a file. extra is merged last (state, approval,
        /// provenance of a transition) and may not replace the core keys.
        /// </summary>
        public static JObject BuildSidecar(ContainerSpec spec, ContainerValidation validation, string file,
                                           long bytes, string sha256, string tool, string sourceDocument,
                                           string revitYear, DateTime createdUtc, JObject extra)
        {
            var fields = new JObject();
            foreach (string f in spec.Naming.FieldOrder)
            {
                string value;
                if (spec.Fields.TryGetValue(f, out value)) fields[f] = value;
            }
            var doc = new JObject
            {
                ["schema"] = SidecarSchema,
                ["name"] = validation.Name,
                ["fields"] = fields,
                ["naming"] = new JObject
                {
                    ["field_order"] = new JArray(spec.Naming.FieldOrder.Cast<object>().ToArray()),
                    ["separator"] = spec.Naming.Separator,
                    ["file_name"] = spec.Naming.FileNameMode
                },
                ["status"] = spec.Status,
                ["revision"] = spec.Revision,
                ["title"] = spec.Title,
                ["file"] = Path.GetFileName(file),
                ["bytes"] = bytes,
                ["sha256"] = sha256,
                ["created_utc"] = createdUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["tool"] = tool,
                ["source_document"] = sourceDocument,
                ["revit_year"] = revitYear
            };
            if (extra != null)
                foreach (JProperty p in extra.Properties())
                {
                    if (doc[p.Name] != null) throw new ArgumentException("extra may not replace the sidecar key '" + p.Name + "'.");
                    doc[p.Name] = p.Value.DeepClone();
                }
            return doc;
        }

        /// <summary>
        /// Write the sidecar beside the file: temporary file in the same folder, then a
        /// move that cannot overwrite. Then READ BACK the exact text and re-hash the file.
        /// Refuses when a sidecar already exists - an existing one is never replaced.
        /// Returns the evidence; throws IOException when anything does not hold (the
        /// message names what, and whether the sidecar is on disk).
        /// </summary>
        public static JObject WriteSidecarVerified(string file, JObject sidecar)
        {
            string path = SidecarPath(file);
            if (File.Exists(path))
                throw new IOException("A sidecar already exists at " + path + " and is never overwritten.");
            string expected = sidecar.ToString(Formatting.Indented);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(expected);
                    writer.Flush();
                    stream.Flush(true);
                }
                File.Move(tmp, path);   // net48 File.Move never overwrites: a race loses loudly
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            string reread = File.ReadAllText(path, new UTF8Encoding(false));
            if (!string.Equals(expected, reread, StringComparison.Ordinal))
                throw new IOException("The sidecar at " + path + " was written but reads back different from what was intended. It is on disk and is NOT trustworthy.");
            long bytes;
            string sha = Sha256File(file, out bytes);
            if (!string.Equals(sha, (string)sidecar["sha256"], StringComparison.Ordinal) || bytes != (long)sidecar["bytes"])
                throw new IOException("The file " + file + " changed while its sidecar was being written (sha256 now " + sha +
                                      "). The sidecar at " + path + " describes the previous bytes.");
            return new JObject
            {
                ["sidecar_path"] = path,
                ["verified"] = true,
                ["reread_matches"] = true,
                ["file_rehashed"] = true,
                ["sha256"] = sha,
                ["bytes"] = bytes,
                ["write_method"] = "temporary file + no-overwrite move, then exact reread"
            };
        }

        /// <summary>Parse a sidecar WITHOUT date promotion. Null plus a reason when unreadable.</summary>
        public static JObject ReadSidecar(string path, out string problem)
        {
            problem = null;
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    var o = JToken.ReadFrom(reader) as JObject;
                    if (o == null) { problem = "not a JSON object"; return null; }
                    if ((string)o["schema"] != SidecarSchema) { problem = "schema is '" + (string)o["schema"] + "', expected " + SidecarSchema; return null; }
                    return o;
                }
            }
            catch (JsonException ex) { problem = "not valid JSON: " + ex.Message; return null; }
            catch (IOException ex) { problem = "could not be read: " + ex.Message; return null; }
            catch (UnauthorizedAccessException ex) { problem = "could not be read: " + ex.Message; return null; }
        }

        /// <summary>
        /// Does this file still match its sidecar? verdict: match | modified | renamed |
        /// name_inconsistent | missing_file | missing_sidecar | sidecar_invalid. A hash
        /// that differs means the bytes changed AFTER sealing, whatever the dates say.
        /// </summary>
        public static JObject VerifyFile(string file)
        {
            string sidecarPath = SidecarPath(file);
            var result = new JObject { ["file"] = file, ["sidecar_path"] = sidecarPath };
            if (!File.Exists(file)) { result["verdict"] = "missing_file"; result["matches"] = false; return result; }
            if (!File.Exists(sidecarPath)) { result["verdict"] = "missing_sidecar"; result["matches"] = false; return result; }
            string problem;
            JObject sidecar = ReadSidecar(sidecarPath, out problem);
            if (sidecar == null)
            {
                result["verdict"] = "sidecar_invalid"; result["matches"] = false; result["reason"] = problem;
                return result;
            }
            long bytes;
            string sha = Sha256File(file, out bytes);
            var mismatches = new JArray();
            if (!string.Equals(sha, (string)sidecar["sha256"], StringComparison.OrdinalIgnoreCase))
                mismatches.Add(new JObject { ["check"] = "sha256", ["sidecar"] = sidecar["sha256"], ["file"] = sha });
            if (sidecar["bytes"] == null || sidecar["bytes"].Type != JTokenType.Integer || (long)sidecar["bytes"] != bytes)
                mismatches.Add(new JObject { ["check"] = "bytes", ["sidecar"] = sidecar["bytes"], ["file"] = bytes });
            string actualName = Path.GetFileName(file);
            bool renamed = !string.Equals((string)sidecar["file"], actualName, StringComparison.Ordinal);
            if (renamed)
                mismatches.Add(new JObject { ["check"] = "file", ["sidecar"] = sidecar["file"], ["file"] = actualName });

            string recomposed = RecomposeStem(sidecar);
            string stem = Path.GetFileNameWithoutExtension(file);
            bool nameInconsistent = recomposed == null || !string.Equals(recomposed, stem, StringComparison.Ordinal);
            if (nameInconsistent)
                mismatches.Add(new JObject { ["check"] = "name", ["sidecar"] = recomposed, ["file"] = stem,
                    ["reason"] = recomposed == null ? "the sidecar's fields/naming cannot recompose a name" : "the name the sidecar's fields compose is not the file's name" });

            bool hashDiffers = mismatches.Any(m => (string)m["check"] == "sha256" || (string)m["check"] == "bytes");
            result["verdict"] = hashDiffers ? "modified" : renamed ? "renamed" : nameInconsistent ? "name_inconsistent" : "match";
            result["matches"] = mismatches.Count == 0;
            result["mismatches"] = mismatches;
            result["sha256"] = sha;
            result["bytes"] = bytes;
            result["sidecar"] = sidecar;
            return result;
        }

        /// <summary>The file stem a sidecar's own fields and naming compose, or null.</summary>
        public static string RecomposeStem(JObject sidecar)
        {
            var naming = sidecar?["naming"] as JObject;
            var fields = sidecar?["fields"] as JObject;
            var order = naming?["field_order"] as JArray;
            string sep = (string)naming?["separator"];
            if (fields == null || order == null || string.IsNullOrEmpty(sep)) return null;
            var parts = new List<string>();
            foreach (JToken f in order)
            {
                string value = (string)fields[(string)f];
                if (string.IsNullOrEmpty(value)) return null;
                parts.Add(value);
            }
            string name = string.Join(sep, parts);
            if (!string.Equals(name, (string)sidecar["name"], StringComparison.Ordinal)) return null;
            if ((string)naming["file_name"] == FileNameWithStatusRevision)
                return name + sep + (string)sidecar["status"] + sep + (string)sidecar["revision"];
            return name;
        }

        /// <summary>
        /// Resolve the file an export should produce when it carries a container: the
        /// directory and extension of output_path, the container's file stem. Pure; the
        /// caller decides what to do with an invalid container (refuse before exporting).
        /// </summary>
        public static string ContainerOutputPath(string outputPath, ContainerValidation validation)
        {
            string dir = Path.GetDirectoryName(outputPath);
            string ext = Path.GetExtension(outputPath);
            return Path.Combine(dir ?? "", validation.FileStem + ext);
        }

        // ---- helpers ------------------------------------------------------------------

        public static bool FullMatch(string pattern, string value)
        {
            try
            {
                return Regex.IsMatch(value ?? "", "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, RegexBudget);
            }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static void CompileOrRefuse(string where, string pattern)
        {
            try { new Regex(pattern, RegexOptions.CultureInvariant, RegexBudget); }
            catch (ArgumentException ex) { throw new ContainerRuleException(where + " is not a valid regular expression: " + ex.Message); }
        }

        private static string OptionalString(JObject o, string key)
        {
            JToken t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.String) throw new ContainerRuleException(key + " must be a string.");
            return (string)t;
        }

        private static void Problem(ContainerValidation v, string field, string value, string code, string reason)
            => v.Problems.Add(new JObject { ["field"] = field, ["value"] = value, ["code"] = code, ["reason"] = reason });

        private static void Warn(ContainerValidation v, string field, string code, string reason)
            => v.Warnings.Add(new JObject { ["field"] = field, ["code"] = code, ["reason"] = reason });
    }
}
