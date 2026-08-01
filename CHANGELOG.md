# Changelog

What changed, and — where it matters — what was actually measured rather than
assumed. Dates are the day the work landed.

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
  o nivel como `(blank)`. Aunque una clave JSON vacía es válida, clientes reales
  como Windows PowerShell 5.1 no pueden materializarla y descartaban la respuesta
  completa. La prueba viva consulta ahora todas las categorías para cubrir este
  caso con datos reales.

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

## Unreleased

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
