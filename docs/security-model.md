# Security model

What this bridge can do, who can make it do it, and what is deliberately not
defended against. Written to be argued with.

Last updated: 2026-07-29.

---

## 1. What an attacker would be attacking

Horizun MCP runs code inside Revit with the full Revit API and the rights of the
signed-in user. Whoever can send it a command can read any open model, write
parameters, delete elements, and save over `.rvt` and `.rfa` files. On a machine
with a client's model open, that is the client's building.

The blast radius is bounded by what Revit itself can do as that user. It is not
bounded by anything in this codebase.

## 2. Who can send a command

Two locks, both local. There is no network surface: named pipes are not reachable
across a network, and the MCP server speaks stdio to whatever process launched
it.

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

**What this does NOT defend against:** anything running as the same Windows user.
A process with that user's rights can read the discovery file, present the token,
and drive Revit. That is not a gap to be closed at this layer — such a process can
already read the models directly.

## 3. Arbitrary code execution

`horizun_execute_python` runs arbitrary Python inside Revit on the UI thread. It
is **disabled by default** and enabled per machine in
`%USERPROFILE%\.horizun\settings.json`.

Gated in **both halves**: the server does not advertise it and refuses it if
called anyway; the add-in refuses it independently. The two ship separately, so
neither may be the only gate — a stale server must not be able to run code on a
machine whose owner turned it off. Absence is off: a settings file that is
missing, unreadable or malformed leaves every default at its safe value.

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

`run_async` additionally requires an `idempotency_key`, bound to the Revit
process id, the document identity, a SHA-256 of the code and every other
argument. The reply carrying the `job_id` is precisely the message that goes
missing, and a client retrying a timeout — the correct thing for a client to do —
otherwise queued the script a second time. The same key with the same request
returns the original `job_id` and queues nothing; the same key with a different
request is **refused**, never silently deduplicated.

**What is NOT closed, and is accepted deliberately.** `execute_python` has

- no dry run,
- no plan hash,
- no confirmation token,

so **nothing rehearses what it will do**. The two-step flow in §4 does not apply
to it and cannot: there is no way to predict the effect of arbitrary code without
running it. It is a *document-scoped privileged bypass*, which is a different
sentence from "it complies with the mutation policy", and the tool's own
description now says so where a caller reads it.

The risk is accepted because every one of the seven workflows this bridge exists
to serve depends on it. The compensating controls are the ones above: off by
default and gated in both halves, document-scoped, key-bound on the async path,
size-capped, and audited on every run.

**The ledger is per process.** Keys live in memory in the Revit that issued them,
like confirmation tokens and for the same reason. Across a Revit restart a key is
forgotten and the same request would run again. That is not closable by
persisting it: a key whose job was in flight when the process died has an outcome
nobody knows, and replaying a mutation on that basis is the failure being
prevented, not the cure.

## 4. Destructive operations

Every command that CHANGES a model must name the model (`target_document`), and
the gate refuses three ways: no match, more than one match, or a match that is not
the *active* document. It never switches documents to make a call work. That now
includes `execute_python` — see §3a for what it still does not do.

`horizun_delete_verified` additionally requires a two-step flow. A dry run issues
a token bound to the command, a fingerprint of the document and a hash of the
request; execution spends it once, and only while all three still match. The
fingerprint includes the Revit year, so the same file open in two versions is two
documents.

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
- Concurrent pipe connections are capped at 8.
- One Revit command runs at a time; further requests are refused with a
  description, not queued.
- A command that overruns its budget **cannot be stopped**. Cancellation stops
  the server waiting; the work continues inside Revit, and the reply says so.

## 7. Supply chain

Every redistributed component is inventoried with its licence and SHA-256 in
`dist/sbom.json`, generated from the payload that actually ships rather than from
the project file. See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

`dotnet list package --vulnerable --include-transitive` reports no vulnerable
packages in either project as of 2026-07-29. That is a snapshot of one advisory
database on one day; CI re-runs it.

The largest third-party surface is the 614-file IronPython standard library, and
nothing in this repository audits it.

## 8. Code signing — an open release blocker

**Nothing is signed.** The consequences, measured rather than assumed:

- Revit raises `Security - Unsigned Add-In` on first load, per add-in per year.
  On this machine that has been answered once per year and does not recur:
  Revit records the decision per **AddInId**, not per binary hash, under
  `HKCU\Software\Autodesk\Revit\Autodesk Revit <year>\CodeSigning`.
- **Signing alone does not remove the dialog.** It downgrades it to
  `Signed Add-In`, once per certificate per machine. Zero dialogs additionally
  requires the publisher certificate in the machine's Trusted Publishers store
  before Revit starts.
- **Signing with a certificate the machine does not trust is WORSE than not
  signing.** Measured with a self-signed certificate: Revit went from loading
  silently to `Security - Invalid Signature — this signed add-in has a security
  problem`. Windows cannot chain it to a trusted root and Revit reads that as
  tampering. Everything was reverted to unsigned.

A certificate costs roughly USD 309 for the first year (SSL.com OV with cloud
signing; a USB token requires a human per signature and breaks CI). Every free
route was checked and rejected: they either sign with someone else's name, are
restricted to individuals, or are invisible to Windows.

**This is a release blocker for installing on a machine that is not ours**, and it
is a decision with a price tag, not an oversight. Writing the Trusted Publishers
step into the installer is deliberately deferred until there is a real certificate
to test it against — it changes the machine's trust configuration and must be an
explicit, opt-in step, never a silent side effect.

## 9. Known gaps, stated

- Nothing audits the Python standard library that ships in every payload.
- The confirmation token binds a request, not a resolved element set (§4).
- Cancellation cannot stop work already inside Revit (§6).
- Anything running as the same Windows user can drive this bridge (§2).
- No signing (§8).
