using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public class GroupType : Element { }
    public class Group : Element
    {
        public GroupType GroupType { get; set; }
        public new LocationPoint Location { get; set; }
        public List<ElementId> Members { get; set; }
        public bool TestQuarterTurn { get; set; }
        public bool TestReflected { get; set; }
        public ICollection<ElementId> GetMemberIds()
        {
            if (Document?.Assimilated == true && Document.FailGroupMemberReadAfterAssimilation)
                throw new InvalidOperationException("Could not reread copy " + Id.Value);
            return Members;
        }

        private XYZ Orient(XYZ direction)
        {
            if (direction == null) return null;
            if (TestReflected) direction = new XYZ(direction.X, -direction.Y, direction.Z);
            return TestQuarterTurn ? new XYZ(-direction.Y, direction.X, direction.Z) : direction;
        }

        public Element PlaceMember(Element source, ElementId id, bool translateOnly = false)
        {
            XYZ PlacePoint(XYZ point) => Location.Point + (translateOnly ? point : Orient(point));
            var member = source is FamilyInstance family
                ? new FamilyInstance { Placement = new Transform { BasisX = translateOnly
                    ? family.Placement.BasisX : Orient(family.Placement.BasisX),
                    BasisY = translateOnly ? family.Placement.BasisY : Orient(family.Placement.BasisY),
                    HasReflection = family.Placement.HasReflection ^ (TestReflected && !translateOnly) } }
                : new Element();
            member.Id = id;
            member.Bounds = PlaceBounds(source.Bounds, translateOnly);
            if (source.Location is LocationPoint point)
                member.Location = new LocationPoint { Point = PlacePoint(point.Point) };
            else if (source.Location is LocationCurve path)
                member.Location = new LocationCurve { Curve = path.Curve is Arc arc
                    ? (Curve)new Arc(t => PlacePoint(arc.Evaluate(t, true)), PlacePoint(arc.Center), arc.Length / arc.Radius)
                    : Line.CreateBound(PlacePoint(path.Curve.GetEndPoint(0)), PlacePoint(path.Curve.GetEndPoint(1))) };
            return member;
        }

        private BoundingBoxXYZ PlaceBounds(BoundingBoxXYZ bounds, bool translateOnly)
        {
            XYZ a = translateOnly ? bounds.Min : Orient(bounds.Min);
            XYZ b = translateOnly ? bounds.Max : Orient(bounds.Max);
            return new BoundingBoxXYZ { Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), a.Z) + Location.Point,
                Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), b.Z) + Location.Point };
        }
    }

    public class Edge { public IList<XYZ> Tessellate() => Array.Empty<XYZ>(); }
    public partial class Element { public virtual GeometryElement get_Geometry(Options options) => null; }

    internal static class ElementTransformUtils
    {
        public static ICollection<ElementId> CopyElement(Document doc, ElementId id, XYZ translation) =>
            throw new NotSupportedException("The fixture only exercises add_members and check_variation.");
    }

    public partial class Document
    {
        public Action<Document> BeforeTransactionObservation { get; set; }
        public Action<Document> AfterGroupRegeneration { get; set; }
        public HashSet<long> UnreportedAdditions { get; } = new HashSet<long>();
        public int TransactionGroupRollbacks { get; set; }
        public long GroupCreationIdOffset { get; set; }
        public bool FailGroupMemberReadAfterAssimilation { get; set; }
        public bool TranslateNewGroupMember { get; set; }
        public bool LoseGroupRotation { get; set; }
    }
}

namespace Autodesk.Revit.UI
{
    public partial class TestApplication
    {
        internal event Action<Document, IEnumerable<long>, IEnumerable<long>> Changed;
        internal void ReportChanges(Document doc, IEnumerable<long> added, IEnumerable<long> deleted) =>
            Changed?.Invoke(doc, added.Where(id => !doc.UnreportedAdditions.Contains(id)), deleted);
    }
}

namespace Horizun.Revit.Transport
{
    internal static class ProLicenseGate { public static void Require(string name) { } }
}

namespace Horizun.Revit.Core
{
    internal sealed partial class ChangeWatch : IDisposable
    {
        private TestApplication _groupApp;
        // One constructor for every fixture: the group doubles observe reported changes,
        // the batch doubles hand the watch to their observer.
        public ChangeWatch(TestApplication app)
        {
            _groupApp = app;
            _groupApp.Changed += OnChange;
            app.ObserveBatchChanges?.Invoke(this);
        }
        private void OnChange(Document doc, IEnumerable<long> added, IEnumerable<long> deleted) =>
            Documents.Add(new DocChanges { Document = doc, Added = new HashSet<long>(added), Deleted = new HashSet<long>(deleted) });
        private void DetachGroupObserver() { if (_groupApp != null) _groupApp.Changed -= OnChange; _groupApp = null; }
    }

    internal static class AnnotationDependencyAudit
    {
        public static JObject Read(Document doc, IEnumerable<ElementId> ids) => new JObject { ["coverage_complete"] = true };
        public static JObject ReadTargets(Document doc, HashSet<long> ids, bool scan) => Read(doc, null);
        public static IEnumerable<long> TargetIds(Document doc, IEnumerable<ElementId> ids, bool includeGroups) =>
            ids.SelectMany(id => ((Group)doc.GetElement(id)).GetMemberIds().Prepend(id)).Select(Rid.Value);
    }
}

namespace Horizun.Revit.Commands
{
    // The production entrypoint, commit handling, change observation and confirmation run
    // unchanged. Model planning and regeneration here describe a small in-memory group.
    public sealed partial class ManageGroupsCommand
    {
        private const long ReferenceId = 10, OldTypeId = 11, NewGroup = 40, NewType = 41;
        private static Plan MakePlan(Document doc, JObject request, string operation, out string error)
        {
            error = null;
            var reference = (Group)doc.Elements[ReferenceId];
            var copies = doc.Elements.Values.OfType<Group>().Where(group => group.Id != reference.Id).ToList();
            long[] requiredDeletions = new[] { ReferenceId, OldTypeId }.Concat(copies.SelectMany(group => group.Members).Select(Rid.Value)).ToArray();
            return new Plan
            {
                Op = operation, TypeAddition = GroupRedefinitionRules.IsTypeAddition(operation,
                    GroupRedefinitionRules.ResolveScope(request.Value<string>("scope"), 1, out error, operation)),
                Groups = new List<Group> { reference },
                Elements = request["element_ids"].Values<long>().Select(Rid.Make).ToList(),
                OtherIds = copies.Select(group => group.Id).ToList(), NewGroupId = Rid.Make(NewGroup + doc.GroupCreationIdOffset),
                NewTypeId = Rid.Make(NewType + doc.GroupCreationIdOffset),
                RequiredDeletions = requiredDeletions,
                AnnotationTargets = new HashSet<long>(AnnotationDependencyAudit.TargetIds(doc, copies.Select(group => group.Id).Prepend(reference.Id), true)
                    .Concat(request["element_ids"].Values<long>()))
            };
        }

        // The in-memory group keeps no hidden type definition (measured only in Revit).
        private static HashSet<long> TypeDefinitionClosure(Document doc, ElementId typeId, ICollection<long> deleted) =>
            new HashSet<long>();

        private static void Apply(Document doc, Plan plan)
        {
            var reference = plan.Groups[0];
            foreach (long id in plan.RequiredDeletions) doc.Elements.Remove(id);
            var type = new GroupType { Id = plan.NewTypeId };
            doc.Elements[plan.NewTypeId.Value] = type;
            doc.Elements[plan.NewGroupId.Value] = new Group
            {
                Id = plan.NewGroupId, GroupType = type, Location = reference.Location,
                Members = reference.Members.Append(Rid.Make(23)).ToList()
            };
            for (int index = 0; index < plan.OtherIds.Count; index++)
            {
                var other = (Group)doc.GetElement(plan.OtherIds[index]);
                var rebuilt = new Group { Id = other.Id, GroupType = type, Location = other.Location, Members = new List<ElementId>(),
                    Document = doc, TestQuarterTurn = other.TestQuarterTurn && !doc.LoseGroupRotation, TestReflected = other.TestReflected };
                foreach (ElementId sourceId in reference.Members.Append(Rid.Make(23)))
                {
                    ElementId id = Rid.Make(50 + 10 * index + sourceId.Value - 21 + doc.GroupCreationIdOffset);
                    var source = doc.Elements[sourceId.Value];
                    doc.Elements[id.Value] = rebuilt.PlaceMember(source, id, translateOnly: sourceId.Value == 23 && doc.TranslateNewGroupMember);
                    rebuilt.Members.Add(id);
                }
                doc.Elements[other.Id.Value] = rebuilt;
            }
            doc.AfterGroupRegeneration?.Invoke(doc);
        }

        private static PostconditionCheck Verify(Document doc, Plan plan, bool observesChanges = false)
        {
            string[] required = observesChanges
                ? new[] { "instance_count", "copy_additions", "also_created", "deleted_elements", "out_of_scope_additions", "addition_roles" }
                : new[] { "instance_count", "copy_additions" };
            Group reference = (Group)doc.GetElement(plan.NewGroupId);
            var referenceMembers = HeightMembers(doc, reference, readPlacement: true);
            var evidence = new JArray();
            bool holds = true;
            foreach (ElementId copyId in plan.OtherIds)
            {
                Group copy = (Group)doc.GetElement(copyId);
                JArray added = VerifyAddedMembersInCopy(doc, referenceMembers,
                    copy, new[] { 23L }, Array.Empty<long>(), plan.ReferencePlacementBefore, plan.CopyPlacementsBefore[copy.Id],
                    plan.LoosePlacementBefore, plan.CopyRotations[copy.Id], out JObject placement);
                holds &= added != null;
                evidence.Add(new JObject { ["group_id"] = Rid.Value(copyId), ["added_member_ids"] = added, ["placement"] = placement });
            }
            return new PostconditionCheck(required).Compare("instance_count", 1 + plan.OtherIds.Count,
                doc.Elements.Values.OfType<Group>().Count(g => g.GroupType.Id == plan.NewTypeId && g.Members.Count == reference.Members.Count))
                .Record("copy_additions", new JArray(23), evidence, holds);
        }

        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, Plan plan) =>
            new ResolvedPlan { Command = "horizun_manage_groups", DocumentKey = gate.Fingerprint };
        private static JObject PlanJson(Plan plan) => new JObject { ["operation"] = plan.Op };
        private static JObject ResultJson(Document doc, Plan plan) =>
            new JObject { ["affected_group_ids"] = new JArray(plan.OtherIds.Select(Rid.Value).Prepend(Rid.Value(plan.NewGroupId))) };
        private static string Kind(GroupType type) => "model";
        private static void Quiet(Transaction transaction, FailureLog log) { }
        private static CommandResult ExecuteEditType(UIApplication app, JObject request) => throw new NotSupportedException();
        private static CommandResult List(Document doc, JObject request) => throw new NotSupportedException();
        private static CommandResult FindDuplicates(Document doc, JObject request) => throw new NotSupportedException();

        private sealed class FailureLog
        {
            public readonly List<string> Errors = new List<string>(), Warnings = new List<string>();
        }

        private sealed class Plan
        {
            public string Op;
            public bool TypeAddition;
            public ElementId NewGroupId, NewTypeId;
            public List<Group> Groups;
            public List<ElementId> Elements;
            public readonly HashSet<ElementId> AllowedExtra = new HashSet<ElementId>();
            public List<ElementId> OtherIds;
            public JObject AnnotationsBefore;
            public HashSet<long> AnnotationTargets;
            public long[] RequiredDeletions;
#pragma warning disable CS0649 // assigned by the linked command source, not by these doubles
            public ElementId DefinitionTypeId;
            public GroupPlacementSnapshot ReferencePlacementBefore;
            public readonly Dictionary<ElementId, GroupPlacementSnapshot> CopyPlacementsBefore = new Dictionary<ElementId, GroupPlacementSnapshot>();
            public readonly Dictionary<ElementId, double> CopyRotations = new Dictionary<ElementId, double>();
            public List<GroupMemberBounds> LoosePlacementBefore;
            public List<GroupMemberBounds> ReferenceMembersBeforeRegeneration;
#pragma warning restore CS0649
            public readonly Dictionary<ElementId, ElementId> RecreatedCopies = new Dictionary<ElementId, ElementId>();
            public bool AcceptRegeneration => false;
            public MembershipChanges InnerCommit;
            public JArray ApprovedAdditionRoles;
            public List<GroupRebuildChecks.Deletion> ApprovedDeletions;
            public readonly HashSet<long> ExplicitDeletes = new HashSet<long>();
            public readonly Dictionary<ElementId, double[]> Shifts = new Dictionary<ElementId, double[]>();
            public readonly List<ElementId> DependentsIncluded = new List<ElementId>();
            public readonly List<object> TagsBefore = new List<object>(), RestoredTags = new List<object>();
            public readonly JArray LostDimensions = new JArray();
        }
    }
}
