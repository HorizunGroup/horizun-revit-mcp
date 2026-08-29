// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The materialised plan. These are the cases a real model will not produce on
// request: a collector enumerating the same elements in a different order, a
// third party saving a change between a dry run and an apply, somebody else
// setting the very parameter this run was about to set. All of it is arithmetic
// over strings, so all of it can be proved here instead of hoped for.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ResolvedPlanTests
    {
        private static PlannedElement El(string uid, PlannedAction action = PlannedAction.Delete,
                                         string typeName = "T", string fireRating = null)
        {
            var e = new PlannedElement
            {
                UniqueId = uid,
                Category = "Walls",
                TypeName = typeName,
                Level = "L1",
                Action = action
            };
            if (fireRating != null)
                e.BeforeValues = new Dictionary<string, string> { { "Fire Rating", fireRating } };
            return e;
        }

        private static ResolvedPlan Plan(params PlannedElement[] els)
        {
            var p = new ResolvedPlan
            {
                Command = "horizun_delete_verified",
                DocumentKey = "doc-1",
                RevitVersion = "2026",
                DocumentFingerprint = "fp-1"
            };
            p.Elements.AddRange(els);
            return p;
        }

        /// <summary>
        /// Revit is free to enumerate a collector in whatever order it likes between two
        /// calls. That is not a change to the plan, and treating it as one would make every
        /// apply fail for a reason nobody could act on.
        /// </summary>
        [Fact]
        public void The_order_elements_come_back_in_is_not_part_of_the_plan()
        {
            string a = Plan(El("u1"), El("u2"), El("u3")).Fingerprint();
            string b = Plan(El("u3"), El("u1"), El("u2")).Fingerprint();
            Assert.Equal(a, b);
        }

        /// <summary>The whole point: one more matching element is a different plan.</summary>
        [Fact]
        public void An_element_that_appears_after_the_dry_run_changes_the_fingerprint()
        {
            string approved = Plan(El("u1"), El("u2")).Fingerprint();
            string now = Plan(El("u1"), El("u2"), El("u3")).Fingerprint();
            Assert.NotEqual(approved, now);
        }

        [Fact]
        public void An_element_that_disappears_changes_the_fingerprint()
        {
            Assert.NotEqual(Plan(El("u1"), El("u2")).Fingerprint(),
                            Plan(El("u1")).Fingerprint());
        }

        /// <summary>
        /// The quiet one. Same elements, same count - but somebody else already wrote the
        /// value this run was about to write. A request hash cannot see it and neither can a
        /// count; only the before-values can.
        /// </summary>
        [Fact]
        public void A_value_edited_by_somebody_else_changes_the_fingerprint()
        {
            string approved = Plan(El("u1", PlannedAction.Modify, fireRating: "60")).Fingerprint();
            string now = Plan(El("u1", PlannedAction.Modify, fireRating: "120")).Fingerprint();
            Assert.NotEqual(approved, now);
        }

        [Fact]
        public void A_type_swap_between_rehearsal_and_apply_changes_the_fingerprint()
        {
            Assert.NotEqual(Plan(El("u1", typeName: "Generic - 200mm")).Fingerprint(),
                            Plan(El("u1", typeName: "Generic - 300mm")).Fingerprint());
        }

        /// <summary>
        /// A plan is never carried across a Revit upgrade or into another document, even if
        /// the elements happen to render the same.
        /// </summary>
        [Fact]
        public void The_document_and_the_revit_version_are_part_of_the_plan()
        {
            var p1 = Plan(El("u1"));
            var p2 = Plan(El("u1")); p2.DocumentKey = "doc-2";
            var p3 = Plan(El("u1")); p3.RevitVersion = "2027";
            var p4 = Plan(El("u1")); p4.DocumentFingerprint = "fp-2";
            Assert.NotEqual(p1.Fingerprint(), p2.Fingerprint());
            Assert.NotEqual(p1.Fingerprint(), p3.Fingerprint());
            Assert.NotEqual(p1.Fingerprint(), p4.Fingerprint());
        }

        /// <summary>
        /// A cascade nobody predicted is a different operation from the one approved, even
        /// when the listed elements are identical.
        /// </summary>
        [Fact]
        public void An_unpredicted_cascade_changes_the_fingerprint()
        {
            var approved = Plan(El("u1")); approved.ExpectedCascadeCount = 0;
            var now = Plan(El("u1")); now.ExpectedCascadeCount = 4;
            Assert.NotEqual(approved.Fingerprint(), now.Fingerprint());
        }

        /// <summary>
        /// Type and parameter names in this domain contain quotes, commas and brackets -
        /// 'Tee 3" x 1 1/2"' is real. Two DIFFERENT plans must not be able to render into the
        /// same string by having a name that contains the separator.
        /// </summary>
        [Fact]
        public void A_name_containing_separators_cannot_forge_another_plan()
        {
            string a = Plan(El("u1", typeName: "Tee 3\" x 1 1/2\""), El("u2", typeName: "X")).Fingerprint();
            string b = Plan(El("u1", typeName: "Tee 3\" x 1 1/2\"|u2|X"), El("u2", typeName: "")).Fingerprint();
            Assert.NotEqual(a, b);
        }

        /// <summary>The refusal has to say WHAT moved, or somebody diffs two runs by hand.</summary>
        [Fact]
        public void The_drift_description_names_what_actually_changed()
        {
            string appeared = ResolvedPlan.DescribeDrift(Plan(El("u1")), Plan(El("u1"), El("u2")));
            Assert.Contains("1 element(s) now match that did not", appeared);

            string gone = ResolvedPlan.DescribeDrift(Plan(El("u1"), El("u2")), Plan(El("u1")));
            Assert.Contains("no longer match", gone);

            string counts = ResolvedPlan.DescribeDrift(Plan(El("u1"), El("u2")), Plan(El("u1")));
            Assert.Contains("deletion count moved from 2 to 1", counts);
        }

        /// <summary>
        /// Same elements, same counts, different value: the description must not go silent,
        /// because this is exactly the case a human would otherwise not understand.
        /// </summary>
        [Fact]
        public void The_drift_description_names_the_value_that_changed_and_both_sides()
        {
            string why = ResolvedPlan.DescribeDrift(
                Plan(El("u1", PlannedAction.Modify, fireRating: "60")),
                Plan(El("u1", PlannedAction.Modify, fireRating: "120")));

            // "a value changed" is not something anybody can act on. The field and both
            // sides of it are what turn a stale refusal into a next step.
            Assert.Contains("u1", why);
            Assert.Contains("60", why);
            Assert.Contains("120", why);
        }

        /// <summary>
        /// The one case where naming the field is not enough: a link binding is four
        /// facts packed into one value, and "this opaque string changed" would send the
        /// reader looking for the wrong thing. It is decoded to the structured code.
        /// </summary>
        [Fact]
        public void A_moved_link_is_described_by_its_structured_code_rather_than_by_two_hashes()
        {
            var before = new PlannedElement
            {
                UniqueId = "action:0", Category = "dimension", Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                { { "ref.0.link", "inst-1|MOD_EST|aaaa|transform-A" } }
            };
            var after = new PlannedElement
            {
                UniqueId = "action:0", Category = "dimension", Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                { { "ref.0.link", "inst-1|MOD_EST|aaaa|transform-B" } }
            };

            string why = ResolvedPlan.DescribeDrift(Plan(before), Plan(after));

            Assert.Contains(LinkedReferenceRules.CodeLinkTransformMoved, why);
            Assert.DoesNotContain("transform-A", why);
        }

        [Fact]
        public void A_replaced_linked_document_is_described_by_its_own_code()
        {
            var before = new PlannedElement
            {
                UniqueId = "action:0", Category = "dimension", Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                { { "ref.0.link", "inst-1|MOD_EST|aaaa|transform-A" } }
            };
            var after = new PlannedElement
            {
                UniqueId = "action:0", Category = "dimension", Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                { { "ref.0.link", "inst-1|MOD_OTHER|aaaa|transform-A" } }
            };

            Assert.Contains(LinkedReferenceRules.CodeLinkedDocumentChanged,
                            ResolvedPlan.DescribeDrift(Plan(before), Plan(after)));
        }

        /// <summary>
        /// A value that is NOT a packed link binding must not be forced through the
        /// link decoder: it falls back to printing both sides, which is still true.
        /// </summary>
        [Fact]
        public void A_link_key_that_is_not_four_fields_falls_back_to_showing_both_sides()
        {
            var before = new PlannedElement
            {
                UniqueId = "action:0", Category = "dimension", Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string> { { "ref.0.link", "corrupted" } }
            };
            var after = new PlannedElement
            {
                UniqueId = "action:0", Category = "dimension", Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string> { { "ref.0.link", "also-corrupted" } }
            };

            string why = ResolvedPlan.DescribeDrift(Plan(before), Plan(after));
            Assert.Contains("corrupted", why);
            Assert.Contains("also-corrupted", why);
        }

        /// <summary>
        /// A plan with many changed values must stay readable. The description names a
        /// bounded number of them and says outright that it stopped, rather than either
        /// printing a wall of text or implying those were all of them.
        /// </summary>
        [Fact]
        public void A_plan_with_many_changed_values_names_a_bounded_number_and_says_it_stopped()
        {
            var beforeValues = new Dictionary<string, string>();
            var afterValues = new Dictionary<string, string>();
            for (int i = 0; i < 40; i++)
            {
                beforeValues["field" + i] = "a";
                afterValues["field" + i] = "b";
            }
            var before = new PlannedElement
            {
                UniqueId = "u1", Category = "wall", Action = PlannedAction.Modify, BeforeValues = beforeValues
            };
            var after = new PlannedElement
            {
                UniqueId = "u1", Category = "wall", Action = PlannedAction.Modify, BeforeValues = afterValues
            };

            string why = ResolvedPlan.DescribeDrift(Plan(before), Plan(after));

            Assert.Contains("not listed", why);
            Assert.True(why.Length < 2000, "a drift description nobody can read is not a description.");
        }

        [Fact]
        public void An_identical_plan_fingerprints_identically()
        {
            Assert.Equal(Plan(El("u1"), El("u2")).Fingerprint(),
                         Plan(El("u1"), El("u2")).Fingerprint());
        }
    }

    public class StalePlanConfirmationTests
    {
        private static ResolvedPlan Plan(int n)
        {
            var p = new ResolvedPlan { Command = "c", DocumentKey = "d", RevitVersion = "2026", DocumentFingerprint = "f" };
            for (int i = 0; i < n; i++)
                p.Elements.Add(new PlannedElement { UniqueId = "u" + i, Action = PlannedAction.Delete });
            return p;
        }

        /// <summary>
        /// The failure this story exists for, end to end: identical request, same document,
        /// live token - and the model moved. It must not execute.
        /// </summary>
        [Fact]
        public void A_token_is_refused_when_the_element_set_moved()
        {
            var store = new ConfirmationStore();
            ResolvedPlan rehearsed = Plan(2);
            Confirmation c = store.Issue("c", "d", "plan-hash", null, rehearsed.Fingerprint());

            ResolvedPlan atApply = Plan(3);   // one more element matches now
            ConfirmationCheck check = store.Validate(c.Token, "c", "d", "plan-hash",
                atApply.Fingerprint(), ResolvedPlan.DescribeDrift(rehearsed, atApply));

            Assert.False(check.Ok);
            Assert.Equal(ConfirmationState.StalePlan, check.State);
            Assert.Contains("THE MODEL MOVED", check.Message);
            Assert.Contains("now match that did not", check.Message);
        }

        [Fact]
        public void A_token_is_accepted_when_the_model_is_unchanged()
        {
            var store = new ConfirmationStore();
            ResolvedPlan p = Plan(2);
            Confirmation c = store.Issue("c", "d", "plan-hash", null, p.Fingerprint());
            ConfirmationCheck check = store.Validate(c.Token, "c", "d", "plan-hash", Plan(2).Fingerprint(), null);
            Assert.True(check.Ok);
            Assert.Null(check.Message);
        }

        /// <summary>
        /// A refused apply must NOT spend the token: the caller re-runs the dry run and
        /// approves the current plan, and a token burned by the refusal would make the
        /// refusal itself destroy the thing needed to recover from it.
        /// </summary>
        [Fact]
        public void A_stale_plan_does_not_consume_the_approval()
        {
            var store = new ConfirmationStore();
            ResolvedPlan rehearsed = Plan(2);
            Confirmation c = store.Issue("c", "d", "plan-hash", null, rehearsed.Fingerprint());

            store.Validate(c.Token, "c", "d", "plan-hash", Plan(3).Fingerprint(), "drift");
            // The model goes back to what was approved - a colleague undid their change.
            ConfirmationCheck again = store.Validate(c.Token, "c", "d", "plan-hash", Plan(2).Fingerprint(), null);
            Assert.True(again.Ok);
        }

        /// <summary>
        /// A command that has not been taught to materialise its plan must not appear to have
        /// been checked against the model. Silence there is the substitution this repository
        /// exists to refuse.
        /// </summary>
        private static ResolvedPlan LinkPlan(string transform)
        {
            // The identity is "title|path-hash" - the REAL five-segment shape the live
            // model packs (measured 2026-08-26), not the four-segment simplification
            // that let the parser require an exact count and miss every real value.
            var p = new ResolvedPlan { Command = "c", DocumentKey = "d", RevitVersion = "2026", DocumentFingerprint = "f" };
            p.Elements.Add(new PlannedElement
            {
                UniqueId = "action:0", Category = "dimension", Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                    { { "ref.1.link", "inst-1|HZ_LINKSRC|ab12cd34ef567890|aaaa|" + transform } }
            });
            return p;
        }

        /// <summary>
        /// The stale refusal must NAME what moved even when the apply call kept no copy of
        /// the rehearsed plan - which is every real apply, because the rehearsal lived in a
        /// different MCP call. The store kept the plan with the token; validating with the
        /// plan recomputed NOW is enough to get the decoded link code into the sentence.
        /// This is the seat dp2 case 6 measures live: before it, the message was the
        /// generic MODEL MOVED sentence with no field named at all.
        /// </summary>
        [Fact]
        public void The_store_names_link_drift_from_the_plan_it_kept_with_the_token()
        {
            var store = new ConfirmationStore();
            ResolvedPlan rehearsed = LinkPlan("transform-A");
            ResolvedPlan atApply = LinkPlan("transform-B");

            Confirmation c = store.Issue("horizun_annotate", "d", "req-hash", null,
                                         rehearsed.Fingerprint(), rehearsed);
            ConfirmationCheck check = store.Validate(
                c.Token, "horizun_annotate", "d", "req-hash", atApply, null);

            Assert.False(check.Ok);
            Assert.Equal(ConfirmationState.StalePlan, check.State);
            Assert.Contains(LinkedReferenceRules.CodeLinkTransformMoved, check.Message);
            Assert.Contains("ref.1.link", check.Message);
        }

        /// <summary>
        /// And a caller that DID keep its rehearsal keeps the floor: an explicit drift
        /// description is never overwritten by the stored plan's own diff.
        /// </summary>
        [Fact]
        public void A_caller_supplied_drift_description_wins_over_the_stored_plan()
        {
            var store = new ConfirmationStore();
            ResolvedPlan rehearsed = LinkPlan("transform-A");
            ResolvedPlan atApply = LinkPlan("transform-B");
            Confirmation c = store.Issue("horizun_annotate", "d", "req-hash", null,
                                         rehearsed.Fingerprint(), rehearsed);
            ConfirmationCheck check = store.Validate(
                c.Token, "horizun_annotate", "d", "req-hash", atApply, "the caller's own words");
            Assert.False(check.Ok);
            Assert.Contains("the caller's own words", check.Message);
        }

        [Fact]
        public void A_command_without_a_materialised_plan_says_so_on_success()
        {
            var store = new ConfirmationStore();
            Confirmation c = store.Issue("c", "d", "plan-hash");   // no fingerprint
            ConfirmationCheck check = store.Validate(c.Token, "c", "d", "plan-hash", (string)null, null);
            Assert.True(check.Ok);
            Assert.Contains("REQUEST only", check.Message);
            Assert.Contains("would not have been detected", check.Message);
        }

        /// <summary>
        /// ContextFingerprint carries state the plan DEPENDS ON that is not one of the
        /// elements listed - family_apply's active type being the case it was added for.
        /// Two plans identical in every row, differing only in what was ambient, are not
        /// the same plan: the rehearsal approved a check of THAT type's shape.
        /// </summary>
        [Fact]
        public void Ambient_context_is_part_of_the_plan()
        {
            ResolvedPlan a = OneElementPlan();
            ResolvedPlan b = OneElementPlan();
            a.ContextFingerprint = "active=600mm|dim=Width=1.5";
            b.ContextFingerprint = "active=900mm|dim=Width=1.5";
            Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        }

        /// <summary>
        /// A command that has nothing ambient to declare must not be forced to invent a
        /// value: unset and empty are the same statement, "nothing ambient here". This is
        /// what lets the field be added to one command without every other command's plan
        /// having to change shape.
        /// </summary>
        [Fact]
        public void Unset_context_and_empty_context_are_the_same_statement()
        {
            ResolvedPlan a = OneElementPlan();
            ResolvedPlan b = OneElementPlan();
            a.ContextFingerprint = null;
            b.ContextFingerprint = "";
            Assert.Equal(a.Fingerprint(), b.Fingerprint());
        }

        /// <summary>
        /// A run that writes no rows can still be a run whose ambient state matters - a
        /// family_apply where every requested value already matches still measured a
        /// specific type's shape. An empty element list must not swallow the context.
        /// </summary>
        [Fact]
        public void Context_still_counts_when_the_plan_writes_nothing()
        {
            var a = new ResolvedPlan { Command = "family_apply", DocumentKey = "d" };
            var b = new ResolvedPlan { Command = "family_apply", DocumentKey = "d" };
            a.ContextFingerprint = "active=600mm";
            b.ContextFingerprint = "active=900mm";
            Assert.Empty(a.Elements);
            Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        }

        /// <summary>A minimal plan the context tests can vary one field of.</summary>
        private static ResolvedPlan OneElementPlan()
        {
            var p = new ResolvedPlan { Command = "family_apply", DocumentKey = "doc-1" };
            p.Elements.Add(new PlannedElement
            {
                UniqueId = "param:Width",
                Action = PlannedAction.Modify,
                BeforeValues = new Dictionary<string, string> { { "op", "set" } }
            });
            return p;
        }
    }
}
