// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE PREVENTION GATE ON THE OPERATIONS THIS BRIDGE OWNS.
//
// horizun_save_document and horizun_export take an optional `require_gate`.
// Without it they behave exactly as before. With it, the operation runs only
// if the model - MEASURED AGAIN, NOW, by the same checks horizun_audit_model
// runs - satisfies the profile the caller declared. One evaluator, not two:
// the rows in a refusal are the rows the audit would have returned for the
// same requirement set, produced by PreDeliveryGateRules, and the decision is
// PreventionGateRules'. This file only decides what to hand them and how to
// say the answer.
//
// THREE THINGS THIS FILE HOLDS:
//
//   * NOT ASSESSABLE IS NOT FAIL, and it leads with the coverage problem. A
//     row that could not be measured, a check that died, a closed workset -
//     each is a reason nobody can say the model is clean, and none is a
//     defect. If the refusal did not say which, people would stop passing
//     require_gate on every workshared model, and the gate would be gone.
//   * AN OVERRIDE NAMES WHAT IT ACCEPTS. It may name a check, a requirement or
//     a finding id, and it covers exactly the failing rows it names. It
//     expires by comparison against a now_utc the caller supplies; no clock is
//     read here.
//   * THE GATE SAYS WHAT IT DOES NOT COVER. Revit's own Save, Save As,
//     Synchronize with Central and Export menu are not intercepted - the events
//     exist and this add-in deliberately subscribes to none of them - and every
//     gated reply says so, whichever way it decided, so a gate on the bridge's
//     save is never read as "this model cannot be delivered dirty".
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The four answers a gated operation records. Every gated reply carries one.</summary>
    public static class OperationGateDecision
    {
        public const string Allowed = "allowed";
        public const string Blocked = "blocked";
        public const string Overridden = "overridden";
        public const string NotAssessable = "not_assessable";
    }

    /// <summary>
    /// A UTC TIMESTAMP AS THE GATE COMPARES IT.
    ///
    /// The gate compares time as STRINGS, ordinally, so that no clock is read and
    /// an expiry is exact in a test. That only works if both strings are in one
    /// format - and they were not: Newtonsoft parses an ISO-8601 value in a
    /// request into a DateTime, and `(string)token` then renders it in the
    /// machine's culture ("03/01/2026 00:00:00"), which an ordinal compare against
    /// "2026-02-01T00:00:00Z" reads as EXPIRED, and which compares two culture
    /// strings month-first across years. Measured while testing the gate; the
    /// audit's own prevention_gate had the same seam. Every timestamp the gates
    /// read goes through here, so both sides of every comparison are one shape.
    /// </summary>
    public static class UtcStamp
    {
        public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        public static string Normalise(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date)
                return ((DateTime)token).ToUniversalTime().ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
            string raw = token.Type == JTokenType.String ? (string)token : token.ToString();
            return Normalise(raw);
        }

        public static string Normalise(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            DateTime parsed;
            if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.AdjustToUniversal |
                                  System.Globalization.DateTimeStyles.AssumeUniversal, out parsed))
                return parsed.ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
            // Not a date at all: handed on as written, so the comparison fails in
            // whatever way the caller's string deserves rather than in silence.
            return raw;
        }
    }

    /// <summary>
    /// THE TIME A GATED OPERATION IS JUDGED AGAINST, AND WHO GETS TO SAY IT.
    ///
    /// The expiry on an override is compared against a reference time. On the pure
    /// evaluation horizun_audit_model performs - which decides nothing and writes
    /// nothing - that reference is supplied by the caller, deliberately, so an
    /// expiry is exact in a test rather than dependent on when the suite runs.
    ///
    /// ON A REAL GATED OPERATION THAT IS A HOLE. The save or the export is about to
    /// touch a file, the override is the only thing standing between a blocked
    /// model and a written one, and now_utc arrives in the same request as the
    /// override it is used to judge. Anyone holding an override that expired in
    /// March could send now_utc "2026-02-01T00:00:00Z" and the gate would agree it
    /// was still in date. An expiry the caller picks the time for is not an expiry;
    /// it is a field.
    ///
    /// So on an operation the reference is THIS MACHINE'S CLOCK, always, and a
    /// caller-supplied now_utc is only ever an ADDITIONAL CONSTRAINT:
    ///
    ///   * it must be a real timestamp - a string that is not one is refused rather
    ///     than compared, because a comparison against nonsense answers something
    ///     and means nothing;
    ///   * it must agree with this machine's clock to within ToleranceSeconds -
    ///     further apart and the gate refuses, saying the skew and the tolerance,
    ///     because one of the two clocks is wrong and the gate cannot tell which;
    ///   * where both are valid the LATER of the two is used, so a caller may bring
    ///     an expiry forward and can never push one back.
    ///
    /// Revit-free: the machine's clock is a parameter, so every rule here is exact
    /// in a test and the only place DateTime.UtcNow is read is the operation itself.
    /// </summary>
    public sealed class GateClockReference
    {
        /// <summary>What an expiry is compared against. Null when Refusal is set.</summary>
        public string ReferenceUtc;
        public string MachineUtc;
        /// <summary>What the caller said, normalised. Null when they said nothing.</summary>
        public string CallerUtc;
        /// <summary>caller - machine, in seconds. 0 when the caller said nothing.</summary>
        public double SkewSeconds;
        /// <summary>Why this reference cannot be used. Null when it can.</summary>
        public string Refusal;

        public bool Ok { get { return Refusal == null; } }
    }

    public static class GateClock
    {
        /// <summary>
        /// How far a caller's now_utc may sit from this machine's clock before the
        /// gate refuses to judge an expiry at all. Five minutes: wide enough for the
        /// ordinary drift between two machines nobody synchronised to the second,
        /// far too narrow to reach back past an expiry.
        /// </summary>
        public const int ToleranceSeconds = 300;

        public const string Means =
            "on a gated OPERATION the reference time is this machine's UTC clock. A caller-supplied now_utc is an " +
            "additional constraint - it must parse, it must agree with the machine clock to within 300 seconds, " +
            "and where both are valid the LATER of the two is used - so a caller may bring an expiry forward and " +
            "can never push one back. The clock-free comparison stays on horizun_audit_model's evaluation, which " +
            "decides nothing and writes nothing.";

        /// <summary>Resolve the reference. `machineUtcNow` is the only clock this reads.</summary>
        public static GateClockReference Resolve(string callerNowUtc, DateTime machineUtcNow)
        {
            DateTime machine = machineUtcNow.Kind == DateTimeKind.Utc ? machineUtcNow : machineUtcNow.ToUniversalTime();
            string machineStamp = machine.ToString(UtcStamp.Format, System.Globalization.CultureInfo.InvariantCulture);

            var r = new GateClockReference { MachineUtc = machineStamp, ReferenceUtc = machineStamp };
            if (string.IsNullOrWhiteSpace(callerNowUtc)) return r;

            string normalised = UtcStamp.Normalise(callerNowUtc);
            DateTime caller;
            if (!DateTime.TryParseExact(normalised, UtcStamp.Format,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                                        System.Globalization.DateTimeStyles.AssumeUniversal, out caller))
            {
                r.CallerUtc = normalised;
                r.ReferenceUtc = null;
                r.Refusal = "require_gate.now_utc is '" + callerNowUtc + "', which is not a UTC timestamp. It is " +
                            "compared against this machine's clock, and a comparison against something that is not " +
                            "a time answers something and means nothing.";
                return r;
            }

            r.CallerUtc = normalised;
            r.SkewSeconds = Math.Round((caller - machine).TotalSeconds, 3);
            if (Math.Abs(r.SkewSeconds) > ToleranceSeconds)
            {
                r.ReferenceUtc = null;
                r.Refusal = "require_gate.now_utc is " + normalised + " and this machine's clock says " + machineStamp +
                            " - " + Math.Round(Math.Abs(r.SkewSeconds)) + " seconds apart, and the tolerance is " +
                            ToleranceSeconds + ". One of the two clocks is wrong and the gate cannot tell which, so " +
                            "it will not judge an expiry against either. " + Means;
                return r;
            }

            // THE LATER OF THE TWO. A caller may bring an expiry forward; never back.
            r.ReferenceUtc = string.CompareOrdinal(normalised, machineStamp) > 0 ? normalised : machineStamp;
            return r;
        }

        /// <summary>This machine's clock alone, for a call that carried no now_utc.</summary>
        public static GateClockReference Machine(DateTime machineUtcNow)
        {
            return Resolve(null, machineUtcNow);
        }

        public static JObject ToJson(GateClockReference c)
        {
            if (c == null) return null;
            return new JObject
            {
                ["reference_utc"] = c.ReferenceUtc,
                ["machine_utc"] = c.MachineUtc,
                ["caller_now_utc"] = c.CallerUtc,
                ["skew_seconds"] = c.SkewSeconds,
                ["tolerance_seconds"] = ToleranceSeconds,
                ["refusal"] = c.Refusal,
                ["means"] = Means
            };
        }
    }


    /// <summary>The `require_gate` argument, parsed and checked. Nothing else is read.</summary>
    public sealed class RequireGateRequest
    {
        public string ProfileName;
        public string ProfileVersion;
        public JObject Requirements;
        /// <summary>Configuration for the checks, in the audit's own grammar. Optional.</summary>
        public JObject Tolerances;
        public JArray ReadinessRoles;
        public JObject WorksetRules;
        public JToken WarningProfile;
        public string DocumentFingerprint;
        public string FindingSetFingerprint;
        public string NowUtc;
        public GateOverride Override;

        public static readonly string[] KnownKeys =
        {
            "profile", "document_fingerprint", "finding_set_fingerprint", "now_utc", "override"
        };

        public static readonly string[] KnownProfileKeys =
        {
            "name", "version", "requirements", "tolerances", "readiness_roles", "workset_rules", "warning_profile"
        };

        public static readonly string[] KnownOverrideKeys =
        {
            "identity", "reason", "timestamp_utc", "operation", "profile_version", "evidence", "expires_utc",
            "findings_ignored"
        };

        /// <summary>Parse, or explain why not. A null return with a refusal is the whole answer.</summary>
        public static RequireGateRequest Parse(JObject raw, out string refusal)
        {
            refusal = null;
            if (raw == null) { refusal = "require_gate must be an object."; return null; }

            ScanRequestVerdict keys = ScanRequestRules.CheckUnknownKeys(raw, KnownKeys, "require_gate");
            if (!keys.Ok) { refusal = keys.Message; return null; }

            var profile = raw["profile"] as JObject;
            if (profile == null)
            {
                refusal = "require_gate.profile is required: {name, version, requirements}. The standard arrives " +
                          "as an argument; none is compiled in.";
                return null;
            }
            ScanRequestVerdict profileKeys = ScanRequestRules.CheckUnknownKeys(profile, KnownProfileKeys,
                                                                                "require_gate.profile");
            if (!profileKeys.Ok) { refusal = profileKeys.Message; return null; }

            var r = new RequireGateRequest
            {
                ProfileName = (string)profile["name"],
                ProfileVersion = (string)profile["version"],
                Requirements = profile["requirements"] as JObject,
                Tolerances = profile["tolerances"] as JObject,
                ReadinessRoles = profile["readiness_roles"] as JArray,
                WorksetRules = profile["workset_rules"] as JObject,
                WarningProfile = profile["warning_profile"],
                DocumentFingerprint = (string)raw["document_fingerprint"],
                FindingSetFingerprint = (string)raw["finding_set_fingerprint"],
                NowUtc = UtcStamp.Normalise(raw["now_utc"])
            };
            if (string.IsNullOrWhiteSpace(r.ProfileName)) { refusal = "require_gate.profile.name must be a non-empty string."; return null; }
            if (string.IsNullOrWhiteSpace(r.ProfileVersion)) { refusal = "require_gate.profile.version must be a non-empty string: an override is signed against it."; return null; }
            if (r.Requirements == null || r.Requirements.Count == 0)
            {
                refusal = "require_gate.profile.requirements must declare at least one requirement, in " +
                          "horizun_audit_model's requirement_set grammar. Known: " +
                          string.Join(", ", PreDeliveryGateRules.KnownRequirements()) + ".";
                return null;
            }

            JToken ov = raw["override"];
            if (ov != null && ov.Type != JTokenType.Null)
            {
                var o = ov as JObject;
                if (o == null) { refusal = "require_gate.override must be an object."; return null; }
                ScanRequestVerdict overrideKeys = ScanRequestRules.CheckUnknownKeys(o, KnownOverrideKeys,
                                                                                     "require_gate.override");
                if (!overrideKeys.Ok) { refusal = overrideKeys.Message; return null; }
                r.Override = new GateOverride
                {
                    Identity = (string)o["identity"],
                    Reason = (string)o["reason"],
                    TimestampUtc = UtcStamp.Normalise(o["timestamp_utc"]),
                    Operation = (string)o["operation"],
                    ProfileVersion = (string)o["profile_version"],
                    Evidence = (string)o["evidence"],
                    ExpiresUtc = UtcStamp.Normalise(o["expires_utc"])
                };
                foreach (JToken t in (o["findings_ignored"] as JArray) ?? new JArray())
                    if (t.Type == JTokenType.String) r.Override.FindingsIgnored.Add((string)t);
            }
            return r;
        }
    }

    /// <summary>What the fresh audit run handed the gate. Facts, not opinions.</summary>
    public sealed class OperationGateEvidence
    {
        public string Operation;
        public string DocumentTitle;
        public string DocumentFingerprint;
        public string FindingSetFingerprint;
        /// <summary>
        /// The fingerprint the caller cited, if it was found in this session's
        /// audit store. Null when the caller cited nothing; the caller's citation
        /// of something this session never produced is a refusal before evidence.
        /// </summary>
        public List<GateRow> Rows = new List<GateRow>();
        public string Verdict;
        /// <summary>check -> finding_id, so a row can name the finding it judged.</summary>
        public Dictionary<string, string> FindingIdsByCheck = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> ChecksFailed = new List<string>();
        public List<string> ChecksIncomplete = new List<string>();
        public bool VisibilityComplete = true;
        public string VisibilityNote;
    }

    public sealed class OperationGateVerdict
    {
        public string Decision;
        public string Why;
        public GateVerdict Inner;
        public List<string> BlockingFindings = new List<string>();
        public List<string> CoverageProblems = new List<string>();
        public bool Proceed { get { return Decision == OperationGateDecision.Allowed || Decision == OperationGateDecision.Overridden; } }
    }

    public static class OperationGateRules
    {
        public const string EnforcedMeans =
            "this gate ENFORCES on the operation it was attached to: a blocked or not-assessable decision " +
            "refuses the call before the file is touched. It covers ONLY the bridge's own save and export - " +
            "see not_interceptable for what it does not cover, which includes Revit's own Save button.";

        public const string NotAssessableMeans =
            "not_assessable is NOT a fail: nothing was found wrong, and nothing can be said to be right either, " +
            "because part of the measurement did not happen. The reason names which part. Fix the coverage - " +
            "open the closed worksets, re-run a check that died, drop a requirement over a part nobody can " +
            "measure - or sign an override that names the findings.";

        /// <summary>
        /// The paths this gate does not reach, said in every gated reply. The list is
        /// data so a client can show it, and so a test can hold it to naming at least
        /// Revit's Save and its Synchronize with Central.
        /// </summary>
        public static JArray NotInterceptable()
        {
            return new JArray
            {
                Path("revit_ui_save", "Revit's own Save (Ctrl+S, File > Save).",
                     "ControlledApplication.DocumentSaving is cancellable and this add-in deliberately does not " +
                     "subscribe to it: cancelling a modeller's save is a change to Revit's behaviour for everyone " +
                     "with the add-in loaded, and it is not a decision this bridge takes. See " +
                     "docs/evidence/prevention-operation-matrix.md."),
                Path("revit_ui_save_as", "Revit's own Save As.",
                     "DocumentSavingAs is cancellable and unsubscribed, for the same reason."),
                Path("synchronize_with_central", "Revit's Synchronize with Central.",
                     "DocumentSynchronizingWithCentral is cancellable and unsubscribed. This bridge does not " +
                     "synchronize at all, so nothing here stands between a local save and the central model."),
                Path("revit_ui_export", "Revit's own Export menu.",
                     "FileExporting is cancellable and unsubscribed. Only horizun_export consults this gate."),
                Path("other_addins", "A save, sync or export made by another add-in.",
                     "The same events fire and the same choice not to subscribe applies."),
                Path("file_copy_outside_revit", "Copying the .rvt in Explorer, or a Desktop Connector upload.",
                     "Outside Revit entirely; no mechanism exists to intercept it.")
            };
        }

        private static JObject Path(string id, string what, string why)
        {
            return new JObject { ["path"] = id, ["what"] = what, ["why_not_intercepted"] = why };
        }

        /// <summary>
        /// Decide. The rows and verdict were produced by PreDeliveryGateRules over the
        /// fresh run; this hands PreventionGateRules the failing checks as blocking
        /// findings, the run's coverage, and the override - and words the answer.
        ///
        /// THE CLOCK IS A PARAMETER AND IT IS NOT OPTIONAL. An expiry is judged
        /// against `clock.ReferenceUtc`, which GateClock resolved from this machine's
        /// clock; request.NowUtc is what the CALLER said and is never the authority
        /// here. There is deliberately no overload that decides without one: an
        /// operation is about to touch a file, and the only reason this ever took the
        /// caller's word for the time was that nothing forced it to ask.
        /// </summary>
        public static OperationGateVerdict Decide(RequireGateRequest request, OperationGateEvidence evidence,
                                                  GateClockReference clock)
        {
            var v = new OperationGateVerdict();
            if (request == null || evidence == null)
            {
                v.Decision = OperationGateDecision.NotAssessable;
                v.Why = "nothing was submitted to the gate.";
                return v;
            }

            // A REFERENCE TIME THIS GATE CANNOT ESTABLISH IS NOT PERMISSION.
            //
            // A caller whose now_utc is not a timestamp, or sits further from this
            // machine's clock than the tolerance, has not been refused a favour: the
            // gate genuinely cannot say whether an override is in date, and answering
            // anything about the model would be answering a different question. It
            // leads with the clock so nobody reads it as a coverage problem or a
            // failing requirement, and it refuses, so the file is not touched.
            if (clock == null || !clock.Ok)
            {
                v.Decision = OperationGateDecision.NotAssessable;
                v.Why = "NOT ASSESSABLE, and not because of the model: the gate could not establish the time it " +
                        "judges an expiry against. " +
                        (clock == null ? "No reference clock was resolved at all." : clock.Refusal) +
                        " Nothing about profile '" + request.ProfileName + "' " + request.ProfileVersion +
                        " was decided, and nothing was written.";
                return v;
            }

            // THE DOCUMENT MUST BE THE AUDITED ONE. A profile approved against another
            // model's audit is not permission for this one.
            if (!string.IsNullOrEmpty(request.DocumentFingerprint) &&
                !string.Equals(request.DocumentFingerprint, evidence.DocumentFingerprint, StringComparison.Ordinal))
            {
                v.Decision = OperationGateDecision.NotAssessable;
                v.Why = "require_gate.document_fingerprint is " + request.DocumentFingerprint + " and the active " +
                        "document '" + evidence.DocumentTitle + "' is " + evidence.DocumentFingerprint +
                        ". The gate was asked about one model and would run on another; nothing was done.";
                return v;
            }

            // THE AUDIT MUST BE CURRENT. The checks were just re-run; if the caller cited
            // a finding set, the fresh one must be the same set or the model has moved
            // since the audit the caller read.
            if (!string.IsNullOrEmpty(request.FindingSetFingerprint) &&
                !string.Equals(request.FindingSetFingerprint, evidence.FindingSetFingerprint, StringComparison.Ordinal))
            {
                v.Decision = OperationGateDecision.NotAssessable;
                v.Why = "the audit you cited (" + request.FindingSetFingerprint + ") is not the model as it stands: " +
                        "the same checks re-run now produce " + evidence.FindingSetFingerprint + ". The model " +
                        "changed after that audit, or it was taken at another top. Re-run horizun_audit_model and " +
                        "read the current findings before deciding.";
                return v;
            }

            // BLOCKING = the failing rows, named by their check. An override may name
            // the check, the requirement, or the finding id; all three resolve to the
            // check before PreventionGateRules compares them.
            var blocking = new List<string>();
            foreach (GateRow row in evidence.Rows)
                if (row.Status == PreDeliveryGateRules.StatusFail && !blocking.Contains(row.Check))
                    blocking.Add(row.Check);
            v.BlockingFindings = blocking;

            foreach (GateRow row in evidence.Rows)
                if (row.Status == PreDeliveryGateRules.StatusNotMeasurable)
                    v.CoverageProblems.Add("requirement '" + row.Requirement + "'" +
                                           (row.Item == null ? "" : " for '" + row.Item + "'") +
                                           " could not be measured: " + row.Reason);
            foreach (string c in evidence.ChecksFailed)
                v.CoverageProblems.Add("check '" + c + "' did not run at all");
            foreach (string c in evidence.ChecksIncomplete)
                v.CoverageProblems.Add("check '" + c + "' could not read every element it examined; its count is a lower bound");
            if (!evidence.VisibilityComplete)
                v.CoverageProblems.Add(string.IsNullOrEmpty(evidence.VisibilityNote)
                    ? "part of the model is not loaded (a closed workset), so every check ran over a model with holes in it"
                    : evidence.VisibilityNote);

            GateOverride ov = NormaliseOverride(request.Override, evidence);
            var input = new GateInput
            {
                Operation = evidence.Operation,
                DocumentTitle = evidence.DocumentTitle,
                DocumentFingerprint = evidence.DocumentFingerprint,
                ProfileVersion = request.ProfileVersion,
                // COVERAGE FROM THE RUN, plus the rows: a requirement over a part nobody
                // measured is a hole in coverage exactly as a closed workset is.
                CoverageComplete = v.CoverageProblems.Count == 0,
                AuditSupplied = true,
                OperationIsControlled = GatedOperation.All.Contains(evidence.Operation),
                BlockingFindings = blocking,
                Override = ov
            };
            // THE MACHINE'S CLOCK, NOT THE CALLER'S. request.NowUtc has already been
            // checked against it by GateClock and folded into the reference; passing
            // it here instead is the defect this parameter exists to close.
            v.Inner = PreventionGateRules.Decide(input, clock.ReferenceUtc);

            switch (v.Inner.Decision)
            {
                case GateDecision.Allow:
                    v.Decision = OperationGateDecision.Allowed;
                    v.Why = "profile '" + request.ProfileName + "' " + request.ProfileVersion + ": every requirement " +
                            "passed with complete coverage. " + v.Inner.Why;
                    break;
                case GateDecision.RequiresOverride:
                    v.Decision = OperationGateDecision.Overridden;
                    v.Why = "profile '" + request.ProfileName + "' " + request.ProfileVersion + ": " + v.Inner.Why +
                            " The failing rows are in the reply; the override is recorded beside them.";
                    break;
                case GateDecision.Block:
                    v.Decision = OperationGateDecision.Blocked;
                    v.Why = "BLOCKED by profile '" + request.ProfileName + "' " + request.ProfileVersion + ": " +
                            blocking.Count + " requirement(s) FAIL on the model as it stands (" +
                            string.Join(", ", blocking) + "). " + v.Inner.Why +
                            (v.CoverageProblems.Count > 0
                                ? " Coverage was also incomplete (" + v.CoverageProblems.Count + " problem(s)), " +
                                  "which cannot excuse a defect that was found."
                                : "");
                    break;
                default:
                    v.Decision = OperationGateDecision.NotAssessable;
                    // LEADS WITH THE COVERAGE PROBLEM, and says this is not a fail.
                    v.Why = "NOT ASSESSABLE, which is not a fail: no requirement of profile '" + request.ProfileName +
                            "' " + request.ProfileVersion + " failed, but " + v.CoverageProblems.Count +
                            " part(s) of the measurement did not happen - " +
                            string.Join("; ", v.CoverageProblems) + ". " + NotAssessableMeans;
                    break;
            }
            return v;
        }

        /// <summary>
        /// An override that names a requirement or a finding id is rewritten to name
        /// the check, which is what the blocking list is keyed by. Unknown names are
        /// kept as they are, so an override that names nothing real still fails to
        /// cover anything.
        /// </summary>
        private static GateOverride NormaliseOverride(GateOverride ov, OperationGateEvidence evidence)
        {
            if (ov == null) return null;
            var named = new List<string>();
            foreach (string entry in ov.FindingsIgnored)
            {
                string resolved = entry;
                foreach (GateRow row in evidence.Rows)
                {
                    if (string.Equals(row.Requirement, entry, StringComparison.Ordinal)) { resolved = row.Check; break; }
                    string fid;
                    string head = row.Check == null ? null : row.Check.Split('.')[0];
                    if (head != null && evidence.FindingIdsByCheck.TryGetValue(head, out fid) &&
                        string.Equals(fid, entry, StringComparison.Ordinal)) { resolved = row.Check; break; }
                }
                if (!named.Contains(resolved)) named.Add(resolved);
            }
            return new GateOverride
            {
                Identity = ov.Identity, Reason = ov.Reason, TimestampUtc = ov.TimestampUtc,
                Operation = ov.Operation, ProfileVersion = ov.ProfileVersion, Evidence = ov.Evidence,
                ExpiresUtc = ov.ExpiresUtc, FindingsIgnored = named
            };
        }

        /// <summary>The `prevention` block every gated reply carries, refused or not.</summary>
        public static JObject ToJson(RequireGateRequest request, OperationGateEvidence evidence, OperationGateVerdict v,
                                     GateClockReference clock)
        {
            var rows = new JArray();
            foreach (GateRow r in (evidence?.Rows) ?? new List<GateRow>())
            {
                string head = r.Check == null ? null : r.Check.Split('.')[0];
                string fid = null;
                if (head != null && evidence.FindingIdsByCheck != null) evidence.FindingIdsByCheck.TryGetValue(head, out fid);
                rows.Add(new JObject
                {
                    ["requirement"] = r.Requirement,
                    ["check"] = r.Check,
                    ["item"] = r.Item,
                    ["finding_id"] = fid,
                    ["limit"] = r.Limit,
                    ["measured"] = r.Measured,
                    ["status"] = r.Status,
                    ["reason"] = r.Reason
                });
            }
            return new JObject
            {
                ["decision"] = v?.Decision,
                ["operation"] = evidence?.Operation,
                ["why"] = v?.Why,
                ["profile"] = request == null ? null : new JObject
                {
                    ["name"] = request.ProfileName, ["version"] = request.ProfileVersion
                },
                ["document_fingerprint"] = evidence?.DocumentFingerprint,
                ["finding_set_fingerprint"] = evidence?.FindingSetFingerprint,
                ["audit_cited"] = request?.FindingSetFingerprint,
                ["gate"] = new JObject { ["verdict"] = evidence?.Verdict, ["rows"] = rows },
                ["blocking_findings"] = new JArray((v?.BlockingFindings ?? new List<string>()).Select(x => (JToken)x)),
                ["coverage_problems"] = new JArray((v?.CoverageProblems ?? new List<string>()).Select(x => (JToken)x)),
                ["coverage_complete"] = v != null && v.CoverageProblems.Count == 0,
                ["override_accepted"] = v?.Inner?.OverrideAccepted ?? false,
                ["override_rejected_because"] = v?.Inner?.OverrideRejectedBecause,
                // WHICH CLOCK JUDGED THE EXPIRY, said out loud. A reader who cannot
                // see the reference time cannot tell an override that was in date
                // from one whose caller chose a convenient now_utc.
                ["clock"] = GateClock.ToJson(clock),
                ["enforced"] = true,
                ["enforced_means"] = EnforcedMeans,
                ["evaluator"] = "PreDeliveryGateRules over a fresh horizun_audit_model run, decided by PreventionGateRules - " +
                                "the same rows and the same rule the audit reports.",
                ["asymmetry_means"] = PreventionGateRules.AsymmetryMeans,
                ["not_interceptable"] = NotInterceptable()
            };
        }
    }
}
