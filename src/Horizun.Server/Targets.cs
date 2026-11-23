// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// Which Revit are we talking to, and how to change it.
//
// Two Revit versions open at once is not an edge case here - it is a Tuesday. A
// model saved by one year does not open in another, so a machine that maintains
// projects across versions runs several at a time, and one of them is often not
// the one the user is looking at (opening a file can start a second instance on
// its own).
//
// Until now the server picked the most recently published live bridge and said
// nothing. Read-only calls just answer about the wrong model; a WRITE lands in
// it. horizun_health could report which Revit answered, but only if you thought
// to ask, and there was no way to change the answer: the environment variable is
// read once when the process starts, and the process is started by the MCP
// client, so choosing a target meant editing a config and restarting everything.
//
// This tool makes the choice visible and reversible from inside the session. It
// touches no model - it reads the same discovery files the router reads.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class Targets
    {
        public static JObject Handle(JObject args)
        {
            string asked = args?["year"] != null ? ((string)args["year"] ?? "").Trim() : null;
            bool wantsPid = args?["pid"] != null && args["pid"].Type == JTokenType.Integer;
            bool wantsYear = !string.IsNullOrEmpty(asked);
            string askedDocument = args?["document"] != null && args["document"].Type == JTokenType.String ? ((string)args["document"]).Trim() : null;
            bool wantsDocument = !string.IsNullOrEmpty(askedDocument);
            if (wantsDocument && (wantsPid || wantsYear))
                throw new ToolRefusal("document names the MODEL to follow and pid/year name a Revit; they are different ways of choosing one " +
                                      "target. The target is unchanged. Pass document alone, or pid/year alone.");

            // focus_window: bring THAT pid's Revit window to the front (WindowFocus.cs). It
            // changes what the person sees, never which Revit the tools talk to.
            string operation = args?["operation"]?.Type == JTokenType.String ? ((string)args["operation"]).Trim().ToLowerInvariant() : null;
            if (operation != null && operation != "focus_window" && operation != "report")
                throw new ToolRefusal("operation must be report (the default) or focus_window. Nothing changed.");
            if (operation == "focus_window")
            {
                if (!wantsPid || wantsYear)
                    throw new ToolRefusal("focus_window needs pid (and no year): a year can name two running Revit, a process id names one window. Nothing was focused.");
                return WindowFocus.Bring((int)args["pid"]);
            }

            // BOTH, IN ONE CALL, IS REFUSED. It used to be accepted and then silently
            // resolved in favour of whichever branch ran last, which was the year - so a
            // caller that passed a pid PRECISELY because a year is ambiguous got the
            // ambiguous thing it was trying to avoid, and a reply that agreed with itself
            // about it. There is no reading of pid+year that is safe to guess: they either
            // agree, in which case the pid alone says it, or they disagree, in which case
            // nobody can say which the caller meant.
            if (wantsPid && wantsYear)
                throw new ToolRefusal(
                    "This names BOTH a process id (" + (int)args["pid"] + ") and a year ('" + asked + "'), and they " +
                    "are two different ways of choosing one target. The target is unchanged. Pass 'pid' to pin one " +
                    "INSTANCE - which is what to use when two Revit of the same year are running - or 'year' to pin " +
                    "a version, or 'year' as 'auto' to clear the choice. Not two at once.");

            TargetSelection chosen = null;    // null = nothing asked, leave the target alone
            string chosenNote = null;

            // A pid names ONE instance. A year no longer does: two Revit 2026 can be
            // running at once, and opening a file is enough to start the second.
            if (wantsDocument)
            {
                // The pid is resolved NOW, from what each live Revit shows, and re-resolved by
                // DocumentTarget.Refresh whenever that instance goes or stops showing the model.
                DocumentTarget.Outcome match = DocumentTarget.Match(askedDocument, DocumentTarget.ReadLive());
                if (match.Chosen == null) throw new ToolRefusal(match.Refusal + " The target is unchanged.");
                // A fragment pins the document the instance REALLY shows, so that following it is by an exact name (INT-6 m-10).
                chosen = TargetSelection.ByDocument(match.Chosen.Pid, match.ByFragment ? DocumentTarget.Normalize(match.Chosen.ActiveDocument) : askedDocument);
                chosenNote = "Target set to the Revit showing '" + askedDocument + "' (Revit " + match.Chosen.Year + ", process " +
                             match.Chosen.Pid + "). It follows the document: if that instance closes or shows another model, calls go to " +
                             "the one instance showing this one, or are refused if there is none or two.";
                if (match.ByFragment)
                    chosenNote += " It was chosen by a FRAGMENT of the name (the instance shows '" + match.Chosen.ActiveDocument + "'); a pinned document " +
                                  "is followed by its exact name only, so pin the exact name to keep the choice stable.";
                Log.Info("target set to document " + askedDocument + " (pid " + match.Chosen.Pid + ")");
            }
            else if (wantsPid)
            {
                int wantPid = (int)args["pid"];
                Discovered byPid = null;
                foreach (Discovered c in PipeClient.ListAll()) if (c.Pid == wantPid) byPid = c;
                if (byPid == null)
                    throw new ToolRefusal("No Revit with process id " + wantPid + " has published a bridge. The " +
                                          "target is unchanged. Call this tool with no arguments to see the ones " +
                                          "that have.");

                chosen = TargetSelection.ByPid(wantPid);
                chosenNote = "Target set to Revit " + byPid.Year + ", process " + wantPid +
                             ". Every call in this session goes to THAT instance until it is changed" +
                             (byPid.ProcessAlive ? "." : " - but that process is NOT running, so calls will fail.");
                Log.Info("target set to pid " + wantPid);
            }
            else if (wantsYear)
            {
                if (string.Equals(asked, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = TargetSelection.Automatic;
                    chosenNote = "Target cleared. Calls go to the running Revit again - and if more than one is " +
                                 "running, they are REFUSED rather than sent to a guess.";
                    Log.Info("target set to auto");
                }
                else
                {
                    // Refuse a year that has published nothing: setting a target that cannot
                    // be reached would turn every later call into a confusing failure, far
                    // from the call that actually made the mistake.
                    string ambiguous;
                    Discovered candidate = PipeClient.Resolve(asked, null, out ambiguous);
                    if (ambiguous != null) throw new ToolRefusal(ambiguous + " The target is unchanged.");
                    if (candidate == null)
                        throw new ToolRefusal(
                            "No Revit " + asked + " has published a bridge, so it cannot be selected. The target is " +
                            "unchanged. Call this tool with no arguments to see which Revit versions are reachable.");

                    chosen = TargetSelection.ByYear(asked);
                    chosenNote = candidate.ProcessAlive
                        ? "Target set to Revit " + asked + ". Every call in this session goes there until it is changed."
                        : "Target set to Revit " + asked + ", but the process that published that bridge (pid " +
                          candidate.Pid + ") is NOT running. Calls will fail until that Revit is started again.";
                    Log.Info("target set to " + asked + (candidate.ProcessAlive ? "" : " (published by a dead process)"));
                }
            }

            // ONE assignment, of a whole target, AFTER every refusal above has had its
            // chance. Nothing partial is ever published: a call that is going to be
            // refused leaves the previous target exactly as it found it, which is what
            // every one of those refusal messages promises.
            if (chosen != null) PipeClient.Target = chosen;
            string remembered = null;
            if (chosen != null)
            {
                try { remembered = TargetMemory.Remember(chosen, Horizun.Revit.Core.HorizunPaths.DataRoot()); }
                catch (Exception ex) { remembered = "NOT remembered (" + ex.Message + "); this process keeps it."; }
            }

            // ONE snapshot for the whole reply. Reading PipeClient.Target repeatedly would
            // reintroduce, inside a single response, the same straddling of a concurrent
            // change that separate fields caused: a reply that reports the year of one
            // target beside the pid of another describes a session that never existed.
            TargetSelection target = PipeClient.Target;

            string envYear = Environment.GetEnvironmentVariable("HORIZUN_REVIT_YEAR") ?? "";
            string effective = target.Year ?? (target.Pid != null || string.IsNullOrEmpty(envYear) ? null : envYear);

            // The automatic rule PREFERS a running Revit; it does not guarantee one. Saying
            // "still running" while selected_process_running is false, two lines further
            // down, is a small lie of exactly the kind this codebase exists to refuse.
            string how = target.Describe(envYear);

            string ambiguityNow;
            Discovered current = PipeClient.Resolve(effective ?? "", target.Pid, out ambiguityNow);

            var list = new JArray();
            List<Discovered> all = PipeClient.ListAll();
            foreach (Discovered d in all)
            {
                list.Add(new JObject
                {
                    ["revit_year"] = d.Year,
                    ["pid"] = d.Pid,
                    ["process_running"] = d.ProcessAlive,
                    ["addin_version"] = d.AddinVersion,     // null when the add-in predates schema 2
                    ["discovery_schema"] = d.Schema,
                    // An add-in that never published its command list gives null, not 0: the
                    // count is unknown, and unknown is not "it has no commands".
                    ["command_count"] = d.Commands == null ? JValue.CreateNull() : new JValue(d.Commands.Count),
                    ["selected"] = current != null && d.SourceFile == current.SourceFile,
                    // E10 (field report, 2026-10-18): with two Revit open the call is refused, but
                    // nothing said which model each one holds. Read from the process's main
                    // window title - what the user sees - and labelled as such: it is the
                    // ACTIVE document and view of that window, not a census of open documents.
                    ["active_document"] = WindowTitleText.Describe(d.ProcessAlive ? WindowTitleText.Of(d.Pid) : null),
                    ["discovery_file"] = d.SourceFile
                });
            }

            var result = new JObject
            {
                // WHERE THE SERVER LOOKS. This tool answers "which Revit am I talking
                // to", and the commonest reason the answer is "none" is not that Revit
                // is absent - it is that the server is reading a different directory
                // from the one the add-in writes to. Both halves report this from the
                // same function, so the two replies can simply be compared.
                ["data_paths"] = Horizun.Revit.Core.HorizunPaths.Describe(),
                ["selected_by"] = how,
                ["selected_year"] = current?.Year,
                ["selected_pid"] = current != null ? (JToken)current.Pid : JValue.CreateNull(),
                ["selected_process_running"] = current != null ? (JToken)current.ProcessAlive : JValue.CreateNull(),
                ["targets_found"] = list.Count,
                ["targets"] = list
            };

            if (chosenNote != null) result["change"] = chosenNote;
            result["session_key"] = TargetMemory.SessionKey();
            if (remembered != null) result["remembered"] = remembered;
            if (TargetMemory.StartupNote != null) result["startup_target"] = TargetMemory.StartupNote;

            // The state that matters most: more than one instance is running and nothing
            // says which. Every model call is being refused until that is settled, and
            // this is the tool that settles it.
            if (ambiguityNow != null)
            {
                result["ambiguous"] = true;
                result["note"] = ambiguityNow;
                return result;
            }
            result["ambiguous"] = false;

            if (current == null)
                result["note"] = all.Count == 0
                    ? "No Revit has published a bridge. Start Revit with the Horizun add-in loaded."
                    : "Nothing is selected: the requested target has no discovery file.";
            else if (!current.ProcessAlive)
                result["note"] = "The selected bridge was published by process " + current.Pid +
                                 ", which is no longer running - Revit did not get to clean up, which usually means " +
                                 "it crashed or was killed. Calls will fail until Revit is started again.";
            else if (all.Count > 1)
                result["note"] = all.Count + " Revit versions have published a bridge. Calls go to Revit " +
                                 current.Year + " (pid " + current.Pid + "). Pass year to change it, or 'auto'.";

            return result;
        }
    }
}
