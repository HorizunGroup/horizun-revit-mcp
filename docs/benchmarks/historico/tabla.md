# Evolución histórica del MCP de Revit

La métrica principal ahora es **operaciones completas en Revit**: ver `operaciones.md`. Esta tabla queda como detalle por tarea.

| Versión | Fecha | Total | Infraestructura | Documentación (original) | Documentación (corregido) | Cantidades | Arquitectura | Familias | DWG/IFC/nube | MEP | Estructura (original) | Estructura (corregido) | Vínculos | Confiabilidad | Tareas «no existía» |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| v1.0.0 | 2026-08-25 | 21,4 % | 51,9 % | 2,9 % | 2,9 % | 59,1 % | 13,8 % | 18,8 % | 6,2 % | 7,1 % | 0,0 % | 0,0 % | 0,0 % | 50,0 % | 49 |
| v2.0.0 | 2026-09-20 | 40,3 % | 59,3 % | 35,3 % | 35,3 % | 68,2 % | 34,5 % | 37,5 % | 18,8 % | 21,4 % | 35,7 % | 28,6 % | 28,6 % | 60,0 % | 37 |
| v3.0.0 | 2026-10-13 | 54,1 % | 77,8 % | 38,2 % | 38,2 % | 77,3 % | 51,7 % | 37,5 % | 68,8 % | 21,4 % | 78,6 % | 35,7 % | 64,3 % | 60,0 % | 30 |
| v3.0.6 | 2026-10-22 | 54,1 % | 77,8 % | 38,2 % | 38,2 % | 77,3 % | 51,7 % | 37,5 % | 68,8 % | 21,4 % | 78,6 % | 35,7 % | 64,3 % | 60,0 % | 30 |
| v4.0.0 | 2026-11-01 | 54,1 % | 77,8 % | 38,2 % | 38,2 % | 77,3 % | 51,7 % | 37,5 % | 68,8 % | 21,4 % | 78,6 % | 35,7 % | 64,3 % | 60,0 % | 30 |
| v4.0.1 | 2026-11-14 | 53,6 % | 74,1 % | 38,2 % | 38,2 % | 77,3 % | 51,7 % | 37,5 % | 68,8 % | 21,4 % | 78,6 % | 35,7 % | 64,3 % | 60,0 % | 30 |
| 5.0.0 | 2026-11-06 | 58,2 % | 92,6 % | 52,9 % | 52,9 % | 95,5 % | 17,2 % | 62,5 % | 75,0 % | 35,7 % | 78,6 % | 28,6 % | 57,1 % | 60,0 % | 0 |
| 5.0.5 | 2026-11-13 | 69,9 % | 92,6 % | 58,8 % | 58,8 % | 95,5 % | 75,9 % | 62,5 % | 81,2 % | 35,7 % | 85,7 % | 35,7 % | 64,3 % | 70,0 % | 0 |
| 5.1.0 | 2026-11-14 | 70,9 % | 96,3 % | 58,8 % | 58,8 % | 95,5 % | 79,3 % | 62,5 % | 81,2 % | 35,7 % | 85,7 % | 35,7 % | 64,3 % | 70,0 % | 0 |
| 0587ab68 | 2026-11-19 | 91,3 % | 100,0 % | 76,5 % | 76,5 % | 100,0 % | 89,7 % | 100,0 % | 93,8 % | 92,9 % | 35,7 % | 100,0 % | 71,4 % | 100,0 % | 0 |
| 8b83c2ec | 2026-11-19 | 93,4 % | 100,0 % | 82,4 % | 82,4 % | 100,0 % | 89,7 % | 100,0 % | 93,8 % | 92,9 % | 35,7 % | 100,0 % | 85,7 % | 100,0 % | 0 |
| 6.0.0 | 2026-11-21 | 96,4 % | 92,6 % | 100,0 % | 100,0 % | 100,0 % | 93,1 % | 100,0 % | 93,8 % | 92,9 % | 35,7 % | 100,0 % | 92,9 % | 100,0 % | 0 |

El total pasó de 21,4 % en v1.0.0 a 96,4 % en 6.0.0. El mayor salto fue de 5.1.0 a 0587ab68, con 20,4 puntos porcentuales más. En 5.0.0, Arquitectura cayó de 51,7 % a 17,2 %.

## Filas del 21 y 22-nov (v4.0.1, 6.0.0 y la nueva medición de v1.0.0 y 5.0.5)

- v4.0.1 es la versión pública (commit f0384484, fecha del commit). Va después de v4.0.0 por orden de versión, no por fecha.
- Las dos se midieron el 2026-11-21 en Revit 2025, con la misma preparación prep-8b83c2ec y copias nuevas.
- INF-01 falla en las dos por el entorno, no por la versión: el banco exige que Python no esté autorizado, y en este equipo hoy sí lo está. La v4.0.1 medida el 19-nov la aprobó; esa es toda la diferencia con su medición anterior (106 → 105 tareas).
- 5.0.5 se volvió a medir el 2026-11-22 con un solo Revit abierto: en la corrida del 19-nov había dos Revit y 11 escrituras de Arquitectura se negaron con documento_objetivo_ambiguo (65,8 % → 69,9 %; Infraestructura baja por INF-01, igual que en las otras corridas del 21-22 nov). v1.0.0 se volvió a medir y dio lo mismo.
- 6.0.0 falla además INF-27: el resumen de model_scan recorta el inventario de categorías y no muestra la fila de muros.

## Cómo se calcularon los puntajes

- El total divide las tareas aprobadas entre las 196 tareas del banco. En Documentación y Estructura se usa el criterio corregido para el total. Cada variante corregida reemplaza a su tarea original. Confiabilidad se mide con sus 10 tareas.
- Todas las versiones se midieron sobre la misma preparación, identificada como prep-8b83c2ec. Las fechas corresponden a versiones.txt.
- Cada fila marcada «no existía» aporta cero en el criterio al que pertenece. La última columna cuenta todas las filas con esa causa, incluidas las variantes corregidas cuando tienen esa marca.
- v1.0.0 registra 49 tareas con esa causa. De ellas, 16 mencionan horizun_manage_links. Las demás corresponden a otras herramientas.

## Comparación con los resúmenes

- Los recálculos coinciden con los resúmenes por criterio de la tabla del JSON. Los subtotales del bloque de filas agrupan ambos criterios en las diez versiones. Documentación contiene 35 filas y usa 34 por criterio. Estructura contiene 23 filas y usa 14 por criterio. Por ejemplo, en 8b83c2ec esos subtotales mezclados son 29/35 (82,9 %) y 14/23 (60,9 %), respectivamente.
- Los puntajes de Documentación coinciden entre criterios en las diez versiones. Sus curvas se superponen.
