// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// VALIDATING AN IDS AGAINST AN EXPORTED IFC. The question IDS actually asks.
//
// The pre-check over a Revit model cannot answer most of this, and saying so is
// not modesty: a Revit parameter named "FireRating" is not evidence that the
// exported file carries a property "FireRating" in "Pset_WallCommon". The export
// mapping decides that, at export time. In an IFC it is not a guess at all - the
// property set is an IfcPropertySet, attached by an IfcRelDefinesByProperties,
// and it is either there or it is not.
//
// WHAT IS READ, per facet, from the file itself:
//
//   ENTITY          the instance's own type and its PredefinedType.
//   ATTRIBUTE       the positionally fixed attributes of IfcRoot and IfcObject.
//                   A CLOSED LIST, because resolving an arbitrary attribute by
//                   NAME needs the EXPRESS schema, which this build does not
//                   carry. An attribute outside the list is NOT DECIDABLE and
//                   says so; guessing an index would produce confident nonsense.
//   PROPERTY        IfcRelDefinesByProperties → IfcPropertySet → IfcProperty*,
//                   and the TYPE's own sets through IfcRelDefinesByType. This is
//                   real property-set membership: the set is named in the file.
//   CLASSIFICATION  IfcRelAssociatesClassification → IfcClassificationReference
//                   → IfcClassification.
//   MATERIAL        IfcRelAssociatesMaterial, through layers, lists, profiles and
//                   constituents.
//   PARTOF          the five relations the schema enumerates, traversed
//                   RECURSIVELY, which is what the manual specifies and what a
//                   single-hop implementation would silently get wrong for a
//                   column in a storey in a building.
//
// SCHEMA VERSIONS. Attribute positions are IFC4's. IFC2X3 agrees on every
// position this reads except where noted, and the specification's own ifcVersion
// is compared against the file's FILE_SCHEMA: a mismatch is reported rather than
// evaluated, because an IDS written for IFC4X3 evaluated against an IFC2X3 file
// produces findings about a grammar neither party agreed to.
//
// Revit-free. It reads a file.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class IdsIfcEvaluator
    {
        /// <summary>
        /// The attributes that are at the same index in EVERY schema and EVERY subtype.
        ///
        /// They are declared on IfcRoot and IfcObject, above everything else in every
        /// chain, so no inheritance information is needed to place them. This is the
        /// fallback for an entity whose chain <see cref="IdsExpress"/> does not carry;
        /// everything else now goes through the chain, which is the only thing that
        /// determines a STEP position.
        /// </summary>
        private static readonly Dictionary<string, int> RootAttributes =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["GlobalId"] = 0,
                ["Name"] = 2,
                ["Description"] = 3,
                ["ObjectType"] = 4
            };

        // =====================================================================
        // The index
        // =====================================================================

        public sealed class Index
        {
            public IfcStepReader.Document Ifc;

            /// <summary>entity id → (property set name, property name) → value as text.</summary>
            public readonly Dictionary<int, List<Tuple<string, string, string>>> Properties =
                new Dictionary<int, List<Tuple<string, string, string>>>();

            /// <summary>entity id → (system, identification).</summary>
            public readonly Dictionary<int, List<Tuple<string, string>>> Classifications =
                new Dictionary<int, List<Tuple<string, string>>>();

            /// <summary>entity id → material names.</summary>
            public readonly Dictionary<int, List<string>> Materials = new Dictionary<int, List<string>>();

            /// <summary>relation name → child entity id → the entities it is part OF.</summary>
            public readonly Dictionary<string, Dictionary<int, List<int>>> PartOf =
                new Dictionary<string, Dictionary<int, List<int>>>(StringComparer.OrdinalIgnoreCase);

            public string FileSchema;
        }

        /// <summary>Index everything the six facets need, in one pass over the file.</summary>
        public static Index Build(IfcStepReader.Document ifc)
        {
            var index = new Index { Ifc = ifc, FileSchema = ifc == null ? null : ifc.SchemaIdentifier };
            if (ifc == null) return index;

            IndexProperties(ifc, index);
            IndexClassifications(ifc, index);
            IndexMaterials(ifc, index);
            IndexPartOf(ifc, index);
            return index;
        }

        private static void IndexProperties(IfcStepReader.Document ifc, Index index)
        {
            // Occurrence property sets: IfcRelDefinesByProperties(…, RelatedObjects 4,
            // RelatingPropertyDefinition 5).
            foreach (IfcEntity rel in ifc.Of("IFCRELDEFINESBYPROPERTIES"))
            {
                IfcEntity definition = ifc.Resolve(rel.At(5));
                List<Tuple<string, string, string>> read = ReadDefinition(ifc, definition);
                if (read.Count == 0) continue;
                foreach (string reference in IfcStepReader.List(rel.At(4)))
                {
                    IfcEntity element = ifc.Resolve(reference);
                    if (element == null) continue;
                    Bucket(index.Properties, element.Id).AddRange(read);
                }
            }

            // TYPE property sets: IfcRelDefinesByType(…, RelatedObjects 4, RelatingType 5),
            // and IfcTypeObject.HasPropertySets sits at 5.
            //
            // AN OCCURRENCE INHERITS ITS TYPE'S SETS, and an implementation that read only
            // the occurrence would report "no Pset_WallCommon" for a wall whose whole
            // property set lives on its type - which is where most exporters put it.
            foreach (IfcEntity rel in ifc.Of("IFCRELDEFINESBYTYPE"))
            {
                IfcEntity type = ifc.Resolve(rel.At(5));
                if (type == null) continue;

                var fromType = new List<Tuple<string, string, string>>();
                foreach (string setReference in IfcStepReader.List(type.At(5)))
                    fromType.AddRange(ReadDefinition(ifc, ifc.Resolve(setReference)));
                if (fromType.Count == 0) continue;

                foreach (string reference in IfcStepReader.List(rel.At(4)))
                {
                    IfcEntity element = ifc.Resolve(reference);
                    if (element == null) continue;
                    Bucket(index.Properties, element.Id).AddRange(fromType);
                }
            }
        }

        /// <summary>One property set or quantity set, flattened to (set, name, value).</summary>
        private static List<Tuple<string, string, string>> ReadDefinition(IfcStepReader.Document ifc,
                                                                         IfcEntity definition)
        {
            var read = new List<Tuple<string, string, string>>();
            if (definition == null) return read;

            string kind = (definition.Type ?? "").ToUpperInvariant();

            if (kind == "IFCPROPERTYSET")
            {
                string setName = IfcStepReader.Text(definition.At(2));
                foreach (string reference in IfcStepReader.List(definition.At(4)))
                {
                    IfcEntity property = ifc.Resolve(reference);
                    if (property == null) continue;
                    string name = IfcStepReader.Text(property.At(0));
                    string value = PropertyValue(ifc, property);
                    if (name != null) read.Add(Tuple.Create(setName, name, value));
                }
                return read;
            }

            if (kind == "IFCELEMENTQUANTITY")
            {
                // QUANTITIES ARE NOT PROPERTIES and IDS does not say they are. They are
                // indexed under their set name anyway, because an IDS that asks for
                // "Qto_WallBaseQuantities/NetSideArea" is asking about this and nothing else
                // would find it.
                string setName = IfcStepReader.Text(definition.At(2));
                foreach (string reference in IfcStepReader.List(definition.At(5)))
                {
                    IfcEntity quantity = ifc.Resolve(reference);
                    if (quantity == null) continue;
                    string name = IfcStepReader.Text(quantity.At(0));
                    double? value = IfcStepReader.Number(quantity.At(3));
                    if (name != null)
                        read.Add(Tuple.Create(setName, name,
                            value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : null));
                }
                return read;
            }

            return read;
        }

        /// <summary>An IfcProperty's value as text, through the typed wrapper IFC puts around it.</summary>
        private static string PropertyValue(IfcStepReader.Document ifc, IfcEntity property)
        {
            string kind = (property.Type ?? "").ToUpperInvariant();
            switch (kind)
            {
                case "IFCPROPERTYSINGLEVALUE":
                    return Unwrap(property.At(2));
                case "IFCPROPERTYENUMERATEDVALUE":
                {
                    var values = IfcStepReader.List(property.At(2)).Select(Unwrap)
                                              .Where(v => v != null).ToList();
                    // ONE VALUE OR NOTHING. IDS compares a single value; an enumerated
                    // property holding three is not "the first one", and reporting it as
                    // such would pass or fail on whichever the exporter happened to write
                    // first.
                    return values.Count == 1 ? values[0] : null;
                }
                case "IFCPROPERTYLISTVALUE":
                {
                    var values = IfcStepReader.List(property.At(2)).Select(Unwrap)
                                              .Where(v => v != null).ToList();
                    return values.Count == 1 ? values[0] : null;
                }
                default:
                    return null;
            }
        }

        /// <summary>IFCLABEL('Concrete') → Concrete. IFCBOOLEAN(.T.) → TRUE. IFCREAL(2.5) → 2.5.</summary>
        public static string Unwrap(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string trimmed = raw.Trim();
            if (trimmed == "$" || trimmed == "*") return null;

            int open = trimmed.IndexOf('(');
            if (open > 0 && trimmed.EndsWith(")", StringComparison.Ordinal))
                trimmed = trimmed.Substring(open + 1, trimmed.Length - open - 2).Trim();

            // THE ENUMERATION IS ASKED FIRST, and the order is the whole point.
            //
            // IfcStepReader.Text is deliberately lenient: an attribute that is not
            // quoted comes back as written, because a reader that returned null for
            // everything unquoted would lose most of a STEP file. That leniency
            // swallowed .T. - Text handed back ".T." and this mapping below never
            // ran, so every boolean requirement in every IDS compared ".T." against
            // "TRUE" and failed. An enumeration cannot be a quoted string, so
            // asking for it first costs nothing and cannot shadow a real text.
            string enumeration = IfcStepReader.Enumeration(trimmed);
            if (enumeration != null)
            {
                // IDS SPELLS BOOLEANS "TRUE" AND "FALSE"; IFC writes .T. and .F. Comparing
                // them raw fails every boolean requirement ever written.
                if (enumeration == "T") return "TRUE";
                if (enumeration == "F") return "FALSE";
                if (enumeration == "U") return "UNKNOWN";
                return enumeration;
            }

            string text = IfcStepReader.Text(trimmed);
            if (text != null) return text;

            double? number = IfcStepReader.Number(trimmed);
            if (number.HasValue) return number.Value.ToString("R", CultureInfo.InvariantCulture);

            return null;
        }

        private static void IndexClassifications(IfcStepReader.Document ifc, Index index)
        {
            foreach (IfcEntity rel in ifc.Of("IFCRELASSOCIATESCLASSIFICATION"))
            {
                IfcEntity reference = ifc.Resolve(rel.At(5));
                if (reference == null) continue;

                // IfcClassificationReference: Location(0), Identification(1) — ItemReference
                // in IFC2X3, same position — Name(2), ReferencedSource(3).
                string identification = IfcStepReader.Text(reference.At(1))
                                        ?? IfcStepReader.Text(reference.At(2));
                string system = null;
                IfcEntity source = ifc.Resolve(reference.At(3));
                if (source != null)
                    system = IfcStepReader.Text(source.At(3)) ?? IfcStepReader.Text(source.At(0));
                if (system == null) system = IfcStepReader.Text(reference.At(2));

                foreach (string related in IfcStepReader.List(rel.At(4)))
                {
                    IfcEntity element = ifc.Resolve(related);
                    if (element == null) continue;
                    Bucket(index.Classifications, element.Id).Add(Tuple.Create(system, identification));
                }
            }
        }

        private static void IndexMaterials(IfcStepReader.Document ifc, Index index)
        {
            foreach (IfcEntity rel in ifc.Of("IFCRELASSOCIATESMATERIAL"))
            {
                IfcEntity material = ifc.Resolve(rel.At(5));
                foreach (string name in MaterialNames(ifc, material, 0))
                {
                    foreach (string related in IfcStepReader.List(rel.At(4)))
                    {
                        IfcEntity element = ifc.Resolve(related);
                        if (element == null) continue;
                        Bucket(index.Materials, element.Id).Add(name);
                    }
                }
            }
        }

        /// <summary>
        /// EVERY material name a construction carries, not the first one.
        ///
        /// IfcSubset.MaterialName answers "what is this made of, roughly" for a plan row and
        /// takes the first layer. IDS asks whether a material IS THERE, and a three-layer
        /// wall whose insulation is named in layer two would fail a requirement it satisfies.
        /// </summary>
        public static List<string> MaterialNames(IfcStepReader.Document ifc, IfcEntity material, int depth)
        {
            var names = new List<string>();
            if (material == null || depth > 8) return names;

            switch ((material.Type ?? "").ToUpperInvariant())
            {
                case "IFCMATERIAL":
                {
                    string name = IfcStepReader.Text(material.At(0));
                    if (name != null) names.Add(name);
                    return names;
                }
                case "IFCMATERIALLAYER":
                    return MaterialNames(ifc, ifc.Resolve(material.At(0)), depth + 1);
                case "IFCMATERIALLAYERSET":
                    foreach (string layer in IfcStepReader.List(material.At(0)))
                        names.AddRange(MaterialNames(ifc, ifc.Resolve(layer), depth + 1));
                    return names;
                case "IFCMATERIALLAYERSETUSAGE":
                    return MaterialNames(ifc, ifc.Resolve(material.At(0)), depth + 1);
                case "IFCMATERIALLIST":
                    foreach (string item in IfcStepReader.List(material.At(0)))
                        names.AddRange(MaterialNames(ifc, ifc.Resolve(item), depth + 1));
                    return names;
                case "IFCMATERIALCONSTITUENT":
                    return MaterialNames(ifc, ifc.Resolve(material.At(2)), depth + 1);
                case "IFCMATERIALCONSTITUENTSET":
                    foreach (string item in IfcStepReader.List(material.At(2)))
                        names.AddRange(MaterialNames(ifc, ifc.Resolve(item), depth + 1));
                    return names;
                case "IFCMATERIALPROFILE":
                    return MaterialNames(ifc, ifc.Resolve(material.At(2)), depth + 1);
                case "IFCMATERIALPROFILESET":
                    foreach (string item in IfcStepReader.List(material.At(2)))
                        names.AddRange(MaterialNames(ifc, ifc.Resolve(item), depth + 1));
                    return names;
                case "IFCMATERIALPROFILESETUSAGE":
                    return MaterialNames(ifc, ifc.Resolve(material.At(0)), depth + 1);
                default:
                    return names;
            }
        }

        /// <summary>
        /// The five relations the IDS schema enumerates, each indexed child → containers.
        ///
        /// Traversal is RECURSIVE at query time, not here: a column is contained in a storey
        /// which is aggregated into a building, and "is this column part of a building?" is
        /// true. A single-hop implementation answers no, confidently.
        /// </summary>
        private static void IndexPartOf(IfcStepReader.Document ifc, Index index)
        {
            // Relating(4) → Related(5)
            AddRelation(ifc, index, "IFCRELAGGREGATES", "IFCRELAGGREGATES", 5, 4);
            AddRelation(ifc, index, "IFCRELNESTS", "IFCRELNESTS", 5, 4);
            // RelatedElements(4) → RelatingStructure(5)
            AddRelation(ifc, index, "IFCRELCONTAINEDINSPATIALSTRUCTURE",
                        "IFCRELCONTAINEDINSPATIALSTRUCTURE", 4, 5);
            // RelatedObjects(4) → RelatingGroup(6)
            AddRelation(ifc, index, "IFCRELASSIGNSTOGROUP", "IFCRELASSIGNSTOGROUP", 4, 6);
            // The schema treats the void/fill pair as ONE relation string.
            AddRelation(ifc, index, "IFCRELVOIDSELEMENT", "IFCRELVOIDSELEMENT IFCRELFILLSELEMENT", 5, 4);
            AddRelation(ifc, index, "IFCRELFILLSELEMENT", "IFCRELVOIDSELEMENT IFCRELFILLSELEMENT", 5, 4);
        }

        private static void AddRelation(IfcStepReader.Document ifc, Index index, string ifcClass,
                                        string relationKey, int childAt, int parentAt)
        {
            Dictionary<int, List<int>> map;
            if (!index.PartOf.TryGetValue(relationKey, out map))
                index.PartOf[relationKey] = map = new Dictionary<int, List<int>>();

            foreach (IfcEntity rel in ifc.Of(ifcClass))
            {
                var parents = new List<int>();
                IfcEntity single = ifc.Resolve(rel.At(parentAt));
                if (single != null) parents.Add(single.Id);
                foreach (string reference in IfcStepReader.List(rel.At(parentAt)))
                {
                    IfcEntity parent = ifc.Resolve(reference);
                    if (parent != null) parents.Add(parent.Id);
                }
                if (parents.Count == 0) continue;

                var children = new List<int>();
                IfcEntity oneChild = ifc.Resolve(rel.At(childAt));
                if (oneChild != null) children.Add(oneChild.Id);
                foreach (string reference in IfcStepReader.List(rel.At(childAt)))
                {
                    IfcEntity child = ifc.Resolve(reference);
                    if (child != null) children.Add(child.Id);
                }

                foreach (int child in children)
                {
                    List<int> list;
                    if (!map.TryGetValue(child, out list)) map[child] = list = new List<int>();
                    list.AddRange(parents);
                }
            }
        }

        // =====================================================================
        // Facet evaluation
        // =====================================================================

        /// <summary>Does this entity match an entity facet? Type name, then PredefinedType.</summary>
        public static IdsMatch MatchesEntity(Index index, IfcEntity entity, IdsEntityFacet facet)
        {
            if (facet == null) return IdsMatch.Yes();
            if (facet.Unsupported != null) return IdsMatch.Unknown(facet.Unsupported);

            // IFC class names are uppercase in a STEP file and IDS mandates uppercase too, so
            // the comparison is ordinal over the upper-cased type.
            IdsMatch name = IdsRestriction.Satisfies((entity.Type ?? "").ToUpperInvariant(), facet.Name);
            if (!name.Satisfied) return name;

            if (facet.PredefinedType == null) return IdsMatch.Yes();

            string predefined = PredefinedTypeOf(index, entity);
            if (predefined == null)
                return IdsMatch.Unknown(
                    "this entity declares no PredefinedType that could be read. Its position differs " +
                    "per IFC class and this build reads it only where the last attribute is an " +
                    "enumeration; guessing an index would compare the wrong attribute.");
            return IdsRestriction.Satisfies(predefined, facet.PredefinedType);
        }

        /// <summary>
        /// PredefinedType, where it can be read WITHOUT the EXPRESS schema.
        ///
        /// It is the last attribute for most rooted product classes, and it is an
        /// ENUMERATION - `.SOLIDWALL.` - which is what makes it identifiable without knowing
        /// the class. USERDEFINED sends the real name to ObjectType, and that is followed
        /// here because an IDS asking for a custom predefined type means the ObjectType.
        /// </summary>
        public static string PredefinedTypeOf(Index index, IfcEntity entity)
        {
            if (entity == null || entity.Attributes == null || entity.Attributes.Count == 0) return null;

            for (int i = entity.Attributes.Count - 1; i >= 0 && i >= entity.Attributes.Count - 3; i--)
            {
                string enumeration = IfcStepReader.Enumeration(entity.At(i));
                if (enumeration == null) continue;
                if (string.Equals(enumeration, "USERDEFINED", StringComparison.OrdinalIgnoreCase))
                {
                    string objectType = IfcStepReader.Text(entity.At(4));
                    return objectType ?? enumeration;
                }
                return enumeration;
            }
            return null;
        }

        /// <summary>
        /// An attribute facet whose NAME is a restriction rather than a literal.
        ///
        /// The IDS meaning is "every attribute whose name matches must satisfy the value",
        /// so the set has to be enumerable - and the only thing that can enumerate an
        /// entity's attributes is its EXPRESS chain. With the chain, this is decidable;
        /// without it, it is exactly as undecidable as it was before.
        ///
        /// NO MATCH IS ABSENCE, NOT FAILURE. Cardinality decides what absence means, and
        /// deciding it here would make `prohibited` unsatisfiable: a pattern matching
        /// nothing is precisely what `prohibited` is asking for.
        /// </summary>
        private static IdsMatch MatchesAttributeByPattern(Index index, IfcEntity entity,
                                                          IdsAttributeFacet facet)
        {
            string schema = index == null || index.Ifc == null ? null : index.Ifc.SchemaIdentifier;
            List<string> attributes = IdsExpress.Attributes(schema, entity.Type);
            if (attributes == null)
                return IdsMatch.Unknown(
                    "this attribute facet constrains the attribute NAME with a restriction, and resolving " +
                    "which attributes it matches needs the EXPRESS chain of '" + entity.Type + "' in this " +
                    "schema, which this build does not carry. No claim is made.");

            var matched = new List<string>();
            for (int i = 0; i < attributes.Count; i++)
                if (IdsRestriction.Satisfies(attributes[i], facet.Name).Satisfied)
                    matched.Add(attributes[i]);

            if (matched.Count == 0)
                return IdsMatch.Missing(
                    "no attribute of '" + entity.Type + "' matches the name this facet describes. Its " +
                    "attributes are: " + string.Join(", ", attributes.ToArray()));

            // EVERY MATCH MUST SATISFY. One attribute out of four passing is not the facet
            // being satisfied; it is three counter-examples being ignored.
            foreach (string name in matched)
            {
                int position = IdsExpress.PositionOf(schema, entity.Type, name);
                string value = position < 0 ? null : IfcStepReader.Text(entity.At(position));

                if (facet.Value == null)
                {
                    if (value == null)
                        return IdsMatch.Missing("the matched attribute '" + name + "' is unset ($) in the file");
                    continue;
                }

                IdsMatch one = IdsRestriction.Satisfies(value, facet.Value);
                if (!one.Satisfied) return one;
            }
            return IdsMatch.Yes();
        }

        /// <summary>An attribute by name, resolved through the entity's EXPRESS chain.</summary>
        public static IdsMatch MatchesAttribute(Index index, IfcEntity entity, IdsAttributeFacet facet)
        {
            if (facet == null || facet.Name == null)
                return IdsMatch.Unknown("this attribute facet names no attribute.");
            // A RESTRICTION ON THE ATTRIBUTE NAME, which needs the chain to answer: the set
            // of attributes a pattern could match is the entity's attribute list, and there
            // is no other way to enumerate it. Where the chain is unknown, this still
            // refuses - the old behaviour for every case.
            if (!facet.Name.IsSimple)
                return MatchesAttributeByPattern(index, entity, facet);

            string wanted = facet.Name.Simple;
            string schema = index == null || index.Ifc == null ? null : index.Ifc.SchemaIdentifier;

            // THE CHAIN FIRST. An attribute's position is the number of attributes declared
            // above it in its entity's inheritance chain, per schema - and the chain is the
            // only thing that knows. The four-attribute table below is not a shortcut: it
            // is what remains correct when the chain is unknown, because those four sit
            // above every branching point.
            int position = IdsExpress.PositionOf(schema, entity.Type, wanted);

            if (position < 0)
            {
                bool chainKnown = IdsExpress.Attributes(schema, entity.Type) != null;
                if (chainKnown)
                    // KNOWN CHAIN, NO SUCH ATTRIBUTE. A finding about the IDS, not the
                    // model: IfcWall.PredefinedType does not exist in IFC2X3, and failing
                    // the element for it blames the wrong document.
                    return IdsMatch.Unknown(IdsExpress.Explain(schema, entity.Type, wanted));

                if (!RootAttributes.TryGetValue(wanted, out position))
                    return IdsMatch.Unknown(
                        IdsExpress.Explain(schema, entity.Type, wanted) +
                        " The attributes that can still be read without the chain are " +
                        string.Join(", ", RootAttributes.Keys) + ", because those are declared on IfcRoot " +
                        "and IfcObject and sit above every branching point.");
            }

            string actual = IfcStepReader.Text(entity.At(position));

            if (facet.Value == null)
                // NO VALUE CONSTRAINT MEANS "IT MUST BE THERE". Cardinality decides whether
                // absence is a failure; the presence question is answered here.
                return actual != null
                    ? IdsMatch.Yes()
                    : IdsMatch.Missing("the attribute '" + wanted + "' is unset ($) in the file");

            return IdsRestriction.Satisfies(actual, facet.Value);
        }

        /// <summary>
        /// A property, in a NAMED property set. This is the claim the Revit pre-check cannot make.
        /// </summary>
        public static IdsMatch MatchesProperty(Index index, IfcEntity entity, IdsPropertyFacet facet,
                                               out string observed)
        {
            observed = null;
            if (facet == null || facet.PropertySet == null || facet.BaseName == null)
                return IdsMatch.Unknown("this property facet names no property set or no base name.");

            List<Tuple<string, string, string>> all;
            if (!index.Properties.TryGetValue(entity.Id, out all) || all.Count == 0)
                return IdsMatch.Missing("this entity carries no property sets at all");

            var inSet = all.Where(p => IdsRestriction.Satisfies(p.Item1, facet.PropertySet).Satisfied).ToList();
            if (inSet.Count == 0)
            {
                observed = "sets present: " + string.Join(", ", all.Select(p => p.Item1).Distinct().Take(12));
                return IdsMatch.Missing("no property set matching " + facet.PropertySet.Describe() + " is attached");
            }

            var matching = inSet.Where(p => IdsRestriction.Satisfies(p.Item2, facet.BaseName).Satisfied).ToList();
            if (matching.Count == 0)
            {
                observed = "properties in that set: " +
                           string.Join(", ", inSet.Select(p => p.Item2).Distinct().Take(12));
                return IdsMatch.Missing("the set is there and holds no property matching " + facet.BaseName.Describe());
            }

            observed = string.Join(", ", matching.Select(p => p.Item1 + "/" + p.Item2 + " = " +
                                                              (p.Item3 ?? "(unreadable)")).Take(4));

            if (facet.Value == null) return IdsMatch.Yes();

            // ANY ONE MATCHING PROPERTY SATISFYING THE VALUE IS ENOUGH. A pattern over base
            // names can match several, and IDS asks whether the requirement is met, not
            // whether every candidate meets it.
            foreach (Tuple<string, string, string> property in matching)
                if (IdsRestriction.Satisfies(property.Item3, facet.Value).Satisfied) return IdsMatch.Yes();

            Tuple<string, string, string> first = matching[0];
            if (first.Item3 == null)
                return IdsMatch.Unknown(
                    "the property is present and its value could not be read as text, a number or an " +
                    "enumeration - it may be a list, a table or a reference this build does not unwrap. " +
                    "No claim is made about whether it satisfies " + facet.Value.Describe() + ".");
            return IdsMatch.No("the property is present and " +
                               IdsRestriction.Satisfies(first.Item3, facet.Value).Reason);
        }

        public static IdsMatch MatchesClassification(Index index, IfcEntity entity,
                                                     IdsClassificationFacet facet, out string observed)
        {
            observed = null;
            List<Tuple<string, string>> all;
            if (!index.Classifications.TryGetValue(entity.Id, out all) || all.Count == 0)
                return IdsMatch.Missing("this entity carries no classification reference at all");

            observed = string.Join(", ", all.Select(c => (c.Item1 ?? "(no system)") + ":" +
                                                         (c.Item2 ?? "(no identification)")).Take(6));

            foreach (Tuple<string, string> classification in all)
            {
                if (facet.System != null &&
                    !IdsRestriction.Satisfies(classification.Item1, facet.System).Satisfied) continue;
                if (facet.Value != null &&
                    !IdsRestriction.Satisfies(classification.Item2, facet.Value).Satisfied) continue;
                return IdsMatch.Yes();
            }
            return IdsMatch.No("no classification matches " +
                               (facet.System == null ? "" : "system " + facet.System.Describe()) +
                               (facet.Value == null ? "" : " value " + facet.Value.Describe()));
        }

        public static IdsMatch MatchesMaterial(Index index, IfcEntity entity, IdsMaterialFacet facet,
                                               out string observed)
        {
            observed = null;
            List<string> all;
            if (!index.Materials.TryGetValue(entity.Id, out all) || all.Count == 0)
                return IdsMatch.Missing("this entity has no material association at all");

            observed = string.Join(", ", all.Distinct().Take(8));
            if (facet.Value == null) return IdsMatch.Yes();

            foreach (string name in all)
                if (IdsRestriction.Satisfies(name, facet.Value).Satisfied) return IdsMatch.Yes();
            return IdsMatch.No("its material(s) are [" + observed + "] and none satisfies " +
                               facet.Value.Describe());
        }

        /// <summary>
        /// partOf, traversed RECURSIVELY as the manual specifies.
        ///
        /// A column is contained in a storey which is aggregated into a building, and "is
        /// this column part of a building" is TRUE. A single-hop implementation answers no.
        /// </summary>
        public static IdsMatch MatchesPartOf(Index index, IfcEntity entity, IdsPartOfFacet facet,
                                             out string observed)
        {
            observed = null;
            if (facet == null || facet.Entity == null)
                return IdsMatch.Unknown("this partOf facet names no entity.");
            if (facet.Unsupported != null) return IdsMatch.Unknown(facet.Unsupported);

            // NO RELATION NAMED MEANS ANY CHAIN OF THEM. A column is CONTAINED in a
            // storey that is AGGREGATED into a building; walking one relation at a
            // time never gets from the column to the building, and answered "not
            // part of it" with confidence.
            List<string> relations = facet.Relation == null
                ? index.PartOf.Keys.ToList()
                : new List<string> { facet.Relation };

            var reached = new List<IfcEntity>();
            var seen = new HashSet<int>();
            Walk(index, entity.Id, relations, seen, reached, 0);

            observed = reached.Count == 0
                ? "(this entity is part of nothing, by any indexed relation)"
                : string.Join(", ", reached.Select(e => e.Type).Distinct().Take(8));

            foreach (IfcEntity container in reached)
            {
                IdsMatch match = MatchesEntity(index, container, facet.Entity);
                if (match.Satisfied) return IdsMatch.Yes();
            }
            return IdsMatch.Missing("nothing it is part of matches the required entity");
        }

        private static void Walk(Index index, int from, List<string> relations, HashSet<int> seen,
                                 List<IfcEntity> reached, int depth)
        {
            if (depth > 24) return;               // a cycle in a file, or a pathological nesting
            foreach (string relation in relations)
            {
                Dictionary<int, List<int>> map;
                if (!index.PartOf.TryGetValue(relation, out map)) continue;
                List<int> parents;
                if (!map.TryGetValue(from, out parents)) continue;

                foreach (int parent in parents)
                {
                    if (!seen.Add(parent)) continue;
                    IfcEntity entity;
                    if (index.Ifc.ById.TryGetValue(parent, out entity) && entity != null) reached.Add(entity);
                    Walk(index, parent, relations, seen, reached, depth + 1);
                }
            }
        }

        // =====================================================================

        private static List<T> Bucket<T>(Dictionary<int, List<T>> map, int key)
        {
            List<T> list;
            if (!map.TryGetValue(key, out list)) map[key] = list = new List<T>();
            return list;
        }
    }
}
