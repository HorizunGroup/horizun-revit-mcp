// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// "Is anyone home, and WHICH Revit is it?" — the first call any workflow makes.
//
// Its whole value is telling you what you are actually talking to, because the
// expensive mistake is not a dead bridge, it is a live one attached to the wrong
// Revit: two versions are often open at once, and a scan aimed at the wrong
// instance reports confident numbers about the wrong building.
//
// So the answer is never a bare "ok". It carries the year, the process, and the
// document actually active right now. No document open is reported as an explicit
// null with no_active_document=true — not as an empty title, which reads like a
// document with no name.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class HealthCommand : ICommand
    {
        public string Name => "horizun_health";

        public string Description =>
            "Is this bridge alive, and which Revit is on the other end. Reports the Revit year and build, " +
            "the process id, every open document, and the one that is ACTIVE right now (null when none is). " +
            "Call it before anything that reads or writes a model: with two Revit versions open, the " +
            "expensive failure is a healthy bridge attached to the wrong instance.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            Autodesk.Revit.ApplicationServices.Application rvt = app.Application;

            UIDocument uidoc = app.ActiveUIDocument;
            Document active = uidoc != null ? uidoc.Document : null;

            // Collect the open documents, then decide which one is active by IDENTITY.
            // ReferenceEquals was used here and it does not work: Revit's Documents set
            // hands out wrappers, so the comparison failed for the very document named in
            // active_document - the reply said "this is active" and "none of these is
            // active" in the same payload. See Core/DocumentIdentity.cs.
            var docs = new List<Document>();
            var identities = new List<DocIdentity>();
            string listError = null;
            try
            {
                foreach (Document d in rvt.Documents)
                {
                    if (d == null) continue;
                    docs.Add(d);
                    identities.Add(IdentityOf(d));
                }
            }
            catch (Exception ex)
            {
                // A partial list is not a complete one, and the difference has to be visible.
                listError = "The open-document list is INCOMPLETE: " + ex.Message;
            }

            DocMatch match = active == null
                ? new DocMatch()
                : DocumentMatcher.Find(identities, IdentityOf(active));

            var open = new List<object>();
            for (int i = 0; i < docs.Count; i++)
            {
                Document d = docs[i];
                // Reference equality still counts when it happens to hold - it is proof.
                bool? isActive = active != null && ReferenceEquals(d, active) ? true : match.IsMatch(i);
                open.Add(new
                {
                    title = SafeTitle(d),
                    path = string.IsNullOrEmpty(d.PathName) ? null : d.PathName,
                    is_family_document = d.IsFamilyDocument,
                    is_workshared = d.IsWorkshared,
                    // true / false / null. null means two or more open documents share this
                    // identity and which is active cannot be determined - never a bare false.
                    is_active = isActive
                });
            }

            int pid;
            try { pid = Process.GetCurrentProcess().Id; } catch { pid = -1; }

            return CommandResult.Ok(new
            {
                status = "healthy",
                // The two questions every support conversation starts with, answered before
                // they are asked: which build of ours is running, and where is its log.
                horizun_version = Build.Version,
                // Where this bridge comes from, and where the layer above it lives.
                // Stated here because the design property it names is real and a caller
                // acts on it: nothing organisation-specific is compiled in, so a command
                // that seems to be missing a standard is not broken - the standard was
                // never meant to be in here, and inventing one would be worse than
                // asking for it.
                horizun_hub = new
                {
                    product = "Horizun Revit MCP",
                    part_of = "Horizun Hub",
                    url = "https://horizunhub.com",
                    note = "This bridge is the generic, organisation-neutral half: transport, safety guards " +
                           "and a tool surface over the Revit API. The delivery workflows built on it - model " +
                           "audits, classification catalogues, family homologation, pre-delivery QA - live in " +
                           "Horizun Hub, not in this add-in."
                },
                // WHICH SOURCE this is, asked of the add-in that is actually loaded.
                // The version changes once a release; the commit changes with the code,
                // and it is what makes "is the fix deployed?" answerable without
                // searching for strings inside a DLL - which is how that question had
                // to be answered all through 2026-07-30. "unknown" when built without
                // git; a "-dirty" suffix means the tree carried uncommitted edits, so
                // the sha names a commit this binary is NOT exactly.
                horizun_commit = Build.Commit,
                built_from_clean_tree = Build.BuiltFromCleanTree,
                log_file = Log.PathFor(rvt.VersionNumber),
                // WHERE THIS ADD-IN KEEPS STATE, and whether it can actually use it.
                //
                // Put beside the server's answer to the same question, this is what makes
                // "the server and Revit are looking at different folders" a thing you can
                // SEE. It used to be undiagnosable from either side: each half computed
                // the location from its own %LOCALAPPDATA%, a value a parent process can
                // change for one of them, and the symptom was "no Revit has published a
                // bridge" with Revit running and its log growing.
                //
                // Readable/writable are MEASURED per path, not inferred from existence -
                // a jobs directory that exists and refuses writes is exactly the state
                // that looks like a broken feature.
                data_paths = HorizunPaths.Describe(),
                revit_version = rvt.VersionNumber,
                revit_build = rvt.VersionBuild,
                revit_name = rvt.VersionName,
                // The UI language, verbatim from Revit. A live report that omits it
                // presents one localization's evidence as everybody's: template names,
                // parameter display names and unit formatting all vary with it, and a
                // matrix row that cannot say which language it measured cannot be
                // compared with a row from another machine.
                revit_language = LanguageOf(rvt),
                username = rvt.Username,
                process_id = pid,
                // WHAT THIS SESSION IS ABOUT: the active tool packs. Published here
                // because "why does my client not show pack_sheets" must be answerable
                // from the one call everybody makes first - and because a session
                // running on the administrator's environment override should say so
                // rather than look like a user choice.
                tool_packs = ToolPacksBlock(),
                // THE JOB LEDGER, folded. queued/running/interrupted are resolved the
                // way the Session panel resolves them - the record's pid asked of the
                // OS - so "did my batch survive the restart" is answerable from the
                // one call everybody makes first. Bounded to the newest 200 records.
                jobs = JobsBlock(),
                // MEASURED per-tool timing for THIS session (resets with Revit; the
                // snapshot says so): expensive tools lead, avg-vs-recent shows drift.
                timings = ToolTimings.Snapshot(),
                // WHO ELSE is talking to this Revit (5.16). Two agents on one machine
                // once cost three journal autopsies: one killed and redeployed the
                // add-in underneath the other, and neither could see the other. The
                // transport records the pid of every pipe connection; this is that
                // registry, read at answer time.
                clients = ClientsBlock(),
                // The point of the whole call: WHICH document your next command will hit.
                no_active_document = active == null,
                active_document = active == null ? null : new
                {
                    title = SafeTitle(active),
                    path = string.IsNullOrEmpty(active.PathName) ? null : active.PathName,
                    is_family_document = active.IsFamilyDocument,
                    is_workshared = active.IsWorkshared,
                    // A document never saved has no path. Say so rather than showing "".
                    has_been_saved_to_disk = !string.IsNullOrEmpty(active.PathName)
                },
                open_document_count = open.Count,
                open_documents = open,
                // How the active document was identified among the open ones, so a null
                // is_active above is explained rather than left to be interpreted.
                active_document_identified_by = active == null ? null : match.Basis,
                active_document_match = active == null ? null : match.Outcome.ToString(),
                note = Note(active, match, listError)
            });
        }

        /// <summary>
        /// The client-presence block. Liveness and process names are read HERE, at
        /// answer time, because a pid is only a number until somebody asks whether
        /// the process behind it still runs - and a name read can throw on
        /// permissions, which is a null name, never a dropped row.
        /// </summary>
        /// <summary>
        /// Health must never die measuring an ornament: unreadable answers "unknown",
        /// which is itself a fact worth seeing, rather than taking the whole call down.
        /// </summary>
        private static string LanguageOf(Autodesk.Revit.ApplicationServices.Application rvt)
        {
            try { return rvt.Language.ToString(); } catch { return "unknown"; }
        }

        /// <summary>
        /// The resolved pack selection, as facts a caller can act on: which packs, who
        /// decided (default / settings / environment / malformed), which packs arrived
        /// through dependencies, how many tools that leaves visible, and the problem
        /// sentence when the configuration is broken. Guarded like every ornament -
        /// health never dies measuring one.
        /// </summary>
        private static object JobsBlock()
        {
            try
            {
                string dir = HorizunPaths.JobsDir();
                if (!System.IO.Directory.Exists(dir))
                    return new { jobs_path = dir, records = 0 };
                var files = new System.IO.DirectoryInfo(dir).GetFiles("*.jsonl");
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                int queued = 0, running = 0, interrupted = 0, runningOrDied = 0, ok = 0, failed = 0, unreadable = 0;
                int examined = Math.Min(files.Length, 200);
                for (int i = 0; i < examined; i++)
                {
                    try
                    {
                        JobRecordSummary summary = JobRecordSummary.FromLines(
                            System.IO.File.ReadLines(files[i].FullName), ProcessAlive);
                        switch (summary.State)
                        {
                            case "queued": queued++; break;
                            case "running": running++; break;
                            case "interrupted": interrupted++; break;
                            case "running_or_died": runningOrDied++; break;
                            case "finished": if (summary.Failed) failed++; else ok++; break;
                        }
                    }
                    catch { unreadable++; }
                }
                return new
                {
                    jobs_path = dir,
                    records = files.Length,
                    examined,
                    truncated = files.Length > examined,
                    queued, running, interrupted,
                    running_or_died = runningOrDied,
                    finished_ok = ok, finished_failed = failed, unreadable,
                    note = interrupted > 0
                        ? "interrupted: the record's writing process is GONE and the finish line will never come. " +
                          "The work already checkpointed is still in the record; re-running on top of it is a " +
                          "second write to weigh, not a default."
                        : null
                };
            }
            catch (Exception ex)
            {
                return new { error = "the job ledger could not be read: " + ex.Message };
            }
        }

        private static bool ProcessAlive(int pid)
        {
            try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        private static object ToolPacksBlock()
        {
            try
            {
                ToolPacks.Resolution packs = Core.Settings.ActivePackResolution();
                var tools = packs.Tools();
                int total = 0;
                int visible = 0;
                foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
                {
                    total++;
                    if (!packs.Restricting || tools.Contains(c.Name)) visible++;
                }
                return new
                {
                    source = packs.Source.ToString().ToLowerInvariant(),
                    restricting = packs.Restricting,
                    active = packs.ActivePacks == null ? null : packs.ActivePacks.ToArray(),
                    chosen = packs.ChosenPacks == null ? null : packs.ChosenPacks.ToArray(),
                    added_by_dependency = packs.AddedByDependency == null || packs.AddedByDependency.Count == 0
                        ? null : packs.AddedByDependency.ToArray(),
                    problem = packs.Problem,
                    tools_visible = visible,
                    tools_total = total,
                    known_packs = System.Linq.Enumerable.ToArray(
                        System.Linq.Enumerable.OrderBy(ToolPacks.KnownPacks, k => k, StringComparer.Ordinal))
                };
            }
            catch (Exception ex)
            {
                return new { source = "unknown", problem = "the pack state could not be read: " + ex.Message };
            }
        }

        private static object ClientsBlock()
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                PresenceSnapshot snap = ClientPresence.Default.Take(now);

                var rows = new List<object>();
                foreach (ClientSeen c in snap.Clients)
                {
                    bool? alive;
                    string processName = null;
                    try
                    {
                        var p = Process.GetProcessById((int)c.Pid);
                        alive = true;
                        try { processName = p.ProcessName; } catch { /* permissions; the pid stands on its own */ }
                    }
                    catch (ArgumentException) { alive = false; }   // no such process any more
                    catch { alive = null; }                        // could not be told
                    rows.Add(new
                    {
                        pid = c.Pid,
                        process_name = processName,
                        seconds_since_last_request = (int)Math.Max(0, (now - c.LastSeenUtc).TotalSeconds),
                        process_alive = alive
                    });
                }

                return new
                {
                    other_clients_connected = snap.OtherThanCaller,
                    distinct_clients_in_window = snap.Clients.Count,
                    unidentified_connections_in_window = snap.UnidentifiedInWindow,
                    window_seconds = (int)ClientPresence.Window.TotalSeconds,
                    clients_seen = rows,
                    note = "APPROXIMATE by design: the transport is one connection per request, so 'connected' " +
                           "means 'sent at least one request in the last " + (int)ClientPresence.Window.TotalMinutes +
                           " minutes'. The count excludes this call's own client, whose connection was recorded " +
                           "when this request arrived." +
                           snap.UnidentifiedNote() +
                           " If this is not 0 while you believed you were alone on this Revit, you are not: " +
                           "another MCP client is sending commands to the same instance, and anything it does - " +
                           "closing documents, recompiling the add-in, killing the process - lands on you too."
                };
            }
            catch (Exception ex)
            {
                // Presence is telemetry; health must answer without it rather than fail over it.
                return new { error = "client presence could not be read: " + ex.Message };
            }
        }

        private static string Note(Document active, DocMatch match, string listError)
        {
            var parts = new List<string>();
            if (active == null)
                parts.Add("The bridge is alive but NO document is active. Commands that need a document will " +
                          "refuse; open one in this Revit first.");
            else if (match.Outcome == DocMatchOutcome.Ambiguous)
                parts.Add("WHICH open document is active could not be determined: " + match.Explain() +
                          " Pass an explicit target document to any command that reads or writes, and do not " +
                          "rely on 'the active one'.");
            if (listError != null) parts.Add(listError);
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        private static DocIdentity IdentityOf(Document d)
        {
            return new DocIdentity
            {
                Title = SafeTitle(d),
                Path = SafeStr(() => d.PathName),
                IsWorkshared = SafeBool(() => d.IsWorkshared),
                ModelGuid = SafeStr(() =>
                {
                    // Only a cloud model has one; anything else legitimately has none, and a
                    // throw here means "not applicable", not "unknown document".
                    var p = d.GetCloudModelPath();
                    return p == null ? null : p.GetModelGUID().ToString();
                })
            };
        }

        private static string SafeTitle(Document d)
        {
            try { return d.Title; } catch (Exception) { return null; }
        }

        private static string SafeStr(Func<string> read)
        {
            try { return read(); } catch (Exception) { return null; }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch (Exception) { return false; }
        }
    }
}
