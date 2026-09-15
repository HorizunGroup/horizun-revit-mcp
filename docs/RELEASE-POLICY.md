# Release policy — channels, versions and required evidence

**Historical context:** v0.6.1 shipped a fix with **no installer** while v0.6.0 remained the
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
- **MCP protocol**: the versions in `ProtocolNegotiation.Supported`. The current
  implementation supports 2025-11-25 and the earlier revisions listed there.
  Support for 2026-07-28 is pending and must be implemented and tested before
  it is advertised.

## Configuration migration

`%USERPROFILE%\.horizun\settings.json` is read with unknown keys **preserved**, so a
downgrade does not destroy a setting a newer version wrote. A key that changes
meaning gets a new name; the old one keeps working for two MINOR releases and the
installer says so once. `enable-execute-python.ps1` writes exactly its two keys and
leaves the rest alone, which is the pattern every future setting follows.

## Release evidence and remaining work

Release approval is evaluated for each tag. Consult its published assets and
workflow, rather than treating a permanent Markdown checkbox as a live status.

| Requirement | Evidence to inspect for that release |
|---|---|
| One version and source identity | Directory.Build.props, tag, binary stamps and manifest |
| Installed payload integrity | SHA-256, package-hashes.json and installed-byte verification |
| Revit compatibility | Complete live reports for each supported year, with failed/unverified/not-covered counts |
| Dependency inventory and trust | SBOM, attestations and the explicit unsigned policy |
| Client installation | Client-specific steps and recorded connection state; a live model test alone does not prove a clean client installation |
| Registry publication | Generated metadata matching the tag; any package entry must identify a real artifact with its measured hash and prerequisites |
| Release notes | Installation changes, remaining client actions, known limits and evidence links |

### Maintaining distribution metadata

When changing `Directory.Build.props`, regenerate the tracked source identity:

```powershell
pwsh scripts/generate-mcp-manifest.ps1 -OutFile .mcp/server.json
```

That file identifies the source version; it cannot promise a downloadable package
before one has been built. CI checks it for drift. During packaging,
`scripts/prepare-release-metadata.ps1` exports the exact staged `.mcpb`, writes
`dist/server.json` with its measured SHA-256 and tagged asset URL, and generates
release notes from that version's CHANGELOG section. The extension is an
installed-server connector: the Windows installer remains a prerequisite.

Stable and preview releases publish the extension and release metadata alongside
Setup. Only stable publication updates the official registry, using the same
immutable package artifact. These pipeline changes take effect on the next
release; editing the source metadata does not alter an already published entry.
The package format follows the [official registry MCPB documentation](https://modelcontextprotocol.io/registry/package-types#mcpb-packages).

The pre-1.0 checklist is retained in Git history. It is not the current state of
every later release. Historical development checkpoints remain labelled in
[production-readiness.md](production-readiness.md).

Work still requiring separate evidence includes MCP 2026-07-28 support, a
clean-machine client-installation matrix and common-fixture comparisons against
other products. Public Authenticode identity is not claimed under the permanent
unsigned policy. None of these facts should be hidden by a design-rubric score.
