// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// VERIFIED IFC DELIVERY - the Revit-free half of horizun_deliver_ifc.
//
// A delivery is a FILE somebody else will open. Its verdict is therefore the
// file's, not the model's: the IDS is evaluated against the exported IFC, the
// user-defined property sets are looked for in the exported IFC, and the header
// the exporter wrote is read back before anyone is told which schema they got.
// This file holds every decision that can be made without a building open:
//
//   * WHICH FILE_SCHEMA a requested IFC version must produce, and whether the
//     head and tail of the produced file say that (a truncated file has no
//     END-ISO-10303-21 trailer, and a truncated file is not a deliverable);
//   * the closed vocabulary of the coordinate basis the exporter is asked for;
//   * the GATES and the one rule that turns them into deliverable_ready;
//   * the minimal composition of a file name from an information container.
//
// NAMING IS DELIBERATELY MINIMAL HERE. ISO 19650 information containers are
// modelled server-side as well; this is only the pure join the add-in needs to
// name a file, it accepts the same object shape (fields, field_order,
// separator, field_patterns) and adds nothing of its own. An integrator unifying
// the two must keep exactly this behaviour or replace the call site.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class DeliveryGateStatus
    {
        public const string Passed = "passed";
        public const string Failed = "failed";
        public const string Skipped = "skipped";
        public const string NotDecidable = "not_decidable";
    }

    /// <summary>One gate of a delivery, with the evidence that decided it.</summary>
    public sealed class DeliveryGate
    {
        public string Name;
        public string Status = DeliveryGateStatus.Skipped;

        /// <summary>Was this gate asked for by the call? A gate nobody asked for is skipped, not failed.</summary>
        public bool Requested;

        /// <summary>
        /// An advisory gate is REPORTED and never decides readiness. The model pre-check is
        /// the one such gate: the delivery's verdict is the file's, and a Revit parameter
        /// is not evidence of an IFC property set.
        /// </summary>
        public bool Advisory;

        public string Reason;
        public JObject Evidence;

        public JObject ToJson() => new JObject
        {
            ["gate"] = Name,
            ["status"] = Status,
            ["requested"] = Requested,
            ["advisory"] = Advisory,
            ["reason"] = Reason,
            ["evidence"] = Evidence
        };
    }

    public static class IfcDeliveryRules
    {
        /// <summary>The fixed gate order of the report.</summary>
        public static readonly string[] GateOrder =
            { "precheck", "export", "schema_header", "ids_validate", "pset_mapping", "bcf" };

        /// <summary>
        /// IFC versions this tool offers, each with the FILE_SCHEMA family it must write.
        ///
        /// Closed on purpose. Every name is a member of Autodesk.Revit.DB.IFCVersion in the
        /// years this bridge builds for (IFC4x3 from 2024 on - the command refuses it by
        /// name on a Revit whose enum lacks it), and every family is one the header check
        /// can prove. IFC2x2, IFCBCA and IFCSG are not offered: this build cannot say which
        /// FILE_SCHEMA they must produce, and an unprovable promise is not offered.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> VersionSchemaFamily =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["IFC2x3"] = "IFC2X3",
                ["IFC2x3CV2"] = "IFC2X3",
                ["IFC2x3BFM"] = "IFC2X3",
                ["IFC2x3FM"] = "IFC2X3",
                ["IFCCOBIE"] = "IFC2X3",
                ["IFC4"] = "IFC4",
                ["IFC4RV"] = "IFC4",
                ["IFC4DTV"] = "IFC4",
                ["IFC4x3"] = "IFC4X3"
            };

        /// <summary>
        /// The coordinate basis, argument vocabulary -> the exporter's SiteTransformBasis name.
        /// The exporter reads the option "SitePlacement" with Enum.TryParse, so the NAME is
        /// what travels (documented behaviour of the open-source Revit IFC exporter, not
        /// something this bridge can read back from the option object).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> SitePlacementOption =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shared"] = "Shared",
                ["survey_point"] = "Site",
                ["project_base_point"] = "Project",
                ["internal"] = "Internal"
            };

        /// <summary>The FILE_SCHEMA family a schema identifier belongs to, or null.</summary>
        public static string SchemaFamily(string schemaIdentifier)
        {
            string s = (schemaIdentifier ?? "").Trim().ToUpperInvariant().Replace("-", "_");
            if (s.Length == 0) return null;
            // IFC4X3 BEFORE IFC4: every IFC4X3 identifier also starts with IFC4, and a
            // prefix test in the other order would accept an IFC4X3 file as IFC4.
            if (s.StartsWith("IFC4X3", StringComparison.Ordinal)) return "IFC4X3";
            if (s.StartsWith("IFC4", StringComparison.Ordinal)) return "IFC4";
            if (s.StartsWith("IFC2X3", StringComparison.Ordinal)) return "IFC2X3";
            if (s.StartsWith("IFC2X2", StringComparison.Ordinal)) return "IFC2X2";
            return null;
        }

        /// <summary>
        /// Read back the head and tail of a produced IFC. Passed only when the file is an
        /// ISO-10303-21 exchange file, declares FILE_SCHEMA of the family the requested
        /// version must produce, and ends with END-ISO-10303-21 - the trailer is what a
        /// truncated write loses, so its absence fails the gate rather than being ignored.
        /// </summary>
        public static DeliveryGate CheckHeader(string headText, string tailText, string requestedVersion)
        {
            var gate = new DeliveryGate { Name = "schema_header", Requested = true };
            string expected;
            VersionSchemaFamily.TryGetValue(requestedVersion ?? "", out expected);
            string head = headText ?? "";
            string tail = tailText ?? "";
            bool iso = head.TrimStart('﻿', ' ', '\r', '\n', '\t')
                           .StartsWith("ISO-10303-21", StringComparison.Ordinal);
            string schema = ExportPresetRules.IfcSchemaOf(head);
            string family = SchemaFamily(schema);
            bool trailer = tail.IndexOf("END-ISO-10303-21", StringComparison.Ordinal) >= 0;

            gate.Evidence = new JObject
            {
                ["iso_10303_21_first_line"] = iso,
                ["file_schema"] = schema,
                ["file_schema_family"] = family,
                ["expected_family"] = expected,
                ["requested_version"] = requestedVersion,
                ["end_trailer_found"] = trailer
            };

            if (expected == null)
            {
                gate.Status = DeliveryGateStatus.NotDecidable;
                gate.Reason = "the requested version '" + requestedVersion + "' has no known FILE_SCHEMA family.";
                return gate;
            }
            var problems = new List<string>();
            if (!iso) problems.Add("the file does not start with ISO-10303-21, so it is not a STEP exchange file");
            if (schema == null) problems.Add("no FILE_SCHEMA could be read from the header");
            else if (family != expected)
                problems.Add("FILE_SCHEMA is '" + schema + "' and " + requestedVersion + " must produce " + expected);
            if (!trailer) problems.Add("the file does not end with END-ISO-10303-21; it is truncated or still being written");

            gate.Status = problems.Count == 0 ? DeliveryGateStatus.Passed : DeliveryGateStatus.Failed;
            gate.Reason = problems.Count == 0
                ? "FILE_SCHEMA '" + schema + "' is the " + expected + " family " + requestedVersion +
                  " must produce, and the file is complete to its END-ISO-10303-21 trailer."
                : string.Join("; ", problems) + ".";
            return gate;
        }

        /// <summary>
        /// The IDS validation gate, from the IDS report over the EXPORTED file. Any
        /// failing specification fails it. Anything not decided - a specification this
        /// build could not evaluate, or one that is not valid IDS - makes it not_decidable:
        /// never a pass, because an unchecked requirement is not a met one.
        /// </summary>
        public static DeliveryGate IdsGate(IdsReport report, string unreadableReason)
        {
            var gate = new DeliveryGate { Name = "ids_validate", Requested = true };
            if (report == null)
            {
                gate.Status = DeliveryGateStatus.NotDecidable;
                gate.Reason = unreadableReason ?? "the IDS validation did not run.";
                return gate;
            }
            int pass = report.Results.Count(r => r.Outcome == IdsOutcome.Pass);
            int fail = report.Results.Count(r => r.Outcome == IdsOutcome.Fail);
            int undecided = report.Results.Count(r => r.Outcome == IdsOutcome.NotDecidable);
            int invalid = report.Results.Count(r => r.Outcome == IdsOutcome.Invalid);
            gate.Evidence = new JObject
            {
                ["specifications"] = report.Results.Count, ["passing"] = pass, ["failing"] = fail,
                ["not_decidable"] = undecided, ["invalid"] = invalid
            };
            if (report.Results.Count == 0)
            {
                gate.Status = DeliveryGateStatus.NotDecidable;
                gate.Reason = "the IDS holds no specification; an empty demand is not a pass.";
            }
            else if (fail > 0)
            {
                gate.Status = DeliveryGateStatus.Failed;
                gate.Reason = fail + " of " + report.Results.Count + " specification(s) fail on the exported file.";
            }
            else if (undecided > 0 || invalid > 0)
            {
                gate.Status = DeliveryGateStatus.NotDecidable;
                gate.Reason = undecided + " specification(s) could not be decided and " + invalid +
                              " are not valid IDS; nothing failed, and nothing unchecked is counted as passed.";
            }
            else
            {
                gate.Status = DeliveryGateStatus.Passed;
                gate.Reason = "every specification passes on the exported file, at the evidence level ifc_validated.";
            }
            return gate;
        }

        /// <summary>
        /// The one rule. deliverable_ready is true only when every REQUESTED, non-advisory
        /// gate PASSED (a gate that was requested and then skipped because there was nothing
        /// to do - a BCF with no failure to report - does not block), and the export and
        /// the header gates, which every delivery requests, passed.
        /// </summary>
        public static bool DeliverableReady(IEnumerable<DeliveryGate> gates, out List<string> blocking)
        {
            blocking = new List<string>();
            var list = (gates ?? Enumerable.Empty<DeliveryGate>()).Where(g => g != null).ToList();
            foreach (string mandatory in new[] { "export", "schema_header" })
                if (!list.Any(g => g.Name == mandatory && g.Status == DeliveryGateStatus.Passed))
                    blocking.Add(mandatory + " did not pass");
            foreach (DeliveryGate gate in list)
            {
                if (!gate.Requested || gate.Advisory) continue;
                if (gate.Name == "export" || gate.Name == "schema_header") continue;
                if (gate.Status == DeliveryGateStatus.Passed || gate.Status == DeliveryGateStatus.Skipped) continue;
                blocking.Add(gate.Name + " is " + gate.Status);
            }
            return blocking.Count == 0;
        }

        /// <summary>Gates in report order, filling any missing one as skipped-not-requested.</summary>
        public static JArray GatesJson(IEnumerable<DeliveryGate> gates)
        {
            var byName = (gates ?? Enumerable.Empty<DeliveryGate>()).Where(g => g != null)
                         .GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.Last());
            var array = new JArray();
            foreach (string name in GateOrder)
            {
                DeliveryGate gate;
                if (!byName.TryGetValue(name, out gate))
                    gate = new DeliveryGate { Name = name, Status = DeliveryGateStatus.Skipped, Requested = false,
                                              Reason = "not requested by this call." };
                array.Add(gate.ToJson());
            }
            return array;
        }

        // =====================================================================
        // The output name
        // =====================================================================

        /// <summary>
        /// The file STEM (no extension, no folder) from output_name and/or an information
        /// container. Null and a reason when neither is usable or when both are given and
        /// disagree - two names for one file is an ambiguity, not a choice to make silently.
        /// </summary>
        public static string ResolveStem(string outputName, JObject container, out string composedFromContainer,
                                         out string error)
        {
            error = null;
            composedFromContainer = null;
            string fromName = null;
            if (!string.IsNullOrWhiteSpace(outputName))
            {
                fromName = outputName.Trim();
                if (fromName.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                    fromName = fromName.Substring(0, fromName.Length - 4);
                string why = FileStemProblem(fromName);
                if (why != null) { error = "output_name " + why; return null; }
            }
            if (container != null)
            {
                composedFromContainer = ComposeContainerName(container, out error);
                if (composedFromContainer == null) return null;
                string why = FileStemProblem(composedFromContainer);
                if (why != null) { error = "the information_container name " + why; return null; }
            }
            if (fromName == null && composedFromContainer == null)
            {
                error = "output_name or information_container is required: the delivery will not invent a file name.";
                return null;
            }
            if (fromName != null && composedFromContainer != null &&
                !string.Equals(fromName, composedFromContainer, StringComparison.Ordinal))
            {
                error = "output_name '" + fromName + "' and the information_container name '" + composedFromContainer +
                        "' disagree. Send one, or make them the same.";
                return null;
            }
            return fromName ?? composedFromContainer;
        }

        /// <summary>
        /// The container's file stem, composed and validated by the ONE implementation the
        /// bridge has (InformationContainer): the same field order, patterns, suitability
        /// status and revision rules that horizun_export and horizun_information_container
        /// apply. A delivery container needs its status and revision, because the sidecar
        /// written next to the IFC records them.
        /// </summary>
        public static string ComposeContainerName(JObject container, out string error)
        {
            error = null;
            ContainerSpec spec;
            try { spec = InformationContainer.ParseSpec(container); }
            catch (ContainerRuleException ex) { error = ex.Message; return null; }
            ContainerValidation v = InformationContainer.Validate(spec, true);
            if (!v.Valid)
            {
                error = "information_container does not validate: " + string.Join("; ",
                    v.Problems.OfType<JObject>().Select(p => p.Value<string>("reason") ?? p.Value<string>("code")));
                return null;
            }
            return v.FileStem;
        }

        /// <summary>Why a stem cannot be a file name in a folder, or null.</summary>
        public static string FileStemProblem(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem)) return "is empty.";
            if (stem.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 ||
                stem.Any(c => c < 32))
                return "'" + stem + "' contains a character a Windows file name cannot hold; it names a file, not a path.";
            if (stem.EndsWith(".", StringComparison.Ordinal) || stem.EndsWith(" ", StringComparison.Ordinal))
                return "'" + stem + "' ends with a dot or a space, which Windows strips silently.";
            if (stem.Length > 180) return "is longer than 180 characters.";
            return null;
        }

        /// <summary>The BCF written beside the IFC for its IDS failures.</summary>
        public static string BcfPathFor(string ifcPath) =>
            Path.Combine(Path.GetDirectoryName(ifcPath) ?? "", Path.GetFileNameWithoutExtension(ifcPath) + ".ids-issues.bcf");
    }
}
