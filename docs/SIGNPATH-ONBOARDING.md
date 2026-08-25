# SignPath onboarding — retired

This runbook is retained only to document a superseded release design.

On 2026-08-25 the owner decided that Horizun public Windows releases will remain
unsigned, including version 1.0 and later. The tagged workflow no longer submits
SignPath requests, consumes SignPath credentials, or waits for a public signing
identity.

The active controls are defined in:

- [the unsigned release policy](../CODE-SIGNING-POLICY.md);
- [the executable release policy](RELEASE-POLICY.md); and
- [production readiness](production-readiness.md).

Do not provision `SIGNPATH_*` variables or credentials for this repository.
Local self-signing remains an explicit machine-owner convenience for Revit trust;
it is not part of public release production.
