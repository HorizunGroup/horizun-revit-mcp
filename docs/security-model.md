# Security model

What this bridge can do, who can make it do it, and what is deliberately not
defended against. Written to be argued with.

Last updated: 2026-08-04.

---

## 1. What an attacker would be attacking

Horizun MCP runs code inside Revit with the full Revit API and the rights of the
signed-in user. Whoever can send it a command can read any open model, write
parameters, delete elements, and save over `.rvt` and `.rfa` files. On a machine
with a client's model open, that is the client's building.

The blast radius is bounded by what Revit itself can do as that user. It is not
bounded by anything in this codebase.

## 2. Who can send a command

Two locks protect the inbound Revit bridge, both local. There is no inbound
network listener: named pipes are not reachable across a network, and the MCP
server speaks stdio to whatever process launched it. The optional Power BI tool
has a separately bounded outbound HTTPS path described in §3b.

**The pipe ACL.** The listening pipe is created with an explicit DACL granting
FullControl to the current Windows user and nothing else — no Everyone, no Users,
no NETWORK. Without it, a pipe takes the process token's default DACL, which on a
shared workstation or a terminal server is reachable by another logged-in user.

**The token.** Each Revit publishes a 256-bit token from a cryptographic RNG in
its discovery file, and every request must carry it. Comparison is constant-time,
so a wrong token leaks nothing through timing. The discovery file itself is
written with an ACL restricting it to the current user.

Two locks rather than one, on purpose: a token is one secret in one file, and if
it leaks the ACL is what still stands between another session and a command that
edits a building.

### 2a. Capability profiles

The same `%USERPROFILE%\.horizun\settings.json` is read by the MCP server and
the add-in on every use. `permission_profile` has four values:

- `read_only`: no model mutation or document-session tool is advertised or run.
- `safe_write`: typed, reversible model edits are allowed; opening,
  saving, relinquishing, document-session changes and exports are refused.
- `full_write`: the session/file operations above are also allowed.
- `unsafe_code`: full write plus eligibility for arbitrary Python.
  Python is additionally controlled by the independent `enable_execute_python`
  switch, which defaults to false.

**The default is `safe_write`, with Python off.** A fresh install can use typed,
verified writes inside the active document, but cannot change document sessions,
write external files or execute arbitrary code. Elevation is an explicit owner
decision. Revit's **Python ON/OFF** button may write a bounded
`execute_python_ui_grant_until_utc`; it expires automatically without changing
the underlying profile. A durable enable requires both `unsafe_code` and an
explicit `enable_execute_python=true`. Changes are announced as
`notifications/tools/list_changed`; this is a discovery convenience only, because
both halves still re-check the permission on every call.

Three fail-closed rules survive that decision. A `permission_profile` string
that is present but not one of the four values falls back to `read_only`, never
to a more privileged mode. A settings file that exists but cannot be parsed
falls **closed** (`read_only`, Python off): it may be a corrupted explicit
restriction, and corruption must never convert "I turned this off" into
"everything is enabled". And `allowed_tools` is an optional allowlist while
`denied_tools` always wins.

**What this does NOT defend against:** anything running as the same Windows user.
A process with that user's rights can read the discovery file, present the token,
and drive Revit. That is not a gap to be closed at this layer — such a process can
already read the models directly.

## 3. Arbitrary code execution

`horizun_execute_python` runs arbitrary Python inside Revit on the UI thread. It
is **disabled by default**. The preferred interactive grant is the Revit ribbon:
the owner acknowledges the risk and enables it for 60 minutes, after which the
grant fails closed. Pressing the button while enabled revokes both temporary and
durable enable flags immediately. A durable developer machine can opt in with
`permission_profile=unsafe_code` plus `enable_execute_python=true`.

Enabling it is a real widening of the exposed surface:
an agent that reads untrusted content (a linked DWG, a PDF, an email) and holds
this tool can be prompt-injected into running code with the signed-in user's
rights. The compensating controls are the ones below, an explicit human grant,
automatic expiry and the independent far-end gate.

Gated in **both halves**: when disabled, the server does not advertise it and
refuses it if called anyway; the add-in refuses it independently. The two ship
separately, so neither may be the only gate — a stale server must not be able to
run code on a machine whose owner turned it off. A settings file that exists but
is unreadable or malformed falls **closed** (Python off, `read_only`), so a
corrupted explicit restriction never reads as consent; genuine absence reads as
the safe-write/Python-off posture.

Scripts are capped at 200,000 characters, and every run writes an audit line with
the user, the document, the script length, the duration and the outcome. **Never
the source** — that is the caller's content, and a log is not the place for it.

**Stated limitation.** A script that leaves the document modifiable has left a
transaction open, and this cannot undo it. The Revit API offers no handle on a
transaction opened by other code. The command reports that as a failure and says
so; it does not pretend to have recovered.

### 3a. It is an ACCEPTED RISK, not a policy it satisfies

This section used to sit beside §4 as if the two described one rule. They did
not, and the difference mattered most for the command that can do the most.

**What was closed.** `execute_python` now goes through the same
`DocumentGate.ForMutation` as every typed write: `target_document` is required
and matched against the *active* document, and the call is refused if they
differ. Until then the single most powerful command in the surface was the only
one aimed at whatever window happened to be in front, while "every mutation
validates the document" was recorded as met on evidence from the seven that did.
A sentence true of the commands that were checked and false of the surface is
worse than no sentence, because it reads as a guarantee.

Every Python execution requires an `idempotency_key`. The dispatcher claims it
durably before the script can run. The same key with the identical operation
replays the recorded answer; reuse for different arguments is refused. A claim
without a terminal record after a crash is `in_doubt` and is never executed
again automatically. `run_async` also binds its command-local queue claim to
the document, code and remaining arguments so two simultaneous submissions
cannot create two jobs.

**What is NOT closed, and is accepted deliberately.** `execute_python` has

- no dry run,
- no plan hash,
- no confirmation token,

so **nothing rehearses what it will do**. The two-step flow in §4 does not apply
to it and cannot: there is no way to predict the effect of arbitrary code without
running it. It is a *document-scoped privileged bypass*, which is a different
sentence from "it complies with the mutation policy", and the tool's own
description now says so where a caller reads it.

Two honesty mechanisms narrow the gap without closing it. `preflight=true`
validates permission, document targeting, size, script hash and basic syntax
without executing, and returns advisory warnings — it proves the request is
well-formed, **not** that the code is safe, and the response says so. And every
run's `__output__` is classified against a structured evidence contract.

That classification is deliberately capped. The host does **not** re-read the
model after arbitrary Python, so it cannot certify anything a script claims; the
strongest state this path returns is `self_reported_verified`, alongside
`completed_unverified`, `partial` and `failed`. There is no `verified` state on
the Python path at all, `host_verified` is always false, and a script claiming
`verified` without evidence is downgraded to `completed_unverified`. The
distinction is load-bearing: a typed write's "verified" is a fact the bridge
re-read, a script's is testimony. An earlier build returned plain `verified` for
any script that said so and attached a non-empty list, which made
`{"evidence":["ok"]}` indistinguishable from a real post-commit re-read — the
contract lying in the one place it exists to protect. A real `verified` for
Python would need a typed evidence contract (ids and properties the host could
re-read itself) plus a Revit-side verifier; neither exists, and a generic one is
not possible.

Neither mechanism is a rehearsal; both only make the report about a run honest.

**The fallback grant is a property of the whole request.** A typed refusal may
carry a machine-readable `fallback` block, and `allowed: true` requires all of:
nothing was written, at least one action failed for a structural capability gap,
and *every* failing action failed for one. A batch mixing an uncovered action
with an invalid argument is refused the grant and told which indices were gaps —
otherwise one uncovered entry would license a script around input the caller
should have fixed. The decision lives in one place (`FallbackDecision`) rather
than in each command, and every structural refusal in the typed surface is
classified in a test-enforced inventory as granted, argument-fixable, or
reachable after a write.

The capability exists because some advanced workflows still need a fallback from
"no typed capability" to "run through Python and self-reported", but it is off by
default and requires an owner-controlled grant. Note the wording: the Python path is never
host-verified. What comes back is the script's own testimony
(`self_reported_verified`, `completed_unverified`, `partial` or `failed`), and
`host_verified` is always false — the bridge does not re-read the model after a
Python run the way it does after a typed write. The compensating controls are the ones above:
gated in both halves with a respected per-machine off switch, document-scoped,
durably key-bound on every run, size-capped, and audited on every run.

**The mutation ledger is durable.** Before any model/session mutation starts, an
append-only claim is written under `%USERPROFILE%\.horizun\idempotency`; the
full terminal result is appended afterward. This survives Revit and MCP-server
restarts. A torn claim is not guessed: it remains `in_doubt` until a human
inspects the model. Records can contain returned model data. Retention is
configurable in `settings.json` with `idempotency_retention_days`,
`idempotency_max_bytes`, `job_retention_days` and `job_max_bytes`. The compatible
default is `0` for both limits — keep forever. Cleanup removes only valid terminal
records; active jobs, claim-only/`in_doubt` records, corrupt files and the key
currently being claimed are never selected. A malformed policy fails closed and
keeps everything. Operators handling sensitive model data should set explicit
time and size limits and protect the directory independently.

### 3b. Bounded outbound Power BI delivery

`horizun_power_bi_push` is the only built-in host operation that sends user data
over the network. It requires `full_write` or `unsafe_code`. Its destination is
constructed internally from GUID dataset/workspace identifiers and a table name:
the data endpoint is always `https://api.powerbi.com`, and service-principal
authentication always uses `https://login.microsoftonline.com/<tenant-guid>`.
No URL, access token or client secret is accepted in MCP arguments.

Credentials come only from fixed server environment variables. Request values
are capped at 10,000 rows, 75 columns, 4,000 characters per string and an 8 MiB
local payload. Nested objects and arrays are refused. Microsoft error response
bodies are not echoed or logged because they may contain tenant data.

The operation claims its durable key before token acquisition or upload. A
successful or definitively rejected HTTP response is recorded; a lost response
after delivery leaves the key `in_doubt`, and the same key never sends again
automatically. This prevents a retry from silently duplicating rows, but it does
not prove whether Microsoft accepted a request whose response was lost. A human
must inspect the destination before choosing a new key.

**What this does NOT defend against:** an authorized caller deliberately sending
model or project data to the configured Power BI tenant. `full_write` grants that
capability. Tenant permissions, data classification and service-principal scope
remain deployment responsibilities.

## 4. Destructive operations

Every command that CHANGES a model must name the model (`target_document`), and
the gate refuses three ways: no match, more than one match, or a match that is not
the *active* document. It never switches documents to make a call work. That now
includes `execute_python` — see §3a for what it still does not do.

Typed plan/apply tools require a two-step flow. A dry run issues a token bound to
the command, a fingerprint of the document and a hash of the request; execution
spends it once, and only while all three still match. `horizun_execute_plan`
extends that rule over an ordered graph inside one TransactionGroup: only
transaction-reversible typed commands are accepted and one failure rolls back
the whole graph. The fingerprint includes the Revit year, so the same file open
in two versions is two documents.

**Stated limitation.** The token binds the *request*, not the element set the
rehearsal found. In purge mode the same request can match a different set once the
model moves, and that would still be accepted.

## 5. Multiple Revit instances

Two instances of the same Revit year is normal — opening a file starts a second
one. Discovery is per instance, and when more than one is running and nothing says
which is meant, calls are **refused**, not sent to a guess. A command sent to the
wrong session is a correct edit to the wrong model.

## 6. Denial of service, and what is bounded

- Requests over 4 MB are refused rather than parsed.
- The pipe reads one request bounded in size (4 MB) and time (30 s), so a peer
  that connects and says nothing cannot hold a thread.
- Concurrent pipe connections are capped at 21: one executing call, up to 16
  FIFO waiters and four slots of headroom so cancellation control can normally
  enter even when the command queue is full. A malicious local peer can occupy
  all 21; cancellation then reports that removal could not be proved.
- One Revit command runs at a time; up to 16 further requests wait in FIFO order.
  The seventeenth waiter is refused before execution as explicit backpressure.
- A queued request can be cancelled over a separate authenticated control
  connection and is removed under the queue lock. That response proves it never
  started. A command already executing **cannot be stopped**; cancellation stops
  the server waiting, the work continues inside Revit, and the reply says so.
- Ordinary calls and explicit `run_async` jobs alternate while both queues have
  work, preventing either queue from starving the other.
- `horizun_submit_job` exposes that bounded async queue to installed typed Revit
  commands. Permission is checked at submission and again when the job starts.

## 7. Supply chain

Every redistributed file is inventoried with its licence and SHA-256 in the
CycloneDX 1.6 `dist/sbom.json`, generated from the payload that actually ships
rather than from the project file. Tagged artifacts receive GitHub/Sigstore build
provenance plus an SBOM attestation. See
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

CI runs `dotnet list package --vulnerable --include-transitive` for the server,
add-in and both test projects. Any clean result is a snapshot of the advisory
sources at that run, not a permanent guarantee.

The largest third-party surface is the 614-file IronPython standard library, and
nothing in this repository audits it.

## 8. Code signing and public trust

The package pipeline reports the signature state; a release must never imply a
public trust chain it does not carry. Stable tags fail closed unless every own
payload binary and the setup wrapper have a valid, non-self-signed Authenticode
signature. The signing key is provisioned outside the repository on the release
runner. Public trust and timestamps are then checked again on a clean Microsoft-
hosted Windows runner, and installed release verification has no unsigned
exception. The source
installer also supports an explicit, per-user self-signing workflow
(`scripts/self-sign.ps1`):

- Revit raises `Security - Unsigned Add-In` on first load, per add-in per year.
  On this machine that has been answered once per year and does not recur:
  Revit records the decision per **AddInId**, not per binary hash, under
  `HKCU\Software\Autodesk\Revit\Autodesk Revit <year>\CodeSigning`.
- **Signing alone does not remove the dialog.** It downgrades it to
  `Signed Add-In`, once per certificate per machine. Zero dialogs additionally
  requires the publisher certificate in the machine's Trusted Publishers store
  before Revit starts.
- **Signing with a certificate the machine does not trust is worse than not
  signing.** Windows cannot build a chain and Revit can report an invalid
  signature. The self-sign helper therefore creates/trusts the certificate only
  after an explicit user action, signs the installed binaries, and verifies them.
  A later source install reuses that already trusted certificate. The optional
  uninstall-cleanup helper can remove that trust and private certificate
  separately; uninstall never removes either silently.

A self-signed certificate is useful only on accounts that explicitly trust it; it
does not establish publisher identity on a clean third-party machine. Until a
public signing identity is provisioned, branch artifacts may use the explicit
unsigned development policy, but **no stable or preview binary may be published
without public trust**. Validation-only tags contain no executable assets.
SHA-256, manifest and attestations complement publisher identity; they do not
replace it.

## 9. Known gaps, stated

- Arbitrary Python remains an explicit privileged bypass (§3), but is off by
  default and temporary ribbon grants expire automatically.
- Nothing audits the Python standard library that ships in every payload.
- Confirmation binds the request and the rehearsed resolved element set; the live
  release matrix still has to prove that invariant for every supported Revit year (§4).
- Cancellation cannot stop work already inside Revit (§6).
- Anything running as the same Windows user can drive this bridge (§2).
- No publicly trusted code-signing identity (§8).
- Durable job/idempotency records may contain result data. Their compatible
  default retains forever until the operator sets the time/size limits (§3a).
