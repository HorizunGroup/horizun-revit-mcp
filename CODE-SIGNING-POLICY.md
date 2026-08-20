# Code signing policy

Horizun Revit MCP is free and open-source software released under Apache-2.0.
Every 1.0+ installable Windows release must use **free code signing provided by
[SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/)**.

## Current status

The SignPath Foundation application was submitted on 2026-08-15 and is awaiting
review. Official 0.x releases may carry an unsigned installer only with an
unmissable disclosure, a full SHA-256 manifest, the five-year live matrix and an
explicit `-AllowUnsigned` acknowledgement in the bootstrap. That verifies byte
identity, not publisher identity. Invalid or self-signed public artifacts remain
forbidden. A release may only claim SignPath signing after the public signature
gate has verified the exact published bytes on a clean Windows runner. Version
1.0 and later fail closed without that identity and timestamp.

## Scope

The signing project may sign only release artifacts built from
[`HorizunGroup/horizun-revit-mcp`](https://github.com/HorizunGroup/horizun-revit-mcp):

- the Horizun MCP server executable and its own assembly;
- the Horizun Revit add-in assembly compiled for Revit 2023 through 2027; and
- the installer that contains those binaries and their open-source runtime
  dependencies.

Autodesk Revit and its API assemblies are system dependencies. They are not
distributed or signed by this project. Third-party open-source binaries included
in the installer retain their own publisher and license identity and are not
re-signed as Horizun binaries.

## Build and release controls

- Source, build scripts and GitHub Actions workflows are public and versioned in
  the same repository.
- Release signing starts only from a protected `v*` tag whose version matches the
  product version in source.
- The tagged workflow requires separate GitHub runner groups for interactive
  Revit integration and packaging/signing. The signing runner needs the five
  locally installed Revit APIs but must not share the integration runner group.
  Group membership, protected environments and key provisioning are external
  controls and must be audited by an organization owner before stable release.
- SignPath Foundation's published OSS origin rules require every job leading to
  a signing request to use GitHub-hosted agents by default. The present build
  requires locally installed, non-redistributed Revit APIs, so separate runner
  groups reduce the persistent-runner risk but do not prove SignPath eligibility.
  Stable signing stays fail-closed until SignPath explicitly approves this origin
  design; the external decision is recorded by
  `SIGNPATH_SELF_HOSTED_ORIGIN_APPROVED=true` only after that approval.
- The pinned SignPath GitHub action binds each of the two signing requests to the
  repository, workflow, commit and uploaded GitHub artifact. The steps are active
  in the tagged release path but remain fail-closed until the external project,
  policies and credential are provisioned after acceptance. A protected
  local-certificate path remains only for development; it cannot satisfy the
  SignPath publisher gate and is never a public release fallback.
- Every stable release requires a complete live verification matrix for Revit
  2023 through 2027. Every 1.0+ installable release requires signing approval.
- CI generates a CycloneDX SBOM, full SHA-256 manifest, release checksums and
  GitHub build-provenance attestations.
- A disposable GitHub-hosted Windows runner verifies Authenticode public trust,
  trusted timestamps and the absence of self-signed own binaries before
  publication.
- A failed install, rollback or required live test blocks publication. A failed
  or malformed signature always blocks publication. Missing signatures are
  permitted only for disclosed 0.x releases; 1.0+ has no exception.

The detailed mechanical gates are documented in
[`docs/RELEASE-POLICY.md`](docs/RELEASE-POLICY.md).
The exact post-acceptance project values, two-request signing sequence and
acceptance test are defined in
[`docs/SIGNPATH-ONBOARDING.md`](docs/SIGNPATH-ONBOARDING.md).

## Team roles

The Horizun Group organization owners are publicly accountable for this policy:

- **Committers and reviewers:**
  [`@pablo-horizun`](https://github.com/pablo-horizun),
  [`@isabela-horizun`](https://github.com/isabela-horizun), and
  [`@daniela-horizun`](https://github.com/daniela-horizun).
- **Signing approvers:** the same organization owners. A stable signing request
  requires approval by an owner who has reviewed the release evidence.

All maintainers must use multi-factor authentication for GitHub and SignPath.
Signing configuration and workflow files are covered by repository CODEOWNERS.

## Privacy and system changes

Horizun does not collect analytics or transmit model data automatically. Network
operations happen only when the user explicitly requests them. The complete
policy is in [`docs/PRIVACY.md`](docs/PRIVACY.md).

The installer announces the paths, client configuration, scheduled completion
and uninstall behavior it changes. It never installs private trust roots for a
public release. Local self-signed development certificates are outside the
public release path and are created only by an explicit developer command.

## Reporting and revocation

Report a suspected signing-policy violation or compromised release privately as
described in [`SECURITY.md`](SECURITY.md). Maintainers will suspend publication,
preserve the affected evidence and work with SignPath Foundation to revoke the
signature or certificate when warranted.
