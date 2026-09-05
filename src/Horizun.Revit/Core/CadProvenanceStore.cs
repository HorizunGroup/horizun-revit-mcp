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
// THE SCHEMA IS VERSIONED AND EVERY GUID EVER WRITTEN STAYS READABLE. Revit
// will not let a schema gain a field once a document holds it, so a new field
// means a new GUID - and a GUID that is simply replaced orphans every element
// the previous release wrote: the data is still in the file and nothing can
// find it. So each version keeps its GUID as a constant, the reader looks for
// the newest first and falls back to every older one, the version is also a
// FIELD, and a reader that meets a newer version says so instead of misreading.
//
// v1 (2026-08-27): entity, rule, set, one folded source fingerprint, as-built.
// v2 (2026-09-03): the same, plus the placement kept APART from the file - the
//     ImportInstance id, its transform, and the path - so two placements of one
//     file can be told apart and a moved placement can be measured. A v1 record
//     is read as v2 with those fields null and IsV1 true; the writer always
//     writes v2 and removes the v1 entity it replaces.
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
        /// The v1 GUID. FIXED FOREVER, and never written again: 1.1.x and 1.2.0
        /// wrote entities under it, and a reader that forgot it would report
        /// every one of those conversions as anonymous.
        /// </summary>
        public static readonly Guid SchemaGuidV1 = new Guid("7b2f4c18-5d3a-4e6b-9a71-3c0f8e2d15a4");

        /// <summary>
        /// The v2 GUID - what this build WRITES and looks for first. Also fixed
        /// forever from here: the next field added takes a v3 GUID and its own
        /// fallback in Read, exactly as this one did.
        /// </summary>
        public static readonly Guid SchemaGuidV2 = new Guid("c4a7e9d2-6b18-4f3c-8e5a-2d91f07b6c43");

        /// <summary>The current writer's GUID. Anything that wants EVERY stamped element uses <see cref="Holders"/>.</summary>
        public static Guid SchemaGuid => SchemaGuidV2;

        /// <summary>Every GUID a stamped element may carry, newest first.</summary>
        public static readonly Guid[] AllSchemaGuids = { SchemaGuidV2, SchemaGuidV1 };

        public const string SchemaName = "HorizunCadProvenance";
        public const string SchemaNameV2 = "HorizunCadProvenanceV2";
        public const int CurrentVersion = 2;

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
        // v2 only
        private const string FieldPlacementId = "PlacementId";
        private const string FieldPlacementTransform = "PlacementTransform";
        private const string FieldPlacementOrigin = "PlacementOrigin";
        private const string FieldPlacementBasis = "PlacementBasis";
        private const string FieldSourcePath = "SourcePath";

        private static Schema _cached;

        /// <summary>
        /// The schema, created once per Revit session. Must be called inside an
        /// open transaction the first time, because creating a schema is a
        /// document change.
        /// </summary>
        public static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuidV2);
            if (existing != null) { _cached = existing; return _cached; }

            var builder = new SchemaBuilder(SchemaGuidV2);
            builder.SetSchemaName(SchemaNameV2);
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
            // ADDING A FIELD TO A SCHEMA THAT IS ALREADY IN A DOCUMENT makes that
            // document unreadable, which is why the v1 schema is never touched
            // and the placement fields live under a NEW GUID. This list is
            // therefore frozen too: the next field takes SchemaGuidV3.
            foreach (string name in new[] { FieldCandidate, FieldGeometryId, FieldSemanticId, FieldRule,
                                            FieldSetId, FieldSetVersion, FieldSetSha,
                                            FieldSourceFp, FieldSourceSha, FieldLayer, FieldPlanFp,
                                            FieldBuiltGeometry, FieldWritten,
                                            FieldPlacementId, FieldPlacementTransform, FieldPlacementOrigin,
                                            FieldPlacementBasis, FieldSourcePath })
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
                entity.Set(FieldPlacementId, p.PlacementId ?? "");
                entity.Set(FieldPlacementTransform, p.PlacementTransform ?? "");
                entity.Set(FieldPlacementOrigin, p.PlacementOrigin ?? "");
                entity.Set(FieldPlacementBasis, p.PlacementBasis ?? "");
                entity.Set(FieldSourcePath, p.SourcePath ?? "");
                // A spec'd field is SET and GET with its unit, always. The
                // unit-less overload throws "The unit unitTypeId is not
                // compatible with the field description", which reads like the
                // spec is wrong when it is the CALL that is incomplete.
                entity.Set(FieldConfidence, p.Confidence, UnitTypeId.General);
                element.SetEntity(entity);
                // ONE RECORD PER ELEMENT. An element migrated from v1 must not
                // keep the old entity beside the new one: the v1 collector would
                // still find it, and a reader that met both would have to choose.
                // Deleting it AFTER the v2 write landed means a failed write
                // leaves the v1 record exactly as it was.
                RemoveV1(element);
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
                // NEWEST FIRST, then every older GUID. A v1 entity is read into
                // the same record with the placement half null: the planner then
                // knows it is looking at a record that cannot name its placement,
                // which is a different fact from "placed nowhere".
                Entity entity = null;
                bool v2 = false;
                Schema schema = Schema.Lookup(SchemaGuidV2);
                if (schema != null)
                {
                    Entity e2 = element.GetEntity(schema);
                    if (e2 != null && e2.IsValid()) { entity = e2; v2 = true; }
                }
                if (entity == null)
                {
                    schema = Schema.Lookup(SchemaGuidV1);
                    if (schema == null) return null;
                    Entity e1 = element.GetEntity(schema);
                    if (e1 == null || !e1.IsValid()) return null;
                    entity = e1;
                }

                int version = entity.Get<int>(FieldVersion);
                if (version > CurrentVersion)
                {
                    problem = "this element carries Horizun CAD provenance at schema version " + version +
                              ", and this build understands version " + CurrentVersion +
                              ". It is NOT read, because reading a newer record with an older reader is how a " +
                              "field silently means something else.";
                    return null;
                }

                var p = new CadProvenance
                {
                    SchemaVersion = v2 ? Math.Max(version, 2) : Math.Min(version, 1),
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
                if (v2)
                {
                    p.PlacementId = Blank(entity.Get<string>(FieldPlacementId));
                    p.PlacementTransform = Blank(entity.Get<string>(FieldPlacementTransform));
                    p.PlacementOrigin = Blank(entity.Get<string>(FieldPlacementOrigin));
                    p.PlacementBasis = Blank(entity.Get<string>(FieldPlacementBasis));
                    p.SourcePath = Blank(entity.Get<string>(FieldSourcePath));
                }
                return p;
            }
            catch (Exception ex)
            {
                problem = "provenance could not be read: " + ex.Message;
                return null;
            }
        }

        /// <summary>Drop the v1 entity an element still carries, if any. Inside a transaction.</summary>
        private static void RemoveV1(Element element)
        {
            try
            {
                Schema v1 = Schema.Lookup(SchemaGuidV1);
                if (v1 == null) return;
                Entity old = element.GetEntity(v1);
                if (old != null && old.IsValid()) element.DeleteEntity(v1);
            }
            catch { /* the v2 record is in; a leftover v1 entity is read second and never wins */ }
        }

        /// <summary>
        /// Every element that carries provenance under ANY version, each once.
        /// The one collector the commands share, so a v1 conversion does not
        /// vanish from an audit the day the writer moves to v2.
        /// </summary>
        public static List<Element> Holders(Document doc)
        {
            var found = new List<Element>();
            if (doc == null) return found;
            var seen = new HashSet<long>();
            foreach (Guid guid in AllSchemaGuids)
            {
                try
                {
                    if (Schema.Lookup(guid) == null) continue;   // never written in this session or file
                    foreach (Element e in new FilteredElementCollector(doc)
                                 .WhereElementIsNotElementType()
                                 .WherePasses(new ExtensibleStorageFilter(guid)))
                    {
                        if (seen.Add(Rid.Value(e.Id))) found.Add(e);
                    }
                }
                catch { }
            }
            return found;
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

            foreach (Element e in Holders(doc))
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

            foreach (Element e in Holders(doc))
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
