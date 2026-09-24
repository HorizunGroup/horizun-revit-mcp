# Live probe modules

`scripts/verify-live.ps1` loads every `*.probes.ps1` in this folder, in name order,
AFTER the ISO 19650 section and inside the write tier (the disposable document is
active). A module adds itself to the list `$script:HzProbeModules`:

```powershell
$script:HzProbeModules += [pscustomobject]@{
    Name    = 'groups'                       # short, unique
    # Every case the module CAN report, so a module that throws is recorded as
    # 'unverified' case by case instead of silently missing.
    Catalog = @(
        @{ Name = 'groups: create a model group from elements and re-read its members'; Tool = 'horizun_manage_groups' }
    )
    # $Ctx: Year, Document (write document), ScratchRoot, RunId, WriteGate (bool),
    #       Call  = { param($tool, $arguments) }            -> reply object (.isError, .data, .text)
    #       Apply = { param($tool, $arguments, $key) }      -> rehearse + apply with token, returns
    #                                                          @{ stage; answer } like Invoke-WriteApply
    # Returns: an array of @{ Name; Tool; Outcome = 'pass'|'fail'|'unverified'|'not_covered'; Detail }
    Run     = { param($Ctx) ... }
}
```

Rules:
- Names in `Catalog` and in the returned cases must match exactly.
- Discover what the fixture holds (query it) instead of assuming ids, names or categories.
- Create what you need, and leave the disposable document as you found it where the
  probe can (delete what you created); the document is never saved.
- Put temporary files under `$Ctx.ScratchRoot`.
- A module must be testable WITHOUT Revit: add `scripts/live-probes/<name>.tests.ps1`
  that dot-sources the module and runs `Run` against a fake `Call`/`Apply`.
