# Release policy — channels, versioning, and what "production ready" will mean

Written because v0.6.1 shipped a fix with **no installer** while v0.6.0 remained the
installable release. That was deliberate and correct, and it was also confusing:
`install-release.ps1` follows `latest` and downloads its assets, so a code-only tag
marked `latest` would break every script that installs the product. The behaviour
was right; the written rule was missing. This is the rule.

## Three channels

| Channel | What it means | Has binaries | Can be `latest` |
|---|---|---|---|
| **stable** | Live matrix approved/published for every supported Revit year; security gates green; payload and installer signed by a publicly trusted publisher | yes | **yes** |
| **preview** | New behaviour, fixtures partial, live verification incomplete or single-machine | yes, marked pre-release | no |
| **validation-only** | Tests, harness, docs or CI. **No new binaries.** | no | **never** |

`latest` **always** points at the newest **stable** release that carries an
installer. A preview can carry binaries but remains marked pre-release, so the
default bootstrap cannot silently move stable users onto incomplete live evidence.
A validation-only tag exists so the code change has a name and a diff, not so
anybody installs it — v0.6.1 is the reference example.

Every release, whatever the channel, publishes: `manifest.json` (the commit and a
SHA-256 per payload), `sbom.json` (every redistributed component with a named
licence) and `SHA256SUMS.txt`. A stable release additionally publishes the live
verification report for each Revit year it claims.

The repository's generic secret patterns always run in hosted CI; the private
client/project wordlist is mandatory on the release runner. GitHub secret scanning
and push protection are repository settings, not files in this tree, and must also
be enabled before stable promotion. A green custom scan does not claim that those
platform controls are on.

Signing and public trust are separate facts. A self-signed or privately trusted
certificate can prove byte identity in a controlled environment but does not make
Windows trust the publisher on a clean machine. Preview builds state whether the
payload and wrapper are unsigned, self-signed, or publicly trusted. A **stable tag
has no unsigned exception**: CI requires a publicly trusted, non-self-signed
Authenticode identity, signs the staged Horizun binaries and the installer wrapper,
timestamps them, re-validates public trust on a disposable Microsoft-hosted Windows
runner, and verifies the installed bytes without `-AllowUnsigned`.
Missing identity, signature, timestamp or trust fails publication before a release
can become `latest`.

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

- [ ] Every typed write binds its confirmation to the **resolved element set**, not
      to the request — and any command that does not says so in its own reply.
      *(backlog 5.1: mechanism landed, per-command wiring outstanding)*
- [ ] Every typed write whose verification fails **rolls back**, or documents in its
      own description exactly what it leaves behind. *(rule adopted in 0.6.0)*
- [ ] The live matrix passes for every supported Revit year, with the report
      published per release. *(5.5)*
- [ ] Operation **receipts**, with retention and redaction the operator controls.
      *(5.2)*
- [ ] The write tier of `verify-live.ps1` is green with **no NOT COVERED probes** on
      the release machine.
- [ ] The protocol layer is isolated and conformance-tested against the official SDK.
      *(5.8)*
- [ ] No hardcoded classification anywhere: annotations and effects derived from the
      contract. *(5.3 — done)*
- [ ] Schemas frozen under the compatibility rules above, with the deprecation
      window written into the CHANGELOG.
- [ ] Two maintainers with release rights.
- [ ] GitHub secret scanning and push protection enabled on the public repository.
- [ ] Payload and installer carry a timestamped, publicly trusted Authenticode
      signature, and the stable tag pipeline verifies the installed signatures
      without an unsigned exception.

Until every box is ticked, this ships as 0.x and says so. A 1.0 that means "we
think it is good now" is the claim this project exists not to make.
