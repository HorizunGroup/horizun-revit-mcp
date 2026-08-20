# SignPath onboarding runbook

This runbook is the hand-off between SignPath Foundation acceptance and the
first installable Horizun release. It deliberately contains no credential and
does not provide a local-certificate fallback. Until every value below exists,
the tagged workflow must fail before publishing executable assets.

## Why there are two signing requests

The installer contains the Revit add-ins and MCP server. Signing only the outer
Setup executable does not sign the binaries Revit and Windows actually load.
The release workflow therefore performs this immutable sequence:

1. build and upload the staged payload as a GitHub artifact;
2. ask SignPath to sign only the seven Horizun-owned PE files in that payload;
3. recompute `manifest.json` from those signed bytes;
4. compile the installer from the signed payload;
5. upload the installer as a second GitHub artifact and ask SignPath to sign it;
6. verify all eight signatures and timestamps on a clean GitHub-hosted Windows
   runner before installation or publication.

Third-party assemblies keep their original publisher identity and are never
re-signed as Horizun code.

## Values provisioned after acceptance

Repository variables:

| Variable | Purpose |
| --- | --- |
| `SIGNPATH_ORGANIZATION_ID` | SignPath organization identifier |
| `SIGNPATH_PROJECT_SLUG` | Project linked to the public GitHub repository |
| `SIGNPATH_PAYLOAD_POLICY_SLUG` | Release policy for the internal payload |
| `SIGNPATH_PAYLOAD_ARTIFACT_CONFIGURATION_SLUG` | ZIP configuration described below |
| `SIGNPATH_INSTALLER_POLICY_SLUG` | Release policy for Setup |
| `SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG` | Single-Setup configuration |
| `SIGNPATH_SELF_HOSTED_ORIGIN_APPROVED` | Exactly `true`, only after written SignPath approval |

Repository environment secret in `release-signing`:

| Secret | Purpose |
| --- | --- |
| `SIGNPATH_API_TOKEN` | Token scoped only to submitting these two policies |

The `release-signing` environment must require a reviewer other than the
workflow initiator and permit only `v*` tags. The public repository must retain
secret scanning, push protection, protected `main`, and two maintainers with
release rights.

## SignPath project configuration

Link the project to SignPath's predefined GitHub.com Trusted Build System and
install the SignPath GitHub App for
`HorizunGroup/horizun-revit-mcp`. Enable origin verification on both release
policies.

The payload artifact configuration has a `<zip-file>` root and signs exactly:

- `server/horizun-mcp.exe`;
- `server/horizun-mcp.dll`; and
- `plugin/2023/Horizun.Revit.dll` through
  `plugin/2027/Horizun.Revit.dll`.

It must reject missing, additional or differently located Horizun-owned PE
files. The installer configuration accepts exactly one
`horizun-mcp-*-setup.exe`. Both policies use the SignPath Foundation release
certificate, SHA-256 Authenticode and a trusted timestamp. They restrict origin
to this repository's protected release tags and require the approval policy
agreed with SignPath Foundation.

SignPath's OSS connector normally requires every upstream job to use a
GitHub-hosted agent. Horizun compiles against locally installed Autodesk Revit
API assemblies, which Autodesk does not publish as an official NuGet reference
package. Do not set `SIGNPATH_SELF_HOSTED_ORIGIN_APPROVED=true` unless SignPath
has approved this exact licensed, self-hosted build origin in writing.

## Acceptance test

1. Configure the six variables and the environment secret.
2. Audit the distinct `REVIT_RUNNER_GROUP` and `SIGNING_RUNNER_GROUP` membership.
3. Create a protected `vX.Y.Z-preview.N` tag from a reviewed commit.
4. Approve the `release-signing` environment from a different maintainer.
5. Preserve both SignPath request URLs, the GitHub artifact attestations and the
   clean-runner Authenticode report.
6. Install that exact preview on a disposable Windows account and run
   `scripts/verify-release.ps1 -Installed` without `-AllowUnsigned`.

A request URL, a successful signature check and an exact published-asset
inventory are all required. Any one of them alone is insufficient evidence.
