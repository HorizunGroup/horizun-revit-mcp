// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// TAGS OF EVERY KIND (horizun_annotate operation=tag, horizun_plan_annotations auto_tags).
//
// The Revit-free half of the tag paths: which tag category labels which model category,
// which tag modes exist (category, multi-category, material, and the three keynote kinds),
// what each mode refuses before anything is created, the multi-reference rules, the leader
// options, and the per-category report of an automatic tag sweep.
//
// MEASURED by reflection over RevitAPI.dll 2023, 2024, 2025, 2026 and 2027 (2026-11-17):
//   * IndependentTag.Create (both overloads), AddReferences, RemoveReferences,
//     GetTaggedReferences, LeaderEndCondition, Get/SetLeaderEnd(Reference, XYZ),
//     Get/SetLeaderElbow(Reference, XYZ), HasLeaderElbow, MultiLeader, TagText: same
//     signatures in all five years (2027 adds LeaderStart, not used here).
//   * SpatialElementTag.LeaderEnd / LeaderElbow / HasLeader are settable in all five.
//   * Document.Create.NewRoomTag(LinkElementId, UV, ElementId) in all five: a room of a
//     LINK is tagged through LinkElementId(link instance, room). NewAreaTag takes an Area and
//     NewSpaceTag a Space object of THIS document: no link form exists.
//   * FilteredElementCollector(Document, viewId, linkId) - the elements of a link visible in a
//     host view - exists from 2024; Revit 2023 has no such constructor.
//   * BuiltInParameter.KEY_SOURCE_PARAM and KEY_VALUE exist in all five; every category name
//     in TagCategoryMap was checked present in all five BuiltInCategory enums.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class TagRules
    {
        public const string ByCategory = "by_category";
        public const string MultiCategory = "multi_category";
        public const string Material = "material";
        public const string KeynoteElement = "keynote_element";
        public const string KeynoteMaterial = "keynote_material";
        public const string KeynoteUser = "keynote_user";

        public static readonly string[] Modes = { ByCategory, MultiCategory, Material, KeynoteElement, KeynoteMaterial, KeynoteUser };

        public const string ExtraIdsField = "element_ids";
        public const string LeaderEndField = "leader_end";
        public const string LeaderElbowField = "leader_elbow";
        public const string KeynoteKeyField = "keynote_key";
        public const int MaxExtraReferences = 50;

        /// <summary>
        /// KEY_SOURCE_PARAM of a keynote tag as this bridge expects it: 0 element, 1 material, 2 user. NOT yet
        /// measured in a live Revit. After the commit the source is read and compared for every keynote mode, so a
        /// different numbering fails that check and the batch rolls back (fail closed, never a false success). The
        /// keynote_user path WRITES 2; re-reading it proves only the write, so that mode is also proven by the tag's
        /// text (KeynoteVerificationFailure).
        /// </summary>
        public const int KeySourceElement = 0, KeySourceMaterial = 1, KeySourceUser = 2;

        /// <summary>tag_mode as sent, case-insensitive like before 5.2 (ToLowerInvariant); absent is by_category.</summary>
        public static string NormalizeMode(string mode) => string.IsNullOrWhiteSpace(mode) ? ByCategory : mode.Trim().ToLowerInvariant();

        public static bool IsKnownMode(string mode) => mode != null && Modes.Contains(mode, StringComparer.Ordinal);
        public static bool IsKeynote(string mode) => mode == KeynoteElement || mode == KeynoteMaterial || mode == KeynoteUser;
        /// <summary>The modes Revit creates through TagMode.TM_ADDBY_MATERIAL (a material reference).</summary>
        public static bool IsMaterialMode(string mode) => mode == Material || mode == KeynoteMaterial;

        /// <summary>The tag category a mode's type must belong to, or null when it depends on the target.</summary>
        public static string RequiredTypeCategory(string mode)
        {
            // A material keynote is a tag of the KEYNOTE category whose instance Key Source is Material (measured, 2025.4).
            if (IsKeynote(mode)) return "OST_KeynoteTags";
            if (mode == Material) return "OST_MaterialTags";
            if (mode == MultiCategory) return "OST_MultiCategoryTags";
            return null;
        }

        // ---- which tag category labels which model category -----------------------------

        private static readonly Dictionary<string, string> TagCategoryMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OST_Doors"] = "OST_DoorTags", ["OST_Windows"] = "OST_WindowTags", ["OST_Walls"] = "OST_WallTags",
            ["OST_Floors"] = "OST_FloorTags", ["OST_Ceilings"] = "OST_CeilingTags", ["OST_Roofs"] = "OST_RoofTags",
            ["OST_Furniture"] = "OST_FurnitureTags", ["OST_FurnitureSystems"] = "OST_FurnitureSystemTags",
            ["OST_Casework"] = "OST_CaseworkTags", ["OST_GenericModel"] = "OST_GenericModelTags",
            ["OST_Columns"] = "OST_ColumnTags", ["OST_StructuralColumns"] = "OST_StructuralColumnTags",
            ["OST_StructuralFraming"] = "OST_StructuralFramingTags", ["OST_StructuralFoundation"] = "OST_StructuralFoundationTags",
            ["OST_Rebar"] = "OST_RebarTags", ["OST_AreaRein"] = "OST_AreaReinTags", ["OST_PathRein"] = "OST_PathReinTags",
            ["OST_FabricAreas"] = "OST_FabricAreaTags", ["OST_FabricReinforcement"] = "OST_FabricReinforcementTags",
            ["OST_StructConnections"] = "OST_StructConnectionTags", ["OST_StructuralStiffener"] = "OST_StructuralStiffenerTags",
            ["OST_Coupler"] = "OST_CouplerTags", ["OST_Stairs"] = "OST_StairsTags", ["OST_StairsRuns"] = "OST_StairsRunTags",
            ["OST_StairsLandings"] = "OST_StairsLandingTags", ["OST_StairsSupports"] = "OST_StairsSupportTags",
            ["OST_StairsRailing"] = "OST_StairsRailingTags", ["OST_Ramps"] = "OST_RampTags",
            ["OST_CurtainWallPanels"] = "OST_CurtainWallPanelTags", ["OST_CurtainWallMullions"] = "OST_CurtainWallMullionTags",
            ["OST_Parking"] = "OST_ParkingTags", ["OST_Planting"] = "OST_PlantingTags", ["OST_Site"] = "OST_SiteTags",
            ["OST_Entourage"] = "OST_EntourageTags", ["OST_Toposolid"] = "OST_ToposolidTags", ["OST_Hardscape"] = "OST_HardscapeTags",
            ["OST_Roads"] = "OST_RoadTags", ["OST_SpecialityEquipment"] = "OST_SpecialityEquipmentTags",
            ["OST_MechanicalEquipment"] = "OST_MechanicalEquipmentTags", ["OST_MechanicalControlDevices"] = "OST_MechanicalControlDeviceTags",
            ["OST_PlumbingFixtures"] = "OST_PlumbingFixtureTags", ["OST_PlumbingEquipment"] = "OST_PlumbingEquipmentTags",
            ["OST_ElectricalEquipment"] = "OST_ElectricalEquipmentTags", ["OST_ElectricalFixtures"] = "OST_ElectricalFixtureTags",
            ["OST_LightingFixtures"] = "OST_LightingFixtureTags", ["OST_LightingDevices"] = "OST_LightingDeviceTags",
            ["OST_DataDevices"] = "OST_DataDeviceTags", ["OST_FireAlarmDevices"] = "OST_FireAlarmDeviceTags",
            ["OST_CommunicationDevices"] = "OST_CommunicationDeviceTags", ["OST_SecurityDevices"] = "OST_SecurityDeviceTags",
            ["OST_NurseCallDevices"] = "OST_NurseCallDeviceTags", ["OST_TelephoneDevices"] = "OST_TelephoneDeviceTags",
            ["OST_AudioVisualDevices"] = "OST_AudioVisualDeviceTags", ["OST_FireProtection"] = "OST_FireProtectionTags",
            ["OST_FoodServiceEquipment"] = "OST_FoodServiceEquipmentTags", ["OST_MedicalEquipment"] = "OST_MedicalEquipmentTags",
            ["OST_Signage"] = "OST_SignageTags", ["OST_TemporaryStructure"] = "OST_TemporaryStructureTags",
            ["OST_VerticalCirculation"] = "OST_VerticalCirculationTags", ["OST_Sprinklers"] = "OST_SprinklerTags",
            ["OST_DuctCurves"] = "OST_DuctTags", ["OST_FlexDuctCurves"] = "OST_FlexDuctTags", ["OST_DuctFitting"] = "OST_DuctFittingTags",
            ["OST_DuctAccessory"] = "OST_DuctAccessoryTags", ["OST_DuctTerminal"] = "OST_DuctTerminalTags",
            ["OST_DuctInsulations"] = "OST_DuctInsulationsTags", ["OST_DuctLinings"] = "OST_DuctLiningsTags",
            ["OST_PipeCurves"] = "OST_PipeTags", ["OST_FlexPipeCurves"] = "OST_FlexPipeTags", ["OST_PipeFitting"] = "OST_PipeFittingTags",
            ["OST_PipeAccessory"] = "OST_PipeAccessoryTags", ["OST_PipeInsulations"] = "OST_PipeInsulationsTags",
            ["OST_CableTray"] = "OST_CableTrayTags", ["OST_CableTrayFitting"] = "OST_CableTrayFittingTags",
            ["OST_Conduit"] = "OST_ConduitTags", ["OST_ConduitFitting"] = "OST_ConduitFittingTags", ["OST_Wire"] = "OST_WireTags",
            ["OST_FabricationDuctwork"] = "OST_FabricationDuctworkTags", ["OST_FabricationPipework"] = "OST_FabricationPipeworkTags",
            ["OST_FabricationHangers"] = "OST_FabricationHangerTags", ["OST_FabricationContainment"] = "OST_FabricationContainmentTags",
            ["OST_DetailComponents"] = "OST_DetailComponentTags", ["OST_Parts"] = "OST_PartTags", ["OST_Assemblies"] = "OST_AssemblyTags",
            ["OST_Mass"] = "OST_MassTags", ["OST_HVAC_Zones"] = "OST_ZoneTags", ["OST_IOSModelGroups"] = "OST_ModelGroupTags",
            ["OST_Rooms"] = "OST_RoomTags", ["OST_Areas"] = "OST_AreaTags", ["OST_MEPSpaces"] = "OST_MEPSpaceTags"
        };

        /// <summary>The tag category (BuiltInCategory name) that labels a model category, or null when none is known.</summary>
        public static string TagCategoryFor(string taggedCategory)
        {
            if (string.IsNullOrEmpty(taggedCategory)) return null;
            string tag;
            return TagCategoryMap.TryGetValue(taggedCategory, out tag) ? tag : null;
        }

        public static IEnumerable<string> MappedCategories => TagCategoryMap.Keys;

        /// <summary>Rooms, areas and spaces take a spatial tag (NewRoomTag / NewAreaTag / NewSpaceTag), never IndependentTag.</summary>
        public static bool IsSpatialCategory(string category) =>
            category == "OST_Rooms" || category == "OST_Areas" || category == "OST_MEPSpaces";

        // ---- refusals before anything is created -----------------------------------------

        /// <summary>
        /// The options of one IndependentTag action (not a room, area or space), refused by name
        /// when they cannot be honoured, so nothing the caller asked for is dropped silently.
        /// </summary>
        public static string OptionRefusal(string mode, bool linked, bool hasKeynoteKey, int extraCount,
                                           bool addLeader, bool hasLeaderEnd, bool hasLeaderElbow)
        {
            if (!IsKnownMode(mode))
                return "tag_mode must be one of " + string.Join(", ", Modes) + "; '" + mode + "' is not a tag mode.";
            if (hasKeynoteKey && mode != KeynoteUser)
                return "keynote_key is the value of a USER keynote; it needs tag_mode=keynote_user (an element or material keynote shows the keynote the element or material already carries).";
            if (!hasKeynoteKey && mode == KeynoteUser)
                return "tag_mode=keynote_user needs keynote_key: the keynote-table key the tag will show.";
            if (linked && IsMaterialMode(mode))
                return "tag_mode=" + mode + " cannot tag an element of a link: Revit creates a material tag only through the " +
                       "TagMode overload of IndependentTag.Create, which answers a link reference with an InternalException " +
                       "(measured live, Revit 2023). Tag the material in the linked model itself, or use by_category / keynote_element.";
            if (extraCount > 0)
            {
                if (linked)
                    return "element_ids (more elements on the same tag) is for elements of THIS document; a multi-reference tag over a link is not proven, so it is refused instead of guessed.";
                if (mode != ByCategory && mode != MultiCategory)
                    return "element_ids (more elements on the same tag) needs tag_mode by_category or multi_category; a " + mode +
                           " tag labels one element or material.";
                if (extraCount > MaxExtraReferences)
                    return "element_ids takes at most " + MaxExtraReferences + " more elements on one tag.";
            }
            if ((hasLeaderEnd || hasLeaderElbow) && !addLeader)
                return "leader_end and leader_elbow shape a leader: send add_leader=true with them.";
            return null;
        }

        /// <summary>The options a room, area or space tag cannot honour (on top of SpatialTagRules).</summary>
        public static string SpatialOptionRefusal(string kindName, int extraCount, bool hasKeynoteKey,
                                                  bool addLeader, bool hasLeaderEnd, bool hasLeaderElbow)
        {
            if (extraCount > 0)
                return "element_ids (more elements on the same tag) is not available for a " + kindName + ": Revit's spatial tags label one room, area or space.";
            if (hasKeynoteKey)
                return "keynote_key belongs to tag_mode=keynote_user; a " + kindName + " takes its own spatial tag.";
            if ((hasLeaderEnd || hasLeaderElbow) && !addLeader)
                return "leader_end and leader_elbow shape a leader: send add_leader=true with them.";
            return null;
        }

        /// <summary>
        /// tag_type_id checked against the mode and the target's category BEFORE creation. Null
        /// when the type fits, or when the target's tag category is not known here (Revit's own
        /// GetValidTypes then proves it during the rehearsal).
        /// </summary>
        public static string TypeCategoryRefusal(string mode, string targetCategory, string typeCategory)
        {
            string required = RequiredTypeCategory(mode);
            if (required != null)
                return string.Equals(typeCategory, required, StringComparison.Ordinal)
                    ? null
                    : "tag_mode=" + mode + " needs a tag_type_id of category " + required + "; the type given is " + (typeCategory ?? "of no category") + ".";
            string expected = TagCategoryFor(targetCategory);
            if (expected == null || typeCategory == null) return null;
            if (string.Equals(typeCategory, expected, StringComparison.Ordinal)) return null;
            if (typeCategory == "OST_MultiCategoryTags") return "tag_type_id is a multi-category tag type: send tag_mode=multi_category with it.";
            if (typeCategory == "OST_MaterialTags") return "tag_type_id is a material tag type: send tag_mode=material with it.";
            if (typeCategory == "OST_KeynoteTags") return "tag_type_id is a keynote tag type: send tag_mode=keynote_element, keynote_material or keynote_user with it.";
            return "tag_type_id is a type of " + typeCategory + ", but a " + targetCategory + " element takes a type of " + expected + ".";
        }

        /// <summary>No type given: the document must offer one for the category (or mode) or nothing can be created.</summary>
        public static string NoLoadedTypeRefusal(string mode, string targetCategory, int loadedTypes)
        {
            if (loadedTypes > 0) return null;
            string category = RequiredTypeCategory(mode) ?? TagCategoryFor(targetCategory);
            if (category == null) return null;
            return "No tag type of " + category + " is loaded in this document, so Revit cannot tag this " + (targetCategory ?? "element") +
                   ". Load an approved tag family (horizun_annotate load_tag_family) or pass tag_type_id.";
        }

        public static string CandidatesText(IList<string> candidates)
            => candidates == null || candidates.Count == 0 ? "none is loaded" : string.Join("; ", candidates);

        /// <summary>
        /// The keynote Key Source of a tag INSTANCE as text (measured, Revit 2025.4: KEY_SOURCE_PARAM is an instance parameter of
        /// STRING storage whose raw value is "Element"; Material and User are the other two) to the 0/1/2 the rules use. Null when
        /// the text is none of them: an unreadable source is never guessed.
        /// </summary>
        public static int? KeySourceFromText(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "element": case "elemento": return KeySourceElement;
                case "material": return KeySourceMaterial;
                case "user": case "usuario": return KeySourceUser;
                default: return null;
            }
        }

        /// <summary>The text written to a string-storage Key Source for 0 element, 1 material, 2 user.</summary>
        public static string KeySourceText(int source)
            => source == KeySourceElement ? "Element" : source == KeySourceMaterial ? "Material" : source == KeySourceUser ? "User" : null;

        /// <summary>A keynote mode with several keynote types loaded and none named is ambiguous.</summary>
        public static string KeynoteTypeChoiceError(int loadedKeynoteTypes)
        {
            if (loadedKeynoteTypes == 1) return null;
            return loadedKeynoteTypes == 0
                ? "No keynote tag type (OST_KeynoteTags) is loaded. Load the Keynote Tag family (horizun_annotate load_tag_family) or pass tag_type_id."
                : loadedKeynoteTypes + " keynote tag types are loaded (number, text, boxed...). Pass tag_type_id to choose one.";
        }

        /// <summary>An element keynote shows the keynote the element (or its type) carries; none means an empty tag.</summary>
        public static string ElementKeynoteRefusal(string instanceKeynote, string typeKeynote, long elementId)
        {
            if (!string.IsNullOrWhiteSpace(instanceKeynote) || !string.IsNullOrWhiteSpace(typeKeynote)) return null;
            return "element " + elementId + " carries no keynote (instance or type), so an element keynote would be empty. " +
                   "Set it first with horizun_set_keynote, or use keynote_user with keynote_key.";
        }

        /// <summary>The key of a user keynote must exist in the project's keynote table.</summary>
        public static string KeynoteKeyRefusal(string key, ICollection<string> tableKeys, string tableProblem)
        {
            if (string.IsNullOrWhiteSpace(key)) return "keynote_key must not be empty.";
            if (tableProblem != null) return "keynote_key cannot be validated: " + tableProblem;
            if (tableKeys == null || !tableKeys.Contains(key))
                return "keynote_key '" + key + "' is not in the project's keynote table, so the tag would read '?'. Use a key of the table " +
                       "(horizun_query_classification) or load the right table (horizun_manage_classification_table).";
            return null;
        }

        /// <summary>
        /// More elements on the same tag (IndependentTag.AddReferences). By category, every one must be
        /// of the main target's category - one category tag cannot label two categories.
        /// </summary>
        public static string ExtraReferencesRefusal(string mode, long mainId, string mainCategory,
                                                    IList<long> extraIds, IList<string> extraCategories)
        {
            if (extraIds == null || extraIds.Count == 0) return null;
            var seen = new HashSet<long>();
            for (int i = 0; i < extraIds.Count; i++)
            {
                if (extraIds[i] == mainId) return "element_ids repeats element_id " + mainId + "; list only the OTHER elements the tag also labels.";
                if (!seen.Add(extraIds[i])) return "element_ids lists " + extraIds[i] + " twice.";
                string cat = extraCategories != null && i < extraCategories.Count ? extraCategories[i] : null;
                if (cat == null) return "element_ids[" + i + "] (" + extraIds[i] + ") has no category and cannot be tagged.";
                if (mode == ByCategory && !string.Equals(cat, mainCategory, StringComparison.Ordinal))
                    return "element_ids[" + i + "] (" + extraIds[i] + ") is " + cat + " but element_id is " + mainCategory +
                           "; a category tag labels one category. Use tag_mode=multi_category or a separate tag.";
                if (IsSpatialCategory(cat)) return "element_ids[" + i + "] (" + extraIds[i] + ") is a room, area or space; those take their own tag.";
            }
            return null;
        }

        /// <summary>The committed tag must label EXACTLY the requested set - no element missing, none extra.</summary>
        public static bool SameTaggedSet(IEnumerable<long> expected, IEnumerable<long> actual)
        {
            if (expected == null || actual == null) return false;
            var a = new HashSet<long>(expected); var b = new HashSet<long>(actual);
            return a.SetEquals(b);
        }

        /// <summary>The kind of a linked target that cannot take operation=tag, with the reason; null when it can.</summary>
        public static string LinkedTargetRefusal(string linkedKind)
        {
            switch (linkedKind)
            {
                case "area":
                    return "An Area of a link cannot be tagged: Revit's NewAreaTag takes an Area of THIS document (no link form exists in 2023-2027). Tag it in the linked model.";
                case "space":
                    return "An MEP Space of a link cannot be tagged: Revit's NewSpaceTag takes a Space of THIS document (no link form exists in 2023-2027). Tag it in the linked model, or create spaces in the host.";
                case "nested_link":
                    return "linked_element_id is itself a link instance (a NESTED link). Revit builds a link reference one level deep only " +
                           "(Reference.CreateLinkReference from a host link instance), so elements of a nested link cannot be tagged from the host. " +
                           "Load the nested model as a direct link, or tag it in its parent model.";
                default: return null;
            }
        }

        /// <summary>The source a keynote mode must read back, or null for a non-keynote mode.</summary>
        public static int? ExpectedKeySource(string mode) =>
            mode == KeynoteElement ? KeySourceElement : mode == KeynoteMaterial ? KeySourceMaterial : mode == KeynoteUser ? KeySourceUser : (int?)null;

        /// <summary>
        /// Why a committed keynote tag does not prove what was asked, or null when it does. Every keynote mode reads
        /// back its expected KEY_SOURCE_PARAM. A user keynote must also read back its key AND show, in its own text,
        /// the key or the table's text for it (or both); a material keynote the same against the KEYNOTE_PARAM of the
        /// material of the tagged face (askedKey). An unknown expected key or an unreadable text is a failure (fail closed).
        /// </summary>
        public static string KeynoteVerificationFailure(string mode, int? source, string key, string askedKey, string tagText, string tableText)
        {
            int? want = ExpectedKeySource(mode);
            if (want == null) return null;
            if (source != want)
                return "tag_mode=" + mode + " expects key source " + want + " but the committed keynote tag reads " +
                       (source?.ToString() ?? "an unreadable key source") + ".";
            if (mode != KeynoteUser && mode != KeynoteMaterial) return null;
            string label = mode == KeynoteUser ? "user keynote" : "material keynote";
            if (string.IsNullOrWhiteSpace(askedKey))
                return "the " + label + " it should show is unknown, so the committed tag is unproven (fail closed).";
            if (!string.Equals(key?.Trim(), askedKey.Trim(), StringComparison.Ordinal))
                return "the committed " + label + " reads key '" + (key ?? "") + "', expected '" + askedKey + "'.";
            if (tagText == null)
                return "the " + label + "'s text could not be read, so what it shows is unproven (fail closed).";
            string shown = tagText.Trim();
            string asked = (askedKey ?? "").Trim();
            string table = tableText?.Trim();
            bool ok = (asked.Length > 0 && shown == asked)
                      || (!string.IsNullOrEmpty(table) && shown == table)
                      || (!string.IsNullOrEmpty(table) && asked.Length > 0 && shown.Contains(asked) && shown.Contains(table));
            return ok ? null
                : "the " + label + " shows '" + tagText + "', which is neither its key '" + askedKey + "' nor the table's text '" + (tableText ?? "") + "'.";
        }

        /// <summary>Distance between two points measured in the view plane (right/up components only).</summary>
        public static double PlanarDistance(double[] a, double[] b, double[] right, double[] up)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            double u = dx * right[0] + dy * right[1] + dz * right[2];
            double v = dx * up[0] + dy * up[1] + dz * up[2];
            return Math.Sqrt(u * u + v * v);
        }

        /// <summary>Leader end and elbow re-read tolerance: 0.5 mm, in feet.</summary>
        public const double LeaderToleranceFeet = 0.5 / 304.8;

        /// <summary>
        /// Does a tag's box [minR,minU,maxR,maxU] hold its head, both in the view frame measured FROM THE VIEW ORIGIN
        /// (the division by the scale is about that origin)? "contains_head" as read; "contains_head_divided_by_view_scale"
        /// when only the box divided by the view scale does (the scale trap measured on text notes); "head_outside" when
        /// neither. EVIDENCE ONLY: it says how far to trust the extent, it never decides whether the tag verified.
        /// </summary>
        public static string ExtentHeadCheck(double[] box, double[] head, int scale, double tolerance)
        {
            if (box == null || head == null || box.Length != 4 || head.Length != 2) return "unchecked";
            if (Holds(box, head, tolerance)) return "contains_head";
            if (scale > 1 && Holds(box.Select(x => x / scale).ToArray(), head, tolerance)) return "contains_head_divided_by_view_scale";
            return "head_outside";
        }

        private static bool Holds(double[] b, double[] h, double tol) =>
            h[0] >= b[0] - tol && h[0] <= b[2] + tol && h[1] >= b[1] - tol && h[1] <= b[3] + tol;

        /// <summary>
        /// The tag type taken when none is named (spatial tags, a multi-category tag over a link): the single loaded type, else the document's own
        /// default for the tag category when it is one of the loaded types; null when it stays ambiguous.
        /// </summary>
        public static long? DefaultTagTypeId(IList<long> loadedTypeIds, long? documentDefault)
        {
            if (loadedTypeIds == null || loadedTypeIds.Count == 0) return null;
            if (loadedTypeIds.Count == 1) return loadedTypeIds[0];
            if (documentDefault.HasValue && loadedTypeIds.Contains(documentDefault.Value)) return documentDefault.Value;
            return null;
        }

        /// <summary>
        /// The warning for a tag whose text was read and is empty (it prints blank); null when the text has content
        /// or could not be read (a tag on a linked element cannot be read inside the rehearsal: that is not "empty").
        /// </summary>
        public static string EmptyTagTextWarning(string text, bool readable, bool required)
        {
            if (!readable || !string.IsNullOrWhiteSpace(text)) return null;
            return "tag_text_empty: the tag reads nothing and will print blank - its family's label shows a parameter " +
                   "this element leaves empty, or the family has no label for it. Fill the parameter or pick a tag type " +
                   "that shows a filled one" + (required ? "; require_tag_text refuses it." : "; send require_tag_text=true to refuse it instead.");
        }

        /// <summary>The refusal when several tag types are loaded and the document names no default among them.</summary>
        public static string AmbiguousTagTypeRefusal(string kindName, IEnumerable<string> candidates)
        {
            var list = (candidates ?? Enumerable.Empty<string>()).ToList();
            return kindName + " needs one tag type: " + list.Count + " are loaded and the document has no default among them. " +
                   "Pass tag_type_id with one of: " + string.Join("; ", list.Take(10)) + (list.Count > 10 ? "; ..." : "") + ".";
        }
    }

    /// <summary>
    /// The per-category account of an automatic tag sweep: what was planned, what was already
    /// tagged, and what cannot be tagged and why - so a sweep is never reported as complete when
    /// part of the view was left out.
    /// </summary>
    public sealed class AutoTagReport
    {
        public const string Planned = "planned";
        public const string AlreadyTagged = "already_tagged";

        public const string NoTagCategory = "no_tag_category_known";
        public const string NoTagTypeLoaded = "no_tag_type_loaded";
        public const string NoBoundingBox = "no_bounding_box";
        public const string NoFreePoint = "no_collision_free_point";
        public const string UnplacedSpatial = "unplaced_room_area_or_space";
        public const string LinkedAreaOrSpace = "linked_area_or_space";
        public const string NestedLink = "nested_link";
        public const string LinkedMaterial = "linked_material";
        public const string LinkNotLoaded = "link_not_loaded";
        public const string LinksNeedRevit2024 = "links_need_revit_2024";
        public const string OverLimit = "over_limit";
        public const string NotInCategories = "not_in_categories";
        public const string AmbiguousTagType = "ambiguous_tag_type";

        private sealed class Row
        {
            public int Visible, Planned, AlreadyTagged, InGroups, FromLinks;
            public readonly SortedDictionary<string, List<string>> NotTaggable = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        }

        private readonly SortedDictionary<string, Row> rows = new SortedDictionary<string, Row>(StringComparer.Ordinal);
        private const int SampleLimit = 5;

        private Row For(string category)
        {
            string key = string.IsNullOrEmpty(category) ? "(no category)" : category;
            Row r;
            if (!rows.TryGetValue(key, out r)) { r = new Row(); rows[key] = r; }
            return r;
        }

        /// <summary>One element seen by the sweep. outcome is Planned, AlreadyTagged or a not-taggable reason code.</summary>
        public void Add(string category, string outcome, string elementKey, bool inGroup, bool fromLink)
        {
            Row r = For(category);
            r.Visible++;
            if (inGroup) r.InGroups++;
            if (fromLink) r.FromLinks++;
            if (outcome == Planned) r.Planned++;
            else if (outcome == AlreadyTagged) r.AlreadyTagged++;
            else
            {
                List<string> samples;
                if (!r.NotTaggable.TryGetValue(outcome, out samples)) { samples = new List<string>(); r.NotTaggable[outcome] = samples; }
                samples.Add(elementKey);
            }
        }

        public int PlannedCount => rows.Values.Sum(r => r.Planned);
        public int AlreadyTaggedCount => rows.Values.Sum(r => r.AlreadyTagged);
        public int NotTaggableCount => rows.Values.Sum(r => r.NotTaggable.Values.Sum(s => s.Count));

        /// <summary>True when nothing visible was left out for a reason other than "already tagged" or an explicit category filter.</summary>
        public bool Complete => rows.Values.All(r => r.NotTaggable.Keys.All(k => k == NotInCategories));

        public static string Explain(string code)
        {
            switch (code)
            {
                case NoTagCategory: return "Revit has no tag category this planner knows for the category; name it in categories to try it anyway (the annotate rehearsal decides).";
                case NoTagTypeLoaded: return "no tag type of the category's tag category is loaded; load one with horizun_annotate load_tag_family.";
                case NoBoundingBox: return "the element has no bounding box in this view.";
                case NoFreePoint: return "no collision-free head point was found within the bounded search.";
                case UnplacedSpatial: return "the room, area or space is not placed (no location point).";
                case LinkedAreaOrSpace: return "Revit tags areas and spaces of this document only (NewAreaTag/NewSpaceTag have no link form).";
                case LinkedMaterial: return "a material tag on an element of a link: Revit throws InternalException creating it (measured); tag it in the linked model.";
                case NestedLink: return "an element of a nested link: Revit builds link references one level deep only.";
                case LinkNotLoaded: return "the link is not loaded.";
                case LinksNeedRevit2024: return "listing the link elements visible in a host view needs FilteredElementCollector(doc, view, link), Revit 2024+.";
                case OverLimit: return "the sweep plans at most 500 tags per call; run it again (skip_existing) for the rest.";
                case NotInCategories: return "left out by the categories filter.";
                case AmbiguousTagType: return "several area/space tag types are loaded and none is the document default; pass tag_type_id.";
                default: return code;
            }
        }

        public JObject ToJson()
        {
            var byCategory = new JObject();
            foreach (var kv in rows)
            {
                var not = new JObject();
                foreach (var reason in kv.Value.NotTaggable)
                    not[reason.Key] = new JObject
                    {
                        ["count"] = reason.Value.Count,
                        ["why"] = Explain(reason.Key),
                        ["sample"] = new JArray(reason.Value.Take(SampleLimit))
                    };
                byCategory[kv.Key] = new JObject
                {
                    ["visible"] = kv.Value.Visible,
                    ["planned"] = kv.Value.Planned,
                    ["already_tagged"] = kv.Value.AlreadyTagged,
                    ["not_taggable"] = kv.Value.NotTaggable.Values.Sum(s => s.Count),
                    ["in_groups"] = kv.Value.InGroups,
                    ["from_links"] = kv.Value.FromLinks,
                    ["not_taggable_by_reason"] = not
                };
            }
            return byCategory;
        }
    }
}
