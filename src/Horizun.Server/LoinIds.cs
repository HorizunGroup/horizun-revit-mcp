// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// LOIN -> IDS: the structured level of information need of a project context,
// translated into an Information Delivery Specification.
//
// WHAT THE TWO STANDARDS ARE, and why only part of one fits in the other.
//
//   ISO 7817-1:2024 (which superseded EN 17412-1:2020) describes a level of
//   information need by its PREREQUISITES - purpose, information delivery
//   milestone, actors - and by three kinds of information need for the objects
//   it applies to: GEOMETRICAL (detail, dimensionality, location, appearance,
//   parametric behaviour), ALPHANUMERICAL (identification and information
//   content) and DOCUMENTATION.
//
//   IDS 1.0 (buildingSMART, namespace http://standards.buildingsmart.org/IDS)
//   states requirements on the CONTENT of an IFC file: which entities or
//   classifications a specification applies to, and which attributes, properties,
//   classifications, materials and containment they must carry, with
//   enumeration / pattern / bounds restrictions on the values.
//
// So the alphanumerical part translates: applies_to -> <applicability> (entity,
// classification), attributes and properties -> <requirements>. The geometrical
// aspects and the documentation have no IDS facet, and the actors no IDS field.
// Those are LISTED, one line each, with why - never approximated into a facet
// that would then be checked as if somebody had asked for it.
//
// THE FILE IS PROVED TWICE BEFORE IT IS WRITTEN. Against the published ids.xsd
// 1.0 (embedded verbatim, with the two XML Schema definitions it imports supplied
// locally so validity does not depend on the network), and by the IDS reader the
// validators of this bridge actually use (IdsReader, the same code
// horizun_validate_ids and horizun_deliver_ifc read with): a file the XSD accepts
// but our own reader would call undecidable is not a file worth writing. After a
// write the bytes on disk are re-read, re-hashed and proved again.
//
// Revit-free, and host-resident through horizun_project_context.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Schema;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    /// <summary>IFC entity names per schema, read from the embedded list. Names only.</summary>
    internal static class IfcEntityCatalog
    {
        internal const string ResourceName = "Horizun.Schemas.ifc-entities.txt";
        public static readonly string[] Schemas = { "IFC2X3", "IFC4", "IFC4X3_ADD2" };

        private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> _tables =
            new Lazy<Dictionary<string, Dictionary<string, string>>>(Load);

        private static Dictionary<string, Dictionary<string, string>> Load()
        {
            var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            using (Stream s = typeof(IfcEntityCatalog).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null)
                    throw new InvalidOperationException("The IFC entity list is not embedded (resource '" + ResourceName +
                                                        "'). The build is incomplete.");
                using (var r = new StreamReader(s, new UTF8Encoding(false)))
                {
                    Dictionary<string, string> current = null;
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        if (line[0] == '[' && line[line.Length - 1] == ']')
                        {
                            current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            tables[line.Substring(1, line.Length - 2)] = current;
                            continue;
                        }
                        if (current != null) current[line] = line;
                    }
                }
            }
            return tables;
        }

        public static int Count(string schema)
            => _tables.Value.TryGetValue(schema, out var t) ? t.Count : 0;

        /// <summary>The entity's name as the schema spells it, or null when the schema has no such entity.</summary>
        public static string Find(string schema, string name)
        {
            if (name == null || !_tables.Value.TryGetValue(schema, out var t)) return null;
            return t.TryGetValue(name, out string canonical) ? canonical : null;
        }
    }

    /// <summary>The published ids.xsd 1.0, compiled once, validating offline.</summary>
    internal static class IdsSchema
    {
        internal const string XsdResource = "Horizun.Schemas.ids-1.0.xsd";
        internal const string XsSubsetResource = "Horizun.Schemas.xmlschema-subset.xsd";
        public const string Namespace = "http://standards.buildingsmart.org/IDS";
        public const string XsdLocation = "http://standards.buildingsmart.org/IDS/1.0/ids.xsd";

        private static readonly object Gate = new object();
        private static readonly Lazy<XmlSchemaSet> _set = new Lazy<XmlSchemaSet>(Compile);
        private static readonly Lazy<string> _sha = new Lazy<string>(() => ProjectContext.Sha256Hex(Resource(XsdResource)));

        public static string XsdSha256 => _sha.Value;

        internal static byte[] Resource(string name)
        {
            using (Stream s = typeof(IdsSchema).Assembly.GetManifestResourceStream(name))
            {
                if (s == null)
                    throw new InvalidOperationException("'" + name + "' is not embedded in this build; an IDS cannot be " +
                                                        "validated against a schema this process does not carry.");
                using (var m = new MemoryStream())
                {
                    s.CopyTo(m);
                    return m.ToArray();
                }
            }
        }

        /// <summary>
        /// The three imports ids.xsd declares, answered from memory. Anything else is refused:
        /// the compiler never reaches the network.
        /// </summary>
        private sealed class OfflineResolver : XmlResolver
        {
            public override object GetEntity(Uri absoluteUri, string role, Type ofObjectToReturn)
            {
                switch (absoluteUri.AbsoluteUri)
                {
                    case "http://www.w3.org/2001/XMLSchema.xsd":
                        return new MemoryStream(Resource(XsSubsetResource));
                    case "http://www.w3.org/2001/xml.xsd":
                        return Empty("http://www.w3.org/XML/1998/namespace");
                    case "http://www.w3.org/2001/XMLSchema-instance":
                        return Empty("http://www.w3.org/2001/XMLSchema-instance");
                    default:
                        throw new XmlException("ids.xsd imports '" + absoluteUri + "', which this offline validator does not carry.");
                }
            }

            private static Stream Empty(string ns) => new MemoryStream(new UTF8Encoding(false).GetBytes(
                "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema' targetNamespace='" + ns + "'/>"));
        }

        private static XmlSchemaSet Compile()
        {
            var problems = new List<string>();
            var set = new XmlSchemaSet { XmlResolver = new OfflineResolver() };
            set.ValidationEventHandler += (s, e) => problems.Add(e.Message);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (XmlReader r = XmlReader.Create(new MemoryStream(Resource(XsdResource)), settings,
                                                  "http://standards.buildingsmart.org/IDS/1.0/ids.xsd"))
                set.Add(null, r);
            set.Compile();
            if (problems.Count > 0 || !set.IsCompiled)
                throw new InvalidOperationException("The embedded ids.xsd did not compile: " + string.Join("; ", problems));
            return set;
        }

        /// <summary>Every schema violation of the document, with its line. Empty means valid.</summary>
        public static List<string> Validate(byte[] xml)
        {
            var errors = new List<string>();
            XmlSchemaSet set = _set.Value;
            lock (Gate)
            {
                var settings = new XmlReaderSettings
                {
                    ValidationType = ValidationType.Schema,
                    Schemas = set,
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
                settings.ValidationEventHandler += (s, e) =>
                    errors.Add((e.Severity == XmlSeverityType.Warning ? "warning" : "error") + " at line " +
                               (e.Exception?.LineNumber ?? 0).ToString(CultureInfo.InvariantCulture) + ": " + e.Message);
                try
                {
                    using (XmlReader r = XmlReader.Create(new MemoryStream(xml), settings))
                        while (r.Read()) { }
                }
                catch (XmlException ex)
                {
                    errors.Add("not well-formed XML: " + ex.Message);
                }
            }
            return errors;
        }
    }

    internal static class LoinIds
    {
        private const string Xs = "http://www.w3.org/2001/XMLSchema";
        private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

        // IDS writes values in the IFC default unit (the User Manual's units table), which is
        // SI. A bound given in one of these units is already in that unit; any other is not
        // converted here, because a converted bound is a number nobody wrote down.
        private static readonly HashSet<string> DefaultUnits = new HashSet<string>(StringComparer.Ordinal)
        {
            "m", "m2", "m²", "m3", "m³", "kg", "s", "K", "Pa", "N", "W", "J", "A", "V", "Hz", "lx", "cd", "mol",
            "rad", "kg/m3", "kg/m³", "W/(m·K)", "W/(m2·K)", "W/(m²·K)", "m/s", "m3/s", "m³/s"
        };

        private static readonly HashSet<string> StringTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "IFCLABEL", "IFCTEXT", "IFCIDENTIFIER", "IFCDESCRIPTIVEMEASURE", "IFCURIREFERENCE", "IFCDATE",
            "IFCDATETIME", "IFCTIME", "IFCDURATION", "IFCLOGICAL", "IFCBINARY"
        };

        private static readonly HashSet<string> IntegerTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "IFCINTEGER", "IFCPOSITIVEINTEGER", "IFCDAYINMONTHNUMBER", "IFCMONTHINYEARNUMBER",
            "IFCDIMENSIONCOUNT", "IFCINTEGERCOUNTRATEMEASURE"
        };

        // ============================================================================
        // Coherence: what validate reports about a loin block
        // ============================================================================

        internal static List<JObject> Coherence(JObject doc)
        {
            var findings = new List<JObject>();
            if (!(doc["loin"]?["requirements"] is JArray requirements)) return findings;

            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var propertyTypes = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
            List<string> deliveryVersions = DeliveryVersions(doc);

            for (int i = 0; i < requirements.Count; i++)
            {
                if (!(requirements[i] is JObject req)) continue;
                string at = "/loin/requirements/" + i;

                string id = Str(req["id"]);
                if (id != null)
                {
                    if (ids.TryGetValue(id, out int first))
                        findings.Add(Finding(at + "/id", "loin_duplicate_requirement_id", "error",
                            "Requirement id '" + id + "' is also used by /loin/requirements/" + first +
                            ". Ids become IDS specification identifiers and must be unique."));
                    else ids[id] = i;
                }

                JObject applies = req["applies_to"] as JObject;
                string entity = Str(applies?["ifc_entity"]);
                bool hasClassification = applies?["classification"] is JObject;
                bool hasCategory = Str(applies?["revit_category"]) != null;
                if (entity == null && !hasClassification && !hasCategory)
                    findings.Add(Finding(at + "/applies_to", "loin_applies_to_empty", "warning",
                        "The requirement does not say which objects it applies to (no ifc_entity, classification or " +
                        "revit_category), so nothing can check it."));

                if (entity != null) CheckEntity(entity, RequirementVersions(req, deliveryVersions), at + "/applies_to/ifc_entity", findings);

                if (!(req["alphanumeric"] is JObject alpha)) continue;
                var seenHere = new HashSet<string>(StringComparer.Ordinal);
                if (alpha["properties"] is JArray props)
                    for (int k = 0; k < props.Count; k++)
                    {
                        if (!(props[k] is JObject p)) continue;
                        string pAt = at + "/alphanumeric/properties/" + k;
                        string pset = Str(p["property_set"]), name = Str(p["name"]);
                        string type = Str(p["data_type"]);
                        if (pset != null && name != null)
                        {
                            string key = pset + "\u001f" + name;
                            bool conflict = false;
                            if (propertyTypes.TryGetValue(key, out var prior))
                            {
                                if (type != null && prior.Value != null &&
                                    !string.Equals(type, prior.Value, StringComparison.OrdinalIgnoreCase))
                                {
                                    conflict = true;
                                    findings.Add(Finding(pAt + "/data_type", "loin_property_type_conflict", "error",
                                        pset + "." + name + " is declared " + type + " here and " + prior.Value + " at " +
                                        prior.Key + ". One property has one IFC data type; an IDS carrying both would fail every file."));
                                }
                                else if (prior.Value == null && type != null)
                                    propertyTypes[key] = new KeyValuePair<string, string>(pAt, type);
                            }
                            else propertyTypes[key] = new KeyValuePair<string, string>(pAt, type);

                            if (!seenHere.Add(key) && !conflict)
                                findings.Add(Finding(pAt, "loin_property_repeated", "warning",
                                    pset + "." + name + " appears twice in the same requirement; the second adds nothing."));
                        }
                        CheckValueRules(p, pAt, type, findings);
                    }
                if (alpha["attributes"] is JArray attrs)
                    for (int k = 0; k < attrs.Count; k++)
                        if (attrs[k] is JObject a) CheckPattern(Str(a["pattern"]), at + "/alphanumeric/attributes/" + k + "/pattern", findings);
            }
            return findings;
        }

        private static void CheckEntity(string entity, List<string> versions, string pointer, List<JObject> findings)
        {
            if (versions.Count > 0)
            {
                var missing = versions.Where(v => IfcEntityCatalog.Find(v, entity) == null).ToList();
                if (missing.Count > 0)
                {
                    var elsewhere = IfcEntityCatalog.Schemas.Where(v => IfcEntityCatalog.Find(v, entity) != null).ToList();
                    findings.Add(Finding(pointer, "loin_unknown_ifc_entity", "error",
                        "'" + entity + "' is not an entity of " + string.Join(" or ", missing) +
                        (elsewhere.Count > 0 ? " (it exists in " + string.Join(", ", elsewhere) + ")" : "") +
                        ", which this requirement targets. An IDS applicability naming it would match nothing."));
                }
                return;
            }
            // No declared version: judge against the two current schemas.
            bool in4 = IfcEntityCatalog.Find("IFC4", entity) != null;
            bool in43 = IfcEntityCatalog.Find("IFC4X3_ADD2", entity) != null;
            if (!in4 && !in43)
                findings.Add(Finding(pointer, "loin_unknown_ifc_entity", "error",
                    "'" + entity + "' is not an entity of IFC4 or IFC4X3_ADD2" +
                    (IfcEntityCatalog.Find("IFC2X3", entity) != null ? " (it exists only in IFC2X3)" : "") + "."));
            else if (!in4 || !in43)
                findings.Add(Finding(pointer, "loin_entity_version_specific", "warning",
                    "'" + entity + "' exists in " + (in4 ? "IFC4" : "IFC4X3_ADD2") + " but not in " + (in4 ? "IFC4X3_ADD2" : "IFC4") +
                    ". Declare ifc_versions (or delivery.ifc.version) so the IDS targets the right schema."));
        }

        private static void CheckValueRules(JObject p, string at, string type, List<JObject> findings)
        {
            double? minI = Num(p["min_inclusive"]), maxI = Num(p["max_inclusive"]);
            double? minE = Num(p["min_exclusive"]), maxE = Num(p["max_exclusive"]);
            bool anyBound = minI.HasValue || maxI.HasValue || minE.HasValue || maxE.HasValue;
            if (minI.HasValue && minE.HasValue)
                findings.Add(Finding(at, "loin_bounds_conflict", "error", "Both min_inclusive and min_exclusive are given; say one lower bound."));
            if (maxI.HasValue && maxE.HasValue)
                findings.Add(Finding(at, "loin_bounds_conflict", "error", "Both max_inclusive and max_exclusive are given; say one upper bound."));
            double? lo = minI ?? minE, hi = maxI ?? maxE;
            if (lo.HasValue && hi.HasValue && (lo.Value > hi.Value || (lo.Value == hi.Value && (minE.HasValue || maxE.HasValue))))
                findings.Add(Finding(at, "loin_bounds_inverted", "error", "The lower bound is not below the upper bound, so no value can satisfy both."));
            string upper = type?.ToUpperInvariant();
            if (anyBound && upper != null && (StringTypes.Contains(upper) || upper == "IFCBOOLEAN"))
                findings.Add(Finding(at, "loin_bounds_on_non_numeric", "error",
                    "Numeric bounds are given for a " + type + " property; they can only constrain a number."));
            string unit = Str(p["unit"]);
            if (anyBound && unit != null && !DefaultUnits.Contains(unit))
                findings.Add(Finding(at + "/unit", "loin_unit_not_ids_default", "warning",
                    "The bounds are in '" + unit + "'. IDS values are in the IFC default (SI) unit and this bridge does not " +
                    "convert, so ids_from_loin will leave these bounds out and list them."));
            CheckPattern(Str(p["pattern"]), at + "/pattern", findings);
        }

        private static void CheckPattern(string pattern, string pointer, List<JObject> findings)
        {
            if (pattern == null) return;
            if (pattern.StartsWith("^", StringComparison.Ordinal) || pattern.EndsWith("$", StringComparison.Ordinal))
                findings.Add(Finding(pointer, "loin_pattern_anchor", "warning",
                    "An XML Schema pattern always matches the whole value and treats ^ and $ as literal characters. " +
                    "Drop the anchors, or the pattern will demand a literal '^' or '$' in the value."));
        }

        // ============================================================================
        // The operation
        // ============================================================================

        internal static JObject Operation(JObject args, CancellationToken ct)
        {
            string path = ProjectContext.OptionalPath(args);
            if (path == null)
                throw new ToolRefusal("ids_from_loin needs 'path': the project-context.json whose loin block is translated. Nothing was read.");
            if (!File.Exists(path))
                throw new ToolRefusal("No file at '" + path + "'. Nothing was read.");
            bool dryRun = (bool?)args["dry_run"] ?? true;
            bool overwrite = (bool?)args["overwrite"] ?? false;
            string output = OptionalAbsolute(args, "output_path");
            if (output != null && !output.EndsWith(".ids", StringComparison.OrdinalIgnoreCase))
                throw new ToolRefusal("output_path must name a .ids file; '" + output + "' does not. Nothing was written.");
            if (!dryRun && output == null)
                throw new ToolRefusal("dry_run=false needs 'output_path': where the .ids is written. Nothing was written.");

            byte[] contextBytes = File.ReadAllBytes(path);
            JToken parsed;
            string parseError;
            if (!ProjectContext.TryParse(contextBytes, out parsed, out parseError) || !(parsed is JObject doc))
                throw new ToolRefusal("'" + path + "' is not a JSON object (" + (parseError ?? "top level is not an object") +
                                      "). Run operation=validate to see why. Nothing was translated.");
            ct.ThrowIfCancellationRequested();

            JObject evaluation = ProjectContext.Evaluate(doc);
            if (!(bool)evaluation["valid"])
                throw new ToolRefusal("The project context breaks the schema (" + ((JArray)evaluation["errors"]).Count +
                                      " error(s); run operation=validate for the list). A LOIN read from an invalid file " +
                                      "would be translated from a guess. Nothing was translated.");
            var loinErrors = ((JArray)evaluation["coherence"])
                .Where(f => ((string)f["rule"]).StartsWith("loin_", StringComparison.Ordinal) && (string)f["severity"] == "error")
                .ToList();
            if (loinErrors.Count > 0)
                throw new ToolRefusal("The loin block contradicts itself (" + string.Join("; ",
                                      loinErrors.Take(5).Select(f => (string)f["rule"] + " at " + (string)f["pointer"])) +
                                      (loinErrors.Count > 5 ? "; ..." : "") + "). An IDS built from it would carry the " +
                                      "contradiction into every file it checks. Fix it; nothing was translated.");
            if (!(doc["loin"]?["requirements"] is JArray reqs) || reqs.Count == 0)
                throw new ToolRefusal("'" + path + "' has no loin.requirements, so there is nothing to translate. Add the " +
                                      "structured LOIN first (see operation=schema, $defs.loin_requirement).");

            HashSet<string> only = null;
            if (args["requirement_ids"] is JArray wanted && wanted.Count > 0)
            {
                only = new HashSet<string>(wanted.Select(t => (string)t), StringComparer.Ordinal);
                var unknown = only.Where(w => !reqs.OfType<JObject>().Any(r => Str(r["id"]) == w)).ToList();
                if (unknown.Count > 0)
                    throw new ToolRefusal("requirement_ids names requirement(s) the loin block does not hold: " +
                                          string.Join(", ", unknown) + ". Nothing was translated.");
            }
            string milestone = Str(args["milestone"]);
            // The two info fields ids.xsd constrains. Checked here so a bad argument is named as
            // one, instead of surfacing as a schema error in a file this tool built.
            JToken infoToken = args["info"];
            if (infoToken != null && infoToken.Type != JTokenType.Null && !(infoToken is JObject))
                throw new ToolRefusal("info must be an object {title, author, version, date}. Nothing was translated.");
            JObject info = infoToken as JObject ?? new JObject();
            var unknownInfo = info.Properties().Select(p => p.Name)
                .Where(n => n != "title" && n != "author" && n != "version" && n != "date").ToList();
            if (unknownInfo.Count > 0)
                throw new ToolRefusal("info takes title, author, version and date; not " + string.Join(", ", unknownInfo) +
                                      ". Nothing was translated.");
            string author = Str(info["author"]);
            if (author != null && !Regex.IsMatch(author, @"^[^@]+@[^\.]+\..+$", RegexOptions.CultureInvariant, RegexBudget))
                throw new ToolRefusal("author must be an e-mail address (ids.xsd: [^@]+@[^.]+\\..+); '" + author +
                                      "' is not. Omit it to leave the IDS without an author. Nothing was translated.");
            string date = Str(info["date"]);
            if (date != null && !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ToolRefusal("date must be YYYY-MM-DD; '" + date + "' is not. Nothing was translated.");

            var translation = Translate(doc, only, milestone, info);
            ct.ThrowIfCancellationRequested();

            var result = new JObject
            {
                ["operation"] = "ids_from_loin",
                ["dry_run"] = dryRun,
                ["path"] = path,
                ["context_sha256"] = ProjectContext.Sha256Hex(contextBytes),
                ["output_path"] = output,
                ["standards"] = new JObject
                {
                    ["loin"] = "ISO 7817-1:2024 (formerly EN 17412-1:2020): prerequisites purpose / milestone / actors; " +
                               "geometrical, alphanumerical and documentation information need.",
                    ["ids"] = "IDS 1.0, namespace " + IdsSchema.Namespace + ", validated against the embedded ids.xsd 1.0.0",
                    ["ids_xsd_sha256"] = IdsSchema.XsdSha256
                },
                ["requirements_considered"] = translation.Considered,
                ["specifications"] = translation.Specifications,
                ["not_translated"] = translation.NotTranslated,
                ["not_translated_means"] =
                    "IDS 1.0 constrains the alphanumerical content of an IFC file. Geometry, documentation, actors and " +
                    "Revit categories have no IDS facet: they are listed here, never approximated into one."
            };

            if (translation.Specifications.Count == 0)
            {
                result["written"] = false;
                result["ids_xml"] = null;
                result["would_refuse"] = "No requirement produced a checkable IDS specification (see not_translated). " +
                                         "An IDS must hold at least one specification, so no file was built.";
                if (!dryRun) throw new ToolRefusal((string)result["would_refuse"] + " Nothing was written.");
                return result;
            }

            byte[] xml = translation.Xml;
            JObject proof = Prove(xml, translation.Specifications.Count);
            result["sha256"] = ProjectContext.Sha256Hex(xml);
            result["bytes"] = xml.LongLength;
            result["validation"] = proof;
            result["ids_xml"] = new UTF8Encoding(false).GetString(xml);
            bool proven = (bool)proof["xsd"]["valid"] && (bool)proof["reader"]["clean"];

            string refusal = !proven
                ? "the generated IDS did not pass its own proof (see validation). It is a defect of this translation, " +
                  "not of the project; nothing is written from it."
                : output == null ? "no output_path given, so there is nowhere to write; the rehearsal is the whole answer."
                : File.Exists(output) && !overwrite ? "'" + output + "' already exists and overwrite is false."
                : !Directory.Exists(Path.GetDirectoryName(output) ?? "") ? "the folder '" + Path.GetDirectoryName(output) +
                  "' does not exist. This tool does not create folders."
                : null;

            if (dryRun)
            {
                result["written"] = false;
                result["would_refuse"] = refusal;
                result["note"] = "Rehearsal: nothing was written. Send the same call with dry_run=false and output_path to " +
                                 "write; the file is then re-read, re-hashed and validated again before it is reported written.";
                return result;
            }
            if (refusal != null) throw new ToolRefusal(refusal + " Nothing was written.");

            string profileRefusal;
            if (!Settings.AllowsExternalSideEffect(out profileRefusal))
                throw new ToolRefusal("Writing the IDS is a file outside the model, and that is what needs the profile: " +
                                      profileRefusal + " The IDS text is in the rehearsal's 'ids_xml' for the person to save.");

            WriteAtomically(output, xml, overwrite);

            byte[] onDisk = File.ReadAllBytes(output);
            string wantSha = ProjectContext.Sha256Hex(xml), gotSha = ProjectContext.Sha256Hex(onDisk);
            JObject reproof = Prove(onDisk, translation.Specifications.Count);
            string readError;
            IdsFile reread = IdsReader.Read(output, out readError);
            bool same = wantSha == gotSha && (bool)reproof["xsd"]["valid"] && (bool)reproof["reader"]["clean"] &&
                        reread != null && readError == null && reread.Specifications.Count == translation.Specifications.Count;
            if (!same)
                throw new InvalidOperationException("'" + output + "' was written but reading it back does not prove it " +
                                                    "(sha256 written " + wantSha + ", read " + gotSha + "; " +
                                                    (readError ?? "revalidation failed") + "). Treat the file as unverified.");
            result["written"] = true;
            result["verification"] = new JObject
            {
                ["reread"] = true,
                ["bytes"] = onDisk.LongLength,
                ["sha256"] = gotSha,
                ["xsd_valid"] = true,
                ["reader_specifications"] = reread.Specifications.Count,
                ["read_from_disk_by"] = "IdsReader.Read, the reader horizun_validate_ids and horizun_deliver_ifc use"
            };
            return result;
        }

        /// <summary>XSD validation plus our own reader, over the same bytes.</summary>
        internal static JObject Prove(byte[] xml, int expectedSpecifications)
        {
            List<string> xsdErrors = IdsSchema.Validate(xml);
            var readerProblems = new List<string>();
            int parsedSpecs = 0;
            try
            {
                var document = new XmlDocument();
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
                using (XmlReader r = XmlReader.Create(new MemoryStream(xml), settings)) document.Load(r);
                string error;
                IdsFile file = IdsReader.Parse(document, out error);
                if (file == null) readerProblems.Add(error ?? "the reader returned nothing.");
                else
                {
                    readerProblems.AddRange(file.Problems);
                    parsedSpecs = file.Specifications.Count;
                    foreach (IdsSpecification s in file.Specifications)
                    {
                        if (s.Undecidable != null) readerProblems.Add("'" + s.Name + "': " + s.Undecidable);
                        if (s.InvalidBecause != null) readerProblems.Add("'" + s.Name + "': " + s.InvalidBecause);
                        IEnumerable<IdsFacet> facets = s.Applicability.All();
                        if (s.Requirements != null) facets = facets.Concat(s.Requirements.All());
                        foreach (IdsFacet f in facets)
                        {
                            if (f.Unsupported != null) readerProblems.Add("'" + s.Name + "' " + f.Kind + ": " + f.Unsupported);
                            foreach (IdsValue v in Values(f))
                            {
                                if (v.Defect != null) readerProblems.Add("'" + s.Name + "' " + f.Kind + ": " + v.Defect);
                                if (v.Unsupported.Count > 0)
                                    readerProblems.Add("'" + s.Name + "' " + f.Kind + ": unsupported " + string.Join(", ", v.Unsupported));
                            }
                        }
                    }
                    if (parsedSpecs != expectedSpecifications)
                        readerProblems.Add("the reader found " + parsedSpecs + " specification(s); " + expectedSpecifications + " were written.");
                }
            }
            catch (XmlException ex) { readerProblems.Add("not well-formed XML: " + ex.Message); }

            return new JObject
            {
                ["xsd"] = new JObject
                {
                    ["valid"] = xsdErrors.Count == 0,
                    ["schema"] = IdsSchema.XsdLocation + " (embedded, sha256 " + IdsSchema.XsdSha256 + ")",
                    ["errors"] = new JArray(xsdErrors)
                },
                ["reader"] = new JObject
                {
                    ["clean"] = readerProblems.Count == 0,
                    ["reader"] = "IdsReader (horizun_validate_ids / horizun_deliver_ifc)",
                    ["specifications"] = parsedSpecs,
                    ["problems"] = new JArray(readerProblems)
                }
            };
        }

        private static IEnumerable<IdsValue> Values(IdsFacet f)
        {
            switch (f)
            {
                case IdsEntityFacet e: return new[] { e.Name, e.PredefinedType }.Where(v => v != null);
                case IdsAttributeFacet a: return new[] { a.Name, a.Value }.Where(v => v != null);
                case IdsClassificationFacet c: return new[] { c.System, c.Value }.Where(v => v != null);
                case IdsPropertyFacet p: return new[] { p.PropertySet, p.BaseName, p.Value }.Where(v => v != null);
                case IdsMaterialFacet m: return new[] { m.Value }.Where(v => v != null);
                default: return Enumerable.Empty<IdsValue>();
            }
        }

        // ============================================================================
        // Translation
        // ============================================================================

        internal sealed class Translation
        {
            public int Considered;
            public JArray Specifications = new JArray();
            public JArray NotTranslated = new JArray();
            public byte[] Xml;
        }

        private sealed class Restriction
        {
            public string Simple;
            public string Base;
            public List<string> Enumeration = new List<string>();
            public string Pattern;
            public double? MinI, MaxI, MinE, MaxE;
            public bool Any => Simple != null || Enumeration.Count > 0 || Pattern != null ||
                               MinI.HasValue || MaxI.HasValue || MinE.HasValue || MaxE.HasValue;
        }

        internal static Translation Translate(JObject doc, HashSet<string> only, string milestone, JObject args)
        {
            var t = new Translation();
            var reqs = ((JArray)doc["loin"]["requirements"]).OfType<JObject>()
                .Where(r => only == null || only.Contains(Str(r["id"])))
                .Where(r => milestone == null || string.Equals(Str(r["milestone"]), milestone, StringComparison.Ordinal))
                .ToList();
            t.Considered = reqs.Count;
            List<string> deliveryVersions = DeliveryVersions(doc);

            var specs = new List<Action<XmlWriter>>();
            foreach (JObject req in reqs)
            {
                string id = Str(req["id"]);
                var skipped = new List<JObject>();
                ListUntranslatable(req, id, skipped);

                List<string> versions = RequirementVersions(req, deliveryVersions);
                JObject applies = req["applies_to"] as JObject;
                string entity = Str(applies?["ifc_entity"]);
                JObject cls = applies?["classification"] as JObject;
                string occurrence = Str(req["occurrence"]) ?? "optional";

                string whyNot = null;
                if (versions.Count == 0)
                    whyNot = "no IFC version: neither the requirement's ifc_versions nor delivery.ifc.version names IFC2X3, IFC4 " +
                             "or IFC4X3_ADD2, and an IDS specification must declare one.";
                else if (entity == null && cls == null)
                    whyNot = "applies_to names no IFC entity and no classification" +
                             (Str(applies?["revit_category"]) != null ? " (a Revit category is not an IFC concept)" : "") +
                             ", so an IDS applicability would match every element.";

                var properties = (req["alphanumeric"]?["properties"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                var attributes = (req["alphanumeric"]?["attributes"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                if (whyNot == null && occurrence == "prohibited" && (properties.Count > 0 || attributes.Count > 0))
                    skipped.Add(Skip(id, "alphanumeric", null, "omitted",
                        "occurrence is prohibited: IDS ignores the requirements of a prohibited applicability, so they are not written."));
                if (occurrence == "prohibited") { properties.Clear(); attributes.Clear(); }
                if (whyNot == null && occurrence == "optional" && properties.Count == 0 && attributes.Count == 0)
                    whyNot = "no alphanumerical requirement (no attributes or properties) and occurrence is optional, so the " +
                             "specification would check nothing.";

                if (whyNot != null)
                {
                    skipped.Insert(0, Skip(id, "requirement", null, "omitted", whyNot));
                    foreach (JObject s in skipped) t.NotTranslated.Add(s);
                    continue;
                }

                // Value facets, decided before writing so the summary and the XML agree.
                var propertyFacets = new List<KeyValuePair<JObject, Restriction>>();
                foreach (JObject p in properties)
                {
                    string type = Str(p["data_type"])?.ToUpperInvariant();
                    Restriction r = ValueOf(p, type, id, "alphanumeric.properties." + Str(p["property_set"]) + "." + Str(p["name"]), skipped);
                    propertyFacets.Add(new KeyValuePair<JObject, Restriction>(p, r));
                    string uri = Str(p["uri"]);
                    if (uri != null && !Uri.IsWellFormedUriString(uri, UriKind.Absolute))
                        skipped.Add(Skip(id, "alphanumeric.properties." + Str(p["name"]) + ".uri", uri, "omitted",
                            "not an absolute URI, which the IDS uri attribute requires."));
                }
                var attributeFacets = attributes.Select(a => new KeyValuePair<JObject, Restriction>(a,
                    ValueOf(a, null, id, "alphanumeric.attributes." + Str(a["name"]), skipped))).ToList();
                if (Str(cls?["uri"]) != null)
                    skipped.Add(Skip(id, "applies_to.classification.uri", Str(cls["uri"]), "omitted",
                        "an IDS applicability classification facet has no uri attribute (only a requirements one does)."));

                string name = id + (Str(req["purpose"]) != null ? " - " + Str(req["purpose"]) : "");
                string description = Describe(req);
                JObject summary = new JObject
                {
                    ["requirement_id"] = id,
                    ["name"] = name,
                    ["ifc_versions"] = new JArray(versions),
                    ["applicability"] = new JObject
                    {
                        ["entity"] = entity == null ? null : entity.ToUpperInvariant(),
                        ["predefined_type"] = Str(applies?["predefined_type"])?.ToUpperInvariant(),
                        ["classification"] = cls == null ? null : new JObject { ["system"] = Str(cls["system"]), ["code"] = Str(cls["code"]) },
                        ["occurrence"] = occurrence
                    },
                    ["attributes"] = attributeFacets.Count,
                    ["properties"] = propertyFacets.Count
                };
                t.Specifications.Add(summary);
                foreach (JObject s in skipped) t.NotTranslated.Add(s);

                specs.Add(w =>
                {
                    w.WriteStartElement("ids", "specification", IdsSchema.Namespace);
                    w.WriteAttributeString("name", name);
                    w.WriteAttributeString("ifcVersion", string.Join(" ", versions));
                    w.WriteAttributeString("identifier", id);
                    if (description != null) w.WriteAttributeString("description", description);

                    w.WriteStartElement("ids", "applicability", IdsSchema.Namespace);
                    w.WriteAttributeString("minOccurs", occurrence == "required" ? "1" : "0");
                    w.WriteAttributeString("maxOccurs", occurrence == "prohibited" ? "0" : "unbounded");
                    if (entity != null)
                    {
                        w.WriteStartElement("ids", "entity", IdsSchema.Namespace);
                        WriteValue(w, "name", new Restriction { Simple = entity.ToUpperInvariant() });
                        string pdt = Str(applies?["predefined_type"]);
                        if (pdt != null) WriteValue(w, "predefinedType", new Restriction { Simple = pdt.ToUpperInvariant() });
                        w.WriteEndElement();
                    }
                    if (cls != null)
                    {
                        w.WriteStartElement("ids", "classification", IdsSchema.Namespace);
                        if (Str(cls["code"]) != null) WriteValue(w, "value", new Restriction { Simple = Str(cls["code"]) });
                        WriteValue(w, "system", new Restriction { Simple = Str(cls["system"]) });
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();   // applicability

                    if (attributeFacets.Count > 0 || propertyFacets.Count > 0)
                    {
                        w.WriteStartElement("ids", "requirements", IdsSchema.Namespace);
                        foreach (var a in attributeFacets)
                        {
                            w.WriteStartElement("ids", "attribute", IdsSchema.Namespace);
                            w.WriteAttributeString("cardinality", Str(a.Key["cardinality"]) ?? "required");
                            if (Str(a.Key["instructions"]) != null) w.WriteAttributeString("instructions", Str(a.Key["instructions"]));
                            WriteValue(w, "name", new Restriction { Simple = Str(a.Key["name"]) });
                            if (a.Value.Any) WriteValue(w, "value", a.Value);
                            w.WriteEndElement();
                        }
                        foreach (var p in propertyFacets)
                        {
                            w.WriteStartElement("ids", "property", IdsSchema.Namespace);
                            string type = Str(p.Key["data_type"]);
                            if (type != null) w.WriteAttributeString("dataType", type.ToUpperInvariant());
                            w.WriteAttributeString("cardinality", Str(p.Key["cardinality"]) ?? "required");
                            string uri = Str(p.Key["uri"]);
                            if (uri != null && Uri.IsWellFormedUriString(uri, UriKind.Absolute)) w.WriteAttributeString("uri", uri);
                            if (Str(p.Key["instructions"]) != null) w.WriteAttributeString("instructions", Str(p.Key["instructions"]));
                            WriteValue(w, "propertySet", new Restriction { Simple = Str(p.Key["property_set"]) });
                            WriteValue(w, "baseName", new Restriction { Simple = Str(p.Key["name"]) });
                            if (p.Value.Any) WriteValue(w, "value", p.Value);
                            w.WriteEndElement();
                        }
                        w.WriteEndElement();   // requirements
                    }
                    w.WriteEndElement();   // specification
                });
            }

            if (specs.Count == 0) return t;
            t.Xml = Write(doc, reqs, args, specs);
            return t;
        }

        private static byte[] Write(JObject doc, List<JObject> reqs, JObject args, List<Action<XmlWriter>> specs)
        {
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace
            };
            using (var m = new MemoryStream())
            {
                using (XmlWriter w = XmlWriter.Create(m, settings))
                {
                    w.WriteStartDocument();
                    w.WriteStartElement("ids", "ids", IdsSchema.Namespace);
                    w.WriteAttributeString("xmlns", "xs", null, Xs);
                    w.WriteAttributeString("xmlns", "xsi", null, Xsi);
                    w.WriteAttributeString("xsi", "schemaLocation", Xsi, IdsSchema.Namespace + " " + IdsSchema.XsdLocation);

                    string project = Str(doc["project"]?["code"]);
                    string title = Str(args["title"]) ?? (project + " - level of information need" +
                                   (Str(doc["project"]?["name"]) != null ? " (" + Str(doc["project"]["name"]) + ")" : ""));
                    w.WriteStartElement("ids", "info", IdsSchema.Namespace);
                    w.WriteElementString("ids", "title", IdsSchema.Namespace, title);
                    if (Str(args["version"]) != null) w.WriteElementString("ids", "version", IdsSchema.Namespace, Str(args["version"]));
                    w.WriteElementString("ids", "description", IdsSchema.Namespace,
                        "Alphanumerical part of the level of information need (ISO 7817-1) of project " + project +
                        (Str(doc["loin"]?["source"]) != null ? ", from " + Str(doc["loin"]["source"]) : "") +
                        ". Generated by Horizun from project-context.json; geometry and documentation needs are not expressible in IDS.");
                    if (Str(args["author"]) != null) w.WriteElementString("ids", "author", IdsSchema.Namespace, Str(args["author"]));
                    w.WriteElementString("ids", "date", IdsSchema.Namespace,
                        Str(args["date"]) ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    string purpose = Common(reqs, "purpose"), ms = Common(reqs, "milestone");
                    if (purpose != null) w.WriteElementString("ids", "purpose", IdsSchema.Namespace, purpose);
                    if (ms != null) w.WriteElementString("ids", "milestone", IdsSchema.Namespace, ms);
                    w.WriteEndElement();

                    w.WriteStartElement("ids", "specifications", IdsSchema.Namespace);
                    foreach (Action<XmlWriter> spec in specs) spec(w);
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndDocument();
                }
                byte[] bytes = m.ToArray();
                // A trailing newline, like every text file this bridge writes.
                return bytes.Concat(new[] { (byte)'\n' }).ToArray();
            }
        }

        private static void WriteValue(XmlWriter w, string element, Restriction r)
        {
            w.WriteStartElement("ids", element, IdsSchema.Namespace);
            if (r.Simple != null)
            {
                w.WriteElementString("ids", "simpleValue", IdsSchema.Namespace, r.Simple);
            }
            else
            {
                w.WriteStartElement("xs", "restriction", Xs);
                w.WriteAttributeString("base", r.Base ?? "xs:string");
                foreach (string e in r.Enumeration) Facet(w, "enumeration", e);
                if (r.Pattern != null) Facet(w, "pattern", r.Pattern);
                if (r.MinI.HasValue) Facet(w, "minInclusive", Number(r.MinI.Value));
                if (r.MinE.HasValue) Facet(w, "minExclusive", Number(r.MinE.Value));
                if (r.MaxI.HasValue) Facet(w, "maxInclusive", Number(r.MaxI.Value));
                if (r.MaxE.HasValue) Facet(w, "maxExclusive", Number(r.MaxE.Value));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        private static void Facet(XmlWriter w, string name, string value)
        {
            w.WriteStartElement("xs", name, Xs);
            w.WriteAttributeString("value", value);
            w.WriteEndElement();
        }

        private static Restriction ValueOf(JObject rule, string ifcType, string id, string aspect, List<JObject> skipped)
        {
            var r = new Restriction { Base = BaseFor(ifcType) };
            if (rule["allowed_values"] is JArray allowed)
                foreach (JToken v in allowed) if (Str(v) != null) r.Enumeration.Add(Str(v));
            r.Pattern = Str(rule["pattern"]);
            double? minI = Num(rule["min_inclusive"]), maxI = Num(rule["max_inclusive"]);
            double? minE = Num(rule["min_exclusive"]), maxE = Num(rule["max_exclusive"]);
            bool anyBound = minI.HasValue || maxI.HasValue || minE.HasValue || maxE.HasValue;
            string unit = Str(rule["unit"]);
            if (anyBound)
            {
                if (unit != null && !DefaultUnits.Contains(unit))
                    skipped.Add(Skip(id, aspect + ".bounds", unit, "omitted",
                        "the bounds are in '" + unit + "' and IDS values are in the IFC default (SI) unit; converting would " +
                        "write a number nobody specified."));
                else
                {
                    r.MinI = minI; r.MaxI = maxI; r.MinE = minE; r.MaxE = maxE;
                    if (r.Base == "xs:string") r.Base = "xs:double";
                }
            }
            else if (unit != null)
                skipped.Add(Skip(id, aspect + ".unit", unit, "omitted",
                    "IDS has no unit field: a property's unit is the IFC default for its data type."));

            // One allowed value and nothing else reads better as a simpleValue, and means the same.
            if (r.Enumeration.Count == 1 && r.Pattern == null && !r.MinI.HasValue && !r.MaxI.HasValue &&
                !r.MinE.HasValue && !r.MaxE.HasValue)
            {
                r.Simple = r.Enumeration[0];
                r.Enumeration.Clear();
            }
            return r;
        }

        private static string BaseFor(string ifcType)
        {
            if (ifcType == null) return "xs:string";
            if (ifcType == "IFCBOOLEAN") return "xs:boolean";
            if (IntegerTypes.Contains(ifcType)) return "xs:integer";
            if (StringTypes.Contains(ifcType)) return "xs:string";
            if (ifcType.EndsWith("MEASURE", StringComparison.Ordinal) || ifcType == "IFCREAL" ||
                ifcType == "IFCNUMERICMEASURE") return "xs:double";
            return "xs:string";
        }

        private static void ListUntranslatable(JObject req, string id, List<JObject> skipped)
        {
            if (req["geometry"] is JObject g)
                foreach (JProperty p in g.Properties())
                    skipped.Add(Skip(id, "geometry." + p.Name, p.Value.ToString(), "omitted",
                        "geometrical information need (ISO 7817-1); IDS 1.0 has no facet for " + GeometryWords(p.Name) + "."));
            if (req["documentation"] is JArray docs)
                for (int i = 0; i < docs.Count; i++)
                    skipped.Add(Skip(id, "documentation[" + i + "]", Str(docs[i]?["name"]) ?? docs[i].ToString(Newtonsoft.Json.Formatting.None),
                        "omitted", "documentation need (ISO 7817-1); IDS constrains the IFC file's content, not accompanying documents."));
            if (req["actors"] is JObject actors && actors.HasValues)
                skipped.Add(Skip(id, "actors", actors.ToString(Newtonsoft.Json.Formatting.None), "description_text",
                    "IDS has no actor field; the provider and receiver are carried only in the specification's description text."));
            if (Str(req["applies_to"]?["revit_category"]) != null)
                skipped.Add(Skip(id, "applies_to.revit_category", Str(req["applies_to"]["revit_category"]), "omitted",
                    "a Revit category is an authoring concept; IDS applicability speaks IFC entities and classifications."));
            if (Str(req["alphanumeric"]?["identification"]) != null)
                skipped.Add(Skip(id, "alphanumeric.identification", Str(req["alphanumeric"]["identification"]), "omitted",
                    "free text; only identification stated as attributes, properties or a classification becomes a facet."));
            if (Str(req["notes"]) != null)
                skipped.Add(Skip(id, "notes", Str(req["notes"]), "omitted", "free text with no IDS counterpart."));
        }

        private static string GeometryWords(string aspect)
        {
            switch (aspect)
            {
                case "detail": return "the detail of a representation";
                case "dimensionality": return "the dimensionality (0D-3D) of a representation";
                case "location": case "location_reference": return "absolute or relative location";
                case "appearance": return "appearance";
                case "parametric_behaviour": return "parametric behaviour";
                default: return "this aspect";
            }
        }

        private static string Describe(JObject req)
        {
            var parts = new List<string>();
            if (Str(req["purpose"]) != null) parts.Add("Purpose: " + Str(req["purpose"]));
            if (Str(req["milestone"]) != null) parts.Add("Milestone: " + Str(req["milestone"]));
            string provider = Str(req["actors"]?["provider"]), receiver = Str(req["actors"]?["receiver"]);
            if (provider != null || receiver != null)
                parts.Add("Actors: " + (provider ?? "?") + " -> " + (receiver ?? "?"));
            return parts.Count == 0 ? null : string.Join(". ", parts) + ".";
        }

        private static string Common(List<JObject> reqs, string key)
        {
            var values = reqs.Select(r => Str(r[key])).Distinct(StringComparer.Ordinal).ToList();
            return values.Count == 1 ? values[0] : null;
        }

        // ============================================================================
        // Versions
        // ============================================================================

        private static List<string> DeliveryVersions(JObject doc)
        {
            string v = Str(doc["delivery"]?["ifc"]?["version"]);
            string mapped = MapVersion(v);
            return mapped == null ? new List<string>() : new List<string> { mapped };
        }

        private static List<string> RequirementVersions(JObject req, List<string> deliveryVersions)
        {
            if (req["ifc_versions"] is JArray a && a.Count > 0)
                return a.Select(x => Str(x)).Where(x => x != null).Distinct(StringComparer.Ordinal).ToList();
            return deliveryVersions;
        }

        /// <summary>A delivery.ifc.version as people write it, mapped onto the three IDS 1.0 names, or null.</summary>
        internal static string MapVersion(string v)
        {
            if (v == null) return null;
            string s = Regex.Replace(v.ToUpperInvariant(), @"[\s\-\.]", "");
            if (s.StartsWith("IFC2X3", StringComparison.Ordinal)) return "IFC2X3";
            if (s.StartsWith("IFC4X3", StringComparison.Ordinal)) return "IFC4X3_ADD2";
            if (s == "IFC4" || s.StartsWith("IFC4ADD", StringComparison.Ordinal) || s.StartsWith("IFC4_ADD", StringComparison.Ordinal)) return "IFC4";
            return null;
        }

        // ============================================================================
        // helpers
        // ============================================================================

        private static JObject Skip(string id, string aspect, string value, string handling, string why) => new JObject
        {
            ["requirement_id"] = id,
            ["aspect"] = aspect,
            ["value"] = value,
            ["handling"] = handling,
            ["why"] = why
        };

        private static JObject Finding(string pointer, string rule, string severity, string message) => new JObject
        {
            ["pointer"] = pointer,
            ["rule"] = rule,
            ["severity"] = severity,
            ["message"] = message
        };

        private static string Str(JToken t)
            => t is JValue v && v.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)v) ? (string)v : null;

        private static double? Num(JToken t)
            => t is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (double?)(double)v : null;

        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string OptionalAbsolute(JObject args, string key)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))
                throw new ToolRefusal(key + " must be a non-empty string. Nothing was written.");
            string p = (string)t;
            if (!Path.IsPathRooted(p))
                throw new ToolRefusal(key + " must be absolute; '" + p + "' is relative. Nothing was written.");
            return Path.GetFullPath(p);
        }

        private static void WriteAtomically(string path, byte[] bytes, bool overwrite)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite);
            }
            catch (IOException ex) when (!overwrite && File.Exists(path))
            {
                throw new ToolRefusal("'" + path + "' appeared while this call was writing and overwrite is false; it was " +
                                      "left untouched (" + ex.Message + "). Nothing was written.");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
