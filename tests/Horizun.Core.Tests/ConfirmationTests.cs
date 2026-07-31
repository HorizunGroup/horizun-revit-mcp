// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A destructive command that takes confirm=true is not confirmed: the caller
// agreed to something, and nothing in the request says what. These pin the rule
// that the PLAN is what gets approved, and that an approval stops being valid the
// moment the document or the scope moves under it.
//
// Every case in the brief is here: wrong document, plan changed after the token
// was issued, expired token, and re-use.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ConfirmationTests
    {
        private DateTime _clock = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        private ConfirmationStore NewStore() => new ConfirmationStore(() => _clock);

        private const string Doc = "rvt:2026|guid:abc|path:x|title:tower";
        private const string OtherDoc = "rvt:2026|guid:def|path:y|title:tower";

        [Fact]
        public void A_token_issued_for_this_plan_and_this_document_is_accepted_once()
        {
            var s = NewStore();
            var c = s.Issue("horizun_delete_verified", Doc, "planA");

            var first = s.Validate(c.Token, "horizun_delete_verified", Doc, "planA");
            Assert.True(first.Ok);

            // Single use: the same approval must not execute twice.
            var second = s.Validate(c.Token, "horizun_delete_verified", Doc, "planA");
            Assert.False(second.Ok);
            Assert.Equal(ConfirmationState.AlreadyUsed, second.State);
        }

        [Fact]
        public void A_token_for_another_document_is_refused_and_says_so()
        {
            var s = NewStore();
            var c = s.Issue("horizun_delete_verified", Doc, "planA");

            var check = s.Validate(c.Token, "horizun_delete_verified", OtherDoc, "planA");

            Assert.Equal(ConfirmationState.DocumentChanged, check.State);
            Assert.Contains("DIFFERENT document", check.Message);
            Assert.Contains("Nothing was changed", check.Message);
        }

        [Fact]
        public void A_request_that_differs_from_the_one_rehearsed_is_refused()
        {
            // The hash every caller feeds this is computed from the REQUEST, not from the
            // elements a rehearsal resolved - delete_verified says so itself in its own
            // confirmation_note ("bound to the REQUEST, not to the set of elements this
            // rehearsal found"). So this fires when the caller changed a field between the
            // dry run and the execution, INCLUDING a flag like 'save' that changes nothing
            // about which elements are touched. Measured 2026-07-30: rehearsing
            // family_apply with save=false and executing with save=true was refused here,
            // and the old wording blamed "a different set of elements" - sending the
            // caller to look at a model that had not moved.
            var s = NewStore();
            var c = s.Issue("horizun_delete_verified", Doc, "request-A");

            var check = s.Validate(c.Token, "horizun_delete_verified", Doc, "request-B");

            Assert.Equal(ConfirmationState.PlanChanged, check.State);
            Assert.Contains("NOT THE ONE THAT WAS REHEARSED", check.Message);
            Assert.Contains("Nothing was changed", check.Message);
            // The message must NOT claim this detects model drift: nothing here reads the model.
            Assert.Contains("cannot detect that the model moved", check.Message);
        }

        [Fact]
        public void An_expired_token_is_refused()
        {
            var s = NewStore();
            var c = s.Issue("horizun_delete_verified", Doc, "planA", TimeSpan.FromMinutes(10));

            _clock = _clock.AddMinutes(11);

            var check = s.Validate(c.Token, "horizun_delete_verified", Doc, "planA");
            Assert.Equal(ConfirmationState.Expired, check.State);
            Assert.Contains("expired", check.Message);
        }

        [Fact]
        public void A_token_still_inside_its_window_is_accepted()
        {
            var s = NewStore();
            var c = s.Issue("horizun_delete_verified", Doc, "planA", TimeSpan.FromMinutes(10));

            _clock = _clock.AddMinutes(9);

            Assert.True(s.Validate(c.Token, "horizun_delete_verified", Doc, "planA").Ok);
        }

        [Fact]
        public void A_token_from_one_command_does_not_authorise_another()
        {
            var s = NewStore();
            var c = s.Issue("horizun_set_keynote", Doc, "planA");

            var check = s.Validate(c.Token, "horizun_delete_verified", Doc, "planA");

            Assert.Equal(ConfirmationState.WrongCommand, check.State);
            Assert.Contains("not a session", check.Message);
        }

        [Fact]
        public void An_invented_or_missing_token_is_refused_with_instructions()
        {
            var s = NewStore();

            foreach (string bad in new[] { null, "", "   ", "hz-madeup" })
            {
                var check = s.Validate(bad, "horizun_delete_verified", Doc, "planA");
                Assert.Equal(ConfirmationState.Unknown, check.State);
                Assert.Contains("dry_run=true", check.Message);
            }
        }

        [Fact]
        public void Tokens_are_unique_and_not_guessable_from_each_other()
        {
            var s = NewStore();
            var a = s.Issue("c", Doc, "p");
            var b = s.Issue("c", Doc, "p");

            Assert.NotEqual(a.Token, b.Token);
            Assert.StartsWith("hz-", a.Token);
            Assert.True(a.Token.Length > 20);
        }

        [Fact]
        public void Spent_and_expired_tokens_do_not_accumulate()
        {
            var s = NewStore();
            var a = s.Issue("c", Doc, "p", TimeSpan.FromMinutes(1));
            s.Issue("c", Doc, "p", TimeSpan.FromMinutes(1));
            s.Validate(a.Token, "c", Doc, "p");          // spends one

            _clock = _clock.AddMinutes(5);               // expires the other

            Assert.Equal(0, s.OutstandingCount);
        }

        // ---- the plan hash -----------------------------------------------------

        [Fact]
        public void The_same_plan_hashes_the_same_and_a_different_one_does_not()
        {
            string a = ConfirmationStore.HashPlan("delete", "1", "2", "3");
            string b = ConfirmationStore.HashPlan("delete", "1", "2", "3");
            string c = ConfirmationStore.HashPlan("delete", "1", "2", "4");

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void Adjacent_parts_cannot_run_together_into_the_same_hash()
        {
            // Without a separator, ("ab","c") and ("a","bc") would be one plan.
            Assert.NotEqual(ConfirmationStore.HashPlan("ab", "c"),
                            ConfirmationStore.HashPlan("a", "bc"));
        }

        [Fact]
        public void A_plan_with_more_elements_is_a_different_plan()
        {
            Assert.NotEqual(ConfirmationStore.HashPlan("delete", "1", "2"),
                            ConfirmationStore.HashPlan("delete", "1", "2", "3"));
        }

        // ---- array order is part of the plan -----------------------------------

        /// <summary>
        /// THE ONE THIS EXISTS FOR. write_params_verified takes a LIST OF OPERATIONS, and
        /// two writes to the same parameter apply in order - the last one wins. So these
        /// two requests leave "Width" at 9 and at 3.5 respectively: different models.
        ///
        /// The first version of the plan hash sorted every array before hashing it, on the
        /// reasoning that a set in another order is the same set. These two then hashed
        /// identically, and a token issued for the rehearsal of one was spendable on the
        /// execution of the other - the exact substitution the confirmation exists to stop.
        /// </summary>
        [Fact]
        public void Two_writes_to_one_parameter_in_the_opposite_order_are_a_different_plan()
        {
            JObject Request(double first, double second) => new JObject
            {
                ["writes"] = new JArray
                {
                    new JObject { ["element_id"] = 1234, ["parameter"] = "Width", ["value"] = first },
                    new JObject { ["element_id"] = 1234, ["parameter"] = "Width", ["value"] = second }
                }
            };

            string forwards = ConfirmationStore.PlanHash(Request(3.5, 9), "writes");
            string backwards = ConfirmationStore.PlanHash(Request(9, 3.5), "writes");

            Assert.NotEqual(forwards, backwards);

            // And the token store agrees, which is where it matters: the approval of one
            // must be refused for the other rather than merely hash differently.
            var s = NewStore();
            var token = s.Issue("horizun_write_params_verified", Doc, forwards);
            var check = s.Validate(token.Token, "horizun_write_params_verified", Doc, backwards);

            Assert.False(check.Ok);
            Assert.Equal(ConfirmationState.PlanChanged, check.State);
        }

        [Fact]
        public void The_very_same_request_still_hashes_the_same()
        {
            // Order-sensitivity must not become order-instability: an unchanged request
            // has to keep its token, or every rehearsal would be refused its own plan.
            JObject Request() => new JObject
            {
                ["writes"] = new JArray { new JObject { ["parameter"] = "Width", ["value"] = 3.5 } },
                ["save"] = false
            };

            Assert.Equal(ConfirmationStore.PlanHash(Request(), "writes", "save"),
                         ConfirmationStore.PlanHash(Request(), "writes", "save"));
        }

        [Fact]
        public void Reordering_a_list_of_element_ids_is_also_a_different_plan()
        {
            // Stated plainly rather than hidden: ids ARE order-insensitive in effect, and
            // this refuses them anyway. Telling one kind of array from the other needs to
            // know what the command does with it, and guessing wrong in the permissive
            // direction is what the write case above cost. A re-rehearsal is the price.
            var a = new JObject { ["element_ids"] = new JArray(1, 2, 3) };
            var b = new JObject { ["element_ids"] = new JArray(3, 2, 1) };

            Assert.NotEqual(ConfirmationStore.PlanHash(a, "element_ids"),
                            ConfirmationStore.PlanHash(b, "element_ids"));
        }

        [Fact]
        public void Two_arrays_cannot_be_shuffled_between_fields_into_the_same_hash()
        {
            var a = new JObject { ["x"] = new JArray("1", "2"), ["y"] = new JArray("3") };
            var b = new JObject { ["x"] = new JArray("1"), ["y"] = new JArray("2", "3") };

            Assert.NotEqual(ConfirmationStore.PlanHash(a, "x", "y"),
                            ConfirmationStore.PlanHash(b, "x", "y"));
        }

        [Fact]
        public void An_absent_field_is_not_an_empty_array()
        {
            var absent = new JObject();
            var empty = new JObject { ["writes"] = new JArray() };

            Assert.NotEqual(ConfirmationStore.PlanHash(absent, "writes"),
                            ConfirmationStore.PlanHash(empty, "writes"));
        }

        // ---- the document fingerprint -----------------------------------------

        [Fact]
        public void The_same_file_open_in_two_Revit_versions_is_two_documents()
        {
            var a = new DocIdentity { Title = "T", Path = @"C:\x\T.rvt", RevitYear = "2025" };
            var b = new DocIdentity { Title = "T", Path = @"C:\x\T.rvt", RevitYear = "2026" };

            Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        }

        [Fact]
        public void The_fingerprint_ignores_path_separator_and_casing_differences()
        {
            var a = new DocIdentity { Title = "T", Path = @"C:\X\T.rvt", RevitYear = "2026" };
            var b = new DocIdentity { Title = "t", Path = "c:/x/T.RVT", RevitYear = "2026" };

            Assert.Equal(a.Fingerprint(), b.Fingerprint());
        }

        [Fact]
        public void Two_different_cloud_models_sharing_a_title_have_different_fingerprints()
        {
            var a = new DocIdentity { Title = "TOWER", ModelGuid = "11111111-1111-1111-1111-111111111111", RevitYear = "2026" };
            var b = new DocIdentity { Title = "TOWER", ModelGuid = "22222222-2222-2222-2222-222222222222", RevitYear = "2026" };

            Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        }
    }
}
