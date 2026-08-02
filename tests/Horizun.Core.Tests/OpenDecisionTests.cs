// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The open guards, as one table, tested once.
//
// Two commands open documents, and until now each carried its own sequence of
// if-statements deciding whether it was allowed to. They were written months
// apart, both carefully, and they had diverged in three ways that neither file
// shows on its own:
//
//   * horizun_document_session had NO CENTRAL GUARD AT ALL. The tool whose
//     description promises it is "guarded against the irreversible" would open the
//     model everybody synchronizes to without a word, while the other one refused.
//   * horizun_open_document had no NEWER-FILE rule, so allow_upgrade=true on a file
//     from a later Revit produced Revit's own error about a file format instead of
//     the sentence explaining that no flag can downgrade anything.
//   * only one of them could open a CLOUD model, and it was the one that does not
//     take expected_version.
//
// Nothing caught that, because there was nothing to catch it WITH: exercising
// either version meant opening real models on a real machine, so the two tables
// were never compared. They are one table now, in OpenDecision.cs, and this is it.
//
// Every case here is a decision, not an open. No Revit, no files, no models.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OpenDecisionTests
    {
        private static OpenFacts LocalFile(string fileVersion, string host = "2026", bool? central = false) =>
            new OpenFacts
            {
                IsCloud = false,
                HostVersion = host,
                FileVersion = fileVersion,
                IsCentral = central,
                DisplayName = "Tower.rvt"
            };

        private static OpenFacts CloudModel(string host = "2026") =>
            new OpenFacts { IsCloud = true, HostVersion = host, FileVersion = null, IsCentral = true };

        /// <summary>The flags a caller passes. Everything off is the careful default.</summary>
        private static OpenIntent Intent(bool allowUpgrade = false, bool detach = false, bool openCentral = false,
                                         string expected = null, bool expectedRequired = false) =>
            new OpenIntent
            {
                ExpectedVersion = expected,
                ExpectedVersionRequired = expectedRequired,
                AllowUpgrade = allowUpgrade,
                Detach = detach,
                OpenCentral = openCentral
            };

        // ---- the plain case ----------------------------------------------------

        [Fact]
        public void A_matching_file_that_is_not_a_central_just_opens()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2026"), Intent());

            Assert.True(v.Ok);
            Assert.False(v.WillUpgrade);
            Assert.Equal("checked", v.VersionGuard);
            Assert.Equal("not_a_central", v.CentralGuard);
        }

        // ---- guard: the wrong bridge -------------------------------------------

        [Fact]
        public void A_stated_version_that_disagrees_with_the_host_is_the_wrong_bridge()
        {
            // The file and the host can both be 2026 and the caller can still be talking
            // to the Revit next door. This is the cheapest check and it runs first.
            OpenVerdict v = OpenDecision.Decide(LocalFile("2025", host: "2025"), Intent(expected: "2026"));

            Assert.False(v.Ok);
            Assert.Contains("wrong bridge", v.Refusal);
            Assert.Contains("Nothing was opened", v.Refusal);
        }

        [Fact]
        public void A_stated_version_that_agrees_with_the_host_gets_out_of_the_way()
        {
            Assert.True(OpenDecision.Decide(LocalFile("2026"), Intent(expected: "2026")).Ok);
        }

        [Fact]
        public void A_version_written_out_in_full_is_the_same_year()
        {
            // Revit answers "2026" from VersionNumber and things like
            // "Autodesk Revit 2026 (Build ...)" elsewhere. One year, either way.
            OpenVerdict v = OpenDecision.Decide(
                LocalFile("2026", host: "Autodesk Revit 2026 (Build: 20250401_1515)"),
                Intent(expected: " 2026 "));

            Assert.True(v.Ok);
        }

        [Fact]
        public void An_expected_version_with_no_year_in_it_is_refused_rather_than_ignored()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2026"), Intent(expected: "latest"));

            Assert.False(v.Ok);
            Assert.Contains("does not contain a Revit year", v.Refusal);
        }

        [Fact]
        public void A_command_that_requires_a_stated_version_refuses_without_one()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2026"), Intent(expectedRequired: true));

            Assert.False(v.Ok);
            Assert.Contains("expected_version is required", v.Refusal);
        }

        [Fact]
        public void A_command_that_does_not_require_one_proceeds_without_it()
        {
            // The only difference between the two commands. The RULE is the same;
            // whether the field may be absent is not.
            Assert.True(OpenDecision.Decide(LocalFile("2026"), Intent(expectedRequired: false)).Ok);
        }

        // ---- guard: the irreversible upgrade -----------------------------------

        [Fact]
        public void An_older_file_is_refused_because_opening_it_would_upgrade_it()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2024"), Intent());

            Assert.False(v.Ok);
            Assert.Contains("UPGRADE the file permanently", v.Refusal);
            Assert.Contains("2024", v.Refusal);
            Assert.Contains("allow_upgrade=true", v.Refusal);
        }

        [Fact]
        public void An_older_file_opens_when_the_caller_says_the_words()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2024"), Intent(allowUpgrade: true));

            Assert.True(v.Ok);
            Assert.True(v.WillUpgrade);
        }

        /// <summary>
        /// THE RULE ONE OF THE TWO COMMANDS DID NOT HAVE. A newer file cannot be opened by
        /// an older Revit at all - there is no downgrade - so allow_upgrade must not be
        /// able to reach it. open_document treated every mismatch as one kind, so passing
        /// the flag here got Revit's own error about a file format, arriving after the
        /// caller had already agreed to something irreversible that was never on offer.
        /// </summary>
        [Fact]
        public void A_newer_file_is_refused_and_allow_upgrade_cannot_reach_it()
        {
            foreach (bool allow in new[] { false, true })
            {
                OpenVerdict v = OpenDecision.Decide(LocalFile("2027", host: "2026"), Intent(allowUpgrade: allow));

                Assert.False(v.Ok);
                Assert.Contains("newer file cannot be opened", v.Refusal);
                Assert.Contains("CANNOT help", v.Refusal);
            }
        }

        [Fact]
        public void An_unreadable_version_is_not_a_matching_version()
        {
            var facts = LocalFile(null);
            facts.ReadError = "The file is not a Revit file, or is corrupt.";

            OpenVerdict v = OpenDecision.Decide(facts, Intent());

            Assert.False(v.Ok);
            Assert.Contains("does not report a readable Revit version", v.Refusal);
            Assert.Contains("The file is not a Revit file", v.Refusal);   // the reason travels
            Assert.Contains("refusal, not a failure to check", v.Refusal);
        }

        [Fact]
        public void An_unreadable_version_can_be_waived_only_by_opting_into_the_upgrade()
        {
            // Nothing else waives it. The flag is the caller accepting that they do not
            // know what this file is and are willing to have it converted anyway.
            var facts = LocalFile(null, central: false);

            Assert.True(OpenDecision.Decide(facts, Intent(allowUpgrade: true)).Ok);
            Assert.False(OpenDecision.Decide(facts, Intent(detach: true)).Ok);
        }

        [Fact]
        public void An_unreadable_version_is_not_reported_as_an_upgrade()
        {
            // "Unknown" must not become "yes" on the way into the response either: a
            // caller reading upgraded=true would go looking for a conversion that may
            // never have happened.
            OpenVerdict v = OpenDecision.Decide(LocalFile(null), Intent(allowUpgrade: true));

            Assert.True(v.Ok);
            Assert.False(v.WillUpgrade);
        }

        // ---- guard: the central model ------------------------------------------

        /// <summary>
        /// THE GUARD document_session DID NOT HAVE. Opening a central directly means
        /// working in the file everybody else synchronizes to.
        /// </summary>
        [Fact]
        public void A_central_model_is_refused_without_detach_or_open_central()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2026", central: true), Intent());

            Assert.False(v.Ok);
            Assert.Contains("workshared CENTRAL model", v.Refusal);
            Assert.Contains("detach=true", v.Refusal);
            Assert.Contains("open_central=true", v.Refusal);
        }

        [Fact]
        public void Detach_is_the_safe_way_through_the_central_guard()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2026", central: true), Intent(detach: true));

            Assert.True(v.Ok);
            Assert.Equal("detached", v.CentralGuard);
        }

        [Fact]
        public void Open_central_is_the_deliberate_way_through_it()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2026", central: true), Intent(openCentral: true));

            Assert.True(v.Ok);
            Assert.Equal("open_central", v.CentralGuard);
        }

        [Fact]
        public void An_unreadable_central_flag_is_not_a_no()
        {
            var facts = LocalFile("2026", central: null);

            OpenVerdict v = OpenDecision.Decide(facts, Intent());

            Assert.False(v.Ok);
            Assert.Contains("could not be read", v.Refusal);
            Assert.Contains("not a 'no'", v.Refusal);

            Assert.True(OpenDecision.Decide(facts, Intent(detach: true)).Ok);
            Assert.True(OpenDecision.Decide(facts, Intent(openCentral: true)).Ok);
        }

        // ---- cloud --------------------------------------------------------------

        [Fact]
        public void A_cloud_model_cannot_have_its_version_checked_and_says_so()
        {
            OpenVerdict v = OpenDecision.Decide(CloudModel(), Intent(detach: true));

            Assert.True(v.Ok);
            Assert.Equal("not_applicable_cloud", v.VersionGuard);
            Assert.False(v.WillUpgrade);       // unknowable, and never guessed as true
        }

        /// <summary>
        /// A cloud model IS the central. Living in ACC rather than on a server share does
        /// not make it less shared, and this guard was applied to one and not the other.
        /// </summary>
        [Fact]
        public void A_cloud_model_needs_the_same_clearance_as_a_central_on_disk()
        {
            OpenVerdict v = OpenDecision.Decide(CloudModel(), Intent());

            Assert.False(v.Ok);
            Assert.Contains("is the CENTRAL model", v.Refusal);
            Assert.Contains("detach=true", v.Refusal);
        }

        [Fact]
        public void The_wrong_bridge_check_still_runs_for_a_cloud_model()
        {
            // It is the ONLY version check that can run there, which is exactly why
            // document_session having no cloud route at all mattered: the command that
            // requires expected_version could not reach these models.
            OpenVerdict v = OpenDecision.Decide(CloudModel(host: "2025"),
                                                Intent(detach: true, expected: "2026"));

            Assert.False(v.Ok);
            Assert.Contains("wrong bridge", v.Refusal);
        }

        [Fact]
        public void A_cloud_model_is_never_refused_for_an_unreadable_file_version()
        {
            // Its version is unknowable by construction, not unreadable by accident.
            // Running the local rule over it would refuse every cloud open ever made.
            OpenVerdict v = OpenDecision.Decide(CloudModel(), Intent(openCentral: true));

            Assert.True(v.Ok);
        }

        // ---- order --------------------------------------------------------------

        [Fact]
        public void The_wrong_bridge_is_reported_before_anything_about_the_file()
        {
            // A caller on the wrong bridge gets told THAT, not that their 2024 file would
            // be upgraded by a host they never meant to be talking to.
            OpenVerdict v = OpenDecision.Decide(LocalFile("2024", host: "2026", central: true),
                                                Intent(expected: "2024"));

            Assert.False(v.Ok);
            Assert.Contains("wrong bridge", v.Refusal);
            Assert.DoesNotContain("UPGRADE the file permanently", v.Refusal);
        }

        [Fact]
        public void The_upgrade_is_reported_before_the_central_guard()
        {
            // Both apply. The upgrade is the irreversible one, so it is the one named.
            OpenVerdict v = OpenDecision.Decide(LocalFile("2024", central: true), Intent());

            Assert.False(v.Ok);
            Assert.Contains("UPGRADE the file permanently", v.Refusal);
        }

        [Fact]
        public void Clearing_the_upgrade_still_leaves_the_central_guard_standing()
        {
            // The one that matters: a flag passed for one reason must not waive another
            // guard on the way past. allow_upgrade says nothing about worksharing.
            OpenVerdict v = OpenDecision.Decide(LocalFile("2024", central: true), Intent(allowUpgrade: true));

            Assert.False(v.Ok);
            Assert.Contains("workshared CENTRAL model", v.Refusal);
        }

        [Fact]
        public void Detach_does_not_waive_the_upgrade_guard_either()
        {
            OpenVerdict v = OpenDecision.Decide(LocalFile("2024", central: true), Intent(detach: true));

            Assert.False(v.Ok);
            Assert.Contains("UPGRADE the file permanently", v.Refusal);
        }

        // ---- the arithmetic underneath ------------------------------------------

        [Theory]
        [InlineData("2026", "2026")]
        [InlineData("Autodesk Revit 2026 (Build: x)", "2026")]
        [InlineData("  2023  ", "2023")]
        [InlineData("v2027", "2027")]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("latest", null)]
        public void A_year_is_recognised_wherever_it_is_written(string input, string expected)
        {
            Assert.Equal(expected, OpenDecision.NormalizeVersion(input));
        }

        [Fact]
        public void Two_unreadable_versions_are_not_a_match()
        {
            // null == null must not read as "these agree", or an open with nothing known
            // about either side would sail through the version guard.
            Assert.False(OpenDecision.SameVersion(null, null));
            Assert.False(OpenDecision.SameVersion("2026", null));
        }

        [Fact]
        public void Facts_and_intent_are_both_required()
        {
            Assert.Throws<ArgumentNullException>(() => OpenDecision.Decide(null, Intent()));
            Assert.Throws<ArgumentNullException>(() => OpenDecision.Decide(LocalFile("2026"), null));
        }
    }
}
