# Direct Power BI connection


`horizun_power_bi_push` uses Microsoft's push semantic-model REST endpoint; it
does not automate Power BI Desktop. Credentials are configured in the
environment of the MCP server, never in a tool call:

```powershell
# Option A: short-lived OAuth access token
$env:HORIZUN_POWER_BI_ACCESS_TOKEN = '<token with Dataset.ReadWrite.All>'

# Option B: Entra service principal; Horizun obtains the access token
$env:HORIZUN_POWER_BI_TENANT_ID = '<tenant-guid>'
$env:HORIZUN_POWER_BI_CLIENT_ID = '<application-guid>'
$env:HORIZUN_POWER_BI_CLIENT_SECRET = '<secret>'
```

The destination is fixed to `api.powerbi.com`; dataset and workspace ids must be
GUIDs; values are primitive JSON only; the union is limited to 75 columns,
strings to 4,000 characters and each call to 10,000 rows, following Microsoft's
[push semantic-model limitations](https://learn.microsoft.com/power-bi/developer/embedded/push-datasets-limitations).
Run with the default `dry_run: true`, then apply with a new `idempotency_key`. An
identical retry replays the stored answer; a connection loss after upload is
reported `in_doubt` and is never sent again automatically.
