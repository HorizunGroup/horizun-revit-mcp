# Calidad del agente — 3.0.0

Fases 4.1–4.2: corpus de 18 tareas y harness Claude headless disponibles.
Fase 4.3: **0 de 18 tareas ejecutadas en cada modo**. Claude no tenía cuota. Un intento de usar Codex con `CODEX_HOME` temporal y sin copiar autenticación del dueño terminó en HTTP 401 antes de iniciar la primera tarea. No hay medidas comparables de completitud, llamadas, rechazos de contrato ni tiempo.

El plan interno de evaluación define prompts, fixtures,
criterios mecánicos, llamadas y rechazos aceptables, incluidos los tres STEEL.
`scripts/eval/run-agent-battery.ps1` compara `facade` y `full` mediante una
factoría de tareas de la sesión desechable, con un `Check` por tarea que lee
las respuestas del puente. Configuración MCP temporal y capturas bajo `%TEMP%`;
no se modifica la configuración del dueño. Cualquier Revit no declarado en
`-AllowRevitPid` impide ejecutar. El harness no inicia ni cierra Revit.

Validación offline: `scripts/eval/run-agent-battery.tests.ps1`, compatible con
PowerShell 7 y Windows PowerShell 5.1. Las fixtures NDJSON tienen envolturas
Claude reales y contenidos sintéticos; ninguna prueba usa Revit o MCP.

## Resultados de la batería agente

Registrar commit/servidor, contrato, versión CLI y modelo Claude, año Revit,
hashes de fixtures y factoría de checks, prompts, tres repeticiones por tarea,
capturas originales y rutas de los resúmenes. Preparar una sesión 2023 para la
tarea de identidad; informar cualquier tarea no ejecutada, sin quitarla del
denominador. Usar exactamente la misma batería en ambos modos.

| Modo | Tareas completadas / 18 | STEEL / 3 | Llamadas (mediana / peor) | Rechazos de contrato | Segundos (mediana / peor) | Tokens (si presentes) |
|---|---|---|---|---|---|---|
| facade | 0/18 medidas | 0/3 medidas | Sin datos | Sin datos | Sin datos | Sin datos |
| full | 0/18 medidas | 0/3 medidas | Sin datos | Sin datos | Sin datos | Sin datos |

El detalle conserva las 18 tareas en el denominador. En cada celda `—`
significa **no ejecutada en ninguno de los dos modos**, no cero llamadas,
rechazos o segundos.

| Tarea | Completada (`full` / `facade`) | Llamadas (`full` / `facade`) | Rechazos de contrato (`full` / `facade`) | Tiempo s (`full` / `facade`) |
|---|---|---|---|---|
| 01 Documento activo | — / — | — / — | — / — | — / — |
| 02 Conteo por nivel | — / — | — / — | — / — | — / — |
| 03 Cantidad de ductos | — / — | — / — | — / — | — / — |
| 04 Cobertura de worksets | — / — | — / — | — / — | — / — |
| 05 Elemento vinculado | — / — | — / — | — / — | — / — |
| 06 Identidad en Revit 2023 | — / — | — / — | — / — | — / — |
| 07 Parámetro ensayado | — / — | — / — | — / — | — / — |
| 08 Crear muro | — / — | — / — | — / — | — / — |
| 09 Mover muro | — / — | — / — | — / — | — / — |
| 10 Borrar con dependientes | — / — | — / — | — / — | — / — |
| 11 Rechazo de documento | — / — | — / — | — / — | — / — |
| 12 Lote de columnas | — / — | — / — | — / — | — / — |
| 13 Corrección de fila inválida | — / — | — / — | — / — | — / — |
| 14 Trabajo largo | — / — | — / — | — / — | — / — |
| 15 Deshacer escritura propia | — / — | — / — | — / — | — / — |
| 16 STEEL cartilla sintética | — / — | — / — | — / — | — / — |
| 17 STEEL marco | — / — | — / — | — / — | — / — |
| 18 STEEL auditoría | — / — | — / — | — / — | — / — |

`Completed` exige evidencia mecánica y un stream terminado correctamente;
no verifica por sí solo la fidelidad de la narración. Conservar también tareas
fallidas, crashes y timeouts. Un timeout detiene la batería hasta comprobar el
trabajo pendiente y restaurar el fixture. No se declara un modo superior ni se recomienda un modelo con estos datos. La decisión conservadora para 3.0.0 es **`full` por defecto** y `facade` opcional mediante `HORIZUN_SCHEMA_MODE=facade`. La alternativa de publicar `facade` por defecto se descarta porque reducir los bytes de `tools/list` no demuestra mejor completitud del agente. La comparación real queda pendiente para 3.1 con un cliente autenticado y fixtures desechables.
