// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_document_session — the PERSIST verb. The guarded superset of
// open_document (which stays; this does not replace it in place).
//
// This is the only tool in the estate whose mistakes cannot be undone. Everything
// else writes into a model you can roll back. This one touches files on disk, and
// two of its failure modes destroy work permanently:
//
//   1. THE UPGRADE. It is normal to run two Revit versions side by side (say
//      2025 and 2026), each with its own host process and its own transport.
//      Opening a 2025 .rfa on the 2026 host upgrades it to 2026 — and
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
//     usual delivery requirement is "a .rvt saved with Audit + Compact", and the
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

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public class DocumentSessionCommand : ICommand
    {
        public string Name => "horizun_document_session";

        public string Description =>
            "Open / save / save_as / close a Revit document, guarded against the irreversible. Before opening it " +
            "reads the file's own Revit version off disk (BasicFileInfo, WITHOUT opening it) and the host's " +
            "version, and refuses unless both match the REQUIRED expected_version — because opening a 2025 file " +
            "on a 2026 host upgrades it and there is no downgrade. Saving reports bytes/mtime/format re-read from " +
            "the filesystem after the write, never 'it did not throw'. Audit is an OPEN option in the Revit API, " +
            "so audit_ran only ever describes the open. It never syncs to central.";

        // The schema the client sees lives in Horizun.Contracts.Contract, once, shared
        // with the server. This property exists because ICommand declares it; pointing it
        // at the same contract is the only way to be sure the two cannot drift - which is
        // the failure this whole file has been correcting all week, in another form.
        public string ParametersSchema =>
            Horizun.Contracts.Contract.Find("horizun_document_session")?
                   .InputSchema?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";

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
            bool wantsCloud = !string.IsNullOrWhiteSpace(request.Value<string>("cloud_project_guid")) ||
                              !string.IsNullOrWhiteSpace(request.Value<string>("cloud_model_guid"));

            // A cloud model has no local path to validate. Everything ELSE about it is
            // checked by the same guards, in the same order, as a file on disk.
            if (!wantsCloud)
            {
                var pathError = ValidateInputPath(path);
                if (pathError != null) return CommandResult.Fail(pathError);
            }

            // EVERY GUARD, from the one place both open commands now read them. This block
            // used to be written out here and again in OpenDocumentCommand, months apart,
            // and the two copies had diverged: THIS one had no central guard at all, so the
            // tool whose description promises it is "guarded against the irreversible" was
            // the one that would open the model everybody synchronizes to without a word.
            // It also could not open a cloud model, so the only route to one was through
            // the command that does not take expected_version. See Core/OpenGuard.cs.
            var openRequest = new OpenRequest
            {
                CommandName = "horizun_document_session",
                Path = wantsCloud ? null : path,
                CloudProjectGuid = request.Value<string>("cloud_project_guid"),
                CloudModelGuid = request.Value<string>("cloud_model_guid"),
                CloudRegion = request.Value<string>("cloud_region"),
                ExpectedVersion = request.Value<string>("expected_version"),
                ExpectedVersionRequired = true,
                AllowUpgrade = request.Value<bool?>("allow_upgrade") ?? false,
                Detach = request.Value<bool?>("detach") ?? false,
                Audit = request.Value<bool?>("audit") ?? false,
                OpenCentral = request.Value<bool?>("open_central") ?? false,
                OpenAllWorksets = request.Value<bool?>("open_all_worksets") ?? false
            };

            OpenPlan plan = OpenGuard.Check(app, openRequest);
            if (!plan.Ok) return plan.Refusal;

            // Everything below is REPORTING. The version comparison, the wrong-bridge
            // check, the unreadable-header refusal and the newer-file rule all happened
            // inside OpenGuard.Check above - once, in the version both commands run.
            var expected = OpenGuard.NormalizeVersion(request.Value<string>("expected_version"));
            var host = plan.HostVersion;
            bool audit = openRequest.Audit;
            bool detach = openRequest.Detach;

            if (plan.IsCloud) return OpenCloud(app, plan, openRequest, expected);

            var probe = ProbeFile(path);
            var fileVersion = plan.FileVersion;

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
                // ONE overload, always, with options built by the shared guard. This used
                // to take the bare-path overload whenever neither audit nor detach was
                // set, which silently meant open_all_worksets could never be honoured
                // here at all - the flag was accepted and dropped.
                uidoc = app.OpenAndActivateDocument(plan.ModelPath, plan.Options(), false);
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
            // is now active — with two Revit instances alive, a script that trusts it can
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
            //
            // WHAT THIS USED TO GET WRONG, measured on Revit 2025.4 rather than assumed:
            // the title branch was gated on the detached document having NO PathName
            // ("a detached model has no PathName until it is saved"). That is not what
            // Revit does. Detaching <a workshared model>.rvt came back with a synthetic
            // PathName of "<a workshared model>_detached.rvt" - non-empty, so hasPath was
            // true, so the title branch never ran, so the path comparison decided it -
            // and a detached path can never equal the requested one. Every detach open
            // therefore failed a check it could not pass by construction, while the
            // document sat open and active behind the refusal. detach + open, the one
            // combination this tool prescribes for reading a central safely, was the one
            // combination it refused.
            //
            // The proof that matters under detach is the TITLE (TitleIdentifies already
            // accepts the '_detached' suffix); a path that Revit invented for a file that
            // does not exist is not evidence of anything. A detach open that activated
            // some OTHER document still fails, because its title would not match.
            bool isRequested = pathIsRequested || (detach && titleIsRequested);
            string identifiedBy = pathIsRequested
                ? "path"
                : (isRequested
                    ? (hasPath
                        ? "title (detached: Revit's PathName is synthetic, not a file that exists)"
                        : "title, matched exactly (detached: no path until saved)")
                    : null);

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
                    (openRequest.AllowUpgrade
                        ? "allow_upgrade was true, so whatever opened may ALREADY have been upgraded in memory and must not be saved. "
                        : "") +
                    "Close it in the UI or name the document explicitly before continuing. The audit flag for this open (audit=" +
                    (audit ? "true" : "false") + ") was recorded against the document Revit returned.");
            }

            // Opening upgrades the IN-MEMORY document. The bytes on disk are still the
            // old version until something saves. Say that precisely — it is the last
            // moment the original can be rescued, and a vague answer wastes it.
            var afterProbe = ProbeFile(path);
            bool upgraded = plan.WillUpgrade;

            return CommandResult.Ok(new JObject
            {
                ["operation"] = "open",
                ["status"] = "opened",
                ["opened_now"] = true,
                ["title"] = title,
                ["path"] = hasPath ? actualPath : null,
                ["path_is_the_one_requested"] = pathIsRequested,
                ["identified_by"] = identifiedBy,
                ["path_note"] = (detach && !hasPath)
                    ? "A detached model has no path until it is saved, so Revit reports none and this open was identified by " +
                      "title instead. Use save_as to give it one; save has nothing to write to."
                    : ((detach && !pathIsRequested)
                        ? "The path above is SYNTHETIC. Revit names a detached document '<original>_detached.rvt' and " +
                          "reports that as its PathName, but no such file exists on disk - so this open was identified " +
                          "by title, not by path, and 'path_is_the_one_requested' is false for a document that IS the " +
                          "one you asked for. Use save_as to give it a real path; save has nothing to write to."
                        : null),
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
                    : null,
                // WHICH guards ran, from the shared guard's own record rather than from
                // this file's memory of what it checked. The two used to differ.
                ["version_guard"] = plan.VersionGuard,
                ["was_central"] = plan.FileIsCentral.HasValue ? (JToken)plan.FileIsCentral.Value : null,
                ["central_guard"] = plan.FileIsCentral == true
                    ? (detach ? "detached" : "open_central")
                    : (plan.FileIsCentral == false ? "not_a_central" : "unreadable, waived by detach or open_central"),
                ["central_path"] = plan.CentralPath,
                ["all_worksets_opened"] = openRequest.OpenAllWorksets
            });
        }

        // =====================================================================
        // OPEN, CLOUD — a model in ACC / BIM 360, named by GUID.
        //
        // This tool could not do it at all, so the only way to reach a cloud model
        // was horizun_open_document - the command that does NOT take
        // expected_version. The wrong-bridge check was therefore unavailable for
        // exactly the models most likely to be opened from a batch across several
        // Revit years. Same guards as the local path now; what differs is only what
        // can be KNOWN, and the response says which of the two it is.
        // =====================================================================
        private static CommandResult OpenCloud(UIApplication app, OpenPlan plan, OpenRequest r, string expected)
        {
            // Written BEFORE the call: opening a cloud model can take Revit down with an
            // access violation inside its own loader, and a dead process reports nothing.
            // A line here with no result after it names the model that did it.
            Log.Warn("document_session OPEN CLOUD attempting region=" + plan.Region +
                     " project=" + plan.CloudProject + " model=" + plan.CloudModel +
                     (r.Detach ? " detached" : "") + (r.OpenAllWorksets ? " all-worksets" : "") +
                     " - if no result line follows this one, opening this model took Revit down.");

            UIDocument uidoc;
            try { uidoc = app.OpenAndActivateDocument(plan.ModelPath, plan.Options(), false); }
            catch (Exception ex)
            {
                Log.Warn("document_session OPEN CLOUD refused model=" + plan.CloudModel + ": " + ex.Message);
                return CommandResult.Fail(
                    "Revit refused to open the cloud model: " + ex.Message + " (region '" + plan.Region +
                    "', project " + plan.CloudProject + ", model " + plan.CloudModel + "). 'The central model is " +
                    "missing' usually means the GUIDs are not this Revit's - the URN from the ACC web UI is a " +
                    "different identifier and does not convert. Nothing was opened.");
            }

            var doc = uidoc != null ? uidoc.Document : null;
            if (doc == null)
                return CommandResult.Fail(
                    "Revit returned no document for cloud model " + plan.CloudModel + ". Nothing is confirmed open.");

            // Before anything below can bail: a refusal to REPORT the open does not
            // un-open the document, and the audit flag cannot be asked for again.
            _auditedOnOpen[doc] = r.Audit;

            Document nowActive = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            bool confirmed = OpenGuard.SameDocument(nowActive, doc);

            return CommandResult.Ok(new JObject
            {
                ["operation"] = "open",
                ["status"] = confirmed ? "opened" : "opened_but_not_active",
                ["opened_now"] = true,
                ["source"] = "cloud",
                ["title"] = SafeTitle(doc),
                // A cloud document's PathName is not a file that can be opened again.
                ["path"] = null,
                ["cloud_region"] = plan.Region,
                ["cloud_project_guid"] = plan.CloudProject.ToString(),
                ["cloud_model_guid"] = plan.CloudModel.ToString(),
                ["is_workshared"] = WorksharedJson(SafeWorkshared(doc)),
                ["detached"] = r.Detach,
                ["audit_ran"] = r.Audit,
                ["all_worksets_opened"] = r.OpenAllWorksets,
                ["expected_version"] = expected,
                ["host_version"] = plan.HostVersion,
                ["file_version_before_open"] = null,
                ["upgraded"] = null,
                ["version_guard"] = plan.VersionGuard,
                ["version_guard_note"] =
                    "The upgrade guard did NOT run. It reads the saved version out of the file with BasicFileInfo, " +
                    "and a cloud model has no local file to read before it is open - so its version was UNKNOWN at " +
                    "the moment of opening, not verified. expected_version was still checked against this HOST, " +
                    "which is the wrong-bridge case and the one this command exists for. 'upgraded' is null, not " +
                    "false: nobody looked.",
                ["central_guard"] = r.Detach ? "detached" : "open_central",
                ["central_guard_note"] =
                    "A model in ACC / BIM 360 IS the central model. It was opened " +
                    (r.Detach
                        ? "DETACHED, so nothing here can reach the model the team synchronizes to."
                        : "DIRECTLY, because open_central=true was passed. Anything saved from this session writes " +
                          "to the model the team synchronizes to."),
                ["note"] = confirmed
                    ? null
                    : "Revit opened a document but it is NOT the active one. Do not run anything that assumes the " +
                      "active document is the model you named."
            });
        }

        // =====================================================================
        // SAVE / SAVE_AS — 'saved' means the filesystem says so.
        // =====================================================================
        private static CommandResult Save(UIApplication app, JObject request, bool saveAs)
        {
            Document doc;
            string pick = PickDocument(app, request, out doc, requireExplicitTarget: true);
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
            // Closing discards whatever is unsaved in it. Naming is required for the
            // same reason as save: not for what it writes, but for what it can lose.
            string pick = PickDocument(app, request, out doc, requireExplicitTarget: true);
            if (pick != null) return CommandResult.Fail(pick);

            var path = SafePath(doc);
            var title = SafeTitle(doc);
            bool saveOnClose = request.Value<bool?>("save_on_close") ?? false;
            bool discardUnsaved = request.Value<bool?>("discard_unsaved") ?? false;
            bool dryRun = request.Value<bool?>("dry_run") ?? false;
            string suppliedToken = request.Value<string>("confirmation_token");

            // Read BEFORE the close: doc is invalidated by Close() and IsWorkshared
            // cannot be asked afterwards, so this is the only chance to have it for the
            // response below. Same always-false defect as Save() lived here, which meant
            // save_on_close silently wrote a workshared file others were standing on.
            bool? worksharedState = SafeWorkshared(doc);

            // MEASURED, and measured HERE, because Document.IsModified cannot be asked of
            // a closed document either. Tri-state for the same reason as IsWorkshared: a
            // read that threw is not a document with nothing to lose.
            bool? isModified = SafeModified(doc);
            if (saveOnClose && worksharedState != false && !(request.Value<bool?>("force_workshared") ?? false))
                return CommandResult.Fail(
                    (worksharedState == true
                        ? "save_on_close=true on a WORKSHARED document without force_workshared. "
                        : "save_on_close=true and whether this document is workshared is UNKNOWN — Document.IsWorkshared " +
                          "could not be read — without force_workshared. An unreadable workshared state is not a " +
                          "non-workshared state. ") +
                    "Refusing: a save-on-close here " +
                    "writes a file others depend on, as a side effect of closing. Save deliberately first, or pass force_workshared.");

            // =============================================================
            // THE UNSAVED-WORK GUARD.
            //
            // doc.Close(false) discards everything unsaved in the document, returns true,
            // and there is nothing afterwards to tell you it happened - IsModified cannot
            // be asked of a closed document, and the file on disk is untouched, so every
            // signal this handler already collected reports a clean, successful close. An
            // hour of edits and a document that was never edited produce byte-identical
            // responses. That is the one shape this file exists to refuse: not a failure
            // that reports success, but a LOSS that reports success.
            //
            // So a close that would discard work needs three things, and none of them
            // substitutes for another:
            //   * discard_unsaved=true, which is the caller saying the words;
            //   * a rehearsal, so the caller sees WHAT would be lost before agreeing;
            //   * a token bound to that rehearsal, so the agreement covers this document
            //     and this request rather than the idea of closing something.
            //
            // Unknown counts as modified. A document whose IsModified could not be read
            // is not a document known to be clean, and this is the direction to be wrong in.
            // =============================================================
            // The rule itself is in CloseDecision.cs, which carries no Revit - so the
            // tri-state can be proved in all three states, which a real Revit will not
            // reliably produce on demand.
            bool wouldDiscard = CloseDecision.Decide(isModified, saveOnClose, discardUnsaved, false).WouldDiscard;

            string documentKey = DocumentGate.IdentityOf(doc, HostVersion(app))?.Fingerprint();
            string planHash = ConfirmationStore.PlanHash(request, CloseDecision.PlanFields);

            if (dryRun)
            {
                // A rehearsal NEVER closes anything, including the harmless case. A caller
                // that asks what would happen and has it happen has been answered by the
                // wrong tool.
                var rehearsal = new JObject
                {
                    ["operation"] = "close",
                    ["dry_run"] = true,
                    ["closed"] = false,
                    ["title"] = title,
                    ["path"] = path,
                    ["is_modified"] = isModified.HasValue ? (JToken)isModified.Value : null,
                    ["is_modified_note"] = isModified.HasValue
                        ? (isModified.Value
                            ? "This document HAS unsaved changes right now. Closing without save_on_close discards them."
                            : "Revit reports no unsaved changes, so this close would lose nothing.")
                        : "UNKNOWN, not 'no': Document.IsModified could not be read. An unreadable modified flag is " +
                          "not a clean document, and this guard treats it as modified.",
                    ["save_on_close"] = saveOnClose,
                    ["would_discard_unsaved"] = wouldDiscard,
                    ["is_workshared"] = WorksharedJson(worksharedState),
                    ["note"] = wouldDiscard
                        ? "NOTHING WAS CLOSED. This close WOULD DISCARD unsaved changes. To go through with it, call " +
                          "again with dry_run=false, discard_unsaved=true and the confirmation_token below - or pass " +
                          "save_on_close=true instead if the work should be kept."
                        : "NOTHING WAS CLOSED. This close would discard nothing, so it needs no token: call again " +
                          "with dry_run=false."
                };

                if (wouldDiscard)
                {
                    Confirmation issued = DocumentGate.Confirmations.Issue(
                        "horizun_document_session:close", documentKey, planHash);
                    rehearsal["confirmation_token"] = issued.Token;
                    rehearsal["confirmation_expires_utc"] = issued.ExpiresUtc.ToString("u");
                    rehearsal["confirmation_note"] =
                        "SINGLE USE, and bound to THIS document and THIS request. If either changes - a different " +
                        "target, save_on_close flipped, a different document in front - it is refused and nothing " +
                        "is closed. It does NOT freeze the model: more editing can happen between now and the " +
                        "close, and this token would still spend. It expires for that reason.";
                }
                return CommandResult.Ok(rehearsal);
            }

            // hasConfirmation is passed as FALSE here on purpose. A token that was merely
            // SUPPLIED is not a token that checks out, and letting the presence of a
            // string decide would make an expired or stale one as good as a valid one.
            // The verdict says whether a confirmation is needed; Validate says whether
            // this is one.
            CloseVerdict verdict = CloseDecision.Decide(isModified, saveOnClose, discardUnsaved, false);

            if (verdict.Requirement == CloseRequirement.NeedsDiscardFlag)
                return CommandResult.Fail(
                    "REFUSING TO CLOSE: '" + (title ?? path ?? "this document") + "' " +
                    (isModified == true
                        ? "HAS UNSAVED CHANGES"
                        : "may have unsaved changes - Document.IsModified could not be read, and unknown is " +
                          "not clean") +
                    ", and this close would discard them permanently. Nothing was closed. There is no undo and " +
                    "no trace afterwards: Close() would return true, the file on disk would be untouched, and " +
                    "the response would look exactly like closing a document nobody had edited. " +
                    "Either pass save_on_close=true to keep the work, or say so explicitly: run this with " +
                    "dry_run=true to see what would be lost and get a confirmation_token, then call again with " +
                    "discard_unsaved=true and that token.");

            if (verdict.Requirement == CloseRequirement.NeedsConfirmation)
            {
                ConfirmationCheck check = DocumentGate.Confirmations.Validate(
                    suppliedToken, "horizun_document_session:close", documentKey, planHash);
                if (!check.Ok)
                    return CommandResult.Fail(
                        "REFUSING TO CLOSE '" + (title ?? path ?? "this document") + "' and discard its unsaved " +
                        "changes: " + check.Message + " Nothing was closed.");
            }
            else if (discardUnsaved)
            {
                // Not an error, and worth a line in the log: a caller passing the flag on
                // a document with nothing to discard is usually a caller who believes
                // something false about it.
                Log.Info("document_session close: discard_unsaved was passed for '" + (title ?? path) +
                         "', which has no unsaved changes to discard");
            }

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
                // MEASURED before the close, and reported whether or not it mattered.
                // This used to be absent entirely, so a close that discarded an hour of
                // edits and a close of an untouched document produced identical replies.
                ["was_modified"] = isModified.HasValue ? (JToken)isModified.Value : null,
                ["discarded_unsaved_changes"] = wouldDiscard,
                ["discard_note"] = wouldDiscard
                    ? "UNSAVED CHANGES WERE DISCARDED" +
                      (isModified == true
                          ? ". Document.IsModified was true immediately before the close"
                          : ". Document.IsModified could not be read, so whether there were any is UNKNOWN - this " +
                            "guard treated unknown as modified") +
                      ", discard_unsaved=true was passed, and a confirmation token issued for this document and " +
                      "this request was spent. There is no undo. The file on disk is unchanged."
                    : (isModified == false
                        ? null
                        : "Nothing was discarded: save_on_close was requested, so the work was written rather " +
                          "than dropped."),
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

        // "2026", "Autodesk Revit 2026 (Build ...)" and " 2026 " are one year. These
        // forward to OpenGuard so the comparison that DECIDES an open and the one that
        // REPORTS it cannot answer differently about the same two strings - which is the
        // class of divergence this whole change is about.
        private static string NormalizeVersion(string raw) => OpenGuard.NormalizeVersion(raw);

        private static bool SameVersion(string a, string b) => OpenGuard.SameVersion(a, b);

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
        /// <summary>
        /// Which document an action acts on.
        ///
        /// <paramref name="requireExplicitTarget"/> is set by every action that WRITES.
        /// It used to be absent, and the fallback below ran for them too - so
        /// action=save with no file_path saved whatever window happened to be in front,
        /// which is the single thing DocumentGate.ForMutation exists to refuse and which
        /// every other mutating command in this bridge does refuse. The refusal text a
        /// few lines down even promised it: "this tool will not fall back to the active
        /// document". It did, whenever the caller simply left the field out.
        ///
        /// Naming is required; being ACTIVE is not, and that difference is deliberate.
        /// Saving a document that is open behind another is a legitimate thing to ask
        /// for, and a save persists what is already in memory rather than destroying
        /// anything. What is not legitimate is not saying which one.
        /// </summary>
        private static string PickDocument(UIApplication app, JObject request, out Document doc,
                                           bool requireExplicitTarget = false)
        {
            doc = null;
            // target_document is what every other mutating command in this bridge calls
            // it. file_path stays as an alias rather than a second concept.
            var wanted = request.Value<string>("target_document");
            if (string.IsNullOrWhiteSpace(wanted)) wanted = request.Value<string>("file_path");

            if (string.IsNullOrWhiteSpace(wanted) && requireExplicitTarget)
                return "target_document is REQUIRED for this action: it writes, and a write that does not name its " +
                       "target is a write aimed at whatever window happens to be in front. Pass the full path or the " +
                       "title of the document you mean. This will not pick one for you.";

            if (!string.IsNullOrWhiteSpace(wanted) && !Path.IsPathRooted(wanted))
            {
                // A title rather than a path - accepted, because target_document is a
                // title everywhere else, and refused when it names more than one.
                var byTitle = app.Application.Documents
                    .Cast<Document>()
                    .Where(d => { try { return !d.IsLinked && string.Equals(SafeTitle(d), wanted, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                    .ToList();
                if (byTitle.Count == 1) { doc = byTitle[0]; return null; }
                if (byTitle.Count > 1)
                    return "More than one open document is titled '" + wanted + "'. Refusing to pick one - pass the " +
                           "full path instead.";
                return "No open document is titled '" + wanted + "', and it is not an absolute path either. Nothing " +
                       "was done.";
            }

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

            // Read-only actions may fall back to the active document: naming nothing and
            // asking "what am I looking at" is a coherent question. A write is not.
            var active = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            if (active == null)
                return "No document is open (and no target_document was given to identify one).";
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

        /// <summary>
        /// Does this document have unsaved changes? Tri-state, and for the same reason:
        /// a read that threw is not a document with nothing to lose, and the close guard
        /// treats unknown as modified rather than waving it through.
        ///
        /// It has to be read BEFORE the close. Revit invalidates the Document object, so
        /// there is no second chance and no way to check afterwards whether the close
        /// that just succeeded took an hour of work with it.
        /// </summary>
        private static bool? SafeModified(Document d) { try { return d.IsModified; } catch { return null; } }

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
