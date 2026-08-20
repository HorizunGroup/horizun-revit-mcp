// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE set of guards for opening a model, because there were two and they differed.
//
// Two commands open documents: horizun_open_document and horizun_document_session
// with operation=open. They were written months apart, each carefully, and each
// ended up with guards the other did not have. Crossing them is the only way to
// see it, and what crossing them showed:
//
//   THE CENTRAL GUARD EXISTED IN ONE OF THEM. open_document refuses a workshared
//   CENTRAL model unless open_central=true, because opening one directly means
//   working in the file everybody else synchronizes to. document_session had no
//   such check at all - so the tool with "guarded against the irreversible" in its
//   description was the one that would open a central without a word. The safer
//   sounding tool was the less safe one, which is the worst possible arrangement:
//   a caller reaches for it BECAUSE of the promise.
//
//   THE NEWER-FILE RULE EXISTED IN THE OTHER. A file saved in a Revit NEWER than
//   the host cannot be opened at all, and allow_upgrade cannot help - there is no
//   downgrade. document_session says that. open_document treated every mismatch as
//   one kind, so allow_upgrade=true on a newer file produced Revit's own error
//   about a file format instead of the sentence explaining why no flag can fix it.
//
//   CLOUD EXISTED IN ONE OF THEM. open_document opens ACC / BIM 360 models by
//   GUID; document_session could not, so the only route to a cloud model was the
//   command WITHOUT the expected_version check - exactly the models most likely to
//   be opened from a batch across several Revit years.
//
// So neither was the strict one. Each was strict about what its author had been
// bitten by. This file gathers the facts, OpenDecision.cs applies the rules, and
// both commands are callers - which also means the next lesson is learned in one
// place instead of in whichever file somebody happens to be editing.
//
// The split matters: everything here needs Revit (BasicFileInfo, ModelPathUtils,
// UIApplication) and can only be exercised by opening real models. Everything in
// OpenDecision.cs is arithmetic over strings and booleans, and every branch of it
// is a test.
//
// WHAT CHANGED FOR CALLERS, stated rather than buried:
//   * document_session open now refuses a CENTRAL model unless detach=true or
//     open_central=true. Anything that opened centrals through it must now say so.
//   * A CLOUD MODEL IS A CENTRAL MODEL. It is the file the team syncs to, and
//     opening it non-detached is exactly the thing the central guard exists to
//     stop - living in the cloud rather than on a server share does not make it
//     less shared. So the same flag is required for it. This is new for cloud
//     opens, and it is a guard that was missing rather than a rule tightened for
//     its own sake.
//   * open_document now accepts expected_version, and applies the newer-file rule.
//   * document_session now honours open_all_worksets, which it used to accept and
//     silently drop by taking the bare-path overload of OpenAndActivateDocument.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Horizun.Revit.Core
{
    /// <summary>What a caller asked to open, in the terms both commands share.</summary>
    public sealed class OpenRequest
    {
        public string Path;

        public string CloudProjectGuid;
        public string CloudModelGuid;
        public string CloudRegion;

        /// <summary>The Revit year the caller BELIEVES this is. Null when not stated.</summary>
        public string ExpectedVersion;

        /// <summary>
        /// True for document_session, which makes it its whole safety mechanism, and
        /// false for open_document, where it is an extra check rather than the contract.
        /// The rule it drives is identical either way; only "may it be absent" differs.
        /// </summary>
        public bool ExpectedVersionRequired;

        public bool AllowUpgrade;
        public bool Detach;
        public bool Audit;
        public bool OpenCentral;
        public bool OpenAllWorksets;
        public IList<string> CloseWorksetNames = new List<string>();

        /// <summary>
        /// How a dialog raised DURING the open is answered. Default Cancel (the safe
        /// unattended answer, unchanged). Dismiss = acknowledge and continue, for reading
        /// a model whose open raises an unattended-answerable dialog (story 5.22).
        /// </summary>
        public DialogAnswer OnOpenDialog = DialogAnswer.Cancel;

        /// <summary>The tool name, for messages that have to tell the caller what to pass.</summary>
        public string CommandName = "this command";

        /// <summary>
        /// Parse the on_open_dialog argument. Delegates to OpenDialogPolicy (Revit-free,
        /// where the rule is unit-tested); kept here as the name both open commands call.
        /// </summary>
        public static DialogAnswer ParseDialogAnswer(string raw, out string error)
            => OpenDialogPolicy.Parse(raw, out error);
    }

    /// <summary>
    /// Everything the guards learned, and a refusal if they found a reason. Facts are
    /// gathered even on the refusal path: a caller told "no" deserves the reading that
    /// produced it.
    /// </summary>
    public sealed class OpenPlan
    {
        public CommandResult Refusal { get; internal set; }
        public bool Ok => Refusal == null;

        public bool IsCloud { get; internal set; }
        public Guid CloudProject { get; internal set; }
        public Guid CloudModel { get; internal set; }
        public string Region { get; internal set; }

        /// <summary>What to hand OpenAndActivateDocument. Null when the guards refused.</summary>
        public ModelPath ModelPath { get; internal set; }

        public string HostVersion { get; internal set; }

        /// <summary>The file's own saved version, or null - unknown, or a cloud model.</summary>
        public string FileVersion { get; internal set; }

        public string BasicInfoError { get; internal set; }
        public string CentralPath { get; internal set; }
        public bool? FileIsWorkshared { get; internal set; }
        public bool? FileIsCentral { get; internal set; }

        /// <summary>True when opening this WILL upgrade it. False for a cloud model: unknowable.</summary>
        public bool WillUpgrade { get; internal set; }

        /// <summary>"checked" or "not_applicable_cloud". Never blank, never implied.</summary>
        public string VersionGuard { get; internal set; }

        /// <summary>How the central guard was satisfied: detached, open_central, not_a_central.</summary>
        public string CentralGuard { get; internal set; }
        internal IList<WorksetId> CloseWorksetIds = new List<WorksetId>();

        internal OpenRequest Request;

        /// <summary>
        /// The open options, built once so the two commands cannot configure the same
        /// open differently. document_session used to skip OpenOptions entirely when
        /// neither audit nor detach was set, taking the path overload instead - which
        /// silently meant open_all_worksets could not be honoured there at all.
        /// </summary>
        public OpenOptions Options()
        {
            var o = new OpenOptions();
            if (Request.Audit) o.Audit = true;
            if (Request.Detach) o.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            if (CloseWorksetIds.Count > 0)
            {
                var worksets = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
                worksets.Close(CloseWorksetIds);
                o.SetOpenWorksetsConfiguration(worksets);
            }
            else if (Request.OpenAllWorksets)
                o.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
            return o;
        }
    }

    public static class OpenGuard
    {
        /// <summary>
        /// Gather the facts, apply the rules, and hand back a plan. Nothing here opens
        /// anything: by the time this returns Ok, the decision has been made entirely out
        /// of a file header, two GUIDs and the host's own version number.
        /// </summary>
        public static OpenPlan Check(UIApplication app, OpenRequest r)
        {
            var plan = new OpenPlan { Request = r, HostVersion = Safe(() => app?.Application?.VersionNumber) };

            bool wantsCloud = !string.IsNullOrWhiteSpace(r.CloudProjectGuid) ||
                              !string.IsNullOrWhiteSpace(r.CloudModelGuid);
            bool wantsPath = !string.IsNullOrWhiteSpace(r.Path);

            // Two ways to name a model is one way too many to guess between.
            if (wantsCloud && wantsPath)
                return Refuse(plan,
                    "Give EITHER 'path' (a local file) OR the cloud GUIDs (cloud_project_guid + " +
                    "cloud_model_guid), not both. They name different models and there is no sensible way to " +
                    "pick one for you. Nothing was opened.");

            if (!wantsCloud && !wantsPath)
                return Refuse(plan,
                    "Nothing to open: pass 'path' for a local .rvt/.rfa, or cloud_project_guid + " +
                    "cloud_model_guid for a model in ACC / BIM 360.");

            OpenFacts facts = wantsCloud ? GatherCloud(plan, r) : GatherLocal(plan, r);
            if (facts == null) return plan;           // gathering already refused, with a reason

            OpenVerdict verdict = OpenDecision.Decide(facts, new OpenIntent
            {
                ExpectedVersion = r.ExpectedVersion,
                ExpectedVersionRequired = r.ExpectedVersionRequired,
                AllowUpgrade = r.AllowUpgrade,
                Detach = r.Detach,
                OpenCentral = r.OpenCentral
            });

            plan.VersionGuard = verdict.VersionGuard;
            plan.WillUpgrade = verdict.WillUpgrade;
            plan.CentralGuard = verdict.CentralGuard;
            if (!verdict.Ok) return Refuse(plan, verdict.Refusal);

            // Only now, with the decision made, is it worth asking Revit for a path.
            try
            {
                plan.ModelPath = wantsCloud
                    ? ModelPathUtils.ConvertCloudGUIDsToCloudPath(plan.Region, plan.CloudProject, plan.CloudModel)
                    : ModelPathUtils.ConvertUserVisiblePathToModelPath(r.Path);
            }
            catch (Exception ex)
            {
                return Refuse(plan, wantsCloud
                    ? "Revit could not build a cloud path from region '" + plan.Region + "' and those GUIDs: " +
                      ex.Message + ". Nothing was opened."
                    : "Revit could not turn '" + r.Path + "' into a model path: " + ex.Message);
            }

            if (r.CloseWorksetNames != null && r.CloseWorksetNames.Count > 0)
            {
                if (r.OpenAllWorksets)
                    return Refuse(plan, "open_all_worksets=true contradicts close_workset_names. Choose one loaded-workset plan; nothing was opened.");
                try
                {
                    IList<WorksetPreview> previews = WorksharingUtils.GetUserWorksetInfo(plan.ModelPath);
                    foreach (string wanted in r.CloseWorksetNames)
                    {
                        var hits = new List<WorksetPreview>();
                        foreach (WorksetPreview preview in previews)
                            if (string.Equals(preview.Name, wanted, StringComparison.OrdinalIgnoreCase)) hits.Add(preview);
                        if (hits.Count != 1)
                            return Refuse(plan, hits.Count == 0
                                ? "No user workset named '" + wanted + "' exists in the file. Nothing was opened."
                                : "More than one user workset matches '" + wanted + "'. Nothing was opened because choosing one would be ambiguous.");
                        plan.CloseWorksetIds.Add(hits[0].Id);
                    }
                }
                catch (Exception ex)
                {
                    return Refuse(plan, "The requested closed-workset plan could not be resolved before opening: " + ex.Message + ". Nothing was opened.");
                }
            }

            return plan;
        }

        // ------------------------------------------------------------------ cloud
        private static OpenFacts GatherCloud(OpenPlan plan, OpenRequest r)
        {
            plan.IsCloud = true;

            if (string.IsNullOrWhiteSpace(r.CloudProjectGuid))
            { Refuse(plan, "'cloud_project_guid' is required alongside cloud_model_guid."); return null; }
            if (string.IsNullOrWhiteSpace(r.CloudModelGuid))
            { Refuse(plan, "'cloud_model_guid' is required alongside cloud_project_guid."); return null; }

            // A GUID that does not parse is a caller mistake worth naming, not a Revit error
            // worth forwarding. Revit's own message for a malformed cloud path says nothing
            // about which of the two ids was wrong.
            Guid projectGuid, modelGuid;
            if (!Guid.TryParse(r.CloudProjectGuid.Trim(), out projectGuid))
            { Refuse(plan, "'cloud_project_guid' is not a GUID: '" + r.CloudProjectGuid + "'."); return null; }
            if (!Guid.TryParse(r.CloudModelGuid.Trim(), out modelGuid))
            { Refuse(plan, "'cloud_model_guid' is not a GUID: '" + r.CloudModelGuid + "'."); return null; }

            if (projectGuid == Guid.Empty || modelGuid == Guid.Empty)
            {
                Refuse(plan,
                    "An all-zero GUID is not a model. Revit builds a cloud path out of whatever it is given, " +
                    "so this would have produced a path that looks valid and resolves to nothing. This is what " +
                    "the ACC web URN decodes to when it is mistaken for a Revit model GUID - they are " +
                    "identifiers from different systems. Nothing was opened.");
                return null;
            }

            plan.CloudProject = projectGuid;
            plan.CloudModel = modelGuid;
            plan.Region = string.IsNullOrWhiteSpace(r.CloudRegion) ? "US" : r.CloudRegion.Trim().ToUpperInvariant();

            // A cloud model IS workshared and IS the central. Not "probably": that is what
            // a model in ACC is for. Reporting it as unknown would be a technicality - the
            // API cannot be asked before the open - standing in for a fact everybody knows.
            plan.FileIsWorkshared = true;
            plan.FileIsCentral = true;

            return new OpenFacts
            {
                IsCloud = true,
                HostVersion = plan.HostVersion,
                FileVersion = null,
                IsCentral = true,
                DisplayName = "cloud model " + modelGuid
            };
        }

        // ------------------------------------------------------------------ local
        private static OpenFacts GatherLocal(OpenPlan plan, OpenRequest r)
        {
            string path = r.Path;

            bool exists;
            try { exists = File.Exists(path); }
            catch (Exception ex)
            { Refuse(plan, "Could not test the path '" + path + "': " + ex.Message); return null; }
            if (!exists) { Refuse(plan, "File not found: " + path); return null; }

            // Read the file's own facts WITHOUT opening it. This is the only way to know
            // before the damage: every other route involves the open that does it.
            try
            {
                BasicFileInfo info = BasicFileInfo.Extract(path);
                if (info != null)
                {
                    plan.FileVersion = Safe(() => info.Format);
                    plan.FileIsWorkshared = SafeBool(() => info.IsWorkshared);
                    plan.FileIsCentral = SafeBool(() => info.IsCentral);
                    plan.CentralPath = Safe(() => string.IsNullOrEmpty(info.CentralPath) ? null : info.CentralPath);
                }
                else plan.BasicInfoError = "BasicFileInfo.Extract returned nothing for this file.";
            }
            catch (Exception ex) { plan.BasicInfoError = ex.Message; }

            return new OpenFacts
            {
                IsCloud = false,
                HostVersion = plan.HostVersion,
                FileVersion = plan.FileVersion,
                ReadError = plan.BasicInfoError,
                IsCentral = plan.FileIsCentral,
                DisplayName = FileName(path)
            };
        }

        // ------------------------------------------------------------------ after
        /// <summary>
        /// Are these two handles the same document? Not a reference comparison: Revit hands
        /// back a fresh managed wrapper for the same underlying document, so ReferenceEquals
        /// answers false for a document that plainly IS the one just opened - a false alarm,
        /// which is as dishonest as a false success. Identity first, then the file it is
        /// bound to. Title is NEVER identity: homonymous files and detached documents are
        /// common, and guessing between them would aim the next command at the wrong model.
        /// </summary>
        public static bool SameDocument(Document a, Document b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;

            try { if (a.Equals(b)) return true; } catch { }

            try
            {
                string projectA = null, modelA = null, projectB = null, modelB = null;
                try
                {
                    ModelPath cloud = a.GetCloudModelPath();
                    if (cloud != null) { projectA = cloud.GetProjectGUID().ToString(); modelA = cloud.GetModelGUID().ToString(); }
                }
                catch { }
                try
                {
                    ModelPath cloud = b.GetCloudModelPath();
                    if (cloud != null) { projectB = cloud.GetProjectGUID().ToString(); modelB = cloud.GetModelGUID().ToString(); }
                }
                catch { }

                return DocumentMatcher.SameStableIdentity(
                    new DocIdentity { Path = a.PathName, ModelGuid = modelA },
                    new DocIdentity { Path = b.PathName, ModelGuid = modelB },
                    projectA, projectB);
            }
            catch { return false; }
        }

        // The version arithmetic lives in OpenDecision, which carries no Revit. These
        // forward so callers have one name to reach for and cannot end up with two
        // implementations of "is this the same year" - the shape of the problem this
        // whole file was written to remove.
        public static string NormalizeVersion(string s) => OpenDecision.NormalizeVersion(s);
        public static bool SameVersion(string a, string b) => OpenDecision.SameVersion(a, b);

        private static OpenPlan Refuse(OpenPlan plan, string message)
        {
            plan.Refusal = CommandResult.Fail(message);
            return plan;
        }

        private static string FileName(string path)
        {
            try { return Path.GetFileName(path); } catch { return path; }
        }

        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static bool? SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
    }
}
