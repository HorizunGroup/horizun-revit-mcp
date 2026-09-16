// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// AN IDS FILE, READ AGAINST THE PUBLISHED SCHEMA.
//
// Written from buildingSMART/IDS Schema/ids.xsd (version 1.0.0) and the User
// Manual, read 2026-09-15. Not from an example file, and not from memory: an IDS
// reader built by looking at one vendor's export handles that vendor's export.
//
// WHAT THE SCHEMA ACTUALLY SAYS, since half of this was previously guessed at:
//
//   ids > info + specifications > specification*
//   specification: @name (required), @ifcVersion (required, a LIST of
//                  IFC2X3 | IFC4 | IFC4X3_ADD2), @identifier, @description,
//                  @instructions; applicability (required) + requirements?
//   applicability: @minOccurs/@maxOccurs, and the six facets
//   requirements:  the same six facets, each with @cardinality and @instructions
//
//   THE SIX FACETS ARE entity, partOf, classification, attribute, property,
//   material. The previous reader supported three and called the others
//   unsupported; three of the six were simply unwritten.
//
// THE CARDINALITY RULES, which are not symmetrical and are easy to get wrong:
//
//   APPLICABILITY cardinality comes from minOccurs/maxOccurs on the applicability
//   element, and the manual gives the table: (1, unbounded) = required, at least
//   one must exist; (0, unbounded) = optional, if any exist they must comply;
//   (0, 0) = prohibited, none must exist AND THE REQUIREMENTS ARE IGNORED.
//   That last clause matters: a prohibited specification whose requirements were
//   evaluated would report failures about elements that must not be there.
//
//   REQUIREMENT cardinality is per facet, @cardinality, default "required":
//     required   — the facet must be present and satisfied
//     optional   — if present it must be satisfied; absent is fine
//     prohibited — it must NOT be present; presence is the failure
//   with two exceptions the schema states outright: the requirements ENTITY facet
//   has no cardinality attribute at all (always required), and partOf allows only
//   required|prohibited — the manual says optional has no meaning there.
//
// UNSUPPORTED CONSTRUCTIONS ARE NAMED. Not skipped, not treated as satisfied: a
// specification this build cannot fully evaluate is reported as NOT DECIDABLE,
// with the construction named, and it never counts as a pass.
//
// Revit-free: this is XML over a published grammar.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public enum IdsCardinality { Required, Optional, Prohibited }

    /// <summary>Where a specification's own cardinality comes from: minOccurs/maxOccurs.</summary>
    public enum IdsApplicabilityMode { Required, Optional, Prohibited }

    public abstract class IdsFacet
    {
        public abstract string Kind { get; }
        public IdsCardinality Cardinality = IdsCardinality.Required;
        public string Instructions;
        public string Uri;

        /// <summary>Why this facet cannot be evaluated by this build, or null.</summary>
        public string Unsupported;

        public virtual JObject ToJson() => new JObject
        {
            ["facet"] = Kind,
            ["cardinality"] = Cardinality.ToString().ToLowerInvariant(),
            ["instructions"] = Instructions,
            ["uri"] = Uri,
            ["unsupported"] = Unsupported
        };
    }

    public sealed class IdsEntityFacet : IdsFacet
    {
        public override string Kind => "entity";
        public IdsValue Name;
        public IdsValue PredefinedType;

        public override JObject ToJson()
        {
            JObject json = base.ToJson();
            json["name"] = Name == null ? null : Name.ToJson();
            json["predefined_type"] = PredefinedType == null ? null : PredefinedType.ToJson();
            return json;
        }
    }

    public sealed class IdsAttributeFacet : IdsFacet
    {
        public override string Kind => "attribute";
        public IdsValue Name;
        public IdsValue Value;

        public override JObject ToJson()
        {
            JObject json = base.ToJson();
            json["name"] = Name == null ? null : Name.ToJson();
            json["value"] = Value == null ? null : Value.ToJson();
            return json;
        }
    }

    public sealed class IdsClassificationFacet : IdsFacet
    {
        public override string Kind => "classification";
        public IdsValue System;
        public IdsValue Value;

        public override JObject ToJson()
        {
            JObject json = base.ToJson();
            json["system"] = System == null ? null : System.ToJson();
            json["value"] = Value == null ? null : Value.ToJson();
            return json;
        }
    }

    public sealed class IdsPropertyFacet : IdsFacet
    {
        public override string Kind => "property";
        public IdsValue PropertySet;
        public IdsValue BaseName;
        public IdsValue Value;

        /// <summary>The IFC defined type, all uppercase, e.g. IFCLABEL, IFCLENGTHMEASURE.</summary>
        public string DataType;

        public override JObject ToJson()
        {
            JObject json = base.ToJson();
            json["property_set"] = PropertySet == null ? null : PropertySet.ToJson();
            json["base_name"] = BaseName == null ? null : BaseName.ToJson();
            json["value"] = Value == null ? null : Value.ToJson();
            json["data_type"] = DataType;
            return json;
        }
    }

    public sealed class IdsMaterialFacet : IdsFacet
    {
        public override string Kind => "material";
        public IdsValue Value;

        public override JObject ToJson()
        {
            JObject json = base.ToJson();
            json["value"] = Value == null ? null : Value.ToJson();
            return json;
        }
    }

    public sealed class IdsPartOfFacet : IdsFacet
    {
        public override string Kind => "partOf";
        public IdsEntityFacet Entity;

        /// <summary>One of the five relation strings the schema enumerates, or null for "any of them".</summary>
        public string Relation;

        public override JObject ToJson()
        {
            JObject json = base.ToJson();
            json["entity"] = Entity == null ? null : Entity.ToJson();
            json["relation"] = Relation;
            json["relation_means"] = Relation == null
                ? "no relation was named, so ALL the supported relationships are traversed, recursively."
                : "only " + Relation + " is traversed, recursively.";
            return json;
        }
    }

    /// <summary>The six facets of an applicability or a requirements block.</summary>
    public sealed class IdsFacetSet
    {
        public IdsEntityFacet Entity;
        public readonly List<IdsPartOfFacet> PartOf = new List<IdsPartOfFacet>();
        public readonly List<IdsClassificationFacet> Classifications = new List<IdsClassificationFacet>();
        public readonly List<IdsAttributeFacet> Attributes = new List<IdsAttributeFacet>();
        public readonly List<IdsPropertyFacet> Properties = new List<IdsPropertyFacet>();
        public readonly List<IdsMaterialFacet> Materials = new List<IdsMaterialFacet>();

        public IEnumerable<IdsFacet> All()
        {
            if (Entity != null) yield return Entity;
            foreach (IdsFacet f in PartOf) yield return f;
            foreach (IdsFacet f in Classifications) yield return f;
            foreach (IdsFacet f in Attributes) yield return f;
            foreach (IdsFacet f in Properties) yield return f;
            foreach (IdsFacet f in Materials) yield return f;
        }

        public int Count => All().Count();

        public JArray ToJson() => new JArray(All().Select(f => f.ToJson()));
    }

    public sealed class IdsSpecification
    {
        public string Name;
        public string Identifier;
        public string Description;
        public string Instructions;
        public string RequirementsDescription;

        /// <summary>IFC2X3 / IFC4 / IFC4X3_ADD2, as a list. The schema makes this required.</summary>
        public readonly List<string> IfcVersions = new List<string>();

        public int MinOccurs = 1;

        /// <summary>null means "unbounded".</summary>
        public int? MaxOccurs;

        public IdsFacetSet Applicability = new IdsFacetSet();
        public IdsFacetSet Requirements;

        /// <summary>Why this specification cannot be evaluated, or null.</summary>
        public string Undecidable;

        /// <summary>
        /// Why this specification does not comply with the audit specification, or null.
        ///
        /// DIFFERENT FROM Undecidable, which says this build could not evaluate it. This
        /// says nobody can: the question is malformed, so no file satisfies or violates
        /// it. Reporting it as a failure makes a statement about somebody's model when
        /// the defect is in the requirements document.
        /// </summary>
        public string InvalidBecause;

        /// <summary>Record a defect of the specification itself. First one wins.</summary>
        public void Invalidate(string because)
        {
            if (InvalidBecause == null) InvalidBecause = because;
        }

        /// <summary>
        /// The manual's table, in code:
        ///   (1, unbounded) required   — at least one applicable element must exist
        ///   (0, unbounded) optional   — if any exist they must comply
        ///   (0, 0)         prohibited — none must exist, AND requirements are ignored
        /// </summary>
        public IdsApplicabilityMode Mode
        {
            get
            {
                if (MinOccurs == 0 && MaxOccurs.HasValue && MaxOccurs.Value == 0)
                    return IdsApplicabilityMode.Prohibited;
                return MinOccurs >= 1 ? IdsApplicabilityMode.Required : IdsApplicabilityMode.Optional;
            }
        }

        public JObject ToJson() => new JObject
        {
            ["name"] = Name,
            ["identifier"] = Identifier,
            ["description"] = Description,
            ["instructions"] = Instructions,
            ["ifc_versions"] = new JArray(IfcVersions),
            ["min_occurs"] = MinOccurs,
            ["max_occurs"] = MaxOccurs.HasValue ? (JToken)MaxOccurs.Value : "unbounded",
            ["mode"] = Mode.ToString().ToLowerInvariant(),
            ["mode_means"] = Mode == IdsApplicabilityMode.Prohibited
                ? "NO element may match this applicability, and the requirements are NOT evaluated: " +
                  "reporting requirement failures about elements that must not exist would name the " +
                  "wrong defect."
                : Mode == IdsApplicabilityMode.Required
                    ? "at least one element must match this applicability, and every match must satisfy " +
                      "the requirements."
                    : "matching elements are optional; any that exist must satisfy the requirements.",
            ["applicability"] = Applicability.ToJson(),
            ["requirements"] = Requirements == null ? new JArray() : Requirements.ToJson(),
            ["requirements_description"] = RequirementsDescription,
            ["undecidable"] = Undecidable
        };
    }

    public sealed class IdsFile
    {
        public string Title;
        public string Version;
        public string Description;
        public string Author;
        public string Date;
        public string Purpose;
        public string Milestone;

        public readonly List<IdsSpecification> Specifications = new List<IdsSpecification>();

        /// <summary>Defects in the FILE, which are never findings about a model.</summary>
        public readonly List<string> Problems = new List<string>();

        public JObject InfoJson() => new JObject
        {
            ["title"] = Title,
            ["version"] = Version,
            ["description"] = Description,
            ["author"] = Author,
            ["date"] = Date,
            ["purpose"] = Purpose,
            ["milestone"] = Milestone,
            ["specifications"] = Specifications.Count,
            ["file_problems"] = new JArray(Problems)
        };
    }

    public static class IdsReader
    {
        public const string Namespace = IdsRestriction.Namespace;

        /// <summary>The ifcVersion values the schema enumerates. Anything else is a file defect.</summary>
        public static readonly string[] KnownIfcVersions = { "IFC2X3", "IFC4", "IFC4X3_ADD2" };

        /// <summary>The five relation strings the schema enumerates for partOf.</summary>
        public static readonly string[] KnownRelations =
        {
            "IFCRELAGGREGATES", "IFCRELASSIGNSTOGROUP", "IFCRELCONTAINEDINSPATIALSTRUCTURE",
            "IFCRELNESTS", "IFCRELVOIDSELEMENT IFCRELFILLSELEMENT"
        };

        public const long MaxBytes = 32L * 1024 * 1024;

        public static IdsFile Read(string path, out string error)
        {
            error = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { error = "no file at '" + path + "'."; return null; }
                if (info.Length > MaxBytes)
                {
                    error = "'" + path + "' is " + (info.Length / (1024 * 1024)) + " MB; the bound is " +
                            (MaxBytes / (1024 * 1024)) + " MB.";
                    return null;
                }

                var document = new XmlDocument();
                // DTD PROCESSING OFF. An .ids arrives from outside; a document that can fetch
                // an external entity is a document that can read this machine's filesystem.
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true
                };
                using (XmlReader reader = XmlReader.Create(path, settings))
                    document.Load(reader);

                return Parse(document, out error);
            }
            catch (XmlException ex)
            {
                error = "'" + path + "' is not well-formed XML: " + ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                error = "'" + path + "' could not be read: " + ex.Message;
                return null;
            }
        }

        public static IdsFile Parse(XmlDocument document, out string error)
        {
            error = null;
            XmlElement root = document == null ? null : document.DocumentElement;
            if (root == null) { error = "the document is empty."; return null; }

            if (!string.Equals(root.LocalName, "ids", StringComparison.Ordinal))
            {
                error = "the root element is <" + root.LocalName + "> and an IDS file's root is <ids>.";
                return null;
            }
            if (!string.Equals(root.NamespaceURI, Namespace, StringComparison.Ordinal))
            {
                // THE NAMESPACE IS THE VERSION HANDSHAKE. A file in another namespace is a
                // different standard, or a draft of this one, and reading it with these rules
                // would produce confident findings from a grammar nobody agreed on.
                error = "this file declares the namespace '" + root.NamespaceURI + "' and this build " +
                        "reads '" + Namespace + "'. That namespace IS the version handshake: reading " +
                        "another one with these rules would produce confident findings from a grammar " +
                        "nobody agreed on. Nothing was evaluated.";
                return null;
            }

            var file = new IdsFile();
            ReadInfo(root, file);

            XmlNode specifications = IdsRestriction.FirstChild(root, "specifications", Namespace);
            if (specifications == null)
            {
                error = "the file declares no <specifications> element.";
                return null;
            }

            foreach (XmlNode node in IdsRestriction.Children(specifications, "specification", Namespace))
                file.Specifications.Add(ReadSpecification(node, file));

            if (file.Specifications.Count == 0)
                file.Problems.Add("the file holds no <specification>. The schema requires at least one.");

            return file;
        }

        private static void ReadInfo(XmlNode root, IdsFile file)
        {
            XmlNode info = IdsRestriction.FirstChild(root, "info", Namespace);
            if (info == null)
            {
                file.Problems.Add("the file declares no <info> block, which the schema requires.");
                return;
            }
            file.Title = Text(info, "title");
            file.Version = Text(info, "version");
            file.Description = Text(info, "description");
            file.Author = Text(info, "author");
            file.Date = Text(info, "date");
            file.Purpose = Text(info, "purpose");
            file.Milestone = Text(info, "milestone");
            if (string.IsNullOrWhiteSpace(file.Title))
                file.Problems.Add("<info> carries no <title>, which the schema requires.");
        }

        private static IdsSpecification ReadSpecification(XmlNode node, IdsFile file)
        {
            var specification = new IdsSpecification
            {
                Name = Attribute(node, "name"),
                Identifier = Attribute(node, "identifier"),
                Description = Attribute(node, "description"),
                Instructions = Attribute(node, "instructions")
            };

            string versions = Attribute(node, "ifcVersion");
            if (string.IsNullOrWhiteSpace(versions))
            {
                file.Problems.Add("specification '" + (specification.Name ?? "(unnamed)") +
                                  "' declares no ifcVersion, which the schema makes required.");
            }
            else
            {
                foreach (string version in versions.Split(new[] { ' ', '\t', '\n', '\r' },
                                                          StringSplitOptions.RemoveEmptyEntries))
                {
                    specification.IfcVersions.Add(version);
                    if (!KnownIfcVersions.Contains(version, StringComparer.Ordinal))
                    {
                        string defect = "specification '" + (specification.Name ?? "(unnamed)") +
                                        "' names ifcVersion '" + version + "', which is not one of " +
                                        string.Join(", ", KnownIfcVersions) + ".";
                        file.Problems.Add(defect);
                        specification.Invalidate(defect);
                    }
                }
            }

            XmlNode applicability = IdsRestriction.FirstChild(node, "applicability", Namespace);
            if (applicability == null)
            {
                specification.Undecidable =
                    "this specification declares no <applicability>, so there is no way to know which " +
                    "elements it is about. Nothing was evaluated for it.";
                return specification;
            }

            ReadOccurs(applicability, specification, file);
            specification.Applicability = ReadFacets(applicability, false, file, specification);

            if (specification.Applicability.Count == 0)
                specification.Undecidable =
                    "this specification's <applicability> holds no facets, so it applies to EVERY " +
                    "element in the model. That is almost certainly an authoring mistake, and " +
                    "evaluating it would produce a finding about every element at once.";

            XmlNode requirements = IdsRestriction.FirstChild(node, "requirements", Namespace);
            if (requirements != null)
            {
                specification.RequirementsDescription = Attribute(requirements, "description");
                specification.Requirements = ReadFacets(requirements, true, file, specification);
            }

            return specification;
        }

        private static void ReadOccurs(XmlNode applicability, IdsSpecification specification, IdsFile file)
        {
            string min = Attribute(applicability, "minOccurs");
            string max = Attribute(applicability, "maxOccurs");

            int parsed;
            if (!string.IsNullOrWhiteSpace(min))
            {
                if (int.TryParse(min, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0)
                    specification.MinOccurs = parsed;
                else
                {
                    string defect = "specification '" + specification.Name + "' has minOccurs='" + min +
                                    "', which is not a non-negative integer.";
                    file.Problems.Add(defect);
                    specification.Invalidate(defect);
                }
            }

            if (string.IsNullOrWhiteSpace(max) ||
                string.Equals(max, "unbounded", StringComparison.OrdinalIgnoreCase))
            {
                specification.MaxOccurs = null;
            }
            else if (int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0)
            {
                specification.MaxOccurs = parsed;
            }
            else
            {
                file.Problems.Add("specification '" + specification.Name + "' has maxOccurs='" + max +
                                  "', which is neither a non-negative integer nor 'unbounded'.");
            }
        }

        private static IdsFacetSet ReadFacets(XmlNode container, bool isRequirements, IdsFile file,
                                              IdsSpecification specification)
        {
            var set = new IdsFacetSet();

            XmlNode entity = IdsRestriction.FirstChild(container, "entity", Namespace);
            if (entity != null) set.Entity = ReadEntity(entity, isRequirements);

            foreach (XmlNode node in IdsRestriction.Children(container, "partOf", Namespace))
            {
                var facet = new IdsPartOfFacet
                {
                    Relation = Attribute(node, "relation"),
                    Instructions = Attribute(node, "instructions")
                };
                XmlNode inner = IdsRestriction.FirstChild(node, "entity", Namespace);
                facet.Entity = inner == null ? null : ReadEntity(inner, false);
                if (facet.Entity == null)
                    facet.Unsupported = "this partOf facet holds no <entity>, which the schema requires.";
                if (facet.Relation != null && !IdsReader.KnownRelations.Contains(facet.Relation, StringComparer.Ordinal))
                    facet.Unsupported = "relation '" + facet.Relation + "' is not one of the five the " +
                                        "schema enumerates, so it is not traversed.";
                if (isRequirements)
                {
                    // THE SCHEMA ALLOWS ONLY required|prohibited HERE. The manual is explicit that
                    // "optional" has no meaning for partOf: saying a thing may or may not be part
                    // of something else states nothing.
                    facet.Cardinality = ReadCardinality(node, file, specification, "partOf", false);
                }
                set.PartOf.Add(facet);
            }

            foreach (XmlNode node in IdsRestriction.Children(container, "classification", Namespace))
            {
                var facet = new IdsClassificationFacet
                {
                    System = IdsRestriction.Read(IdsRestriction.FirstChild(node, "system", Namespace)),
                    Value = IdsRestriction.Read(IdsRestriction.FirstChild(node, "value", Namespace)),
                    Uri = Attribute(node, "uri"),
                    Instructions = Attribute(node, "instructions")
                };
                if (isRequirements) facet.Cardinality = ReadCardinality(node, file, specification, "classification", true);
                set.Classifications.Add(facet);
            }

            foreach (XmlNode node in IdsRestriction.Children(container, "attribute", Namespace))
            {
                var facet = new IdsAttributeFacet
                {
                    Name = IdsRestriction.Read(IdsRestriction.FirstChild(node, "name", Namespace)),
                    Value = IdsRestriction.Read(IdsRestriction.FirstChild(node, "value", Namespace)),
                    Instructions = Attribute(node, "instructions")
                };
                if (isRequirements) facet.Cardinality = ReadCardinality(node, file, specification, "attribute", true);
                set.Attributes.Add(facet);
            }

            foreach (XmlNode node in IdsRestriction.Children(container, "property", Namespace))
            {
                var facet = new IdsPropertyFacet
                {
                    PropertySet = IdsRestriction.Read(IdsRestriction.FirstChild(node, "propertySet", Namespace)),
                    BaseName = IdsRestriction.Read(IdsRestriction.FirstChild(node, "baseName", Namespace)),
                    Value = IdsRestriction.Read(IdsRestriction.FirstChild(node, "value", Namespace)),
                    DataType = Attribute(node, "dataType"),
                    Uri = Attribute(node, "uri"),
                    Instructions = Attribute(node, "instructions")
                };
                if (isRequirements) facet.Cardinality = ReadCardinality(node, file, specification, "property", true);
                set.Properties.Add(facet);
            }

            foreach (XmlNode node in IdsRestriction.Children(container, "material", Namespace))
            {
                var facet = new IdsMaterialFacet
                {
                    Value = IdsRestriction.Read(IdsRestriction.FirstChild(node, "value", Namespace)),
                    Uri = Attribute(node, "uri"),
                    Instructions = Attribute(node, "instructions")
                };
                if (isRequirements) facet.Cardinality = ReadCardinality(node, file, specification, "material", true);
                set.Materials.Add(facet);
            }

            // ANYTHING ELSE IN THE CONTAINER IS NAMED. An element the schema does not define is
            // either a newer IDS than this build reads or an authoring error, and both are worth
            // saying out loud rather than skipping.
            foreach (XmlNode child in container.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                string name = child.LocalName;
                if (name == "entity" || name == "partOf" || name == "classification" ||
                    name == "attribute" || name == "property" || name == "material") continue;
                file.Problems.Add("specification '" + specification.Name + "' holds a <" + name +
                                  "> element inside its " + (isRequirements ? "requirements" : "applicability") +
                                  ", which the IDS 1.0.0 schema does not define. It was not evaluated.");
            }

            return set;
        }

        private static IdsEntityFacet ReadEntity(XmlNode node, bool isRequirements)
        {
            var facet = new IdsEntityFacet
            {
                Name = IdsRestriction.Read(IdsRestriction.FirstChild(node, "name", Namespace)),
                PredefinedType = IdsRestriction.Read(IdsRestriction.FirstChild(node, "predefinedType", Namespace)),
                Instructions = Attribute(node, "instructions")
            };
            // THE SCHEMA GIVES THE REQUIREMENTS ENTITY FACET NO CARDINALITY ATTRIBUTE. Its state
            // is always "required", and the schema's own comment explains why: the list of IFC
            // classes is finite, so a negative constraint is expressed as an enumeration or a
            // pattern instead.
            facet.Cardinality = IdsCardinality.Required;
            if (facet.Name == null)
                facet.Unsupported = "this entity facet holds no <name>, which the schema requires.";
            return facet;
        }

        private static IdsCardinality ReadCardinality(XmlNode node, IdsFile file,
                                                      IdsSpecification specification, string facetKind,
                                                      bool optionalAllowed)
        {
            string raw = Attribute(node, "cardinality");
            if (string.IsNullOrWhiteSpace(raw)) return IdsCardinality.Required;   // the schema's default

            switch (raw)
            {
                case "required": return IdsCardinality.Required;
                case "prohibited": return IdsCardinality.Prohibited;
                case "optional":
                    if (optionalAllowed) return IdsCardinality.Optional;
                    file.Problems.Add("specification '" + specification.Name + "' gives a " + facetKind +
                                      " facet cardinality='optional', which the schema does not allow " +
                                      "there: saying a thing may or may not be part of something else " +
                                      "states nothing. It was read as 'required'.");
                    return IdsCardinality.Required;
                default:
                    file.Problems.Add("specification '" + specification.Name + "' gives a " + facetKind +
                                      " facet cardinality='" + raw + "', which is not one of required, " +
                                      "prohibited" + (optionalAllowed ? ", optional" : "") +
                                      ". It was read as 'required'.");
                    return IdsCardinality.Required;
            }
        }

        private static string Text(XmlNode parent, string localName)
        {
            XmlNode node = IdsRestriction.FirstChild(parent, localName, Namespace);
            return node == null ? null : node.InnerText;
        }

        private static string Attribute(XmlNode node, string name)
        {
            if (node == null || node.Attributes == null) return null;
            XmlAttribute attribute = node.Attributes[name];
            return attribute == null ? null : attribute.Value;
        }
    }
}
