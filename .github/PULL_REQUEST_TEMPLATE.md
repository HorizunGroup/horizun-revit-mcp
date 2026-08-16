<!--
Thanks for contributing. CONTRIBUTING.md has the long version; this is the
short one. Delete any section that genuinely does not apply — an honest "not
applicable, because …" is welcome, an unticked box left silently is not.
-->

## What this changes

<!-- One paragraph. What is different after this merges, and why. -->

## Evidence

<!--
This repository's whole contract is that nothing claims work it did not verify.
Hold the pull request to the same standard: paste what you ran and what came
back, not a description of it.
-->

- [ ] `dotnet test tests/Horizun.Core.Tests` and `dotnet test tests/Horizun.Server.Tests` pass
- [ ] `dotnet build src/Horizun.Server -c Release` is clean
- [ ] For add-in changes: built for at least one Revit year (`-p:RevitYear=<year>`) — say which
- [ ] For anything that touches a model: verified against a real Revit, with the output pasted below

```
paste the run here
```

**Revit years exercised:** <!-- e.g. 2024 and 2026, or "none — Revit-free change" -->

## Contract and safety

- [ ] Typed writes are re-read after the commit, and counts come from that re-read
- [ ] New destructive or bulk behaviour defaults to `dry_run: true` and needs a confirmation token
- [ ] Ambiguous input is refused with a reason rather than resolved by guessing
- [ ] The shared contract hash was regenerated if the tool surface changed (server and add-in ship together)
- [ ] No organisation-specific standard, catalogue or naming rule is compiled in — those are call-time inputs

## Nothing private travels with this

- [ ] No client or project names, model paths, element ids, screenshots or logs from real work
- [ ] No credentials, tokens or machine-specific paths (`C:\Users\<someone>\…`)

## Related

<!-- Issue, discussion or release this belongs to. -->
