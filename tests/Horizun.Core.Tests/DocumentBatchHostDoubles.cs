using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public partial class Document
    {
        internal Action TestBatchSave;
        public void SaveAs(string destination, SaveAsOptions options) => throw new NotSupportedException();
    }
    public sealed class BasicFileInfo : IDisposable
    {
        public static BasicFileInfo Extract(string path) => new BasicFileInfo();
        public bool IsWorkshared => false;
        public void Dispose() { }
    }
    public sealed class SaveAsOptions
    {
        public bool OverwriteExistingFile { get; set; }
        public void SetWorksharingOptions(WorksharingSaveAsOptions options) => throw new NotSupportedException();
    }
    public sealed class WorksharingSaveAsOptions
    {
        public bool SaveAsCentral { get; set; }
    }
}

namespace Autodesk.Revit.UI
{
    public enum PostableCommand { ExitRevit }
    public sealed class RevitCommandId
    {
        public static RevitCommandId LookupPostableCommandId(PostableCommand command) => new RevitCommandId();
    }
    public partial class UIApplication
    {
        internal Action<Document, Document> OnFixtureActivation;
        internal int PostedCommands;
        public bool CanPostCommand(RevitCommandId command) => true;
        public void PostCommand(RevitCommandId command) => PostedCommands++;
    }
    public partial class TestApplication
    {
        internal Action<ChangeWatch> ObserveBatchChanges;
    }
}

namespace Horizun.Revit.Core
{
    internal static class OpenGuard
    {
        public static bool SameDocument(Document a, Document b) => ReferenceEquals(a, b);
    }
    internal static class IdentityPrepass
    {
        public static CommandResult Execute(UIApplication app, ICommand command, string json,
            Func<UIApplication, ICommand, string, CommandResult> execute) => execute(app, command, json);
    }
    internal static partial class ChangeLedger
    {
        public static void Rename(string oldKey, string newKey) => throw new NotSupportedException();
    }
    internal static partial class HrzOwnership
    {
        public static void AfterCommand(string name, CommandContract contract, ChangeWatch watch, CommandResult result) { }
    }
}

namespace Horizun.Revit.Commands
{
    public partial class DocumentSessionCommand
    {
        private readonly Func<string, ICommand> _resolve;
        private readonly Func<Document> newModel;
        internal DocumentSessionCommand(Func<string, ICommand> resolve, Func<Document> newModel)
        {
            _resolve = resolve;
            this.newModel = newModel;
        }

        internal CommandResult RunFixtureBatch(UIApplication app, JObject request) => Batch(app, request);
        private static string HostVersion(UIApplication app) => app.Application.VersionNumber;
        private static string SafeTitle(Document doc) => doc.Title;
        private static string SafePath(Document doc) => doc.PathName;
        private static bool? SafeWorkshared(Document doc) => doc.IsWorkshared;
        private static bool? SafeModified(Document doc) => doc.IsModified;
        private static string LocalDestinationProblem(string path) => null; // Windows paths have already passed the batch contract.
        private static JObject ProbeFile(string path) => new JObject
            { ["exists"] = File.Exists(path), ["bytes"] = new FileInfo(path).Length, ["version"] = "2026" };
        private static string VersionOf(JObject file) => file.Value<string>("version");
        private static bool SameVersion(string first, string second) => first == second;

        private CommandResult Open(UIApplication app, JObject args)
        {
            Document model = newModel();
            model.PathName = args.Value<string>("file_path");
            model.Title = Path.GetFileNameWithoutExtension(model.PathName);
            app.Application.Documents.Add(model);
            app.ActiveUIDocument = new UIDocument { Document = model };
            return CommandResult.Ok(new JObject());
        }

        private static CommandResult Close(UIApplication app, JObject args, Func<Document, CommandResult> exitGuard = null)
        {
            Document model = app.Application.Documents.SingleOrDefault(d =>
                DocIdentity.SamePath(args.Value<string>("target_document"), d.PathName));
            if (model == null) return CommandResult.Fail("Document is not open.");
            CommandResult refusal = CheckExitClose(model, exitGuard);
            if (refusal != null) return refusal;
            if (args.Value<bool>("dry_run"))
                return CommandResult.Ok(new JObject { ["confirmation_token"] = model.IsModified ? "fixture-close" : null });
            bool discard = args.Value<bool?>("discard_unsaved") == true;
            if (model.IsModified && (!discard || args.Value<string>("confirmation_token") != "fixture-close"))
                return CommandResult.Fail("Fixture close lacks discard authorization.");
            JObject activation = null;
            if (ReferenceEquals(app.ActiveUIDocument?.Document, model))
            {
                var next = app.Application.Documents.FirstOrDefault(d => !ReferenceEquals(d, model));
                app.ActiveUIDocument = next == null ? null : new UIDocument { Document = next };
                app.OnFixtureActivation?.Invoke(model, next);
                if (next != null)
                    activation = new JObject { ["activated_title"] = next.Title, ["activated_path"] = next.PathName };
            }
            refusal = CheckExitClose(model, exitGuard, activation);
            if (refusal != null) return refusal;
            app.Application.Documents.Remove(model);
            model.IsValidObject = false;
            return CommandResult.Ok(new JObject { ["closed"] = true, ["activation"] = activation, ["discard_requested"] = discard,
                ["discarded_unsaved_changes"] = model.IsModified });
        }
    }

    internal static class RelinquishAllCommand
    {
        public static CommandResult Release(UIApplication app, Document doc, string source) => throw new NotSupportedException();
        public static JObject ExitOwnership(Document doc) => throw new NotSupportedException();
    }
}
