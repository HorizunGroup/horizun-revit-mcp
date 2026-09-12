# BIM Quick Start

This path gets a BIM coordinator from a connected Revit model to a useful,
read-only result. It changes neither the model nor files outside it.

For release evidence, use [RELEASE-VALIDATION.md](RELEASE-VALIDATION.md). The
live Revit check belongs at the end, after code and installation work are complete.

1. Install Horizun Revit MCP and start Revit with the intended model open. In
   Revit, use **Horizun Hub > Estado de conexión** (Connection status); it must say that the bridge is
   active.
2. In the MCP client, call `horizun_health`. Confirm its active document is the
   exact model you intend to inspect.
3. Run the ready-made **read-only-audit** prompt, or ask:

   ```text
   Audit the active model without writes. First confirm the active document,
   then run a model scan and a pre-delivery audit. Separate measured findings,
   unknown coverage and suggested next actions.
   ```

4. If a company or project standard applies, review and provide it explicitly.
   The optional examples in [`../standards/`](../standards/) are generic
   starting points, not hidden policy.
5. Review the evidence. Only then choose a correction. Every typed correction
   must be rehearsed first and re-read after commit; keep `dry_run: true` until
   the affected elements and expected result are clear.

## Permission modes

| Mode | Intended use | What it can change |
| --- | --- | --- |
| `read_only` | Audits, discovery and evidence | Nothing in the model or filesystem |
| `safe_write` | Verified edits inside the active model | Typed, reversible model edits only |
| `full_write` | Approved delivery outputs | Also document-session actions and external exports |
| `unsafe_code` | Owner-approved exceptional automation | Adds eligibility for Python; it is never the default |

Use the lowest mode that can accomplish the work. The mode is a machine-owner
control, not something a client or prompt may silently elevate.

## What a good request contains

Before a write, name all three: the intended outcome, the exact elements in
scope, and how the result will be recognised as correct. For example:

```text
For the active document only, rehearse a batch update of parameter `Type Mark`
to `D-101` for these explicit door element ids. Do not apply it. Report every
resolved target, any missing or read-only parameter, and the verification that
would run after commit.
```

For the complete tool reference, including every refusal, see
[TOOLS.md](TOOLS.md).
