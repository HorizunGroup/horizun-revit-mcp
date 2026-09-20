# Build from source

This is the developer route. For a normal installation use the [Windows
installer](INSTALL.md), which includes its server runtime.

## Requirements

- Windows and at least one installed Revit 2023–2027.
- Revit closed before installation.
- Git and the exact .NET SDK in [global.json](../global.json): currently 10.0.400.

The SDK builds against the Revit API installed on the machine. Add-ins target
.NET Framework 4.8 for 2023–2024, .NET 8 for 2025–2026 and .NET 10 for 2027.
The public release does not redistribute Autodesk's Revit API assemblies.

```powershell
git clone https://github.com/HorizunGroup/horizun-revit-mcp
cd horizun-revit-mcp
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The installer verifies the installed binaries against the staged hashes and
commit. It deploys the server and add-ins together. Follow the
[client connection steps](CLIENTS.md) after installation.

To update a source installation, pull the intended branch, close Revit and run
`install.ps1` again. Updating a release installation uses the published Setup.

## Build and test

```powershell
dotnet build src/Horizun.Revit -c Release -p:RevitYear=2026
dotnet build src/Horizun.Server -c Release
dotnet test tests/Horizun.Core.Tests
dotnet test tests/Horizun.Server.Tests
pwsh -NoProfile -File scripts/publication-docs.tests.ps1
```

Run Revit-dependent verification only on the intended disposable fixtures:

```powershell
pwsh scripts/verify-live.ps1 -Year 2026 -OldFile 'C:\fixtures\model-from-another-Revit.rvt'
```

Read [AGENTS.md](../AGENTS.md) for model-write rules and
[CONTRIBUTING.md](../CONTRIBUTING.md) for contribution checks.

## Keep the public capability catalog complete

After changing the tool contract, rebuild the server, regenerate the inventory,
and add or update both descriptions in [readme-catalog.json](readme-catalog.json).
Then render the tool and suboperation blocks into both READMEs:

```powershell
pwsh scripts/generate-inventory.ps1
pwsh scripts/update-readme-catalog.ps1
pwsh scripts/inventory.tests.ps1
```

CI checks every tool name, every distinct dispatch choice, the bilingual
descriptions and the marked headline counts. Published release evidence remains
version-scoped in [release-evidence.json](release-evidence.json); changing the
catalog does not create new live-test results.
