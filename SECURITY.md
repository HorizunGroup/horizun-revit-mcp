# Security policy

## Supported version

Security fixes are applied to the current stable release and `main`. Older
releases may be used for comparison, but are not maintained independently.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability, credential exposure or a
report that contains client/model data. Use GitHub's **Report a vulnerability**
private-reporting form for this repository. Include the affected version or
commit, Revit year, MCP client, reproduction steps, impact and any relevant logs
after removing project names, model names, paths, tokens and credentials.

If private vulnerability reporting is temporarily unavailable, contact Horizun
Group through [Horizun Hub](https://horizunhub.com) and request a private security
channel. Do not send secrets in the first message.

## Scope and known limitations

The detailed trust boundaries, permission profiles, idempotency guarantees,
local transport model, Python escape hatch and unsigned-build limitation are in
[docs/security-model.md](docs/security-model.md). The optional release installer
is currently unsigned; verify its SHA-256 against the file shipped in the same
GitHub release. The recommended Codex workflow builds from public source locally.

We will acknowledge a complete report as soon as practical, investigate it
privately and coordinate disclosure after a fix or mitigation is available.
