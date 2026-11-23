// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// TAG KINDS for horizun_annotate operation=tag (rules: Core/TagRules.cs).
//
// On top of the category, room, area, space and link paths:
//   * tag_mode keynote_element / keynote_material / keynote_user - IndependentTag with a type of
//     OST_KeynoteTags; the user keynote writes KEY_SOURCE_PARAM and KEY_VALUE and re-reads both;
//   * the type checked against the target's category BEFORE anything is created, and a category
//     with no loaded tag type refused naming load_tag_family;
//   * element_ids: more elements on the same tag (IndependentTag.AddReferences), re-read as the
//     exact set of tagged ids;
//   * leader_end (a FREE leader end) and leader_elbow, set through the tag's own reference
//     (SetLeaderEnd / SetLeaderElbow) or the spatial tag's LeaderEnd / LeaderElbow, re-read in the
//     view plane within 0.5 mm;
//   * a ROOM of a link (NewRoomTag over LinkElementId); an area, a space or a nested link is
//     refused with the API reason.
// Every API used here has the same signature in RevitAPI.dll 2023-2027 (measured, TagRules).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        /// <summary>The room a tag action names: a room of this document, or a room of a link.</summary>
        private static Room TaggedRoom(Plan p) => p.LinkedElement as Room ?? p.Target as Room;

        private static string LinkedKind(Element linked)
        {
            if (linked is Room) return "room";
            if (linked is Area) return "area";
            if (linked is Space) return "space";
            if (linked is RevitLinkInstance) return "nested_link";
            return null;
        }

        private static bool RoomTagPointsAt(RoomTag native, Plan p, Room room)
        {
            if (p.LinkInstance == null) return native.TaggedLocalRoomId == room.Id;
            LinkElementId bound = native.TaggedRoomId;
            return bound != null && bound.LinkInstanceId == p.LinkInstance.Id && bound.LinkedElementId == room.Id;
        }

        /// <summary>
        /// Is the plan point (host coordinates) inside the room? Asked at RoomTagRules.ProbeZFeet (the room's own base plus half its
        /// height, at most a foot), carried into the link's coordinates for a room of a link. Null - Revit decides - when the room
        /// is not placed (no location, no area), has no level or no readable height; never "outside" for a room that cannot answer.
        /// </summary>
        private static bool? RoomContainsHostPoint(Room room, RevitLinkInstance link, XYZ hostPoint)
        {
            try
            {
                if (room == null || room.Location == null || room.Area <= 0 || room.Level == null) return null;
                double? upper = null;
                try { if (room.UpperLimit != null) upper = room.UpperLimit.ProjectElevation; } catch { upper = null; }
                double? z = RoomTagRules.ProbeZFeet(room.Level.ProjectElevation, room.BaseOffset, upper, room.LimitOffset, room.UnboundedHeight);
                if (!z.HasValue) return null;
                XYZ local = link == null ? hostPoint : link.GetTotalTransform().Inverse.OfPoint(hostPoint);
                return room.IsPointInRoom(new XYZ(local.X, local.Y, z.Value));
            }
            catch { return null; }
        }

        private static string CategoryName(Category c)
        {
            if (c == null) return null;
            try { BuiltInCategory b = c.BuiltInCategory; return b == BuiltInCategory.INVALID ? null : b.ToString(); }
            catch { return null; }
        }

        private static List<long> ReadExtraIds(JToken token)
        {
            var ids = new List<long>();
            if (token == null) return ids;
            JArray list = token as JArray;
            if (list == null || list.Count == 0 || list.Any(t => t.Type != JTokenType.Integer))
                throw new ArgumentException("element_ids (more elements on the same tag) must be a non-empty array of integer ids.");
            foreach (JToken t in list) ids.Add(t.Value<long>());
            return ids;
        }

        /// <summary>The options every tag action shares: leader shape, more references, keynote key - refused by name when they cannot apply.</summary>
        private static void PlanTagOptions(Document doc, Plan p, JObject a, double scale)
        {
            bool addLeader = a.Value<bool?>("add_leader") == true;
            bool hasEnd = a[TagRules.LeaderEndField] != null, hasElbow = a[TagRules.LeaderElbowField] != null;
            bool hasKey = a[TagRules.KeynoteKeyField] != null;
            List<long> extraIds = ReadExtraIds(a[TagRules.ExtraIdsField]);
            if (hasEnd) p.LeaderEnd = Point(a[TagRules.LeaderEndField], scale, false);
            if (hasElbow) p.LeaderElbow = Point(a[TagRules.LeaderElbowField], scale, false);

            Room room = TaggedRoom(p);
            string spatial = SpatialKind(p.Target);
            if (room != null || spatial != null)
            {
                string refusal = TagRules.SpatialOptionRefusal(room != null ? "RoomTag" : SpatialTagRules.NativeName(spatial),
                    extraIds.Count, hasKey, addLeader, hasEnd, hasElbow);
                if (refusal != null) throw new ArgumentException(refusal);
                return;
            }
            string mode = TagRules.NormalizeMode(a.Value<string>("tag_mode"));
            string optionRefusal = TagRules.OptionRefusal(mode, p.LinkedElement != null, hasKey, extraIds.Count, addLeader, hasEnd, hasElbow);
            if (optionRefusal != null) throw new ArgumentException(optionRefusal);
            p.Mode = mode;
            if (hasKey) p.KeynoteKey = a.Value<string>(TagRules.KeynoteKeyField);
            if (extraIds.Count > 0)
            {
                var extras = new List<Element>(); var categories = new List<string>();
                for (int i = 0; i < extraIds.Count; i++)
                {
                    Element x = Rid.CanRepresent(extraIds[i]) ? doc.GetElement(Rid.Make(extraIds[i])) : null;
                    if (x == null || x is ElementType)
                        throw new ArgumentException("element_ids[" + i + "] (" + extraIds[i] + ") is not an element of this document.");
                    extras.Add(x); categories.Add(CategoryName(x.Category));
                }
                string extraRefusal = TagRules.ExtraReferencesRefusal(mode, Rid.Value(p.Target.Id), CategoryName(p.Target.Category), extraIds, categories);
                if (extraRefusal != null) throw new ArgumentException(extraRefusal);
                p.ExtraTargets = extras;
            }
        }

        /// <summary>An IndependentTag action: the type against the mode and the category, the keynote preconditions.</summary>
        private static void PlanIndependentTag(Document doc, Plan p, JObject a)
        {
            Element subject = p.LinkedElement ?? p.Target;
            string targetCategory = CategoryName(subject.Category);
            string mode = p.Mode ?? TagRules.ByCategory;
            if (p.Type != null)
            {
                string typeRefusal = TagRules.TypeCategoryRefusal(mode, targetCategory, CategoryToken(p.Type));
                if (typeRefusal != null) throw new ArgumentException(typeRefusal);
            }
            else if (TagRules.IsKeynote(mode))
            {
                // The single keynote tag type, else the document's default when it is loaded; only then a refusal.
                string ambiguous;
                if (mode == TagRules.KeynoteMaterial)
                {
                    // Key Source belongs to the INSTANCE (one keynote type serves Element, Material and User), so no type is
                    // "the material one": with several loaded the caller names it.
                    List<ElementType> loaded = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_KeynoteTags)
                        .WhereElementIsElementType().OfType<ElementType>().OrderBy(t => Rid.Value(t.Id)).ToList();
                    if (loaded.Count > 1)
                        throw new ArgumentException(TagRules.AmbiguousTagTypeRefusal("A keynote tag", loaded.Select(t =>
                            Rid.Value(t.Id) + " " + ((t as FamilySymbol)?.FamilyName ?? "?") + ": " + t.Name)));
                }
                ElementType keynoteType = DefaultTagType(doc, BuiltInCategory.OST_KeynoteTags, "A keynote tag", out ambiguous);
                if (keynoteType == null) throw new ArgumentException(ambiguous ?? TagRules.KeynoteTypeChoiceError(0));
                p.Type = keynoteType; p.TypeDefaulted = true;
            }
            else if (mode == TagRules.MultiCategory && p.LinkedElement != null)
            {
                // The tag-type overload is the only one Revit accepts over a link, so the
                // multi-category type is chosen here: the document default, or the single loaded one.
                // Field (2025): a linked column with multi_category was refused for having no
                // Structural Column tag in the host - the multi-category type is what it needs.
                string ambiguous;
                ElementType pick = DefaultTagType(doc, BuiltInCategory.OST_MultiCategoryTags, "A multi-category tag", out ambiguous);
                if (pick == null)
                    throw new ArgumentException(ambiguous ??
                        "No multi-category tag type is loaded in the host. Load one (horizun_annotate load_tag_family) or pass tag_type_id.");
                p.Type = pick; p.TypeDefaulted = true;
            }
            else if (p.LinkedElement == null)
            {
                // A category (or mode) whose tag category is known must have a loaded type, or
                // IndependentTag.Create fails with a generic message deep in the rehearsal.
                string tagCategory = mode == TagRules.ByCategory ? TagRules.TagCategoryFor(targetCategory) : TagRules.RequiredTypeCategory(mode);
                BuiltInCategory bic;
                if (tagCategory != null && Enum.TryParse(tagCategory, out bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
                {
                    int loaded = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType().GetElementCount();
                    string none = TagRules.NoLoadedTypeRefusal(mode, targetCategory, loaded);
                    if (none != null) throw new ArgumentException(none);
                }
            }

            if (mode == TagRules.KeynoteElement)
            {
                string instance = null, type = null;
                try { instance = subject.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString(); } catch { instance = null; }
                try { type = subject.Document.GetElement(subject.GetTypeId())?.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString(); } catch { type = null; }
                string empty = TagRules.ElementKeynoteRefusal(instance, type, Rid.Value(subject.Id));
                if (empty != null) throw new ArgumentException(empty);
            }
            if (mode == TagRules.KeynoteMaterial && p.LinkedElement == null)
            {
                // Key Source is READ-ONLY on the instance (measured, 2025.4): Revit makes a keynote a material keynote only when
                // it is placed on a FACE whose material it then reads. So the plan picks that face here - one carrying a material
                // with a keynote - and the verification demands the tag read and show exactly that material's keynote.
                string why;
                if (!FindMaterialKeynoteFace(p.Target, p.View, p, out why)) throw new ArgumentException(why);
                string problem;
                Dictionary<string, string> keys = ReadKeynoteKeys(doc, out problem);
                p.MaterialKeynoteTable = keys;
                string text;
                if (keys != null && keys.TryGetValue(p.MaterialKeynote, out text)) p.KeynoteText = text;
            }
            if (mode == TagRules.KeynoteUser)
            {
                string problem;
                Dictionary<string, string> keys = ReadKeynoteKeys(doc, out problem);
                string keyRefusal = TagRules.KeynoteKeyRefusal(p.KeynoteKey, keys?.Keys, problem);
                if (keyRefusal != null) throw new ArgumentException(keyRefusal);
                p.KeynoteText = keys[p.KeynoteKey];
            }
        }

        /// <summary>
        /// keynote_material: a face of <paramref name="el"/> with a stable reference whose material carries a keynote
        /// (Material KEYNOTE_PARAM). Faces turned towards the viewer win, then the larger area. False with the reason when
        /// no face qualifies: no geometry in the view, no face reference, or no material of it has a keynote.
        /// </summary>
        private static bool FindMaterialKeynoteFace(Element el, View view, Plan p, out string why)
        {
            why = null;
            Document doc = el.Document;
            XYZ toViewer = null;
            try { toViewer = view.ViewDirection; } catch { toViewer = null; }
            var faces = new List<Face>();
            GeometryElement geo = null;
            try { geo = el.get_Geometry(new Options { View = view, ComputeReferences = true }); } catch { geo = null; }
            CollectFaces(geo, faces, 0);
            var materialsSeen = new SortedSet<string>(StringComparer.Ordinal);
            var candidates = new List<Tuple<double, Face, string, ElementId>>();
            foreach (Face f in faces)
            {
                if (f.Reference == null || f.MaterialElementId == null || f.MaterialElementId == ElementId.InvalidElementId) continue;
                Material m = doc.GetElement(f.MaterialElementId) as Material;
                if (m == null) continue;
                string key = null;
                try { key = m.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString(); } catch { key = null; }
                materialsSeen.Add(Rid.Value(m.Id) + " " + m.Name + (string.IsNullOrWhiteSpace(key) ? " (no keynote)" : " (" + key.Trim() + ")"));
                if (string.IsNullOrWhiteSpace(key)) continue;
                double facing = 0;
                PlanarFace pf = f as PlanarFace;
                if (pf != null && toViewer != null) facing = Math.Abs(pf.FaceNormal.DotProduct(toViewer));
                // Faces seen edge-on (cut in a plan or a section) first, then faces turned to the viewer, larger first:
                // creation tries them in this order and keeps the first one Revit accepts as taggable.
                double score = (facing < 0.1 ? 2e6 : facing > 0.5 ? 1e6 : 0) + f.Area;
                candidates.Add(Tuple.Create(score, f, key.Trim(), m.Id));
            }
            if (candidates.Count == 0)
            {
                why = faces.Count == 0
                    ? "tag_mode=keynote_material needs a face of element " + Rid.Value(el.Id) + " in view " + Rid.Value(view.Id) +
                      ", and its geometry there has none with a reference. Revit sets a material keynote only on a face (Key Source is read-only on the tag)."
                    : "tag_mode=keynote_material: no material of element " + Rid.Value(el.Id) + " seen in view " + Rid.Value(view.Id) +
                      " carries a keynote (" + (materialsSeen.Count == 0 ? "no face has a material" : string.Join("; ", materialsSeen.Take(8))) +
                      "). Give the material a keynote (horizun_set_keynote) or use keynote_element / keynote_user.";
                return false;
            }
            p.MaterialFaces = candidates.OrderByDescending(c => c.Item1).Take(24)
                .Select(c => Tuple.Create(c.Item2.Reference, c.Item4, c.Item3)).ToList();
            p.MaterialFaceRef = p.MaterialFaces[0].Item1;
            p.MaterialId = p.MaterialFaces[0].Item2;
            p.MaterialKeynote = p.MaterialFaces[0].Item3;
            return true;
        }

        private static void CollectFaces(GeometryElement geo, List<Face> faces, int depth)
        {
            if (geo == null || depth > 3) return;
            foreach (GeometryObject o in geo)
            {
                Solid solid = o as Solid;
                if (solid != null && solid.Faces.Size > 0) { foreach (Face f in solid.Faces) faces.Add(f); continue; }
                GeometryInstance gi = o as GeometryInstance;
                if (gi != null)
                {
                    GeometryElement inner = null;
                    try { inner = gi.GetInstanceGeometry(); } catch { inner = null; }
                    CollectFaces(inner, faces, depth + 1);
                }
            }
        }

        /// <summary>The keynote table's keys with their text (KeynoteEntry.KeynoteText; null when an entry has none).</summary>
        private static Dictionary<string, string> ReadKeynoteKeys(Document doc, out string problem)
        {
            problem = null;
            try
            {
                KeynoteTable table = KeynoteTable.GetKeynoteTable(doc);
                if (table == null) { problem = "this project has no keynote table."; return null; }
                if (table.IsExternalFileReference())
                {
                    LinkedFileStatus status = table.GetExternalFileReference().GetLinkedFileStatus();
                    if (status != LinkedFileStatus.Loaded)
                    { problem = "the project's keynote table is " + status + ", not Loaded (horizun_manage_classification_table reload)."; return null; }
                }
                var keys = new Dictionary<string, string>(StringComparer.Ordinal);
                KeyBasedTreeEntries entries = table.GetKeyBasedTreeEntries();
                if (entries != null)
                    foreach (KeyBasedTreeEntry e in entries)
                        if (e != null && !string.IsNullOrEmpty(e.Key)) keys[e.Key] = (e as KeynoteEntry)?.KeynoteText;
                if (keys.Count == 0) { problem = "the project's keynote table has no entries."; return null; }
                return keys;
            }
            catch (Exception ex) { problem = "the keynote table could not be read: " + ex.Message; return null; }
        }

        /// <summary>The tag's own reference to its main target, as Revit stores it on the tag.</summary>
        private static Reference MainReference(Plan p, IndependentTag tag)
        {
            foreach (Reference r in tag.GetTaggedReferences())
            {
                if (r == null) continue;
                if (p.LinkedElement != null)
                {
                    if (r.ElementId == p.LinkInstance.Id && r.LinkedElementId == p.LinkedElement.Id) return r;
                }
                else if (r.ElementId == p.Target.Id) return r;
            }
            return null;
        }

        /// <summary>After creation and retyping: more references, and the user keynote's source and key.</summary>
        private static void ApplyTagExtras(Document doc, Plan p, IndependentTag tag)
        {
            if (p.ExtraTargets != null && p.ExtraTargets.Count > 0)
                tag.AddReferences(p.ExtraTargets.Select(x => new Reference(x)).ToList());
            if (p.Mode == TagRules.KeynoteUser || p.Mode == TagRules.KeynoteMaterial)
            {
                // Key Source is an INSTANCE parameter (string storage "Element"/"Material"/"User" in Revit 2025.4, integer elsewhere):
                // the tag is created as an element keynote and its source is set here, then re-read; one that cannot be set to what
                // the mode needs makes the row fail and roll back - never a keynote that reads Element called a material keynote.
                int want = p.Mode == TagRules.KeynoteUser ? TagRules.KeySourceUser : TagRules.KeySourceMaterial;
                WriteKeySource(tag, want, p.Mode);
            }
            if (p.Mode == TagRules.KeynoteUser)
            {
                Parameter value = tag.get_Parameter(BuiltInParameter.KEY_VALUE);
                if (value == null || value.IsReadOnly || value.StorageType != StorageType.String)
                    throw new InvalidOperationException("the keynote tag has no writable Key Value (KEY_VALUE), so the user keynote '" + p.KeynoteKey + "' cannot be written.");
                value.Set(p.KeynoteKey);
            }
        }

        /// <summary>Sets KEY_SOURCE_PARAM (integer or string storage) to <paramref name="want"/> when it is not already, and re-reads it.</summary>
        private static void WriteKeySource(IndependentTag tag, int want, string mode)
        {
            if (ReadKeySource(tag) == want) return;
            if (want == TagRules.KeySourceMaterial)
                throw new InvalidOperationException("Revit made the keynote tag on the material face read Key Source " + (ReadKeySourceText(tag) ?? "nothing") +
                    ", not Material, and Key Source is read-only on the tag, so tag_mode=keynote_material cannot be built here. Nothing was kept.");
            Parameter source = tag.get_Parameter(BuiltInParameter.KEY_SOURCE_PARAM);
            if (source == null || source.IsReadOnly || (source.StorageType != StorageType.Integer && source.StorageType != StorageType.String))
                throw new InvalidOperationException("the keynote tag's Key Source (KEY_SOURCE_PARAM) is missing or read-only, so tag_mode=" + mode + " cannot be set on it. Nothing was kept.");
            bool set = source.StorageType == StorageType.Integer ? source.Set(want) : source.Set(TagRules.KeySourceText(want));
            if (!set || ReadKeySource(tag) != want)
                throw new InvalidOperationException("the keynote tag's Key Source did not take " + TagRules.KeySourceText(want) + " (it reads " +
                    (ReadKeySourceText(tag) ?? "nothing") + "), so tag_mode=" + mode + " cannot be built on this type. Nothing was kept.");
        }

        /// <summary>The Key Source of a tag instance as 0 element / 1 material / 2 user, from integer or string storage; null when unreadable.</summary>
        private static int? ReadKeySource(Element tag)
        {
            try
            {
                Parameter x = tag.get_Parameter(BuiltInParameter.KEY_SOURCE_PARAM);
                if (x == null || !x.HasValue) return null;
                if (x.StorageType == StorageType.Integer) return x.AsInteger();
                if (x.StorageType == StorageType.String) return TagRules.KeySourceFromText(x.AsString()) ?? TagRules.KeySourceFromText(x.AsValueString());
                return null;
            }
            catch { return null; }
        }

        private static string ReadKeySourceText(Element tag)
        {
            try
            {
                Parameter x = tag.get_Parameter(BuiltInParameter.KEY_SOURCE_PARAM);
                return x == null ? null : (x.StorageType == StorageType.Integer ? x.AsInteger().ToString(CultureInfo.InvariantCulture) : x.AsString());
            }
            catch { return null; }
        }

        /// <summary>A free leader end and a leader elbow on the main reference, before any layout move of the head.</summary>
        private static void ApplyTagLeader(Plan p, IndependentTag tag)
        {
            if (p.LeaderEnd == null && p.LeaderElbow == null) return;
            Reference main = MainReference(p, tag)
                ?? throw new InvalidOperationException("the tag's reference to its target was not found among GetTaggedReferences(), so its leader cannot be shaped.");
            if (p.LeaderEnd != null)
            {
                if (!tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free))
                    throw new InvalidOperationException("Revit does not allow a free leader end on this tag (CanLeaderEndConditionBeAssigned(Free) is false).");
                tag.LeaderEndCondition = LeaderEndCondition.Free;
                tag.SetLeaderEnd(main, p.LeaderEnd);
            }
            if (p.LeaderElbow != null) tag.SetLeaderElbow(main, p.LeaderElbow);
        }

        private static void ApplySpatialLeader(SpatialElementTag tag, Plan p)
        {
            if (p.LeaderEnd != null) tag.LeaderEnd = new XYZ(p.LeaderEnd.X, p.LeaderEnd.Y, tag.LeaderEnd?.Z ?? p.LeaderEnd.Z);
            if (p.LeaderElbow != null) tag.LeaderElbow = new XYZ(p.LeaderElbow.X, p.LeaderElbow.Y, tag.LeaderElbow?.Z ?? p.LeaderElbow.Z);
        }

        private static double PlanarGap(View view, XYZ read, XYZ wanted) =>
            TagRules.PlanarDistance(Arr(read), Arr(wanted), Arr(view.RightDirection), Arr(view.UpDirection));

        private static string FeetText(double feet) => (feet * 304.8).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm";

        private static bool VerifySpatialLeader(SpatialElementTag tag, Plan p, out string reason)
        {
            reason = null;
            if (p.LeaderEnd != null)
            {
                double gap = PlanarGap(p.View, tag.LeaderEnd, p.LeaderEnd);
                if (gap > TagRules.LeaderToleranceFeet) { reason = "the committed leader end is " + FeetText(gap) + " from leader_end"; return false; }
            }
            if (p.LeaderElbow != null)
            {
                if (!tag.HasElbow) { reason = "the committed tag has no leader elbow although leader_elbow was asked"; return false; }
                double gap = PlanarGap(p.View, tag.LeaderElbow, p.LeaderElbow);
                if (gap > TagRules.LeaderToleranceFeet) { reason = "the committed leader elbow is " + FeetText(gap) + " from leader_elbow"; return false; }
            }
            return true;
        }

        private static bool VerifyTagLeader(Plan p, IndependentTag tag, out string reason)
        {
            reason = null;
            if (p.LeaderEnd == null && p.LeaderElbow == null) return true;
            Reference main = MainReference(p, tag);
            if (main == null) { reason = "the committed tag no longer carries its reference to the target, so its leader cannot be re-read"; return false; }
            if (p.LeaderEnd != null)
            {
                if (tag.LeaderEndCondition != LeaderEndCondition.Free) { reason = "the committed leader end is " + tag.LeaderEndCondition + ", not Free"; return false; }
                double gap = PlanarGap(p.View, tag.GetLeaderEnd(main), p.LeaderEnd);
                if (gap > TagRules.LeaderToleranceFeet) { reason = "the committed leader end is " + FeetText(gap) + " from leader_end"; return false; }
            }
            if (p.LeaderElbow != null)
            {
                if (!tag.HasLeaderElbow(main)) { reason = "the committed tag has no leader elbow although leader_elbow was asked"; return false; }
                double gap = PlanarGap(p.View, tag.GetLeaderElbow(main), p.LeaderElbow);
                if (gap > TagRules.LeaderToleranceFeet) { reason = "the committed leader elbow is " + FeetText(gap) + " from leader_elbow"; return false; }
            }
            return true;
        }

        private static string ReadString(Element e, BuiltInParameter bip)
        {
            try { return e.get_Parameter(bip)?.AsString(); } catch { return null; }
        }

        /// <summary>The mode, the references and the keynote of the committed tag, against what the plan asked.</summary>
        private static bool VerifyTagKind(Plan p, IndependentTag tag, out string reason)
        {
            reason = null;
            string mode = p.Mode ?? TagRules.ByCategory;
            if (p.ExtraTargets != null)
            {
                var expected = new[] { p.Target }.Concat(p.ExtraTargets).Select(x => Rid.Value(x.Id)).ToList();
                var actual = tag.GetTaggedLocalElementIds().Select(Rid.Value).ToList();
                if (!TagRules.SameTaggedSet(expected, actual))
                {
                    reason = "the committed tag labels [" + string.Join(",", actual.OrderBy(x => x)) + "] instead of exactly [" +
                             string.Join(",", expected.OrderBy(x => x)) + "]";
                    return false;
                }
            }
            if (mode == TagRules.MultiCategory && !tag.IsMulticategoryTag)
            { reason = "tag_mode=multi_category but the committed tag is not a multi-category tag"; return false; }
            if (mode == TagRules.Material && !tag.IsMaterialTag)
            { reason = "tag_mode=" + mode + " but the committed tag is not a material tag"; return false; }
            if (mode == TagRules.KeynoteElement && tag.IsMaterialTag)
            { reason = "tag_mode=keynote_element but the committed tag reads a material"; return false; }
            if (TagRules.IsKeynote(mode))
            {
                string category = CategoryToken(tag.Document.GetElement(tag.GetTypeId()));
                if (category != "OST_KeynoteTags") { reason = "tag_mode=" + mode + " but the committed tag type is " + (category ?? "of no category") + ", not a keynote tag"; return false; }
            }
            if (TagRules.IsKeynote(mode))
            {
                // The source is checked for every keynote mode. For element and material keynotes Revit
                // sets it; for a user keynote this path wrote it, so its re-read alone proves nothing and
                // the tag's own TEXT must independently show the key or the table's text for it.
                int? source = ReadKeySource(tag);
                string key = mode == TagRules.KeynoteUser || mode == TagRules.KeynoteMaterial ? ReadString(tag, BuiltInParameter.KEY_VALUE) : null;
                string text = AnnotationLayout.SafeTagText(tag);
                string expectedKey = mode == TagRules.KeynoteMaterial ? p.MaterialKeynote : p.KeynoteKey;
                reason = TagRules.KeynoteVerificationFailure(mode, source, key, expectedKey, text, p.KeynoteText);
                if (reason != null) return false;
            }
            return true;
        }

        /// <summary>What kind of tag was committed, read back - beside verified, never instead of it.</summary>
        private static JObject TagKindEvidence(Plan p, IndependentTag tag)
        {
            if (p.Mode == null) return null;
            var o = new JObject { ["tag_mode"] = p.Mode };
            try
            {
                o["is_material_tag"] = tag.IsMaterialTag;
                o["is_multicategory_tag"] = tag.IsMulticategoryTag;
                if (p.ExtraTargets != null)
                    o["tagged_element_ids"] = new JArray(tag.GetTaggedLocalElementIds().Select(Rid.Value).OrderBy(x => x));
                if (TagRules.IsKeynote(p.Mode))
                {
                    o["key_value"] = ReadString(tag, BuiltInParameter.KEY_VALUE);
                    if (p.Mode == TagRules.KeynoteMaterial && p.MaterialId != null)
                    {
                        o["material_id"] = Rid.Value(p.MaterialId);
                        o["material_keynote"] = p.MaterialKeynote;
                        o["tagged_reference"] = "face";
                    }
                    int? source = ReadKeySource(tag);
                    o["key_source"] = source.HasValue ? (JToken)source.Value : JValue.CreateNull();
                    o["key_source_text"] = ReadKeySourceText(tag);
                    o["tag_text"] = AnnotationLayout.SafeTagText(tag);
                }
                if (p.LeaderEnd != null || p.LeaderElbow != null)
                {
                    Reference main = MainReference(p, tag);
                    if (main != null)
                    {
                        o["leader_end_condition"] = tag.LeaderEndCondition.ToString();
                        o["leader_end_feet"] = new JArray(Arr(tag.GetLeaderEnd(main)));
                        if (tag.HasLeaderElbow(main)) o["leader_elbow_feet"] = new JArray(Arr(tag.GetLeaderElbow(main)));
                    }
                }
            }
            catch (Exception ex) { o["unreadable"] = ex.Message; }
            return o;
        }

        /// <summary>
        /// The tag type of a spatial tag when none is named: the single loaded type, else the document's own default
        /// for the tag category (Revit's GetDefaultFamilyTypeId) when it is loaded. Null with a refusal that lists the
        /// candidates (id and Family: Type) when it stays ambiguous; null with no refusal when none is loaded.
        /// </summary>
        private static ElementType DefaultTagType(Document doc, BuiltInCategory bic, string kindName, out string refusal)
        {
            refusal = null;
            List<ElementType> loaded = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType()
                .OfType<ElementType>().OrderBy(t => Rid.Value(t.Id)).ToList();
            if (loaded.Count == 0) return null;
            long? def = null;
            try
            {
                ElementId d = doc.GetDefaultFamilyTypeId(new ElementId(bic));
                if (d != null && d != ElementId.InvalidElementId) def = Rid.Value(d);
            }
            catch { def = null; }
            long? chosen = TagRules.DefaultTagTypeId(loaded.Select(t => Rid.Value(t.Id)).ToList(), def);
            if (chosen == null)
            {
                refusal = TagRules.AmbiguousTagTypeRefusal(kindName, loaded.Select(t =>
                    Rid.Value(t.Id) + " " + ((t as FamilySymbol)?.FamilyName ?? "?") + ": " + t.Name));
                return null;
            }
            return loaded.First(t => Rid.Value(t.Id) == chosen.Value);
        }

        /// <summary>
        /// Field (2025): a multi-category tag on a toilet was created and verified reading "" - blank on paper.
        /// A tag whose text was READ and is empty is flagged on every row (rehearsal and apply), with or
        /// without require_tag_text (which turns the same finding into a refusal in the verification).
        /// </summary>
        private static void MarkEmptyTagText(JObject row, Plan p, string text, bool readable)
        {
            string warning = TagRules.EmptyTagTextWarning(text, readable, p.Input.Value<bool?>("require_tag_text") == true);
            if (warning == null) return;
            row["tag_text_empty"] = true;
            JArray warnings = row["warnings"] as JArray ?? new JArray();
            warnings.Add(warning);
            row["warnings"] = warnings;
        }
    }
}
