// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// IS IT SAFE TO BUILD FROM THIS READING?
//
// A DWG-to-BIM plan is made of two halves that come from two places. The GEOMETRY
// is the CAD link, as Revit loaded it. The SIZES, systems and elevations come from
// the drawing FILE, read now through its own extractor. Those two can belong to
// different issues of the same drawing, and nothing in Revit says so: a link holds
// whatever it held when it was loaded, and the host file's hash does not move when
// one of its references is revised.
//
// MEASURED (campaign 8): an xref revised with the host untouched produced a plan of
// twenty-six runs from the previous issue and a label from the new one - and the
// only thing the reply could say was that the label named nothing it could see.
// Publishing two hashes made that observable. It did not make it SAFE: a caller
// reading a plan still had no answer to "may I apply this?".
//
// So this answers exactly that, in one of four states, and never guesses:
//
//   sources_match_the_link   this bridge loaded the link, nothing has touched it
//                            since (its geometry still fingerprints as recorded),
//                            and the source set hashes as it did then. APPLICABLE.
//
//   revisions_not_aligned    same, except the source set has changed since. The
//                            geometry is the older issue. NOT applicable: reload
//                            the link and plan again.
//
//   coherence_unknown        no record of this bridge loading it, or the link's
//                            geometry no longer matches the record - somebody
//                            loaded or edited it elsewhere - or the set identity
//                            could not be computed. NOT applicable, and it says
//                            which of those it is. Unknown is not a failure; it is
//                            the refusal to claim a correspondence nobody measured.
//
//   continued_snapshot       the reading continued a snapshot and deliberately
//                            checked nothing. NOT applicable by construction.
//
// The rule is one-directional on purpose: only a state this bridge can DEMONSTRATE
// grants "ready to apply". Everything else keeps the diagnosis and withholds the
// permission, and one typed reload clears it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadSourceCoherence
    {
        public const string Aligned = "sources_match_the_link";
        public const string NotAligned = "revisions_not_aligned";
        public const string Unknown = "coherence_unknown";
        public const string Snapshot = "continued_snapshot";

        /// <summary>
        /// The same surrogates horizun_query_cad publishes, so a fingerprint taken here and one taken by
        /// horizun_manage_cad_links across a reload are the same number and can be compared.
        /// </summary>
        public static string GeometryFingerprint(Document doc, Element instance)
        {
            try
            {
                if (doc == null || instance == null) return null;
                CadHarvest harvest = CadGeometryHarvest.Harvest(doc, instance, 5.0, 20000);
                return GeometryFingerprint(harvest);
            }
            catch { return null; }
        }

        public static string GeometryFingerprint(CadHarvest harvest)
        {
            try
            {
                if (harvest == null) return null;
                return CadIdentity.SetFingerprint(harvest.Segments.Select(seg =>
                    CadIdentity.SurrogateUndirected(null, seg.Layer, "root", seg.SourceKind,
                        new List<CadPoint> { seg.A, seg.B }, 1.0)));
            }
            catch { return null; }
        }

        /// <summary>
        /// Judge one CAD instance. <paramref name="harvest"/> may be the one the caller already took -
        /// re-harvesting a 42 000 segment drawing to answer a question the reading has already paid for
        /// would double the cost of every plan.
        /// </summary>
        public static JObject Evaluate(Document doc, Element instance, CadInstanceFacts facts,
                                       CadHarvest harvest, bool fromSnapshot)
        {
            var o = new JObject();
            string uid = null;
            try { uid = instance?.UniqueId; } catch { }
            string path = facts != null ? facts.ExternalPath : null;
            string fileSha = facts != null ? facts.FileSha256 : null;
            string setNow = CadDwgCache.SourceSetSha256(path, fileSha);

            o["sources_now"] = new JObject
            {
                ["file"] = path,
                ["file_sha256"] = fileSha,
                ["source_set_sha256"] = setNow,
                ["set_means"] = "the host file and every external reference resolved from beside it. This is " +
                                "what moves when a drawing is revised; the host's own hash does not have to."
            };

            if (fromSnapshot)
            {
                o["state"] = Snapshot;
                o["applicable"] = false;
                o["means"] = "this reading continued a named snapshot and checked nothing: by its own contract " +
                             "no file was hashed and no reference was looked at. A plan cannot be declared " +
                             "current on a reading that did not look.";
                o["remedy"] = "read again without expect_analysis_fingerprint, or with require_current_sources.";
                return o;
            }

            JObject record = CadLinkLoads.Read(doc, uid);
            if (record == null)
            {
                o["state"] = Unknown;
                o["applicable"] = false;
                o["why"] = "no_record_of_loading_this_link";
                o["means"] = "this bridge did not load this link - it was linked in Revit, or loaded on another " +
                             "machine, or the model was saved as a copy - so it cannot say which issue of the " +
                             "drawing the geometry is. Revit records no moment for a CAD link's load.";
                o["remedy"] = "horizun_manage_cad_links operation=reload on this instance, then plan again: the " +
                              "reload is verified by the geometry it returns and records what it loaded.";
                return o;
            }

            o["link_loaded"] = new JObject
            {
                ["utc"] = record["loaded_utc"],
                ["by"] = record["by"],
                ["file_sha256"] = record["file_sha256"],
                ["source_set_sha256"] = record["source_set_sha256"]
            };

            string printNow = GeometryFingerprint(harvest) ?? GeometryFingerprint(doc, instance);
            string printThen = record.Value<string>("geometry_fingerprint");
            o["geometry_fingerprint"] = new JObject { ["when_loaded"] = printThen, ["now"] = printNow };
            if (string.IsNullOrWhiteSpace(printThen) || string.IsNullOrWhiteSpace(printNow) ||
                !string.Equals(printThen, printNow, StringComparison.Ordinal))
            {
                o["state"] = Unknown;
                o["applicable"] = false;
                o["why"] = printThen == null || printNow == null
                    ? "the_geometry_could_not_be_fingerprinted"
                    : "the_link_changed_after_this_bridge_recorded_it";
                o["means"] = "the geometry this link holds is not the geometry recorded when this bridge loaded " +
                             "it, so the record no longer describes what is loaded: somebody reloaded or edited " +
                             "the link elsewhere. What is loaded may well be current - this bridge cannot show it.";
                o["remedy"] = "horizun_manage_cad_links operation=reload, then plan again.";
                return o;
            }

            string setThen = record.Value<string>("source_set_sha256");
            if (string.IsNullOrWhiteSpace(setNow) || string.IsNullOrWhiteSpace(setThen))
            {
                o["state"] = Unknown;
                o["applicable"] = false;
                o["why"] = "the_source_set_could_not_be_identified";
                o["means"] = "the identity of the file set could not be computed on one of the two sides - a " +
                             "reference that does not resolve from beside the host, or a drawing this machine " +
                             "has never read through the text extractor. Without both, the comparison would be " +
                             "between a number and nothing.";
                o["remedy"] = "read the drawing once (horizun_plan_from_cad or horizun_cad_networks without a " +
                              "fingerprint), check that every reference resolves, then plan again.";
                return o;
            }

            if (string.Equals(setThen, setNow, StringComparison.Ordinal))
            {
                o["state"] = Aligned;
                o["applicable"] = true;
                o["means"] = "the link was loaded by this bridge, nothing has touched it since, and the drawing " +
                             "and every reference still hash as they did then. The geometry in this plan and the " +
                             "sizes read from the file are the same issue of the drawing.";
                return o;
            }

            o["state"] = NotAligned;
            o["applicable"] = false;
            o["why"] = "the_sources_changed_since_the_link_was_loaded";
            o["differs"] = new JObject
            {
                ["file_sha256_when_loaded"] = record["file_sha256"],
                ["file_sha256_now"] = fileSha,
                ["host_file_changed"] = !string.Equals(record.Value<string>("file_sha256"), fileSha, StringComparison.Ordinal),
                ["source_set_when_loaded"] = setThen,
                ["source_set_now"] = setNow
            };
            o["means"] = "the drawing, or something it references, was revised after this link was loaded. The " +
                         "geometry here is the older issue; anything read from the file now is the newer one. A " +
                         "plan made of both would build one issue's runs with another issue's sizes.";
            o["remedy"] = "horizun_manage_cad_links operation=reload on this instance, then plan again.";
            return o;
        }
    }
}
