# Changelog

What changed, and — where it matters — what was actually measured rather than
assumed. Dates are the day the work landed.

## v3.0.5 (2026-10-22)

- Instalador: el chequeo de manifiestos de Revit distingue un AddInId de Horizun duplicado de un manifiesto o una carpeta Addins que no pudo leer, y nombra el archivo (con el motivo y la línea que da MSXML) o la carpeta (con el error de Windows). Un manifiesto ilegible de otro proveedor seguía bloqueando la instalación con "Another manifest with the Horizun AddInId already exists" sin decir cuál, en un equipo donde Horizun nunca se había instalado. El bloqueo sigue siendo fail-closed; ahora el mensaje dice que no significa que Horizun esté instalado y cómo destrabarlo, y queda completo en el log de Setup, que se escribe antes de mostrar el diálogo. `scripts/installer-preflight.tests.ps1` compila una variante de prueba del instalador y ejecuta el Setup real contra manifiestos de prueba (limpio, de otro proveedor, duplicado renombrado, DOCTYPE, XML roto, ambos a la vez, carpeta de año que es un archivo); corre en el job que compila el instalador.
- Add-in: el recordatorio de guardado de Revit ("Project Not Saved Recently") ya no bloquea el puente. Revit lo abre a los 30 minutos (por defecto) del último guardado y lo abre con Revit inactivo, entre dos comandos, donde ningún vigilante de comando escucha; nadie lo contestaba y cada solicitud siguiente se rechazaba como nunca iniciada. Así se cayó la puerta en vivo de la primera corrida de 3.0.5 (2026-10-22): 15 fallos en Revit 2023 y 18 en 2024, 52 llamadas rechazadas. Un vigilante nuevo (`IdleDialogWatcher`, `SaveReminderPolicy`) contesta **solo** ese recordatorio, **solo** mientras el puente está en uso (una solicitud en cola o una en los últimos 10 minutos), y siempre con Cancelar, que no guarda (medido en los journals de Revit: en ese diálogo las opciones 1001 y 1002 sí guardan): no escribe el archivo del usuario ni toca el modelo. Lo que contestó llega en la respuesta del siguiente comando, en `between_commands`, para que el agente avise que el modelo tiene trabajo sin guardar. Cualquier otro diálogo sigue esperando a una persona, como antes, y una persona trabajando sola en Revit sigue viendo su recordatorio.

## v3.0.4 (2026-10-19)

- Mismos arreglos de 3.0.2 y 3.0.3, que no llegaron a publicarse; primera versión con el instalador firmado por HORIZUN GROUP S.A.S. (Certum). Sin cambios funcionales.

## v3.0.3 (2026-10-19)

- Mismos arreglos de 3.0.2, que no llegó a publicarse; primera versión con el instalador firmado por HORIZUN GROUP S.A.S. (Certum). Sin cambios funcionales.

## v3.0.2 (2026-10-18)

- Corrected false verification and data loss when duplicating system and family types, reloading families, moving connected MEP elements, and opening documents with unresolved references.
- Corrected CAD elevation, reinforcement geometry and overlap checks, glazed roof verification, and accented catalog search.
- Rejected duplicate names for bound shared family parameters before loading. No tools, arguments, or capabilities were added.
- The Setup is signed with the certificate of HORIZUN GROUP S.A.S. (Certum); the server and add-ins it installs are byte-identical to the CI build and remain unsigned. No functional code changes. The release is now created as a draft and published only after signing (`docs/RELEASING.md`); the bootstrap accepts a Setup signed by that certificate without `-AllowUnsigned`.

## v3.0.1

- The release runner now enables Python only in its isolated test data root and restores the owner's Python setting after each Revit year. The first v3.0.0 release gate copied the owner's disabled setting into the isolated root, leaving fixture-dependent probes without coverage. The owner's durable setting is never edited. The v3.0.0 tag remains immutable and did not publish a stable release.
- The compound-structure release probe lists one host wall, then resolves its instance and type by exact ElementId. A broad model query scanned the entire fixture before paging and outlived the live harness's RPC wait; this bounded route still verifies that the selected row is a host system type.

## v3.0.0

### Field defects

- Isolated IronPython dependencies from other add-ins on .NET 8 and .NET Framework 4.8. Health now reports `not_authorized`, `not_loadable` with cause, `cold`, or `warm`. A synthetic conflicting add-in was exercised in Revit 2026.
- Corrected CAD discovery wording: Revit geometry extraction alone does not read DWG text, while a readable source DWG through AutoCAD can provide TEXT/MTEXT. `horizun_cad_extract` offers a paged detail plan, source status, coverage and explicit gaps. The available field copy had an embedded import and no readable source DWG, so full text, hatch and original-source coverage remain unproven.
- Added group duplicate detection, annotation previews by view, position-preserving level change, equivalent tag recreation on type change, rotated and mirrored placement from a pattern, and a session annotation report. Synthetic probes ran in Revit 2024 and 2026. Live dimension-loss coverage and reporting of operations beyond type change remain pending.
- Added a Python preflight warning for `FilteredElementCollector(doc, viewId, linkId)`, a typed linked-view candidate scan without that overload, and process or recent WER dump evidence in job status. The scan does not prove final visibility when crop, phases, design options or link display settings are unresolved.

- A column section's cover now participates in the smallest declared cover of its into union, alongside cover_rules; a 40 mm column layer through a footing declaring 75 mm remains valid (RebarIntoCoverTests, live J8 pending).
- Hooked rows at a stirrup's hooks corner accept only the declared corner seat in plan, apply and audit; a crossing_order move beyond that seat removes the allowance (RebarAuditRulesTests).

Major release over 2.1.6. The 2.1.7, 2.2.0 and 2.3.0 candidates were never published; their work ships together here, so this section is their three changelogs in one. **Bridge protocol 3** (2.1.6 was protocol 2): a 3.0.0 server and a 2.1.x add-in refuse each other, so update both halves together, as the installer does. The Breaking items are in the 2.2.0 part below.

### Additional 3.0.0 fixes and interoperability

### 3.0.0 phase 3B (the backlog after 2.3.0: Core, commands and steel)

Each fix was exercised with a red-first test.

Remaining #12 limitation (3.1, M): projection corrects the plane used in sections and elevations, but a model-axis bounding box stays conservative in a rotated plan. Two 100 x 20 screen rectangles 110 units apart at 45 degrees still produce overlapping projected model boxes. `TagOverlapRulesTests.Model_axis_boxes_remain_conservative_in_a_rotated_plan` pins this counterexample; exact annotation contours are needed to remove that false positive. The #12 change below is partial, not a full resolution.

- **#13 `ifcVersion` is compared by schema family.** The IDS run took a declared `IFC4` and a file `IFC4X3_ADD2` (and the other way round) for one schema, because it compared by prefix in both directions. It now compares `IfcDeliveryRules.SchemaFamily` of both, as the delivery gate already did: IFC4 against IFC4X3 is `NotDecidable` with the FILE_SCHEMA note. Tests: `IdsTests.IFC4_and_IFC4X3_are_different_schemas`.
- **#18 The `horizun_manage_cad_links add` token binds every import option.** `custom_scale`, `colors`, `orient_to_view`, `visible_layers`, `visible_layers_only`, `current_view_only` and `auto_correct_almost_vertical` were outside it, so the apply could link at another scale or with other layers than the rehearsal showed. The token's scope is now every key the option reader reads, and the resolved plan carries the declared options. Test: `Backlog3BWiringTests.The_cad_add_token_binds_every_import_option` (holds the scope to the reader's own keys).
- **#42 A BCF viewpoint carries the Guid its markup names.** The `VisualizationInfo` Guid was new on every export and matched nothing in `<Viewpoints Guid>`, so readers that link the two found no camera. IDS and coordination exports now write the topic's stable viewpoint Guid in both, and the IDS re-read refuses an archive where they differ. Tests: `BcfViewpointGuidTests`.
- **#43 An IFC rectangle profile whose `Position` does not resolve is refused**, instead of being placed at the profile's origin without a word. An absent `Position` (`$`) is still the origin. Tests: `IfcGeometryTests.A_rectangle_whose_position_does_not_resolve_is_refused`.
- **#44 Mixed units in a foreign quantity no longer spoil a budget line.** A quantity whose rows carry two units made every line of its code `unit_incompatible`, even when the line is fed by another quantity in its own unit. Mixed units still refuse a line when they are in the quantity that could feed it, or in the pinned one. Tests: `BudgetComparisonRulesTests.Mixed_units_*`.
- **#45 Product texts write their numbers invariant.** On a Spanish system a tolerance read "2,5%", a delta "0,25 mm", an overlap "12,5 mm" (`PairTally.Headline`, `LinkSurveyRules`, `WarningRootCauseRules`), and two CAD open junctions could share one key (`CadOpenJunctions`). Tests: `ProductTextCultureTests`.
- **#46 A schedule date keeps the day it was written on.** `ScheduleImport` moved an offset date to UTC before truncating it, so a task at `2026-10-10T00:30+02:00` started on the 9th in the 4D. Tests: `ScheduleImportDateTests`.
- **#47 An xlsx row or column past the worksheet's limits is refused** (rows 1 to 1 048 576, columns A to XFD). The row attribute padded the sheet without a bound, and `r="2000000000"` exhausted memory; a long column reference could wrap an int. Tests: `XlsxWorkbookTests.A_row_or_column_past_the_sheet_limits_is_refused`.
- **#48 The quality-history key is lower case and keeps what it cuts.** It was cut at 80 characters and kept the case on a file system that ignores it, so two projects could write one history. A key longer than 80 characters now ends in a digest of the whole name; such a project starts a new history file. Tests: `QualityHistoryTests`.
- **#49 A ramp's built slope runs along its long side**, not the diagonal of its plan box (which added the width and read a straight ramp flatter). Only the measurement's note uses it. Tests: `RampBuiltSlopeTests`.
- **#50 A crosshatch fill pattern reads its second grid back**: angle (the first grid's plus 90 degrees, compared modulo pi) and spacing. Only grid 0 was verified. Tests: `FillGridRulesTests`. Live probe pending (see below).
- **#51 `manage_cad_links add` and `repoint` judge their commit and load.** `add` ignored the transaction status; `repoint` ignored it and the `LoadResult`, and verified on the path alone. Test: `Backlog3BWiringTests.Cad_add_and_repoint_judge_their_commit_and_load`.
- **#12 The tag overlap check measures what is on screen.** `TagOverlapCheck` compared the model X/Y of each `get_BoundingBox(view)`: in a section or an elevation every tag box is flat in model Y and the overlap was always 0 ("clean" by construction), and in a rotated plan side-by-side tags could read as overlapping. The box's corners, through its own transform, are projected onto the view's right and up directions. Feeds `spatial_check` and `horizun_verify_changes`. Tests: `TagOverlapRulesTests` (sections, transforms, the wiring). Live probe pending.
- **#14 An audit narrowed by `host_ids` lists no orphans from other hosts.** A bar of a zone, mat or section rule in a host outside the list expands under that host's id, which the narrowed audit never expanded, so it read as an orphan and the audit could never agree. (The `chunk` path already skipped other pieces' hosts since L10.) Test: `Backlog3BWiringTests.A_host_ids_audit_reads_orphans_only_in_its_hosts`.
- **#15 and #16 were already resolved by L10 (`92a48b93`)** and are now pinned: a chunked apply seals every bar with the WHOLE set's digest (`Sha256Of(request.requirement_set)`), so the whole set's audit does not read each piece as `stale_requirement_set`; and the cut's inventory is read by the chunking itself with BuiltInCategory names, the form selectors use, not `query_model`'s localized names. Tests: `Backlog3BWiringTests.A_piece_is_sealed_with_the_whole_sets_digest`, `The_cut_inventory_names_categories_as_selectors_do`.
- **#17 A recorded plan never reaches another command.** A command that recorded its plan and then refused (token, document, failure) left the thread-static plan for the next command on the thread, whose dry run published it as its `change_preview` and said its token was bound to it. The dispatcher clears it around every command it runs, and `apply_corrections` / `execute_plan` never stamp a child's plan as theirs. Test: `Backlog3BWiringTests.A_recorded_plan_never_outlives_its_command`.
- **#19 `relinquish_all` counts checkouts with two filtered collectors.** A collector with no filter throws in Revit, so the census came back `Unreadable=1` and `fully_relinquished` could never be true. Test: `Backlog3BWiringTests.The_relinquish_census_uses_filtered_collectors`. Live probe pending.
- **#20 `slab_shape` reads vertex Position as model coordinates.** An offset reading selected by agreement with the requested result could accept even a single unchanged vertex. Points and crease ends use the absolute reading throughout; the per-point alternative remains diagnostic evidence only. Tests: `ArchitecturalEditRulesTests.One_edit_reads_every_vertex_with_one_convention`, `An_unmoved_vertex_cannot_choose_the_reading_that_would_pass_it`, and the wiring. Live probe pending.
- **#29 `run-year-matrix -SkipBuild` cannot file one year's add-in as another's.** The add-in carries `AssemblyMetadata("HorizunRevitYear", <year>)`, and `Assert-HorizunBuiltYear` (`horizun-deploy.lib.ps1`) refuses a DLL built for another year, or with no stamp, in the development session and before the matrix starts a Revit (`build_year_mismatch`). The TFM guard could not tell 2023 from 2024 nor 2025 from 2026. Tests: `scripts/deployment-security.tests.ps1`.
- **#30 A naming profile's regex runs with a 250 ms limit.** A catastrophic pattern froze Revit inside `model_scan`. A name whose check times out is the finding `regex_timed_out` - not judged, never a pass. Test: `NamingProfileTests.A_catastrophic_regex_times_out_and_the_name_is_not_passed` (red: 20 s and still running).
- **#31 A stranded wall profile is measured by its ends, not its centre.** A profile edited to run past one end of the wall moved its centre and read as drift; `realign_wall_sketch` moved it and verified with the same metric. A move shifts both ends by one amount; one end on the wall's is an edit (not stranded); both off by different amounts is stranded and not correctable (no translation is chosen). Tests: `WallSketchDriftRulesTests`. Live probe pending.
- **#32 Receipt redaction runs over the values, not the serialized line.** JSON doubles every backslash of a path, so a pattern written for the path never matched and the account name stayed in the receipt. A pattern that does not compile still withholds the receipt. Test: `ReceiptLedgerTests.A_path_pattern_redacts_the_path_as_the_reply_carried_it`.
- **#33 `manage_links change_path` says it opens every workset.** The reload used `new WorksetConfiguration()`, which opens the worksets the link had closed, and said nothing. Revit cannot report the configuration a link was loaded with (measured 2026-09-04), so it cannot be carried over: the configuration is now explicit (`OpenAllWorksets`) and the dry run and the reply carry `worksets_after: "all_open"` with what it means. Test: `Backlog3BWiringTests.Change_path_says_it_opens_every_workset`.
- **#52 `isolate_elements` is verified only when everything else was hidden.** The requested ids being visible is true before any isolate, and an earlier temporary mode satisfied the mode check, so a call that did nothing read verified. Every collectable candidate is checked; an unreadable hideability check fails verification. Test: `Backlog3BWiringTests.An_isolate_checks_that_everything_else_was_hidden`. Live probe pending.
- **#53 Cutting a piece again keeps the original `source_selector`.** The narrowed selector replaced it. Test: `RequirementSetChunksTests.Cutting_a_piece_again_keeps_the_original_selector`.
- **#54 The canonical section-rule example is one the bridge accepts.** It put two layers on the bottom face over the same stretch (`layers_coincide`) and a bastón `from_mm: 0` with no end cover. It now lays the extra bar `between` the two bottom bars, from the start with the end cover kept, and the example is run as written. Tests: `SectionRuleDocExampleTests`.
- **#56 A stirrup zone count past an int is refused with `notation_bad_number`**, instead of throwing `OverflowException` out of `RebarNotation`. Test: `RebarNotationTests.A_zone_count_past_any_int_is_refused_with_a_code`.
- **#57 A mark whose schedule lines state `weight_kg` only in part says so** (`weight_not_compared`: "only N of M lines"), instead of dropping the mark's weight silently. Test: `RebarReconcileTests.A_mark_whose_lines_state_weight_only_in_part_says_so`.
- **Phase 0 deferred:** the partial classifier treated shallow intersections with straight stirrup legs as bend seats. Its synthetic fixture contained no bend. The classifier and its unverified tests were removed; reliable classification needs bend evidence and per-bar classification before set-pair aggregation. Existing refusals and error reporting stay intact.
- **To 3.1: #21**, sequential corrections need a binding shared by every permitted child, including children that publish no `plan_resolved` fingerprint. The partial recheck skipped those children and was removed; it did not resolve the finding.
- **To 3.1: #55** (the bend set-back applied to side bars, and the hook pass lowering the whole top row). The top row is one rebar set on one line, so its corner bars and its middle bars cannot sit at different heights without splitting the set, and the corner positions are the ones the stage 1c audit measured against the drawn bars in the earlier validation. Changing them offline could break that agreement; it needs Revit.

Live probes for the matrix: `scripts/live/backlog-3b-matrix.md` and `scripts/live/verify-backlog-3b.ps1` (prepared disposable fixtures), plus `run-year-matrix.ps1 -SkipBuild` against a bin built for another year (#29). All remain pending live.
### 3.0.0, phase 2: interoperability (exchange manifests, model identity, the correction cycle)

Owner decisions D0 (model identity) and D1 (one server per app in each client) approved 2026-10-12. Built
without a Revit (the owner's was open): the add-in half compiles for 2023-2027 with no warnings and is
NOT live-measured yet - `scripts/live/verify-interop-identity.ps1` is its probe for the final matrix.
See [docs/INTEROP.md](docs/INTEROP.md) for the public protocol and client workflow.

- **`horizun.exchange/0`, the manifest of a cross-app hop** (Core `ExchangeManifest`, schema
  `schemas/horizun.exchange.v0.schema.json`). It wraps an existing format, lives at
  `<exchange_root>/<kind>/<exchange_id>/exchange.hzx.json` beside a copy of the payload, and is written
  LAST: payload copied under a temporary name, moved without overwriting, re-hashed; then the manifest,
  then the whole exchange re-verified. Payload without a manifest, or a manifest whose payload has not
  arrived, is `not_delivered`. `verified` requires `host_verified: true`; anything else reads as
  `self_reported_verified` at best, and a chain is as strong as its weakest hop. The exchange root is
  LOCAL by default (`%USERPROFILE%\.horizun\exchange\<project>`), a conservative decision taken for the owner.
  Tests: `ExchangeManifestTests`.
- **New host-resident tool `horizun_exchange`** (toolsets coordination, interoperability; external
  effect only on request): `emit` (dry_run by default; a wrap made by the server is capped at
  `self_reported_verified` and keeps the producer's `verified` as `state_capped_from`), `verify`
  (delivery, sha256, effective state, identity against the registry, `may_continue`),
  `correction_plan_check` and `correction_plan_reverify`. tools/list +3,937 bytes (483,077 -> 487,014 of
  524,288); ledger regenerated. Tests: `InteropToolTests`.
- **Model identity, decision D0** (Core `ModelIdentity`). A `model_ref` binds strong evidence in the order
  `creation_guid`, `central_guid`, `cloud_model_guid`, `unique_document_guid`; the first key both sides
  carry decides, and a weaker key that agrees with a ruled-out model is reported as `evidence_conflict`.
  Project Information never matches by itself. Evidence read on Revit 2023 is always
  `identity_proven: false` (`identity_unprovable_2023`, the code `horizun_model_diff` already uses).
  `get_document_info` publishes it as `model_identity` (contract text changed, so the hash changes).
  Tests: `ModelIdentityTests`.
- **The project registry, `project-integrations.json`** (schema `horizun.project-integrations/0`, beside
  `project-context.json`): `horizun_project_context` operations `integrations`, `register_model` (dry_run by
  default; refuses a model with no strong key and a second model with the same binding evidence; writes
  atomically, re-reads, needs `full_write`) and `resolve_model` (refuses to match without strong evidence or
  against an ambiguous registry). The caller's `identity_proven` is ignored and recomputed.
- **Review plans enter `horizun_apply_corrections correction_plan`**: typed rehearsal, token bound
  to document and child model facts, re-rehearsal before apply, then fresh reads of ALL targets after
  the final step. A later overwrite is persistent. Explicit parameter writes and revision actions
  without clouds; no Python, CSV/sequence or placeholders. Per-step transactions; PDF re-review
  remains pending. Relayed reconciliation is capped at `self_reported_verified`, `host_verified:false`.
  Real parameter-read object shapes and red-first tests; `verify-interop-corrections.ps1` pending live.
- **Exchange verification fails closed** on uncheckable model identity, missing method/host flag and
  duplicate/reserved payload names. Dot-only project codes cannot escape the exchange directory.
  Red-first `ExchangeManifestTests`.
- **Offline tests isolate state**: disposable job/Excel roots; profile-precedence tests supply the
  known-folder API's real string shape while source guards retain the production lookup. No owner
  state/configuration is written, and no assertion is removed.
- **Sensitive scan keeps every rule while avoiding empty term checks**: the OR of the same escaped
  private terms prefilters neutral lines; matching lines retain every per-term governance exception,
  overlap and original line number. Red-first CPU regression: 12k lines / 64 terms took 34.2 CPU s.
  `scan-sensitive.performance.tests.ps1` also requires both case-insensitive findings at line 12001.
- **The review -> correction -> re-review cycle, server reconciliation** (Core `ReviewCorrectionPlan`). A
  horizun-review `plan_correcciones_revit` plan is checked (Python never, every step rehearses, every
  `<...>` placeholder named with its resolver) and, after the approved applies, reconciled target by target
  against the PLAN's own values from the typed replies the bridge re-read: `corrected`,
  `confirmed_by_reread_only`, `persistent` (also when the applied value drifted from the plan),
  `not_applied` (a rehearsal), `not_verifiable` (no row, a truncated reply). The reply says it re-derives,
  it does not read the model again, and that only horizun-review's delta closes a PDF finding. Tests:
  `ReviewCorrectionPlanTests` (red first).
- **Prompt `cross-app-coordination`**: the agent as the only bus - preflight of peers and duplicates,
  one intake (WHAT / WHICH / HOW, one correlation_id), approval per stage, owner order, three-layer
  verification, one closing row per hop with the weakest state as the global one. Prompts: 30.

### Steel geometry, crossing and audit fixes

- `crossing_order` declares which rule stays and which moves; the plan rechecks cover, containment and overlap after displacement, and the audit verifies the built position. Without a declared order, the bridge does not choose one.
- The reinforcement apply and audit share geometry comparison. A bar moved by Revit is no longer reported verified merely because its length matches. `hold_ends` can preserve declared endpoints when Revit constraints would move them.
- Open arc chains, hook orientation, drawn hook cover, bend-aware corner overlap, duplicate bars, whole-model steel review and fabric mass are measured against drawn geometry. Ambiguous or unreadable cases remain named refusals.
- The operations pane opens only through its ribbon button and adapts to a narrow docked panel.
- Red-first tests cover these changes; synthetic live probes are listed in the steel capability matrix.
### Server, installer and CI backlog (stage 3, part A)

The findings come from the post-2.3.0 backlog; the fixes are grouped by area.

#### The repair_memory quarantine is enforced

Calls the owner quarantined are refused before they reach the add-in.

- **A quarantine refuses the call it was set on (backlog #4).** 2.1.7 made `quarantine` stop promising a refusal nothing enforced: a failure shape is the failure's own message, known only after the call fails. What is known before a call runs is the call, so `observe` and `quarantine` now take `call_arguments`, the failing call's arguments, and keep only a hash of their normalised form (keys sorted at every depth, `idempotency_key`, `confirmation_token` and `_meta` left out, an integral number written as an integer). No argument value reaches the file. While the shape is quarantined, the tools/call path refuses that call before a task is created or anything is routed to Revit. The refusal is an error result with `structuredContent.code = call_quarantined`, the shape, the reason, who set it and when, `ran: false`, and a `remedy` naming `operation=release shape=…`. Any other argument value, `dry_run: true` included, is a different call and runs. `quarantine` reports `refuses_calls`, and with no call recorded it says that it refuses nothing. The gate reads the memory without the mutex and caches it by size and write time. A damaged memory refuses nothing and is not moved aside, and `horizun_repair_memory` is never refused, because it is how a quarantine is lifted. The 500-entry bound keeps quarantines and their fingerprints. Tests: `RepairMemoryCallGateTests` and `QuarantineWireTests` (the real server over stdio, under an empty data root, red first).

#### The server's queue and pipe

How cancellation and the pipe behave under load.

- **`tasks/cancel` no longer stalls the server while a submission holds the task (backlog #22).** It is answered on the server's reader thread and took the task's mutex with no bound, while `Submit` holds that mutex for its whole round trip to Revit (up to 600 s): nothing else was answered meanwhile. The wait is now bounded (1 s, like `tasks/get`); a busy task gets its cancellation deferred to a background thread, which records it and asks Revit for the removal as soon as the submission lets go - by then with the job id it lacked. An unknown task id is still refused on the call. Test: `TaskCancellationTests.Tasks_cancel_answers_promptly_while_a_submission_holds_the_task` (red first).
- **A request claimed after its own raise was refused no longer looks like an ordinary failure when its wait expires (backlog #38).** When Revit refused a request's `Raise()` but the UI thread had already claimed it through another raise, the dispatcher waited and ignored whether the wait ended: a command still running came back as "produced no result". It now says the command STARTED and is STILL RUNNING, with `never_started: false`, `still_running: true` and `transaction_status: unknown` (`RequestGate.AwaitClaimed`). Tests: `RequestGateTests.A_claimed_request_whose_wait_expires_is_reported_still_running_not_failed`, `A_claimed_request_that_finishes_returns_its_own_result` (red first).
- **A cancel that overtakes its request stops it (backlog #36).** The cancel travels on its own connection and could reach the add-in while the request was still being read and authenticated, before the gate: it was answered "finished or left the queue" and the request then ran. The gate now remembers the last 64 ids cancelled before it saw them and refuses such a request on arrival, with the never-started proof; wire ids are fresh per send, so this can only stop work. The reply says `refused_on_arrival`, and the server tells the caller both readings instead of only "it finished". Out of reach: `horizun_submit_job` is admitted on the pipe thread without the gate, and its jobs are cancelled by id (`__horizun_cancel_job`). Tests: `RequestGateTests.A_cancel_that_arrives_before_its_request_refuses_the_request_on_arrival`, `An_early_cancel_refuses_only_its_own_id_and_only_once`, `Early_cancels_are_remembered_within_a_bound`, `TransportFailureDetailTests.A_cancel_answered_not_found_names_the_refusal_on_arrival` (red first).
- **Pipe writes have a deadline on both ends (backlog #37).** The bridge's pipe has no buffer, so a write completes only when the other end reads; when the add-in refused a request as `TooLong` it stopped reading and wrote its refusal, while the server was still blocked writing the request: both ends stuck, with no timeout, holding a connection slot until Revit closed. The server now refuses a request over the contract's `MaxRequestBytes` before sending anything, reads while it writes (a valid pre-dispatch refusal envelope (null id, success=false, nonempty error) that arrives before the request was read whole is returned as never dispatched, `request_may_have_been_submitted: false`) and abandons a write nobody reads after 35 s. The add-in writes its replies through `LineWriter` with a deadline (5 s for a refusal of an unread request, 60 s for a reply) and closes the connection past it. A closed pipe, malformed line or unrelated reply keeps the execution outcome unknown. Tests: `PipeWriteBoundTests` (server, including three additional red-first uncertainty cases) and `LineWriterTests` (add-in) (red first).

#### Paths, containers, procedures and Power BI

What the server reads and writes on the machine.

- **A Power BI selection read that names no document returns no keys** (backlog #24). `horizun_selection_exchange` `read` without `document_fingerprint` handed back the keys whatever model was open, although the contract returns no keys to a read from another document. It now returns `keys: []` with `document_unidentified` saying to pass the fingerprint `horizun_health` reports. Test: `PowerBiSelectionStoreTests.A_read_that_names_no_document_returns_no_keys` (red first); the existing store test now reads with the fingerprint it published.
- **A Power BI push cancelled before sending is recorded as not sent** (backlog #40). A cancellation after the key was claimed and before the request was sent was reported as `in_doubt`, and the record was never completed, so a retry met an open claim for rows that never left the machine. The cancellation is now checked before the send, completed in the ledger as a failure ("No rows were sent"), and a retry with the same key replays it. Test: `PowerBiPushTests.CancellationBeforeSending_IsRecordedAsNotSentRatherThanInDoubt` (red first).
- **A procedure run and the script promotion registry are locked across processes** (backlog #35). `horizun_run_procedure` serialised a run with a Monitor and `horizun_promote_script` with a `lock`, both inside one server, so Claude's and Codex's servers could load the same record and advance the same step twice. Both now also take a named `Local\` mutex derived from the record's path (`HostFileMutex`, the idea of `RepairMemory.MutexName`): a run is refused at once when another process holds it (waiting would dispatch the next step), the registry after a bounded wait, and the refusal names the path. Tests: `ProcedureResumeTests.A_run_held_by_another_process_is_refused_and_dispatches_nothing`, `ScriptPromotionLockTests` (red first; the second holder is a separate Mutex on another thread).

- **The xlsx reader round-trip test keeps its backups in its own temporary data root.** Its shipping-writer fixture used the real owner data root; the full Server suite exposed the attempted write outside the test sandbox. It now uses `ExcelBackupRoot`, as the other workbook fixtures do. Test: `ExcelReadRowsTests.The_round_trip_reads_what_the_shipping_writer_wrote` (failed before isolation).
- **No path a client sends reaches a network share (backlog #23).** Only the CSV writer refused UNC and device paths; `excel_read_rows`, which `read_only` allows, opened `\\host\share\x.xlsx` and Windows signed in to that host as the user, handing it the NTLM hash. One rule, `LocalPath`, now runs on the string before the first file-system call in the xlsx reader and writer, the CSV writer, `catalog_lookup`, `project_context`, `ids_from_loin`, the information containers (`file_path`, `file_paths`, `root`, `states`, `project_context_path`), `budget_compare` (`model_rows_path`, `baseline.file_path`, `outputs.excel.file_path`), `export_bc3`, `read_schedule` and the ISO evaluation of the model explanation. It refuses `\\host`, `//host`, the mixed `\/` and `/\` spellings the CSV check missed, `\\?\`, `\\.\` and `\??\`. Test: `LocalPathTests` (red first).
- **A full path means a drive, a colon and a separator (backlog #41).** `Path.IsPathRooted` accepted `C:x.json` (relative to drive C's current folder) and `\x.bc3` (relative to the current drive), so `project_context`, `export_bc3` and `ids_from_loin` read or wrote wherever the server's working folder pointed. The same `LocalPath` rule refuses both forms everywhere it applies. Test: `LocalPathTests` (red first).
- **`inspect` stops at `max_files` and reports what it could not read (backlog #34).** It listed and sorted every file of a state folder before applying the cap, and one subfolder without permission threw out of the whole call. The walk is now lazy, in the same order as before (each folder sorted on its own, a subfolder by its name and separator), so nothing past the cap is listed; an unreadable folder is named in the state's new `unreadable_folders`, the state is `complete: false` and its coverage says why. Junctions and symbolic links are not followed. Tests: `Inspect_names_a_subfolder_it_cannot_read_instead_of_failing_the_whole_call`, `Inspect_stops_at_max_files_without_walking_the_rest_of_the_tree` (red first), `Inspect_walks_in_the_same_order_as_one_sort_of_every_path`.
- **A transition's destination stays inside the next state's folder (backlog #39).** With `file_name=name_status_revision` the status and the revision become part of the file name, but only the fields were checked for `\` and `/`; a permissive revision pattern let `P01\..\..\x` carry the copy out of the state folder. The core validation now refuses a status or revision with a character Windows forbids in file names whenever they enter the name (the same rule names `horizun_export`'s file), and `transition` refuses any destination that does not resolve to a file name inside the target folder. Test: `Transition_never_builds_a_destination_outside_the_state_folder` (red first).

#### Installer and CI

Client registration, the source install and the release gates.

- **A Codex table is found under any valid TOML spelling (backlog #25).** `toml-section.lib.ps1` matched the header text exactly, so `[mcp_servers."horizun-revit"]`, a spaced or literal-quoted key or a header with a trailing comment was not found: registration appended a second table, and Codex refuses a file that defines one twice. The same test took a commented header after ours (`[profiles.x] # c`) for a key line and deleted that table on replace. Headers are now parsed into keys (quotes, spaces, comments, multi-line strings); registration also refuses and restores if the file would end with the table twice, and removal verifies by table, not by text. Test: `register-client.tests.ps1` (red first).
- **A Node warning on stderr no longer fails the Claude legacy cleanup under Windows PowerShell 5.1 (backlog #26).** `& $cli mcp remove ... 2>&1` under `Stop` turned the first stderr line of the real `claude.cmd` into a terminating error after the CLI had already removed the entry, and that removal was not put back. The CLI's output is captured under `Continue` and its exit code is the verdict; anything that still throws restores the pass backup first. Test: `register-client-legacy.tests.ps1`, a `.cmd` stand-in that writes a Node deprecation warning to stderr (red first under 5.1).
- **CI runs the client-registration suites under both hosts (backlog #27).** `register-client`, `register-client-legacy` and `client-config-encoding` said "run it under both" but ran only under pwsh 7, while the installed helpers run under `powershell.exe`. `ci.yml` and `pr.yml` now run them with the ChatGPT tunnel suite in one step over `powershell.exe` and `pwsh`. Test: `public-consistency.tests.ps1` (red first).
- **The live release gate certifies the build of its own job (backlog #28).** `run-release-live-gate.ps1` took the INSTALLED manifest as expected whenever one existed and never compared its commit with the run's, so on the shared runner another install could be certified. CI now passes `-Manifest dist/stage/manifest.json -ExpectedCommit ${{ github.sha }}`, and the gate refuses a manifest of another commit before any fixture, process or Revit is touched. Test: `public-consistency.tests.ps1`, the workflow pin and the gate run against a manifest of another commit and of the expected one (red first).
- **The source install stages the Claude Desktop helper and its libraries (backlog #58).** `install.ps1` ran `client-tools\install-claude-desktop-extension.ps1` behind a `Test-Path` but never copied it (nor `mcp-clients`, `mcpb-manifest`, `mcp-stdio`, `integration-status` and `process` libraries), so the Claude Desktop step silently never ran; `pack.ps1` already shipped them. Test: `install-arguments.tests.ps1`, which proves the staged list closed over every helper install.ps1 runs and everything they load (red first).
- **One registration pass takes one backup per file (backlog #59).** The Claude legacy cleanup and the add each took a backup of `~/.claude.json`, so `-Rollback`, which restores the newest, put back the state between the two. A pass now reuses its first backup for every step. Test: `register-client-legacy.tests.ps1`, one backup and a byte-exact `-Rollback` (red first).
- **A client that reopens during the quiet window and stays open is `registration_pending` (backlog #60).** `complete-install.ps1` registered anyway when that wait reached its deadline, which wrote underneath the client or was refused and counted as a failed resume attempt. It now records `registration_pending`, exits 3 and counts nothing. Test: `install-completion.tests.ps1` (red first).
### Schema on demand: the `tools/list` facade (phase 1 of 3.0.0)

Design, measurements and client trials: `docs/plans/lazy-schema-3.0.md`.

#### Optional schema surface

- **`full` remains the default; `facade` is opt-in with `HORIZUN_SCHEMA_MODE=facade`.** `horizun_health` stays visible with three server-only tools: `horizun_list_tools` lists reachable names, `horizun_describe_tool` returns exact tool or variant schemas, and `horizun_call_tool` dispatches the same typed call. Direct tool names and the full 2.x-style list remain available by default. The isolated Codex battery could not authenticate (401), and Claude had no quota, so no real agent completion comparison justifies changing the default.
- **Measured over stdio, 2026-10-12** (`{"tools":[...]}` UTF-8 bytes; tokens = ceil(characters/4)): `safe_write` 417,545 B (~104k) -> 4,045 B (~1.0k); `unsafe_code` 478,423 B -> 4,045 B; `unsafe_code` + Python 483,087 B (~121k) -> 4,045 B. On demand: `horizun_list_tools` structured payload 21,136 B in `safe_write`, 23,186 B in `unsafe_code`, 23,370 B with Python. Reproduce with `scripts/schema-facade-probe.ps1`; it explicitly decodes stdout as UTF-8.
- **The facade is the same call, not a second implementation.** `horizun_call_tool` is unwrapped at dispatch into the `tools/call` it stands for, so validation against the exact schema, `schema_help` (failing path + exact-schema URI), the permission profile and toolset gates, task augmentation, progress and cancellation are the direct call's own code. A tool the profile or a toolset switches off is not listed, is described `callable: false` with the gate's own reason, and is refused through the facade with the same text as directly.
- **The contract hash does not move.** The three meta-tools are server-only, not contract rows: a 3.0 server and an add-in from the same contract keep accepting each other. The facade has its own `facade_hash`, reported with the mode, the contract hash and the measured size of `tools/list` in the new `horizun_health.schema_mode` and in `horizun://build/identity`.
- **What the facade loses**, and `full` keeps: the per-tool `_meta.ui` of the MCP Apps (`horizun_clash` and the five impact previews) and per-tool annotations in `tools/list` (`horizun_call_tool` carries the strongest; `horizun_describe_tool` returns each tool's `effect` and `destructive`).
- `initialize` and `server/discover` send the instructions head for the surface served: in facade mode rule 7 names the three tools (2,041 of 2,048 B). `notifications/tools/list_changed` still watches the reachable set, so a client is told to list again.
- **The guidance resource follows the active schema mode too.** `horizun://guidance/typed-first` now explains the facade when it is served, instead of incorrectly claiming every tool schema is in `tools/list`; full-mode guidance remains identical. Its stdio regression test failed before this fix.
- **Facade inputs obey their published nonnullable types.** Explicit `null` for `toolset`, `variant`, or inner `arguments` is refused; omitting optional fields still works. The partial implementation accepted these values contrary to its schema; a new regression test failed before the fix. Profile and toolset restrictions are also checked through the actual stdio dispatcher.
- Tests: `SchemaFacadeTests`, `SchemaFacadeWireTests` (red reproduced before integration: 4 of 6 wire tests failed; nullable regression: 1 failed before the fix). Codex CLI 0.162.0-alpha.17.2 with isolated `CODEX_HOME` recognizes both temporary configurations but has no login; the authorized stdio fallback listed, described and called `horizun_target` in both modes and all measured profiles. Claude Code was not run. `scripts/live/schema-facade.ps1` is **pending live**, syntax-checked only; no Revit was used.

### Steel (stage 1 of 3.0.0)

The gap matrix the stage works from is `docs/plans/acero-3.0-matriz.md`.

#### L1 - steel that crosses from one element to another

- **`into: [element ids]` on `reinforcement_rules` and section layers.** A dowel from the footing into the column, a beam bar anchored in the column, a column bar lapped into the storey above: the bars are hosted in one element and declared into others, and containment is measured against the UNION of the host and those elements - by the plan, by the apply's post-commit check and by the audit, which all report `measured_against` (host first).
  - The face two elements share is clipped out of the boundary (concrete on both sides); two faces in one plane facing the same way stay a boundary (air on that side of both); elements that overlap are one solid.
  - The cover held is the smallest declared by any of the elements.
  - Every element must be a closed solid; an open one is refused as `into_union_not_measurable`, never measured against the host alone. (Non-convex elements: see below.)
  - New refusals: `into_element_not_found`, `into_names_the_host`, `into_needs_a_single_host` (into names ONE host's neighbours; a rule matching several hosts is refused, for section rules too).
  - A section layer that declares `into` may start below its host (`from_mm` < 0) and end past it; without `into` both are refused as before.
- **`anchorage: [{ element_id, min_length_mm }]`** (section layers add `end: start | end`, so a lapped layer checks only the piece at that end): the length of the bar's centreline inside that element's solid, clipped exactly (Cyrus-Beck against its faces), the set anchored as well as its least-anchored bar.
  - The plan measures the declared centreline and refuses `anchorage_short`; short with a hook declared is `decided_after_commit`.
  - The apply re-measures on the bar Revit drew, hooks included (`checks.anchorage`), and the audit reports a new finding, `anchorage_short`.
  - Unmeasurable is `anchorage_not_measured`, never met.
- **Dowels coordinated with the column bars, and continuity between storeys, are one lapped layer**: the layer runs from inside the footing to inside the column above, the lap at the joint makes the dowel and the column bar, and the existing lap check (`lap_insufficient`, overlap and distance across) is the coordination. A dowel declared as its own rule is measured for containment and anchorage; its lap with another rule's bars is not paired (`undeclared_lap` stays in `not_checked`).
- Tests: `UnionContainmentTests`, `AnchorageCheckTests`, `RebarIntoSchemaTests` (red first).
- **Joined elements that are not convex are measured, not refused.** Measured on a real model (Revit 2024): every bar crossing from a beam cut by round columns, or into a notched footing, was refused as `into_union_not_measurable`.
  - Inside-ness was already each element's winding number. The distance to air near a joint is now the nearest face point confirmed to be beside air (exact), or 2562 rays followed to where they leave the union, times cos 2.73 degrees: never more than the true clearance, short of it by at most 0.11 mm at 100 mm and 0.17 mm at 150 mm. A gap between two elements no wider than the chord tolerance is a seam, not air.
  - The anchored length in a non-convex element is cut at every crossing of its triangles, each piece judged by its winding number: exact for a closed mesh.
  - A union of convex elements is measured exactly as before. `how_measured` and `containment.joints` publish the method, the bound and the seam width.
  - Speed, measured: 100 bars sampled every 25 mm across a beam-column joint of 492 triangles in about 4 s; 50 bars across 4812 triangles in about 13 s.
  - A boundary that cannot be read now names the element and its diagnosis, once; it used to say only "the host boundary was not available", twice.
  - Tests: `NonConvexUnionTests`, `BoundaryUnavailableWordingTests` (red first).

#### L2 - columns

- **Crossties** (`ties` on a section rule): one single-leg tie at every intermediate bar of a named layer, from stirrup leg to stirrup leg, beside the stirrup, at the stirrups' zone stations, with declared hooks.
  - Measured: a two-point tie declared `left` is drawn `right` (the apply reports it unverified); declared `right` it is built as declared.
  - Revit carries a 135 degree hooked end on along the bar by half a diameter. The audit accepts exactly that at a hooked end of an open bar, as the info finding `hook_end_extension`.
- **Bayoneta** (`crank: { at_mm, run_mm, inward_mm }` on a layer): the bar moves inward over a declared run and carries on, usually `into` the column above.
  - Refusals: `crank_outside_the_bar`, `crank_with_laps_not_implemented`, and a perimeter crank (four faces).
- **Circular columns.**
  - The section is read off a cylindrical solid.
  - `face: ring` lays N ≥ 3 bars evenly round inside the hoop, each its own single bar.
  - Face layers on a circle are `section_not_rectangular`; a ring on a box is `ring_needs_a_circular_section`.
- **Arcs in `curve_mm`** (`{ through }`, a three-point arc).
  - Measured on Revit 2024: a single arc between two points builds and reads back; every chain with an arc fails inside `Rebar.CreateFromCurves` with an internal error, closed circles included. Those are refused in the plan as `arc_chain_not_buildable_from_curves`.
  - **The circular hoop and the spiral therefore move to L7** (placed rebar shapes, e.g. `M_SP`).
- **Confinement zones in columns** measured live: 100 over 600 mm at each end and 200 between, symmetric, with the ties following them.
- **The audit's length check is now exact.**
  - Revit's `TotalLength` is rounded per bar (measured 3 × 3770 against 3 × 3767.85; the API states the rule in display units), so RebarFacts publishes `measured.bar_centreline_length_mm` and the audit compares that.
  - A bent bar's prediction subtracts R(2·tan(θ/2) − θ) per corner, R = inside bend radius + half the bar (measured: an L in 16M is 2366.2 mm).
- **Live:** `verify-rebar-column-detail` K1-K7 7/7 in Revit 2024 and 2026.
- Tests: `SectionTiesCrankTests`, `RebarArcsAndCircularTests`, `RebarHookEndExtensionTests`, `RebarBentLengthTests`.

#### L3 - foundations

- **Measured live, with the rules that already existed:**
  - a combined footing with two-way bottom and top mats;
  - a pile cap with bottom and top mats and bars on its side faces (mats with horizontal face normals);
  - a strip footing under a bearing wall (the wall foundation is a valid host);
  - a foundation beam below the level (section rule with stirrups).
- **Piles: `axis: "vertical"` on a section rule.** A point-placed STRUCTURAL FOUNDATION is then framed as a vertical member. Only when the rule says so: P0 #8 still refuses every footing that does not, and the probe checks both.
- **A mat over a host with no common cover.**
  - Measured: a strip footing's bottom is "cast against earth" at 75 mm and its sides 40, and a layer declared 74 mm under the bottom was drawn at 82.95 mm (75 + half a 16M bar).
  - The resolver now reads the cover of the exposed face the mat lies under, and a layer whose surface would sit inside it is refused as `mat_offset_below_the_face_cover`, instead of Revit moving it in silence.
- **A closed bar is not bent at the corner it starts and ends at.** Measured: a hookless closed 10M stirrup of 190.5 × 490.5 mm measures 1330.13 mm, which is three bends of R = 24.75, not four. The audit's length prediction now bends three corners.
- **Live:** `verify-rebar-foundations` G1-G8 8/8 in Revit 2024 and 2026.
- Tests: `MatRulesTests` (face cover), `SectionPointHostTests` (axis), `RebarBentLengthTests` (closed bar).

#### L4 - slabs

- **Mat windows: `run` and `spread` on a mat component** (`{ from_mm, to_mm }` from the host's own extents).
  - `run` limits where the bars run along their direction: top bars over a support line (negativos).
  - `spread` limits where the array marches: a band.
  - A window inside the host's covers or past the host is `mat_window_outside_the_host`.
- **Measured live, with existing rules:**
  - a slab with an opening (mat with `openings: omit`);
  - a ribbed slab (joists as beams with section rules, the topping with a temperature mesh);
  - a drop panel (a thicker floor under the slab, with its own mat).
- **New named refusals (L8 items):**
  - **`host_not_structural`:** a floor or wall whose structural flag is off is refused with the parameter's name, instead of the generic `host_ineligible`.
  - **`section_cover_below_the_host_cover`:** a section rule whose declared cover, or zone cover, is below the host's common cover. Measured: joists with a 40 mm cover reinforced from a declared 25 came out with the stirrup array 30 mm short and the bars moved - Revit holds hosted bars to the host's cover.
- **Live:** `verify-rebar-slabs` H1-H9 9/9 in Revit 2024.
- Tests: `MatWindowTests`, `SectionTiesCrankTests` (cover below host).

#### L5 - walls

- **Boundary elements: `wall_boundary_rules`.** `{ id, host: { element_ids: [one wall] }, end: start | end, length_mm, cover, layers, stirrups, ties, mark, required }` - a section rule whose section is the wall's end region: U up the wall, V along it over `length_mm` from the declared end, W across the thickness. The layers, laps, `into`, cranks, stirrups and ties are the section rule's, read by the same parser, and the ids are `<id>#h<wall id>#<layer>` / `...#stirrups`.
  - Refused by name: `wall_boundary_longer_than_the_wall`, `wall_boundary_too_short_for_its_bars` (two covers, two stirrup legs and the largest bar, with the arithmetic), `wall_boundary_host_is_not_a_wall`; a wall that is not a box is `section_not_rectangular`. The resolver reports them as `wall_boundary_rule_refused`.
  - In the schema: a host other than exactly one element id, and a missing or bad `end` or `length_mm`, are refused with their path.
- **Hooks at the ends of a mat's bars: `start_hook` / `end_hook` on a mat component** (`{ hook_type, orientation }`, declared hook types). Only ends at the host's edges carry them; a bar an opening trims keeps them on its outer ends. A full-thickness U return (a hairpin) is not a hook and stays a `reinforcement_rules` curve.
- **Retaining walls and tanks need no new schema**: a mat per face (`face_normal` ± the wall's normal), starter bars from the footing with `into`. A Core test pins two opposite-face mats on one wall standing apart.
- **Live:** `verify-rebar-walls` W1-W5 5/5 in Revit 2024 and 2026 (boundary elements at both ends, two-face mat, dowels from the footing). Not yet measured: a wall at a join, and a horizontal's 180 degree hook toward the other face.
- Tests: `WallBoundaryTests`, `WallMatTests` (red first).

#### L9 - marks and bending schedule (live: the schedule probe Y6, Revit 2024 and 2026)

- **One mark, one bar: `bar_mark_duplicate` is replaced by `bar_mark_conflict`.** Measured live before this lot: the audit raised an error whenever two sets carried the same schedule mark, and Revit itself numbers identical bars with the same Rebar Number, so probes S3, L4 and Z4/Z5 had to excuse the code.
  - Sets are now grouped by mark and each is compared with the first of its mark: bar type and shape by id (exact), and one bar's length (`bar_centreline_length_mm`, else `total_length_mm / quantity`) within the set's `tolerances.length_mm`.
  - The same bar under one mark is not a finding; those marks are listed in the audit's `summary.marks_shared`.
  - A different bar under one mark is `bar_mark_conflict` (error), with `differences` naming every property that differs, both sets and both values. A property that would not read, with nothing proved different, is `unreadable` about `bar_mark_conflict` (unknown), never "the same bar".
  - The live probes no longer excuse a mark code (`verify-rebar-sections`, `-bastones`, `-laps`, `-geometry`); `verify-rebar` R4 accepts either `bar_mark_conflict` or a `marks_shared` entry.
- **`horizun_query_structure mode=bending_schedule`: the cartilla, derived from the model.**
  - Input: `element_ids` / `host_id` as in `mode=quantities`, `density_kg_per_m3` and `density_source` REQUIRED (refused as `density_required` before a bar is read), and optional `length_tolerance_mm` (default 0, exact).
  - Output: one row per mark - bar type, nominal diameter, shape, `segments_mm` read off `centreline_points_as_declared_mm` (collinear points merged), hooks per end, bars, one bar's length and its source, total length, total kg, sets, rebar and host ids - plus `totals_by_diameter`, `totals_by_host` and `totals`, marked `derived_from_model` with a `not_included` list (area and path reinforcement, fabric, linked models, hook dimensions, cut lengths, variable-length sets).
  - A mark over different bars is split into one row per bar and named in `mark_conflicts`, never averaged. A value that would not read is a null total with a `_counted` number beside it, never a zero.
  - Kilograms are Revit's measured volume times the declared density, the same volume `mode=quantities` weighs.
  - The aggregation is pure Core (`RebarBendingSchedule`, with `RebarMarkIdentity` shared by the audit), so the schedule and the audit cannot disagree about what a mark covers. The Revit side gathers RebarFacts rows only, through the same scope reader `mode=quantities` and `mode=reconcile` use.
- **Not measured yet:** no live run. The kilograms rest on Revit's `Rebar.Volume`, whose diameter (nominal or model) was not measured here.
- Contract: the `horizun_query_structure` schema gains `bending_schedule` and `length_tolerance_mm`, and the audit's description names `bar_mark_conflict`. Tool contract hash `96cc159db781d8108e837912` -> `863cc2dab94cd229335a2f4f`. The tools/list ledger was re-recorded through `HORIZUN_UPDATE_TOOLS_LEDGER=1`: query_structure +373 B, audit_reinforcement -1 B, all 123 tools 473,981 -> 474,353 B (headroom 49,935 B); no budget assertion changed. `docs/inventory.json` was not regenerated.
- Tests: `RebarMarkConflictTests`, `RebarBendingScheduleTests` (red first: 30 of 30 failed against stubs).

#### L4b - area, path and fabric reinforcement (live: all three, `verify-rebar-systems` in Revit 2023-2027 at 0a411522; the disposable fixtures carry Revit's default area and path types, created once for the fixture - the bridge still creates no types)

- **Three new arrays in `horizun.structural-requirements/1`**, strict-keyed like the rest: `area_rules` (outline, major direction, area reinforcement type, the active layers each with bar type and spacing), `path_rules` (open polyline, path reinforcement type, declared `flip`, primary bars with bar type, spacing and length) and `fabric_rules` (outline, major direction AND its origin, fabric area and sheet types, optional cover offset).
  - One host, by element id. Every number and name declared; type names matching two types are refused as ambiguous.
  - Outline: three or more points, none repeated, one plane, an area, no self-crossing. Path: two or more, open, one plane. Major direction (and fabric origin) in that plane.
  - Layer words follow Revit: `top_*`/`bottom_*` on a floor, `exterior_*`/`interior_*` on a wall, never mixed or translated. `major_direction_origin_mm` was added to the fabric rule because the sheets are laid out from it and nothing else in the rule says where.
- **Plan**: new `area`, `path` and `fabric` rows beside `reinforcement`. The outline's corners and the path's points must lie on ONE face of the host's own boundary (`boundary_not_on_a_host_face`, `boundary_outside_the_host_face`), the major direction in it (`major_direction_not_in_the_host_face`), the host a floor or wall (`system_host_not_a_floor_or_wall`) whose layer words match (`area_layer_not_on_this_host`), and every type resolved (`area_reinforcement_type_*`, `path_reinforcement_type_*`, `fabric_area_type_*`, `fabric_sheet_type_*`, `_not_found` / `_ambiguous`). All published in `refusal_codes`.
- **Apply**: in the same transaction after the rebar sets; undeclared area layers built inactive, no hooks, path alternating bars off; provenance written on the system element by the same store as bars. After the commit every parameter it set is read back (layers, spacings, bar types, hooks, direction, path length, sheet type, origin, cover offset, mark) and the system must have produced at least one RebarInSystem (fabric: one sheet), or the row is `verified: false` and success is not claimed.
- **Audit**: systems matched by provenance and the same parameters compared (`system_parameter_differs`). Not compared, and named in `not_checked`: `area_reinforcement_geometry`, `path_reinforcement_geometry`, `path_reinforcement_flip`, `fabric_sheet_layout`, `area_reinforcement_layout_rule`.
- API names checked by reflection on RevitAPI.dll 2023, 2024 and 2027 and read from RevitAPI.xml 2027 (`*_FRONT_*` is "Exterior", `*_BACK_*` "Interior", `DIR_1` "Major"); the add-in compiles with 0 warnings for 2023-2027. **Nothing here has been run in Revit yet.**
- Tests: `SystemReinforcementTests` (red first).

#### Stage 1b - the bars of area and path reinforcement are in the takeoff (live: Revit 2025 and 2027 at 44c14c73, `verify-rebar-systems` 7/7 and `verify-rebar-acceptance` 11/11 in each)

- **Fixed: `horizun_query_structure` mode=quantities, mode=reconcile and mode=bending_schedule left out the bars of area and path reinforcement and still said `complete`.** They collected `Rebar` sets only; a system's bars are `RebarInSystem` elements, so a slab's area and path steel was missing from the takeoff, the schedule and the reconciliation under the one word that promises nothing is missing.
  - The three modes read ONE scope (`RebarScope`, Core): rebar sets, described as before with `"system": null`, and every `RebarInSystem`, read into Revit-free facts and turned by `RebarSystemBars.Row` into the same row shape (bar type with both diameters, shape, quantity, Revit's own total length and volume, one bar's centreline, schedule mark, host) plus `"system": { "kind": "area" | "path", "id" }`. A row that would not read completely makes the coverage partial with its reason (`rebar_in_system`).
  - `element_ids` may name an area or path reinforcement element, which scopes to its bars (`scope.system_ids_expanded`); a bar named both ways is read once. An id that is no reinforcement, or not in the document, is named in `scope.ids_not_read` and in the coverage (`element_ids`, partial) instead of being dropped. `host_id` keeps the sets and system bars of that host; a bar whose host would not read is a `host` reason.
  - **Fabric is named, never silently absent.** A fabric sheet's steel is a mass from its type, not a bar volume the declared density weighs, so it is not added in: fabric in scope (on `host_id`, when given) is a `fabric` reason naming each element and the coverage is partial.
  - The takeoff counts system rows per group and in total as `sets_from_systems`; the scope publishes `bar_sets_in_scope`, `system_bars_in_scope`, `fabric_in_scope`. The bending schedule schedules system bars under their mark, names their systems on each line (`systems`), and no longer lists area and path reinforcement in `not_included` (fabric stays). Reconcile compares the same takeoff, so a mark only the system bars carry is `not_in_schedule`.
  - Docs: STRUCTURAL-REQUIREMENT-SET.md, "Steel in scope" under the bending schedule, and the area/path section. Contract: the `horizun_query_structure` description says so; tools/list unchanged (the description is abridged there), ledger not re-recorded.
  - **Measured live, Revit 2025 and 2027:** an area system's takeoff by its id 29 bars / 87 m and a path system's 10 bars / 12 m, each equal to its own bars re-read by parameter, coverage complete; the slab's takeoff 54 bars = mat 15 + 39 system bars, `sets_from_systems` 3, coverage partial with 3 `fabric` reasons (fabric was on the slab); the schedule over the slab 54 bars on 3 system lines. Acceptance A5/A6 with B5's system bars in the hand count: Floors 233/233 bars, 2831.5 kg against 2833.4 by hand (-0.07 %), coverage complete.
  - Live probes: `verify-rebar-systems` Y7 (each system's takeoff by its id against its own bars re-read by parameter, the slab's takeoff = mat + system bars, coverage complete - partial naming fabric when fabric is on the slab - and the schedule over the slab with the same bars); `verify-rebar-acceptance` A5/A6 now include B5's area and path bars in the hand count and A5 requires a complete coverage.
- Tests (red first: 15 of 30 failed against stubs): `RebarSystemBarsTests` (Core); `RebarBendingScheduleTests` now asserts area and path are no longer `not_included`.

#### L7 - placed shapes, couplers, per-face cover (live: `verify-rebar-placed` P1-P5 5/5, Revit 2024 and 2026)

- **Circular hoops and spirals, placed from a rebar shape** (`shape_placement` on a circular section rule's stirrups: `shape`, `kind: lapped_circle | spiral`, and for a spiral `pitch_mm`, `base_finishing_turns`, `top_finishing_turns`, none defaulted).
  - The shape is judged by its definition (`RebarShapeDefinitionByArc` and its `Type`), never its name: `shape_is_not_a_circle`, `shape_is_not_a_spiral`, `shape_definition_unreadable`. On a section that is not circular: `shape_placement_needs_a_circular_section`.
  - A lapped circle is one placed hoop per zone station (`...#<zone>#s<k>`, layout single); a spiral is one zone laid out single, one placed spiral over the zone's length.
  - Built with `Rebar.CreateFromRebarShape`, hooks cleared, fitted with `ScaleToBox` (box = centreline diameter + one bar, as documented) or `ScaleToBoxFor3D`, spiral pitch, turns and height set from the declaration.
  - Re-read: the tessellated drawn centreline against the radius, plane/station or height (`placement_centreline`), the shape's diameter/radius constraint parameter (`placement_size_parameter`), the spiral's four parameters (`spiral_parameters`). Audit: `placed_shape_differs`; the lap's start angle and length are in `not_checked`.
  - A chain with an arc declared as curves is still `arc_chain_not_buildable_from_curves`; a single arc between two points builds, as measured, and needed nothing new.
- **Couplers** (`laps: [{ at_mm | at_ratio, coupler_type_name }]`): collinear pieces meeting at the station, joined end to start by `RebarCoupler.Create`; `coupler_type_not_found` / `coupler_type_ambiguous`; a layer mixing couplers and overlaps, or `toward` beside a coupler, is refused. Re-read with `GetCouplerId` on both bars, the coupler's type and `CouplerLinkTwoBars`. The audit now checks `missing_coupler` and `coupler_type_differs` (removed from `not_checked`; `coupler_incompatible` stays).
- **Cover on one face** (`face: { normal: [x, y, z] }` on a cover rule): every exposed planar face whose outward normal is the declared one gets the cover type (`SetCoverType`), found again inside the transaction and re-read face by face; `cover_face_not_found` lists the normals the host has. Per-face rules do not feed containment's single cover.
- **Free-form bars** stay not implemented: a non-planar centreline is `curve_not_planar`, whose message now says why (`CreateFreeForm` gives a bar with no shape or layout to verify).
- RebarFacts publishes `coupler_readable` per end. New Revit-free code: `PlacedShapes`, `CoverFaceRules`, `RebarAuditRules.CheckCoupler`.
- Tests (red first): `PlacedShapeTests`, `SectionCouplerAndFaceCoverTests`; two existing tests updated to the new truth (a face word is still refused; `missing_coupler` is now checked). Add-in builds 2023, 2024, 2026, 2027 with 0 warnings. Nothing measured live yet.

#### L10 - scale, notation, comparison (live: XL 8/8, Revit 2023-2027 at 0a411522)

- **Large sets in pieces: `chunk` on `horizun_plan_reinforcement`, `horizun_apply_reinforcement` and `horizun_audit_reinforcement`.** `chunk: { by: level | host_type, hosts_per_piece (1-2000), piece_index, partition_sha256 }` runs ONE piece of the set, so a building of tens of thousands of bars becomes N calls a caller submits with `horizun_submit_job` and polls with `horizun_job_status`.
  - The command reads the hosts every selector reaches with the resolver's own host selection and cuts them with `RequirementSetChunks.Split` (now wired to a tool); each rule of the piece keeps its `source_selector`.
  - Every reply names the cut: `chunk.partition_sha256` (the set's sha256, the dimension, the size and every piece's hosts), `piece_count`, `next_piece_index`, the piece's hosts and its own sha256.
  - The apply REQUIRES the digest (`chunk_partition_required`, and the refusal does not hand it back); a cut that moved between two calls of a loop is `chunk_partition_changed`. Also `chunk_bad_request` (including `chunk` with `host_ids`) and `chunk_piece_out_of_range`.
  - The token binds `chunk`; provenance keeps the WHOLE set's sha256, so a whole-set audit later does not call the pieces' bars stale; the audit of a piece judges orphans in its own hosts only.
  - Calls without `chunk` mean what they meant before. The recommended loop is in `docs/STRUCTURAL-REQUIREMENT-SET.md`.
- **`horizun_query_structure mode=parse_notation`** (`lines: [...]`, 1-5000 strings): each schedule cell read by `RebarNotation` into bars, length and spacing or zones in mm, or refused with its code; one refused line never stops the rest, and a request that is not a list of strings is `notation_lines_not_usable`. Reads no model.
- **`horizun_query_structure mode=compare_bars`: bar-by-bar comparison** of two sets of bars of the active model (`element_ids_a` or `host_id_a` against `element_ids_b` or `host_id_b`), under `bar_tolerances: { identical_mm, near_mm, length_mm }` - required, no defaults.
  - A bar is one position of a set that carries a bar: position 0's drawn centreline offset by `GetBarPositionTransform`, as the apply and the audit place it. A free-form set of several positions is reported `unreadable`, not guessed.
  - `RebarBarComparison` (Core): mean point-to-segment distance between centrelines, sampled both ways and the larger kept, and the length difference; same NOMINAL diameter only.
  - One-to-one greedy pairing, identical pairs first, then by distance (documented as not a global optimum). Each bar of A is `identical`, `near` or `unmatched` (with its nearest same-diameter bar of B, however far, and which bar of A took it); B's leftover bars are listed. Verdict `identical` / `incomplete` / `differences_found`; rows paged by `offset` / `max_rows`.
  - Measured offline: 20 000 bars a side paired in about one second (uniform grid over the boxes; nothing that could match is pruned).
- **XL fixture and probe** (live: 8/8 in Revit 2023-2027): `scripts/live/verify-rebar-xl.ps1` builds 320 beams (about 20 500 bars from one section rule), plans every piece, applies and audits each through `horizun_submit_job`, and records seconds per piece against ceilings declared in its header.
- **Schedule cards from DWG and vector PDF:** `horizun_read_schedule` reads extracted DWG entities and vector PDF primitives with PdfPig 0.1.13, returning rows and named refusals for ambiguity.
- **Contract:** new keys `chunk` (plan, apply, audit), `lines`, `element_ids_a`, `element_ids_b`, `host_id_a`, `host_id_b`, `bar_tolerances`, and the two modes. tools/list grew +2,899 B (ledger re-recorded with `HORIZUN_UPDATE_TOOLS_LEDGER=1`; headroom 47,408 B).
- Tests (red first): `RequirementSetPieceTests`, `RebarNotationLinesTests`, `RebarBarComparisonTests` (Core); `SteelScaleContractTests` (Server).

#### Declared hook lengths (live: `verify-rebar-hooklen` 6/6, Revit 2023-2027 at 0a411522)

- **`lengths_mm` on a `hook_types` entry**: `{ "<bar_types id>": mm }`, Revit's Hook Length of that hook on that bar type ("stirrup #3 with 135° hooks of .10" is `{ "N3": 100 }`). It is Revit's own measure, not the straight tail: measured on Revit 2024, 100 on a 10M 135° stirrup hook draws a 71.18 mm tail,. Until now the set could only name a `RebarHookType`, and the extension was whatever the bar type carried.
  - Refused with their path (`hook_types['H135'].lengths_mm['N3']`): an undeclared bar type id, a value that is zero, negative, not finite or not a number, `lengths_mm` on a `none: true` hook, an empty or non-object `lengths_mm`, and two entries giving one pair of type names two lengths.
- **It changes a TYPE.** Revit keeps the length on the bar type, once per hook type (`SetAutoCalcHookLengths` / `SetHookLength`). Read by reflection over the five installed `RevitAPI.dll`: the same members in 2023 to 2027, so no per-year code.
  - `horizun_plan_reinforcement` lists every declared pair in a top-level `bar_type_changes`: current length (auto-calculated or fixed), declared length, `change`, and a sentence that every bar of that type with that hook takes it. A pair already fixed at the declared length is `change: false`; an auto-calculated one is a change even at the same number.
  - `horizun_apply_reinforcement` lists the same pairs in the rehearsal, writes them in the same transaction before any bar is built, and re-reads every pair after the commit (`bar_type_changes`: requested, before, read, verified). A pair that does not read back fixed at the declared length fails the verification; a bar or hook type name that resolves to nothing or to two types is a refusal (`kind: bar_type_change`). The token binds each pair's current state.
  - `horizun_audit_reinforcement` compares each pair once (`hook_lengths`) and reports the new finding `hook_length_differs` (error) for a different or auto-calculated length, `unreadable` when it could not be read.
- Tests (red first): `HookLengthTests` (Core) - parsing and refusals, and the plan, re-read and audit comparison on the row the Revit reader builds (`HookLengthRules.ReadRow`).

#### L10 - reading schedule cards (offline: synthetic PDF/DWG/primitive fixtures and 67 real PDF sheets read for counts only; the DWG route on a real file is not measured)

- **`horizun_read_schedule`** (new tool, host-resident, read-only; toolsets `structure` and `interoperability`): reads reinforcement schedule SHEETS into rows, bar notation and self-checks, and proposes the beam cards as a `horizun.structural-requirements/1` set. Exactly one source per call: `file_path` (a vector PDF), `cad_entities` + `cad_text_anchor` (the entities `horizun_cad_extract` returns for a DWG) or `primitives`. See docs/STRUCTURAL-REQUIREMENT-SET.md, "Reading schedule cards".
  - **One pure reader**, `ScheduleCards` (Core): segments, polylines and positioned texts in drawing millimetres, every window a multiple of the callout's text height. Beam cards (framed card, title = mark, axis labels, `n#k L=x.xx` over its line, hooks walked along the bar's colour - a tick or a bend of chords and a leg, read as 90/135/180 - zones `60e#3` over `c/.30`, `11@.08`, `14#3@.07`, `SON n`, `Son n Vigas`, `B=.. H=..` as written), wall cards (turned callouts beside their bars, level lines, `n#k` with `c/.20`) and bending tables (header texts DECLARED by the caller in `table.columns`; no header dictionary compiled in). Every callout goes through F3 `RebarNotation`.
  - **Declared, never inferred:** `scale` (`scale_not_declared`) and `tolerances { length_mm, zone_span_mm }` (`tolerances_not_declared`).
  - **Refused by name:** `callout_without_line`, `callout_two_lines_equally_close`, `line_claimed_by_two_callouts`, `callout_notation_refused` (with `notation_code`), `hook_ambiguous`, `zone_spacing_missing`, `zone_bar_not_stated`, `horizontal_bar_spacing_missing`, `level_without_line`, `card_frame_not_found`, `card_frame_shared`, `card_kind_ambiguous`, `sheet_has_no_text` (no OCR), `text_formatting_not_read` (MTEXT codes), `cad_text_anchor_not_declared`, the `table_*` codes, and the tool's own `source_not_declared`, `file_not_found`, `pdf_not_readable`, `page_out_of_range`, `too_many_pages`.
  - **Self-checks, each `pass` / `fail` / `not_run`:** `bar_length_matches_callout` (drawn line plus hook legs at the scale vs L), `zone_count_matches_its_length` ((n-1) x s vs the dimension line), `stirrups_sum_to_stated_total`, `zones_fill_axis_span`, `levels_agree_with_scale`, `table_row_total_matches`, `table_rows_sum_to_total`, `cards_match_stated_count`.
  - **The proposal is all or nothing:** each beam card one `section_rules` entry keyed by `mark`, `host_length` layers per face, measured zones; the caller names the bar and hook types. A card with a refusal or a failed check, a baston, a one-end hook or asymmetric zones (unless `card_left_is_host_start`), an unread face or an undeclared type stops the whole proposal, and every reason is listed in `requirement_set_not_proposed`. Held to `StructuralRequirementSet.Load` by the tests.
  - **PDF primitives** (`SchedulePdf`, server): stroked lines with their colour, letters joined into texts; curves and fills counted and left out. MEASURED: PdfPig 0.1.13 already reports a `/Rotate 90` page in its displayed frame, so no rotation is applied (applying one turned every title on its side).
  - **Real sheets, read-only, counts only:** 67 PDFs of one project (none of their content is in the repository). On the beam-card sheets of the style above: 14 to 20 cards per sheet found, every zone read passed its count check, bar lengths mostly within 50 mm; a second drafting style (zones that state no bar) is refused zone by zone. No real set was proposed whole. Figures in the L10 report.
- **Dependency: PdfPig 0.1.13** (Apache-2.0), owner-approved, in the SERVER only - the exact version the add-in already pinned. `src/Horizun.Server/packages.lock.json`, THIRD-PARTY-NOTICES.md, `scripts/sbom.ps1` (every server NuGet package is now a PURL component with its licence, read from the published deps; an unknown package fails the SBOM) and `scripts/pack.ps1` (every server package's runtime assemblies must be staged) were updated; `scripts/sbom.tests.ps1` asserts the PdfPig package and assemblies are Apache-2.0.
- **Contract:** new tool `horizun_read_schedule`; the contract hash changes (server and add-in update together). tools/list grew +3,979 B (ledger re-recorded with `HORIZUN_UPDATE_TOOLS_LEDGER=1`; headroom 43,059 B).
- **Still to measure live:** a real schedule DWG through `horizun_cad_extract` (where the extractor's text point sits for the justifications a schedule uses, MTEXT, colours and layers), and a proposed set planned and applied against real beams.
- Tests (red first): `ScheduleCardReaderTests` (Core, 50) on synthetic sheets (`ScheduleCardFixtures`); `ReadScheduleToolTests` (Server, 11) on synthetic vector PDFs drawn by `SyntheticPdf`, including a `/Rotate 90` page and a page with no text layer.

#### hold_ends - the ends where the plan put them (stage 1b, lot 3; live: E2/E3 Revit 2023-2027, acceptance B1)

- **`hold_ends`** (root bool, or per rule, overriding the set's; off unless asked). The apply re-pins a bar's handles to fixed distances from its host faces and steps them until the drawn bar is within 1 mm of the plan at every vertex, before the commit; after the commit it re-reads every held bar and reports `checks.hold_ends` (`before_mm`, `after_commit_mm`, `verified`). A bar that cannot be held fails the verification. Closed, coupled and placed bars are not held (`applicable: false`). A non-boolean `hold_ends` is refused with its path. See docs/STRUCTURAL-REQUIREMENT-SET.md, "Holding the ends where the plan put them".
- Revit 2025 already has the target-element constraint API (the old pair is obsolete there); 2025-2027 use it, 2023-2024 the old one.
- Tests (red first): `HoldEndsTests` (Core). Live: `verify-rebar-holdends` E2/E3 in all five years; E1 not assessable (the synthetic fixture does not make Revit snap).

#### More than one coupler per layer (stage 1b, lot 5; live: P6 Revit 2023-2027, acceptance B2)

- The refusal `coupled_layer_has_more_than_one_splice` is gone: a layer splits into one piece per stretch, and a middle piece is coupled at both ends (end 0 into the coupler before it, end 1 into the one after it). Measured live: `verify-rebar-placed` P6 (Revit 2024, and the five-year matrix) and acceptance B2 (2025, 2027).
- Tests (red first): `A_layer_with_two_couplers_has_a_middle_piece_coupled_at_both_of_its_ends` (Core).

#### overlapping_bar - bars of different rules against each other (live: `verify-rebar-overlap` 6/6, Revit 2023-2027 at 0a411522; acceptance B3)

- **Measured live, Revit 2024 and 2026:** a wall's horizontals laid through a boundary element's stirrups were pushed by Revit until they touched them, and the array came out 12.5 mm short. Revit moves a bar that crosses another and says nothing. Until now `overlapping_bar` sat in `not_checked`: no check looked at two sets against each other.
- **`clearances` on the requirement set:** `{ between_rules_mm, crossing_mm, pairs: [{ rules: [a, b], min_clear_mm }] }`. These are the minimum clear distances between bar SURFACES, declared and never compiled in.
  - `between_rules_mm` holds bars that run alongside (within 45 degrees).
  - `crossing_mm` holds bars that cross. It is 0 unless declared, because crossing bars are tied in contact.
  - A pair overrides `between_rules_mm` for two declared rule ids.
  - Refused by path: values that are negative, not finite, not a number or over 500 mm; an unknown key; a pair that does not name two DECLARED rules (`reference_not_declared`); a pair without `min_clear_mm`.
  - A chunked piece keeps only the pairs whose two rules it still carries.
- **Without `clearances` only intersections are checked.** These are surfaces that interpenetrate beyond `tolerances.length_mm`. The reply says so (`checked: intersections_only`).
- **Geometry.** Each bar is its centreline polyline (arcs sampled) at every position of its set, with the MODEL diameter. The closest distance between segments is exact; the clear distance is that distance less both radii. The broad phase is a grid: segments are cut into pieces no longer than a cell, and each pair of pieces is measured once, in the lowest cell their boxes share.
- **Excused, each with a test:**
  - bars of one set or one layout (the zones of a stirrup zone rule, the pieces of a mat component);
  - the pieces of a declared lap (in contact, D4) or a declared coupler (end to end);
  - a cage's stirrups and ties against its own longitudinal bars, and its stirrups against its ties (same section or wall boundary rule, same host);
  - hooks. The plan's centrelines carry none; the audit cuts the hook off the drawn bar where the hook-free centreline ends.
  - Excused contacts are counted in `excused`. Two longitudinal bars of one cage, two ties, another cage's bars, starter bars declared as their own rule, and another mat's layers are all checked.
- **Plan and apply (`horizun_plan_reinforcement`, `horizun_apply_reinforcement`)** refuse BOTH sets of a clash, before anything is written. `bars_intersect` is a clash through each other; `bars_closer_than_declared_clearance` is a clash under the declared clearance; `overlap_not_evaluable` means no model diameter.
  - Each refused row carries `overlaps`: the other rule and host, the worst clear distance, the point, alongside or crossing, and the number of bar pairs.
  - The reply's `overlap_check` publishes what was checked, the clearances, the counts, the broad phase and the excuses.
  - A refusal, not a warning: a warning would let the apply build steel that Revit then moves.
- **Audit (`horizun_audit_reinforcement`)** reports the new finding `overlapping_bar` (error), one per pair of sets. It names both rule ids, both bar ids, the clear distance, the required clearance, the point, the kind, the relation and the number of bar pairs.
  - The bars are the ones the model carries: the drawn centreline at every position Revit says exists.
  - A set that cannot be measured is `unreadable` about `overlapping_bar`, never a pass.
  - The reply carries `overlaps`.
- **`not_checked`:** `overlapping_bar` is out. `duplicate_bar` stays: two sets laid as the same bar are not looked for as such, and where they are different rules `overlapping_bar` reports them. `overlap_with_unattributed_bar` is new: bars no rule of the set built are not compared.
- **The rows now publish `belongs_to`:** the declared rule, the layout, the cage and its part in it, and the declared lap or coupler. The section, wall boundary, stirrup zone and mat expansions set it.
- **Speed, measured at a desk:** 21,721 bars in 2,001 sets (ten storeys: 30 x 30 m slabs with four mat layers, 49 caged columns) and 51,121 segments took 1,281,368 exact tests, against about 1.3 billion for all pairs, in 0.85 s.
- **Live probe:** `scripts/live/verify-rebar-overlap.ps1`, 6/6 in Revit 2023-2027. Its first runs failed on the harness, not the product: the column cage's stirrups lacked the host's cover, the beam bar ran through a corner bar outside the beam, and an empty `unreadable` list was counted as one.
  - O1/O2: a beam bar into a caged column. Declared through the column's corner bar, the plan refuses both sets. Placed between two stirrup stations on the column's centre line, under a declared 25 mm clearance, it plans, applies and audits with no `overlapping_bar`.
  - O3/O4: a wall mat against a boundary element. Run through it as measured, with no clearances, it is refused, and the reply says only intersections were checked. Kept out of the region under 25 mm, it plans, applies and audits clean.
  - O5/O6: a second beam and column. Planned as one set, the pair is refused. Built as two separate sets, the audit of the whole reports `overlapping_bar` on the same pair (with `crossing_mm` 5, so a bar Revit pushed into contact is a finding too).
- **`verify-rebar-walls.ps1` changed:** its mat horizontals ran end to end through the boundary regions, across the boundary's vertical corner bars (6.5 mm apart against 12.75 mm of radii), and the plan now refuses that. They now stop 480 mm from the wall's ends.
- Tests (red first): `RebarOverlapTests` (Core).

#### Bars that follow the face of sloped and variable-thickness slabs (live: `verify-rebar-slabface` 4/4, Revit 2023-2027 at 0a411522; acceptance B4)

- **`follow_face: { planarity_mm, min_facet_mm }` on a mat component, and `into: [ids]` on a mat rule.** Measured on a real roof (two floors, a flat strip and a 6% slope meeting at a break), whose bars a flat mat could not lay. Its top and bottom bars run across both pieces bent at the break, and its bars across the slope are each at their own height. The geometry is read from the host meshes the bridge already reads; nothing casts rays.
  - The face is the outermost envelope, along `face_normal`, of the facing triangles of the host and the `into` elements, merged into planar facets within `planarity_mm`.
  - Each bar is that face cut at its station, every facet's plane moved inward by `offset_from_face_mm` along its own normal. It bends where two offset planes meet: the higher in a valley, the lower under a ridge, so the cover is the cover to the face it lies under on both sides. On the roof that is 1.199 mm past the break for the top bars and 1.199 mm before it for the bottom ones.
  - Bends appear only at real breaks. The ends are the face's extent at that station less the end cover.
  - Stations whose polylines are the same, translated, are one Revit set (a run); every other station is its own, a single when alone. A component covered by one run keeps its plain id and layout; otherwise the ids are `#run1`, `#run2`, ... (`#segN` after a trim), deterministic, for the audit.
  - Each set is hosted by the element most of it lies on and declared `into` the others, so containment is measured against their union. Each hosting element's cover is held as a flat mat's host is.
  - Hooks stay on the ends at the face's edge, on bent bars too. The `openings` policy applies to the face's holes (found per element) exactly as on a flat mat.
  - The plan row carries `follow_face`: the polyline, the facets with their normals and deviation, the stations, host and into.
  - New refusals, each with its measurement: `face_steps_under_the_bar`, `face_not_planar_enough_to_follow` (a bend at a curved face's tessellation, or at a curved face the mesh did not attribute), `face_facet_too_small_to_follow`, `no_face_under_the_bar`, `no_face_to_follow`, `mat_into_needs_follow_face`, `follow_face_not_usable`. In the set: a missing or non-positive `planarity_mm` / `min_facet_mm`, an unknown key, and `into` naming the host.
  - Verification and containment are unchanged: each built bar is compared with its planned polyline as any bent bar is.
- **The host mesh now says which triangles came from a curved face** (`HostMesh.CurvedTriangles`, filled where it is read off Revit and carried through the weld and the crack repair), so a follower can tell a tessellation from a break.
- **A flat mat is unchanged.** The ring chaining it uses for openings is shared with the follower and keeps its messages.
- **Live probe:** `scripts/live/verify-rebar-slabface.ps1`, 4/4 in Revit 2023-2027 (T1 the roof's top, T2 its bottom with 180° hooks, T3 a variable-thickness slab made with a variable-layer floor type and `horizun_slab_shape`, T4 the `into` refusal).
- **Contract:** unchanged (the set's keys are not in the tool schema); tools/list ledger not re-recorded.
- Tests (red first): `MatFollowFaceTests` (Core).

### The former 2.3.0 candidate

Minor release over 2.2.0: reinforcement from a detail schedule (F1-F3: section rules, bastones, laps, the bar notation of a schedule and large sets cut into jobs), the `local` toolset for local or short-context models, a `tools/list` that no longer pays for the same argument text once per tool, with a budget test that keeps it from growing back, and a registration that removes the product's own duplicate entries from each MCP client. Tool contract hash `96cc159db781d8108e837912` (2.2.0: `714d1a9ad33dbf3fd90afc6b`); bridge protocol 3, unchanged - a 2.2.0 add-in and a 2.3.0 server refuse each other by contract hash, so update both together, as always.

#### L6 - stairs

- **A stair is reinforced through what it is made of:** the waist is a structural floor sloped with `horizun_slab_shape`, each landing is a structural floor, and both are reinforced with `mat_rules`. The mat is measured on its face (see below).
- **A stair element named as a host follows Revit.** Measured on Revit 2024 and 2026: Revit accepts a cast-in-place stair as a host, and a bar inside its solid is planned like any other. A stair Revit refuses is reported as `stair_not_a_rebar_host`, with the way out.
- **Live:** `verify-rebar-stairs` T1 to T3.

#### A bar exactly on the plane its normal declares

- **Rounding noise is squared away; a bar off its plane is refused by name.** Measured on Revit 2024 (a validation fixture): an oblique column bar whose points had been rounded to 0.001 mm made `Rebar.CreateFromCurves` fail with "An internal error has occurred". The points were 0.0005 mm off the plane perpendicular to the declared normal. The same bar at 0 degrees, where rounding keeps the points in the plane, was built.
- The plan moves the declared points (and arc through-points) onto that plane, through their mean height, when none is further than 0.01 mm from it.
- Further than 0.01 mm, it is `curve_off_its_normal_plane`, with the distance.
- A normal lying in the bar's own plane keeps its own refusal.
- Tests: `NormalPlaneTests`.

#### Cracks in Revit's triangulation

- **A solid whose triangulation leaves a crack is closed rather than refused.** Measured on Revit 2024: a round column in a real model came back with 108 triangles and 4 edges not used once in each direction. Two of its faces tessellate a shared curved edge differently.
- A loop of open edges no wider than the chord tolerance the mesh already declares is closed with triangles that run its edges the other way round. The width is 4 × area / perimeter.
- The mesh's `source` says how many cracks were closed and how wide the widest was. A wider opening is a real hole and is still refused by name.
- Tests: `MeshRepairTests`.

#### Live measurement of the offline lots (Revit 2024 and 2026)

Every rebar probe was run on Revit 2024 and 2026 at `b2514846`. That is 13 probes per year: 77 checks passed, 0 failed, and 2 were recorded as fixture missing. The fixture-missing checks are the area and path reinforcement types the template does not carry.

What the live probes found, and what changed because of it:

- **A column raised off its level was rolled back** (L8). Asked for base +3000 and a 3000 mm height on the first level, `horizun_create_elements` set the base first. That left the column 0 mm tall, Revit posted "Column height is not 0.0", and the error survived to the commit even though the next step made the column valid. The top is now set first whenever the wanted top lies above the current base (`ColumnConstraintOrder`).
- **The end boundary element's corner bars sat 3.96 mm off the plan** (L5). The stirrups at both ends of the wall were identical except for the hooks' second pass. At the start, the hooks' corner rests against the wall's end face, both legs lie on the cover, and Revit laid the pass along the same leg. At the end, the corner fell inside the wall with one leg free, and Revit moved the pass half a stirrup along it, taking the corner bars with it. The end region's stirrup now starts at the corner against the wall's end face, with the same winding.
- **A revolved round column was refused as not circular** (L7). Its end caps' centre vertices sit a hair off the axis and were read as a 0 mm radius. A vertex within the slack of the axis is now treated as on it. The refusal of a curved section that is not round now names the measured extents, the spread of radii and the slack.
- **A stair's waist slab could not be meshed** (L6). For a sloped structural floor made with `horizun_slab_shape`, a mat's extents were taken over the whole solid, whose vertical end cuts reach past the face along the slope. The first bar was laid outside and refused. A mat is now measured on its face when that face is the host's only plane facing that way. A stepped host keeps the whole solid, so containment still refuses a bar over the lower step.
- **A straight set's length is compared against the centreline Revit drew** (L6), not `TotalLength`. Revit rounds `TotalLength` per bar by the host's rebar rounding: a 4432.1 mm bar reads 4430, and nine bars came out 19 mm "short". Revit's figure stays in `revit_reports_mm`, beside `measured_centreline_mm`.
- **Couplers leave a gap** (L7). Measured: a 56 mm CPL16M left the second piece 12 mm short of the station, and no type parameter predicts the gap.
  - A coupled end is now held to what a coupled splice is: it lies on the declared line and inside the coupler's body, with the declared station inside the body too.
  - The distance the end moved is the trim. The apply reports it as `coupled_end_trim_mm`, and the audit as `coupled_end_seated` (info).
  - Length and shape are compared from the trimmed end.
  - Revit seats a coupler at end 0 of both bars, and reverses a bar that offers it end 1, in either order. So the piece before the station is now declared from the station back, with the layer's start hook on its end, turned the other way.
  - ~~A layer with more than one coupler is refused~~ - superseded in stage 1b: two or more couplers per layer are built, the middle pieces coupled at both ends (see below).
- **The row at the stirrup hooks' corner may seat in either pass** (L10, measured in Revit 2024 and 2026).
  - Revit's own tie-break decides which leg the hooks' second pass lies along. Identical beams in one model came out either way depending on their position.
  - The corner bar seats in that pass's bend, so the row lands either 0 or 3.964 mm off the plan.
  - Only that row now carries `hook_corner_seat_mm`, which is half a stirrup. Within it the audit reports `hook_corner_seat` as info; past it, the deviation is still `geometry_differs`.
  - This is the same kind of allowance as decision D1 (the hook opening, accepted within one diameter at the hook corner), and the owner may reverse it.
- **A system type the document lacks** (L4b): the refusal names the types the document has, or says it has none of that kind. Measured: Revit 2024's metric template has no area or path reinforcement type at all, and this bridge does not create types.
- **Measured, not changed:**
  - A bar that crosses another is pushed out by Revit until the two touch. A wall's horizontals laid through the boundary stirrups came out 12.5 mm short.
  - Alone on a wall, the same side cover was honoured to the millimetre: 48, 52, 54.25, 56 and 64 mm were each laid as asked.
  - Bars of different rules were not checked against each other (`overlapping_bar` in `not_checked`). They are now: see "overlapping_bar" above.

#### Client registration

- **Registering the MCP no longer leaves duplicate entries of the same product.** A machine that once carried the product as `horizun` ended with two entries launching the same `horizun-mcp.exe` (measured: Codex with `[mcp_servers.horizun]` and `[mcp_servers.horizun-revit]` side by side). `register-client.ps1` and `install-claude-desktop-extension.ps1` now remove, before registering, the entries of THIS product under another name: the retired names `horizun` and `horizun-next` when they launch a `horizun-mcp` executable, and any name that runs the very executable being registered. Everything else is left alone; a retired name that launches another program is kept and reported, and the run says what it removed. Codex: the table and its subtables are cut in the same write that adds the entry, after a dated backup, and the result is proved to be the original minus those tables (and to still parse when a Python with `tomllib` is at hand) or nothing is written. Claude Code: through `claude mcp remove <old> --scope user`, never a hand edit of `~/.claude.json`; without the CLI the command to run is printed, and project-scoped leftovers are reported. Claude Desktop: removed one member at a time with a backup, skipped with a notice while the app runs. `-KeepOtherEntries` opts out. Every read and write of `.claude.json`, `config.toml` and `claude_desktop_config.json` on these paths goes through the strict UTF-8 reader and the BOM-less, atomic writer of the 2.2.0 audit fixes: the BOM-keeping writer and the `ConvertFrom-Json` / `Set-Content -Encoding UTF8` path the change first carried are gone, so no path can put a BOM back or re-encode a non-ASCII name. `scripts/register-client-legacy.tests.ps1` (temporary profile, stand-in `claude` CLI, child processes under the same PowerShell as the suite) passes under 5.1 and 7; one case runs each client twice with a config holding a non-ASCII name - with an old `horizun` entry and without it - and requires the two results to be the same bytes, without a BOM (red against the change as first written). Not measured: a real Claude Code CLI and a real Claude Desktop.

#### Toolsets

- **`local`: a toolset for local or short-context models.** `HORIZUN_TOOLSETS=local` advertises core, reading (document facts, element listing and queries, model scan, quantities, view capture, navigation, model diff) and six typed writes (parameters, create, transform, delete, views, undo) - about a fifth of the full list: MEASURED 17 tools and 99,771 characters at the default `safe_write` profile (capture_view appears at `full_write`). It never carries the Python surface (a test fails if a tool from `unsafe_code` is added), and a budget test fails above the measurement + 10 %. `horizun_health.toolsets` now also names the toolsets a selection leaves out (`toolsets_not_active`) and how many tools are not advertised (`tools_not_advertised`). Toolset membership is not part of the contract hash, which does not change; the tools/list ledger records the new pack. docs/CLIENTS.md shows how to set it per client.

#### Tools list

- **Short forms for argument texts repeated across tools.** `target_document` (three wordings), `confirmation_token` (two) and the four cooperative-reading arguments of `horizun_list_elements`, `horizun_query_model` and `horizun_model_scan` were advertised word for word in 3 to 8 tools each. The advertised copy now shows a short form that keeps REQUIRED and every argument name; the contract, its hash (`ddabd733db91755296bf1634`) and `horizun://contract/tools` keep every word. MEASURED (every contract row, UTF-8): `tools/list` 475,800 -> 473,188 bytes (-2,612). A budget test now fails when the list grows past 473,188 bytes + 3 %, when a description of 100+ characters is repeated in three or more tools without a short form, or when a short form drops REQUIRED or an argument name. The optional schema facade is documented in `docs/plans/lazy-schema-3.0.md`; `full` remains the default.

#### Reinforcement from a detail schedule (F1)

- **F3: the bar notation of a schedule, read without guessing.** `RebarNotation.Parse` (Core, pure) reads `4#5`, `4#5 L=6.20`, `2#4+1#5`, `4Ø16`, `E#3 c/0.10` and `E#3 1@0.05 10@0.10 R@0.20` into bars, a length and a spacing or zones in millimetres; a decimal comma (es-CO) is read as `MeasuredNumber` reads it, and `1,234` is refused as ambiguous. Everything else is refused with a code (`notation_unrecognized`, `notation_bad_number`, `notation_rest_not_last`, ...). Not yet wired to a tool.
- **F3: a large requirement set cut into pieces for `horizun_submit_job`.** `RequirementSetChunks.Split` (Core, pure) groups the hosts a set targets by level or by host type (category and type name), at most N per piece, and returns one loadable set per piece with every rule narrowed to that piece's element ids - its other filters kept, matched exactly as the resolver reads a selector - and reports rules that target nothing and element ids the inventory does not hold. The live XL measurement (20 000+ bars) is pending. Every rule of every piece keeps its original selector as `host.source_selector` (provenance, never matched on); the loader accepts it, the section expansion carries it and `horizun_audit_reinforcement` echoes it per row.
- **A closed stirrup with hooks at both ends no longer reads as reshaped (D1, option A).** Revit's as-declared form of such a stirrup is OPEN at its first corner, that corner moved half the stirrup's diameter - where it lays the overlapping hooks; MEASURED 4.75 mm for a 10M stirrup, alike in Revit 2024, 2025, 2026 and 2027. The audit reported it as two `geometry_differs` errors (closed vs open, 4.75 mm). It now accepts exactly that: both open ends within one bar diameter of the declared first corner, published as an info finding `hook_corner_opening` with the opening measured; every other point is compared as before, a stirrup without hooks at both ends or open by more than a diameter is still an error (tests `A_hooked_closed_stirrup_open_at_its_first_corner_agrees_and_says_so`, `The_hook_opening_is_not_an_allowance_anywhere_else`).
- **Section rules: reinforcement declared relative to the host's section.** A
  structural requirement set may now carry `section_rules`: faces (`top`, `bottom`,
  `left`, `right`, `perimeter`), counts, bar types, extents and a perimeter stirrup with
  zones, for every member a selector picks — selectors now also take `mark` and
  `level`. The resolver measures each host's frame off its own solid (straight members
  along their line, inclined ones on the slope, columns up their height) and expands
  the rule into ordinary reinforcement and stirrup zone rules (`<id>#h<element>#<layer>`),
  so plan, apply, audit and provenance are unchanged. A member that is not a
  rectangular prism, or whose cover cannot be read, is refused by name. See
  docs/STRUCTURAL-REQUIREMENT-SET.md.
- **Rebar audit: hookless ends reported as turning the wrong way.** Revit reports an
  orientation for a bar end with no hook, decided by the curve and the normal, and the
  audit compared it with the declared default — two `orientation_differs` per straight
  set (measured live). Only a hooked end is compared now.
- **Rebar audit: every bar a zone, mat or section built read as an orphan.** The orphan
  check knew only the set's top-level `reinforcement_rules`, so bars recorded under an
  expanded id (`<zone rule>#<zone>`, a mat component, a section layer) were reported as
  `rule_built_nothing` by their own set. It now knows every id the resolver expanded.
  Measured live on the section-rule probe; the existing zone probe had been accepting
  that finding as known.
- **Rebar apply: a correct straight set refused for the orientation of a hookless end.**
  The apply's re-read compared the termination orientation at ends with no hook, as the
  audit did; both now skip it (one rule, `OrientationApplies`). Measured live: four straight
  16M sets read back exactly and were refused for it.
- **Columns: `span: host_length` and section rules on members placed at a point.** A
  stirrup zone rule could measure its run only along a location curve, so a column's
  ties needed a typed `span_mm`. A member placed at a point now measures its height off
  its own solid (base to top, offsets applied), and section rules lay a column's bars
  and ties in its own section (up its height, the facing as the section's up).
- **Rebar audit: a set of one bar reported as partly audited.** Revit will not report an
  array length or a side for a single bar, and the audit filed both as `unreadable`
  (measured live on a column's single face bars). They are not compared for a single
  bar now, as the first/last-bar flags already were not.
- **`horizun_query_structure mode=reconcile`: the model against its detail schedule.**
  Pass the schedule (`mark`, `bar_type` or `diameter_mm`, `count`, `length_mm` of each
  bar, optional `weight_kg`) and declared `tolerances` (`length_pct` required,
  `weight_pct` optional); every bar in scope is read and compared mark by mark — count
  exactly, total length and weight within the tolerances — and each mark only one side
  has is named (`missing_in_model`, `not_in_schedule`). Weight is compared only with a
  declared density and its source, as in `mode=quantities`. Tool contract: new mode and
  arguments.
- **Bastones: bars laid from one end of a member.** A section rule layer may extend
  `{ from: start | end, run_mm | run_ratio }`: extra bars over a support region, a
  declared length or a share of the member's measured length (0.25 = L/4), the end
  cover kept; a run longer than the member is refused by name.
- **A perimeter baston keeps its run.** A `perimeter` layer is split into its top and bottom pairs, and the split copied every extent field but the F2.2 run, so `extend: {from, run_mm}` on a perimeter layer failed when it was expanded. The run is copied now (test `A_perimeter_baston_keeps_its_run_on_every_face`).
- **Laps (F2.1).** A section rule layer may be spliced: `laps: [{ at_mm | at_ratio, length_mm | length_db }]`, the length in mm or in bar diameters with the caller's multiplier (no code's lap length is compiled in). The layer becomes pieces `...#p1`, `#p2`… overlapping by the declared length; only its own ends keep their terminations. The pieces are not collinear (owner decision D4): every other piece sits one bar diameter across, in contact, toward the inside of the section, or the outside when `toward: "outside"` is asked; a moved piece that would leave the bars' cage is `lap_offset_does_not_fit`, and the laps of one layer name one direction. The audit measures each declared lap off the two centrelines the model returns - `lap_insufficient` when the overlap is short or the pieces do not stand at the declared distance across (one diameter; a collinear pair fails) - lists them in `laps` with `declared_across_mm` and `measured_across_mm`, and publishes `undeclared_lap` in `not_checked` for splices nobody declared. `stagger`, continuity between spans and between storeys are not laid (tests `SectionLapTests`).
- **Two layers on one face no longer stand in each other.** Every layer on a face is spread across the same width, so a continuous pair and bastones on the same face, overlapping along the member, were planned onto the same positions. The plan now refuses that as `layers_coincide`; bastones from each end, which do not overlap, still share a face (tests `Two_layers_on_one_face_over_the_same_stretch_are_refused`, `Bastones_from_each_end_share_a_face`).

#### Pre-release review (2026-10-10)

- **A section rule on a footing is refused instead of laid as a column.** A host placed at a point was framed as a column - its axis up world Z, its facing as the section's up - whatever it was: a `{face: bottom, count: 5}` rule on an isolated footing laid short vertical bars inside a side face, which the plan accepted and the audit agreed with. Only a structural column is framed that way now; any other point-placed host is refused (`section_rule_refused`, "not a structural column") and pointed at `mat_rules`. `SectionPointHostTests` (red before the fix); live probe F3 in `verify-rebar-columns-footings.ps1`.
- **`host_ids` only intersects for section rules too.** The 2.1.7 fix covered cover, reinforcement, stirrup-zone and mat rules; a section rule (new in this release) whose hosts all lie outside `host_ids` is now skipped as well, instead of refused. `HostNarrowingTests` covers the section path (red before the fix).

### The former 2.2.0 candidate

Minor release over 2.1.7: the culture, atomic-write, protocol, command-verification and DWG findings of the 2026-10-08 audit of v2.1.6 that 2.1.7 held back. **Bridge protocol 3** (2.1.7 was protocol 2) and tool contract hash `714d1a9ad33dbf3fd90afc6b` (2.1.7: `6d489de376b5e194786333e8`; D5, the model_diff identity fields, the guard-argument wording and the repair_memory quarantine wording each moved it): the server and the add-in must be updated together, which the installer already does. Arguments are unchanged except where an entry below says otherwise.

#### Breaking

- **Bridge protocol 3.** `tasks/cancel` now really cancels queued work through a new authenticated control message, `__horizun_cancel_job` (see "Protocol and concurrency" below). A 2.2.0 server refuses a 2.1.x add-in and a 2.1.x server refuses a 2.2.0 add-in: the reply names both contract hashes and nothing is sent. There is no partial deployment. `horizun_job_status` can report the new state `cancelled`, which a client that switched on the state names must accept.
- **`horizun_coordination operation=show` reports `elements_painted_requested` in the dry run too.** The apply already said `elements_painted_requested` (6.10 above); the dry run still said `elements_painted`, a name that read as paint that happened when it counts the overrides asked for. A client that reads `elements_painted` from the dry run must read `elements_painted_requested`; what was verified is `overrides_applied` and `shown`. The contract says so, so the tool contract hash changes (`06f6099f32f59ceed40b8377` → `b8a1a5e2b760c6a47022ecd5`).
- **Three writing tools no longer describe their guard arguments as a delete.** `horizun_write_params_verified`, `horizun_set_keynote` and `horizun_family_apply` carried `horizun_delete_verified`'s `target_document` and `confirmation_token` text word for word ("the document to delete from", "nothing is deleted"). They now say "the document to write to" and "nothing is written". Arguments, validation and behaviour are unchanged; the text is part of the contract, so the contract hash moves (`ddabd733db91755296bf1634` -> `5663f2c49479ebbd280926f4`; with the 2.1.7 repair_memory wording merged in, `714d1a9ad33dbf3fd90afc6b`) and a server and add-in of different builds refuse each other as for any contract change. A test keeps the delete wording on `horizun_delete_verified` only.

#### Culture and dates — audit of v2.1.6 (2026-10-08)

- **A decimal comma read as ten times the value.** Requirement-set numeric
  operators (`gt`, `gte`, `lt`, `lte`, `between`) and planimetry rule values parsed
  with `NumberStyles.Any`, which takes `1,5` as fifteen (and `(5)` as -5). Measured
  text is now read by one helper: invariant form, or a single unambiguous decimal
  comma (`1,5`, `0,125`); text that reads equally as a thousands group (`1,234`),
  carries both separators, currency or parentheses is "not a number" and cannot
  pass a numeric bar. `equals`, `not_equals` and `in_list` against a numeric rule
  value compare by value, and against `true`/`false` match a Yes/No parameter's
  stored `1`/`0` - they used to compare with the rule value's current-culture text
  (`"1,5"`, `"True"`) and never matched on a Spanish machine.
- **JSON values turned into current-culture text before being compared.** The CAD
  audit and the CAD update compared a rule's parameter value (`1.5` → `"1,5"`,
  `true` → `"True"`) with the element's display text (`"1,5 m"`, `"Sí"`), so a
  number or a boolean the rule wrote could never agree and was reported as edited
  by somebody. The reader now keeps the STORED value (the double in internal
  units, the Yes/No integer, invariant) beside the display text, and agreement is
  decided by the rule value's type, the way `horizun_write_params_verified` writes
  it. An access-profile threshold given as a JSON number was refused as "no
  numeric value" on a Spanish machine; export preset options were passed as
  `"0,5"`. *Pending live verification* of the reader change in Revit.
- **Timestamps re-formatted when a store was read back.** The diagnostics snapshot
  store, the undo journal, the coordination ledger and the repair memory read their
  own files with `JObject.Parse`, which turns ISO 8601 text into dates. The
  snapshot then re-hashed `...56.1234560Z` as `...56.123456Z` and refused its own
  file as `hash_mismatch` (whenever the fraction ended in 0), and every timestamp
  read back as `12/31/2026 23:00:00`: `undo last` took December 2026 for newer
  than January 2027, the coordination BCF `CreationDate` stopped being an
  `xs:dateTime`, and the repair memory trimmed the wrong entries. They now read
  with dates left as text and order timestamps by instant.
- **A Spanish schedule CSV lost its progress.** A semicolon-separated schedule (what
  a Spanish-locale Excel writes) had its `percent_complete` `37,5` rejected as
  "not a number": the separator was detected, the decimal comma that goes with it
  was not. A `;` file now reads a decimal comma; a cell with both marks is still
  rejected rather than guessed.
- **`horizun_write_params_verified tabular_source` compared a number and wrote
  text.** With `decimal_separator` declared, a cell such as `1,5` was compared with
  the model as 1.5, but the op carried the raw text to `SetValueString`, which
  parses with the document's own decimal symbol - so it could land as 15 or be
  refused. Such a cell is now written as the number it was compared as: converted
  from the parameter's display unit to internal units for a Double parameter, the
  integer for an Integer one. *Pending live verification* in Revit.

#### Atomic writes — audit of v2.1.6 (2026-10-08)

- **A corrupt repair memory was silently replaced by an empty one.**
  `horizun_repair_memory` read a file that did not parse as an empty memory, and
  the next write saved that emptiness over it - every recorded remedy lost without
  a word. The file is now moved aside as `<file>.corrupt-<utc>`, kept unchanged,
  and named in the reply's `memory_problem`; if it cannot be moved aside nothing is
  written. Saves are atomic, and a named mutex spans processes, so Claude's and
  Codex's servers no longer interleave their load-modify-save cycles.
- **Durable records written straight over themselves, or through a shared
  temporary.** The CAD update-operation and CAD link-load records were written
  directly over the live file (a crash left half a record); the Power BI selection
  channel deleted the live selection before moving a fixed `<file>.tmp` into place;
  and the procedure-run record used a fixed `<file>.tmp` that two writers shared.
  All now write through a temporary file of their own and swap it in, waiting out
  a concurrent swap instead of failing. Procedure-run locks no longer distinguish
  run ids that differ only in case (they name one file on Windows), and a Power BI
  selection `channel` of `""` is refused instead of becoming a file called `.json`.
- **`horizun_excel_write_rows` rewrote a held workbook in place.** When another
  program held the workbook without delete sharing (Excel itself, an indexer),
  `File.Replace` failed and a `File.Copy` fallback rewrote the user's file in
  place, non-atomically, and reported success. There is no fallback now: the call
  fails, the workbook is untouched, and the message names the verified new
  workbook (kept as the temporary file) and the backup. When the workbook is
  proved unchanged the idempotency key records the failure instead of staying in
  doubt.

#### Protocol and concurrency — audit of v2.1.6 (2026-10-08)

- **`tasks/cancel` did not cancel.** It recorded the request and its status
  message said queued work "is removed before it starts", but nothing asked Revit
  to remove it, so a queued task ran anyway. It now sends the add-in a new
  authenticated control message, `__horizun_cancel_job`, which takes the durable
  job out of the queue if it has not started and finishes its record as
  `cancelled`; the task becomes `cancelled` only then. A job already running, an
  unanswered request or a submission Revit has not acknowledged yet is reported as
  exactly that (`cancellation_answer`), never as cancelled. `horizun_job_status`
  knows the `cancelled` state. **Bridge protocol 3:** server and add-in move
  together, as the contract hash already required.
- **"NEVER STARTED" could be said of a request that was running.** When a modal
  dialog or a timeout made the add-in give up on a queued request, it decided
  "never started" from a read of the request taken before removing it, and Revit's
  UI thread could claim the request in between; on a timeout, a request that
  finished in that same instant was reported as timed out. The removal now answers
  whether it really removed the request (decided under the queue's lock), only then
  is "never started" said, with a structured `never_started` beside it, and a
  request that finished returns its real result. The server decides on that flag
  (and on the transport's `cancelled_before_start`), no longer on the wording.
  *Pending live verification* in Revit.
- **A finished result was replaced by "cancelled".** When a cancellation arrived
  while a tool call was completing, the server discarded the result it already had
  and answered "cancelled - it may still be running in Revit", hiding work that had
  happened and inviting a second write. A returned result is now delivered as
  such; it is answered as cancelled only when the result itself proves the request
  never started.
- **Smaller protocol and concurrency defects from the same audit.** A task whose
  submission may have reached Revit (sent, no answer) was declared failed and lost
  its idempotency key; it now stays working and `tasks/result` reconciles it by
  replaying the same key. `tasks/get` no longer blocks the server's reader thread
  while another call holds the task (it reports the last recorded state after at
  most one second). A `notifications/cancelled` sent with an `id` is answered
  `-32600` instead of being left unanswered. The add-in keeps four pipe
  connections for control messages, so cancellation and status still get through
  when every ordinary connection is held by a queued command. A refused
  `ExternalEvent.Raise()` now withdraws only its own request or job, instead of
  failing every other one as "never ran". *Add-in changes pending live
  verification* in Revit.
- **Queued tool calls starved the server's thread pool.** Each tool call blocked a
  thread-pool thread for its whole round trip to Revit, and each pipe read blocked
  a second one, so a queue of calls slowed the server's own timers, heartbeats and
  replies. Those waits now run on dedicated threads.
- **One connection too many froze the bridge.** Found live on 2026-10-08: the
  add-in's pipe has no buffer, and with every connection slot taken the listener
  thread wrote its "too many connections" refusal before reading the request; the
  client was writing its request at the same moment, both writes blocked, and the
  listener stopped accepting anyone until that client went away. Refusals are now
  answered on a bounded thread of their own that reads the request first; past
  that bound the connection is closed unanswered. The listener never blocks.
- **Every never-started result carries the structured proof.** The cancel control,
  the shutdown drain and the shutting-down refusal answered "NEVER STARTED" in
  words only, which the server no longer trusts; they now carry `never_started`.

#### Command verification — audit of v2.1.6 (2026-10-08)

- **`horizun_deliver_ifc` left the model modified.** The IFC exporter writes export
  bookkeeping and needs an open transaction; `horizun_export` already rolls that back
  inside an isolation group, but the delivery command committed it. It now exports
  inside the same group, rolled back once the file is on disk, and reports
  `model_isolation` (and `model_changes` when Revit's own flag proves the document
  unchanged) exactly like `horizun_export`.
- **CAD and IFC applies reported provenance they had not proved.** `horizun_apply_cad_plan`,
  `horizun_apply_cad_update` and `horizun_apply_ifc_plan` marked a provenance row
  `written: true` the moment the record was set, before the transaction committed and
  with its commit status ignored. A row is now `written` only after the commit
  returned `Committed` and the record read back off the element; every row carries
  `written_state` (`read_back` / `not_written`), and a commit that does not land
  leaves every row `written: false` with the status named.
- **`horizun_quantities` takeoffs sent internal units under the declared label.** A
  parameter takeoff converted a number only when its spec was exactly Length, Area or
  Volume; any other measured spec (a rebar length, a mass, a force) went out in Revit's
  internal units labelled with the declared unit. It is now converted to the declared
  unit (m, mm, cm, ft, in, m2, ft2, m3, ft3, l, kg, t, N, kN, kg/m), or the reading is
  refused as `invalid` when the unit is unknown or does not measure that spec. The
  volume fallback reads a parameter named "Volume" only when it measures a volume.
- **IDS: sibling `xs:pattern` facets are alternatives.** XML Schema ORs the patterns of
  one restriction step; the evaluator required every one, so a value that followed one
  of two declared conventions failed. One match now satisfies; with no match, a pattern
  that could not be evaluated makes the result undecidable rather than a failure.
- **IDS pre-check compared Revit's internal units against SI bounds.** The model
  pre-check of `horizun_deliver_ifc` / IDS validation read a length in feet and a Yes/No
  as 1/0, so a 3 m wall failed "<= 3.5" at 9.84 and every boolean requirement failed. A
  measured value is now converted to the SI unit the IFC carries (m, m², m³, rad, kg,
  N, Pa…) and a Yes/No reads TRUE/FALSE, as in the file evaluator; a measured value no
  SI unit fits is reported as not established instead of compared raw.

- **Plans from IFC and CAD placed elements off their storey under an elevated base
  point.** `horizun_plan_from_ifc` (wall and column base offsets) and
  `horizun_plan_from_cad` (heights declared above a storey, opening sill and head
  heights) used `Level.Elevation`, which is measured from the level type's elevation
  base, against geometry in the internal frame. With the project base point raised,
  every derived offset was wrong by that height. They now use `Level.ProjectElevation`.
- **`horizun_manage_schedules` removed the wrong fields and filtered lengths in feet.**
  `remove_fields` resolved each `field_index` after the previous removal, so removing
  positions 1 and 2 of A,B,C,D removed B and D; every entry is now resolved against the
  schedule as it stands before the first removal, and two entries naming one field are
  refused. A filter's `number_value` was handed to Revit in internal units (3000 on a
  length field meant 3000 ft); it is now read in the unit the field measures — mm for a
  length, m2 for an area, m3 for a volume, as given when unitless — and any other measured
  field is refused (tool contract: `number_value` describes this).
- **`horizun_transform_elements`: a correct copy of a wall with a door failed, and a
  failed check left no undo.** Copying or arraying a host copies its inserts, and the
  check demanded exactly one created element per source, so it failed after the commit.
  Elements hosted (directly or through another) on a copy are now counted as dependents
  and named (`dependent_created_ids`); anything else Revit made is named as unexplained
  and still fails. The copies of hosted elements must be hosted on a face
  (`hosted_copies_off_host`). The inverse for `horizun_undo` is recorded as soon as the
  commit lands, so a batch that fails its check after committing still carries `undo`.
- **`horizun_write_params_verified` changed parameter definitions without saying so.**
  `allow_vary_between_groups` defaulted to true, so writing a value into a group member
  silently let that parameter vary between group instances for the whole project. It now
  defaults to **false** (behaviour change): a write to a group member whose parameter does
  not vary is refused before anything is written. When the caller allows it, the change is
  listed in `definition_changes`, re-read after the commit, and `horizun_undo` reverts it
  (values first, then the definition). `tabular_source.category` takes one exact OST_
  name, as `sequence.category` does: numbers and comma lists are refused.
- **`horizun_coordination operation=show` reported a view as applied with its colours
  refused.** The verdict looked at the view alone; overrides were read back on a sample
  of 30. Every override is now read back, and the outcome is applied only when the view
  exists, its section box is active at the asked extent and every override landed
  (`shown`, `overrides_requested`, `overrides_applied`, `overrides_not_applied`).
  `elements_painted` (which counted requests) is replaced by
  `elements_painted_requested`; the postcondition is `overrides_applied`.
- **`horizun_undo` refused to undo the first write of a text parameter.** Restoring a
  never-written text parameter writes "" (Revit cannot unset a string), and the re-read
  compared "" with the recorded null, so the undo rolled itself back. For a text
  parameter state the two are now one state; nothing else is compared more loosely.
  Measured live while probing the definition-change undo.

- **`horizun_model_diff` says when "one project" is not proven (owner decision 2026-10-10, option B).** MEASURED in Revit 2023: two different projects made from one Autodesk template share their Project Information identity, and `compare` reported them as 276 elements unchanged, with no warning. Revit 2023 has no document creation GUID to tell them apart, so the comparison still runs, and `compare` and `colorize` now carry `project_identity_kind` (`creation` | `project_info`), `identity_proven` (true only for a creation GUID, Revit 2024+) and, when false, `warnings` with the code `identity_unprovable_2023`; a colorize applied unproven says so in `verified_means`. The contract text names the fields, so the hash changes (tests `A_creation_guid_proves_the_identity`, `Project_information_alone_proves_nothing_and_says_so`).
- **Wall split: three checks that were reported but not enforced.** A pinned wall's
  re-pin was attempted and never re-read — it now is, and a wall that did not take the
  pin back fails the conversion (`verify_pin_not_restored`). The carrier's joins to
  elements outside the split are now held to the ones it had before; a join it gained
  is a refusal. A continuous footing's curve digest, reported before and after, is now
  compared when the carrier did not move.
- **`horizun_navigate` answered success with a selection that did not read back.** The
  re-read flags `host_elements_verified` / `link_instances_verified` were reported beside
  an ordinary reply; a false one now makes the call fail, with the same payload.
- **`horizun_model_diff` compared two different projects, and keyed parameters by a
  translated name.** Snapshots of two projects (different Project Information) are now
  refused instead of reporting every element as added and deleted; a side whose project
  identity cannot be read is refused too. Parameters are keyed by their BuiltInParameter
  or shared GUID (project parameters keep their own name), so a model opened in another
  Revit language no longer reports every built-in value as changed. Snapshot schema is
  now `horizun.model-diff-snapshot/2`; a `/1` snapshot is refused with "take both again".
- **Sleeves could cut a foundation without the structural opt-in.** `horizun_resolve_clash`
  sleeves treated framing and columns as structural hosts by category, but not
  structural foundations; a footing now needs the same opt-in.
- **`horizun_navigate` refused every linked selection after the 6.12 fix.** The selection was read back with `Selection.GetElementIds()`, believed to report a linked element's link instance; measured in Revit 2026, it reports nothing for an element in a link, so `link_instances_verified` was always false and the new refusal failed every linked `selections` call. The selection is now read back by reference (`Selection.GetReferences()`), the way it was set: a linked element is verified as its link instance AND the element inside it, and `linked_elements_verified` is now a real check that the call requires. Contract text updated (Core `SelectionReadBack`, tests `SelectionReadBackTests`; live probe `verify-command-verification-t3b.ps1`).
- **`horizun_model_diff` told two projects apart by an element the template hands to every project.** The 6.13 identity was the ProjectInformation element's UniqueId; measured live in Revit 2024 and 2027, a project made from Autodesk's template and another model from the same template share it, so two different projects still compared as one. The identity is now the document's creation GUID (`Document.CreationGUID`, Revit 2024+; kept by copies of the same project), with the ProjectInformation UniqueId only on Revit 2023; identities of two different kinds are refused rather than compared (Core `ModelDiffRules.ProjectIdentity`, tests `ModelDiffComparableTests`).

- **`horizun_manage_revisions` wrote `issued` in the middle of its edits.** Revit locks an
  issued revision, so creating one with `issued: true` and a description, sheets or clouds
  failed on whatever came after, and an issued revision could never be updated. `issued`
  is now written after every other edit, and an issued revision whose update states
  `issued` is un-issued first (and issued again if asked).
- **`horizun_cad_connect` assumed its transition type changes committed.** The tried and
  the chosen transition types were committed without reading the status, and the type
  was never read back. Both now commit through the guard and re-read the fitting's type;
  a type that did not stick is a refused transition, not a fitted one.
- **`horizun_plan_annotations` read its layout defaults in the caller's units.** The
  defaults (offset 15, chain separation 15, clearance 10, displacement 1200 model / 30
  paper) are millimetres but were taken in `units`, so with `units: "m"` a dimension was
  planned 15 m off its element. They are now converted from millimetres; the tool
  contract says so.
- **Failure words that contradicted their state, and starts never checked.**
  `horizun_mep_routing` said "was rolled back" beside `state: uncertain`; the headline now
  follows the measured rollback. `horizun_pack_sheets` and `horizun_model_diff
  operation=colorize` did not check that their transaction (group) started; a start that
  did not happen now fails before anything is attempted.
- **`horizun_cad_connect` wrote a drawn transition's numbers in the machine's culture.** On an es-CO machine the `means` sentence read "Revit's 241,4 mm fitting … (tolerance 25,4 mm)" beside fields carrying 241.4 and 25.4 (measured live). The sentence is built in Core with invariant numbers (`CadTransitionWords`, tests `CadTransitionWordsTests` under es-CO).

#### DWG and geometry — audit of v2.1.6 (2026-10-08)

- **DWG INSUNITS: four codes converted to the wrong size.** 8 (microinches) read as mils,
  13 (microns) as nanometres, 14 (decimetres) as 0.1 mm instead of 100 mm and 17
  (gigametres) as 1e9 mm instead of 1e12. The table now follows the DXF reference for
  every code 1–24 (including miles, kilometres, ångströms, nanometres, astronomical and
  US survey units); unitless and unknown codes are still refused.
- **CAD audit: a curved wall built as drawn read as a quarter of its length.** The built
  arc's sweep was taken as |a1 − a0|, so a 270° arc read as 90° and its extent was
  reported different. The audit now reads the built arc's own sweep (its length over its
  radius); without it, of the two arcs through the ends the one that agrees with the
  drawn orientation is read. A minor arc built for a drawn major one is still reported.
- **DWG reader: arc angles read as degrees.** The AutoCAD extractor writes the entity
  list's angles, which are radians (a DXF *file* stores degrees; `entget` does not), and
  the reader converted them as degrees — a real extraction's 3.141593..4.712389 became an
  arc of 1.57°. Arcs from the extractor are now read in radians. Found while working the
  lote 7 arc findings, on a cached real extraction.
- **`horizun_acc_upload_status` misread accented file names.** The Desktop Connector log
  was decoded as Latin-1, so a name like "Sección" read as "SecciÃ³n" and never matched.
  It is decoded as UTF-8 now — measured on real connector logs: every non-ASCII name in
  them is valid UTF-8.
- **DWG reader: mirrored entities read in the wrong place.** ARC, CIRCLE, LWPOLYLINE,
  INSERT and TEXT are written in their own object coordinate system, and a mirrored one
  (extrusion 0,0,−1) was read as if its coordinates were world — a mirrored arc landed
  reflected on the other side of the drawing. The extractor now records the extrusion
  direction when it is not +Z and the reader converts with the DXF Arbitrary Axis
  Algorithm; a mirrored INSERT becomes a reflection with its turn reversed, and a tilted
  extrusion (no plan angle) is named in the reading.
- **Nested blocks placed off by their parent's base point.** Placements and definition
  points ignored the block definition's base point (group 10), so a block based anywhere
  but its origin placed its nested symbols shifted by that point. The extractor now
  records each definition's base point and placement subtracts it; extractions made
  before it read as the origin, as before.

#### Smaller fixes

- **Messages no longer carry numbers in the machine's culture.** A rounded decimal concatenated into a reply or refusal followed the Revit thread's culture, so on an es-CO machine a sentence said `241,4 mm` beside a field carrying 241.4 (measured live in `horizun_cad_connect`, fixed there separately). Twenty-seven such sites in twelve commands (code-check travel, element creation, framing, line styles, MEP routing, structure planning, wall split) now format with the invariant culture; a source check (`MessageNumbersInvariantTests`) fails the build on a new one. Concatenation of a non-rounded double is not covered by that check.

- **Every script reads the same under Windows PowerShell 5.1 as under PowerShell 7.** Ten scripts held non-ASCII text (an em dash, an ellipsis) without a byte-order mark; 5.1 reads such a file as the ANSI code page, where the third byte of an em dash is a closing double quote, so `verify-optimization-live.ps1` (which declares 5.1) could not even be parsed there. They now carry a UTF-8 BOM, and `scripts/script-encoding.tests.ps1` (in CI) fails on any script with non-ASCII bytes and no BOM. `dwg-state.tests.ps1` aborted under 5.1 at the first refusal it exists to observe, because 5.1 turns a native command's stderr into a terminating error under `Stop`; its generator call now runs under `Continue`. Both suites pass under 5.1 and 7.

#### Pre-release review (2026-10-10)

- **Text that looks like a date reaches Revit as the same text.** Every hop parsed with Newtonsoft's default, which turns ISO 8601 text into a DateTime: an argument `"2026-10-10T12:00:00+02:00"` reached the command as `10/10/2026 05:00:00` (this machine's zone, invariant culture), was written to the parameter like that, and the re-read compared against the same converted value, so it was reported verified. The stdin reader, both ends of the pipe, the dispatcher and the 95 commands that parse their own `paramsJson` now use `JsonText.ParseObject`, which keeps text as text. `WireDateTextTests` pins the parse and fails on any wire hop that converts again (red before the fix). No contract change.
- **`hz-call.ps1` sends the arguments it was given.** It parsed them with `ConvertFrom-Json` - which under PowerShell 6+ turns ISO 8601 text into a DateTime - and sent that object back out, so `"2026-10-10T12:00:00+02:00"` left the helper as `"2026-10-10T05:00:00-05:00"` before the server saw it. Found while measuring the wire fix live: the helper reproduced the defect on its own. The arguments are still validated, and are now sent as text. `hz-call.tests.ps1` records what a fake server receives (red before the fix).

### The former 2.1.7 candidate

Patch release over 2.1.6 (tool contract: the descriptions of excel_write_rows format and replace, and what a repair_memory quarantine does, changed, so the contract hash changes to `6d489de376b5e194786333e8`; no argument added or removed; bridge protocol unchanged). It carries the security and installer findings of the 2026-10-08 audit of v2.1.6, and the guard that keeps a link's load-state change from crashing Revit when one of its elements is selected (owner decision 2026-10-10; no contract change); the culture, atomic-write, protocol, command-verification and DWG findings are held for 2.2.0 because the protocol change does not fit a patch.

#### Security — audit of v2.1.6 (2026-10-08)

- **`horizun_excel_write_rows format=csv` wrote any file anywhere.** It accepted any
  extension, created missing folders and appended free text, so a `.bat` in the
  Startup folder was one call away without Python permission. It now writes only an
  absolute local `.csv` into a folder that already exists, refuses UNC/device paths,
  alternate data streams and the Startup, Windows, Program Files and `.ssh` folders,
  and goes through the workbook path's lock, backup, temp-and-replace and on-disk
  re-read. Text starting with `= + - @`, tab or CR gets a leading apostrophe. An
  append keeps the file's encoding (UTF-8, UTF-8 with BOM, Windows-1252; a character
  the file cannot hold is refused, never written as `?`), delimiter (a `;` file gets
  `;` and decimal commas) and line ending. Reply fields `encoding`, `delimiter`,
  `newline`, `backup_path`, `replace_method`.
- **`horizun_cde_cloud provider=opencde` sent the token wherever the arguments
  said.** The bearer token went to any `server_url` and to whatever Documents API
  base that server advertised. The token now belongs to the server named in the new
  `HORIZUN_OPENCDE_SERVER_URL`; a `server_url` naming another is refused before any
  request (it may be omitted), and a Documents API on another host receives the
  token only if listed in `HORIZUN_OPENCDE_ALLOWED_HOSTS` — otherwise nothing is sent
  there and the reply is `coverage_complete: false` with the reason. **Behaviour
  change:** an existing OpenCDE setup must add `HORIZUN_OPENCDE_SERVER_URL`.
- **`horizun_budget_compare` ignored `denied_tools`.** Its Excel and Power BI
  destinations called `horizun_excel_write_rows` / `horizun_power_bi_push`
  directly, so an owner who denied either still had it run. Each destination now
  asks that tool's own admission first and is `skipped` with
  `skip_reason: denied_by_owner` (nothing sent or written) when refused. The Excel
  report is no longer a bare overwrite with a backup left beside the file: the new
  workbook replaces the old one under the workbook lock via temp-and-replace, the
  backup goes to the Horizun state folder, and `overwrite_policy` is checked again
  under the lock.
- **Reading an .xlsx inflated every part whole.** `horizun_excel_read_rows` (and the
  write path's own reader) copied each zip entry into memory with no bound, so a
  few kilobytes of crafted package became gigabytes. Parts are now counted while
  they stream and refused past 256 MB each, 512 MB in total, or a 200:1
  inflated/compressed ratio (checked once a part passes 1 MB).
- **Content safety let several invisible characters through.** Variation selectors
  (U+FE00–FE0F, U+E0100–E01EF — enough to hide ASCII one byte per character), the
  soft hyphen, the combining grapheme joiner, the Hangul fillers, U+2065–206F and
  the invisible musical formatting characters now become visible `[U+XXXX]` tokens
  like the rest. One text/emoji presentation selector right after a visible
  character is kept, so `❤️` is unchanged.

#### Installer — audit of v2.1.6 (2026-10-08)

- **The Run-key sweep never removed anything.** `complete-install.ps1` enumerated
  the properties of a one-element array (`Length`, `Count`, …) instead of the Run
  key's value names, on Windows PowerShell 5.1 and PowerShell 7 alike — so stale
  resume entries were never swept and `-CancelPending` (which the uninstaller
  calls) left every `HorizunMCPCompleteInstall-*` entry behind. It now reads the
  key's value names. A generation that ends `registration_failed`,
  `verification_failed` or `completion_failed` retries at sign-in at most three
  times and then removes its own entry; the state records `resume_attempts`,
  `resume_attempts_max` and `resume_cleared`. New test
  `scripts/install-run-entries.tests.ps1` runs against a throwaway key under
  `HKCU:\Software\HorizunTest`, never the real Run key.
- **Registering corrupted the client's configuration under Windows PowerShell
  5.1.** `register-client.ps1` and the Claude Desktop helper read
  `~/.claude.json` / `claude_desktop_config.json` without an encoding (ANSI on
  5.1: `José` → `JosÃ©`), re-serialised the whole document and wrote it back with
  a BOM; and `ConvertFrom-Json` refused keys that differ only in case — routine in
  `~/.claude.json`, one project entry per spelling of a path — so registration,
  and the `verify-install.ps1` check right after it, failed on exactly the
  machines that had used Claude most. A new `scripts/json-member.lib.ps1` edits
  only the `mcpServers.<name>` member in the text: strict UTF-8 in (a file that is
  not UTF-8 is refused and left alone), BOM-less UTF-8 out through a temp file and
  a move, every other byte unchanged. The Codex TOML edit keeps the file's own
  line endings and encoding the same way, and every reader (`verify-install`,
  `verify-clients`, `mcp-clients.lib`) uses the same scanner. New test
  `scripts/client-config-encoding.tests.ps1`: add then remove leaves the file
  byte-identical, under 5.1 and 7.
- **Setup handed the clients a server it had not verified yet.** The Inno
  installer started the Claude/Codex registration helpers before writing the
  deployment manifests and verifying the installed payload, so a verification
  that failed and rolled back left the clients registered against the rolled-back
  files. The helpers now start only after the payload is verified and committed,
  and never after a rollback.
- **Smaller installer defects from the same audit.** `install-release.ps1` waited
  on Setup with `Start-Process -Wait`, which waits for the whole process tree, so
  the bootstrap hung for as long as the completion helper Setup leaves running
  waited for a client or for Revit; it now waits for Setup's own process and
  returns its exit code. The server location is read from Setup's uninstall
  record (`InstallLocation`) instead of assumed, and Setup hands
  `complete-install.ps1` the real `-ServerPath` (which, installed, also finds its
  sibling server). "Is Claude running" is decided by executable path and command
  line (`scripts/client-process.lib.ps1`): Claude Desktop's own process no longer
  blocks Claude Code's registration, and an open Claude Code is no longer reported
  as Claude Desktop. Messages that named Start-menu shortcuts Setup never created
  now print the real command. `install-release.ps1` is kept pure ASCII (5.1 read
  its accented text as ANSI; a BOM would break `irm` use) and its usage names the
  `& ([scriptblock]::Create((irm ...))) -AllowUnsigned` form, since `irm | iex`
  cannot pass `-AllowUnsigned`. `scripts/mcpb-portability.tests.ps1` gained a BOM:
  under 5.1 its UTF-8 `Ñ` (`C3 91`) read as a typographic quote and the file did
  not parse.

#### Live harness

- **verify-live sent probes whose prerequisite was missing.** `Needs` was only checked when reporting, after the probe had run, so "close the open source before loading its detached closed-workset copy" closed the write document even when the closed-workset paths were never discovered, and the whole write tier then stayed shut (measured live in Revit 2026). A probe whose prerequisite is empty is no longer sent; it is reported not_covered as before (`scripts/verify-live.gates.ps1`, `scripts/verify-live-gates.tests.ps1`).
- **verify-live opened an `-OldFile` saved by the running Revit version.** That is no upgrade: in Revit 2023 `HZ_OLD_2023` opened, became the active document and took the write tier with it. The file's version is read from its header first (typed `inspect`, nothing opened); when it is the running version the probe is not_covered with the reason `fixture_same_version`.
- **A Revit crash during a live run is classified, never passed.** Every probe after the crash used to come back UNVERIFIED. verify-live now reads the Autodesk crash dumps written since the run started and files those probes under one verdict. `revit_crash_autodesk_adp` is the known Revit 2027 crash in `AdpSDKCore.dll+0x11b98` off the main thread; it exits 4, and the run is repeated. `revit_crash_unclassified` is any other dump or no dump; it exits 1 (docs/RELEASE-POLICY.md, "Revit 2027 ADP crash").

#### Links

- **Unloading or reloading a link with one of its elements selected crashed Revit.** MEASURED 2026-10-10 in Revit 2026: after `horizun_navigate` selected an element inside a link (by reference, as it must), `horizun_manage_links` unload or reload of that link committed and Revit died about three seconds later (0xC0000005 in RevitDB.dll, on the main thread) - three times, deterministically; the same reload with nothing selected did not. `unload`, `reload` and `change_path` now drop the selection references INTO that link first (the link instance and everything else stay selected) and report how many in `selection_references_cleared` (tests `A_link_change_drops_only_the_references_into_that_link`; live: the same sequence now leaves Revit running).

#### Pre-release review (2026-10-10)

- **A machine with only Claude Desktop no longer ends the installation in `registration_failed`.** Setup, the release bootstrap and `install.ps1` ask the completion helper for `-Client Both`, which skipped the "no client" path: register-client's "neither Claude Code nor Codex is configured" (exit 2) became `registration_failed`, exit 1, and a Run entry relaunched the hidden helper at the next three logons. It now reads as `failed_no_client` (exit 2), names the Claude Desktop helper, and removes its resume entry. `install-run-entries.tests.ps1` covers it under 5.1 and 7 (red before the fix).
- **The release bootstrap reads the completion state instead of assuming it.** `install-release.ps1` printed "complete or safely scheduled" and exited 0 whenever Setup's finisher had written any status in the last seconds, `registration_failed` included. It now reads `state`: a failure stops the bootstrap with the status file named, `failed_no_client` gives the "start the client once" instructions, and an unknown state is not taken as success. `install-release-helpers.tests.ps1` covers the mapping under 5.1 and 7 (red before the fix).
- **A repair_memory quarantine no longer claims a refusal that never happened.** The tool and its contract said "calls matching this exact shape are now REFUSED", but nothing refused anything - and nothing could: a failure shape is the failure's own message, known only once a call has failed. A quarantine now says what it is: the shape is marked do-not-retry, and `advice` on it answers `quarantine` with the reason, who set it, when, and `operation=release` to lift it. The 500-entry bound kept the most recently seen shapes whatever their state, so newer observations could drop a quarantine or a validated remedy; those are now kept ahead of the bound. The contract description changed, so the hash is `6d489de376b5e194786333e8`. `RepairMemoryQuarantineTests` (red before the fix). Refusing an identical call before it runs would need an argument fingerprint per shape: proposed for 2.4.0.
- **create_elements binds a CSV's placements to its token.** With `tabular_source`, the plan hash covered `units` and `elements` only, and each `family_instance` row of the resolved plan recorded type, level and host but not where: a file whose rows kept their count, type and level but moved to other coordinates (or another file at the same path) was applied with the rehearsal's token, although the code comment promised it would refuse as stale. The hash now includes `tabular_source`, and every planned row records `start_mm`, `end_mm` and `rotation_deg`. `PlanHashFieldTests` pins both (red before the fix). No contract change.
- **resolve_clash binds the network it will move.** The apply's token covered only the proposals' text and `clearance_mm`, while the connected network was worked out again at apply time and nothing recorded where its members were: a fitting connected after the rehearsal widened what moved, and a pipe already moved by hand was moved again by the same vector. The rehearsal now records the resolved plan - every member, its finding, mode, vector and location - and the apply recomputes it and refuses a different one as a stale plan. `PlanHashFieldTests` pins it (red before the fix). No contract change.
- **`host_ids` only intersects, as the contract says.** A cover, reinforcement, stirrup-zone or mat rule whose hosts all lay outside `host_ids` was refused as `host_not_found` ("no element in this document matches"), and one required refusal aborts the whole apply: a set applied to the beams of one floor wrote nothing, and the audit of that floor reported differences on a correct model. Such a rule is now skipped; a rule that matches nothing in the document is still refused. `HostNarrowingTests` (red before the fix). No contract change.
- **An IFC whose header contains the word "data" no longer loses its schema.** The STEP reader started the data section at the first "DATA" anywhere in the text, ignoring case, so a header carrying the word - a file named `Datacenter.ifc` in FILE_NAME, an author, an originating system - was cut before FILE_SCHEMA: the schema read as null, and with it `ifc_schema` in plan_from_ifc and validate_ids and the IDS `ifcVersion` check. Checked first on 24 IFC files the bridge exported from Revit 2024-2026: their headers ('ODA SDAI') do not contain the word, so a Revit export trips it only through its own file name. The data section is now the `DATA;` keyword after the header's `ENDSEC;`. `IfcHeaderSectionTests` (red before the fix).
- **IDS reads an IFC4 door's or window's PredefinedType, not the attribute after it.** The evaluator took the first enumeration among an entity's last three attributes; for IFC4 IfcDoor and IfcWindow that is OperationType or PartitioningType, so `PredefinedType=DOOR` failed a door that is a door ("is 'SINGLE_SWING_LEFT'"). Where the schema chain is known the attribute is now read at its real position, and an unset one is reported as unset rather than replaced by the next enumeration. `IdsPredefinedTypeTests` (red before the fix).
- **IDS compares a measure as a number.** A property value such as `IFCLENGTHMEASURE(2100.)` was indexed as the text "2100." (the reader asked for text before number, and text returns an unquoted token as written), so an IDS simpleValue of 2100, or an enumeration of numbers, failed on a string compare; ranges kept working, which hid it. Numbers are now read first; a quoted label stays text. `IdsNumericValueTests` (red before the fix).

## v2.1.6 — 2026-10-03

Patch release over 2.1.5 (tool contract: new optional arguments and reply fields; nothing removed; still 123 tools). It gathers the defects found by two live exercises on copies of the exercise and Autodesk sample models in Revit 2026: the «Comité de obra» run of 2026-10-01 and the «Control 4D/5D con agentes» course rehearsal of 2026-10-03. Each fix was unit-tested offline and replayed live in Revit 2026 on its own branch; the 2023–2027 release gate runs on this tag.

### Course rehearsal «Control 4D/5D con agentes» (2026-10-03)

- **Type bindings are confirmed, not failed.** `horizun_bind_shared_param` with
  `binding_kind=Type` committed and read back correctly, then answered
  `not_bound` / `failed` because it called `SetAllowVaryBetweenGroups`, which Revit
  rejects for a type parameter. Varying between groups is an instance notion: for a
  Type binding `varies_across_groups.state` is now `not_applicable`, the call is
  skipped and the outcome is `confirmed`. Instance bindings are unchanged.
- **Takeoffs too large for the conversation.** `horizun_quantities mode=takeoff`
  accepts `categories: [...]` (one takeoff over several categories, each element
  once) and `rows_file: true`, which writes the complete reply - every row,
  ignoring `top` - to `<data root>/takeoffs/` and returns its path, row count,
  size and SHA-256. Pass `rows_file.path` as `horizun_budget_compare
  model_rows_path`. The location is the bridge's own, so no extra permission is
  needed; a failed write is reported, never thrown.
- **Element ids across Revit years.** `horizun_execute_python` scripts get
  `horizun.id_value(id)`, which reads `.Value` (2024+) or `.IntegerValue` (2023);
  Revit 2026 removed `IntegerValue`. The tool description says so.

### Comité de obra fixes (2026-10-01)

Five defects reported by a live run of the «Comité de obra» exercise in Revit 2026.4 on 2026-10-01. Built for 2023–2027, unit-tested offline, and **measured live the same evening in Revit 2026.4** on fresh copies of the exercise models, through a development session of this build (`0c6a558`, clean tree) with the installed add-in set aside and restored afterwards. Tool contract: new optional arguments on `horizun_query_model` (`source_models`, `link_instance_ids`) and new reply fields; nothing removed.

- **`horizun_export`: the "11 rebar modified" was the bridge's report, not the model.** Measured: Revit's NWC exporter writes while exporting (transaction `Navisworks23`, 12 elements added and 12 modified), yet afterwards `Document.IsModified` is false and the eleven rebar re-read byte-identical — with v2.1.5 and with this build alike — while `DocumentChanged` keeps listing them, because the event that undoes the change does not name them. NWC and IFC now export inside a transaction group rolled back once the file is on disk, and the verdict comes from `Document.IsModified` read before and after: clean→clean states `model_changes` as zero with `proven_unchanged` and the event residue named; clean→dirty says the model was left modified; unsaved work before the export is reported as unverified. Live: NWC `exporter_changes_rolled_back`, `model_changes` 0, no false alarm; IFC complete, document unchanged.
- **`horizun_clash`:** every full-mode row carries `clash_index` (live: 0…265 over 266 rows). `coverage.pairs_without_solid` names the elements behind `pairs.skipped_no_solids`, which used to sit beside an empty `unresolved_pairs`: live, 248 such pairs came down to 19 walls «Standard» of the MEP model's own architecture, each holding one empty solid.
- **`horizun_query_model`:** grids and levels carry a `datum` block in host coordinates (grid line, plan angle, length; level elevation), and a grid's `bounding_box` is the box of its curve (live: 49 grids across host and link, none null; grid 1 at X −12 282 mm in the MEP model and −9 202 mm in the structural link — the survey-point offset the run found by hand). `source_models` / `link_instance_ids` read only the named documents (live: 600 of 731 rows from the link alone; a misspelt title refuses with the available sources). `compact` converts measurable numbers to the host's display units and names them in `parameter_units` (live: column volume 0.556639 m³ where it returned 19.66 ft³). **Behaviour change:** callers that converted compact values themselves must stop; `parameter_format=full` still returns `raw`.

Not fixed, observed live once: linking `LNK-Mirador-redes.rvt` (39 MB) into the architecture copy through `horizun_manage_links add` kept Revit busy (about ten cores, no dialog) for 35 minutes until the session's own Revit was terminated. Revit's journal places the stall inside Revit, before the bridge's code after the commit: the linked file loaded, the active perspective view `From Parking Area` began generating its graphics, and the next entry - `FullUpdateGraphicCacheUpdater::updateAll()` for the link, then `Transaction Successful` - never came; in the course run at 17:13 the same link, host and view reached it in 0.8 s. Not reproduced in four further runs (v2.1.5 and this build, perspective view active, with and without the NWC and IFC exports that preceded the stall): the MEP link loaded in 4-6 s every time. Not attributed to the bridge; `horizun_manage_links` and the failure handler are unchanged on this branch and the bridge's timeout reply was the documented one.

## v2.1.5 — 2026-09-30

Patch release over 2.1.4 (tool contract: new optional fields and one new operation; nothing removed; still 123 tools). It fixes the 18 defects found by an end-to-end dry run on 2026-09-30: seven course flows on copies of the Autodesk sample models in Revit 2026. Every item was built and unit-tested offline, then **replayed live in Revit 2026 on 2026-09-30** on fresh copies of the same Autodesk samples, through the installed build. The live replay found three more defects, fixed in this release:

- **`horizun_verify_changes`: the capture view now draws every discipline.** The first fix (frame the camera on the box) did not cure the blank image. The structural sample's 3D view type is Structural, which does not draw non-structural walls. 68 such walls came back blank in top, isometric and front, while five beams of the same model captured fine. The temporary view now applies no template and uses the Coordination discipline. Measured after the fix: top 19,937 content pixels, isometric 70,009.
- **`horizun_verify_changes`: the crop keeps Revit's depth.** Writing the framed box's depth into a 3D view's crop Z was part of the first fix; it is dropped. The rectangle is still fitted to the box.
- **`horizun_model_scan`: placed schedules no longer count as off-sheet.** `ViewSheet.GetAllPlacedViews()` does not return schedules, so the scan listed placed schedules as off-sheet. The new reconciliation with `horizun_audit_model` exposed it (38 against 35). Schedule placement now comes from `ScheduleSheetInstance`, and the two tools reconcile exactly (34 ↔ 28).

Measured live, per defect:

| # | Result in Revit 2026 |
|---|---|
| 1 | units millimeter, link declares inch, DWG header millimetre: `units_check.verdict: agrees`, applied millimetre by `measured_scale` (0.977 mm per drawing unit) |
| 2 | walls-only plan `applicable` on `link_geometry_only`; `apply_cad_plan` by `plan_id` created 68 walls, `verified_applied` |
| 3 | all 68 walls at Z = 30,000 mm (drawn Z 0) |
| 4 | `M_Concrete-Round-Column: 300mm` refused `column_top_unstated` without `top_level`, `usable_with_warnings` with it; 55 columns planned with `top_level_id` |
| 5 | 10 body rows grouped by Type, Length/Area/Volume totalled (119 in the dry run) |
| 6 | crop active and visible on a placed sheet |
| 7 | the capture is no longer blank (see above) |
| 8 | `cad_extract` with `view_id` answers |
| 9 | 397 beams on 02/03/Roof through `INSTANCE_REFERENCE_LEVEL_PARAM` |
| 10 | `HOST_AREA_COMPUTED` resolves; Type Name gives the 8 wall types with the dry run's totals |
| 11 | sums carry `sum_unit`, metres and the display unit |
| 12 | workbook created; the backup lands in `%USERPROFILE%\.horizun\backups\excel` |
| 13 | `already_open_activated` without `allow_upgrade` on a 2023 header |
| 14 | `horizun_health`: `suspected_instructions: 0` |
| 15 | Project Information write: no `vary_between_groups_error` |
| 16 | 34 ↔ 28 reconcile in both directions |
| 17 | `plan_from_cad` 183 kB → 25 kB; `query_cad` profile 83 kB → 33 kB; `create_elements` summary 4,966 B for 20 walls |
| 18 | `new_project` from Revit's default template, created, re-read and activated |

Still open:
- The clash summary's size is measured offline only: the live model had no clashes.
- A `manage_views` batch that places a view and then crops it fails `place_view`'s centre check and rolls back whole. Cropping first works.
- The `instance` block of a `manage_cad_links add` reply shows `applied_units: null`. `units_check` and every later read carry the applied unit.

### DWG to model

- **`horizun_apply_cad_plan` could never apply a walls-only DWG plan on a normal machine.** The source-set identity behind `sources_match_the_link` is written only by headless AutoCAD (accoreconsole), and only for `blocks`, `solid_hatch_layers` or `section` rules. The dry run's 75 walls were refused `coherence_unknown`, and neither `dwg_path` nor `horizun_cad_extract` could clear it. A plan whose requirement set reads nothing from the file is now judged on the link and the host DWG's hash. The link must have been loaded by this bridge and its geometry must be unchanged. New state `link_geometry_only`, `basis: link_geometry_and_host_file`, `references_checked: false`. A revised host is `revisions_not_aligned`. The strict refusal names what writes the set. The plan's binding records `link_geometry_fingerprint`, so a reload between plan and apply counts as drift. 12 Core tests; live probe `cad-plan-storey`.
- **DWG plans put walls on their level, not on the drawing's Z.** On a level at +30,000 mm, the plan emitted Z = 0. `create_elements` derives the base offset from Z, so the walls would have been built 30 m low and verified there. Walls, floors, ceilings, roofs and absolute-mode families now stand at the resolved level's elevation plus the rule's offset (`storey_placement` in the reply). Creates from `plan_cad_update` follow the same path. 9 Core tests, including +30,000 mm = 98.4252 ft. Still open: flat MEP runs without `offset_mm`, room separators and `plan_cad_update`'s host search still read the drawing's Z.
- **`structural_column` rules can declare `top_level`.** `catalog_check_only` refused `M_Concrete-Round-Column` (TwoLevelsBased) as "cannot be placed without a host", with no way to state its top. The plan now resolves `top_level` to `top_level_id` and refuses a top at or below the base. Without it, the rule is refused as `column_top_unstated`, which names the field. A top on a one-level family is refused rather than ignored. Columns read from closed loops were also placed at the ring's first corner, 150 mm off for a 300 mm column; they now stand at its centre. 8 Core tests.
- **`horizun_manage_cad_links add` checks the unit by the linked geometry's scale instead of stamping `verified_applied` unchecked.** `units: millimeter` gave a link declaring `inch` (`IMPORT_DISPLAY_UNITS` ordinal 2), and the reply said verified because the stamp was a literal. That declaration cannot settle the question: it does not follow a forced unit (verify-dwg-cadlink W2), and on the dry run's drawing it did not match the DWG header either (INSUNITS 4, millimetres, extents 82.4 × 66.4 m). When a unit is asked for, `add` compares the placed geometry's XY diagonal with the drawing's own `$EXTMIN`/`$EXTMAX`, read by the headless reader: a cached extraction or a new header-only read, 5.1 s on that drawing. The new `units_check.verdict` is one of:
  - `agrees` or `applied_header_differs`: verified;
  - `not_applied`: a reused link type kept the header unit (W3); `units_not_applied`, partial;
  - `unconfirmable`: no reader, degenerate extents, or a scale that matches neither; uncertain, never partial.

  `units_check.applied` is kept in the machine's link load record and published per instance as `applied_units` / `applied_units_route`. A contradiction between the declaration and the drawing's INSUNITS is reported in `declared_contradicts_drawing`. 29 Core test cases.
- **The DWG unit gate compares the unit Revit applied.** In the dry run, a link forced to millimetre that still declared inch was refused a millimetre set. `plan_from_cad`, `plan_cad_update` and `audit_cad_model` now use `applied_units` when present, fall back to the declared unit, and name the basis in `source.units_checked`. 5 Core tests.
- **`horizun_cad_extract` with `view_id` no longer throws `DetailLevel is already set`.** The CAD harvest set `Options.DetailLevel` and then `Options.View`, and Revit accepts only one of the two. It now sets one or the other (`GeometryOptionsRules`). A source scan over all 34 `new Options` in the add-in found no other site that set both, and the scan stays as a test.
- Live probe `cad-links-units-extract` (6 cases) covers the unit verdicts, the view-scoped extract and the compact profile. On a machine without AutoCAD the unit cases report `unverified`.

### Documents, schedules and views

- **`horizun_create_schedule` no longer groups a non-itemized schedule by its quantities.** Walls with Type/Count/Length/Area/Volume were sorted and grouped by every non-Count field, which gave 119 rows instead of 8. Fields are now classified from what Revit reports (field type, spec, `IsMeasurableSpec`, `CanTotal`). Identity fields are sorted and grouped in request order. Quantity fields that Revit can total, except Count, get Totals. A new optional `group_by` is honoured exactly; a name that is not one of the requested fields is refused in the rehearsal. The postcondition re-reads `sort_group` and `totals`, and the reply's `grouping` block names each field's role. 11 Core tests.
- **`horizun_manage_views` `set_crop` verifies the crop that governs the view, not just the stored box.** It answered `verified: true` while the placed view still looked uncropped. `CropBoxActive` was already set; the verdict re-read only the CropBox, which Revit stores whether or not it governs the view. The verdict now needs `CropBoxActive`, the Crop View parameter (read independently), the CropBox and the drawn crop shape. A view template that controls the crop, a scope box that drives it, or a sketched crop is refused by name before any write. The row's `crop` block gives the crop's model corners and each viewport's sheet size against the crop. A larger viewport is the named finding `viewport_larger_than_crop`, which points at `set_annotation_crop`. Which of these caused the dry-run case is not yet measured. 11 Core tests.
- **`horizun_verify_changes` frames the camera on the elements and no longer calls a blank image captured.** `orientation=top` on 75 walls at +30,000 mm returned a blank 7.7 KB PNG with `captured: true`. The view kept the default isometric eye and crop depth, both sized for the model as it was. The eye now stands outside the elements' box for every orientation, and the crop depth covers the box. The exported PNG is measured: fewer than max(25 px, 0.05 %) pixels that differ from the background gives `captured: false` with `finding: "blank_image"`. 13 Core tests.
- **`horizun_document_session` `operation: "new_project"`** (improvement). It creates a blank project from `template_path`, or from Revit's `DefaultProjectTemplate` (named in `template_source`), at a `save_as_path` .rvt that must not exist; it never overwrites. It rehearses by default, with a token bound to the template's path, size and write time and to the target. The apply runs `NewProjectDocument` + `SaveAs`, re-reads the file and the document path, removes its own output on a mismatch, and reports `activated`. 25 tests.
- **`horizun_open_document` activates an already-open document without `allow_upgrade`.** A 2023 model upgraded in memory on 2026 and not saved still has a 2023 header on disk, so activating it again demanded `allow_upgrade`. A path already open in the session, with no detach or audit, is now an activation: `version_guard: 'not_applicable_already_open'`. The wrong-bridge and central-model checks still apply. 6 Core tests.
- Live probes `docs-dry-run` (top capture at +30 m, the dry run's wall schedule, set_crop on a placed view) and `document-session-new-project` (also covers the activation path).

### Reading the model

- **`horizun_query_model` resolves levels from where each category keeps them.** `group_by: level` put all 397 Structural Framing, 91 Rooms, 6 Roofs and 5 Stairs under "(no level)". The level read was a `??` chain that stopped at the first level parameter that *existed*, even when it held no level. The level now comes from an ordered list of sources (`LevelResolutionRules`): `Element.LevelId` first, then base and reference parameters including `INSTANCE_REFERENCE_LEVEL_PARAM`, `ROOF_BASE_LEVEL_PARAM`, `STAIRS_BASE_LEVEL_PARAM` and `ROOM_LEVEL_ID`, then a level host, and schedule levels last. A source counts only when it names a real Level. Summaries add `by_level_source`.
- **`horizun_quantities` uses one parameter resolver with `query_model`.** `mode=takeoff` answered "absent" for `HOST_AREA_COMPUTED`, and `classification_parameter: "Type Name"` read "(empty)" on every instance. Both tools now accept BuiltInParameter tokens, GUIDs and names, reading the instance and then the type, with Type Name / Family Name read on the type first. An ambiguous display name is reported as unreadable instead of silently taking the first match.
- **`horizun_query_model` sums name their unit.** `sum_parameters` returned Revit internal units with nothing saying so, so a 38.66 m² room read 416.16. `sum` is unchanged. Each sum adds `sum_unit`, `spec`, `quantity`, `value` + `unit` in m / m2 / m3, and the document's display unit when it can be read. Non-measurable specs are labelled unitless; mixed specs are never given a converted value.
- **`horizun_audit_model` / `horizun_model_scan` view counts say what they count.** The two tools reported 28/36 views off sheets and 34/42 views without a template, with no explanation; schedules account for the difference. No count changed. Each finding adds its scope, a split by view type, what was left out, and the arithmetic that reconciles the two numbers.
- 23 Core tests; four read-tier probes in `scripts/verify-live.ps1`.

### Reply size

- **Opt-in compact replies for the four tools that overflowed the client.** In the dry run `plan_from_cad` answered 232 kB, `create_elements` of 75 walls 262–276 kB, `query_cad` profile 78 kB and `clash` 58 kB, and the client cut each one to a file. `full` stays the default everywhere. Measured offline on synthetic replies:

  | Tool | New option | What it keeps | Full → reduced |
  |---|---|---|---|
  | `horizun_plan_from_cad` | `response_mode: "summary"` | counts, coverage, warnings, coherence, `apply_binding` | 39.8 kB → 5.7 kB |
  | `horizun_create_elements` | `response_mode: "summary"` | every failed or flagged row in full; clean rows as counts and ids | 198,370 B → 1,444 B |
  | `horizun_clash` | `response_mode: "summary"` | totals by category pair and the 10 largest clashes | 51,545 B → 5,041 B |
  | `horizun_query_cad` `mode=profile` | `response_mode: "compact"` | every number; drops the prose | 63 kB → 13 kB |

  - Every DWG plan is now stored for 14 days under `plan_id`, which `horizun_apply_cad_plan` accepts in place of `apply_binding`/`actions`.
  - Profile mode now honours the `layer` glob.
  - Omissions are named in `response_omissions` / `omitted`.

### Smaller fixes

- **`horizun_excel_write_rows` can start a workbook, and its backup no longer lands in the user's folder.** Against a missing path it answered `Workbook not found`. `create_if_missing: true` creates an .xlsx, never replaces an existing file, and goes through the same lock and the same in-memory and on-disk read-back. The per-append backup now lives in `%USERPROFILE%\.horizun\backups\excel\` and is named in `backup_path`. A new workbook takes no backup. 10 server tests.
- **`content_safety` no longer flags the bridge's own sentences.** `horizun_health`'s `recent_horizun_writes.note` and `horizun_audit_model`'s `finding_set_means` name Horizun tools and were reported as suspected instructions. Detection now skips exactly those (tool, path) pairs; the same sentence in an element name or comment is still flagged. 6 tests.
- **`horizun_write_params_verified` stops reporting `vary_between_groups_error` on writes that succeeded.** Filling Project Information showed "does not support allowVaryBetweenGroups" on every row. `SetAllowVaryBetweenGroups` is now attempted only for instance-bound project/shared parameters on instances (`VaryBetweenGroupsRules`). 7 Core tests.

tools/list grows by the new optional fields and the `new_project` operation; see `tests/Horizun.Server.Tests/tools-list-ledger.json`.

## v2.1.4 — 2026-09-27

v2.1.3 was tagged but not released. Its Revit 2024 and 2025 live gates came back 584 passed, 0 failed, 0 unverified, with only the approved exemption not covered - and the gate script then checked the report a second time for not_covered = 0 and failed it. The named exemptions now live in one file, `scripts/release-gate-exemptions.json`, read by verify-live, the gate script, the stable-evidence job and the evidence tools; a report cannot widen it (name, reason and year must all match). Its 2023 gate had two more gaps: the panel-schedule probe now always stages its own panelboard from the year's template (every 2023 panel already had a schedule), and design options in Revit 2023 - which the API cannot create and whose only Autodesk sample has none - joins the list, approved by the project owner.

### From the v2.1.3 tag (not released)

v2.1.0, v2.1.1 and v2.1.2 were tagged but never released; the product is the same in all four. v2.1.2 passed its builds, packaging and installation, and its Revit 2024 and 2025 release gates found 18 failures each - all in the live probes, none in the product: the release model is a workshared central, and probes that opened another model could not give it back (open_document refuses a central), so a dozen later probes refused the active-document check. Fixed and re-run locally on the release runner's exact fixtures (Revit 2024: 755 passed, 0 failed, 0 unverified):

- The workshared-fixture helper gives the release central back through document_session; sync-central, worksets and spatial-links provoke their refusals on scratch copies of a plain model; the sync detached-copy check uses the probe's own closed central.
- The gate carries the owner's ACC sign-in into its isolated data folder and returns a refreshed token; one ACC issue is created under a fixed key, and later runs prove the keyed create does not duplicate it.
- The curtain mullion type comes from the year's Autodesk template; the point cloud cleanup deletes the instance only (Revit's API refuses to delete a PointCloudType, measured); the owner-off sync refusal is proved in the run's isolated settings copy only.
- **Named release-gate exemption** (docs/RELEASE-POLICY.md, approved by the project owner): the floor-on-a-scan case may stay not covered while the runner has only a sparse scan. It is still reported and listed in each report's `release_gate_exemptions`.
- README.md and README.es.md list the 30 improvements of 2.1 one by one.

v2.1.0 and v2.1.1 were tagged but never released. v2.1.0's hosted whitespace gate stopped it before any package was built (trailing whitespace in the vendored IDS schema, one example and two tests; the schema is kept byte for byte and exempted from the gate). v2.1.1 passed every Revit add-in build for 2023-2027, then its hosted `package` job found no .NET SDK 10.0.400: that job and `public-integrity` relied on whatever SDK the hosted image carried, and `global.json` pins 10.0.400 with roll-forward disabled. Both now install it. The product is the same in all three.

Minor release over 2.0.5 (tool contract: new tools, operations and optional fields; nothing removed). Live matrix at the release candidate: Revit 2023, 2024, 2026 and 2027 with 0 failures; 2025 is measured by the release gate.

### Coordination loop, verification coverage, fixtures

- **`horizun_export` `format: "cobie"`: a COBie 2.4 workbook of the active model** (still 123 tools; tools/list +2,510 B). Facility, Floor, Space, Zone, Type, Component and System from a `cobie` mapping that is the caller's data (`created_by` is never taken from the machine; a `project-context.json` can fill project name, site, stage and the classification parameter). A required cell with no source stays empty and is a finding, never "n/a"; duplicate names and broken references are findings too, and `deliverable_ready` stays false while one remains - the workbook is still written. Components are placed in rooms by the product's existing rules (To/From room for doors and windows, `RoomMembershipReader` otherwise). Written by a new dependency-free `.xlsx` writer in Core and re-read from disk cell by cell before `files_verified`. Built and tested offline only (39 Core tests, the `cobie-workbook` probe against fakes; a sample workbook opened in Excel and LibreOffice). **Measured live in Revit 2026 (2026-09-27): 6/6** - an own room, walls and door staged, the seven sheets planned, the workbook written and re-read from disk cell for cell, the own room a Space row and the own door a Component in it, `created_by` on every row; a missing Category is a blocking finding and `deliverable_ready` stays false. The probe had sent a `phase_id` beside a point room, which `create_elements` refuses (it would be ignored): a point room takes the last phase, which the export names. Details: [TOOLS-EXTENDED.md](docs/TOOLS-EXTENDED.md#horizun_export--cobie-workbook-format-cobie).
- **Four-year matrix at 2628cf8 (2026-09-27; 2023, 2024, 2026, 2027 - 2025 is somebody's open session): 547-551 cases pass per year, 2-3 fail, 24-28 not covered.** Every failure was fixed and re-measured live:
  - `horizun_fix_planimetry`'s harness check still counted 9 operations after `set_view_display` made them 10.
  - `horizun_verify_changes` over the session: past the cap it kept the OLDEST writes, so the newest - the one just made - could go unchecked. The union is now newest-first (`SessionScopeRules.Union`) and the truncation note says the oldest are the ones not checked.
  - gbXML failed in 2023 and 2024 with "no main EnergyAnalysisDetailModel ... not compatible with the option set". Measured in Revit 2024 on the SpatialElement/Final model that `Create` had just made main: with the energy settings on RoomsOrSpaces, `ExportEnergyModelType.SpatialElement` (the documented default before 2026) is refused, while `AnalysisMode` (2026's default) writes the space with its constructions; with the settings on building elements, SpatialElement is accepted but writes no construction. 2023-2025 now export `AnalysisMode`: export-formats 10/10 live in 2024 (1 space, 2 constructions) and in 2023 (88 spaces, 6 constructions), energy 6/6 in 2023.
  - Probes: two modules shared the idempotency-key prefix `-rm-` (a Core test now refuses any shared prefix); the curtain ceiling is not covered, never failed, when the own layer types could not be staged; and the curtain probe missed a type the template copy had just brought in 2023 because its type query read one 500-row page of 1459 mullions, and the German-template fixture names the system family `Rechteckiger Pfosten`. The query now pages, and a type name alone is taken only when exactly one type has it. framing-curtain 11/11 live in 2023.
- **Research roadmap, batch 1 - new operations and modes on existing tools (still 123 tools; dispatched operations 435 -> 456; built, reviewed by two adversarial lenses and fixed; measured live in Revit 2026).** Each reports what it could not read by name and never counts it as zero or as a pass.
  - `horizun_quantities` **`room_finishes`**: gross wall, floor and ceiling faces per room or space (phase required), by bounding type, material (the paint when painted) and a caller code; openings facing the room are a separate deduction column, listed one by one with their size basis - never a silent net. **`carbon`**: volume, area and mass per material times the caller's factor table (kgCO2e per m3 or kg); nothing compiled in. Takeoff **`group_by='room'`** and `horizun_query_model` **`include_room`** place elements in rooms for a phase, linked ones through their transform; an element in two rooms is `spans_rooms`.
  - `horizun_fix_planimetry` **`set_view_display`** fixes detail level or discipline citing an audit finding (refused when a template controls it); `horizun_manage_views` **`renumber_sheets`** (collisions checked first, swaps resolved in one transaction), **`create_perspective`** (eye, target, up, or a fan of azimuths) and **`set_sun_study`**; `horizun_write_params_verified` gains a **`sequence`** generator (by level, x, y or room; prefix, start, step, restart per level) shown in the rehearsal and bound by the token.
  - `horizun_plan_mep` **`system_analysis`** reads the critical path, pressure loss, flow and velocity Revit computed (a system Revit does not call well connected is not judged); `horizun_query_structure` **`analytical`** (physical/analytical association, releases, node gaps) and **`loads`**.
  - `horizun_export` **`dwg`** for many views or sheets in one job, **`rfa`** (loaded families to files), **`gbxml`** (re-read space/zone counts; refuses "no spaces"), **`dgn`** and **`dwfx`**; `horizun_code_check` **`energy_readiness`** (energy model built at tier Final inside a rolled-back transaction) and **`headroom`** (vertical rays; a ray that finds nothing is "not measured").
  - `horizun_create_elements` **`placement='all_enclosed'`** for rooms and spaces (one per empty circuit, `min_area` skips shafts) and **`kind=toposolid`** (2024+; refused by name in 2023); `horizun_federation_check` **`levels_match`**.
  - `horizun_document_session` **`sync_with_central`**: off by default and enabled only by the machine owner in Revit (Advanced options); the preview is a labelled estimate bound by the token; ownership re-read by UniqueId against the relinquish choice after the sync; detached copies and non-workshared documents refused.
  - `horizun_manage_links` **`acquire_coordinates`** (real rollback rehearsal, re-read of the active location), **`add kind=point_cloud`**, **`scan_deviation`** (coverage measured by where the points fall; under half coverage is "not measured", never "ok") and **`add kind=ifc`**.
  - `horizun_transform_elements` **`edit_sketch`** for floors, ceilings and openings: the element keeps its id and hosted data; `horizun_budget_compare` **`export_bc3`** (FIEBDC-3 from the takeoff and the caller's prices; a code without a price is refused, never invented).
  - **Measured live in Revit 2026 on 2026-09-27** (own development session, disposable model, every probe staging its own elements): 99 cases pass across the 15 probe modules of the batch, none fails, and 7 are not covered, each for a named reason: three need a point-cloud fixture (.rcp/.rcs) this machine does not have, three need the machine owner to switch on Synchronize with central (a probe never does), and one is by design (a circuit bounded by a link is declared, not proven). What the probes found in the product, now fixed:
    - `set_view_display` re-read the property it was not asked to set without declaring it, so its verdict could never be `all_verified`.
    - `all_enclosed` spaces: Revit's `NewSpaces2` over the plan returns redundant zero-area spaces; they are now named `skipped_zero_area` and never counted as created.
    - DGN export produced nothing without a seed: `DGNExportOptions.SeedName` now comes from Revit's own `ACADInterop` seeds (metric or imperial, 2D or 3D), and a missing seed is refused in the rehearsal.
    - A beam created on a level was re-hosted by Revit on the nearest level below; the requested reference level is set back and regenerated.
    - Links, point clouds and IFC links given a path with forward slashes loaded, but `GetLinkDocument()` returned null; paths are normalized before Revit sees them (`LinkPathRules`). `horizun_document_session open` and `model_diff` compared the requested and the opened path as raw strings and refused a local file they had just opened and activated; the comparison ignores separators and case now (`DocIdentity.SamePath`).
    - `query_structure mode=loads` grouped a load with no load case under an empty key (`""`), which is valid JSON that PowerShell refuses whole. Such a load now reads `load_case.assigned: false`, with its nature and category null by design (not unread), and is counted under `"(no load case)"`.
    - The open guard explains a file that reads as a central but records another file as its central. It is either a copy of that central or a local made by `CreateNewLocal` and never opened: Revit reports both as `IsCentral=true, IsLocal=false`, and only opening and saving the local changes that.
    - A gbXML read-back also counts `Construction` elements (counted, not judged).
  - Probes: they now stage what they measure instead of relying on the fixture:
    - an own room and MEP space for energy and gbXML (HZ_WRITE exports energy by spaces);
    - an own sheet with a placed view for DWG linked, DWG bound and DGN;
    - pipes hung under a floor and run inside one for headroom;
    - an analytical member with its own load case and a 10 kN load for the analytical and loads reads (staged by Python, since no typed tool creates them);
    - a wall inside the link source copy for the clash-against-link case;
    - a door family brought by name for the `.rfa` export;
    - a first open and save of the scratch local before the sync.

    Energy and gbXML also check that the main energy model is unchanged afterwards.
  - Harness: `hz-call` read a reply that PowerShell's `ConvertFrom-Json` refuses as "not JSON" and waited out its 15-minute timeout on an answer it already had. It now reads such a reply as a hashtable, renames the offending keys and says so in `reply_parse_note`.
- **New tool `horizun_framing`: light-gauge wall framing and suspended-ceiling framing from a detail spec** (123 tools; live in Revit 2026: 10/10 - a wall with a door and a window, 30 members at 0 mm; a suspended ceiling, 29 members with 12 hangers on the slab; a 45-degree wall framed with columns and beams; idempotent apply, read and remove).
  - Measured while making it pass: Revit hosts a line on a FACE of an element, so a line-based family refused every created reference plane or sketch plane ("does not coincide with the input face", 0.000 mm off the plane). Line-based members are now horizontal only, placed on their level with the height as an offset, and a vertical line-based member (stud, king, jack, cripple, hanger) is refused by name - those need a Structural Columns type. The re-read adds that level offset: a line-based family's location curve stays on its level, like a wall's location line. A column placed on a vertical line reports a location curve, its real axis.
  - The MCP client reads a wall-type or ceiling detail (image or DWG) into a typed spec with the prompt `framing-from-detail`; the person confirms it; the tool builds it. Families, sizes and spacings are the caller's data - nothing organisation-specific is compiled in.
  - `wall`: studs, bottom/top tracks, kings, jacks, headers, sills, cripples and blocking inside one layer of each straight Basic wall; hosted doors, windows and wall openings are read from the wall and no stud crosses one. `ceiling`: mains, furring, perimeter track and hangers ray-cast to the structure above, re-cast after the commit (`hanger_reaches_support` within 1 mm). `read` and `remove` find the members by a marker that records each member's own UniqueId, so a copied member is never taken for the original's.
  - Every write rehearses first; the token binds the resolved plan (every member's role, type and both endpoints) and, for `remove`, the cascade Revit deletes with the members.
  - **The curtain method** (`spec.wall.method` / `spec.ceiling.method = "curtain"`), for teams that model framing with curtain elements instead of one family per member. The wall core becomes Curtain Wall pieces whose type does the layout (vertical grid at the stud spacing, stud mullions, border tracks, infill panel), split around every opening, with header and sill curtain walls. The ceiling becomes flat Sloped Glazing layers plus hanger curtain walls on the caller's own types (type ids only; nothing compiled in).
    - A wall with several openings keeps its original wall full length as a placeholder (`multi_opening = "keep_carrier"`, the default), so every door keeps its id, tags and data; the pieces overlap it and the reply says so.
    - What changing or deleting the carrier takes along is rehearsed in a rolled-back transaction and bound by the token. Every piece's grid, mullions and line are re-read, and `remove` restores the carrier from the record.
    - **Measured live in Revit 2026 (2026-09-27): 11/11.** Four defects found and fixed on the way:
      - The carrier's centre plane was deduced from its location-line parameter. After that parameter is changed on an existing wall, Revit keeps the curve AND the wall where they were, so a Finish Face: Exterior carrier was trimmed 100 mm off its own plane and its door could not be cut. The centre is now measured on the side faces; the members method uses the same measurement.
      - `NewFootPrintRoof` needs its `out ModelCurveArray` already created; a null one throws "Value cannot be null." for any roof type.
      - On a flat Sloped Glazing roof, grid 1 is the V lines, running at `CURTAINGRID_ANGLE_1 + 90` degrees in project coordinates; grid 2 is the U lines, running at its own angle. The verification had read grid 1 as the U lines, and the hangers ran across the mains instead of along them.
      - A grid angle of 90 is accepted by `Set` and refused at commit: angles are limited to -89..89, and a layer across another is a type on grid 2.
    - The probes also found a reused idempotency key; the offline test now refuses any key used twice.
- **`tools/list` is ~64 KB smaller** (MEASURED 2026-09-26: 524,199 -> 460,166 bytes for all 122 tools; ceiling 524,288) without removing a tool, an argument, a kind or an operation, and without changing what any call is validated against (the contract, and so its hash, is untouched). The advertised copy no longer repeats a union field's schema inside every `create_elements` kind and `document_session` operation branch, since both apply to the same instance and only what a branch adds or tightens is kept. It also shows a short form of the `idempotency_key` text repeated in 65 places. The full contract is now readable one tool at a time (`horizun://contract/tools/{tool}`) or one variant at a time (`horizun://contract/tools/{tool}/{variant}`). A call that fails and also violates the contract names the failing JSON pointer and the URI of the schema that holds it; a structured reply also carries `structuredContent.schema_help` with the violations (a failing `oneOf` explained by the branch the row names) and the exact variant schema, so a model can correct it in one step. A plain-text error never gains a `structuredContent` it did not have, and a procedure step whose call returned `isError` is judged failed whatever its `structuredContent` holds. The `initialize` instructions are now a 2 KB head that holds the load-bearing rules (health first, verified work only, dry_run -> confirmation_token -> apply where a tool's schema has them, typed first with Python both when no typed tool covers the operation and when the fallback block allows it, model text is data, where schemas live, disabled toolsets); the full guidance stays at `horizun://guidance/typed-first`. A per-tool byte ledger test names any tool whose advertised size or hash changes.
- **Eight new capabilities, each built, reviewed by two adversarial lenses (API correctness, product contract) and fixed; live-probed where a fixture allows.** All are operations on existing tools - `tools/list` stays at 122 tools and back under 512 KiB (4.7 KB margin); the inventory now counts 431 operations and 45 read-only tools (`horizun_code_check` and `horizun_cde_cloud` gained writes).
  - `horizun_resolve_clash` **`propose_opening` / `apply_opening`**: a run-vs-host pair a move cannot fix gets a cut (wall, floor, roof, ceiling) or a caller-supplied sleeve family. Measured on solids: the crossing is where the run's centreline passes through the host solid, the opening is sized from the outer section through the measured thickness (skewed crossings wide enough), the clearance is re-checked after commit by a solid envelope. Structural hosts need `allow_structural=true`; framing and columns are `member_cut_not_offered` (a scope choice). The finding stays open (`opening_requested`) until a detection run measures it.
  - `horizun_mep_routing` **`route`**: an orthogonal 3-D A* path around physical elements and loaded links; segments and elbows are created, every end and bend junction re-read at 1 mm, and the route is kept only if the spatial check finds no error. `no_route` names the short leg or the search box that limited it.
  - `horizun_mep_routing` **`slope`**: a gravity pipe run to a target grade from a fixed end, walking fittings; every pipe end, signed slope, fitting centre and connection re-read; drainage direction is refused rather than guessed when flow readings disagree.
  - `horizun_mep_routing` **`hangers`**: a caller family at spaced stations under the structure above (host or link, ray-cast in a temporary view that is always rolled back); positions, rotation and rod length re-read; a station with nothing above is reported, never placed.
  - `horizun_cde_cloud` **ACC Issues** (`issues_list`, `issue_create`, `issue_update`): from a coordination ledger row, 3-legged `data:write` only, idempotent by an external key kept in the issue, read back after the write. Not called live here (it needs the owner's 3-legged sign-in).
  - `horizun_verify_changes` **`clearance_rules`**: equipment working-space zones declared by the caller (category, face, depth), checked on solids against host and links like the door clear zone; `clearance-rules.json` for project rules.
  - `horizun_verify_changes` **`snapshot` / `compare_to`**: a named before/after image with the SAME camera, a red diff, changed regions mapped back to model coordinates and to the elements Horizun wrote since.
  - `horizun_code_check` **egress travel distance** with Revit's PathOfTravel: reported as a lower bound; a pass needs a proven ceiling (convex rooms), otherwise `not_decidable` with the reason.
- **`horizun_split_multilayer_walls` converts walls that host doors, windows and nested components, measured live in Revit 2026 (5/5 on the year's own template types).**
  - The carrier keeps its identity and its inserts: values Revit derives from the host (host id, sill/head, levels, phases, type) may follow the host and are no longer read as a failed copy; a nested shared component is verified through its owner and symbol.
  - A door's frame or trim that reaches a layer wall lining its host is now a spatial **warning** that names the overlap, not a blocked opening: the opening is still cut through the host. Touches under 3 L stay the expected frame contact.
- **The live-matrix failures of 2023, 2024, 2026 and 2027, fixed and re-measured live in Revit 2023 and 2026.**
  - Product: a `space` asserts it contains its placement point only when it is enclosed (an unbounded space is reported with area 0, never refused); `color_by_value` refuses a view that shows none of the categories in the rehearsal, before any token; `create_area_plan` takes `area_scheme_name` and every refusal lists the schemes (an AreaScheme has no queryable category); duct and cable-tray catalogs report nominal sizes only (their inner/outer are constants, measured); a `run_shift` over a network holding an elbow no longer dies on the elbow's captured location shape (`UndoRules.ShiftLoc`, unit-tested); a transient failure reading `settings.json` is retried, and a refusal that fell closed says the file did not decide it instead of claiming it says `read_only`.
  - Probes: the new element kinds (sprinkler, flex pipe/duct, space enclosed and unbounded, area, flex resize) pass 7/7 in 2023 and 2026 - they had read a field the real reply does not carry, and their offline test faked it; design options close the sample they open (a close that discards nothing needs no token), so no foreign document is left for the driver; the resolve-clash link source is kept for the harness manifest, and `run_shift` stages its own level and column; `navisworks_readiness` stages its own view on a fixture without one; resize, colour and overlap cases stand on their own duct, wall and plan instead of whatever the fixture happens to hold.
  - Second pass on the same matrix (15a7270, four years): every remaining failure was the harness, plus one capability the product claimed without ever measuring it.
    - `hangers` now refuses work-plane- and face-based families **before** anything is placed, naming the family and its placement type. Measured in Revit 2023: a work-plane-based generic model placed at a station stayed at z=0 (the rehearsal re-read it and refused, so nothing was committed, but the capability was never there). Level-based families pass 3/3 in 2023 and 2026.
    - Probes: `mep-route` and `resolve-clash` run_shift took "the first" structural-column type, which in the full run is a template-authored column with no height (bounding box z 0..0). The router and the clash had nothing to avoid. They now pick the Autodesk concrete column by name. `mep-hangers` authors its own hanger before falling back to a fixture's generic models. `copy_between_documents` overwrote its tool-name variable (`$c` is `$C` in PowerShell), which counted one unrowed fail in every year; a unit test now forbids that pattern in every probe.

- **The Navisworks loop closes in both directions, measured live (2026-09-26, Navisworks Manage 2026 + Revit 2026).** `horizun_coordination`:
  - `navisworks_readiness` judges the 3D view(s) Navisworks will read: detail level, section box, phase, hidden MEP categories.
  - `prepare_navisworks` (dry run -> token -> apply, re-read) sets those views to Fine.
  - `navisworks_status` turns this ledger's measured verdicts into `navis_set_status` suggestions.

  Second round on the same model:
  1. Readiness caught a Medium view.
  2. Prepare fixed it.
  3. Navisworks detected a new pipe-through-column clash.
  4. Revit reproduced it, showed it, and `resolve_clash` moved the pipe 338 mm.
  5. `navis_set_status` marked the issue Resolved in Navisworks, verified by re-reading the document: zero active issues, without re-running the test.
- **Navisworks reads any 3D view named with "Navisworks" before `{3D}`**, measured by renaming one view: the pipes went from 1-primitive lines back to 1587/1787-triangle solids. `show` named its own view "Horizun - Navisworks <date>", so the product planted the view that spoiled the next export.
  - The default is now "Horizun - Coordination <date>".
  - Such names are refused.
  - Readiness/prepare judge every candidate view (`Core/NavisworksViewRules`).
- **BCF from any tool.** `operation=import` resolves topics from Navisworks, ACC, Solibri or BIMcollab (BCF 2.1/3.0) by their viewpoint components:
  - IfcGuid, through `IFC_GUID` or a Revit-free decode of `ExportUtils.GetExportId` (`Core/IfcGuidCodec`, checked against an external test vector);
  - AuthoringToolId.

  It re-detects each pair: only a reproduced pair becomes a finding (origin `bcf`), and the rest are `not_traceable` with the reason. The ledger CSV export gained six provenance columns: `scope`, `external_source`, `external_issue_id`, `priority`, `responsible`, `immovable_discipline`.
- **`horizun_resolve_clash` sees links and moves connected runs.**
  - Propose's box prediction and apply's solid re-detection carry the moved element(s) into every loaded link, so a new clash against a link rolls back a move like a host one.
  - A connected MEP network (runs + fittings, up to 60 members, all host/unpinned/ungrouped, no boundary connector to equipment) can move as one rigid body (`mode: run_shift`), with every internal connection re-read.
- **Spatial check coverage.**
  - Data-only writes that move geometry are no longer skipped: a bounding-box cache compares before/after, and a never-seen element is checked under a 200-element cap.
  - `horizun_verify_changes scope=session` unions every write since start or `since_utc` (cap 2000).
  - `include_annotation` finds overlapping tags and text notes; a label-only tag without extent is `unmeasured`, never clear.
- **Composite tools read each child's own verdict** (`Core/CompositeVerdict`). `execute_plan`, `apply_ifc_plan`, `apply_cad_plan`, `apply_cad_update` and `cad_connect` stamp an `application` block; a child that merely succeeded without a verified verdict makes the composite partial or uncertain. Fixes along the way:
  - `apply_cad_plan`'s `required_missing` was keyed by parameter name across a batch, so one element's value hid another's gap.
  - `CheckedWriteGroup.Keep()` was trusted without its own outcome.
  - `family_apply` now folds a post-commit invariant breach into a `partial` verdict.
- **Re-reads that were missing.**
  - `manage_views` re-reads created names, `color_by_value` colours, the ids a temporary hide/isolate hid, and legend component detail/position.
  - `export` counts the files produced for non-PDF formats; an unreadable-before file is `unmeasured`, not new.
  - `annotate` no longer accepts a null read-back.
  - `horizun_health` gains `include_verification_catalog`, and names a blocking modal dialog (title, text, buttons, owning module) without clicking it.
- **New element kinds.** `create_elements`: `sprinkler`, `flex_pipe`, `flex_duct`, `space`, `area`, `area_boundary`, each rehearsed, committed and re-read. `ramp` is refused by name, because no Revit 2023–2027 API creates one. `horizun_mep_routing` resize now covers flex runs and checks every fitting it retypes or inserts against the target size; a mismatch fails the plan. `tools/list` stays under 512 KiB by cutting argument descriptions at 250 characters. Inventory: 122 tools, 414 operations.
- **IFC and planimetry.**
  - `deliver_ifc`'s Pset mapping tells an empty Revit parameter apart from a mapping the exporter did not apply (`model_comparison`, from a census before export).
  - `ids_from_loin` converts length/area/volume bounds to the IFC default unit (`converted_units`).
  - `fix_planimetry set_crop` accepts a polygon, verified vertex by vertex.
- **Defects found by running things live.**
  - `create_elements` rolled back every beam: a framing member has no `Element.LevelId`, and the level now comes from its Reference Level.
  - `copy_between_documents` gave a clean dry run and then refused on type-name collisions. The rehearsal now performs the copy in a transaction that is always rolled back, and names them.
- **Fixtures that were missing.**
  - Workset writes run on a detached copy of the closed-workset fixture.
  - A panelboard is staged from the year's electrical template for the panel-schedule case.
  - Design options are read from Autodesk's own sample of the year.
  - The library document is the year's own template (`{year}` in the path).
  - `verify-rebar-geometry.ps1` ran live for the first time: 19/19, including Z5, M6 and M7.
- **`horizun_cde_cloud` measured live against ACC.** On a test project, 2-legged, read-only: `list_projects`, `list_states`, `inspect` (58 files, 30 calls) and `versions`.
- **Power BI coordination dashboard** (`examples/coordination-dashboard`): a `.pbip` over the ledger CSV. It was opened, refreshed and read back by DAX in Power BI Desktop; the sample data is synthetic.
### ISO 19650 information management

- **Verified is not the same as right: a spatial coherence check after every write.** Field use (2026-09-25) left a column and a door in the same place with every postcondition true. The dispatcher now watches `DocumentChanged` for the duration of each call (`Core/ChangeWatch.cs`), keeps what the call added or modified after its own rollbacks, and checks those model elements (`Core/SpatialCoherence.cs`): shared solid with every intersecting element (`ElementIntersectsElementFilter` + `BooleanOperationsUtils`), judged by unit-tested rules (`Core/SpatialCoherenceRules.cs`) - an opening blocked by a column, wall, MEP or furniture, a duplicate of the same type, MEP through structure are errors; unjoined overlaps and contents in structure are warnings; hosts, joins, MEP connections, curtain members, the structural frame and MEP through enclosures are expected. A door clear-zone pass flags a column or wall IN FRONT of a door that touches nothing. Replies carry `spatial_check` and `attention` as their first key; data-only tools are skipped; bounded (800 elements, 8 s) and `partial` when a bound stops it; `HORIZUN_SPATIAL_CHECK=off` disables it. New read tool `horizun_verify_changes` (122 tools) checks the last write (or given ids) and returns an IMAGE from a temporary isometric view in a rolled-back transaction group, changed elements blue, errors red, warnings orange. Every call that changed a document also carries `model_changes` (added/modified/deleted as Revit reported them).
- **Measured live, 2026-09-25/26.** In a dev session on Revit 2026: a door alone came back clean; an architectural column placed in its doorway came back with `attention` and two errors ("door is blocked by column", 0.051 m³ shared, and "column stands in the passage"); a column 700 mm in front of the door, touching nothing, with "column stands in the passage"; `horizun_verify_changes` returned the image with all three in red and the temporary view rolled back. Five-year matrix at 556452c (run-year-matrix + verify-live -WriteProbes): 2023 355 passed / 1 failed, 2024 357/0, 2025 357/0, 2026 364/0, 2027 357/0 - every spatial case passing in every year. The 2023 failure (create_shared re-read after the commit threw: Revit 2023 invalidates the ExternalDefinition) is fixed at 8c98699 by re-reading through a GUID copied before the commit, and Revit 2023 re-measured at 17e51f3: 356 passed / 0 failed, create_shared and all six spatial cases passing. Found on the way and fixed: architectural columns (TwoLevelsBased) were refused by create_elements - they are now placed on their base level, and any column takes `top_level_id`/`top_offset` or `height`, set and read back; `copy_between_documents` accepts 'Family: Type' names, lists the types that exist when one is not found, and a refusal for a type-name collision says which names and how to proceed instead of Revit's "User cancelled the operation"; sheet-set update no longer saves an unchanged membership (Revit threw "Save of the setting was unsuccessful") and `sheet_set_list` answers in the rehearsal; `model_diff explain` never emits two keys differing only in case (a model had "Center Line" and "Center line", which made the reply unreadable to PowerShell). Inventory: 122 tools, 403 operations.
- **The operations pane said "failed" on every row.** It read `receipt.success`, a field the ledger never wrote (`outcome=ok|failed`), and `document_title` instead of `document`. Rows now show the result in words (changed the model / rehearsal / read / failed) and one sentence in Revit's language saying what was done (tool phrase, request operation or the script's new optional `purpose`, added/modified/deleted, spatial findings); reads and rehearsals are hidden unless asked for (`Core/OperationDescription.cs`, tested).
- **Field-session fixes (2026-09-25).** `horizun_catalog_lookup` detects its delimiter (tab, `;`, `,`, `|`; a tie refuses) instead of assuming comma - a tab catalog declared every valid code absent - and gains `code_column`/`description_column`/`has_header` and `operation=search` by description. `horizun_execute_python run_async=true` now forwards `arguments` (they arrived as `{}`), accepts evidence given as a single object (it was reported "empty"), and gains `read_only=true` (always rolled back, re-checked with `IsModified` and a census; Save/SaveAs/Close/open refused before running; no lower permission). `horizun_health` reports owned worksets, a bounded borrowed-element scan and Horizun's own recent writes (Revit's Undo stack is not readable by an add-in, and says so). `horizun_delete_verified` previews and re-verifies parameter and global-parameter deletions. `horizun_manage_worksets` measures `ownership_effect` (a rename silently took 14,697 elements) and offers `relinquish_after`. `horizun_transform_elements` gains `rename_level` (views that will rename, Copy/Monitor alerts rehearsed), `change_type_by_rule` (per-instance rule on measured short/long side or area - the linear-metre vs square-metre rule) and `realign_wall_sketch` (SketchEditScope, real rehearsal). `horizun_manage_views` gains sheet sets (`sheet_set_list/create/update/delete`, print selection restored). `horizun_copy_between_documents` accepts `source_path` (library opened in the background, never upgraded, closed without saving; types by name). `horizun_audit_model` gains `wall_sketch_drift`, per-warning `root_cause` (exact duplicate / contained / vertical overlap in mm / stranded profile) and opt-in `template_comparison` against a template and an SPF. A wire test pins `readOnlyHint` on every read tool. Argument descriptions are capped at 290 characters to keep tools/list inside 512 KiB.
- **Live harness.** `clash-resolve` and `model-diff` read the write gate inverted and never ran with the write tier; fixed, and clash-resolve passed live on Revit 2026. A reply PowerShell could not parse (keys differing only in case) was skipped and the caller waited 620 s, leaving a pending read that killed every later module; the pending read is now reused, late answers are skipped by id and an unparseable reply is returned as an error. New probe modules: spatial-coherence, execute-python-async, read-only-python, health-state, delete-parameters, worksets-ownership, transform-rename-level, manage-views-sheet-sets, copy-between-documents-source-path, field-audit-typechange (all with offline fakes).
- **The spatial check looks into loaded links.** Each changed solid is carried into every loaded Revit link's coordinates and intersected there; a finding names `b.source=link` and the link, `links_examined`/`link_neighbours_examined` say how far it looked, and an unloaded link is listed in `links_skipped`, never counted as clear. Live probe `spatial-links` (a host wall across a linked wall) passed on Revit 2026.
- **Calibrated on a real project model, read-only (2026-09-26).** 157 false errors became one warning and 234 doors were checked in 24 s instead of over 300 s: openings under 3 L of shared volume are the frame touching, a door's clear zone is 0.6 m deep and only for doors a person walks through (>= 1.80 m), obstacles under 10 L are ignored, an intersection Revit cannot measure is at most a warning, and a wall sketch may drift 30 mm before `realign_wall_sketch` calls it drift. `horizun_verify_changes` frames the image on the section box and skips link-side elements.
- **Navisworks round trip, measured end to end (2026-09-26, Navisworks Manage 2026 + Revit 2026).** naviscoord's `navis_handoff` -> `horizun_coordination operation=import_navisworks` (re-detects every pair here; only a reproduced pair enters the ledger, with the issue's priority, responsible and immovable side) -> `operation=show` (a persistent 3D view: the side that must move red, the immovable one orange) -> `horizun_resolve_clash` (moves the responsible side, never the immovable one) -> `horizun_verify_changes`. A pipe through a column: moved 363 mm, re-detection on solids clean, and the same Navisworks test re-run marked it Resolved. Two traps found on the way: at Coarse detail a pipe reaches Navisworks as a line and a Hard test reports zero clashes against it (the 3D view must be Fine); and Revit files every 3D view's camera under a MODEL category, so three cameras made the resolver refuse every lateral move - cameras, viewers, section boxes and MEP system elements are no longer physical anywhere.
- **`model_changes` tells the truth about rollbacks.** A read-only `verify_changes` reported 11 elements added for a temporary view it had rolled back; the event tally is now reconciled against the model when the call ends (an added id that no longer resolves is gone, a deleted one that resolves came back), Revit's own bookkeeping element is no longer a "modification", and the block carries a bounded sample (id, category, class, name).
- **Fixtures per year.** `verify-live-year.ps1` reads each year's closed-workset model and S-size model from the private runner map, the S workload opens a scratch copy of it, and case 15 links a scratch copy of the write document when that document has no link to unload (the 2023 one).
- **Second code-review batch (11 findings, 10 confirmed).** `catalog_lookup` validates `catalog_path` (absolute, .csv/.tsv/.txt, <= 50 MB) before reading; `change_type_by_rule` never lets an unmeasured or non-rectangular face fall through to `else`; `realign_wall_sketch` reports the real rollback status; `sheet_set_create` restores the in-session sheet set it borrowed; parameter-binding removal is confirmed by element identity/GUID, not name; `horizun_health`'s workshare scan drops its unbounded pre-count and the account name; a failed `create_shared` names the definition it already wrote to the SPF (`external_side_effect`); `manage_groups` add/remove_members scope=all declares which instances regenerate and refuses when their members carry Mark/Comments unless `accept_member_regeneration=true`; `manage_curtain` remove_grid_line names the mullions and panels it merges and refuses a door panel unless `accept_panel_merge=true`; MRTR request state never evicts a live nonce and refuses a full table as `capacity`. The one not confirmed (TwoLevelsBased base) was hardened with a note, no behaviour change. Inventory: 122 tools, 47 read-only (`horizun_coordination` now writes a view), 405 operations.
- **Live matrix, 2026-09-26, at b9aee80** (run-year-matrix + verify-live with -WriteProbes, add-in built per year from a clean tree): **all five years green, 374 passed / 0 failed each** (2025 run at efd2f35 once the user's own Revit 2025 session had closed), including the Navisworks handoff (5/5), spatial links, change_type_by_rule, the closed-workset fixture, the S/M/L workloads and case 15 with a staged link in 2023. The first sweep at 11caa65 caught two real defects the review batch had introduced or hidden - joined walls left unmeasured by an "exactly four edges" rule, and a probe that applied a token-less import through the token path - both fixed (6fa6278, b9aee80) and re-measured. The 2025 run left its own Revit open with no visible dialog after a normal close request (recorded as recovery_pending, not killed); a normal close afterwards exited it and the manifest was restored. The "not covered" rows are fixture limits (no workshared write model, no panel, no design options).

- **`horizun_project_context`** (host-resident): JSON Schema `project-context.v1`, `validate` that keeps invalid / inconsistent / incomplete apart, the ordered intake `questions` (es/en) and a `draft` that writes only with `dry_run=false` and re-reads the file. MCP prompt `project-intake` and resource `horizun://schemas/project-context/v1`.
- **`horizun_information_container`** (host-resident): ISO 19650-2 container names, `.container.json` sidecars sealed by SHA-256, `verify`, a paginated `inspect` of WIP/Shared/Published/Archived folders cross-checked against the MIDP, and `transition` that copies (never moves), requires `approved_by` for publication and logs to `.horizun/cde-transitions.jsonl`. `horizun_export` accepts the same `information_container`.
- **`horizun_deliver_ifc`**: one call that exports an IFC with explicit options, checks the file header, validates the FILE against IDS, measures user-defined Pset mapping coverage, reads back the georeference, writes a BCF of the failures and seals the container last. `deliverable_ready` is the file's verdict, never the model's.
- **Toolsets**: `HORIZUN_TOOLSETS` / `toolsets` select tool packs declared once in the contract; `core` advertises 4 tools (~2.2k tokens) instead of 97 (~108k). Unconfigured, tools/list is unchanged. Resource `horizun://session/toolsets`.
- **Model-content safety**: invisible and bidirectional control characters in text read from models and files are neutralised to visible `[U+XXXX]` tokens, replies carry a `content_safety` block, and instruction-like text is flagged (never removed). See SECURITY.md.
- **Clash viewer follows MCP Apps 2026-01-26** (`ui://horizun/clash-viewer`): the CSP is declared at `_meta.ui.csp` with the spec's fields (`connectDomains`, `resourceDomains`, `frameDomains`, `baseUriDomains`, all empty) on the listing and on the `resources/read` content item; tool calls are enabled by `hostCapabilities.serverTools` of the `ui/initialize` result (a `capabilities.tools` beside it grants nothing); the View sends `ui/notifications/initialized` before anything else and nothing before the host answers; `ui/context-update` became `ui/update-model-context`, tool results arrive as `ui/notifications/tool-result`, and unknown host requests get `-32601`. Kept for pre-spec hosts, because harmless: the old closed `io.modelcontextprotocol/ui` CSP block and the legacy `capabilities` read when a host sends no `hostCapabilities`. A node suite (`tests/Horizun.Server.Tests/McpApps/app-handshake.test.js`) runs BOTH apps' shipped pages against a scripted host and pins the three points; run against the previous clash viewer it fails 12 of 16 checks.
- **NSR-10 Título K set with real values**: `standards/co-nsr10-titulo-k-evacuacion.json` (0.2.0) now applies the K.3 numbers read from the published text - riser 100–180 mm, tread ≥ 280 mm, 2C+H 620–640 mm (K.3.8.3.4), stair width ≥ 900 / 1 200 mm (K.3.8.3.3), exit door ≥ 800 mm wide and ≥ 2.0 m high (K.3.8.2.1), exits per level from Tabla K.3.4-1 with the occupant load from Tabla K.3.3-1 - with the source URL and numeral on every rule; only travel distance stays `unverified_value` (group-dependent and never computed). `exit_count_minus_required` gains `occupancy_parameter` + `area_per_person_by_group`, so each room takes the factor of the occupancy group it declares; a room with no group or an unlisted one is `not_decidable`, never defaulted.
- Measured: 5,296 unit tests pass (734 server, 4,562 core); the add-in builds with 0 warnings for Revit 2023–2027.
- Measured live in Revit 2026 (build 654a954, disposable HZ_WRITE, 1,084 duct segments): deliver_ifc with no duct code failed ids_validate and pset_mapping (0/1084), wrote one BCF topic and sealed the container; after a verified write of the code to all 1,084 ducts the same call returned deliverable_ready=true (IDS passed on the file, mapping 1084/1084), which proves the named ExportUserDefinedPsets options on the file itself. export with a container named the IFC, sealed it (verify = match) and refused a second export onto the sealed name. On a local CDE, inspect reported an overdue and an under-state MIDP deliverable, a missing sidecar and a non-compliant name; shared->published was refused without approved_by and succeeded with it, as a copy, logged. The run found one defect, fixed in 654a954: the container gate was written but not reported.
- **Transmittals and approval register** (`horizun_information_container`): `transmittal` issues `<project>-TR-0001` for sealed containers in one state (sender, recipients, purpose code and meaning, each file's SHA-256 re-measured against its sidecar or the whole issue is refused), writes `.horizun/transmittals/<id>.json` + `.md` + `.csv` without overwriting and reads them back; the sequence is held under an exclusive file handle, so concurrent issues never share a number. `record_review` appends the receiving party's outcome (accepted / accepted_with_comments / rejected) to `.horizun/reviews.jsonl` and changes no state. `register` joins transitions, transmittals and reviews per container, filtered and paginated, and names the incoherences between them (file changed after issue, review of an unknown container or transmittal, publication logged without `approved_by`). Host-resident; tested without Revit.
- **MCP elicitation** (form mode, 2025-06-18 and 2025-11-25): the server can now send requests to the client — ids `horizun-server-<n>`, responses recognised by shape and handed over without blocking the reader, per-request timeout, tool-call cancellation, `notifications/cancelled` when it stops waiting, and every pending request failed at once when stdin or stdout closes. Sent only when the client declared `elicitation` at initialize. `horizun_project_context` `operation=elicit` asks the pending intake questions one short form per topic (es/en, typed enums, at most 6 fields), applies them like `draft` (dry_run by default; a refused write is refused before the first form), respects decline/cancel/timeout and lists every question left unanswered with its reason; without the capability it fails with `code: elicitation_unsupported` and the agent asks in the chat. The `project-intake` prompt and the server instructions say so. Measured: 32 new tests, including a round trip through the real server over stdio with a `ping` answered while the form is open; 766 server tests pass. Not yet measured with a real client UI.
- **`horizun_cde_cloud`** (host-resident, read-only): reads a cloud CDE - ACC/BIM 360 Docs through the APS Data Management API (`data:read`) or a buildingSMART OpenCDE server (Foundation discovery + Documents API 1.0). `list_states` maps cloud folders to the four ISO 19650 states from `cde.states` by exact name and reports unmapped folders; `inspect` lists files per state (version, date, size), checks names and crosses the MIDP through `ContainerInspection`, the core now shared with the local `inspect`; `versions` gives an item's history. Credentials only from server environment variables or `%USERPROFILE%\.horizun\aps-token.json`; with none configured, not one request is made. Pagination, a call budget, backoff on 429 and `coverage_complete=false` for anything unread. OpenCDE cannot enumerate a project without its interactive browser flow, so it reads documents by id. Proved against a fake HTTP handler (21 tests); not yet measured against a live ACC project. Its tools/list entry is 1,210 bytes, which leaves the whole list 16 bytes under the 512 KiB budget.
- **Live probes for the ISO 19650 tools** in `scripts/verify-live.ps1` (`scripts/live-iso19650.probes.ps1`): deliver_ifc (dry run, apply with a per-run TAB mapping and IDS for the class the model is found to contain, container seal with `verify = match`, readiness that follows the gates, refusal of a second seal, IFC4x3 planned on 2024+ and refused by name on 2023, opt-in `-IsoReadyProbe` for `deliverable_ready=true`), export with a container (IFC, PDF of a real sheet, refused repeat), and host-resident project_context and information_container cases on a temporary CDE. Each run also writes a `horizun.live-evidence/2` record for `consolidate-live-session.py`. Exercised without Revit by `scripts/live-iso19650.tests.ps1` (69 assertions, PowerShell 7 and 5.1). Not yet run in Revit. `verify-live-matrix.ps1` now accepts the schema-2 reports verify-live has written since 1.0.0; it rejected every real report before.
- **`examples/`**: contract-validated, bilingual payloads for the ISO 19650 start-up (questions → draft → validate, plus a complete `project-context.example.json`), containers (name → stamp → inspect → transition), a verified IFC delivery with a TAB Pset mapping and a matching IDS 1.0 (`HZ_Delivery.Code` on `IfcDuctSegment`), and one call per discipline. `ExamplePayloadTests` validates every `examples/**/*.json` against the `InputSchema` of the tool it declares (unknown schema keywords fail, never pass) and runs the host-resident ones; `ExampleDeliveryFilesTests` proves the example IDS and mapping agree on a hand-written IFC.
- **One declared verification mechanism per writing tool** (`Core/WriteVerificationCatalog.cs`): a row per tool with mechanism, evidence field, sources and the residual gaps the 2026-09-24 inventory found, and a test that fails when a writer - by contract effect OR by opening `DocumentGate.ForMutation` in its source - has no row. It found `horizun_cad_connect` classified `ReadOnly` while writing through its children in-process (admitted by `read_only`, `readOnlyHint=true`); it is now `MutatingUnlessDryRun`. Verdicts that could pass over nothing now cannot: recipe tools whose every element failed (0 == 0) or that report no count (-1 == -1), delete/set_keynote with nothing compared, a colour legend or override/hide set with no rows, `remove_fields` by `field_index`, a preset with no provable option, an update-only IFC plan stage that covered nothing, a family re-read with no dimensions (parameters and types are now re-read from the saved file). Unreadable values are unmeasured instead of reported: tag leader flags (transform tag operations are now a `PostconditionCheck`), a link's pin state, a duct connector's size (create_elements production properties are now a `PostconditionCheck`, `production_postconditions`). Literal verdicts are derived: materials (and six written-but-never-compared fields), copy between documents, CAD link reload (commit and load result now decide). Responses only gained fields. Not yet measured in Revit.
- **LOIN to IDS**: `project-context.json` gains an optional, additive `loin` block (ISO 7817-1:2024, formerly EN 17412-1: purpose, milestone, actors; geometry, alphanumeric, documentation; `schema_version` stays 1). `validate` names its contradictions (`loin_property_type_conflict`, `loin_unknown_ifc_entity` against embedded IFC2X3/IFC4/IFC4X3_ADD2 entity lists, repeated ids, impossible bounds). `horizun_project_context operation=ids_from_loin` writes the alphanumerical part as IDS 1.0 and lists, never approximates, what IDS cannot express; the file is proved against the embedded ids.xsd 1.0.0 (buildingSMART, CC BY-ND 4.0, verbatim) and by the bridge's own IdsReader, dry run by default, re-read and re-validated after writing.
- **bSDD lookup**: `horizun_catalog_lookup` gains read-only `bsdd_search`, `bsdd_search_dictionary`, `bsdd_class`, `bsdd_property` and `bsdd_dictionaries` over the public bSDD API (TextSearch/v2, SearchInDictionary/v1, Class/v1, Property/v5, Dictionary/v1). Classes return `loin_property` suggestions that never guess an IFC data type. Cached 7 days in `%USERPROFILE%\.horizun\bsdd-cache`, 30 calls/min and 500 per process; offline it says so and serves only an expired copy labelled `stale_cache`. No new tool: tools/list grows by 472 bytes (catalog_lookup +308, project_context +164). Tested with a fake HTTP handler only; the live API was not reachable from the build machine.

- **Five-year live matrix, 2026-09-24** (run-year-matrix + verify-live with -WriteProbes, add-in built per year at 8e9d5ca from a clean tree, release-gate fixtures: labelled tag family, HZ24_INACTIVE as the foreign file for 2023): 2023 243 passed / 0 failed / 4 unverified / 12 not covered; 2024 245/0/3/11; 2025 245/0/3/11; 2026 252/0/2/5; 2027 245/0/3/11 - 1,230 passed, 0 failed. The ISO 19650 section passed 13 of 13 in every year. A first run without those fixtures failed only on them, and Revit 2027 exited on its own once; the rerun did not reproduce it. That run left each Revit running (the harness's own link-source copies were not registered); fixed the same day: harness scratch models are adopted through a horizun.harness-documents/v1 manifest, own LINKED models unload with their host (health publishes is_linked), and the harness exit code reaches the driver. Measured on Revit 2026: close.state=closed, manifest restored, 252 passed / 0 failed. Also measured live that day: bSDD (IfcWall with Pset_WallCommon; a SearchInDictionary parsing defect found and fixed - 179 Uniclass classes instead of 0) and ACC through APS 2-legged (list_projects: 2 hubs, 176 projects; list_states read a real project's folders and mapped none it could not match exactly).
- **Final 2026 live pass, 2026-09-24/25** (run-year-matrix at 81dc3f7, disposable HZ_WRITE): 321 passed / 3 failed / 2 unverified (S size not in the fixture set). The 2027 run at af91438 had failed only on the curtain panel case. A rolled-back typed edit now names the postconditions that disagreed with both values (81dc3f7), which showed the two remaining defects: a panel swapped to a wall type gets a new id that `GetPanelIds` does not list, because the grid keeps the original Panel and `FindHostPanel` names the wall shown in its place (the check now accepts that, and the evidence publishes both ids); and `horizun_manage_parameters create_shared` restored `SharedParametersFilename` before the binding was inserted, so its rehearsal read an invalid ExternalDefinition (the SPF now stays open until the run ends, and the user's file is restored in the run's `finally`). Both fixed at a6e9fff. **Re-measured live on 2026-09-25** (run-year-matrix at b46caf7, Revit 2026, disposable HZ_WRITE): 324 passed / 0 failed / 2 unverified (the S workload size is not in the fixture set); the curtain panel, create_shared and remove_binding cases pass, and the driver closed its own Revit after Revit's crash-on-exit dialog and restored the manifest. The other years at c7b0f8b (same day, same driver): 2023 315 passed / 0 failed / 5 unverified, 2024 317/0/3, 2027 317/0/3 - every unverified case is a fixture limit (no S workload model; HZ_CLOSED_L saved in 2026 cannot open in 2023 and is refused in 2027 rather than upgraded; the 2023 unloaded-link fixture could not be staged; once, in 2023, `settings.json` was held by another process during the tool-pack round-trip and the probe restored it). Revit 2025 crashed natively while opening its fixture (journal: exception 0x80000003 on a non-main thread during File: Open, before any command ran), and the rerun that afternoon passed: 2025 317 passed / 0 failed / 3 unverified (same fixture limits). All five years now have 0 failures with both fixes, curtain panel and create_shared passing in each.
- **The year-matrix driver adopts a harness's own scratch models, and only those.** `run-year-matrix.ps1` sets `HORIZUN_HARNESS_DOCUMENTS_MANIFEST` to a fresh JSON per harness run (cleared afterwards); `verify-live.ps1` writes there (schema `horizun.harness-documents/v1`, rewritten in a script-level `finally`, so a run that dies half-way still declares them) the `.rvt`/`.rfa` files under its own `%TEMP%\horizun-live-<run>`. `Register-HzHarnessDocuments` registers an entry in the same register, kind `harness_scratch`, only when the schema is right, the folder is a direct child of the temp directory named `horizun-live-<probe_run>`, is not a link and was created after the driver started that Revit, and the path is absolute, inside it, without `..`, and exists; everything refused is reported in the run's `harness_documents` and stays foreign, and so does a model of that folder the manifest did not list. Closing is unchanged: by registered path, rehearsal then token, discarding. 23 new offline cases in `year-matrix.session.tests.ps1` (159 pass, PowerShell 7). Not yet run in Revit.
- **MCP 2026-07-28 elicitation (multi round-trip requests, SEP-2322)**: `horizun_project_context operation=elicit` under a 2026-07-28 request now answers with an `InputRequiredResult` (`resultType: "input_required"`, one form in `inputRequests`, a `requestState`) and continues when the client calls again with the same arguments, `inputResponses` and the state; the last round returns the usual result (dry_run by default, written only then and re-read). The state is HMAC-SHA256 sealed with a per-process key, bound to the tool, the arguments' digest (another `path` or `dry_run` is `mismatch`) and `clientInfo.name`, expires with `timeout_seconds` and is single-use; any failure is `request_state_rejected` with nothing applied. Capability read from each request's `_meta`; `elicitation_unsupported` loses `input_required_result_not_implemented` and gains `nested_call`. Legacy 2025-06-18/2025-11-25 elicitation is unchanged. Logging under 2026-07-28 (deprecated by SEP-2577, still served): `server/discover` now declares `logging` in the modern block, since the server emits `notifications/message` for requests that name a `logLevel`, and an unknown level is `-32602`. No tools/list change (0 bytes). Proved over stdio; no 2026-07-28 client was available.

- **Parse errors and cancellations say where they came from and what to do next.** Traced from `server.log` on 2026-09-24: all 132 `Expected ':' but got` warnings (Sep 19–24) were this repository's own wire tests sending `{this is not json` and `{not json at all` on purpose, and all 10 `Cancelled while waiting for Revit to answer 'horizun_create_elements'` ERRORs were verify-live W13 case 11 cancelling a queued write on purpose (10–170 ms, nothing ran). No client was corrupting its stream and no batch was lost. A -32700 now carries `error.data` {hint, line, column, offset, length, masked shape} - hints `unescaped_backslash`, `concatenated_messages`, `truncated_message`, `byte_order_mark`, `bare_word`, `not_json` - with every letter and digit masked, so the content is still never echoed; the log line names the client from `initialize`. A cancelled or timed-out call now says what an identical retry does (`retry.verdict`: `same_key_runs_fresh`, `same_key_replays_recorded_answer`, `inspect_model_first`) and names `horizun_submit_job` after a wait of 60 s or more; a client cancellation proven to have removed the work before start is logged as WARN, not ERROR with a stack. The wire tests log to a throwaway data root instead of the owner's. `hz-call.ps1` validates arguments before starting a server, explains unescaped Windows paths and gains `-ArgumentsObject`. No tools/list bytes added; no Revit behaviour changed.
- **Phases, phase filters, design options, parts and assemblies** (two new tools, ~4.2 KB of tools/list): `horizun_manage_phases` reads phases in order, phase filters with their new/existing/demolished/temporary presentation, design option sets/options/primary/active with members, and each element's phase status (`ElementOnPhaseStatus`) and option; it writes `set_element_phases` (demolition never before creation), `create_phase_filter`, `edit_phase_filter` and `rename_phase`. `horizun_manage_assemblies_parts` lists parts and assemblies and writes `create_parts`, `divide_parts` (levels, grids, reference planes), `exclude_parts`/`restore_parts`, `dissolve_parts`, `create_assembly`, `assembly_views` and `disassemble`. Every write is rehearsed in a TransactionGroup that is rolled back, applied with the token, checked with a `PostconditionCheck` while reversible and re-read after the group assimilates. Measured against RevitAPI.xml 2023-2027: no API creates or reorders phases and `Element.DesignOption` has no setter, so `create_phase` and `assign_design_option` are typed refusals (`no_phase_creation_api`, `no_design_option_assignment_api`) with no Python fallback. Live probes in `scripts/live-probes/phases-options-parts.probes.ps1`, exercised offline only; not yet run in Revit.
- **Views, schedules and DWG layers** (extends existing tools, +1.5 KB of tools/list): `horizun_manage_views` gains `edit_filter`, `order_filters` (remove/re-add, since Revit has no SetFilterOrder), `apply_filter enabled`, the read-only `explain_graphics` precedence report, category/subcategory V/G overrides with `line_pattern`, `create_template`, `set_template_controls` and template removal (`template_view_id: -1`); a view whose template governs V/G is refused naming the template to edit instead. `horizun_create_schedule` creates multi-category (`OST_MultiCategory`) and key schedules (`key_schedule`, `key_rows`). `horizun_export` gains `format: dwg_layers` (read/create/write a named DWG setup's layer table, re-read from a fresh lookup before and after the commit) and `dwg_setup` for `dwg`. Live probe `scripts/live-probes/views-schedules-dwg.probes.ps1` measures layer-table persistence per Revit year. See docs/TOOLS-EXTENDED.md.
- **`horizun_manage_groups` and `horizun_manage_worksets`** (two new typed tools; 96 real scripts created or redefined groups through Python, and worksets had no typed write at all). Groups: `list` (types, instances, members, nesting, attached detail groups), `create`, `add_members`/`remove_members` (Revit has no edit-group API: the reference is ungrouped and regrouped; with other instances of the type `scope` is REQUIRED - `all_instances` swaps them onto the new type, re-places each by a measured displacement and checks every retained member by category, type and box - or `this_instance`), `rename_type`, `duplicate_type`, `swap_type`, `ungroup`; `convert_to_link` is refused typed (`code: api_absent`, no API in 2023–2027, Python not suggested); a type with attached detail groups is not redefined. Worksets: `list`, `create`, `rename`, `move_elements` (ids or category; borrowed or read-only elements reported, never forced), `set_default` (active workset; measured preview), `visibility` per view; a model that is not workshared is refused with `code: not_workshared`. Dry run by default, single-use token, `PostconditionCheck` re-read after commit. tools/list grows by 3,385 bytes (1,732 + 1,653). Live probes in `scripts/live-probes/groups-worksets.probes.ps1` (exercised offline by its `.tests.ps1`); not yet run in Revit.
- **`horizun_mep_routing`**: one multi-operation tool for MEP routing and sizing - `read` (routing rules per group with size ranges, pipe segments with nominal/inner/outer sizes, duct/conduit/cable-tray catalogs, element sizes), `set_rules` (add/remove/move rules, preferred junction), `add_sizes`/`remove_sizes` (segment, conduit standard, duct shapes, cable tray; a size in use is refused), `resize` (runs by ids or system to catalog sizes only; connections re-read, replaced/inserted fittings reported) and `size_by_flow` (velocity-limit proposal, writes nothing). Writes rehearse, take a confirmation token and re-read after commit. Live probe `scripts/live-probes/mep-routing.probes.ps1`, not yet run in Revit. Details in `docs/TOOLS-EXTENDED.md`.
- **Styles, units and electrical** (`horizun_manage_styles`, `horizun_manage_units`, `horizun_electrical`): object styles, subcategories, line styles, line and fill patterns; project units per spec (ForgeTypeId), Project Information and base points (shared-coordinate writes need `confirm_shared_coordinates=true`); panels, circuits, panel assignment, circuit membership and panel schedules. Reads answer directly; every write rehearses in a rolled-back transaction, applies inside a TransactionGroup and assimilates only a `PostconditionCheck` re-read from the committed model. tools/list grows by 5,941 bytes. Builds with 0 warnings for Revit 2023–2027 (`ElectricalSystem.VoltageDrop` is read from its parameter on 2026–2027). Live probes in `scripts/live-probes/styles-units-electrical.probes.ps1`, exercised offline by its `.tests.ps1`; not yet run in Revit. Detail: `docs/TOOLS-EXTENDED.md`.
- **Curtain grids, railings, slab shape and arrays**: `horizun_manage_curtain` (read, add/remove grid line, add/remove mullions per segment, panel type incl. curtain-wall doors), `horizun_slab_shape` (read, add_point, add_split_line, modify_subelement, reset_shape; the editor and point/split-line calls guarded per Revit year), `horizun_create_railing` (on a stair/ramp or along a sketched path) and `array_linear` / `array_radial` in `horizun_transform_elements` (grouped or not; every copy re-read at its formula position). Each edit is re-read through a `PostconditionCheck` and rolled back on disagreement. tools/list grows by 5,722 bytes. Builds with 0 warnings for 2023–2027; live probe `scripts/live-probes/architecture-edits.probes.ps1`, exercised offline only. Not yet run in Revit. See docs/TOOLS-EXTENDED.md.
- **`horizun_resolve_clash` and `horizun_undo`**: resolve_clash `propose` (read-only) turns open findings of the horizun_clash ledger into conservative candidates - the unconnected, unpinned host MEP run (the smaller one when both are runs) is shifted perpendicular to itself or re-elevated the minimum plus `clearance_mm`, capped by `max_move_mm`; structure, architecture, linked elements, connected runs and moves that would reach a third element are report-only, by code. `apply` (dry run -> token) moves inside a TransactionGroup and re-detects on solids over the swept neighbourhood: the pair must be gone and no new pair may appear, otherwise the whole group rolls back; a kept move marks the finding `resolved_by_model` with the measurement in its history. `horizun_undo` (`list`, `undo_last`) applies the inverse that transform_elements, write_params_verified, create_elements and resolve_clash now record after a verified commit (`undo` block in their replies), refusing when the batch's elements changed since, when the document was saved or synchronized (VersionGUID/NumberOfSaves), or when the last batch was not recordable; every element is re-read against the state the batch found. tools/list +2,905 bytes. 10 core tests; live probe `scripts/live-probes/clash-resolve.probes.ps1` (exercised offline by its .tests.ps1). Not yet run in Revit.
- **`horizun_manage_parameters`** and **`horizun_query_classification`** (read only): the BindingMap in full (name, data type, Instance/Type, categories, group, GUID, varies across groups); `create_shared` writes the definition to a chosen SPF and binds it, rehearsing against a temporary COPY of the SPF and re-reading both the binding and the file on disk; `rebind` and `remove_binding` count the values that would be lost in the plan the token binds; Global Parameters list/create/set (display units, formula, association to element parameters)/delete with the computed value re-read. `create_project` is a named refusal: RevitAPI 2023-2027 has no call that creates a non-shared project parameter. Classification: keynote and assembly-code tables with path, load status, entries, parent and use counts by code, `unused_codes`, `missing_codes`, and family lookup tables read from the project through `FamilySizeTableManager` without opening the family. Revit 2026+ renamed `UNIFORMAT_CODE` to `ASSEMBLY_CODE`; both are handled per year. tools/list grows by 3,776 bytes. Live probes in `scripts/live-probes/parameters.probes.ps1`, exercised offline by `parameters.tests.ps1`. Not yet run in Revit. See docs/TOOLS-EXTENDED.md.
- **`horizun_model_diff`** (one multi-operation tool, 2,609 bytes in tools/list): `snapshot` records the active model - or a `.rvt` opened detached in the background and closed without saving - to `%USERPROFILE%\.horizun\snapshots\<id>.json.gz` (per element UniqueId, category, family/type, level, workset, phases, bbox, location, instance and type parameters in internal units, a volume/area/extent hash; hashed, re-read after writing); `compare` reports added / deleted / modified with parameter before/after, moves over a tolerance and type changes, grouped by category, inferred discipline and level, paged, with CSV + JSON exports; a re-created model (few shared UniqueIds) is flagged and `heuristic_match` pairs by category/family/type/location, every pair marked `inferred`. `colorize` is the only model write: a duplicate of the caller's view with per-state overrides, dry-run token bound to the resolved element set, re-read inside the transaction (rollback on mismatch) and after the commit. `explain` narrates only measured facts (counts, links, worksets, phases, last quality run); the server adds the ISO 19650 gaps from `project_context_path` with the same evaluation `horizun_project_context` runs. `record_quality` runs `model_scan` or `audit_model` in process and appends the reported totals to `quality-history\<project>.jsonl`; `quality_trend` returns the series as rows for `horizun_power_bi_push` plus a CSV. 36 new unit tests; live probe `scripts/live-probes/model-diff.probes.ps1` exercised offline only. Not yet run in Revit.
- **Impact preview (MCP App `ui://horizun/impact-preview`)**: an interactive view of the rehearsal (dry_run) of `horizun_write_params_verified`, `horizun_set_keynote`, `horizun_delete_verified`, `horizun_transform_elements` and `horizun_create_elements` - totals by category and level, before -> after per row, warnings (collateral, cascade, truncation, withheld token) and a checkbox per row to EXCLUDE it. The token binds the whole request (the narrowed field is in each command's plan hash, now asserted by a test), so excluding rows asks for a NEW rehearsal of the reduced request and Apply only ever spends the token of the rehearsal on screen, with a fresh idempotency key. Self-contained HTML (no URL, no fetch, empty CSP), light/dark and host theme variables, accessible table. No new tool: tools/list grows by 305 bytes (five `_meta.ui.resourceUri`). The adapter is pure JS run under node by `ImpactPreviewAppTests` against five synthetic fixtures whose shape is tied to the emitting source; `scripts/live-probes/impact-preview.probes.ps1` measures the real replies and the refusal of a full-plan token for a narrowed request. Not yet run in Revit.
- **Code checks, 4D and federation** (three tools, +3,474 bytes of tools/list): `horizun_code_check` runs the requirement-set grammar of `docs/requirement-set.md` - which had a loader and no tool - extended with geometric `measure` assertions (door width as an upper bound, ramp slope/run/width/landing from the ramp's own faces, stair riser/tread/2R+T/run width, space illuminance, exits per level against a declared occupant load), `between`, `measure_range`, `missing_is`, per-rule `source` and `unverified_value`; outcomes passes/fails/not_decidable/unreadable, and a rule that examined nothing is not_decidable. Example sets `standards/co-ntc6047-accesibilidad.json`, `co-nsr10-titulo-k-evacuacion.json` (every threshold unverified: no number applied) and `co-retilap-iluminancia.json` (RETILAP 2024, Tabla 3.2.2.6 a). `horizun_link_schedule` imports MS Project XML (MSPDI), CSV and Primavera XER, matches by parameter or rules without ever picking between two activities, writes activity/dates to text instance parameters and colours a duplicated status view, both dry-run/token/re-read. `horizun_federation_check` compares categories per model, expected links, worksets and a measured same-site check against declared rules. Unit-tested; live probes in `scripts/live-probes/code-checks-4d-federation.probes.ps1`, not yet run in Revit.

## v2.0.5 — 2026-09-22

- Release hygiene correction: removes trailing blank lines that blocked the v2.0.4 CI run before package installation or live Revit verification.

## v2.0.4 — 2026-09-22

- **Reliable English release runner.** Live release verification launches Revit with /language ENU and requires horizun_health to report English before it accepts evidence, so Revit 2023 cannot silently run its localized UI path.
- **Installer false-positive repair.** A clean Addins directory that Inno Setup reports as ERROR_SUCCESS while finding no *.addin files is accepted; access and I/O failures still block the installation.

## v2.0.3 — 2026-09-22

### Revit 2023 Spanish release-gate fixes

- The verified parameter writer resolves stable English contract names for Mark,
  Diameter, Rebar Cover - Exterior Face, Unconnected Height and Structural through
  their `BuiltInParameter` identities. Client requests and CSV imports no longer
  depend on the Revit display language.
- Detached local opens recognise Revit's exact Spanish `_desenlazado` title as
  well as `_detached`, while preserving exact title matching. A correctly opened,
  active detached model is no longer rejected solely because Revit localized its
  synthetic filename.
- The live verifier now uses stable parameter ids where it is testing schedule
  fields and API identities where it reads wall height. Its duct-takeoff assertion
  relies on the command's connector verification instead of an English category
  display string.

## v2.0.2 — 2026-09-22

### Fixed after independent review

- The loopback check now fails closed when Windows cannot inspect the tunnel
  process and its direct children, and verifies that the URL published by the
  client belongs to a loopback listener it owns. An empty inspection result no
  longer becomes a positive connection result.
- The startup grace now uses the actual first-poll timeout of tunnel-client
  v0.0.14. A configured 60-second poll receives an 85-second grace; the previous
  fictional `CONTROL_PLANE_INITIAL_POLL_TIMEOUT` left it at 55 seconds and could
  report a healthy client as failed before its first allowed poll completed.

### ChatGPT Work through OpenAI's Secure MCP Tunnel: the helper now works on Windows

Reported from the field: a user on Revit 2026 with 2.0.1 could not connect
ChatGPT. Every cause below was reproduced against OpenAI's official
`tunnel-client` 0.0.14 (SHA-256 checked against its `SHA256SUMS`, SLSA
attestation verified) before it was fixed.

- **The server path never reached tunnel-client intact.** `--mcp-command` is
  parsed twice - by Windows, then by tunnel-client's own shell-like parser, where
  `\` escapes and `'` opens a quote. `C:\Windows\System32\cmd.exe` arrived as
  `C:WindowsSystem32cmd.exe`, so `init` refused every Windows machine, and a path
  with an apostrophe did not parse at all. The helper now passes forward slashes
  inside literal double quotes, proves the value parses back to the same path
  before handing it over, and was verified with the official `init` on a path
  containing a space, an apostrophe and accented letters.
- **The right package is named, and the wrong one is recognised.** OpenAI
  publishes runtime-only variants with no `init`, `doctor` or `--mcp-command`.
  The helper names the exact ZIP (`tunnel-client-v<version>-windows-amd64|arm64.zip`)
  and detects a runtime build from its own answers, even renamed to
  `tunnel-client.exe`.
- **Detection keeps the evidence.** Exit codes, stdout/stderr and timeouts are
  kept apart; `--version` replaces the `version` command that does not exist; an
  error that mentions `--mcp-command` is no longer read as support. An explicitly
  chosen executable that does not exist is reported, never silently replaced; a
  proven one is remembered.
- **One profile directory** (`%LOCALAPPDATA%\Horizun\integrations\chatgpt\profiles`)
  for init, doctor, run and revoke. A `horizun-revit` profile left in
  tunnel-client's default directory by an earlier version is reported and left
  untouched.
- **The tunnel survives the window that started it, and holds none of its
  handles.** It used to die when that console closed, and it now runs in a hidden
  window of its own; a caller capturing the helper's output is released at once.
  The process is recorded by pid, start time and executable, so a reused pid is
  never stopped, and `-Stop`/`-Revoke` say "not verified" rather than "stopped"
  when they cannot see it exit.
- **Windows PowerShell 5.1.** `ProcessStartInfo.ArgumentList`, absent there, is
  gone from the helper and the MCP probe.
- **"Connected" is measured.** `/readyz` stays 200 while every poll of OpenAI
  fails, so the helper reads `commands_poll_last_successful_timestamp_seconds`
  from the client's loopback `/metrics` (published through `--health.url-file`)
  and reports *configured* only when the last successful poll is recent. The
  window is derived from the poll settings the tunnel runs with (two
  `poll_timeout + guardrail` cycles plus 20 s: 90 s with OpenAI's defaults), and a
  new process is *connecting* rather than failed during its first poll. Measured
  against a local stand-in, not against OpenAI. A call from ChatGPT reaching Revit
  is never claimed by this machine.
- **Loopback, checked.** The health listener is requested as `127.0.0.1:0` in the
  profile and as a flag on `run`, and the addresses the process really listens on
  are read from the operating system; anything else fails local health.
- **The key during start.** It is placed in the helper's own process environment
  only for the instant ShellExecute copies it, and the previous value (or its
  absence) is restored even when the start fails - under PowerShell 7 that needs
  `[NullString]::Value`, since `$null` would leave the variable defined and empty.
- **Prerequisites block what depends on them**; a JSON report and the durable
  state are written on every path, including early failures; the general
  diagnosis measures the tunnel instead of trusting a recorded state.
- **Setup no longer says ChatGPT Work was configured for you.** It is not: it
  needs account-side objects and OpenAI's client.
- The tunnel tests now run in pull requests and CI under both Windows
  PowerShell 5.1 and PowerShell 7.

## v2.0.1 — 2026-09-21

> **The first published 2.0 release.** `v2.0.0` was tagged and built, and its
> Revit 2023 live gate found the defect below before anything was published. Tags
> are never moved, so that tag has no release; everything described in this
> section ships in 2.0.1.

### Fixed since the v2.0.0 tag

- **A wall built exactly on its line was refused in models whose project base
  point is not at the internal origin's height.** The line check introduced in 2.0
  measured the distance ACROSS the wall's line in three dimensions, so it compared
  the height of Revit's location line (internal coordinates) with the height of the
  asked point (project elevation). MEASURED in Revit 2023 on a model whose project
  base point sits 94.17 mm off the internal origin: every wall was refused as
  94.17 mm off its line, and 3800 mm on Level 2, and the batch rolled back — the
  refusal was safe, nothing wrong was written, but no wall could be created. The
  distance across and along the line is now measured in plan; the height stays
  verified by `level_id`, `level_elevation` and `offset`, on the level's own
  ruler. Re-measured live in Revit 2023 on the same model: the walls that were
  refused on Level 1 and Level 2 now commit and verify.

### The 2.0 release

**A revision update now re-measures the world it was planned against, can be
continued after a failure, and never claims a network it did not build.** This is
a major version because two calls that used to succeed now fail: see *Breaking*
below before upgrading.

### An apply that checks the plan is still about this model

`horizun_apply_cad_update` accepted an `apply_binding` and **never read it**. A plan
made against one issue of a drawing could be applied to a model whose drawing,
references, link geometry, rules or elements had all moved since, and the command
would carry it out and report success.

Both CAD applies now go through the same check (`CadApplyGuard`), which names what
moved before anything is written. Measured live on seven situations: a label edited
after planning, geometry edited after planning, a nested reference revised, a source
missing, the link reloaded, an element edited by hand, and a plan made while the
sources and the link could not be shown to be the same issue. All seven refuse
before any write, and the model's element and connector census is identical either
side of the refusal.

### An update that stops half-way can be continued

Three different things get called a retry, and the product now tells them apart:

| | |
|---|---|
| **repeat** | the same key over finished work replays that reply and runs nothing |
| **continue** | `continue_operation` carries out only what is still pending, under the decisions the first call was given |
| **a new plan** | the world moved; the old decisions do not carry, and the guard refuses |

The operation record is durable on the machine, so **save the model, close Revit,
open a new session and continue** works — measured across two sessions. The reply
carries `operation` with what is confirmed, what is still to do, what each action
created or removed, and the decisions that were authorised. Finished records are
swept after 30 days; an unfinished one is never swept, at any age.

A caller whose apply committed and whose answer was lost gets
`already_applied_under_this_key`, naming what is left and how to continue, instead
of being told the drawing had changed — what had changed was what their own call
did.

### It says what it leaves unjoined, and refuses what it cannot rebuild

An update writes **geometry**. Joining is `horizun_cad_connect`'s consented step, and
it always was — but nothing said so, and a division built by an update left two ends
at the same point holding nothing while every count reported the model correct.

- the reply carries `verdict`: `geometry` applied, `network: not_asserted`;
- `ends_that_meet_and_are_not_joined` names the ends left loose at the points the
  call worked at;
- a plan that **releases fittings** is refused before any write unless the caller
  sends `accept_connections_not_rebuilt`. Consenting to lose a fitting is not
  consenting to lose the junction it served.

### A fitting remembers where it came from

Every fitting this bridge places is stamped with its junction, its members, its type
and a print of how it was left, and reads back as `made_here`, `made_here_modified`
or `origin_unknown`. A fitting the bridge placed and a person then moved is **their**
work: releasing it needs `release_protected_fittings`, the same second consent as a
fitting nobody here placed. A model from before this record is never claimed
retrospectively.

*(The stamping had never worked: it looked for a connector within 50 mm of the drawn
junction, and inserting an elbow trims both runs ~380 mm back, so every fitting read
as `origin_unknown`. The fitting that serves a junction is the one element, not a
member, that **both** members hold.)*

### Every held row of an update plan says why

`held_rows` collects each row the plan will not carry out on its own with the reason
in the same place, including what it is waiting for. A row whose `held_because` is
null is a gap in the planner and is labelled as one.

### Isolated Revit sessions report what is true

`scripts/dwg-bim/session.ps1 status` asked the state file and answered from it, so a
session whose Revit had gone still read *running*. It now reports three separate
things — the record, the process (`alive` / `exited` / `not_ours` / `never_started`)
and the environment verified against files and hashes — and **never** takes the
absence of Revit as evidence that the year's manifest was restored.

### Eighteen new tools — 80 → 98

The surface this release publishes is larger than the revision work above. Nothing was removed
or renamed; **98 tools, 262 suboperations**, and the eighteen that are new fall into five
groups.

**Reading a DWG without importing it.** `horizun_cad_extract` reads a linked drawing's layers,
lines, arcs, text and blocks directly; `horizun_cad_symbols` lists the block symbols it defines
and where each is placed; `horizun_cad_unit_instances` finds repeated units and the transform
that places each occurrence. Revit's own import cannot see a block name, which is why these
exist.

**Networks, not just lines.** `horizun_cad_networks` derives what a drawing says about its own
network — which ends meet, what belongs between them, which stay open on purpose — and
`horizun_cad_connect` builds those joins, placing the fitting each junction needs and verifying
it. `horizun_connect_mep` connects or disconnects named connectors and refuses to close a
visible gap by moving somebody's geometry. `horizun_cad_review` reports what a conversion could
not settle, with the evidence for each held candidate.

**IFC and IDS.** `horizun_plan_from_ifc` plans Revit elements from an IFC under a declared
mapping without importing it, `horizun_apply_ifc_plan` carries that plan out through typed
commands, and `horizun_validate_ids` checks a model against an IDS specification and reports
each requirement it fails.

**Model work.** `horizun_manage_materials` reads, creates and assigns materials;
`horizun_copy_between_documents` copies elements between open documents keeping identity and
reporting substitutions; `horizun_structural_connections` reads and applies structural
connection types; `horizun_selection_exchange` reads what a person selected in Revit and can
offer a set back for them to select.

**Machine state and procedures.** `horizun_audit_access` reports what this bridge is allowed to
do on this machine and who decided it; `horizun_repair_memory` recovers durable state when a
record is unreadable; `horizun_run_procedure` runs a named, versioned procedure this machine
has stored, and `horizun_promote_script` is how a verified script becomes one instead of
staying ad-hoc code.

### Breaking

- **`apply_binding` is now a required argument of `horizun_apply_cad_update`.** It
  was neither declared nor read before. Any caller following the documented flow
  already has it: copy the `apply_binding` block from the `horizun_plan_cad_update`
  reply verbatim.
- **A plan that releases fittings is refused** unless `accept_connections_not_rebuilt`
  is `true`. A call that used to apply geometry and silently leave the network broken
  now writes nothing until the caller says it accepts that.
- `state: applied` never meant the network was built. It still does not; the new
  `verdict` block says so explicitly rather than leaving it to be assumed.

### Limits of this release

- The live evidence for these changes is **Revit 2026**. The add-in compiles for
  2023–2027 and the release gate runs the matrix; the DWG→BIM revision work above was
  measured on one year.
- `horizun_plan_cad_update` takes no outfall, so a revision that re-shapes a run on a
  layer with a declared fall **holds it** rather than laying it level. Building a
  falling run is `horizun_plan_from_cad` with an outfall, and pipes only — a duct has
  no bore to turn an invert into a centreline, and asking for one is refused.
- The unjoined-end report covers ends that coincide. A declared junction where the
  model has a single loose end is invisible to it by construction; that is what the
  drawing-aware acceptance is for.

### Also in this release — documentation and distribution metadata

- Correct Claude Desktop's required in-app extension step and installed recovery
  paths; align English, Spanish and agent instructions with Setup.
- Separate stable, source and installed versions, historical benchmark scores and
  release evidence. Document current protocol support without changing it.
- Keep checked-in registry identity aligned with the product version. Publish the
  existing extension as an additional release asset with verified metadata and
  an explicit prerequisite: install the Windows product first.
- Generate readable release notes and check publication documentation in CI.
- Publish the complete bilingual tool catalog, named suboperations, the PDF-to-Revit
  video and versioned live-test evidence directly in the README. Explain the
  measured 70/79/80-tool permission profiles and the core-only subset.
- Deduplicate repeated schema branches in the suboperation counter: 208 distinct
  tool/selector/value choices replace 213 schema occurrences, with no tool removed.



## v2.0.0 — 2026-09-20 (tagged, not released)

Built and put through the release gate, and withdrawn before publication by the
wall defect described under v2.0.1. Its content ships in v2.0.1.

## v1.3.3 — 2026-09-14

**Claude Desktop is installed by the person using it, and Setup now hands the
file over properly.** Its extension is installed from inside the app - that step
exists nowhere else - so Setup no longer tries to write the app's configuration
underneath it. Instead it copies the `.mcpb` and a printed instruction sheet to
**Documents\Horizun-Revit-MCP** and opens that folder with the file selected.
The package used to sit under `AppData\Local\Programs`, which Explorer hides and
the app's own file picker opens nowhere near: the one manual step began by
hunting for a file.

**The instruction sheet ships as a PDF, with screenshots**, in Spanish and
English, rendered at build time so nothing has to be produced on a machine that
has no renderer. It describes the UI as it actually is - drag the `.mcpb` onto
the Extensions page, or Advanced settings > Install extension - rather than the
path the documentation used to claim.

**No Start-menu shortcut asks anybody to run a script any more.** Claude Code,
Codex and ChatGPT Work are configured by Setup itself; Claude Desktop is the one
manual step and it now arrives with its file and its instructions. What is left
in the Start menu is the product folder and the Hub link.

**Two faults found by installing rather than reading.** The configuration writer
refused with *"it would have removed"* and named nothing, on a config with no
other MCP entries - a false positive in the guard that protects other servers.
And the whole final section of Setup sat behind `if WizardSilent then exit`, so
a quiet install skipped the handover entirely; copying the files is installing,
not reporting, and now happens either way.

## v1.3.2 — 2026-09-14

**The installer's post-install steps never ran.** A screen recording of an
ordinary install showed `CreateProcess failed; code 267`, then a success page.
Nothing had been configured: no client registered, no Claude Desktop connection,
no `.mcpb` offered. Three separate faults, each hidden behind the next, and all
of them present in every release before this one.

- **The helpers ran before the server existed.** Inno executes a `[Run]` entry
  without the `postinstall` flag during *Finishing installation*, which is before
  `CurStepChanged(ssPostInstall)` — and that is where this installer swaps
  `server.installing` into place. The working directory could not be entered:
  267, `ERROR_DIRECTORY`. They now start from `ssPostInstall`.
- **They ran under 32-bit PowerShell, where System32 is a lie.** Setup is a
  32-bit program, so the system directory was redirected to SysWOW64 — where
  `conhost.exe` does not exist. Measured: from a 32-bit PowerShell,
  `Test-Path C:\WINDOWS\System32\conhost.exe` is False while the file plainly
  exists. Setup now uses the native path, and `complete-install.ps1` resolves
  System32 or Sysnative by its own bitness instead of assuming.
- **Every child inherited a working directory that was deleted.** The chain led
  back to Setup's temp folder, which Windows removes as Setup exits, so starting
  the MCP server failed with *The directory name is invalid*. Setup passes the
  install directory; the stdio helper starts a command in the command's own
  folder.

**One language at a time.** Prose picked a language while shortcut names, the
wizard's task text and the Hub action were Spanish literals, so an English Setup
told people to click shortcuts that existed only in Spanish. All of it now comes
from one set of `CustomMessages`, used both by the Start-menu entry and by the
text that names it. Verified by installing in each language and reading the
result off the screen.

**The final dialog states the `.mcpb` path.** The extension route is the one
where a human has to find a file, and a route that ends in "go and look for it"
is not a route.

**An open Claude Desktop is a pending step, not a failed install.** It was
recorded as `failed`, which sends people to reinstall a product that installed
correctly. It now records `pending_user_action` and names the one click left.

## v1.3.1 — 2026-09-13

The 1.3 release. Everything described under v1.3.0 below ships here; that tag
was never published, because its five-year live matrix was not clean and
published tags in this repository cannot move.

**Geometry on a level whose elevation is not zero.** Four defects, each measured
live in Revit 2023 and 2026 against the 1.3.0 candidate, each invisible while
every fixture used a level at elevation zero.

- A wall asked for an absolute base Z was verified against its `LocationCurve`,
  which sits on the level's reference plane, not on the physical base. A wall
  based at Z=0 on a level at 5 ft reports `LocationCurve.Z` 5.0 with a base
  offset of −5.0, while its solid starts at 0 — where it was asked to. The
  postcondition therefore refused correct geometry and rolled the batch back.
  Z is now compared against the element's own base constraint, re-read after the
  commit, and the solid's measured elevation span is published beside it.
- A structural column was never placed where it was asked. Revit drops the Z of
  the creation point, and translating the instance by that Z changes nothing.
  The elevation a level-based instance obeys is its base offset, so the move is
  confined to XY and the elevation goes through the parameter that governs it.
- `level_elevation` re-read the level that was *requested*, which can only agree
  with itself. It now re-reads the level the committed element carries.
- `BeamSystem.Create` can silently bind another level — measured, the lowest
  level of an Autodesk MEP sample binds one level up — and refuses a level with
  no plan view through a message that names a parameter. Both are refused by
  name now.

**Tags.** `horizun_create_family` gains `source_path`, which loads an `.rfa`
that already exists and re-reads the family and its types from the project
afterwards; the public Revit API cannot create a label in an annotation family,
so a usable tag family can only come from a file. A label-only tag family —
which is every stock Autodesk tag — publishes no extent in the API, and
requiring one refused all of them for a fact that says nothing about whether the
tag is correct. Placement is verified by view, host, head, orientation, leader
and text; the missing extent is reported with its reason, and layout says when
it placed a tag as a point rather than claiming a clearance it did not take.

**Claude Desktop.** Setup staged a `.mcpb` under `%LOCALAPPDATA%` — a folder
Explorer hides and a file picker cannot browse to — and asked the user to find
it. The helper now writes the documented configuration entry and finishes on its
own; `-Extension` keeps the package route and asks where to put the file,
offering `Documents\Horizun`, then opens Explorer there. It does not write to
the Desktop: where somebody's files land is their choice, not the installer's. The installer's own dialogs now speak the language the
user chose and name the shortcuts that really exist: the success dialog was
English in a Spanish install and pointed at a shortcut name that was not on the
Start menu. The command-line shortcut no longer closes its own window on an
unlabelled hex fingerprint, and a machine with only Claude Desktop is told so
instead of getting an error about two CLIs it never installed.

## v1.3.0 — tagged, never published

Integrates the BIM production workflows, owner-local mode and history controls,
workshared protection, annotation/sheet checks, PDF verification and delivery
ledger developed after 1.2.1. Enterprise policy and receipt forwarding remain
optional and explicitly configured.

Requested geometry is re-read after commit, with atomic rollback on mismatch.
Creation adds explicit point coordinate modes, per-edge roof slopes, wall
profiles, stairs, displacement sets and per-element parameters/source references.
Architectural type and family-symbol duplication gains real rollback rehearsal.
Existing production creation, MEP connections, tag editing and curve editing are
preserved. `wall_join` uses `join_end`, keeping `end` as the curve endpoint.

Save operations respect `dry_run`. Python source/include hashes participate in
idempotency, structured transport errors preserve the underlying cause, and
progress reports distinguish queueing from execution. Temporary capture options
restore view state; eligible plan/section captures return measured world-to-pixel
calibration. Python remains owner-controlled and self-reported.

The README, installation FAQ and LLM instructions foreground the downloadable
Windows installer and distinguish its included runtime from the SDK needed only
to compile source. See [release details](docs/RELEASE-1.3.0.md). Final package and
live release-matrix evidence are still pending; this entry is not a release claim.
Its live matrix failed on Revit 2023 and left two probes unverified in every
year. The tag stays where it is; the work ships as v1.3.1 above.

## v1.2.1 — 2026-09-04

ChatGPT Work is a supported client through OpenAI's Secure MCP Tunnel. The
installer ships setup, credential, status and diagnostic helpers and names the
client explicitly. This was verified in the desktop Work interface with a free
account. Active organisation-specific classification skills were removed, and a
release gate prevents their terminology from returning in current product files.

## v1.2.0 — 2026-09-04

The first release since **1.1.6**, and the version stamp has said `1.2.0` since
the work below started: no `v1.2.0` tag or release exists anywhere, so this is
that version being finished rather than a new number. MINOR under the release
policy — new tools, new optional arguments, new fields, new refusals for cases
that were previously undefined — and **not** a MAJOR: no tool was removed or
renamed and no returned field changed meaning.

**The contract hash is `d1b3abb98b4d8df680572b29` (80 tools).** It differs from
1.1.6's, so the server and the Revit add-in **must be installed together**; a
mixed pair refuses to pair rather than half-working. There is no partial
deployment and no separate server version.

**One installer now prepares Codex, Claude Code and Claude Desktop.** Every client
runs the same installed stdio server. The completion helper registers Codex and
Claude Code beside their existing MCP servers after each client closes, while
Claude Desktop receives a real Desktop Extension (`.mcpb`, manifest_version 0.3)
that names the installed server. Its machine-local copy carries the resolved path
instead of depending on `${HOME}` expansion. The one action owned by Claude
Desktop — selecting **Install Extension** — is recorded as
`pending_user_action` with the exact staged package rather than reported as done.

Claude Code and Codex CLI remain optional: the installer does not require either
executable to install the Revit bridge or prepare Claude Desktop. Per-client state
is durable in `install-status.json`; backups preserve all existing MCP entries.
`horizun_execute_python` remains refused by default and connecting any client
never grants it. See `docs/CLIENTS.md` for the universal GitHub installation and
the final step for each client.

**Upgrading.** Close Revit and every MCP client, then run the installer and
acknowledge that it is unsigned — public releases are unsigned by policy and
carry no Windows publisher identity. The server and the add-in are installed
together; a mixed pair refuses to pair rather than half-working, so an upgrade
that stops half-way leaves nothing usable until it is finished or rolled back.
From source: `git pull`, close Revit, run `install.ps1`.

**Known limits of this release, stated as limits.** The effective state of a
workset closed on a LINKED model is not readable through the public Revit API
that was inspected: a link loaded with a workset closed reports every workset
open and still hands over that workset's elements, so a takeoff cannot tell the
two loads apart and every linked document row now says so instead of implying a
coverage it cannot support. A Revit-side failure inside one action of a confirmed
correction plan, and a geometry read that throws, are proved over the real code
paths offline but were never produced by Revit in the available fixtures. An
element held by a second Revit user, a model in ACC and a real Power BI push
remain unmeasured for want of a second user, an authorised disposable cloud model
and an authorised test destination — those are absences of a resource, and none
of them is described here as fixed or as passing.

**Static, at the head:** Core tests 3740, Server tests 476, the add-in compiled
for Revit 2023–2027 with `-warnaserror` and no warnings, and the 23
`scripts/*.tests.ps1` gates the PR workflow runs, all passing.

**Live, across FIVE Revit years** (2023, 2024, 2025, 2026, 2027), without
replacing the installed pair: **878 unique cases - 841 passed, 0 failed, 16
unverified, 21 fixture-missing** over 87 runs and 3230 recorded results, every
year measured at the head. Every case carries result_status, evidence_level and
blocker_kind, and the published matrix is RENDERED from the record rather than
typed beside it.

NOTHING IS FAILING, and what is not passing is labelled as one of three
different things: an EXTERNAL BLOCKER (21 cases - a second Revit user, an
authorised ACC model, an authorised Power BI destination), NOT OBSERVABLE
through the API (5 - the effective state of a workset closed on a link), or
STRUCTURALLY VERIFIED but not reproduced live (11 - a Revit-side failure inside
one action of a confirmed plan, and a geometry read that throws). A condition
nobody observed is no longer filed as a missing fixture.

- **An action whose postcondition could not be READ is `uncertain`, not
  `failed`.** The apply loop moved to `CorrectionApplyLoop`, in Core and
  Revit-free, driven by a delegate - the substitutable thing is the step
  EXECUTOR, and the shipped command builds exactly one, the one that dispatches
  the typed child. There is no failure switch in the product. That made all six
  situations of `rollback_scope: per_action` testable, and one was wrong: a step
  that came back `uncertain` made its action `failed`, which claims knowledge
  nobody has. The write may have happened; the action now says so, and the
  re-audit sends its elements to not_verifiable rather than to failed or
  corrected. The scope is defined where it is implemented, in five numbered
  promises, and pinned by CorrectionApplyLoopTests.

- **A workset closed on a LINK is not observable, and the reply says so.**
  MEASURED: a link created with a WorksetConfiguration closing one workset by id
  reports every workset open inside the linked document AND hands over that
  workset's 392 elements - identical, element for element, to the same type
  reloaded with every workset open. The API exposes no way to read back a link's
  load configuration. Every linked document row of a takeoff now carries
  `linked_document_means` - an absence in a link is NOT evidence of a closed
  workset - the headline stops blaming a link's worksets for an absence, and the
  probe reports requested_closed apart from observed_closed.

- **An inventory finding is corrected when the item CHANGES, not when it
  vanishes.** The re-audit judged every correction by disappearance, and the
  `links` check lists every link type with its status - so a reload that applied,
  and that the typed child verified as `Loaded`, was reported `persistent` in
  every Revit year. `CorrectionRecipe.Postcondition` now says which of the two a
  recipe means: `removed_from_finding` for the defect lists (unchanged) and
  `item_leaves_the_filter` for the links/reload recipe, judged on the same typed
  `status` the recipe already filters on. An item that is NO LONGER LISTED is
  not_verifiable, never corrected - it may have been deleted, or cut off by top.
  The row publishes the postcondition it applied and the value it read per
  element. Fixed, and green in all five years; the regression fails on the code
  before the fix.

- **One broken link hid that defect in every year.** The Snowdon fixtures carry a
  link whose file is not on disk; it unloads to `NotFound`, the typed command
  refuses to call that an unload - correctly - and the corrections harness, which
  only ever tried the FIRST link type, reported the whole reload recipe as a
  missing fixture. It now tries each link type in turn and says what it tried.

- **`ElementId.Value` is a 2024 API.** The DWG harness read it when staging an
  imported drawing, so on Revit 2023 the script threw and the harness recorded
  "Revit gave back no ImportInstance" - a harness bug reported as a missing
  fixture, in the one year where the fixture was perfectly possible.

- **The evidence pipeline refuses what it cannot count, and chooses what it
  stands on.** `consolidate-live-session.py` rejects a session carrying a status
  outside the nine buckets, a run that cannot name its commit and binaries,
  totals that disagree with the probes, or an undeclared repetition. A CASE is
  keyed on the SCENARIO rather than the document title, because Revit renames a
  detached copy and `HZ_CLOSED_L_detached_1` is not new coverage. The accepted
  run of a case is the latest that QUALIFIES - clean tree, a named candidate, the
  right contract - and a run that does not qualify is kept as history with the
  reason it was not chosen; a case with nothing that qualifies fails the session
  by name. Results are grouped by the bytes that produced them.

- **A run keeps its own binaries.** `run-year-matrix.ps1` copies the signed DLL
  Revit loaded, the UNSIGNED build it came from and the server executable into
  the artifact directory, named by hash, because the development store holds one
  signed copy per year and the next session signs over it. It also reads the
  server's stamped commit - the driver does not build the server, and a run that
  records only a hash names a file nobody can attribute afterwards.

- **The server was reproducible all along.** The claim that it does not set
  `DeterministicSourcePaths`, so a rebuild elsewhere cannot reproduce its bytes,
  was wrong: `-getProperty` answers true and two worktrees of one commit produce
  the identical DLL, MVID and apphost. What was actually wrong is that the driver
  hashed a server it had not built, whose own stamp said it came from another
  commit and a dirty tree.

- **A signed binary is tied to its source by a hash, not by an MVID alone.**
  `scripts/live/verify-binary-provenance.ps1` rebuilds the candidate in a
  throwaway worktree; for every year the rebuild is BYTE-IDENTICAL to the
  pre-signature artifact the run kept, and the MVID only carries that identity
  across the signature, which changes the file. Signed files that a later session
  signed over, and that nobody kept, are named as unrecoverable rather than
  re-derived.

- **Breaking for a mixed deployment:** the contract hash changed. Deploy the
  server and the add-in together.

### Landed first, on 2026-09-01

- **Typed multi-layer wall decomposition.**
  `horizun_split_multilayer_walls` creates one independent wall per volumetric
  layer and names each type from the original type, material and layer number.
  The original wall becomes the core carrier, preserving its `ElementId`,
  `UniqueId`, hosted doors, windows, openings, sweeps, reveals, dimensions and
  tags. Straight and arc walls, all six location lines, joins, structural
  footings and hosted reinforcement are verified after commit. Rebar may follow
  its carrier curve or either constrained face point by point; geometry-derived
  shape dimensions may change only when both shape ownership and the centreline
  constraints are proved. A wall that cannot preserve its dependencies rolls
  back atomically.

- **Model Doctor over the native RVT.** `horizun_model_scan` now exposes 32
  paged diagnostic sections for performance, model hygiene, naming, families,
  views, sheets, annotations, coordinates, datums, worksharing, structure,
  federation and delivery readiness. `horizun_audit_model` adds durable
  snapshots, trends, a coverage-aware health index, typed correction proposals
  that remain dry-run, and a prevention decision that never claims to enforce a
  Revit save. Caller-supplied 4D/5D and documentary profiles stay
  organisation-neutral; absent profiles are `not_requested`, never a pass or a
  failure invented by Horizun.

- **Measured in one Revit 2026 session (build 26.4.0.32), on the commits
  before the version bump.** The wall matrix ran at `b08b7a2` and accounted for
  all 55 cases: 45/45 executable cases passed, with five unavailable fixtures,
  one multiuser environment case and four public-API limits kept in separate
  buckets. The Model Doctor ran at `a792663`: 71 passed, 0 failed, and five
  cases that need ACC or real multiuser/workset state recorded as
  fixture-missing rather than simulated. Both binaries carried the same source
  as this release (`git diff b08b7a2 1b3575c -- src` is empty) but were stamped
  `1.1.6`; no `1.2.0`-stamped binary has been measured, and Revit 2023, 2024,
  2025 and 2027 were not opened. Every number is bound to its commit, binary,
  contract hash and fixture in `docs/evidence/release-1.2.0-live-evidence.md`.
  Neither campaign saved its disposable fixtures.

- **Breaking migration:** `horizun_audit_model` requires
  `target_document`. The server and Revit add-in must be upgraded together so
  their contract hashes match.

## v1.1.6 — 2026-08-29

- **The optional Session panel has been removed from Revit's ribbon.** Tool
  packs, permission inspection, planimetry auditing and durable-job status remain
  available through the MCP surface, but Revit no longer shows the four
  provisional letter-icon buttons for those operations. The primary bridge,
  Python-consent and Horizun Hub controls are unchanged.

## v1.1.5 — 2026-08-29

- **A planimetry census no longer regenerates every placed view.** The protected
  `v1.1.4` release matrix passed Revit 2023–2026, then Revit 2027 crashed natively
  on the first `horizun_query_planimetry mode=inventory` call. Its journal
  recorded access violation `0xc0000005` inside `generateViewSpecificGRep` while
  Revit was producing viewport graphics for unrelated MEP views. The inventory
  answer asks only “how many?”, but the shared collector had materialised every
  viewport outline and annotation box before returning those counts. An
  unscoped inventory now takes a lightweight exact-census path: population
  totals still come from native collectors and reference states remain
  explicit, while viewport, crop and annotation geometry is reserved for the
  detailed `placements`, `views` and `annotations` modes that actually return
  it. Missing counts remain null and named, never fabricated as zero.

## v1.1.4 — 2026-08-29

- **The release gate now asserts the actual queued-cancellation guarantee.**
  Revit 2023 in the `v1.1.3` matrix proved that an apply cancelled while still
  behind another command never wrote its grid and consumed neither its
  confirmation token nor its durable idempotency key: the identical retry ran
  freshly and produced exactly one grid. The harness still asserted an older
  measured no-recall path for work already delivered to Revit, contradicting
  both the probe title and the safer observed result, so it marked correct
  behaviour as a failure. The probe now distinguishes the boundary explicitly:
  queued work must be removed before start; work already running remains
  non-interruptible. The release harness no longer guesses that boundary from a
  sleep: a marker proves a blocking command owns Revit's UI thread, the add-in
  log proves the apply entered the FIFO behind it, and the MCP cancellation
  reply must prove it was removed before start. Revit 2026 then passed the full
  gate 242/242 with 157 committing probes.

## v1.1.3 — 2026-08-29

- **No dropped command in Revit's callback-unwind window.** The `v1.1.2`
  release matrix measured Revit 2026 return `ExternalEventRequest.Denied` to a
  sequential caller only 4 ms after the preceding command completed; Revit
  remained open and served every later request. Horizun had treated every
  `Denied` as terminal shutdown, so that one dry run never entered the FIFO and
  the stable release was correctly blocked. The dispatcher now retries that
  answer for a short, bounded interval off Revit's UI thread, while repeated
  denial and shutdown still close untouched work as `not_started`. Four pure
  sequence tests plus a wiring regression cover transient Denied → Accepted,
  transient Denied → Pending, terminal repeated denial and Unknown.

## v1.1.2 — 2026-08-29

- **Deterministic complete-surface inventory.** The inventory generator now
  launches the measured server with a disposable `HORIZUN_DATA_ROOT`, the full
  permission profile and all tool packs. It therefore counts the complete
  product contract without inheriting a developer's or hosted runner's active
  modelling profile, and it never reads or rewrites that user's settings. The
  regression test deliberately supplies a restrictive parent profile and still
  requires the complete 78-tool inventory.

## v1.1.1 — 2026-08-29

- **Clean-runner release fix.** Inventory generation now preserves a complete
  executable path when a fresh checkout contains only the Release server
  binary. PowerShell previously unwrapped the one-item candidate list into a
  string and indexing it selected the first character (`C`) as the process to
  execute. Developer machines with both Debug and Release outputs masked the
  defect. A regression gate now pins the one-binary case. The structural-state
  fixture also creates its own `docs/evidence` parent so it runs in the public
  projection where private evidence artifacts are intentionally absent. No
  Revit command, schema or model behaviour changed from v1.1.0.

## v1.1.0 — 2026-08-29

- **Native-model diagnostics, snapshots and readiness.** The diagnostic surface
  now reads coordinates, datums, naming, families, views, model weight and
  delivery readiness directly from the active RVT, without IFC, COBie or a PDF
  export. Requirement-set rules remain organisation-neutral and fail closed on
  incomplete coverage. A durable local snapshot and health-index layer records
  provenance, coverage and run-over-run changes without uploading model data.
  The P0 slice was measured live on Revit 2026 at candidate `09fc20b`: 16/16
  probes, 0 failed / 0 unverified / 0 not covered. The wider 33-story programme
  remains explicitly tracked in `docs/MODEL-DIAGNOSTICS-PROGRAM-STATE.json`;
  incomplete backlog items are not presented as released behaviour.

- **Structural modelling and reinforcement.** Typed structure reads and verified
  rebar planning/apply/audit now cover hosted bars, zones, slab mats, geometry
  containment and quantities, with explicit refusals for design decisions the
  caller did not supply. The dedicated matrix ran the same candidate on Revit
  2023, 2024, 2025, 2026 and 2027: 60/60 probes per year, 300/300 total, with no
  failed, unverified, not-covered or fixture-missing result. Exact evidence and
  known exclusions are recorded in `docs/evidence/structure-matrix.json` and
  `docs/STRUCTURAL-PROGRAM-STATE.json`.

- **Expanded production surface.** Loaded-link dimensioning, whole-chain
  annotation planning, per-room view planning, richer view and sheet operations,
  schedule definitions, revisions, durable jobs, MEP/coordination/penetration
  workflows, tabular exchange, family checks and dynamic tool packs ship as one
  compatible minor increment. The generated contract inventory contains 78 MCP
  tools and 182 dispatched operations; those are capability counts, not claims
  that every enum variant has live coverage.

- **The release harness now has one owner for MCP stdout.** The tool-pack live
  probe no longer starts a second asynchronous read while the first is pending,
  and it edits the isolated `HORIZUN_DATA_ROOT` used by the release run instead
  of the machine owner's settings. The first `1.1.0` candidate was rejected on
  Revit 2023 by this race before it could produce a report; the source gate now
  pins both invariants.

### DWG-to-BIM and installation hardening — 2026-08-27

- **DWG → BIM, phase DWG-4.** A linked drawing becomes a model through a
  requirement set the caller writes; no layer name, family or office standard is
  compiled in. **Measured live on Revit 2026 at candidate `c3deb2f`: 238/238
  probes across eighteen versioned harnesses under `scripts/live/`, every artifact
  from ONE build** — `scripts/live/verify-dwg-all.ps1` runs them in a fixed order
  and refuses to add up results that are not.

  New in this phase: **typed CAD linking** (`horizun_manage_cad_links`) verified
  by content, with `unload` refused by name because `CADLinkType` has no `Unload`
  in 2023–2027; **curved walls** surviving drawing → element → audit; **floors
  with holes**; **rooms** placed by a point genuinely inside an L; **doors and
  windows hosted** in the wall the drawing implies, with a refusal that names the
  two-pass order; **architectural and structural columns, grids, beams**; a
  `structural` flag re-read from Revit's own parameter; `bridge_openings_mm`, so
  a wall a plan drawing breaks at every opening is read as one wall; the
  **twelve-name change vocabulary** for an incremental revision, every count
  reported including the zeros; the audit's **substance codes** `unhosted`,
  `type_differs` and `size_differs`; **planimetry** derived from the converted
  model and audited in the model, never through a PDF; and **S/M/L performance**
  against budgets declared in source before anything was measured.

  Seven product defects were found by the run series and fixed. The two worth
  naming: a producer that never called `Finalise`, so **every curved wall ever
  read came back at confidence 1.00 with no reason given and was silently held
  back** — from outside, a drawing full of curved walls converted to nothing; and
  a compound wall exporting **six** concentric arcs, of which the reading built
  two walls, the second out of the first one's material layers.

  `horizun_query_model` can now be asked what an element lives IN (`host_id`,
  `host_category`) — there was no typed way to check hosting, so it could only
  be believed. `horizun_query_cad` publishes the drawing's **arcs as arcs**, not
  only as the chords they were also broken into.

  Guide: `docs/DWG-TO-BIM.md`. The decision NOT to read DWG files directly, with
  the conditions under which to reopen it: `docs/ADR-001-direct-dwg-reader.md`.
  Ledger: `docs/DWG-PROGRAM-STATE.json`, generated from the roll-up artifact.

- **`install-status` can no longer claim more than it checked.** `live_verified`
  now requires five things to agree, each recorded beside the answer: the server
  that replied is the one INSTALLED at `server_path` (by SHA-256), the commit it
  reports is the one stamped into that binary, the contract hash is readable, a
  Revit is named, and a document is open. Anything missing writes
  `deployed_pending_health` with the reason. On a development machine an older
  `horizun-mcp.exe` left running answers `horizun_health` perfectly and reports
  its own commit — that is why.

### Model and document production — 2026-08-25

- **Dimensions into loaded RVT links.** The host-only rule is gone:
  `horizun_get_dimension_references` takes `linked_targets` (one entry per link
  INSTANCE — two placements of one file are two identities), reports the link
  instance, link type, linked document and linked element as four separated ids
  with the placement transform and its 0.1 mm-grid fingerprint (handedness
  included), and returns geometry in HOST coordinates. `horizun_annotate`
  consumes the returned host-document representations — mixed host+linked
  chains included — and binds the link's placement into the plan, so moving the
  link between rehearsal and apply refuses as `stale_plan` naming
  `link_transform_moved`. `horizun_query_dimensions` resolves linked references
  THROUGH the live link when loaded and reports `link_state`/`reference_coverage`
  instead of counting an unloaded link as broken. Unloaded, missing, nested and
  non-creatable cases each carry a structured code.

- **Whole-chain and per-room production planners.** `horizun_plan_annotations`
  gains `auto_dimension_grids`/`_levels`/`_curtain_walls`/`_openings` (host or
  ONE named link instance): direction grouping at a stated 0.5° tolerance,
  positional ordering with a 0.1 mm coincidence refusal, duplicate detection by
  unordered reference-set identity, stacked chains, structured omission codes
  and a never-optimistic coverage verdict. New read-only `horizun_plan_views`
  plans per-room deliverables (oriented elevations via the smallest turn a
  four-way marker's symmetry allows, crossing sections from exact box support
  functions, a cropped plan) under a token naming pattern that refuses unknown
  tokens, returning a complete `horizun_manage_views` request.

- **`horizun_manage_views` grows from 12 to 24 operations.** Area plans,
  callouts, placeholder sheets and their conversion, sheet duplication with or
  without content (refused by name when the source carries placed schedules),
  phases, scope boxes, view ranges, rectangular crops carried through the crop
  frame conversion, annotation crops, viewport retyping against `GetValidTypes`,
  cross-sheet viewport alignment against a still anchor, and elevation-marker
  rotation verified by re-reading the turned view direction. Sheet numbers are
  checked unique against the document AND the batch before anything runs.

- **`horizun_manage_schedules` (new).** Schedule DEFINITIONS as a verified
  batch: material takeoffs, sheet/view lists, revision schedules and keynote
  legends created; fields resolved by stable parameter id or unambiguous name
  (a name matching two columns refuses listing both ids); filters and sorting
  DECLARED whole so replays are idempotent; the confirmation token binds each
  target's whole definition fingerprint; replies carry the canonical definition
  before/after and the changed sections. `horizun_manage_revisions` learns to
  WITHDRAW a revision from sheets, refusing by name the case where it reaches
  the sheet through a cloud.

- **Dynamic tool packs.** `tool_packs` in settings.json selects named subsets
  of the 62-tool surface (core is welded on; dependencies arrive transitively
  and visibly; malformed configurations fall closed to core-only, loudly). A
  pack is visibility, never elevation — the permission profile stays the
  authority, execute_python still needs the owner grant, and hidden means
  unreachable on every dispatch path including `horizun_execute_plan` children.
  `HORIZUN_TOOL_PACKS` gives administrators an environment override; compatible
  clients refresh via `tools/list_changed`; `horizun_health` publishes the
  active selection. See docs/TOOL-PACKS.md.

- **The Session ribbon panel.** Tool packs, security (profile, Python origin,
  recent ribbon changes), a planimetry review that runs the SAME read-only
  audit an MCP client runs, and the durable job records — all native dialogs,
  bilingual, and never a write path: corrections stay behind the typed tools.

## v1.0.0 — 2026-08-25

- **Version 1.0.0 and permanent unsigned-release policy.** The shared product
  version is now 1.0.0. Public Windows artifacts remain intentionally unsigned:
  the bootstrap requires `-AllowUnsigned`, CI enforces `NotSigned` on every
  Horizun-owned binary and Setup, and package metadata discloses that publisher
  identity is unavailable. SHA-256 manifests, SBOM, GitHub attestations, exact
  installed-byte verification and the full Revit matrix remain mandatory.

- **End-to-end planimetry production.** The direct-model audit now feeds a full
  typed production surface: deterministic whole-sheet packing
  (`horizun_pack_sheets`), read-only collision-aware tag and semantic
  intent-dimension planning (`horizun_plan_annotations`) through the existing
  rehearsed `horizun_annotate` writer, atomic revision/sheet/cloud production
  (`horizun_manage_revisions`), and the `planimetry-review` MCP prompt for visual
  judgement over real sheet PNGs without a PDF intermediary. Explicit tag types
  and the pre-existing tag count are bound into the materialised plan. Production
  annotation discovery uses document-wide class sweeps filtered by
  `OwnerViewId`, avoiding Revit 2023's measured unopened-view collector gap.
  Unplaced sheet content is measured through a real provisional viewport or
  schedule instance (including the viewport label) with a confirmed rollback;
  apply spends approval before that measurement transaction and preserves the
  measured offset between the paper rectangle and Revit's insertion point.

- **Deletion now fails closed when its discriminator is missing.**
  `horizun_delete_verified` requires an explicit `mode`; omitting both `mode`
  and `ids` no longer selects the broader `purge_unused` branch. `dry_run` had
  prevented an immediate deletion, but a malformed request must not even
  rehearse a destructive scope the caller did not name.
- **Live evidence now identifies its harness.** New release-gate reports carry
  the committed `verify-live.ps1` commit, Git blob and SHA-256, and the evidence generator
  requires the same clean harness across all five years. Historical schema-2
  evidence remains valid. The completed schema-4 matrix pins candidate
  `32baa87`: Revit 2023–2027 each passed 165/165 probes (825/825 total), and
  separately records 23 correction plus 5 autonomous-production cases per year.

- **Planimetry corrections — `horizun_fix_planimetry`.** The auditor had eyes;
  this is the hands, and they never guess. Nine typed operations —
  `set_view_template`, `set_view_scale`, `rename_view`, `rename_sheet`,
  `place_title_block`, `move_viewport`, `move_schedule`,
  `clear_element_override`, `set_crop` — with every final value explicit in the
  request. A missing instruction is a refusal, not a choice.
  - **A finding is the only licence to write.** Every action cites the finding
    it repairs (rule id, requirement set with version and SHA-256, element ids,
    and the `observed` block verbatim). Before any transaction opens the whole
    audit is recomputed through the auditor's OWN collector and rules, and the
    action is refused when the finding is gone (`STALE FINDING`), when the
    model no longer shows what was observed (`STALE OBSERVATION`, printing both
    states), when the finding is currently `unknown` — an unmeasured fact is
    never corrected — when the operation does not address that rule, or when an
    inline requirement set hashes differently from the one the finding cites.
  - **Rehearsed by materialisation, not by prediction.** `dry_run` defaults to
    true and CREATES the whole batch provisionally inside a transaction,
    measures every postcondition there, and rolls back; a rollback Revit does
    not confirm withholds the token and reports the call `uncertain` rather
    than clean. The token binds the request AND the resolved elements'
    before-state, so a model that moved refuses as `stale_plan`.
  - **Atomic, then re-read.** The apply commits ONE `TransactionGroup`; any
    failed action or postcondition rolls the ENTIRE batch back. Every promised
    property is then re-read from the committed model — including the sheet
    field that was *not* renamed (whose expected value is its before-value, so
    a fix that quietly moved it fails), a title-block count that must stay at
    exactly one, and proof that clearing an element override left the CATEGORY
    override and the view template untouched.
  - **`resolved` is the auditor's verdict, not the writer's.** After the commit
    the full audit runs again — a partial re-evaluation of only the affected
    checks is not demonstrably equivalent when overlap and coverage are
    cross-entity — and the reply separates findings resolved, persistent and
    NEW, with coverage before and after. A verified postcondition does not by
    itself resolve a finding, and resolving one cannot hide that another
    appeared. If the re-audit fails, nothing is declared resolved.
  - No export, no PDF and no arbitrary-code path anywhere on this surface, each
    pinned by a source-scanning test. Packing, auto-tagging, dimensioning by
    intent, revision generation and visual judgement are refused BY NAME in
    every reply's `not_covered` rather than approximated.

- **A description cap that did not hold.** `Tools.CompactDescription` promised
  900 characters and could return 901: the ellipsis it appends was never
  counted against the budget, so a description whose sentence boundary fell
  exactly on the limit came back one over. The only coverage was an aggregate
  assertion over the descriptions that happened to exist, every one of which cut
  earlier — so the defect was waiting for the next tool, and the planimetry fix
  was the next tool. Fixed, and pinned by a sweep across every input length
  through the boundary instead of a sample of the current table.

- **The pinned-runtime check compared a version against a type name.** Every
  self-contained publish failed with `expected pinned System.Xml.XmlElement`
  while the publish itself was correct. PowerShell's XML adapter returns the
  `XmlElement` rather than its text once the element carries an attribute, and
  `RuntimeFrameworkVersion` carries `Condition`. `pack.ps1` and `sbom.ps1` now
  read through `SelectNodes` + `InnerText`, which does not depend on whether an
  element happens to be attributed.

### Planimetry foundation — 2026-08-24

- **Planimetry, read and audited from the model — `horizun_query_planimetry`
  and `horizun_audit_planimetry`.** The documentation surface (sheets, views,
  viewports, schedule placements, dimensions, tags, text, 2D detail,
  view-to-view references) is now queryable and auditable directly from the
  database, with real ids, sheet-coordinate and view-plane geometry, explicit
  coverage and no PDF anywhere in the loop. Both tools are read-only by
  construction — no `Transaction` on the whole path, enforced by a
  source-scanning test — and both consume ONE shared collector
  (`PlanimetryInventory`), so the query and the audit cannot disagree about
  what is on a sheet.
  - The query answers in six explicit modes (`inventory`, `sheets`, `views`,
    `placements`, `annotations`, `references`), deterministically ordered and
    paginated with cursors bound to both the arguments and the result set; a
    total the inventory could not compute is absent and named, never zero, and
    a reference the API cannot resolve is an explicit `unknown` with the
    reason, never inferred from a name.
  - The auditor returns findings — `blocking`, `advisory` or `unknown`, no
    0–100 score — from a universal catalog of 46 checks that are true without
    any company standard (touching placements are NOT overlapping; references
    into links are NOT broken; a view without a template is advisory, not a
    defect), plus an INLINE requirement set for everything with a number or a
    name in it: naming, allowed scales/templates/types, margins and gaps,
    required parameters, forbidden overrides, and tag coverage that counts
    only elements visible in the view, separates host from linked, names the
    exact untagged ElementId and honours explicit exclusions. A malformed set
    is refused whole; every regex runs under a match timeout; every finding
    cites the set's id, version and SHA-256.
  - An unreadable fact is ALWAYS `unknown` and never a pass, a check with
    unknowns is never `passed`, and incomplete coverage (a died pass, an
    unreadable field, a closed workset, an unloaded link) is stated in words
    on the reply. This phase applies no correction: `fixable` is false on
    every finding, and the deliberate non-judgements (which walls should be
    dimensioned, whether a sheet "looks right") are published per reply in
    `not_covered`.
  - The five-year live matrix is green WITH the planimetry section: 136
    probes per year (114 previous + the 22 planimetry cases, split 11 query /
    11 audit), 0 failed / 0 unverified / 0 not covered on Revit 2023-2027,
    dimensions still 17/17 and 2D detail still 11/11, all bound to one
    candidate. Three product facts fell out of getting there, each measured
    live before a line changed: TextNote.Text appends a terminating CR and
    re-encodes line separators (the annotate text verification refused every
    correct note ever created - fixed via a pure, 13-case-pinned
    normalisation that undoes exactly Revit's re-encoding and nothing more);
    the view-scoped FilteredElementCollector omits elements that are
    demonstrably in a not-yet-regenerated view (tag coverage now decides
    visibility by substance: un-hidden + bounding box in the view +
    crop intersection); and Viewport.Create returns null - without throwing -
    for an EMPTY drafting view, which manage_views only discovers after its
    commit (recorded in the backlog).
  - Everything except the Revit read is proved Revit-free: 163 Core cases
    (layout geometry where touching is not overlapping and an unreadable box
    is not an empty one at the origin; loader refusals; universal and
    configurable rules; determinism; contract and permission visibility) plus
    4 Server cases for the published tools/list entries. The live gate gains a
    22-case planimetry section per Revit year — staged sheets with a known
    overlap, a deliberately untagged pipe, a stale cursor refused by name, a
    byte-compared twin audit, an unloaded link proving coverage degradation —
    and the durable evidence manifest moves to schema 2, refusing any year
    whose planimetry cases are not all green across BOTH tools. Measured on
    the way in: the members this surface uses are identical across all five
    RevitAPI.dll generations, except the `GuideGrid` class, which exists in
    none of them (the guide grid is read via
    `BuiltInParameter.SHEET_GUIDE_GRID`). Full reference:
    `docs/PLANIMETRY-AUDIT.md`.
- **The live matrix has a durable, sanitized in-tree record.** The full
  verify-live reports never enter Git (they carry machine-local paths and ids;
  per release they travel as attached artifacts), which left the green matrix
  with no durable record at all. `scripts/generate-live-evidence.ps1` now
  distills the five artifacts into `docs/evidence/live-matrix.json` — per-year
  probe counts, case totals, Revit builds, and the SHA-256 of every artifact,
  server and add-in, bound to one candidate commit — and refuses to write
  anything that is not a complete green five-year matrix. The committed file is
  independently held to the same contract (green, coherent, machine-local-free)
  by `EvidenceManifestTests` in the Server suite.
- **The five-year live matrix is green at one commit.** The full release gate —
  114 probes, 31 committing, 17 dimension cases, 11 2D-detail cases, 0 failed /
  0 unverified / 0 not covered — passed on Revit 2023, 2024, 2025, 2026 and
  2027 against the same installed candidate, each artifact binding the server
  and add-in hashes to the release manifest. Three defects had to fall first,
  each measured rather than patched around:
  - The redistributed-runtime pin (`RuntimeFrameworkVersion` 8.0.30) was
    unconditional, so the ordinary framework-dependent server build — the one
    the test suite launches — demanded a runtime patch the machine did not
    carry and 14 wire tests reported their codes untested. The pin is now
    self-contained-only (the only route that ships a runtime), a regression
    test reads the built runtimeconfig and refuses any exact-patch demand, and
    the self-contained publish still resolves the exact pinned runtime pack
    that `pack.ps1`/`sbom.ps1` verify.
  - A shipped live report said `probes=112` beside 114 rows: the summary was a
    formula frozen before the manifest-hash checks existed. The summary is now
    computed from the definitive row collection, the harness refuses to write
    a report that disagrees with itself (counters vs rows, sums, duplicate
    names), and the gate independently re-checks the written JSON.
  - Measured year split: Revit 2023 exposes NO reference-carrying pipe
    centerline under any geometry `Options` combination — the discovery row is
    the negative `no_stable_centerline` there, while 2024–2027 expose the
    reference and carry the measured `mep_centerline_rejected_by_dimension_api`.
    The centerline probe accepts exactly one uniform measured branch per run,
    and the link-refusal probe stages its own `RevitLinkInstance` (a same-year
    copy, never-saved model) when the fixture ships without one.
- **Typed, verified 2D detail production.** Two new tools close the gap real use
  reported in 2026-08-04 ("the bridge can model a building and annotate it, and
  cannot draw a line on a sheet"):
  - `horizun_query_detail_2d` (read-only): one view's drawable resources - line
    styles, filled-region types with `IsMasking` read from each type, placeable
    view-based symbols with activation state - and its existing detail elements
    with normalised geometry and deterministic 0.1 mm signatures. Resources are
    never resolved by name: every answer is ids, so ambiguity cannot happen.
  - `horizun_detail_2d`: atomic batches of detail lines, arcs (two unambiguous
    forms), polylines under one key, filled and masking regions (both
    type-vs-operation mismatches refused, from the type's own flag), view-based
    detail components and generic annotations (activated inside the
    transaction), and line-style edits over existing curves or same-batch keys.
    Loops are proven pure before Revit is asked - closed, non-degenerate,
    non-self-intersecting, exactly one exterior containing every hole, in exact
    integer arithmetic on the signature grid. The rehearsal CREATES the batch
    provisionally and rolls it back; the token binds views, types, styles and
    normalised geometry; the apply verifies everything inside a TransactionGroup
    and rolls the whole batch back on any failed check. Coordinates are
    view-plane, and a non-zero third component is refused rather than silently
    projected.
- **What the dimension live bring-up measured, folded back into the product:**
  Revit materialises dimension references and values only for a DISPLAYED view -
  so `horizun_annotate` requires the active view for dimension operations,
  refused at plan time naming `horizun_navigate` as the fix; computed facts
  (values, `AreReferencesAvailable`, EQ) materialise after commit+regenerate, so
  creation AND `horizun_edit_dimensions` verify after a materialising
  regeneration inside the still-open TransactionGroup; the availability flag is
  computed lazily on instance-geometry references and reopened RFAs, so where
  every substantive check passes the row stands on substance and reports the
  flag as observed; a document whose default-type table is wrong falls back to
  the lowest-id type of the right style; and `NewDimension` refuses MEP-curve
  centerline references outright, so discovery marks them incompatible with the
  measured structured code instead of promising a creation Revit will refuse.

- **Dimension production became a verified workflow instead of a primitive.**
  Four connected surfaces, all typed:
  - `horizun_get_dimension_references` (new, read-only): semantic discovery of
    dimensionable references — wall side faces via `HostObjectUtils`,
    centerlines, grids, levels, reference planes, edges, endpoints,
    nearest/farthest planar face from an explicit probe point. Every candidate
    carries its stable representation, the geometry that justified it, a 0.1 mm
    quantised fingerprint and a structured incompatibility reason; equivalent
    candidates return marked `ambiguous` instead of one being chosen silently.
  - `horizun_annotate` (rebuilt dimension path): linear simple and chains,
    angular, radial, diameter, arc-length, spot elevation/coordinate. The dry
    run CREATES the batch provisionally and rolls it back — `constructible` is
    Revit's answer, and the reported rollback status is Revit's too. The token
    binds view, effective dimension type (materialised default included), every
    reference's stable representation, owner and geometry fingerprint, and the
    measured value; drift refuses as `stale_plan`. Apply verifies in a
    still-reversible state inside a TransactionGroup and rolls the WHOLE batch
    back on any failed check, `expected_value` included. Closed outcome set:
    `committed_verified`, `rolled_back`, `refused`, `stale_plan`, `uncertain`.
  - `horizun_query_dimensions` / `horizun_edit_dimensions` (new): complete
    reads, and atomic edits — type, line move, overrides per dimension or per
    segment, EQ/lock — each read back requested/read/match, stale-refusing,
    with total rollback.
  - `horizun_create_family`: family dimensions gained view selection, explicit
    linear type, lock and EQ; the saved RFA is CLOSED, REOPENED and every
    dimension re-read from the file on disk before the RFA may be called
    verified or loaded into the project.
  What the API does not offer is refused by name rather than imitated:
  radial/diameter/arc-length on 2023/2024 (the classes arrive in 2025), spot
  slope everywhere, reference replacement everywhere, linked references, and a
  leader option on linear dimensions — none of these grants a Python fallback,
  because Python calls the same absent API. Measured against the RevitAPI.dll
  metadata of all five installed generations before a line was written.
- **`horizun_health` reports `revit_language`** — a live-matrix row that cannot
  say which localization it measured cannot be compared with anybody else's.
- **`JsonRpcErrorCodeTests` runs the server of its own build configuration**,
  never the newest binary found lying around: a Debug test run answered by last
  week's Release apphost was a claim about bytes the run never built. Missing
  builds fail naming the exact `dotnet build` command.

## v0.9.6 — 2026-08-20

- **Persistent, owner-controlled Python permission.** Revit's **Python ON/OFF**
  button now grants access until the machine owner explicitly turns it off,
  instead of expiring after 60 minutes. The permission remains specific to
  `horizun_execute_python`; read-only profiles, allowlists and denylists still
  take precedence.
- **Consent can be requested without sacrificing automation.** MCP clients can
  call `horizun_request_python_access` to bring the consent prompt to Revit, but
  they cannot approve it. Once the owner grants access, it remains available
  across documents and compatible clients refresh their tool list automatically.
- **Localized consent UI.** The ribbon dialog follows Revit's language: English
  on English installations, Spanish on Spanish installations, with English as
  the safe fallback.

## v0.9.5 — 2026-08-20

- **Deep multi-agent hardening.** Typed plan execution now fails closed on
  partial/rolled-back children; document identity, workset configuration,
  relinquish, schedule postconditions and Python stream/observer isolation are
  verified instead of inferred. Host calls, task persistence, images, job
  records and retention have bounded, durable failure semantics.
- **Supply-chain and installation hardening.** Pull requests never reach
  persistent Revit/signing hosts; release hand-offs use immutable artifact IDs;
  dependencies, the embedded .NET 8.0.30 runtime and all 614 IronPython stdlib
  files are inventoried/audited. Installation verifies exact hidden/extra files,
  AddInId identity, reparse confinement and rollback generations.
- **Official pre-1.0 unsigned release.** SignPath acceptance is still pending.
  The 0.9.5 installer is therefore disclosed as unsigned, carries full hashes,
  SBOM and Revit 2023–2027 live evidence, and requires the explicit bootstrap
  switch `-AllowUnsigned`. This is byte verification, not public publisher
  authentication; 1.0+ remains fail-closed without public Authenticode trust.

## v0.9.0 — 2026-08-15

- **Public release trust state.** By owner decision, 0.9.0 ships without a
  publicly trusted Authenticode identity while the SignPath Foundation
  application is reviewed. The installer, manifest, SBOM, SHA-256 checksums,
  provenance and Revit 2023–2027 live matrix remain mandatory. Windows/Revit may
  show an unknown-publisher warning. Starting with 1.0.0, the clean-runner public
  signature and timestamp gate is mandatory and has no unsigned exception.

- **Release hardening: protocol, durability, installation and public supply chain.**
  JSON-RPC now enforces the MCP initialization lifecycle and request-id rules; the
  unsupported MCP Tasks advertisement is gone; stdout failure stops the server
  instead of reporting a write that never happened. Excel writes claim durable
  idempotency keys under the workbook lock, replay preserves the complete result,
  async jobs refuse IDs without a durable record, and opt-in retention removes only
  terminal records. Packaging is transactional and self-contained, validates the
  installed result rather than Setup's exit code alone, registers Claude at user
  scope, preserves unrelated client configuration on removal, and produces an exact
  CycloneDX 1.6 inventory. Stable publication now requires clean artifacts and live
  reports for Revit 2023–2027. `horizun_execute_python` is now disabled by default,
  with an expiring Revit ribbon grant; its evidence remains self-reported and
  `host_verified` remains false.

- **One effective version, not merely one version file.** Server, add-in, installer,
  registry metadata and SBOM now inherit `0.9.0` from the repository root. The
  source-level `Directory.Build.props` explicitly imports that root because MSBuild
  otherwise stops at the nearest file and silently stamped every source binary as
  `1.0.0`. The release tests resolve the effective MSBuild property for both projects
  and inspect the packaged binaries, so filename and embedded version cannot drift.

- **The 0.9 live gate exercises valid Revit structures.** The parametric-family
  rehearsal now preserves the outer profile-loop array when PowerShell serializes
  it. Compound system types apply and verify layer wrapping only on exterior or
  interior shell layers: Revit reports core layers as participating but rejects the
  wrapping setter on those same layers, so a core layer is treated as effectively
  non-wrapping and `wraps=true` there is refused before a transaction starts.

- **`execute_python` devuelve los diálogos que Revit levantó, y el script puede leerlos
  MIENTRAS corre (5.25).** El bridge cancela los diálogos modales —correcto: no hay nadie
  al teclado— y lo único que le llegaba al script era el `Opening was canceled.` de Revit.
  Medido: 3 modelos sin auditar y sin causa el 2026-08-13, después de 4 el 2026-08-07. La
  captura existía desde siempre; el registro no viajaba hasta quien lo necesitaba. Ahora la
  respuesta trae `dialogs` y `failures` junto a `__output__` (y como `detail` estructurado
  cuando el script falla), más `revit_raised_observed`: una corrida que nadie vigiló no puede
  leerse como una corrida tranquila. Pero la respuesta llega tarde para un lote —el script
  escribe su veredicto por modelo DURANTE la corrida—, así que `revit_raised(since)` lee el
  mismo registro desde DENTRO: `len(revit_raised())` antes de abrir, ese mismo número después,
  y lo que vuelve es exactamente lo que levantó ESA apertura. Cada registro trae `while`, que
  es el último `checkpoint()` del propio script: la forma honesta del campo que pedía el
  reporte, porque el bridge no puede ver qué llamada estaba en vuelo y lo dice. El extra
  opcional llegó ACOTADO, no como interruptor de script entero: `with dialog_answer('dismiss'):`
  alrededor de UNA llamada — un dismiss global responde OK a todo, y Revit lee OK en el diálogo
  de cerrar-con-cambios como GUARDAR, así que una auditoría de solo lectura podría haber
  escrito en 250 modelos. (La forma del registro vive en `RaisedRecord`, libre de Revit y con
  tests: la ventana `since` y la distinción observado/tranquilo. El prelude es Python y ahora
  vive en `ScriptPrelude.cs`, donde un motor IronPython real lo parsea en los tests — encontró
  un bug de verdad: Python MANGLEA un nombre con doble guion bajo referenciado dentro de una
  clase, y `dialog_answer` habría fallado en cada uso.)

- **`horizun_file_info` devuelve la FIRMA de los 8 primeros bytes cuando el header no se
  deja leer (5.26).** El mensaje de Revit para ese caso ofrece dos causas y las dos hablan de
  archivos de Revit, así que un ZIP renombrado `.rvt` se lee como "un formato más nuevo".
  Costó dos diagnósticos falsos seguidos sobre los mismos dos archivos: "es un Revit más
  nuevo" (escrito en la documentación de un cliente el 2026-08-07 y creído seis días) y luego
  "la descarga se corrompió" (se volvió a bajar y llegó byte por byte idéntica, 337.648.956
  bytes, coincidiendo con el `storageSize` de la API de la nube). La respuesta apareció al
  leer ocho bytes a mano: `50 4b 03 04`. Ahora vienen en `signature`, con `signature_means` en
  lenguaje llano y `is_revit_container`; el resumen cuenta `not_revit_files`. `d0cf11e0a1b11ae1`
  es el contenedor OLE de un `.rvt` de verdad —el único caso donde la historia de la versión
  vale— y `504b0304` es un ZIP, que no es un modelo. Cualquier otra firma vuelve en hexadecimal
  y NO se interpreta. No es un caso aislado: un barrido de 3.252 archivos encontró 1.193 así en
  40 proyectos. (Regla en `FileSignature`, libre de Revit y con tests, incluyendo lo que debe
  negarse a decir.)

- **`execute_python` acepta `code_path`: un `.py` en la máquina, no solo una cadena (5.27).**
  Un driver de 535 líneas / 26 KB tenía que llegar como un stub que leyera y compilara su
  propio archivo: tres intentos, dos perdidos en errores de IronPython que no le enseñan nada a
  nadie (`'charmap' codec can't decode byte 0x8f in position 2634`, porque `open()` usa cp1252
  y el driver lleva tildes; después `TypeError: expected IList[Byte], got str`). Exactamente uno
  de `code` / `code_path`, y el handler es la puerta que dice cuál de los dos errores se cometió.
  El archivo se lee del lado del HOST, así que no evade nada: el límite de 200.000 caracteres,
  `submitted_source_sha256` y la huella de idempotencia miden los bytes leídos, y `run_async` lo
  lee UNA vez, al encolar (una corrida diferida debe ejecutar el script del que se tomó su
  huella, no lo que haya en esa ruta veinte minutos después). El decode respeta BOM, luego una
  línea PEP-263 `# coding:`, luego UTF-8 ESTRICTO — un archivo que no decodifica se RECHAZA
  nombrando el byte y el offset, porque un decode indulgente compila y después corre un script
  que nadie escribió. Y el motor recibe la fuente CON NOMBRE, así que los tracebacks dicen
  `hz_driver.py:214` y no `<string>`. (Regla en `PythonSourceText`, libre de Revit y con tests.)

- **`__revit__` existe en el ámbito del script (5.28).** El reporte pedía exponer `app`; `app`
  (el `Application`) y `uiapp` (el `UIApplication`) ya estaban inyectados, siempre lo estuvieron.
  El hueco real es el que nombraba el propio error y nadie leyó como hueco: `__revit__` —como lo
  llaman pyRevit y RevitPythonShell— no existía, así que lo primero que escribe quien viene de
  cualquiera de los dos responde `NameError`, que se lee como que el bridge no tiene objeto de
  aplicación. Ahora es alias de `uiapp`, y la descripción nombra cada variable inyectada y QUÉ es
  cada una.

- **Nuevo comando tipado `horizun_acc_upload_status` — ¿subido a ACC o pendiente? (5.15).**
  Copiar a la carpeta del Desktop Connector y hashear la copia prueba la CACHÉ LOCAL,
  no la nube: la subida es un paso asíncrono posterior que falla bajo throttling
  ("Too many people or processes…", circuit breaker de ~11 minutos) — medido en campo:
  3 de 8 familias sin subir en silencio, detectadas solo por una captura humana. El
  único registro local que responde la pregunta es el WAL del propio conector
  (`*.properties-log.db`): al completarse una subida, el `Name` aparece junto a un
  `ParentFolderUrn`; mientras está pendiente o fallida, no. Un script externo probó la
  lectura; ahora es un comando del bridge. Acepta `names` y/o `paths` (se usan los
  basenames), `project_id` opcional convierte cada hallazgo en URL de ACC Docs, y
  `wal_root` cubre instalaciones no estándar. Dos líneas de honestidad en la respuesta:
  un hallazgo es el TESTIMONIO del conector leído en esta máquina, no una consulta a la
  API de la nube; y una ausencia es ausencia de EVIDENCIA, nunca prueba de ausencia —
  pendiente, fallida o subida con otro nombre se ven idénticas desde aquí. Un log que
  no se puede leer se reporta por archivo y declara los contadores como cotas
  inferiores. Org-neutral donde el script no lo era: los nombres son argumento, ningún
  prefijo de cliente va compilado. Read-only, no toca ningún modelo, no necesita
  documento abierto. (Parseo y matching en `AccUploadWal`, libre de Revit y con tests;
  el comando lee los logs con el share mode que el conector exige — la misma lección
  de 5.12.)

- **Nuevo comando tipado `horizun_file_info` — triage de carpeta sin abrir nada (5.20).**
  Leer formato/versión guardada, `is_workshared`, `is_central`, `is_local` y ruta de
  central de una lista de archivos o de una carpeta entera, DESDE DISCO con
  `BasicFileInfo`, sin abrir ninguno y sin documento activo. Es lo primero que hace
  cualquier lote, y hasta ahora se escribía a mano en `execute_python` cada vez —
  peor, había que crear un proyecto en blanco solo para satisfacer el chequeo de
  documento activo. Acepta `paths` (lista) o `folder` (barrido por `pattern`, default
  `*.rvt`, `recursive` opcional); los explícitos primero, luego la carpeta, dedup sin
  distinguir mayúsculas, tope de 2000 con aviso. Cada archivo nombra su propio
  `read_error` cuando no se puede leer; el resumen cuenta legibles/ilegibles/ausentes.
  Nada se abre, nada se actualiza. Read-only. (La regla de qué archivos leer vive en
  `FileInfoPaths`, libre de Revit y con tests; el lector `BasicFileProbe` necesita la
  API de Revit.) Esto levanta el freeze de herramientas para este comando por decisión
  del dueño.

- **`on_open_dialog: cancel | dismiss` en las aperturas (5.22).** Cancelar por
  defecto es correcto y sigue siendo el default: un modelo que no abre desatendido
  es un hallazgo. Pero 6 de 123 modelos de un lote no se podían auditar porque su
  apertura levanta un diálogo cuya única respuesta desatendida sensata es "reconocer
  y continuar", y no había forma de decirlo por llamada. Con `dismiss`, `open_document`
  y el `open` de `document_session` responden OK/continuar al diálogo de apertura y lo
  registran en `revit_said`; es best-effort (un diálogo cuyo "continuar" no es el botón
  por defecto se anota como respondido en vez de proceder a ciegas) y está acotado a la
  llamada de apertura — cualquier otro diálogo sigue cancelándose. La regla de parseo
  vive en `OpenDialogPolicy`, libre de Revit y con tests; un valor mal escrito es un
  error, no un cancel silencioso.

- **El servidor barre discovery huérfano al arrancar (5.24).** Un archivo de
  discovery (`revit-<año>-<pid>.json`) lo escribe un Revit vivo y lo borra al salir;
  uno que crashea —o lo matan pasando un modal— nunca llega a borrarlo. El add-in ya
  barría, pero SOLO al publicar el suyo, es decir cuando arranca un Revit: si el
  servidor levanta tras un crash y no hay Revit nuevo, nada limpiaba el huérfano y el
  siguiente comando tropezaba con él. Ahora el servidor barre al arrancar, decidiendo
  QUÉ archivos con la MISMA regla que el add-in (`DiscoverySweep`, libre de Revit,
  con sus dos negativas: los nombres legacy de dos segmentos jamás se tocan, y un pid
  vivo conserva su archivo). Una sola fuente de verdad para las dos mitades.

- **`self_reported_verified` deja de ser un status que el puente rechaza (5.23).**
  El clasificador aceptaba `verified|completed_unverified|partial|failed`; un script
  que declaraba `self_reported_verified` —la palabra que TODA la documentación y cada
  disclaimer del puente popularizan para esto mismo— caía en la rama de status
  desconocido y se DEGRADABA con una advertencia que nombraba una lista que lo omitía:
  la jerga contradiciéndose, rechazando una palabra que ella misma enseña. Ahora es un
  status declarado, con la MISMA vara que `verified` (checked=true + evidence, si no
  downgrade), y se registra tal cual. Nunca sube una afirmación: sigue sin existir
  `verified` para Python.

- **`revit_said` ahora viaja también por la ruta async (5.21).** La ruta síncrona
  adjunta `revit_said` —advertencias, errores y diálogos modales cancelados— junto
  al payload en cada respuesta (`PipeEnvelope`). La async escribía solo
  `result.Data` en el record del job, así que esa telemetría —la que diagnosticó el
  caso más difícil del 2026-08-07, un `Dialog_Revit_DocWarnDialog` cancelado más los
  errores que Revit levantó antes— existía para una llamada síncrona y desaparecía
  para el mismo trabajo enviado por `run_async`, que es justo como corren los lotes:
  4 modelos caídos quedaron sin diagnóstico por esto. `Job.Result` ahora guarda
  `revit_said` en el evento `result` (construido EXACTO como `PipeEnvelope`, misma
  forma en ambas rutas), `RunOneAsync` lo serializa —también en fallo, porque suele
  ser la razón— y `horizun_job_status` lo expone como hermano de `result`. Ausente
  significa "Revit no levantó nada", igual que en síncrono, nunca "se perdió".

- **Un modal se devuelve como RESULTADO, no como timeout (5.19).** Medido
  2026-08-07: un diálogo "New Project" abierto costó 600 s a cada una de tres
  llamadas a `horizun_health` — 30 minutos para enterarse de lo que el log del
  puente dijo en el primer segundo ("it never started"). Revit solo atiende el
  ExternalEvent cuando está ocioso y un modal significa que nunca lo estará;
  el hilo del transporte NO está bloqueado, así que ahora la espera va en
  rebanadas de 1 s y entre rebanadas pregunta a `ModalProbe` (Win32: la ventana
  principal deshabilitada + la ventana visible y habilitada del hilo de UI).
  Un diálogo que persiste 3 sondeos consecutivos — la regla es `ModalSighting`,
  libre de Revit y con 7 tests, porque un solo avistamiento puede ser un
  diálogo que `Interference` ya estaba cancelando — se responde YA, con el
  diálogo nombrado y la petición sacada de la cola: "NEVER STARTED, nada se
  escribió". Una petición que ya arrancó nunca se abandona por sondeo; el
  timeout final ahora nombra el modal visible si lo hay. Y en el servidor,
  `horizun_health` tiene techo propio de 30 s: es el comando de diagnóstico, y
  no contestar rápido ES la respuesta — el respaldo para cuando la sonda no
  puede mirar. Una sonda que no capturó sus datos degrada exactamente al
  comportamiento anterior.

- **`document_session` close puede activar el señuelo por dentro (5.13).** La
  API de Revit no cierra el documento ACTIVO, y el baile del señuelo — abrir un
  documento que no quieres para desplazar al que sí — lo hacía el humano: tres
  veces en una sesión el 2026-08-05, dos más el 2026-08-07 a escala de lote,
  donde el último modelo de 54 quedó abierto y relanzar el lote se lo saltó.
  `activate_other: true` hace el baile dentro del comando: activa otro
  documento abierto (el primero cuya ruta exista en disco — uno detached no
  puede reabrirse por su ruta sintética) o, cuando no hay otro, abre el ANCLA
  propia del puente (`.horizun\anchor\HZ_ANCHOR_<año>.rvt`, un proyecto vacío
  creado una vez y reutilizado), y REPORTA cuál activó y cómo. La decisión es
  `ActivationChoice` (libre de Revit, 8 tests, incluidos los estados que un
  Revit vivo no produce a demanda: todos los documentos detached, lista nula).
  La activación se afirma midiendo — el activo posterior ya no es el objetivo —
  nunca por "no lanzó excepción"; ocurre después de todos los rechazos (una
  petición rechazada no te cambia el documento activo de camino) y antes del
  cierre. `activate_other` entra en `PlanFields`: un rehearsal aprobado sin él
  no autoriza una ejecución que además cambia de documento. Apagado por
  defecto: la activación cambia lo que el usuario está mirando y se pide, no
  se hereda.

## v0.8.0 — 2026-08-05

La versión que v0.7.0 dijo ser. Sus notas presumían el rollback arreglado y la
verificación viva pendiente; esta lleva ambas cosas hechas de verdad, más lo que
una jornada de uso real y las probes vivas encontraron. MINOR porque
`geometry_check` gana campos nuevos (`types_renamed`, `types_renamed_note`).

- **La mentira del rollback vivía en once sitios, no en dos.** v0.7.0 corrigió
  `execute_plan` y `annotate`; un barrido encontró el patrón idéntico en nueve
  comandos más — `RollBack()` llamado, su `TransactionStatus` descartado, y el
  caso limpio afirmado en prosa — y, peor, **tres campos `transaction_status`
  que eran literales**, no mediciones (`write_params` y `bind_shared_param` los
  reportaban desde dentro del handler de `SilentRollbackException` teniendo el
  estado real en la excepción). Los once pasan ahora por `Guard.RollBack` y una
  sola frase compartida (`PlanFailure.SingleTransactionOutcome`); un estado
  distinto de `RolledBack` se lee como INCIERTO y nombra lo que no debe
  asumirse. Dos guards nuevos impiden la duodécima: ningún comando puede llamar
  `RollBack()` a pelo ni asignar el nombre del estado como literal — ambos
  dispararon en su primera corrida y destaparon seis archivos más.

- **`family_apply` ya no tropieza con su propio renombrado (5.11).** Pedir
  `family_name` renombra el tipo superviviente, y la comparación de forma
  emparejaba por nombre: el comando devolvía `changed` con
  `dimensions_compared: 0` sobre el trabajo que se le acababa de encargar —
  medido en 9 familias reales, en 0.5.0 y otra vez en 0.6.1. Ahora la
  comparación recibe el mapa de renombrados que el comando MISMO ejecutó (solo
  si se pidió, no fue creación y la llamada no falló) y empareja la forma de
  antes bajo el nombre de después. Nada se adivina: un tipo que desaparece sin
  renombrado declarado sigue siendo `removed`, y un renombrado declarado no es
  por sí solo un cambio de forma. Campos nuevos `types_renamed` y
  `types_renamed_note`.

- **El servidor perdía el diagnóstico de rollback un salto antes del cliente.**
  El forwarder de errores de `Program.cs` no llama a `McpResult.FromPluginReply`
  (la función que los tests ejercitan) y botaba `reply["detail"]`: el add-in
  calculaba la traza estructurada, el pipe la llevaba, y el cliente nunca la
  veía. Segunda ocurrencia del mismo patrón que ya perdió el veredicto del
  fallback en el camino de éxito; guardado igual, sobre el fuente de Program.cs.
  Medido: la probe viva pasó de UNVERIFIED a PASS con esta única línea.

- **`clash` adopta el marcador canónico de cobertura.** Su frase de cobertura
  incompleta usaba redacción propia ("AS COORDINATED"); ahora lleva `DO NOT READ
  AN ABSENCE`, la misma frase que scan, audit y quantities enseñan a buscar.

- **Retroalimentación de una jornada real, convertida en trabajo.** El reporte
  crudo (9 familias reales contra Revit 2025) está en
  `docs/feedback-from-use-2026-08-05.md`; sus hallazgos son las historias
  5.12–5.18 del backlog, con sus medidas.

### Verificación de esta versión — en vivo esta vez

Estáticos: 466 + 235 tests en verde; seis compilaciones (servidor + add-in
2023–2027) con 0 advertencias y 0 errores. **Vivo, contra Revit 2026 y un solo
binario:** el tier de escritura commiteó y releyó del modelo
(`created_verified`), la probe de rollback probó con TRAZA ESTRUCTURAL que el
grupo arrancó, la acción válida commiteó, la inválida fue alcanzada,
`rollback_status=RolledBack` y el modelo quedó sin residuo; y las cinco probes
de workset cerrado pasaron contra un modelo workshared real con un workset
cerrado — 0 FAIL / 0 UNVERIFIED en la unión de ambas corridas.

## v0.7.0 — 2026-08-05

Dos mitades. La primera es el cambio de dirección de producto de la sección de
abajo: `horizun_execute_python` pasa de excepción bloqueada a **ruta de ejecución
de respaldo**, habilitada por defecto. La segunda son las correcciones que salieron
de auditar esa entrega antes de publicarla — cada una nombrada por el defecto que
arregla, no por la función que añade.

- **Un rollback fallido ya no se afirma sin comprobarlo.** `horizun_execute_plan`
  llamaba a `TransactionGroup.RollBack()` y devolvía la frase fija "EVERY action was
  rolled back" **sin leer el `TransactionStatus` que ese RollBack devuelve**. Un
  estado distinto de `RolledBack` (`Pending`, `Error`) significa que el modelo quedó
  en un estado INCIERTO, no limpio, y el mensaje afirmaba lo limpio siempre. Ahora un
  plan fallido devuelve un diagnóstico estructurado en `structuredContent`:
  `transaction_group_started`, `transaction_group_status`, `rollback_attempted`,
  `rollback_status`, `rollback_confirmed` y un `execution_trace` por acción con su
  índice, key, tool, success y error. `rollback_confirmed` se calcula del estado
  FINAL del grupo, y sólo `RolledBack` lo concede; cualquier otro conserva su
  incertidumbre y lo dice en la prosa. Se añadió `Guard.RollBack`, que reporta el
  estado real en vez de suponerlo, y la misma afirmación falsa se corrigió en
  `horizun_annotate`.

- **La probe viva del rollback podía dar un falso PASS.** Aprobaba con (hubo error
  AND el conteo del modelo no cambió) — condiciones que también cumple un rechazo por
  token stale o por confirmación, donde el TransactionGroup **nunca llegó a empezar**.
  Ahora exige, como DATOS y no como texto: que el grupo arrancara, que la primera
  acción se ejecutara con `success=true`, que la segunda fuera alcanzada y fallara,
  que `rollback_status` sea `RolledBack`, y que el conteo del modelo antes y después
  coincida. Un rechazo previo al grupo se reporta explícitamente como "prueba
  validación, no rollback".

- **Firma, manifiesto e instalador dejan de poder mezclarse.** `sign.ps1` firmaba
  `src/…/bin/…/horizun-mcp.exe` —una copia que nunca se empaqueta— y **nunca firmaba
  `horizun-mcp.dll`**, que es donde vive el código del servidor (el `.exe` es un
  apphost). Además el manifiesto se calculaba ANTES de firmar, y firmar cambia los
  bytes, así que describía archivos que ya no existían; `-InstallerOnly` no lo
  recalculaba ni lo revisaba. Ahora se firma el **stage** (exe, dll y cada
  `Horizun.Revit.dll` por año), el manifiesto se **recalcula después de firmar** con
  hashes, tamaños, estado de firma y thumbprint del firmante, y `pack.ps1
  -InstallerOnly` **se niega a construir** si algún byte del stage no coincide con el
  manifiesto. `verify-release.ps1` comprueba además que los binarios propios lleven
  firma de verdad, de un solo certificado, y que el firmante en disco sea el que el
  manifiesto declara.

- **`script_sha256` se renombra a `submitted_source_sha256`** (respuesta de
  `preflight`). El nombre viejo prometía identificar "el script" y no cubría imports,
  `exec`/`eval` ni un archivo leído en tiempo de ejecución; el campo nuevo lo declara
  explícitamente en `submitted_source_sha256_covers`. **`script_sha256` sigue
  presente con el mismo valor**, depreciado según la política de esta versión: se
  mantiene dos releases MINOR y no se retira antes de 0.9.0.

- **El rechazo por tamaño deja de enseñar a evadirlo.** Un script por encima del
  límite recomendaba "ponlo en un archivo y léelo desde un script corto" — lo que
  evade el límite, el hash del código enviado y su vínculo con la clave de
  idempotencia a la vez. Ahora dice por qué el límite existe y propone reducir o
  partir el trabajo.

- **La contradicción de idempotencia async, resuelta.** La respuesta de `run_async`
  afirmaba que "un reinicio olvida cada clave, así que un reintento vuelve a ejecutar
  el script", cuando el dispatcher ya usa un ledger DURABLE que reclama la clave en
  disco antes de ejecutar y **reproduce** la respuesta tras un reinicio. La autoridad
  única es ese ledger; el ledger en memoria queda documentado como capa subordinada
  para la carrera intra-proceso. Una clave cuyo resultado terminal nunca se escribió
  queda `in_doubt` y no se vuelve a ejecutar: el riesgo residual es un resultado que
  hay que inspeccionar, nunca una segunda ejecución.

- **Ningún atajo para conceder el fallback.** Se eliminó `CommandResult.FailUnsupported`,
  fábrica pública que concedía el permiso sin pasar por la decisión central ni ver el
  resto del lote. Las razones de incapacidad pasan a ser un **conjunto cerrado**
  validado en `FallbackSignal.Allowed` y en `UnsupportedCapability`: una razón
  inventada falla en el sitio del throw en vez de viajar como un string que ningún
  cliente puede discriminar.

- **Un fixture declarado y jamás consumido es un defecto.** `VoidFamilyDocument`
  aparecía como parámetro, en `fixtures_present` y en el JSON de ejemplo, pero su
  único consumidor (`family_mirror_void`) estaba retirado: anunciaba una cobertura
  inexistente. Se eliminó, y un test nuevo falla si cualquier fixture declarado deja
  de ser consumido por una probe.

- **`docs/security-model.md` ya no llama "verified" a Python.** Decía que el camino
  de respaldo terminaba "executed and verified in Python"; la ruta Python no es
  host-verificada nunca.

### Verificación de esta versión

Estáticos: 454 tests de Core y 234 de Server en verde, y seis compilaciones
(servidor más el add-in para Revit 2023–2027) con 0 advertencias y 0 errores. La
cadena `pack → sign → manifiesto → instalador → verify-release` se ejecutó completa
y sus comprobaciones de firma e integridad pasan.

**Lo que NO se verificó en vivo, dicho aquí porque callarlo sería el defecto que esta
versión corrige:** el tier de escritura contra un Revit real (`-WriteProbes`), la
probe de rollback con su traza estructurada, y las cinco probes de workset cerrado.
Se corren sobre fixtures desechables y quedan pendientes; hasta que existan, esta
versión no reclama "0 FAIL / 0 UNVERIFIED" en vivo.

---

Cambio de dirección de producto: `horizun_execute_python` pasa de excepción
bloqueada a **ruta de ejecución de respaldo**, habilitada por defecto.

- **Los valores por defecto son la superficie completa.** Sin `settings.json`, o
  con uno que no traiga esas claves, se lee `permission_profile=unsafe_code` y
  `enable_execute_python=true`: una instalación nueva anuncia Python en
  `tools/list`. Una elección **explícita** (`read_only`, `safe_write`,
  `full_write`, `enable_execute_python=false`) se respeta siempre — los defaults
  solo llenan la ausencia. Y se conserva una asimetría deliberada: un archivo que
  existe pero **no parsea** cae CERRADO (`read_only`, Python apagado), porque
  puede ser una restricción explícita corrupta y un byte dañado no debe convertir
  "esto lo apagué" en "todo está encendido".

- **Política de fallback automático**, escrita donde un cliente la lee: en las
  `instructions` del servidor y en la descripción de la herramienta. Tipado
  primero cuando cubra el caso completo; cuando no exista capacidad tipada, o una
  herramienta tipada se niegue **antes de escribir**, el LLM genera Python mínimo
  y lo EJECUTA en lugar de responder "no soportado". Dos límites duros: nunca
  tras una escritura tipada que falló a mitad (pudo escribir parcialmente — un
  reintento es una segunda escritura), y no detenerse a pedir aprobación cuando
  objetivo, documento, alcance y criterio de éxito ya son inequívocos.

- **El veto por solape tipado pasa a ser un aviso.** Un script que llama a una
  API que un comando tipado ya hace y verifica recibe `typed_alternatives`
  nombrando el comando, y **corre igual**. El veto obligaba a partir una
  operación compuesta en dos transacciones o a rendirse; la recomendación
  conserva el valor sin el bloqueo.

- **Contrato de evidencia en `__output__`, autorreportado y no acreditado.** El
  host **no** relee el modelo tras código arbitrario, así que no puede certificar
  nada que un script afirme: el estado más fuerte de esta ruta es
  `self_reported_verified`, junto a `completed_unverified`, `partial` y `failed`.
  **En la ruta Python no existe `verified`**, `host_verified` siempre es false, y
  un `status: verified` sin `verification.evidence` se degrada a
  `completed_unverified` diciendo por qué. `script_reported_status` conserva lo
  que declaró el script. La distinción es la que sostiene el contrato de
  honestidad: el "verified" de una escritura tipada es un hecho que el puente
  releyó; el de un script es testimonio. Una versión anterior devolvía `verified`
  a secas para cualquier script que lo dijera y adjuntara una lista no vacía, lo
  que hacía `{"evidence":["ok"]}` indistinguible de una relectura real.
  `print()` sigue funcionando como compatibilidad. Plantilla y recetas en
  [`docs/python-fallback-recipes.md`](docs/python-fallback-recipes.md).

- **El fallback es decidible por máquina, no por redacción.** Un rechazo tipado
  previo a escribir puede traer un bloque estructurado —
  `fallback: {recommended_tool, allowed, reason, write_started}` — en
  `structuredContent` y repetido en el texto del error. `allowed: true` solo se
  emite cuando ninguna capacidad tipada cubre lo pedido **y** no se escribió nada;
  el invariante `allowed=true ⇒ write_started=false` se valida en el constructor,
  no se confía. Sin bloque, o con `allowed: false`, el cliente no debe caer a
  Python. La incapacidad viaja como tipo (`UnsupportedCapability`) para sobrevivir
  al catch que aplana todo lo demás a un string. Cubre hoy `horizun_create_elements`
  (kind), `horizun_annotate`, `horizun_transform_elements` y `horizun_manage_views`
  (operation).

- **El aviso de solape tipado se enmascara con el lexer de Python.** Un
  `# ElementTransformUtils.MoveElement` en un comentario generaba un aviso falso.
  Ahora se tokeniza con `TokenCategorizer` —servicio público del DLR que **lexa
  sin ejecutar**— y se blanquean comentarios y literales; el escáner a mano queda
  como respaldo si el tokenizer no está disponible o la fuente no lexa (falla
  suave: un aviso nunca puede impedir una corrida). **Límite documentado:** un
  f-string es un solo token de cadena, así que una llamada dentro de `f"{...}"`
  también se enmascara y no genera aviso — dirección conservadora y fijada en los
  tests. Sigue siendo aviso, no bloqueo.

- **Un lote mixto ya no concede el fallback.** Antes bastaba con que UNA acción
  fuera una incapacidad para que toda la solicitud devolviera `allowed: true`,
  aunque otra acción del mismo lote tuviera argumentos inválidos — permiso cierto
  de una entrada, publicado para la llamada entera. Ahora la decisión es una pieza
  pura y central (`FallbackDecision`) que exige las tres condiciones: nada escrito,
  al menos una incapacidad, y que **todos** los fallos sean incapacidades. El lote
  mixto recibe `allowed: false` con `reason: mixed_capability_and_invalid_input`
  más `capability_gaps` por índice: un mapa, no una licencia.

- **Auditoría de incapacidades tipadas con inventario probado.** Cada rechazo
  estructural de `src/Horizun.Revit/Commands` está clasificado como concedido,
  argumento corregible o posterior a escritura, con su razón escrita; un escáner
  falla si aparece uno sin clasificar, si una fila queda obsoleta, o si un comando
  sin fila concedida empieza a emitir fallback.

- **Transporte probado como valores, no como texto fuente.** El ensamblado del
  sobre del pipe (`PipeEnvelope`) y del resultado MCP (`McpResult`) se extrajeron
  para que las pruebas construyan un `CommandResult` y lean el JSON que llegaría
  al cliente. Una señal perdida en serialización es indistinguible de una que
  nunca se concedió, y el cliente deja de caer a Python en silencio.

- **`preflight=true`**: valida permiso, documento objetivo, tamaño, SHA-256 del
  script y sintaxis **sin ejecutar**, y devuelve advertencias detectables. No
  gasta clave de idempotencia (no muta) y no se combina con `run_async`. Declara
  explícitamente lo que no puede: demostrar la seguridad o el efecto de código
  arbitrario.

- **`enable-execute-python.ps1` sigue existiendo como herramienta de
  administración** — re-habilita o restaura una instalación apagada
  explícitamente, y apaga con `-Disable`. Ya no es el único modo de activarlo.

- **Riesgo aceptado, dicho en `docs/security-model.md`**: el default amplía la
  superficie expuesta. Una máquina que procese contenido no confiable o corra
  desatendida debería apagarlo explícitamente.

## v0.6.1 — 2026-08-04

Cortada el mismo día que v0.6.0, sobre ella:

- **El set de requisitos como datos** (`docs/requirement-set.md`): el esquema
  que hace verificable ISO 19650 / IFC / COBie sin una línea de código por
  estándar, con su test de aceptación escrito.
- **`execution.taskSupport`** emitido por herramienta, derivado del contrato —
  `optional` exactamente para lo que `horizun_submit_job` acepta.
- **Política de releases** (`docs/RELEASE-POLICY.md`): ventana de deprecación de
  dos MINOR, ningún campo se reutiliza jamás.
- **Fe de erratas de este archivo**: los `.csproj` de esta versión salieron
  diciendo `0.6.0` — la etiqueta y el binario no coincidían. Corregido, y ahora
  un check de CI compara la etiqueta contra `<Version>` para que no vuelva a
  pasar en silencio.

## v0.6.0 — 2026-08-04

Endurecimiento, no superficie: cero herramientas nuevas, y en cambio las
garantías que una release debe poder demostrar sobre sí misma.

- **El servidor MCP se registra como `horizun-revit`**, no `horizun` a secas, para
  leerse como lo que es en la lista de servidores de un cliente junto a
  civil3d-mcp, navisworks y el resto de puentes. Afecta a `install.ps1`, a la
  página final del instalador, al `-Name` por defecto de
  `register-client.ps1`/`verify-clients.ps1` y a toda la documentación de
  instalación. Un registro existente con el nombre viejo no se renombra solo:
  quítalo y vuelve a añadirlo, o pasa `-Name horizun` para conservarlo.

- **Guard de solape tipado en `execute_python`.** Un script que hace exactamente
  lo que un comando tipado y verificado ya hace es redirigido a ese comando, con
  la razón: qué comprueba el comando que un script no puede comprobar sobre sí
  mismo. El escape hatch sigue siendo el escape hatch; deja de ser un duplicado
  silencioso de lo verificado.

- **Activación de `execute_python` en un paso, hecha para una persona.**
  `scripts/enable-execute-python.ps1` muestra qué estás aceptando, escribe
  exactamente las dos claves (`permission_profile` + `enable_execute_python`),
  conserva el resto de la configuración y se revierte con `-Disable`. Sigue
  apagado de fábrica y ningún agente debe encenderlo por ti.

- **`verify-live.ps1` gana un nivel de ESCRITURA (`-WriteProbes`).** El nivel por
  defecto sigue sin tocar nada; el nuevo COMMITEA contra un modelo que el
  archivo de fixtures declara desechable, relee el resultado del modelo y nunca
  guarda. Existe porque tres comandos llegaron a review con toda su batería
  unitaria en verde y ninguno podía hacer su trabajo: las refusals prueban los
  guards, los dry runs prueban la aritmética, y solo un commit prueba el
  comando. Con dos llaves independientes (el switch y el fixture), y sin ambas
  cada probe sale NOT COVERED con su motivo.

- **Regla nueva del contrato: una escritura tipada cuya verificación falla debe
  REVERTIR.** Reportar el fallo con honestidad no basta: un comando que commitea
  y luego dice que no pudo confirmar deja al llamador un modelo que desenredar a
  mano. Está en CONTRIBUTING como regla dura y en el nivel de escritura como
  probe que recorre cada respuesta y nombra a los infractores.

- **La maquinaria de publicación vuelve al árbol privado.** El export por
  allowlist y la política que implementa se perdieron al reemplazar main el
  2026-08-02 y sobrevivían solo en una rama; restaurados sin cambios. El escaneo
  de nombres vuelve a estar limpio sobre todos los archivos trackeados.

- Backlog: Épica 4 — estándares como datos, no compilados. El estándar llega
  como requirement set versionado; el puente mide y nunca juzga; cada hallazgo
  nombra el comando tipado que lo arreglaría. ISO 19650, IFC/buildingSMART y
  COBie entran como tres documentos contra un solo comando, desde el día uno.

## v0.5.0 — 2026-08-01

Esta versión apunta directamente al benchmark de operaciones tipadas,
interoperabilidad, familias y distribución; no suma herramientas por contar.

### Creación BIM y documentación

- `horizun_create_elements` añade cielos rasos, cubiertas por huella con
  pendiente, bandejas portacables, vigas/arriostramientos y columnas
  estructurales. Conserva lote heterogéneo atómico, ensayo, confirmación y
  relectura de clase/tipo estructural.
- `horizun_manage_views` añade plantas de cielo y estructura, vistas de detalle,
  secciones y elevaciones; siguen componiéndose con alias dentro de una sola
  transacción.

### Familias

- Nuevo `horizun_create_family`: compila una familia cargable desde una plantilla
  RFT con parámetros, fórmulas, tipos y valores por tipo; formas sólidas o vacías
  de extrusión/blend/revolución/barrido/swept blend; planos de referencia,
  cotas etiquetadas, líneas simbólicas/de modelo y RFA anidados por punto con
  propagación de parámetros; asociación paramétrica de
  offsets, ángulos, material y visibilidad; y conectores de tubería, ducto, electricidad, conduit
  y bandeja alojados en una cara seleccionada por normal. Guarda y verifica el
  RFA, puede cargarlo al proyecto y relee la `Family` cargada.
- Nuevo `horizun_manage_system_types`: duplica y parametriza tipos de sistema
  residentes en proyecto dentro de una transacción. En tipos host de muro, piso,
  cubierta y cielo puede reconstruir la composición homogénea completa: capas
  ordenadas exterior-interior, función, material, espesor, envolvente, límites
  shell/núcleo, capa estructural/variable y deck. Relee clase, nombre, valores y
  toda la composición después del commit.
- Se documenta el límite real: la API pública de Revit no ofrece creación
  general de familias in-place. No se simula con automatización frágil de UI.

### IFC, Navisworks, FBX y Power BI

- `horizun_export` añade NWC nativo por modelo o vista y FBX de una o varias
  vistas 3D. IFC expone versión, cantidades base, partición muro/columna,
  límites espaciales y filtro de vista. Todos atribuyen éxito solo a archivos
  no vacíos realmente nuevos o modificados.
- Nuevo `horizun_power_bi_push`: inserción directa en tablas de semantic models
  push, en My workspace o un workspace. Credenciales solo desde variables de
  entorno; endpoint fijo de Microsoft; límites 10.000 filas/75 columnas/4.000
  caracteres; token temporal o service principal de Entra.
- Power BI comparte la garantía durable de como máximo una vez: un reintento
  idéntico reproduce la respuesta; una conexión perdida después del envío queda
  `in_doubt` y no duplica filas automáticamente.

### Instalación y benchmark

- `install-release.ps1` selecciona una release, descarga setup y
  `SHA256SUMS.txt`, verifica el SHA-256 completo y solo entonces ejecuta el
  instalador. No necesita Git ni SDK.
- El setup incluye registrador y verificador para Codex/Claude. La opción es
  explícita: respalda las configuraciones, conserva los demás MCP y se niega a
  editar un cliente abierto que pueda sobrescribir el cambio.
- `docs/BENCHMARK.md` publica casos, puntuación y grados de evidencia. El
  instalador sigue sin firma de una CA pública; por eso distribución no recibe
  puntuación perfecta.

El contrato compartido cambió: servidor y add-in 0.5.0 deben desplegarse juntos.

## v0.4.0 — 2026-08-01

Esta versión cambia el foco de “más comandos” a **operaciones generales,
componibles y verificables**.

### Superficie BIM general

- `horizun_query_model` consulta host y vínculos cargados con filtros de
  categoría, familia/tipo/nombre/nivel, parámetros y caja 3D; proyecta campos,
  agrupa resultados y usa cursores que detectan cambios del modelo.
- `horizun_create_elements` crea en un solo lote niveles, ejes, muros, pisos,
  habitaciones, instancias de familia, ductos, tuberías y conduit.
- `horizun_transform_elements` mueve, copia, rota, fija y cambia tipos;
  `horizun_manage_views` crea vistas, planos y colocaciones; `horizun_annotate`
  crea texto, tags y cotas con referencias estables; `horizun_export` produce
  PDF/DWG/IFC/imagen/CSV y verifica los archivos realmente escritos.
- `horizun_navigate` devuelve selección, encuadre y apertura de vista a la UI
  de Revit sin fingir una confirmación visual que la API no expone.

### Composición y trabajos largos

- `horizun_execute_plan` encadena hasta 100 comandos tipados en un
  `TransactionGroup`. Un paso puede usar un valor exacto anterior mediante
  `${clave.ruta}`; si cualquier paso falla, se revierte el grafo completo.
- `horizun_submit_job` abre la cola async a cualquier comando instalado del
  lado Revit. Devuelve `job_id` de inmediato; `horizun_job_status` distingue
  queued/running/ok/failed/not_started o muerte del proceso.

### Seguridad e idempotencia

- Toda mutación y cambio de sesión exige una clave de idempotencia **durable**.
  El claim se escribe antes de ejecutar y el resultado terminal después. Un
  reintento idéntico tras reiniciar reproduce la respuesta sin ejecutar; una
  clave reutilizada con otros argumentos se rechaza; un claim cortado por un
  crash queda `in_doubt` y nunca se repite automáticamente.
- Perfiles `read_only`, `safe_write` (predeterminado), `full_write` y
  `unsafe_code`, más `allowed_tools`/`denied_tools`. Python exige a la vez
  perfil `unsafe_code` y `enable_execute_python=true`.
- Se corrigió una incompatibilidad introducida durante el endurecimiento:
  Python síncrono ya acepta —y exige— la clave durable universal.

### MCP y honestidad de resultados

- Negociación MCP hasta `2025-11-25`, conservando compatibilidad con
  2024-11-05/2025-03-26/2025-06-18. Todas las herramientas anuncian
  `outputSchema`, título y anotaciones de lectura/destrucción/idempotencia/
  mundo abierto; las respuestas exitosas llevan `structuredContent` y el JSON
  serializado en texto para clientes antiguos.
- Export ya no atribuye archivos ajenos que cambiaron simultáneamente en la
  carpeta; la plantilla de vista se verifica contra el ID exacto solicitado; la
  verificación de curvas transformadas tolera la inversión de extremos con la
  que Revit normaliza geometría equivalente.

- Los resúmenes de `horizun_query_model` normalizan nombres vacíos de categoría
  o nivel como `(blank)` y consolidan nombres que solo difieren por mayúsculas.
  Aunque ambas formas son JSON válido, clientes reales como Windows PowerShell
  5.1 no pueden materializarlas y descartaban la respuesta completa. La prueba
  viva consulta ahora todas las categorías para cubrir estos casos con datos reales.

El contrato compartido cambió: servidor y add-in 0.4.0 deben desplegarse juntos.

## v0.3.5 — 2026-08-01

### Cola FIFO para todas las llamadas de Revit

- **Las llamadas concurrentes ya no se rechazan por estar ocupado Revit.** Una
  operación sigue ejecutándose a la vez —la API de Revit continúa siendo de un
  solo hilo—, pero hasta 16 solicitudes adicionales esperan en orden FIFO. El
  límite es backpressure deliberado: una cola sin límite convertiría un bucle o
  una tormenta de reintentos en horas de mutaciones futuras aceptadas en silencio.

- **Cada respuesta JSON mide su espera.** `bridge_queue.queued` informa si había
  otra llamada del puente delante al admitirla; `waited_ms` mide además la espera
  hasta que el hilo UI de Revit quedó disponible. También informa capacidad y
  tiempo total de espera más ejecución.

- **Cancelar antes de empezar significa que nunca corrió.** El servidor envía
  una orden de control autenticada por una conexión separada; el add-in elimina
  la solicitud bajo el mismo lock de la cola y despierta a su dueño con
  `cancelled_before_start`. Si ya entró al hilo de UI, no afirma cancelarla: la
  API de Revit no puede interrumpir ese trabajo.

- **Sin starvation entre colas.** Las llamadas normales y los trabajos
  `run_async` alternan cuando ambas colas tienen trabajo. Un flujo continuo de
  lecturas no puede dejar eternamente esperando una mutación async, ni al revés.

- **Cierre y errores terminales drenan con verdad.** Si Revit se apaga o
  `ExternalEvent.Raise()` responde que no llegará ningún callback, todas las
  solicitudes todavía en espera se despiertan como `NEVER STARTED`; no quedan
  conexiones bloqueadas ni operaciones que puedan arrancar después.

- **El heartbeat dejó de llamar “running” a lo que quizá está en cola.** Ahora
  dice que espera una respuesta de Revit y que desde el proceso MCP no puede
  distinguir todavía entre espera FIFO y ejecución. No inventa estado.

- **La concurrencia tiene una prueba viva reproducible.**
  `scripts/verify-queue-live.ps1` comprueba posiciones de admisión y orden FIFO,
  cancela una escritura de marcador todavía en espera y verifica fuera de Revit
  que el archivo nunca apareció. Después ocupa los 16 slots y demuestra que la
  llamada 17 recibe backpressure mientras las 16 aceptadas terminan normalmente.

## v0.3.4 — 2026-08-01

Los botones de pyRevit se vuelven herramientas. **Nueve** de los doce de la
extensión "Horizun AEC" pasan a ser comandos `horizun_*` de primera clase; dos son
**imposibles** de portar con lo que hay, y uno se deja fuera a propósito.
Además, cuatro comandos cierran el primer flujo nativo de tablas de planificación
y consulta federada de elementos vinculados.

**EL CONTRATO SE MOVIÓ** — trece comandos nuevos —, así que las dos mitades hay
que actualizarlas juntas.

### Añadido

- **Tablas de planificación nativas y vínculos.** `horizun_create_schedule`
  crea una `ViewSchedule` real con campos, orden, itemización e
  `IncludeLinkedFiles`; empieza en `dry_run`, exige documento objetivo y token,
  y después del commit relee la tabla y sus campos. `horizun_list_schedules` y
  `horizun_get_schedule_data` inspeccionan la definición y las celdas que Revit
  muestra, con límites y truncamiento explícitos. `horizun_list_elements`
  pagina por categoría a través del anfitrión y los vínculos cargados, conserva
  el modelo e instancia de origen y reporta los vínculos no cargados en vez de
  convertirlos en un cero falso.

- **Recetas: el álgebra en Python, la honestidad en C#.** Estas geometrías llevan
  meses corriendo contra modelos reales; reescribirlas en C# no las haría más
  correctas, reiniciaría su historial de bugs en cero. Así que el algoritmo se
  queda en Python (`Recipes\*.py`, junto al DLL) y `Core\Recipe.cs` se queda con
  todo lo que decide si la respuesta es verdad: la transacción y su
  `Guard.Commit`, el `dry_run` — que **no abre transacción en absoluto** —, la
  relectura **después del commit** y un bloque `Guard.Verify` por cantidad
  contada. Una receta que dice 40 contra un modelo que reporta 37 **falla la
  llamada**. Una receta no puede abrir su propia transacción: se comprueba en
  ejecución y en CI. Y no es un rodeo a `enable_execute_python` — ese ajuste
  regula código que **llega de quien llama**, y aquí no llega nada: el nombre se
  resuelve contra una carpeta fija, `..` y separadores se rechazan, y el fichero
  lo instaló el mismo deploy que instaló el DLL. El sha256 de la receta que
  **realmente** corrió viaja en cada respuesta.

- **Nueve herramientas nuevas**: `horizun_split_floor_loops`,
  `horizun_split_multilayer_walls`, `horizun_split_multilayer_slabs`,
  `horizun_ungroup_and_mark`, `horizun_regroup_by_param`,
  `horizun_copy_slab_elevations`, `horizun_embed_floors_in_toposolid`,
  `horizun_grade_toposolid_around_floors` y `horizun_rectangularize_walls`.
  Todas con `dry_run` **por defecto TRUE**, `target_document` obligatorio y token
  de confirmación de un solo uso.

- **Los dos gigantes se portaron COPIANDO el fuente, no reescribiéndolo.**
  "Rectangularizar muros" (2.051 líneas) y "Grading TopoSolido" (1.267) se
  copiaron tal cual y se editaron: cabecera, argumentos en vez de diálogos, la
  transacción al host y `plan/apply/verify`. La geometría queda **idéntica byte a
  byte**, que es justo el objetivo — un puerto reescrito reinicia el historial de
  bugs, y transcribir dos mil líneas a mano lo garantiza. Ambos conservan su `doc`
  de módulo, que los puntos de entrada enlazan al documento que resolvió el host:
  es seguro porque el puente corre **un comando a la vez** y rechaza el segundo en
  vez de encolarlo.

### Arreglado — defectos que traían los botones, no el puerto

Un botón puede permitirse esto porque hay alguien mirando. Una herramienta que
llama un agente, no.

- **El partidor de muros convertía un muro CURVO en su CUERDA, y reportaba
  éxito.** `offset_curve()` construía cada capa con `Line.CreateBound` desde los
  **extremos** de la directriz. En un muro recto es exacto; en uno en arco es la
  cuerda — el muro se mueve, y nada lo decía. Ahora los muros curvos se
  **rechazan** en `plan()` con el motivo. Rehacerlos bien es otro algoritmo, y no
  uno que este puerto tenga derecho a inventar.

- **Se borraban originales PINEADOS.** Revit responde "está intentando eliminar
  elementos pineados", y un aviso que nadie contesta es un modal que retiene el
  hilo de UI hasta que quien llama expira. Su propio botón hermano (Separar
  losas) ya despineaba antes. Ahora lo hacen los tres.

- **Reagrupar barría anotación hacia un Model Group.** Un grupo de modelo no
  puede contener un elemento view-specific, y Revit rechaza la llamada **entera**
  con un `ArgumentException` que no nombra a nadie: una sola etiqueta suelta hacía
  fallar el botón por completo. Ahora se excluyen y se **listan**, y el resto sí
  se agrupa.

- **Reagrupar limpiaba el parámetro DESPUÉS de agrupar.** Escribir un parámetro en
  un elemento que ya está dentro de un grupo es justo lo que levanta el modal de
  grupo. Ahora se limpia **antes**: mismo estado final, sin modal, y si el
  agrupado falla el host revierte la limpieza con él.

- **Desagrupar descubría demasiado tarde que el parámetro no existía.** Desagrupaba
  primero y luego intentaba marcar; si el parámetro no estaba, el modelo quedaba
  desagrupado **y sin marcar** — irrecuperable, porque la pertenencia al grupo ya
  no existía. Ahora se muestrea a los miembros **antes** de tocar nada.

- **Partir losa perdía el offset de nivel**, dejando cada losa partida en la cota
  del nivel. Ahora se copia y se reporta.

- **"Adquirir Elevaciones" rechazaba una losa fuente legítimamente alabeada.**
  Aceptaba la fuente solo si exponía más de cuatro vértices ("no parece tener una
  forma editada") — pero una losa rectangular con **una esquina levantada** tiene
  exactamente cuatro vértices y SÍ está alabeada: la losa deformada más común del
  mundo, rechazada con un mensaje que decía que no lo estaba. Ahora se juzga por si
  la forma realmente varía: más vértices que su contorno, O cotas distintas entre
  vértices, O split lines.

- **"Adquirir Elevaciones" reducía cada arista curva a su punto inicial.** Tomaba
  solo `GetEndPoint(0)` de cada curva del contorno, así que un arco aportaba un
  punto y el polígono del destino cortaba recto por la panza: los puntos se
  probaban contra una forma que no es la losa. Ahora las curvas se **teselan**, y
  los destinos afectados salen nombrados en `curved_boundary_note`.

- **"Adquirir Elevaciones" reseteaba la forma del destino en silencio.** Es la
  operación correcta (dos alabeos no se fusionan), pero el botón no decía nada. El
  dry run ahora lista exactamente qué losas van a **perder** su forma existente.

- **Una losa mala ya no tira el lote.** `split_multilayer_slabs` corre cada losa en
  su propia `SubTransaction`: la que falla revierte **sola**, sin dejar geometría a
  medias, y se reporta por id.

- **Los instaladores no copiaban `Recipes\`.** Exactamente la omisión que en 0.3.3
  dejó la cinta sin iconos — pero peor: un icono que falta degrada a botón sin
  imagen, mientras que una receta que falta es una herramienta que `tools/list`
  anuncia, el dispatcher acepta y **falla al usarla**. `pack.ps1` ahora **aborta**
  si el payload no lleva tantas recetas como produjo la compilación, y los deploys
  reportan cuántas aterrizaron releyéndolas del disco.

### No portado, y por qué

- **"Pases de nivel" y "Revertir Pases" son imposibles con lo entregado.** Ambos
  scripts son cáscaras: dicen en su propia cabecera que *"toda la lógica vive en
  la librería compartida `iec_pases` (extension/lib)"*, y ese `lib/` **no viene en
  el zip** ni está en el disco. No hay nada que auditar ni que portar hasta que
  aparezca el módulo.

- **"Nivelar TopoSolido" (v1) se deja fuera A PROPÓSITO, y lo dice su propio
  sucesor.** La cabecera de "Nivelar Topo V2" enumera las "diferencias clave con
  v1" y entre ellas está la razón: v1 fusiona losas con **booleanas de sólidos**, y
  *"el kernel booleano de Revit falla con losas que solo se tocan por el borde"* —
  justo el caso que la herramienta tiene que resolver. V2 lo reemplazó por
  cancelación de bordes en 2D. Montar la versión que su autor ya había sustituido
  sería publicar un modo de fallo conocido con una etiqueta `horizun_`. Su
  capacidad está cubierta por `horizun_embed_floors_in_toposolid`.

## v0.3.3 — 2026-07-31

La versión que se puede regalar. El puente se prepara para ser público —
gratuito, Apache-2.0, parte del ecosistema Horizun Hub — y eso pidió tres cosas
que ninguna auditoría de código habría pedido: que se vea, que diga de dónde
viene, y que se instale desde el fuente sin descargar un solo ejecutable.

**El contrato NO se movió**: ninguna descripción de comando cambió, así que las
mitades de 0.3.2 y 0.3.3 emparejan entre sí. El bump es de producto, no de
protocolo.

### Añadido

- **Una pestaña propia en Revit: "Horizun Hub", panel "Horizun RVT MCP".** El
  add-in corrió headless toda su vida — un pipe, un fichero de descubrimiento y
  un log — y esa forma correcta para un puente tenía un coste que nadie había
  pesado: en una máquina donde funciona, no hay manera de saberlo. *Invisible no
  se distingue de ausente.* El botón **Estado del puente** lee el fichero de
  descubrimiento **desde disco**, no un campo en memoria: la pregunta es si un
  cliente MCP podría conectar ahora mismo, y eso es lo que un cliente lee.
  Responde versión, commit, árbol limpio o no, y enlaza el log y el Hub. La
  cinta se construye ANTES que el puente y no depende de él — si el pipe falla,
  la pestaña es lo único que puede decírselo a alguien que mira Revit y no un
  log. Su propio fallo se registra y se traga.

- **El puente dice de dónde viene, en los tres sitios donde es visible.** El
  handshake MCP devuelve `instructions` (el slot que el protocolo reserva para
  esto): el contrato, que `health` va primero, y que el puente es neutral por
  diseño — los estándares no están aquí y no hay que inventarlos.
  `horizun_health` devuelve `horizun_hub`, que nombra esa misma propiedad como
  algo sobre lo que quien llama actúa: un comando al que "le falta" un catálogo
  no está roto. Y el instalador publica la URL del Hub donde Windows la
  reenseña después. El acceso directo del navegador es **opt-in y desmarcado**.

- **`install.ps1`: instalación desde el fuente, sin ejecutables.** El camino
  para un agente (Claude Code, Codex) o una persona tras clonar el repo:
  detecta los Revit instalados por su propia `RevitAPI.dll`, compila cada año
  contra su API, compila el servidor, **primera instalación incluida** (el caso
  que los scripts de deploy rehúsan), todo stageado antes de instalar nada,
  libro de deshacer, y verificación releyendo cada binario instalado.
  `AGENTS.md` documenta el procedimiento completo para el agente.

### Arreglado

- **Los iconos de la cinta no se desplegaban.** `Install-HorizunPayload` y el
  staging de `pack.ps1` copiaban `*.dll` y `lib/` y nada más, así que el primer
  deploy de la cinta envió botones sin imagen — y `Ribbon.cs` degrada un icono
  ausente a botón plano, que es exactamente por qué nada falló y nadie fue
  avisado. Ambas copias llevan ahora `Resources/`.

## v0.3.2 — 2026-07-31

Una entrada. La encontró un lote de 31 modelos, no una lectura del código.

**EL CONTRATO SE MOVIÓ**, así que las dos mitades hay que actualizarlas juntas: el
hash cubre las descripciones y la de `horizun_job_status` cambió. Un Revit en 0.3.1
hablando con un servidor 0.3.2 se refusa en el hash — en voz alta, que es lo
correcto, pero no hay despliegue parcial que valga.

### Arreglado

- **`job_status` no distinguía "corriendo" de "el proceso murió", y sí podía.**
  Medido el 2026-07-31 auditando 31 modelos desde ACC: Revit se cayó **tres veces**
  y las tres el comando respondió `"running"` con *"or the process died […] this
  will not guess"*, mientras `Get-Process` llevaba ocho minutos sin Revit. Cada
  caída costó los minutos que tardó alguien en sospechar y salir del MCP a
  preguntarle a Windows.

  La negativa a adivinar era correcta **sobre lo que un log puede saber**, y ese
  era el error: el log no era la única fuente. El registro del job lo escribe un
  proceso concreto, y preguntarle al sistema operativo si ese pid sigue existiendo
  no toca Revit — exactamente igual que leer el log desde disco, que es lo que este
  comando ya hacía.

  El evento `start` estampa ahora el **pid**, y la respuesta trae `pid` y
  `process_alive`. El texto se parte según lo que se sabe: con el proceso **vivo**,
  "running o colgado, mira `seconds_since_last_event`" — la ambigüedad *real* se
  conserva, porque un paso lento y un cuelgue son indistinguibles desde un log. Con
  el proceso **muerto**, "este job no va a terminar nunca", que es un hecho; y avisa
  de que lo ya checkpointeado **sí ocurrió**, así que relanzar es una segunda
  escritura y no una recuperación. Un `queued` con el proceso muerto tampoco va a
  correr nunca, y ese **sí** es seguro de reenviar — el consejo contrario al del
  `queued` con Revit vivo, que es justo la distinción que justifica el campo.

  Los registros anteriores al estampado del pid conservan la frase antigua: ahí la
  vida del proceso genuinamente no se puede saber, e inventarla sería la suposición
  que este comando existe para rechazar.

  La liveness la decide `PipeClient.IsRevitAlive`, la **misma** que usa el
  descubrimiento: dos chequeos con dos reglas terminarían discrepando.

  `run_async` sigue siendo **at-most-once**. Esto no relanza Revit ni reintenta
  nada; solo dice lo que ya se podía saber.

## v0.3.1 — 2026-07-31

Hotfix. Two of these are defects 0.3.0 introduced, and the first one is the worse
kind: a guard that did not protect the thing it was added for, it removed the
command instead.

### Fixed

- **The close confirmation could never be spent.** `discard_unsaved` was one of the
  fields the plan hash was computed over. A rehearsal is sent *without* it — that is
  what makes it a rehearsal — and the execution is sent *with* it, so the two hashes
  could never match. Every token came back `PlanChanged`, and a document with unsaved
  changes could not be closed **at all**, by anyone. The distinction the code was
  missing: that list is the PLAN (what will be done, and to what); `discard_unsaved`
  is the APPROVAL. An approval that must appear inside the thing being approved is a
  circle, and this made the fourth command in this codebase to ship with it.

  The test that shipped beside the bug built **one** request object and hashed it
  twice, which is not the sequence and cannot fail on it. There is now a test that
  sends the two requests a caller actually sends.

- **`horizun_clash` measured workset coverage for the host only.** A clash is a
  statement about a *federated* model, and the host is one document among several:
  the structural link can have three worksets closed while the host has none, and
  the check would report full coverage over a model it had seen half of. The link is
  where the other discipline lives, which makes it both the likelier place for this
  and the one that matters more. Coverage is now measured per source — host and each
  loaded link — and **any** source that is incomplete or unreadable makes `result`
  `partial`.

- **`deploy-both`'s rollback left new files behind.** It moved the originals back and
  stopped. Any file the new release *adds* that the old one did not have was copied
  in and never removed, so a "successful" rollback produced a directory that was
  neither release: every old file restored, plus strangers — under a message saying
  the machine was as it was before the run. It now removes what the run brought in
  before restoring what it moved aside.

## v0.3.0 — 2026-07-31

Ten defects, all of the same family: a guard that read as stronger than it was.
None of them announced itself, and most were only visible by crossing two files
that each looked correct alone.

### Fixed — guards that were weaker than they read

- **`PlanHash` sorted every array before hashing it**, on the reasoning that a set
  in another order is the same set. `write_params_verified` takes a list of
  **operations**, and two writes to the same parameter apply in order — the last
  one wins — so `[{Width:3.5},{Width:9}]` and `[{Width:9},{Width:3.5}]` leave the
  model in two different states and hashed identically. A token issued for the
  rehearsal of one was spendable on the execution of the other, which is the
  single substitution the confirmation exists to refuse. The arithmetic moved to
  `Confirmation.cs`, which carries no Revit, so it is now provable without a
  building.

- **The session's Revit target was two static fields written one after the
  other.** The server answers requests on several threads on purpose, so a call in
  flight could read the pid of the new target beside the year of the old one — and
  a pid wins, so the command went to the instance the caller *used to* be pointed
  at and the reply looked entirely correct, about the wrong model. It is one
  immutable value now. `horizun_target` also accepted a pid **and** a year and
  silently resolved it in favour of whichever branch ran last, which was the year:
  a caller passing a pid precisely to escape ambiguity got the ambiguity. Refused.

- **The two open commands ran different guards.** `horizun_document_session` had
  **no central guard at all** — the tool whose description promises it is "guarded
  against the irreversible" would open the model everybody synchronizes to without
  a word, while `horizun_open_document` refused. `open_document` in turn had no
  newer-file rule, so `allow_upgrade=true` on a file from a later Revit bought
  Revit's own error about a file format, after the caller had agreed to something
  irreversible that was never on offer. And only `open_document` could open a
  cloud model — the command that does not take `expected_version`. One shared
  guard now, with the rules in a Revit-free decision table where every branch is a
  test. **A cloud model is a central model**, so it now needs `detach` or
  `open_central` too.

- **`Close(false)` discarded unsaved work and reported success.** `IsModified`
  cannot be asked of a closed document and the file on disk is untouched, so an
  hour of edits and a document nobody had touched produced byte-identical replies —
  a loss reported as success, undetectable afterwards by anyone including the
  handler that wrote it. Closing a modified document now needs `discard_unsaved`,
  a `dry_run`, and a token bound to that rehearsal; every close reports the
  `IsModified` it measured first. Unknown counts as modified.

- **The MCP writer refused to answer an id it had answered before, forever.**
  JSON-RPC reserves an id only while its request is outstanding. The second
  request to arrive with `id: 7` ran in full — the model was touched — and its
  reply was dropped on the way out, leaving the client waiting for an answer to
  work that had already happened. Exactly-once now belongs to the request.

- **`stdin.ReadLine()` allocated the whole line before the length check ran.** A
  client pumping a file into the wrong pipe took the process down, and with it the
  user's only bridge to Revit. The 4 MB limit is enforced *while* reading, the
  refused line is drained so the next request starts on a boundary, and the same
  shared limits now bound the pipe reply and what a script may print.

### Added — how much of the model an answer is about

- **`visibility_coverage` on `model_scan`, `audit_model`, `quantities` and
  `clash`.** A closed workset's elements are not in the document, so a scan does
  not skip them — it never sees them — and no count comes back short by a knowable
  amount. "0 imported CAD instances", "no clashes" and a concrete total were all
  true statements about what got loaded, presented as statements about the
  building. Where a command already had a flag for "this answer is not the whole
  story", a closed workset now reaches it. Unknown counts as incomplete.

### Fixed — deployment and release

- **`deploy-both` was creating the split contracts it exists to prevent.** It
  defaulted to `-Years 2025,2026`; this machine had five years installed on two
  different commits because of it. It now finds every `Horizun.addin` on the
  machine — both Addins roots — builds and stages everything before installing
  anything, rolls every change back if any step fails, and reads back the commit,
  clean-tree flag and SHA-256 of every binary that landed.

- **The release manifest covered two files out of the whole payload.**
  `horizun-mcp.exe` is an apphost; the code that runs is in the `.dll` the
  manifest never named, alongside Newtonsoft, IronPython and two thousand stdlib
  files. Everything is hashed now, with the stdlib as one ordered digest.

- **CI's `revit-integration` never downloaded the artifact it claimed to test.**
  It read `dist/` off the runner. It downloads the package now and re-checks every
  hash the packaging job recorded, including that it is from this run's commit,
  before executing the installer.

### Changed

- **Clients register as `horizun`, not `horizun-next`.** The name came from the
  months when this build was a candidate sitting beside a shipped one. It is the
  shipped one now, and a default that says "next" makes every session, screenshot
  and set of instructions disagree with the product's own name. `-Name` still
  takes anything, which is what it was always for.

## v0.3.0 (previa) — 2026-07-31

*(Esta sección quedó como «Unreleased» cuando se cortó la v0.3.0 y nunca se
retituló: es el trabajo entre v0.2.0 y v0.3.0, y se publicó con la v0.3.0.)*

### Added — release and cutover tooling

- **`scripts/verify-release.ps1` — one commit, from source to what is running.**
  The acceptance report recorded twice that "a hash cannot prove a binary came
  from a commit", which was true and the wrong place to stop: the hash proves
  *identity*, and the commit stamped into each binary proves *provenance*.
  Together they answer the question a release asks. Four links, each checked:
  git HEAD and a clean tree → a manifest naming that commit → every staged file
  matching by hash **and** stamping that commit → the same for what is installed.
  The last link is what proves the installer carried the right payload.

  It also catches two things no hash in a manifest reveals: two years sharing a
  binary (one was built against the wrong `RevitAPI`, invisible in a build log
  because `bin/` is shared), and an installer older than the payload it wrapped.

- **`scripts/register-client.ps1` — register beside, never instead.** Adds one
  entry under its own name (`horizun-next` at the time; `horizun` from 0.3.0) to
  Claude and Codex and touches
  nothing else; if the write would remove any existing server it restores its own
  backup and reports the attempt. `-Rollback` undoes it. It **refuses a running
  client** by default, because both rewrite their config from memory while they
  run — measured here: `~/.claude.json` was rewritten four minutes into an
  editing session, which loses the edit silently and looks like a tool that never
  appeared.

### Changed — release gate

- **`verify-live` emits JSON, and NOT COVERED became an exit code.** It printed
  four outcomes and returned only three. `not_covered` was a warning at the
  bottom of a run that exited 0 — so a run that never attempted half its
  guarantees looked, to any script reading the exit code, exactly like one that
  established all of them. Exit codes are now `1` failed, `2` unverified, `3` not
  covered, most-severe first, and `-ReleaseGate` is what makes the third fatal.
  `-Json` writes every probe with its outcome, the fixtures that were present,
  and the provenance — written from the same list the console prints from,
  because two renderings of one run are two things that can disagree.

- **The integration suite runs against the INSTALLED artifact.** `-Server`
  defaulted to `bin/Release`, so it proved the developer build worked and said
  nothing about the package anybody installs. It now defaults to the installed
  executable and **refuses** a `bin/Release` path unless `-AllowDevServer` says
  deliberately that this run does not speak for the artifact — and that flag adds
  a `not_covered` entry, so it cannot pass a release gate quietly.

- **Provenance is checked, not merely present.** `horizun_version is non-empty`
  proves something answered; a stale add-in from three days ago passes it. New
  probes assert the exact `-ExpectedCommit` with `built_from_clean_tree`, and the
  full SHA-256 of the installed server and of that year's add-in against the
  release manifest. A hash that does not match is a **failure**, not a gap: a run
  against the wrong binary cannot speak for the right one.

- **The manifest carries what identifies a release.** Schema 2: the full 40-character
  commit, whether the tree was clean, the **server** (which it never mentioned at
  all), and full SHA-256 per payload instead of a 16-character prefix — a prefix
  is convenient to read in a log and verifies nothing.

- **`pack.ps1` refuses a dirty tree.** A build from uncommitted changes is stamped
  `<sha>-dirty`, and a sha with `-dirty` on it names a commit the binary is not.
  It used to be discovered afterwards by reading `horizun_health` on something
  already installed; it is now refused before anything is built, listing the
  files responsible.

- **CI runs the live suite on 2025 *and* 2026**, against the installed package,
  and publishes the JSON as a workflow artifact `if: always()` — the results of a
  failed run are the ones somebody actually needs. Fixture names come from a file
  on the runner rather than the repository, for the reason in
  the client-name policy; see
  [docs/live-fixtures.example.json](docs/live-fixtures.example.json).

### Fixed

- **The async lifecycle: a drain nothing called, and two raises whose answers
  were thrown away.**

  `AsyncQueue.DrainForShutdown()` existed, was correct, and had a passing test.
  **Nothing called it.** So every job still queued when Revit closed kept an open
  record — and an open record is reported by `job_status` as the ambiguity it
  deliberately refuses to resolve, *"still running, or the process died"*, when
  the truth was known exactly: it never started. No behavioural test could catch
  that, because the behaviour was never reached. `OnShutdown` now drains, and a
  source-level test fails if that wire is cut again.

  `ExternalEvent.Raise()` answers, and Revit can refuse. `Dispatcher.Invoke`
  handled that — a caller is blocked on it. The two places that raise for the
  **async queue** did not: one logged a warning and carried on, and
  `RunOneAsync`'s was a bare `_event.Raise();` with the result discarded
  entirely. A refusal there stranded every *successive* job in a batch silently:
  the entry that had just run reported itself correctly and the rest sat on a
  queue nothing would ever pump again, records open. Both go through
  `AsyncPump.Pump` now, which closes the queue as `not_started` when Revit says
  no — Denied is not transient, so there is no later raise that would rescue them.

  **`Denied` is a test case now, not a reasoned argument.** The acceptance report
  recorded it as unverifiable because it "needs a Revit that is shutting down".
  What made it unverifiable was the logic living inline in a method holding an
  `ExternalEvent`. Behind `IWorkRaiser` all three answers — and a raiser that
  throws — are ordinary tests.

- **The async queue is bounded, at 32.** Entries run one at a time on the UI
  thread, so a queue is a promise about the future: an unbounded one lets a
  caller in a loop put hours of committed mutations behind a reply that said
  "queued" in milliseconds. `Add` became `TryAdd` with an explicit refusal —
  a void add on a bounded queue has two implementations and both are wrong.

- **`job_status` reports five states.** `queued`, `running`, `ok`, `failed`,
  `not_started` — where there used to be `finished` plus one sentence covering
  three different situations. The ambiguity is kept only where it is real
  (`running` genuinely cannot be told from a dead process), and dropped where it
  never was. A `not_started` job is safe to send again and now says so; a
  "might be running" one is not, and that difference is the whole point.

- **"Exactly once" is no longer claimed unconditionally.** The reply now names
  the three things at-most-once rests on and the one it does not cover: a Revit
  restart forgets every idempotency key, so a retry across one runs the script
  again.

### Changed

- **`execute_python` is inside the mutation policy now — and is still a
  privileged bypass.** Two changes and one retraction.

  *It was the only command aimed at whatever window was in front.* Every typed
  write refuses to act without `target_document`, matched against the **active**
  document. The command that can do everything the typed writes can, plus
  everything they cannot, did not. Meanwhile "every mutation validates the
  document" sat in the acceptance report marked met, on evidence from the seven
  commands that were checked. A sentence true of the seven and false of the
  surface is worse than no sentence: it reads as a guarantee. `target_document`
  is now required and gated by the same `DocumentGate.ForMutation`. The cost is
  real and stated: a script that needs no document at all can no longer run
  through this tool.

  *`run_async` was at-most-once only if the request arrived once.* `AsyncQueue`
  guarantees a queued entry is claimed exactly once, and that was written down as
  the reason `run_async` is safe to point at a mutation. It is half the story.
  The other half is the wire: the reply carrying the `job_id` is exactly the
  message that gets lost, and a client that retries a timeout — the correct thing
  for a client to do — produced a **second** queue entry, claimed exactly once,
  for a total of two executions that nothing downstream could tell from two
  deliberate runs. `run_async` now requires an `idempotency_key` bound to the
  Revit process id, the document identity, a SHA-256 of the code and every other
  argument, canonicalised so key order in the JSON does not matter and a changed
  value does. Same key, same request → the original `job_id`, **nothing queued**.
  Same key, different request → refused, because silently honouring it would
  discard the new request while reporting it as deduplicated. Supplying a key
  *without* `run_async` is refused rather than ignored: a synchronous run keeps
  no stored answer to replay, so accepting the key would claim a guarantee that
  does not exist.

  The queued copy carries `target_document` too, and the gate runs again when the
  UI thread takes the entry — a queued mutation whose target is no longer active
  must not land somewhere else. It fails into the job record, which is where an
  async caller reads outcomes anyway.

  *The retraction.* `execute_python` still has no dry run, no plan hash and no
  confirmation token, so nothing rehearses what it will do, and there is no way
  to predict the effect of arbitrary code without running it. It is now a
  **document-scoped privileged bypass** — an accepted risk with named
  compensating controls, written into [docs/security-model.md](docs/security-model.md)
  §3a and into the tool's own description where a caller reads it. Horizun no
  longer claims one uniform typed-mutation policy.

  Nineteen new tests, and the ordering ones assert position rather than presence:
  a gate that runs after the work is queued is not a gate. Verified to fail
  against the previous commit — 9 of the 10 source-level checks did, and the
  tenth exposed a fault in itself first (`IndexOf` returns −1 for absent, and −1
  is less than every real offset, so it passed with the gate deleted outright).

- **One data root, shared by both halves: `%USERPROFILE%\.horizun\`.** Settings,
  discovery, jobs and logs each used to compute their own location from
  `SpecialFolder.LocalApplicationData` — **seven** lines, in two projects that
  ship separately, that agreed by coincidence. They stop agreeing the moment the
  two processes resolve that folder differently, which is not exotic: a packaged
  (MSIX/AppContainer) host redirects `FOLDERID_LocalAppData` into its own
  per-package cache, and a different user or elevation context is a different
  profile outright. The MCP server is launched by the MCP client; Revit is
  launched by the user.

  Nothing errors when they diverge. The server lists an empty directory and
  reports *"no Revit has published a bridge"* while Revit sits there with the
  add-in loaded and its own log growing — a symptom that points at everything
  except the cause.

  `HorizunPaths` is now the single answer, linked into both projects the way
  `Settings.cs` already was. `horizun_health` and `horizun_target` both report
  `data_root`, `settings_path`, `discovery_path`, `jobs_path` and `logs_path`,
  each with **measured** readable/writable — so "the two halves are looking at
  different folders" is something you can see by putting two replies side by
  side.

  Corrected in passing, because the first version of this change was written on a
  wrong premise: `Environment.GetFolderPath` does **not** read `%LOCALAPPDATA%`.
  Measured on .NET 8 — setting the variable in-process changes the variable and
  leaves `GetFolderPath` returning the real folder. So the tests written to
  "simulate the split" by moving that variable would have passed against the old
  code too. They were replaced by two that fail against the previous commit: the
  root is asserted not to be under `LocalApplicationData`, and a source scan over
  everything under `src/` fails if any state path is computed from it again.
  `%USERPROFILE%` is consulted only as a fallback for the same reason — it is
  inheritable, and the server is a child process of the client.

  **Upgrading resets `enable_execute_python` to its safe default.** The old
  `%LOCALAPPDATA%\Horizun\settings.json` is not read and not migrated: absence is
  OFF, and silently carrying a machine's arbitrary-code-execution posture across
  a relocation is not a thing an installer should do quietly. Re-state it in the
  new file. `horizun_health` names the old folder when it still holds files, so a
  machine mid-migration explains itself.

### Added

- **Revit's objections now reach the caller.** The number one way this kind of
  automation dies: a transaction commits, Revit raises a warning, a modal dialog
  opens, and the UI thread stops — not crashed, not finished, waiting for a click
  nobody is there to give, until the call times out with nothing to show for the
  work already done. Dialogs are now cancelled so the bridge cannot hang. But the
  obvious other half — swallow the warnings — is the exact lie this codebase
  refuses: a warning is Revit telling you something about the model. So every
  failure and dialog is recorded and returned as `revit_said`, beside the result,
  with the elements Revit blamed. It travels on FAILURE too, because what Revit
  objected to is usually the reason. Errors are not auto-resolved: resolving one
  changes the model, usually by deleting something.

  Proven live: two deliberately overlapping walls produced *"Highlighted walls
  overlap…"* with both element ids, dismissed so the commit could finish and
  reported in full — on a call that had failed for an unrelated reason.

- **`horizun_job_status` and `checkpoint()` — watching a long run from outside.**
  While a long command executes, Revit's UI thread is inside it and the pipe is
  waiting for it to end, so asking the plugin for progress means asking the thing
  that is busy. Scripts now call `checkpoint("label", done, total)` with no
  import; each call reaches disk immediately, and `horizun_job_status` reads that
  file **host-side, without touching Revit at all**. Verified by querying a
  running job mid-flight and getting its checkpoints back while the UI thread was
  blocked.

  The record is append-only and flushed line by line, so a Revit crash at minute
  twenty leaves everything up to minute twenty — the next run can skip what is
  already done. A job with no finish record is reported as exactly that and never
  guessed to be "stalled": a log cannot tell a slow step from a dead process.

- **`horizun_capture_view` — the caller can see.** Everything else here reads
  parameters and counts elements, which settles what is written down and nothing
  else: whether a wall landed where it should, why a section looks wrong, whether
  a sheet reads properly. An automation that cannot look at the model builds on
  what it never saw. The view is exported as a PNG and the image itself rides back
  in the response as an MCP image block, not as a path only something with a
  filesystem could use.

  The honesty problem it exists to solve: `ExportImage` does **not** write the file
  you ask for. It treats the path as a stem and appends the view type and name —
  `view.png` came back as `view - 3D View - HZ_3D_Prueba.png` in the first live
  run. A handler echoing the requested path names a file that is not there. This
  one exports into a folder of its own, looks at what actually appeared, and
  reports that: real path, real byte count, and pixel dimensions read out of the
  PNG header rather than the ones requested. Schedules are refused — Revit cannot
  raster-export them — instead of reporting a capture that does not exist.

  Verified live in Revit 2026: four walls and a 3D view built for the purpose,
  captured at 1200×1048, and the image came back legible.

- **`horizun_target` — which Revit is answering, and how to change it.** Two Revit
  versions open at once is a Tuesday here: a model saved by one year does not open
  in another, and opening a file can start a second instance on its own. The server
  picked the newest live bridge and said nothing about it, and the only way to
  override that was an environment variable read once at process start — set by the
  MCP client, so choosing a target meant editing a config and restarting everything.
  A read then answers about the wrong model; a **write lands in it**. This reports
  every bridge (year, pid, whether that process is still alive, add-in version) and
  makes the choice switchable inside the session. Host-resident: it reads the same
  discovery files the router reads and never touches Revit.

- **The server keeps a log.** The plugin has kept one since a silent startup
  failure proved indistinguishable from "not installed"; this half had none. A
  stdio server cannot report anything about itself — stdout *is* the protocol and
  stderr belongs to whoever launched it — so a bad call left nothing on disk to
  compare a chat message against. `%LOCALAPPDATA%\Horizun\logs\server.log` now
  records tool names, which Revit each call was routed to, outcomes and durations.
  Never arguments: those carry model content and file paths.

### Fixed

- **A caller could be handed another caller's result.** The dispatcher kept ONE
  pending request and ONE completion signal for everybody. After a timeout it
  returned and released its lock — but a Revit command cannot be aborted from
  outside, so the work kept running. Three silent failures followed from that, and
  every reply carried the asking caller's own request id, so nothing downstream
  could tell:

  1. *Stale wake* — A times out, B starts, A finishes and signals "done", and B
     returns A's result as its own.
  2. *Double execution* — an `ExternalEvent` raise that has not fired yet fires
     later, finds the pending slot overwritten, and runs the NEW request; then the
     new raise runs it again. For a write, the same edit applied twice.
  3. *Zombie start* — a request abandoned while Revit sat on a modal starts minutes
     later against a model the user has moved on from.

  Requests are now objects that own their own completion signal, the UI thread
  *takes* one exactly once, and an abandoned request is dropped before it can
  start. While something is in flight new work is **refused** with a description of
  what is holding the thread and how long it has been there — not queued behind a
  run that already blew a ten-minute budget. `RequestGate` carries no
  `using Autodesk.*`, so all three failures are pinned by unit tests instead of by
  a live run that only a large model would ever reach.

- **One malformed line killed the server.** `id` and `method` were pulled out of
  each message *before* the guard that catches bad ones. A message whose `id` was
  an object rather than a scalar threw an uncaught cast on the way in and ended the
  process — and with it the client's only bridge to Revit. Every step of reading a
  message is now inside the guard. An id that cannot be echoed is refused as an
  invalid request and **not dispatched**: a reply nobody can match to a request is
  not an answer, and doing the work anyway was measurably worse than declining.

- **A long command blocked every later connection.** The accept loop served each
  request inline before listening again, so while a command held the UI thread a
  second client sat in its connect timeout and got "the pipe did not answer" — you
  could not even ask whether Revit was alive. Connections are now accepted
  concurrently, each on its own thread. Accepting is not permission to run: the
  dispatcher still admits one command at a time, so the second caller gets a
  sentence instead of a hang. The header comment claiming this already worked was
  wrong, which is its own kind of defect in a codebase that sells honesty.

- **The server advertised tools the connected add-in might not have.** The two
  halves deploy separately, so a server built today routinely meets a plugin
  installed months ago — on this machine, four Revit years were running a build
  with 16 commands against a server offering 20. The only symptom was "Unknown
  command", which reads like a bug in the request. The add-in now publishes its
  version and its actual command list (discovery schema 2) and the server names the
  mismatch and the fix. A schema-1 file publishes no list, and that is reported as
  **unknown, never as "supports nothing"**.

## v0.2.0 — 2026-07-24

### Added

- **Session tools**: `horizun_health` (which Revit is on the other end, our own
  build, the log path, and the document active right now), `horizun_open_document`,
  `horizun_save_document`, `horizun_relinquish_all`. 18 tools in total.
- **`horizun_open_document` refuses an upgrade.** The file's own saved version is
  read from the file (`BasicFileInfo`, nothing is opened) and compared to the
  running Revit. A mismatch — or a version that cannot be read — is a refusal
  unless `allow_upgrade=true`. A batch pointed at the wrong Revit does not fail
  loudly; it succeeds every time and moves a whole library forward a version.
- **A log.** `%LOCALAPPDATA%\Horizun\logs\revit-<year>.log`, with stack traces.
  `OnStartup` has to swallow its exception (throwing takes Revit down), which made
  a failed install indistinguishable from no install at all.
- **Installer** (`installer/horizun-mcp.iss`, built by `scripts/pack.ps1`). One
  setup carrying both plugin runtimes; each Revit year on the machine gets the one
  it can load. Refuses to run while Revit is open, because Revit holds the files
  and a partial copy leaves the user running the old build believing they upgraded.
- **`scripts/verify-live.ps1`** — the half of the test story CI cannot reach. It
  asserts on what each answer says, refuses a stale discovery file, and prints what
  it did *not* cover.
- **`scripts/sign.ps1`** for code signing (certificate supplied by the operator).

### Fixed

- **`execute_python` was broken on .NET Framework** (Revit 2024 and earlier) and
  nobody knew, because it had never been run there. `ScriptScope` on that runtime
  carries a second `SetVariable(string, ObjectHandle)` overload; an untyped `null`
  binds to it, since `ObjectHandle` is more specific than `object`, and it throws
  `ArgumentNullException`. Fixed with three casts.
- **`excel_write_rows` left `<dimension>` stale.** The rows were in the file and
  the tool reported them verified — but a reader that trusts that element, which
  includes `openpyxl` in read-only mode and therefore `pandas.read_excel`, never
  saw them. The verification was circular: it re-read with its own parser, which
  ignores `dimension`.
- **`catalog_lookup` assumed UTF-8 silently.** A catalog saved as ANSI (what Excel
  produces on a non-English Windows) lost every accented character to U+FFFD and
  came back `exists: false` — a fabricated "not in the catalog" that was really "I
  misread the file". It now decodes strictly, falls back to Latin-1, and reports
  which encoding it used.
- **`Reconcile.Compare(NaN, x)` returned `Agree = true`.** `Math.Max(NaN, x)` is
  `NaN`, `NaN > 1e-9` is false, so it fell into the zero-guard and claimed
  agreement between a real measurement and none at all. Non-finite input is now
  `comparable: false`, and `Guard.Reconcile` no longer emits `NaN` into JSON.
- **IronPython needed the codepage provider registered.** On .NET 5+ codepage 1252
  is not available unless `CodePagesEncodingProvider` is registered, and the engine
  dies in a console-less host. It appeared intermittent because the registration is
  process-global: it worked whenever another add-in had already done it.
- `confirmed_active` in `open_document` compared document handles by reference and
  reported a false negative for the document it had just opened.

### Security

- The named pipe now grants `FullControl` to the current Windows user and nothing
  else. It previously inherited the process token's default DACL, which is reachable
  by other logged-in users on a shared machine. The auth token already gated every
  request; this is the second lock.

### Verified

- Live in **Revit 2026 (net8)** and **Revit 2024 (net48)**, 5/5 each. The 2026 run
  was against the add-in as deployed by the installer, not a developer copy.
- The upgrade guard was proven against a real family saved in Revit 2023.
- Both of `open_document`'s guards, live: the version guard, and the central guard
  refusing a real workshared central until `open_central=true` was passed.
- `relinquish_all`'s happy path, on a workshared central created for the purpose
  rather than borrowed from a client: 2 worksets owned before, 0 after,
  `fully_relinquished: true` — measured on both sides, not assumed.
- 59 Revit-free tests; CI green on a hosted runner.

### Known limits

- `excel_write_rows` appends below an Excel Table without expanding the table's
  range (reported per call).
- The add-in is unsigned: Revit shows its "Security - Unsigned Add-In" dialog.
  Worth stating precisely, because the obvious assumption is wrong: **signing the
  DLL does not remove the dialog**. It becomes a "Signed Add-In" prompt naming the
  publisher, shown once per certificate per machine rather than per binary. No
  dialog at all additionally requires the publisher's public certificate in the
  machine's Trusted Publishers store before Revit starts
  (`certutil -addstore TrustedPublisher`), which is a change to the machine's
  trust configuration and must be an explicit opt-in during install.

  **Measured the hard way: signing WITHOUT trusting is worse than not signing.**
  A self-signed certificate was created, the add-in signed and timestamped, and
  Revit answered with `Security - Invalid Signature` - "This signed add-in has a
  security problem", Publisher: Unknown, Issuer: None - where the unsigned build
  had been loading silently. Windows cannot chain a self-signed certificate to a
  trusted root, and Revit reports that as tampering rather than as "unknown
  publisher". So the certificate and the trust-store step are one package, not two
  steps where the first helps a little. Reverted to unsigned; scripts/dev-cert.ps1
  and scripts/sign.ps1 are ready for the day there is a certificate to trust.

  Measured on this machine, and worth knowing before buying anything: after the
  add-in was authorised once, roughly eight later rebuilds and restarts loaded
  with no dialog at all — so on Revit 2026.4 the trust survived new binaries.
  That contradicts the common claim that every new build re-triggers the prompt.
  The dialog is a problem for CLIENT machines, not for a developer's own.

## v0.1.0 — 2026-07-24

First tagged state. 14 tools over a clean-room plugin (UI-thread dispatcher over
`ExternalEvent`, token-authed named pipe, discovery file) and a hand-rolled MCP
server. `Guard`/`Reconcile` carry the honesty contract: a command may not report
work it did not verify.

History squashed to a single commit before the repository ever had a remote — the
development history carried client-specific strings, and rewriting the files alone
would have left them reachable.
