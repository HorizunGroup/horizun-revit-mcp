// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Where an element remembers which drawing it came from.
//
// WHY EXTENSIBLE STORAGE AND NOT A PARAMETER. A shared parameter is visible in
// every schedule, exportable to IFC, and editable by anyone who opens the
// properties palette. Provenance is none of those things: it is machinery, it
// must survive a round trip, and it must not turn up in somebody's door schedule
// because a CAD import once touched the element. Extensible Storage is invisible
// to the UI, survives save/reload, and travels with the element.
//
// A SHARED PARAMETER REMAINS AVAILABLE, as an explicit opt-in, for the offices
// that genuinely want provenance in a schedule. That is a decision with visible
// consequences, so it is a decision somebody makes rather than a default.
//
// THE SCHEMA IS VERSIONED AND THE GUID IS FIXED. A schema whose GUID changes
// between releases orphans every element written by the previous one - the data
// is still in the file and nothing can find it. So the GUID is a constant, the
// version is a FIELD, and a reader that meets a newer version says so instead of
// misreading it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadProvenanceStore
    {
        /// <summary>
        /// FIXED FOREVER. Changing it orphans every element any earlier release
        /// wrote: the entity is still in the file and nothing can find it.
        /// </summary>
        public static readonly Guid SchemaGuid = new Guid("7b2f4c18-5d3a-4e6b-9a71-3c0f8e2d15a4");

        public const string SchemaName = "HorizunCadProvenance";
        public const int CurrentVersion = 1;

        /// <summary>
        /// EXACTLY the VendorId in Horizun.addin. Revit will not let an add-in
        /// write a vendor-scoped schema registered to anybody else, so a typo
        /// here is not a cosmetic difference - it is provenance never working.
        /// </summary>
        public const string VendorId = "HRZN";

        private const string FieldVersion = "SchemaVersion";
        private const string FieldCandidate = "CandidateId";
        private const string FieldGeometryId = "GeometryId";
        private const string FieldSemanticId = "SemanticId";
        private const string FieldRule = "RuleId";
        private const string FieldSetId = "RequirementSetId";
        private const string FieldSetVersion = "RequirementSetVersion";
        private const string FieldSetSha = "RequirementSetSha256";
        private const string FieldSourceFp = "SourceFingerprint";
        private const string FieldSourceSha = "SourceFileSha256";
        private const string FieldLayer = "Layer";
        private const string FieldPlanFp = "PlanFingerprint";
        private const string FieldWritten = "WrittenUtc";
        private const string FieldConfidence = "Confidence";
        private const string FieldBuiltGeometry = "BuiltGeometry";

        private static Schema _cached;

        /// <summary>
        /// The schema, created once per Revit session. Must be called inside an
        /// open transaction the first time, because creating a schema is a
        /// document change.
        /// </summary>
        public static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return _cached; }

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            // Vendor-only write, public read: another add-in may READ where an
            // element came from - that is useful and harmless - but only this one
            // may claim to have put it there.
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Vendor);
            // THE VENDOR ID MUST BE THE ADD-IN'S OWN, or Revit refuses every write.
            //
            // This said "HORIZUN" while Horizun.addin registers HRZN. A
            // vendor-write schema can only be written by the add-in whose vendor
            // id matches, so SetEntity threw on every single element - and the
            // blanket catch below reported it as "the element would not take it",
            // which is the wrong culprit. Provenance, one of the three reasons
            // apply_cad_plan exists, had never once been written.
            builder.SetVendorId(VendorId);
            builder.SetDocumentation(
                "Horizun CAD provenance: which DWG entity, under which requirement-set rule, produced this element. " +
                "Written by horizun_apply_cad_plan; read by horizun_audit_cad_model. Invisible to the UI on purpose.");

            builder.AddSimpleField(FieldVersion, typeof(int));
            // ADDING A FIELD TO A SCHEMA THAT IS ALREADY IN A DOCUMENT would make
            // that document unreadable, so this is only safe because no released
            // build ever wrote one: until the VendorId was fixed on 2026-08-27
            // every SetEntity threw, and the GUID has never reached a saved file.
            // The next field added after a release must take a new GUID and a
            // migration, and CurrentVersion is the field that will say which.
            foreach (string name in new[] { FieldCandidate, FieldGeometryId, FieldSemanticId, FieldRule,
                                            FieldSetId, FieldSetVersion, FieldSetSha,
                                            FieldSourceFp, FieldSourceSha, FieldLayer, FieldPlanFp,
                                            FieldBuiltGeometry, FieldWritten })
                builder.AddSimpleField(name, typeof(string));
            // A FLOATING-POINT FIELD MUST DECLARE ITS UNITS, or Revit refuses the
            // ENTITY - not the field, the whole entity.
            //
            // MEASURED live, 2026-08-27: "Units are required for field
            // Confidence". Every element came back anonymous, and until the write
            // was made to report what Revit actually said, the reply blamed the
            // element for refusing an entity it was never offered. Confidence is
            // a ratio, so the spec is Number: dimensionless, and the same in
            // every unit system the model might be in.
            builder.AddSimpleField(FieldConfidence, typeof(double)).SetSpec(SpecTypeId.Number);

            _cached = builder.Finish();
            return _cached;
        }

        /// <summary>Write provenance onto an element. Inside a transaction; returns false when the element refuses it.</summary>
        public static bool Write(Element element, CadProvenance p) { string _; return Write(element, p, out _); }

        /// <summary>Write provenance, and say why when it does not land.</summary>
        public static bool Write(Element element, CadProvenance p, out string lastError)
        {
            lastError = null;
            if (element == null || p == null) { lastError = "no element or no record"; return false; }
            try
            {
                Schema schema = GetOrCreate();
                var entity = new Entity(schema);
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldCandidate, p.CandidateId ?? "");
                entity.Set(FieldGeometryId, p.GeometryId ?? "");
                entity.Set(FieldSemanticId, p.SemanticId ?? "");
                entity.Set(FieldRule, p.RuleId ?? "");
                entity.Set(FieldSetId, p.RequirementSetId ?? "");
                entity.Set(FieldSetVersion, p.RequirementSetVersion ?? "");
                entity.Set(FieldSetSha, p.RequirementSetSha256 ?? "");
                entity.Set(FieldSourceFp, p.SourceFingerprint ?? "");
                entity.Set(FieldSourceSha, p.SourceFileSha256 ?? "");
                entity.Set(FieldLayer, p.Layer ?? "");
                entity.Set(FieldPlanFp, p.PlanFingerprint ?? "");
                entity.Set(FieldBuiltGeometry, p.BuiltGeometry ?? "");
                entity.Set(FieldWritten, p.WrittenUtc ?? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                // A spec'd field is SET and GET with its unit, always. The
                // unit-less overload throws "The unit unitTypeId is not
                // compatible with the field description", which reads like the
                // spec is wrong when it is the CALL that is incomplete.
                entity.Set(FieldConfidence, p.Confidence, UnitTypeId.General);
                element.SetEntity(entity);
                lastError = null;
                return true;
            }
            catch (Exception ex)
            {
                // Say WHICH failure this was. "The element would not take it" and
                // "this add-in may not write this schema" need different fixes,
                // and reporting the second as the first cost this repository a
                // provenance layer that never ran.
                lastError = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Read it back. Null when the element carries none - which is a fact
        /// about the element, not an error - and a NEWER schema version is
        /// reported rather than misread.
        /// </summary>
        public static CadProvenance Read(Element element, out string problem)
        {
            problem = null;
            if (element == null) return null;
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return null;
                Entity entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;

                int version = entity.Get<int>(FieldVersion);
                if (version > CurrentVersion)
                {
                    problem = "this element carries Horizun CAD provenance at schema version " + version +
                              ", and this build understands version " + CurrentVersion +
                              ". It is NOT read, because reading a newer record with an older reader is how a " +
                              "field silently means something else.";
                    return null;
                }

                return new CadProvenance
                {
                    SchemaVersion = version,
                    CandidateId = entity.Get<string>(FieldCandidate),
                    GeometryId = entity.Get<string>(FieldGeometryId),
                    SemanticId = entity.Get<string>(FieldSemanticId),
                    RuleId = entity.Get<string>(FieldRule),
                    RequirementSetId = entity.Get<string>(FieldSetId),
                    RequirementSetVersion = entity.Get<string>(FieldSetVersion),
                    RequirementSetSha256 = entity.Get<string>(FieldSetSha),
                    SourceFingerprint = entity.Get<string>(FieldSourceFp),
                    SourceFileSha256 = entity.Get<string>(FieldSourceSha),
                    Layer = entity.Get<string>(FieldLayer),
                    PlanFingerprint = entity.Get<string>(FieldPlanFp),
                    BuiltGeometry = Blank(entity.Get<string>(FieldBuiltGeometry)),
                    WrittenUtc = entity.Get<string>(FieldWritten),
                    Confidence = entity.Get<double>(FieldConfidence, UnitTypeId.General)
                };
            }
            catch (Exception ex)
            {
                problem = "provenance could not be read: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Every element in the document that remembers a CAD origin. This is
        /// what makes an incremental run and an audit possible at all: without
        /// it, "has this already been built?" has no answer.
        /// </summary>
        /// <summary>
        /// Index by SEMANTIC id - the identity that survives a re-issue of the
        /// drawing. Indexing by candidate (revision) id would answer "was this
        /// built from these exact bytes", which is the audit's question, not the
        /// incremental run's: with it, every re-issue reads as the whole model
        /// deleted and rebuilt.
        /// </summary>
        /// <summary>
        /// An empty string means NOT RECORDED, and must not read as "recorded as
        /// nothing" - the difference decides whether an update can tell a moved
        /// drawing from a moved element.
        /// </summary>
        private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        public static Dictionary<string, List<Element>> IndexBySemanticId(Document doc, out List<string> problems)
        {
            var index = new Dictionary<string, List<Element>>(StringComparer.Ordinal);
            problems = new List<string>();
            if (doc == null) return index;
            if (Schema.Lookup(SchemaGuid) == null) return index;

            foreach (Element e in new FilteredElementCollector(doc)
                         .WhereElementIsNotElementType()
                         .WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
            {
                string problem;
                CadProvenance p = Read(e, out problem);
                if (problem != null) { problems.Add("element " + Rid.Value(e.Id) + ": " + problem); continue; }
                if (p == null || string.IsNullOrEmpty(p.SemanticId)) continue;
                List<Element> bucket;
                if (!index.TryGetValue(p.SemanticId, out bucket)) index[p.SemanticId] = bucket = new List<Element>();
                bucket.Add(e);
            }
            return index;
        }

        public static Dictionary<string, List<Element>> IndexByCandidate(Document doc, out List<string> problems)
        {
            var index = new Dictionary<string, List<Element>>(StringComparer.Ordinal);
            problems = new List<string>();
            if (doc == null) return index;
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return index;   // nothing was ever written here

            foreach (Element e in new FilteredElementCollector(doc)
                         .WhereElementIsNotElementType()
                         .WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
            {
                string problem;
                CadProvenance p = Read(e, out problem);
                if (problem != null) { problems.Add("element " + Rid.Value(e.Id) + ": " + problem); continue; }
                if (p == null || string.IsNullOrEmpty(p.CandidateId)) continue;
                List<Element> bucket;
                if (!index.TryGetValue(p.CandidateId, out bucket)) index[p.CandidateId] = bucket = new List<Element>();
                bucket.Add(e);
            }
            return index;
        }
    }
}
