using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// WHAT AN APPLY MUST REFUSE.
    ///
    /// horizun_apply_cad_plan has re-measured its binding since it existed. horizun_apply_cad_update
    /// never read one: MEASURED by reading the command, it checked that actions was a list, that
    /// provenance was present, that a placement move was consented to again, and that the reading
    /// version matched - and then deleted fittings, re-shaped runs and rewrote provenance.
    ///
    /// These fix the seven situations that must stop it, and the one shape that may proceed. They are
    /// about the DECISION, which is why they run without Revit: the commands measure, this decides.
    /// </summary>
    public class CadApplyGuardTests
    {
        private static JObject Binding(params (string, object)[] overrides)
        {
            var b = new JObject
            {
                ["actions_fingerprint"] = "acts:1",
                ["source_fingerprint"] = "cadsrc:host1",
                ["source_set_sha256"] = "set:AAA",
                ["link_geometry_fingerprint"] = "geo:1",
                ["requirement_set_sha256"] = "rules:1",
                ["interpretation_version"] = "ir:1",
                ["target_document"] = "MODEL",
                ["revit_version"] = "2026",
                ["coherence_state"] = CadSourceCoherenceRules.Aligned,
                ["touched_elements"] = new JArray(new JObject { ["element_id"] = 5001L, ["fingerprint"] = "el:aaa" })
            };
            foreach (var (k, v) in overrides) b[k] = JToken.FromObject(v);
            return b;
        }

        private static CadApplyNow Now(string actions = "acts:1", string src = "cadsrc:host1", string set = "set:AAA",
                                       string geo = "geo:1", string rules = "rules:1", string ir = "ir:1",
                                       string doc = "MODEL", string revit = "2026", string el = "el:aaa")
        {
            var n = new CadApplyNow
            {
                ActionsFingerprint = actions, SourceFingerprint = src, SourceSetSha256 = set,
                LinkGeometryFingerprint = geo, RequirementSetSha256 = rules, InterpretationVersion = ir,
                TargetDocument = doc, RevitVersion = revit
            };
            if (el != null) n.Touched[5001L] = el;
            return n;
        }

        private static string[] What(JArray drift) =>
            drift.OfType<JObject>().Select(d => (string)d["what"]).ToArray();

        [Fact]
        public void Nothing_moved_is_no_drift_at_all()
        {
            Assert.Empty(CadApplyGuard.Drift(Binding(), Now()));
        }

        // ---- A: a label changed after planning ----------------------------------
        // The host file need not move: a label lives in the xref, and the SET is what sees it.
        [Fact]
        public void A_label_changed_after_planning_is_the_drawings_references()
        {
            JArray d = CadApplyGuard.Drift(Binding(), Now(set: "set:BBB"));
            Assert.Equal(new[] { "the drawing's references" }, What(d));
            Assert.Contains("host file alone may be untouched", (string)d[0]["means"]);
        }

        // ---- B: geometry changed after planning ---------------------------------
        [Fact]
        public void B_geometry_changed_after_planning_is_caught_whether_the_host_moved_or_not()
        {
            Assert.Equal(new[] { "the drawing's references" }, What(CadApplyGuard.Drift(Binding(), Now(set: "set:CCC"))));
            Assert.Equal(new[] { "the drawing", "the drawing's references" },
                         What(CadApplyGuard.Drift(Binding(), Now(src: "cadsrc:host2", set: "set:CCC"))));
        }

        // ---- C: a nested xref changed -------------------------------------------
        // Two levels down is still the set: the identity is host plus every reference resolved from
        // beside it, and campaign 8 measured it moving for a file the host never names directly.
        [Fact]
        public void C_a_nested_reference_moves_the_set_like_any_other()
        {
            Assert.Equal(new[] { "the drawing's references" }, What(CadApplyGuard.Drift(Binding(), Now(set: "set:NESTED"))));
        }

        // ---- D: a source that is no longer there --------------------------------
        // The set cannot be identified, so the guard measures nothing and says nothing: what refuses
        // is the coherence, which is the honest place for "this cannot be shown".
        [Fact]
        public void D_a_missing_source_is_not_drift_but_it_is_not_applicable_either()
        {
            Assert.Empty(CadApplyGuard.Drift(Binding(), Now(set: null)));
            JObject unknown = CadSourceCoherenceRules.Decide(null, null, "sha", "geo:1", false);
            string message;
            Assert.NotNull(CadApplyGuard.CoherenceRefusal(Binding(), unknown, out message));
            Assert.StartsWith("plan_not_applicable: coherence_unknown", message);
        }

        // ---- E: the link reloaded after the plan --------------------------------
        // The file can be the same bytes at the same path with the same transform, and the link can be
        // showing something else. Nothing else in the binding sees it.
        [Fact]
        public void E_a_link_reloaded_after_the_plan_is_drift_on_its_own()
        {
            JArray d = CadApplyGuard.Drift(Binding(), Now(geo: "geo:2"));
            Assert.Equal(new[] { "the link's geometry" }, What(d));
            Assert.Contains("reloaded or repointed", (string)d[0]["means"]);
        }

        // ---- F: an element or fitting edited by hand ----------------------------
        [Fact]
        public void F_an_element_the_actions_touch_that_somebody_moved_is_drift_by_name()
        {
            JArray d = CadApplyGuard.Drift(Binding(), Now(el: "el:zzz"));
            Assert.Equal(new[] { "element 5001" }, What(d));
            Assert.Contains("person's work", (string)d[0]["means"]);
        }

        [Fact]
        public void F_an_element_the_actions_touch_that_is_gone_is_drift_too()
        {
            JArray d = CadApplyGuard.Drift(Binding(), Now(el: null));
            Assert.Equal(new[] { "element 5001" }, What(d));
            Assert.Contains("cannot be read now", (string)d[0]["means"]);
        }

        // ---- G: a plan made while the coherence could not be shown ---------------
        [Fact]
        public void G_a_plan_made_under_unknown_or_not_aligned_is_refused_even_when_all_is_well_now()
        {
            JObject alignedNow = CadSourceCoherenceRules.Decide(
                new JObject { ["source_set_sha256"] = "set:AAA", ["geometry_fingerprint"] = "geo:1",
                              ["file_sha256"] = "sha" }, "set:AAA", "sha", "geo:1", false);
            Assert.True(alignedNow.Value<bool>("applicable"));

            foreach (string was in new[] { CadSourceCoherenceRules.Unknown, CadSourceCoherenceRules.NotAligned,
                                           CadSourceCoherenceRules.Snapshot })
            {
                string message;
                JObject refusal = CadApplyGuard.CoherenceRefusal(
                    Binding(("coherence_state", was)), alignedNow, out message);
                Assert.NotNull(refusal);
                Assert.Equal(was, refusal.Value<string>("coherence_when_planned"));
                Assert.Contains("fixes the model's state and not the plan", refusal.Value<string>("means"));
            }
        }

        [Fact]
        public void An_aligned_plan_on_an_aligned_model_is_the_only_one_that_proceeds()
        {
            JObject alignedNow = CadSourceCoherenceRules.Decide(
                new JObject { ["source_set_sha256"] = "set:AAA", ["geometry_fingerprint"] = "geo:1",
                              ["file_sha256"] = "sha" }, "set:AAA", "sha", "geo:1", false);
            string message;
            Assert.Null(CadApplyGuard.CoherenceRefusal(Binding(), alignedNow, out message));
            Assert.Null(message);
        }

        [Fact]
        public void A_binding_that_never_carried_a_value_is_not_held_to_it()
        {
            // an older plan, from before a check existed, is not made stale by the check
            var old = new JObject { ["actions_fingerprint"] = "acts:1" };
            Assert.Empty(CadApplyGuard.Drift(old, Now(set: "set:DIFFERENT", geo: "geo:9")));
        }

        [Fact]
        public void Every_drift_entry_names_what_moved_and_what_it_means()
        {
            JArray d = CadApplyGuard.Drift(Binding(), Now(actions: "acts:2", src: "cadsrc:2", set: "set:2",
                                                          geo: "geo:2", rules: "rules:2", ir: "ir:2",
                                                          doc: "OTHER", revit: "2025", el: "el:2"));
            Assert.Equal(9, d.Count);
            Assert.All(d.OfType<JObject>(), e =>
            {
                Assert.False(string.IsNullOrWhiteSpace((string)e["what"]));
                Assert.False(string.IsNullOrWhiteSpace((string)e["means"]));
                Assert.NotNull(e["planned_against"]);
                Assert.NotNull(e["now"]);
            });
            Assert.Contains("NOTHING WAS WRITTEN", CadApplyGuard.StalePlanMessage(d));
        }
    }
}
