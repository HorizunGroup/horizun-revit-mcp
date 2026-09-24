// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// UNDO FOR A BATCH, BECAUSE REVIT HAS NO UNDO IN ITS API.
//
// Before a typed write commits, the tool records an INVERSE: created elements to
// delete, parameters with their previous values, transforms with their inverse.
// The record is durable (one JSON file per document under the data root) and it
// carries two guards, because an inverse applied to a model that moved on is a
// second, unreviewed edit:
//
//   * THE ELEMENTS' STATE. Each entry keeps the state the batch LEFT each element
//     in. If anything changed it since (a person, another tool), undo refuses and
//     names the element - it never overwrites work it did not do.
//   * THE DOCUMENT'S SAVE STAMP. A save or a sync makes the batch part of the file
//     other people may already have. Undo refuses and says so.
//
// Only the LAST batch is undoable, and a last batch that could not be recorded
// blocks - it is never skipped to reach an older one.
//
// Revit-free: records, comparison and selection are provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class UndoEntry
    {
        /// <summary>created | parameter | move | rotate | mirror | pin | type | curve | tag_head</summary>
        public string Op;
        public List<long> ElementIds = new List<long>();
        public List<string> UniqueIds = new List<string>();
        /// <summary>element id (string) -> state the batch found.</summary>
        public JObject Before = new JObject();
        /// <summary>element id (string) -> state the batch left.</summary>
        public JObject After = new JObject();
        /// <summary>The inverse's arguments: vector, axis, angle, plane, parameter identity.</summary>
        public JObject Inverse = new JObject();

        public JObject ToJson() => new JObject
        {
            ["op"] = Op,
            ["element_ids"] = new JArray(ElementIds),
            ["unique_ids"] = new JArray(UniqueIds),
            ["before"] = Before, ["after"] = After, ["inverse"] = Inverse
        };

        public static UndoEntry FromJson(JObject o) => new UndoEntry
        {
            Op = o.Value<string>("op"),
            ElementIds = (o["element_ids"] as JArray ?? new JArray()).Select(t => (long)t).ToList(),
            UniqueIds = (o["unique_ids"] as JArray ?? new JArray()).Select(t => (string)t).ToList(),
            Before = o["before"] as JObject ?? new JObject(),
            After = o["after"] as JObject ?? new JObject(),
            Inverse = o["inverse"] as JObject ?? new JObject()
        };
    }

    public sealed class UndoBatch
    {
        public string Id;
        public string Tool;
        public string CreatedUtc;
        public string DocumentTitle;
        /// <summary>Save/sync stamp of the document when the batch committed (VersionGUID + saves).</summary>
        public string SaveStamp;
        public bool Undoable = true;
        public string NotUndoableReason;
        public string UndoneUtc;
        public List<UndoEntry> Entries = new List<UndoEntry>();

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["id"] = Id, ["tool"] = Tool, ["created_utc"] = CreatedUtc, ["document"] = DocumentTitle,
                ["save_stamp"] = SaveStamp, ["undoable"] = Undoable,
                ["entries"] = new JArray(Entries.Select(e => (JToken)e.ToJson()))
            };
            if (NotUndoableReason != null) o["not_undoable_reason"] = NotUndoableReason;
            if (UndoneUtc != null) o["undone_utc"] = UndoneUtc;
            return o;
        }

        public static UndoBatch FromJson(JObject o) => new UndoBatch
        {
            Id = o.Value<string>("id"), Tool = o.Value<string>("tool"), CreatedUtc = o.Value<string>("created_utc"),
            DocumentTitle = o.Value<string>("document"), SaveStamp = o.Value<string>("save_stamp"),
            Undoable = o.Value<bool?>("undoable") != false, NotUndoableReason = o.Value<string>("not_undoable_reason"),
            UndoneUtc = o.Value<string>("undone_utc"),
            Entries = (o["entries"] as JArray ?? new JArray()).OfType<JObject>().Select(UndoEntry.FromJson).ToList()
        };

        /// <summary>Short row for operation=list.</summary>
        public JObject Summary() => new JObject
        {
            ["batch_id"] = Id, ["tool"] = Tool, ["created_utc"] = CreatedUtc,
            ["entries"] = Entries.Count,
            ["elements"] = Entries.SelectMany(e => e.ElementIds).Distinct().Count(),
            ["ops"] = new JArray(Entries.Select(e => e.Op).Distinct()),
            ["undoable"] = Undoable && UndoneUtc == null,
            ["state"] = UndoneUtc != null ? "undone" : Undoable ? "recorded" : "not_undoable",
            ["reason"] = NotUndoableReason
        };
    }

    public static class UndoRules
    {
        public const int MaxBatches = 20;
        public const double Tolerance = 1e-4;   // feet / radians: ~0.03 mm, ~0.006 degrees

        /// <summary>
        /// The batch `undo last` acts on: the newest one not yet undone. If it is not
        /// undoable the answer is a refusal naming it - never the batch before it, because
        /// undoing an older batch under a newer one reorders history nobody reviewed.
        /// </summary>
        public static UndoBatch Last(IList<UndoBatch> batches, out string refusal)
        {
            refusal = null;
            UndoBatch last = (batches ?? new List<UndoBatch>()).Where(b => b.UndoneUtc == null)
                                                             .OrderBy(b => b.CreatedUtc, StringComparer.Ordinal)
                                                             .LastOrDefault();
            if (last == null) { refusal = "no Horizun batch is recorded for this document (or every one is already undone)."; return null; }
            if (!last.Undoable)
            {
                refusal = "the last Horizun batch (" + last.Id + ", " + last.Tool + ") could not be recorded as undoable: " +
                          (last.NotUndoableReason ?? "unknown reason") + ". Older batches are NOT undone past it.";
                return null;
            }
            return last;
        }

        /// <summary>The save stamp guard: a save or a sync since the batch refuses.</summary>
        public static bool SavedSince(string recorded, string current, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(recorded) || string.IsNullOrEmpty(current))
            {
                if (string.IsNullOrEmpty(recorded) && string.IsNullOrEmpty(current)) return false;
                reason = "the document's save stamp cannot be compared (recorded '" + (recorded ?? "none") + "', now '" +
                         (current ?? "unreadable") + "'), so a save or sync since the batch cannot be ruled out.";
                return true;
            }
            if (string.Equals(recorded, current, StringComparison.Ordinal)) return false;
            reason = "the document was SAVED or SYNCHRONIZED since this batch (stamp " + recorded + " -> " + current +
                     "). The batch is now part of a file others may have; undo it deliberately in Revit or with a new edit.";
            return true;
        }

        /// <summary>
        /// Two recorded states agree: numbers within the tolerance, everything else exact.
        /// A null on one side and not the other is a disagreement - never "close enough".
        /// </summary>
        public static bool StatesMatch(JToken a, JToken b, double tolerance = Tolerance)
        {
            if (a == null || a.Type == JTokenType.Null) return b == null || b.Type == JTokenType.Null;
            if (b == null || b.Type == JTokenType.Null) return false;
            bool an = a.Type == JTokenType.Float || a.Type == JTokenType.Integer;
            bool bn = b.Type == JTokenType.Float || b.Type == JTokenType.Integer;
            if (an && bn)
            {
                double x = a.Value<double>(), y = b.Value<double>();
                double scale = Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)));
                return Math.Abs(x - y) <= tolerance * scale;
            }
            if (a is JArray aa && b is JArray ba)
            {
                if (aa.Count != ba.Count) return false;
                for (int i = 0; i < aa.Count; i++) if (!StatesMatch(aa[i], ba[i], tolerance)) return false;
                return true;
            }
            if (a is JObject ao && b is JObject bo)
            {
                var keys = new HashSet<string>(ao.Properties().Select(p => p.Name).Concat(bo.Properties().Select(p => p.Name)));
                foreach (string k in keys) if (!StatesMatch(ao[k], bo[k], tolerance)) return false;
                return true;
            }
            return JToken.DeepEquals(a, b);
        }

        /// <summary>
        /// Every element whose current state differs from the state the batch left, by id.
        /// `current` maps element id -> state now (null = the element is gone).
        /// </summary>
        public static List<string> Drifted(UndoBatch batch, IDictionary<string, JToken> current)
        {
            var drift = new List<string>();
            foreach (UndoEntry e in batch.Entries)
                foreach (var p in e.After.Properties())
                {
                    string key = e.Op + ":" + p.Name + (e.Op == "parameter" ? ":" + e.Inverse.Value<string>("parameter") : "");
                    JToken now;
                    if (current == null || !current.TryGetValue(key, out now)) now = null;
                    if (!StatesMatch(p.Value, now)) drift.Add(key);
                }
            return drift;
        }

        public static string NewId(string nowUtc) =>
            "u" + (nowUtc ?? "").Replace("-", "").Replace(":", "").Replace(".", "").Replace("T", "").Replace("Z", "") +
            Guid.NewGuid().ToString("N").Substring(0, 6);

        /// <summary>Append, trimming the oldest past the cap.</summary>
        public static void Append(List<UndoBatch> batches, UndoBatch batch)
        {
            batches.Add(batch);
            while (batches.Count > MaxBatches) batches.RemoveAt(0);
        }
    }

    /// <summary>The journal on disk: one file per document, written atomically.</summary>
    public static class UndoJournalStore
    {
        public static string Dir() => Path.Combine(HorizunPaths.DataRoot(), "undo");

        public static string PathFor(string documentTitle, string documentPath)
        {
            string identity = (documentTitle ?? "untitled") + "\x1f" + (documentPath ?? "");
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return Path.Combine(Dir(), hex + ".json");
            }
        }

        public static List<UndoBatch> Load(string path)
        {
            if (!File.Exists(path)) return new List<UndoBatch>();
            JObject root = JObject.Parse(File.ReadAllText(path));
            return (root["batches"] as JArray ?? new JArray()).OfType<JObject>().Select(UndoBatch.FromJson).ToList();
        }

        public static void Save(string path, List<UndoBatch> batches)
        {
            var root = new JObject { ["schema"] = 1, ["batches"] = new JArray(batches.Select(b => (JToken)b.ToJson())) };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, root.ToString());
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }
}
