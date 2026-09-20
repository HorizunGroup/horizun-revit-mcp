// -----------------------------------------------------------------------------
// Horizun MCP server - what this answer was produced BY. Original Horizun code.
//
// G04 of the competitive inventory: "informe ata commit, hashes de archivos y
// binarios; ninguna prueba histórica se atribuye al build nuevo".
//
// A version number does not identify a build. Two binaries built from the same
// HEAD with different uncommitted changes carry the same version and the same
// commit, and the benchmark that produced this gap was itself run against a
// working tree whose local changes were never committed. The only thing that
// identifies a build is the bytes, so this reports the bytes: the informational
// version, the commit the compiler stamped, whether the tree was dirty when it
// was stamped, the hash of the shared command contract, and the SHA-256 of the
// server assembly that is answering right now.
//
// WHAT IS NOT HERE. The add-in's own hash. That file lives in another process
// and is reported by horizun_health, which asks the add-in; guessing it from a
// deployment path is exactly the mistake Build.Assembly_ exists to have stopped
// making. A field that could not be read stays null and is never a zero.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal static class ProvenanceStamp
    {
        private static JObject _cached;
        private static readonly object Gate = new object();

        /// <summary>
        /// The provenance of the running server, computed once. The assembly cannot
        /// change under a running process, so re-hashing it per request would be work
        /// with no possible new answer.
        /// </summary>
        public static JObject Current()
        {
            if (_cached != null) return (JObject)_cached.DeepClone();
            lock (Gate)
            {
                if (_cached == null) _cached = Compute();
            }
            return (JObject)_cached.DeepClone();
        }

        /// <summary>The short form that rides in every modern result's `_meta`.</summary>
        public static JObject Compact()
        {
            JObject full = Current();
            return new JObject
            {
                ["version"] = full["version"],
                ["commit"] = full["commit"],
                ["built_from_clean_tree"] = full["built_from_clean_tree"],
                ["contract_hash"] = full["contract_hash"]
            };
        }

        private static JObject Compute()
        {
            string informational = null;
            string version = "unknown";
            string commit = "unknown";
            string path = null;

            try
            {
                Assembly asm = typeof(ProvenanceStamp).Assembly;
                var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(AssemblyInformationalVersionAttribute));
                informational = attr != null ? attr.InformationalVersion : null;
                if (string.IsNullOrEmpty(informational) && asm.GetName().Version != null)
                    informational = asm.GetName().Version.ToString();

                if (!string.IsNullOrEmpty(informational))
                {
                    int plus = informational.IndexOf('+');
                    version = plus > 0 ? informational.Substring(0, plus) : informational;
                    if (plus > 0 && plus < informational.Length - 1) commit = informational.Substring(plus + 1);
                }

                try { path = asm.Location; } catch { path = null; }
            }
            catch { /* every field below stays at its unknown/null default */ }

            // "unknown" is not clean. A build whose provenance could not be read must
            // never be reported as one that came from a committed tree.
            bool clean = commit != "unknown" && !commit.EndsWith("-dirty", StringComparison.Ordinal);

            var stamp = new JObject
            {
                ["server_name"] = "horizun-mcp",
                ["version"] = version,
                ["informational_version"] = informational == null ? (JToken)JValue.CreateNull() : informational,
                ["commit"] = commit,
                ["built_from_clean_tree"] = clean,
                ["contract_hash"] = Horizun.Contracts.Contract.Hash,
                ["bridge_protocol_version"] = Horizun.Contracts.Contract.ProtocolVersion,
                ["assembly"] = AssemblyFacts(path),
                ["means"] =
                    "the bytes that produced this answer. built_from_clean_tree=false means the commit names a " +
                    "tree this binary is NOT exactly, so results measured against it must be attributed to the " +
                    "assembly sha256 rather than to the commit. A null field could not be read and is not a zero."
            };
            return stamp;
        }

        private static JObject AssemblyFacts(string path)
        {
            var o = new JObject
            {
                ["path"] = path == null ? (JToken)JValue.CreateNull() : path,
                ["sha256"] = JValue.CreateNull(),
                ["bytes"] = JValue.CreateNull(),
                ["written_utc"] = JValue.CreateNull()
            };
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return o;
                var info = new FileInfo(path);
                o["bytes"] = info.Length;
                o["written_utc"] = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                using (var sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(fs);
                    var sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    o["sha256"] = sb.ToString();
                }
            }
            catch { /* unreadable stays null */ }
            return o;
        }
    }
}
