# Gráfica histórica del MCP de Revit

Las 12 versiones (de v1.0.0 a 6.0.0) se midieron en Revit 2025 sobre una sola preparación del banco, hecha con la versión actual
(`prep-8b83c2ec`, huella de `preparacion.json` b46dcc4d…). Cada versión solo midió sobre copias nuevas de esa preparación. Una herramienta
que no existía en una versión cuenta 0 solo en sus tareas («no existía»).

- `grafica.png`: total y todas las categorías (original y corregido donde aplica).
- `grafica-sencilla.png`: **operaciones completas en Revit (%)** por versión publicada, con la Community v4.0.1 y la PRO 6.0.0 resaltadas. Definición y cifras en `operaciones.md`, datos en `operaciones.csv`, cálculo en `operaciones.py`.
- `tabla.md` y `puntajes.csv`: cifras por versión. Dos auditores independientes recalcularon por tarea (96 y 95).
- `rarezas.md`: por qué cae Estructura (original) en 0587ab68 y Arquitectura en 5.0.0.

v3.0.0, v3.0.6 y v4.0.0 dan cifras idénticas: cada corrida cargó su propio add-in (health respondió f2351f07, 000f262c y 00fe15b5),
pero las tres comparten el mismo hash de contrato (e44f15ba2c5bbc64f7555ae8).
