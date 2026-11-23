using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public partial class DocumentSessionCommand
    {
        private const string ActivateScope = "horizun_document_session:activate";
        private static readonly ActivationSessionIdentity<Document> ActivationKeys = new ActivationSessionIdentity<Document>();

        private static CommandResult Activate(UIApplication app, JObject request)
        {
            string target = request.Value<string>("target_document");
            if (string.IsNullOrWhiteSpace(target)) target = request.Value<string>("file_path");
            var documents = app.Application.Documents.Cast<Document>().Where(d => d != null && !d.IsLinked).ToList();
            var identities = documents.Select(d => DocumentGate.IdentityOf(d, null)).ToList();
            var match = DocumentMatcher.FindTarget(identities, target);
            if (match.Outcome == DocMatchOutcome.Ambiguous)
                return CommandResult.FailCoded(ErrorCodes.DocumentoObjetivoAmbiguo,
                    "Several open documents match target_document. Pass the full path. Nothing changed.",
                    extra: new JObject { ["candidates"] = new JArray(match.Candidates.Select(i => new JObject
                    { ["title"] = identities[i].Title, ["path"] = identities[i].Path })), ["write_started"] = false });
            if (match.Outcome != DocMatchOutcome.Matched)
                return CommandResult.Fail("No open document matches target_document. Nothing changed.");
            Document doc = documents[match.Index];
            bool cloud, detached;
            try { cloud = doc.IsModelInCloud; detached = doc.IsDetached; }
            catch (Exception ex) { return CommandResult.Fail("Cannot determine cloud/detached state: " + ex.Message + ". Nothing changed."); }
            string path = SafePath(doc);
            string choice = ActivationChoice.ForDocument(cloud, detached, !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            string destination = request.Value<string>("save_as_path");
            if (choice != "save_as" && !string.IsNullOrWhiteSpace(destination))
                return CommandResult.Fail("save_as_path applies only to unsaved or detached documents. Nothing changed.");
            if (choice == "save_as")
            {
                bool isFamily;
                try { isFamily = doc.IsFamilyDocument; } catch (Exception ex) { return CommandResult.Fail("Cannot determine document kind: " + ex.Message); }
                string problem = ActivationChoice.DestinationProblem(destination, TestExists(destination), FolderExists(destination), isFamily);
                if (problem == null) problem = LocalDestinationProblem(destination);
                if (problem != null) return CommandResult.Fail(problem);
            }
            string documentKey = ActivationKeys.Key(doc, SafeTitle(doc));
            string hash = ConfirmationStore.HashPlan("choice=" + choice, "target=" + (DocIdentity.NormalizePath(destination) ?? "-"));
            bool dry = request.Value<bool?>("dry_run") ?? true;
            if (dry)
            {
                var result = new JObject { ["operation"] = "activate", ["dry_run"] = true, ["method"] = choice,
                    ["target_document"] = SafeTitle(doc), ["save_as_path"] = destination,
                    ["write_started"] = false, ["changes_applied"] = false,
                    ["note"] = choice == "save_as" ? "SaveAs will write this document to the new local path, then activate it. Nothing was written." : choice == "cloud" ? "Will activate using the cloud ModelPath; no save or synchronization. Revit documents that this overload may override local changes if the cloud model was restored, relinquished or out of date." : "Will activate the already-open document; no save or synchronization." };
                if (choice == "save_as") result["confirmation_token"] = DocumentGate.Confirmations.Issue(ActivateScope, documentKey, hash).Token;
                return CommandResult.Ok(result);
            }
            if (choice == "save_as")
            {
                var check = DocumentGate.Confirmations.Validate(request.Value<string>("confirmation_token"), ActivateScope, documentKey, hash);
                if (!check.Ok) return CommandResult.Fail(check.Message + " Nothing changed.");
                string oldKey = ChangeWatch.Key(doc);
                try
                {
                    var options = new SaveAsOptions { OverwriteExistingFile = false };
                    if (doc.IsWorkshared)
                        options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                    doc.SaveAs(destination, options);
                    try { DocumentLifecycleRules.SavedAs(oldKey, ChangeWatch.Key(doc), ChangeLedger.Rename); } catch { }
                }
                catch (Exception ex)
                {
                    return CommandResult.FailWithDetail("SaveAs failed: " + ex.Message + ". Inspect the destination; a partial file may remain.",
                        new JObject { ["write_started"] = true, ["save_as_path"] = destination, ["activated"] = false });
                }
                JObject file = ProbeFile(destination);
                if (file.Value<bool?>("exists") != true || (file.Value<long?>("bytes") ?? 0) <= 0 ||
                    !DocIdentity.SamePath(SafePath(doc), destination) || !SameVersion(VersionOf(file), HostVersion(app)))
                    return CommandResult.FailWithDetail("SaveAs could not be verified; activation was not attempted. Inspect the destination.",
                        new JObject { ["write_started"] = true, ["save_as_path"] = destination, ["file"] = file });
                path = destination;
            }
            try
            {
                if (choice == "cloud") app.OpenAndActivateDocument(doc.GetCloudModelPath(), new OpenOptions(), false);
                else app.OpenAndActivateDocument(path);
                Document active = app.ActiveUIDocument?.Document;
                bool verified = OpenGuard.SameDocument(active, doc);
                if (!verified) return CommandResult.FailWithDetail("Activation returned but the active document did not match the target.",
                    new JObject { ["write_started"] = choice == "save_as", ["save_as_path"] = destination, ["activated"] = false });
                return CommandResult.Ok(new JObject { ["operation"] = "activate", ["dry_run"] = false, ["method"] = choice,
                    ["activated"] = true, ["active_document_verified"] = true, ["write_started"] = choice == "save_as", ["save_as_path"] = destination });
            }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail("Activation failed: " + ex.Message + (choice == "save_as" ? ". SaveAs already completed; the file remains." : "."),
                    new JObject { ["write_started"] = choice == "save_as", ["save_as_path"] = destination, ["activated"] = false });
            }
        }

        private static string LocalDestinationProblem(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (new DriveInfo(Path.GetPathRoot(full)).DriveType != DriveType.Fixed)
                    return "Destination must be on a fixed local drive; network and removable drives are refused.";
                for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)); dir != null; dir = dir.Parent)
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                        return "Destination crosses a reparse point; its local identity cannot be established.";
                var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ACCDocs") };
                using (var manager = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"))
                {
                    if (manager != null)
                        foreach (string name in manager.GetSubKeyNames())
                        {
                            if (!name.StartsWith("DesktopConnector!", StringComparison.OrdinalIgnoreCase)) continue;
                            using (var registered = manager.OpenSubKey(name + @"\UserSyncRoots"))
                                if (registered != null)
                                    foreach (string valueName in registered.GetValueNames())
                                        if (registered.GetValue(valueName) is string root && !string.IsNullOrWhiteSpace(root))
                                            roots.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)));
                        }
                }
                return ActivationChoice.RegisteredRootProblem(full, roots);
            }
            catch (Exception ex) { return "Cannot establish a local destination: " + ex.Message; }
        }
    }
}
