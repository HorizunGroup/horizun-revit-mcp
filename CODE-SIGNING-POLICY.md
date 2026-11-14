# Release signing policy

Horizun Revit MCP is free and open-source software released under Apache-2.0.
From v4.0.1 **every binary a release installs and its Setup are signed** with the
Certum code-signing certificate of HORIZUN GROUP S.A.S., timestamped by Certum:
the MCP server (`horizun-mcp.exe` and `horizun-mcp.dll`), the Revit add-in of
every supported year (`Horizun.Revit.dll`), the unsigned third-party libraries
they load (IronPython, the Dynamic Language Runtime and PdfPig), the Setup, its
uninstaller and the temporary copy of itself that Setup runs from `%TEMP%`.
Third-party files that already carry their publisher's valid signature (the .NET
runtime, Newtonsoft.Json) are shipped as their publisher signed them.

Windows Smart App Control blocks any unsigned binary it is asked to load, which
is why signing only the Setup (v3.0.2 to v4.0.0) was not enough: the server and
the add-in it installed were refused at run time.

## Trust statement

A public release must never describe an invalid, expired, privately trusted or
self-signed signature as public trust. Every Horizun-owned binary and Setup must
read back as `Valid`, signed by `O=HORIZUN GROUP S.A.S.`, with a Certum issuer
and a Certum timestamp; every other signable binary must read back as `Valid`.
Any other state blocks publication (`scripts/verify-release.ps1 -RequireSigned`).

Releases before v3.0.2 have an unsigned Setup, and releases before v4.0.1 an
unsigned server and add-ins. The public bootstrap still accepts an unsigned Setup
only with an explicit `-AllowUnsigned` acknowledgement; for a signed Setup the
flag changes nothing.

## How a release is signed (v4.0.1 onwards)

Sign, then wrap. CI builds the stage on the licensed Revit runner, unsigned
(`authenticode: unsigned_by_policy` is still what `build-stage` requires of its
own output). The `compile-installer` job then runs on the dedicated signing
runner, waits for a manual approval of the `firma-certum` environment, signs
every signable binary of the stage through the local signing queue
(`scripts/firmar-cola.ps1`, SimplySign kept connected by a person), recomputes
the payload manifest, and compiles Setup with Inno Setup's sign tool so that
Setup, the uninstaller and the temporary self-copy are signed as they are
written. Every hash, SBOM entry and attestation downstream describes the signed
bytes.

A stable release is created as a **draft**, because a published GitHub release
is immutable. The `firmar-y-publicar` job (second `firma-certum` approval)
re-verifies every signature of the draft's assets, re-reads what it uploaded and
only then publishes. If any step fails, nothing is published. See
[`docs/RELEASING.md`](docs/RELEASING.md).

## Release controls

Every installable stable release must:

- originate at a protected `vX.Y.Z` tag matching the version in source;
- be published only after every binary and the Setup are signed and verified (draft first);
- build the server and distinct add-ins against Revit 2023 through 2027;
- name one full clean commit in every owned binary and in `manifest.json`;
- publish a full SHA-256 payload manifest, `SHA256SUMS.txt`,
  `package-hashes.json`, a CycloneDX SBOM and build-provenance attestations;
- pass the exact installer, rollback and installed-byte verification chain;
- pass the complete live verification matrix for every supported Revit year;
- disclose `authenticode: signed_by_certum` and
  `publisher_identity_available: true` in the package record
  (`release_signing: payload_and_setup_signed_by_certum_in_ci`); and
- require `-AllowUnsigned` in the public bootstrap for any Setup that is not
  signed by HORIZUN GROUP S.A.S.

Hashes downloaded from the same GitHub release detect corruption or inconsistent
assets, GitHub attestations bind the published subjects to the workflow run, and
the Authenticode signatures name the publisher to Windows. Users who
need stronger assurance should verify the protected tag, workflow attestation
and source commit in addition to the checksums.

The detailed mechanical gates are documented in
[`docs/RELEASE-POLICY.md`](docs/RELEASE-POLICY.md).

## Local Revit trust

Source installs may create or reuse a local self-signing certificate only after
the machine owner explicitly chooses that workflow. Its sole purpose is to avoid
repeated Revit unsigned-add-in prompts for a source build on that machine. It is local trust, is not
a public publisher identity, and is never used to build a public release.

## Team accountability

The Horizun Group organization owners are publicly accountable for releases:

- [`@pablo-horizun`](https://github.com/pablo-horizun)
- [`@isabela-horizun`](https://github.com/isabela-horizun)
- [`@daniela-horizun`](https://github.com/daniela-horizun)

All maintainers must use multi-factor authentication for GitHub. Release policy
and workflow files remain covered by repository CODEOWNERS.

## Privacy and system changes

Horizun does not collect analytics or transmit model data automatically. Network
operations happen only when the user explicitly requests them. The complete
policy is in [`docs/PRIVACY.md`](docs/PRIVACY.md).

The installer announces the paths, client configuration, scheduled completion
and uninstall behavior it changes. It never installs a trust root as part of a
public release.

## Incident response

Report a suspected release-policy violation or compromised artifact privately as
described in [`SECURITY.md`](SECURITY.md). Maintainers will suspend publication,
preserve the affected evidence and replace any compromised release assets.
