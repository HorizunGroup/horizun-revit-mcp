// -----------------------------------------------------------------------------
// Horizun Revit MCP - where an IFC attribute sits, by name. Original Horizun code.
//
// AN IDS ATTRIBUTE FACET NAMES AN ATTRIBUTE. A STEP FILE NUMBERS THEM. Closing
// that gap is the whole content of this file, and getting it wrong is silent:
// reading position 8 when the attribute lives at 7 returns a real value of the
// wrong thing, and the report states it with the same confidence as a correct one.
//
// The previous build refused everything except GlobalId, Name, Description and
// ObjectType - four attributes whose positions are the same in every schema - and
// answered `not decidable` for the rest. That was honest and it failed most real
// IDS files, because the attributes people constrain are Tag, PredefinedType,
// LongName, Elevation, OverallHeight and friends.
//
// HOW STEP NUMBERS ATTRIBUTES, which is the only rule this file encodes:
// an instance serialises the attributes of its WHOLE INHERITANCE CHAIN, from the
// root of the chain down, in declaration order. So the position of an attribute is
// the number of attributes declared above it in that chain. Nothing about the
// class name predicts it; only the chain does.
//
//     IfcRoot        GlobalId, OwnerHistory, Name, Description    -> 0..3
//     IfcObject      ObjectType                                   -> 4
//     IfcProduct     ObjectPlacement, Representation              -> 5, 6
//     IfcElement     Tag                                          -> 7
//     IfcWall        PredefinedType (IFC4 only)                   -> 8
//
// THE CHAIN IS PER SCHEMA, and that is not a detail. IFC2X3's IfcWall has NO
// PredefinedType; IFC4 added it. IFC2X3 has no IfcSpatialElement layer, so
// IfcBuildingStorey's Elevation sits at a different index than in IFC4. A single
// table covering "IFC" would be right for one schema and quietly wrong for the
// other, which is the failure mode this file exists to avoid - so the schema is an
// argument, and an entity whose chain this build does not know for THAT schema is
// refused rather than approximated.
//
// WHAT IS DELIBERATELY ABSENT. Every entity in IFC is not here. The set below is
// the one an IDS actually constrains - products, elements, spatial structure and
// their types - and anything outside it returns null, which the caller turns into
// `not decidable` with the attribute named. A partial table that refuses what it
// does not know is worth having; one that guesses is worth less than none.
//
// Revit-free: string tables and arithmetic. Nothing here opens a document.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One link of an EXPRESS inheritance chain: a supertype and the attributes declared here.</summary>
    internal sealed class ExpressType
    {
        public string Name;
        public string Supertype;
        public string[] Attributes = new string[0];
    }

    public static class IdsExpress
    {
        public const string Ifc2x3 = "IFC2X3";
        public const string Ifc4 = "IFC4";

        /// <summary>
        /// Which chain table a file's schema identifier uses.
        ///
        /// IFC4X3 keeps IFC4's chains for everything in this table; where it renamed a type
        /// (IfcBuildingElement became IfcBuiltElement) both names are present, because a
        /// file written by either writes the name it knows.
        /// </summary>
        public static string Family(string schemaIdentifier)
        {
            string s = (schemaIdentifier ?? "").Trim().ToUpperInvariant().Replace("-", "_");
            if (s.StartsWith("IFC2X3", StringComparison.Ordinal)) return Ifc2x3;
            if (s.StartsWith("IFC4", StringComparison.Ordinal)) return Ifc4;
            return null;
        }

        // =====================================================================
        // The chains
        // =====================================================================

        private static ExpressType T(string name, string super, params string[] attributes) =>
            new ExpressType { Name = name, Supertype = super, Attributes = attributes ?? new string[0] };

        /// <summary>
        /// Shared by both schema families: the chain from IfcRoot down to IfcElement, and
        /// the type-object side. These are the links that did not change between IFC2X3
        /// and IFC4.
        /// </summary>
        private static readonly ExpressType[] Common =
        {
            T("IfcRoot", null, "GlobalId", "OwnerHistory", "Name", "Description"),
            T("IfcObjectDefinition", "IfcRoot"),
            T("IfcObject", "IfcObjectDefinition", "ObjectType"),
            T("IfcProduct", "IfcObject", "ObjectPlacement", "Representation"),
            T("IfcElement", "IfcProduct", "Tag"),

            // The type side. IfcTypeObject restarts from IfcRoot, so HasPropertySets is at
            // 5 and Tag - a different Tag from IfcElement's - is at 7.
            T("IfcTypeObject", "IfcObjectDefinition", "ApplicableOccurrence", "HasPropertySets"),
            T("IfcTypeProduct", "IfcTypeObject", "RepresentationMaps", "Tag"),
            T("IfcElementType", "IfcTypeProduct", "ElementType"),
        };

        /// <summary>IFC4 and IFC4X3.</summary>
        private static readonly ExpressType[] Ifc4Chains =
        {
            // Spatial. IFC4 inserted IfcSpatialElement, which IFC2X3 does not have.
            T("IfcSpatialElement", "IfcProduct", "LongName"),
            T("IfcSpatialStructureElement", "IfcSpatialElement", "CompositionType"),
            T("IfcSite", "IfcSpatialStructureElement", "RefLatitude", "RefLongitude", "RefElevation",
              "LandTitleNumber", "SiteAddress"),
            T("IfcBuilding", "IfcSpatialStructureElement", "ElevationOfRefHeight", "ElevationOfTerrain",
              "BuildingAddress"),
            T("IfcBuildingStorey", "IfcSpatialStructureElement", "Elevation"),
            T("IfcSpace", "IfcSpatialStructureElement", "PredefinedType", "ElevationWithFlooring"),

            // Elements. IFC4 gave most of them a PredefinedType; IFC4X3 renamed the
            // intermediate class and kept the shape.
            T("IfcBuildingElement", "IfcElement"),
            T("IfcBuiltElement", "IfcElement"),
            T("IfcWall", "IfcBuildingElement", "PredefinedType"),
            T("IfcWallStandardCase", "IfcWall"),
            T("IfcSlab", "IfcBuildingElement", "PredefinedType"),
            T("IfcBeam", "IfcBuildingElement", "PredefinedType"),
            T("IfcColumn", "IfcBuildingElement", "PredefinedType"),
            T("IfcMember", "IfcBuildingElement", "PredefinedType"),
            T("IfcPlate", "IfcBuildingElement", "PredefinedType"),
            T("IfcRoof", "IfcBuildingElement", "PredefinedType"),
            T("IfcStair", "IfcBuildingElement", "PredefinedType"),
            T("IfcRamp", "IfcBuildingElement", "PredefinedType"),
            T("IfcRailing", "IfcBuildingElement", "PredefinedType"),
            T("IfcCovering", "IfcBuildingElement", "PredefinedType"),
            T("IfcFooting", "IfcBuildingElement", "PredefinedType"),
            T("IfcPile", "IfcBuildingElement", "PredefinedType", "ConstructionType"),
            T("IfcCurtainWall", "IfcBuildingElement", "PredefinedType"),
            T("IfcDoor", "IfcBuildingElement", "OverallHeight", "OverallWidth", "PredefinedType",
              "OperationType", "UserDefinedOperationType"),
            T("IfcWindow", "IfcBuildingElement", "OverallHeight", "OverallWidth", "PredefinedType",
              "PartitioningType", "UserDefinedPartitioningType"),

            // Distribution. The chain, not each leaf: a leaf with no local attributes
            // inherits its positions unchanged, and listing all of them would be a table
            // nobody can check.
            T("IfcDistributionElement", "IfcElement"),
            T("IfcDistributionFlowElement", "IfcDistributionElement"),
            T("IfcFlowSegment", "IfcDistributionFlowElement"),
            T("IfcFlowFitting", "IfcDistributionFlowElement"),
            T("IfcFlowTerminal", "IfcDistributionFlowElement"),
            T("IfcFlowController", "IfcDistributionFlowElement"),
            T("IfcEnergyConversionDevice", "IfcDistributionFlowElement"),
            T("IfcDuctSegment", "IfcFlowSegment", "PredefinedType"),
            T("IfcPipeSegment", "IfcFlowSegment", "PredefinedType"),
            T("IfcCableSegment", "IfcFlowSegment", "PredefinedType"),
            T("IfcDuctFitting", "IfcFlowFitting", "PredefinedType"),
            T("IfcPipeFitting", "IfcFlowFitting", "PredefinedType"),

            T("IfcFurnishingElement", "IfcElement"),
            T("IfcFurniture", "IfcFurnishingElement", "PredefinedType"),
            T("IfcBuildingElementProxy", "IfcBuildingElement", "PredefinedType"),
            T("IfcOpeningElement", "IfcElement", "PredefinedType"),

            // Type objects people constrain by name.
            T("IfcBuildingElementType", "IfcElementType"),
            T("IfcWallType", "IfcBuildingElementType", "PredefinedType"),
            T("IfcSlabType", "IfcBuildingElementType", "PredefinedType"),
            T("IfcDoorType", "IfcBuildingElementType", "PredefinedType", "OperationType",
              "ParameterTakesPrecedence", "UserDefinedOperationType"),
            T("IfcWindowType", "IfcBuildingElementType", "PredefinedType", "PartitioningType",
              "ParameterTakesPrecedence", "UserDefinedPartitioningType"),

            // ---- added to close the coverage gap --------------------------------------
            // LEAVES WITH NO LOCAL ATTRIBUTES ARE THE SAFE ONES TO ADD: their positions are
            // entirely their parents', so the chain is right if the parent's is, and there
            // is no new number to get wrong. Each one below is a class an IDS routinely
            // constrains and which previously answered not_decidable.
            T("IfcGroup", "IfcObject"),
            T("IfcSystem", "IfcGroup"),
            T("IfcZone", "IfcSystem", "LongName"),
            T("IfcDistributionSystem", "IfcSystem", "LongName", "PredefinedType"),
            T("IfcBuildingSystem", "IfcGroup", "PredefinedType", "LongName"),

            T("IfcAnnotation", "IfcProduct"),
            T("IfcGrid", "IfcProduct", "UAxes", "VAxes", "WAxes", "PredefinedType"),
            T("IfcVirtualElement", "IfcElement"),
            T("IfcElementAssembly", "IfcElement", "AssemblyPlace", "PredefinedType"),
            T("IfcTransportElement", "IfcElement", "PredefinedType"),
            T("IfcCivilElement", "IfcElement"),
            T("IfcGeographicElement", "IfcElement", "PredefinedType"),

            T("IfcStairFlight", "IfcBuildingElement", "NumberOfRisers", "NumberOfTreads",
              "RiserHeight", "TreadLength", "PredefinedType"),
            T("IfcRampFlight", "IfcBuildingElement", "PredefinedType"),
            T("IfcShadingDevice", "IfcBuildingElement", "PredefinedType"),
            T("IfcChimney", "IfcBuildingElement", "PredefinedType"),
            T("IfcBuildingElementPart", "IfcElementComponent", "PredefinedType"),
            T("IfcElementComponent", "IfcElement"),
            T("IfcFastener", "IfcElementComponent", "PredefinedType"),
            T("IfcMechanicalFastener", "IfcElementComponent", "NominalDiameter", "NominalLength",
              "PredefinedType"),
            T("IfcReinforcingElement", "IfcElementComponent", "SteelGrade"),
            T("IfcReinforcingBar", "IfcReinforcingElement", "NominalDiameter", "CrossSectionArea",
              "BarLength", "PredefinedType", "BarSurface"),
            T("IfcReinforcingMesh", "IfcReinforcingElement", "MeshLength", "MeshWidth",
              "LongitudinalBarNominalDiameter", "TransverseBarNominalDiameter",
              "LongitudinalBarCrossSectionArea", "TransverseBarCrossSectionArea",
              "LongitudinalBarSpacing", "TransverseBarSpacing", "PredefinedType"),
        };

        /// <summary>IFC2X3, where several of the above simply do not exist.</summary>
        private static readonly ExpressType[] Ifc2x3Chains =
        {
            // No IfcSpatialElement layer: LongName and CompositionType are declared on
            // IfcSpatialStructureElement itself, so everything below shifts by one.
            T("IfcSpatialStructureElement", "IfcProduct", "LongName", "CompositionType"),
            T("IfcSite", "IfcSpatialStructureElement", "RefLatitude", "RefLongitude", "RefElevation",
              "LandTitleNumber", "SiteAddress"),
            T("IfcBuilding", "IfcSpatialStructureElement", "ElevationOfRefHeight", "ElevationOfTerrain",
              "BuildingAddress"),
            T("IfcBuildingStorey", "IfcSpatialStructureElement", "Elevation"),
            T("IfcSpace", "IfcSpatialStructureElement", "InteriorOrExteriorSpace", "ElevationWithFlooring"),

            // NO PredefinedType on the element classes. This is the difference that would
            // silently misread: an IDS asking for IfcWall.PredefinedType against a 2X3 file
            // must be told the attribute does not exist in that schema, not handed
            // whatever sits at index 8.
            T("IfcBuildingElement", "IfcElement"),
            T("IfcWall", "IfcBuildingElement"),
            T("IfcWallStandardCase", "IfcWall"),
            T("IfcSlab", "IfcBuildingElement", "PredefinedType"),
            T("IfcBeam", "IfcBuildingElement"),
            T("IfcColumn", "IfcBuildingElement"),
            T("IfcMember", "IfcBuildingElement"),
            T("IfcPlate", "IfcBuildingElement"),
            T("IfcRoof", "IfcBuildingElement", "ShapeType"),
            T("IfcStair", "IfcBuildingElement", "ShapeType"),
            T("IfcRamp", "IfcBuildingElement", "ShapeType"),
            T("IfcRailing", "IfcBuildingElement", "PredefinedType"),
            T("IfcCovering", "IfcBuildingElement", "PredefinedType"),
            T("IfcFooting", "IfcBuildingElement", "PredefinedType"),
            T("IfcPile", "IfcBuildingElement", "PredefinedType", "ConstructionType"),
            T("IfcCurtainWall", "IfcBuildingElement"),
            T("IfcDoor", "IfcBuildingElement", "OverallHeight", "OverallWidth"),
            T("IfcWindow", "IfcBuildingElement", "OverallHeight", "OverallWidth"),
            T("IfcBuildingElementProxy", "IfcBuildingElement", "CompositionType"),
            T("IfcOpeningElement", "IfcElement"),

            T("IfcDistributionElement", "IfcElement"),
            T("IfcDistributionFlowElement", "IfcDistributionElement"),
            T("IfcFlowSegment", "IfcDistributionFlowElement"),
            T("IfcFlowFitting", "IfcDistributionFlowElement"),
            T("IfcFlowTerminal", "IfcDistributionFlowElement"),
            T("IfcFlowController", "IfcDistributionFlowElement"),
            T("IfcFurnishingElement", "IfcElement"),

            T("IfcBuildingElementType", "IfcElementType"),
            T("IfcWallType", "IfcBuildingElementType", "PredefinedType"),
            T("IfcSlabType", "IfcBuildingElementType", "PredefinedType"),
            T("IfcDoorStyle", "IfcTypeProduct", "OperationType", "ConstructionType",
              "ParameterTakesPrecedence", "Sizeable"),
            T("IfcWindowStyle", "IfcTypeProduct", "ConstructionType", "OperationType",
              "ParameterTakesPrecedence", "Sizeable"),
        };

        private static readonly Dictionary<string, Dictionary<string, ExpressType>> Tables = Build();

        private static Dictionary<string, Dictionary<string, ExpressType>> Build()
        {
            var tables = new Dictionary<string, Dictionary<string, ExpressType>>(StringComparer.Ordinal);
            foreach (var pair in new[]
                     {
                         new { Family = Ifc4, Chains = Ifc4Chains },
                         new { Family = Ifc2x3, Chains = Ifc2x3Chains }
                     })
            {
                var table = new Dictionary<string, ExpressType>(StringComparer.OrdinalIgnoreCase);
                foreach (ExpressType t in Common) table[t.Name] = t;
                foreach (ExpressType t in pair.Chains) table[t.Name] = t;
                tables[pair.Family] = table;
            }
            return tables;
        }

        // =====================================================================
        // Resolving
        // =====================================================================

        /// <summary>
        /// Every attribute of an entity, in STEP order, or null when the chain is unknown.
        ///
        /// Null is the important return. It means "this build cannot place this entity's
        /// attributes in THIS schema", and the caller must turn it into an undecidable
        /// result. The alternative - falling back to the four root attributes and treating
        /// the rest as absent - reports a missing attribute for one that is present.
        /// </summary>
        public static List<string> Attributes(string schemaIdentifier, string entityName)
        {
            string family = Family(schemaIdentifier);
            if (family == null || string.IsNullOrWhiteSpace(entityName)) return null;

            Dictionary<string, ExpressType> table;
            if (!Tables.TryGetValue(family, out table)) return null;

            ExpressType type;
            if (!table.TryGetValue(entityName.Trim(), out type)) return null;

            var chain = new List<ExpressType>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExpressType cursor = type;
            while (cursor != null)
            {
                if (!seen.Add(cursor.Name)) return null;      // a cycle in the table is a bug, not a chain
                chain.Add(cursor);
                if (cursor.Supertype == null) break;
                ExpressType parent;
                if (!table.TryGetValue(cursor.Supertype, out parent)) return null;
                cursor = parent;
            }

            chain.Reverse();
            var attributes = new List<string>();
            foreach (ExpressType link in chain) attributes.AddRange(link.Attributes);
            return attributes;
        }

        /// <summary>
        /// The position of one attribute, or -1 when this build cannot place it.
        ///
        /// -1 covers two DIFFERENT situations and the caller has to tell them apart, which
        /// is why <see cref="Explain"/> exists: the chain is unknown to this build, or the
        /// chain is known and the attribute is not in it - which means the attribute does
        /// not exist on that entity in that schema, and that is a finding about the IDS.
        /// </summary>
        public static int PositionOf(string schemaIdentifier, string entityName, string attribute)
        {
            List<string> attributes = Attributes(schemaIdentifier, entityName);
            if (attributes == null || string.IsNullOrWhiteSpace(attribute)) return -1;
            for (int i = 0; i < attributes.Count; i++)
                if (string.Equals(attributes[i], attribute.Trim(), StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>Why an attribute could not be placed, in words, or null when it could.</summary>
        public static string Explain(string schemaIdentifier, string entityName, string attribute)
        {
            string family = Family(schemaIdentifier);
            if (family == null)
                return "this file declares schema '" + (schemaIdentifier ?? "(none)") + "', which this build " +
                       "has no attribute table for. Attribute positions differ between schemas, so no " +
                       "position is assumed.";

            List<string> attributes = Attributes(schemaIdentifier, entityName);
            if (attributes == null)
                return "this build does not carry the EXPRESS inheritance chain for '" + entityName + "' in " +
                       family + ". An attribute's position is the number of attributes declared above it in " +
                       "its chain, and nothing about the class name predicts it - so reading a guessed index " +
                       "would return a real value of the wrong attribute.";

            if (PositionOf(schemaIdentifier, entityName, attribute) >= 0) return null;

            return "'" + entityName + "' has no attribute '" + attribute + "' in the " + family + " EXPRESS " +
                   "schema. Its " +
                   "attributes are: " + string.Join(", ", attributes.ToArray()) + ". This is a finding about " +
                   "the IDS rather than about the model - an attribute that does not exist in the schema " +
                   "cannot be satisfied or violated by any file written in it.";
        }

        /// <summary>
        /// What this build can and cannot place, as a report.
        ///
        /// THE SECOND HALF IS THE POINT. A coverage figure that only counts what works
        /// reads as complete; naming the classes that still answer not_decidable is what
        /// tells somebody whether their IDS is affected. It is generated from the table
        /// rather than written beside it, so it cannot drift from what the table holds.
        /// </summary>
        public static JObject Coverage(string schemaIdentifier)
        {
            string family = Family(schemaIdentifier);
            Dictionary<string, ExpressType> table;
            if (family == null || !Tables.TryGetValue(family, out table))
                return new JObject
                {
                    ["schema"] = schemaIdentifier,
                    ["resolvable"] = false,
                    ["means"] = "no attribute table for this schema, so no attribute beyond the four " +
                                "root ones can be placed in it."
                };

            var resolvable = table.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            return new JObject
            {
                ["schema"] = family,
                ["entities_resolvable"] = resolvable.Count,
                ["entities"] = new JArray(resolvable),
                ["not_covered_means"] =
                    "an entity outside this list answers NOT DECIDABLE for any attribute beyond GlobalId, " +
                    "Name, Description and ObjectType - never a pass and never a failure. IFC4 declares " +
                    "several hundred entity types and this table carries the ones an IDS constrains in " +
                    "practice: products, elements, spatial structure, groups and systems, reinforcement and " +
                    "the type objects beside them. Anything else is a gap in this build, not a limit of the " +
                    "format, and adding a chain is the fix.",
                ["always_resolvable"] =
                    new JArray("GlobalId", "Name", "Description", "ObjectType")
            };
        }

        /// <summary>Every entity this build can place attributes for, for a diagnostic.</summary>
        public static IEnumerable<string> KnownEntities(string schemaIdentifier)
        {
            string family = Family(schemaIdentifier);
            Dictionary<string, ExpressType> table;
            if (family == null || !Tables.TryGetValue(family, out table)) return new string[0];
            return table.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        }
    }
}
