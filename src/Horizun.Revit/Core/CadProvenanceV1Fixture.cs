// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A FIXTURE, NOT A FEATURE: the only thing in this repository that writes the v1
// provenance schema.
//
// WHY IT HAS TO EXIST. CadProvenanceStore reads the v1 GUID and never writes it
// again, which is correct - and it leaves the v1 -> v2 migration unprovable on
// any machine where the previous release is not installed. Every element the
// current build stamps is v2, so the migration branches in CadPlacementRules,
// PlanCadUpdateCommand and ApplyCadUpdateCommand were reasoned about and never
// once run against a record that actually lacks a placement id. "Reasoned about"
// is what this repository calls unverified.
//
// WHAT IT IS ALLOWED TO CLAIM. A record written here has v1's shape, taken from
// CadProvenanceV1Shape - which is the v1 definition as it stood in
// CadProvenanceStore before provenance v2, and nothing invented. Exercising the
// migration against it proves THIS build's reader, scope rules, planner and
// apply handle a record of that shape. It does NOT prove that a 1.1.x binary
// produced that shape: no old binary is run here, and no fixture can make that
// claim. The evidence for the shape is documentary and is cited in
// CadProvenanceV1Shape and in docs/DWG-TO-BIM.md.
//
// HOW IT IS KEPT OUT OF THE PRODUCT. No command resolves it, no tool exposes it,
// and CadProvenanceV1ShapeTests fails the build if any file under Commands/ so
// much as names it. A live harness reaches it the way a debugger would: through
// horizun_execute_python, by reflection on the loaded add-in assembly. That is a
// deliberate seam - a capability only reachable when the machine owner has
// already granted arbitrary code cannot be reached by an ordinary caller at all.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Writes provenance under the v1 schema so the v1 -> v2 migration can be
    /// measured. FIXTURE ONLY. Never call this from a command.
    /// </summary>
    public static class CadProvenanceV1Fixture
    {
        /// <summary>
        /// The v1 schema, built from <see cref="CadProvenanceV1Shape"/> - the
        /// ONE place in this repository that hands a SchemaBuilder the v1 GUID.
        /// Must be called inside an open transaction the first time, because
        /// creating a schema is a document change.
        /// </summary>
        public static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(CadProvenanceV1Shape.SchemaGuid);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(CadProvenanceV1Shape.SchemaGuid);
            builder.SetSchemaName(CadProvenanceV1Shape.SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Vendor);
            builder.SetVendorId(CadProvenanceV1Shape.VendorId);
            builder.SetDocumentation(CadProvenanceV1Shape.Documentation);

            // THE FIELD LIST COMES FROM THE SHAPE, never from a literal here.
            // A second copy of the list is a second definition of "v1", and the
            // day they disagree the migration is being proved against a schema
            // no release ever wrote.
            foreach (CadProvenanceV1Field field in CadProvenanceV1Shape.Fields)
            {
                FieldBuilder added;
                if (field.ClrType == "int") added = builder.AddSimpleField(field.Name, typeof(int));
                else if (field.ClrType == "double") added = builder.AddSimpleField(field.Name, typeof(double));
                else added = builder.AddSimpleField(field.Name, typeof(string));
                // A floating-point field with no unit spec makes Revit refuse the
                // whole ENTITY, not the field. v1 spec'd exactly one.
                if (field.NumberSpec) added.SetSpec(SpecTypeId.Number);
            }
            return builder.Finish();
        }

        /// <summary>
        /// Write a record under the v1 schema and remove the v2 entity the
        /// element carries, so it is a v1 element and not an element carrying
        /// two records. Inside a transaction; false with a reason when it does
        /// not land.
        /// </summary>
        public static bool WriteV1ForMigrationTest(Element element, CadProvenance p, out string lastError)
        {
            lastError = null;
            if (element == null || p == null) { lastError = "no element or no record"; return false; }
            try
            {
                Schema schema = GetOrCreateSchema();
                var entity = new Entity(schema);
                entity.Set(CadProvenanceV1Shape.FieldVersion, CadProvenanceV1Shape.Version);
                entity.Set("CandidateId", p.CandidateId ?? "");
                entity.Set("GeometryId", p.GeometryId ?? "");
                entity.Set("SemanticId", p.SemanticId ?? "");
                entity.Set("RuleId", p.RuleId ?? "");
                entity.Set("RequirementSetId", p.RequirementSetId ?? "");
                entity.Set("RequirementSetVersion", p.RequirementSetVersion ?? "");
                entity.Set("RequirementSetSha256", p.RequirementSetSha256 ?? "");
                entity.Set("SourceFingerprint", p.SourceFingerprint ?? "");
                entity.Set("SourceFileSha256", p.SourceFileSha256 ?? "");
                entity.Set("Layer", p.Layer ?? "");
                entity.Set("PlanFingerprint", p.PlanFingerprint ?? "");
                entity.Set("BuiltGeometry", p.BuiltGeometry ?? "");
                entity.Set("WrittenUtc", p.WrittenUtc ?? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                entity.Set(CadProvenanceV1Shape.FieldConfidence, p.Confidence, UnitTypeId.General);
                element.SetEntity(entity);

                // AFTER the v1 write landed, exactly as the store removes v1
                // after a v2 write lands: a failed write must leave the element
                // with the record it had, not with none.
                RemoveV2(element);
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Rewrite the record an element already carries as a v1 record: the same
        /// entity, rule, set, source and as-built line, with the five fields v2
        /// added simply absent. This is what a model converted by 1.1.x looks
        /// like, and it is produced by DEMOTING a real conversion rather than by
        /// inventing values - so every field carries what this build's own
        /// converter wrote, and only the placement half is gone.
        /// </summary>
        public static bool DemoteToV1ForMigrationTest(Element element, out string lastError)
        {
            lastError = null;
            if (element == null) { lastError = "no element"; return false; }
            string problem;
            CadProvenance existing = CadProvenanceStore.Read(element, out problem);
            if (existing == null)
            {
                lastError = problem ?? "the element carries no readable provenance to demote";
                return false;
            }
            CadProvenance demoted = existing.Clone();
            demoted.SchemaVersion = CadProvenanceV1Shape.Version;
            demoted.PlacementId = null;
            demoted.PlacementTransform = null;
            demoted.PlacementOrigin = null;
            demoted.PlacementBasis = null;
            demoted.SourcePath = null;
            return WriteV1ForMigrationTest(element, demoted, out lastError);
        }

        /// <summary>Drop the v2 entity, so an element demoted to v1 carries one record and not two.</summary>
        private static void RemoveV2(Element element)
        {
            try
            {
                Schema v2 = Schema.Lookup(CadProvenanceStore.SchemaGuidV2);
                if (v2 == null) return;
                Entity current = element.GetEntity(v2);
                if (current != null && current.IsValid()) element.DeleteEntity(v2);
            }
            catch { /* reported by the re-read: an element still carrying v2 is not demoted */ }
        }

        /// <summary>
        /// What each element carries NOW, read back through the product's own
        /// reader: which GUID holds an entity, what version the record says it
        /// is, and which placement it names. A fixture that reported its own
        /// intention rather than the model would be worth nothing.
        /// </summary>
        public static JObject Inspect(Document doc, IEnumerable<long> elementIds)
        {
            var rows = new JArray();
            Schema v1 = Schema.Lookup(CadProvenanceV1Shape.SchemaGuid);
            Schema v2 = Schema.Lookup(CadProvenanceStore.SchemaGuidV2);
            foreach (long id in elementIds ?? new long[0])
            {
                Element e = null;
                try { if (doc != null && Rid.CanRepresent(id)) e = doc.GetElement(Rid.Make(id)); } catch { }
                if (e == null)
                {
                    rows.Add(new JObject { ["element_id"] = id, ["present"] = false });
                    continue;
                }
                string problem;
                CadProvenance p = CadProvenanceStore.Read(e, out problem);
                rows.Add(new JObject
                {
                    ["element_id"] = id,
                    ["present"] = true,
                    ["has_v1_entity"] = HasEntity(e, v1),
                    ["has_v2_entity"] = HasEntity(e, v2),
                    ["provenance_version"] = p == null ? null : (p.IsV1 ? "v1" : "v2"),
                    ["placement_id"] = p == null ? null : p.PlacementId,
                    ["semantic_id"] = p == null ? null : p.SemanticId,
                    ["source_file_sha256"] = p == null ? null : p.SourceFileSha256,
                    ["source_fingerprint"] = p == null ? null : p.SourceFingerprint,
                    ["built_geometry_mm"] = p == null ? null : p.BuiltGeometry,
                    ["problem"] = problem
                });
            }
            return new JObject { ["elements"] = rows };
        }

        private static bool HasEntity(Element e, Schema schema)
        {
            if (schema == null) return false;
            try
            {
                Entity entity = e.GetEntity(schema);
                return entity != null && entity.IsValid();
            }
            catch { return false; }
        }

        /// <summary>
        /// THE ONE ENTRY POINT A HARNESS CALLS, JSON in and JSON out, because it
        /// is reached by reflection from horizun_execute_python and an `out`
        /// parameter across that boundary is a trap for no benefit.
        ///
        ///   { "op": "demote",  "element_ids": [ .. ] }
        ///   { "op": "inspect", "element_ids": [ .. ] }
        ///
        /// A demote opens its own transaction and re-reads every element after
        /// the commit; the reply says what the MODEL holds, never what was asked
        /// for. Any element that did not end up v1 is reported as a failure and
        /// the harness is expected to stop.
        /// </summary>
        public static string Run(Document doc, string requestJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(requestJson) ? new JObject() : JObject.Parse(requestJson); }
            catch (Exception ex) { return Error("the fixture arguments are not valid JSON: " + ex.Message); }
            if (doc == null) return Error("no document");

            string op = (request.Value<string>("op") ?? "").Trim().ToLowerInvariant();
            var ids = new List<long>();
            foreach (JToken token in request["element_ids"] as JArray ?? new JArray())
            {
                long value;
                if (long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    ids.Add(value);
            }

            if (op == "inspect")
            {
                JObject inspected = Inspect(doc, ids);
                inspected["op"] = "inspect";
                inspected["schema"] = CadProvenanceV1Shape.ToJson();
                return inspected.ToString(Formatting.None);
            }

            if (op != "demote") return Error("op must be 'demote' or 'inspect' (got '" + op + "')");
            if (ids.Count == 0) return Error("element_ids is required and must name at least one element");

            var attempted = new JArray();
            using (var t = new Transaction(doc, "Horizun FIXTURE: write provenance under the v1 schema"))
            {
                t.Start();
                foreach (long id in ids)
                {
                    Element e = null;
                    try { if (Rid.CanRepresent(id)) e = doc.GetElement(Rid.Make(id)); } catch { }
                    if (e == null)
                    {
                        attempted.Add(new JObject { ["element_id"] = id, ["ok"] = false, ["error"] = "no such element" });
                        continue;
                    }
                    string why;
                    bool ok = DemoteToV1ForMigrationTest(e, out why);
                    attempted.Add(new JObject { ["element_id"] = id, ["ok"] = ok, ["error"] = why });
                }
                t.Commit();
            }

            // THE VERDICT COMES FROM RE-READING, after the commit. An element that
            // still reads v2, or that carries both entities, is not demoted -
            // whatever the write said.
            JObject after = Inspect(doc, ids);
            var verified = new List<long>();
            var failed = new JArray();
            foreach (JObject row in (after["elements"] as JArray).OfType<JObject>())
            {
                bool isV1 = string.Equals(row.Value<string>("provenance_version"), "v1", StringComparison.Ordinal);
                bool clean = isV1 && row.Value<bool?>("has_v1_entity") == true &&
                             row.Value<bool?>("has_v2_entity") == false;
                if (clean) verified.Add(row.Value<long>("element_id"));
                else failed.Add(row);
            }

            return new JObject
            {
                ["op"] = "demote",
                ["requested"] = ids.Count,
                ["attempted"] = attempted,
                ["demoted_verified"] = verified.Count,
                ["demoted_element_ids"] = new JArray(verified),
                ["not_demoted"] = failed,
                ["after"] = after["elements"],
                ["schema"] = CadProvenanceV1Shape.ToJson(),
                ["verified_by"] = "every element was re-read from the model after the commit: it must hold an " +
                                  "entity under the v1 GUID, none under the v2 GUID, and the store's own reader " +
                                  "must report it as v1.",
                ["fixture_only"] = "this wrote the RETIRED v1 schema on purpose, to make the v1 -> v2 migration " +
                                   "measurable. No product command can reach this code."
            }.ToString(Formatting.None);
        }

        private static string Error(string message) =>
            new JObject { ["ok"] = false, ["error"] = message }.ToString(Formatting.None);
    }
}
