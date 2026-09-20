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
        public const string Aligned = CadSourceCoherenceRules.Aligned;
        public const string NotAligned = CadSourceCoherenceRules.NotAligned;
        public const string Unknown = CadSourceCoherenceRules.Unknown;
        public const string Snapshot = CadSourceCoherenceRules.Snapshot;

        /// <summary>
        /// ONE HARVEST, ALWAYS THE SAME ONE. The fingerprint only means anything when the two sides were
        /// taken the same way, and the first thing the live cases caught was that they were not: the record
        /// was written from a harvest capped at 20 000 primitives with a 5 mm sagitta, and the plan compared
        /// it against ITS harvest, taken with the requirement set's sagitta and the plan's own cap. A link
        /// nobody had touched came back as "changed after this bridge recorded it" - a false alarm, in the
        /// direction that withholds permission, which is how it was found rather than believed.
        ///
        /// So the caller's harvest is never used here, however convenient: this takes its own, with the
        /// constants below, and horizun_manage_cad_links records it with the same call.
        /// </summary>
        public const double SagittaMm = 5.0;
        public const int MaxPrimitives = 20000;

        public static string GeometryFingerprint(Document doc, Element instance)
        {
            try
            {
                if (doc == null || instance == null) return null;
                CadHarvest harvest = CadGeometryHarvest.Harvest(doc, instance, SagittaMm, MaxPrimitives);
                return CadIdentity.SetFingerprint(harvest.Segments.Select(seg =>
                    CadIdentity.SurrogateUndirected(null, seg.Layer, "root", seg.SourceKind,
                        new List<CadPoint> { seg.A, seg.B }, 1.0)));
            }
            catch { return null; }
        }

        /// <summary>
        /// Judge one CAD instance. It takes its own harvest on purpose - see GeometryFingerprint.
        /// </summary>
        public static JObject Evaluate(Document doc, Element instance, CadInstanceFacts facts,
                                       bool fromSnapshot)
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

            JObject record = CadLinkLoads.Read(doc, uid);
            string printNow = record == null ? null : GeometryFingerprint(doc, instance);
            JObject decided = CadSourceCoherenceRules.Decide(record, setNow, fileSha, printNow, fromSnapshot);
            if (record != null)
            {
                o["link_loaded"] = new JObject
                {
                    ["utc"] = record["loaded_utc"],
                    ["by"] = record["by"],
                    ["file_sha256"] = record["file_sha256"],
                    ["source_set_sha256"] = record["source_set_sha256"]
                };
                o["geometry_fingerprint"] = new JObject
                {
                    ["when_loaded"] = record["geometry_fingerprint"],
                    ["now"] = printNow
                };
            }
            foreach (JProperty prop in decided.Properties()) o[prop.Name] = prop.Value;
            return o;
        }
    }
}
