// Revit API doubles for exercising the production railing command without Revit.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public sealed class SaveOptions { public bool Compact { get; set; } }
    public sealed class ElementId { public static readonly ElementId InvalidElementId = new ElementId(-1); public long Value; public ElementId(int value) { Value = value; } public ElementId(long value) { Value = value; } public ElementId(BuiltInParameter value) { Value=(long)value; } public ElementId(BuiltInCategory value) { Value=(long)value; } }
    public partial class XYZ
    {
        public double X, Y, Z;
        public XYZ(double x, double y, double z) { X=x; Y=y; Z=z; }
        public double DistanceTo(XYZ other) => Math.Sqrt(Math.Pow(X-other.X,2)+Math.Pow(Y-other.Y,2)+Math.Pow(Z-other.Z,2));
        public static XYZ operator -(XYZ a, XYZ b) => new XYZ(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static XYZ operator +(XYZ a, XYZ b) => new XYZ(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public XYZ Multiply(double scale) => new XYZ(X * scale, Y * scale, Z * scale);
    }
    public partial class Category
    {
        public string Name { get; set; } = "test";
        public ElementId Id { get; set; } = new ElementId(0);
    }
    public partial class FamilySymbol : ElementType { public Family Family; }
    public class BoundingBoxXYZ { public XYZ Min, Max; public Transform Transform = Transform.Identity; }
    public partial class Element
    {
        public virtual ElementId Id { get; set; }
        public string TestUniqueId { get; set; }
        public string UniqueId => TestUniqueId ?? Id.Value.ToString();
        internal Category _category;
        public bool TestNoManagedCategory;
        public virtual Category Category
        {
            get => TestNoManagedCategory ? null : _category ?? new Category { Id = new ElementId((long?)TestCategory ?? 0) };
            set => _category = value;
        }
        public Location Location { get; set; }
        public string Name { get; set; } = "test";
        public ElementId OwnerViewId { get; set; } = new ElementId(0);
        public WorksetId WorksetId => new WorksetId();
        public virtual ElementId GetTypeId() => new ElementId(1);
        public BoundingBoxXYZ BoundingBox { get; set; }
        public BoundingBoxXYZ TestModelBox { get => BoundingBox; set => BoundingBox = value; }
        public BoundingBoxXYZ Bounds { get => BoundingBox; set => BoundingBox = value; }
        public BoundingBoxXYZ TestBoundingBox { get => BoundingBox; set => BoundingBox = value; }
        public BoundingBoxXYZ get_BoundingBox(object view) => BoundingBox;
        public Dictionary<BuiltInParameter, Parameter> BuiltInParams { get; } = new Dictionary<BuiltInParameter, Parameter>();
        public virtual Parameter get_Parameter(BuiltInParameter p) => BuiltInParams.TryGetValue(p, out Parameter value) ? value : null;
    }
    public partial class Parameter
    {
        public bool IsReadOnly => false;
        public bool Set(double value) => true;
        public bool Set(ElementId value) => true;
        public bool HasValue => Value != null;
        public object Value { get; set; }
        // Structural doubles set Value; schedule doubles set Name / Text and read strings.
        private StorageType? storageType;
        public StorageType StorageType
        {
            get => storageType ?? (Value is ElementId ? StorageType.ElementId
                : Value is int ? StorageType.Integer : Value is double ? StorageType.Double
                : Name != null || Text != null ? StorageType.String : StorageType.Double);
            set => storageType = value;
        }
        public Definition Definition { get; set; } = new InternalDefinition();
        public double AsDouble() => Value is double number ? number : 0;
        public int AsInteger() => (int)Value;
        public ElementId AsElementId() => Value as ElementId;
    }
    public enum StorageType { Double, ElementId, Integer, String }
    public static class SpecTypeId { public const string Length = "length", Area = "area", Volume = "volume"; }
    public enum BuiltInParameter
    {
        STAIRS_RAILING_HEIGHT_OFFSET, STAIRS_RAILING_HEIGHT, VIEW_SCALE, INSTANCE_REFERENCE_LEVEL_PARAM,
        STRUCTURAL_SECTION_COMMON_HEIGHT, FAMILY_HEIGHT_PARAM, STRUCTURAL_SECTION_COMMON_WIDTH, FAMILY_WIDTH_PARAM,
        YZ_JUSTIFICATION, Y_JUSTIFICATION, Y_OFFSET_VALUE, START_Y_JUSTIFICATION, START_Y_OFFSET_VALUE,
        END_Y_JUSTIFICATION, END_Y_OFFSET_VALUE, START_EXTENSION, END_EXTENSION, START_JOIN_CUTBACK, END_JOIN_CUTBACK,
        STRUCTURAL_BEND_DIR_ANGLE, WALL_BASE_OFFSET, GROUP_LEVEL, GROUP_OFFSET_FROM_LEVEL, ROOF_LEVEL_OFFSET_PARAM,
        FLOOR_HEIGHTABOVELEVEL_PARAM, ROOF_ATTR_THICKNESS_PARAM, ELEM_FAMILY_PARAM, SYMBOL_FAMILY_NAME_PARAM, ELEM_TYPE_PARAM,
        ELEM_TYPE_LABEL, ELEM_CATEGORY_PARAM, ELEM_CATEGORY_PARAM_MT, VIS_GRAPHICS_FILTERS, VIS_GRAPHICS_MODEL,
        VIS_GRAPHICS_ANNOTATION, VIS_GRAPHICS_ANALYTICAL_MODEL, WALL_USER_HEIGHT_PARAM, WALL_KEY_REF_PARAM, TEXT_SIZE, STRUCTURAL_MATERIAL_PARAM,
        RBS_CONDUIT_DIAMETER_PARAM, INVALID = -1
    }
    public class Level : DatumPlane
    {
        public double Elevation { get; set; }
        public double ProjectElevation { get; set; }
    }
    public class Location { }
    public class LocationPoint : Location { public XYZ Point { get; set; } public double Rotation => throw new NotSupportedException(); }
    public class WorksetId { public int IntegerValue => 0; }
    public class ModelPath { public Guid GetProjectGUID() => Guid.Empty; public Guid GetModelGUID() => Guid.Empty; }
    public static class ModelPathUtils { public static string ConvertModelPathToUserVisiblePath(ModelPath path) => "test-central"; }
    public partial class View : Element { }
    public class IndependentTag : Element { public string TagText => throw new NotSupportedException(); }
    public partial class TextNote : Element { }
    public class Curve : GeometryObject
    {
        public XYZ A, B;
        public virtual bool IsBound => true;
        public XYZ GetEndPoint(int index) => !IsBound ? throw new InvalidOperationException("This curve is unbound.") : index == 0 ? A : B;
        public IList<XYZ> Tessellate() => new[] { A, B };
        public virtual IntersectionResult Project(XYZ point) => throw new NotSupportedException();
        public virtual XYZ Evaluate(double parameter, bool normalized) => throw new NotSupportedException();
    }
    public class Line : Curve
    {
        public static Line CreateBound(XYZ a, XYZ b) => new Line { A = a, B = b };
        public override IntersectionResult Project(XYZ point)
        {
            XYZ axis = B - A;
            double t = (point - A).DotProduct(axis) / axis.DotProduct(axis);
            return t < 0 || t > 1 ? null : new IntersectionResult { XYZPoint = A + axis.Multiply(t) };
        }
    }
    public class CurveLoop : List<Curve> { public void Append(Curve curve) => Add(curve); }
    public partial class Document
    {
        public readonly Dictionary<long, Element> Elements = new Dictionary<long, Element>();
        public long NextElementId = 100;
        public long LatestCreatedId;
        public bool Assimilated, ThrowAfterAssimilation;
        public bool RejectGroupRollback, RejectGroupDisposal, RejectPendingDisposal;
        public readonly Queue<TransactionStatus> CommitResults = new Queue<TransactionStatus>();
        public string TestSpatialStatus = "clean";
        public bool IsDetached => false;
        public bool IsReadOnly => false;
        public bool IsModifiable { get; set; }
        public bool IsReadOnlyFile => false;
        public bool IsModelInCloud => false;
        public Autodesk.Revit.UI.TestApplication Application { get; } = new Autodesk.Revit.UI.TestApplication();
        public ModelPath GetWorksharingCentralModelPath() => new ModelPath();
        public ModelPath GetCloudModelPath() => new ModelPath();
        public void Save() => (TestBatchSave ?? throw new InvalidOperationException("Save must not run in this test."))();
        public void Save(SaveOptions options) => Save();
        public bool HasAllChangesFromCentral() => true;
        // Scripting hooks for the sync scenario tests (M10); null keeps the original behaviour of these doubles.
        public static Func<Document, string, Element> UniqueIdLookup;
        public static Action<Document> SyncHook;
        internal Func<string, Element> TestLookupUniqueId;
        
        internal Func<Action> CaptureFixtureState;
        internal Horizun.Revit.Core.ConfirmationStore FixtureConfirmations;
        public Element GetElement(string uniqueId) => TestLookupUniqueId != null ? TestLookupUniqueId(uniqueId)

            : TestScheduleHost
                ? (TestScheduleUniqueIdLookup == null ? Elements.Values.FirstOrDefault(element => element.UniqueId == uniqueId)
                    : TestScheduleUniqueIdLookup(uniqueId))
                : UniqueIdLookup?.Invoke(this, uniqueId);
        public void SynchronizeWithCentral(object transact, object options)
        {
            if (SyncHook == null) throw new InvalidOperationException("Sync must never be reached by this test.");
            SyncHook(this);
        }
        public Element GetElement(ElementId id)
        {
            if (Assimilated && ThrowAfterAssimilation && id.Value >= 100) throw new InvalidOperationException("post-commit read failure");
            Elements.TryGetValue(id.Value, out Element value); return value;
        }
        public int Regenerations { get; private set; }
        public void Regenerate() { Regenerations++; TestRegenerate?.Invoke(); }
    }
    public enum TransactionStatus { Uninitialized, Started, Committed, RolledBack, Pending, Error }
    public interface ITransactionFinalizer
    {
        void OnCommitted(Document document, string name);
        void OnRolledBack(Document document, string name);
    }
    public class FailureHandlingOptions
    {
        internal ITransactionFinalizer Finalizer;
        public void SetTransactionFinalizer(ITransactionFinalizer finalizer) => Finalizer = finalizer;
        public void SetClearAfterRollback(bool clear) { }
        public void SetFailuresPreprocessor(object preprocessor) { }
    }
    public class Transaction : IDisposable
    {
        internal readonly Document Doc;
        internal readonly string Name;
        internal Dictionary<long, Element> Snapshot;
        internal TransactionStatus Status = TransactionStatus.Uninitialized;
        private Action restoreFixtures;
        private FailureHandlingOptions options = new FailureHandlingOptions();
        public Transaction(Document doc, string name) { Doc = doc; Name = name; }
        public FailureHandlingOptions GetFailureHandlingOptions() => options;
        public void SetFailureHandlingOptions(FailureHandlingOptions value) => options = value;
        internal void FinalizePending(bool committed)
        {
            Status = committed ? TransactionStatus.Committed : TransactionStatus.RolledBack;
            if (committed) options.Finalizer?.OnCommitted(Doc, Name);
            else options.Finalizer?.OnRolledBack(Doc, Name);
        }
        public TransactionStatus Start()
        {
            if (Doc.TestTransactionStartError is Exception error) throw error;
            Doc.TransactionStarts++;
            Doc.TestTransactionStarts++;
            Doc.TestTransactionStarted?.Invoke(Name);
            Snapshot = new Dictionary<long, Element>(Doc.Elements);
            restoreFixtures = Doc.CaptureFixtureState?.Invoke();
            return Status = TransactionStatus.Started;
        }
        public bool HasStarted() => Status == TransactionStatus.Started;
        // Revit's own Commit(), scripted by the CommitResults queue; Guard.Commit adds the scripted-status hooks.
        public TransactionStatus Commit()
        {
            Status = Doc.CommitResults.Count == 0 ? TransactionStatus.Committed : Doc.CommitResults.Dequeue();
            if (Status == TransactionStatus.Committed) ObserveCommit();
            return Status;
        }
        public TransactionStatus GetStatus() => Status;
        // What Revit's commit makes observable: the group doubles' pre-observation hook,
        // the change report the ChangeWatch reads, then the post-commit fixture hook.
        internal void ObserveCommit()
        {
            Doc.BeforeTransactionObservation?.Invoke(Doc);
            Doc.Application.ReportChanges(Doc, Doc.Elements.Keys.Except(Snapshot.Keys).ToList(),
                Snapshot.Keys.Except(Doc.Elements.Keys).ToList());
            Doc.TestAfterCommit?.Invoke();
        }
        public TransactionStatus RollBack()
        {
            Doc.TestRollbacks++;
            Status = Doc.TestScheduleRollbackStatus;
            if (Status == TransactionStatus.RolledBack) RestoreSnapshot();
            return Status;
        }
        internal void RestoreSnapshot()
        {
            Doc.Elements.Clear();
            foreach (var entry in Snapshot) Doc.Elements.Add(entry.Key, entry.Value);
            restoreFixtures?.Invoke();
        }
        public void Dispose()
        {
            if (Status == TransactionStatus.Pending && Doc.TestPendingDisposeError is Exception pendingError)
                throw pendingError;
            if (Status == TransactionStatus.Committed && Doc.TestCommittedTransactionDisposeError is Exception error)
                throw error;
            if (Status == TransactionStatus.Pending && Doc.TestPendingTransactionDisposeError is Exception pendingTransactionError)
                throw pendingTransactionError;
            if (Status == TransactionStatus.Pending && Doc.RejectPendingDisposal)
                throw new InvalidOperationException("Fixture transaction cannot close while handling failures.");
        }
    }
    public sealed class SubTransaction : IDisposable
    {
        internal readonly Document Doc;
        internal Dictionary<long, Element> Snapshot;
        internal TransactionStatus Status;
        public SubTransaction(Document doc) { Doc = doc; }
        public TransactionStatus Start()
        {
            Snapshot = new Dictionary<long, Element>(Doc.Elements);
            return Status = TransactionStatus.Started;
        }
        public TransactionStatus GetStatus() => Status;
        public TransactionStatus Commit() => Status = TransactionStatus.Committed;
        public TransactionStatus RollBack()
        {
            Doc.CreationProbeRollback?.Invoke();
            Doc.Elements.Clear();
            foreach (var entry in Snapshot) Doc.Elements.Add(entry.Key, entry.Value);
            return Status = TransactionStatus.RolledBack;
        }
        public void Dispose() { }
    }
    public class TransactionGroup : IDisposable
    {
        internal readonly Document Doc;
        internal Dictionary<long, Element> Snapshot;
        internal Action RestoreFixtures;
        internal Dictionary<Viewport, (double[] Box, double[] Label)> ViewportExtents;
        internal bool RolledBack, Assimilated, PreviouslyAssimilated;
        public TransactionGroup(Document doc, string name) { Doc=doc; }
        public TransactionStatus Start()
        {
            Snapshot = new Dictionary<long, Element>(Doc.Elements);
            RestoreFixtures = Doc.CaptureFixtureState?.Invoke();
            ViewportExtents = Snapshot.Values.OfType<Viewport>().ToDictionary(vp => vp, vp => (vp.TestBox, vp.TestLabel));
            PreviouslyAssimilated = Doc.Assimilated;
            return TransactionStatus.Started;
        }
        public TransactionStatus GetStatus() => RolledBack ? TransactionStatus.RolledBack :
            Assimilated ? TransactionStatus.Committed : TransactionStatus.Started;
        public TransactionStatus Assimilate()
        {
            Assimilated = true;
            Doc.Assimilated = true;
            Doc.TestAfterAssimilate?.Invoke();
            return TransactionStatus.Committed;
        }
        public void Dispose()
        {
            if (Doc.RejectGroupDisposal) throw new InvalidOperationException("Group cannot close while handling failures.");
            if (Doc.TestGroupDisposeError is Exception error) throw error;
        }
    }
}

namespace Autodesk.Revit.DB.Structure
{
    public enum StructuralType { Beam, Column, Brace, UnknownFraming }
}

namespace Autodesk.Revit.DB.Architecture
{
    public class RailingType : Element
    {
        public BalusterPlacement BalusterPlacement { get; set; }
        public Element Duplicate(string name) => throw new InvalidOperationException("Duplicate must not run in this test.");
    }
    public class BalusterPlacement { public BalusterPattern BalusterPattern { get; set; } public PostPattern PostPattern { get; set; } }
    public class BalusterInfo { public ElementId BalusterFamilyId { get; set; } public double DistanceFromPreviousOrSpace { get; set; } }
    public class BalusterPattern
    {
        private readonly List<BalusterInfo> items = new List<BalusterInfo>();
        public int GetBalusterCount() => items.Count;
        public BalusterInfo GetBaluster(int index) => items[index];
        public void RemoveBaluster(int index) => items.RemoveAt(index);
    }
    public class PostPattern { public BalusterInfo StartPost { get; set; } public BalusterInfo EndPost { get; set; } public BalusterInfo CornerPost { get; set; } }
    public enum RailingPlacementPosition { Stringer, Treads }
    public class Railing : Element
    {
        private CurveLoop path;
        public bool HasHost => false;
        public ElementId HostId => null;
        public IList<Curve> GetPath() => path;
        public static bool IsValidHostForNewRailing(Document doc, ElementId id) => true;
        public static bool IsValidPathForRailing(CurveLoop path) => true;
        public static IList<ElementId> Create(Document doc, ElementId host, ElementId type, RailingPlacementPosition placement) => new List<ElementId>();
        public static Railing Create(Document doc, CurveLoop path, ElementId type, ElementId level)
        {
            var id = doc.NextElementId++;
            var railing = new Railing { Id = new ElementId(id), path = path };
            doc.Elements[id] = railing; doc.LatestCreatedId = id; return railing;
        }
    }
}

namespace Horizun.Revit.Core
{
    internal sealed class GateResult { public bool Ok = true; public CommandResult Refusal { get; set; } public Document Document; public string Fingerprint = "fake"; public DocIdentity Identity { get; set; } public ConfirmationStore Confirmations { get; set; } }
    internal static partial class DocumentGate
    {
        public static readonly ConfirmationStore Confirmations = new ConfirmationStore();
        [ThreadStatic] private static ResolvedPlan pendingPlan;
        public static GateResult ForMutation(UIApplication app, JObject request, string name)
        {
            Document doc = app.ActiveUIDocument.Document;
            if (doc.TestReinforcementGateRefusal != null)
                return new GateResult { Ok = false, Refusal = doc.TestReinforcementGateRefusal };
            if (doc.TestScheduleHost)
            {
                var identity = new DocIdentity { Title = doc.Title, RevitYear = app.Application.VersionNumber };
                return new GateResult { Document = doc, Identity = identity, Fingerprint = identity.Fingerprint() };
            }
            return new GateResult { Document = doc, Fingerprint = (doc.TestConfirmationStore ?? doc.FixtureConfirmations) == null ? "fake" : doc.Title,
                Identity = new DocIdentity { Title = doc.Title, Path = doc.PathName }, Confirmations = doc.CreationConfirmations };
        }
        public static CommandResult ReadGuard(Document doc, JObject request, string name)
        {
            string wanted = ReadDocumentRules.Target(request);
            if (ReadDocumentRules.Allows(wanted, doc.Title)) return null;
            JObject detail = ReadDocumentRules.MissingTarget(wanted, new[] { new DocIdentity { Title = doc.Title } });
            return CommandResult.FailWithDetail((string)detail["message"], detail);
        }
        public static string PlanHash(JObject request, params string[] fields) => ConfirmationStore.PlanHash(request, fields);
        public static void RecordResolvedPlan(ResolvedPlan plan) { pendingPlan = plan; }
        public static void StampConfirmation(JObject result, GateResult gate, string name, string hash, bool required, string reason = null)
        {
            ResolvedPlan plan = pendingPlan;
            pendingPlan = null;
            if (gate.Document.TestScheduleHost)
            {
                Confirmations.StampResponse(result, gate.Identity, name, hash, required, plan, reason);
                return;
            }
            if (gate.Confirmations != null)
            {
                // The creation fixture: a per-document store that publishes the plan like production.
                if (required) result["confirmation_token"] = gate.Confirmations.IssueForPlan(name, gate.Fingerprint, hash, null, plan).Token;
                if (plan != null)
                {
                    result["change_preview"] = PlanPreview.Describe(plan);
                    result["plan_resolved"] = plan.DescribeResolution();
                }
                return;
            }
            if (plan != null) result["plan_resolved"] = plan.DescribeResolution();
            if (required && (gate.Document.TestConfirmationStore ?? gate.Document.FixtureConfirmations) is ConfirmationStore store)
                result["confirmation_token"] = store.Issue(name, gate.Fingerprint, hash,
                    elementFingerprint: plan?.Fingerprint(), rehearsedPlan: plan).Token;
        }
        public static CommandResult RequireConfirmation(UIApplication app, GateResult gate, JObject request, string name, string hash, ResolvedPlan plan, object extra, bool checkRequestOnly = false)
        {
            if (InConfirmedAtomicPlan) return StillTheSame(app, gate.Fingerprint, name);
            var store = gate.Document.TestScheduleHost ? Confirmations
                : (gate.Document.TestConfirmationStore ?? gate.Document.FixtureConfirmations);
            string token = request.Value<string>("confirmation_token");
            if (store == null) return gate.Confirmations?.Require(token, name, gate.Fingerprint, hash, plan, null);
            ConfirmationCheck check = checkRequestOnly ? store.CheckRequest(token, name, gate.Fingerprint, hash) :
                store.Validate(token, name, gate.Fingerprint, hash, plan, null);
            return check.Ok ? null : CommandResult.FailWithDetail(check.Message,
                new JObject { ["confirmation_state"] = check.State.ToString() });
        }
        public static CommandResult StillTheSame(UIApplication app, string fingerprint, string name) => null;
    }
    internal sealed class RevitErrorRecorder { public static RevitErrorRecorder On(Transaction tx) => new RevitErrorRecorder(); public string Said() => ""; }
    internal sealed class SilenceWarnings { }
    internal static class Guard
    {
        internal readonly struct RollbackResult
        {
            public RollbackResult(TransactionStatus status) { Status = status; }
            public bool Confirmed => Status == TransactionStatus.RolledBack;
            public string StatusName => Status.ToString();
            public TransactionStatus Status { get; }
        }
        public static TransactionStatus Commit(Transaction tx, string name)
        {
            if (tx.Doc.TestPendingCommit || tx.Doc.TestPendingCommitFor?.Invoke(tx.Name) == true)
            {
                tx.Status = TransactionStatus.Pending;
                throw new SilentRollbackException("Nothing was written. Retry in smaller batches.");
            }
            tx.Doc.TestScheduleBeforeCommit?.Invoke(tx);
            // A queued CommitResults entry is Revit's raw answer, returned as the fixture tests expect.
            if (tx.Doc.CommitResults.Count > 0) return tx.Commit();
            // Otherwise the scripted statuses: schedule doubles use TestScheduleCommitStatus, the others TestCommitStatus.
            TransactionStatus returned = tx.Doc.TestScheduleCommitStatus != TransactionStatus.Committed
                ? tx.Doc.TestScheduleCommitStatus : tx.Doc.TestCommitStatus;
            tx.Status = tx.Doc.TestStatusAfterCommit ?? returned;
            if (tx.Status == TransactionStatus.Pending) tx.Doc.TestPendingTransaction = tx;
            if (tx.Status == TransactionStatus.RolledBack) tx.RestoreSnapshot();
            if (tx.Status == TransactionStatus.Committed) tx.ObserveCommit();
            if (tx.Status != TransactionStatus.Pending) tx.FinalizePending(tx.Status == TransactionStatus.Committed);
            if (returned != TransactionStatus.Committed) throw new SilentRollbackException(returned);
            return tx.Status;
        }
        public static RollbackResult RollBack(Transaction tx) => new RollbackResult(tx.RollBack());
        public static void Commit(SubTransaction tx, string name) => tx.Status = TransactionStatus.Committed;
        public static RollbackResult RollBack(SubTransaction tx)
        {
            tx.Status = TransactionStatus.RolledBack;
            tx.Doc.TestRollbacks++;
            Restore(tx.Doc, tx.Snapshot);
            return new RollbackResult(TransactionStatus.RolledBack);
        }
        public static void Assimilate(TransactionGroup group, string name)
        {
            group.Assimilated = true;
            group.Doc.Assimilated = true;
            group.Doc.TestAfterAssimilate?.Invoke();
        }
        public static RollbackResult RollBack(TransactionGroup group)
        {
            if (group.Doc.RejectGroupRollback) throw new InvalidOperationException("Group cannot roll back while handling failures.");
            group.Doc.TestRollbacks++;
            if (group.Doc.TestGroupRollbackError is Exception error) throw error;
            if (group.Doc.TestGroupRollbackStatus != TransactionStatus.RolledBack)
                return new RollbackResult(group.Doc.TestGroupRollbackStatus);
            group.Doc.TransactionGroupRollbacks++;
            Restore(group.Doc, group.Snapshot);
            group.RestoreFixtures?.Invoke();
            foreach (var entry in group.ViewportExtents)
            {
                entry.Key.TestBox = entry.Value.Box;
                entry.Key.TestLabel = entry.Value.Label;
            }
            group.Doc.Assimilated = group.PreviouslyAssimilated;
            group.RolledBack = true;
            return new RollbackResult(TransactionStatus.RolledBack);
        }
        private static void Restore(Document doc, Dictionary<long, Element> snapshot)
        {
            doc.Elements.Clear();
            foreach (var entry in snapshot) doc.Elements.Add(entry.Key, entry.Value);
        }
    }
    internal static class BBoxCache
    {
        public static double[] Snapshot(Element e) => null;
        public static double[] Get(Document doc, long id) => null;
        public static void Put(Document doc, long id, double[] box) { }
    }
    internal static class ClearanceRulesSource
    {
        public static List<ClearanceZoneRules.Rule> Load(Document doc, out string origin, out List<string> errors)
        { origin = null; errors = new List<string>(); return null; }
    }
    // Models the spatial engine's measured outcome; its geometry traversal is not under test.
    internal static class SpatialCoherence
    {
        internal sealed class Outcome { public string Status; public List<string> ClearanceRuleErrors = new List<string>(); }
        public static List<Element> Subjects(Document doc, IEnumerable<ElementId> ids)
        {
            if (doc.TestSpatialStatus == "not_measured") throw new InvalidOperationException("spatial measurement unavailable");
            var found = new List<Element>(); foreach (var id in ids) { var e = doc.GetElement(id); if (e != null) found.Add(e); } return found;
        }
        public static bool IsPhysical(Element e) => e != null;
        public static Outcome Check(Document doc, IList<Element> subjects, int max, int budget, bool includeLinks = true, IList<ClearanceZoneRules.Rule> clearanceRules = null) => new Outcome { Status = doc.TestSpatialStatus };
        public static JObject ToJson(Outcome outcome, int max) => new JObject { ["status"] = outcome.Status, ["partial"] = outcome.Status == "partial" };
        public static string Headline(Outcome outcome, bool dryRun = false) => null;
    }
    internal static class TagOverlapCheck { public static JObject Run(Document doc, List<View> views) => new JObject(); }
}
