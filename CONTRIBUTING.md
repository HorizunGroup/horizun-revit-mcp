# Contributing — Horizun Revit MCP

How to work this repo from any machine without stepping on anyone. Read
[AGENTS.md](AGENTS.md) too — it carries the project rules and loads every
session.

## The single source of truth is GitHub, not any one PC

`origin` is the team's private GitHub repository — `git remote get-url origin`
names it on your machine. Your clone has no special status; it is one copy of
what is on GitHub. (The URL is deliberately not written here: this file is
tracked, and the sensitive-data scan gates a release on tracked files carrying
no account or repository names.)

## One branch per task, a PR into `main`, never a direct commit to `main`

```bash
git checkout main
git pull origin main
git checkout -b epic1/place-sprinklers      # epicN/short-name
# ...work...
git add -A && git commit -m "…"
git push -u origin epic1/place-sprinklers
# then open a Pull Request into main on GitHub; do not self-merge
```

Two people on two machines work in parallel this way: each task lives on its own
branch and `main` only moves through reviewed merges. Committing straight to
`main` from two places clobbers — that is the failure this rule exists to avoid.

## Build and test before opening a PR

```bash
dotnet build src/Horizun.Revit/Horizun.Revit.csproj -p:RevitVersion=2026
dotnet test tests/Horizun.Core.Tests
```

The add-in compiles against the Revit year installed on the machine. Tests must
be green before the PR.

Green tests are the floor, not the ceiling. If the branch adds or changes a
command that WRITES, exercise it against a running Revit as well:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-live.ps1 -Year 2026 -WriteProbes
```

That tier commits into a model your `%USERPROFILE%\.horizun\live-fixtures.json`
declares disposable, re-reads the result, and never saves. Without the fixture it
reports every write probe as NOT COVERED by name rather than passing quietly — see
[docs/live-fixtures.example.json](docs/live-fixtures.example.json).

## Hard rules

- **The product name is written `Horizun`** — never `HORIZUN`, never `horizun`
  as a word. Only `horizun_*` tool names and `HORIZUN_*` env vars are lower/upper
  by design.
- **The verified contract:** every typed command must re-read the model after the
  commit — no command reports work it did not verify. Follow the shape of
  `src/Horizun.Revit/Commands/TransformElementsCommand.cs`.
- **A typed write whose verification fails must ROLL BACK.** Reporting the failure
  honestly is not enough on its own: a command that commits and then says it could
  not confirm the result leaves the caller a model to untangle by hand, and it
  cannot simply be retried. `TerminateRiserCommand` gets this right — it names the
  stage that failed and builds nothing. A command that deliberately keeps partial
  work must say so in its description and report exactly what stayed.
- **A write path is not verified until it has been COMMITTED against a real
  model.** Green unit tests plus a clean build have already shipped three commands
  to review that could not do their job: refusals prove the guards, dry runs prove
  the arithmetic, and neither one executes the Revit half. Add a probe to the write
  tier of `scripts/verify-live.ps1` (`-WriteProbes`) and run it before the PR.
- **Respect an explicit `horizun_execute_python` off-switch.** The tool is
  enabled by default and serves as the execution fallback, but a machine whose
  owner disabled it (`enable_execute_python=false` or a profile below
  `unsafe_code`) made a deliberate choice — never edit their `settings.json` to
  reverse it. The owner re-enables it with `scripts/enable-execute-python.ps1`.
- Every new typed command that overlaps `execute_python` gets an entry in its
  typed-overlap **advisory** table — the advisory recommends the verified typed
  command; it does not block the script.

## Where the backlog lives

[docs/BACKLOG.md](docs/BACKLOG.md). Pick a story, branch, PR.

## Knowledge is a DIFFERENT channel

Reusable knowledge — agent memory (`api.md`, `mep.md`, `familias.md`), skills,
field contributions — does **not** go in this git repo. It flows through the
Horizun CORE on OneDrive via the `sincronizar` skill. Code → GitHub; knowledge →
CORE. Keep them separate.
