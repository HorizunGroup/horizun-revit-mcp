// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// WHAT AN IDS EVALUATION PRODUCED, and how much of it is a claim.
//
// THE DISTINCTION THIS FILE EXISTS TO CARRY. There are two ways to check an IDS
// and they answer different questions:
//
//   A PRE-CHECK over the live Revit model. Fast, available before anybody
//   exports, and STRUCTURALLY UNABLE to answer some of what IDS asks. A Revit
//   parameter named "FireRating" is not evidence that the exported IFC will carry
//   a property "FireRating" in the property set "Pset_WallCommon": that is decided
//   by the export mapping, at export time, and a pre-check that claimed otherwise
//   would be handing somebody a passing report about a file that does not exist
//   yet.
//
//   A VALIDATION over the exported IFC. Slower, needs the export to have
//   happened, and answers the question IDS actually asks - because the property
//   set, the classification, the material association and the partOf relations
//   are all IN the file, as relationships, and can be read.
//
// So every finding carries its EVIDENCE LEVEL, and the two are never summed into
// one number. A report that said "23 of 25 pass" without saying which twenty-five
// were checked against what would be the exact overstatement this campaign was
// reopened to remove.
//
// AND THE THIRD OUTCOME. `not_decidable` is not a soft fail: it is the honest
// answer when a construction is unsupported, a value is unreadable, or the
// evidence level cannot reach the question. It never counts as a pass, it never
// counts as a failure, and it is reported with the reason.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>How much a finding is entitled to claim.</summary>
    public static class IdsEvidence
    {
        /// <summary>Read from the live Revit model. Cannot establish IFC property-set membership.</summary>
        public const string RevitPrecheck = "revit_precheck";

        /// <summary>Read from an exported IFC file. Answers what IDS asks.</summary>
        public const string IfcValidated = "ifc_validated";
    }

    public static class IdsOutcome
    {
        public const string Pass = "pass";
        public const string Fail = "fail";

        /// <summary>Neither. Named, never silently folded into either.</summary>
        public const string NotDecidable = "not_decidable";

        /// <summary>
        /// THE IDS ITSELF does not comply with the specification, so the question it asks
        /// has no answer for any file.
        ///
        /// A THIRD OUTCOME, and the official test suite treats it as one - 27 of its 334
        /// cases expect it. Reporting those as `fail` makes a statement about somebody's
        /// MODEL when the defect is in the requirements document, and sends them looking
        /// for elements to fix that are not wrong. Reporting them as `not_decidable` is
        /// nearly as bad in the other direction: that means "this build could not tell",
        /// which is a fact about the checker, while this is a fact about the file that
        /// every conforming implementation is supposed to reach identically.
        /// </summary>
        public const string Invalid = "invalid";
    }

    /// <summary>One element's answer to one requirement facet.</summary>
    public sealed class IdsElementFinding
    {
        /// <summary>Whatever identifies the element in the world this was read from.</summary>
        public string EntityKey;

        /// <summary>The IFC GlobalId, when there is one. The identity that survives an export.</summary>
        public string GlobalId;

        /// <summary>The Revit ElementId, when this came from a model.</summary>
        public long? RevitElementId;

        public string IfcClass;
        public string Name;

        public string Outcome = IdsOutcome.NotDecidable;
        public string Facet;
        public string Reason;

        /// <summary>What was actually found, so a reader can check the finding rather than believe it.</summary>
        public string Observed;

        /// <summary>What the specification asked for, in words.</summary>
        public string Required;

        public JObject ToJson() => new JObject
        {
            ["outcome"] = Outcome,
            ["facet"] = Facet,
            ["global_id"] = GlobalId,
            ["revit_element_id"] = RevitElementId.HasValue ? (JToken)RevitElementId.Value : JValue.CreateNull(),
            ["entity"] = EntityKey,
            ["ifc_class"] = IfcClass,
            ["name"] = Name,
            ["required"] = Required,
            ["observed"] = Observed,
            ["reason"] = Reason
        };
    }

    /// <summary>One specification's whole answer.</summary>
    public sealed class IdsSpecificationResult
    {
        public string Name;
        public string Identifier;
        public string Evidence = IdsEvidence.IfcValidated;

        public int Applicable;
        public int Passing;
        public int Failing;
        public int NotDecidable;

        /// <summary>Set when the specification could not be evaluated at all.</summary>
        public string Undecidable;

        public readonly List<IdsElementFinding> Findings = new List<IdsElementFinding>();

        /// <summary>Constructions this build did not evaluate, by name.</summary>
        public readonly List<string> Unsupported = new List<string>();

        /// <summary>Corrections a person could apply. PROPOSED, never applied here.</summary>
        public readonly List<IdsCorrection> Corrections = new List<IdsCorrection>();

        /// <summary>
        /// The specification's own verdict.
        ///
        /// NOT DECIDABLE WINS OVER PASS, always. A specification with one unevaluated element
        /// is a specification nobody has checked, and rounding it up to a pass is how an
        /// unsupported construction becomes a clean report.
        /// </summary>
        /// <summary>
        /// Why this specification cannot be asked of any file, or null.
        ///
        /// Set from the reader's own defects. It OUTRANKS every other outcome, because a
        /// malformed question has no answer: evaluating it anyway and reporting whatever
        /// came out is how a defect in a requirements document becomes a finding against
        /// a model.
        /// </summary>
        public string InvalidBecause;

        public string Outcome
        {
            get
            {
                if (InvalidBecause != null) return IdsOutcome.Invalid;
                if (Undecidable != null) return IdsOutcome.NotDecidable;
                if (Failing > 0) return IdsOutcome.Fail;
                if (NotDecidable > 0) return IdsOutcome.NotDecidable;
                return IdsOutcome.Pass;
            }
        }

        public JObject ToJson(int maxFindings) => new JObject
        {
            ["specification"] = Name,
            ["identifier"] = Identifier,
            ["evidence"] = Evidence,
            ["outcome"] = Outcome,
            ["applicable"] = Applicable,
            ["passing"] = Passing,
            ["failing"] = Failing,
            ["not_decidable"] = NotDecidable,
            ["undecidable_reason"] = Undecidable,
            ["invalid_because"] = InvalidBecause == null ? (JToken)JValue.CreateNull() : InvalidBecause,
            ["unsupported"] = new JArray(Unsupported.Distinct()),
            ["findings_shown"] = Math.Min(Findings.Count, maxFindings),
            ["findings_total"] = Findings.Count,
            ["findings"] = new JArray(Findings.Take(maxFindings).Select(f => f.ToJson())),
            ["corrections"] = new JArray(Corrections.Take(maxFindings).Select(c => c.ToJson())),
            ["outcome_means"] = Outcome == IdsOutcome.Invalid
                ? "INVALID. The problem is in the IDS, not in the model: this specification does not " +
                  "comply with the audit specification, so no file can satisfy or violate it. Nothing " +
                  "here is a finding about any element, and looking for elements to fix would be " +
                  "looking for something that is not wrong."
                : Outcome == IdsOutcome.NotDecidable
                ? "NOT DECIDABLE. Either the specification could not be evaluated at all, or at least " +
                  "one applicable element could not be. It is not a pass: a specification with one " +
                  "unchecked element is a specification nobody has checked."
                : Outcome == IdsOutcome.Fail
                    ? "at least one applicable element does not satisfy the requirements. The findings " +
                      "name which, and what was observed."
                    : "every applicable element satisfied every requirement this build evaluated, at " +
                      "the evidence level above."
        };
    }

    /// <summary>
    /// A correction somebody could apply. PROPOSED AND NEVER APPLIED.
    ///
    /// It is a REQUEST, built against a tool that already exists, with its own rehearsal and
    /// its own re-read. This file opens no transaction and holds no reference to anything
    /// that could: an IDS validator that could also edit the model is a validator whose
    /// findings nobody can trust, because the thing reporting the defect also caused the fix.
    /// </summary>
    public sealed class IdsCorrection
    {
        public string Specification;
        public string Facet;
        public string What;
        public string Why;

        /// <summary>Elements the correction would touch, by Revit id where one is known.</summary>
        public readonly List<long> RevitElementIds = new List<long>();

        /// <summary>Elements by IFC GlobalId, for findings that came from a file.</summary>
        public readonly List<string> GlobalIds = new List<string>();

        /// <summary>A ready request for an existing tool, or null when no tool covers it.</summary>
        public JObject Request;

        /// <summary>Why no request could be built, when none could.</summary>
        public string NotAutomatable;

        public JObject ToJson() => new JObject
        {
            ["specification"] = Specification,
            ["facet"] = Facet,
            ["what"] = What,
            ["why"] = Why,
            ["revit_element_ids"] = new JArray(RevitElementIds.Take(200)),
            ["global_ids"] = new JArray(GlobalIds.Take(200)),
            ["elements_total"] = Math.Max(RevitElementIds.Count, GlobalIds.Count),
            ["request"] = Request,
            ["not_automatable"] = NotAutomatable,
            ["applied"] = false,
            ["means"] = Request == null
                ? "no request was built, and the reason is above. Reporting a correction nobody can " +
                  "send would be worse than reporting none."
                : "a REQUEST, not a change. Nothing here writes to a model: send it to the tool it " +
                  "names, which rehearses, confirms and re-reads its own work exactly as it does for " +
                  "any other caller. A validator that could also edit the model is a validator whose " +
                  "findings nobody can trust."
        };
    }

    /// <summary>The whole run, with the two evidence levels kept apart.</summary>
    public sealed class IdsReport
    {
        public string Operation;
        public string Evidence;
        public string Source;

        public readonly List<IdsSpecificationResult> Results = new List<IdsSpecificationResult>();
        public readonly List<string> FileProblems = new List<string>();

        public JObject ToJson(int maxFindings)
        {
            int pass = Results.Count(r => r.Outcome == IdsOutcome.Pass);
            int fail = Results.Count(r => r.Outcome == IdsOutcome.Fail);
            int undecided = Results.Count(r => r.Outcome == IdsOutcome.NotDecidable);
            int invalid = Results.Count(r => r.Outcome == IdsOutcome.Invalid);

            return new JObject
            {
                ["operation"] = Operation,
                ["evidence"] = Evidence,
                ["source"] = Source,
                ["specifications"] = Results.Count,
                ["passing"] = pass,
                ["failing"] = fail,
                ["not_decidable"] = undecided,
                ["invalid"] = invalid,
                ["invalid_means"] =
                    "specifications whose own definition does not comply with the IDS audit specification. " +
                    "They are counted apart from failures ON PURPOSE: a failure is a statement about the " +
                    "model, and this is a statement about the requirements document. Adding the two " +
                    "together sends somebody looking for elements to fix that are not wrong.",
                ["ids_file_problems"] = new JArray(FileProblems),
                ["results"] = new JArray(Results.Select(r => r.ToJson(maxFindings))),
                ["not_a_certificate"] =
                    "This is not a certificate of IDS conformance. It is what THIS build evaluated, at " +
                    "the evidence level named above, against the constructions it supports - and every " +
                    "construction it does not support is listed per specification rather than skipped. " +
                    undecided + " specification(s) could not be decided and " + invalid + " are not " +
                    "valid IDS at all; both are counted separately from the " + pass + " that passed, " +
                    "precisely so they cannot be added together by mistake."
            };
        }
    }
}
