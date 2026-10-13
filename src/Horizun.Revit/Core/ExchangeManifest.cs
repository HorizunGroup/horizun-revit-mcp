// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free and net48-safe.
//
// horizun.exchange/0 - the manifest every cross-app hop leaves behind (3.0.0
// interoperability programme). It WRAPS a format that already exists (the
// coordination ledger CSV/JSON, BCF 2.1, the NavisCoord hand-off, quantity rows,
// MSPDI, IFC + the BCF of IDS failures) without replacing it.
//
// WHERE IT LIVES:  <exchange_root>/<kind>/<exchange_id>/exchange.hzx.json, beside a
// COPY of the payload. Never a fixed sidecar next to a file that changes (.rvt,
// .nwf, .pbip).
//
// ORDER:  payload first - each file copied under a temporary name, moved into place
// WITHOUT overwriting, and re-hashed - and the manifest LAST, the same way. A folder
// with payload and no manifest is "not delivered yet", and so is a manifest whose
// payload has not arrived (a synced folder can deliver them in either order): never
// an error, never consumed.
//
// HONESTY:  verification.state speaks the bridge's vocabulary - verified,
// self_reported_verified, partial, failed. `verified` requires host_verified=true:
// only a producer that re-read its own output in the host may claim it. A hop that
// went through Python, or a wrap of somebody else's files, is at most
// self_reported_verified, and a chain is as strong as its weakest hop.
//
// The VERIFIER is the consumer's half: delivery, sha256 of every file, the
// producer's declared state capped by what it can claim, and - given the project
// registry - the source model's identity (ModelIdentity, decision D0).
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
    public static class ExchangeManifest
    {
        public const string Schema = "horizun.exchange/0";
        public const string ManifestFileName = "exchange.hzx.json";

        public static readonly string[] Kinds =
        {
            "coordination.ledger", "coordination.status", "quantities.rows", "schedule.element_4d",
            "model.element_lineage", "receipt"
        };

        /// <summary>The honesty vocabulary, strongest first.</summary>
        public static readonly string[] States = { "verified", "self_reported_verified", "partial", "failed" };

        public const string Verified = "verified";
        public const string SelfReported = "self_reported_verified";
        public const string Partial = "partial";
        public const string Failed = "failed";

        /// <summary>Delivery outcomes of the verifier.</summary>
        public const string Delivered = "delivered";
        public const string NotDelivered = "not_delivered";

        private static readonly Regex ExchangeIdShape = new Regex(@"^hzx-\d{8}T\d{6}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant);
        private static readonly Regex SafeFileName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$", RegexOptions.CultureInvariant);

        public static int Rank(string state)
        {
            int i = Array.IndexOf(States, state);
            return i < 0 ? States.Length : i;
        }

        /// <summary>The weakest state in a chain; unknown words count as failed.</summary>
        public static string Weakest(IEnumerable<string> states)
        {
            string worst = Verified;
            foreach (string s in states ?? Enumerable.Empty<string>())
            {
                string v = Array.IndexOf(States, s) < 0 ? Failed : s;
                if (Rank(v) > Rank(worst)) worst = v;
            }
            return worst;
        }

        /// <summary>
        /// What a producer may claim. `verified` without host_verified=true is capped to
        /// self_reported_verified - the claim is kept, its strength is not.
        /// </summary>
        public static string Effective(string declared, bool hostVerified)
        {
            if (Array.IndexOf(States, declared) < 0) return Failed;
            if (declared == Verified && !hostVerified) return SelfReported;
            return declared;
        }

        public static string NewExchangeId(DateTime utc, string hex)
        {
            string h = (hex ?? Guid.NewGuid().ToString("N")).Substring(0, 8).ToLowerInvariant();
            return "hzx-" + utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + h;
        }

        /// <summary>
        /// The LOCAL default root (conservative decision taken for the owner, 2026-10-12):
        /// %USERPROFILE%\.horizun\exchange\&lt;project code&gt;. A synced CDE folder is a
        /// separate, explicit publication step - never the place a hop writes to.
        /// </summary>
        public static string DefaultRoot(string userProfile, string projectCode)
            => DefaultRootUnder(Path.Combine(userProfile, ".horizun"), projectCode);

        /// <summary>The same default under Horizun's data root (HorizunPaths.DataRoot(), which honours its override).</summary>
        public static string DefaultRootUnder(string dataRoot, string projectCode)
        {
            string code = string.IsNullOrWhiteSpace(projectCode) ? "_unassigned" : Regex.Replace(projectCode.Trim(), @"[^A-Za-z0-9._-]", "_");
            if (code == "." || code == "..") code = "_unassigned";
            return Path.Combine(dataRoot, "exchange", code);
        }

        public sealed class PayloadFile
        {
            public string SourcePath;
            public string Name;      // file name inside the exchange folder; default: the source's
            public string Format;    // e.g. horizun.coordination-ledger.csv/2
            public long? Rows;
        }

        public sealed class Request
        {
            public string Kind;
            public string ProjectCode;
            public string ProducerApp;
            public string ProducerVersion;
            public string ProducerCommit;
            public string ModelRef;
            public JObject ModelEvidence;
            public JObject SpatialRef;
            public List<PayloadFile> Files = new List<PayloadFile>();
            public string State;
            public string Method;
            public bool HostVerified;
            public List<string> Caveats = new List<string>();
            public string CorrelationId;
        }

        /// <summary>Everything wrong with a request, before a byte is written. Empty means it may be emitted.</summary>
        public static List<string> Check(Request r)
        {
            var errors = new List<string>();
            if (r == null) { errors.Add("the request is empty."); return errors; }
            if (Array.IndexOf(Kinds, r.Kind) < 0) errors.Add("kind must be one of " + string.Join(", ", Kinds) + ".");
            if (string.IsNullOrWhiteSpace(r.ProducerApp)) errors.Add("producer app is required.");
            if (Array.IndexOf(States, r.State) < 0) errors.Add("verification state must be one of " + string.Join(", ", States) + ".");
            if (r.State == Verified && !r.HostVerified)
                errors.Add("verified needs host_verified=true: only a producer that re-read its own output in the host may claim it. " +
                           "Send self_reported_verified.");
            if (string.IsNullOrWhiteSpace(r.Method)) errors.Add("verification method is required (how the state was established).");
            if (r.Files == null || r.Files.Count == 0) errors.Add("at least one payload file is required.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; r.Files != null && i < r.Files.Count; i++)
            {
                PayloadFile f = r.Files[i];
                if (f == null || string.IsNullOrWhiteSpace(f.SourcePath)) { errors.Add("files[" + i + "]: path is required."); continue; }
                if (!Path.IsPathRooted(f.SourcePath)) errors.Add("files[" + i + "]: '" + f.SourcePath + "' must be absolute.");
                else if (!File.Exists(f.SourcePath)) errors.Add("files[" + i + "]: '" + f.SourcePath + "' does not exist.");
                string name = f.Name ?? Path.GetFileName(f.SourcePath);
                if (!SafeFileName.IsMatch(name ?? "") || name.Contains("..") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    errors.Add("files[" + i + "]: '" + name + "' is not a plain file name the exchange folder can carry.");
                else if (string.Equals(name, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    errors.Add("files[" + i + "]: '" + name + "' is the manifest's own name.");
                else if (!names.Add(name)) errors.Add("files[" + i + "]: '" + name + "' appears twice.");
                if (string.IsNullOrWhiteSpace(f.Format)) errors.Add("files[" + i + "]: format is required (the wrapped format's name and version).");
                if (f.Rows.HasValue && f.Rows.Value < 0) errors.Add("files[" + i + "]: rows cannot be negative.");
            }
            if (r.ModelRef != null && !r.ModelRef.StartsWith("mdl-", StringComparison.Ordinal))
                errors.Add("model_ref must be a registry id 'mdl-...'.");
            return errors;
        }

        /// <summary>The manifest that WOULD be written - hashes measured on the sources, nothing written.</summary>
        public static JObject Build(Request r, string exchangeId, Func<string, string> sha256OfFile)
        {
            var files = new JArray();
            foreach (PayloadFile f in r.Files)
            {
                var row = new JObject
                {
                    ["path"] = f.Name ?? Path.GetFileName(f.SourcePath),
                    ["format"] = f.Format,
                    ["sha256"] = sha256OfFile(f.SourcePath),
                    ["bytes"] = new FileInfo(f.SourcePath).Length
                };
                if (f.Rows.HasValue) row["rows"] = f.Rows.Value;
                files.Add(row);
            }
            var caveats = new JArray((r.Caveats ?? new List<string>()).Where(c => !string.IsNullOrWhiteSpace(c)));
            return new JObject
            {
                ["schema"] = Schema,
                ["exchange_id"] = exchangeId,
                ["kind"] = r.Kind,
                ["correlation_id"] = r.CorrelationId,
                ["producer"] = new JObject { ["app"] = r.ProducerApp, ["version"] = r.ProducerVersion, ["commit"] = r.ProducerCommit },
                ["project"] = new JObject { ["code"] = r.ProjectCode },
                ["source"] = new JObject
                {
                    ["model_ref"] = r.ModelRef,
                    ["model_evidence"] = r.ModelEvidence != null ? r.ModelEvidence.DeepClone() : JValue.CreateNull()
                },
                ["spatial_ref"] = r.SpatialRef != null ? r.SpatialRef.DeepClone() : JValue.CreateNull(),
                ["files"] = files,
                ["verification"] = new JObject { ["state"] = r.State, ["method"] = r.Method, ["host_verified"] = r.HostVerified },
                ["caveats"] = caveats
            };
        }

        /// <summary>
        /// Emit: payload first, manifest last, each re-hashed after it lands. Returns the
        /// written manifest and its own verification. Throws InvalidOperationException with
        /// the exact state when a step cannot be proved; the folder is then left WITHOUT a
        /// manifest, which every consumer reads as not delivered.
        /// </summary>
        public static JObject Emit(Request r, string root, string exchangeId)
        {
            List<string> errors = Check(r);
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
            if (!ExchangeIdShape.IsMatch(exchangeId ?? "")) throw new ArgumentException("exchange_id '" + exchangeId + "' is not hzx-yyyyMMddTHHmmssZ-xxxxxxxx.");
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) throw new ArgumentException("the exchange root must be an absolute path.");

            string dir = Path.Combine(root, r.Kind, exchangeId);
            if (Directory.Exists(dir)) throw new InvalidOperationException("'" + dir + "' already exists; an exchange is never written twice.");
            Directory.CreateDirectory(dir);

            // PAYLOAD FIRST.
            var measured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PayloadFile f in r.Files)
            {
                string name = f.Name ?? Path.GetFileName(f.SourcePath);
                string target = Path.Combine(dir, name);
                string before = Sha256File(f.SourcePath);
                string temporary = AtomicFile.TemporaryFor(target);
                try
                {
                    File.Copy(f.SourcePath, temporary, false);
                    File.Move(temporary, target);      // never overwrites
                }
                finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
                string landed = Sha256File(target);
                string after = Sha256File(f.SourcePath);
                if (landed != before || after != before)
                    throw new InvalidOperationException("'" + f.SourcePath + "' changed while it was copied (sha256 before " + before +
                        ", copy " + landed + ", source after " + after + "). The exchange '" + dir + "' has no manifest and reads as not delivered.");
                measured[f.SourcePath] = landed;
            }

            // MANIFEST LAST.
            JObject manifest = Build(r, exchangeId, p => measured[p]);
            string manifestPath = Path.Combine(dir, ManifestFileName);
            byte[] bytes = Serialize(manifest);
            string tmp = AtomicFile.TemporaryFor(manifestPath);
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, manifestPath);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }

            JObject check = Verify(manifestPath, null);
            if ((string)check["delivery"] != Delivered || (string)check["integrity"] != "ok")
                throw new InvalidOperationException("The manifest was written to '" + manifestPath + "' but reading the exchange back does not " +
                    "verify (delivery " + check["delivery"] + ", integrity " + check["integrity"] + "). Treat it as not delivered.");
            return new JObject
            {
                ["exchange_id"] = exchangeId,
                ["directory"] = dir,
                ["manifest_path"] = manifestPath,
                ["manifest"] = manifest,
                ["reread"] = check
            };
        }

        /// <summary>
        /// The consumer's check. <paramref name="registryModels"/> (the project registry's
        /// models) adds the identity verdict; without it identity is not_checked.
        /// </summary>
        public static JObject Verify(string manifestOrFolder, IEnumerable<JObject> registryModels)
        {
            string manifestPath = manifestOrFolder;
            if (Directory.Exists(manifestOrFolder)) manifestPath = Path.Combine(manifestOrFolder, ManifestFileName);
            string dir = Path.GetDirectoryName(manifestPath);
            var reasons = new JArray();
            var result = new JObject
            {
                ["manifest_path"] = manifestPath,
                ["delivery"] = null,
                ["integrity"] = "not_checked",
                ["producer_state"] = null,
                ["effective_state"] = null,
                ["identity"] = new JObject { ["state"] = "not_checked" },
                ["may_continue"] = false,
                ["reasons"] = reasons
            };

            if (!File.Exists(manifestPath))
            {
                bool payloadThere = Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any();
                result["delivery"] = NotDelivered;
                reasons.Add(payloadThere
                    ? "The folder holds payload but no manifest: the producer has not finished (or failed before the manifest). Not delivered - do not consume it."
                    : "No manifest at '" + manifestPath + "'.");
                return result;
            }

            JObject m;
            try { m = JObject.Parse(File.ReadAllText(manifestPath, new UTF8Encoding(false))); }
            catch (Exception ex)
            {
                result["delivery"] = Delivered;
                result["integrity"] = "invalid";
                result["effective_state"] = Failed;
                reasons.Add("The manifest does not parse as a JSON object: " + ex.Message);
                return result;
            }

            var shape = Shape(m);
            if (shape.Count > 0)
            {
                result["delivery"] = Delivered;
                result["integrity"] = "invalid";
                result["effective_state"] = Failed;
                foreach (string s in shape) reasons.Add(s);
                return result;
            }
            result["exchange_id"] = (string)m["exchange_id"];
            result["kind"] = (string)m["kind"];

            var files = new JArray();
            bool missing = false, mismatch = false;
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestFileName };
            foreach (JObject f in ((JArray)m["files"]).OfType<JObject>())
            {
                string name = (string)f["path"];
                listed.Add(name);
                string path = Path.Combine(dir, name);
                var row = new JObject { ["path"] = name, ["sha256_declared"] = (string)f["sha256"] };
                if (!File.Exists(path)) { row["state"] = "missing"; missing = true; }
                else
                {
                    string got = Sha256File(path);
                    row["sha256_measured"] = got;
                    row["bytes"] = new FileInfo(path).Length;
                    bool ok = string.Equals(got, (string)f["sha256"], StringComparison.OrdinalIgnoreCase);
                    row["state"] = ok ? "ok" : "sha256_mismatch";
                    if (!ok) mismatch = true;
                }
                if (f["rows"] != null) row["rows_declared"] = f["rows"];
                files.Add(row);
            }
            result["files"] = files;
            var extra = Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(n => !listed.Contains(n)).ToList();
            if (extra.Count > 0) result["unlisted_files"] = new JArray(extra);

            JObject v = (JObject)m["verification"];
            string declared = (string)v["state"];
            bool host = (bool?)v["host_verified"] == true;
            result["producer_state"] = declared;

            if (missing)
            {
                result["delivery"] = NotDelivered;
                reasons.Add("The manifest names payload that has not arrived (a synced folder can deliver the manifest first). Not delivered - wait, do not consume it.");
                return result;
            }
            result["delivery"] = Delivered;
            if (mismatch)
            {
                result["integrity"] = "sha256_mismatch";
                result["effective_state"] = Failed;
                reasons.Add("At least one payload file does not match its sha256: it changed after the manifest was written, or was replaced.");
                return result;
            }
            result["integrity"] = "ok";
            string effective = Effective(declared, host);
            if (effective != declared)
                reasons.Add("The producer declared " + declared + " without host_verified=true; read as " + effective + ".");
            result["effective_state"] = effective;

            JObject identity = Identity(m, registryModels);
            result["identity"] = identity;
            bool identityOk = (string)identity["state"] == "not_declared" ||
                ((string)identity["state"] == "proven" || (string)identity["state"] == "unproven") && identity["matched_by"] != null;
            bool strongEnough = effective == Verified || effective == SelfReported;
            if (!strongEnough) reasons.Add("The producer's state is " + effective + ": a downstream hop does not run on it.");
            if (!identityOk) reasons.Add("The source model's identity does not agree with the project registry.");
            result["may_continue"] = strongEnough && identityOk;
            return result;
        }

        private static JObject Identity(JObject m, IEnumerable<JObject> registryModels)
        {
            string modelRef = (string)m["source"]?["model_ref"];
            JObject evidence = m["source"]?["model_evidence"] as JObject;
            if (modelRef == null) return new JObject { ["state"] = "not_declared", ["note"] = "The manifest names no source model." };
            if (registryModels == null) return new JObject { ["state"] = "not_checked", ["model_ref"] = modelRef, ["note"] = "No project registry was given." };
            JObject registered = registryModels.FirstOrDefault(x => (string)x["model_ref"] == modelRef);
            if (registered == null)
                return new JObject { ["state"] = "unknown_model_ref", ["model_ref"] = modelRef, ["note"] = "The project registry has no " + modelRef + "." };
            if (evidence == null)
                return new JObject { ["state"] = "unproven", ["model_ref"] = modelRef, ["note"] = "The manifest names the model but carries no evidence to check it against." };
            List<string> problems;
            JObject observed = ModelIdentity.Normalize(evidence, out problems);
            if (observed == null)
                return new JObject { ["state"] = "unproven", ["model_ref"] = modelRef, ["note"] = string.Join(" ", problems) };
            JObject match = ModelIdentity.Match(new[] { registered }, observed);
            string state = (string)match["state"];
            if (state == ModelIdentity.Matched)
                return new JObject
                {
                    ["state"] = (bool)match["identity_proven"] ? "proven" : "unproven",
                    ["model_ref"] = modelRef, ["matched_by"] = match["matched_by"], ["warnings"] = match["warnings"]
                };
            if (state == ModelIdentity.NoStrongEvidence)
                return new JObject { ["state"] = "unproven", ["model_ref"] = modelRef, ["note"] = (string)match["reason"] };
            return new JObject
            {
                ["state"] = "mismatch", ["model_ref"] = modelRef,
                ["note"] = "The evidence in the manifest is not the registered model's evidence (" + match["reason"] + ")."
            };
        }

        /// <summary>The manifest's shape. Empty means every required field is there and well-formed.</summary>
        public static List<string> Shape(JObject m)
        {
            var e = new List<string>();
            if ((string)m["schema"] != Schema) e.Add("schema must be '" + Schema + "'.");
            if (!ExchangeIdShape.IsMatch((string)m["exchange_id"] ?? "")) e.Add("exchange_id is not hzx-yyyyMMddTHHmmssZ-xxxxxxxx.");
            if (Array.IndexOf(Kinds, (string)m["kind"]) < 0) e.Add("kind '" + m["kind"] + "' is not a known kind.");
            if (!(m["producer"] is JObject) || string.IsNullOrWhiteSpace((string)m["producer"]["app"])) e.Add("producer.app is required.");
            if (!(m["source"] is JObject)) e.Add("source is required (model_ref may be null).");
            var files = m["files"] as JArray;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (files == null || files.Count == 0) e.Add("files must list at least one payload file.");
            else
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i] as JObject;
                    string name = (string)f?["path"];
                    if (f == null || name == null || !SafeFileName.IsMatch(name) || name.Contains(".."))
                        e.Add("files[" + i + "].path must be a plain file name inside the exchange folder.");
                    if (name != null && (!names.Add(name) || name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                        e.Add("files[" + i + "].path is duplicate or reserved.");
                    if (f != null && !Regex.IsMatch((string)f["sha256"] ?? "", "^[0-9a-fA-F]{64}$")) e.Add("files[" + i + "].sha256 is not a sha256.");
                    if (f != null && string.IsNullOrWhiteSpace((string)f["format"])) e.Add("files[" + i + "].format is required.");
                }
            var v = m["verification"] as JObject;
            if (v == null) e.Add("verification is required.");
            else if (Array.IndexOf(States, (string)v["state"]) < 0) e.Add("verification.state '" + v["state"] + "' is not in the vocabulary (" + string.Join(", ", States) + ").");
            if (v != null && (v["host_verified"]?.Type != JTokenType.Boolean)) e.Add("verification.host_verified must be boolean.");
            if (v != null && (v["method"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)v["method"]))) e.Add("verification.method is required.");
            if (m["caveats"] != null && !(m["caveats"] is JArray)) e.Add("caveats must be an array of sentences.");
            return e;
        }

        public static byte[] Serialize(JObject manifest)
            => new UTF8Encoding(false).GetBytes(manifest.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n");

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }
    }
}
