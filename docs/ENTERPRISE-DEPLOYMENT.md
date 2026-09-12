# Enterprise Deployment Blueprint

This is the intended deployment architecture for multi-user AEC teams. It
separates what the current bridge already enforces from the control plane that
requires a centrally managed service; calling a roadmap item “available” would
misrepresent the product.

| Control | Current bridge | Enterprise delivery target |
| --- | --- | --- |
| Permission profiles | Local `read_only`, `safe_write`, `full_write`, `unsafe_code` | Centrally provisioned role policy with local visibility |
| Tool allow/deny lists | Local settings | Signed, centrally distributed policy |
| Model writes | Typed writes re-read after commit | Role and project approval records |
| Central/cloud models | Existing guards and explicit document targeting | Project policy that defaults sensitive models to audit-only |
| Audit evidence | Command results, local log, hashes and receipts | Optional Application Insights receipt stream, queryable in the organisation's Azure tenant |
| Installation | Setup installer and client registration | Repository-driven silent installer for Codex, Claude Code and ChatGPT Work; MSI only where endpoint management requires it |
| Standards | Explicit project inputs | Versioned company packs with approval and release history |

## Required design constraints

- A central policy must fail closed when it cannot be read; loss of connectivity
  must not turn an audit-only machine into a write-enabled machine.
- A role may narrow local permissions but may never silently elevate them.
- Every centrally recorded event needs model identity, document fingerprint,
  actor, tool, effective policy, result, coverage and verification state.
- A cloud or central model needs an explicit policy decision before any write;
  no generic “yes, edit it” default is acceptable.
- Secrets and customer standards never belong in MCP prompts, release artifacts
  or tool schemas.

## Delivery sequence

1. Define the policy schema and its signed/distributed storage model.
2. Add a read-only policy evaluator to the bridge and prove fail-closed behavior.
3. Add local receipts for policy decisions before sending any events externally.
4. Add a central receipt collector with durable retry and no model-write path.
5. Package the approved configuration with silent deployment, then produce the
   MSI only once its upgrade, rollback and uninstall semantics are specified.

Until those steps exist, the bridge should describe its permissions as
machine-local controls, not enterprise administration.
## Selected first implementation: Application Insights and repository installer

## Optional centralized operational receipts

For a small or medium BIM team, use **Azure Application Insights** as the first
central receipt destination. It provides role-based access, retention, queries
and alerts without operating an application server. It is **off by default**.
No Horizun-operated service receives data.

An Azure administrator creates an Application Insights resource and gives the
machine owner its connection string through an approved secret channel. On the
machine, run:

```powershell
pwsh -File scripts/configure-application-insights-logging.ps1 -ConnectionString '<Azure connection string>'
pwsh -File scripts/forward-receipts-to-application-insights.ps1
```

The configuration is protected with the current user's Windows DPAPI. The
forwarder transmits only allowlisted operational facts: tool name, success,
dry-run state, transaction status, verified flag, duration and a receipt hash.
It never sends model names, paths, element ids, prompts, arguments, usernames or
credentials. Run the forwarder from the organisation's existing user-level task
scheduler or endpoint-management mechanism; a failed send leaves local receipts
and its cursor unchanged.

## Installation from a repository

The supported baseline is the existing, signed-and-verified silent source
installer, not MSI. It compiles against the Revit versions on the machine and
registers Codex and Claude Code without replacing other MCP entries:

```powershell
git clone <approved Horizun repository URL>
Set-Location <cloned repository>
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

ChatGPT Work uses the same installed server through the Secure MCP Tunnel:

```powershell
pwsh -File scripts/chatgpt-tunnel.ps1 -Status
```

An MSI is appropriate only when endpoint management specifically requires its
own package format. It is not a prerequisite for repository-driven Codex,
Claude Code or ChatGPT Work installation.
