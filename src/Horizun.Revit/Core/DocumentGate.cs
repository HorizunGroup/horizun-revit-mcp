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
                RevitYear = revitYear,
                ModelGuid = Safe(() =>
                {
                    var p = d.GetCloudModelPath();
                    return p == null ? null : p.GetModelGUID().ToString();
                })
            };
        }

        /// <summary>
        /// Resolve the document a mutating command should act on.
        ///
        /// `target_document` is REQUIRED: a mutation that does not name its target is a
        /// mutation aimed at whatever happens to be in front. It is matched against the
        /// ACTIVE document only - this never switches documents on the caller's behalf,
        /// because silently retargeting is the failure it exists to prevent.
        /// </summary>
        public static GateResult ForMutation(UIApplication app, JObject request, string commandName)
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

            var wanted = new DocIdentity { Title = target, Path = target, RevitYear = revitYear };
            DocMatch match = DocumentMatcher.Find(openIdentities, wanted);

            if (match.Outcome == DocMatchOutcome.None)
                return Refuse("No open document matches target_document '" + target + "'. Open documents: " +
                              Describe(openIdentities) + ". Nothing was changed.");

            if (match.Outcome == DocMatchOutcome.Ambiguous)
                return Refuse("target_document '" + target + "' matches MORE THAN ONE open document and nothing " +
                              "available distinguishes them. " + match.Explain() + " Nothing was changed - pass a " +
                              "full path instead of a title.");

            Document resolved = open[match.Index];
            if (!ReferenceEquals(resolved, active) &&
                resolved.Fingerprint(revitYear) != activeIdentity.Fingerprint())
                return Refuse("target_document '" + target + "' resolves to '" + Safe(() => resolved.Title) +
                              "', but the ACTIVE document is '" + Safe(() => active.Title) + "'. This command acts " +
                              "on the active document and will NOT switch for you: activating a document changes " +
                              "what the user is looking at, and guessing which one they meant is the mistake this " +
                              "check exists to prevent. Activate the right document in Revit, then call again. " +
                              "Nothing was changed.");

            return new GateResult { Document = active, Identity = activeIdentity };
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
                                                        string commandName, string planHash)
        {
            ConfirmationCheck check = Confirmations.Validate(
                request?.Value<string>("confirmation_token"), commandName, gate.Fingerprint, planHash);
            if (!check.Ok)
                return CommandResult.Fail(check.Message + " (Nothing was changed.)");

            // The token was minted against this document; prove it has not moved between
            // then and now, immediately before any work starts.
            return StillTheSame(app, gate.Fingerprint, commandName);
        }

        /// <summary>
        /// Name the document that was touched, and - on a rehearsal - hand back the key to
        /// this exact plan. Applied to the response whichever path produced it.
        /// </summary>
        public static void StampConfirmation(object data, GateResult gate, string commandName,
                                             string planHash, bool dryRun, string limitNote = null)
        {
            var o = data as JObject;
            if (o == null) return;

            o["document"] = gate.Identity?.Describe();
            o["document_fingerprint"] = gate.Identity?.FingerprintDigest();
            if (!dryRun) return;

            Confirmation issued = Confirmations.Issue(commandName, gate.Fingerprint, planHash);
            o["confirmation_token"] = issued.Token;
            o["confirmation_expires_utc"] = issued.ExpiresUtc.ToString("u");
            o["confirmation_note"] =
                "To execute this, call again with dry_run=false and this confirmation_token. It is SINGLE USE, it " +
                "expires, and it is bound to this document and this request - if either changes it is refused and " +
                "nothing is written." +
                (limitNote == null ? "" : " STATED LIMIT: " + limitNote);
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
