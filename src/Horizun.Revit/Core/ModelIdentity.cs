// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free and net48-safe.
//
// WHICH MODEL IS THIS - decision D0 of the 3.0.0 interoperability programme
// (approved by the owner, 2026-10-12).
//
// A model_ref is an id the PROJECT REGISTRY assigns (project-integrations.json,
// sibling of project-context.json). It is bound to STRONG evidence, read in this
// order and decided by the first key both sides carry:
//
//   1. creation_guid         Document.CreationGUID (Revit 2024+); kept by copies of
//                            the same project, different for two projects made
//                            from one template.
//   2. central_guid          Document.WorksharingCentralGUID (server-based centrals).
//   3. cloud_model_guid      the cloud model path's model GUID (ACC / BIM 360).
//   4. unique_document_guid  the file header's Unique Document GUID.
//
// project_info_unique_id is WEAK: every project made from one template inherits
// the template's ProjectInformation element (MEASURED 2026-10-09 in Revit 2023,
// 2024 and 2027). It is recorded, and it never matches anything by itself.
//
// identity_proven is true only when the binding key is strong AND the evidence was
// not read on Revit 2023 - the owner's rule: Revit 2023 has no creation GUID, so a
// 2023 reading is reported identity_proven:false whatever else it carries.
//
// Element identity is (owner model_ref, UniqueId) plus its placement; the
// "<link>::<uid>" form some tools print is presentation only and is never parsed
// back into an identity here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ModelIdentity
    {
        /// <summary>The strong keys, in the D0 order. The first one both sides carry decides.</summary>
        public static readonly string[] StrongKeys =
            { "creation_guid", "central_guid", "cloud_model_guid", "unique_document_guid" };

        /// <summary>Recorded and reported; never matches by itself.</summary>
        public const string WeakKey = "project_info_unique_id";

        /// <summary>Context the evidence carries beside the keys. Never used to match.</summary>
        public static readonly string[] ContextKeys = { "cloud_project_guid", "revit_version", "title", "path" };

        public static IEnumerable<string> AllKeys => StrongKeys.Concat(new[] { WeakKey }).Concat(ContextKeys);

        /// <summary>Match outcomes, one vocabulary for the registry and the exchange verifier.</summary>
        public const string Matched = "matched";
        public const string NoMatch = "no_match";
        public const string Ambiguous = "ambiguous";
        public const string NoStrongEvidence = "no_strong_evidence";

        /// <summary>
        /// The evidence block a Revit read publishes (get_document_info model_identity).
        /// A GUID that is empty (all zeros) is "not applicable", never a value: a file-based
        /// model's WorksharingCentralGUID is Guid.Empty and two such models must not match.
        /// </summary>
        public static JObject Evidence(string creationGuid, string centralGuid, string cloudModelGuid,
                                       string cloudProjectGuid, string uniqueDocumentGuid, string projectInfoUniqueId,
                                       string revitVersion, string title, string path)
        {
            var o = new JObject
            {
                ["creation_guid"] = Guid0(creationGuid),
                ["central_guid"] = Guid0(centralGuid),
                ["cloud_model_guid"] = Guid0(cloudModelGuid),
                ["unique_document_guid"] = Guid0(uniqueDocumentGuid),
                [WeakKey] = Text(projectInfoUniqueId),
                ["cloud_project_guid"] = Guid0(cloudProjectGuid),
                ["revit_version"] = Text(revitVersion),
                ["title"] = Text(title),
                ["path"] = Text(path)
            };
            string binding = BindingKey(o);
            o["binding_key"] = binding;
            o["identity_proven"] = Proven(o, binding);
            o["identity_note"] = Note(o, binding);
            return o;
        }

        /// <summary>
        /// A caller's evidence object, normalised: unknown keys refused by name, GUIDs in
        /// canonical lower-case form, zero GUIDs and blanks dropped to null. The derived
        /// fields (binding_key, identity_proven, identity_note) are ignored on input and
        /// recomputed - a caller cannot declare its own evidence proven.
        /// </summary>
        public static JObject Normalize(JObject evidence, out List<string> problems)
        {
            problems = new List<string>();
            if (evidence == null) { problems.Add("evidence is required (an object)."); return null; }
            var known = new HashSet<string>(AllKeys, StringComparer.Ordinal)
                { "binding_key", "identity_proven", "identity_note" };
            foreach (JProperty p in evidence.Properties())
                if (!known.Contains(p.Name))
                    problems.Add("evidence." + p.Name + " is not an evidence key (known: " + string.Join(", ", AllKeys) + ").");
            foreach (string k in StrongKeys.Concat(new[] { "cloud_project_guid" }))
            {
                JToken t = evidence[k];
                if (t == null || t.Type == JTokenType.Null) continue;
                if (t.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)t)) continue;
                Guid g;
                if (t.Type != JTokenType.String || !Guid.TryParse((string)t, out g))
                    problems.Add("evidence." + k + " must be a GUID string or null; got '" + t + "'.");
            }
            if (problems.Count > 0) return null;
            return Evidence((string)evidence["creation_guid"], (string)evidence["central_guid"],
                            (string)evidence["cloud_model_guid"], (string)evidence["cloud_project_guid"],
                            (string)evidence["unique_document_guid"], (string)evidence[WeakKey],
                            (string)evidence["revit_version"], (string)evidence["title"], (string)evidence["path"]);
        }

        /// <summary>The first strong key the evidence carries, in D0 order; null when it carries none.</summary>
        public static string BindingKey(JObject evidence)
        {
            if (evidence == null) return null;
            foreach (string k in StrongKeys)
                if (Value(evidence, k) != null) return k;
            return null;
        }

        public static bool IsRevit2023(JObject evidence)
        {
            string v = Value(evidence, "revit_version");
            return v != null && v.Trim().StartsWith("2023", StringComparison.Ordinal);
        }

        private static bool Proven(JObject evidence, string binding) => binding != null && !IsRevit2023(evidence);

        private static string Note(JObject evidence, string binding)
        {
            if (binding == null)
                return "No strong evidence: only " + WeakKey + " (if any), which every project made from the same template " +
                       "shares. This model cannot be registered or matched until a strong key is read.";
            if (IsRevit2023(evidence))
                return "Read on Revit 2023, which records no document creation GUID: bound by " + binding +
                       " but reported identity_proven:false (owner decision D0).";
            return null;
        }

        /// <summary>
        /// Match observed evidence against registered models. For each model, the FIRST strong
        /// key both carry decides: equal is a candidate, different rules the model out even
        /// when a weaker key agrees (reported as an evidence conflict). project_info_unique_id
        /// never decides. More than one candidate is ambiguous and matches nothing.
        /// </summary>
        public static JObject Match(IEnumerable<JObject> models, JObject observed)
        {
            var result = new JObject { ["state"] = null, ["model_ref"] = null, ["matched_by"] = null, ["identity_proven"] = false };
            var warnings = new JArray();
            result["warnings"] = warnings;
            string observedBinding = BindingKey(observed);
            if (observedBinding == null)
            {
                result["state"] = NoStrongEvidence;
                result["reason"] = "The observed evidence carries no strong key (" + string.Join(", ", StrongKeys) + "). " +
                                   WeakKey + " alone is shared by every project made from one template, so nothing is matched.";
                return result;
            }

            var candidates = new List<KeyValuePair<JObject, string>>();
            foreach (JObject m in models ?? Enumerable.Empty<JObject>())
            {
                JObject ev = m?["evidence"] as JObject;
                if (ev == null) continue;
                string decidedBy = null;
                bool equal = false;
                foreach (string k in StrongKeys)
                {
                    string a = Value(ev, k), b = Value(observed, k);
                    if (a == null || b == null) continue;
                    decidedBy = k;
                    equal = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                    break;
                }
                if (decidedBy == null) continue;
                if (equal) { candidates.Add(new KeyValuePair<JObject, string>(m, decidedBy)); continue; }
                // Ruled out by the decisive key. A weaker strong key that agrees is worth saying.
                foreach (string k in StrongKeys.SkipWhile(x => x != decidedBy).Skip(1))
                {
                    string a = Value(ev, k), b = Value(observed, k);
                    if (a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                        warnings.Add(new JObject
                        {
                            ["code"] = "evidence_conflict",
                            ["model_ref"] = (string)m["model_ref"],
                            ["message"] = "Registered model " + (string)m["model_ref"] + " differs on " + decidedBy +
                                          " but shares " + k + ". It was ruled out by the stronger key; a copy made " +
                                          "with Save As can look like this."
                        });
                }
            }

            if (candidates.Count == 0)
            {
                result["state"] = NoMatch;
                result["reason"] = "No registered model carries the observed " + observedBinding + ".";
                return result;
            }
            if (candidates.Count > 1)
            {
                result["state"] = Ambiguous;
                result["candidates"] = new JArray(candidates.Select(c => (string)c.Key["model_ref"]));
                result["reason"] = candidates.Count + " registered models carry the same evidence; the registry is " +
                                   "inconsistent and nothing is matched until it is repaired.";
                return result;
            }
            JObject hit = candidates[0].Key;
            result["state"] = Matched;
            result["model_ref"] = (string)hit["model_ref"];
            result["matched_by"] = candidates[0].Value;
            bool proven = !IsRevit2023(observed) && !IsRevit2023(hit["evidence"] as JObject);
            result["identity_proven"] = proven;
            if (!proven)
                warnings.Add(new JObject
                {
                    ["code"] = "identity_unprovable_2023",
                    ["message"] = "One side of this match was read on Revit 2023, which records no document creation " +
                                  "GUID. The match stands on " + candidates[0].Value + " and is reported unproven (D0)."
                });
            return result;
        }

        /// <summary>A new registry id: mdl- and twelve hex digits. The registry, not the model, owns it.</summary>
        public static string NewModelRef(Func<string> hex = null)
        {
            string h = hex != null ? hex() : Guid.NewGuid().ToString("N");
            return "mdl-" + h.Substring(0, 12).ToLowerInvariant();
        }

        public static string Value(JObject o, string key)
        {
            JToken t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            string s = t.Type == JTokenType.String ? (string)t : t.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        private static JToken Guid0(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return JValue.CreateNull();
            Guid g;
            if (!Guid.TryParse(s.Trim(), out g)) return JValue.CreateNull();
            if (g == Guid.Empty) return JValue.CreateNull();
            return g.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
        }

        private static JToken Text(string s) => string.IsNullOrWhiteSpace(s) ? JValue.CreateNull() : (JToken)s.Trim();
    }

    /// <summary>
    /// project-integrations.json, schema horizun.project-integrations/0: the project's
    /// registry of models (model_ref + D0 evidence) and its exchange root. A sibling of
    /// project-context.json, whose v1 schema is closed and does not admit this block.
    /// </summary>
    public static class ProjectIntegrations
    {
        public const string Schema = "horizun.project-integrations/0";
        public const string FileName = "project-integrations.json";

        private static readonly HashSet<string> TopKeys = new HashSet<string>(StringComparer.Ordinal)
            { "schema", "project", "exchange_root", "models", "notes" };

        private static readonly HashSet<string> ModelKeys = new HashSet<string>(StringComparer.Ordinal)
            { "model_ref", "name", "discipline", "evidence", "binding_key", "identity_proven", "registered_utc", "notes" };

        public static JObject Empty(string projectCode) => new JObject
        {
            ["schema"] = Schema,
            ["project"] = new JObject { ["code"] = projectCode },
            ["exchange_root"] = null,
            ["models"] = new JArray()
        };

        /// <summary>Every problem with a registry document, as JSON-pointer-prefixed sentences. Empty means valid.</summary>
        public static List<string> Validate(JToken doc)
        {
            var errors = new List<string>();
            var o = doc as JObject;
            if (o == null) { errors.Add("/: the registry must be a JSON object."); return errors; }
            if ((string)o["schema"] != Schema) errors.Add("/schema: must be '" + Schema + "'.");
            foreach (JProperty p in o.Properties())
                if (!TopKeys.Contains(p.Name)) errors.Add("/" + p.Name + ": unknown key.");
            JToken root = o["exchange_root"];
            if (root != null && root.Type != JTokenType.Null &&
                (root.Type != JTokenType.String || !System.IO.Path.IsPathRooted((string)root)))
                errors.Add("/exchange_root: must be null (local default) or an absolute path.");
            var models = o["models"] as JArray;
            if (models == null) { errors.Add("/models: must be an array."); return errors; }
            var refs = new HashSet<string>(StringComparer.Ordinal);
            var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < models.Count; i++)
            {
                string at = "/models/" + i;
                var m = models[i] as JObject;
                if (m == null) { errors.Add(at + ": must be an object."); continue; }
                foreach (JProperty p in m.Properties())
                    if (!ModelKeys.Contains(p.Name)) errors.Add(at + "/" + p.Name + ": unknown key.");
                string r = (string)m["model_ref"];
                if (string.IsNullOrWhiteSpace(r) || !r.StartsWith("mdl-", StringComparison.Ordinal))
                    errors.Add(at + "/model_ref: must be an id 'mdl-...'.");
                else if (!refs.Add(r)) errors.Add(at + "/model_ref: '" + r + "' appears twice.");
                List<string> evProblems;
                JObject ev = ModelIdentity.Normalize(m["evidence"] as JObject, out evProblems);
                foreach (string p in evProblems) errors.Add(at + "/" + p);
                if (ev == null) continue;
                string binding = ModelIdentity.BindingKey(ev);
                if (binding == null)
                {
                    errors.Add(at + "/evidence: carries no strong key; a model is never registered on " +
                               ModelIdentity.WeakKey + " alone.");
                    continue;
                }
                string key = binding + "=" + ModelIdentity.Value(ev, binding);
                string other;
                if (bindings.TryGetValue(key, out other))
                    errors.Add(at + "/evidence: same " + binding + " as " + other + "; two registered models cannot share their binding evidence.");
                else bindings[key] = r;
            }
            return errors;
        }

        public static IEnumerable<JObject> Models(JObject doc) => (doc?["models"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
    }
}
