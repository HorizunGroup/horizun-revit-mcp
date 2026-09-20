// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// THE DECISION BEHIND CadSourceCoherence, with no Revit in it.
//
// Four facts go in - what was recorded when this bridge loaded the link, the source
// set as it is now, the link's geometry fingerprint as it is now, and whether the
// reading continued a snapshot - and one of four states comes out. Keeping it here
// is what makes the rule testable: the Revit side measures, this side decides, and
// the decision is the part that must not drift.
//
// The rule is one-directional on purpose. Only "the record describes what is loaded
// AND the sources still hash as they did" grants applicable; every other shape of
// the evidence withholds it and says which shape it was. A caller who wants the
// permission gets it by reloading the link through the bridge, which is one typed
// call and is verified by the geometry it returns.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadSourceCoherenceRules
    {
        public const string Aligned = "sources_match_the_link";
        public const string NotAligned = "revisions_not_aligned";
        public const string Unknown = "coherence_unknown";
        public const string Snapshot = "continued_snapshot";

        /// <summary>
        /// <paramref name="record"/> is what CadLinkLoads wrote when this bridge loaded the link, or null.
        /// The two "now" values are measured by the caller. Returns state, applicable, why and the sentences.
        /// </summary>
        public static JObject Decide(JObject record, string setNow, string fileShaNow, string printNow,
                                     bool fromSnapshot)
        {
            var o = new JObject();
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

            string printThen = record.Value<string>("geometry_fingerprint");
            if (string.IsNullOrWhiteSpace(printThen) || string.IsNullOrWhiteSpace(printNow) ||
                !string.Equals(printThen, printNow, StringComparison.Ordinal))
            {
                o["state"] = Unknown;
                o["applicable"] = false;
                o["why"] = string.IsNullOrWhiteSpace(printThen) || string.IsNullOrWhiteSpace(printNow)
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
                ["file_sha256_now"] = fileShaNow,
                ["host_file_changed"] = !string.Equals(record.Value<string>("file_sha256"), fileShaNow, StringComparison.Ordinal),
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
