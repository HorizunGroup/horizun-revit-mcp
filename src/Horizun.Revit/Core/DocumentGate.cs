// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The check every mutating command makes before it touches anything.
//
// "The active document" is not an address. It is whatever window happened to be
// in front when the call arrived, and on a real machine that is decided by a
// second Revit instance, a user clicking, or a file finishing its open. A write
// aimed at "the active document" is a write aimed at whatever turns up.
//
// So a mutation must NAME its document, and this refuses to proceed unless the
// document in front of us is the one named. Refusing is the whole feature: the
// alternative is not an error, it is a correct edit to the wrong building.
//
// It also carries the confirmation store, so a destructive command's dry run can
// issue a token bound to THIS document and THIS plan, and the execution can only
// spend it while both still match. See Confirmation.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class GateResult
    {
        /// <summary>The document to work on. Null when Refusal is set.</summary>
        public Document Document { get; internal set; }

        public DocIdentity Identity { get; internal set; }

        /// <summary>Set when the command must not proceed. Return it unchanged.</summary>
        public CommandResult Refusal { get; internal set; }

        public bool Ok => Refusal == null;

        /// <summary>The key a confirmation token is bound to.</summary>
        public string Fingerprint => Identity?.Fingerprint();
    }

    public static class DocumentGate
    {
        [ThreadStatic]
        private static int _atomicPlanDepth;

        /// <summary>True inside a confirmed execute_plan: a child command carries no token of its own.</summary>
        internal static bool InConfirmedAtomicPlan => _atomicPlanDepth > 0;

        /// <summary>
        /// A confirmed execute_plan owns one outer TransactionGroup. Typed child
        /// commands retain their document and verification checks, but do not demand
        /// separate single-use tokens because the outer token binds the whole graph.
        /// </summary>
        internal static IDisposable EnterConfirmedAtomicPlan()
        {
            _atomicPlanDepth++;
            return new AtomicPlanScope();
        }

        private sealed class AtomicPlanScope : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _atomicPlanDepth = Math.Max(0, _atomicPlanDepth - 1);
            }
        }

        /// <summary>
        /// Confirmations live for the lifetime of this Revit session, in memory. They are
        /// deliberately NOT persisted: a token that survives a restart is a token that
        /// outlived the model state it was issued against.
        /// </summary>
        public static readonly ConfirmationStore Confirmations = new ConfirmationStore();

        public static DocIdentity IdentityOf(Document d, string revitYear)
        {
            if (d == null) return null;
            return new DocIdentity
            {
                Title = Safe(() => d.Title),
                Path = Safe(() => d.PathName),
                IsWorkshared = SafeBool(() => d.IsWorkshared),
                IsDetached = ReadDetached(d),
                IsCloud = ReadCloud(d),
                RevitYear = revitYear,
                ModelGuid = Safe(() =>
                {
                    var p = d.GetCloudModelPath();
                    return p == null ? null : p.GetModelGUID().ToString();
                })
            };
        }

        private static bool? ReadDetached(Document d)
        {
            try { return d.IsDetached; } catch { return null; }
        }
        private static bool? ReadCloud(Document d)
        {
            try { return d.IsModelInCloud; } catch { return null; }
        }

        /// <summary>
        /// Resolve the document a mutating command should act on.
        ///
        /// `target_document` is REQUIRED: a mutation that does not name its target is a
        /// mutation aimed at whatever happens to be in front. It is matched against the
        /// ACTIVE document only - this never switches documents on the caller's behalf,
        /// because silently retargeting is the failure it exists to prevent.
        /// </summary>
        /// <summary>
        /// The READ-side guard: refuse when the caller named a document and the active one
        /// is not it. Returns null when there is nothing to object to.
        ///
        /// OPTIONAL, UNLIKE THE MUTATION GATE, and the asymmetry is deliberate. A mutation
        /// that does not name its target changes whatever happens to be in front, so
        /// naming it is mandatory. A read that does not name one produces a report about
        /// whatever happens to be in front - which is recoverable, because the reply says
        /// which document it read. What must never happen is a caller naming a document,
        /// getting a clean report, and that report being about a different file: a clean
        /// report about a model nobody looked at is worse than no report.
        ///
        /// `target_document` and `target_document_title` are both accepted. The second is
        /// what horizun_model_scan shipped with and callers already send; renaming it
        /// would break working callers to no purpose.
        /// </summary>
        public static CommandResult ReadGuard(Document active, JObject request, string commandName)
        {
            string wanted = ReadDocumentRules.Target(request);
            if (string.IsNullOrWhiteSpace(wanted)) return null;

            string actual = Safe(() => active?.Title);
            if (ReadDocumentRules.Allows(wanted, actual, Safe(() => active?.PathName))) return null;

            var openIdentities = new List<DocIdentity>();
            try { foreach (Document d in active.Application.Documents) if (d != null && !d.IsLinked) openIdentities.Add(IdentityOf(d, null)); }
            catch { openIdentities.Clear(); }
            DocMatch match = DocumentMatcher.FindTarget(openIdentities, wanted);
            if (match.Outcome == DocMatchOutcome.None)
            {
                JObject detail = ReadDocumentRules.MissingTarget(wanted, openIdentities);
                return CommandResult.FailWithDetail((string)detail["message"], detail);
            }
            string message =
                "Refusing to read: '" + commandName + "' was asked for document '" + wanted +
                "' and the ACTIVE document is '" + (actual ?? "(title unreadable)") + "'. Nothing was read. " +
                "Two Revit hosts run side by side on machines like this one, and a clean report about a model " +
                "nobody looked at is worse than no report. Activate the intended document, or check you are " +
                "talking to the right Revit host.";
            return match.Outcome == DocMatchOutcome.Matched
                ? CommandResult.FailWithDetail(message, DocumentActivationSuggestion.Build(openIdentities[match.Index], commandName, request, System.IO.File.Exists(openIdentities[match.Index].Path)))
                : CommandResult.Fail(message);
        }

        /// <summary>
        /// The gate for a script that PROMISES not to change the model (read_only / rehearsal,
        /// always rolled back and verified). Naming a target is optional there: reading the
        /// active document needs no decision about which building to write into. When a target
        /// IS named it is checked exactly as for a write (4.2-N5, CT-P1-5c).
        /// </summary>
        public static GateResult ForReadOnlyScript(UIApplication app, JObject request, string commandName)
        {
            string target = request?.Value<string>("target_document");
            if (string.IsNullOrWhiteSpace(target)) target = request?.Value<string>("expected_document");
            if (string.IsNullOrWhiteSpace(target)) target = request?.Value<string>("target_document_title");
            if (!string.IsNullOrWhiteSpace(target)) return ForMutation(app, request, commandName);

            string revitYear = Safe(() => app?.Application?.VersionNumber);
            Document active = null;
            try { active = app?.ActiveUIDocument?.Document; } catch { active = null; }
            if (active == null)
                return Refuse("No document is active in Revit " + (revitYear ?? "?") + ", so '" + commandName +
                              "' has nothing to read. Open the model you mean first.");
            return new GateResult { Document = active, Identity = IdentityOf(active, revitYear) };
        }

        public static GateResult ForMutation(UIApplication app, JObject request, string commandName)
        {
            GateResult resolved = ResolveDocument(app, request, commandName, true);
            if (!resolved.Ok) return resolved;
            Document active = resolved.Document;
            DocIdentity activeIdentity = resolved.Identity;
            return VerifyIdentity(active, activeIdentity, request, commandName);
        }

        /// <summary>
        /// The identity check, run by the dispatcher BEFORE the command parses its arguments (IdentityPrepass), so that no validator
        /// of any command ever sees the identity fields and every covered tool accepts them. The document is resolved the way a write
        /// resolves it (target_document, when sent, must name the active one). On success the request is normalized in place: the
        /// verified ids are written where the command reads them and the identity fields are gone. Null when all is well.
        /// </summary>
        internal static CommandResult CheckIdentityEarly(UIApplication app, JObject request, string tool) =>
            IdentityPrepassCore.Early(request, tool, () =>
            {
                GateResult resolved = ResolveDocument(app, request, tool, false);
                return resolved.Ok ? Lookups(resolved.Document) : new IdentityLookups { Refusal = resolved.Refusal };
            });

        private static IdentityLookups Lookups(Document active) => new IdentityLookups
        {
            ById = id => Rid.CanRepresentElementId(id) ? IdentityFacts(active, active.GetElement(Rid.Make(id))) : null,
            ByUniqueId = uid => IdentityFacts(active, active.GetElement(uid)),
            // Collaborative = workshared, a local file of a central included: the only place where a synchronize renumbers ids and other people's
            // elements live (R2U). A read that throws counts as collaborative (IdentityPrepassCore.CollaborativeOrUnknown).
            IsCollaborative = () => active.IsWorkshared
        };

        private static GateResult VerifyIdentity(Document active, DocIdentity activeIdentity, JObject request, string commandName)
        {
            // Typed writes that take element ids may state the identity they expect (unique_id, unique_ids, expected_category,
            // expected_type). Check the entire request, nested plan actions included, before any child can start a transaction.
            // A writer that has no ids to check refuses the fields instead of ignoring them (ElementIdentityGuard.RejectUnsupported).
            CommandResult refusal = IdentityPrepassCore.Verify(request, commandName, Lookups(active));
            if (refusal != null) return new GateResult { Refusal = refusal };
            return new GateResult { Document = active, Identity = activeIdentity };
        }

        internal static CommandResult IdentityRefusal(JObject mismatch) => IdentityPrepassCore.Refusal(mismatch);

        private static GateResult ResolveDocument(UIApplication app, JObject request, string commandName, bool requireTarget)
        {
            string revitYear = Safe(() => app?.Application?.VersionNumber);

            Document active = null;
            try { active = app?.ActiveUIDocument?.Document; } catch { active = null; }

            if (active == null)
                return Refuse("No document is active in Revit " + (revitYear ?? "?") + ", so '" + commandName +
                              "' has nothing to act on. Open the model you mean first.");

            // `expected_document` is accepted as an alias: save and relinquish shipped with
            // that name as an OPTIONAL guard, and callers already pass it. Same meaning,
            // now mandatory - renaming it would have broken working callers to no purpose.
            string target = request?.Value<string>("target_document");
            if (string.IsNullOrWhiteSpace(target)) target = request?.Value<string>("expected_document");
            if (string.IsNullOrWhiteSpace(target)) target = request?.Value<string>("target_document_title");
            if (string.IsNullOrWhiteSpace(target) && !requireTarget)
                return new GateResult { Document = active, Identity = IdentityOf(active, revitYear) };
            if (string.IsNullOrWhiteSpace(target))
                return Refuse("'target_document' is required for '" + commandName + "'. A command that CHANGES a " +
                              "model must name the model: 'the active document' is whatever window was in front " +
                              "when this call arrived, and on a machine with two Revit instances open that is not " +
                              "a decision anybody made. The document active right now is '" +
                              Safe(() => active.Title) + "'. Pass that as target_document if it is the one you mean.");

            DocIdentity activeIdentity = IdentityOf(active, revitYear);

            // Compare against every OPEN document, so "it matched the active one" can be
            // told apart from "it matched nothing" and from "it matched several".
            var open = new List<Document>();
            var openIdentities = new List<DocIdentity>();
            try
            {
                foreach (Document d in app.Application.Documents)
                {
                    if (d == null) continue;
                    open.Add(d);
                    openIdentities.Add(IdentityOf(d, revitYear));
                }
            }
            catch { /* the active document below is what actually gates the call */ }

            DocMatch match = DocumentMatcher.FindTarget(openIdentities, target);

            if (match.Outcome == DocMatchOutcome.None)
            {
                JObject detail = ReadDocumentRules.MissingTarget(target, openIdentities, isWrite: true);
                return new GateResult { Refusal = CommandResult.FailWithDetail((string)detail["message"], detail) };
            }

            if (match.Outcome == DocMatchOutcome.Ambiguous)
                return Refuse("target_document '" + target + "' matches MORE THAN ONE open document and nothing " +
                              "available distinguishes them. " + match.Explain() + " Open documents: " + Describe(openIdentities) + ". Nothing was changed - pass a " +
                              "full path instead of a title.");

            Document resolved = open[match.Index];
            if (!ReferenceEquals(resolved, active) &&
                resolved.Fingerprint(revitYear) != activeIdentity.Fingerprint())
                return new GateResult { Refusal = CommandResult.FailWithDetail("target_document '" + target + "' resolves to '" + Safe(() => resolved.Title) +
                              "', but the ACTIVE document is '" + Safe(() => active.Title) + "'. This command acts " +
                              "on the active document and will NOT switch for you: activating a document changes " +
                              "what the user is looking at, and guessing which one they meant is the mistake this " +
                              "check exists to prevent. Activate the right document in Revit, then call again. " +
                              "Nothing was changed.", DocumentActivationSuggestion.Build(openIdentities[match.Index], commandName, request, !resolved.IsLinked && System.IO.File.Exists(openIdentities[match.Index].Path))) };

            return new GateResult { Document = active, Identity = activeIdentity };
        }


        /// <summary>What an identity check knows about an element, read the same way horizun_query_model reports it.</summary>
        internal static ElementIdentityFacts IdentityFacts(Document doc, Element e)
        {
            if (e == null) return null;
            string category = ElementIdentityGuard.CategoryNameFor(
                () => e.Category?.Name, e is TextNoteType,
                () => Category.GetCategory(doc, BuiltInCategory.OST_TextNotes)?.Name);
            string builtIn = ElementIdentityGuard.BuiltInNameFor(() => e.Category == null ? null : e.Category.BuiltInCategory.ToString(), e is TextNoteType);
            string type = null, family = null;
            try
            {
                ElementType t = e as ElementType ?? doc.GetElement(e.GetTypeId()) as ElementType;
                type = t?.Name;
                family = t?.FamilyName;
            }
            catch { }
            // The Horizun ownership mark; anything that cannot be read counts as "not ours" (the guard fails closed).
            bool owned = ElementIdentityGuard.SafeOwned(() => HrzOwnership.IsTrustedOwned(doc, e));
            return new ElementIdentityFacts { Id = Rid.Value(e.Id), UniqueId = e.UniqueId, Category = category, CategoryBuiltIn = builtIn, Type = type, Family = family, Owned = owned };
        }

        /// <summary>
        /// Re-read the document immediately before committing, and prove it is still the
        /// one the plan was built against. Cheap, and it closes the window between a dry
        /// run and an execution during which anything can have moved.
        /// </summary>
        public static CommandResult StillTheSame(UIApplication app, string expectedFingerprint, string commandName)
        {
            string revitYear = Safe(() => app?.Application?.VersionNumber);
            Document now = null;
            try { now = app?.ActiveUIDocument?.Document; } catch { now = null; }

            if (now == null)
                return CommandResult.Fail("The document closed before '" + commandName + "' could act. Nothing was changed.");

            string fingerprintNow = IdentityOf(now, revitYear).Fingerprint();
            if (fingerprintNow != expectedFingerprint)
                return CommandResult.Fail("The ACTIVE document changed between the plan and this execution - it is " +
                                          "now '" + Safe(() => now.Title) + "'. Nothing was changed. Re-run the dry " +
                                          "run against the document you mean.");
            return null;
        }

        private static string Fingerprint(this Document d, string revitYear) => IdentityOf(d, revitYear).Fingerprint();

        /// <summary>
        /// The two-step flow, in one place so four commands cannot implement it four ways.
        ///
        /// Call before doing any work when dry_run is false. Returns null to proceed, or a
        /// refusal to return unchanged. On a dry run it does nothing - the token is issued
        /// by StampConfirmation once the plan is known.
        /// </summary>
        public static CommandResult RequireConfirmation(UIApplication app, GateResult gate, JObject request,
                                                        string commandName, string planHash) =>
            RequireConfirmation(app, gate, request, commandName, planHash, null, null);

        /// <summary>
        /// The same gate, given the plan RECOMPUTED NOW. `atApply` is the resolved plan as
        /// this call sees the model; `rehearsed` is what the dry run recorded, when the
        /// command kept it. Pass nulls from a command that does not materialise its plan yet:
        /// the check degrades to the request comparison and the answer SAYS so, because a
        /// guarantee nobody mentions reads exactly like one that held.
        /// </summary>
        public static CommandResult RequireConfirmation(UIApplication app, GateResult gate, JObject request,
                                                        string commandName, string planHash,
                                                        ResolvedPlan atApply, ResolvedPlan rehearsed, bool checkRequestOnly = false)
        {
            if (_atomicPlanDepth > 0)
                return StillTheSame(app, gate.Fingerprint, commandName);

            // The plan object travels down so a stale refusal can name what moved even
            // when the caller did not keep its own rehearsal: the store kept it with the
            // token, and the drift is computed against that.
            string token = request?.Value<string>("confirmation_token");
            ConfirmationCheck check = checkRequestOnly
                ? Confirmations.CheckRequest(token, commandName, gate.Fingerprint, planHash)
                : Confirmations.Validate(token, commandName, gate.Fingerprint, planHash, atApply,
                    (atApply != null && rehearsed != null) ? ResolvedPlan.DescribeDrift(rehearsed, atApply) : null);
            if (!check.Ok)
                // The refusal travels as DATA beside the prose. The dimension contracts
                // promise a closed outcome set - committed_verified, rolled_back, refused,
                // stale_plan, uncertain - and a client branching on `state` must not have
                // to sniff "THE MODEL MOVED" out of a sentence to tell a stale plan from
                // any other refusal. One seat, here, so every token-gated command answers
                // the same way.
                return CommandResult.FailWithDetail(check.Message + " (Nothing was changed.)",
                    new JObject
                    {
                        ["error_code"] = string.IsNullOrWhiteSpace(request?.Value<string>("confirmation_token"))
                            ? ErrorCodes.ConfirmacionRequerida : ErrorCodes.TokenInvalidoOVencido,
                        ["state"] = check.State == ConfirmationState.StalePlan ? "stale_plan" : "refused",
                        ["confirmation_state"] = check.State.ToString()
                    });

            // The token was minted against this document; prove it has not moved between
            // then and now, immediately before any work starts.
            return StillTheSame(app, gate.Fingerprint, commandName);
        }

        /// <summary>
        /// Name the document that was touched, and - on a rehearsal - hand back the key to
        /// this exact plan. Applied to the response whichever path produced it.
        /// </summary>
        /// <summary>
        /// The plan a dry run resolved, handed to the next StampConfirmation. A field rather
        /// than a parameter so that every existing caller keeps compiling: a command is
        /// taught to materialise its plan by calling RecordResolvedPlan, and one that has not
        /// been taught yet behaves exactly as before and says so.
        /// </summary>
        [ThreadStatic] private static ResolvedPlan _pendingPlan;

        /// <summary>Called by a command's dry run once it knows WHAT it resolved.</summary>
        public static void RecordResolvedPlan(ResolvedPlan plan) { _pendingPlan = plan; }

        /// <summary>
        /// Forget any plan a command recorded and did not stamp (backlog #17). A command that
        /// records its plan and then refuses - the token, the document, a failure - never reaches
        /// StampConfirmation, and the thread-static plan waited for the NEXT command on the
        /// thread: its dry run published another command's plan as its change_preview and said
        /// the token was bound to it. The dispatcher calls this around every command it runs, and
        /// a command that runs children calls it before stamping its own reply.
        /// </summary>
        public static void ClearResolvedPlan() { _pendingPlan = null; }

        public static void StampConfirmation(object data, GateResult gate, string commandName,
                                             string planHash, bool dryRun, string limitNote = null, TimeSpan? ttl = null)
        {
            var o = data as JObject;
            if (o == null) return;

            Confirmations.StampResponse(o, gate.Identity, commandName, planHash, dryRun, _pendingPlan, limitNote, ttl);
            _pendingPlan = null;
        }

        /// <summary>
        /// A stable fingerprint of a request's SCOPE. Only fields that change WHAT IS
        /// AFFECTED belong in it: a guard that fires on a display option is one callers
        /// learn to work around, which was measured on delete's first version.
        ///
        /// The arithmetic itself lives in Confirmation.cs, which carries no Revit at all,
        /// so the property that a token approves ONE plan is provable without a building.
        /// This stays as the name every command calls, because it is the name that reads
        /// correctly at the call site.
        /// </summary>
        public static string PlanHash(JObject request, params string[] scopeFields)
            => ConfirmationStore.PlanHash(request, scopeFields);

        private static GateResult Refuse(string message) =>
            new GateResult { Refusal = CommandResult.Fail(message) };

        private static string Describe(List<DocIdentity> ids)
        {
            if (ids == null || ids.Count == 0) return "(none could be listed)";
            var parts = new List<string>();
            foreach (var i in ids) parts.Add("'" + i.Describe() + "'");
            return string.Join(", ", parts);
        }

        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    }
}
