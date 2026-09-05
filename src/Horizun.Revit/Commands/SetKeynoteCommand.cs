// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_set_keynote — replaces apply_keynote_to_element.
//
// Keynotes are how Horizun ties a model to a budget: the keynote code is the
// join key between an element and a line item. A keynote that silently did not
// land is a quantity that silently does not get billed, so this handler is built
// to be unable to claim a write it cannot prove.
//
// Three defects in the handler this replaces, all of which we hit for real:
//
//   1. THE BLAST RADIUS. In Revit the Keynote parameter lives on the TYPE, not
//      the instance — that is the normal case, not the edge case. The old
//      handler walked to the type, wrote there, and reported the ELEMENT id. So
//      "keynote applied to 1 door" quietly re-coded every door of that type in
//      the project. Nothing in the response hinted at it. We now resolve the
//      write target first, write each type ONCE, and report exactly which
//      elements were affected — including the ones the caller never mentioned.
//   2. `Parameter.Set()` returns a bool and the old code discarded it. Set() can
//      decline a write and return false without throwing. Reporting "updated"
//      off a call that returned false is the same class of lie as counting a
//      rolled-back Commit as success.
//   3. Nothing re-read the model afterwards. We now read the value back and
//      compare, so what we report is the model's state, not our intent.
//
// Also: ids that are not integers used to be dropped from the request without a
// word, and `requested` was counted after the drop, so the numbers looked
// consistent while the caller's elements were never touched. They are errors now.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public class SetKeynoteCommand : ICommand
    {
        public string Name => "horizun_set_keynote";

        public string Description =>
            "Set the Keynote code on elements, reporting exactly what it touched. In Revit the Keynote " +
            "parameter normally lives on the TYPE, so writing it re-codes every instance of that type: " +
            "this tool resolves the target first, tells you the blast radius (including elements you did " +
            "not name), writes each type once, and VERIFIES AFTER THE COMMIT: every target is re-resolved from the committed document and its value read fresh, because a value read inside an open transaction can still disappear with it. elements_now_carrying_this_keynote is counted by asking the model again afterwards, never by summing what the plan expected. The counts are kept apart because they answer different questions: requested_ids (every id sent, INCLUDING entries that were not integers), parsed_ids, targets_resolved, writes_accepted_in_transaction (not evidence), writes_verified_after_commit (evidence) and writes_failed. Use scope='instance' to " +
            "refuse any write that would spill onto siblings, or dry_run=true to see the impact first.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // WHICH model. A keynote write lands on a TYPE, so it changes every instance
            // of it - the last command that should be aimed at whatever window is in front.
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            var doc = gate.Document;

            _census = null;   // the model may have changed since the last request

            var idsToken = request["element_ids"] as JArray;
            if (idsToken == null || idsToken.Count == 0)
                return CommandResult.Fail("element_ids is required and must not be empty.");

            var keynote = request.Value<string>("keynote");
            if (keynote == null)
                return CommandResult.Fail("keynote is required (use \"\" to clear it).");

            var scope = (request.Value<string>("scope") ?? "auto").ToLowerInvariant();
            if (scope != "auto" && scope != "instance" && scope != "type")
                return CommandResult.Fail("scope must be 'auto', 'instance' or 'type'.");

            // dry_run defaults to TRUE. A keynote write lands on a TYPE, so it re-codes
            // every instance of it - including ones the caller never named. That blast
            // radius is exactly what a rehearsal exists to show before it happens.
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            // The SCOPE of the request: which elements, which value, which rule. Not
            // anything that only changes what is displayed - a guard that fires on a
            // cosmetic option is one callers learn to work around.
            string planHash = DocumentGate.PlanHash(request, "element_ids", "keynote", "scope");

            var failed = new JArray();
            var ids = new List<long>();
            foreach (var tok in idsToken)
            {
                // An id we cannot read is an error, not something to drop quietly.
                if (tok.Type != JTokenType.Integer)
                {
                    failed.Add(new JObject { ["element_id"] = tok.ToString(), ["error"] = "Not an integer element id; ignored ids are how a caller thinks it coded elements it never touched." });
                    continue;
                }
                ids.Add(tok.Value<long>());
            }

            // ---- Resolve where each write would land, before writing anything. ----
            var plans = new List<WritePlan>();
            foreach (var id in ids)
            {
                if (!Rid.CanRepresentElementId(id))
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = Rid.ElementIdRangeError(id) });
                    continue;
                }
                var elem = doc.GetElement(Rid.ToElementId(id));
                if (elem == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "Element not found." });
                    continue;
                }

                var plan = Resolve(doc, elem, scope, out string why);
                if (plan == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = why });
                    continue;
                }
                plans.Add(plan);
            }

            // THE COUNT OF IDS THAT NEVER BECAME A TARGET, taken HERE - before the write
            // loop appends its own refusals to the same `failed` array. Read afterwards it
            // is a mixed bucket, and deriving anything from it then counted a refused write
            // twice: once as a target inside byTarget, once as a "failure" outside it, which
            // also reported a refused write as an unresolved id. The three failures are
            // different facts and each is counted once, at the place it happens.
            int unresolvedIds = failed.Count;

            // One type written once, no matter how many of its instances were named.
            var byTarget = plans
                .GroupBy(p => p.Target.Id.ToString())
                .Select(g => g.First())
                .ToList();

            // ---- Blast radius: who else changes because they share the type. ----
            var targets = new JArray();
            foreach (var plan in byTarget)
            {
                var requested = plans.Where(p => p.Target.Id == plan.Target.Id).Select(p => p.Source.Id).ToList();
                var affected = plan.IsTypeLevel ? InstancesOfType(doc, plan.Target.Id) : new List<ElementId> { plan.Source.Id };
                var collateral = affected.Where(a => !requested.Contains(a)).ToList();

                plan.Collateral = collateral.Count;

                targets.Add(new JObject
                {
                    ["writes_to"] = plan.IsTypeLevel ? "type" : "instance",
                    ["target_id"] = plan.Target.Id.ToString(),
                    ["target_name"] = SafeName(plan.Target),
                    ["parameter"] = plan.Parameter.Definition?.Name,
                    ["current_keynote"] = plan.Parameter.AsString() ?? "",
                    ["requested_elements"] = new JArray(requested.Select(r => (JToken)r.ToString())),
                    ["elements_affected"] = affected.Count,
                    ["collateral_elements"] = collateral.Count,
                    ["collateral_note"] = collateral.Count == 0
                        ? null
                        : $"{collateral.Count} element(s) you did not name share this type and WILL be re-coded. " +
                          "The Keynote lives on the type; there is no way to code one instance without them. " +
                          "Use scope='instance' to refuse this, or duplicate the type first."
                });
            }

            // ---- The MATERIALISED plan: the TYPES this actually resolved to, and what
            // they say right now. ----
            // planHash binds the REQUEST - the ids, the keynote, the scope. The paragraph
            // this command already prints in its rehearsal admits what that cannot cover:
            // "the token binds the REQUEST, not the set of types this rehearsal resolved."
            // Here that gap closes. Two drifts matter and only a re-read finds either:
            //
            //   * SOMEBODY ELSE RE-CODED THE TYPE between the rehearsal and the apply.
            //     The caller approved replacing "" - or "22.11.31" - and would be
            //     overwriting a colleague's classification instead. The keynote itself is
            //     therefore part of the plan, not just the target's identity.
            //   * A NEW INSTANCE OF THE TYPE APPEARED, so the blast radius the caller
            //     accepted has grown. That rides in ExpectedCascadeCount, which exists for
            //     exactly this: an effect BEYOND the elements listed.
            //
            // Built identically on both paths, so the rehearsal's fingerprint travels in
            // the token and the apply recomputes it.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ExpectedCascadeCount = byTarget.Sum(t => t.Collateral)
            };
            foreach (WritePlan t in byTarget)
            {
                resolvedPlan.Elements.Add(new PlannedElement
                {
                    UniqueId = SafePlanUniqueId(t.Target),
                    Category = t.Target?.Category == null ? null : t.Target.Category.Name,
                    TypeName = SafeName(t.Target),
                    Action = PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string>
                    {
                        // The value being replaced. This is the whole point.
                        { "keynote", SafePlanCurrent(t) },
                        { "parameter", t.Parameter?.Definition == null ? "" : t.Parameter.Definition.Name },
                        // instance vs type is a different write with a different blast
                        // radius, so a plan that resolved one must not satisfy the other.
                        { "writes_to", t.IsTypeLevel ? "type" : "instance" }
                    }
                });
            }

            if (dryRun)
            {
                var dryResult = new JObject
                {
                    ["dry_run"] = true,
                    ["keynote"] = keynote,
                    // Every id sent, including the ones that were not integers - counting
                    // only the parsed ones under-reports what the caller asked for.
                    ["requested_ids"] = idsToken.Count,
                    ["parsed_ids"] = ids.Count,
                    ["writes_planned"] = byTarget.Count,
                    ["targets"] = targets,
                    ["failed"] = failed,
                    ["total_elements_affected"] = byTarget.Sum(p => p.IsTypeLevel ? InstancesOfType(doc, p.Target.Id).Count : 1),
                    ["note"] = "Nothing was written. Re-run with dry_run=false and the confirmation_token below."
                };
                // Ids that never resolved make this a partial rehearsal: the apply it
                // authorises would be over fewer targets than the caller asked about. At
                // this point nothing has been written, so `failed` holds resolution
                // failures only and unresolvedIds is the same number - it is used anyway,
                // so that moving this block later cannot silently change what it counts.
                ApplicationOutcome.StampRehearsal(dryResult, byTarget.Count + unresolvedIds, unresolvedIds, 0, 0);
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(dryResult, gate, Name, planHash, true,
                    "the token binds the TYPES this rehearsal resolved and the keynote each one carries right now, " +
                    "plus how many elements you did not name would be re-coded. If somebody re-codes one of these " +
                    "types, or the model gains an instance of one, the apply is refused as a stale plan rather than " +
                    "overwriting work you never saw.");
                return CommandResult.Ok(dryResult);
            }

            // The rehearsed PLAN does not travel in the token, only its fingerprint, so a
            // stale refusal names the drift generically. Still refused, still nothing
            // written - and the caller re-runs the rehearsal to see what moved.
            CommandResult refused = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                    resolvedPlan, null);
            if (refused != null) return refused;

            // ---- Write. ----
            var written = new JArray();
            int confirmed = 0;
            // Targets whose Parameter.Set threw or was refused: a PRE-COMMIT diagnostic. It
            // answers a different question from verifyFailed - this one says the write never
            // landed, that one says the committed model does not carry the value - and the
            // two OVERLAP rather than partition. A refused write is normally in both, so
            // only verifyFailed feeds any total; see the declaration at the end.
            int writesRefused = 0;
            using (var tx = new Transaction(doc, "Horizun: set keynote"))
            {
                tx.Start();
                try
                {
                    foreach (var plan in byTarget)
                    {
                        var before = plan.Parameter.AsString() ?? "";
                        bool accepted;
                        try { accepted = plan.Parameter.Set(keynote); }
                        catch (Exception ex)
                        {
                            writesRefused++;
                            failed.Add(new JObject { ["target_id"] = plan.Target.Id.ToString(), ["error"] = ex.Message });
                            continue;
                        }

                        // Set() returning false is a refused write that does not throw.
                        if (!accepted)
                        {
                            writesRefused++;
                            failed.Add(new JObject
                            {
                                ["target_id"] = plan.Target.Id.ToString(),
                                ["error"] = "Revit refused the write (Parameter.Set returned false). Nothing changed on this target."
                            });
                            continue;
                        }

                        // Read it back INSIDE the transaction. This is not verification: an
                        // uncommitted value is a value that can still vanish, and this read
                        // used to be the only one - so a silent rollback left every row
                        // saying confirmed:true about a model that never changed. It stays
                        // because it catches a refused write immediately; the verdict comes
                        // from the post-commit pass below.
                        var afterInTx = plan.Parameter.AsString() ?? "";
                        bool acceptedInTx = string.Equals(afterInTx, keynote, StringComparison.Ordinal);
                        if (acceptedInTx) confirmed++;

                        written.Add(new JObject
                        {
                            ["writes_to"] = plan.IsTypeLevel ? "type" : "instance",
                            ["target_id"] = plan.Target.Id.ToString(),
                            ["target_name"] = SafeName(plan.Target),
                            ["before"] = before,
                            ["after_in_transaction"] = afterInTx,
                            ["accepted_in_transaction"] = acceptedInTx,
                            ["collateral_elements"] = plan.Collateral,
                            ["error"] = acceptedInTx ? null : $"Set() was accepted but the model reads back '{afterInTx}', not '{keynote}'."
                        });
                    }

                    // Turns a silent rollback into an error instead of a false success.
                    Guard.Commit(tx, "set keynote");
                }
                catch (SilentRollbackException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) Guard.RollBack(tx);
                    return CommandResult.Fail("Set keynote failed, nothing written: " + ex.Message);
                }
            }

            // ---- Verification, AFTER the commit. -------------------------------------
            // The transaction is closed. Every target is resolved again from the document
            // and its parameter read fresh, because the read above happened inside a
            // transaction that had not been committed yet - and a value that has not been
            // committed is a value that can still disappear.
            int verified = 0, verifyFailed = 0;
            int elementsCarrying = 0;
            bool carryingCountComplete = true;

            foreach (var plan in byTarget)
            {
                string readBack = null, error = null;
                Element fresh = null;
                try { fresh = doc.GetElement(plan.Target.Id); }
                catch (Exception ex) { error = "the target could not be re-resolved after the commit: " + ex.Message; }

                if (error == null && fresh == null)
                    error = "the target no longer exists in the committed document.";

                if (error == null)
                {
                    try
                    {
                        var p = fresh.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? fresh.LookupParameter("Keynote");
                        if (p == null) error = "the Keynote parameter is not present on the committed element.";
                        else readBack = p.AsString() ?? "";
                    }
                    catch (Exception ex) { error = "the committed value could not be read: " + ex.Message; }
                }

                bool ok = error == null && string.Equals(readBack, keynote, StringComparison.Ordinal);
                if (ok) verified++; else verifyFailed++;

                // The count of elements now carrying it comes from ASKING the model again,
                // not from summing the pre-commit affected counts. Those were taken before
                // anything was written and would report a number for a write that failed.
                int carrying = 0;
                if (ok)
                {
                    try
                    {
                        carrying = plan.IsTypeLevel ? InstancesOfType(doc, plan.Target.Id).Count : 1;
                        elementsCarrying += carrying;
                    }
                    catch { carryingCountComplete = false; }
                }

                var row = written.FirstOrDefault(w => (string)w["target_id"] == plan.Target.Id.ToString()) as JObject;
                if (row != null)
                {
                    row["verified_after_commit"] = ok;
                    row["value_after_commit"] = readBack;
                    row["elements_carrying_after_commit"] = ok ? (JToken)carrying : JValue.CreateNull();
                    if (error != null) row["verification_error"] = error;
                    if (!ok && error == null)
                        row["verification_error"] = "the committed document reads '" + readBack + "', not '" + keynote + "'.";
                }
            }

            var result = new JObject
            {
                ["dry_run"] = false,
                ["keynote"] = keynote,

                // Six distinct numbers, because they answer six different questions and
                // collapsing them is how a caller believes it coded elements it never
                // touched. requested counts EVERY id the caller sent, including the ones
                // that were not integers - it used to count only the ones that parsed, so
                // a request with bad entries under-reported what had been asked for.
                ["requested_ids"] = idsToken.Count,
                ["parsed_ids"] = ids.Count,
                ["targets_resolved"] = byTarget.Count,
                ["writes_accepted_in_transaction"] = confirmed,
                ["writes_verified_after_commit"] = verified,
                // DISTINCT WRITE TARGETS THE COMMITTED MODEL DOES NOT CARRY. That is what a
                // failed write is, and it is measured by the post-commit read - the only
                // pass that sees every target once.
                //
                // It used to be verifyFailed + failed.Count, which double-counted: `failed`
                // is one array appended to from three places, so a refused write was in it
                // AND failed its read-back. It also added ids that never became targets,
                // which are not failed writes at all. The old value is kept for one
                // deprecation window under its own name rather than under this one.
                ["writes_failed"] = verifyFailed,
                ["writes_failed_legacy"] = verifyFailed + failed.Count,
                ["writes_failed_legacy_note"] =
                    "DEPRECATED, and wrong: it double-counts a refused write (present in the detailed 'failed' " +
                    "array AND unverified after the commit) and adds unresolved ids, which never became write " +
                    "targets. Read writes_failed, ids_unresolved and writes_refused_in_transaction instead. This " +
                    "field exists only so a consumer pinned to the old number sees it change deliberately.",

                // The three numbers, apart - and they are NOT disjoint, which is exactly why
                // only one of them may be totalled:
                //
                //   ids_unresolved                 ids that never became a target. Disjoint
                //                                  from the other two by construction: no
                //                                  target exists to write to or read back.
                //   writes_refused_in_transaction  a PRE-COMMIT diagnostic. Revit refused
                //                                  Parameter.Set, so the old value stayed.
                //                                  These targets normally reappear in
                //                                  targets_unverified_after_commit - the
                //                                  read-back is what proves it - so the two
                //                                  overlap and must never be summed. (One
                //                                  exception, stated because it is real: a
                //                                  target that ALREADY carried the requested
                //                                  keynote verifies even though its write was
                //                                  refused. The post-commit read is right in
                //                                  that case too, which is why it, and not
                //                                  the refusal count, is what decides.)
                //   targets_unverified_after_commit  the evidence. Every resolved target the
                //                                  committed model does not carry.
                ["ids_unresolved"] = unresolvedIds,
                ["writes_refused_in_transaction"] = writesRefused,
                ["targets_unverified_after_commit"] = verifyFailed,

                ["elements_now_carrying_this_keynote"] = elementsCarrying,
                ["elements_now_carrying_this_keynote_complete"] = carryingCountComplete,
                ["counts_note"] =
                    "requested_ids is every id sent, including entries that were not integers. parsed_ids is how " +
                    "many were readable. targets_resolved is how many distinct types/instances those map to - one " +
                    "type written once no matter how many of its instances were named. " +
                    "writes_accepted_in_transaction is what Revit accepted BEFORE the commit and is NOT evidence; " +
                    "writes_verified_after_commit is the number re-read from the committed document, which is. " +
                    "elements_now_carrying_this_keynote is counted by asking the model again after the commit, not " +
                    "by summing what the plan expected. writes_failed counts DISTINCT write targets the committed " +
                    "model does not carry; ids_unresolved is separate and is not a failed write, because those ids " +
                    "never became targets; writes_refused_in_transaction overlaps writes_failed rather than adding " +
                    "to it. Do not sum the three.",

                // The verdict is over the POST-COMMIT number now. It used to be over the
                // in-transaction one, which cannot distinguish a committed write from one
                // that was rolled back underneath it.
                ["verification"] = JObject.FromObject(Guard.Verify("keynote writes", byTarget.Count, verified)),
                ["written"] = written,
                ["failed"] = failed
            };
            // The transaction committed or Guard.Commit would have thrown above, which is
            // what makes the literal status honest here - it is not assumed, it is the only
            // status that reaches this line.
            //
            // The counts are handed over separately and each thing asked for is counted
            // ONCE: unresolvedIds never became targets, so they add to what was requested;
            // verifyFailed is every resolved target the committed model does not carry, and
            // it already covers the writesRefused ones - a refused write leaves the old
            // value and the read-back is what proves it - so writesRefused is a diagnostic
            // here and is deliberately NOT passed. Any target neither verified nor
            // unverified was never measured, and WriteTally turns that into unknown rather
            // than absorbing it; counts that cannot describe a real batch come back
            // uncertain rather than clamped.
            ApplicationOutcome.Stamp(result, WriteTally.PerTarget(
                ApplicationOutcome.Committed,
                resolvedTargets: byTarget.Count,
                unresolvedIds: unresolvedIds,
                verifiedTargets: verified,
                unverifiedTargets: verifyFailed));
            DocumentGate.StampConfirmation(result, gate, Name, planHash, false);
            return CommandResult.Ok(result);
        }

        private class WritePlan
        {
            public Element Source;      // what the caller named
            public Element Target;      // what actually gets written (may be the type)
            public Parameter Parameter;
            public bool IsTypeLevel;
            public int Collateral;
        }

        /// <summary>
        /// Decide where the write lands and say why if it cannot. Honest about the
        /// instance/type distinction rather than silently walking to the type.
        /// </summary>
        private static WritePlan Resolve(Document doc, Element elem, string scope, out string why)
        {
            why = null;

            // The caller handed us a TYPE id. A type carries its own writable Keynote
            // parameter, so the generic path below would treat it as "the element I was
            // asked to code" and report writes_to:"instance" with a blast radius of 1 -
            // while every instance of the type was about to be re-coded. Measured
            // 2026-07-30: a wall type with 381 instances reported 1 affected and 0
            // collateral through this path, while write_params_verified handed the SAME
            // id reported 381 collateral. The information was always reachable; this
            // path just never looked. A type-level write is what it IS, so say so and
            // count its instances like any other type-level write.
            if (elem is ElementType)
            {
                if (scope == "instance")
                {
                    why = "This id IS a type, and writing a type's Keynote re-codes every instance of it - " +
                          "there is no instance-scoped way to do what this id asks. scope='instance' refuses " +
                          "it by definition. Pass an instance id, or use scope='auto' to accept the type-wide write.";
                    return null;
                }
                var ownP = elem.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? elem.LookupParameter("Keynote");
                if (ownP == null) { why = "This type has no Keynote parameter."; return null; }
                if (!Usable(ownP, out why)) return null;
                return new WritePlan { Source = elem, Target = elem, Parameter = ownP, IsTypeLevel = true };
            }

            Parameter instP = null;
            if (scope != "type")
            {
                instP = elem.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? elem.LookupParameter("Keynote");
                if (instP != null && instP.IsReadOnly) instP = null;
            }

            if (instP != null && Usable(instP, out why))
                return new WritePlan { Source = elem, Target = elem, Parameter = instP, IsTypeLevel = false };

            if (scope == "instance")
            {
                why = "No writable instance-level Keynote on this element. In Revit the Keynote normally " +
                      "lives on the type, and writing it there would re-code every sibling instance. " +
                      "scope='instance' refuses that. Use scope='auto' to accept the type-wide write, or " +
                      "duplicate the type so this element can carry its own code.";
                return null;
            }

            var typeId = elem.GetTypeId();
            if (typeId == ElementId.InvalidElementId)
            {
                why = "Element has no type, and no writable instance-level Keynote parameter.";
                return null;
            }
            var type = doc.GetElement(typeId);
            if (type == null)
            {
                why = "Element's type could not be resolved.";
                return null;
            }

            var typeP = type.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? type.LookupParameter("Keynote");
            if (typeP == null)
            {
                why = "Neither the element nor its type has a Keynote parameter.";
                return null;
            }
            if (!Usable(typeP, out why)) return null;

            return new WritePlan { Source = elem, Target = type, Parameter = typeP, IsTypeLevel = true };
        }

        private static bool Usable(Parameter p, out string why)
        {
            why = null;
            if (p.IsReadOnly) { why = "Keynote parameter is read-only here."; return false; }
            if (p.StorageType != StorageType.String)
            {
                why = "Keynote parameter is " + p.StorageType + ", not a string; this is not the Revit Keynote field.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Every instance of a type — i.e. everyone a type-level write reaches.
        ///
        /// Deliberately NOT FamilyInstanceFilter: that only matches FamilyInstances,
        /// so for a wall/floor/roof type it returns empty WITHOUT throwing, and we
        /// would report a blast radius of zero for exactly the categories where a
        /// type-wide re-code does the most damage. One scan of the model, cached per
        /// request, covers every category and costs less than a filter per target.
        /// </summary>
        // Per-request only. The dispatcher keeps one handler instance alive for the
        // whole session, so a cache that outlived a request would answer the next
        // one from a model that has since changed. Cleared on entry to Execute.
        private Dictionary<ElementId, List<ElementId>> _census;

        /// <summary>Identity for the plan, guarded: measuring must never be what fails.</summary>
        private static string SafePlanUniqueId(Element e)
        {
            try { return e == null ? null : e.UniqueId; } catch { return null; }
        }

        /// <summary>
        /// The keynote as it reads NOW. Distinguishes "" (empty, the normal starting
        /// state) from an unreadable parameter, because collapsing those two would let an
        /// unreadable value drift silently past the comparison.
        /// </summary>
        private static string SafePlanCurrent(WritePlan t)
        {
            try
            {
                if (t == null || t.Parameter == null) return "<unreadable>";
                return t.Parameter.AsString() ?? "";
            }
            catch { return "<unreadable>"; }
        }

        private List<ElementId> InstancesOfType(Document doc, ElementId typeId)
        {
            if (_census == null)
            {
                _census = new Dictionary<ElementId, List<ElementId>>();
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    ElementId tid;
                    try { tid = e.GetTypeId(); } catch { continue; }
                    if (tid == null || tid == ElementId.InvalidElementId) continue;
                    if (!_census.TryGetValue(tid, out var list))
                        _census[tid] = list = new List<ElementId>();
                    list.Add(e.Id);
                }
            }
            return _census.TryGetValue(typeId, out var hits) ? hits : new List<ElementId>();
        }

        private static string SafeName(Element e)
        {
            try { return e?.Name; } catch { return null; }
        }
    }
}
