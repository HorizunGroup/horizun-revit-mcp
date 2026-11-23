// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_export format=ifc_setup: a NAMED IFC export setup saved INSIDE the
// document, decided without a Revit.
//
// Revit itself has no public API for the IFC exporter's saved setups. The setups
// the Export IFC dialog lists live in the document as Extensible Storage written by
// Autodesk's open-source IFC exporter UI (Autodesk.IFC.Export.UI.dll, the same
// three schema GUIDs in the 2023, 2024, 2025 and 2026 builds - read from each
// installed DLL on 2026-10-18 - and, merged into Revit.IFC.Export.dll, in 2027): one DataStorage element per setup, an entity of
// the schema "IFCExportConfigurationMap" {C2A3E6FE-CE51-4F35-8FF1-20C34567B687}
// whose single string field "MapField" holds the setup as JSON, keyed by the
// exporter's own IFCExportConfiguration property names (Name, IFCVersion,
// ExportBaseQuantities, ClassificationSettings, ...). Absent keys take the
// exporter's defaults when it deserialises the setup.
//
// This file decides what may go into that JSON (a closed key list with a kind per
// key - an unknown key is refused, never written and silently ignored by the
// exporter), validates the per-type class mapping (IfcExportAs / IfcExportType),
// builds the JSON and compares a re-read with what was requested. The Revit half
// (ExportIfcSetup.cs) writes, re-reads, and asks the installed exporter UI to load
// the setup back when that assembly is present.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class IfcTypeMapping
    {
        public long TypeId { get; set; }
        /// <summary>IfcExportAs (Revit "Export Type to IFC As"): an IFC entity name such as IfcFooting.</summary>
        public string ExportAs { get; set; }
        /// <summary>IfcExportType (Revit "Type IFC Predefined Type"): PAD_FOOTING, or null to leave it.</summary>
        public string PredefinedType { get; set; }
    }

    public sealed class IfcSetupRequest
    {
        public string Name { get; set; }
        public JObject Options { get; } = new JObject();
        public JObject Classification { get; set; }
        public List<IfcTypeMapping> TypeMappings { get; } = new List<IfcTypeMapping>();
    }

    public static class IfcSetupRules
    {
        /// <summary>The exporter UI's JSON schema: one DataStorage per saved setup.</summary>
        public const string SchemaGuid = "C2A3E6FE-CE51-4F35-8FF1-20C34567B687";
        public const string SchemaName = "IFCExportConfigurationMap";
        public const string FieldName = "MapField";
        public const int MaxTypeMappings = 2000;

        /// <summary>
        /// The two OTHER schemas the exporter UI keeps saved setups under (read from the 2023-2027 DLLs, with
        /// the one above: the original per-field schema and the map schema). An older exporter wrote setups there and the
        /// current one migrates them when it loads the model; this tool reads only the JSON schema above. A model that
        /// holds entities under these is a model whose setups this tool cannot all see (INT-6 M-2).
        /// </summary>
        public static readonly string[] OtherSetupSchemaGuids =
        {
            "A1E672E5-AC88-4933-A019-F9068402CFA7",
            "DCB88B13-594F-44F6-8F5D-AE9477305AC3"
        };

        internal enum Kind { Bool, Int, Double, String }

        /// <summary>
        /// Exporter keys a caller may set, with their kind. Only keys present with the SAME
        /// meaning in the 2023-2026 exporter builds; enums whose numbering changed between
        /// builds (ExportLinkedFiles, SitePlacement, FileFormat) are deliberately absent.
        /// </summary>
        internal static readonly Dictionary<string, Kind> OptionKinds = new Dictionary<string, Kind>(StringComparer.Ordinal)
        {
            { "ExportBaseQuantities", Kind.Bool }, { "SplitWallsAndColumns", Kind.Bool }, { "SpaceBoundaries", Kind.Int },
            { "ExportInternalRevitPropertySets", Kind.Bool }, { "ExportIFCCommonPropertySets", Kind.Bool },
            { "ExportSchedulesAsPsets", Kind.Bool }, { "ExportUserDefinedPsets", Kind.Bool },
            { "ExportUserDefinedPsetsFileName", Kind.String }, { "ExportUserDefinedParameterMapping", Kind.Bool },
            { "ExportUserDefinedParameterMappingFileName", Kind.String }, { "Export2DElements", Kind.Bool },
            { "VisibleElementsInCurrentView", Kind.Bool }, { "Use2DBoundariesForRoomVolume", Kind.Bool },
            { "UseFamilyAndTypeNameForReference", Kind.Bool }, { "ExportPartsAsBuildingElements", Kind.Bool },
            { "ExportBoundingBox", Kind.Bool }, { "ExportSolidModelRep", Kind.Bool }, { "IncludeSiteElevation", Kind.Bool },
            { "StoreIFCGUID", Kind.Bool }, { "ExportRoomsInView", Kind.Bool }, { "IncludeSteelElements", Kind.Bool },
            { "UseOnlyTriangulation", Kind.Bool }, { "UseTypeNameOnlyForIfcType", Kind.Bool },
            { "UseVisibleRevitNameAsEntityName", Kind.Bool }, { "TessellationLevelOfDetail", Kind.Double },
            { "ExportSpecificSchedules", Kind.Bool }
        };

        /// <summary>ClassificationSettings keys (strings). The edition DATE is left out: its JSON form differs between exporter builds.</summary>
        internal static readonly string[] ClassificationKeys =
        {
            "ClassificationName", "ClassificationEdition", "ClassificationSource", "ClassificationLocation", "ClassificationFieldName"
        };

        private static readonly Regex IfcEntity = new Regex(@"^Ifc[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);
        private static readonly Regex Predefined = new Regex(@"^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant);
        private static readonly char[] NameForbidden = { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '{', '}', '[', ']', ';' };

        /// <summary>
        /// Read the ifc_setup object (and the exporter-level arguments horizun_export already
        /// has) into a request, or throw ArgumentException naming the first problem.
        /// </summary>
        public static IfcSetupRequest Read(JObject setup, bool? baseQuantities, bool? splitWalls, int? spaceBoundaryLevel)
        {
            if (setup == null) throw new ArgumentException("format ifc_setup needs ifc_setup.name: the setup to save in the document.");
            foreach (JProperty p in setup.Properties())
                if (p.Name != "name" && p.Name != "options" && p.Name != "classification" && p.Name != "type_mappings")
                    throw new ArgumentException("ifc_setup does not take '" + p.Name + "'.");
            var r = new IfcSetupRequest { Name = ((string)setup["name"] ?? "").Trim() };
            if (r.Name.Length == 0 || r.Name.Length > 120) throw new ArgumentException("ifc_setup.name must be 1..120 characters.");
            if (r.Name.IndexOfAny(NameForbidden) >= 0 || r.Name.StartsWith("<", StringComparison.Ordinal))
                throw new ArgumentException("ifc_setup.name '" + r.Name + "' contains a character the setup list refuses.");

            if (setup["options"] != null && setup["options"].Type != JTokenType.Null)
            {
                if (!(setup["options"] is JObject options)) throw new ArgumentException("ifc_setup.options must be an object.");
                foreach (JProperty p in options.Properties())
                {
                    if (!OptionKinds.TryGetValue(p.Name, out Kind kind))
                        throw new ArgumentException("ifc_setup.options." + p.Name + " is not an exporter option this tool writes. It writes: " +
                                                    string.Join(", ", OptionKinds.Keys) + ".");
                    r.Options[p.Name] = Coerce(p.Value, kind, "ifc_setup.options." + p.Name);
                }
            }
            Merge(r.Options, "ExportBaseQuantities", baseQuantities.HasValue ? (JToken)baseQuantities.Value : null);
            Merge(r.Options, "SplitWallsAndColumns", splitWalls.HasValue ? (JToken)splitWalls.Value : null);
            Merge(r.Options, "SpaceBoundaries", spaceBoundaryLevel.HasValue ? (JToken)spaceBoundaryLevel.Value : null);
            if (r.Options["SpaceBoundaries"] != null)
            {
                int level = (int)r.Options["SpaceBoundaries"];
                if (level < 0 || level > 2) throw new ArgumentException("SpaceBoundaries (ifc_space_boundary_level) must be 0, 1 or 2.");
            }
            if (r.Options["TessellationLevelOfDetail"] != null)
            {
                double lod = (double)r.Options["TessellationLevelOfDetail"];
                if (lod < 0 || lod > 1) throw new ArgumentException("TessellationLevelOfDetail must be within 0..1.");
            }

            if (setup["classification"] != null && setup["classification"].Type != JTokenType.Null)
            {
                if (!(setup["classification"] is JObject c)) throw new ArgumentException("ifc_setup.classification must be an object.");
                var cls = new JObject();
                foreach (JProperty p in c.Properties())
                {
                    if (Array.IndexOf(ClassificationKeys, p.Name) < 0)
                        throw new ArgumentException("ifc_setup.classification." + p.Name + " is not written. Keys: " + string.Join(", ", ClassificationKeys) + ".");
                    cls[p.Name] = Coerce(p.Value, Kind.String, "ifc_setup.classification." + p.Name);
                }
                if (cls.Count > 0) r.Classification = cls;
            }

            if (setup["type_mappings"] != null && setup["type_mappings"].Type != JTokenType.Null)
            {
                if (!(setup["type_mappings"] is JArray rows)) throw new ArgumentException("ifc_setup.type_mappings must be an array.");
                if (rows.Count > MaxTypeMappings) throw new ArgumentException("ifc_setup.type_mappings holds at most " + MaxTypeMappings + " rows.");
                var seen = new HashSet<long>();
                for (int i = 0; i < rows.Count; i++)
                {
                    string at = "ifc_setup.type_mappings[" + i + "]";
                    if (!(rows[i] is JObject row)) throw new ArgumentException(at + " must be an object.");
                    foreach (JProperty p in row.Properties())
                        if (p.Name != "type_id" && p.Name != "export_as" && p.Name != "predefined_type")
                            throw new ArgumentException(at + " does not take '" + p.Name + "'.");
                    if (row["type_id"] == null || row["type_id"].Type != JTokenType.Integer) throw new ArgumentException(at + ".type_id (integer) is required.");
                    long id = (long)row["type_id"];
                    if (!seen.Add(id)) throw new ArgumentException(at + ": type_id " + id + " is mapped twice.");
                    string exportAs = ((string)row["export_as"] ?? "").Trim();
                    if (!IfcEntity.IsMatch(exportAs)) throw new ArgumentException(at + ".export_as must be an IFC entity name such as IfcFooting or IfcWallType.");
                    string predefined = ((string)row["predefined_type"])?.Trim();
                    if (predefined != null && predefined.Length == 0) predefined = null;
                    if (predefined != null && !Predefined.IsMatch(predefined))
                        throw new ArgumentException(at + ".predefined_type must be an IFC enumeration value such as PAD_FOOTING (upper case).");
                    r.TypeMappings.Add(new IfcTypeMapping { TypeId = id, ExportAs = exportAs, PredefinedType = predefined });
                }
            }
            return r;
        }

        /// <summary>The JSON the exporter UI reads: its own keys, IFCVersion as the enum's integer.</summary>
        public static JObject BuildConfig(IfcSetupRequest r, int ifcVersion)
        {
            var config = new JObject
            {
                ["Name"] = r.Name,
                ["IFCVersion"] = ifcVersion,
                ["IsBuiltIn"] = false,
                ["IsInSession"] = false
            };
            foreach (JProperty p in r.Options.Properties()) config[p.Name] = p.Value.DeepClone();
            if (r.Classification != null) config["ClassificationSettings"] = r.Classification.DeepClone();
            return config;
        }

        /// <summary>Every requested key whose re-read value differs (missing counts). Empty when the re-read holds the request.</summary>
        public static List<string> Mismatches(JObject requested, JObject reread)
        {
            var bad = new List<string>();
            if (requested == null) return bad;
            if (reread == null) { bad.Add("(setup not found)"); return bad; }
            foreach (JProperty p in requested.Properties())
            {
                JToken back = reread[p.Name];
                if (p.Value is JObject inner)
                {
                    if (!(back is JObject backInner)) { bad.Add(p.Name); continue; }
                    bad.AddRange(Mismatches(inner, backInner).Select(k => p.Name + "." + k));
                }
                else if (!SameScalar(p.Value, back)) bad.Add(p.Name);
            }
            return bad;
        }

        /// <summary>True when the name matches, ordinal and case-insensitive: the exporter keys its map by name.</summary>
        public static bool SameName(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        internal static bool SameScalar(JToken a, JToken b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (IsNumber(a) && IsNumber(b))
                return Math.Abs(Convert.ToDouble(((JValue)a).Value, CultureInfo.InvariantCulture) -
                                Convert.ToDouble(((JValue)b).Value, CultureInfo.InvariantCulture)) < 1e-9;
            if (a.Type == JTokenType.Boolean && b.Type == JTokenType.Boolean) return (bool)a == (bool)b;
            if (a.Type == JTokenType.String && b.Type == JTokenType.String) return string.Equals((string)a, (string)b, StringComparison.Ordinal);
            return JToken.DeepEquals(a, b);
        }

        private static bool IsNumber(JToken t) => t.Type == JTokenType.Integer || t.Type == JTokenType.Float;

        private static void Merge(JObject options, string key, JToken value)
        {
            if (value == null) return;
            if (options[key] != null && !SameScalar(options[key], value))
                throw new ArgumentException("ifc_setup.options." + key + " disagrees with the matching horizun_export argument; send one.");
            options[key] = value;
        }

        private static JToken Coerce(JToken value, Kind kind, string at)
        {
            switch (kind)
            {
                case Kind.Bool:
                    if (value.Type != JTokenType.Boolean) throw new ArgumentException(at + " must be a boolean.");
                    return value.DeepClone();
                case Kind.Int:
                    if (value.Type != JTokenType.Integer) throw new ArgumentException(at + " must be an integer.");
                    return value.DeepClone();
                case Kind.Double:
                    if (!IsNumber(value)) throw new ArgumentException(at + " must be a number.");
                    double d = Convert.ToDouble(((JValue)value).Value, CultureInfo.InvariantCulture);
                    if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException(at + " must be finite.");
                    return new JValue(d);
                default:
                    if (value.Type != JTokenType.String) throw new ArgumentException(at + " must be a string.");
                    string s = (string)value;
                    if (s.Length > 1024) throw new ArgumentException(at + " is longer than 1024 characters.");
                    return new JValue(s);
            }
        }
    }
}
