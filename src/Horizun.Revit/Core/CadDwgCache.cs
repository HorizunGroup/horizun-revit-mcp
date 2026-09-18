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
        /// <summary>The host drawing's hash as it was taken for this key, before any extraction.</summary>
        public string HostSha;
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
                HostSha = dwgSha,
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
            // A PUBLICATION INTERRUPTED BETWEEN ITS TWO FILES leaves one reading's report beside another's
            // references. The sidecar names the report it describes; a mismatch is a miss, never a hit.
            string describes = (string)side["tsv_sha256"];
            if (describes != null && !string.Equals(describes, Sha256(entry.TsvPath), StringComparison.OrdinalIgnoreCase))
            {
                entry.Miss = "publication_incomplete";
                entry.Detail = new JObject
                {
                    ["means"] = "the report under this key is not the one its sidecar describes (a publication was " +
                                "interrupted between the two files). It is read again."
                };
                return entry;
            }

            // THE DEPENDENCIES. A drawing that references others was read WITH
            // them, so a changed reference is a changed reading even though the
            // host file's own bytes never moved. This is the invalidation that a
            // content hash of one file cannot give.
            // RESOLVED FROM THIS DRAWING'S FOLDER, not from where the reading was made.
            // MEASURED: a revised COPY of a drawing set has a host file byte-identical to
            // the original and a changed reference beside it; checking the paths the
            // original reading resolved compared the ORIGINAL reference and served the
            // stale reading for the copy.
            string here = null;
            try { here = Path.GetDirectoryName(dwgPath); } catch { }
            JArray changed = ChangedDependencies(side, here);
            if (changed.Count > 0)
            {
                // ANOTHER READING OF THE SAME BYTES, WITH OTHER REFERENCES. MEASURED (campaign 4): a
                // revised copy whose host file is byte-identical to the original evicted the original's
                // reading, and alternating between the two re-read the set cold each time (356-372 s).
                // Each reference set keeps its own variant; one whose references all still hash as
                // recorded is a hit.
                foreach (string variant in VariantSidecars(key))
                {
                    JObject vs;
                    try { vs = JObject.Parse(File.ReadAllText(variant)); } catch { continue; }
                    string vtsv = Path.ChangeExtension(variant, ".tsv");
                    if (!File.Exists(vtsv) || ChangedDependencies(vs, here).Count > 0) continue;
                    string vdesc = (string)vs["tsv_sha256"];
                    if (vdesc != null && !string.Equals(vdesc, Sha256(vtsv), StringComparison.OrdinalIgnoreCase)) continue;
                    entry.Hit = true;
                    entry.TsvPath = vtsv;
                    entry.Detail = HitDetail(Path.GetFileNameWithoutExtension(variant), vs);
                    entry.Detail["variant_of"] = key;
                    return entry;
                }
                entry.Miss = "dependency_changed";
                entry.Detail = new JObject { ["changed"] = changed };
                return entry;
            }

            entry.Hit = true;
            entry.Detail = HitDetail(key, side);
            return entry;
        }

        private static IEnumerable<string> VariantSidecars(string key)
        {
            try { return Directory.GetFiles(Root, key + "-v-*.json").OrderByDescending(File.GetLastWriteTimeUtc).ToList(); }
            catch { return new List<string>(); }
        }

        private static JObject HitDetail(string key, JObject side) => new JObject
        {
            ["key"] = key,
            ["read_utc"] = side["read_utc"],
            ["seconds_when_first_read"] = side["seconds"],
            ["dependencies_checked"] = (side["dependencies"] as JArray ?? new JArray()).Count,
            ["means"] = "the same file, read by the same extractor through the same engine with the same " +
                        "options, and every external reference still hashes to what it did then. The RULES " +
                        "are not part of this key: interpretation runs from scratch every time."
        };

        /// <summary>Every recorded reference that no longer hashes as recorded, resolved from here.</summary>
        private static JArray ChangedDependencies(JObject side, string here)
        {
            var changed = new JArray();
            var recorded = (side["dependencies"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            Dictionary<string, string> resolvedHere = ResolveAll(here, recorded.Select(d => new CadIrExternalReference
            {
                Name = (string)d["name"], Path = (string)d["declared"]
            }).ToList());
            foreach (JObject dep in recorded)
            {
                string path = (string)dep["path"];
                string was = (string)dep["sha256"];
                if (dep["name"] != null || dep["declared"] != null)
                {
                    // NOT FOUND FROM HERE IS ABSENT. MEASURED risk: falling back to the path the
                    // ORIGINAL resolved compared the original's reference for a copy that lacks it.
                    string again;
                    resolvedHere.TryGetValue(Key(dep), out again);
                    path = again;
                }
                if (string.IsNullOrWhiteSpace(path))
                {
                    changed.Add(new JObject
                    {
                        ["name"] = (string)dep["name"],
                        ["path"] = (string)dep["declared"] ?? (string)dep["name"],
                        ["was"] = was, ["now"] = "(absent)",
                        ["means"] = "the reference this drawing was read with is no longer on this machine"
                    });
                    continue;
                }
                string now = File.Exists(path) ? Sha256(path) : null;
                if (!string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
                    changed.Add(new JObject
                    {
                        ["name"] = (string)dep["name"],
                        ["path"] = path,
                        ["was"] = was,
                        ["now"] = now ?? "(absent)",
                        ["means"] = now == null
                            ? "the reference this drawing was read with is no longer on this machine"
                            : "the reference this drawing was read with has changed"
                    });
            }
            return changed;
        }

        /// <summary>
        /// Keep a reading, with what it depended on. Failure to cache is never
        /// failure to read: the caller already has its answer.
        /// </summary>
        public static JObject Store(CadDwgCacheEntry entry, string tsvSource, CadDwgReading reading,
                                    string dwgPath, double seconds, string hostShaBefore = null,
                                    DateTime? extractionStartedUtc = null)
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
                // WHAT WAS READ IS WHAT IS RECORDED. The host was hashed BEFORE the extraction (that hash
                // is the key); a host or a reference written while the console was reading gives a
                // reading of neither version, and it is not kept under either one's identity.
                if (hostShaBefore != null)
                {
                    string hostNow = Sha256(dwgPath);
                    if (!string.Equals(hostNow, hostShaBefore, StringComparison.OrdinalIgnoreCase))
                        return new JObject
                        {
                            ["stored"] = false, ["refused"] = "changed_during_extraction",
                            ["what"] = dwgPath,
                            ["means"] = "the drawing was written while it was being read; this reading is used for this " +
                                        "run only and not cached."
                        };
                }
                if (extractionStartedUtc.HasValue)
                {
                    string folderNow = null;
                    try { folderNow = Path.GetDirectoryName(dwgPath); } catch { }
                    List<CadIrExternalReference> refsNow = reading?.ExternalReferences ?? new List<CadIrExternalReference>();
                    foreach (string path in ResolveAll(folderNow, refsNow).Values.Where(v => v != null))
                    {
                        DateTime written;
                        try { written = File.GetLastWriteTimeUtc(path); } catch { continue; }
                        if (written > extractionStartedUtc.Value)
                            return new JObject
                            {
                                ["stored"] = false, ["refused"] = "changed_during_extraction", ["what"] = path,
                                ["means"] = "a reference was written after the extraction started; which version the " +
                                            "reading saw is unknown, so it is used for this run only and not cached."
                            };
                    }
                }
                // THE READING BEING REPLACED IS KEPT, under a variant named by its references.
                string kept = KeepAsVariant(entry);
                // PUBLISHED WHOLE: written beside the target and moved into place, so a
                // reader never finds half a report under a key.
                Publish(entry.TsvPath, tmp => File.Copy(tsvSource, tmp, true));

                var deps = new JArray();
                string folder = null;
                try { folder = Path.GetDirectoryName(dwgPath); } catch { }
                List<CadIrExternalReference> refs = reading?.ExternalReferences ?? new List<CadIrExternalReference>();
                Dictionary<string, string> resolvedNow = ResolveAll(folder, refs);
                foreach (CadIrExternalReference x in refs)
                {
                    string resolved;
                    resolvedNow.TryGetValue(Key(x.Name, x.Path), out resolved);
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
                    ["drawing_sha256"] = Sha256(dwgPath),
                    ["read_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["seconds"] = Math.Round(seconds, 1),
                    ["entities"] = reading?.Entities?.Count ?? 0,
                    ["dependencies"] = deps,
                    // the report this sidecar describes: a sidecar published beside another report is a miss
                    ["tsv_sha256"] = Sha256(entry.TsvPath)
                };
                Publish(entry.SidecarPath, tmp => File.WriteAllText(tmp, side.ToString(Formatting.Indented)));
                var stored = new JObject
                {
                    ["stored"] = true,
                    ["key"] = entry.Key,
                    ["dependencies_recorded"] = deps.Count
                };
                if (kept != null) stored["earlier_reading_kept_as"] = kept;
                return stored;
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
        /// Copies the primary reading of a key to "key-v-(digest of its references)" before it is
        /// replaced. Null when there was nothing to keep or it records no reference.
        /// </summary>
        private static string KeepAsVariant(CadDwgCacheEntry entry)
        {
            try
            {
                if (!File.Exists(entry.SidecarPath) || !File.Exists(entry.TsvPath)) return null;
                JObject side = JObject.Parse(File.ReadAllText(entry.SidecarPath));
                var deps = (side["dependencies"] as JArray ?? new JArray()).OfType<JObject>()
                    .Select(d => ((string)d["name"] ?? "").ToLowerInvariant() + "=" + (string)d["sha256"])
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (deps.Count == 0) return null;
                string variant = entry.Key + "-v-" + Hash(string.Join("|", deps)).Substring(0, 12);
                string vtsv = Path.Combine(Root, variant + ".tsv"), vside = Path.Combine(Root, variant + ".json");
                string src = entry.TsvPath;
                Publish(vtsv, tmp => File.Copy(src, tmp, true));
                side["key"] = variant;
                Publish(vside, tmp => File.WriteAllText(tmp, side.ToString(Formatting.Indented)));
                return variant;
            }
            catch { return null; }
        }

        /// <summary>
        /// THE IDENTITY OF WHAT WAS READ, when the drawing references others: the host's
        /// bytes and every reference it is read with, resolved from the host's folder now.
        /// Null when no reading of these host bytes on this machine recorded its references
        /// - the caller then has the host's hash only, and says so. Prefixed "set:" so it is
        /// never compared as if it were a single file's hash.
        /// </summary>
        public static string SourceSetSha256(string dwgPath, string hostSha)
        {
            if (string.IsNullOrWhiteSpace(dwgPath) || string.IsNullOrWhiteSpace(hostSha) || !Directory.Exists(Root))
                return null;
            string here = null;
            try { here = Path.GetDirectoryName(dwgPath); } catch { }
            // THE READING OF THIS FOLDER, not the newest reading of these host bytes anywhere. MEASURED
            // (campaign 5): after a fixture whose copy of A-109 references one more file was read, the
            // ORIGINAL set's identity took that file's name as "(absent)" and changed with no file of the
            // original changing. A reading whose references all resolve and hash as recorded FROM HERE is
            // the one that describes this folder; only when none does is the newest one used.
            var sides = new List<JObject>();
            foreach (string sidecar in Directory.GetFiles(Root, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                JObject candidate;
                try { candidate = JObject.Parse(File.ReadAllText(sidecar)); } catch { continue; }
                if (!string.Equals((string)candidate["drawing_sha256"], hostSha, StringComparison.OrdinalIgnoreCase)) continue;
                sides.Add(candidate);
            }
            JObject valid = sides.FirstOrDefault(x => (x["dependencies"] as JArray)?.Count > 0 &&
                                                      ChangedDependencies(x, here).Count == 0);
            foreach (JObject side in valid != null ? new[] { valid } : sides.Take(1).ToArray())
            {
                var parts = new List<string>();
                var recorded = (side["dependencies"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                Dictionary<string, string> resolvedHere = ResolveAll(here, recorded.Select(d => new CadIrExternalReference
                {
                    Name = (string)d["name"], Path = (string)d["declared"]
                }).ToList());
                foreach (JObject dep in recorded)
                {
                    string path;
                    resolvedHere.TryGetValue(Key(dep), out path);
                    parts.Add(((string)dep["name"] ?? "").ToLowerInvariant() + "=" +
                              (path == null ? "(absent)" : Sha256(path) ?? "(unreadable)"));
                }
                if (parts.Count == 0) return null;
                parts.Sort(StringComparer.Ordinal);
                return "set:" + Hash(hostSha.ToLowerInvariant() + "|" + string.Join("|", parts));
            }
            return null;
        }

        private static void Publish(string target, Action<string> write)
        {
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                write(tmp);
                if (File.Exists(target)) File.Replace(tmp, target, null);
                else File.Move(tmp, target);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ---- one extraction per key at a time ------------------------------------

        /// <summary>
        /// ONE READING AT A TIME PER KEY. A client that timed out and asked again, or a
        /// second Revit, must not start a second six-minute extraction of the same file:
        /// the second caller waits for the first one's result instead. The lock is a file
        /// naming its owner's process; a lock whose owner is gone is taken over.
        /// </summary>
        public static string LockPath(string key) => Path.Combine(Root, key + ".lock");

        /// <summary>Null when this process now owns the key; otherwise who does.</summary>
        public static JObject TryLock(string key)
        {
            Directory.CreateDirectory(Root);
            string path = LockPath(key);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        byte[] body = Encoding.UTF8.GetBytes(new JObject
                        {
                            ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                            ["since_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                        }.ToString(Formatting.None));
                        f.Write(body, 0, body.Length);
                    }
                    return null;
                }
                catch (IOException)
                {
                    JObject owner = ReadLock(path);
                    int pid = (int?)owner?["pid"] ?? -1;
                    if (pid > 0 && Alive(pid) && pid != System.Diagnostics.Process.GetCurrentProcess().Id)
                        return owner;
                    // the owner is gone (or it is this process, which does not hold a
                    // read of this key any more): the lock is stale
                    try { File.Delete(path); } catch { return owner ?? new JObject { ["pid"] = pid }; }
                }
            }
            return new JObject { ["error"] = "the lock could not be taken" };
        }

        public static void Unlock(string key)
        {
            string path = LockPath(key);
            JObject owner = ReadLock(path);
            if ((int?)owner?["pid"] == System.Diagnostics.Process.GetCurrentProcess().Id)
                try { File.Delete(path); } catch { }
        }

        private static JObject ReadLock(string path)
        {
            try { return JObject.Parse(File.ReadAllText(path)); } catch { return null; }
        }

        private static bool Alive(int pid)
        {
            try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        /// <summary>
        /// The references an EARLIER reading of these same bytes had, that are gone now: absent
        /// from this drawing's folder when the cache was consulted AND left unresolved by the fresh
        /// reading. A drawing read without one of them is a different drawing - its walls or
        /// symbols simply are not there - so the caller refuses rather than plan from it. A
        /// reference that was never resolved (a seal on a drive this machine does not have) is not
        /// one of these: it was not part of any reading.
        /// </summary>
        public static List<string> MissingReferences(JObject missDetail, IList<CadIrExternalReference> fresh)
        {
            var absent = new HashSet<string>(
                (missDetail?["changed"] as JArray ?? new JArray()).OfType<JObject>()
                    .Where(c => (string)c["now"] == "(absent)" && (string)c["name"] != null)
                    .Select(c => (string)c["name"]), StringComparer.OrdinalIgnoreCase);
            return (fresh ?? new List<CadIrExternalReference>())
                .Where(x => x != null && x.Name != null && x.Resolved != true && absent.Contains(x.Name))
                .Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Key(string name, string declared) =>
            (name ?? "").ToLowerInvariant() + "\u0001" + (declared ?? "").ToLowerInvariant();

        private static string Key(JObject dep) => Key((string)dep["name"], (string)dep["declared"]);

        /// <summary>
        /// Every reference resolved from the drawing's folder, a NESTED one ("Parent|Child")
        /// from its parent's folder - a nested reference records its path relative to the file
        /// that references it, not to the drawing at the top.
        /// </summary>
        public static Dictionary<string, string> ResolveAll(string folder, IList<CadIrExternalReference> refs)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (CadIrExternalReference x in (refs ?? new List<CadIrExternalReference>())
                         .Where(r => r != null).OrderBy(r => (r.Name ?? "").Count(c => c == '|')))
            {
                string parentFolder = null;
                int bar = (x.Name ?? "").LastIndexOf('|');
                if (bar > 0)
                {
                    string parentPath;
                    if (byName.TryGetValue(x.Name.Substring(0, bar), out parentPath) && parentPath != null)
                        try { parentFolder = Path.GetDirectoryName(parentPath); } catch { }
                }
                string resolved = ResolveReference(folder, x, parentFolder);
                result[Key(x.Name, x.Path)] = resolved;
                if (x.Name != null && !byName.ContainsKey(x.Name)) byName[x.Name] = resolved;
            }

            // A NESTED REFERENCE DOES NOT SAY WHOSE IT IS. MEASURED (campaign 4, live fixture): a
            // drawing referenced through another one is reported under its own bare name with the
            // path its PARENT recorded - "HZ-NEST.dwg", relative to the folder the parent lives in.
            // Resolved from the top drawing's folder that is nothing, so the reference went
            // untracked and a change to it was served from cache. So what is still unresolved is
            // looked for beside the references that DID resolve, and only where exactly one of
            // those folders holds it: two candidates is not a measurement.
            var folders = result.Values.Where(v => v != null)
                .Select(v => { try { return Path.GetDirectoryName(v); } catch { return null; } })
                .Where(v => v != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (CadIrExternalReference x in (refs ?? new List<CadIrExternalReference>()).Where(r => r != null))
            {
                string key = Key(x.Name, x.Path);
                string had;
                if (result.TryGetValue(key, out had) && had != null) continue;
                string leaf = null;
                try { leaf = Path.GetFileName(x.Path ?? x.Name); } catch { }
                if (string.IsNullOrWhiteSpace(leaf)) continue;
                bool relative = false;
                try { relative = !string.IsNullOrWhiteSpace(x.Path) && (!Path.IsPathRooted(x.Path) || x.Path.StartsWith(".", StringComparison.Ordinal)); }
                catch { }
                // its declared RELATIVE path, from each resolved reference's folder (a nested reference records
                // its path relative to its parent) - and only when that finds nothing, the bare file name
                Func<Func<string, string>, List<string>> probe = make => folders.Select(make)
                    .Where(p => { try { return p != null && File.Exists(p); } catch { return false; } })
                    .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> found = relative ? probe(f => { try { return Path.Combine(f, x.Path); } catch { return null; } })
                                              : new List<string>();
                if (found.Count == 0) found = probe(f => Path.Combine(f, leaf));
                if (found.Count != 1) continue;
                result[key] = found[0];
                if (x.Name != null) byName[x.Name] = found[0];
            }
            return result;
        }

        /// <summary>
        /// Where an external reference actually is. A DWG records the path its
        /// author had; the file is usually beside the drawing.
        /// </summary>
        private static string ResolveReference(string folder, CadIrExternalReference x, string parentFolder = null)
        {
            if (x == null) return null;
            foreach (string candidate in Candidates(folder, x, parentFolder))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); } catch { }
            }
            return null;
        }

        private static IEnumerable<string> Candidates(string folder, CadIrExternalReference x, string parentFolder)
        {
            if (!string.IsNullOrWhiteSpace(x.Path))
            {
                bool rooted = false;
                try { rooted = Path.IsPathRooted(x.Path) && !x.Path.StartsWith(".", StringComparison.Ordinal); } catch { }
                // A RELATIVE PATH IS RELATIVE TO THE FILE THAT DECLARES IT - never to this process.
                if (!rooted)
                {
                    if (parentFolder != null) yield return Path.Combine(parentFolder, x.Path);
                    if (folder != null) yield return Path.Combine(folder, x.Path);
                }
                else yield return x.Path;
                string leaf = null;
                try { leaf = Path.GetFileName(x.Path); } catch { }
                if (leaf != null)
                {
                    if (parentFolder != null) yield return Path.Combine(parentFolder, leaf);
                    if (folder != null) yield return Path.Combine(folder, leaf);
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
