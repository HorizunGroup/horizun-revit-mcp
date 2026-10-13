# Interoperability — exchange manifests, model identity and the correction cycle

Horizun Revit MCP is one of several Horizun servers (Navisworks, MS Project, Power BI, the PDF
review). They **never call each other**: the agent in the MCP client is the only bus, and it passes
**paths to manifests** between them, never large JSON inline. Everything on this page answers
without Revit except `get_document_info`, which reads the evidence a model's identity rests on.

## The manifest: `horizun.exchange/0`

Every hop between apps leaves one manifest beside a copy of its payload:

```
<exchange_root>/<kind>/<exchange_id>/exchange.hzx.json
<exchange_root>/<kind>/<exchange_id>/<payload files>
```

It **wraps** a format that already exists (the coordination ledger CSV/JSON, BCF 2.1, the NavisCoord
hand-off, quantity rows, MSPDI, IFC with the BCF of IDS failures) — it never replaces it. The schema is
[`schemas/horizun.exchange.v0.schema.json`](../schemas/horizun.exchange.v0.schema.json):

```json
{
  "schema": "horizun.exchange/0",
  "exchange_id": "hzx-20261012T163000Z-3f9a1c2e",
  "kind": "coordination.ledger",
  "correlation_id": "run-17",
  "producer": { "app": "horizun-revit", "version": "3.0.0", "commit": "…" },
  "project": { "code": "P01" },
  "source": { "model_ref": "mdl-1a2b3c4d5e6f", "model_evidence": { "creation_guid": "…", "central_guid": null } },
  "spatial_ref": { "frame": "shared", "length_unit": "mm" },
  "files": [ { "path": "ledger.csv", "format": "horizun.coordination-ledger.csv/2", "sha256": "…", "bytes": 1234, "rows": 42 } ],
  "verification": { "state": "self_reported_verified", "method": "wrapped_by_horizun_exchange", "host_verified": false },
  "caveats": [ "what this file does NOT prove" ]
}
```

- **Order.** Payload first — each file copied under a temporary name, moved into place without
  overwriting, re-hashed — and the manifest **last**. A folder with payload and no manifest, or a
  manifest whose payload has not arrived (synced folders deliver in either order), is **not
  delivered**: never an error, never consumed.
- **Honesty.** `verification.state` is `verified`, `self_reported_verified`, `partial` or `failed`.
  `verified` needs `host_verified: true` — only a producer that re-read its own output in the host may
  claim it. A hop through Python, or a wrap made by `horizun_exchange`, is at most
  `self_reported_verified`. A chain is as strong as its weakest hop; `partial` or `failed` stops it.
- **Root.** Local by default: `%USERPROFILE%\.horizun\exchange\<project code>` (under
  `HORIZUN_DATA_ROOT` when that is set). Publishing to a CDE is a separate, explicit ISO 19650 step
  (`horizun_information_container`), never the place a hop writes to.

### `horizun_exchange`

| Operation | What it does |
|---|---|
| `emit` | Wraps existing payload files into a hop. `dry_run` defaults to true and returns the manifest that would be written. With `dry_run=false` (needs `full_write`) it copies, re-hashes, writes the manifest last and re-verifies the exchange before saying `written`. `model_ref` copies the registered model's evidence into `source`. |
| `verify` | The consumer's check: `delivery`, `integrity` (sha256 per file), `producer_state` and `effective_state`, `identity` against the project registry (`proven`, `unproven`, `mismatch`, `unknown_model_ref`), and `may_continue`. |
| `correction_plan_check` | Takes horizun-review's `plan_correcciones_revit` plan: which steps may run (`ready`), which still carry `<…>` placeholders (`requires_input`, each with its pointer and what resolves it), which are refused (`not_permitted`: Python, unpublished tools, a step that does not rehearse). |
| `correction_plan_reverify` | After a person approved and each step was applied with its rehearsal's token: per target, whether the model now carries what the **plan** asked — `corrected`, `confirmed_by_reread_only`, `persistent`, `not_applied`, `not_verifiable` — rolled up per step and overall, plus the re-review still owed. |

## Model identity (decision D0) and the project registry

A `model_ref` is an id the project registry assigns. The registry is `project-integrations.json`, beside
`project-context.json` (whose v1 schema is closed). It binds each model to **strong evidence**, in this
order — the first key both sides carry decides:

1. `creation_guid` — `Document.CreationGUID` (Revit 2024+);
2. `central_guid` — `Document.WorksharingCentralGUID` (server-based centrals);
3. `cloud_model_guid` — the cloud model's GUID;
4. `unique_document_guid` — the file header's Unique Document GUID (accepted; not read by the add-in yet).

`project_info_unique_id` is **weak**: every project made from one template inherits it, so it is recorded
and never matches by itself. **Evidence read on Revit 2023 is always `identity_proven: false`.** An
element's identity is (`model_ref`, `UniqueId`) plus its placement; `<link>::<uid>` is presentation only.

- `get_document_info` publishes `model_identity` — the evidence, its `binding_key`, `identity_proven` and
  `identity_note`.
- `horizun_project_context operation=integrations` reads the registry and the effective exchange root.
- `register_model` (`dry_run` defaults to true) registers `{name, discipline, evidence}` and assigns
  `mdl-…`; it refuses a model with no strong key and a second model with the same binding evidence.
- `resolve_model` matches observed evidence to a `model_ref`; it refuses to match without strong evidence
  or against an ambiguous registry. The caller's `binding_key` and `identity_proven` are ignored and
  recomputed.

## The review → correction → re-review cycle

1. horizun-review reviews the PDF set and `plan_correcciones_revit` proposes typed steps (`dry_run=true`).
2. `horizun_exchange correction_plan_check` says what may run and what must be resolved first.
3. Pass the resolved plan as `correction_plan` to `horizun_apply_corrections`: every step rehearses
   through its typed tool; one token binds the document, plan and each child's resolved model facts.
   After approval the add-in re-rehearses, checks the token, applies per step, then freshly reads ALL
   targets after the final step. A later step overwriting an earlier one is persistent. Supported:
   explicit parameter writes and revision actions without clouds; other operations need separate typed calls.
4. `correction_plan_reverify` reconciles the applied arguments and replies with the plan — the plan's own
   targets and values, not a count of calls. It re-derives from the replies the bridge re-read in Revit;
   this server operation does not read the model again, and is at most `self_reported_verified`.
   The add-in's fresh-read verdict is independent: `verified` requires `host_verified:true`.
5. Re-export the sheets and run horizun-review again: only its delta closes a PDF finding.

## The prompt `cross-app-coordination`

`prompts/get cross-app-coordination` (arguments `objective`, optional `integrations_path`) walks the agent
through: preflight of the peers (their health tools, duplicates flagged, the registry read, identities
resolved), one intake for the whole chain (WHAT / WHICH / HOW, one `correlation_id`), approval per stage,
execution in owner order (Revit → Navisworks → MS Project → Power BI → CDE), verification in three layers
(producer re-read, consumer `verify`, cross-app invariants) and a closing row per hop with the weakest state
as the global one. Reloading a re-exported NWC in Navisworks stays manual.

## What is not proven yet

The add-in half (`get_document_info model_identity`) compiles for Revit 2023–2027 but has not been run
against a Revit: `scripts/live/verify-interop-identity.ps1` is the probe for the final matrix;
`scripts/live/verify-interop-corrections.ps1` tests rehearsal, apply, fresh reads and replay on an
approved disposable fixture. Both are **pending live**. The other
servers' halves require their own implementation and verification before a cross-app chain is claimed as complete.
