# Matriz de capacidades de acero — Horizun Revit MCP 3.0.0

Esta matriz describe la superficie de producto. Los resultados de aceptación corresponden al candidato 1c (`3192bf5a`); la validación final de 3.0.0 se registra por separado. Las reglas geométricas y tolerancias son argumentos del proyecto, no normas de una organización integradas en el puente.

| Área | Disponible en 3.0.0 | Límite o rechazo explícito |
|---|---|---|
| Geometría y anclaje | Barras por curvas, ganchos, `into` sobre la unión de sólidos, anclaje recto y con pata, arranques y continuidad | Se rechaza la barra que sale de la región declarada o no cumple la longitud de anclaje |
| Columnas y vigas | Capas, estribos por zona, confinamiento, crossties, bayoneta geométrica, sección circular y longitudinal por anillo | Secciones en L/T requieren reglas explícitas; no se infiere una descomposición |
| Cimentación | Zapata aislada, combinada y corrida; cabezal, viga de cimentación y pilote vertical | La canasta en U debe declararse como mallas por cara o barra explícita |
| Losas y muros | Mallas por cara, negativos, bandas, aberturas, losas inclinadas y de espesor variable, elementos de borde, arranques | Hosts no estructurales y recubrimientos imposibles se rechazan por nombre |
| Escaleras | Tramo vaciado en sitio y descansos; malla medida sobre la cara inclinada | Escaleras prefabricadas fuera del alcance de 3.0.0 |
| Sistemas Revit | Armadura por área y recorrido, malla fabric, couplers, aros y espirales por forma colocada | Los tipos necesarios deben existir en el documento; el puente no los inventa |
| Cruces y duplicados | `overlapping_bar`, `duplicate_bar`, holguras declaradas y `crossing_order` explícito; revisión `steel_review` del modelo | El puente no decide qué barra se mueve ni corrige silenciosamente un cruce |
| Verificación | El plan comprueba geometría antes de escribir; `apply` relee después del commit; auditoría compara geometría, recubrimiento y procedencia | Una barra movida por Revit se informa como desviación, no como verificada |
| Cantidades | Barras de área/recorrido y alambres de fabric; masa calculada con densidad declarada | La masa de Revit se muestra como referencia, sin mezclarla con el cálculo declarado |
| Cartillas | `horizun_read_schedule` propone reglas de PDF vectorial, DWG o primitivas, con autocomprobaciones | Lectura asistida: una tarjeta ambigua o una suma fallida queda rechazada para revisión humana |

## Compatibilidad de API

El add-in compila por separado contra Revit 2023–2027. La creación de barras con gancho usa la API propia de cada año. El traslapo y la bayoneta se expresan como geometría declarada para mantener el mismo contrato en los cinco años. La masa se calcula con volumen y densidad declarada. Un detalle de doblado nativo requiere Revit 2024 o posterior.

## Evidencia de aceptación sintética

En `3192bf5a`, la batería de acero ejecutó 22 sondas por año en Revit 2023–2027: **141 pases, 0 fallos y 0 no evaluables por año**. Incluyó un edificio sintético de aceptación, una prueba XL de 21 440 barras en 16 piezas y las sondas de contención, sistemas, duplicados y cruces. Las cifras describen ese commit; deben repetirse sobre el candidato final antes de publicar.

`horizun_query_structure` con `{"mode":"steel_review","tolerance_mm":2,"max_rows":500}` revisa el acero del documento activo sin un requirement set. El resultado `clean` exige cobertura completa y ausencia de hallazgos. El usuario debe interpretar los cruces conforme al diseño antes de declarar un modelo conforme.

## Pendientes declarados

- Mejorar patrones de cartilla y clasificación automática de cruces solo cuando la entrada sea inequívoca.
- Mantener escaleras prefabricadas fuera del alcance hasta disponer de un host y un contrato verificables.
- Repetir la matriz final en los cinco años sobre el commit exacto de publicación.
