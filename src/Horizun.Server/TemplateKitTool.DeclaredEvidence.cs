using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class TemplateKitTool
    {
        internal const string DeclaredEvidenceSchemaName = "horizun.template-check-evidence/1";
        private static readonly HashSet<string> DrawingChecks = new HashSet<string>(new[]
        { "cobertura_contra_plano", "visual_evidence", "quantities_by_code", "induccion_completa", "compare_drawings" }, StringComparer.Ordinal);

        // This schema describes declarations, not measurements or a signature of the author.
        internal static JObject DeclaredEvidenceSchema() => JObject.Parse(@"{
  '$schema':'https://json-schema.org/draft/2020-12/schema',
  'type':'object','additionalProperties':false,'required':['schema','target_document','checks'],
  'properties':{
    'schema':{'type':'string','enum':['horizun.template-check-evidence/1']},
    'target_document':{'type':'string','minLength':1},
    'checks':{'type':'array','minItems':1,'maxItems':200,'items':{
      'type':'object','additionalProperties':false,'required':['check_id','source','coverage','result','summary'],
      'properties':{
        'check_id':{'type':'string','minLength':1},
        'source':{'type':'object','additionalProperties':false,'required':['origin','path','sha256'],
          'properties':{'origin':{'type':'string','minLength':1},'path':{'type':'string','minLength':1},'sha256':{'type':'string','pattern':'^[0-9a-fA-F]{64}$'}}},
        'coverage':{'type':'array','minItems':1,'maxItems':10000,'items':{
          'type':'object','additionalProperties':false,'required':['drawing_ref','model_element_ids'],
          'properties':{'drawing_ref':{'type':'string','minLength':1},'model_element_ids':{'type':'array','minItems':1,'uniqueItems':true,'items':{'type':'integer','minimum':1}}}}},
        'result':{'type':'string','enum':['passed','failed']},
        'summary':{'type':'string','minLength':1}
      }
    }}
  }
}");

        private static Dictionary<string, JObject> AuditDeclaredEvidence(JObject kit, JObject args, IEnumerable<JObject> rows, bool complete, JArray findings)
        {
            var states = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var checks = (kit["checks"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(c => new { Id = (string)c["id"] ?? (string)c["name"] ?? "unnamed", Check = c })
                .Where(c => DrawingChecks.Contains(CanonicalCheck(c.Id))).ToList();
            foreach (var check in checks)
            {
                var state = CheckState("not_executed", "requiere planos");
                state["required_evidence"] = new JObject
                {
                    ["input"] = "declared_evidence_path", ["schema"] = DeclaredEvidenceSchemaName,
                    ["schema_operation"] = "evidence_schema", ["check_id"] = check.Id,
                    ["missing"] = new JArray("declared external source with origin, absolute path and matching SHA-256", "drawing_ref to model_element_ids coverage", "declared passed/failed result and summary")
                };
                states[CanonicalCheck(check.Id)] = state;
            }
            string path = (string)args["declared_evidence_path"];
            if (string.IsNullOrWhiteSpace(path)) return states;
            JObject manifest;
            string manifestHash;
            try
            {
                if (!Path.IsPathRooted(path)) throw new ArgumentException("declared_evidence_path must be absolute");
                if (new FileInfo(path).Length > MaxBytes) throw new ArgumentException("evidence manifest exceeds 32 MiB");
                byte[] bytes = File.ReadAllBytes(path);
                manifest = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                JObject validationSchema = DeclaredEvidenceSchema();
                validationSchema.Remove("$schema"); // The shared validator deliberately rejects unknown annotation keywords.
                var errors = ContractSchemaCheck.Validate(manifest, validationSchema);
                if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
                if ((string)manifest["target_document"] != (string)args["target_document"])
                    throw new ArgumentException("target_document does not match this audit");
                foreach (JObject entry in (JArray)manifest["checks"])
                    if (!checks.Any(c => c.Id == (string)entry["check_id"]))
                        throw new ArgumentException("check_id is not a drawing check in this kit: " + (string)entry["check_id"]);
                manifestHash = EvidenceHash(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
            {
                foreach (JObject state in states.Values)
                { state["reason"] = "declared evidence rejected: " + ex.Message; state["evidence_errors"] = new JArray(ex.Message); }
                return states;
            }
            var entries = ((JArray)manifest["checks"]).OfType<JObject>().ToList();
            var knownIds = new HashSet<long>(rows.Where(r => r["element_id"]?.Type == JTokenType.Integer).Select(r => (long)r["element_id"]));
            foreach (var check in checks)
            {
                string key = CanonicalCheck(check.Id);
                var matches = entries.Where(e => (string)e["check_id"] == check.Id).ToList();
                if (matches.Count == 0) continue; // Evidence for another check cannot satisfy this one.
                var errors = new JArray(); JObject entry = matches[0];
                if (matches.Count != 1) errors.Add("check_id must occur exactly once");
                if (!complete) errors.Add("model census is incomplete; covered element ids cannot be fully validated");
                string sourcePath = (string)entry["source"]["path"];
                string actualHash = null;
                try
                {
                    if (!Path.IsPathRooted(sourcePath)) throw new ArgumentException("source.path must be absolute");
                    if (string.IsNullOrWhiteSpace((string)entry["source"]["origin"])) throw new ArgumentException("source.origin must name the declared producer or source");
                    using (var stream = File.OpenRead(sourcePath))
                    using (var sha = SHA256.Create()) actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(actualHash, (string)entry["source"]["sha256"], StringComparison.OrdinalIgnoreCase)) errors.Add("source SHA-256 mismatch");
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
                { errors.Add(ex.Message); }
                foreach (JObject coverage in (JArray)entry["coverage"])
                {
                    if (string.IsNullOrWhiteSpace((string)coverage["drawing_ref"])) errors.Add("drawing_ref must name the drawing, sheet or region");
                    foreach (JToken reference in (JArray)coverage["model_element_ids"])
                    {
                        if (!long.TryParse(reference.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id))
                            errors.Add("model element id is outside the supported Int64 range: " + reference);
                        else if (!knownIds.Contains(id)) errors.Add("covered model element was not read in this audit: " + id);
                    }
                }
                if (string.IsNullOrWhiteSpace((string)entry["summary"])) errors.Add("summary must explain the declared result");
                if (errors.Count > 0)
                {
                    states[key]["reason"] = "declared evidence rejected: " + string.Join("; ", errors.Values<string>());
                    states[key]["evidence_errors"] = errors; continue;
                }
                states[key] = new JObject
                {
                    ["status"] = "executed_with_declared_evidence", ["reason"] = "source bytes, schema and model references validated; drawing comparison and result are externally declared, not host measured",
                    ["measured"] = false, ["host_verified"] = false, ["declared_passed"] = (string)entry["result"] == "passed",
                    ["declared_evidence"] = entry.DeepClone(), ["manifest_path"] = path, ["manifest_sha256"] = manifestHash, ["source_sha256_verified"] = actualHash
                };
                if ((string)entry["result"] == "failed")
                    findings.Add(new JObject
                    {
                        ["check"] = check.Id, ["severity"] = (bool?)check.Check["blocking"] == false || (string)check.Check["severity"] == "optional" || (string)check.Check["severity"] == "information" ? "correction" : "blocking",
                        ["count"] = 1, ["what"] = "Externally declared drawing check failed: " + (string)entry["summary"],
                        ["basis"] = "declared_evidence", ["host_verified"] = false, ["fix"] = "review the declared source and drawing-to-model coverage"
                    });
            }
            return states;
        }

        private static string EvidenceHash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
