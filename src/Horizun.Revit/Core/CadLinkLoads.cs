// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// WHICH ISSUE OF THE DRAWING A LINK IS SHOWING.
//
// Revit records no moment for a CAD link's load. GetExternalFileReference gives a
// path; CADLinkType publishes no status that changes across a reload (which is why
// horizun_manage_cad_links verifies a reload by the geometry it hands back rather
// than by a status). So from inside the model there is no way to ask "is what I am
// looking at the file as it is now?", and on a sheet whose ductwork all lives in an
// xref the host's own hash cannot answer it either: the host's bytes do not move
// when its reference is revised.
//
// MEASURED (campaign 8, block 8): an xref edited with the host untouched. A reading
// with require_current_sources re-read the texts, re-hashed the host, reported
// sources_checked true - and returned the previous issue's geometry, because the
// link still held it. The plan made at that moment had the older runs and the newer
// labels, and the only thing the reply could say was that a label named nothing it
// could see.
//
// So when THIS BRIDGE loads a link - add, reload or repoint - it records what the
// link was loaded from: the file's hash, the identity of the whole source SET (host
// plus every reference resolved from beside it) and a fingerprint of the geometry
// Revit handed back. The last one is what keeps the record honest: if the link's
// geometry no longer fingerprints as it did, somebody reloaded or edited it outside
// this bridge and the record no longer describes what is loaded - which is reported
// as UNKNOWN, never as a disagreement this bridge cannot prove.
//
// The record lives beside the bridge's other state, on this machine. It does not
// travel with the model: on another machine, or after a save-as, there is no record
// and the answer is unknown. Unknown is the safe direction - it withholds "ready to
// apply" rather than granting it - and it is cleared by one typed reload.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a CAD link was loaded from, recorded when this bridge loaded it.</summary>
    public static class CadLinkLoads
    {
        public static string Root
        {
            get
            {
                string dir = Path.Combine(HorizunPaths.DataRoot(), "cad-link-loads");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>
        /// One file per link instance. The document is named by its own path when it has one and by
        /// its title otherwise, so two models with the same title in different folders never share a
        /// record; the instance is named by its unique id, which survives a save and a reopen.
        /// </summary>
        public static string PathFor(Document doc, string instanceUniqueId)
        {
            string docKey = null;
            try { docKey = string.IsNullOrWhiteSpace(doc?.PathName) ? doc?.Title : doc.PathName; } catch { }
            string basis = (docKey ?? "(no-document)") + "|" + (instanceUniqueId ?? "(no-instance)");
            return Path.Combine(Root, "link-" + CadIdentity.Sha256Hex(basis.ToLowerInvariant()).Substring(0, 32) + ".json");
        }

        public static void Record(Document doc, string instanceUniqueId, long instanceId, string externalPath,
                                  string fileSha256, string geometryFingerprint, string by)
        {
            if (doc == null || string.IsNullOrWhiteSpace(instanceUniqueId)) return;
            var o = new JObject
            {
                ["schema"] = "horizun.cad-link-load/1",
                ["document"] = SafeTitle(doc),
                ["document_path"] = SafePath(doc),
                ["instance_unique_id"] = instanceUniqueId,
                ["instance_id"] = instanceId,
                ["external_path"] = externalPath,
                ["file_sha256"] = fileSha256,
                ["source_set_sha256"] = CadDwgCache.SourceSetSha256(externalPath, fileSha256),
                ["geometry_fingerprint"] = geometryFingerprint,
                ["loaded_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["by"] = by
            };
            try { File.WriteAllText(PathFor(doc, instanceUniqueId), o.ToString(Formatting.Indented)); }
            catch { /* a record that cannot be written leaves the answer unknown, which is the safe one */ }
        }

        public static JObject Read(Document doc, string instanceUniqueId)
        {
            try
            {
                string p = PathFor(doc, instanceUniqueId);
                return File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : null;
            }
            catch { return null; }
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafePath(Document d) { try { return d.PathName; } catch { return null; } }
    }
}
