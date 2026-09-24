// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of the undo journal (Core/UndoJournal.cs): read an element's
// state, record a committed batch, and apply a batch's inverse. Recording NEVER
// fails a write: the write was already verified, and a journal that could not be
// written is reported beside it as `undo.recorded=false` with the reason.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public static class UndoCapture
    {
        private static JArray P(XYZ p) => new JArray(Math.Round(p.X, 7), Math.Round(p.Y, 7), Math.Round(p.Z, 7));
        private static XYZ X(JToken t) => t is JArray a && a.Count == 3 ? new XYZ((double)a[0], (double)a[1], (double)a[2]) : null;

        /// <summary>The state an inverse restores and a drift check compares. Null when the element is gone.</summary>
        public static JObject State(Document doc, long id)
        {
            Element e;
            try { e = doc.GetElement(Rid.Make(id)); } catch { e = null; }
            if (e == null) return null;
            var s = new JObject();
            try
            {
                if (e.Location is LocationCurve lc && lc.Curve != null)
                {
                    s["loc"] = new JArray(P(lc.Curve.GetEndPoint(0)), P(lc.Curve.GetEndPoint(1)));
                    s["line"] = lc.Curve is Line;
                }
                else if (e.Location is LocationPoint lp)
                {
                    s["loc"] = new JArray(P(lp.Point));
                    try { s["rot"] = Math.Round(lp.Rotation, 7); } catch { }
                }
            }
            catch { }
            try { s["type"] = Rid.Value(e.GetTypeId()); } catch { }
            try { s["pinned"] = e.Pinned; } catch { }
            if (e is FamilyInstance fi)
            {
                try { s["facing"] = P(fi.FacingOrientation); } catch { }
                try { s["hand"] = P(fi.HandOrientation); } catch { }
                try { s["mirrored"] = fi.Mirrored; } catch { }
            }
            if (e is IndependentTag tag) { try { s["head"] = P(tag.TagHeadPosition); } catch { } }
            return s;
        }

        public static JObject States(Document doc, IEnumerable<long> ids)
        {
            var o = new JObject();
            foreach (long id in ids) o[id.ToString(CultureInfo.InvariantCulture)] = (JToken)State(doc, id) ?? JValue.CreateNull();
            return o;
        }

        /// <summary>Stable identity of a parameter for re-resolution at undo time.</summary>
        public static string ParameterKey(Parameter p)
        {
            try
            {
                if (p.Definition is InternalDefinition d && d.BuiltInParameter != BuiltInParameter.INVALID)
                    return "builtin:" + d.BuiltInParameter;
                if (p.IsShared) return "guid:" + p.GUID.ToString();
                return "name:" + p.Definition.Name;
            }
            catch { return null; }
        }

        public static Parameter ResolveParameter(Element e, string key)
        {
            if (e == null || string.IsNullOrEmpty(key)) return null;
            if (key.StartsWith("builtin:", StringComparison.Ordinal) &&
                Enum.TryParse(key.Substring(8), out BuiltInParameter bip)) return e.get_Parameter(bip);
            if (key.StartsWith("guid:", StringComparison.Ordinal) && Guid.TryParse(key.Substring(5), out Guid g)) return e.get_Parameter(g);
            if (key.StartsWith("name:", StringComparison.Ordinal)) return e.LookupParameter(key.Substring(5));
            return null;
        }

        public static JObject ParameterState(Parameter p)
        {
            if (p == null) return null;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return new JObject { ["storage"] = "String", ["value"] = p.AsString() };
                    case StorageType.Integer: return new JObject { ["storage"] = "Integer", ["value"] = p.AsInteger() };
                    case StorageType.Double: return new JObject { ["storage"] = "Double", ["value"] = p.AsDouble() };
                    case StorageType.ElementId: return new JObject { ["storage"] = "ElementId", ["value"] = Rid.Value(p.AsElementId()) };
                }
            }
            catch { }
            return null;
        }

        public static string SaveStamp(Document doc)
        {
            try
            {
                DocumentVersion v = Document.GetDocumentVersion(doc);
                if (v == null) return "unsaved";
                return v.VersionGUID.ToString() + "#" + v.NumberOfSaves.ToString(CultureInfo.InvariantCulture);
            }
            catch { return "unsaved"; }
        }

        /// <summary>Record a committed, verified batch. Returns the reply's `undo` block; never throws.</summary>
        public static JObject Record(Document doc, string tool, List<UndoEntry> entries, string notUndoableReason = null)
        {
            try
            {
                string path = UndoJournalStore.PathFor(doc.Title, SafePath(doc));
                List<UndoBatch> batches = UndoJournalStore.Load(path);
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                var batch = new UndoBatch
                {
                    Id = UndoRules.NewId(now), Tool = tool, CreatedUtc = now, DocumentTitle = doc.Title,
                    SaveStamp = SaveStamp(doc), Entries = entries ?? new List<UndoEntry>(),
                    Undoable = notUndoableReason == null && entries != null && entries.Count > 0,
                    NotUndoableReason = notUndoableReason ?? (entries == null || entries.Count == 0 ? "nothing recordable" : null)
                };
                UndoRules.Append(batches, batch);
                UndoJournalStore.Save(path, batches);
                bool reread = UndoJournalStore.Load(path).Any(b => b.Id == batch.Id);
                return new JObject
                {
                    ["recorded"] = reread, ["batch_id"] = batch.Id, ["undoable"] = batch.Undoable,
                    ["reason"] = batch.NotUndoableReason, ["note"] = "horizun_undo operation=undo_last reverses it while nothing changed it since."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["recorded"] = false, ["reason"] = "the undo journal could not be written: " + ex.Message };
            }
        }

        public static string SafePath(Document doc) { try { return doc.PathName; } catch { return null; } }

        /// <summary>A transform entry for ids that were moved/rotated/mirrored/pinned/re-typed.</summary>
        public static UndoEntry Entry(Document doc, string op, IEnumerable<long> ids, JObject before, JObject inverse)
        {
            var list = ids.ToList();
            var e = new UndoEntry { Op = op, ElementIds = list, Before = before ?? new JObject(), Inverse = inverse ?? new JObject() };
            foreach (long id in list)
            {
                string uid = null;
                try { uid = doc.GetElement(Rid.Make(id))?.UniqueId; } catch { }
                e.UniqueIds.Add(uid);
            }
            e.After = States(doc, list);
            return e;
        }

        /// <summary>Current state keyed the way UndoRules.Drifted reads it.</summary>
        public static Dictionary<string, JToken> Current(Document doc, UndoBatch batch)
        {
            var d = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (UndoEntry e in batch.Entries)
                foreach (var p in e.After.Properties())
                {
                    long id = long.Parse(p.Name, CultureInfo.InvariantCulture);
                    if (e.Op == "parameter")
                    {
                        string key = e.Inverse.Value<string>("parameter");
                        Element el = null; try { el = doc.GetElement(Rid.Make(id)); } catch { }
                        d[e.Op + ":" + p.Name + ":" + key] = (JToken)ParameterState(ResolveParameter(el, key)) ?? JValue.CreateNull();
                    }
                    else d[e.Op + ":" + p.Name] = (JToken)State(doc, id) ?? JValue.CreateNull();
                }
            return d;
        }

        /// <summary>
        /// Apply one entry's inverse inside an open transaction. `deleted` collects what
        /// Revit removed so the caller can prove nothing beyond the batch went with it.
        /// </summary>
        public static void ApplyInverse(Document doc, UndoEntry e, List<long> deleted)
        {
            var ids = e.ElementIds.Select(Rid.Make).ToList();
            JObject inv = e.Inverse;
            switch (e.Op)
            {
                case "created":
                    foreach (ElementId gone in doc.Delete(ids)) deleted.Add(Rid.Value(gone));
                    break;
                case "move":
                    ElementTransformUtils.MoveElements(doc, ids, X(inv["vector"]).Negate());
                    break;
                case "rotate":
                    ElementTransformUtils.RotateElements(doc, ids, Line.CreateBound(X(inv["axis_start"]), X(inv["axis_end"])),
                                                         -(double)inv["angle"]);
                    break;
                case "mirror":
                    ElementTransformUtils.MirrorElements(doc, ids,
                        Plane.CreateByNormalAndOrigin(X(inv["plane_normal"]), X(inv["plane_origin"])), false);
                    break;
                case "pin":
                    foreach (long id in e.ElementIds) doc.GetElement(Rid.Make(id)).Pinned = (bool)e.Before[id.ToString(CultureInfo.InvariantCulture)]["pinned"];
                    break;
                case "type":
                    foreach (long id in e.ElementIds) doc.GetElement(Rid.Make(id)).ChangeTypeId(Rid.Make((long)e.Before[id.ToString(CultureInfo.InvariantCulture)]["type"]));
                    break;
                case "curve":
                    foreach (long id in e.ElementIds)
                    {
                        JArray loc = (JArray)e.Before[id.ToString(CultureInfo.InvariantCulture)]["loc"];
                        ((LocationCurve)doc.GetElement(Rid.Make(id)).Location).Curve = Line.CreateBound(X(loc[0]), X(loc[1]));
                    }
                    break;
                case "tag_head":
                    foreach (long id in e.ElementIds)
                        ((IndependentTag)doc.GetElement(Rid.Make(id))).TagHeadPosition = X(e.Before[id.ToString(CultureInfo.InvariantCulture)]["head"]);
                    break;
                case "parameter":
                    foreach (long id in e.ElementIds)
                    {
                        Parameter p = ResolveParameter(doc.GetElement(Rid.Make(id)), inv.Value<string>("parameter"));
                        JObject before = (JObject)e.Before[id.ToString(CultureInfo.InvariantCulture)];
                        if (p == null) throw new InvalidOperationException("parameter " + inv.Value<string>("parameter") + " no longer resolves on " + id);
                        bool ok;
                        switch (before.Value<string>("storage"))
                        {
                            case "String": ok = p.Set(before.Value<string>("value") ?? ""); break;
                            case "Integer": ok = p.Set(before.Value<int>("value")); break;
                            case "Double": ok = p.Set(before.Value<double>("value")); break;
                            case "ElementId": ok = p.Set(Rid.Make(before.Value<long>("value"))); break;
                            default: ok = false; break;
                        }
                        if (!ok) throw new InvalidOperationException("Revit refused to restore " + inv.Value<string>("parameter") + " on " + id);
                    }
                    break;
                default:
                    throw new InvalidOperationException("no inverse for op '" + e.Op + "'");
            }
        }
    }
}
