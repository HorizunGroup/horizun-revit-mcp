// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code.
//
// WHAT A PLAN MUST STILL BE TRUE OF BEFORE ANYTHING IS WRITTEN.
//
// horizun_apply_cad_plan has re-measured its binding since it existed: the drawing,
// the rules, the actions, the document, the Revit build, the reading, and - since
// block 8 - the identity of the whole source SET and whether the link and the files
// can be shown to be the same issue.
//
// horizun_apply_cad_update had none of it. MEASURED by reading it: it checks that
// 'actions' is a list, that provenance is present, that a placement move is consented
// to again, and that the reading version matches. It never reads apply_binding at
// all - the plan emits one and nothing consumes it - so the actions could be edited,
// the drawing revised, the link reloaded or the requirement set swapped, and the
// update would delete fittings, re-shape runs and re-stamp provenance anyway.
//
// An update writes MORE dangerously than a first conversion: it deletes, it re-shapes
// elements somebody may have touched, and it rewrites the record that says where they
// came from. So the check lives here, once, and both commands call it. Two copies of
// a rule this important is how one of them quietly stops being true.
//
// The order matters and is deliberate:
//
//   1. DRIFT  - something moved between the plan and this apply. Named one by one,
//               because "stale" without a noun sends the reader to look everywhere.
//   2. COHERENCE - the prior question: can the geometry the plan was made of and the
//               files its sizes came from be shown to be the same issue at all? A
//               plan made while that could not be shown is not applied because the
//               model looks unchanged; it is not applied because nobody measured it.
//
// Both refuse with NOTHING WRITTEN, and both name what to do next.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What the world says NOW, for the values a binding recorded when the plan was made.</summary>
    public sealed class CadApplyNow
    {
        public string ActionsFingerprint;
        public string SourceFingerprint;
        public string SourceSetSha256;
        public string RequirementSetSha256;
        public string InterpretationVersion;
        public string TargetDocument;
        public string RevitVersion;
        /// <summary>The geometry the CAD link holds now - what a reload changes and nothing else records.</summary>
        public string LinkGeometryFingerprint;
        /// <summary>element id -> a fingerprint of what the apply is about to touch, re-read now.</summary>
        public Dictionary<long, string> Touched = new Dictionary<long, string>();
    }

    public static class CadApplyGuard
    {
        /// <summary>
        /// Everything that moved between the plan and this apply, one entry each. Empty means nothing did.
        /// A binding that does not carry a value is not held to it: an older plan is not made stale by a
        /// check that did not exist when it was made.
        /// </summary>
        public static JArray Drift(JObject binding, CadApplyNow now)
        {
            var drift = new JArray();
            if (binding == null || now == null) return drift;

            Add(drift, binding, "actions_fingerprint", now.ActionsFingerprint, "the actions",
                "the actions submitted are not the actions the plan emitted. A coordinate, a type, an " +
                "element or the order differs. NOTHING was written.");
            Add(drift, binding, "source_fingerprint", now.SourceFingerprint, "the drawing",
                "the file, its bytes, its path, its load state, its declared units or its transform changed " +
                "since the plan was made. The plan describes a different drawing.");
            Add(drift, binding, "source_set_sha256", now.SourceSetSha256, "the drawing's references",
                "the drawing or one of its external references was revised after this plan was made. The " +
                "host file alone may be untouched; the set is not.");
            // A RELOAD MOVES NONE OF THE VALUES ABOVE. The file can be the same bytes at the same path
            // with the same transform, and the link can be showing something else entirely because
            // somebody reloaded it - which is exactly the remedy this bridge recommends elsewhere. The
            // plan was made from the geometry the link held THEN.
            Add(drift, binding, "link_geometry_fingerprint", now.LinkGeometryFingerprint, "the link's geometry",
                "the CAD link was reloaded or repointed after this plan was made, so the geometry the plan " +
                "was read from is not the geometry in the model now. Re-plan against what is loaded.");
            Add(drift, binding, "requirement_set_sha256", now.RequirementSetSha256, "the requirement set",
                "the rules that decided what this drawing means are not the rules the plan used.");
            Add(drift, binding, "interpretation_version", now.InterpretationVersion, "the reading",
                "this build reads drawings differently from the build that made the plan; the same bytes " +
                "may now mean other elements.");
            Add(drift, binding, "target_document", now.TargetDocument, "the target document",
                "this plan was made against a different model. Applying it here would put one model's " +
                "drawing into another.");
            Add(drift, binding, "revit_version", now.RevitVersion, "the Revit build",
                "the plan was made against a different Revit; type and level resolution are not guaranteed " +
                "to mean the same thing across builds.");

            // THE ELEMENTS THE ACTIONS ARE ABOUT, re-read. Everything above checks the drawing and the
            // request; this checks the MODEL. Between a plan and its apply somebody can move, resize or
            // reconnect exactly the run this update is about to re-shape - and the plan, which classified
            // it when it was still where it was built, would carry on and take that work away.
            var touched = binding["touched_elements"] as JArray;
            if (touched != null)
            {
                foreach (JObject t in touched.OfType<JObject>())
                {
                    long id = t.Value<long?>("element_id") ?? -1;
                    string then = t.Value<string>("fingerprint");
                    string nowPrint;
                    bool known = now.Touched.TryGetValue(id, out nowPrint);
                    if (!known)
                        drift.Add(new JObject
                        {
                            ["what"] = "element " + id.ToString(CultureInfo.InvariantCulture),
                            ["planned_against"] = then,
                            ["now"] = "it could not be re-read",
                            ["means"] = "this plan acts on element " + id + " and it cannot be read now - " +
                                        "deleted, or in another document. NOTHING was written."
                        });
                    else if (!string.Equals(then, nowPrint, StringComparison.Ordinal))
                        drift.Add(new JObject
                        {
                            ["what"] = "element " + id.ToString(CultureInfo.InvariantCulture),
                            ["planned_against"] = then,
                            ["now"] = nowPrint,
                            ["means"] = "element " + id + " is not as it was when this plan was made: its line, " +
                                        "its size or its type changed, which is a person's work if nothing in " +
                                        "this plan did it. Applying would take that work away. NOTHING was written."
                        });
                }
            }
            return drift;
        }

        private static void Add(JArray drift, JObject binding, string key, string now, string what, string means)
        {
            string then = binding.Value<string>(key);
            if (string.IsNullOrWhiteSpace(then) || string.IsNullOrWhiteSpace(now)) return;
            if (string.Equals(then, now, StringComparison.Ordinal)) return;
            drift.Add(new JObject { ["what"] = what, ["planned_against"] = then, ["now"] = now, ["means"] = means });
        }

        public static string StalePlanMessage(JArray drift)
        {
            return "stale_plan: " + string.Join(" and ", drift.OfType<JObject>().Select(d => (string)d["what"])) +
                   " moved between the plan and this apply. NOTHING WAS WRITTEN. Re-run the planner and review " +
                   "the new plan; a plan aimed at a drawing that has since changed is a plan aimed at a " +
                   "different building. Drift: " + drift.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// The prior question. Returns null when the plan may proceed, or the refusal detail when it may
        /// not: either the correspondence cannot be shown NOW, or the plan was made while it could not be.
        /// A reload after the fact fixes the model's state, not the plan - the actions still describe what
        /// was read earlier.
        /// </summary>
        public static JObject CoherenceRefusal(JObject binding, JObject coherenceNow, out string message)
        {
            message = null;
            if (coherenceNow == null) return null;
            bool applicableNow = coherenceNow.Value<bool?>("applicable") ?? false;
            string planned = binding == null ? null : binding.Value<string>("coherence_state");
            if (!applicableNow)
            {
                message = "plan_not_applicable: " + coherenceNow.Value<string>("state") + ". " +
                          coherenceNow.Value<string>("means") + " NOTHING WAS WRITTEN. " +
                          coherenceNow.Value<string>("remedy");
                return new JObject
                {
                    ["refused"] = "plan_not_applicable",
                    ["coherence_now"] = coherenceNow,
                    ["coherence_when_planned"] = planned,
                    ["means"] = "a plan is applied only when this bridge can SHOW that the geometry it was made " +
                                "of and the files its sizes came from are the same issue of the drawing. " +
                                "Warning and writing anyway would put one issue's runs in the model with " +
                                "another issue's sizes, and the model would look finished."
                };
            }
            if (!string.IsNullOrWhiteSpace(planned) &&
                !string.Equals(planned, CadSourceCoherenceRules.Aligned, StringComparison.Ordinal))
            {
                message = "plan_not_applicable: this plan was made while the coherence of its sources was '" +
                          planned + "', so its actions were read from a state nobody could vouch for. The link " +
                          "is coherent NOW - plan again against it and apply that plan. NOTHING WAS WRITTEN.";
                return new JObject
                {
                    ["refused"] = "plan_not_applicable",
                    ["coherence_when_planned"] = planned,
                    ["coherence_now"] = coherenceNow,
                    ["means"] = "the remedy was applied after the plan was made, which fixes the model's state " +
                                "and not the plan: the actions still describe what was read earlier."
                };
            }
            return null;
        }
    }
}
