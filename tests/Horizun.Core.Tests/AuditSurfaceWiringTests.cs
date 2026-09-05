// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE TWO OPT-IN SURFACES ON horizun_audit_model, GUARDED IN SOURCE.
//
// AuditModelCommand needs a UIApplication and a document, so nothing here can
// execute it. What can be checked from a desk is that the wiring is real -
// which matters more than usual, because both of these were pure rules for a
// while: files full of careful decisions that NOTHING CALLED. A rules file with
// a green test suite and no caller reads exactly like a feature.
//
// So these guards assert the seams: the request keys are read, the results
// reach the reply, the gate is fed THIS run's coverage rather than a caller's
// assurance, and the correction surface executes nothing.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AuditSurfaceWiringTests
    {
        private static DirectoryInfo Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d;
        }

        private static string Audit()
        {
            return File.ReadAllText(Path.Combine(Root().FullName,
                "src", "Horizun.Revit", "Commands", "AuditModelCommand.cs"));
        }

        [Fact]
        public void Both_surfaces_are_read_from_the_request_and_reach_the_reply()
        {
            string src = Audit();

            // Read...
            Assert.Contains("request[\"propose_corrections\"]", src);
            Assert.Contains("preventionGate = request[\"prevention_gate\"] as JObject;", src);
            // ...used...
            Assert.Contains("ProposeCorrections(proposeCorrections, findings", src);
            Assert.Contains("DecidePrevention(preventionGate, findings", src);
            // ...and published. A block computed and dropped is the same as no block.
            Assert.Contains("[\"corrections\"] = corrections,", src);
            Assert.Contains("[\"prevention\"] = prevention,", src);
        }

        [Fact]
        public void Snapshot_trend_and_health_are_connected_to_the_live_audit()
        {
            string src = Audit();
            Assert.Contains("request[\"store_snapshot\"]", src);
            Assert.Contains("request[\"health_profile\"]", src);
            Assert.Contains("AuditDiagnosticArtifacts.SnapshotAndTrend", src);
            Assert.Contains("AuditDiagnosticArtifacts.Health", src);
            Assert.Contains("[\"snapshot\"] = snapshot", src);
            Assert.Contains("[\"trend\"] = trend", src);
            Assert.Contains("[\"health_index\"] = healthIndex", src);
        }

        [Fact]
        public void Correction_proposals_come_from_the_registry_and_nothing_else()
        {
            string src = Audit();

            Assert.Contains("CorrectionRegistry.Default, title, fingerprint", src);
            // The registry travels with the answer ON THE TALLY, so a caller who
            // asked for corrections can see what will NEVER be proposed rather than
            // inferring it from an empty list. Asserted on the exact line: the name
            // appears twice in this file, and matching it loosely meant the guard
            // stayed green with the tally's copy deleted.
            Assert.Contains("tally[\"registry\"] = CorrectionRegistry.Describe();", src);
            // And on the unasked path too, where it is the only thing the block says.
            Assert.Contains("[\"registry\"] = CorrectionRegistry.Describe()", src);
            // And nothing in this command runs one.
            Assert.DoesNotContain("horizun_execute_python", src);
        }

        [Fact]
        public void The_correction_surface_says_it_executed_nothing()
        {
            string src = Audit();
            Assert.Contains("tally[\"executed\"] = false;", src);
        }

        [Fact]
        public void A_truncated_finding_is_marked_truncated_rather_than_corrected_in_part()
        {
            string src = Audit();
            // shown < total is the only signal the reply carries about a scope that
            // did not fit, and correcting the part that fitted is the one mistake
            // this surface must not make.
            Assert.Contains("bool truncated = total > shown;", src);
            Assert.Contains("Truncated = truncated", src);
        }

        [Fact]
        public void The_gate_is_fed_this_runs_coverage_rather_than_a_callers_assurance()
        {
            string src = Audit();

            // The asymmetry is only real if the coverage is measured. A gate handed a
            // caller's "coverage_complete: true" would decide on somebody's word.
            Assert.Contains("CoverageComplete = checksFailed.Count == 0 && incompleteChecks.Count == 0 &&", src);
            Assert.Contains("visibility.CoverageComplete,", src);
            Assert.Contains("AuditSupplied = true,", src);
            Assert.Contains("PreventionGateRules.Decide(input,", src);
        }

        [Fact]
        public void The_gate_decides_and_says_it_does_not_enforce()
        {
            string src = Audit();
            Assert.Contains("json[\"enforced\"] = false;", src);
            Assert.Contains("prevention-operation-matrix.md", src);
        }

        [Fact]
        public void An_operation_the_bridge_cannot_gate_is_refused_rather_than_allowed()
        {
            string src = Audit();
            Assert.Contains("!GatedOperation.All.Contains(operation)", src);
            Assert.Contains("GateDecision.NotAssessable", src);
        }

        [Fact]
        public void Neither_surface_runs_unless_it_was_asked_for()
        {
            string src = Audit();
            // An audit that always hands back a list of things it could change is a
            // different tool from one that reports what it found.
            Assert.Contains("[\"status\"] = \"not_requested\",", src);
            Assert.Contains("That is not a claim", src);
            // And an unasked surface says what its silence does NOT mean, in both
            // directions: no proposals is not "this model needs none", and no gate
            // verdict is not permission for anything.
            Assert.Contains("permission for anything.", src);
        }
    }
}
