// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE CORRECTION CYCLE'S DECISIONS: diagnose -> select -> rehearse -> approve ->
// apply -> re-audit. Everything here that can be decided without a Revit IS
// decided without one, so the cases that matter are proved at a desk:
//
//   * WHICH findings an action names, and that the ids it narrows to were the
//     finding's own. A correction may narrow and never widen; a caller who
//     names an element the audit never listed is refused by name.
//   * WHAT is skipped. The findings the caller did NOT select are listed, so
//     a reply that ran two corrections cannot be mistaken for one that ran
//     them all. An empty selection is refused rather than read as "everything".
//   * WHICH inputs are missing, per action, while the rest still rehearse.
//   * WHETHER the model still shows the findings that were approved: the
//     cited checks are re-run and their ids compared, before anything writes.
//   * WHAT the re-audit says afterwards, per finding and per element:
//     corrected, persistent, failed, or not verifiable - never a count of the
//     calls that were made.
//
// The half that needs a Document - running the checks and the typed commands -
// lives in ApplyCorrectionsCommand and is held to this file's shapes by source
// guards.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The per-action states a reply can carry. One vocabulary, both phases.</summary>
    public static class CorrectionActionState
    {
        /// <summary>Rehearsed cleanly by the typed tool; executable under the token.</summary>
        public const string Rehearsed = "rehearsed";
        public const string RequiresInput = ProposalState.RequiresInput;
        public const string Unsupported = ProposalState.Unsupported;
        public const string Unsafe = ProposalState.Unsafe;
        public const string AlreadyResolved = ProposalState.AlreadyResolved;
        public const string UnknownFinding = "unknown_finding";
        public const string NotPermitted = "not_permitted";
        public const string RehearsalFailed = "rehearsal_failed";
        public const string Applied = "applied";
        public const string Failed = "failed";

        /// <summary>
        /// A step's postcondition could not be READ. The write may have happened.
        /// Reporting it as failed would claim knowledge nobody has; reporting it as
        /// applied would be worse. Its elements come back not_verifiable from the
        /// re-audit - never corrected, never failed.
        /// </summary>
        public const string Uncertain = "uncertain";
        public const string Skipped = "skipped";
    }

    /// <summary>What the re-audit said about one finding after the apply.</summary>
    public static class ReAuditOutcome
    {
        public const string Corrected = "corrected";
        public const string Persistent = "persistent";
        public const string Failed = "failed";
        public const string NotVerifiable = "not_verifiable";
    }

    public static class CorrectionRequestRules
    {
        public static readonly string[] KnownKeys =
        {
            "target_document", "finding_set_fingerprint", "actions", "dry_run", "confirmation_token",
            "idempotency_key"
        };

        public static readonly string[] KnownActionKeys = { "finding_id", "element_ids", "inputs" };

        public const int MaxActions = 100;

        /// <summary>
        /// The shape, before anything is looked up. Unknown keys are refused rather
        /// than ignored - the audit's own rule - and an empty actions array is
        /// refused rather than read as "correct everything", because a caller who
        /// sends [] has selected nothing, not all.
        /// </summary>
        public static ScanRequestVerdict Check(JObject request)
        {
            if (request == null) return ScanRequestVerdict.Refused(ScanRequestCodes.UnknownKey, "no request.");
            ScanRequestVerdict keys = ScanRequestRules.CheckUnknownKeys(request, KnownKeys, "horizun_apply_corrections");
            if (!keys.Ok) return keys;

            JToken fp = request["finding_set_fingerprint"];
            if (fp == null || fp.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)fp))
                return ScanRequestVerdict.Refused("missing_finding_set_fingerprint",
                    "finding_set_fingerprint is required: it names the audit run whose findings these actions " +
                    "cite. horizun_audit_model publishes it beside its findings.");

            JToken actions = request["actions"];
            if (actions == null || actions.Type != JTokenType.Array)
                return ScanRequestVerdict.Refused("invalid_actions",
                    "actions must be an array of {finding_id, element_ids?, inputs?}.");
            var arr = (JArray)actions;
            if (arr.Count == 0)
                return ScanRequestVerdict.Refused("empty_actions",
                    "actions is empty. An empty selection is NOT 'every finding': a correction runs only for " +
                    "the findings you name, so name them. The audit's issue findings are listed under " +
                    "'skipped' when you select some and not others.");
            if (arr.Count > MaxActions)
                return ScanRequestVerdict.Refused("invalid_actions",
                    "actions has " + arr.Count + " entries and the ceiling is " + MaxActions + ".");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < arr.Count; i++)
            {
                var a = arr[i] as JObject;
                if (a == null)
                    return ScanRequestVerdict.Refused("invalid_actions", "actions[" + i + "] is not an object.");
                ScanRequestVerdict actionKeys = ScanRequestRules.CheckUnknownKeys(a, KnownActionKeys, "actions[" + i + "]");
                if (!actionKeys.Ok) return actionKeys;

                JToken id = a["finding_id"];
                if (id == null || id.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)id))
                    return ScanRequestVerdict.Refused("invalid_actions",
                        "actions[" + i + "].finding_id is required and must be a finding id from the audit.");
                if (!seen.Add((string)id))
                    return ScanRequestVerdict.Refused("invalid_actions",
                        "actions[" + i + "] names finding '" + (string)id + "' a second time. One action per finding.");

                JToken ids = a["element_ids"];
                if (ids != null && ids.Type != JTokenType.Null)
                {
                    if (ids.Type != JTokenType.Array)
                        return ScanRequestVerdict.Refused("invalid_actions",
                            "actions[" + i + "].element_ids must be an array of integer element ids.");
                    if (((JArray)ids).Count == 0)
                        return ScanRequestVerdict.Refused("invalid_actions",
                            "actions[" + i + "].element_ids is empty. Omit it to act on every element the " +
                            "finding names; an empty list selects nothing and is refused rather than widened.");
                    foreach (JToken t in (JArray)ids)
                        if (t.Type != JTokenType.Integer)
                            return ScanRequestVerdict.Refused("invalid_actions",
                                "actions[" + i + "].element_ids contains '" + t + "', which is not an integer id.");
                }

                JToken inputs = a["inputs"];
                if (inputs != null && inputs.Type != JTokenType.Null && inputs.Type != JTokenType.Object)
                    return ScanRequestVerdict.Refused("invalid_actions",
                        "actions[" + i + "].inputs must be an object of named inputs, e.g. {\"template_view_id\": 123}.");
            }
            return ScanRequestVerdict.Fine();
        }
    }

    /// <summary>One typed call a correction expands to. A per-element recipe yields several.</summary>
    public sealed class CorrectionStep
    {
        public int ActionIndex;
        public string ProposalId;
        public string FindingId;
        public string Check;
        public string Tool;
        public List<long> ElementIds = new List<long>();
        public JObject Arguments;
        public CorrectionProposal Proposal;

        // Filled by the Revit half.
        public bool? RehearsalOk;
        public string RehearsalState;
        public string RehearsalError;
        public string ChildPlanFingerprint;
        public JToken RehearsalData;

        public bool? ApplyOk;
        public string ApplyState;
        public string ApplyError;
        public JToken ApplyData;
    }

    public sealed class CorrectionAction
    {
        public int Index;
        public string FindingId;
        public string Check;
        public string Tool;
        public string State;
        public string RefusalCode;
        public string Why;
        public string Risk;
        public bool Reversible;
        public string ExpectedOutcome;
        public string Verification;
        public List<string> Caveats = new List<string>();
        public List<string> RequiredInputs = new List<string>();
        public List<long> SelectedElementIds = new List<long>();
        /// <summary>Elements the finding listed that the recipe's typed filter excludes.</summary>
        public List<long> ExcludedElementIds = new List<long>();
        public List<CorrectionStep> Steps = new List<CorrectionStep>();

        public bool Actionable { get { return State == CorrectionActionState.Rehearsed; } }
    }

    public static class CorrectionSelection
    {
        public const string NarrowNeverWiden =
            "a correction may narrow to some of the elements a finding named and never widen past them.";

        /// <summary>
        /// The name a destructive recipe asks for. Declared once so the refusal, the
        /// action's required_inputs and the tests cannot spell it three ways.
        /// </summary>
        public const string ElementIdsInput = "element_ids";

        public const string ExplicitSelectionMeans =
            "a destructive correction acts on the ids the caller listed and on nothing else. An action that " +
            "names the finding and omits element_ids used to mean every element the finding listed - the same " +
            "reading of an absent selection that an empty actions array is refused for, with a delete on the " +
            "other side of it.";

        /// <summary>
        /// Resolve every action against the recorded audit. Nothing here runs a tool:
        /// the steps come back with their arguments assembled, in the state
        /// `rehearsed` MEANING 'ready to be rehearsed', and the Revit half either
        /// confirms that by running the child's dry run or downgrades it.
        /// </summary>
        public static List<CorrectionAction> Select(FindingSetRecord record, JArray actions,
                                                    IReadOnlyDictionary<string, CorrectionRecipe> registry)
        {
            var result = new List<CorrectionAction>();
            if (record == null || actions == null) return result;

            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i] as JObject;
                var act = new CorrectionAction { Index = i, FindingId = (string)a?["finding_id"] };
                result.Add(act);

                RecordedFinding rf = record.Find(act.FindingId);
                if (rf == null)
                {
                    act.State = CorrectionActionState.UnknownFinding;
                    act.RefusalCode = ProposalRefusal.UnknownFinding;
                    act.Why = "no finding '" + act.FindingId + "' exists in audit " + record.Fingerprint +
                              ". Finding ids belong to one audit run at one top: " + FindingIdentity.TopMeans;
                    continue;
                }
                act.Check = rf.Check;

                if (!rf.IsIssue)
                {
                    act.State = CorrectionActionState.AlreadyResolved;
                    act.Why = "the audit reported no issue for '" + rf.Check + "'; there is nothing to correct.";
                    continue;
                }

                CorrectionRecipe recipe = null;
                if (registry != null && !registry.TryGetValue(rf.Check ?? "", out recipe)) recipe = null;

                // WHAT THIS RECIPE ASKS FOR, NAMED BEFORE ANYTHING CAN REFUSE.
                //
                // This used to be filled in on the LAST line of the actionable path, so
                // required_inputs came back empty on exactly the row where a client reads
                // it: the action whose state is requires_input. The prose named the input
                // and the machine-readable field did not, which is the wrong way round -
                // a client branching on data could not tell "which template" from any
                // other refusal.
                if (recipe != null)
                {
                    act.RequiredInputs = new List<string>(recipe.RequiredArguments);
                    if (recipe.RequiresExplicitSelection && !act.RequiredInputs.Contains(ElementIdsInput))
                        act.RequiredInputs.Add(ElementIdsInput);
                }

                // THE TYPED FILTER, applied before the caller's narrowing so an id the
                // filter excludes is refused as outside the correction rather than as
                // unknown to the finding.
                List<long> eligible = rf.ElementIds;
                if (recipe != null && recipe.ItemFilterField != null)
                {
                    eligible = FindingIdentity.ElementIdsOf(
                        FindingIdentity.ItemsWhere(rf.Items, recipe.ItemFilterField, recipe.ItemFilterValues));
                    act.ExcludedElementIds = rf.ElementIds.Where(x => !eligible.Contains(x)).ToList();
                }

                List<long> selected;
                var narrowing = a["element_ids"] as JArray;

                // A DELETE IS NEVER 'ALL OF THEM BY DEFAULT'. The registry's own
                // destructive_means says the correction is narrowed to the ids the caller
                // listed; without this, omitting them listed every id for the caller and
                // deleted the lot.
                if (recipe != null && recipe.RequiresExplicitSelection && narrowing == null)
                {
                    act.State = CorrectionActionState.RequiresInput;
                    act.RefusalCode = ProposalRefusal.BadArguments;
                    act.Tool = recipe.Tool;
                    act.Risk = recipe.Risk;
                    act.Reversible = recipe.Reversible;
                    act.Why = "'" + recipe.Tool + "' DELETES, so this action must LIST the element_ids it deletes. " +
                              ExplicitSelectionMeans + " '" + rf.Check + "' names " + eligible.Count +
                              " element(s) this correction may act on" +
                              (act.ExcludedElementIds.Count > 0
                                  ? " (" + act.ExcludedElementIds.Count + " more are excluded by its typed filter)"
                                  : "") +
                              "; put the ones you mean in element_ids and rehearse again.";
                    continue;
                }

                if (narrowing != null)
                {
                    selected = new List<long>();
                    foreach (JToken t in narrowing)
                    {
                        long id = t.Value<long>();
                        if (!rf.ElementIds.Contains(id))
                        {
                            act.State = CorrectionActionState.Unsafe;
                            act.RefusalCode = ProposalRefusal.ScopeWidened;
                            act.Why = "element " + id + " is not one finding '" + rf.Check + "' named; " +
                                      NarrowNeverWiden;
                            break;
                        }
                        if (!eligible.Contains(id))
                        {
                            act.State = CorrectionActionState.RequiresInput;
                            act.RefusalCode = ProposalRefusal.BadArguments;
                            act.Why = "element " + id + " is listed by '" + rf.Check + "' but its " +
                                      recipe.ItemFilterField + " is not one this correction acts on (" +
                                      string.Join(", ", recipe.ItemFilterValues) + "). It is excluded, not corrected.";
                            break;
                        }
                        if (!selected.Contains(id)) selected.Add(id);
                    }
                    if (act.State != null) continue;
                }
                else selected = new List<long>(eligible);

                act.SelectedElementIds = selected;
                var inputs = a["inputs"] as JObject;

                // EXPLICIT NARROWING DEFEATS TRUNCATION, and only explicit narrowing does.
                //
                // A cut list makes the scope of "correct this finding" unknown: the
                // elements past the cut were never shown to anybody. It says nothing
                // about the scope of "correct these four ids", which is those four ids -
                // every one of them read off the part of the list that WAS shown, and
                // each one already checked against it above. Without this a caller could
                // not correct a single view on any model with more views without a
                // template than `top`, which is most of them.
                bool scopeUnknown = rf.Truncated && narrowing == null;

                // The proposals, from the registry and nothing else. One per element
                // for a tool that acts on one; one for the list otherwise.
                var findings = new List<Finding>();
                bool perElement = recipe != null && recipe.Tool != null && recipe.ElementArgument != null;
                if (perElement && !scopeUnknown && selected.Count > 0)
                    foreach (long id in selected)
                        findings.Add(new Finding
                        {
                            FindingId = rf.FindingId, FindingType = rf.Check,
                            DocumentTitle = record.DocumentTitle, DocumentFingerprint = record.DocumentFingerprint,
                            ElementIds = new List<long> { id }, Truncated = false
                        });
                else
                    findings.Add(new Finding
                    {
                        FindingId = rf.FindingId, FindingType = rf.Check,
                        DocumentTitle = record.DocumentTitle, DocumentFingerprint = record.DocumentFingerprint,
                        ElementIds = selected, Truncated = scopeUnknown
                    });

                foreach (Finding f in findings)
                {
                    CorrectionProposal p = GuidedCorrectionRules.Propose(f, registry, record.DocumentTitle,
                        record.DocumentFingerprint, null, null, inputs);
                    if (p.State != ProposalState.Actionable)
                    {
                        act.State = p.State;
                        act.RefusalCode = p.RefusalCode;
                        act.Why = p.Why;
                        act.Tool = p.Tool;
                        act.Steps.Clear();
                        break;
                    }
                    act.Steps.Add(new CorrectionStep
                    {
                        ActionIndex = i,
                        ProposalId = f.ElementIds.Count == 1 && perElement
                            ? "prop:" + rf.FindingId + ":" + f.ElementIds[0]
                            : "prop:" + rf.FindingId,
                        FindingId = rf.FindingId,
                        Check = rf.Check,
                        Tool = p.Tool,
                        ElementIds = new List<long>(f.ElementIds),
                        Arguments = p.Arguments,
                        Proposal = p
                    });
                }
                if (act.State != null) continue;

                CorrectionProposal first = act.Steps[0].Proposal;
                act.Tool = first.Tool;
                act.Risk = first.Risk;
                act.Reversible = first.Reversible;
                act.ExpectedOutcome = first.ExpectedOutcome;
                act.Verification = first.Verification;
                act.Caveats = new List<string>(first.Ambiguities);
                act.State = CorrectionActionState.Rehearsed;
            }
            return result;
        }

        /// <summary>The audit's issue findings the caller did not name, by id.</summary>
        public static List<string> Skipped(FindingSetRecord record, JArray actions)
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken t in actions ?? new JArray())
            {
                string id = (string)(t as JObject)?["finding_id"];
                if (!string.IsNullOrEmpty(id)) named.Add(id);
            }
            var skipped = new List<string>();
            foreach (RecordedFinding f in (record?.Findings) ?? new List<RecordedFinding>())
                if (f.IsIssue && !named.Contains(f.FindingId ?? "")) skipped.Add(f.FindingId);
            return skipped;
        }

        /// <summary>
        /// The finding types an action set touches - the checks the apply re-runs
        /// before and after writing.
        /// </summary>
        public static List<string> ChecksOf(IEnumerable<CorrectionAction> actions)
        {
            var checks = new List<string>();
            foreach (CorrectionAction a in actions ?? Enumerable.Empty<CorrectionAction>())
                if (!string.IsNullOrEmpty(a.Check) && !checks.Contains(a.Check)) checks.Add(a.Check);
            return checks;
        }

        /// <summary>
        /// Whether every action rehearsed cleanly - the only state in which a token
        /// is issued. One requires_input action withholds the whole token: the
        /// caller must fix or drop it, because a token over "the ones that worked"
        /// authorises a set the caller never read as such.
        /// </summary>
        public static bool RehearsedCleanly(IEnumerable<CorrectionAction> actions)
        {
            var list = (actions ?? Enumerable.Empty<CorrectionAction>()).ToList();
            if (list.Count == 0) return false;
            foreach (CorrectionAction a in list)
            {
                if (a.State != CorrectionActionState.Rehearsed) return false;
                if (a.Steps.Count == 0) return false;
                foreach (CorrectionStep s in a.Steps)
                    if (s.RehearsalOk != true) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Whether the checks re-run at apply time still produce the finding ids the
    /// caller was approved against. A different id means the model moved: an
    /// element appeared, disappeared, or the list was cut differently.
    /// </summary>
    public static class FindingSetDrift
    {
        public static string Describe(FindingSetRecord record, IEnumerable<string> checks,
                                      IDictionary<string, string> freshIdsByCheck,
                                      ICollection<string> checksThatFailed)
        {
            var parts = new List<string>();
            foreach (string check in checks ?? Enumerable.Empty<string>())
            {
                RecordedFinding was = record?.FindByCheck(check);
                if (checksThatFailed != null && checksThatFailed.Contains(check))
                {
                    parts.Add("'" + check + "' could not be re-run, so whether its finding still stands is unknown");
                    continue;
                }
                string now;
                if (freshIdsByCheck == null || !freshIdsByCheck.TryGetValue(check, out now))
                {
                    parts.Add("'" + check + "' produced no finding on re-run");
                    continue;
                }
                if (was == null || !string.Equals(was.FindingId, now, StringComparison.Ordinal))
                    parts.Add("'" + check + "' now reads " + now + " (approved: " + (was?.FindingId ?? "none") + ")");
            }
            return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
        }
    }

    public static class ReAuditRules
    {
        /// <summary>
        /// The fresh finding's items, by the element id each one names. Needed
        /// because an inventory check is judged on the ITEM, not on whether an id
        /// appears in a list of ids.
        /// </summary>
        private static Dictionary<long, JObject> ItemsById(JObject finding)
        {
            var byId = new Dictionary<long, JObject>();
            var items = finding == null ? null : finding["items"] as JArray;
            foreach (JToken t in items ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) continue;
                long id;
                if (!FindingIdentity.TryElementId(o, out id)) continue;
                if (!byId.ContainsKey(id)) byId[id] = o;
            }
            return byId;
        }

        /// <summary>
        /// Before/after for one action. Decided per ELEMENT from the fresh finding's
        /// items, never from the count of calls made.
        ///
        /// WHAT "corrected" MEANS DEPENDS ON THE RECIPE, and getting that wrong is
        /// how a verified reload was reported as persistent in every Revit year:
        ///
        ///   removed_from_finding  - a DEFECT LIST. The element is corrected when
        ///                           it is gone; still listed is persistent; gone
        ///                           from a truncated list is not verifiable,
        ///                           because it may sit past the cut.
        ///
        ///   item_leaves_the_filter - an INVENTORY. The check lists the element
        ///                           whatever its state, so the element is
        ///                           corrected when its own typed code stops being
        ///                           one of the values the recipe acts on, and its
        ///                           ABSENCE is not verifiable rather than success:
        ///                           an item no longer listed may have been deleted
        ///                           or cut off by top, and neither is the fix.
        ///
        /// The recipe comes from the registry when the caller does not pass one, so
        /// the postcondition is always the one the registry published.
        /// </summary>
        public static JObject Compare(CorrectionAction action, JObject freshFinding, bool checkFailed,
                                      CorrectionRecipe recipe = null)
        {
            var corrected = new List<long>();
            var persistent = new List<long>();
            var failed = new List<long>();
            var notVerifiable = new List<long>();

            if (recipe == null && action != null && action.Check != null)
            {
                CorrectionRecipe fromRegistry;
                if (CorrectionRegistry.Default.TryGetValue(action.Check, out fromRegistry)) recipe = fromRegistry;
            }
            string postcondition = recipe != null && recipe.Postcondition != null
                ? recipe.Postcondition
                : CorrectionPostcondition.RemovedFromFinding;
            bool byFilter = postcondition == CorrectionPostcondition.ItemLeavesTheFilter &&
                            recipe != null && !string.IsNullOrEmpty(recipe.ItemFilterField);

            bool anyApplied = action.Steps.Any(s => s.ApplyOk == true);
            bool anyInDoubt = action.Steps.Any(s => s.ApplyOk != true && string.Equals(
                s.ApplyState, ApplicationOutcome.Name(ApplicationState.Uncertain), StringComparison.Ordinal));
            var freshItems = ItemsById(freshFinding);
            var freshIds = new HashSet<long>(freshItems.Keys);
            bool freshTruncated = freshFinding != null && freshFinding["truncated"]?.Type == JTokenType.Boolean &&
                                  (bool)freshFinding["truncated"];
            var stateAfter = new JObject();

            foreach (CorrectionStep s in action.Steps)
                foreach (long id in s.ElementIds)
                {
                    if (s.ApplyOk != true)
                    {
                        // A step that could not re-read its own work may still have
                        // written it. `failed` says nothing landed, and nobody knows
                        // that; not_verifiable says exactly what is known.
                        if (string.Equals(s.ApplyState, ApplicationOutcome.Name(ApplicationState.Uncertain),
                                          StringComparison.Ordinal))
                            notVerifiable.Add(id);
                        else
                            failed.Add(id);
                        continue;
                    }
                    if (checkFailed || freshFinding == null) { notVerifiable.Add(id); continue; }

                    if (byFilter)
                    {
                        JObject item;
                        if (!freshItems.TryGetValue(id, out item))
                        {
                            // NOT corrected. This inventory lists the element in every
                            // state it can be in, so no longer listing it says the
                            // element left the model or the list was cut - never that
                            // the correction worked.
                            notVerifiable.Add(id);
                            stateAfter[id.ToString(CultureInfo.InvariantCulture)] = "not_listed";
                            continue;
                        }
                        JToken raw = item[recipe.ItemFilterField];
                        string value = raw == null || raw.Type == JTokenType.Null ? null : raw.ToString();
                        stateAfter[id.ToString(CultureInfo.InvariantCulture)] = value;
                        if (value == null) { notVerifiable.Add(id); continue; }
                        if (recipe.ItemFilterValues != null &&
                            recipe.ItemFilterValues.Contains(value, StringComparer.Ordinal))
                        {
                            persistent.Add(id);
                            continue;
                        }
                        corrected.Add(id);
                        continue;
                    }

                    if (freshIds.Contains(id)) { persistent.Add(id); continue; }
                    if (freshTruncated) { notVerifiable.Add(id); continue; }
                    corrected.Add(id);
                }

            string outcome;
            // An action that applied NOTHING but whose write may have happened is not
            // a failure: it is the one thing this vocabulary has a word for.
            if (!anyApplied && anyInDoubt) outcome = ReAuditOutcome.NotVerifiable;
            else if (!anyApplied) outcome = ReAuditOutcome.Failed;
            else if (persistent.Count > 0) outcome = ReAuditOutcome.Persistent;
            else if (notVerifiable.Count > 0) outcome = ReAuditOutcome.NotVerifiable;
            else if (failed.Count > 0 && corrected.Count == 0) outcome = ReAuditOutcome.Failed;
            else outcome = ReAuditOutcome.Corrected;

            string why;
            switch (outcome)
            {
                case ReAuditOutcome.Corrected:
                    why = byFilter
                        ? "every selected element is still listed by '" + action.Check + "' - it is an " +
                          "inventory - and none of them carries a " + recipe.ItemFilterField +
                          " the recipe acts on any more" +
                          (failed.Count > 0 ? ", except the " + failed.Count + " whose typed call failed" : "") + "."
                        : "every selected element is gone from the re-run finding" +
                          (failed.Count > 0 ? ", except the " + failed.Count + " whose typed call failed" : "") + ".";
                    break;
                case ReAuditOutcome.Persistent:
                    why = byFilter
                        ? persistent.Count + " selected element(s) still carry a " + recipe.ItemFilterField +
                          " the recipe acts on after the apply. The typed call reported what it verified; the " +
                          "audit re-read the item and does not agree, and the audit is the judge."
                        : persistent.Count + " selected element(s) are still listed by '" + action.Check +
                          "' after the apply. The typed call reported what it verified; the audit does not " +
                          "agree that the finding is gone, and the audit is the judge.";
                    break;
                case ReAuditOutcome.NotVerifiable:
                    why = checkFailed
                        ? "'" + action.Check + "' could not be re-run, so nothing is claimed about the result."
                        : byFilter
                            ? "'" + action.Check + "' is an inventory and no longer lists " +
                              notVerifiable.Count + " selected element(s), or lists them without a readable " +
                              recipe.ItemFilterField + ". An element that fell out of an inventory may have been " +
                              "deleted or cut off by top; either way the postcondition could not be read, and an " +
                              "absence is not a success."
                            : "the re-run finding's list was cut at top, so an element missing from it may sit past " +
                              "the cut. Re-run the audit with a larger top to know.";
                    break;
                default:
                    why = "no typed call for this action was applied.";
                    break;
            }

            return new JObject
            {
                ["finding_id"] = action.FindingId,
                ["check"] = action.Check,
                ["outcome"] = outcome,
                ["why"] = why,
                // WHAT WAS CHECKED, beside the verdict. A reader who disagrees with
                // `outcome` can see which postcondition was applied and, when the
                // finding is an inventory, what the item's typed code reads NOW.
                ["postcondition"] = postcondition,
                ["postcondition_means"] = CorrectionPostcondition.Means,
                ["item_state_after"] = byFilter ? (JToken)stateAfter : JValue.CreateNull(),
                ["item_state_field"] = byFilter ? (JToken)recipe.ItemFilterField : JValue.CreateNull(),
                ["before"] = new JObject
                {
                    ["selected"] = action.SelectedElementIds.Count,
                    ["element_ids"] = new JArray(action.SelectedElementIds.Select(x => (JToken)x))
                },
                ["after"] = new JObject
                {
                    ["finding_id"] = freshFinding == null ? null : (string)freshFinding["finding_id"],
                    ["is_issue"] = freshFinding == null ? JValue.CreateNull() : freshFinding["is_issue"],
                    ["count"] = freshFinding == null ? JValue.CreateNull() : freshFinding["count"],
                    ["truncated"] = freshTruncated,
                    ["check_failed"] = checkFailed
                },
                ["elements"] = new JObject
                {
                    ["corrected"] = new JArray(corrected.Select(x => (JToken)x)),
                    ["persistent"] = new JArray(persistent.Select(x => (JToken)x)),
                    ["failed"] = new JArray(failed.Select(x => (JToken)x)),
                    ["not_verifiable"] = new JArray(notVerifiable.Select(x => (JToken)x))
                },
                ["counts"] = new JObject
                {
                    ["corrected"] = corrected.Count,
                    ["persistent"] = persistent.Count,
                    ["failed"] = failed.Count,
                    ["not_verifiable"] = notVerifiable.Count
                }
            };
        }
    }

    public static class CorrectionReply
    {
        public static JObject ActionJson(CorrectionAction a)
        {
            var steps = new JArray();
            foreach (CorrectionStep s in a.Steps)
            {
                var row = new JObject
                {
                    ["proposal_id"] = s.ProposalId,
                    ["tool"] = s.Tool,
                    ["element_ids"] = new JArray(s.ElementIds.Select(x => (JToken)x)),
                    ["arguments"] = s.Arguments
                };
                if (s.RehearsalOk != null)
                {
                    row["rehearsal"] = new JObject
                    {
                        ["ok"] = s.RehearsalOk,
                        ["application_state"] = s.RehearsalState,
                        ["error"] = s.RehearsalError,
                        ["child_plan_fingerprint"] = s.ChildPlanFingerprint,
                        ["data"] = s.RehearsalData
                    };
                }
                if (s.ApplyOk != null)
                {
                    row["apply"] = new JObject
                    {
                        ["ok"] = s.ApplyOk,
                        ["application_state"] = s.ApplyState,
                        ["error"] = s.ApplyError,
                        ["data"] = s.ApplyData
                    };
                }
                steps.Add(row);
            }
            return new JObject
            {
                ["index"] = a.Index,
                ["finding_id"] = a.FindingId,
                ["check"] = a.Check,
                ["state"] = a.State,
                ["refusal_code"] = a.RefusalCode,
                ["why"] = a.Why,
                ["tool"] = a.Tool,
                ["risk"] = a.Risk,
                ["reversible"] = a.Reversible,
                ["expected_outcome"] = a.ExpectedOutcome,
                ["verification"] = a.Verification,
                ["caveats"] = new JArray(a.Caveats.Select(x => (JToken)x)),
                ["required_inputs"] = new JArray(a.RequiredInputs.Select(x => (JToken)x)),
                ["selected_element_ids"] = new JArray(a.SelectedElementIds.Select(x => (JToken)x)),
                ["excluded_by_filter"] = new JArray(a.ExcludedElementIds.Select(x => (JToken)x)),
                ["steps"] = steps
            };
        }

        public static JObject Tally(IEnumerable<CorrectionAction> actions)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CorrectionAction a in actions ?? Enumerable.Empty<CorrectionAction>())
            {
                string s = a.State ?? "unknown";
                counts[s] = counts.ContainsKey(s) ? counts[s] + 1 : 1;
            }
            var o = new JObject();
            foreach (KeyValuePair<string, int> kv in counts.OrderBy(k => k.Key, StringComparer.Ordinal))
                o[kv.Key] = kv.Value;
            return o;
        }
    }
}
