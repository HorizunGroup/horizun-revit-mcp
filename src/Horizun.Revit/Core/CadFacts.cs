// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// What Revit will actually tell you about a DWG. MEASURED, never decided.
//
// Every claim in this file was verified by reflection over RevitAPI.dll 26.4 and
// then by linking a real DWG into Revit 2026 and reading it back. The findings
// that shaped it, in the order they mattered:
//
//   LAYERS ARE REACHABLE, INDIRECTLY. A curve's GraphicsStyleId resolves to a
//   GraphicsStyle whose GraphicsStyleCategory.Name IS the DWG layer name, with
//   Parent.Name being the import symbol. Measured: four layers came back named
//   exactly as the drawing had them.
//
//   TEXT IS NOT. Zero strings are reachable from imported geometry. A text
//   entity arrives as curves on its own layer - the layer name survives, the
//   words do not. Anything that wanted to read a room name off a label is dead
//   on this path, and this file says `unavailable` rather than returning "".
//
//   UNITS LIVE ON THE TYPE, NOT THE INSTANCE. IMPORT_DISPLAY_UNITS reads null
//   on the ImportInstance; the CADLinkType carries "Import Units". Measured.
//
//   THERE IS NO HANDLE, AND GeometryObject.Id COLLIDES. 35 objects, 24 distinct
//   ids, nine PolyLines all answering 1. Identity is computed (CadIdentity), and
//   this file is where the inputs to that computation are gathered.
//
// The house split applies: *Facts.cs touches Revit and only measures; *Rules.cs
// is Revit-free and decides. Nothing here decides anything.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>How a fact was obtained. Published on every reply, per the mandate that a reader knows what they are trusting.</summary>
    public static class CadProvenanceKind
    {
        /// <summary>Revit handed this over directly.</summary>
        public const string Native = "native";
        /// <summary>Computed here from native facts (a surrogate id, a decomposed chord).</summary>
        public const string Derived = "derived";
        /// <summary>The API does not expose it at all, on any path this bridge has.</summary>
        public const string Unavailable = "unavailable";
        /// <summary>Native but lossy - a chorded arc, a tessellated spline.</summary>
        public const string Approximate = "approximate";
    }

    /// <summary>One CAD instance in a document, as measured.</summary>
    public sealed class CadInstanceFacts
    {
        public long ElementId;
        public string UniqueId;
        public string Name;                     // the import SYMBOL name - the drawing's own
        public string PlacementLabel;           // Revit's placement label, kept beside it, never instead
        public bool? IsLinked;                  // null when Revit would not say
        public string IsLinkedError;
        public bool Pinned;
        public bool ViewSpecific;
        public long? OwnerViewId;
        public string OwnerViewName;

        public string ExternalPath;             // user-visible, resolved
        public string PathType;
        public string LinkedFileStatus;
        public string ExternalReferenceError;

        public string DeclaredUnits;            // off the TYPE, by built-in parameter ordinal
        public string DeclaredUnitsRoute;       // HOW it was read, so a reader knows what they are trusting
        public double? ScaleFactor;
        public double? InstanceScale;
        public string BaseLevel;
        public double? BaseLevelOffsetMm;

        public double[] TransformOrigin;        // mm
        public double[] TransformBasisX;
        public double[] TransformBasisY;
        public bool? TransformHasReflection;
        public double? TransformScale;
        public string TransformFingerprint;

        public string FileSha256;               // when the file is readable on this machine
        public long? FileBytes;
        public string FileModifiedUtc;
        public string FileError;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["element_id"] = ElementId,
                ["unique_id"] = UniqueId,
                ["name"] = Name,
                ["placement_label"] = PlacementLabel,
                ["is_linked"] = IsLinked.HasValue ? (JToken)IsLinked.Value : JValue.CreateNull(),
                ["import_or_link"] = IsLinked.HasValue ? (IsLinked.Value ? "linked" : "imported") : "unreadable",
                ["pinned"] = Pinned,
                ["view_specific"] = ViewSpecific,
                ["owner_view_id"] = OwnerViewId.HasValue ? (JToken)OwnerViewId.Value : JValue.CreateNull(),
                ["owner_view_name"] = OwnerViewName,
                ["external_path"] = ExternalPath,
                ["path_type"] = PathType,
                ["linked_file_status"] = LinkedFileStatus,
                ["declared_units"] = DeclaredUnits,
                ["declared_units_route"] = DeclaredUnitsRoute,
                ["scale_factor"] = ScaleFactor.HasValue ? (JToken)ScaleFactor.Value : JValue.CreateNull(),
                ["instance_scale"] = InstanceScale.HasValue ? (JToken)InstanceScale.Value : JValue.CreateNull(),
                ["base_level"] = BaseLevel,
                ["base_level_offset_mm"] = BaseLevelOffsetMm.HasValue ? (JToken)BaseLevelOffsetMm.Value : JValue.CreateNull(),
                ["file_sha256"] = FileSha256,
                ["file_bytes"] = FileBytes.HasValue ? (JToken)FileBytes.Value : JValue.CreateNull(),
                ["file_modified_utc"] = FileModifiedUtc
            };
            if (IsLinkedError != null) o["is_linked_error"] = IsLinkedError;
            if (ExternalReferenceError != null) o["external_reference_error"] = ExternalReferenceError;
            if (FileError != null) o["file_error"] = FileError;
            if (TransformOrigin != null)
                o["transform"] = new JObject
                {
                    ["origin_mm"] = new JArray(TransformOrigin),
                    ["basis_x"] = new JArray(TransformBasisX),
                    ["basis_y"] = new JArray(TransformBasisY),
                    ["scale"] = TransformScale.HasValue ? (JToken)TransformScale.Value : JValue.CreateNull(),
                    ["has_reflection"] = TransformHasReflection.HasValue ? (JToken)TransformHasReflection.Value : JValue.CreateNull(),
                    ["fingerprint"] = TransformFingerprint
                };
            return o;
        }
    }

    public static class CadFacts
    {
        /// <summary>
        /// Every CAD instance in the document, in three buckets: readable, and
        /// the ones whose own identity Revit would not answer for. The third
        /// bucket is the point - a throw becomes a named unreadable, never a
        /// row quietly missing from a census somebody bills off.
        /// </summary>
        public static List<CadInstanceFacts> Collect(Document doc, out List<JObject> unreadable)
        {
            var facts = new List<CadInstanceFacts>();
            unreadable = new List<JObject>();
            if (doc == null) return facts;

            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))
            {
                var f = new CadInstanceFacts();
                try
                {
                    f.ElementId = Rid.Value(e.Id);
                    f.UniqueId = Safe(() => e.UniqueId);
                    // Element.Name on an ImportInstance answers Revit's own
                    // placement label - measured: "location <Not Shared>" - which
                    // is not what anybody means by the name of a drawing. The
                    // symbol name is the drawing's, so it is preferred and the
                    // placement label is kept beside it rather than instead.
                    f.PlacementLabel = Safe(() => e.Name);
                    f.Name = ParamValueString(e, BuiltInParameter.IMPORT_SYMBOL_NAME) ?? f.PlacementLabel;
                    f.Pinned = Safe(() => e.Pinned, false);
                    f.ViewSpecific = Safe(() => e.ViewSpecific, false);
                    ElementId owner = Safe(() => e.OwnerViewId, ElementId.InvalidElementId);
                    if (owner != null && owner != ElementId.InvalidElementId)
                    {
                        f.OwnerViewId = Rid.Value(owner);
                        f.OwnerViewName = Safe(() => (doc.GetElement(owner) as View)?.Name);
                    }
                }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject
                    {
                        ["element_id"] = Safe(() => Rid.Value(e.Id), -1L),
                        ["error"] = ex.Message,
                        ["means"] = "this CAD instance could not be identified at all; it is NOT counted as absent"
                    });
                    continue;
                }

                // IsLinked in its own try: measured on the RVT side of this repo
                // that a cloud-hosted reference throws here, and a throw that
                // defaults to false is a lie about what is in the model.
                try { f.IsLinked = ((ImportInstance)e).IsLinked; }
                catch (Exception ex) { f.IsLinkedError = ex.Message; }

                ReadExternalReference(doc, e, f);
                ReadTypeAndInstanceParameters(doc, e, f);
                ReadTransform(e, f);
                ReadFileFacts(f);
                facts.Add(f);
            }
            return facts;
        }

        private static void ReadExternalReference(Document doc, Element e, CadInstanceFacts f)
        {
            try
            {
                ElementId typeId = e.GetTypeId();
                ElementId owner = (typeId != ElementId.InvalidElementId &&
                                   ExternalFileUtils.IsExternalFileReference(doc, typeId)) ? typeId : e.Id;
                if (!ExternalFileUtils.IsExternalFileReference(doc, owner))
                {
                    // An IMPORT (as opposed to a link) has no external reference,
                    // and that is a fact about it rather than a failure to read.
                    f.LinkedFileStatus = "not_an_external_reference";
                    return;
                }
                ExternalFileReference r = ExternalFileUtils.GetExternalFileReference(doc, owner);
                f.PathType = Safe(() => r.PathType.ToString());
                f.LinkedFileStatus = Safe(() => r.GetLinkedFileStatus().ToString());
                // GetAbsolutePath().CentralServerPath comes back empty for a local
                // file - measured. ConvertModelPathToUserVisiblePath is the route
                // that answers.
                f.ExternalPath = Safe(() => ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetAbsolutePath()));
            }
            catch (Exception ex) { f.ExternalReferenceError = ex.Message; }
        }

        private static void ReadTypeAndInstanceParameters(Document doc, Element e, CadInstanceFacts f)
        {
            f.InstanceScale = ParamDouble(e, BuiltInParameter.IMPORT_INSTANCE_SCALE);
            f.BaseLevel = ParamValueString(e, BuiltInParameter.IMPORT_BASE_LEVEL);
            double? offsetFeet = ParamDouble(e, BuiltInParameter.IMPORT_BASE_LEVEL_OFFSET);
            if (offsetFeet.HasValue) f.BaseLevelOffsetMm = CadUnits.FeetToMm(offsetFeet.Value);

            ElementId typeId = Safe(() => e.GetTypeId(), ElementId.InvalidElementId);
            Element type = typeId != ElementId.InvalidElementId ? Safe(() => doc.GetElement(typeId)) : null;
            if (type == null) return;
            // MEASURED: the units are on the TYPE. IMPORT_DISPLAY_UNITS on the
            // instance reads null; the CADLinkType answers.
            //
            // READ THE BUILT-IN PARAMETER, NOT ITS DISPLAY NAME. The first
            // version matched the string "Import Units", which is the ENGLISH
            // name: on a Spanish Revit the parameter is called "Unidades de
            // importación", the match failed, the units read as null, and every
            // drawing in that office was refused by the unit gate. AsValueString
            // is localised too, so even a successful name match would have
            // answered "Milímetros", which resolves to nothing. The enum
            // ordinal is the same in every language.
            int? unitOrdinal = ParamInteger(type, BuiltInParameter.IMPORT_DISPLAY_UNITS);
            if (unitOrdinal.HasValue)
            {
                f.DeclaredUnits = ImportUnitName(unitOrdinal.Value);
                f.DeclaredUnitsRoute = "IMPORT_DISPLAY_UNITS on the type (ordinal " + unitOrdinal.Value + ")";
            }
            f.ScaleFactor = ParamDouble(type, BuiltInParameter.IMPORT_SCALE);

            if (f.DeclaredUnits == null)
            {
                // Last resort, and it says so: the English display name.
                foreach (Parameter p in Safe(() => type.Parameters, null) ?? (System.Collections.IEnumerable)new Parameter[0])
                {
                    string name = Safe(() => p.Definition?.Name);
                    if (string.Equals(name, "Import Units", StringComparison.OrdinalIgnoreCase))
                    {
                        f.DeclaredUnits = Safe(() => p.AsValueString());
                        f.DeclaredUnitsRoute = "the type parameter named 'Import Units' - an ENGLISH display name, " +
                                               "so this route does not work on a localised Revit";
                    }
                    else if (f.ScaleFactor == null && string.Equals(name, "Scale Factor", StringComparison.OrdinalIgnoreCase))
                        f.ScaleFactor = Safe(() => (double?)p.AsDouble(), null);
                }
            }
        }

        private static void ReadTransform(Element e, CadInstanceFacts f)
        {
            try
            {
                var instance = e as Instance;
                if (instance == null) return;
                Transform t = instance.GetTotalTransform();
                f.TransformOrigin = new[] { CadUnits.FeetToMm(t.Origin.X), CadUnits.FeetToMm(t.Origin.Y), CadUnits.FeetToMm(t.Origin.Z) };
                f.TransformBasisX = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z };
                f.TransformBasisY = new[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z };
                f.TransformScale = t.Scale;
                f.TransformHasReflection = t.HasReflection;
                // The fingerprint is what a stale-plan check compares: a link that
                // was nudged between the rehearsal and the apply must not be
                // written into on the strength of where it used to be.
                f.TransformFingerprint = "cadtf:" + CadIdentity.Sha256Hex(string.Join(",",
                    f.TransformOrigin.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))
                    .Concat(f.TransformBasisX.Select(v => v.ToString("0.#########", CultureInfo.InvariantCulture)))
                    .Concat(f.TransformBasisY.Select(v => v.ToString("0.#########", CultureInfo.InvariantCulture)))
                    .Concat(new[] { t.Scale.ToString("0.#########", CultureInfo.InvariantCulture),
                                    t.HasReflection ? "reflected" : "direct" }))).Substring(0, 20);
            }
            catch { /* a transform that will not read stays null rather than becoming identity */ }
        }

        private static void ReadFileFacts(CadInstanceFacts f)
        {
            if (string.IsNullOrWhiteSpace(f.ExternalPath)) return;
            try
            {
                var info = new System.IO.FileInfo(f.ExternalPath);
                if (!info.Exists)
                {
                    f.FileError = "the referenced file is not on this machine at " + f.ExternalPath;
                    return;
                }
                f.FileBytes = info.Length;
                f.FileModifiedUtc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (System.IO.FileStream stream = info.OpenRead())
                    f.FileSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex) { f.FileError = ex.Message; }
        }

        /// <summary>
        /// The identity a plan binds itself to. Everything that could make the
        /// same request mean something different tomorrow: which file, which
        /// bytes, which link, where it sits.
        /// </summary>
        public static string SourceFingerprint(CadInstanceFacts f)
        {
            if (f == null) return null;
            string basis = string.Join("|", new[]
            {
                f.UniqueId ?? "(no-uid)",
                f.FileSha256 ?? "(no-file-hash)",
                f.ExternalPath ?? "(no-path)",
                f.LinkedFileStatus ?? "(no-status)",
                f.DeclaredUnits ?? "(no-units)",
                f.TransformFingerprint ?? "(no-transform)"
            });
            return "cadsrc:" + CadIdentity.Sha256Hex(basis).Substring(0, 24);
        }

        // ---- small safe readers ---------------------------------------------
        private static T Safe<T>(Func<T> f, T fallback = default(T))
        {
            try { return f(); } catch { return fallback; }
        }

        private static double? ParamDouble(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                return p.AsDouble();
            }
            catch { return null; }
        }

        private static int? ParamInteger(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Integer) return null;
                return p.AsInteger();
            }
            catch { return null; }
        }

        /// <summary>
        /// Revit's ImportUnit enum, by ordinal, in the vocabulary CadUnits
        /// resolves. The ordinal is language-independent; the display string is
        /// not, which is the whole reason this exists.
        /// </summary>
        private static string ImportUnitName(int ordinal)
        {
            // The enum's NAME, not a switch over its members.
            //
            // Two reasons, and the second cost a build: the ordinal is
            // language-independent where the display string is not, and the
            // member set DIFFERS BETWEEN REVIT VERSIONS - ImportUnit.USSurveyFoot
            // does not exist in the 2023 API, so naming it stops this file
            // compiling for the years it must support. ToString() gives
            // "Millimeter", "USSurveyFoot", "Default" and so on, which is exactly
            // the vocabulary CadUnits.MillimetresPer resolves, and an unknown
            // member comes back as its own name rather than as a guess.
            try
            {
                if (!Enum.IsDefined(typeof(ImportUnit), ordinal)) return null;
                return ((ImportUnit)ordinal).ToString().ToLowerInvariant();
            }
            catch { return null; }
        }

        private static string ParamValueString(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null) return null;
                return p.AsValueString() ?? p.AsString();
            }
            catch { return null; }
        }
    }
}
