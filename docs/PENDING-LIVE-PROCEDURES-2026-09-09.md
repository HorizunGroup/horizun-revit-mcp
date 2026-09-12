# Procedimientos preparados — 2026-09-09

> **EJECUTADOS el 2026-09-09 por la tarde, salvo Revit 2025.** El documento ajeno
> real (B) se midió y se recuperó; la familia de etiqueta (A) NO necesitó el paso
> humano descrito abajo — se resolvió por vía tipada desde una plantilla de
> Autodesk, y las etiquetas pasan en 2023, 2024, 2026 y 2027. Lo único que sigue
> pendiente es Revit 2025, porque su sesión es del usuario. El resultado está en
> la sección «Sesión 2026-09-09 (tarde)» de la bitácora; esto se conserva como el
> plan que se siguió, con el paso humano ya innecesario.
>
> **Para cerrar 2025**, con Revit 2025 cerrado por su dueño:
> ```powershell
> & scripts/live/run-year-matrix.ps1 -Years 2025 `
>   -Harness 'verify-deliverable-visual.ps1 -Document {title} -Disposable yes-this-model-is-disposable -ExpectedAddinSha256 {addin_sha256}' `
>   -PrepareDocument '2025=C:\hz-live\HZ_TAGBASE_2025.rvt' -PrepareAllowUpgrade `
>   -ArtifactRoot <evidencia>\ym-2025-tags2
> ```
> Sale bien si la fila queda `green`, `tag-with-leader` en PASS, y la página
> renderizada muestra la cota 6000 desplazada y el líder llegando al muro.

Dos pruebas reales quedaron aplazadas porque el usuario tenía Revit ocupado.
Todo lo que se puede preparar sin tocar Revit está hecho; esto es lo que falta,
escrito para poder ejecutarse tal cual cuando la máquina esté libre.

Nada de este documento se ha ejecutado. Ninguno de los pasos arranca solo.

---

## A. Etiquetas en Revit 2023–2025

### Por qué hace falta un paso humano

Medido offline el 2026-09-09 (`artifacts/stabilization-2026-09-09/rfa-summary.txt`,
producido por `scripts/rfa-provenance.py`, que lee el `BasicFileInfo` de cada archivo sin
abrir Revit):

- **2.241 familias** `.rfa` en las bibliotecas de esta máquina; **411** con
  formato 2023, es decir abribles por Revit 2023.
- Entre esas 411, **ninguna es una etiqueta**. La única cuyo nombre contiene
  "tag" es italiana y es un símbolo de corte (`Solaio a nucleo cavo - Simbolo di
  taglio.rfa`: "taglio" = corte). Las familias de `Annotations` del contenido
  Structural Precast existen solo desde formato 2024 en adelante, y son
  anotaciones de prefabricado, no etiquetas de muro.
- `HZ_MULTICAT_TAG_2026.rfa` se reconfirmó como **formato 2026** leyendo el
  archivo: no abre en 2023, 2024 ni 2025.

Y hay un segundo obstáculo, de contrato: **ninguna herramienta tipada carga una
`.rfa` existente en un proyecto abierto**. `horizun_family_apply` homologa la
familia ACTIVA y declara explícitamente que nunca abre archivos;
`horizun_create_family` crea desde una plantilla y puede cargar *lo que ella
misma creó*, pero la API de Revit no permite crear una etiqueta (label) dentro de
una familia de anotación, así que no puede fabricar una etiqueta con texto.
`horizun_execute_python` sí podría hacer ambas cosas y está **deshabilitado por
el dueño de la máquina** (`enable_execute_python: false`), que es una decisión
que se respeta.

Conclusión: el paso humano es **uno solo**, y después todo es automático.

### Paso humano (una vez, en Revit 2023)

Con Revit 2023 abierto por ti:

1. Abre `C:\hz-live\HZ23_TAG.rvt` — ya preparado, desechable, formato 2023
   (copia de `HZ23_BASE.rvt`, hecha el 09-09;
   SHA-256 `4b2b4f618a2b40d9ef4373c7079528a46175c2ba3b84f1eb31f23465f30ee925`).
2. Carga en él una familia de etiqueta que pueda etiquetar un MURO. Dos rutas,
   cualquiera vale:
   - **Desde una plantilla de proyecto**: abre un proyecto nuevo con
     `Default_M_ENU.rte` (o el multidisciplinar métrico), selecciona la etiqueta
     `M_Multi Category Tag` en el navegador de proyecto, *Editar familia*,
     *Guardar como* → `C:\hz-live\fam\HZ_MULTICAT_TAG_2023.rfa`, y *Cargar en*
     `HZ23_TAG.rvt`.
   - **Desde la biblioteca de Autodesk**: *Insertar → Cargar familia de Autodesk*
     y toma `M_Multi-Category Tag` o `M_Wall Tag`.
3. **Guarda `HZ23_TAG.rvt`** y cierra Revit 2023.

Eso es todo lo que hace falta de tu parte.

### Pasos automáticos (los ejecuto yo cuando avises)

```powershell
# 1. Una copia INDEPENDIENTE por año: cada Revit actualiza la suya al abrirla, y
#    un fixture compartido dejaría de ser el mismo entre años.
Copy-Item C:\hz-live\HZ23_TAG.rvt C:\hz-live\HZ24_TAG.rvt
Copy-Item C:\hz-live\HZ23_TAG.rvt C:\hz-live\HZ25_TAG.rvt
Set-ItemProperty C:\hz-live\HZ24_TAG.rvt -Name IsReadOnly -Value $false
Set-ItemProperty C:\hz-live\HZ25_TAG.rvt -Name IsReadOnly -Value $false
Get-FileHash C:\hz-live\HZ2*_TAG.rvt | Format-Table Path, Hash
```

```powershell
# 2. Un año por corrida, con Revit de ESE año cerrado. El conductor compila,
#    aísla la sesión, abre el fixture, corre el arnés, cierra y restaura.
& scripts/live/run-year-matrix.ps1 -Years 2023 `
  -Harness 'verify-deliverable-visual.ps1 -Document {title} -Disposable yes-this-model-is-disposable -ExpectedAddinSha256 {addin_sha256}' `
  -PrepareDocument '2023=C:\hz-live\HZ23_TAG.rvt' `
  -ArtifactRoot <carpeta de evidencia>\ym-2023-tags
# ídem -Years 2024 con HZ24_TAG.rvt, y -Years 2025 con HZ25_TAG.rvt
```

```powershell
# 3. Mirar la página, que es lo que ningún arnés puntúa.
python scripts/render-pdf.py `
  <ruta del visual-review.pdf> <carpeta de imágenes> --dpi 150 --clip 95,60,190,140 --zoom-dpi 500
```

### Cómo se reconoce que salió bien

| Señal | Qué significa |
| --- | --- |
| `tag-with-leader` en **PASS** (hoy es `fixture_missing`) | la familia está cargada y el puente pudo etiquetar el muro con líder |
| `visual-request.json` con `tag_has_leader: true` y `tag_targets` = el id del muro | la etiqueta apunta al elemento correcto, releído del modelo |
| En la página renderizada: la caja de la etiqueta con su texto y una línea de líder que llega al muro | lo único que prueba que se puede LEER |
| Fila del año en `green` **y** el artefacto sin `fixture_missing` | cobertura real, no cobertura declarada |

Si la familia cargada no puede etiquetar un muro, el arnés lo dirá con las
palabras de Revit (`There is no loaded tag type that can be used when tagging
referenceToTag with tagMode`) y habrá que elegir otra familia: una de
multicategoría necesita `tag_mode='multi_category'`, que el arnés ya deduce de la
categoría de la familia que encuentra.

---

## B. Documento ajeno REAL

La única ruta del conductor que nunca ha visto un caso real es la más
importante: «hay abierto un documento que esta corrida no abrió → dejar el Revit
corriendo y no cerrar nada». Hoy está cubierta por siete casos simulados.

### Lo que ya está preparado

- **El documento**: `C:\hz-live\HZ_DESECHABLE_NO_REGISTRADO.rvt`, copia de
  `HZ_LINK_B.rvt` hecha el 09-09, formato 2023, 6.475.776 bytes,
  SHA-256 `703585d2f4cebf167b3163410b270eedbfa63db58f2b9350929261e12588ee67`.
  Su nombre dice lo que es. **Nunca se usa un proyecto real del usuario para
  esto**: el arnés se niega por nombre.
- **El arnés**: `scripts/live/open-unregistered-document.ps1`. Corre COMO arnés
  dentro de la sesión de ensayo y abre su archivo por el puente directamente, de
  modo que el registro del conductor nunca se entera. Comprobado offline: con una
  ruta que no empieza por `HZ_DESECHABLE` se niega **antes de hacer una sola
  llamada** al puente (`calls made: 0`).

### El comando (cuando Revit 2026 esté libre)

```powershell
& scripts/live/run-year-matrix.ps1 -Years 2026 `
  -Harness 'open-unregistered-document.ps1 -Document {title} -Disposable yes-this-model-is-disposable -Path C:\hz-live\HZ_DESECHABLE_NO_REGISTRADO.rvt' `
  -PrepareDocument '2026=C:\hz-live\HZ_WRITE3.rvt' `
  -ArtifactRoot <carpeta de evidencia>\ym-2026-foreign
```

### Qué debe pasar — y qué sería un hallazgo

| Esperado | Dónde se lee |
| --- | --- |
| La fila del año termina en `recovery_pending` | resumen `year-matrix-*.json` |
| `close.state = left_running_foreign_document` | mismo resumen |
| `close.foreign` nombra `HZ_DESECHABLE_NO_REGISTRADO` con el motivo «this run did not open …» | mismo resumen |
| `close.closed_documents` **vacío** | mismo resumen |
| El Revit sigue corriendo y el manifiesto de 2026 **no** restaurado | `recovery-pending-2026.json` |

Cualquier otra cosa —y en particular que el documento se cierre— es un fallo de
la protección, no del arnés.

### Recuperación (deliberada, después de mirar el resultado)

La corrida deja la sesión abierta a propósito. Se cierra con el mismo módulo,
registrando explícitamente los dos documentos que sabemos que son nuestros
(el fixture y el desechable), como se hizo el 09-09 con la sesión varada:
`artifacts/stabilization-2026-09-09/ym-2026/resolution.md` es el ejemplo exacto,
y `recovery-2026-run3.log` la salida esperada — cierre por ruta registrada,
salida normal por la ventana y `Restore-HzYearSession` verificando el manifiesto
contra la instantánea previa.

---

## Orden sugerido cuando avises

1. B primero si solo hay un rato: es una corrida, no necesita nada tuyo salvo
   Revit 2026 cerrado, y cierra la última ruta sin caso real.
2. Luego A, que necesita tu paso manual en Revit 2023 antes de las tres corridas.
3. Y, si quieres cerrar la matriz entera con el conductor corregido, 2024, 2025 y
   2027 con los arneses del 08-09 — el producto no ha cambiado desde `7fe8693`,
   así que eso mide el conductor, no el producto.
