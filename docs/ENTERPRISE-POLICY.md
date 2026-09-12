# Applying a local enterprise policy

Horizun Revit MCP currently enforces local, machine-owner settings. This is not
a cloud administration service. The policy installer below makes that local
configuration repeatable and auditable while preserving the bridge's existing
fail-closed rules.

1. Copy [`../enterprise/settings-policy.example.json`](../enterprise/settings-policy.example.json).
2. Give the copy an organisation-owned `policy_id` and version.
3. Review `settings` with the BIM/IT owner. Only existing enforceable keys are
   accepted: `permission_profile`, `mcp_paused`,
   `force_read_only_on_workshared`, `enable_execute_python`, `allowed_tools`,
   and `denied_tools`.
4. Deploy it silently, for example:

   ```powershell
   pwsh -File scripts/apply-enterprise-policy.ps1 -PolicyPath C:\Policies\horizun.json -Yes
   ```

The script validates the policy before taking the shared settings lock, backs up
the existing settings file and atomically writes only the declared controls. It
does not accept a policy that enables Python: arbitrary-code access remains an
explicit local owner decision.

Use `-WhatIfOnly` to inspect a policy without modifying the machine. The bridge
re-reads its setting file on every request; compatible clients also receive a
tool-list change notification.
