# Dos rarezas de la gráfica histórica del MCP de Revit

## 1. Estructura en 0587ab68

**Veredicto.** El plan dejó de proponer vigas duplicadas y el criterio antiguo seguía exigiendo 16 vigas nuevas. Eso explica las siete caídas por tarea. El criterio corregido pasó de 5/14 a 14/14 y las columnas del CSV coinciden con los resultados por tarea.

| Serie | h-08 · 5.1.0 | h-09 · 0587ab68 |
| --- | ---: | ---: |
| Original | 12/14 · 85,7 % | 5/14 · 35,7 % |
| Corregido | 5/14 · 35,7 % | 14/14 · 100,0 % |

Reconté las 14 tareas sin sufijo para la serie original. En la corregida reemplacé nueve tareas por su variante con sufijo `c` y conservé EST-01 a EST-04 y EST-07. El recuento coincide con los dos resúmenes JSON y con `estructura_original_pct` y `estructura_corregido_pct` en `puntajes.csv`. El script de la gráfica histórica dibuja cada serie desde su campo correspondiente.

| Tarea original | h-08 | h-09 | Mensaje exacto si falló (h-09) | Clasificación |
| --- | --- | --- | --- | --- |
| EST-01 · Leer vigas y columnas | Aprobada | Aprobada | — | — |
| EST-02 · Cantidades de refuerzo | Aprobada | Aprobada | — | — |
| EST-03 · Planear columnas | Aprobada | Aprobada | — | — |
| EST-04 · Construir y volver a planear columnas | Aprobada | Aprobada | — | — |
| EST-05 · Planear 16 vigas y suprimir 11 | Aprobada | Fallida | `planeadas 7, suprimidas 20` | Omisión intencional de duplicados. El plan devolvió un resultado que el criterio antiguo no aceptaba. |
| EST-06 · Construir 16 vigas | Aprobada | Fallida | `depende de EST-05` | Bloqueada por el plan original. |
| EST-07 · Crear zapata corrida | Aprobada | Aprobada | — | — |
| EST-08 · Planear refuerzo por sección | Aprobada | Fallida | `depende de EST-06` | Bloqueada por la construcción original. |
| EST-09 · Construir tres juegos de refuerzo | Aprobada | Fallida | `depende de EST-08` | Bloqueada por el plan original de refuerzo. |
| EST-10 · Auditar refuerzo | Aprobada | Fallida | `depende de EST-06` | Bloqueada por la construcción original. |
| EST-11 · Comprobar rechazo de barra inexistente | Aprobada | Fallida | `depende de EST-06` | Bloqueada por la construcción original. |
| EST-12 · Comprobar rechazo de gancho inexistente | Aprobada | Fallida | `depende de EST-06` | Bloqueada por la construcción original. |
| EST-13 · Planear estribos sin `source` | Fallida (`entrada_invalida`) | Fallida | `depende de EST-06` | Falla previa. En h-08 faltaba un campo obligatorio. En h-09 el caso quedó bloqueado antes de probarlo. |
| EST-14 · Exigir rechazo de la regla V-TAB en columna | Fallida | Fallida | `armó una columna con una regla de viga` | Falla previa. El contrato admite una columna elegible seleccionada directamente. |

Las seis tareas bloqueadas entre las siete caídas no prueban fallas propias de sus herramientas. EST-14 tampoco explica la caída, pues ya fallaba en h-08. En ambos registros la herramienta aplicó la regla a la columna. El criterio corregido comprueba la aplicación y exige un aviso que identifique la columna.

| Variante corregida | h-08 | Mensaje exacto (h-08) | h-09 |
| --- | --- | --- | --- |
| EST-05c | Fallida | `viga 1372162 sin nivel de referencia medido` | Aprobada. Verificó huecos y omisiones con 7 vigas planeadas. |
| EST-06c | Fallida | `depende de EST-05c` | Aprobada. Construcción verificada, de 32 a 39 vigas y cero duplicadas en Nivel 2. |
| EST-08c | Fallida | `depende de EST-06c` | Aprobada. |
| EST-09c | Fallida | `depende de EST-08c` | Aprobada. |
| EST-10c | Fallida | `depende de EST-06c` | Aprobada. |
| EST-11c | Fallida | `depende de EST-06c` | Aprobada. |
| EST-12c | Fallida | `depende de EST-06c` | Aprobada. |
| EST-13c | Fallida | `depende de las vigas verificadas por EST-06c` | Aprobada. Rechazó duplicar refuerzo en una viga y planeó tres juegos en otra libre. |
| EST-14c | Fallida | `la aplicación verificada no trae un aviso de regla de viga para la columna 1370264` | Aprobada. Verificó la aplicación con aviso. |

Las cinco tareas compartidas aprobaron en ambas corridas. Los informes de h-08 y h-09 documentan que la meta antigua duplicaba vigas sobre el mismo eje. La variante corregida verifica el plan y su construcción en el modelo. La caída del original queda explicada por ese cambio y sus dependencias, sin evidencia de una regresión del MCP en esas siete tareas.

## 3. Arquitectura en 5.0.0 y 5.0.5

**Veredicto.** En 5.0.0 hubo una regresión observada al aplicar `horizun_create_elements`: las 13 tareas que cayeron registraron el mismo error al medir el solape piso–muro. En 5.0.5 se recuperaron siete y las otras seis quedaron bloqueadas por un documento objetivo ambiguo.

El error fue interno, al leer las caras de un sólido del par piso **1396341** y muro **1362523**. Varias tareas necesitaban crear elementos para poder probar su operación principal. Los registros muestran el paso que las bloqueó, pero no identifican el cambio de código que originó el error.

| Tarea | h-05 · v4.0.0 | h-06 · 5.0.0 | Mensaje exacto (h-06) | h-07 · 5.0.5 |
| --- | --- | --- | --- | --- |
| ARQ-01 · Crear muro recto | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Aprobada |
| ARQ-04 · Crear piso de 12 m² | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Aprobada |
| ARQ-06 · Crear habitación entre cuatro muros | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Aprobada |
| ARQ-08 · Separar las capas de un muro | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Aprobada |
| ARQ-10 · Mover un muro 500 mm | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Aprobada |
| ARQ-11 · Borrar un muro con `unique_id` | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Fallida. `horizun_delete_verified (ensayo): documento_objetivo_ambiguo` |
| ARQ-12 · Crear cubierta por huella | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Fallida. `horizun_create_elements (ensayo): documento_objetivo_ambiguo` |
| ARQ-16 · Mover tres muros 500 mm | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Fallida. `horizun_create_elements (ensayo): documento_objetivo_ambiguo` |
| ARQ-18 · Cambiar el tipo de un muro | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Fallida. `horizun_create_elements (ensayo): documento_objetivo_ambiguo` |
| ARQ-19 · Alargar un muro de 4 a 5 m | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Fallida. `horizun_create_elements (ensayo): documento_objetivo_ambiguo` |
| ARQ-21 · Agregar punto de 50 mm al piso | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Fallida. `horizun_create_elements (ensayo): documento_objetivo_ambiguo` |
| ARQ-26 · Ensayar y aplicar `revit_rollback` | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Aprobada |
| ARQ-27 · Comprobar borrado con `element_ids` sin confirmación | Aprobada | Fallida | `horizun_create_elements (aplicar): error_interno \| floor_wall_overlap_unmeasured: floor 1396341, wall 1362523: A solid's faces are unreadable.` | Aprobada |

El puntaje pasó de **15/29 (51,7 %)** a **5/29 (17,2 %)** y luego a **13/29 (44,8 %)**. De h-05 a h-06 se mantuvieron ARQ-05 y ARQ-17, y aprobaron por primera vez en este tramo ARQ-15, ARQ-25 y ARQ-28. De h-06 a h-07, además de las siete recuperaciones de la tabla, aprobaron ARQ-02, ARQ-03, ARQ-07 y ARQ-09, que ya fallaban en h-05. ARQ-15, ARQ-17 y ARQ-25 volvieron a fallar.

## Fuentes

- Resultados por tarea, causas y resúmenes en los resultados de la corrida histórica (fuera del repositorio). Para Estructura, `h-08-5.1.0` y `h-09-0587ab68`. Para Arquitectura, `h-05-v4.0.0`, `h-06-5.0.0` y `h-07-5.0.5`. Se consultaron los archivos `.json`, `.log` y las notas de criterio en los `.md`.
- `puntajes.csv` y `graficar.py` para comprobar las columnas y el trazado de la gráfica histórica.
