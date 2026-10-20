# Releasing a stable version / Cómo se publica una versión estable

A stable `vX.Y.Z` release is built and tested by CI, left as a **draft**, signed
with the Certum certificate of HORIZUN GROUP S.A.S. and only then published.
A published GitHub release is immutable (its assets cannot be replaced), which
is why nothing is published before it is signed. The policy is in
[CODE-SIGNING-POLICY.md](../CODE-SIGNING-POLICY.md) and the mechanical gates in
[RELEASE-POLICY.md](RELEASE-POLICY.md).

## The cycle

1. **Prepare** a branch with the version (`Directory.Build.props`, `.mcp/server.json`),
   the `CHANGELOG.md` section, the inventory and the other release metadata, and
   merge it to `main` through a pull request (`pr.yml`, `validate`,
   `windows-deployment`, CodeQL).
2. **Tag**: create the annotated tag `vX.Y.Z` on that `main` commit and push it.
   `ci.yml` runs end to end: build, installer, public integrity, installation on
   the Revit runner and the live gate for Revit 2023-2027 (about two hours, Revit
   closed on that machine, the `Horizun Revit Release` runner pool).
3. **Draft**: `publish-stable-release` creates the release as a **draft** with
   exactly 14 assets (installer, `.mcpb`, `server.json`, `SHA256SUMS.txt`,
   `sbom.json`, two Python audit files, `package-hashes.json`, `manifest.json`
   and the five `live-*.json`). It fails if the release already exists and is
   published.
4. **Notice**: `firmar-y-publicar` waits for the required reviewer of the
   `firma-certum` environment. GitHub notifies the reviewer.
5. **Connect SimplySign Desktop** on the signing PC (the certificate lives in
   the Certum cloud and appears in `Cert:\CurrentUser\My` only while connected).
6. **Approve** the `firma-certum` environment in the **Actions** tab of the run.
7. The job runs `scripts/firmar-release.ps1`, which, on the draft:
   - signs a canary first and stops if SimplySign is not connected;
   - downloads the assets and signs the Horizun setup (`*horizun*` executables);
   - regenerates `SHA256SUMS.txt` and `package-hashes.json` (and any hash that
     points at the signed file), and verifies every signature with `signtool` and
     `Get-AuthenticodeSignature` (certificate thumbprint, Certum issuer, Certum
     timestamp);
   - uploads with `--clobber`, downloads again and compares hashes;
   - publishes the draft only if every step succeeded.
8. **Registry**: `publish-mcp-registry` (official MCP Registry, GitHub OIDC)
   depends on `firmar-y-publicar`, so it runs only after the release is public.

## What is signed and what is not

Only the Setup (`horizun-mcp-X.Y.Z-setup.exe`) is signed. The server, the
add-ins and the Claude Desktop extension are not changed by signing:

- `.mcpb` contains only a manifest, icons, README and LICENSE; it has no
  executable to sign, so its hash in `server.json` (the MCP Registry metadata)
  and in `SHA256SUMS.txt` stays valid.
- The payload inside the Setup keeps the bytes that `stage/manifest.json` hashes
  and that `verify-release.ps1 -Installed` compares after installation.
- `release-notes.md` (the release body) does not pin the Setup hash.
- No hash of the Setup is pinned anywhere in the repository (`docs/INSTALL.md`
  tells users to compare against `SHA256SUMS.txt` of the same release).
- `package-hashes.json` records `authenticode: unsigned_by_policy` for the CI
  build and `release_signing: setup_signed_by_certum_after_ci`; its Setup hash and
  size are replaced by the signed ones.
- The GitHub build attestations and the CycloneDX SBOM bind the CI-built Setup;
  the published Setup differs from it only by the Authenticode signature.
- `install-release.ps1` accepts a Setup signed by HORIZUN GROUP S.A.S. (valid,
  Certum issuer, Certum timestamp) without `-AllowUnsigned` and still accepts an
  unsigned Setup with it.

## The signing runner

The job runs on a repository-level self-hosted runner labelled `horizun-sign`
(`self-hosted, windows, horizun-sign`) under Pablo's Windows account, which is the
account SimplySign Desktop is connected to. It is separate from the live-gate
runner (pool `Horizun Revit Release`).

- Start or check it: `%USERPROFILE%\.horizun\sign-runners\iniciar-runners.ps1`,
  or the scheduled task **Horizun Sign Runners** (`Start-ScheduledTask 'Horizun Sign Runners'`).
- Check it in the repository **Settings > Actions > Runners**: it must be
  *Idle*. The live-gate runner is the scheduled task
  *Horizun GitHub Actions Runner (Interactive)*; it is not used for signing.
- The workflow-wide concurrency group `ci-self-hosted-revit` lets one run at a
  time, so a run waiting for approval holds back the next push to `main`. Approve
  or reject the pending deployment promptly.

## If signing fails

- **"No se puede firmar (SimplySign desconectado...)"** or a missing certificate:
  SimplySign Desktop is disconnected or its session expired. Reconnect it, then
  open the run and use **Re-run failed jobs**, and approve the environment again.
  The draft is untouched because the check happens before downloading or
  uploading anything.
- **A verification or upload failed after signing started:** the release stays a
  draft. Re-run the job; it downloads whatever the draft holds, treats files that
  already carry the Horizun signature as signed and signs the rest. If the
  failure happened between uploads, compare the Setup hash in
  `package-hashes.json` with the uploaded Setup before approving the re-run
  (`SHA256SUMS.txt` is always recomputed from the real files).
- **The gate failed (live matrix):** no draft exists yet. Fix the cause, then
  `gh run rerun <id> --failed`. If a new commit is needed, delete the tag
  (`git push origin :refs/tags/vX.Y.Z`), merge the fix and tag again; this is
  only possible while the release has never been published.
- **The release was published unsigned:** it cannot be repaired (immutable). Ship
  a new patch version.

## Resumen en español

La CI deja un **borrador** (14 assets) y el job `firmar-y-publicar` espera.
Pablo conecta SimplySign Desktop y aprueba el environment `firma-certum` en la
pestaña Actions; el job firma el Setup, regenera `SHA256SUMS.txt` y
`package-hashes.json`, verifica, sube, relee y publica. Después se publica en el
MCP Registry. Si falla por SimplySign desconectado: reconectar y *Re-run failed
jobs*. El runner de firma se arranca con
`%USERPROFILE%\.horizun\sign-runners\iniciar-runners.ps1` (tarea programada
*Horizun Sign Runners*).
