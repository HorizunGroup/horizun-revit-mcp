// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The shape every recipe-backed tool has, written once.
//
// These tools began as pyRevit buttons. A button is allowed to be casual about
// three things because a person is watching it: WHICH document it edits (the one
// on screen), WHETHER the user meant it (a dialog), and WHETHER it worked (you
// look). None of the three survives being called by an agent, so each becomes
// explicit here, and identically for every tool:
//
//   * target_document, through DocumentGate.ForMutation — a mutation names its
//     model, because "the active document" is whatever window was in front when
//     the call arrived;
//   * dry_run, defaulting to TRUE, and a single-use confirmation_token bound to
//     this document AND this plan before anything is written;
//   * a verification block per counted quantity, built by Guard.Verify from the
//     count the recipe claimed and the count the model reports after the commit.
//
// A subclass supplies four things: the tool name, the description the caller
// reads, the recipe that carries the geometry, and which counts must agree. It
// supplies no control flow, so no tool can quietly skip a step.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>
    /// One claim a recipe makes and the model's answer to it. `Intended` is what
    /// apply() said it did — evidence of nothing. `Actual` is what verify() read back
    /// after the commit. They are named separately so the reply can show both.
    /// </summary>
    public sealed class VerifiedCount
    {
        public readonly string What;
        public readonly string IntendedKey;
        public readonly string ActualKey;

        public VerifiedCount(string what, string intendedKey, string actualKey)
        {
            What = what; IntendedKey = intendedKey; ActualKey = actualKey;
        }
    }

    public abstract class RecipeCommand : ICommand
    {
        public abstract string Name { get; }
        public abstract string Description { get; }

        /// <summary>The .py beside the assembly that carries the geometry.</summary>
        protected abstract string RecipeName { get; }

        /// <summary>What the undo stack will call this. Shown to the user in Revit.</summary>
        protected abstract string TransactionName { get; }

        /// <summary>Which counts must agree for this tool to report success.</summary>
        protected abstract VerifiedCount[] Verifications { get; }

        /// <summary>
        /// The request fields that change WHAT IS AFFECTED. A confirmation token is bound
        /// to these; a field that only changes presentation must not be here, or callers
        /// learn to re-run the dry run for nothing.
        /// </summary>
        protected virtual string[] ScopeFields => new[] { "element_ids", "view_id" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            bool dryRun;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                // Default TRUE. These tools delete and recreate geometry; the caller who
                // wanted that says so, and the caller who forgot gets a plan instead of a
                // changed building.
                dryRun = request.Value<bool?>("dry_run") ?? true;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string planHash = DocumentGate.PlanHash(request, ScopeFields);

            // ---- The materialised plan needs the recipe's own resolution, so on apply a
            // read-only planning pass runs FIRST and the token is checked against what it
            // found. Two facts bind, both deliberately coarse:
            //
            //   * WHICH ALGORITHM: the recipe file's SHA-256. It lives on disk and can
            //     change between rehearsal and apply; the caller approved a specific
            //     version of the transformation.
            //   * THE APPROVED ARITHMETIC: the intended counts this command itself
            //     verifies after commit. Those are the numbers the person read. The full
            //     planned JSON is NOT hashed - a recipe is free to describe its plan in
            //     prose, and prose that varies between two honest runs would refuse every
            //     apply. Coarse and stable beats precise and self-refusing.
            RecipeOutcome rehearsedNow = null;
            if (!dryRun)
            {
                try { rehearsedNow = Recipe.Run(doc, RecipeName, request, true, TransactionName); }
                catch (Exception ex)
                {
                    return CommandResult.Fail(Name + ": the pre-apply planning pass failed, so what was approved " +
                        "cannot be compared with what would run now. Nothing was committed: " +
                        PythonEngine.FormatException(ex));
                }
                CommandResult refused = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                         RecipePlan(gate, app, rehearsedNow), null);
                if (refused != null) return refused;
            }

            RecipeOutcome outcome;
            try
            {
                outcome = Recipe.Run(doc, RecipeName, request, dryRun, TransactionName);
            }
            catch (RecipeFailedException ex)
            {
                // Revit rolled back and did not throw, or the geometry gave up part-way.
                // Either way nothing was committed — and whatever the recipe managed to say
                // about which element defeated it goes out with the error, not into a log.
                string why = ex.InnerException is SilentRollbackException
                    ? ex.InnerException.Message
                    : Name + " failed and nothing was committed: " +
                      PythonEngine.FormatException(ex.InnerException ?? ex);

                return CommandResult.Fail(string.IsNullOrEmpty(ex.Printed)
                    ? why
                    : why + "\n\nWhat it reported before it stopped:\n" + ex.Printed);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(
                    Name + " failed and nothing was committed: " + PythonEngine.FormatException(ex));
            }

            var result = new JObject
            {
                ["tool"] = Name,
                ["recipe"] = RecipeName,
                // Which version of the algorithm ran, answerable after the fact.
                ["recipe_sha256"] = outcome.RecipeSha256,
                ["dry_run"] = dryRun,
                ["planned"] = outcome.Planned,
                // Per-element diagnostics the recipe emitted. Null when it had nothing to
                // say; never dropped, because "8 of your 10 walls, and here is what went
                // wrong with the other two" is the reply, not a footnote to it.
                ["recipe_reported"] = outcome.Printed
            };

            if (dryRun)
            {
                result["applied"] = JValue.CreateNull();
                result["verified"] = JValue.CreateNull();
                result["note"] =
                    "DRY RUN: no transaction was opened and NOTHING was written. 'planned' is what would happen. " +
                    "Call again with dry_run=false and the confirmation_token to apply it.";
                // A recipe dry run either planned or it threw; there is no half-planned
                // recipe to declare, so a clean rehearsal is the honest verdict.
                ApplicationOutcome.StampRehearsal(result, Verifications.Length, 0, 0, 0);
                DocumentGate.RecordResolvedPlan(RecipePlan(gate, app, outcome));
                DocumentGate.StampConfirmation(result, gate, Name, planHash, true,
                    "the token binds the recipe BY CONTENT (its SHA-256) and the intended counts of this plan - a " +
                    "recipe file that changed, or a model whose plan now touches different numbers of elements, " +
                    "refuses as stale. It does not bind per-element identity: the recipe re-resolves its own " +
                    "targets at apply, and says what it found.");
                return CommandResult.Ok(result);
            }

            result["applied"] = outcome.Applied;
            result["verified"] = outcome.Verified;

            var blocks = new JArray();
            bool allAgree = true;
            foreach (VerifiedCount check in Verifications)
            {
                int intended = ReadCount(outcome.Applied, check.IntendedKey);
                int actual = ReadCount(outcome.Verified, check.ActualKey);
                JObject block = JObject.FromObject(Guard.Verify(check.What, intended, actual));
                if (block.Value<bool?>("verified") != true) allAgree = false;
                blocks.Add(block);
            }

            result["verification"] = blocks;
            result["all_verified"] = allAgree;
            result["verification_note"] = allAgree
                ? "Every count below was RE-READ from the model after the commit, not counted from calls that " +
                  "did not throw."
                : "AT LEAST ONE COUNT DOES NOT MATCH. The model does not contain what this run claims to have " +
                  "done. Do not treat this as finished — look at the mismatched block below and at 'errors' in " +
                  "'applied' before running anything that builds on this.";

            // The recipe's own verdict, in the vocabulary a plan reads. Every count above
            // was re-read from the model after the commit; a block that does not agree means
            // the model does not contain what this run claims, and `all_verified` already
            // says so in prose. This is the same fact where a composing caller can act on it.
            // The transaction committed - a silent rollback is caught above and fails - so
            // the open question is only whether the counts agree.
            int checks = blocks.Count;
            int agreed = blocks.Count(b => (b as JObject)?.Value<bool?>("verified") == true);
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, checks, agreed, agreed,
                                            0, checks - agreed, 0);
            DocumentGate.StampConfirmation(result, gate, Name, planHash, false);
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// A count out of a recipe's reply. An ABSENT key is not zero: it means the recipe
        /// never reported that quantity, and reporting it as 0 would manufacture agreement
        /// with another 0. It comes back as -1 so Guard.Verify sees a mismatch and says so.
        /// </summary>
        /// <summary>
        /// The recipe's plan, coarse on purpose: algorithm identity plus the intended
        /// counts the person approved - exactly the ones Verifications re-reads after
        /// commit, so the approved numbers and the verified numbers are the same numbers.
        /// </summary>
        private Core.ResolvedPlan RecipePlan(Core.GateResult gate, UIApplication app, RecipeOutcome outcome)
        {
            string counts = "";
            foreach (VerifiedCount check in Verifications)
                counts += check.IntendedKey + "=" + ReadCount(outcome.Planned, check.IntendedKey) + ";";
            string version; try { version = app?.Application?.VersionNumber; } catch { version = null; }
            return new Core.ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = version,
                DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ContextFingerprint = "recipe_sha=" + (outcome.RecipeSha256 ?? "<none>") + ";" + counts
            };
        }

        private static int ReadCount(JToken block, string key)
        {
            JToken value = block?[key];
            if (value == null || value.Type == JTokenType.Null) return -1;
            try { return value.Value<int>(); } catch { return -1; }
        }

        /// <summary>
        /// The arguments every recipe-backed tool takes, so twelve schemas cannot drift
        /// into twelve dialects of the same three ideas. A tool adds its own on top.
        /// </summary>
        public static JObject CommonSchema(string elementDescription)
        {
            return new JObject
            {
                ["target_document"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "REQUIRED. The title or full path of the model to change, matched against the document " +
                        "ACTIVE in Revit right now. This never switches documents for you: with two models open, " +
                        "a write aimed at 'the active document' is a write aimed at whatever turned up."
                },
                ["element_ids"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "integer" },
                    ["description"] =
                        "Exactly these elements — " + elementDescription + ". An id that does not exist comes back " +
                        "in scope.missing_ids and one of the wrong kind in scope.wrong_type_ids; neither is " +
                        "silently dropped. Omit to use view_id, or omit both for the whole model."
                },
                ["view_id"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] =
                        "Everything eligible VISIBLE IN THIS VIEW. Used only when element_ids is omitted. Omit " +
                        "both and the whole model is in scope, which on a large model is rarely what you meant."
                },
                ["dry_run"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns the plan " +
                        "and a single-use confirmation_token. Pass false WITH that token to apply it."
                },
                ["confirmation_token"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "The token from the dry run. Bound to this document and this exact scope, single use, and " +
                        "it expires — if either changed since the dry run it is refused and nothing is written."
                }
            };
        }
    }
}
