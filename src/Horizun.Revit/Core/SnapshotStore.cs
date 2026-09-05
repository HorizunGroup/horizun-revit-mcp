// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHERE A SNAPSHOT LIVES, and what must be true of it before anyone trusts it.
//
// A snapshot is read weeks later by somebody comparing today against a run
// nobody remembers. Everything here exists because of that gap in time:
//
//   ATOMIC WRITE       a crash mid-write must not leave a file that parses.
//                      Written to a temporary and swapped into place.
//   HASH VALIDATION    the content carries a SHA-256 of itself. A file whose
//                      hash does not match is REFUSED, not repaired - a
//                      trend built on a half-written snapshot is worse than
//                      no trend.
//   PARTIAL DETECTION  a file that does not parse, or parses without its
//                      envelope, is reported as partial rather than as an
//                      empty run.
//   SANITISATION       no usernames, no tokens, no personal paths. A snapshot
//                      is the most likely thing in this system to be mailed
//                      to somebody, and a central model path is somebody
//                      else's network.
//   HORIZUN PATH       snapshots go under Horizun's own data root and NEVER
//                      beside the .rvt by default: writing next to a central
//                      model puts a file into somebody's project directory,
//                      and onto their backup, without being asked.
//
// Revit-free, so all of it is provable at a desk.
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
    public static class SnapshotStoreCodes
    {
        public const string Ok = "ok";
        public const string NotFound = "not_found";
        public const string Partial = "partial";
        public const string HashMismatch = "hash_mismatch";
        public const string WrongSchema = "wrong_schema";
        public const string Unreadable = "unreadable";
        public const string RefusedPath = "refused_path";
    }

    public sealed class SnapshotWriteResult
    {
        public bool Ok;
        public string Code;
        public string Message;
        public string Path;
        public string Sha256;
        public int RedactedValues;
    }

    public sealed class SnapshotReadResult
    {
        public bool Ok;
        public string Code;
        public string Message;
        public JObject Content;
        public string Sha256;
    }

    public static class SnapshotStore
    {
        public const string EnvelopeSchema = "horizun.diagnostics-snapshot-envelope/1";

        /// <summary>
        /// Values that must never reach a stored snapshot. A snapshot is the most
        /// likely artefact here to be attached to an email.
        /// </summary>
        private static readonly Regex[] Personal =
        {
            // Windows and Unix user-profile paths - the username is IN the path.
            new Regex(@"[A-Za-z]:[\\/]Users[\\/][^\\/""]+", RegexOptions.IgnoreCase),
            new Regex(@"/home/[^/""]+", RegexOptions.IgnoreCase),
            // A UNC share is somebody else's network, and the server name identifies it.
            new Regex(@"\\\\[^\\""]+\\[^\\""]+"),
            // Anything that announces itself as a secret.
            new Regex(@"(?i)\b(?:bearer|token|password|secret|api[_-]?key)\b\s*[:=]?\s*\S+")
        };

        public const string SanitisationMeans =
            "usernames, tokens, UNC shares and personal paths are redacted before a snapshot is written, not " +
            "when it is read. A snapshot is the artefact most likely to be mailed to somebody, and a central " +
            "model path is somebody else's network.";

        public const string LocationMeans =
            "snapshots are written under Horizun's own data root and NEVER beside the .rvt by default: a file " +
            "written next to a central model lands in somebody's project directory, and on their backup, " +
            "without anyone asking for it.";

        public static string DirectoryUnder(string dataRoot)
        {
            if (string.IsNullOrWhiteSpace(dataRoot)) return null;
            return Path.Combine(dataRoot, "snapshots");
        }

        /// <summary>
        /// Redacts personal material in place and returns how many values changed.
        /// Reported rather than silent: a caller comparing two snapshots deserves
        /// to know that something was removed from one of them.
        /// </summary>
        public static int Sanitise(JToken token)
        {
            int redacted = 0;
            if (token == null) return 0;

            if (token.Type == JTokenType.String)
            {
                string before = token.Value<string>() ?? "";
                string after = before;
                foreach (Regex r in Personal) after = r.Replace(after, "<redacted>");
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    ((JValue)token).Value = after;
                    redacted++;
                }
                return redacted;
            }

            if (token is JObject o)
            {
                foreach (JProperty p in o.Properties().ToList()) redacted += Sanitise(p.Value);
                return redacted;
            }
            if (token is JArray a)
            {
                foreach (JToken t in a.ToList()) redacted += Sanitise(t);
                return redacted;
            }
            return 0;
        }

        /// <summary>Canonical JSON: sorted keys, no whitespace, so the hash is stable.</summary>
        public static string Canonical(JToken token)
        {
            if (token == null) return "null";
            if (token is JObject o)
            {
                var sb = new StringBuilder("{");
                bool first = true;
                foreach (JProperty p in o.Properties().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonConvert.ToString(p.Name)).Append(':').Append(Canonical(p.Value));
                }
                return sb.Append('}').ToString();
            }
            if (token is JArray a)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Canonical(a[i]));
                }
                return sb.Append(']').ToString();
            }
            return token.ToString(Formatting.None);
        }

        public static string Sha256Of(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Wraps content in the envelope that makes it verifiable: schema, the
        /// hash of the content, and nothing else. The hash covers the CONTENT
        /// only, so the envelope can gain fields without invalidating old files.
        /// </summary>
        public static JObject Envelope(JObject content, out string sha)
        {
            sha = Sha256Of(Canonical(content));
            return new JObject
            {
                ["schema"] = EnvelopeSchema,
                ["sha256"] = sha,
                ["content"] = content
            };
        }

        public static SnapshotWriteResult Write(string directory, string fileName, JObject content,
                                                Func<string, string, bool> writeFile)
        {
            var r = new SnapshotWriteResult();
            if (string.IsNullOrWhiteSpace(directory))
            {
                r.Code = SnapshotStoreCodes.RefusedPath;
                r.Message = "no snapshot directory was resolved. " + LocationMeans;
                return r;
            }
            if (content == null)
            {
                r.Code = SnapshotStoreCodes.Unreadable;
                r.Message = "there is nothing to write.";
                return r;
            }

            r.RedactedValues = Sanitise(content);
            string sha;
            JObject envelope = Envelope(content, out sha);
            r.Sha256 = sha;
            r.Path = Path.Combine(directory, fileName);

            // The caller performs the ATOMIC swap; this decides what goes in it, so
            // the decision is testable without a disk.
            if (writeFile != null && !writeFile(r.Path, envelope.ToString(Formatting.Indented)))
            {
                r.Code = SnapshotStoreCodes.Unreadable;
                r.Message = "the snapshot could not be written to " + r.Path + ".";
                return r;
            }

            r.Ok = true;
            r.Code = SnapshotStoreCodes.Ok;
            r.Message = "written with its content hash. " + SanitisationMeans;
            return r;
        }

        /// <summary>
        /// Reads and VERIFIES. A file whose hash does not match its content is
        /// refused rather than repaired: a trend built on a half-written snapshot
        /// is worse than no trend, because it looks like evidence.
        /// </summary>
        public static SnapshotReadResult Read(string text)
        {
            var r = new SnapshotReadResult();
            if (text == null)
            {
                r.Code = SnapshotStoreCodes.NotFound;
                r.Message = "there is no snapshot at that path.";
                return r;
            }

            JObject envelope;
            try { envelope = JObject.Parse(text); }
            catch (Exception ex)
            {
                r.Code = SnapshotStoreCodes.Partial;
                r.Message = "this file does not parse (" + ex.Message + "), which usually means a write was " +
                            "interrupted. It is NOT an empty run.";
                return r;
            }

            if (envelope["content"] == null || envelope["sha256"] == null)
            {
                r.Code = SnapshotStoreCodes.Partial;
                r.Message = "this file parses but carries no content and hash, so it cannot be verified. It is " +
                            "NOT an empty run.";
                return r;
            }

            string schema = envelope.Value<string>("schema");
            if (!string.Equals(schema, EnvelopeSchema, StringComparison.Ordinal))
            {
                r.Code = SnapshotStoreCodes.WrongSchema;
                r.Message = "this snapshot was written by envelope schema '" + (schema ?? "(none)") +
                            "' and this build reads '" + EnvelopeSchema + "'.";
                return r;
            }

            var content = envelope["content"] as JObject;
            string stored = envelope.Value<string>("sha256");
            string actual = Sha256Of(Canonical(content));
            if (!string.Equals(stored, actual, StringComparison.OrdinalIgnoreCase))
            {
                r.Code = SnapshotStoreCodes.HashMismatch;
                r.Message = "the stored hash does not match the content. The file has been changed or was " +
                            "written incompletely; it is refused rather than repaired.";
                return r;
            }

            r.Ok = true;
            r.Code = SnapshotStoreCodes.Ok;
            r.Content = content;
            r.Sha256 = actual;
            return r;
        }
    }
}
