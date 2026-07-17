// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// horizun_document_session — the PERSIST verb. The guarded superset of
// open_document (which stays; this does not replace it in place).
//
// This is the only tool in the estate whose mistakes cannot be undone. Everything
// else writes into a model you can roll back. This one touches files on disk, and
// two of its failure modes destroy work permanently:
//
//   1. THE UPGRADE. Pablo runs Revit 2025 (:3001) and 2026 (:3000) at the same
//      time. Opening a 2025 .rfa on the 2026 bridge upgrades it to 2026 — and
//      there is no downgrade, ever. A whole family library dies to a handler that
//      just calls OpenAndActivateDocument and reports "opened". The current
//      defense is that the agent remembers to eyeball two health calls; the
//      symptom ("'ElementId' object has no attribute 'IntegerValue'") arrives
//      AFTER the file has been touched. So: before opening we read the file's own
//      version off the disk with BasicFileInfo — WITHOUT opening it — read the
//      host's app.VersionNumber, and compare both against a REQUIRED
//      expected_version. Any disagreement is a refusal, not a warning. An upgrade
//      must be typed out by a human as allow_upgrade=true.
//   2. THE SAVE THAT DIDN'T. A family was destroyed once by a SaveAs that got
//      skipped while the delete line ran anyway, because the script's success
//      signal was "SaveAs did not throw" and it printed "OK -> " + newpath. Here,
//      "saved" is never anchored to the absence of an exception: it is anchored to
//      File.Exists plus a size plus an mtime plus a BasicFileInfo that reads back
//      off the filesystem after the write.
//
// Two truths this handler refuses to paper over:
//
//   * AUDIT IS NOT A SAVE OPTION. The Revit API exposes Audit on OpenOptions and
//     nowhere else — SaveOptions/SaveAsOptions have Compact and no Audit. The
//     pre-entrega deliverable is "a .rvt saved with Audit + Compact", and the
//     honest reading of that is: audited when it was OPENED, compacted when it was
//     SAVED. So audit_ran can only report on the open. If this handler did not
//     perform the open, it does not know, and "unknown" is a DIFFERENT value from
//     false. A handler that returned audit_ran=true off a save would be inventing
//     a flag the API never received.
//   * NO SYNC. Ever. Horizun does not sync to central from a robot, so there is no
//     sync operation here and there never will be. On a workshared document a save
//     is a save — of the local, or worse of the central — and it is refused unless
//     force_workshared says otherwise, with the distinction spelled out.
//
// Compact's whole point is the delta, so bytes_before/bytes_after are stat-ed from
// the filesystem on both sides. bytes_after == bytes_before after a compact is
// reported as SUSPICIOUS, in the response, not swallowed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunDocumentSessionHandler : IRevitCommand
    {
        public string Name => "horizun_document_session";

        public string Description =>
            "Open / save / save_as / close a Revit document, guarded against the irreversible. Before opening it " +
            "reads the file's own Revit version off disk (BasicFileInfo, WITHOUT opening it) and the host's " +
            "version, and refuses unless both match the REQUIRED expected_version — because opening a 2025 file " +
            "on a 2026 host upgrades it and there is no downgrade. Saving reports bytes/mtime/format re-read from " +
            "the filesystem after the write, never 'it did not throw'. Audit is an OPEN option in the Revit API, " +
            "so audit_ran only ever describes the open. It never syncs to central.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""open"", ""save"", ""save_as"", ""close"", ""inspect""],
                     ""description"": ""inspect: read a file's version off disk without opening it. open/save/save_as/close do what they say."" },
    ""file_path"": { ""type"": ""string"",
                     ""description"": ""open/inspect: the file to read. save/save_as/close: which OPEN document to act on (default: the active one)."" },
    ""expected_version"": { ""type"": ""string"",
                            ""description"": ""REQUIRED for open. The Revit year you believe this is, e.g. '2026'. Checked against BOTH the file on disk and the host. Any disagreement aborts before the file is touched."" },
    ""allow_upgrade"": { ""type"": ""boolean"", ""default"": false,
                         ""description"": ""Opt in to opening a file older than the host. This upgrades it and CANNOT be undone. Nothing else in this tool will do it for you."" },
    ""audit"": { ""type"": ""boolean"", ""default"": false, ""description"": ""open only: open with Audit. This is the ONLY place the Revit API accepts an audit flag."" },
    ""detach"": { ""type"": ""boolean"", ""default"": false, ""description"": ""open only: detach from central, preserving worksets."" },
    ""save_as_path"": { ""type"": ""string"", ""description"": ""save_as: absolute destination path."" },
    ""compact"": { ""type"": ""boolean"", ""default"": false, ""description"": ""save/save_as: pass Compact to the API. The response reports the byte delta it actually produced."" },
    ""overwrite"": { ""type"": ""boolean"", ""default"": false, ""description"": ""save_as: allow overwriting an existing destination file."" },
    ""max_backups"": { ""type"": ""integer"", ""minimum"": 1, ""description"": ""save_as: cap the .000N backup pile Revit leaves behind."" },
    ""force_workshared"": { ""type"": ""boolean"", ""default"": false,
                            ""description"": ""Required to save/save_as a workshared document, to close one with save_on_close, and in either case when the workshared state cannot be read at all (unknown is not a clearance). This tool never syncs to central; on a central model a save still writes to central."" },
    ""save_on_close"": { ""type"": ""boolean"", ""default"": false, ""description"": ""close: save before closing. Off by default — closing should not be a write you did not ask for."" }
  }
}";

        // Audit is an OpenOptions flag and Revit never tells you afterwards whether a
        // document was audited. So the only honest source is our own memory of the
        // open WE performed. Anything else is 'unknown', which is not 'false'.
        //
        // Keyed on the Document OBJECT, never on a path. A path outlives the document
        // instance and answers for opens this tool did not perform: close the model in
        // the Revit UI, re-open it there WITHOUT audit, save through this tool, and a
        // path-keyed memory stamps the deliverable audit_ran=true off a document that
        // no longer exists. It also survives SaveAs, where the path changes under the
        // same document. A miss here is a MISS — AuditState answers null ('nobody
        // knows'). The only false this file ever writes is the measured audit flag of
        // an open THIS tool performed (audit=false is a fact about that open); no false
        // is ever manufactured out of a cache miss, because "we never looked" and "we
        // looked and it was not audited" are different facts about a deliverable.
        private static readonly Dictionary<Document, bool> _auditedOnOpen =
            new Dictionary<Document, bool>(new DocumentIdentity());

        /// <summary>
        /// Reference identity for the audit memory. Keying on anything Revit could
        /// re-derive (path, title) is what let the memory answer for a different
        /// document; if Revit ever hands back a fresh wrapper the lookup misses and
        /// the answer degrades to 'unknown', which is the safe direction.
        /// </summary>
        private sealed class DocumentIdentity : IEqualityComparer<Document>
        {
            public bool Equals(Document a, Document b) { return ReferenceEquals(a, b); }
            public int GetHashCode(Document d)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d);
            }
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var operation = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            switch (operation)
            {
                case "inspect": return Inspect(app, request);
                case "open": return Open(app, request);
                case "save": return Save(app, request, false);
                case "save_as": return Save(app, request, true);
                case "close": return Close(app, request);
                default:
                    return CommandResult.Fail(
                        "operation is required and must be one of: inspect, open, save, save_as, close.");
            }
        }

        // =====================================================================
        // INSPECT — the whole point: know the version WITHOUT touching the file.
        // =====================================================================
        private static CommandResult Inspect(UIApplication app, JObject request)
        {
            var path = request.Value<string>("file_path");
            var pathError = ValidateInputPath(path);
            if (pathError != null) return CommandResult.Fail(pathError);

            var host = HostVersion(app);
            var probe = ProbeFile(path);

            return CommandResult.Ok(new JObject
            {
                ["operation"] = "inspect",
                ["opened"] = false,
                ["file"] = probe,
                ["host_version"] = host,
                ["versions_match"] = SameVersion(VersionOf(probe), host),
                ["note"] = "Read from disk only. This document was NOT opened, so nothing was upgraded."
            });
        }

        // =====================================================================
        // OPEN — the refusal gate.
        // =====================================================================
        private static CommandResult Open(UIApplication app, JObject request)
        {
            var path = request.Value<string>("file_path");
            var pathError = ValidateInputPath(path);
            if (pathError != null) return CommandResult.Fail(pathError);

            // Required, and required for a reason: it is the human's stated belief.
            // Without it there is nothing to disagree WITH — the file and the host
            // could both be 2025 and the caller could still be on the wrong bridge.
            var expected = NormalizeVersion(request.Value<string>("expected_version"));
            if (expected == null)
                return CommandResult.Fail(
                    "expected_version is required for open and must contain a Revit year (e.g. '2026'). " +
                    "It is the whole safety mechanism: opening a file on a newer host upgrades it irreversibly, " +
                    "and this tool has no way to know which bridge you meant to be on unless you say so.");

            var host = HostVersion(app);
            var probe = ProbeFile(path);
            var fileVersion = VersionOf(probe);

            bool allowUpgrade = request.Value<bool?>("allow_upgrade") ?? false;

            // "I could not look" is not "there is nothing there". If BasicFileInfo
            // could not read the version, we refuse — an unreadable version is
            // exactly when a blind open does the damage.
            if (fileVersion == null)
            {
                return CommandResult.Fail(
                    "Could not read this file's Revit version from disk (" + (probe.Value<string>("read_error") ?? "no reason given") + "). " +
                    "This is a refusal, not a failure to check: an unknown version is not a matching version, and " +
                    "opening it on the wrong host would upgrade it with no way back. Path: " + path);
            }

            bool hostMatches = SameVersion(host, expected);
            bool fileMatches = SameVersion(fileVersion, expected);

            if (!hostMatches)
            {
                // The wrong-bridge case, caught before the file is touched.
                return CommandResult.Fail(BuildDisagreement(path, fileVersion, host, expected,
                    "This Revit host is " + (host ?? "(unreadable)") + ", not " + expected + ". You are talking to the wrong bridge. " +
                    "Nothing was opened. Send this to the port running Revit " + expected + "."));
            }

            if (!fileMatches)
            {
                int fileYear, hostYear;
                bool older = TryYear(fileVersion, out fileYear) && TryYear(host, out hostYear) && fileYear < hostYear;

                if (!older)
                {
                    return CommandResult.Fail(BuildDisagreement(path, fileVersion, host, expected,
                        "The file on disk is Revit " + fileVersion + " and this host is " + host + ". A newer file cannot be " +
                        "opened by an older Revit at all, and allow_upgrade cannot help. Nothing was opened."));
                }

                if (!allowUpgrade)
                {
                    return CommandResult.Fail(BuildDisagreement(path, fileVersion, host, expected,
                        "The file on disk is Revit " + fileVersion + ". Opening it on this Revit " + host + " host UPGRADES it, and " +
                        "there is no downgrade — the " + fileVersion + " original stops existing the moment it is saved. " +
                        "Nothing was opened. If that is genuinely what you want, pass allow_upgrade=true and " +
                        "expected_version=\"" + host + "\", and back the file up first."));
                }
            }

            bool audit = request.Value<bool?>("audit") ?? false;
            bool detach = request.Value<bool?>("detach") ?? false;

            // Already open? Then no open happens and no upgrade happens; say which.
            var already = app.Application.Documents
                .Cast<Document>()
                .FirstOrDefault(d => !d.IsLinked && string.Equals(SafePath(d), path, StringComparison.OrdinalIgnoreCase));
            if (already != null)
            {
                return CommandResult.Ok(new JObject
                {
                    ["operation"] = "open",
                    ["status"] = "already_open",
                    ["opened_now"] = false,
                    ["upgraded"] = false,
                    ["title"] = SafeTitle(already),
                    ["path"] = SafePath(already),
                    ["is_workshared"] = WorksharedJson(SafeWorkshared(already)),
                    ["file"] = probe,
                    ["host_version"] = host,
                    ["expected_version"] = expected,
                    ["audit_ran"] = AuditState(app, already),
                    ["note"] = "This document was already open; this call did not open, upgrade or modify anything. " +
                               "audit_ran describes the open THIS tool performed, if it performed one."
                });
            }

            UIDocument uidoc;
            try
            {
                if (audit || detach)
                {
                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                    var opts = new OpenOptions();
                    opts.Audit = audit;
                    if (detach) opts.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    uidoc = app.OpenAndActivateDocument(modelPath, opts, false);
                }
                else
                {
                    uidoc = app.OpenAndActivateDocument(path);
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to open '" + path + "': " + ex.Message +
                    " (file is Revit " + fileVersion + ", host is Revit " + host + ")");
            }

            var doc = uidoc != null ? uidoc.Document : null;
            if (doc == null)
                return CommandResult.Fail("Revit returned no document for '" + path + "'. Nothing can be reported about it.");

            // The open HAPPENED. Record the audit flag before anything below can bail:
            // a refusal to *report* the open does not un-open the document, and if we
            // returned first the audit fact for a genuinely audited open would be lost
            // forever — the API cannot be asked for it again.
            _auditedOnOpen[doc] = audit;

            // OpenAndActivateDocument not throwing proves nothing about WHICH document
            // is now active — with two Revit instances alive, a blob that trusts it can
            // run against whatever the other instance had open. So prove identity.
            //
            // But NOT by path when detaching: a detached model has no PathName until it
            // is saved, so a path check there fails 100% of the time and would refuse
            // the one route this tool prescribes to a standalone deliverable
            // ("re-open with detach=true and save_as from there"). Its identity is its
            // title, which Revit derives from the file it detached from.
            var actualPath = SafePath(doc);
            var title = SafeTitle(doc);
            bool hasPath = !string.IsNullOrEmpty(actualPath);
            bool pathIsRequested = hasPath && string.Equals(actualPath, path, StringComparison.OrdinalIgnoreCase);

            bool titleIsRequested = TitleIdentifies(title, path);

            // Detach on a non-workshared file is a no-op in Revit, so a detach open can
            // legitimately come back with the original path. Accept either proof.
            bool detachedNoPath = detach && !hasPath;
            bool isRequested = pathIsRequested || (detachedNoPath && titleIsRequested);
            string identifiedBy = pathIsRequested
                ? "path"
                : (isRequested ? "title, matched exactly (detached: no path until saved)" : null);

            if (!isRequested)
            {
                // Do not imply nothing happened. A document is open right now, and on an
                // allow_upgrade open it has already been upgraded in memory. Saying
                // "nothing was opened" here is what sends someone looking in the wrong
                // place while the damage sits in the session.
                return CommandResult.Fail(
                    "A DOCUMENT WAS OPENED, but this tool cannot prove it is the file you asked for. Revit reports the active " +
                    "document as '" + (actualPath ?? "(no path)") + "' / title '" + (title ?? "(no title)") + "', and the request was " +
                    "'" + path + "'" + (detach ? " with detach=true" : "") + ". Refusing to report this as opened: every tool that runs " +
                    "next would silently target the wrong model. " +
                    (allowUpgrade
                        ? "allow_upgrade was true, so whatever opened may ALREADY have been upgraded in memory and must not be saved. "
                        : "") +
                    "Close it in the UI or name the document explicitly before continuing. The audit flag for this open (audit=" +
                    (audit ? "true" : "false") + ") was recorded against the document Revit returned.");
            }

            // Opening upgrades the IN-MEMORY document. The bytes on disk are still the
            // old version until something saves. Say that precisely — it is the last
            // moment the original can be rescued, and a vague answer wastes it.
            var afterProbe = ProbeFile(path);
            bool upgraded = !SameVersion(fileVersion, host);

            return CommandResult.Ok(new JObject
            {
                ["operation"] = "open",
                ["status"] = "opened",
                ["opened_now"] = true,
                ["title"] = title,
                ["path"] = hasPath ? actualPath : null,
                ["path_is_the_one_requested"] = pathIsRequested,
                ["identified_by"] = identifiedBy,
                ["path_note"] = detachedNoPath
                    ? "A detached model has no path until it is saved, so Revit reports none and this open was identified by " +
                      "title instead. Use save_as to give it one; save has nothing to write to."
                    : null,
                ["opened_from"] = path,
                ["is_workshared"] = WorksharedJson(SafeWorkshared(doc)),
                ["detached"] = detach,
                ["audit_ran"] = audit,
                ["audit_note"] = audit
                    ? "Audit was passed to OpenOptions.Audit. This is the only place the Revit API accepts it — a save cannot audit."
                    : "Not audited. Audit is an OPEN option in the Revit API; you cannot add it later by saving.",
                ["expected_version"] = expected,
                ["host_version"] = host,
                ["file_version_before_open"] = fileVersion,
                ["file_on_disk_now"] = afterProbe,
                ["upgraded"] = upgraded,
                ["upgrade_note"] = upgraded
                    ? "IRREVERSIBLE UPGRADE IN PROGRESS. This document was Revit " + fileVersion + " and is now open in Revit " + host +
                      ". The in-memory model has been upgraded; there is no downgrade. The bytes on disk are STILL " + fileVersion +
                      " until something saves — do not save unless losing the " + fileVersion + " original is the intent."
                    : null
            });
        }

        // =====================================================================
        // SAVE / SAVE_AS — 'saved' means the filesystem says so.
        // =====================================================================
        private static CommandResult Save(UIApplication app, JObject request, bool saveAs)
        {
            Document doc;
            string pick = PickDocument(app, request, out doc);
            if (pick != null) return CommandResult.Fail(pick);

            var sourcePath = SafePath(doc);
            bool compact = request.Value<bool?>("compact") ?? false;
            bool force = request.Value<bool?>("force_workshared") ?? false;

            // No sync. Not now, not behind a flag. A workshared save is still a write
            // to a file other people are standing on, and a central save is worse.
            //
            // Read ONCE into a bool?. The old line was
            // `bool workshared = SafeWorkshared(doc) is bool && (bool)SafeWorkshared(doc);`
            // against a JToken-returning SafeWorkshared, which is always false — so this
            // refusal never fired and central models were saved with no opt-in, and
            // sync_note below (gated on the same dead flag) was never emitted.
            bool? worksharedState = SafeWorkshared(doc);
            if (worksharedState != false && !force)
            {
                // null lands here too, deliberately: not being able to read IsWorkshared
                // is not a clearance to write a file others may be standing on. Same rule
                // as the unreadable-version refusal in Open and the unreadable pre-write
                // stat in the gate below.
                return CommandResult.Fail(
                    (worksharedState == true
                        ? "This document is WORKSHARED and force_workshared was not set. Refusing. "
                        : "Whether this document is workshared is UNKNOWN — Document.IsWorkshared could not be read — and " +
                          "force_workshared was not set. Refusing: an unreadable workshared state is not a non-workshared " +
                          "state, and this is the one write in this tool that cannot be undone. ") +
                    "On a local file this would write the local; on a central file it would write the central out from " +
                    "under everyone attached to it. " +
                    "Note this tool has no sync operation and will not get one — synchronizing to central is a human's call. " +
                    "If you want a standalone deliverable, re-open with detach=true and save_as from there. " +
                    "Document: " + (sourcePath ?? SafeTitle(doc)));
            }

            string targetPath;
            if (saveAs)
            {
                targetPath = request.Value<string>("save_as_path");
                if (string.IsNullOrWhiteSpace(targetPath))
                    return CommandResult.Fail("save_as_path is required for save_as.");
                if (!Path.IsPathRooted(targetPath))
                    return CommandResult.Fail("save_as_path must be an absolute rooted path: " + targetPath);
                var dir = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return CommandResult.Fail("Destination folder does not exist: " + (dir ?? "(none)") +
                                              ". Refusing to guess where this should go.");
            }
            else
            {
                targetPath = sourcePath;
                if (string.IsNullOrWhiteSpace(targetPath))
                    return CommandResult.Fail(
                        "This document has never been saved, so there is no path to save to and nothing to compare against. " +
                        "Use save_as with an explicit save_as_path.");
            }

            bool overwrite = request.Value<bool?>("overwrite") ?? false;
            if (saveAs && File.Exists(targetPath) && !overwrite)
                return CommandResult.Fail("Destination already exists and overwrite=false: " + targetPath +
                                          ". Refusing to replace a file nobody asked to replace.");

            var before = StatFile(targetPath);
            // Probe the file BEFORE the write, or there is no before-version and any
            // claim about whether this save upgraded the format is invented. This read
            // is the only chance to have it: after the write the old bytes are gone.
            var beforeProbe = ProbeFile(targetPath);
            var versionBefore = VersionOf(beforeProbe);

            try
            {
                if (saveAs)
                {
                    var opts = new SaveAsOptions();
                    opts.Compact = compact;
                    opts.OverwriteExistingFile = overwrite;
                    var maxBackups = request.Value<int?>("max_backups");
                    if (maxBackups.HasValue && maxBackups.Value >= 1) opts.MaximumBackups = maxBackups.Value;
                    doc.SaveAs(ModelPathUtils.ConvertUserVisiblePathToModelPath(targetPath), opts);
                }
                else
                {
                    var opts = new SaveOptions();
                    opts.Compact = compact;
                    doc.Save(opts);
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail((saveAs ? "SaveAs" : "Save") + " failed for '" + targetPath + "': " + ex.Message +
                    ". Nothing was written that this tool can vouch for — re-stat the path yourself before deleting any original.");
            }

            // THE LINE. Everything above is intent; only what follows is evidence.
            // The Python this replaces returned "OK -> " + newpath here, off nothing
            // but the absence of a throw, and a family died to a SaveAs that got
            // skipped while the delete line ran anyway.
            var after = StatFile(targetPath);
            var existsAfter = StatExists(after);

            if (existsAfter == false)
            {
                return CommandResult.Fail(
                    (saveAs ? "SaveAs" : "Save") + " raised no exception, but there is NO FILE at '" + targetPath + "'. " +
                    "The API call is not the evidence; the filesystem is. Do not delete any original on the strength of this call.");
            }
            if (existsAfter == null)
            {
                // "Could not look" is not "it is there". Everything below this point is
                // measured off the filesystem; with no stat there is nothing to measure.
                return CommandResult.Fail(
                    (saveAs ? "SaveAs" : "Save") + " raised no exception, but '" + targetPath + "' could not be stat-ed afterwards (" +
                    (after.Value<string>("stat_error") ?? "no reason given") + "), so whether a file is there at all is UNKNOWN. " +
                    "This tool reports 'saved' off the filesystem, not off the absence of a throw, and the filesystem did not answer. " +
                    "UNPROVEN, not saved: re-stat the path yourself and do not delete any original on the strength of this call.");
            }

            var readback = ProbeFile(targetPath);
            var readbackVersion = VersionOf(readback);

            if (readbackVersion == null)
            {
                return CommandResult.Fail(
                    "A file exists at '" + targetPath + "' but its Revit version cannot be read back off disk (" +
                    (readback.Value<string>("read_error") ?? "no reason given") + "). A file that BasicFileInfo cannot parse is not a " +
                    "verified save. Treat this output as unproven and do not delete any original.");
            }

            // THE GATE'S INPUT. The mtime gate below can only fire if we know what was at
            // this path BEFORE the write. A failed pre-write stat used to read as
            // "nothing was here", which silently disabled the gate and let the response
            // claim saved:true on evidence (exists + size + BasicFileInfo) that a file
            // sitting here untouched satisfies just as well. So a pre-write stat we could
            // not take is a refusal, not a fast path.
            var preExistingState = StatExists(before);
            if (!preExistingState.HasValue)
            {
                return CommandResult.Fail(
                    (saveAs ? "SaveAs" : "Save") + " was CALLED and may well have written '" + targetPath + "', but this tool cannot " +
                    "prove it either way: the path could not be stat-ed BEFORE the write (" +
                    (before.Value<string>("stat_error") ?? "no reason given") + "), so there is no before-mtime and no before-size to " +
                    "compare against. Existence, a size and a parseable BasicFileInfo are all satisfied by a file that was already " +
                    "sitting here, so without the before-stat nothing distinguishes a write that landed from one that went nowhere. " +
                    "UNPROVEN, not saved: re-stat the path yourself, and do not delete any original on the strength of this call.");
            }
            bool preExisting = preExistingState.Value;

            long? bytesBefore = preExisting ? before.Value<long?>("bytes") : null;
            long? bytesAfterNullable = after.Value<long?>("bytes");
            if (!bytesAfterNullable.HasValue)
                return CommandResult.Fail(
                    (saveAs ? "SaveAs" : "Save") + " raised no exception and a file exists at '" + targetPath + "', but its size could " +
                    "not be read (" + (after.Value<string>("stat_error") ?? "no reason given") + "). The size is part of the evidence " +
                    "this tool reports 'saved' on; without it there is nothing to report. UNPROVEN, not saved.");
            long bytesAfter = bytesAfterNullable.Value;
            long? delta = bytesBefore.HasValue ? (long?)(bytesAfter - bytesBefore.Value) : null;

            // Compact's entire purpose is the delta. If it did not move, the user is
            // owed that fact — it is the difference between 'compacted' and 'the flag
            // went in and nothing happened'.
            string compactNote = null;
            if (compact)
            {
                if (!bytesBefore.HasValue)
                    compactNote = "Compact was passed to the API, but there was no pre-existing file to compare against, " +
                                  "so there is no delta to prove it did anything.";
                else if (delta.Value == 0)
                    compactNote = "SUSPICIOUS: Compact was passed and the file is byte-identical in size (" + bytesAfter +
                                  " bytes). Either it was already compact or the flag did nothing. Reported rather than hidden.";
                else if (delta.Value > 0)
                    compactNote = "Compact was passed and the file GREW by " + delta.Value + " bytes. That is legal (new content " +
                                  "can outweigh reclaimed space) but it is not the outcome 'compact' implies.";
                else
                    compactNote = "Compact reclaimed " + Math.Abs(delta.Value) + " bytes.";
            }

            // mtime_changed is the RESULT OF A COMPARISON, so it only has a value where a
            // comparison ran. On a brand-new destination there was no earlier file and no
            // mtime moved — the file was created — and answering true there made
            // saved_evidence cite "an mtime that MOVED" for a check that never happened.
            // null = not compared; the evidence in that branch is that the pre-write stat
            // proved the path EMPTY and a file is there now.
            bool? mtimeChanged = null;
            if (preExisting)
                mtimeChanged = !string.Equals(before.Value<string>("modified_utc"),
                                              after.Value<string>("modified_utc"), StringComparison.Ordinal);

            // THE MTIME GATE. Every other element of the evidence — Exists, a nonzero
            // size, a parseable BasicFileInfo — is satisfied by a file that was already
            // sitting at this path before the call. Only the mtime distinguishes "we
            // wrote it" from "it was already there and the write went nowhere", which is
            // exactly the SaveAs-that-got-skipped this file was written for. It was
            // computed, reported, and never gated on; now it decides. It can only decide
            // because an unreadable before-stat was refused above instead of being read
            // as "nothing was here", which used to skip this gate entirely.
            if (preExisting && mtimeChanged == false)
            {
                return CommandResult.Fail(
                    (saveAs ? "SaveAs" : "Save") + " raised no exception and a file exists at '" + targetPath + "', but its " +
                    "LAST-WRITE TIME DID NOT MOVE (" + (before.Value<string>("modified_utc") ?? "?") + " before, " +
                    (after.Value<string>("modified_utc") ?? "?") + " after; " + (bytesBefore.HasValue ? bytesBefore.Value.ToString() : "?") +
                    " bytes before, " + bytesAfter + " after). That file was already there — its existence, its size and its " +
                    "BasicFileInfo prove nothing about THIS call. Either there was nothing to save or the write did not land. " +
                    "This is UNPROVEN, not saved: do not delete any original on the strength of it. Re-stat the path yourself, " +
                    "or make a change and save again.");
            }

            // Only reachable now: either the path was empty before, or the mtime moved.
            if (saveAs) PruneAuditMemory(app);

            // Whether this save upgraded the file is a BEFORE-vs-AFTER question about the
            // bytes on disk, and nothing else can answer it. The old field compared the
            // readback against the host, which a save always satisfies by definition —
            // so it read 'true' loudest in the one case that matters: a 2025 file opened
            // on the 2026 host with allow_upgrade, saved, its 2025 original now gone.
            // An upgrade we could not measure is null, never a clearance.
            // The evidence string must name the checks that ACTUALLY ran in this branch.
            // One sentence citing "an mtime that MOVED" was emitted on both paths, so on
            // every new-file save it advertised a comparison that had no left-hand side.
            string savedEvidence = preExisting
                ? "A pre-existing file at this path, plus File.Exists + a size + a parseable BasicFileInfo + an mtime that MOVED, all " +
                  "re-read from the filesystem after the write. Not 'the call did not throw'. The mtime is a gate, not a note: an " +
                  "unmoved one on a pre-existing file is refused above, because every other check here passes for a file that was " +
                  "already sitting at this path."
                : "The pre-write stat proved this path was EMPTY, and afterwards it holds a file with a size and a parseable " +
                  "BasicFileInfo, all re-read from the filesystem. Not 'the call did not throw'. No mtime comparison is claimed: " +
                  "there was no earlier file to compare against, so mtime_changed is null rather than true — the appearance of the " +
                  "file is the evidence here, and it only counts because the path was proven empty first.";

            var hostAfter = HostVersion(app);
            bool? upgradeOccurred = null;
            string upgradeNote;
            if (!preExisting)
            {
                upgradeNote = "UNKNOWN: there was no file at this path before the write, so there is no before-version to " +
                              "compare against. This says nothing about the document itself — if it was upgraded when it was " +
                              "OPENED, this save has now written that upgrade to disk. Check the open's upgrade_note.";
            }
            else if (versionBefore == null)
            {
                upgradeNote = "UNKNOWN: a file existed at this path before the write but its Revit version could not be read (" +
                              (beforeProbe.Value<string>("read_error") ?? "no reason given") + "), so whether this save changed " +
                              "its format cannot be established. Not being able to look is not a clearance.";
            }
            else
            {
                upgradeOccurred = !SameVersion(versionBefore, readbackVersion);
                upgradeNote = upgradeOccurred.Value
                    ? "IRREVERSIBLE UPGRADE, ALREADY WRITTEN. The file at this path was Revit " + versionBefore + " and now reads " +
                      "back as Revit " + readbackVersion + ". The " + versionBefore + " original that was here is gone and there is " +
                      "no downgrade. If a copy exists elsewhere, it is the only one."
                    : "The file at this path was Revit " + versionBefore + " before the write and reads back as Revit " +
                      readbackVersion + ": this save did not change its format.";
            }

            return CommandResult.Ok(new JObject
            {
                ["operation"] = saveAs ? "save_as" : "save",
                ["saved"] = true,
                ["saved_evidence"] = savedEvidence,
                ["source_path"] = sourcePath,
                ["path"] = targetPath,
                ["title"] = SafeTitle(doc),
                ["bytes_before"] = bytesBefore.HasValue ? (JToken)bytesBefore.Value : null,
                ["bytes_after"] = bytesAfter,
                ["bytes_delta"] = delta.HasValue ? (JToken)delta.Value : null,
                ["mb_before"] = bytesBefore.HasValue ? (JToken)Math.Round(bytesBefore.Value / 1048576.0, 2) : null,
                ["mb_after"] = Math.Round(bytesAfter / 1048576.0, 2),
                ["modified_utc_before"] = preExisting ? before["modified_utc"] : null,
                ["modified_utc_after"] = after["modified_utc"],
                ["mtime_changed"] = mtimeChanged.HasValue ? (JToken)mtimeChanged.Value : null,
                ["mtime_note"] = mtimeChanged.HasValue
                    ? null
                    : "null, not true: there was no file at this path before the write, so no mtime was compared. Nothing 'moved' — " +
                      "the file was created. The proof of this save is the pre-write stat showing the path empty, not this field.",
                ["compact_requested"] = compact,
                ["compact_flag_passed_to_api"] = compact,
                ["compact_note"] = compactNote,
                ["audit_ran"] = AuditState(app, doc),
                ["audit_note"] = "Audit is an OpenOptions flag in the Revit API; SaveOptions/SaveAsOptions have no Audit. " +
                                 "This value describes the OPEN of THIS document object, and only if this tool performed it — " +
                                 "null means 'unknown' (opened in the UI, or by someone else), which is not 'no'. There is no way " +
                                 "to re-read the flag, so an unknown here stays unknown forever; nothing infers it from the path. " +
                                 "To deliver an audited+compacted file, open with audit=true and save with compact=true.",
                // The SAME read the guard above decided on, not a fresh one. A second
                // SafeWorkshared call here is how the response came to say
                // is_workshared=true beside sync_note=null: two reads, one field
                // reporting workshared and the other silently branching on a flag that
                // was always false.
                ["is_workshared"] = WorksharedJson(worksharedState),
                ["synced_to_central"] = false,
                ["sync_note"] = worksharedState == true
                    ? "This document is workshared and was saved, NOT synchronized. No changes were relinquished and nothing " +
                      "reached other users. This tool does not sync. force_workshared was passed to reach this write."
                    : worksharedState == null
                        ? "Whether this document is workshared is UNKNOWN: Document.IsWorkshared could not be read. It was " +
                          "SAVED anyway because force_workshared was passed. If it was in fact workshared, this write landed " +
                          "on the local — or on the central — and nothing was synchronized or relinquished. This tool does " +
                          "not sync. Null here means nobody looked successfully, NOT that the document is non-workshared."
                        : null,
                ["file_on_disk_before"] = beforeProbe,
                ["version_on_disk_before"] = versionBefore,
                ["file_on_disk_after"] = readback,
                ["version_on_disk_after"] = readbackVersion,
                ["host_version"] = hostAfter,
                ["upgrade_occurred"] = upgradeOccurred.HasValue ? (JToken)upgradeOccurred.Value : null,
                ["upgrade_note"] = upgradeNote,
                ["version_note"] = SameVersion(readbackVersion, hostAfter)
                    ? "The saved file reads back as Revit " + readbackVersion + ", matching this host — expected, since a save " +
                      "always writes the host's format. That is why it says NOTHING about whether an upgrade happened: see " +
                      "upgrade_occurred, which compares the file's version before the write against its version now."
                    : "The saved file reads back as Revit " + readbackVersion + " on a Revit " + hostAfter + " host. That should " +
                      "not happen; do not trust this file until someone looks."
            });
        }

        // =====================================================================
        // CLOSE — the document is gone only if Revit no longer lists it.
        // =====================================================================
        private static CommandResult Close(UIApplication app, JObject request)
        {
            Document doc;
            string pick = PickDocument(app, request, out doc);
            if (pick != null) return CommandResult.Fail(pick);

            var path = SafePath(doc);
            var title = SafeTitle(doc);
            bool saveOnClose = request.Value<bool?>("save_on_close") ?? false;

            // Read BEFORE the close: doc is invalidated by Close() and IsWorkshared
            // cannot be asked afterwards, so this is the only chance to have it for the
            // response below. Same always-false defect as Save() lived here, which meant
            // save_on_close silently wrote a workshared file others were standing on.
            bool? worksharedState = SafeWorkshared(doc);
            if (saveOnClose && worksharedState != false && !(request.Value<bool?>("force_workshared") ?? false))
                return CommandResult.Fail(
                    (worksharedState == true
                        ? "save_on_close=true on a WORKSHARED document without force_workshared. "
                        : "save_on_close=true and whether this document is workshared is UNKNOWN — Document.IsWorkshared " +
                          "could not be read — without force_workshared. An unreadable workshared state is not a " +
                          "non-workshared state. ") +
                    "Refusing: a save-on-close here " +
                    "writes a file others depend on, as a side effect of closing. Save deliberately first, or pass force_workshared.");

            var before = StatFile(path);

            bool closed;
            try { closed = doc.Close(saveOnClose); }
            catch (Exception ex)
            {
                return CommandResult.Fail("Close failed for '" + (title ?? path) + "': " + ex.Message +
                    ". The document is still open. (Revit refuses to close the last open document, and refuses to close a " +
                    "document that has open views it cannot discard.)");
            }

            // Close() returns a bool the old code discarded, and even that is the API's
            // word. The model's word is the Documents collection — compared by OBJECT
            // IDENTITY, never by path. A path check here was vacuous or wrong and never
            // in between: SafePath returns null when the read threw (matching nothing,
            // so 'closed' would be an assertion about a comparison that never ran) and
            // "" for a detached or never-saved document (matching every OTHER unsaved
            // document, failing a close that succeeded).
            bool stillListed = app.Application.Documents
                .Cast<Document>()
                .Any(d => ReferenceEquals(d, doc));

            // Second, independent probe: Revit invalidates the Document object on close.
            // null = the probe itself threw, which is not evidence of anything.
            bool? stillValid;
            try { stillValid = doc.IsValidObject; }
            catch { stillValid = null; }

            var after = StatFile(path);
            if (!stillListed) _auditedOnOpen.Remove(doc);

            if (stillListed || stillValid == true)
            {
                return CommandResult.Fail(
                    "Close returned " + closed + " but this document is still open: Revit " +
                    (stillListed ? "still lists this exact Document object" : "no longer lists it, yet the object is still valid") +
                    " ('" + (title ?? path ?? "(unidentifiable)") + "'). Reporting this as closed would strand every caller that " +
                    "waits on it. Nothing further was done.");
            }

            // Same tri-state as Save reads, for the same reason: a stat that could not be
            // taken is not a stat that found nothing. disk_changed is a comparison of two
            // mtimes, so it only has a value when both sides were actually measured;
            // otherwise it is null and the note says the write is UNKNOWN. Folding that
            // into false would tell someone their save_on_close did not land when in fact
            // nobody looked.
            var beforeExists = StatExists(before);
            var afterExists = StatExists(after);
            long? bytesBefore = beforeExists == true ? before.Value<long?>("bytes") : null;
            long? bytesAfter = afterExists == true ? after.Value<long?>("bytes") : null;

            bool? diskChanged = null;
            if (beforeExists == true && afterExists == true)
                diskChanged = !string.Equals(before.Value<string>("modified_utc"),
                                             after.Value<string>("modified_utc"), StringComparison.Ordinal);
            else if (beforeExists == false && afterExists == false)
                diskChanged = false;

            string diskNote;
            if (!diskChanged.HasValue)
                diskNote = "UNKNOWN, not 'no write': this path could not be measured on both sides of the close (" +
                           (before.Value<string>("stat_error") ?? after.Value<string>("stat_error") ?? "the file appeared or vanished between the two stats") +
                           "), so no mtime comparison was possible." +
                           (saveOnClose
                               ? " save_on_close was requested, so a write may well have landed. Re-stat the path yourself."
                               : " No save was requested, so nothing here was expected to change — but this field is not evidence of that.");
            else if (saveOnClose && diskChanged == false)
                diskNote = "save_on_close was requested but the file's mtime did not move. Either there was nothing to save or the " +
                           "save did not land. Do not treat this as a confirmed write.";
            else
                diskNote = null;

            return CommandResult.Ok(new JObject
            {
                ["operation"] = "close",
                ["closed"] = true,
                ["closed_evidence"] = "This exact Document object is no longer in Application.Documents — compared by object " +
                                      "identity, not by path, because a null or empty PathName would have matched nothing and the " +
                                      "check would have passed without comparing anything" +
                                      (stillValid == false
                                          ? ", and doc.IsValidObject is now false."
                                          : ". (doc.IsValidObject could not be read, so the Documents list is the only witness here.)") +
                                      " Not just 'Close() returned true'.",
                ["api_returned"] = closed,
                ["title"] = title,
                ["path"] = path,
                ["saved_on_close"] = saveOnClose,
                ["file_exists_after"] = afterExists.HasValue ? (JToken)afterExists.Value : null,
                ["file_exists_after_unknown_reason"] = afterExists.HasValue
                    ? null
                    : (JToken)(after.Value<string>("stat_error") ?? "the path could not be stat-ed after the close"),
                ["bytes_before"] = bytesBefore.HasValue ? (JToken)bytesBefore.Value : null,
                ["bytes_after"] = bytesAfter.HasValue ? (JToken)bytesAfter.Value : null,
                ["modified_utc_after"] = after["modified_utc"],
                ["disk_changed"] = diskChanged.HasValue ? (JToken)diskChanged.Value : null,
                ["disk_note"] = diskNote,
                // Read before the close, because the Document is invalid now. The
                // response used to carry no workshared caveat at all, so a forced
                // save_on_close of a central model read as an ordinary close.
                ["is_workshared"] = WorksharedJson(worksharedState),
                ["synced_to_central"] = false,
                ["sync_note"] = !saveOnClose
                    ? null
                    : worksharedState == true
                        ? "This document is workshared and was SAVED on close, NOT synchronized. No changes were relinquished " +
                          "and nothing reached other users. force_workshared was passed to reach this write. This tool does not sync."
                        : worksharedState == null
                            ? "Whether this document was workshared is UNKNOWN: Document.IsWorkshared could not be read before " +
                              "the close, and it cannot be asked now. It was SAVED on close because force_workshared was passed. " +
                              "If it was workshared, nothing was synchronized or relinquished. This tool does not sync."
                            : null,
                ["file_on_disk_after"] = ProbeFile(path)
            });
        }

        // =====================================================================
        // Reading the disk. Each of these says when it could not look.
        // =====================================================================

        /// <summary>
        /// The version of a Revit file, read from its header WITHOUT opening it —
        /// the only way to know before the damage. A read that fails returns a
        /// read_error, never a blank that reads like "no problem found".
        /// </summary>
        private static JObject ProbeFile(string path)
        {
            var result = new JObject();
            result["path"] = path;

            if (string.IsNullOrWhiteSpace(path))
            {
                // No path is not an empty disk. Close() reaches here when Document.PathName
                // threw, and answering false there would assert about a file nobody located.
                result["exists"] = null;
                result["read_error"] = "No path was given, so nothing was read: whether a file exists is UNKNOWN, not false.";
                return result;
            }

            bool exists;
            try { exists = File.Exists(path); }
            catch (Exception ex)
            {
                result["exists"] = null;
                result["read_error"] = "Could not test the path: " + ex.Message;
                return result;
            }

            result["exists"] = exists;
            if (!exists)
            {
                result["read_error"] = "File does not exist.";
                return result;
            }

            var stat = StatFile(path);
            result["bytes"] = stat["bytes"];
            result["mb"] = stat["mb"];
            result["modified_utc"] = stat["modified_utc"];
            // The file is there (File.Exists said so above) but the stat failed, so the
            // nulls in bytes/mb/modified_utc are "could not measure", not "zero-length,
            // never written". Say which, or the reader picks the wrong one.
            if (stat["stat_error"] != null)
                result["stat_error"] = stat["stat_error"];

            try
            {
                var info = BasicFileInfo.Extract(path);
                if (info == null)
                {
                    result["read_error"] = "BasicFileInfo.Extract returned nothing. This is probably not a Revit file.";
                    return result;
                }
                try
                {
                    result["format"] = Safe(delegate { return info.Format; });
                    // SavedInVersion exists on the 2022 API and is gone by 2026. Reflection
                    // keeps one source file for both WITHOUT pretending the property was
                    // read and came back empty on the version that does not have it.
                    result["saved_in_version"] = ReflectString(info, "SavedInVersion");
                    result["is_workshared"] = SafeBool(delegate { return info.IsWorkshared; });
                    result["is_central"] = SafeBool(delegate { return info.IsCentral; });
                    result["is_local"] = SafeBool(delegate { return info.IsLocal; });
                    result["central_path"] = Safe(delegate { return info.CentralPath; });
                    result["revit_version"] = NormalizeVersion(result.Value<string>("format")) ??
                                              NormalizeVersion(result.Value<string>("saved_in_version"));
                    if (result["revit_version"] == null || result["revit_version"].Type == JTokenType.Null)
                        result["read_error"] = "BasicFileInfo read the file but no Revit year could be parsed from it " +
                                               "(format='" + (result.Value<string>("format") ?? "") + "', saved_in_version='" +
                                               (result.Value<string>("saved_in_version") ?? "") + "').";
                }
                finally
                {
                    var d = info as IDisposable;
                    if (d != null) { try { d.Dispose(); } catch { } }
                }
            }
            catch (Exception ex)
            {
                // NOT swallowed. An unreadable version is the exact condition under
                // which an unguarded open destroys the file, so it must surface.
                result["read_error"] = "BasicFileInfo could not read this file: " + ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Stat a path, tri-state. exists is true / false / null, where null is "the stat
        /// failed, so nobody knows" — NOT false.
        ///
        /// This used to answer exists=false when the stat THREW, which is the same value
        /// it answers for an empty path, and this JObject is what the mtime gate in Save()
        /// reads. An existing destination whose stat threw came back preExisting=false, the
        /// gate concluded there was nothing to compare and switched itself off, and the
        /// response shipped saved:true plus a compact_note asserting "there was no
        /// pre-existing file" — about a file that was there. ProbeFile has answered
        /// exists=null on the same failure since it was written; this is that rule applied
        /// at the site that actually decides.
        /// </summary>
        private static JObject StatFile(string path)
        {
            var o = new JObject();
            o["exists"] = null;
            o["bytes"] = null;
            o["mb"] = null;
            o["modified_utc"] = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                o["stat_error"] = "No path was given, so nothing was stat-ed: whether a file exists is UNKNOWN, not false.";
                return o;
            }
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { o["exists"] = false; return o; }
                o["exists"] = true;
                o["bytes"] = fi.Length;
                o["mb"] = Math.Round(fi.Length / 1048576.0, 2);
                o["modified_utc"] = fi.LastWriteTimeUtc.ToString("o");
            }
            catch (Exception ex)
            {
                o["exists"] = null;
                o["bytes"] = null;
                o["mb"] = null;
                o["modified_utc"] = null;
                o["stat_error"] = "Could not stat this path (" + ex.Message + "), so whether a file exists here is UNKNOWN, not false.";
            }
            return o;
        }

        /// <summary>
        /// The one way to read StatFile's exists. null = the stat could not be taken.
        /// Callers must branch on all three; a bare Value&lt;bool&gt;() here would both throw
        /// on the null and re-collapse "could not look" into "not there".
        /// </summary>
        private static bool? StatExists(JObject stat)
        {
            if (stat == null) return null;
            var t = stat["exists"];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Value<bool>();
        }

        // =====================================================================
        // Small helpers.
        // =====================================================================

        private static string ValidateInputPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "file_path is required.";
            if (!Path.IsPathRooted(path)) return "file_path must be an absolute rooted path: " + path;
            if (!File.Exists(path)) return "File not found: " + path;
            return null;
        }

        /// <summary>
        /// The host's own version, read from the API. Never the caller's
        /// expected_version echoed back — echoing the input is how a check that
        /// never happened looks identical to one that passed.
        /// </summary>
        private static string HostVersion(UIApplication app)
        {
            try { return NormalizeVersion(app.Application.VersionNumber); }
            catch { return null; }
        }

        private static string VersionOf(JObject probe)
        {
            if (probe == null) return null;
            var t = probe["revit_version"];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Value<string>();
        }

        /// <summary>"2026", "Autodesk Revit 2026 (Build ...)" and " 2026 " are one year.</summary>
        private static string NormalizeVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = Regex.Match(raw, @"(20\d{2})");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool SameVersion(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static bool TryYear(string v, out int year)
        {
            year = 0;
            return v != null && int.TryParse(v, out year);
        }

        /// <summary>All three numbers, always — the caller cannot act on "mismatch".</summary>
        private static string BuildDisagreement(string path, string fileVersion, string host, string expected, string why)
        {
            return "REFUSED, nothing opened. " + why + "\n" +
                   "  file_on_disk : Revit " + (fileVersion ?? "(unreadable)") + "  (" + path + ")\n" +
                   "  this_host    : Revit " + (host ?? "(unreadable)") + "\n" +
                   "  you_expected : Revit " + expected + "\n" +
                   "All three must agree before this tool will open anything, because the open is the point of no return.";
        }

        /// <summary>
        /// null means "this tool did not open it, so it does not know" — which is not
        /// false. A cache miss is never manufactured into a negative fact: audit is the
        /// one flag the API can never re-read, so "Not audited" said about an open we
        /// did not perform is a claim nothing on earth could back up.
        /// </summary>
        private static JToken AuditState(UIApplication app, Document doc)
        {
            PruneAuditMemory(app);
            if (doc == null) return null;
            bool v;
            if (!_auditedOnOpen.TryGetValue(doc, out v)) return null;
            // The entry belongs to a Document object. If Revit no longer lists that
            // object, the memory is of a dead document instance and must not answer
            // for whatever is standing at its path now.
            if (!IsLive(app, doc)) { _auditedOnOpen.Remove(doc); return null; }
            return v;
        }

        /// <summary>Is this exact Document object still one Revit is holding open?</summary>
        private static bool IsLive(UIApplication app, Document doc)
        {
            try
            {
                if (!doc.IsValidObject) return false;
                return app.Application.Documents.Cast<Document>().Any(d => ReferenceEquals(d, doc));
            }
            catch { return false; }
        }

        /// <summary>
        /// Drop entries for documents Revit has closed behind our back (the UI can close
        /// a document without this handler ever hearing about it). Without this the
        /// dictionary is a process-lifetime leak AND a source of stale answers.
        /// </summary>
        private static void PruneAuditMemory(UIApplication app)
        {
            List<Document> dead = null;
            foreach (var kv in _auditedOnOpen)
            {
                if (IsLive(app, kv.Key)) continue;
                if (dead == null) dead = new List<Document>();
                dead.Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var d in dead) _auditedOnOpen.Remove(d);
        }

        /// <summary>Which open document this operates on. Ambiguity is an error, not a guess.</summary>
        private static string PickDocument(UIApplication app, JObject request, out Document doc)
        {
            doc = null;
            var wanted = request.Value<string>("file_path");

            if (!string.IsNullOrWhiteSpace(wanted))
            {
                if (!Path.IsPathRooted(wanted)) return "file_path must be an absolute rooted path: " + wanted;
                var matches = app.Application.Documents
                    .Cast<Document>()
                    .Where(d => { try { return !d.IsLinked && string.Equals(d.PathName, wanted, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                    .ToList();
                if (matches.Count == 0)
                    return "No open document has the path '" + wanted + "'. Open it first — this tool will not fall back to the " +
                           "active document, because saving the wrong model is the mistake it exists to prevent.";
                if (matches.Count > 1)
                    return "More than one open document reports the path '" + wanted + "'. Refusing to pick one.";
                doc = matches[0];
                return null;
            }

            var active = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            if (active == null)
                return "No document is open (and no file_path was given to identify one).";
            doc = active;
            return null;
        }

        /// <summary>
        /// Title identity for a detached open, where there is no path to compare and the
        /// title is the only witness. This MUST be an exact match against the closed set
        /// of titles Revit derives from a file. A prefix test ("does the title start with
        /// the requested base name") accepts a DIFFERENT model: ask for 'Torre.rvt' while
        /// the other Revit instance has 'Torre_A_detached' active and the prefix passes,
        /// the response says status=opened / opened_from=Torre.rvt, and every tool that
        /// runs next targets Torre_A — which is precisely the two-instance confusion this
        /// check exists to stop. Unproven identity must be refused, not approximated.
        /// </summary>
        private static bool TitleIdentifies(string title, string requestedPath)
        {
            if (string.IsNullOrEmpty(title) || string.IsNullOrWhiteSpace(requestedPath)) return false;
            var baseName = Path.GetFileNameWithoutExtension(requestedPath);
            if (string.IsNullOrEmpty(baseName)) return false;
            var fileName = Path.GetFileName(requestedPath);
            var ext = Path.GetExtension(requestedPath) ?? "";

            // Revit titles a detached document after the file it detached from, with or
            // without the extension, and some versions append '_detached'. Nothing else.
            var candidates = new List<string>
            {
                baseName,
                fileName,
                baseName + "_detached",
                baseName + "_detached" + ext
            };
            foreach (var c in candidates)
                if (string.Equals(title, c, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafePath(Document d) { try { return d.PathName; } catch { return null; } }
        /// <summary>
        /// Tri-state: true / false / null, where null is "the read threw, so nobody
        /// knows" — NOT false.
        ///
        /// This returned JToken, and both callers tested it with
        /// `SafeWorkshared(doc) is bool && (bool)SafeWorkshared(doc)`. That is ALWAYS
        /// false: the bool is implicitly converted to a JValue on return, and `is bool`
        /// considers only identity/boxing conversions — never JValue's user-defined
        /// explicit operator bool. So the workshared refusal in Save() and Close() was
        /// unreachable and a CENTRAL model was saved with no opt-in, which is the one
        /// write this file says it refuses by default. bool? is the return type because
        /// the type system then makes that mistake impossible to write: there is no
        /// silent conversion from bool? to bool, and no branch that forgets the null.
        /// </summary>
        private static bool? SafeWorkshared(Document d) { try { return d.IsWorkshared; } catch { return null; } }

        /// <summary>The tri-state as JSON: true / false / null, null meaning unknown.</summary>
        private static JToken WorksharedJson(bool? state)
        {
            return state.HasValue ? (JToken)state.Value : null;
        }

        /// <summary>
        /// Read a property that only some Revit versions have. Returns null when the
        /// property genuinely read as null, and an explicit "(not available...)" string
        /// when this API version has no such property — those are different facts and
        /// collapsing them into null would be a small lie in a tool built to not tell any.
        /// </summary>
        private static JToken ReflectString(object target, string propertyName)
        {
            if (target == null) return null;
            try
            {
                var prop = target.GetType().GetProperty(propertyName);
                if (prop == null)
                    return "(not available in this Revit API version)";
                var v = prop.GetValue(target, null);
                return v == null ? null : (JToken)v.ToString();
            }
            catch (Exception ex)
            {
                return "(unreadable: " + ex.Message + ")";
            }
        }

        private static JToken Safe(Func<string> read)
        {
            try { return read(); } catch { return null; }
        }

        private static JToken SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return null; }
        }
    }
}
