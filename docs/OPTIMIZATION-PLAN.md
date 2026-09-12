# Internal MCP optimization

Implemented in the requested order **4 → 2 → 3 → 1 → 5**, for this local PC.
No release, tag, upload or publication is part of this work.

## 4. Cross-version verification

`verify-live-matrix.ps1` rejects missing, stale, inconsistent or incomplete
version reports. Historical evidence cannot certify a different build.
`live-family.probes.ps1` creates a native RFA with two parameterized types,
loads it into a disposable RVT, inspects its saved version and independently
reads the loaded parameter values. Mock tests are separate from live evidence.

`verify-optimization-live.ps1` exercises the new typed behavior on Revit
2023–2027 through the existing disposable-fixture lifecycle runner. It verifies
installed binary hashes and source provenance, and accepts an explicitly
identified local working-tree build. It preserves the owner's Python setting.
Reports and call transcripts go under `artifacts/optimization/` (gitignored).
This focused suite is not the complete release certification suite.

```powershell
pwsh scripts/verify-local-optimizations.ps1 -Years 2026
```

Omit `-Years` to cover 2023–2027. This command only starts a local Revit,
tests explicitly disposable fixtures and closes its own process without saving.
The fixture map must already exist outside the repository. Existing Revit
sessions cause a refusal. No client deliverable is a test fixture.

## 2. Response efficiency

`horizun_query_model.response_mode` accepts `full` (unchanged default), `compact`
and `summary`. Compact supplies lean default fields and parameter formatting;
explicit projections remain authoritative. Summary omits element rows and
pagination while retaining whole-set counts and coverage findings. Incompatible
detail arguments are refused. Bounding boxes are read only for requested spatial
filtering or output. Optional view metadata enables template discovery.

Live probes compare counts, summaries, coverage and response bytes across modes.
Reported timings include transport overhead; they are not isolated API benchmarks.

## 3. Durable recovery

Call `horizun_health` first: async admission uses an immutable document identity
captured on Revit's UI thread, keeping admission off that thread. It records the
semantic request and document identity. Execution
requires a successfully persisted running event. Document changes before execution
are refused. Job status gives a recovery action instead of suggesting blind retries.

`resume_from_job_id` only replaces a job with durable proof it never started:
the owner must be dead or the record terminal `not_started`, arguments and document
must match, and the key must be `resume:<source-job-id>`. A refreshed confirmation
token is allowed. Running, completed, legacy, truncated or ambiguous records are
refused. Started writes are never automatically replayed. Plan checkpoints are
explicitly provisional until the transaction group commits.

## 1. Operations by intent

`horizun_execute_plan` accepts either raw `actions` or a declarative `workflow`:

- `pin_elements`: explicit element IDs.
- `apply_view_template`: explicit view IDs and template ID.
- `prepare_sheet_set`: explicit views, sheet numbers/names, title-block types,
  placement X/Y points (optional Z must be zero) and optional templates.

Workflows expand deterministically into existing typed actions. They retain
document checks, dry-run confirmation, group rollback and post-commit verification.
No organization-specific standards or guessed targets are embedded.
Post-commit checks include sheet names/numbers, title-block types, viewport
view/sheet identities and planar position. A dictionary-serialization defect
that could throw after successful group commit is also corrected and regression-tested.

## 5. Change previews

Dry runs expose `change_preview` with target IDs, captured state, proposed values
and the full resolved-plan fingerprint. Parameter writes, transforms and template
assignment supply concrete values; unsupported fields remain null. Preview output
is bounded (50 rows / 32 KiB), with explicit truncation. Confirmation still binds
the complete values and all targets, including those omitted from the preview.

## Verification commands

```powershell
dotnet test tests/Horizun.Core.Tests/Horizun.Core.Tests.csproj -c Release
dotnet test tests/Horizun.Server.Tests/Horizun.Server.Tests.csproj -c Release `
  -p:RuntimeFrameworkVersion=8.0.28 -p:RestoreLockedMode=true
pwsh scripts/live-matrix.tests.ps1
pwsh scripts/live-family.tests.ps1
```

The Server override uses this PC's installed shared runtime for source tests.
It does not change the production self-contained runtime pin (8.0.30).
Final observed results are recorded in `artifacts/optimization/`; build success
alone is never claimed as live Revit verification.

Observed local validation on 2026-09-06 (Bogotá): **935 Core tests, 352 Server
tests and 35 live checks passed** (7 each on Revit 2023–2027). All five native
RFA probes passed. Installed binary hashes were reconciled against every report.
The local consolidated evidence is `artifacts/optimization/INFORME-LOCAL.md`
and `verified-local-summary.json`. This was an explicitly local working-tree
installation, with no release or publication.
