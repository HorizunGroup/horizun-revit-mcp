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
            JObject setState = CadDwgCache.SetState(path, fileSha);
            JObject record = CadLinkLoads.Read(doc, uid);

            o["sources_now"] = new JObject
            {
                ["file"] = path,
                ["file_sha256"] = fileSha,
                ["source_set_sha256"] = setNow,
                ["set_means"] = "the host file and every external reference resolved from beside it. This is " +
                                "what moves when a drawing is revised; the host's own hash does not have to.",
                ["set_state"] = setState
            };

            // A REFERENCE THAT MOVED IS NOT AN UNKNOWN SET. The cache knows the name of what changed, and
            // saying "the identity could not be computed" sends the reader to look for a missing file
            // instead of at the revision they just received.
            if (!fromSnapshot && record != null && setState.Value<string>("state") == "changed" &&
                !string.IsNullOrWhiteSpace(record.Value<string>("source_set_sha256")))
            {
                o["state"] = NotAligned;
                o["applicable"] = false;
                bool missing = (setState["absent"] as JArray)?.Count > 0 &&
                               (setState["changed"] as JArray)?.Count == 0;
                o["why"] = missing ? "a_reference_of_the_drawing_is_missing" : "a_reference_of_the_drawing_changed";
                o["differs"] = new JObject
                {
                    ["source_set_when_loaded"] = record["source_set_sha256"],
                    ["source_set_now"] = "(not computable until the drawing is read again)",
                    ["references_that_changed"] = setState["changed"],
                    ["references_that_are_missing"] = setState["absent"]
                };
                o["means"] = missing
                    ? "a reference this drawing needs no longer resolves from beside it: " +
                      setState["absent"].ToString(Newtonsoft.Json.Formatting.None) + ". Nothing can be read " +
                      "from a set with a file missing, and the geometry here is whatever was loaded before."
                    : "the drawing was revised through its references after this link was loaded: " +
                      setState["changed"].ToString(Newtonsoft.Json.Formatting.None) + " no longer hashes as " +
                      "it did. The geometry here is the older issue.";
                o["remedy"] = missing
                    ? "put the missing reference back where the drawing looks for it, then reload the link " +
                      "with horizun_manage_cad_links and plan again."
                    : "horizun_manage_cad_links operation=reload on this instance, then plan again.";
                return o;
            }

            string printNow = record == null ? null : GeometryFingerprint(doc, instance);

            // THE WINDOW BETWEEN A LOAD AND THE FIRST READ OF WHAT IT LOADED.
            //
            // A load records the set identity from what this machine already knows about the file. After a
            // REPOINT - which is how a revision arrives - the new drawing has never been read here, so the
            // references are unknown and the record's set is null. The next plan then compares a number
            // against nothing and answers coherence_unknown, and the caller is told to reload a link that
            // was just loaded. MEASURED on the fixture: the ordinary repoint-then-plan order produced that
            // every time.
            //
            // So the set is LEARNED at the first read after the load, and the record says so. It is not a
            // claim about the moment of loading: anything that changed between the load and this first
            // read is inside that window and cannot be seen. The window is named in the reply rather than
            // smoothed over, and it only ever closes once - a record that already carries a set is never
            // rewritten here, because that would quietly move the baseline a comparison depends on.
            if (record != null && string.IsNullOrWhiteSpace(record.Value<string>("source_set_sha256")) &&
                !string.IsNullOrWhiteSpace(setNow) &&
                string.Equals(record.Value<string>("geometry_fingerprint"), printNow, StringComparison.Ordinal))
            {
                CadLinkLoads.LearnSet(doc, uid, setNow);
                record = CadLinkLoads.Read(doc, uid) ?? record;
                o["source_set_learned_at_first_read"] = true;
                o["source_set_learned_means"] =
                    "this link was loaded before anything on this machine had read the drawing, so its " +
                    "references were unknown and the record carried no set. It was learned on this read, " +
                    "with the link still fingerprinting as it did when it was loaded. Any change between " +
                    "the load and this read is inside that window and was not seen.";
            }
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
