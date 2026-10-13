# Horizun Revit MCP 3.0.0 — release notes

## English

Horizun Revit MCP 3.0.0 brings the work previously developed as the unpublished 2.1.7, 2.2.0 and 2.3.0 candidates into one major release. The complete contract has **125 tools and 481 named suboperations or dispatch modes**. A client sees the subset allowed by its permissions and toolsets.

### Highlights

- **Reinforcement:** typed rules cover host crossings, anchorage, laps, sections, slabs, foundations, walls, columns, couplers and declared crossing order. The plan checks the displaced result, the apply rereads committed geometry, and the audit reports differences. `horizun_read_schedule` reads vector PDF or DWG schedule cards and refuses ambiguous rows. PdfPig 0.1.13 is included for vector PDF reading.
- **Cross-app exchange:** `horizun_exchange` emits and checks `horizun.exchange/0` manifests, model identity and review correction plans. A manifest reports the evidence of a handoff; it does not certify work performed by another application.
- **Schema on demand:** `full` remains the default. The optional `HORIZUN_SCHEMA_MODE=facade` exposes health plus three server-side tools to list, describe and call reachable typed tools. The contract, permission profile and toolset checks still apply. The smaller `tools/list` was measured; agent task completion has not yet been compared in an authenticated battery, so the facade stays opt-in.
- **Reliability:** the release includes targeted server queue, cancellation, path validation, client registration, model audit and installation fixes. The operations pane opens only on request.
- **Field fixes:** IronPython dependencies load in an isolated runtime and health distinguishes access, load failures, and cold or warm state. CAD extraction offers a paged detail plan with measured coverage. Group operations detect duplicates, preview dependent annotations by view, keep position across level changes, recreate equivalent tags on type changes, and copy rotated or mirrored patterns. Linked view analysis reports candidates and uncertainty without the unsafe collector overload; job status distinguishes a dead Revit process and recent crash dumps.

### Updating from 2.1.x

Bridge protocol **3** is incompatible with protocol 2. Close Revit and update the server and every installed add-in together with the installer. Restart the MCP client, start Revit, and check that `horizun_health` reports `healthy` with matching version, commit and contract hash. Review renamed fields and changed responses in [MIGRATION-3.0.md](../MIGRATION-3.0.md). A contract hash mismatch means the two halves differ; close Revit and reinstall the matching pair.

The published release installer carries its own runtime; a .NET SDK is only needed for a source build. Release assets are unsigned; use the published SHA-256 and manifest to verify what you install. Local Python access remains owner controlled and disabled by default.

### Evidence and limits

The [tool inventory](../inventory.json) counts the compiled contract, not proven model outcomes. An imported CAD without a readable source DWG cannot prove original text or hatch coverage; the detail plan reports gaps and never claims complete conversion. Linked element candidates do not prove final view visibility because crop, phases, design options, and link display settings remain unresolved. Group dimension-loss evidence and the scope of the session annotation report remain limited. Offline Core and Server gates and synthetic live probes are separate evidence. The facade versus full agent battery remains unmeasured (0/18 tasks in either mode): Claude had no quota and an isolated Codex session could not authenticate. The default therefore remains `full`. See [agent quality](calidad-agente-3.0.md) and the [steel capability matrix](acero-3.0-matriz.md). Rotated plan tag overlap remains conservative, and some lower priority backlog work is deferred to 3.1.

## Español

Horizun Revit MCP 3.0.0 reúne en una versión mayor el trabajo de los candidatos 2.1.7, 2.2.0 y 2.3.0 que no se publicaron. El contrato completo tiene **125 herramientas y 481 suboperaciones o modos de despacho nombrados**. Cada cliente ve el subconjunto permitido por sus permisos y toolsets.

### Novedades

- **Acero:** las reglas tipadas cubren cruces entre anfitriones, anclajes, traslapos, secciones, losas, cimentaciones, muros, columnas, acoples y orden declarado de cruces. El plan comprueba el resultado desplazado, la aplicación relee la geometría confirmada y la auditoría informa diferencias. `horizun_read_schedule` lee cartillas vectoriales PDF o DWG y rechaza filas ambiguas. Se incluye PdfPig 0.1.13 para leer PDF vectoriales.
- **Intercambio entre aplicaciones:** `horizun_exchange` emite y comprueba manifiestos `horizun.exchange/0`, identidad de modelo y planes de corrección de revisiones. El manifiesto informa la evidencia del intercambio; no certifica lo que hizo otra aplicación.
- **Esquemas a petición:** `full` sigue predeterminado. `HORIZUN_SCHEMA_MODE=facade` es opcional y muestra health junto con tres herramientas del servidor para listar, describir y llamar herramientas tipadas disponibles. Se mantienen los controles de contrato, permisos y toolsets. Se midió la reducción de `tools/list`, pero falta comparar tareas completadas en una batería autenticada; por eso la fachada sigue siendo opcional.
- **Fiabilidad:** se incluyen correcciones de cola, cancelación, rutas locales, registro de clientes, auditoría e instalación. La ventana de operaciones solo se abre a petición.
- **Defectos de campo:** las dependencias de IronPython se cargan aisladas y health distingue acceso, fallo de carga y estado frío o caliente. La extracción CAD ofrece un plan de detalle paginado con cobertura medida. Los grupos detectan duplicados, anticipan anotaciones por vista, conservan posición al cambiar de nivel, reponen etiquetas equivalentes y copian patrones rotados o espejados. La lectura de vínculos comunica candidatos e incertidumbre sin la sobrecarga insegura del collector; job status diferencia un Revit caído y volcados recientes.

### Actualización desde 2.1.x

El protocolo de puente **3** es incompatible con el 2. Cierra Revit y actualiza juntos el servidor y todos los add-ins instalados con el instalador. Reinicia el cliente MCP, abre Revit y comprueba que `horizun_health` responda `healthy` con versión, commit y hash de contrato coincidentes. Consulta los nombres y respuestas cambiados en [MIGRATION-3.0.md](../MIGRATION-3.0.md). Si los hashes difieren, cierra Revit y reinstala el par correspondiente.

El instalador publicado trae su runtime; el SDK de .NET solo hace falta para compilar desde fuente. Los activos publicados no tienen firma pública: verifica el SHA-256 y el manifiesto antes de instalar. El acceso a Python sigue bajo control del dueño y deshabilitado por defecto.

### Evidencia y límites

El [inventario](../inventory.json) cuenta el contrato compilado, no resultados probados en modelos. Un CAD importado sin DWG fuente legible no permite demostrar cobertura de textos ni sombreados originales; el plan informa las brechas y no afirma conversión completa. Los candidatos del vínculo no prueban visibilidad final porque faltan recorte, fases, opciones y ajustes de visualización del vínculo. Quedan limitadas la evidencia viva sobre cotas perdidas y la cobertura del informe de anotaciones de sesión. Los gates Core y Server y las sondas sintéticas en Revit son evidencias distintas. La batería de agentes fachada frente a full no tiene medidas (0/18 tareas en cada modo): Claude no tenía cuota y una sesión aislada de Codex no pudo autenticarse. Por eso `full` sigue predeterminado. Véanse [calidad del agente](calidad-agente-3.0.md) y la [matriz de acero](acero-3.0-matriz.md). La detección de traslapo de etiquetas en planos rotados sigue siendo conservadora; parte del backlog de menor prioridad queda para 3.1.
