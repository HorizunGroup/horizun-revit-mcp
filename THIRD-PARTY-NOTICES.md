# Third-party notices

Horizun MCP is free and open source under Apache-2.0 (see [LICENSE](LICENSE)). It **redistributes** the
components below, each under its own licence, and those licences require their
notices to travel with the files.

This list was produced from the payload that is actually installed — the staged
`plugin\<year>` folders and `server\` — not from the project file. A dependency
that is referenced but never copied imposes nothing; a dependency that lands on a
user's disk does, whatever the csproj says about it.

**This is a technical inventory, not a legal opinion.** It states what ships and
under which licence each component is published. Whether the resulting
distribution satisfies every obligation of every licence is a question for a
lawyer, and nothing here should be read as an answer to it.

---

## Redistributed with the Revit add-in

| Component | Version | Licence | Why it ships |
| --- | --- | --- | --- |
| Newtonsoft.Json | 13.0.3 | MIT | JSON on the pipe and in every reply. Revit ships its own copy, but the add-in cannot rely on which version. |
| IronPython | 3.4.2 | Apache-2.0 | The scripting escape hatch. |
| IronPython.Modules | 3.4.2 | Apache-2.0 | Ships with IronPython; required by the standard library. |
| IronPython.SQLite | 3.4.2 | Apache-2.0 | Ships with IronPython. Contains a managed port of SQLite (public domain). |
| IronPython.Wpf | 3.4.2 | Apache-2.0 | Ships with IronPython. |
| Microsoft.Dynamic | 1.3.5 | Apache-2.0 | The DLR, which IronPython runs on. |
| Microsoft.Scripting | 1.3.5 | Apache-2.0 | The DLR. |
| Microsoft.Scripting.Metadata | 1.3.5 | Apache-2.0 | The DLR. |
| Mono.Unix | 7.1.0 | MIT | Pulled in transitively by IronPython. |
| System.CodeDom | 8.0.0 | MIT | Pulled in transitively by IronPython. |
| System.Text.Encoding.CodePages | 8.0.0 | MIT | Registers codepage 1252, without which the IronPython engine cannot start on .NET 8. **net8 payloads only** — .NET 10 provides it. |

### The Python standard library — 614 files

Each plugin payload carries `lib\` with **614 `.py` files**: the IronPython
standard library, which is a derivative of CPython's.

It is distributed under the **PSF License Agreement**, with some individual
modules under other permissive licences (MIT, BSD) as noted in their own headers.
The PSF licence requires its notice and a summary of changes to accompany
redistribution.

This is the obligation this repository had not previously named anywhere, and it
is the largest single body of third-party code it ships.

## Redistributed with the MCP server

| Component | Version | Licence |
| --- | --- | --- |
| Newtonsoft.Json | 13.0.3 | MIT |

The server is otherwise a framework-dependent .NET 8 executable; the runtime is
the user's, not ours.

## Referenced but NEVER redistributed

**Autodesk Revit API** (`RevitAPI.dll`, `RevitAPIUI.dll`). Referenced at compile
time with `Private=false`, so no Autodesk assembly is copied to the output or
included in the installer. Revit loads its own from its own installation folder.
This is deliberate and load-bearing: redistributing them would be both a licence
problem and a technical one, since the versions must match the running Revit
exactly.

---

## Vulnerability audit

`dotnet list package --vulnerable --include-transitive`, run against both
projects on 2026-07-29:

```
Horizun.Server : no vulnerable packages given the current sources
Horizun.Revit  : no vulnerable packages given the current sources
```

"Given the current sources" is doing real work in that sentence: it means the
NuGet advisory database as it stood that day. It is a snapshot, not a guarantee,
and it says nothing about the Python standard library or about Revit's own
assemblies. Re-run it before every release; CI does.

## Signing

Nothing here is signed. See [docs/security-model.md](docs/security-model.md) for
what that means, what it would take, and why signing with an untrusted
certificate was measured to be **worse** than not signing at all.
