// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The gate on the bridge's own save and export, proved by running its
// decision. Three things carry the file: not_assessable is not fail and leads
// with the coverage problem; an override covers exactly what it names; and
// every reply names the paths this gate does not reach.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OperationGateTests
    {
        private const string Now = "2026-06-01T00:00:00Z";

        /// <summary>
        /// THE MACHINE'S CLOCK, HELD STILL. On a real operation GateClock reads
        /// DateTime.UtcNow; here it is a constant, so an expiry is exact rather than
        /// a fact about when the suite ran - which is the same reason the pure
        /// evaluation takes its reference as an argument.
        /// </summary>
        private static readonly System.DateTime Machine =
            new System.DateTime(2026, 6, 1, 0, 0, 0, System.DateTimeKind.Utc);

        private static GateClockReference Clock(string callerNowUtc = null)
            => GateClock.Resolve(callerNowUtc, Machine);

        /// <summary>
        /// Decide exactly as the operation does: the caller's now_utc is resolved
        /// AGAINST the machine clock rather than used as the reference. A test that
        /// handed request.NowUtc straight to the decision would be reproducing the
        /// defect it is meant to hold closed.
        /// </summary>
        private static OperationGateVerdict Decide(RequireGateRequest request, OperationGateEvidence evidence,
                                                   GateClockReference clock = null)
            => OperationGateRules.Decide(request, evidence, clock ?? Clock(request == null ? null : request.NowUtc));

        private static RequireGateRequest Request(string overrideJson = null, string cited = null, string docFp = null,
                                                  string nowUtc = null)
        {
            var raw = new JObject
            {
                ["profile"] = new JObject
                {
                    ["name"] = "delivery", ["version"] = "v1",
                    ["requirements"] = new JObject { ["max_warnings"] = 0, ["forbid_imported_cad"] = true }
                }
            };
            if (overrideJson != null) raw["override"] = JObject.Parse(overrideJson);
            if (cited != null) raw["finding_set_fingerprint"] = cited;
            if (docFp != null) raw["document_fingerprint"] = docFp;
            if (nowUtc != null) raw["now_utc"] = nowUtc;
            string refusal;
            RequireGateRequest r = RequireGateRequest.Parse(raw, out refusal);
            Assert.True(r != null, refusal);
            return r;
        }

        private static GateRow Row(string requirement, string check, string status, string item = null)
            => new GateRow { Requirement = requirement, Check = check, Status = status, Item = item, Reason = status + " reason" };

        private static OperationGateEvidence Evidence(params GateRow[] rows)
        {
            var e = new OperationGateEvidence
            {
                Operation = GatedOperation.Save,
                DocumentTitle = "Tower",
                DocumentFingerprint = "doc-1",
                FindingSetFingerprint = "fs:now",
                Rows = rows.ToList(),
                Verdict = rows.Any(r => r.Status == PreDeliveryGateRules.StatusFail) ? PreDeliveryGateRules.VerdictFail
                        : rows.Any(r => r.Status == PreDeliveryGateRules.StatusNotMeasurable) ? PreDeliveryGateRules.VerdictNotAssessable
                        : PreDeliveryGateRules.VerdictPass,
                VisibilityComplete = true
            };
            e.FindingIdsByCheck["warnings"] = "f:warn";
            e.FindingIdsByCheck["imported_cad"] = "f:cad";
            return e;
        }

        private const string Override =
            @"{ ""identity"": ""a.person"", ""reason"": ""client accepted"", ""timestamp_utc"": ""2026-01-01T00:00:00Z"",
                ""operation"": ""save"", ""profile_version"": ""v1"", ""findings_ignored"": [""warnings""] }";

        // ---------------------------------------------------------- parsing

        [Fact]
        public void The_argument_is_parsed_strictly_and_a_missing_profile_is_refused()
        {
            string refusal;
            Assert.Null(RequireGateRequest.Parse(JObject.Parse(@"{ ""profil"": {} }"), out refusal));
            Assert.Contains("'profil'", refusal);
            Assert.Null(RequireGateRequest.Parse(JObject.Parse(@"{ ""profile"": { ""name"": ""d"", ""version"": ""1"" } }"), out refusal));
            Assert.Contains("requirements", refusal);
            Assert.Null(RequireGateRequest.Parse(JObject.Parse(@"{ ""profile"": { ""name"": ""d"", ""requirements"": { ""max_warnings"": 0 } } }"), out refusal));
            Assert.Contains("version", refusal);
            Assert.Null(RequireGateRequest.Parse(JObject.Parse(
                @"{ ""profile"": { ""name"": ""d"", ""version"": ""1"", ""requirements"": { ""max_warnings"": 0 } }, ""override"": { ""who"": ""x"" } }"), out refusal));
            Assert.Contains("'who'", refusal);
            Assert.NotNull(Request());
        }

        // ---------------------------------------------------------- decisions

        [Fact]
        public void Every_row_passing_with_complete_coverage_is_the_only_way_to_allowed()
        {
            OperationGateVerdict v = Decide(Request(),
                Evidence(Row("max_warnings", "warnings", "pass"), Row("forbid_imported_cad", "imported_cad", "pass")));
            Assert.Equal(OperationGateDecision.Allowed, v.Decision);
            Assert.True(v.Proceed);
            Assert.Empty(v.CoverageProblems);
        }

        [Fact]
        public void A_failing_row_blocks_and_the_refusal_names_the_check()
        {
            OperationGateVerdict v = Decide(Request(),
                Evidence(Row("max_warnings", "warnings", "fail"), Row("forbid_imported_cad", "imported_cad", "pass")));
            Assert.Equal(OperationGateDecision.Blocked, v.Decision);
            Assert.False(v.Proceed);
            Assert.Contains("warnings", v.Why);
            Assert.Equal(new[] { "warnings" }, v.BlockingFindings.ToArray());
        }

        [Fact]
        public void Not_assessable_is_distinct_from_fail_and_leads_with_the_coverage_problem()
        {
            // A requirement over a part nobody could measure, and nothing failing.
            OperationGateVerdict v = Decide(Request(),
                Evidence(Row("max_warnings", "warnings", "pass"), Row("forbid_imported_cad", "imported_cad", "not_measurable")));
            Assert.Equal(OperationGateDecision.NotAssessable, v.Decision);
            Assert.False(v.Proceed);
            Assert.StartsWith("NOT ASSESSABLE, which is not a fail", v.Why);
            Assert.Contains("forbid_imported_cad", v.Why);
            Assert.Single(v.CoverageProblems);

            // A closed workset: the same answer, naming the workset.
            OperationGateEvidence closed = Evidence(Row("max_warnings", "warnings", "pass"), Row("forbid_imported_cad", "imported_cad", "pass"));
            closed.VisibilityComplete = false;
            closed.VisibilityNote = "2 of 5 worksets are closed";
            OperationGateVerdict w = Decide(Request(), closed);
            Assert.Equal(OperationGateDecision.NotAssessable, w.Decision);
            Assert.Contains("2 of 5 worksets are closed", w.Why);

            // A check that died.
            OperationGateEvidence dead = Evidence(Row("max_warnings", "warnings", "pass"), Row("forbid_imported_cad", "imported_cad", "pass"));
            dead.ChecksFailed.Add("rooms");
            Assert.Contains("'rooms' did not run", Decide(Request(), dead).Why);
        }

        [Fact]
        public void Incomplete_coverage_cannot_excuse_a_failing_row()
        {
            OperationGateEvidence e = Evidence(Row("max_warnings", "warnings", "fail"));
            e.VisibilityComplete = false;
            OperationGateVerdict v = Decide(Request(), e);
            Assert.Equal(OperationGateDecision.Blocked, v.Decision);
            Assert.Contains("Coverage was also incomplete", v.Why);
        }

        [Fact]
        public void An_override_that_names_the_failing_check_lets_the_operation_proceed_as_overridden()
        {
            OperationGateVerdict v = Decide(Request(Override),
                Evidence(Row("max_warnings", "warnings", "fail"), Row("forbid_imported_cad", "imported_cad", "pass")));
            Assert.Equal(OperationGateDecision.Overridden, v.Decision);
            Assert.True(v.Proceed);
            Assert.True(v.Inner.OverrideAccepted);
        }

        [Fact]
        public void An_override_may_name_the_requirement_or_the_finding_id_and_covers_only_what_it_names()
        {
            OperationGateEvidence twoFails = Evidence(Row("max_warnings", "warnings", "fail"), Row("forbid_imported_cad", "imported_cad", "fail"));

            string byRequirement = Override.Replace("[\"warnings\"]", "[\"max_warnings\", \"forbid_imported_cad\"]");
            Assert.Equal(OperationGateDecision.Overridden, Decide(Request(byRequirement), twoFails).Decision);

            string byFindingId = Override.Replace("[\"warnings\"]", "[\"f:warn\", \"f:cad\"]");
            Assert.Equal(OperationGateDecision.Overridden, Decide(Request(byFindingId), twoFails).Decision);

            // Names one of two: blocked, and the reason says the override left one uncovered.
            OperationGateVerdict partial = Decide(Request(Override), twoFails);
            Assert.Equal(OperationGateDecision.Blocked, partial.Decision);
            Assert.Contains("does not cover", partial.Inner.OverrideRejectedBecause);
        }

        [Fact]
        public void An_override_for_another_operation_blocks()
        {
            OperationGateEvidence fail = Evidence(Row("max_warnings", "warnings", "fail"));

            string wrongOp = Override.Replace("\"operation\": \"save\"", "\"operation\": \"export\"");
            OperationGateVerdict v = Decide(Request(wrongOp), fail);
            Assert.Equal(OperationGateDecision.Blocked, v.Decision);
            Assert.Contains("signed for 'export'", v.Inner.OverrideRejectedBecause);
        }

        // ---------------------------------------------------------- the expiry

        private static string Expiring(string expiresUtc)
            => Override.Replace("\"findings_ignored\"", "\"expires_utc\": \"" + expiresUtc + "\", \"findings_ignored\"");

        /// <summary>
        /// THE DEFECT THIS TEST EXISTS FOR.
        ///
        /// The expiry used to be compared against the caller's now_utc, and now_utc
        /// arrives in the same object as the override it judges. An override that
        /// expired in March plus now_utc "2026-02-01T00:00:00Z" was, to the gate, an
        /// override in date - on the APPLY path, with the file about to be written.
        /// The reference is this machine's clock now, and a caller who disagrees
        /// with it by more than the tolerance is refused rather than believed.
        /// </summary>
        [Fact]
        public void An_expired_override_cannot_be_revived_by_a_convenient_now_utc()
        {
            OperationGateEvidence fail = Evidence(Row("max_warnings", "warnings", "fail"));
            RequireGateRequest backdated = Request(Expiring("2026-03-01T00:00:00Z"), nowUtc: "2026-02-01T00:00:00Z");

            // The caller's own value would have said "still in date"; it is not the authority.
            Assert.Equal("2026-02-01T00:00:00Z", backdated.NowUtc);

            OperationGateVerdict v = Decide(backdated, fail);
            Assert.Equal(OperationGateDecision.NotAssessable, v.Decision);
            Assert.False(v.Proceed);
            Assert.StartsWith("NOT ASSESSABLE, and not because of the model", v.Why);
            Assert.Contains("seconds apart", v.Why);
            Assert.Contains("300", v.Why);
        }

        [Fact]
        public void An_expiry_is_judged_against_the_machine_clock_whether_or_not_a_now_utc_was_sent()
        {
            OperationGateEvidence fail = Evidence(Row("max_warnings", "warnings", "fail"));

            // NO now_utc. The pure evaluation refuses this for want of a reference;
            // an operation has one, so the override is simply out of date.
            OperationGateVerdict silent = Decide(Request(Expiring("2026-03-01T00:00:00Z")), fail);
            Assert.Equal(OperationGateDecision.Blocked, silent.Decision);
            Assert.Contains("expired", silent.Inner.OverrideRejectedBecause);

            // now_utc agreeing with the machine clock: same answer, and the agreement
            // bought nothing - which is the point.
            OperationGateVerdict stated = Decide(Request(Expiring("2026-03-01T00:00:00Z"), nowUtc: Now), fail);
            Assert.Equal(OperationGateDecision.Blocked, stated.Decision);
            Assert.Contains("expired", stated.Inner.OverrideRejectedBecause);

            // An override that has NOT expired against the machine clock is accepted,
            // with and without a now_utc. The gate did not become unusable.
            Assert.Equal(OperationGateDecision.Overridden, Decide(Request(Expiring("2026-12-01T00:00:00Z")), fail).Decision);
            Assert.Equal(OperationGateDecision.Overridden,
                Decide(Request(Expiring("2026-12-01T00:00:00Z"), nowUtc: Now), fail).Decision);
        }

        [Fact]
        public void A_now_utc_may_bring_an_expiry_forward_and_never_push_one_back()
        {
            OperationGateEvidence fail = Evidence(Row("max_warnings", "warnings", "fail"));

            // Inside the tolerance and AHEAD of the machine clock: the later of the
            // two wins, so an override expiring between them is out of date.
            string justAhead = "2026-06-01T00:04:00Z";              // machine + 240s, tolerance 300s
            GateClockReference forward = Clock(justAhead);
            Assert.True(forward.Ok);
            Assert.Equal(justAhead, forward.ReferenceUtc);

            OperationGateVerdict v = Decide(Request(Expiring("2026-06-01T00:02:00Z"), nowUtc: justAhead), fail);
            Assert.Equal(OperationGateDecision.Blocked, v.Decision);
            Assert.Contains("expired", v.Inner.OverrideRejectedBecause);

            // Inside the tolerance and BEHIND it: the machine clock still wins.
            GateClockReference back = Clock("2026-05-31T23:56:00Z");  // machine - 240s
            Assert.True(back.Ok);
            Assert.Equal(Now, back.ReferenceUtc);
        }

        [Fact]
        public void The_clock_refuses_a_now_utc_that_is_not_a_time_or_that_disagrees_with_the_machine()
        {
            GateClockReference nonsense = Clock("last Tuesday");
            Assert.False(nonsense.Ok);
            Assert.Null(nonsense.ReferenceUtc);
            Assert.Contains("not a UTC timestamp", nonsense.Refusal);

            GateClockReference tooEarly = Clock("2026-05-31T23:50:00Z");   // machine - 600s
            Assert.False(tooEarly.Ok);
            Assert.Equal(-600, tooEarly.SkewSeconds);
            Assert.Contains("tolerance is 300", tooEarly.Refusal);

            GateClockReference tooLate = Clock("2026-06-01T00:10:00Z");    // machine + 600s
            Assert.False(tooLate.Ok);
            Assert.Equal(600, tooLate.SkewSeconds);

            // The boundary itself is inside: exactly the tolerance is agreement.
            Assert.True(Clock("2026-06-01T00:05:00Z").Ok);
            Assert.True(Clock("2026-05-31T23:55:00Z").Ok);
            Assert.False(Clock("2026-06-01T00:05:01Z").Ok);

            // No now_utc at all is not a refusal; it is the machine clock, alone.
            GateClockReference silent = Clock();
            Assert.True(silent.Ok);
            Assert.Equal(Now, silent.ReferenceUtc);
            Assert.Null(silent.CallerUtc);
            Assert.Equal(0, silent.SkewSeconds);
            Assert.Equal(Now, GateClock.Machine(Machine).ReferenceUtc);
        }

        /// <summary>
        /// A refused clock stops the operation for a reason that is about the CLOCK.
        /// It reads as not_assessable, but it must not be mistaken for a coverage
        /// problem in the model - so it leads with what could not be established and
        /// the model's own rows are never mentioned as the cause.
        /// </summary>
        [Fact]
        public void A_refused_clock_refuses_the_operation_and_says_so_before_anything_about_the_model()
        {
            OperationGateEvidence clean = Evidence(Row("max_warnings", "warnings", "pass"),
                                                   Row("forbid_imported_cad", "imported_cad", "pass"));
            GateClockReference skewed = Clock("2027-01-01T00:00:00Z");
            Assert.False(skewed.Ok);

            OperationGateVerdict v = Decide(Request(), clean, skewed);
            Assert.Equal(OperationGateDecision.NotAssessable, v.Decision);
            Assert.False(v.Proceed);
            Assert.StartsWith("NOT ASSESSABLE, and not because of the model", v.Why);
            Assert.Empty(v.CoverageProblems);

            // A null reference is refused too - a decision must never be reached
            // without one.
            Assert.Equal(OperationGateDecision.NotAssessable,
                OperationGateRules.Decide(Request(), clean, null).Decision);
            Assert.Contains("No reference clock was resolved",
                OperationGateRules.Decide(Request(), clean, null).Why);
        }

        [Fact]
        public void The_reply_records_which_clock_judged_the_expiry()
        {
            OperationGateEvidence e = Evidence(Row("max_warnings", "warnings", "pass"));
            JObject json = OperationGateRules.ToJson(Request(nowUtc: Now), e,
                                                     Decide(Request(nowUtc: Now), e), Clock(Now));
            Assert.Equal(Now, (string)json["clock"]["reference_utc"]);
            Assert.Equal(Now, (string)json["clock"]["machine_utc"]);
            Assert.Equal(Now, (string)json["clock"]["caller_now_utc"]);
            Assert.Equal(300, (int)json["clock"]["tolerance_seconds"]);
            Assert.Null((string)json["clock"]["refusal"]);
            Assert.Contains("this machine's UTC clock", (string)json["clock"]["means"]);
        }

        /// <summary>
        /// The pure path keeps its clock-free comparison, and keeps refusing an
        /// expiry it has no reference for. horizun_audit_model decides nothing and
        /// writes nothing, so determinism there costs nobody a file.
        /// </summary>
        [Fact]
        public void The_pure_evaluation_still_takes_its_reference_from_the_caller_and_refuses_without_one()
        {
            var expiring = new GateOverride
            {
                Identity = "a.person", Reason = "client accepted", TimestampUtc = "2026-01-01T00:00:00Z",
                Operation = GatedOperation.Save, ExpiresUtc = "2026-03-01T00:00:00Z",
                FindingsIgnored = new List<string> { "warnings" }
            };
            var input = new GateInput
            {
                Operation = GatedOperation.Save, AuditSupplied = true, CoverageComplete = true,
                BlockingFindings = new List<string> { "warnings" }, Override = expiring
            };
            Assert.Contains("send now_utc", PreventionGateRules.RejectOverride(expiring, input, null));
            Assert.Contains("expired", PreventionGateRules.RejectOverride(expiring, input, Now));
            Assert.Null(PreventionGateRules.RejectOverride(expiring, input, "2026-02-01T00:00:00Z"));
        }

        [Fact]
        public void The_audited_document_must_be_the_active_one_and_the_cited_audit_must_be_current()
        {
            OperationGateEvidence clean = Evidence(Row("max_warnings", "warnings", "pass"));

            OperationGateVerdict other = Decide(Request(docFp: "doc-2"), clean);
            Assert.Equal(OperationGateDecision.NotAssessable, other.Decision);
            Assert.Contains("doc-2", other.Why);
            Assert.Contains("doc-1", other.Why);

            OperationGateVerdict stale = Decide(Request(cited: "fs:old"), clean);
            Assert.Equal(OperationGateDecision.NotAssessable, stale.Decision);
            Assert.Contains("fs:old", stale.Why);
            Assert.Contains("fs:now", stale.Why);

            Assert.Equal(OperationGateDecision.Allowed, Decide(Request(cited: "fs:now", docFp: "doc-1"), clean).Decision);
        }

        // ---------------------------------------------------------- the reply

        [Fact]
        public void Every_gated_reply_says_it_enforces_and_names_the_paths_it_cannot_reach()
        {
            OperationGateEvidence e = Evidence(Row("max_warnings", "warnings", "pass"));
            JObject json = OperationGateRules.ToJson(Request(), e, Decide(Request(), e), Clock());

            Assert.Equal("allowed", (string)json["decision"]);
            Assert.True((bool)json["enforced"]);
            Assert.Equal("f:warn", (string)json["gate"]["rows"][0]["finding_id"]);
            var paths = ((JArray)json["not_interceptable"]).Select(p => (string)p["path"]).ToList();
            Assert.Contains("revit_ui_save", paths);
            Assert.Contains("synchronize_with_central", paths);
            foreach (JToken p in (JArray)json["not_interceptable"])
                Assert.False(string.IsNullOrWhiteSpace((string)p["why_not_intercepted"]));
            Assert.Contains("prevention-operation-matrix.md", json.ToString());
        }

        [Fact]
        public void Timestamps_are_normalised_so_a_parsed_date_and_a_written_string_compare_as_one_shape()
        {
            // Newtonsoft turns an ISO value in a request into a DateTime; a bare
            // (string) cast then renders it in the machine's culture, and an ordinal
            // comparison against "2026-02-01T00:00:00Z" reads that as expired.
            JToken parsed = JObject.Parse(@"{ ""t"": ""2026-03-01T00:00:00Z"" }")["t"];
            Assert.Equal(JTokenType.Date, parsed.Type);
            Assert.Equal("2026-03-01T00:00:00Z", UtcStamp.Normalise(parsed));
            Assert.Equal("2026-03-01T00:00:00Z", UtcStamp.Normalise("2026-03-01T00:00:00Z"));
            Assert.Equal("2026-03-01T00:00:00Z", UtcStamp.Normalise("2026-03-01T02:00:00+02:00"));
            Assert.Equal("not-a-date", UtcStamp.Normalise("not-a-date"));
            Assert.Null(UtcStamp.Normalise((JToken)null));
        }

        [Fact]
        public void The_four_decisions_are_the_only_ones()
        {
            foreach (string d in new[] { OperationGateDecision.Allowed, OperationGateDecision.Blocked,
                                         OperationGateDecision.Overridden, OperationGateDecision.NotAssessable })
                Assert.False(string.IsNullOrEmpty(d));
            Assert.Equal(OperationGateDecision.NotAssessable, Decide(null, null).Decision);
        }
    }
}
