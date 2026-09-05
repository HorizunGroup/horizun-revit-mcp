// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// GUIDED CORRECTIONS: turning findings into PROPOSALS, and nothing else.
//
// The Doctor stays read-only. This surface produces typed proposals a human
// reads; it executes nothing, and execution - if it is ever built - is a
// separate call with its own confirmation.
//
// THE REGISTRY IS THE WHOLE SAFETY MODEL. A proposal may only name a tool from
// an explicit allow-list, with arguments built from typed fields. Nothing is
// assembled out of free text: a tool name composed from a finding's message is
// how a report becomes an arbitrary command, and no amount of validation
// downstream fixes a name that came from a string somebody else wrote.
//
// FOUR THINGS A PROPOSAL MUST SURVIVE, all checked here:
//
//   the finding still exists, in THIS document, at this fingerprint;
//   the tool exists in the registry and the arguments satisfy its contract;
//   the proposal has not expired;
//   the target elements are the finding's, never a superset.
//
// That last one is the quiet failure: a proposal that widens from "these four
// walls" to "all walls" is still well-typed, still passes its contract, and
// does something nobody agreed to.
//
// AMBIGUITY IS RETURNED, NOT RESOLVED. Where a correction could go two ways the
// proposal carries both with their trade-offs and asks. Choosing for the user
// is how a tool that was going to save them an afternoon costs them a week.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ProposalState
    {
        public const string Actionable = "actionable";
        public const string RequiresInput = "requires_input";
        public const string Unsupported = "unsupported";
        public const string Unsafe = "unsafe";
        public const string AlreadyResolved = "already_resolved";
        public const string NotApplicable = "not_applicable";

        public static readonly string[] All =
        {
            Actionable, RequiresInput, Unsupported, Unsafe, AlreadyResolved, NotApplicable
        };
    }

    public static class ProposalRefusal
    {
        public const string UnknownFinding = "unknown_finding";
        public const string WrongDocument = "wrong_document";
        public const string FingerprintChanged = "fingerprint_changed";
        public const string Expired = "expired";
        public const string NoSuchTool = "no_such_tool";
        public const string BadArguments = "bad_arguments";
        public const string ScopeWidened = "scope_widened";
        public const string Truncated = "truncated_findings";
        public const string RequiresPython = "requires_arbitrary_code";
    }

    /// <summary>
    /// One correction this bridge is willing to propose. The registry is an
    /// allow-list: a finding type not in it yields `unsupported`, which is an
    /// honest answer, rather than an improvised tool call.
    /// </summary>
    /// <summary>
    /// What "corrected" looks like for a recipe. See CorrectionRecipe.Postcondition.
    /// </summary>
    public static class CorrectionPostcondition
    {
        /// <summary>The element leaves the finding: a defect list stops listing it.</summary>
        public const string RemovedFromFinding = "removed_from_finding";

        /// <summary>
        /// The element STAYS listed - the finding is an inventory - and its typed
        /// filter code stops being one of the values the recipe acts on.
        /// </summary>
        public const string ItemLeavesTheFilter = "item_leaves_the_filter";

        public const string Means =
            "removed_from_finding: the check is a defect list and a corrected element is gone from it. " +
            "item_leaves_the_filter: the check is an INVENTORY that lists the element whatever its state, so " +
            "the correction is judged on the item's own typed code - and its absence is NOT success, because " +
            "an item that is no longer listed may have been deleted or cut off by top.";
    }

    public sealed class CorrectionRecipe
    {
        public string FindingType;
        public string Tool;
        /// <summary>Argument names this tool requires. Values come from typed fields only.</summary>
        public List<string> RequiredArguments = new List<string>();

        /// <summary>
        /// Typed constants this correction always sends - operation names, modes,
        /// flags. DECLARED HERE, never read out of a finding's text: a tool call
        /// composed from a message is how a report becomes an arbitrary command.
        /// </summary>
        public JObject FixedArguments;

        /// <summary>
        /// When set, this tool acts on ONE element and the proposal carries that id
        /// under this argument name. A finding with four elements becomes four
        /// proposals rather than one call the tool cannot make - and a proposal
        /// carrying more than one id under a single-element argument is refused
        /// rather than silently acting on the first.
        /// </summary>
        public string ElementArgument;

        /// <summary>
        /// When the tool acts on a LIST, the argument name it takes the list under.
        /// horizun_delete_verified reads `ids`; nothing reads `element_ids`. Null
        /// means `element_ids`, which keeps the older tests and callers honest about
        /// what they were given.
        /// </summary>
        public string ElementsArgument;

        /// <summary>
        /// A TYPED CODE the finding's items carry, and the values a correction may
        /// act on. The rooms finding lists unplaced AND unenclosed rooms in one
        /// list, and only the first can be deleted as a correction; the links
        /// finding lists every link type with its status, and only an unloaded one
        /// can be reloaded. The filter reads the code, never the sentence beside it.
        /// Items outside the filter are excluded from the proposal by name.
        /// </summary>
        public string ItemFilterField;
        public List<string> ItemFilterValues = new List<string>();

        /// <summary>
        /// WHAT THE MODEL LOOKS LIKE WHEN THIS CORRECTION WORKED, so the re-audit
        /// can check the postcondition instead of assuming one.
        ///
        /// The default is the one most findings have: the element LEAVES the
        /// finding. Pin the link, delete the room, give the view a template, and
        /// the check stops listing it.
        ///
        /// An INVENTORY finding does not work that way. The links check lists
        /// every link type with its status, loaded ones included, so a reloaded
        /// link is still listed - and a re-audit that judges by disappearance
        /// called a verified reload `persistent` in every year of the matrix. For
        /// those the correction is done when the item's own typed code stops
        /// being one of the values that made it a finding, and its ABSENCE proves
        /// nothing: the type may have been deleted, or fallen past the cut.
        /// </summary>
        public string Postcondition = CorrectionPostcondition.RemovedFromFinding;

        /// <summary>
        /// THE CALLER MUST LIST THE IDS. Set on a correction that destroys something.
        ///
        /// The registry already promised this about the two deletions - "narrowed to the
        /// ids the caller listed" - and the selection did not enforce it: an action that
        /// named the finding and omitted element_ids acted on EVERY element the finding
        /// listed. That is the same reading of an absent selection that `actions: []` is
        /// refused for ("an empty selection is NOT every finding"), one level down and
        /// with an irreversible delete on the other side of it.
        ///
        /// A recipe that only pins or reloads keeps the old default: naming the finding
        /// is a decision about a reversible change to the elements the finding named.
        /// </summary>
        public bool RequiresExplicitSelection;

        /// <summary>
        /// The tool takes its work as an `actions` array rather than as top-level
        /// arguments - horizun_manage_views does. When set, everything the recipe
        /// built except target_document and dry_run is wrapped as actions[0].
        /// Without this the views_without_template recipe produced a call the
        /// tool refused, which nothing noticed while proposals were only read.
        /// </summary>
        public bool ActionsEnvelope;

        /// <summary>
        /// The ambiguities ARE the questions the required inputs answer. Once every
        /// required input is supplied the proposal is actionable and the ambiguities
        /// travel as caveats; while one is missing the proposal is requires_input
        /// naming it. Off by default: a recipe whose ambiguity is not an argument
        /// (delete or explode?) stays requires_input however it is called.
        /// </summary>
        public bool AmbiguitiesResolvedByInputs;
        public bool DryRunSupported = true;
        public bool ConfirmationRequired = true;
        public string Risk = "low";
        public bool Reversible = true;
        /// <summary>Set when this correction cannot be automated, and why.</summary>
        public string CannotAutomateBecause;
        /// <summary>Choices a human must make before this can proceed.</summary>
        public List<string> Ambiguities = new List<string>();
        public string ExpectedOutcome;
        public string Verification;
    }

    public sealed class Finding
    {
        public string FindingId;
        public string FindingType;
        public string DocumentTitle;
        public string DocumentFingerprint;
        public List<long> ElementIds = new List<long>();
        /// <summary>True when the finding's own list was cut, so its scope is unknown.</summary>
        public bool Truncated;
        public bool Resolved;
    }

    public sealed class CorrectionProposal
    {
        public string ProposalId;
        public string FindingId;
        public string FindingType;
        public string TargetDocument;
        public string DocumentFingerprint;
        public List<long> ElementIds = new List<long>();
        public string Tool;
        public JObject Arguments;
        public List<string> Preconditions = new List<string>();
        public string ExpectedOutcome;
        public string Risk;
        public bool Reversible;
        public List<string> Ambiguities = new List<string>();
        public bool DryRunSupported;
        public bool ConfirmationRequired = true;
        public string Verification;
        public string CannotAutomateBecause;
        public string State;
        public string RefusalCode;
        public string Why;
        /// <summary>UTC stamp supplied by the caller; this file never reads a clock.</summary>
        public string IssuedUtc;
        public string ExpiresUtc;
    }

    public static class GuidedCorrectionRules
    {
        public const string ReadOnlyMeans =
            "the Doctor stays read-only. This surface produces PROPOSALS a human reads and never executes one; " +
            "execution, if it exists, is a separate call with its own document check, its own confirmation and " +
            "its own dry run. Nothing here writes to a model.";

        public const string RegistryMeans =
            "a proposal may only name a tool from an explicit registry, with arguments built from typed fields. " +
            "Nothing is assembled from free text: a tool name composed out of a finding's message is how a " +
            "report becomes an arbitrary command, and validation downstream cannot repair a name that came " +
            "from a string somebody else wrote.";

        public const string AmbiguityMeans =
            "where a correction could go two ways the proposal carries BOTH with their trade-offs and asks. " +
            "Choosing for somebody is how a tool that would have saved them an afternoon costs them a week.";

        /// <summary>
        /// Builds a proposal, or explains why there is none. Every refusal names a
        /// code, because "no proposal" is several different situations and a
        /// caller needs to know which.
        /// </summary>
        public static CorrectionProposal Propose(Finding finding,
                                                 IReadOnlyDictionary<string, CorrectionRecipe> registry,
                                                 string targetDocument,
                                                 string documentFingerprint,
                                                 string issuedUtc,
                                                 string expiresUtc)
        {
            return Propose(finding, registry, targetDocument, documentFingerprint, issuedUtc, expiresUtc, null);
        }

        /// <summary>
        /// The same, with the INPUTS a caller supplied for the recipe's required
        /// arguments - the template a view should follow, and nothing else. An
        /// input naming an argument the recipe did not ask for is refused: the
        /// caller may answer the recipe's questions, not add to its call.
        /// </summary>
        public static CorrectionProposal Propose(Finding finding,
                                                 IReadOnlyDictionary<string, CorrectionRecipe> registry,
                                                 string targetDocument,
                                                 string documentFingerprint,
                                                 string issuedUtc,
                                                 string expiresUtc,
                                                 JObject inputs)
        {
            var p = new CorrectionProposal
            {
                FindingId = finding == null ? null : finding.FindingId,
                FindingType = finding == null ? null : finding.FindingType,
                TargetDocument = targetDocument,
                DocumentFingerprint = documentFingerprint,
                IssuedUtc = issuedUtc,
                ExpiresUtc = expiresUtc,
                ConfirmationRequired = true
            };

            if (finding == null)
                return Refuse(p, ProposalState.NotApplicable, ProposalRefusal.UnknownFinding,
                    "there is no such finding.");

            p.ProposalId = "prop:" + (finding.FindingId ?? "?");

            // ALREADY FIXED is not a failure and not an action.
            if (finding.Resolved)
                return Refuse(p, ProposalState.AlreadyResolved, null,
                    "this finding is already resolved; there is nothing to propose.");

            // THE DOCUMENT MUST BE THE ONE THE FINDING CAME FROM.
            if (!string.Equals(finding.DocumentTitle, targetDocument, StringComparison.Ordinal))
                return Refuse(p, ProposalState.Unsafe, ProposalRefusal.WrongDocument,
                    "this finding came from '" + finding.DocumentTitle + "' and the target is '" +
                    targetDocument + "'. A correction aimed at the wrong document is the worst thing this " +
                    "surface could produce.");

            if (!string.Equals(finding.DocumentFingerprint, documentFingerprint, StringComparison.Ordinal))
                return Refuse(p, ProposalState.Unsafe, ProposalRefusal.FingerprintChanged,
                    "the document has changed since this finding was recorded, so its element ids may now " +
                    "name different elements.");

            // A TRUNCATED FINDING HAS AN UNKNOWN SCOPE.
            if (finding.Truncated)
                return Refuse(p, ProposalState.RequiresInput, ProposalRefusal.Truncated,
                    "this finding's element list was truncated, so the full scope of the correction is " +
                    "unknown. Re-run the audit with a budget that returns all of them.");

            CorrectionRecipe recipe;
            if (registry == null || !registry.TryGetValue(finding.FindingType ?? "", out recipe))
                return Refuse(p, ProposalState.Unsupported, ProposalRefusal.NoSuchTool,
                    "no correction is registered for '" + finding.FindingType + "'. That is an honest answer: " +
                    "improvising one would mean composing a tool call for a situation nobody reviewed.");

            if (recipe.CannotAutomateBecause != null)
                return Refuse(p, ProposalState.Unsupported, null, recipe.CannotAutomateBecause);

            p.Tool = recipe.Tool;
            p.Risk = recipe.Risk;
            p.Reversible = recipe.Reversible;
            p.DryRunSupported = recipe.DryRunSupported;
            p.ConfirmationRequired = recipe.ConfirmationRequired;
            p.ExpectedOutcome = recipe.ExpectedOutcome;
            p.Verification = recipe.Verification;
            p.Ambiguities = new List<string>(recipe.Ambiguities);
            p.ElementIds = new List<long>(finding.ElementIds);
            p.Preconditions.Add("the document is '" + targetDocument + "' at fingerprint " +
                                documentFingerprint);
            p.Preconditions.Add("the finding is still open");

            // ARGUMENTS FROM TYPED FIELDS ONLY.
            p.Arguments = new JObject
            {
                ["target_document"] = targetDocument,
                ["dry_run"] = true
            };

            if (recipe.ElementArgument != null)
            {
                // ONE ELEMENT, NAMED. A tool that pins one link cannot be handed
                // four, and choosing the first would act on a scope nobody agreed.
                if (finding.ElementIds.Count != 1)
                    return Refuse(p, ProposalState.RequiresInput, ProposalRefusal.BadArguments,
                        "'" + recipe.Tool + "' acts on one element and this finding names " +
                        finding.ElementIds.Count + ". " +
                        (finding.ElementIds.Count == 0
                            ? "There is nothing here to act on: the check reported an issue without naming an " +
                              "element, which usually means it could not read some of them. What is missing is " +
                              "the elements, not an argument."
                            : "Split it into one proposal per element; acting on the first would silently " +
                              "narrow the correction to a scope nobody chose."));
                p.Arguments[recipe.ElementArgument] = finding.ElementIds[0];
            }
            else
            {
                if (finding.ElementIds.Count == 0)
                    return Refuse(p, ProposalState.RequiresInput, ProposalRefusal.BadArguments,
                        "'" + recipe.Tool + "' acts on a list of elements and this finding names none. The " +
                        "check reported an issue without naming an element, which usually means it could not " +
                        "read some of them - or every item it listed was outside what this correction may " +
                        "touch. What is missing is the elements, not an argument.");
                p.Arguments[recipe.ElementsArgument ?? "element_ids"] =
                    new JArray(finding.ElementIds.Select(x => (JToken)x));
            }

            // The registry's own typed constants. Merged after the element fields and
            // never over them: a recipe cannot redirect a correction to another
            // document or turn a rehearsal into a write.
            if (recipe.FixedArguments != null)
                foreach (JProperty f in recipe.FixedArguments.Properties())
                {
                    if (f.Name == "target_document" || f.Name == "dry_run")
                        return Refuse(p, ProposalState.Unsafe, ProposalRefusal.BadArguments,
                            "a recipe may not set '" + f.Name + "'. The target document and the rehearsal flag " +
                            "are decided by this surface, not by an entry in the registry.");
                    p.Arguments[f.Name] = f.Value.DeepClone();
                }

            // THE CALLER'S INPUTS, and only where the recipe asked a question. An input
            // for an argument the recipe never declared is refused rather than merged:
            // merging it would let a caller add a field to a typed call the registry
            // did not review, which is the same door the registry exists to close.
            if (inputs != null)
                foreach (JProperty i in inputs.Properties())
                {
                    if (!recipe.RequiredArguments.Contains(i.Name))
                        return Refuse(p, ProposalState.RequiresInput, ProposalRefusal.BadArguments,
                            "input '" + i.Name + "' is not one '" + recipe.Tool + "' asks for on this finding. " +
                            (recipe.RequiredArguments.Count == 0
                                ? "This correction takes no inputs."
                                : "It asks for: " + string.Join(", ", recipe.RequiredArguments) + "."));
                    if (i.Value == null || i.Value.Type == JTokenType.Null)
                        return Refuse(p, ProposalState.RequiresInput, ProposalRefusal.BadArguments,
                            "input '" + i.Name + "' is null, which is not an answer.");
                    p.Arguments[i.Name] = i.Value.DeepClone();
                }

            foreach (string required in recipe.RequiredArguments)
                if (p.Arguments[required] == null)
                    return Refuse(p, ProposalState.RequiresInput, ProposalRefusal.BadArguments,
                        "'" + recipe.Tool + "' requires '" + required + "', which this finding does not supply. " +
                        "Pass it as an input on the action.");

            if (p.Ambiguities.Count > 0 && !recipe.AmbiguitiesResolvedByInputs)
            {
                p.State = ProposalState.RequiresInput;
                p.Why = "this correction could go more than one way. " + AmbiguityMeans;
                return p;
            }

            // THE ENVELOPE. A tool that takes an actions array gets one action holding
            // everything the recipe built; the two surface-owned fields stay outside it.
            if (recipe.ActionsEnvelope)
            {
                var action = new JObject();
                foreach (JProperty f in p.Arguments.Properties().ToList())
                {
                    if (f.Name == "target_document" || f.Name == "dry_run") continue;
                    action[f.Name] = f.Value;
                    p.Arguments.Remove(f.Name);
                }
                p.Arguments["actions"] = new JArray(action);
            }

            p.State = ProposalState.Actionable;
            p.Why = "proposed, not performed. " + ReadOnlyMeans;
            return p;
        }

        private static CorrectionProposal Refuse(CorrectionProposal p, string state, string code, string why)
        {
            p.State = state;
            p.RefusalCode = code;
            p.Why = why;
            return p;
        }

        /// <summary>
        /// The scope check, applied to whatever an executor is about to run. A
        /// proposal whose ids have grown since it was made is refused: widening
        /// from four walls to every wall is well-typed and unagreed.
        /// </summary>
        public static bool ScopeIsUnchanged(CorrectionProposal p, Finding original, out string why)
        {
            why = null;
            if (p == null || original == null) { why = "nothing to compare."; return false; }

            var proposed = new HashSet<long>(p.ElementIds ?? new List<long>());
            var found = new HashSet<long>(original.ElementIds ?? new List<long>());
            if (proposed.Except(found).Any())
            {
                why = "the proposal names elements the finding never did. A correction may narrow, never widen.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Whether a proposal is still current. Time is COMPARED, never read: this
        /// file has no clock, so a test can place the boundary exactly.
        /// </summary>
        public static bool IsExpired(CorrectionProposal p, string nowUtc)
        {
            if (p == null || string.IsNullOrEmpty(p.ExpiresUtc) || string.IsNullOrEmpty(nowUtc)) return false;
            return string.CompareOrdinal(nowUtc, p.ExpiresUtc) > 0;
        }

        public static JObject Tally(IEnumerable<CorrectionProposal> proposals)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string s in ProposalState.All) counts[s] = 0;
            foreach (CorrectionProposal p in proposals ?? Enumerable.Empty<CorrectionProposal>())
                if (p != null && p.State != null && counts.ContainsKey(p.State)) counts[p.State]++;

            var o = new JObject();
            foreach (string s in ProposalState.All) o[s] = counts[s];
            o["read_only_means"] = ReadOnlyMeans;
            o["registry_means"] = RegistryMeans;
            return o;
        }

        public static JObject ToJson(CorrectionProposal p)
        {
            if (p == null) return null;
            return new JObject
            {
                ["proposal_id"] = p.ProposalId,
                ["finding_id"] = p.FindingId,
                ["finding_type"] = p.FindingType,
                ["target_document"] = p.TargetDocument,
                ["document_fingerprint"] = p.DocumentFingerprint,
                ["element_ids"] = new JArray((p.ElementIds ?? new List<long>()).Select(x => (JToken)x)),
                ["proposed_tool"] = p.Tool,
                ["arguments"] = p.Arguments,
                ["preconditions"] = new JArray(p.Preconditions.Select(x => (JToken)x)),
                ["expected_outcome"] = p.ExpectedOutcome,
                ["risk"] = p.Risk,
                ["reversible"] = p.Reversible,
                ["ambiguities"] = new JArray(p.Ambiguities.Select(x => (JToken)x)),
                ["dry_run_supported"] = p.DryRunSupported,
                ["confirmation_required"] = p.ConfirmationRequired,
                ["verification"] = p.Verification,
                ["cannot_automate_because"] = p.CannotAutomateBecause,
                ["state"] = p.State,
                ["refusal_code"] = p.RefusalCode,
                ["why"] = p.Why,
                ["issued_utc"] = p.IssuedUtc,
                ["expires_utc"] = p.ExpiresUtc
            };
        }
    }
}
