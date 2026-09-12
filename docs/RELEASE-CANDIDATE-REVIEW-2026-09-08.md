# Revisión del candidato local — 2026-09-08 (fases 1, 2 y 3)

Continúa [RELEASE-CANDIDATE-REVIEW-2026-09-07.md](RELEASE-CANDIDATE-REVIEW-2026-09-07.md).
La bitácora con decisiones, medidas e incidentes es
[STABILIZATION-LOG-2026-09-08.md](STABILIZATION-LOG-2026-09-08.md); el contrato
del bloque está resumido en [DELIVERABLE-PRODUCTION.md](DELIVERABLE-PRODUCTION.md).

## Conclusión

**Candidato local validado en este alcance.** Producto en el commit local
**`7fe8693`** (c14; c15–c19 solo tocan el conductor de la matriz y sus
pruebas: `git diff 7fe8693..e0e2436 -- src/` está vacío). No es un release
aprobado: no se instaló, no se publicó, no se hizo push, y quedan los
pendientes de publicación de la sección final (todos fuera de este alcance).

> Una sesión posterior (2026-09-09) cerró los pendientes de seguridad del
> conductor y la revisión visual sin tocar el producto: ver la sección
> **Continuación — 2026-09-09** al final de este documento. Lo que sigue es el
> resultado del 08-09 y se conserva tal cual.

## Resumen ejecutivo

- **Seguridad del conductor de la matriz** (`fefadf8`, `3e6dcba`, `3d273f2`, `5f3e3a0`, `4719769`, `e0e2436`):
  ya no mata ningún Revit; identifica su sesión por pid + hora de inicio +
  ejecutable y la recomprueba justo antes de cerrar; lee por el puente lo que
  está abierto y lo contrasta con el libro de lo que abrió el ensayo; ante
  documento ajeno, identidad dudosa, health sin respuesta o cierre rechazado
  deja el Revit corriendo y escribe `recovery-pending-<año>.json`; cierra solo
  fixtures propios por el cierre tipado con ensayo y token; salida normal por
  la ventana; ni foco ni clics; los diálogos de arranque solo por lista blanca
  y nunca guardar/descartar/seguridad; `-Enable` fallido detiene el año antes
  de arrancar Revit; `-Restore` se cree solo con código de salida y disco; no
  se restaura con un Revit del año abierto; no se borran descubrimientos
  ajenos. Probado sin Revit con `year-matrix.session.tests.ps1` (**44
  casos**, procesos auxiliares y respuestas enlatadas). Seis defectos del propio
  conductor salieron de sus corridas reales (modo estricto filtrado, cierres
  sin capturar, código de salida como array, ejecutable no legible al arrancar,
  bandera de árbol limpio siempre falsa, modal ajeno que llega después de la
  ventana de asentamiento) — en todos la guarda se sostuvo: ningún Revit se
  mató, ningún manifiesto se tocó bajo un Revit abierto, y las dos sesiones
  dejadas corriendo lo fueron por una duda real (identidad, modal) y se
  recuperaron por las mismas reglas.
- **Fixture de etiquetas cerrado**: `HZ_WRITE3.rvt` (copia nueva, desechable)
  con la familia `M_Multi Category Tag` de la plantilla Autodesk, enmarcada y
  con `Type Mark` en 50 tipos; verify-live pasa las dos sondas que quedaban
  sin verificar. Por el camino, tres hechos medidos de la API (plantilla que
  oculta la categoría, familia solo-etiqueta sin caja, anfitrión fuera del
  rango de vista) y una negativa del puente que ahora nombra la causa.
- **Candidato final validado en Revit 2026 (c14)**: estabilización **41/0/2 y
  42/0/1**, general **14/0/1**, verify-live **241/0/0** funcionales (242/1/0/1
  con el manifiesto de release y el artefacto instalable, ambos de
  publicación), interfaz **21/21** sobre la DLL del candidato.
- **Matriz local**: los cinco años instalados medidos con el mismo producto (`git diff 7fe8693..e0e2436 -- src/` vacío): 2026 en la sesión c14 (DLL estampada `7fe8693`); 2023, 2024, 2025 y 2027 por el conductor c16 (binarios por año estampados `3d273f2`); 2025 repetido con el conductor c17 (`5f3e3a0`) y 2023 repetido con el conductor c19 (`e0e2436`) tras un intento con c18 que un modal ajeno tardío dejó corriendo y que se recuperó por las reglas del módulo. Estabilización 41/0/2 en la primera corrida de cada fixture y 42/0/1 al reanudar su libro, general 14/0/1 en todas las filas; cierre y restauración automáticos en 2023, 2024, 2027 y en las repeticiones; la primera fila 2025 quedó corriendo por una duda de identidad real (ejecutable no legible al arrancar, corregido en c17) y la de 2026 era una sesión manual — el usuario cerró ambas sin guardar antes de reiniciar y sus manifiestos se restauraron el 09-09 con Revit cerrado.
- **Revisión visual** de páginas PDF renderizadas: sin recortes en las
  impresiones con offsets (zoom explícito), papel y orientación correctos, el
  desbordamiento del rótulo aparece donde la política lo declara y la
  exportación lo rechaza; un número de lámina del arnés general desborda la
  casilla del rótulo Snowdon sin que la página crezca — caso que
  `fits_titleblock_cell` cubre cuando el perfil declara la casilla.

## Correcciones realizadas (fase 3)

| Commit | Qué | Clase |
| --- | --- | --- |
| `fefadf8` | conductor de la matriz: identidad, libro de documentos, cierre solo de fixtures propios, sin terminación forzada, restauración verificada, recovery pendiente; módulo `year-matrix.session.ps1` + 26 pruebas | seguridad del arnés |
| `efc7502` | textos del menú ajustados a lo que el código garantiza | interfaz |
| `1c8e249` | la negativa "no readable extent" nombra la causa (anfitrión no mostrado, categorías apagadas, categoría oculta por plantilla, texto vacío) antes de culpar a la familia; límite medido de las familias solo-etiqueta | producto |
| `7fe8693` | módulo sin `Set-StrictMode` filtrado; lista de Revit en ejecución siempre array | conductor |
| `3e6dcba` | sondas reales con cierres capturados; una fila por año; 28 pruebas | conductor |
| `3d273f2` | códigos de salida de Enable/Restore como entero; 31 pruebas | conductor |
| `5f3e3a0` | identidad: espera un ejecutable legible y guarda el esperado; `unknown` sin ninguno; 34 pruebas | conductor |
| `4719769` | bandera de árbol limpio contada (`Get-HzRepoStatus`, `repo_status` grabado) y aviso del servidor formateado; 38 pruebas | conductor |
| `e0e2436` | un modal tardío de la lista blanca se cierra una vez sobre el pid propio y se reintenta la apertura o el health; guardar/descartar/seguridad nunca; `state_before` conservado; 44 pruebas | conductor |

## Candidato final: identidad

| | Valor |
| --- | --- |
| Commit de producto | `7fe8693` (árbol limpio, estampado limpio) |
| DLL Revit 2026 cargada | `8adb404f8b5450561653752cb0c30202ebaf19d18c61ad23781a62d581e6dfe9` (`%USERPROFILE%\.horizun\dev-addin-2026-09-08-c14\2026\Horizun\Horizun.Revit.dll`, firmada con el certificado local) |
| Servidor de desarrollo | `f4daacdd28f96aa9f39e142a497751c4fbdcef6e910f9e88a894…` (binario local de desarrollo, no incluido en el repositorio) |
| Revit | 2026.4 (26.4.0.32), `English_USA`, pid 35476, sesión aislada |
| Arneses | `verify-deliverable-stabilization.ps1` (44 casos), `verify-deliverable-production.ps1` (15), `verify-live.ps1` (241) con `-ExpectedCommit`, `-ExpectedAddinSha256`, `-ExpectedServerSha256` |
| Fixtures | `HZ_WRITE` (estabilización y general), `HZ_WRITE3` + `HZ_LIVE_B` (verify-live); nunca guardados durante las corridas |

## Pruebas ejecutadas y conteos exactos

| Capa | Resultado |
| --- | --- |
| Core (xUnit) | **3.882 aprobadas, 0 fallidas, 0 omitidas** |
| Server | **477 aprobadas, 0 fallidas** |
| Contrato, inventario, consistencia pública, escaneo sensible | en verde (80 herramientas / 204 operaciones / 798 valores) |
| Compilación 2023, 2024, 2025, 2026, 2027 | cero errores, cero advertencias |
| Seguridad del conductor (`year-matrix.session.tests.ps1`) | **44 aprobadas, 0 fallidas** |
| Estabilización, Revit 2026, c14 | run 1 **41/0/0 sin verificar/2 no cubiertas**; run 2 (reanuda el libro) **42/0/0/1** — la no cubierta es la aceptación visual |
| General de entregables, Revit 2026, c14 | **14/0/0/1** (aceptación visual) |
| verify-live, Revit 2026, c14, `HZ_WRITE3` activo, identidad exigida | **241 aprobadas, 0 fallidas, 0 sin verificar** de las 241 funcionales; en total 242/1/0/1: falla "binario ≠ manifiesto de release" y no cubre "artefacto instalable" — ambos requisitos de publicación |
| Interfaz sobre la DLL c14 | **21/21** (estado mostrado = `settings.json`; cada fila selecciona su clave; Cerrar no selecciona; sin cambio de ajustes; sin ids técnicos a la vista) |
| Matriz por año | ver tabla |

### Matriz por año (conductor corregido, producto `7fe8693`)

| Año | Revit | DLL cargada | Estabilización | General | Cierre y restauración |
| --- | --- | --- | --- | --- | --- |
| 2023 | pid 49596, sesión aislada del conductor c16 (`3d273f2`) | `a9f37f0e…` | 41/0/0/2 (primera corrida en `HZ23_BASE`) | 14/0/0/1 | cerrado por el puente y la ventana; manifiesto restaurado y verificado; modal ajeno de arranque cerrado por lista blanca |
| 2023 (repetición, conductor c18 `4719769`) | pid 37728 | `aa0f784e…` | — | — | **sin medir**: el modal ajeno llegó después de la ventana de asentamiento, la apertura fue rechazada y el conductor dejó la sesión corriendo; recuperada por las reglas del módulo (0 documentos, cierre normal, `-Restore` 0) |
| 2023 (repetición, conductor c19 `e0e2436`) | pid 10008 | `f39d0645…` | 42/0/0/1 | 14/0/0/1 | modal ajeno cerrado en la espera del puente; cerrado; restaurado y verificado; salida 0 |
| 2024 | pid 53332 (c16) | `efd124af…` | 42/0/0/1 | 14/0/0/1 | cerrado; restaurado y verificado |
| 2025 | pid 53400 (c16) | `b7202762…` | 42/0/0/1 | 14/0/0/1 | **dejado corriendo** por identidad dudosa (ejecutable no legible al arrancar); el usuario lo cerró sin guardar (23:03Z); manifiesto restaurado el 09-09 con Revit cerrado |
| 2025 (repetición, conductor c17 `5f3e3a0`) | pid 28728 | `e186594f…` | 42/0/0/1 | 14/0/0/1 | cerrado; restaurado y verificado; salida 0 |
| 2026 | pid 35476, sesión c14 manual (`7fe8693`) | `8adb404f…` | 41/0/0/2 y 42/0/0/1 | 14/0/0/1 | además verify-live 241/0/0 funcionales (242/1/0/1) e interfaz 21/21; el usuario la cerró sin guardar (23:03Z); manifiesto restaurado el 09-09 |
| 2027 | pid 56428 (c16) | `1fd08835…` | 42/0/0/1 | 14/0/0/1 | cerrado; restaurado y verificado |

Cada fila es una corrida independiente con su propio binario firmado (el
conductor conserva ambos, firmado y sin firmar, en `year-matrix/<año>/binaries/`);
los registros de cada arnés llevan `code_candidate_commit`, `built_from_clean_tree`
y `repo_tracked_clean`. No se suman resultados de binarios distintos.

## Resultado de la revisión visual

Ver `artifacts/stabilization-2026-09-08/visual-review.md` (local): c14 —
`margins.pdf` (lower_left + offsets + zoom 35 %) íntegro; `a3-landscape.pdf`
íntegro; `default-overflow.pdf` con el número largo fuera del rótulo y la
exportación rechazada por política; `combined.pdf` del arnés general con el
número `HZD-…-1` desbordando la casilla del rótulo Snowdon (artefacto de
nomenclatura del arnés; `fits_titleblock_cell` lo detecta cuando el perfil
declara la casilla). Cotas y etiquetas de §8B se verifican por relectura de
posición y extensión; su revisión visual en PDF queda no cubierta (deliberado).

## Estado de la interfaz

- **Idioma**: los rótulos salen de `LanguageType` de Revit, no de Windows; en esta máquina Revit 2026 es `English_USA` con Windows en español y la cinta se ve en inglés (captura `c13-ui/c13-revit-window.png` con la pestaña Horizun Hub presente; panel completo en `c10-revit-horizun-tab.png`); el render español/inglés del menú desde la DLL c14 está en `c14-ui/`.
- **Estado mostrado = estado efectivo**: las cuatro filas de "Opciones avanzadas" leen el mismo `settings.json` que usa el dispatcher (perfil traducido a palabras; ningún `safe_write` ni identificador técnico a la vista) — 21/21 checks sobre la DLL del candidato (`c14-ui/ui-checks.json`).
- **Cada opción ejecuta su acción**: cada fila selecciona su clave y cierra; el botón Cerrar no selecciona nada; abrir, pulsar filas y cerrar deja el hash de `settings.json` idéntico (`F2DD958BB2CE` antes y después).
- **Textos sin sobreprometer** (`efc7502`): el historial no nombra el archivo; un asistente en pausa aún responde que lo está; el consentimiento de *Permitir scripts* conserva la casilla de comprensión y sus textos fijados por prueba.
- **Permisos**: los del dueño no se cambiaron para probar estos controles; el perfil quedó como estaba.

## Estado final de la instalación y las sesiones

- **Instalación estable intacta**: `install.ps1` no se ejecutó; las cinco DLL instaladas conservan huella y fecha del 07-09 (2023 `548be2b0…`, 2024 `c2abafa5…`, 2025 `316b5283…`, 2026 `fe792a0d…`, 2027 `a7c8bae8…`) y el servidor instalado `dbeebb54…` (07-09 21:03Z); ningún cliente MCP reconfigurado; permisos de Python sin tocar; sin sincronización con CORE.
- **Manifiestos**: los cinco años con `Horizun.addin` → `Horizun\Horizun.Revit.dll`, sin `Horizun-dev-session.addin` ni `Horizun.addin.dev-session-aside` (2023/2024/2027 restaurados por el conductor; 2025 y 2026 restaurados el 09-09 con Revit cerrado, `-Restore` con salida 0 y verificación en disco).
- **Health de la instalación estable** (09-09 01:49Z, por el servidor instalado): `healthy`, commit `5498141` limpio, DLL `fe792a0d…`, cero documentos; la sesión propia usada para leerlo se cerró por la ventana.
- **Sesiones**: ninguna sesión del usuario se cerró, guardó ni modificó; las dos sesiones de ensayo que quedaban (Revit 2025 pid 53400 y Revit 2026 c14 pid 35476) las cerró el usuario a las 23:03Z respondiendo **No** al diálogo de guardar (leído en los diarios de Revit), y la máquina se reinició a las 23:59Z; al terminar no hay ningún Revit corriendo.
- **Fixtures**: `HZ25_BASE`, `HZ_WRITE`, `HZ23_BASE`, `HZ27_BASE` conservan sus fechas; `HZ_WRITE3.rvt` conserva la de su preparación (21:44Z); nada del ensayo se guardó sobre ningún fixture fuente. Archivos nuevos creados: `C:\hz-live\HZ_WRITE3.rvt`, `C:\hz-live\fam\HZ_MULTICAT_TAG_2026.rfa`, `%USERPROFILE%\.horizun\dev-addin-2026-09-08-c12…c14\` y `dev-addin\<año>\` (copias de desarrollo). Nada ajeno se borró; los archivos de descubrimiento no se tocaron; las evidencias de campañas anteriores siguen en `artifacts/` (el registro de recuperación de 2025 se conservó como `recovery-pending-2025.resolved-20260909.json` + `resolution.md`).

## Pendientes concretos

1. **Publicación** (fuera de este alcance): `install.ps1` en entorno seguro y
   `verify-live.ps1 -ReleaseGate` con las huellas del manifiesto.
2. **Cotas y etiquetas en PDF**: un plano del arnés que coloque la vista con
   la cota desplazada y la etiqueta con líder, para revisarlas a ojo.
3. **RFA de etiqueta para 2023–2025**: `HZ_MULTICAT_TAG_2026.rfa` es de 2026;
   las dos sondas de autoetiquetado de verify-live solo corren en 2026.
4. **`repo_tracked_clean` en los resúmenes anteriores a c18**: `year-matrix-*.json` del 08-09 y la repetición de 2025 llevan `false` por el defecto de la expresión (corregido en `4719769`); los registros de cada arnés (`repo_tracked_clean=true`, `built_from_clean_tree=true`) son los válidos.
5. **Documento ajeno real**: la ruta "documento que el ensayo no abrió → dejar corriendo" se probó por simulación (44 casos) y las de identidad dudosa y modal tardío además con un caso real cada una; un documento ajeno real no ocurrió en las corridas de hoy.

## Comandos reproducibles

```bash
dotnet test tests/Horizun.Core.Tests -c Release
dotnet test tests/Horizun.Server.Tests -c Release
for y in 2023 2024 2025 2026 2027; do dotnet build src/Horizun.Revit/Horizun.Revit.csproj -c Release -p:RevitYear=$y -warnaserror; done
pwsh -File scripts/inventory.tests.ps1; pwsh -File scripts/public-consistency.tests.ps1; pwsh -File scripts/scan-sensitive.ps1
pwsh -File scripts/live/year-matrix.session.tests.ps1
pwsh -File scripts/live/dev-addin-session.ps1 -Year 2026 -Enable -DevRoot <dir>   # con Revit 2026 cerrado
pwsh -File scripts/live/verify-deliverable-stabilization.ps1 -Document HZ_WRITE -Disposable yes-this-model-is-disposable -ExpectedAddinSha256 <sha>
pwsh -File scripts/live/verify-deliverable-production.ps1 -Document HZ_WRITE -Disposable yes-this-model-is-disposable -ExpectedAddinSha256 <sha>
pwsh -File scripts/verify-live.ps1 -Year 2026 -Server <dev exe> -AllowDevServer -ExpectedCommit <commit> -ExpectedAddinSha256 <sha> -ExpectedServerSha256 <sha> -Document HZ_WRITE3 -WriteProbes -WriteDocument HZ_WRITE3 -WriteDocumentDisposable yes-this-model-is-disposable
pwsh -Command "& scripts/live/run-year-matrix.ps1 -Years @('2023','2024','2025','2027') -Harness @('verify-deliverable-stabilization.ps1 -Document {title} -Disposable yes-this-model-is-disposable -ExpectedAddinSha256 {addin_sha256}','verify-deliverable-production.ps1 -Document {title} -Disposable yes-this-model-is-disposable -ExpectedAddinSha256 {addin_sha256}') -PrepareDocument @('2023=C:\hz-live\HZ23_BASE.rvt','2024=C:\hz-live\HZ24_BASE.rvt','2025=C:\hz-live\HZ25_BASE.rvt','2027=C:\hz-live\HZ27_BASE.rvt') -ArtifactRoot <dir>"
pwsh -File scripts/live/dev-addin-session.ps1 -Year 2026 -Restore   # con Revit 2026 cerrado
```

---

# Continuación — 2026-09-09: cierre de los pendientes de seguridad y de cobertura

Lo anterior queda tal cual: describe el candidato `7fe8693` y lo medido el
08-09. Esta sección es una sesión posterior, sobre la misma rama, que **no tocó
el producto**: `git diff 7fe8693..403f7b6 -- src/` está vacío. Cambian cinco
archivos de arnés y conductor.

## Veredicto

**Cierre local validado en este alcance.** Los tres pendientes de seguridad del
conductor (propiedad real de los documentos, restauración verificada, Revit de
año desconocido) están corregidos, con regresiones que reproducen cada defecto
contra el código anterior y pasan contra el actual, y con dos corridas reales —
Revit 2026 y Revit 2023 — que terminaron en verde, cerrando su sesión y
restaurando su manifiesto por las reglas nuevas. La revisión visual pendiente
está hecha y aceptada. El fixture de etiquetas para 2023–2025 sigue **bloqueado**,
ahora con la causa medida y la acción humana que lo desbloquea.

No es un release: no se instaló, no se publicó, no se hizo push.

## Cambios, por corrección

| Commit | Qué corrige | Prueba que lo reproduce | Evidencia de que pasa |
| --- | --- | --- | --- |
| `ced8ff4` | **A** propiedad por título; **B** restauración sin verificar; **C** Revit de año desconocido; conflicto del helper borrando antes de validar; excepción en el `finally` que perdía el informe | `repro-defects-at-318a821.ps1` casos A1–A3, B1–B3, C1–C2 (8/8 reproducidos) | `year-matrix.session.tests.ps1`: 7 casos de propiedad, 10 de restauración, 6 de identidad de proceso, el lector de manifiestos real contra un `%APPDATA%` temporal, el helper real (conflicto, parcial, completado, idempotencia) y un proceso real llamado `Revit.exe` |
| `da70072` | falta un plano que lleve la cota desplazada y la etiqueta con líder | — (cobertura ausente, no defecto) | arnés `verify-deliverable-visual.ps1` |
| `e12f9c0` | sonda que no ve las funciones de su archivo bajo `pwsh -Command`; juicios de respuesta inprobables; hora no comparable leída como otro proceso; documento limpio imposible de cerrar | la corrida real de 2026 (`ym-2026`) y `recovery-2026-run{1,2}.log` | prueba AST + las sondas reales bajo las dos formas de arranque; lectores de respuesta contra respuestas enlatadas; identidad ida y vuelta por JSON; `recovery-2026-run3.log` |
| `32c2c14`, `e1e531b`, `e5d842d`, `4e1f4d0`, `403f7b6` | defectos del arnés nuevo hallados en vivo: nombres de campo, familia de etiqueta incapaz de etiquetar un muro, modo `multi_category`, líder degenerado, medida del viewport, familia ausente tratada como interrupción | cada corrida real, `ym-2026-run{2..6}`, `ym-2023-run{1,2}` | `ym-2026-run6` y `ym-2023-run2` en verde |

## Pruebas ejecutadas

| Capa | Fecha | Commit | Revit | Resultado | Evidencia |
| --- | --- | --- | --- | --- | --- |
| Seguridad del conductor (sin Revit) | 09-09 | `403f7b6` | — | **136/0/0** | salida del arnés |
| Reproducción de defectos | 09-09 | contra `318a821` | — | **8/8 reproducidos** | `repro-defects-at-318a821.log` |
| Core | 09-09 | `403f7b6` | — | **3.882/0/0** | — |
| Server | 09-09 | `403f7b6` | — | **477/0** | — |
| Inventario / consistencia pública / escaneo sensible | 09-09 | `403f7b6` | — | verde | — |
| Matriz por año | 09-09 | `4e1f4d0` | 2026 (pid 47704) | **green** — 6 medidas, 1 no cubierta | `ym-2026-run6/` |
| Matriz por año | 09-09 | `403f7b6` | 2023 (pid 42176) | **green** — 5 medidas, 1 `fixture_missing`, 1 no cubierta | `ym-2023-run2/` |
| Revisión visual de páginas renderizadas | 09-09 | `4e1f4d0` / `403f7b6` | 2026, 2023 | aceptada | `visual-review.md`, `visual-run6/`, `visual-2023/` |

No se repitió la matriz completa de cinco años: el producto no cambió
(`git diff 7fe8693..403f7b6 -- src/` vacío), y lo que cambió es el conductor —
que se prueba ejecutándolo de verdad, en dos años distintos, incluido uno con el
modal ajeno de arranque. Las medidas de producto del 08-09 siguen siendo las
válidas.

## Estado final del entorno

- Git: rama `codex/bim-production-product-layer`, HEAD `403f7b6`, árbol limpio,
  ocho commits locales. **Sin push.**
- Manifiestos: los cinco años con `Horizun.addin` apuntando a
  `Horizun\Horizun.Revit.dll`, sin manifiesto de desarrollo ni copia aparte; las
  cinco DLL instaladas con las mismas huellas que al empezar; servidor instalado
  intacto.
- Procesos: solo el Revit 2025 del usuario, anterior a esta sesión. Ninguna
  sesión de ensayo quedó abierta.
- Recuperaciones pendientes: ninguna. La única que hubo se resolvió y quedó
  documentada (`ym-2026/resolution.md`).
- **No se ejecutó `install.ps1`, no hubo release, no hubo push**, no se
  reconfiguró ningún cliente MCP, no se tocaron certificados ni el permiso de
  Python, y no se sincronizó nada con CORE.

## Pendientes

**Defectos reales conocidos**: ninguno abierto de esta sesión.

**Cobertura pendiente** (procedimientos preparados en
[PENDING-LIVE-PROCEDURES-2026-09-09.md](PENDING-LIVE-PROCEDURES-2026-09-09.md))

1. **Familia de etiqueta para 2023–2025.** Bloqueo medido: el fixture de 2023
   tiene cero familias de etiqueta capaces de etiquetar un muro; la RFA de 2026 no
   abre hacia atrás; extraerla de la plantilla exige `execute_python`, que el
   dueño tiene deshabilitado; y la API no permite crear un label dentro de una
   familia de anotación. Necesita una acción humana.
2. **Revit 2025 y 2024/2027 con el conductor corregido.** 2025 es la sesión del
   usuario y no se tocó; 2024 y 2027 no se midieron en esta sesión porque el
   producto no cambió y el conductor ya quedó demostrado en dos años.
3. **Documento ajeno REAL.** La ruta «documento que la corrida no abrió → dejar
   corriendo» sigue probada por simulación (siete casos) y no por un documento
   ajeno real, que no ocurrió.

**Publicación (fuera de este alcance)**: `install.ps1` en entorno seguro y
`verify-live.ps1 -ReleaseGate` con las huellas del manifiesto.

## Adenda del 2026-09-09 (tarde): trabajo offline, con Revit ocupado

### Lo que «green» NO quiere decir

La fila **2023** quedó en `green` **con una sonda `fixture_missing`**. Son cosas
distintas y el informe las separa a propósito:

| | Revit 2026 | Revit 2023 |
| --- | --- | --- |
| **Mediciones automáticas aprobadas** | 6 sondas PASS | 5 sondas PASS |
| **No medido** (fixture ausente, jamás «aprobado») | — | **`tag-with-leader`** |
| **Revisión visual** (juicio, nunca puntuado por el arnés) | aceptada: cota desplazada + etiqueta con líder | aceptada **solo para la cota**; esa página no lleva etiqueta |
| **Pendiente en vivo** | documento ajeno real | etiquetas 2023–2025 |

El estado del año resume el código de salida del arnés; el artefacto del arnés
es lo que dice qué se midió. **La cobertura de etiquetas fuera de 2026 sigue
abierta.** Queda registrada, sin aplicar, la observación de que `Complete-HzRun`
traduce hoy a salida 0 un run con sondas `fixture_missing`: cambiarlo afectaría a
todos los arneses del repositorio y es una decisión del dueño.

### Búsqueda de familia de etiqueta compatible con 2023: sin candidatas

Leyendo el `BasicFileInfo` de cada archivo, sin abrir Revit
(`scripts/rfa-provenance.py`, validado contra un caso
conocido): **2.241 familias** escaneadas, **411** con formato 2023, y **ninguna
es una etiqueta** — el único nombre con «tag» es un símbolo de corte italiano.
Las anotaciones de Structural Precast existen solo desde formato 2024. Inventario
y rutas en `artifacts/stabilization-2026-09-09/rfa-summary.txt` y
`rfa-inventory.json`.

A eso se suma un obstáculo de contrato: **ninguna herramienta tipada carga una
`.rfa` existente en un proyecto**. Por eso el procedimiento preparado necesita un
paso humano único.

### Aislamiento comprobado antes de correr nada

Auditoría estática del arnés (todo escribe bajo `%TEMP%`; cada llamada al helper
real corre con `%APPDATA%` redirigido) más una huella de lo real antes y después
(`scripts/live/isolation-guard.ps1`): **133 aprobadas, 0 fallidas, 3 omitidas** y las dos
huellas **idénticas** — manifiestos, DLL, servidor instalado, ajustes y procesos
Revit sin cambio, con el usuario trabajando en su Revit 2025. Las 3 omitidas
necesitan un proceso que la máquina llamaría «Revit» y se saltaron con
`-SkipProcessNamedRevit`; **omitidas no es aprobadas** (se midieron en verde por
la mañana).

### Preparado y no ejecutado

`docs/PENDING-LIVE-PROCEDURES-2026-09-09.md`: el procedimiento A (etiquetas
2023–2025, con un solo paso manual y tres corridas automáticas) y el B
(documento ajeno REAL, con su fixture desechable ya creado y su arnés escrito y
probado en su negativa, sin una sola llamada al puente).

---

# Continuación — 2026-09-09 (tarde): validación local en vivo

Producto sin cambios desde `7fe8693` (`git diff -- src/` vacío). Todo lo de esta
sesión es arnés, conductor y evidencia.

## Veredicto

**Validación local PENDIENTE por un solo motivo, nombrado**: los cinco casos de
Revit 2025. El usuario abrió Revit 2025 durante la sesión y sigue abierto; una
sesión ajena no se cierra ni se rodea. Todo lo demás está cubierto y medido:

- el **documento ajeno real** — la última ruta del conductor sin caso real —
  refutó el cierre como debía y se recuperó con el manifiesto verificado;
- la **familia de etiqueta** para 2023–2025 quedó resuelta por vía tipada, sin
  Python, sin permisos nuevos y sin ningún paso humano;
- **cuatro años** (2023, 2024, 2026, 2027) en verde con etiqueta con líder, cota
  desplazada, PDF exportado y página **mirada**;
- el evaluador estricto responde `complete` solo cuando todo lo obligatorio está
  cubierto, y hoy responde `PENDING` con esos cinco casos por nombre.

No es un release: no se instaló, no se publicó, no se hizo push.

## Matriz por año y capacidad

| Año | Conductor (aislar, abrir, registrar, cerrar, restaurar) | Etiqueta con líder | Cota desplazada | PDF verificado | Revisión visual |
| --- | --- | --- | --- | --- | --- |
| 2023 | **verde** | **PASS** | **PASS** | **PASS** | **aceptada** |
| 2024 | **verde** | **PASS** | **PASS** | **PASS** | **aceptada** |
| 2025 | **bloqueado** (sesión del usuario) | pendiente | pendiente | pendiente | pendiente |
| 2026 | **verde** | **PASS** | **PASS** | **PASS** | **aceptada** |
| 2027 | **verde** | **PASS** | **PASS** | **PASS** | **aceptada** |

`bloqueado` y `pendiente` no son aprobados. El caso del documento ajeno se
registra aparte, como **rechazo intencional satisfecho**: refutó, no cerró nada,
y su recuperación dejó el manifiesto verificado.

## Defectos encontrados y corregidos

| Defecto | Commit |
| --- | --- |
| Un tipo de cota no es una cota: los arneses preguntaban por instancias y rechazaban cualquier fixture limpia | `cd8af9e` |
| El `dimension_type_id` lo resuelve el producto; el arnés exigía un id que no necesitaba (`intent_dimension` en vez de `dimension_set`) | `5e7f036` |
| Un tipo de rótulo está cargado antes de que ninguna lámina lo use; el arnés pedía instancias | `0ccb764` |
| Una fixture de un año anterior se actualiza al abrirla en uno posterior y el puente lo exige por escrito: `-PrepareAllowUpgrade`, apagado por defecto | `0ccb764` |
| El procedimiento del documento ajeno usaba una fixture 2023 en una sesión 2026 — el puente se negó, con razón | copia 2026, `ym-2026-foreign2` |

Ninguno es del producto.

## Pruebas ejecutadas

| Capa | Commit | Revit | Resultado | Evidencia |
| --- | --- | --- | --- | --- |
| Seguridad del conductor | `0ccb764` | — | **136/0/0** | `session-tests-summary.json` |
| Evaluador del cierre | `0ccb764` | — | **18/0** | `verify-local-closure.tests.ps1` |
| Core | `0ccb764` | — | **3.882/0/0** | — |
| Server | `0ccb764` | — | **477/0** | — |
| Inventario, consistencia pública, escaneo sensible | `0ccb764` | — | verde | — |
| Compilación del add-in por año | `5e7f036`/`0ccb764` | 2023, 2024, 2026, 2027 | 0 errores, 0 advertencias en cada corrida | logs del conductor |
| Documento ajeno real + recuperación | `bfda438` | 2026 | **satisfecho** | `ym-2026-foreign2/` |
| Fixture con familia de etiqueta | `5e7f036` | 2023 | **PASS** | `ym-2023-tagbase3/` |
| Etiquetas, cota y PDF | `0ccb764` | 2023, 2024, 2026, 2027 | **6/0/1** por año (la no cubierta es la aceptación visual, que se hace mirando) | `ym-<año>-tags2/` |
| Revisión visual | — | 2023, 2024, 2026, 2027 | **aceptada** | `visual-acceptance.json`, `visual-<año>/` |
| Evaluación consolidada | `0ccb764` | — | **PENDING**: 5 casos de 2025 | `closure-result.json` |

## Estado final del entorno

`isolation-guard.ps1` contra la instantánea del inicio de la sesión: **la única
diferencia es el Revit 2025 del usuario** (pid 43640). Los cinco manifiestos
instalados, sus DLL, el servidor instalado y `settings.json` son idénticos byte a
byte. Ninguna sesión propia quedó abierta; ninguna recuperación pendiente.

## Pendientes

1. **Revit 2025** — cinco casos. Una sola corrida cuando la máquina esté libre.
2. Publicación: fuera de este alcance por decisión expresa.

---

# Continuación — 2026-09-09: Revit 2025, y el cierre

Producto sin cambios: `git diff 7fe8693..HEAD -- src/` sigue vacío.

## Veredicto

**Validación local completa en el alcance acordado.** El único motivo por el que
la revisión anterior decía «pendiente» — los cinco casos de Revit 2025, cuya
sesión era del usuario — está cerrado con la máquina libre. El evaluador estricto
responde `COMPLETE`.

Sigue sin ser una release: no se instaló, no se publicó, no se hizo push.

## Matriz por año y capacidad

| Año | Conductor | Etiqueta con líder | Cota desplazada | PDF verificado | Revisión visual |
| --- | --- | --- | --- | --- | --- |
| 2023 | **verde** | **PASS** | **PASS** | **PASS** | **aceptada** |
| 2024 | **verde** | **PASS** | **PASS** | **PASS** | **aceptada** |
| 2025 | **verde** | **PASS** | **PASS** | **PASS** | **aceptada** |
| 2026 | **verde** | **PASS** | — | — | **aceptada** |
| 2027 | **verde** | **PASS** | — | — | **aceptada** |

En 2026 y 2027 la cota y el PDF no están entre los casos obligatorios de este
cierre: el conductor los ejerció igual y salieron `passed`, pero lo que el
evaluador exige ahí es conductor, etiqueta y página. **No está aprobado lo que no
se pidió**; está medido.

Aparte, como clase propia: **rechazo intencional satisfecho** — el documento
ajeno real, refutado y recuperado con el manifiesto verificado.

## Pruebas ejecutadas

| Capa | Commit | Revit | Resultado | Evidencia |
| --- | --- | --- | --- | --- |
| Seguridad del conductor | `0ccb764` | — | **136/0/0** | `session-tests-summary.json` |
| Evaluador del cierre | `0ccb764` | — | **18/0** | `verify-local-closure.tests.ps1` |
| Core | `0ccb764` | — | **3.882/0/0** | — |
| Server | `0ccb764` | — | **477/0** | — |
| Inventario, consistencia pública, escaneo sensible | `0ccb764` | — | verde | — |
| Compilación del add-in por año | `5e7f036`/`0ccb764`/`192c8d2` | 2023, 2024, **2025**, 2026, 2027 | 0 errores, 0 advertencias | logs del conductor |
| Documento ajeno real + recuperación | `bfda438` | 2026 | **satisfecho** | `ym-2026-foreign2/` |
| Etiquetas, cota y PDF | `0ccb764` | 2023, 2024, 2026, 2027 | **6/0/1** por año | `ym-<año>-tags2/` |
| Etiquetas, cota y PDF | `192c8d2` | **2025** | **6/0/1** | `ym-2025-tags2/` |
| Revisión visual | — | 2023, 2024, **2025**, 2026, 2027 | **aceptada** | `visual-acceptance.json`, `visual-<año>/` |
| Evaluación consolidada | `192c8d2` | — | **COMPLETE** | `closure-result.json` |

## Lo que la revisión visual de 2025 destapó del propio registro

El nombre de hoja del arnés desborda el campo del rótulo de Autodesk y pisa sus
marcadores. Está en **las cinco** páginas con los mismos siete pares de palabras
en las mismas coordenadas, y las cuatro revisiones anteriores no lo nombraron.
Corregido en `visual-acceptance.json` para los cinco años, con el criterio
reescrito: la aceptación cubre el contenido anotado; el defecto del rótulo queda
**nombrado, no dispensado**. No es del producto y no se le añade nada por él.

## Estado final del entorno

`isolation-guard.ps1` contra la instantánea del inicio de la sesión:
**idéntico, sin salvedades**. Cero procesos de Revit. Los cinco manifiestos, sus
DLL, el servidor instalado y `settings.json`, byte a byte. Ninguna sesión propia
abierta; ninguna recuperación pendiente.

## Pendientes

Ninguno dentro de este alcance. La publicación queda fuera por decisión expresa.

---

# Continuación — 2026-09-09: la presentación del plano

Producto sin cambios: `git diff 7fe8693..HEAD -- src/` sigue vacío.

## Veredicto

**Validación local completa en el alcance acordado**, ahora también en la
presentación. El criterio original —ausencia de superposiciones evidentes en la
hoja— se cumple **y se mide**, en vez de reinterpretarse.

## Matriz por año y capacidad

| Año | Conductor | Etiqueta+líder | Cota desplazada | PDF | Hoja completa | Detalle | Superposiciones |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2023 | verde | PASS | PASS | PASS | mirada | mirado | **0** |
| 2024 | verde | PASS | PASS | PASS | mirada | mirado | **0** |
| 2025 | verde | PASS | PASS | PASS | mirada | mirado | **0** |
| 2026 | verde | PASS | *medido, no exigido* | *medido, no exigido* | mirada | mirado | **0** |
| 2027 | verde | PASS | *medido, no exigido* | *medido, no exigido* | mirada | mirado | **0** |

Aparte, clase propia: **rechazo intencional satisfecho** (documento ajeno real,
refutado y recuperado).

## Defectos de presentación y commits

| Defecto | Dónde | Commit |
| --- | --- | --- |
| Una frase de 49 caracteres en el campo de nombre de hoja: envuelve a 5 líneas y se imprime sobre «Project Name» y «Project Number», en los 5 años | `verify-deliverable-visual.ps1` | `13ffaf8` |
| «Sin superposiciones» no se medía: solo se comprobaba que ninguna palabra saliera del papel, que es otra pregunta | `render-pdf.py`, `verify-local-closure.ps1` | `13ffaf8` |
| El primer arreglo movió la frase al título de vista, que envuelve a ~96 mm y deja su segunda línea sobre la escala: 3 colisiones nuevas en los 5 años | `verify-deliverable-visual.ps1` | `bd9917f` |

Ninguno es del producto: los tres son del arnés y de cómo se verificaba.

## Pruebas repetidas

Solo las afectadas, según lo pedido — no la matriz funcional completa ni el
escaneo interrumpido.

| Capa | Commit | Resultado |
| --- | --- | --- |
| Seguridad del conductor | `bd9917f` | **136/0/0** |
| Evaluador del cierre (con 3 regresiones nuevas) | `bd9917f` | **21/0** |
| Etiquetas, cota y PDF, 5 años | `bd9917f` | **6/0/1** por año |
| Revisión visual, hoja completa y detalle | — | **aceptada** en los 5 |
| Evaluación consolidada | `bd9917f` | **COMPLETE** |

## Estado final del entorno

`isolation-guard.ps1` contra la instantánea del inicio: **idéntico, sin
salvedades**. Cero procesos de Revit. Los cinco manifiestos, sus DLL, el servidor
instalado y `settings.json`, byte a byte. Ninguna sesión propia abierta; ninguna
recuperación pendiente.
