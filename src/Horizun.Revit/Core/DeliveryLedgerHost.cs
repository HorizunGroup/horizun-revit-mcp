// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The Revit half of the delivery ledger: what only the host can do. Reading a
// scope's VersionGuids, hashing the files a stage produced, and turning "this
// element changed" into an invalidation the ledger then cascades. The state
// machine itself lives in DeliveryLedger.cs and is tested without a Revit.
//
// Nothing here touches the model. The ledger lives on disk under
// %USERPROFILE%\.horizun\deliveries\<delivery_id>.jsonl; the document is only
// ever READ, to compare what a completed stage recorded with what stands now.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class DeliveryLedgerHost
    {
        internal static string Directory() { return HorizunPaths.DeliveriesDir(); }

        internal static JObject DocumentIdentity(UIApplication app, Document doc)
        {
            string year = app.Application.VersionNumber;
            DocIdentity id = DocumentGate.IdentityOf(doc, year);
            return new JObject
            {
                ["title"] = doc.Title, ["path"] = string.IsNullOrWhiteSpace(doc.PathName) ? JValue.CreateNull() : (JToken)doc.PathName,
                ["fingerprint"] = id.Fingerprint(), ["revit_year"] = year, ["revit_build"] = app.Application.VersionBuild
            };
        }

        internal static JObject AddinIdentity()
        {
            return new JObject { ["version"] = Build.Version, ["commit"] = Build.Commit, ["built_from_clean_tree"] = Build.BuiltFromCleanTree };
        }

        /// <summary>The scope of a set of elements as the host reads it now: id, unique id, VersionGuid.</summary>
        internal static JArray ReadScope(Document doc, IEnumerable<long> ids, out List<string> missing)
        {
            var scope = new JArray();
            missing = new List<string>();
            foreach (long id in ids.Distinct().OrderBy(x => x))
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null) { missing.Add(id.ToString(CultureInfo.InvariantCulture)); continue; }
                string uid, version;
                try { uid = e.UniqueId; } catch { uid = null; }
                try { version = e.VersionGuid.ToString("d"); } catch { version = null; }
                scope.Add(new JObject { ["element_id"] = id, ["unique_id"] = uid, ["version_guid"] = version });
            }
            return scope;
        }

        internal static JArray HashFiles(IEnumerable<string> paths, out List<string> missing)
        {
            var files = new JArray();
            missing = new List<string>();
            foreach (string p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) { missing.Add(p ?? "(null)"); continue; }
                using (var s = File.OpenRead(p))
                using (var sha = SHA256.Create())
                    files.Add(new JObject { ["path"] = p, ["bytes"] = s.Length,
                        ["sha256"] = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant() });
            }
            return files;
        }

        /// <summary>
        /// Compare every recorded scope with the document as it stands. A deleted or
        /// changed element invalidates its stage (and, through the ledger, everything
        /// built on it). Events are appended; the returned list names what was hit.
        /// </summary>
        internal static JArray Reverify(Document doc, JObject record, string deliveryId, DateTime utc)
        {
            var report = new JArray();
            JObject resume = DeliveryLedger.Resume(record);
            foreach (JObject stage in ((JArray)resume["needs_reverification"]).OfType<JObject>())
            {
                string key = stage.Value<string>("key");
                var reasons = new List<string>();
                foreach (JObject entry in (stage["scope"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    long id = entry.Value<long?>("element_id") ?? -1;
                    string recorded = entry.Value<string>("version_guid");
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (e == null) { reasons.Add("element " + id + " no longer exists"); continue; }
                    string now;
                    try { now = e.VersionGuid.ToString("d"); } catch (Exception ex) { reasons.Add("element " + id + ": VersionGuid unreadable (" + ex.Message + ")"); continue; }
                    if (recorded != null && !string.Equals(recorded, now, StringComparison.OrdinalIgnoreCase))
                        reasons.Add("element " + id + " changed (VersionGuid " + recorded + " -> " + now + ")");
                }
                if (reasons.Count == 0) { report.Add(new JObject { ["key"] = key, ["scope_current"] = true }); continue; }
                string reason = string.Join("; ", reasons);
                List<string> hit = DeliveryLedger.Invalidate(record, key, reason, utc);
                DeliveryLedger.AppendEvent(FileJobSink.Instance, Directory(), deliveryId, DeliveryLedger.InvalidationEvent(key, reason, utc));
                report.Add(new JObject { ["key"] = key, ["scope_current"] = false, ["reason"] = reason, ["invalidated"] = new JArray(hit) });
            }
            return report;
        }

        internal static JObject Load(string deliveryId, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrWhiteSpace(deliveryId)) { refusal = "delivery_id is required."; return null; }
            JObject record;
            try { record = DeliveryLedger.Load(Directory(), deliveryId); }
            catch (Exception ex) { refusal = "The delivery ledger could not be read: " + ex.Message; return null; }
            if (record == null) { refusal = "No delivery '" + deliveryId + "' has been opened on this machine (" + DeliveryLedger.FileFor(Directory(), deliveryId) + ")."; return null; }
            return record;
        }

        /// <summary>The document this ledger was opened on must be the active one.</summary>
        internal static string DocumentMismatch(UIApplication app, Document doc, JObject record)
        {
            string recorded = record["document"]?.Value<string>("fingerprint");
            string now = DocumentIdentity(app, doc).Value<string>("fingerprint");
            if (recorded != null && recorded != now)
                return "delivery '" + record.Value<string>("delivery_id") + "' was opened on document fingerprint " + recorded +
                       " and the active document is " + now + "; activate the right document. Nothing was recorded.";
            return null;
        }

        internal static JObject Summary(JObject record, JArray reverification)
        {
            JObject resume = DeliveryLedger.Resume(record);
            return new JObject
            {
                ["delivery_id"] = record["delivery_id"].DeepClone(),
                ["profile"] = record["profile"].DeepClone(),
                ["document"] = record["document"].DeepClone(),
                ["addin"] = record["addin"].DeepClone(),
                ["ledger_file"] = DeliveryLedger.FileFor(Directory(), record.Value<string>("delivery_id")),
                ["reverification"] = reverification ?? new JArray(),
                ["resume"] = resume,
                ["stages"] = new JArray(((JArray)record["stages"]).OfType<JObject>().Select(s => new JObject
                {
                    ["key"] = s["key"].DeepClone(), ["kind"] = s["kind"].DeepClone(), ["status"] = s["status"].DeepClone(),
                    ["depends_on"] = s["depends_on"].DeepClone(), ["updated_utc"] = s["updated_utc"].DeepClone()
                })),
                ["replay_problems"] = record["replay_problems"]?.DeepClone() ?? new JArray(),
                ["atomicity"] = record["atomicity"].DeepClone(), ["authority"] = record["authority"].DeepClone()
            };
        }
    }
}
