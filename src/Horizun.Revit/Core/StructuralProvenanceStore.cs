// -----------------------------------------------------------------------------
// Horizun Revit MCP - where a bar came from, written onto the bar.
// Original Horizun code.
//
// A SEPARATE SCHEMA from the CAD one, and deliberately so. Extensible storage
// has an unforgiving property: adding a field to a schema that is already in a
// saved document makes that document unreadable. Reinforcement provenance needs
// fields CAD provenance does not have - the host, the layout, the expected
// count - so folding them into HorizunCadProvenance would break every model that
// already carries a converted drawing.
//
// Two lessons from the CAD store are copied verbatim rather than rediscovered:
// the VendorId must be EXACTLY the one in Horizun.addin or every SetEntity
// throws, and a double field must declare a spec or Revit refuses the whole
// entity rather than the field.
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
    /// <summary>What a structural plan records about one element it created.</summary>
    public sealed class StructuralProvenance
    {
        public int SchemaVersion = StructuralProvenanceStore.CurrentVersion;
        public string RuleId;
        public string RequirementSetId;
        public string RequirementSetVersion;
        public string RequirementSetSha256;
        public string PlanFingerprint;
        public long HostElementId = -1;
        public string HostUniqueId;
        public string LayoutRule;
        public int ExpectedQuantity;
        public string HorizunVersion;
        public string HorizunCommit;
        public string WrittenUtc;

        public JObject ToJson()
        {
            return new JObject
            {
                ["schema_version"] = SchemaVersion,
                ["rule_id"] = RuleId,
                ["requirement_set_id"] = RequirementSetId,
                ["requirement_set_version"] = RequirementSetVersion,
                ["requirement_set_sha256"] = RequirementSetSha256,
                ["plan_fingerprint"] = PlanFingerprint,
                ["host_element_id"] = HostElementId,
                ["host_unique_id"] = HostUniqueId,
                ["layout_rule"] = LayoutRule,
                ["expected_quantity"] = ExpectedQuantity,
                ["horizun_version"] = HorizunVersion,
                ["horizun_commit"] = HorizunCommit,
                ["written_utc"] = WrittenUtc
            };
        }
    }

    public static class StructuralProvenanceStore
    {
        /// <summary>
        /// FIXED FOREVER. Changing it orphans every element any earlier release
        /// wrote: the entity stays in the file and nothing can find it.
        /// </summary>
        public static readonly Guid SchemaGuid = new Guid("2c9e51b4-8f37-4d0a-b6e2-9a4c7d1f83b5");

        public const string SchemaName = "HorizunStructuralProvenance";
        public const int CurrentVersion = 1;

        /// <summary>EXACTLY the VendorId in Horizun.addin, or every SetEntity throws.</summary>
        public const string VendorId = "HRZN";

        private const string FieldVersion = "SchemaVersion";
        private const string FieldRule = "RuleId";
        private const string FieldSetId = "RequirementSetId";
        private const string FieldSetVersion = "RequirementSetVersion";
        private const string FieldSetSha = "RequirementSetSha256";
        private const string FieldPlanFp = "PlanFingerprint";
        private const string FieldHostId = "HostElementId";
        private const string FieldHostUid = "HostUniqueId";
        private const string FieldLayout = "LayoutRule";
        private const string FieldExpectedQty = "ExpectedQuantity";
        private const string FieldVersionTag = "HorizunVersion";
        private const string FieldCommit = "HorizunCommit";
        private const string FieldWritten = "WrittenUtc";

        private static Schema _cached;

        /// <summary>Created once per session. Must be called inside an open transaction the first time.</summary>
        public static Schema GetOrCreate()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return _cached; }

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Vendor);
            builder.SetVendorId(VendorId);
            builder.SetDocumentation(
                "Horizun structural provenance: which requirement-set rule, against which host, produced this " +
                "reinforcement. Written by horizun_apply_reinforcement; read by horizun_audit_reinforcement. " +
                "Invisible to the UI on purpose.");

            builder.AddSimpleField(FieldVersion, typeof(int));
            builder.AddSimpleField(FieldExpectedQty, typeof(int));
            // A long does not exist as an extensible-storage simple field, and the
            // element id is stored as TEXT rather than as a double: a double loses
            // exact integers above 2^53, and an id that is nearly right is worse
            // than one that is absent.
            foreach (string name in new[] { FieldRule, FieldSetId, FieldSetVersion, FieldSetSha, FieldPlanFp,
                                            FieldHostId, FieldHostUid, FieldLayout,
                                            FieldVersionTag, FieldCommit, FieldWritten })
                builder.AddSimpleField(name, typeof(string));

            _cached = builder.Finish();
            return _cached;
        }

        /// <summary>Write provenance onto an element, inside a transaction. Says why when it does not land.</summary>
        public static bool Write(Element element, StructuralProvenance p, out string lastError)
        {
            lastError = null;
            if (element == null || p == null) { lastError = "no element or no provenance"; return false; }
            try
            {
                Schema schema = GetOrCreate();
                var entity = new Entity(schema);
                entity.Set(FieldVersion, p.SchemaVersion);
                entity.Set(FieldExpectedQty, p.ExpectedQuantity);
                entity.Set(FieldRule, p.RuleId ?? "");
                entity.Set(FieldSetId, p.RequirementSetId ?? "");
                entity.Set(FieldSetVersion, p.RequirementSetVersion ?? "");
                entity.Set(FieldSetSha, p.RequirementSetSha256 ?? "");
                entity.Set(FieldPlanFp, p.PlanFingerprint ?? "");
                entity.Set(FieldHostId, p.HostElementId.ToString(CultureInfo.InvariantCulture));
                entity.Set(FieldHostUid, p.HostUniqueId ?? "");
                entity.Set(FieldLayout, p.LayoutRule ?? "");
                entity.Set(FieldVersionTag, p.HorizunVersion ?? "");
                entity.Set(FieldCommit, p.HorizunCommit ?? "");
                entity.Set(FieldWritten, p.WrittenUtc ?? DateTime.UtcNow.ToString("o"));
                element.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                // WHAT REVIT SAID, not what we guessed. The CAD store spent a
                // release blaming elements for refusing an entity they were never
                // offered, because the real error was a vendor-id mismatch.
                lastError = ex.Message;
                return false;
            }
        }

        /// <summary>Why a read produced nothing. THREE different facts, not one null.</summary>
        public const string ReadAbsent = "no_provenance";
        public const string ReadNewerSchema = "written_by_a_newer_release";
        public const string ReadFailed = "could_not_be_read";

        /// <summary>Read provenance back, or null when the element carries none - which is a fact, not an error.</summary>
        public static StructuralProvenance Read(Element element)
        {
            string why;
            return Read(element, out why);
        }

        /// <summary>
        /// Read provenance, and say WHY when there is none.
        ///
        /// One null used to answer three different questions: this element carries
        /// no provenance, this element was written by a later release whose schema
        /// this build refuses, and the read threw. The audit then reported the first
        /// of those - "a bar somebody modelled by hand carries none either" - as a
        /// fact, about bars that carried provenance it had declined to read.
        /// </summary>
        public static StructuralProvenance Read(Element element, out string why)
        {
            why = ReadAbsent;
            if (element == null) return null;
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return null;
                Entity entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;

                var p = new StructuralProvenance
                {
                    SchemaVersion = entity.Get<int>(FieldVersion),
                    ExpectedQuantity = entity.Get<int>(FieldExpectedQty),
                    RuleId = entity.Get<string>(FieldRule),
                    RequirementSetId = entity.Get<string>(FieldSetId),
                    RequirementSetVersion = entity.Get<string>(FieldSetVersion),
                    RequirementSetSha256 = entity.Get<string>(FieldSetSha),
                    PlanFingerprint = entity.Get<string>(FieldPlanFp),
                    HostUniqueId = entity.Get<string>(FieldHostUid),
                    LayoutRule = entity.Get<string>(FieldLayout),
                    HorizunVersion = entity.Get<string>(FieldVersionTag),
                    HorizunCommit = entity.Get<string>(FieldCommit),
                    WrittenUtc = entity.Get<string>(FieldWritten)
                };
                long hostId;
                p.HostElementId = long.TryParse(entity.Get<string>(FieldHostId), NumberStyles.Integer,
                                                CultureInfo.InvariantCulture, out hostId) ? hostId : -1;
                // A NEWER SCHEMA IS REFUSED rather than half-read: a later release
                // may mean something different by the same field.
                if (p.SchemaVersion > CurrentVersion) { why = ReadNewerSchema; return null; }
                why = null;
                return p;
            }
            catch { why = ReadFailed; return null; }
        }

        /// <summary>Every element in the document that carries structural provenance.</summary>
        public static Dictionary<long, StructuralProvenance> Index(Document doc)
        {
            var map = new Dictionary<long, StructuralProvenance>();
            if (doc == null) return map;
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return map;
            var found = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new ExtensibleStorageFilter(SchemaGuid))
                .ToList();
            foreach (Element e in found)
            {
                StructuralProvenance p = Read(e);
                if (p != null) map[Rid.Value(e.Id)] = p;
            }
            return map;
        }
    }
}
