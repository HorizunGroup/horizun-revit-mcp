// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A failed write used to answer only "the application declaration is failed": the
// reason each room was refused sat in evidence.rejected_rooms, which the caller never
// saw (the field case: six finish floors refused, the reply saying nothing about why).
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;
using Xunit;
using Horizun.Revit.Core;

namespace Horizun.Core.Tests
{
    public class WriteFailureCauseTests
    {
        private static JObject Failed(JObject evidence) => new JObject
        {
            ["dry_run"] = false,
            ["application"] = new JObject { ["state"] = "failed", ["requested"] = 2, ["verified"] = 0 },
            ["evidence"] = evidence
        };

        /// <summary>The reply horizun_manage_cad_links add stamps when the link committed and only its unit is unconfirmed.</summary>
        private static JObject CadLinkAdded(string failedPostcondition, int failed, int unknown)
        {
            var payload = new JObject
            {
                ["operation"] = "add",
                ["dry_run"] = false,
                ["element_id"] = 4242,
                ["host_verified"] = false,
                ["failed_postconditions"] = new JArray(failedPostcondition),
                ["failures"] = new JArray(new JObject { ["element_id"] = 4242, ["reason"] = "the link was created (element 4242) but its unit is not confirmed" })
            };
            ApplicationOutcome.StampApplied(payload, "Committed", 1, 1, 0, 0, failed, unknown);
            return payload;
        }

        [Fact]
        public void A_link_created_with_its_unit_unconfirmed_is_verificacion_fallida_not_a_revit_exception()
        {
            // Field 2026-11-18: the add committed the link, only the unit could not be confirmed, and the
            // reply carried api_revit_excepcion although Revit raised nothing.
            string message = CommandResult.WriteResultFailure(CadLinkAdded("units_unconfirmable", 0, 1), out string code);
            Assert.NotNull(message);
            Assert.Equal(ErrorCodes.VerificacionFallida, code);
            Assert.Contains("the link was created (element 4242)", message);

            CommandResult result = CommandResult.Ok(CadLinkAdded("units_unconfirmable", 0, 1));
            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.VerificacionFallida, (string)result.Detail[ErrorCodes.CodeField]);
            Assert.Equal(4242, (int)result.Detail["element_id"]);   // the rest of the reply is kept
        }

        [Fact]
        public void A_link_committed_at_the_wrong_unit_is_verificacion_fallida_too()
        {
            string message = CommandResult.WriteResultFailure(CadLinkAdded("units_not_applied", 1, 0), out string code);
            Assert.NotNull(message);
            Assert.Equal(ErrorCodes.VerificacionFallida, code);
        }

        [Fact]
        public void Zero_of_n_without_a_named_postcondition_keeps_its_previous_code()
        {
            var payload = new JObject { ["dry_run"] = false };
            ApplicationOutcome.StampApplied(payload, "Committed", 1, 1, 0, 0, 0, 1);
            CommandResult.WriteResultFailure(payload, out string code);
            Assert.Equal(ErrorCodes.ApiRevitExcepcion, code);
        }

        [Fact]
        public void FailedWriteNamesTheRecordedReasonWithItsId()
        {
            string message = CommandResult.WriteResultFailure(Failed(new JObject
            {
                ["rejected_rooms"] = new JArray(
                    new JObject { ["room_id"] = 828540, ["reason"] = "floor runs through nearby wall 733871" },
                    new JObject { ["room_id"] = 829817, ["reason"] = "shared volume with nearby wall 1054769 is not measurable" })
            }));
            Assert.Contains("Cause: room_id 828540: floor runs through nearby wall 733871; room_id 829817: shared volume", message);
        }

        [Fact]
        public void RehearsalWithInvalidRowsIsEntradaInvalidaAndNamesEachRowAndItsCause()
        {
            var payload = new JObject
            {
                ["dry_run"] = true, ["valid"] = 3, ["invalid"] = 2,
                ["errors"] = new JArray(
                    new JObject { ["index"] = 1, ["error"] = "level 'N9' does not exist" },
                    new JObject { ["index"] = 4, ["path"] = "actions[4].type", ["error"] = "type 'X' not found" })
            };
            string message = CommandResult.WriteResultFailure(payload, out string code);
            Assert.Equal(ErrorCodes.EntradaInvalida, code);
            Assert.StartsWith("Rehearsal refused 2 of 5 rows.", message);
            Assert.Contains("Row 1: level 'N9' does not exist; row 4 (actions[4].type): type 'X' not found.", message);
        }

        [Fact]
        public void ParameterRehearsalWithUnresolvedRowsIsEntradaInvalidaNotARevitException()
        {
            // Field 2026-11 (5.0.2, write_params_verified): rows sent with element_id instead of
            // target_id were refused before any transaction, and the reply said "Write failed: 0 of N
            // confirmed" with api_revit_excepcion. The rehearsal now declares itself and its rows.
            var payload = new JObject
            {
                ["dry_run"] = true, ["mode"] = "dry_run", ["transaction_status"] = "not_started",
                ["requested"] = 2, ["valid"] = 0, ["invalid"] = 2,
                ["errors"] = new JArray(
                    new JObject { ["index"] = 0, ["error"] = "writes[0] has 'element_id', which a write entry does not take - use target_id." },
                    new JObject { ["index"] = 1, ["error"] = "writes[1] has 'element_id', which a write entry does not take - use target_id." }),
                ["application"] = new JObject
                {
                    ["state"] = "rejected", ["transaction_status"] = "not_started", ["fully_applied"] = false,
                    ["requested"] = 2, ["applied"] = 0, ["verified"] = 0, ["unresolved"] = 2, ["failed"] = 0, ["unknown"] = 0
                }
            };
            string message = CommandResult.WriteResultFailure(payload, out string code);
            Assert.Equal(ErrorCodes.EntradaInvalida, code);
            Assert.StartsWith("Rehearsal refused 2 of 2 rows.", message);
            Assert.Contains("Row 0: writes[0] has 'element_id'", message);
            Assert.DoesNotContain("Write failed", message);
        }

        [Fact]
        public void RehearsalKeepsADeclaredStableCodeAndCountsOperations()
        {
            var payload = new JObject
            {
                ["dry_run"] = true, ["valid_operations"] = 0, ["invalid_operations"] = 1, ["error_code"] = ErrorCodes.ElementoNoEncontrado,
                ["errors"] = new JArray("element 12 is not in the model")
            };
            string message = CommandResult.WriteResultFailure(payload, out string code);
            Assert.Equal(ErrorCodes.ElementoNoEncontrado, code);
            Assert.Contains("refused 1 of 1 operation.", message);
            Assert.Contains("Element 12 is not in the model.", message);
        }

        [Fact]
        public void FailedParameterWriteNamesTheRowErrorWithItsTargetAndParameter()
        {
            // The field case: START_EXTENSION / END_EXTENSION on a beam, "0 of 2 confirmed" and no cause.
            var payload = new JObject
            {
                ["requested"] = 2, ["writes_confirmed"] = 0,
                ["rows"] = new JArray(
                    new JObject { ["index"] = 0, ["target_id"] = "1377191", ["parameter"] = "START_EXTENSION", ["error"] = "Set threw: the parameter is read-only" },
                    new JObject { ["index"] = 1, ["target_id"] = "1377191", ["parameter"] = "END_EXTENSION", ["error"] = null })
            };
            string message = CommandResult.WriteResultFailure(payload);
            Assert.Contains("0 of 2 requested changes were confirmed", message);
            Assert.Contains("Cause: target_id 1377191 (START_EXTENSION): Set threw: the parameter is read-only.", message);
        }

        [Fact]
        public void SuccessfulRowsAddNoCause()
        {
            var payload = new JObject
            {
                ["requested"] = 1, ["writes_confirmed"] = 0,
                ["rows"] = new JArray(new JObject { ["target_id"] = "5", ["parameter"] = "Comments", ["outcome"] = "not_confirmed" })
            };
            Assert.DoesNotContain("Cause:", CommandResult.WriteResultFailure(payload));
        }

        [Fact]
        public void FailedWriteWithoutRecordedReasonsKeepsTheOldMessage()
        {
            string message = CommandResult.WriteResultFailure(Failed(new JObject()));
            Assert.DoesNotContain("Cause:", message);
            Assert.StartsWith("Write failed: the application declaration is failed.", message);
        }

        [Fact]
        public void CauseIsBoundedToThreeItems()
        {
            var rows = new JArray();
            for (int i = 0; i < 6; i++) rows.Add(new JObject { ["room_id"] = i, ["reason"] = "reason " + i });
            string message = CommandResult.WriteResultFailure(Failed(new JObject { ["rejected_rooms"] = rows }));
            Assert.Contains("room_id 2: reason 2", message);
            Assert.DoesNotContain("room_id 3:", message);
        }

        // Field case: an annotate dimension rehearsal answered five times only "the
        // application declaration is partial"; the reason sat in rehearsal.actions[].
        private static JObject FailedRehearsal(string reason) => new JObject
        {
            ["dry_run"] = true,
            ["application"] = new JObject { ["state"] = "partial" },
            ["rehearsal"] = new JObject
            {
                ["not_constructible"] = 1,
                ["actions"] = new JArray(
                    new JObject { ["index"] = 0, ["operation"] = "dimension", ["constructible"] = true, ["reason"] = null },
                    new JObject { ["index"] = 1, ["operation"] = "dimension", ["constructible"] = false, ["reason"] = reason })
            }
        };

        [Fact]
        public void FailedRehearsalNamesTheRowAndItsReason()
        {
            string message = CommandResult.WriteResultFailure(FailedRehearsal(
                "rehearsal checks did not match: references_owners_vs_requested - see verification"));
            Assert.StartsWith("Rehearsal failed: the application declaration is partial.", message);
            Assert.Contains("1 row(s) could not be constructed.", message);
            Assert.Contains("Cause: action 1 (dimension): rehearsal checks did not match: references_owners_vs_requested", message);
            Assert.DoesNotContain("action 0", message);
        }

        [Fact]
        public void FailedCadConnectRehearsalNamesEachRefusedJunction()
        {
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true,
                ["application"] = new JObject { ["state"] = "rejected" },
                ["junctions"] = new JArray(
                    new JObject { ["id"] = "j1", ["state"] = "would_create" },
                    new JObject { ["id"] = "j2", ["state"] = "refused", ["refused"] = "close_ends_not_aligned", ["says"] = "ends 12.7 mm apart" },
                    new JObject { ["id"] = "j3", ["state"] = "refused", ["refused"] = "member_too_short_for_the_fitting" })
            });
            Assert.StartsWith("Rehearsal failed: the application declaration is rejected.", message);
            Assert.Contains("2 junction(s) were refused", message);
            Assert.Contains("junction j2: close_ends_not_aligned - ends 12.7 mm apart", message);
            Assert.Contains("junction j3: member_too_short_for_the_fitting", message);
            Assert.DoesNotContain("junction j1", message);
        }

        // Field 2026-11-19 (EST-14c): a reinforcement rehearsal with refused required rows answered
        // only "the application declaration is rejected"; the code and the reason sat in refused[].
        [Fact]
        public void RejectedReinforcementRehearsalNamesEachRefusedRow()
        {
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true,
                ["application"] = new JObject { ["state"] = "rejected" },
                ["refused"] = new JArray(
                    new JObject { ["index"] = 0, ["kind"] = "rebar", ["rule_id"] = "V-TAB/top", ["code"] = "host_section_unreadable", ["why"] = "column 412 has no b x h" },
                    new JObject { ["index"] = 1, ["kind"] = "cover", ["rule_id"] = "V-TAB", ["code"] = "cover_type_missing" })
            });
            Assert.StartsWith("Rehearsal failed: the application declaration is rejected.", message);
            Assert.Contains("2 required row(s) refused.", message);
            Assert.Contains("refused rebar rule V-TAB/top (refused[0]): host_section_unreadable - column 412 has no b x h", message);
            Assert.Contains("refused cover rule V-TAB (refused[1]): cover_type_missing", message);
        }

        [Fact]
        public void ManyRefusedRowsAreCountedAndTheRestPointsAtTheArray()
        {
            var rows = new JArray();
            for (int i = 0; i < 200; i++)
                rows.Add(new JObject { ["index"] = i, ["kind"] = "rebar", ["rule_id"] = "R" + i, ["code"] = "c", ["why"] = "no cover." });
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true, ["application"] = new JObject { ["state"] = "rejected" }, ["refused"] = rows
            });
            Assert.Contains("200 required row(s) refused.", message);
            Assert.Contains("; and 197 more in refused.", message);
            Assert.DoesNotContain("..", message);
            Assert.DoesNotContain("R3 ", message);
        }

        [Fact]
        public void ARefusedRowWithOnlyAReasonIsNamedAndAnIntegerRefusedIsLeftAlone()
        {
            string reasonOnly = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true, ["application"] = new JObject { ["state"] = "rejected" },
                ["refused"] = new JArray(new JObject { ["kind"] = "hook", ["why"] = "bar 9 is too short." })
            });
            Assert.Contains("refused hook (refused[0]): bar 9 is too short.", reasonOnly);
            Assert.DoesNotContain("short..", reasonOnly);
            // cad_connect / apply_ifc_plan carry refused as a count: no refused sentence is made of it.
            string counted = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true, ["application"] = new JObject { ["state"] = "rejected" }, ["refused"] = 4
            });
            Assert.DoesNotContain("required row(s) refused", counted);
        }

        private static JObject Skipped(string id, string reason = "not_marked_automatic", string readingSays = null)
        {
            var o = new JObject
            {
                ["id"] = id, ["state"] = "skipped_needs_review", ["review_reason"] = reason,
                ["says"] = "this junction is not marked automatic and carries no reason. Nothing here said it needs review."
            };
            if (readingSays != null) o["reading_says"] = readingSays;
            return o;
        }

        [Fact]
        public void CadConnectRehearsalNamesJunctionsSkippedForReviewAndTheRemedy()
        {
            // Field 2026-11-17 (p9-con-n1b-dry): 7 junctions rehearsed, 12 hand-written direct ones without
            // `automatic` were skipped for review, and the error named no cause at all.
            var rows = new JArray(
                new JObject { ["id"] = "16660|8050|0", ["state"] = "would_create" },
                new JObject { ["id"] = "16935|7540|0", ["state"] = "would_create" },
                Skipped("20835|18835|0|direct"), Skipped("20835|19060|0|direct"),
                Skipped("20835|19840|0|direct"), Skipped("29530|19835|0|direct"));
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true,
                ["application"] = new JObject { ["state"] = "rejected" },
                ["junctions"] = rows
            }, out string code);
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, code);
            Assert.StartsWith("Rehearsal failed: the application declaration is rejected.", message);
            Assert.Contains("4 junction(s) were skipped and not rehearsed; 2 rehearsed and can be sent again without them.", message);
            Assert.Contains("skipped_needs_review, 4: 20835|18835|0|direct, 20835|19060|0|direct, 20835|19840|0|direct and 1 more", message);
            Assert.Contains("not marked automatic and carry no reason", message);
            Assert.Contains("automatic=true", message);
            Assert.Contains("include_needs_review=true", message);
            Assert.DoesNotContain("16660|8050|0", message);
            Assert.EndsWith("inspect the preserved diagnostic.", message);
        }

        [Fact]
        public void CadConnectRehearsalSaysTheReadingsOwnReasonAndKeepsRefusalsFirst()
        {
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true,
                ["application"] = new JObject { ["state"] = "rejected" },
                ["junctions"] = new JArray(
                    new JObject { ["id"] = "j1", ["state"] = "would_join" },
                    new JObject { ["id"] = "j2", ["state"] = "refused", ["refused"] = "close_ends_not_aligned", ["says"] = "ends 25.4 mm apart" },
                    new JObject { ["id"] = "j3", ["state"] = "skipped_unresolved", ["says"] = "run never built" },
                    Skipped("j4", "marked_by_the_reading", "an irregular three-way"))
            });
            Assert.Contains("1 junction(s) were refused and 1 junction(s) were skipped and not rehearsed; 1 rehearsed", message);
            Assert.Contains("(1 more skipped as unresolved, which does not refuse the call)", message);
            Assert.Contains("Cause: junction j2: close_ends_not_aligned - ends 25.4 mm apart.", message);
            Assert.Contains("the network reading marked them for a person (first: an irregular three-way)", message);
            Assert.True(message.IndexOf("junction j2", System.StringComparison.Ordinal) <
                        message.IndexOf("Skipped for review", System.StringComparison.Ordinal));
        }

        [Fact]
        public void FailedCadConnectApplyNamesTheSkippedJunctionsToo()
        {
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = false,
                ["application"] = new JObject { ["state"] = "failed", ["requested"] = 1, ["verified"] = 0 },
                ["junctions"] = new JArray(Skipped("j9"))
            });
            Assert.StartsWith("Write failed: the application declaration is failed.", message);
            Assert.Contains("1 junction(s) were skipped and not made; none of the others was made.", message);
            Assert.DoesNotContain("not rehearsed", message);
            Assert.Contains("skipped_needs_review, 1: j9", message);
        }

        [Fact]
        public void AlreadyConnectedJunctionsAreNotCountedAsRehearsed()
        {
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true,
                ["application"] = new JObject { ["state"] = "rejected" },
                ["junctions"] = new JArray(
                    new JObject { ["id"] = "j1", ["state"] = "already_connected" },
                    new JObject { ["id"] = "j2", ["state"] = "already_connected" },
                    Skipped("j3"))
            });
            Assert.Contains("1 junction(s) were skipped and not rehearsed; none of the others rehearsed (2 already connected).", message);
        }

        [Fact]
        public void ACadConnectCallWhereNothingResolvedSaysSoInsteadOfPassingAsANoOp()
        {
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true,
                ["application"] = new JObject { ["state"] = "rejected" },
                ["junctions"] = new JArray(
                    new JObject { ["id"] = "j1", ["state"] = "skipped_unresolved", ["says"] = "no element remembers cadsem:a" },
                    new JObject { ["id"] = "j2", ["state"] = "skipped_unresolved", ["says"] = "no element remembers cadsem:b" })
            });
            Assert.StartsWith("Rehearsal failed: the application declaration is rejected.", message);
            Assert.Contains("None of the 2 junction(s) resolved to elements of this document (skipped_unresolved), so nothing could be rehearsed.", message);
            Assert.Contains("Check target_document", message);
            Assert.Contains("First: no element remembers cadsem:a", message);
        }

        [Fact]
        public void FailedRehearsalWithoutRowsKeepsTheOldMessage()
        {
            string message = CommandResult.WriteResultFailure(new JObject
            {
                ["dry_run"] = true,
                ["application"] = new JObject { ["state"] = "partial" }
            });
            Assert.Equal("Rehearsal failed: the application declaration is partial. No executable confirmation is established; inspect the preserved diagnostic.", message);
        }

        [Fact]
        public void MismatchReasonNamesOnlyTheFailedChecks()
        {
            var verification = new JObject
            {
                ["checks"] = new JArray(
                    new JObject { ["field"] = "class", ["match"] = true },
                    new JObject { ["field"] = "references_owners_vs_requested", ["match"] = false },
                    new JObject { ["field"] = "segments", ["match"] = false })
            };
            Assert.Equal("rehearsal checks did not match: references_owners_vs_requested, segments - see verification",
                CommandResult.MismatchReason(verification));
            Assert.Equal("one or more rehearsal checks did not match - see verification",
                CommandResult.MismatchReason(new JObject()));
        }
    }
}
