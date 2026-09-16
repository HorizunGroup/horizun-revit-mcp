// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// READING A DRAWING COSTS SIX MINUTES. INTERPRETING IT COSTS NOTHING.
//
// MEASURED on a real electrical permit drawing: 343.8 s of headless AutoCAD per
// read, and one campaign session spent six of them on the SAME file, because
// changing a rule or a zone meant running the whole route again. The rules and
// the zone are the things a person iterates on; the file is the thing that does
// not move.
//
// So the two halves are separated. Extraction produces the reader's raw report
// and that is what is cached. Interpretation parses it and applies the rules,
// every time, from scratch - so a changed rule, a changed zone, a changed
// tolerance is always honoured and never served from a cache.
//
// WHAT THE KEY IS MADE OF, and why each part is in it:
//
//   the drawing's SHA-256        a new issue of the file is a different drawing
//   every external reference     an xref is part of what was read; a changed
//                                one changes the reading with the host file
//                                untouched
//   the engine's version         a different AutoCAD reads differently
//   the extractor's own text     the .lsp is the reader; editing it invalidates
//                                every reading made by the old one
//   the extraction options       what was asked for, not what was done with it
//
// A cache that cannot say why it hit is a cache nobody should trust, so every
// hit reports the key it matched and the parts that made it.
//
// WHAT IS NOT IN THE KEY, deliberately: the requirement set, the zone, the
// tolerances, the level, the target document. None of them change what the
// reader saw, and putting them in would defeat the whole point.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a cache lookup found, or why it did not.</summary>
    public sealed class CadDwgCacheEntry
    {
        public string Key;
        public string TsvPath;
        public string SidecarPath;
        public bool Hit;
        public string Miss;                 // why, when Hit is false
        public JObject Detail;
    }

    /// <summary>
    /// The extraction cache: raw reader output, addressed by what produced it.
    /// </summary>
    public static class CadDwgCache
    {
        /// <summary>Where readings live. One directory, one file per key.</summary>
        public static string Root =>
            Environment.GetEnvironmentVariable("HORIZUN_DWG_CACHE") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".horizun", "dwg-cache");

        /// <summary>SHA-256 of a file, or null when it cannot be read.</summary>
        public static string Sha256(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (FileStream f = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)))
                                   .Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// The extractor's own text, hashed. This is the READER: if it changes,
        /// every reading it made describes a drawing through different eyes.
        /// </summary>
        public static string ReaderFingerprint()
        {
            var sb = new StringBuilder();
            foreach (string form in CadDwgScript.Forms) sb.Append(form).Append('\n');
            return Hash(sb.ToString()).Substring(0, 16);
        }

        /// <summary>
        /// The key for one extraction. Dependencies are the xref paths a PREVIOUS
        /// reading of this file found; on a first read there are none to know
        /// about, which is why they are checked on the hit rather than the miss.
        /// </summary>
        public static string KeyFor(string dwgSha, string engineVersion, string options)
        {
            return Hash(string.Join("|", new[]
            {
                "cad-dwg-extract/2",
                dwgSha ?? "(no-sha)",
                engineVersion ?? "(no-engine)",
                ReaderFingerprint(),
                options ?? ""
            })).Substring(0, 32);
        }

        /// <summary>
        /// Look for a reading. A hit means: the file is the same file, the reader
        /// is the same reader, the options are the same options, AND every
        /// external reference the earlier reading recorded still hashes to what
        /// it hashed then.
        /// </summary>
        public static CadDwgCacheEntry Lookup(string dwgPath, string dwgSha, string engineVersion, string options)
        {
            string key = KeyFor(dwgSha, engineVersion, options);
            var entry = new CadDwgCacheEntry
            {
                Key = key,
                TsvPath = Path.Combine(Root, key + ".tsv"),
                SidecarPath = Path.Combine(Root, key + ".json")
            };

            if (!File.Exists(entry.TsvPath) || !File.Exists(entry.SidecarPath))
            {
                entry.Miss = "not_cached";
                return entry;
            }

            JObject side;
            try { side = JObject.Parse(File.ReadAllText(entry.SidecarPath)); }
            catch (Exception ex)
            {
                entry.Miss = "sidecar_unreadable";
                entry.Detail = new JObject { ["error"] = ex.Message };
                return entry;
            }

            // THE DEPENDENCIES. A drawing that references others was read WITH
            // them, so a changed reference is a changed reading even though the
            // host file's own bytes never moved. This is the invalidation that a
            // content hash of one file cannot give.
            var changed = new JArray();
            foreach (JObject dep in (side["dependencies"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string path = (string)dep["path"];
                string was = (string)dep["sha256"];
                if (string.IsNullOrWhiteSpace(path)) continue;
                string now = File.Exists(path) ? Sha256(path) : null;
                if (!string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
                    changed.Add(new JObject
                    {
                        ["path"] = path,
                        ["was"] = was,
                        ["now"] = now ?? "(absent)",
                        ["means"] = now == null
                            ? "the reference this drawing was read with is no longer on this machine"
                            : "the reference this drawing was read with has changed"
                    });
            }
            if (changed.Count > 0)
            {
                entry.Miss = "dependency_changed";
                entry.Detail = new JObject { ["changed"] = changed };
                return entry;
            }

            entry.Hit = true;
            entry.Detail = new JObject
            {
                ["key"] = key,
                ["read_utc"] = side["read_utc"],
                ["seconds_when_first_read"] = side["seconds"],
                ["dependencies_checked"] = (side["dependencies"] as JArray ?? new JArray()).Count,
                ["means"] = "the same file, read by the same extractor through the same engine with the same " +
                            "options, and every external reference still hashes to what it did then. The RULES " +
                            "are not part of this key: interpretation runs from scratch every time."
            };
            return entry;
        }

        /// <summary>
        /// Keep a reading, with what it depended on. Failure to cache is never
        /// failure to read: the caller already has its answer.
        /// </summary>
        public static JObject Store(CadDwgCacheEntry entry, string tsvSource, CadDwgReading reading,
                                    string dwgPath, double seconds)
        {
            // WHOLE OR NOT AT ALL. A report with no end marker describes a walk
            // that stopped, and a cache would serve that stop to every later run.
            // The guard lives here rather than only at the call site, because a
            // second caller would not remember it.
            if (reading == null || !reading.Complete)
                return new JObject
                {
                    ["stored"] = false,
                    ["refused"] = "reading_is_not_complete",
                    ["means"] = "the extractor wrote no end marker, so this reading is a walk that stopped. " +
                                "It is not cached: the next run reads the file again rather than inheriting " +
                                "somebody else's interruption."
                };

            try
            {
                Directory.CreateDirectory(Root);
                File.Copy(tsvSource, entry.TsvPath, true);

                var deps = new JArray();
                string folder = null;
                try { folder = Path.GetDirectoryName(dwgPath); } catch { }
                foreach (CadIrExternalReference x in reading?.ExternalReferences ?? new List<CadIrExternalReference>())
                {
                    string resolved = ResolveReference(folder, x);
                    if (resolved == null) continue;
                    deps.Add(new JObject
                    {
                        ["name"] = x.Name,
                        ["declared"] = x.Path,
                        ["path"] = resolved,
                        ["sha256"] = Sha256(resolved)
                    });
                }

                var side = new JObject
                {
                    ["key"] = entry.Key,
                    ["drawing"] = dwgPath,
                    ["read_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["seconds"] = Math.Round(seconds, 1),
                    ["entities"] = reading?.Entities?.Count ?? 0,
                    ["dependencies"] = deps
                };
                File.WriteAllText(entry.SidecarPath, side.ToString(Formatting.Indented));
                return new JObject
                {
                    ["stored"] = true,
                    ["key"] = entry.Key,
                    ["dependencies_recorded"] = deps.Count
                };
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["stored"] = false,
                    ["error"] = ex.Message,
                    ["means"] = "the reading is still correct; only the cache write failed."
                };
            }
        }

        /// <summary>
        /// Where an external reference actually is. A DWG records the path its
        /// author had; the file is usually beside the drawing.
        /// </summary>
        private static string ResolveReference(string folder, CadIrExternalReference x)
        {
            if (x == null) return null;
            foreach (string candidate in Candidates(folder, x))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); } catch { }
            }
            return null;
        }

        private static IEnumerable<string> Candidates(string folder, CadIrExternalReference x)
        {
            if (!string.IsNullOrWhiteSpace(x.Path))
            {
                yield return x.Path;
                if (folder != null)
                {
                    string leaf = null;
                    try { leaf = Path.GetFileName(x.Path); } catch { }
                    if (leaf != null) yield return Path.Combine(folder, leaf);
                }
            }
            if (folder != null && !string.IsNullOrWhiteSpace(x.Name))
            {
                yield return Path.Combine(folder, x.Name);
                yield return Path.Combine(folder, x.Name + ".dwg");
            }
        }
    }
}
