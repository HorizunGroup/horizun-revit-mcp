# Unsigned release policy

Horizun Revit MCP is free and open-source software released under Apache-2.0.
Public Windows releases are intentionally **unsigned**, including version 1.0
and later. The project does not claim a Windows publisher identity.

## Trust statement

An unsigned release can prove byte identity and source provenance, but it cannot
make Windows authenticate the publisher. Every installation therefore requires
an explicit `-AllowUnsigned` acknowledgement. Windows and Revit may display an
unknown-publisher or unsigned-add-in warning.

A public release must never describe an invalid, expired, privately trusted or
self-signed signature as public trust. The release pipeline requires every
Horizun-owned PE file and Setup to be exactly `NotSigned`; any other
Authenticode state blocks publication.

## Release controls

Every installable stable release must:

- originate at a protected `vX.Y.Z` tag matching the version in source;
- build the server and distinct add-ins against Revit 2023 through 2027;
- name one full clean commit in every owned binary and in `manifest.json`;
- publish a full SHA-256 payload manifest, `SHA256SUMS.txt`,
  `package-hashes.json`, a CycloneDX SBOM and build-provenance attestations;
- pass the exact installer, rollback and installed-byte verification chain;
- pass the complete live verification matrix for every supported Revit year;
- disclose `authenticode: unsigned_by_policy` and
  `publisher_identity_available: false` in the package record; and
- require `-AllowUnsigned` in the public bootstrap.

Hashes downloaded from the same GitHub release detect corruption or inconsistent
assets, while GitHub attestations bind the published subjects to the workflow
run. Neither substitutes for an independently authenticated publisher. Users who
need stronger assurance should verify the protected tag, workflow attestation
and source commit in addition to the checksums.

The detailed mechanical gates are documented in
[`docs/RELEASE-POLICY.md`](docs/RELEASE-POLICY.md).

## Local Revit trust

Source installs may create or reuse a local self-signing certificate only after
the machine owner explicitly chooses that workflow. Its sole purpose is to avoid
repeated Revit unsigned-add-in prompts on that machine. It is local trust, is not
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
