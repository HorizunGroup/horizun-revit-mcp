# Contributing

Horizun Revit MCP accepts focused bug reports, documentation corrections, tests
and implementation improvements. Keep contributions organisation-neutral: client
standards, project names, model names, credentials and proprietary catalogues do
not belong in this public repository.

## Before opening a pull request

1. Open an issue for a substantial behaviour or contract change so its safety
   and Revit-version impact can be discussed first.
2. Add or update tests for every behaviour change. A typed mutation must retain
   its dry-run/confirmation, idempotency and post-commit verification guarantees.
3. Run:

   ```powershell
   dotnet test tests/Horizun.Core.Tests/Horizun.Core.Tests.csproj -c Release
   dotnet test tests/Horizun.Server.Tests/Horizun.Server.Tests.csproj -c Release
   dotnet build src/Horizun.Server/Horizun.Server.csproj -c Release -warnaserror
   ```

4. If the change touches the add-in, compile it against every affected Revit
   year. Live claims require retained fixture output; a successful compilation
   alone is evidence grade B, not L, under [docs/BENCHMARK.md](docs/BENCHMARK.md).
5. Run `pwsh scripts/scan-sensitive.ps1` and remove any client or project data.

## Pull requests

Keep each pull request reviewable and explain the user-visible result, failure
behaviour and verification performed. Do not commit generated `bin`, `obj`,
`dist`, installed add-ins, Revit models, credentials or production logs.

By contributing, you agree that your contribution is licensed under the
repository's Apache License 2.0.
