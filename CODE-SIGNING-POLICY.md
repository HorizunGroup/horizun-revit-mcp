# Code signing policy

Horizun Revit MCP is free and open-source software released under Apache-2.0.
Every installable Windows release must use **free code signing provided by
[SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/)**.

## Current status

The SignPath Foundation application was submitted on 2026-08-15 and is awaiting
review. Legacy unsigned artifacts are not accepted by the release bootstrap:
automatic binary publication and release installation resume only after SignPath has issued
the public identity and a release is validly signed and timestamped. Source
installation remains available because it compiles locally and does not cross
this download trust boundary. A release may only claim SignPath signing after the
public signature gate has verified the exact published bytes on a clean Windows
runner.
Until then, only source and validation-only releases without binary assets are allowed.

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
- The intended SignPath GitHub integration will bind each signing request to the
  repository, workflow, commit and uploaded GitHub artifact. That request step is
  **not active yet** because the project, policy and credentials do not exist
  before acceptance. The current workflow therefore cannot publish binaries: a
  protected local-certificate signing path exists for development/integration
  testing, but it cannot satisfy the SignPath publisher gate and is not presented
  as an origin-verified public release path.
- Every stable release requires a complete live verification matrix for Revit
  2023 through 2027. Every installable release requires signing approval.
- CI generates a CycloneDX SBOM, full SHA-256 manifest, release checksums and
  GitHub build-provenance attestations.
- A disposable GitHub-hosted Windows runner verifies Authenticode public trust,
  trusted timestamps and the absence of self-signed own binaries before
  publication.
- A failed install, rollback or required live test blocks publication. A failed or
  missing signature or timestamp blocks every installable release; there is no
  unsigned binary exception. Validation-only tags carry no assets.

The detailed mechanical gates are documented in
[`docs/RELEASE-POLICY.md`](docs/RELEASE-POLICY.md).

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
