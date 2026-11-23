using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public interface ICentralLockedCallback { bool ShouldWaitForLockAvailability(); }
    public sealed class RelinquishOptions
    {
        public RelinquishOptions(bool all) { }
        public bool StandardWorksets, ViewWorksets, FamilyWorksets, UserWorksets, CheckedOutElements;
    }
    public sealed class SynchronizeWithCentralOptions
    {
        public string Comment; public bool Compact, SaveLocalBefore, SaveLocalAfter;
        public void SetRelinquishOptions(RelinquishOptions options) { }
    }
    public sealed class TransactWithCentralOptions { public void SetLockCallback(ICentralLockedCallback callback) { } }
    public sealed class Workset { public string Owner; public WorksetId Id; public string Name; }
    public sealed class FilteredWorksetCollector : IEnumerable<Workset>
    {
        public FilteredWorksetCollector(Document doc) { }
        public IEnumerator<Workset> GetEnumerator() => new List<Workset>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public sealed class FilteredElementCollector : IEnumerable<Element>
    {
        private readonly Document _doc; private bool _types;
        public FilteredElementCollector(Document doc) { _doc = doc; }
        public FilteredElementCollector WhereElementIsElementType() { _types = true; return this; }
        public FilteredElementCollector WhereElementIsNotElementType() { _types = false; return this; }
        public IEnumerator<Element> GetEnumerator() =>
            (_types ? new List<Element>() : new List<Element>(_doc.Elements.Values)).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public enum CheckoutStatus { OwnedByCurrentUser, OwnedByOtherUser, NotOwned }
    public static class WorksharingUtils
    {
        // Scripted per element id by the scenario tests; an unscripted element reads as owned, as before.
        public static readonly Dictionary<long, CheckoutStatus> Status = new Dictionary<long, CheckoutStatus>();
        public static long? UnreadableId;
        public static CheckoutStatus GetCheckoutStatus(Document doc, ElementId id) =>
            UnreadableId == id.Value ? throw new InvalidOperationException("checkout unreadable") :
            Status.TryGetValue(id.Value, out CheckoutStatus s) ? s : CheckoutStatus.OwnedByCurrentUser;
        public static string GetModelUpdatesStatus(Document doc, ElementId id) => "CurrentWithCentral";
    }
}

namespace Horizun.Sync.Integration.Tests
{
    /// <summary>
    /// The sync tests run the product's real Settings against a settings.json in a private data root, so no double of Settings is needed
    /// and no machine setting is read or modified. The defaults are the full_write profile with the owner's sync grant on.
    /// </summary>
    internal static class SyncSettings
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "hz-sync-tests-" + Guid.NewGuid().ToString("N"));

        [ModuleInitializer]
        internal static void Init()
        {
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", Root);
            Set(false, "full_write");
            // INT-6 m-12: this run's private data root goes when the run ends (only THIS root: never a pattern over %TEMP%).
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Cleanup();
        }

        /// <summary>Delete this run's private data root. Idempotent; a failure is ignored (the OS temp cleanup is the backstop).</summary>
        internal static void Cleanup()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }

        internal static bool RootExists => Directory.Exists(Root);

        internal static void Set(bool syncOnly, string profile) =>
            File.WriteAllText(Path.Combine(Root, "settings.json"), new JObject
            {
                ["permission_profile"] = profile,
                ["sync_with_central_owner_granted"] = true,
                ["allow_sync_only_grant"] = syncOnly
            }.ToString());
    }
}

namespace Horizun.Revit.Core
{
    internal static class ChangeWatch { public static string Key(Document doc) => doc.Title; }
    internal static class OpenRecoveryDocuments { public static bool IsMarked(Document doc) => false; }
    internal static class DocumentGate
    {
        public static ConfirmationStore Confirmations = new ConfirmationStore();
        public static DocIdentity IdentityOf(Document doc, string version) => null;
        public static GateResult ForMutation(UIApplication app, JObject request, string name) => new GateResult { Document = app.ActiveUIDocument.Document };
    }
    internal sealed class GateResult { public bool Ok = true; public CommandResult Refusal { get; set; } public Document Document; }
    internal sealed class OperationGateResult { public CommandResult Refusal { get; set; } public bool Requested { get; set; } public JObject Prevention { get; set; } }
    internal static class OperationGate { public static OperationGateResult Evaluate(UIApplication app, Document doc, JToken request, string operation, string name) => new OperationGateResult(); }
    internal static class Rid { public static long Value(ElementId id) => id.Value; }
}

namespace Horizun.Revit.Commands
{
    internal static class RelinquishAllCommand { public static CommandResult RelinquishOwn(UIApplication app, GateResult gate, JObject request) => throw new InvalidOperationException(); }
    public partial class DocumentSessionCommand
    {
        private static string PickDocument(UIApplication app, JObject request, out Document doc, bool requireExplicitTarget) { doc=app.ActiveUIDocument.Document; return null; }
        private static string SafeTitle(Document doc) => doc.Title;
        private static string SafePath(Document doc) => doc.PathName;
        private static bool? SafeWorkshared(Document doc) => doc.IsWorkshared;
        private static bool? SafeModified(Document doc) => doc.IsModified;
        private static string HostVersion(UIApplication app) => app.Application.VersionNumber;
    }
}
