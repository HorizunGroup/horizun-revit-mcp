# Operaciones completas en Revit

Métrica principal desde el 2026-11-22. Una **operación** es un encargo de cliente de punta a punta. Cuenta como **completa** solo si todas sus tareas se aprobaron en vivo en Revit 2025, sin ayuda humana ni Python de escape, y cada una verificó su resultado releyendo el modelo o el archivo. Basta que falle un paso para que la operación no cuente.

| Versión | Fecha | Operaciones completas (ponderado) | Completas |
| --- | --- | ---: | ---: |
| v1.0.0 | 2026-08-25 | 0,0 % | 0 de 16 |
| v2.0.0 | 2026-09-20 | 3,4 % | 1 de 16 |
| v3.0.0 | 2026-10-13 | 13,8 % | 3 de 16 |
| v3.0.6 | 2026-10-22 | 13,8 % | 3 de 16 |
| v4.0.0 | 2026-11-01 | 13,8 % | 3 de 16 |
| v4.0.1 | 2026-11-14 | 13,8 % | 3 de 16 |
| 5.0.5 | 2026-11-13 | 44,8 % | 7 de 16 |
| 6.0.0 | 2026-11-21 | 89,7 % | 14 de 16 |

## Las 16 operaciones

Peso: 3 = el cliente lo pide cada semana; 2 = una vez por proyecto; 1 = ocasional. Es el criterio de Horizun, no un dato medido de clientes. El porcentaje es el peso de las operaciones completas sobre el peso total (29).

| Operación | Peso | Tareas del banco que la forman | v1.0.0 | v2.0.0 | v3.0.0 | v3.0.6 | v4.0.0 | v4.0.1 | 5.0.5 | 6.0.0 |
| --- | ---: | --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Modelar arquitectura: muros, puertas, ventanas, pisos, cielos, cubierta y recintos | 3 | ARQ-01, ARQ-02, ARQ-03, ARQ-04, ARQ-05, ARQ-06, ARQ-12, ARQ-13 | no | no | no | no | no | no | sí | sí |
| Corregir arquitectura existente: mover, rotar, cambiar tipo, alargar y borrar verificado | 2 | ARQ-10, ARQ-11, ARQ-16, ARQ-17, ARQ-18, ARQ-19, ARQ-27, ARQ-28 | no | no | no | no | no | no | sí | sí |
| Acabados de muros y pisos por recinto | 1 | ARQ-07, ARQ-08, ARQ-09, ARQ-14, CAN-14 | no | no | no | no | no | no | no | sí |
| Set de planos: plantas y cortes en hojas, etiquetas y cotas, exportado a PDF y revisado | 3 | DOC-01, DOC-02, DOC-03, DOC-04, DOC-06, DOC-08, DOC-09, DOC-10, DOC-11 | no | no | no | no | no | no | no | sí |
| Anotar y emitir revisiones: notas, nubes, revisiones y cotas verificadas en el PDF | 2 | DOC-07, DOC-12, DOC-13, DOC-18, DOC-21, DOC-25, DOC-26 | no | no | no | no | no | no | no | sí |
| Cantidades del modelo exportadas a tabla (CSV) y cuadradas con el modelo | 3 | CAN-01, CAN-03, CAN-05, CAN-06, CAN-10, CAN-13 | no | no | no | no | no | no | sí | sí |
| Tablas de planificación: leer las existentes y crear puertas, ventanas y materiales | 1 | CAN-07, CAN-08, CAN-09, CAN-11, CAN-12 | no | sí | sí | sí | sí | sí | sí | sí |
| Red sanitaria desde DWG: tubos con pendiente y todas las uniones conectadas, también las tees oblicuas, y guardado | 2 | MEP-01, MEP-02, MEP-03, MEP-04, MEP-05, MEP-06, MEP-07, MEP-08, MEP-09, MEP-10, MEP-12 | no | no | no | no | no | no | no | no |
| Familias: crear paramétricas y cargarlas al proyecto, y colocar una familia cargada desde RFA | 2 | FAM-01, FAM-02, FAM-05, FAM-08, FAM-16 | no | no | no | no | no | no | no | sí |
| Homologar una familia: parámetro compartido, guardar el RFA y recargarla | 1 | FAM-10, FAM-12, FAM-13 | no | no | no | no | no | no | no | sí |
| Estructura por ejes: columnas, vigas y refuerzo auditado | 2 | EST-01, EST-03, EST-04, EST-05c, EST-06c, EST-08c, EST-09c, EST-10c | no | no | no | no | no | no | no | sí |
| Entrega IFC4 verificada con su contenido | 2 | DIN-01, DIN-02 | no | no | sí | sí | sí | sí | sí | sí |
| Federar vínculos y encontrar interferencias | 2 | VIN-01, VIN-02, VIN-03, VIN-06, VIN-07, MEP-11 | no | no | no | no | no | no | no | sí |
| Exportar planos a DWG e imágenes | 1 | DIN-08, DIN-13, DIN-15, DOC-15 | no | no | no | no | no | no | sí | sí |
| Proyecto nuevo desde plantilla verificado, y cambio con guardado verificado en el modelo de trabajo | 1 | INF-14, INF-06, INF-18 | no | no | sí | sí | sí | sí | sí | sí |
| Nube de puntos E57 a muros | 1 | DIN-09, DIN-10 | no | no | no | no | no | no | no | no |

## Cómo se midió

- Las operaciones se arman con tareas del banco histórico (prep-8b83c2ec, Revit 2025). Cada tarea se ejecutó en vivo, por separado y sobre una copia nueva, y verificó su resultado releyendo el modelo o el archivo. **No es una corrida encadenada de encargos**: por ejemplo, en «Proyecto nuevo» el proyecto se crea y se verifica, y el cambio con guardado se hace sobre el modelo de trabajo.
- Solo entran las versiones publicadas: v1.0.0 a v4.0.1 como releases, 5.0.5 por la Suite y la PRO 6.0.0 oficial. 5.0.0 y 5.1.0 no salieron al público, y 0587ab68 y 8b83c2ec son commits, no versiones. El eje va en orden de versión: v4.0.1 (14-nov) va antes de 5.0.5 (13-nov).
- v1.0.0 y 5.0.5 se volvieron a medir el 2026-11-22 con un solo Revit abierto. En su corrida del 19-nov había dos Revit, y varias escrituras se negaron con documento_objetivo_ambiguo: eso era el entorno, no el producto.
- INF-01 (el banco exige que Python no esté autorizado en el equipo) no forma parte de ninguna operación, así que no mueve esta cifra.
- Estructura usa las variantes corregidas de las tareas (EST-05c a EST-10c), igual que el total de `tabla.md`.
- En 6.0.0 no se completan dos operaciones. «Red sanitaria desde DWG»: las 3 tees oblicuas siguen rechazadas también con la Y del fabricante (MEP-10). «Nube de puntos E57 a muros»: el plan sale bien, pero construir se niega porque, para un muro del plan (índice 54), ningún tipo de muro está a menos de 1 mm del grosor medido (DIN-10).
- Lo que el banco no cubre y por eso no está: auditoría con corrección aplicada de punta a punta y arquitectura generada desde un DWG. Quedan como operaciones por agregar.
- Las corridas marcan el commit con «-dirty» porque el árbol de medición tiene registros de compilación sin versionar; el código del producto es el del commit.
- Se regenera con `python operaciones.py <resultado.json>` y `python grafica_sencilla.py`.
