# Release policy — channels, versioning, and what "production ready" will mean

Written because v0.6.1 shipped a fix with **no installer** while v0.6.0 remained the
installable release. That was deliberate and correct, and it was also confusing:
`install-release.ps1` follows `latest` and downloads its assets, so a code-only tag
marked `latest` would break every script that installs the product. The behaviour
was right; the written rule was missing. This is the rule.

## Three channels

| Channel | What it means | Has binaries | Can be `latest` |
|---|---|---|---|
| **stable** | Live matrix approved/published for every supported Revit year; unsigned state explicitly disclosed and acknowledged | yes | **yes** |
| **preview** | New behaviour, fixtures partial, live verification incomplete or single-machine | yes, marked pre-release | no |
| **validation-only** | Tests, harness, docs or CI. **No new binaries.** | no | **never** |

`latest` **always** points at the newest **stable** release that carries an
installer. A preview can carry binaries but remains marked pre-release, so the
default bootstrap cannot silently move stable users onto incomplete live evidence.
A validation-only tag exists so the code change has a name and a diff, not so
anybody installs it — v0.6.1 is the reference example.

The tag names are executable policy: `vX.Y.Z` is stable,
`vX.Y.Z-preview.N` is preview and `vX.Y.Z-validation.N` is validation-only.
The workflow rejects every other `v*` shape. Stable and preview releases publish
`manifest.json` (commit and SHA-256 per payload), `sbom.json` (redistributed
components and licences) and `SHA256SUMS.txt`; validation-only releases attach no
binaries or payload metadata. Stable additionally publishes the live verification
report for each Revit year it claims. Preview is always GitHub pre-release and is
never published to the MCP registry; validation-only is also never registry/latest.

The repository's generic secret patterns always run in hosted CI; the private
client/project wordlist is mandatory on the release runner. GitHub secret scanning
and push protection are repository settings, not files in this tree, and must also
be enabled before stable promotion. A green custom scan does not claim that those
platform controls are on.

Public releases are unsigned by permanent policy, including version 1.0 and
later. The bootstrap requires `-AllowUnsigned`, the README discloses the absence
of publisher authentication, and `package-hashes.json` records
`authenticode: unsigned_by_policy` plus
`publisher_identity_available: false`. The pipeline refuses an unexpected,
invalid or self-signed Authenticode state instead of presenting it as trust.

The release assurance chain is a protected tag, one clean commit stamped into
every owned binary, full SHA-256 manifests, a CycloneDX SBOM, GitHub build
attestations, exact installed-byte verification and the complete Revit 2023–2027
live matrix. This proves which bytes were built and tested; it does **not** give
Windows an independently authenticated publisher identity. Local self-signing
may reduce Revit prompts on a machine whose owner explicitly trusts it, but it is
never used for public artifacts. See the repository
[unsigned release policy](../CODE-SIGNING-POLICY.md).

## Versioning

SemVer over the **tool contract**, not over the C#. What is public is the set of tool
names, their input schemas, and the shape of what they return.

- **MAJOR** — a tool is removed or renamed, a required argument is added, a returned
  field changes meaning, or a refusal that used to fire stops firing. Anything that
  makes a working client stop working, or keep working while meaning something else.
- **MINOR** — a new tool, a new optional argument, a new field in a reply, a new
  refusal for a case that previously did something undefined.
- **PATCH** — a fix that makes a command do what it already promised. **A command
  that begins refusing input it used to accept silently is a PATCH when the old
  behaviour was unverified** — the promise did not change, the honesty did.

Both halves inherit the same `<Version>` from `Directory.Build.props` and are released **together**. They share a
contract hash and refuse to pair across builds; there is no partial deployment, so
there is no such thing as a server version and an add-in version.

### Schema compatibility

- A field is never repurposed. A field that must change meaning gets a new name and
  the old one is deprecated.
- A deprecated tool, argument or field is announced in the CHANGELOG, kept working
  for **two MINOR releases**, and only then removed in the next MAJOR.
- `additionalProperties: false` stays on every input schema. It is what makes a
  typo a refusal instead of a silently ignored argument.
- `idempotency_key` is injected into every mutating tool's schema by the contract
  itself, so it can never be missing from one and present in another.

## Supported versions

- **Revit**: the years the current release's live matrix covers. A year without a
  published verification report is not supported, whatever the code compiles against.
- **Horizun**: the current MINOR, plus the previous one for security fixes. Older
  versions get the upgrade path, not a backport.
- **MCP protocol**: the versions in `SupportedMcpProtocols`. A revision still marked
  RC upstream is not adopted until the protocol layer is isolated behind an adapter
  (backlog 5.8) — the point of the adapter is that adopting one does not touch a
  single Revit command.

## Configuration migration

`%USERPROFILE%\.horizun\settings.json` is read with unknown keys **preserved**, so a
downgrade does not destroy a setting a newer version wrote. A key that changes
meaning gets a new name; the old one keeps working for two MINOR releases and the
installer says so once. `enable-execute-python.ps1` writes exactly its two keys and
leaves the rest alone, which is the pattern every future setting follows.

## What "production ready" will mean — the 1.0 checklist

1.0 is not a date and not a feature count. Every line below has to be true, and each
one is currently checkable rather than a matter of opinion:

- [x] Every typed write binds its confirmation to the **resolved element set**, not
      to the request; orchestrated children are re-rehearsed inside their group.
      *(backlog 5.1: source and focused live proof complete; release matrix still applies)*
- [ ] Every typed write whose verification fails **rolls back**, or documents in its
      own description exactly what it leaves behind. *(rule adopted in 0.6.0)*
- [ ] The live matrix passes for every supported Revit year, with the report
      published per release. *(5.5)*
- [ ] Operation **receipts**, with retention and redaction the operator controls.
      *(5.2)*
- [ ] The write tier of `verify-live.ps1` is green with **no NOT COVERED probes** on
      the release machine.
- [ ] MCP negotiation and standard primitives are implemented through 2025-11-25;
      official Inspector/SDK conformance and the client matrix remain. *(5.8)*
- [ ] No hardcoded classification anywhere: annotations and effects derived from the
      contract. *(5.3 — done)*
- [ ] Schemas frozen under the compatibility rules above, with the deprecation
      window written into the CHANGELOG.
- [x] Two maintainers with release rights. *(verified on the public repository on
      2026-08-20)*
- [x] GitHub secret scanning and push protection enabled on the public repository.
      *(verified on 2026-08-20)*
- [x] The permanent unsigned trust boundary is explicit in the bootstrap,
      package record and README; unexpected/invalid/self-signed public artifacts
      fail closed. See [production readiness](production-readiness.md).
- [x] Stable tags publish hashes, manifest, SBOM, attestations and the complete
      live matrix, then verify the exact installed unsigned bytes end to end.

Version 1.0 is promoted only from the tagged pipeline after every applicable box
and the live matrix are green. Unsigned is a disclosed trust boundary, not a
claim of Windows publisher authentication.
