# Real Revit API signature tests

Run explicitly on a Windows development machine with the Revit 2023 and 2025
APIs installed (or set `HORIZUN_TEST_REVIT_2023_DIR` and
`HORIZUN_TEST_REVIT_2025_DIR` to folders containing their genuine RevitAPI.dll):

```powershell
dotnet test tests/Horizun.ApiSignature.Tests/Horizun.ApiSignature.Tests.csproj -c Release
```

This opt-in project is intentionally absent from the portable Core suite. Missing
required assemblies fail the test with the exact path; they are never silently
skipped. The .NET Framework 4.8 reference assemblies used to resolve the 2023 API
come from the already restored `Microsoft.NETFramework.ReferenceAssemblies.net48`
package, or `HORIZUN_TEST_NET48_REFERENCES`. The SDK is pinned by `global.json`.

`MetadataLoadContext` reads assembly metadata in an isolated type universe. It
does not execute Autodesk code, invoke methods, construct API objects, open a
document, start Revit or change an installed add-in. The tests exercise the same
signature checker against these metadata-only `Type` objects.

The 15 cases cover the ElementId integer-width change, Document.Delete scalar and
collection overloads, and Element.get_Parameter(Guid). The latter proves that an
explicitly named special-name member is checked across its three real overloads.
