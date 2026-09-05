// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE v1 PROVENANCE SCHEMA, WRITTEN DOWN, so a migration can be proved.
//
// The v1 GUID is still READ by CadProvenanceStore and is never written again -
// which is right, and which leaves the v1 -> v2 migration with no way to be
// exercised on a machine where the previous build is not installed. Every
// element a current build stamps is v2, so the migration path in the planner,
// in the scope rules and in the apply was reasoned about and never run.
//
// This file is the fixture's half of the answer. It is the v1 schema as a
// DESCRIPTION - the GUID, the name, the vendor, the access levels, the
// documentation and the ordered field list with each field's type and unit spec
// - taken from the v1 definition as it stood in CadProvenanceStore before
// provenance v2 (commit c56a1be^), and from nowhere else. Nothing here is
// invented: a field that v1 did not have is a field the migration was never
// asked to read.
//
// TWO THINGS USE IT, and that is the whole point:
//
//   - CadProvenanceV1Fixture builds the schema from this list, so a fixture
//     record has exactly v1's shape rather than a plausible one;
//   - CadProvenanceV1ShapeTests pins every value here AND cross-checks the
//     field names against the constants still in CadProvenanceStore.cs, so an
//     edit to either side breaks a test instead of silently changing what the
//     word "v1" means in this repository.
//
// WHAT IT DOES NOT ESTABLISH. A record written from this description proves
// that THIS build's reader, planner and apply handle a record of that shape. It
// does not prove that the 1.1.x binary produced that shape - no binary is run
// here - and no fixture can. The evidence for the shape itself is documentary:
// the definition in this repository's own history, cited above.
//
// REVIT-FREE ON PURPOSE. It carries no `using Autodesk.*`, so the test project
// compiles it directly and the pinning runs anywhere `dotnet test` runs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One field of the v1 schema, exactly as v1 declared it.</summary>
    public sealed class CadProvenanceV1Field
    {
        public CadProvenanceV1Field(string name, string clrType, bool numberSpec)
        {
            Name = name;
            ClrType = clrType;
            NumberSpec = numberSpec;
        }

        /// <summary>The field name Revit stores it under. A rename is a different schema.</summary>
        public string Name { get; private set; }

        /// <summary>"int" or "string" or "double" - the CLR type v1 passed to AddSimpleField.</summary>
        public string ClrType { get; private set; }

        /// <summary>
        /// True for the one field v1 gave a unit spec. A floating-point field
        /// with no spec makes Revit refuse the WHOLE entity, so this is not
        /// decoration: a fixture that forgot it would write nothing and report
        /// the element as the culprit.
        /// </summary>
        public bool NumberSpec { get; private set; }

        public JObject ToJson() => new JObject
        {
            ["name"] = Name,
            ["clr_type"] = ClrType,
            ["number_spec"] = NumberSpec
        };
    }

    /// <summary>
    /// The v1 Extensible Storage schema, as data. FIXTURE INPUT ONLY: no product
    /// command reads this, and nothing that writes provenance for real may.
    /// </summary>
    public static class CadProvenanceV1Shape
    {
        /// <summary>
        /// The v1 GUID. The same constant CadProvenanceStore.SchemaGuidV1 holds -
        /// pinned equal by CadProvenanceV1ShapeTests, which reads the store's
        /// source rather than trusting two literals to stay in step.
        /// </summary>
        public static readonly Guid SchemaGuid = new Guid("7b2f4c18-5d3a-4e6b-9a71-3c0f8e2d15a4");

        /// <summary>v1's schema name. v2 took a NEW name as well as a new GUID.</summary>
        public const string SchemaName = "HorizunCadProvenance";

        /// <summary>The version v1 wrote into its own SchemaVersion field.</summary>
        public const int Version = 1;

        /// <summary>EXACTLY the VendorId in Horizun.addin; Revit refuses a vendor-write schema registered to anybody else.</summary>
        public const string VendorId = "HRZN";

        /// <summary>Public read, vendor write - what v1 declared, and what v2 still declares.</summary>
        public const string ReadAccessLevel = "Public";
        public const string WriteAccessLevel = "Vendor";

        /// <summary>The documentation string v1 set. v2 carries the same words.</summary>
        public const string Documentation =
            "Horizun CAD provenance: which DWG entity, under which requirement-set rule, produced this element. " +
            "Written by horizun_apply_cad_plan; read by horizun_audit_cad_model. Invisible to the UI on purpose.";

        public const string FieldVersion = "SchemaVersion";
        public const string FieldConfidence = "Confidence";

        /// <summary>
        /// v1's thirteen string fields, in the order v1 added them. v2 adds five
        /// more (PlacementId, PlacementTransform, PlacementOrigin, PlacementBasis,
        /// SourcePath) under a new GUID; a v1 record has none of them, which is
        /// the whole reason the planner cannot tell which placement built it.
        /// </summary>
        public static readonly string[] StringFields =
        {
            "CandidateId", "GeometryId", "SemanticId", "RuleId",
            "RequirementSetId", "RequirementSetVersion", "RequirementSetSha256",
            "SourceFingerprint", "SourceFileSha256", "Layer", "PlanFingerprint",
            "BuiltGeometry", "WrittenUtc"
        };

        /// <summary>The five fields v2 added. Named here so a test can assert a v1 record carries NONE of them.</summary>
        public static readonly string[] FieldsAddedByV2 =
        {
            "PlacementId", "PlacementTransform", "PlacementOrigin", "PlacementBasis", "SourcePath"
        };

        /// <summary>
        /// Every v1 field, in build order: the version int, then the thirteen
        /// strings, then Confidence with its Number spec. A schema builder that
        /// walks this list produces v1 and nothing else.
        /// </summary>
        public static IList<CadProvenanceV1Field> Fields
        {
            get
            {
                var fields = new List<CadProvenanceV1Field>();
                fields.Add(new CadProvenanceV1Field(FieldVersion, "int", false));
                foreach (string name in StringFields)
                    fields.Add(new CadProvenanceV1Field(name, "string", false));
                fields.Add(new CadProvenanceV1Field(FieldConfidence, "double", true));
                return fields;
            }
        }

        /// <summary>The whole description, for an artifact that has to say what it wrote.</summary>
        public static JObject ToJson() => new JObject
        {
            ["schema_guid"] = SchemaGuid.ToString(),
            ["schema_name"] = SchemaName,
            ["version"] = Version,
            ["vendor_id"] = VendorId,
            ["read_access"] = ReadAccessLevel,
            ["write_access"] = WriteAccessLevel,
            ["documentation"] = Documentation,
            ["fields"] = new JArray(Fields.Select(f => f.ToJson())),
            ["fields_added_by_v2"] = new JArray(FieldsAddedByV2),
            ["derived_from"] = "src/Horizun.Revit/Core/CadProvenanceStore.cs as it stood before provenance v2 " +
                               "(git show c56a1be^). Nothing here is invented.",
            ["does_not_prove"] = "that a 1.1.x BINARY wrote records of this shape. No binary is run to produce a " +
                                 "record from this description; it proves the CURRENT reader, planner and apply " +
                                 "handle a record of v1's shape."
        };
    }
}
