# Bimwright Rvt-MCP 221-Tool Implementation Specs

This directory is the implementation companion for `docs/roadmap-221-tools.md`.
It turns the 15-wave inventory into agent-ready contracts. Each wave file is
intended to be specific enough that an AI agent can implement or review the
wave without guessing the public MCP contract.

## Files

- `wave-01-families.md`
- `wave-02-mep-systems.md`
- `wave-03-graphics-phases.md`
- `wave-04-print-export.md`
- `wave-05-sheets.md`
- `wave-06-materials.md`
- `wave-07-geometry-analysis.md`
- `wave-08-annotation-detail.md`
- `wave-09-rooms-areas-spaces.md`
- `wave-10-links-cad-coordinates.md`
- `wave-11-parameters.md`
- `wave-12-organization.md`
- `wave-13-workflows.md`
- `wave-14-structural.md`
- `wave-15-final-fill.md`

## Common Implementation Contract

These rules apply to every wave unless a wave file explicitly tightens them.

### Handler Shape

- One MCP tool maps to one `src/shared/Handlers/<Name>Handler.cs` file.
- Each handler implements `IRevitCommand` with `Name`, `Description`,
  `ParametersSchema`, and `Execute(UIApplication app, string paramsJson)`.
- Parse parameters with the guarded pattern:
  `string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson)`.
- Catch `JsonException` and return `CommandResult.Fail(...)` with a useful message.
- Return DTOs made of primitives, strings, arrays, dictionaries, and anonymous
  objects. Never serialize raw Revit API objects.

### IDs And Versions

- External element IDs are `long`.
- Before converting a caller-supplied ID, call
  `RevitCompat.CanRepresentElementId(long)` and return
  `RevitCompat.ElementIdRangeError(long)` when false.
- Convert via `RevitCompat.ToElementId(long)` and return IDs via
  `RevitCompat.GetId(ElementId)`.
- Do not use `ElementId.IntegerValue` or `ElementId.Value` directly in handlers.
- Use `#if REVIT20XX_OR_GREATER` only when the Revit API surface truly differs.

### Units

- Public length inputs/outputs use millimetres.
- Public area outputs use square metres.
- Public volume outputs use cubic metres.
- Public angle inputs/outputs use degrees.
- Convert to Revit internal units at API boundaries:
  `feet = mm / 304.8`, `m2 = sqFt * 0.09290304`,
  `m3 = cuFt * 0.02831685`, `radians = degrees * Math.PI / 180`.
- For parameter values, detect `ForgeTypeId`/`SpecTypeId` where feasible. If the
  unit cannot be determined, return an explicit `unit_note` instead of silently
  claiming a converted value.

### Transactions

- Read tools must not open a `Transaction`.
- Write tools use the narrowest reasonable `Transaction` name:
  `Bimwright: <tool action>`.
- Batch write tools must define partial-failure behavior. Prefer dry-run previews
  for broad/destructive operations.
- If one item in a batch fails after earlier item mutations, use `SubTransaction`
  or a clear all-or-nothing transaction strategy so the DTO does not lie about
  committed state.

### Validation And Errors

- Names must fail ambiguous matches instead of silently picking the first match.
- Caller-supplied empty arrays are not the same as omitted arrays. Empty arrays
  should fail unless the tool explicitly documents an empty-list behavior.
- File-system outputs must require absolute paths and reject rooted secondary
  names, path separators, `..`, and invalid filename characters.
- Broad tools need a `limit` parameter with a safe maximum or a documented
  pagination strategy.
- Error DTOs should be structured when the caller can act on partial results;
  hard input/schema errors can use `CommandResult.Fail`.

### MCP Wiring

Every implemented wave must update:

- `src/shared/Infrastructure/CommandDispatcher.cs`
- `src/server/Program.cs`
- `src/server/ToolsetFilter.cs`
- `tests/Bimwright.Rvt.Tests/Golden/tools-list.json`
- `tests/Bimwright.Rvt.Tests/Golden/tools-list-adaptive-bake.json`
- README/toolset tables and `CHANGELOG.md` when the public surface changes.

Do not wait until all remaining waves are complete to refresh the golden
snapshots. Refresh them after each wave so drift is localized.

### Verification

Minimum gate for each wave:

1. `dotnet build src/Bimwright.Rvt.sln -c Debug`
2. `dotnet test tests/Bimwright.Rvt.Tests/Bimwright.Rvt.Tests.csproj`
3. Static review pass focused on BLOCKER and MAJOR issues.
4. Fix pass for every accepted BLOCKER and MAJOR finding.
5. Re-run build and the relevant tests.

Manual Revit smoke is still required for tools that depend on active UI state,
family placement behavior, export add-ins, linked documents, or geometry
selection context.

## Wave File Format

Each wave spec uses the same sections:

- `Status`
- `Scope`
- `Dependencies`
- `Toolset Contract`
- `Tool Specs`
- `Wiring`
- `Acceptance Criteria`
- `Review Checklist`
- `Known Risks`

For Waves 1-4, the files are as-built specs grounded in current code. For Waves
5-15, the files are target implementation specs.
