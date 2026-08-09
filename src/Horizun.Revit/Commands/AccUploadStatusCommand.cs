// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_acc_upload_status - has each of these files been assigned a cloud
// folder yet, or is its upload still pending? (story 5.15)
//
// Publishing to ACC through the Desktop Connector is copy + wait: the copy
// lands in the local cache and the upload happens later, asynchronously, and
// fails under throttling ("Too many people or processes...", ~11-minute
// circuit breaker). Measured in the field: 3 of 8 families silently
// unuploaded while every local hash checked out - the local cache is exactly
// what a hash proves, and the cloud is exactly what it does not. The one
// local record that answers the question is the connector's own WAL, and
// until this command the only way to read it was an external script.
//
// Read-only over files the connector owns. The parsing and matching rules are
// AccUploadWal (Revit-free, unit-tested); this file is the IO: find the WAL
// files, read them WITHOUT demanding exclusive access (the connector holds
// them open - the same share-mode lesson family_apply's save proof paid for),
// and say per file when one could not be read, because a count over a partial
// read must never present itself as a count over the log.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class AccUploadStatusCommand : ICommand
    {
        public string Name => "horizun_acc_upload_status";

        public string Description =>
            "Has each of these files been assigned an ACC cloud folder yet, or is its upload still pending? " +
            "Reads the Desktop Connector's own log on THIS machine - the record that appears when an upload " +
            "completes and is missing while one is pending or failed. Pass 'names' and/or 'paths' (basenames " +
            "are used). A hit is the connector's testimony, not a cloud API check; a miss is absence of " +
            "evidence, never proof of absence. Read-only, no document needed.";

        /// <summary>Where the Desktop Connector keeps the BIM Docs data source's logs.</summary>
        private static string DefaultWalRoot()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Autodesk", "Desktop Connector", "Data",
                                "Autodesk.DataSourceType.BIMDocs");
        }

        /// <summary>A WAL file is a log, not a model; one this size is something else.</summary>
        private const long MaxBytesPerFile = 256L * 1024 * 1024;

        private const int MaxNames = 1000;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject req;
            try { req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // ---- WHICH names. 'names' as given; 'paths' contribute their basenames. ----
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string n)
            {
                if (!string.IsNullOrWhiteSpace(n) && seen.Add(n.Trim())) names.Add(n.Trim());
            }
            if (req["names"] is JArray na)
                foreach (JToken t in na)
                    if (t.Type == JTokenType.String) Add((string)t);
            if (req["paths"] is JArray pa)
                foreach (JToken t in pa)
                    if (t.Type == JTokenType.String)
                    {
                        string p = (string)t;
                        try { Add(Path.GetFileName(p)); }
                        catch { Add(p); }   // an unparseable path still gets looked up as given
                    }

            if (names.Count == 0)
                return CommandResult.Fail(
                    "Nothing to check: pass 'names' (file names, with or without extension) and/or 'paths' " +
                    "(their basenames are used). An empty check reporting zero pending uploads would be an " +
                    "answer about nothing wearing the shape of an all-clear.");
            if (names.Count > MaxNames)
                return CommandResult.Fail(
                    "Too many names: " + names.Count + " (cap " + MaxNames + "). Split the batch.");

            string projectId = req.Value<string>("project_id");

            // ---- WHERE the log is. Overridable for nonstandard installs; never guessed past that. ----
            string root = req.Value<string>("wal_root");
            bool rootWasDefaulted = string.IsNullOrWhiteSpace(root);
            if (rootWasDefaulted) root = DefaultWalRoot();

            if (!Directory.Exists(root))
                return CommandResult.Fail(
                    "The Desktop Connector's log folder was not found at '" + root + "'" +
                    (rootWasDefaulted ? " (the default location)" : "") + ". Upload status is UNKNOWN for every " +
                    "name - the absence of the log is not the absence of uploads. Is the Desktop Connector " +
                    "installed on this machine" + (rootWasDefaulted ? "? A nonstandard install can be pointed at with 'wal_root'." : ", and is 'wal_root' right?"));

            List<string> walFiles;
            try
            {
                walFiles = Directory.GetFiles(root, "*.properties-log.db", SearchOption.TopDirectoryOnly)
                                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("The log folder '" + root + "' could not be listed: " + ex.Message +
                                          ". Upload status is UNKNOWN for every name.");
            }
            if (walFiles.Count == 0)
                return CommandResult.Fail(
                    "No *.properties-log.db files exist under '" + root + "'. Upload status is UNKNOWN for " +
                    "every name - the connector may never have synced a BIM Docs project on this machine, or " +
                    "the data source folder is different. Nothing here says the files are not in the cloud.");

            // ---- Read and scan. A file that will not read is REPORTED, never skipped silently. ----
            var hits = new List<WalHit>();
            var read = new JArray();
            var unread = new JArray();
            foreach (string f in walFiles)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > MaxBytesPerFile)
                    {
                        unread.Add(new JObject
                        {
                            ["file"] = f,
                            ["error"] = "the file is " + fi.Length + " bytes, over the " + MaxBytesPerFile +
                                        "-byte cap for a log read"
                        });
                        continue;
                    }
                    byte[] raw;
                    // FileShare.ReadWrite | Delete: the connector holds its log open, and
                    // demanding exclusive access is how family_apply's save hash was null
                    // on every file it was ever pointed at (5.12).
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                    {
                        raw = new byte[fs.Length];
                        int off = 0;
                        while (off < raw.Length)
                        {
                            int got = fs.Read(raw, off, raw.Length - off);
                            if (got <= 0) break;
                            off += got;
                        }
                        if (off < raw.Length) Array.Resize(ref raw, off);
                    }
                    hits.AddRange(AccUploadWal.Scan(f, AccUploadWal.Decode(raw)));
                    read.Add(f);
                }
                catch (Exception ex)
                {
                    unread.Add(new JObject { ["file"] = f, ["error"] = ex.Message });
                }
            }

            if (read.Count == 0)
                return CommandResult.Fail(
                    "None of the " + walFiles.Count + " log file(s) under '" + root + "' could be read: " +
                    string.Join("; ", unread.Select(u => u["error"])) + ". Upload status is UNKNOWN for every name.");

            // ---- Match and render. ----
            List<AccNameStatus> statuses = AccUploadWal.Match(names, hits);
            var rows = new JArray();
            int recorded = 0, notRecorded = 0;
            foreach (AccNameStatus s in statuses)
            {
                if (s.HasFolderUrn) recorded++; else notRecorded++;
                var row = new JObject
                {
                    ["requested"] = s.Requested,
                    ["has_folder_urn"] = s.HasFolderUrn,
                    ["matched_names"] = new JArray(s.MatchedNames.Select(n => (JToken)n)),
                    ["folder_urns"] = new JArray(s.FolderUrns.Select(u => (JToken)("urn:adsk.wipprod:fs.folder:" + u))),
                    ["recorded_in"] = new JArray(s.SourceFiles.Select(f => (JToken)f)),
                    ["note"] = s.Note
                };
                if (projectId != null && s.FolderUrns.Count > 0)
                    row["urls"] = new JArray(s.FolderUrns.Select(u => (JToken)AccUploadWal.BuildUrl(projectId, u)));
                rows.Add(row);
            }

            return CommandResult.Ok(new JObject
            {
                ["names"] = rows,
                ["recorded"] = recorded,
                ["not_recorded"] = notRecorded,
                ["wal_root"] = root,
                ["wal_files_read"] = read,
                ["wal_files_unread"] = unread,
                ["lower_bound_note"] = unread.Count > 0
                    ? unread.Count + " log file(s) could not be read, so 'recorded' is a LOWER BOUND and a " +
                      "not-recorded name may in fact be recorded in a file this call could not open."
                    : null,
                ["evidence"] = "The Desktop Connector's own log (*.properties-log.db) on this machine, read " +
                               "without opening or touching any model. The ParentFolderUrn-beside-Name record " +
                               "is what the connector writes when an upload completes.",
                ["note"] = "TWO HONESTY LINES. has_folder_urn=true is the CONNECTOR'S testimony that this name " +
                           "has a cloud folder - the record that appears when its upload completes - not a cloud " +
                           "API check. has_folder_urn=false is absence of EVIDENCE, never proof of absence: the " +
                           "upload may be pending, may have failed (throttling opens an ~11-minute circuit " +
                           "breaker and retries later), or the file may have synced under a different name. " +
                           "Local hashes prove the LOCAL CACHE only; this log is what turns 'I copied it' into " +
                           "'the connector uploaded it'."
            });
        }
    }
}
