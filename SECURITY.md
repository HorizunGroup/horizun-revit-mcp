# Security policy

## Supported versions

Security fixes target `main`, the current release channel and, when practical,
the previous MINOR release. A Revit year is supported by a stable release only
when that release publishes its live verification report for the year.

## Reporting a vulnerability

Do not open a public issue for a vulnerability, exposed credential, or report
containing client/model data. Use GitHub private vulnerability reporting for
this repository. Include the affected version or commit, Revit year, MCP client,
reproduction steps and impact after removing project names, paths, tokens and
credentials.

If private reporting is unavailable, contact Horizun Group through
[Horizun Hub](https://horizunhub.com) and request a private security channel.
Do not send secrets in the first message.

## Scope and known limitations

The detailed trust boundaries, permission profiles, local transport,
idempotency model and Python fallback are in
[docs/security-model.md](docs/security-model.md). `horizun_execute_python` is
disabled by default; when an owner explicitly enables it, it executes arbitrary
code as the signed-in user, its output is self-reported and `host_verified` is
always false. Release notes state the
actual signature/trust status, and users should verify the published SHA-256.
