# Code signing policy

Horizun Revit MCP is free and open-source software released under Apache-2.0.
Windows releases from 1.0.0 onward must use **free code signing provided by
[SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/)**.

## Current status

The SignPath Foundation application was submitted on 2026-08-15 and is awaiting
review. Horizun 0.9.0 is intentionally released unsigned and its download page
must identify it that way. Windows and Revit may therefore display an unknown
publisher warning. A release may only claim SignPath signing after the public
signature gate has verified the exact published bytes on a clean Windows runner.
Version 1.0.0 and every later release is blocked unless that gate passes.

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
- The unsigned signing input must be built on GitHub-hosted runners from the
  tagged commit. The separate self-hosted Revit runner may consume and test the
  signed package, but it is not an origin for SignPath signing input.
- SignPath origin verification binds each request to the repository, workflow,
  commit and GitHub artifact.
- Every release requires a complete live verification matrix for Revit 2023
  through 2027. Releases from 1.0.0 onward additionally require human signing
  approval.
- CI generates a CycloneDX SBOM, full SHA-256 manifest, release checksums and
  GitHub build-provenance attestations.
- A disposable GitHub-hosted Windows runner verifies Authenticode public trust,
  trusted timestamps and the absence of self-signed own binaries before
  publication.
- A failed install, rollback or live test blocks every release. From 1.0.0 onward,
  a failed or missing signature or timestamp also blocks release and there is no
  unsigned exception. The only temporary exception is a correctly disclosed 0.x
  artifact whose own binaries are all verified as unsigned on a clean runner.

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
