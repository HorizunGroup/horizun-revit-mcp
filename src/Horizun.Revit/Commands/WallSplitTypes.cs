// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE SINGLE-LAYER TYPE FOR ONE LAYER, and the mark that says where a wall came
// from.
//
// TYPES. The naming rule is somebody's convention and it is followed exactly:
//
//     [NOMBRE DEL TIPO ORIGINAL] - [NOMBRE DEL MATERIAL] - [NN]
//
// But a NAME is not an IDENTITY. The previous implementation reused the first
// WallType whose name matched, which put a stranger's compound structure on real
// geometry, and when SetCompoundStructure threw it kept a duplicate carrying the
// WHOLE multilayer assembly - so a "layer" wall was the entire compound wall, at
// one layer's offset, committed and verified. Here the name is looked up, the
// candidate's compound structure is RE-READ and compared layer by layer, and a
// name that already means something else gets a deterministic variant rather
// than being overwritten. No existing type is ever modified.
//
// PROVENANCE. A wall that came out of a split says so, durably, in Extensible
// Storage: which wall it came from, under which plan, which layer it is and what
// role it plays. That is what lets a second call answer "already_split" instead
// of building a second set of walls beside the first.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>Which type a layer got, how it was obtained, and why not.</summary>
    public sealed class TypeResolution
    {
        public ElementId TypeId;
        public string Name;
        public bool Reused;
        public bool Created;
        public string Failure;
    }

    public static class WallSplitTypes
    {
        /// <summary>
        /// Find or build the single-layer type for one layer, following the naming rule and
        /// refusing to trust a name on its own.
        /// </summary>
        public static TypeResolution Resolve(Document doc, WallType sourceType, WallAssemblyFacts assembly,
                                             WallLayerPlan layer)
        {
            var resolution = new TypeResolution();

            CompoundStructureLayer source = SourceLayer(sourceType, layer.LayerIndex);
            if (source == null)
            {
                resolution.Failure = "the source layer could not be re-read from the wall type.";
                return resolution;
            }

            // 1. The expected name.
            WallType candidate = FindByName(doc, layer.ExpectedTypeName);
            if (candidate != null)
            {
                if (Matches(doc, candidate, source, assembly, layer))
                {
                    resolution.TypeId = candidate.Id;
                    resolution.Name = layer.ExpectedTypeName;
                    resolution.Reused = true;
                    return resolution;
                }

                // 3. The name is taken by something else. It is NOT overwritten and NOT
                //    modified: somebody else's walls are on it.
                WallType variant = FindByName(doc, layer.VariantTypeName);
                if (variant != null)
                {
                    if (Matches(doc, variant, source, assembly, layer))
                    {
                        resolution.TypeId = variant.Id;
                        resolution.Name = layer.VariantTypeName;
                        resolution.Reused = true;
                        return resolution;
                    }
                    resolution.Failure =
                        "'" + layer.ExpectedTypeName + "' already exists with a different composition, and so does " +
                        "the deterministic variant '" + layer.VariantTypeName + "'. Neither is modified, because " +
                        "other walls are using them.";
                    return resolution;
                }

                return Create(doc, sourceType, source, assembly, layer, layer.VariantTypeName, resolution);
            }

            // 4. Nothing by that name: build it.
            return Create(doc, sourceType, source, assembly, layer, layer.ExpectedTypeName, resolution);
        }

        private static TypeResolution Create(Document doc, WallType sourceType, CompoundStructureLayer source,
                                             WallAssemblyFacts assembly, WallLayerPlan layer, string name,
                                             TypeResolution resolution)
        {
            ElementType duplicate = null;
            try
            {
                duplicate = sourceType.Duplicate(name);
                var made = duplicate as WallType;
                if (made == null)
                {
                    resolution.Failure = "duplicating " + WallSplitFacts.SafeName(sourceType) +
                                         " did not produce a wall type.";
                    return Undo(doc, duplicate, resolution);
                }

                CompoundStructure single = CompoundStructure.CreateSingleLayerCompoundStructure(
                    source.Function, source.Width, source.MaterialId);

                // SetCompoundStructure REPLACES the whole structure, wrapping and end caps
                // included, so the source's values are carried over deliberately. They are in
                // the type fingerprint, which means they have to be applied here and re-read
                // below: a fact in the digest that nobody sets is a fact nobody can match on.
                CarryWrapping(sourceType, single);
                made.SetCompoundStructure(single);

                string failure = Confirm(doc, made, source, assembly, layer);
                if (failure != null)
                {
                    resolution.Failure = failure + " The duplicate was deleted and nothing was left behind.";
                    return Undo(doc, made, resolution);
                }

                resolution.TypeId = made.Id;
                resolution.Name = name;
                resolution.Created = true;
                return resolution;
            }
            catch (Exception ex)
            {
                resolution.Failure = "the single-layer type " + name + " could not be created: " + ex.Message;
                return Undo(doc, duplicate, resolution);
            }
        }

        /// <summary>
        /// Carry the source assembly's wrapping and end-cap settings onto the new structure.
        /// Guarded individually: a setting Revit refuses is left at its default and caught by
        /// the read-back, rather than taking the whole type down.
        /// </summary>
        private static void CarryWrapping(WallType sourceType, CompoundStructure target)
        {
            try
            {
                CompoundStructure from = sourceType.GetCompoundStructure();
                if (from == null) return;
                try { target.OpeningWrapping = from.OpeningWrapping; } catch { }
                try { target.EndCap = from.EndCap; } catch { }
            }
            catch { }
        }

        /// <summary>
        /// Re-read a freshly built type and check EVERY fact the fingerprint is made of.
        /// Same list, same order, same source of truth as <see cref="Matches"/> - which is
        /// the property the director asked for: a digest richer than the check accepts types
        /// it never compared, and a check richer than the digest rebuilds types it had.
        /// </summary>
        private static string Confirm(Document doc, WallType made, CompoundStructureLayer source,
                                      WallAssemblyFacts assembly, WallLayerPlan layer)
        {
            CompoundStructure back = made.GetCompoundStructure();
            IList<CompoundStructureLayer> layers = back == null ? null : back.GetLayers();
            int count = layers == null ? -1 : layers.Count;
            if (count != 1)
                return "the new type " + WallSplitFacts.SafeName(made) + " came back with " + count +
                       " layers instead of 1, so it would not be a single-layer wall.";

            string mismatch = CompareIdentity(doc, made, back, layers[0], source, assembly);
            if (mismatch != null)
                return "the new type " + WallSplitFacts.SafeName(made) + " does not carry what was asked of it: " +
                       mismatch + ".";

            // And the digest itself, computed off what the MODEL now holds.
            string rebuilt = FingerprintOf(doc, made, back, layers[0]);
            if (!string.Equals(rebuilt, layer.TypeFingerprint, StringComparison.Ordinal))
                return "the new type's fingerprint, recomputed from the model, is not the one the plan approved";

            return null;
        }

        /// <summary>
        /// Delete a duplicate that did not become what it was meant to be. Leaving it would
        /// leave a wall type named for a layer that carries a different assembly.
        /// </summary>
        private static TypeResolution Undo(Document doc, Element duplicate, TypeResolution resolution)
        {
            if (duplicate != null)
            {
                try { doc.Delete(duplicate.Id); } catch { /* the SubTransaction rolls it back anyway */ }
            }
            return resolution;
        }

        private static CompoundStructureLayer SourceLayer(WallType type, int index)
        {
            try
            {
                IList<CompoundStructureLayer> layers = type?.GetCompoundStructure()?.GetLayers();
                return layers != null && index >= 0 && index < layers.Count ? layers[index] : null;
            }
            catch { return null; }
        }

        private static WallType FindByName(Document doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(t => string.Equals(WallSplitFacts.SafeName(t), name, StringComparison.Ordinal));
        }

        /// <summary>
        /// Is this existing type ACTUALLY the type this layer needs? Read back from the
        /// model and compared against THE SAME FACT LIST the fingerprint is built from -
        /// then against the fingerprint itself, recomputed from what the model holds. A name
        /// that matches proves none of it.
        /// </summary>
        private static bool Matches(Document doc, WallType candidate, CompoundStructureLayer source,
                                    WallAssemblyFacts assembly, WallLayerPlan layer)
        {
            try
            {
                if (candidate.Kind != WallKind.Basic) return false;

                CompoundStructure structure = candidate.GetCompoundStructure();
                IList<CompoundStructureLayer> layers = structure == null ? null : structure.GetLayers();
                if (layers == null || layers.Count != 1) return false;

                if (CompareIdentity(doc, candidate, structure, layers[0], source, assembly) != null) return false;

                return string.Equals(FingerprintOf(doc, candidate, structure, layers[0]),
                                     layer.TypeFingerprint, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// The one comparison both the builder's read-back and the reuse check run. It names
        /// what differs so a refusal can say why rather than just "no".
        /// </summary>
        private static string CompareIdentity(Document doc, WallType candidate, CompoundStructure structure,
                                              CompoundStructureLayer only, CompoundStructureLayer source,
                                              WallAssemblyFacts assembly)
        {
            if (only.Function != source.Function)
                return "function is " + only.Function + " and the layer is " + source.Function;

            if (!WallLayerRules.WithinTolerance(source.Width, only.Width))
                return "width is " + WallLayerRules.FeetToMm(only.Width).ToString("F2") +
                       " mm and the layer is " + WallLayerRules.FeetToMm(source.Width).ToString("F2") + " mm";

            string wanted = MaterialUniqueId(doc, source.MaterialId);
            string has = MaterialUniqueId(doc, only.MaterialId);
            if (!string.Equals(wanted ?? "", has ?? "", StringComparison.Ordinal))
                return "the material is a different element";

            if (candidate.Kind.ToString() != (assembly.WallKind ?? ""))
                return "wall kind is " + candidate.Kind + " and the source assembly is " + assembly.WallKind;

            if (!string.Equals(SafeWrapping(structure), assembly.OpeningWrapping ?? "", StringComparison.Ordinal))
                return "opening wrapping is " + SafeWrapping(structure) + " and the source assembly is " +
                       assembly.OpeningWrapping;

            if (!string.Equals(SafeEndCap(structure), assembly.EndCap ?? "", StringComparison.Ordinal))
                return "end cap is " + SafeEndCap(structure) + " and the source assembly is " + assembly.EndCap;

            return null;
        }

        /// <summary>The fingerprint of a type AS THE MODEL HOLDS IT, not as it was requested.</summary>
        internal static string FingerprintOf(Document doc, WallType type, CompoundStructure structure,
                                            CompoundStructureLayer only)
        {
            var facts = new WallLayerFacts
            {
                Index = 0,
                WidthFeet = only.Width,
                MaterialUniqueId = MaterialUniqueId(doc, only.MaterialId),
                Function = only.Function.ToString()
            };
            return WallLayerRules.LayerTypeFingerprint(facts, type.Kind.ToString(),
                                                       SafeWrapping(structure), SafeEndCap(structure));
        }

        private static string SafeWrapping(CompoundStructure structure)
        {
            try { return structure == null ? "" : structure.OpeningWrapping.ToString(); } catch { return ""; }
        }

        private static string SafeEndCap(CompoundStructure structure)
        {
            try { return structure == null ? "" : structure.EndCap.ToString(); } catch { return ""; }
        }

        private static string MaterialUniqueId(Document doc, ElementId id)
        {
            try
            {
                if (id == null || Rid.Value(id) <= 0) return null;
                Element material = doc.GetElement(id);
                return material == null ? null : material.UniqueId;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// How the "is there anything else out there carrying this plan?" question is answered.
    /// </summary>
    public enum ExtrasScan
    {
        /// <summary>Answered from an index built once for the whole call.</summary>
        Indexed,

        /// <summary>
        /// Not asked, because it cannot have an answer. Inside the SubTransaction that just
        /// minted a plan fingerprint, the only walls that can carry it are the ones this
        /// conversion just created - they are the siblings. Scanning every wall in the
        /// document once per wall in the batch is the O(N x M) shape that makes a fifty-wall
        /// call time out, for a question whose answer is known.
        /// </summary>
        SkippedByConstruction
    }

    /// <summary>
    /// Every wall in the document that carries a provenance stamp, indexed by plan
    /// fingerprint. Built ONCE per command invocation: the alternative is a full wall
    /// collector per wall inspected, which on a real model is minutes of UI thread.
    /// </summary>
    public sealed class WallProvenanceIndex
    {
        private readonly Dictionary<string, List<KeyValuePair<string, long>>> _byPlan =
            new Dictionary<string, List<KeyValuePair<string, long>>>(StringComparer.Ordinal);

        public bool ScanRan { get; private set; }
        public string ScanFailure { get; private set; }

        public static WallProvenanceIndex Build(Document doc)
        {
            var index = new WallProvenanceIndex();
            try
            {
                foreach (Wall wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    WallSplitProvenance.Stamp stamp = WallSplitProvenance.ReadStamp(wall);
                    if (!stamp.Present || string.IsNullOrEmpty(stamp.PlanFingerprint)) continue;

                    if (!index._byPlan.TryGetValue(stamp.PlanFingerprint, out var list))
                    {
                        list = new List<KeyValuePair<string, long>>();
                        index._byPlan[stamp.PlanFingerprint] = list;
                    }
                    list.Add(new KeyValuePair<string, long>(WallSplitFacts.SafeUniqueId(wall), Rid.Value(wall.Id)));
                }
                index.ScanRan = true;
            }
            catch (Exception ex)
            {
                // A scan that could not run is NOT a scan that found nothing.
                index.ScanRan = false;
                index.ScanFailure = ex.Message;
            }
            return index;
        }

        public IEnumerable<KeyValuePair<string, long>> CarryingPlan(string planFingerprint)
            => _byPlan.TryGetValue(planFingerprint ?? "", out var list)
                ? list
                : Enumerable.Empty<KeyValuePair<string, long>>();
    }

    /// <summary>
    /// The durable mark a split leaves on every wall it produced or converted. It is what
    /// makes a second call idempotent instead of doubling the geometry.
    /// </summary>
    public static class WallSplitProvenance
    {
        // Fixed for the lifetime of the schema. Changing it orphans every stamp already
        // written, so it never changes; the SCHEMA VERSION field carries evolution.
        // Bumped from 7a1c9e42-... when the record grew the fields the sibling-set check
        // needs. A Schema is registered once per session and its field set is fixed, so
        // changing the record means a new GUID; nothing has been stamped in the field yet,
        // so no stamps are orphaned by this.
        private static readonly Guid SchemaGuid = new Guid("c4f83b17-6d02-4a95-8e31-9b7042ac5d68");

        public const string FieldSchemaVersion = "schema_version";
        public const string FieldSourceWallUniqueId = "source_wall_unique_id";
        public const string FieldPlanFingerprint = "plan_fingerprint";
        public const string FieldOriginalWallTypeId = "original_wall_type_id";
        public const string FieldLayerIndex = "layer_index";
        public const string FieldRole = "role";
        public const string FieldSiblings = "sibling_unique_ids";
        public const string FieldConvertedAt = "converted_at";

        // ---- what the sibling-set check needs to be able to PROVE a set is complete ----
        public const string FieldExpectedWallCount = "expected_wall_count";
        public const string FieldExpectedLayerIndices = "expected_layer_indices";
        public const string FieldExpectedRoleByLayer = "expected_role_by_layer";
        public const string FieldTypeFingerprint = "type_fingerprint";
        public const string FieldExpectedTypeName = "expected_type_name";

        private static Schema Ensure()
        {
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("HorizunWallSplit");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetVendorId("HZUN");
            builder.SetDocumentation("Provenance of a wall produced by horizun_split_multilayer_walls.");

            builder.AddSimpleField(FieldSchemaVersion, typeof(string));
            builder.AddSimpleField(FieldSourceWallUniqueId, typeof(string));
            builder.AddSimpleField(FieldPlanFingerprint, typeof(string));
            builder.AddSimpleField(FieldOriginalWallTypeId, typeof(string));
            builder.AddSimpleField(FieldLayerIndex, typeof(int));
            builder.AddSimpleField(FieldRole, typeof(string));
            builder.AddSimpleField(FieldSiblings, typeof(string));
            builder.AddSimpleField(FieldConvertedAt, typeof(string));
            builder.AddSimpleField(FieldExpectedWallCount, typeof(int));
            builder.AddSimpleField(FieldExpectedLayerIndices, typeof(string));
            builder.AddSimpleField(FieldExpectedRoleByLayer, typeof(string));
            builder.AddSimpleField(FieldTypeFingerprint, typeof(string));
            builder.AddSimpleField(FieldExpectedTypeName, typeof(string));

            return builder.Finish();
        }

        /// <summary>
        /// The stamp a wall carries, read back out. One object rather than eight lookups so
        /// the verification compares a whole record instead of a field at a time.
        /// </summary>
        public sealed class Stamp
        {
            public string SchemaVersion;
            public string SourceWallUniqueId;
            public string PlanFingerprint;
            public string OriginalWallTypeId;
            public int LayerIndex = -1;
            public string Role;
            public List<string> Siblings = new List<string>();
            public string ConvertedAt;

            /// <summary>How many walls the conversion was supposed to produce.</summary>
            public int ExpectedWallCount = -1;

            /// <summary>The layer indices the conversion covers, ascending: "0;2;3".</summary>
            public string ExpectedLayerIndices;

            /// <summary>index=role pairs: "0=finish;2=core_carrier;3=shell".</summary>
            public string ExpectedRoleByLayer;

            public string TypeFingerprint;
            public string ExpectedTypeName;
            public bool Present;

            public IReadOnlyList<int> LayerIndices()
            {
                var result = new List<int>();
                foreach (string part in (ExpectedLayerIndices ?? "").Split(';'))
                    if (int.TryParse(part, out int value)) result.Add(value);
                return result;
            }

            public IReadOnlyDictionary<int, string> RoleByLayer()
            {
                var result = new Dictionary<int, string>();
                foreach (string part in (ExpectedRoleByLayer ?? "").Split(';'))
                {
                    string[] halves = part.Split('=');
                    if (halves.Length == 2 && int.TryParse(halves[0], out int index))
                        result[index] = halves[1];
                }
                return result;
            }
        }

        /// <summary>
        /// Write the provenance and PROVE IT LANDED, by reading the entity straight back and
        /// comparing every field.
        ///
        /// The first version swallowed every exception here and carried on, which meant a
        /// wall could be converted, reported as verified, and be indistinguishable next run
        /// from a wall nobody had touched - so a second call would split it again. A stamp
        /// that cannot be written is a failure of the conversion, not a footnote to it.
        /// </summary>
        /// <returns>null on success, or the reason the stamp is not trustworthy.</returns>
        public static string WriteVerified(Element element, string planFingerprint, string sourceWallUniqueId,
                                           string originalWallTypeId, int layerIndex, string role,
                                           IReadOnlyList<string> siblings, string convertedAt,
                                           int expectedWallCount, string expectedLayerIndices,
                                           string expectedRoleByLayer, string typeFingerprint,
                                           string expectedTypeName)
        {
            string joined = string.Join(";", siblings ?? new List<string>());

            try
            {
                Schema schema = Ensure();
                var entity = new Entity(schema);
                entity.Set(FieldSchemaVersion, WallSplitCodes.SchemaVersion);
                entity.Set(FieldSourceWallUniqueId, sourceWallUniqueId ?? "");
                entity.Set(FieldPlanFingerprint, planFingerprint ?? "");
                entity.Set(FieldOriginalWallTypeId, originalWallTypeId ?? "");
                entity.Set(FieldLayerIndex, layerIndex);
                entity.Set(FieldRole, role ?? "");
                entity.Set(FieldSiblings, joined);
                entity.Set(FieldConvertedAt, convertedAt ?? "");
                entity.Set(FieldExpectedWallCount, expectedWallCount);
                entity.Set(FieldExpectedLayerIndices, expectedLayerIndices ?? "");
                entity.Set(FieldExpectedRoleByLayer, expectedRoleByLayer ?? "");
                entity.Set(FieldTypeFingerprint, typeFingerprint ?? "");
                entity.Set(FieldExpectedTypeName, expectedTypeName ?? "");
                element.SetEntity(entity);
            }
            catch (Exception ex)
            {
                return "the provenance stamp could not be written to element " + Rid.Value(element.Id) + ": " +
                       ex.Message;
            }

            // READ IT BACK. SetEntity can be refused by a read-only element, a workshared
            // element somebody else owns, or a schema whose access level does not allow it,
            // and none of those throw where you would expect them to.
            Stamp back = ReadStamp(element);
            if (!back.Present)
                return "the provenance stamp on element " + Rid.Value(element.Id) + " could not be read back after " +
                       "being written, so nothing durable records that this wall was converted.";

            if (back.SchemaVersion != WallSplitCodes.SchemaVersion) return Drift(element, "schema_version");
            if (back.SourceWallUniqueId != (sourceWallUniqueId ?? "")) return Drift(element, "source_wall_unique_id");
            if (back.PlanFingerprint != (planFingerprint ?? "")) return Drift(element, "plan_fingerprint");
            if (back.OriginalWallTypeId != (originalWallTypeId ?? "")) return Drift(element, "original_wall_type_id");
            if (back.LayerIndex != layerIndex) return Drift(element, "layer_index");
            if (back.Role != (role ?? "")) return Drift(element, "role");
            if (string.Join(";", back.Siblings) != joined) return Drift(element, "sibling_unique_ids");
            if (back.ConvertedAt != (convertedAt ?? "")) return Drift(element, "converted_at");
            if (back.ExpectedWallCount != expectedWallCount) return Drift(element, "expected_wall_count");
            if (back.ExpectedLayerIndices != (expectedLayerIndices ?? "")) return Drift(element, "expected_layer_indices");
            if (back.ExpectedRoleByLayer != (expectedRoleByLayer ?? "")) return Drift(element, "expected_role_by_layer");
            if (back.TypeFingerprint != (typeFingerprint ?? "")) return Drift(element, "type_fingerprint");
            if (back.ExpectedTypeName != (expectedTypeName ?? "")) return Drift(element, "expected_type_name");

            return null;
        }

        private static string Drift(Element element, string field)
            => "the provenance stamp on element " + Rid.Value(element.Id) + " read back with a different " + field +
               " than was written, so the record cannot be trusted.";

        /// <summary>The whole stamp, or Present=false. Never a half-read record.</summary>
        public static Stamp ReadStamp(Element element)
        {
            var stamp = new Stamp();
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return stamp;

                Entity entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return stamp;

                stamp.SchemaVersion = entity.Get<string>(FieldSchemaVersion);
                stamp.SourceWallUniqueId = entity.Get<string>(FieldSourceWallUniqueId);
                stamp.PlanFingerprint = entity.Get<string>(FieldPlanFingerprint);
                stamp.OriginalWallTypeId = entity.Get<string>(FieldOriginalWallTypeId);
                stamp.LayerIndex = entity.Get<int>(FieldLayerIndex);
                stamp.Role = entity.Get<string>(FieldRole);
                stamp.ConvertedAt = entity.Get<string>(FieldConvertedAt);
                stamp.ExpectedWallCount = entity.Get<int>(FieldExpectedWallCount);
                stamp.ExpectedLayerIndices = entity.Get<string>(FieldExpectedLayerIndices);
                stamp.ExpectedRoleByLayer = entity.Get<string>(FieldExpectedRoleByLayer);
                stamp.TypeFingerprint = entity.Get<string>(FieldTypeFingerprint);
                stamp.ExpectedTypeName = entity.Get<string>(FieldExpectedTypeName);

                string joined = entity.Get<string>(FieldSiblings) ?? "";
                if (joined.Length > 0) stamp.Siblings.AddRange(joined.Split(';'));

                stamp.Present = true;
                return stamp;
            }
            catch { return new Stamp(); }
        }

        /// <summary>
        /// Is the WHOLE family of walls this conversion produced still present and coherent?
        ///
        /// The first version of this checked presence, a stamp, duplicate layer indices, a
        /// count and that the role was a known word. That is not enough to say already_split:
        /// it could not see a sibling belonging to a DIFFERENT conversion, an extra wall
        /// carrying the same plan outside the list, two carriers, a role that is valid but
        /// wrong for its layer, a missing layer index, a wall that has since been re-typed,
        /// or a siblings list that disagrees between members.
        ///
        /// Every one of those is now a named finding, and already_split is returned only
        /// when none of them fired.
        /// </summary>
        public static string InspectSiblingSet(Document doc, Element queried, ExtrasScan mode,
                                               WallProvenanceIndex index, out JObject report)
        {
            report = new JObject();
            Stamp anchor = ReadStamp(queried);
            report["queried_element_id"] = Rid.Value(queried.Id);
            report["queried_stamped"] = anchor.Present;

            if (!anchor.Present)
            {
                report["state"] = WallSplitCodes.NotSplit;
                return WallSplitCodes.NotSplit;
            }

            report["queried_role"] = anchor.Role;
            report["queried_layer_index"] = anchor.LayerIndex;
            report["source_wall_unique_id"] = anchor.SourceWallUniqueId;
            report["plan_fingerprint"] = anchor.PlanFingerprint;
            report["expected_wall_count"] = anchor.ExpectedWallCount;
            report["expected_layer_indices"] = anchor.ExpectedLayerIndices;
            report["converted_at"] = anchor.ConvertedAt;

            // A record that does not describe a conversion at all is its own answer: it is
            // not a partial state to repair, it is a stamp nobody can act on.
            var problems = new JArray();
            if (anchor.SchemaVersion != WallSplitCodes.SchemaVersion)
                problems.Add("the stamp was written by schema '" + anchor.SchemaVersion + "' and this build speaks '" +
                             WallSplitCodes.SchemaVersion + "'");
            if (string.IsNullOrEmpty(anchor.PlanFingerprint)) problems.Add("the stamp carries no plan fingerprint");
            if (string.IsNullOrEmpty(anchor.SourceWallUniqueId)) problems.Add("the stamp names no source wall");
            if (anchor.ExpectedWallCount <= 0) problems.Add("the stamp records no expected wall count");
            if (anchor.Siblings.Count == 0) problems.Add("the stamp lists no siblings");

            if (problems.Count > 0)
            {
                report["problems"] = problems;
                report["state"] = WallSplitCodes.ProvenanceInvalid;
                return WallSplitCodes.ProvenanceInvalid;
            }

            IReadOnlyList<int> expectedIndices = anchor.LayerIndices();
            IReadOnlyDictionary<int, string> expectedRoles = anchor.RoleByLayer();

            var rows = new JArray();
            var seenIndices = new List<int>();
            var carriers = new List<long>();
            var measured = new List<KeyValuePair<Wall, Stamp>>();
            Element carrierElement = null;
            var presentUniqueIds = new List<string>();
            int missing = 0, unstamped = 0, foreign = 0, roleWrong = 0, listDivergent = 0,
                fingerprintWrong = 0, notSingleLayer = 0, typeNameWrong = 0;

            string canonicalSiblings = string.Join(";", anchor.Siblings);

            foreach (string uniqueId in anchor.Siblings)
            {
                var row = new JObject { ["unique_id"] = uniqueId };
                Element sibling = null;
                try { sibling = doc.GetElement(uniqueId); } catch { }

                if (!(sibling is Wall wall) || !sibling.IsValidObject)
                {
                    row["present"] = false;
                    missing++;
                    rows.Add(row);
                    continue;
                }

                row["present"] = true;
                row["element_id"] = Rid.Value(wall.Id);
                presentUniqueIds.Add(uniqueId);

                Stamp stamp = ReadStamp(wall);
                row["stamped"] = stamp.Present;
                if (!stamp.Present) { unstamped++; rows.Add(row); continue; }

                row["layer_index"] = stamp.LayerIndex;
                row["role"] = stamp.Role;

                // Does this wall belong to THIS conversion, or to another one that happens to
                // be stamped? Source, plan, original type, schema and time all have to agree.
                bool sameConversion =
                    string.Equals(stamp.SourceWallUniqueId, anchor.SourceWallUniqueId, StringComparison.Ordinal) &&
                    string.Equals(stamp.PlanFingerprint, anchor.PlanFingerprint, StringComparison.Ordinal) &&
                    string.Equals(stamp.OriginalWallTypeId, anchor.OriginalWallTypeId, StringComparison.Ordinal) &&
                    string.Equals(stamp.SchemaVersion, anchor.SchemaVersion, StringComparison.Ordinal) &&
                    string.Equals(stamp.ConvertedAt, anchor.ConvertedAt, StringComparison.Ordinal) &&
                    stamp.ExpectedWallCount == anchor.ExpectedWallCount;
                row["same_conversion"] = sameConversion;
                if (!sameConversion) { foreign++; rows.Add(row); continue; }

                // Every member has to agree about who the members ARE.
                bool listAgrees = string.Equals(string.Join(";", stamp.Siblings), canonicalSiblings,
                                                StringComparison.Ordinal);
                row["sibling_list_agrees"] = listAgrees;
                if (!listAgrees) listDivergent++;

                seenIndices.Add(stamp.LayerIndex);
                measured.Add(new KeyValuePair<Wall, Stamp>(wall, stamp));
                if (string.Equals(stamp.Role, LayerRole.CoreCarrier, StringComparison.Ordinal))
                {
                    carriers.Add(Rid.Value(wall.Id));
                    carrierElement = wall;
                }

                // The role has to be the RIGHT role for this layer, not merely a word from
                // the vocabulary.
                string wantedRole = expectedRoles.TryGetValue(stamp.LayerIndex, out string r) ? r : null;
                bool roleOk = wantedRole != null &&
                              string.Equals(stamp.Role, wantedRole, StringComparison.Ordinal);
                row["expected_role"] = wantedRole;
                row["role_correct"] = roleOk;
                if (!roleOk) roleWrong++;

                // And the wall has to still BE what the conversion produced: single-layer,
                // on the type it was given, with that type's fingerprint.
                CompoundStructure structure = null;
                try { structure = wall.WallType == null ? null : wall.WallType.GetCompoundStructure(); } catch { }
                IList<CompoundStructureLayer> layers = structure == null ? null : structure.GetLayers();
                int count = layers == null ? -1 : layers.Count;
                row["layer_count"] = count;
                row["single_layer"] = count == 1;
                if (count != 1) notSingleLayer++;

                string actualName = WallSplitFacts.SafeName(wall.WallType);
                row["type_name"] = actualName;
                bool nameOk = string.IsNullOrEmpty(stamp.ExpectedTypeName) ||
                              string.Equals(actualName, stamp.ExpectedTypeName, StringComparison.Ordinal);
                row["type_name_correct"] = nameOk;
                if (!nameOk) typeNameWrong++;

                if (count == 1 && !string.IsNullOrEmpty(stamp.TypeFingerprint))
                {
                    string now = WallSplitTypes.FingerprintOf(doc, wall.WallType, structure, layers[0]);
                    bool fingerprintOk = string.Equals(now, stamp.TypeFingerprint, StringComparison.Ordinal);
                    row["type_fingerprint_matches"] = fingerprintOk;
                    if (!fingerprintOk) fingerprintWrong++;
                }

                rows.Add(row);
            }

            // ---- IS THERE ANYTHING ELSE OUT THERE? ----------------------------------
            //
            // A wall carrying this conversion's plan that is NOT in the list means the set
            // is not what the stamp says it is - a repair that half ran, or a copy somebody
            // made. Scanning is the only way to see it, and not scanning is how a duplicate
            // set stays invisible.
            var extras = new JArray();
            if (mode == ExtrasScan.SkippedByConstruction)
            {
                // Not asked, and the report says so rather than implying an empty answer.
                report["extra_scan_ran"] = JValue.CreateNull();
                report["extra_scan_note"] =
                    "not asked: this runs inside the SubTransaction that minted this plan fingerprint, so the only " +
                    "walls that can carry it are the ones this conversion just created. A document-wide scan per " +
                    "wall would be minutes of UI thread for a question whose answer is known.";
            }
            else if (index == null || !index.ScanRan)
            {
                report["extra_scan_ran"] = false;
                report["extra_scan_note"] = index == null
                    ? "no provenance index was supplied, so whether another wall carries this plan is unknown."
                    : "the provenance index could not be built (" + (index.ScanFailure ?? "no reason given") + ").";
            }
            else
            {
                foreach (KeyValuePair<string, long> candidate in index.CarryingPlan(anchor.PlanFingerprint))
                {
                    if (candidate.Key == null || anchor.Siblings.Contains(candidate.Key)) continue;
                    extras.Add(new JObject
                    {
                        ["element_id"] = candidate.Value,
                        ["unique_id"] = candidate.Key
                    });
                }
                report["extra_scan_ran"] = true;
            }

            // ---- AND ARE THEY STILL WHERE THEY BELONG? ------------------------------
            JObject geometry;
            string geometryFailure = MeasureLayerPositions(doc, queried as Wall ?? carrierElement as Wall,
                                                           anchor, measured, out geometry);
            report["geometry"] = geometry;

            var missingIndices = expectedIndices.Where(i => !seenIndices.Contains(i)).ToList();
            var duplicateIndices = seenIndices.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

            report["siblings"] = rows;
            report["siblings_recorded"] = anchor.Siblings.Count;
            report["siblings_present"] = presentUniqueIds.Count;
            report["siblings_missing"] = missing;
            report["siblings_unstamped"] = unstamped;
            report["siblings_from_another_conversion"] = foreign;
            report["sibling_lists_divergent"] = listDivergent;
            report["carriers_found"] = new JArray(carriers);
            report["expected_layer_indices_missing"] = new JArray(missingIndices);
            report["duplicate_layer_indices"] = new JArray(duplicateIndices);
            report["roles_incorrect"] = roleWrong;
            report["type_fingerprints_incorrect"] = fingerprintWrong;
            report["type_names_incorrect"] = typeNameWrong;
            report["not_single_layer"] = notSingleLayer;
            report["extra_walls_with_this_plan"] = extras;

            // already_split now REQUIRES the layers to be where they belong. A conversion
            // whose post-commit check failed - and which nothing could roll back, because
            // the outer transaction had closed - used to read back as complete here.
            report["geometry_failure"] = geometryFailure;

            bool geometryOk = geometryFailure == null;
            bool geometryMeasured = geometry != null && geometry.Value<bool?>("measured") == true;

            bool complete =
                geometryOk && geometryMeasured &&
                missing == 0 && unstamped == 0 && foreign == 0 && listDivergent == 0 &&
                roleWrong == 0 && fingerprintWrong == 0 && typeNameWrong == 0 && notSingleLayer == 0 &&
                missingIndices.Count == 0 && duplicateIndices.Count == 0 &&
                carriers.Count == 1 &&
                extras.Count == 0 &&
                // Either the scan ran and found nothing, or it was skipped because it
                // cannot have an answer. A scan that FAILED is neither, and blocks.
                (mode == ExtrasScan.SkippedByConstruction || report.Value<bool?>("extra_scan_ran") == true) &&
                anchor.Siblings.Count == anchor.ExpectedWallCount &&
                presentUniqueIds.Count == anchor.ExpectedWallCount &&
                expectedIndices.Count == anchor.ExpectedWallCount;

            report["state"] = complete ? WallSplitCodes.AlreadySplit : WallSplitCodes.RepairablePartialState;
            if (!complete && !geometryOk)
                report["state_reason"] = geometryFailure;
            else if (!complete && !geometryMeasured)
                report["state_reason"] =
                    "the layer positions could not be re-derived, and this capability does not report a set as " +
                    "complete on the strength of its stamps alone.";

            return complete ? WallSplitCodes.AlreadySplit : WallSplitCodes.RepairablePartialState;
        }

        /// <summary>
        /// ARE THE LAYERS STILL WHERE THEY BELONG? Measured, not remembered.
        ///
        /// A review found that `already_split` was decided entirely from stamp-vs-stamp and
        /// stamp-vs-TYPE comparisons: present, stamped, same conversion, agreeing lists,
        /// right role, single-layer, right type name and fingerprint. Not one term touched a
        /// wall's position. So a conversion this tool ITSELF reported as failing its
        /// post-commit check - layers 40 mm off, unrollbackable because the transaction had
        /// closed - read back on the very next call as "a completed split ... present and
        /// coherent", with partial_state_walls: 0, and the tool then refused to touch the
        /// one wall it knew was wrong.
        ///
        /// The geometry can be re-derived without remembering the original curve at all,
        /// because everything is relative to the carrier: from the ORIGINAL wall type's
        /// compound structure we know every layer's centre on the `u` axis, and the carrier
        /// IS its own layer's centre (its location line was normalised to WallCenterline).
        /// So sibling `i` must sit at `c_carrier - c_i` from the carrier, along the carrier's
        /// exterior normal. That is a measurement of the model as it stands today.
        /// </summary>
        private static string MeasureLayerPositions(Document doc, Wall carrier, Stamp anchor,
                                                    IEnumerable<KeyValuePair<Wall, Stamp>> siblings,
                                                    out JObject report)
        {
            report = new JObject();

            Element originalType = null;
            try
            {
                originalType = string.IsNullOrEmpty(anchor.OriginalWallTypeId)
                    ? null : doc.GetElement(anchor.OriginalWallTypeId);
            }
            catch { }

            IList<CompoundStructureLayer> layers = null;
            try { layers = (originalType as WallType)?.GetCompoundStructure()?.GetLayers(); } catch { }

            if (layers == null || layers.Count == 0)
            {
                // The original type is gone or unreadable. That is NOT "the geometry is
                // fine": it is "the geometry cannot be checked", and it is reported as such.
                report["measured"] = false;
                report["reason"] = "the original wall type this conversion came from is no longer in the model, " +
                                   "so where each layer belongs cannot be re-derived. The set is reported on its " +
                                   "stamps alone, and this call does not claim its geometry was checked.";
                return null;
            }

            Curve carrierCurve = null;
            try { carrierCurve = (carrier.Location as LocationCurve)?.Curve; } catch { }
            XYZ normal = MeasuredNormal(carrier);

            if (carrierCurve == null || normal == null)
            {
                report["measured"] = false;
                report["reason"] = "the carrier's curve or exterior direction could not be read, so the layers " +
                                   "cannot be measured against it.";
                return null;
            }

            double carrierCentre = CentreOnU(layers, anchor.LayerIndex);
            var rows = new JArray();
            string failure = null;

            foreach (KeyValuePair<Wall, Stamp> pair in siblings)
            {
                if (Rid.Value(pair.Key.Id) == Rid.Value(carrier.Id)) continue;

                double centre = CentreOnU(layers, pair.Value.LayerIndex);

                // u grows towards the interior; the offset is measured along the exterior
                // normal, so the sign flips - exactly as in WallLayerRules.OffsetForLayer.
                double expectedOffset = carrierCentre - centre;

                Curve actual = null;
                try { actual = (pair.Key.Location as LocationCurve)?.Curve; } catch { }

                var row = new JObject
                {
                    ["element_id"] = Rid.Value(pair.Key.Id),
                    ["layer_index"] = pair.Value.LayerIndex,
                    ["expected_offset_from_carrier_mm"] = Math.Round(WallLayerRules.FeetToMm(expectedOffset), 3)
                };

                Curve target = WallSplitExecutor.OffsetCurve(carrierCurve, expectedOffset, normal, ArcSign(carrierCurve, normal));
                double deviation = WallSplitExecutor.Deviation(actual, target);

                row["deviation_mm"] = double.IsNaN(deviation) ? (JToken)JValue.CreateNull() : Math.Round(deviation, 3);
                row["in_place"] = !double.IsNaN(deviation) && deviation <= WallLayerRules.ToleranceMm;
                rows.Add(row);

                if (failure == null && row.Value<bool>("in_place") == false)
                    failure = "layer " + pair.Value.LayerIndex + " (element " + Rid.Value(pair.Key.Id) + ") is " +
                              (double.IsNaN(deviation) ? "at an unmeasurable distance" : deviation.ToString("F2") + " mm") +
                              " from where it belongs relative to the carrier";
            }

            report["measured"] = true;
            report["tolerance_mm"] = WallLayerRules.ToleranceMm;
            report["layers"] = rows;
            report["all_in_place"] = failure == null;
            return failure;
        }

        private static double CentreOnU(IList<CompoundStructureLayer> layers, int index)
        {
            double before = 0.0;
            for (int i = 0; i < index && i < layers.Count; i++) before += layers[i].Width;
            return before + (index >= 0 && index < layers.Count ? layers[index].Width / 2.0 : 0.0);
        }

        private static XYZ MeasuredNormal(Wall wall)
        {
            try
            {
                XYZ orientation = wall.Orientation;
                if (orientation == null || orientation.IsZeroLength()) return null;
                var flat = new XYZ(orientation.X, orientation.Y, 0);
                return flat.GetLength() < 1e-6 ? null : flat.Normalize();
            }
            catch { return null; }
        }

        private static int ArcSign(Curve curve, XYZ normal)
        {
            if (!(curve is Arc arc)) return 0;
            try
            {
                XYZ mid = arc.Evaluate(0.5, true);
                return mid.Subtract(arc.Center).DotProduct(normal) > 0 ? 1 : -1;
            }
            catch { return 1; }
        }

        /// <summary>
        /// The element that carries the conversion this stamp belongs to. A caller who
        /// selected a SECONDARY layer wall must get the same diagnosis as one who selected
        /// the carrier, and that only works if the carrier can be found from any member.
        /// </summary>
        public static Element FindCarrier(Document doc, Element member)
        {
            Stamp stamp = ReadStamp(member);
            if (!stamp.Present) return null;
            if (string.Equals(stamp.Role, LayerRole.CoreCarrier, StringComparison.Ordinal)) return member;

            foreach (string uniqueId in stamp.Siblings)
            {
                Element sibling = null;
                try { sibling = doc.GetElement(uniqueId); } catch { }
                if (sibling == null) continue;
                Stamp siblingStamp = ReadStamp(sibling);
                if (siblingStamp.Present &&
                    string.Equals(siblingStamp.Role, LayerRole.CoreCarrier, StringComparison.Ordinal))
                    return sibling;
            }

            return null;
        }

        // NOTE. There used to be a StateOf(element, plannedFingerprint) here that answered
        // matches_existing_plan / existing_plan_conflict from the CARRIER'S FINGERPRINT
        // ALONE. It is deleted rather than left unused, because a shallow check sitting
        // next to a thorough one is a trap: it cannot tell a finished conversion from one
        // somebody has since deleted three walls out of, and the next person to need this
        // question answered would have called it. InspectSiblingSet is the answer.

    }
}
