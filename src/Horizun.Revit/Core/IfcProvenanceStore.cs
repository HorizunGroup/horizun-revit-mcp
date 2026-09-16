// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// WHERE AN IMPORTED ELEMENT CAME FROM, recorded on the element itself.
//
// This is not decoration. It is the thing that makes the second run possible.
// Without it, "import this IFC" can only ever mean "build the whole file again",
// because nothing in the model remembers what the first run built — and a second
// run over the same file produces a duplicate building, perfectly aligned with
// the first, which is the single most expensive mistake an importer can make.
//
// WHAT IS RECORDED, and why each field earns its place:
//
//   IfcGlobalId    · the IFC entity's own identity. It survives a re-export of
//                    the same model from the same authoring tool, which is what
//                    an incremental update is keyed on. Revit's UniqueId is NOT
//                    this and never will be.
//   IfcClass       · so an audit can say "these 40 elements came in as IfcWall"
//                    without re-reading the file.
//   SourceSha256   · the exact bytes the element was built from. This answers
//                    the audit's question — "was this built from THIS issue?" —
//                    which is a different question from the incremental run's.
//   SourcePath     · for a person reading the record a year later.
//   Representation · WHICH representation was used ("Axis/Curve2D",
//                    "Body/SweptSolid"). Two elements of the same class can be
//                    built by different routes, and the route is what decides
//                    how much of the original the element actually carries.
//   PlanFingerprint· the plan this came from, so the apply and the audit agree.
//   RowFingerprint · a hash of the PLANNED ROW - geometry, type, level. This is
//                    what turns a re-run into an UPDATE: without it a second
//                    import can only ask "have I seen this GlobalId?", which
//                    answers yes for an entity whose wall has since moved 300 mm.
//   WrittenUtc     · when.
//
// THE FIELD LIST IS FROZEN FROM THE FIRST RELEASE. Adding a field to a schema
// already present in a document makes that document unreadable by the build that
// lacks it; the next field takes a new GUID, exactly as the CAD provenance store
// does. RowFingerprint was added to the v1 list rather than to a v2 for one
// reason only: this store has never been written anywhere. It is new and
// uncommitted, so no document in the world carries a v1 entity to be broken. That
// exemption expires the moment this ships.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class IfcProvenance
    {
        public string GlobalId;
        public string IfcClass;
        public string SourceSha256;
        public string SourcePath;
        public string Representation;
        public string PlanFingerprint;

        /// <summary>
        /// A hash of the PLANNED ROW this element was built from: its geometry, its type, its
        /// level - everything the row would build.
        ///
        /// THIS IS WHAT MAKES A RE-RUN AN UPDATE. Without it a second import can only ask
        /// "have I seen this GlobalId?", which answers yes for an entity whose wall has since
        /// moved 300 mm, and the re-issued file reports identically to one where nothing
        /// changed at all.
        /// </summary>
        public string RowFingerprint;

        public string WrittenUtc;

        /// <summary>The element this record was read FROM. Not stored; filled in by Records().</summary>
        public long ElementId = -1;

        public JObject ToJson() => new JObject
        {
            ["ifc_global_id"] = GlobalId,
            ["ifc_class"] = IfcClass,
            ["source_sha256"] = SourceSha256,
            ["source_path"] = SourcePath,
            ["representation"] = Representation,
            ["plan_fingerprint"] = PlanFingerprint,
            ["row_fingerprint"] = RowFingerprint,
            ["written_utc"] = WrittenUtc
        };
    }

    public static class IfcProvenanceStore
    {
        public static readonly Guid SchemaGuid = new Guid("f1c93a86-4d27-4b5e-9a04-6e83d2c71f59");
        public const string SchemaName = "HorizunIfcProvenance";
        public const int CurrentVersion = 1;

        /// <summary>EXACTLY the VendorId in Horizun.addin. A vendor-write schema whose id does not match is refused on every write.</summary>
        public const string VendorId = "HRZN";

        private const string FieldVersion = "Version";
        private const string FieldGlobalId = "IfcGlobalId";
        private const string FieldClass = "IfcClass";
        private const string FieldSourceSha = "SourceSha256";
        private const string FieldSourcePath = "SourcePath";
        private const string FieldRepresentation = "Representation";
        private const string FieldPlanFp = "PlanFingerprint";
        private const string FieldRowFp = "RowFingerprint";
        private const string FieldWritten = "WrittenUtc";

        private static Schema _cached;

        /// <summary>The schema. Must be called inside an open transaction the first time: creating one is a document change.</summary>
        public static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return _cached; }

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            // Public read, vendor write: another add-in may READ where an element came
            // from, which is useful and harmless; only this one may claim to have put it
            // there.
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Vendor);
            builder.SetVendorId(VendorId);
            builder.SetDocumentation(
                "Horizun IFC provenance: which IFC entity, from which file, through which representation, " +
                "produced this element. Written by horizun_apply_ifc_plan; read by horizun_plan_from_ifc to " +
                "detect what a previous run already built. Invisible to the UI on purpose.");

            builder.AddSimpleField(FieldVersion, typeof(int));
            foreach (string name in new[] { FieldGlobalId, FieldClass, FieldSourceSha, FieldSourcePath,
                                            FieldRepresentation, FieldPlanFp, FieldRowFp,
                                            FieldWritten })
                builder.AddSimpleField(name, typeof(string));

            _cached = builder.Finish();
            return _cached;
        }

        /// <summary>Write provenance onto an element. Inside a transaction; says why when it does not land.</summary>
        public static bool Write(Element element, IfcProvenance p, out string lastError)
        {
            lastError = null;
            if (element == null || p == null) { lastError = "no element or no record"; return false; }
            try
            {
                var entity = new Entity(GetOrCreate());
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldGlobalId, p.GlobalId ?? "");
                entity.Set(FieldClass, p.IfcClass ?? "");
                entity.Set(FieldSourceSha, p.SourceSha256 ?? "");
                entity.Set(FieldSourcePath, p.SourcePath ?? "");
                entity.Set(FieldRepresentation, p.Representation ?? "");
                entity.Set(FieldPlanFp, p.PlanFingerprint ?? "");
                entity.Set(FieldRowFp, p.RowFingerprint ?? "");
                entity.Set(FieldWritten, p.WrittenUtc ??
                                         DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                element.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                // SAY WHICH FAILURE THIS WAS. "The element would not take it" and "this
                // add-in may not write this schema" need different fixes, and reporting
                // the second as the first once cost this repository a provenance layer
                // that never ran.
                lastError = ex.Message;
                return false;
            }
        }

        /// <summary>The record on an element, or null when it carries none.</summary>
        public static IfcProvenance Read(Element element)
        {
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null || element == null) return null;
                Entity entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;
                return new IfcProvenance
                {
                    GlobalId = Blank(entity.Get<string>(FieldGlobalId)),
                    IfcClass = Blank(entity.Get<string>(FieldClass)),
                    SourceSha256 = Blank(entity.Get<string>(FieldSourceSha)),
                    SourcePath = Blank(entity.Get<string>(FieldSourcePath)),
                    Representation = Blank(entity.Get<string>(FieldRepresentation)),
                    PlanFingerprint = Blank(entity.Get<string>(FieldPlanFp)),
                    RowFingerprint = Blank(entity.Get<string>(FieldRowFp)),
                    WrittenUtc = Blank(entity.Get<string>(FieldWritten))
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// GlobalId → the element built from it, for this document.
        ///
        /// THE KEY IS THE GLOBAL ID ALONE, not the id plus the file hash, and that is the
        /// decision that makes an incremental update work: a re-exported IFC has new bytes
        /// and the same GlobalIds, so keying on the hash would read every re-issue as an
        /// entirely new building. The hash is recorded and reported per element, so an
        /// audit can still ask the other question.
        /// </summary>
        /// <summary>
        /// GlobalId → the whole record, not just the id.
        ///
        /// The id alone answers "have I built this?", which is enough to avoid a duplicate and
        /// not enough to notice a change. The record carries the row fingerprint the next plan
        /// compares against.
        /// </summary>
        public static Dictionary<string, IfcProvenance> Records(Document doc)
        {
            var records = new Dictionary<string, IfcProvenance>(StringComparer.Ordinal);
            if (doc == null) return records;
            try
            {
                if (Schema.Lookup(SchemaGuid) == null) return records;
                foreach (Element element in new FilteredElementCollector(doc)
                             .WhereElementIsNotElementType()
                             .WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
                {
                    IfcProvenance p = Read(element);
                    if (p == null || string.IsNullOrWhiteSpace(p.GlobalId)) continue;
                    if (!records.ContainsKey(p.GlobalId))
                    {
                        p.ElementId = Rid.Value(element.Id);
                        records[p.GlobalId] = p;
                    }
                }
            }
            catch { }
            return records;
        }

        public static Dictionary<string, long> Index(Document doc)
        {
            var index = new Dictionary<string, long>(StringComparer.Ordinal);
            if (doc == null) return index;
            try
            {
                if (Schema.Lookup(SchemaGuid) == null) return index;   // never written in this document
                foreach (Element element in new FilteredElementCollector(doc)
                             .WhereElementIsNotElementType()
                             .WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
                {
                    IfcProvenance p = Read(element);
                    if (p == null || string.IsNullOrWhiteSpace(p.GlobalId)) continue;
                    // FIRST WINS, and the collision is not hidden: two elements claiming the
                    // same IFC entity means an earlier run was interrupted between the write
                    // and the commit, and a caller that needs to know can read both.
                    if (!index.ContainsKey(p.GlobalId)) index[p.GlobalId] = Rid.Value(element.Id);
                }
            }
            catch { }
            return index;
        }

        /// <summary>Every element in this document that remembers an IFC origin.</summary>
        public static List<Element> Holders(Document doc)
        {
            var found = new List<Element>();
            if (doc == null) return found;
            try
            {
                if (Schema.Lookup(SchemaGuid) == null) return found;
                foreach (Element element in new FilteredElementCollector(doc)
                             .WhereElementIsNotElementType()
                             .WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
                    found.Add(element);
            }
            catch { }
            return found;
        }

        private static string Blank(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
