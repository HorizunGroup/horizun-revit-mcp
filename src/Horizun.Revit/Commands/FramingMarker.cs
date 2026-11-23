// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing: the marker every framing member carries.
// Original Horizun code.
//
// WHY A MARKER. read answers "what framing did a previous apply produce for this
// wall/ceiling", remove deletes exactly that and nothing else, and a second apply
// of the same spec must recognise its own members instead of doubling them. None
// of that can be told from geometry: a stud a person modelled by hand in the same
// place is NOT ours to delete. So each member (and each work plane the tool had to
// create for a line-based family) records, in extensible storage: the source
// element (id and UniqueId), its role, its index in the plan, the spec hash and
// the plan signature it was built from.
//
// A COPY IS NOT OURS. Extensible storage travels with a copied element, so a wall
// copied together with its framing (copy/paste, array, mirror) brings members whose
// marker still names the ORIGINAL wall. Each member therefore also records its OWN
// UniqueId at write time: a marked element whose UniqueId differs from the recorded
// one is a foreign copy. remove never deletes it, idempotence never counts it, the
// spatial check never excuses it, and read lists it apart.
//
// Public read, vendor write (the same policy as the CAD/IFC provenance stores):
// another add-in may see where a member came from; only this one may claim it.
// ADDING A FIELD TO A SCHEMA ALREADY IN A DOCUMENT MAKES THAT DOCUMENT UNREADABLE:
// this field list is frozen; the next field takes a new GUID. V2 added MemberUniqueId
// (V1 never shipped in a release, so no document carries it outside test copies).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class FramingMark
    {
        public long SourceId;
        public string SourceUniqueId;
        /// <summary>A FramingRoles value, or "work_plane" for a reference plane the tool created.</summary>
        public string Role;
        public int Index;
        public string SpecHash;
        public string PlanSignature;
        /// <summary>wall | ceiling.</summary>
        public string Operation;
        /// <summary>The UniqueId the member had when the marker was written.</summary>
        public string MemberUniqueId;
        /// <summary>Read-time: the element's own UniqueId differs from MemberUniqueId (a copy of a member, not a member).</summary>
        public bool Foreign;
    }

    internal static class FramingMarker
    {
        public static readonly Guid SchemaGuid = new Guid("9b4d2e71-3c58-4a6f-b8e2-71d0c4a95f36");
        public const string SchemaName = "HorizunFramingMemberV2";
        public const string WorkPlaneRole = "work_plane";
        private const string FVersion = "SchemaVersion", FSourceId = "SourceId", FSourceUid = "SourceUniqueId",
            FRole = "Role", FIndex = "MemberIndex", FSpec = "SpecHash", FSignature = "PlanSignature", FOperation = "Operation", FMemberUid = "MemberUniqueId";

        private static Schema _cached;

        public static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return _cached; }
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Vendor);
            // The add-in's own vendor id (Horizun.addin), or every SetEntity throws.
            builder.SetVendorId(HorizunVendor.Id);
            builder.SetDocumentation("Horizun framing: which wall or ceiling, under which spec, produced this member. " +
                                     "Written by horizun_framing; read/remove find members by it.");
            builder.AddSimpleField(FVersion, typeof(int));
            builder.AddSimpleField(FIndex, typeof(int));
            foreach (string name in new[] { FSourceId, FSourceUid, FRole, FSpec, FSignature, FOperation, FMemberUid })
                builder.AddSimpleField(name, typeof(string));
            _cached = builder.Finish();
            return _cached;
        }

        /// <summary>Writes the marker; throws when Revit refuses, so the transaction fails rather than leaving an unmarked member.</summary>
        public static void Write(Element element, FramingMark m)
        {
            Schema schema = GetOrCreate();
            var entity = new Entity(schema);
            entity.Set(FVersion, 2);
            entity.Set(FIndex, m.Index);
            entity.Set(FSourceId, m.SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            entity.Set(FSourceUid, m.SourceUniqueId ?? "");
            entity.Set(FRole, m.Role ?? "");
            entity.Set(FSpec, m.SpecHash ?? "");
            entity.Set(FSignature, m.PlanSignature ?? "");
            entity.Set(FOperation, m.Operation ?? "");
            entity.Set(FMemberUid, element.UniqueId ?? "");
            element.SetEntity(entity);
        }

        public static FramingMark Read(Element element)
        {
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null || element == null) return null;
                Entity e = element.GetEntity(schema);
                if (e == null || !e.IsValid()) return null;
                long.TryParse(e.Get<string>(FSourceId), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long source);
                string own = e.Get<string>(FMemberUid);
                return new FramingMark
                {
                    SourceId = source, SourceUniqueId = e.Get<string>(FSourceUid), Role = e.Get<string>(FRole),
                    Index = e.Get<int>(FIndex), SpecHash = e.Get<string>(FSpec), PlanSignature = e.Get<string>(FSignature),
                    Operation = e.Get<string>(FOperation), MemberUniqueId = own,
                    // An empty record cannot prove the element is the one we wrote: foreign.
                    Foreign = string.IsNullOrEmpty(own) || !string.Equals(own, element.UniqueId, StringComparison.Ordinal)
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// True only for a framing member and its OWN source element: a stud sits inside the wall
        /// it frames by construction, so the spatial check after a write counts that pair as
        /// expected (SpatialCoherenceRules.Pair.FramedBy). Two members of one source are NOT
        /// excused: they meet by contact (below the touch volume), and a real interpenetration
        /// between them (mains through cross members, a planner overlap) must stay a finding.
        /// A foreign copy frames nothing.
        /// </summary>
        public static bool Frames(Element a, Element b)
        {
            if (a == null || b == null || Schema.Lookup(SchemaGuid) == null) return false;
            FramingMark ma = Read(a), mb = Read(b);
            if (ma != null && !ma.Foreign && ma.Role != WorkPlaneRole && ma.SourceId == Rid.Value(b.Id)) return true;
            return mb != null && !mb.Foreign && mb.Role != WorkPlaneRole && mb.SourceId == Rid.Value(a.Id);
        }

        /// <summary>Every marked element of the document that IS a member (not a copy) of a source in the set (all sources when null).</summary>
        public static List<KeyValuePair<Element, FramingMark>> Find(Document doc, ICollection<long> sources) => Scan(doc, sources, false);

        /// <summary>Copies of members whose marker names a source in the set: kept by remove, never counted as ours.</summary>
        public static List<KeyValuePair<Element, FramingMark>> FindForeign(Document doc, ICollection<long> sources) => Scan(doc, sources, true);

        private static List<KeyValuePair<Element, FramingMark>> Scan(Document doc, ICollection<long> sources, bool foreign)
        {
            var found = new List<KeyValuePair<Element, FramingMark>>();
            if (Schema.Lookup(SchemaGuid) == null) return found;
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                         .WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
            {
                FramingMark m = Read(e);
                if (m != null && m.Foreign == foreign && (sources == null || sources.Contains(m.SourceId))) found.Add(new KeyValuePair<Element, FramingMark>(e, m));
            }
            return found.OrderBy(p => p.Value.SourceId).ThenBy(p => p.Value.Index).ToList();
        }
    }
}
