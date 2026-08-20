# Production and certification readiness

Updated: 2026-08-20.

“Certification-ready” means every prerequisite that can be built in this repository
is present and fail-closed, while evidence controlled by Autodesk, GitHub, SignPath
or a clean external machine remains an explicit gate. It does **not** mean that a
public certificate exists, that Autodesk endorses the product, or that a local build
is a public release.

## What this repository can prove without buying a certificate

- One contract and one version for the server and all Revit payloads.
- Revit 2023–2027 compile gates against each installed Revit API.
- Exact payload inventory, SHA-256 manifest, CycloneDX SBOM and GitHub artifact
  attestation.
- Deterministic static triage of the staged IronPython standard library, including
  the pinned 614-file inventory, cross-year byte equality, unexpected-file and
  high-confidence source-risk checks, with JSON/SARIF evidence. This is not a
  comprehensive semantic or vulnerability audit.
- Release gates that submit two pinned SignPath requests (inner payload, then
  installer), require one valid timestamped SignPath Foundation publisher across
  every Horizun binary, and reject self-signed public-trust claims. The steps are
  implemented; the external project identifiers, policies and credential remain
  unavailable until the application is accepted.
- A bootstrap that verifies Authenticode publisher identity independently of a
  checksum downloaded from the same release.
- Separation between untrusted pull-request jobs, Revit integration runners and the
  signing runner, with immutable GitHub Actions references.
- Safe capability defaults: typed in-model writes available, document/external side
  effects gated, arbitrary Python off; a visible Revit ON/OFF grant expires.
- Bounded queues, requests, responses, images, task records and retention stores.
- MCP protocol negotiation through 2025-11-25 with Tools, Resources, Prompts,
  Completions, progress, opt-in Logging and durable task-augmented tool calls.
- Live harnesses that refuse a green release report when a required probe is failed,
  unverified or not covered.

## Evidence state

| Gate | Current state | Authority |
| --- | --- | --- |
| Core tests | 906/906 on 2026-08-20 | repository/CI |
| Server tests | 350/350 on 2026-08-20 | repository/CI |
| Windows deployment suites | 14/14 on 2026-08-20, each child exit code enforced | repository/CI |
| Revit API compile | 2023–2027, 0 warnings/errors on 2026-08-20 | local licensed runner |
| Live Revit matrix | not yet published for this candidate | Revit runners + fixtures |
| SignPath request integration and public identity | workflow integrated and pinned; external project/identity unavailable | repository + external SignPath project/credentials |
| Clean-machine public trust | blocked until a public identity exists | disposable hosted runner |
| GitHub repository controls | secret scanning and push protection enabled; two administrators; protected `release-signing` environment and immutable `v*` tag ruleset created on 2026-08-20 | repository administrators |
| Separate physical runner groups | not provisioned/auditable with the current repository token | organization administrators |
| Autodesk support/endorsement | not claimed and not obtainable from source changes | Autodesk |

The first four rows are reproducible source-candidate evidence. They are not a
substitute for the remaining rows.

## Release decision

- **Stable:** blocked until public signing, clean-machine trust and the five-year
  live matrix pass.
- **Preview with binaries:** also blocked until public signing; it may omit the
  complete live matrix and never replaces `latest`, but it cannot bypass the
  bootstrap's trust boundary.
- **Source/validation commit:** allowed when repository gates pass; it carries no
  installation or certification claim.

## Operator actions outside this tree

1. Obtain acceptance from SignPath Foundation and provision the SignPath GitHub
   project, artifact configurations, signing policies and API credential consumed by
   the already-pinned request steps. The repository must not claim a successful
   signing integration before those external identifiers produce verified requests.
   Follow the exact two-request hand-off in
   [`SIGNPATH-ONBOARDING.md`](SIGNPATH-ONBOARDING.md).
2. Configure and audit distinct `REVIT_RUNNER_GROUP` and `SIGNING_RUNNER_GROUP`
   memberships. The protected signing environment, immutable release-tag ruleset,
   two administrators, secret scanning and push protection already exist; branch
   protection still needs its final post-publication tightening.
3. Run `scripts/run-release-live-gate.ps1` for every supported year with the published
   synthetic fixture set; retain the JSON/JUnit evidence.
4. Build/sign the release, install it on a disposable clean Windows account, and run
   `scripts/verify-release.ps1 -Installed` without `-AllowUnsigned`.

Until those actions exist as attached evidence, the correct release verdict is
**source-validated, with binary signing integration externally blocked**, not
“certified” or “ready to publish binaries”.
