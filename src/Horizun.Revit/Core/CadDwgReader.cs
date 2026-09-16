// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// RUNNING THE EXTRACTION. The parsing half is in CadDwgExtract and is pure; this
// half is the part that touches the machine, and every decision in it was made
// against a measurement rather than a belief.
//
// WHY ACCORECONSOLE. It is the headless AutoCAD that ships with the AutoCAD this
// machine already has. Nothing is bought, nothing is downloaded, no cloud service
// is involved and no library is loaded into Revit's process - it is a separate
// executable reading a file. Where AutoCAD is not installed this reader is simply
// unavailable, and the IR says so rather than pretending.
//
// WHY THE DRAWING IS READ IN PLACE. The obvious safe move is to copy the file to
// a temporary folder and read the copy. Measured on a real permit set, that loses
// nine tenths of the drawing: external references resolve RELATIVE TO THE
// DRAWING, so a copy in a temp folder opens with every reference unresolved -
// 6,515 report lines against 61,807 for the same file read where it lives.
//
// So it is read where it lives, and what that costs was measured too: opening the
// real folder with accoreconsole left 169 files unchanged, no new file, no
// changed timestamp, and the drawing's SHA-256 identical. This reader issues no
// command that writes and never saves.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CadDwgRunResult
    {
        public CadDwgReading Reading;
        public string Refusal;
        public JObject RefusalDetail;
        public string EnginePath;
        public string EngineVersion;
        public int ExitCode;
        public double Seconds;
        public string ConsoleTail;

        /// <summary>
        /// True when the EXTRACTION was served from cache. The interpretation was
        /// not: the report is re-parsed on every call, so a rule, a zone or a
        /// tolerance that changed is always honoured.
        /// </summary>
        public bool FromCache;

        /// <summary>What the cache did and why, hit or miss. Never null after a read.</summary>
        public JObject CacheDetail;

        public bool Ok { get { return Refusal == null && Reading != null; } }
    }

    public static class CadDwgReader
    {
        /// <summary>Set this to an accoreconsole.exe to override the search.</summary>
        public const string EngineEnvVar = "HORIZUN_ACCORECONSOLE";

        /// <summary>
        /// Where AutoCAD puts its headless console, newest year first.
        ///
        /// Returns null when there is none, which is not an error: it is the answer
        /// to "can this machine read a DWG properly", and the caller turns it into
        /// a capability declaration rather than a crash.
        /// </summary>
        public static string FindEngine()
        {
            string overridden = Environment.GetEnvironmentVariable(EngineEnvVar);
            if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden)) return overridden;

            var roots = new List<string>();
            foreach (string pf in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramW6432"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)")
                     })
                if (!string.IsNullOrWhiteSpace(pf)) roots.Add(Path.Combine(pf, "Autodesk"));

            var found = new List<string>();
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(root, "AutoCAD*"); }
                catch { continue; }
                foreach (string d in dirs)
                {
                    string exe = Path.Combine(d, "accoreconsole.exe");
                    if (File.Exists(exe)) found.Add(exe);
                }
            }
            // Newest first: the directory names end in the year.
            return found.OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        public static string EngineVersionOf(string enginePath)
        {
            try
            {
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(enginePath);
                string product = v.ProductName ?? Path.GetFileName(Path.GetDirectoryName(enginePath));
                return (product + " " + (v.FileVersion ?? "")).Trim();
            }
            catch { return Path.GetFileName(Path.GetDirectoryName(enginePath)); }
        }

        /// <summary>
        /// The script accoreconsole is fed: the extractor's forms, one per line,
        /// then the call. Written to a temp file the caller owns.
        ///
        /// It is inlined rather than loaded because AutoCAD 2025 ships SECURELOAD
        /// on and refuses (load) from any path the user has not trusted - and this
        /// bridge does not edit the user's AutoCAD trust settings in order to read
        /// a file.
        /// </summary>
        public static string BuildScript(string outputPath)
        {
            var sb = new StringBuilder();
            foreach (string form in CadDwgScript.Forms) sb.Append(form).Append("\r\n");
            sb.Append("(hz-dump \"").Append(outputPath.Replace('\\', '/')).Append("\")\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// Read one DWG. The file is opened WHERE IT LIVES - see the header - and
        /// nothing is written to its folder.
        /// </summary>
        public static CadDwgRunResult Read(string dwgPath, int timeoutSeconds = 300,
                                           double? assumeMmPerUnit = null, bool useCache = true)
        {
            var result = new CadDwgRunResult();
            if (string.IsNullOrWhiteSpace(dwgPath) || !File.Exists(dwgPath))
            {
                result.Refusal = "drawing_not_found";
                result.RefusalDetail = new JObject
                {
                    ["refused"] = "drawing_not_found",
                    ["path"] = dwgPath ?? "(none)",
                    ["means"] = "nothing was read. The path is the caller's to fix."
                };
                return result;
            }

            string engine = FindEngine();
            if (engine == null)
            {
                result.Refusal = "no_dwg_engine";
                result.RefusalDetail = new JObject
                {
                    ["refused"] = "no_dwg_engine",
                    ["looked_for"] = "accoreconsole.exe under Program Files\\Autodesk\\AutoCAD*",
                    ["override_with"] = EngineEnvVar,
                    ["means"] = "this machine has no AutoCAD, so the file's text, block names, attributes, " +
                                "handles and external references cannot be read from the DWG itself. The " +
                                "Revit-import reader still works and declares those axes unavailable - which " +
                                "is a smaller reading, honestly labelled, not a failure."
                };
                return result;
            }
            result.EnginePath = engine;
            result.EngineVersion = EngineVersionOf(engine);

            // ---- the cache, which addresses the EXTRACTION and nothing else ----
            //
            // A hit skips six minutes of headless AutoCAD. It does not skip the
            // parse, and it cannot skip the rules: interpretation happens after
            // this method returns, every time, from the report's own text.
            string options = "mm_per_unit=" +
                (assumeMmPerUnit.HasValue
                    ? assumeMmPerUnit.Value.ToString("R", CultureInfo.InvariantCulture)
                    : "(none)");
            string dwgSha = CadDwgCache.Sha256(dwgPath);
            CadDwgCacheEntry cached = null;
            if (useCache)
            {
                cached = CadDwgCache.Lookup(dwgPath, dwgSha, result.EngineVersion, options);
                if (cached.Hit)
                {
                    var hitClock = Stopwatch.StartNew();
                    try
                    {
                        result.Reading = CadDwgExtract.Parse(File.ReadLines(cached.TsvPath), assumeMmPerUnit);
                        result.FromCache = true;
                        result.Seconds = hitClock.Elapsed.TotalSeconds;
                        result.CacheDetail = new JObject
                        {
                            ["state"] = "hit",
                            ["key"] = cached.Key,
                            ["parse_seconds"] = Math.Round(hitClock.Elapsed.TotalSeconds, 2),
                            ["detail"] = cached.Detail
                        };
                        if (!result.Reading.Complete)
                        {
                            // A cached report that is not complete is not a
                            // shortcut worth taking; read the file again.
                            result.Reading = null;
                            result.FromCache = false;
                            result.CacheDetail["state"] = "hit_discarded_incomplete";
                        }
                        else return result;
                    }
                    catch (Exception ex)
                    {
                        result.CacheDetail = new JObject
                        {
                            ["state"] = "hit_unreadable",
                            ["key"] = cached.Key,
                            ["error"] = ex.Message
                        };
                    }
                }
                else
                {
                    result.CacheDetail = new JObject
                    {
                        ["state"] = "miss",
                        ["why"] = cached.Miss,
                        ["key"] = cached.Key,
                        ["detail"] = cached.Detail
                    };
                }
            }
            else
            {
                result.CacheDetail = new JObject
                {
                    ["state"] = "bypassed",
                    ["why"] = "the caller asked for a fresh extraction"
                };
            }

            string work = Path.Combine(Path.GetTempPath(), "horizun-dwg", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string scriptPath = Path.Combine(work, "extract.scr");
            string outPath = Path.Combine(work, "reading.tsv");
            File.WriteAllText(scriptPath, BuildScript(outPath), new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = engine,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = work
            };
            psi.Arguments = "/i \"" + dwgPath + "\" /s \"" + scriptPath + "\" /l en-US";

            var console = new StringBuilder();
            var clock = Stopwatch.StartNew();
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) console.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) console.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(Math.Max(5, timeoutSeconds) * 1000))
                    {
                        try { p.Kill(); } catch { }
                        result.Refusal = "extraction_timed_out";
                        // MEASURED, NOT ASSUMED. The refusal used to leave Seconds at
                        // zero, so a reply said the read took no time and timed out.
                        result.Seconds = clock.Elapsed.TotalSeconds;
                        result.RefusalDetail = new JObject
                        {
                            ["refused"] = "extraction_timed_out",
                            ["waited_seconds"] = timeoutSeconds,
                            ["measured_seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
                            ["means"] = "the headless AutoCAD did not finish. A drawing with many external " +
                                        "references opens them all, and a reference on a disconnected network " +
                                        "drive is the usual reason - the console waits for the file system, " +
                                        "not for the drawing. A large permit drawing legitimately takes " +
                                        "minutes: raise the caller's timeout before concluding the file is " +
                                        "unreadable."
                        };
                        return result;
                    }
                    result.ExitCode = p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                result.Refusal = "engine_would_not_start";
                result.RefusalDetail = new JObject
                {
                    ["refused"] = "engine_would_not_start",
                    ["engine"] = engine,
                    ["error"] = ex.Message
                };
                return result;
            }
            finally { clock.Stop(); }

            result.Seconds = clock.Elapsed.TotalSeconds;
            string tail = console.ToString();
            result.ConsoleTail = tail.Length > 4000 ? tail.Substring(tail.Length - 4000) : tail;

            if (!File.Exists(outPath))
            {
                result.Refusal = "extraction_produced_nothing";
                result.RefusalDetail = new JObject
                {
                    ["refused"] = "extraction_produced_nothing",
                    ["exit_code"] = result.ExitCode,
                    ["console_tail"] = result.ConsoleTail,
                    ["means"] = "the console ran and wrote no report. The commonest cause is a drawing that " +
                                "needs a proxy application the console does not have, and the console says so " +
                                "in the tail above."
                };
                return result;
            }

            result.Reading = CadDwgExtract.Parse(File.ReadLines(outPath), assumeMmPerUnit);

            // KEPT ONLY WHEN IT IS WHOLE. A report with no end marker describes a
            // walk that stopped, and caching one would serve that stop forever.
            if (useCache && cached != null && result.Reading.Complete)
            {
                JObject stored = CadDwgCache.Store(cached, outPath, result.Reading, dwgPath, result.Seconds);
                result.CacheDetail = result.CacheDetail ?? new JObject();
                result.CacheDetail["stored"] = stored;
            }

            if (!result.Reading.Complete)
            {
                result.Refusal = "extraction_incomplete";
                result.RefusalDetail = new JObject
                {
                    ["refused"] = "extraction_incomplete",
                    ["entities_read"] = result.Reading.Entities.Count,
                    ["means"] = "the report has no end marker, so the walk stopped part way. What was read is " +
                                "REAL but it is not the whole drawing, and a conversion from it would report " +
                                "coverage of a file it only partly saw."
                };
            }
            return result;
        }
    }
}
