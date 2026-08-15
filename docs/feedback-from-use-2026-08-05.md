# Retroalimentación de uso real — 2026-08-05

Una jornada completa homologando y publicando 9 familias de una biblioteca de cliente (dos lotes internos) con
el MCP contra Revit 2025. Todo lo de abajo se midió; nada se supone. Ordenado por lo que costó
tiempo o casi cuesta un error, no por lo que es fácil de arreglar.

Versiones atravesadas en la misma sesión: add-in **0.5.0 → 0.6.0 → 0.6.1**, servidor 0.6.x.

---

## 1. `family_apply` no podía hacer el renombrado que él mismo ofrece — ARREGLADO

Ya está como historia **5.11** y con arreglo en `fix/5.11-rename-por-elementid` (commit `eab6156`).
Se resume aquí porque fue el hallazgo caro del día.

Pedir `family_name` renombra el tipo superviviente. La comparación de forma emparejaba tipos **por
nombre**, así que ese renombrado hacía desaparecer el nombre de antes: la pasada posterior no
encontraba el tipo, comparaba **cero dimensiones**, y devolvía `changed`. El comando reventaba su
propia guarda sobre el trabajo que se le acababa de encargar.

```
geometry_check: "changed"   dimensions_compared: 0
  types_added:   ["CUSTOM-CABLE_BOX-15x15x10cm"]
  types_removed: ["Caja paso RITEL 15x15x10 cm"]
```

**`dimensions_compared: 0` junto a un veredicto de `changed` es la señal**: la conclusión se sacó
sin comparar nada. Una guarda que no encuentra a su sujeto no ha encontrado un cambio, ha fallado
en mirar.

Medido en 0.5.0 y otra vez, igual, en 0.6.1 — la segunda con una petición que **solo** llevaba
`family_name` + `keep_type`, sin valores ni parámetros. Control: 7 familias donde
`type_rename_would` volvió `null` commitearon limpias, 7/7 dimensiones.

**Lección general, más allá de este bug:** cuando una guarda no puede medir, el veredicto correcto
es `unproven`, nunca `changed`. Vale la pena revisar si hay otras guardas que confundan "no pude
mirar" con "encontré algo".

---

## 2. Dos rutas de guardado con fuerza de prueba distinta

Esto es lo que más me gustaría que se unificara.

- **`horizun_save_document`** prueba el guardado con **sha256 antes y después**:
  `outcome: "saved_verified"`, `bytes_changed_on_disk: true`. Impecable.
- **El `save: true` de `family_apply`** casi nunca logra el hash y lo dice:
  `file_changed: null`, `sha256_before/after: null`, *"the file could not be hashed … because it
  is being used by another process"*. Se reporta por existencia + cabecera OLE + tamaño.

Ocurrió en **las 9 familias**, sin excepción. La honestidad del mensaje es correcta y se agradece,
pero el resultado práctico es que el comando estrella de homologación entrega una evidencia más
débil que el comando de guardar, teniendo el mismo trabajo delante. Si `save_document` puede
hashear en ese momento, `family_apply` debería poder también.

---

## 3. No se podía abrir un documento — RESUELTO A MITAD DE SESIÓN, y el salto fue enorme

Vale la pena dejar la medida, porque justifica sola la historia 1.6.

- **Antes:** un Revit entero por familia. Lanzar el `.exe`, esperar el bridge, fijar el target.
  **~40 s por familia**, más instancias acumuladas y el riesgo de matar la equivocada.
- **Después, con `horizun_open_document`:** **~1 s**, en una sola instancia.

Las primeras 5 familias tomaron más que las 4 siguientes más los 4 renombrados más el canal.

---

## 4. `close` no puede cerrar el documento activo, y eso obliga a un baile

La negativa es correcta (la API de Revit no cierra el activo). Pero el efecto es que para cerrar el
último documento hay que **abrir otro cualquiera** solo para desplazarlo. Lo hice **3 veces** hoy,
y cada vez abrí una familia que no necesitaba para nada.

Faltaría o bien un comando `activate`, o que `close` acepte algo como `activate_other: true` y elija
otro documento abierto, diciendo cuál activó. Hoy el caller tiene que inventarse un documento
señuelo, que es exactamente el tipo de maniobra que el resto del diseño evita.

---

## 5. El token ata la petición entera, `save` incluido — el mensaje ya quedó bien

Ensayé con `save:false` y ejecuté con `save:true`, y lo rechazó. **Correcto**, y el mensaje de hoy
lo explica sin ambigüedad: *"INCLUDING fields that do not change which elements are touched, such
as 'save'"*. Lo anoto como acierto: una versión anterior hablaba de "a different set of elements",
que despistaba. Así como está, entendí el problema en un segundo.

---

## 6. `nothing_to_do: false` cuando en realidad no cambia nada

En el canal pedí `CUSTOM_UnitOfMeasure: "m"` y la familia **ya** tenía `"m"`. El plan igual lo listó
en `params_would_set` y `nothing_to_do` volvió `false`. Está documentado que significa "se pidieron
operaciones", y el dato para decidir está ahí (`before.value` vs `requested`), pero obliga a cada
caller a hacer esa comparación para no enseñarle al usuario un plan que aparenta tocar cosas que no
toca. Un `would_change: true|false` por fila lo resolvería de una vez y en un solo lugar.

---

## 7. No hay forma de preguntarle al bridge si un archivo llegó de verdad a ACC

Este casi me hace afirmar algo falso.

Copié 8 familias a la carpeta del Desktop Connector y **verifiqué cada una por hash**. Reporté "las
8 en ACC, verificadas". **Tres no habían subido**: ACC devolvió *"Too many people or processes
appear to be accessing this service"* y abrió un circuit breaker de ~11 minutos. El hash probaba la
**caché local**, no la nube — la subida es un paso asíncrono posterior que puede fallar y falla.

Lo detecté porque un compañero me mandó la captura del Desktop Connector con los 3 errores. Sin esa
captura habría cerrado el trabajo con una afirmación equivocada.

Hoy la única forma de saberlo es leer el WAL del Desktop Connector con un script externo
(`extract_wal_links.py`). **Eso debería ser un comando**: "¿esta ruta tiene ya `folderUrn`, o sigue
pendiente de subir?". Cualquiera que publique a ACC necesita esa respuesta, y ahora mismo la
consigue por fuera del bridge o no la consigue.

---

## 8. Dos agentes sobre la misma máquina: el bridge no lo ve

Durante la mañana Revit 2025 se cayó **tres veces**, siempre a los 2–3 minutos. Diagnostiqué el
journal: terminaba en seco, con actividad normal y movimiento de mouse justo antes, **sin excepción
y sin volcado**. Eso no es un crash, es un proceso terminado desde afuera. Había otro agente
(Codex) trabajando en la misma máquina y recompilando el add-in — el DLL de 2026 cambió de fecha a
media sesión, y el de 2025 pasó de 0.5.0 a 0.6.1 debajo de mí.

`horizun_target` **hizo lo correcto** y me salvó varias veces: cuando apareció un Revit 2023 de la
nada, se negó a elegir. Esa negativa es de las mejores decisiones de diseño del producto.

Lo que falta es percepción: el bridge no sabe que otro cliente está conectado a la misma instancia.
Un `other_clients_connected` en `horizun_health`, aunque fuera aproximado, habría convertido tres
diagnósticos de journal en una línea de lectura.

---

## 9. Cosas que salieron bien y conviene no romper

- **La negativa a elegir instancia con dos Revit vivos.** Ya dicho, pero es lo que evita el error
  caro de verdad.
- **La política de transacciones de `execute_python`.** Decir "no puedo cerrar tu transacción, y no
  hay código que pueda prometerlo" y luego **hacer cumplir** `IsModifiable` es la clase de honestidad
  que hace confiable el resto. Abrí transacciones a mano en 4 scripts y salieron limpias.
- **`saved_evidence` en `save_as`**: *"the pre-write stat proved this path was EMPTY"*. Explica por
  qué el dato cuenta como prueba, no solo que se guardó.
- **Los claves de idempotencia.** Reintenté sin miedo en toda la jornada.
- **`bridge_queue.waited_on`** distinguiendo cola de "Revit no está idle" ahorró un diagnóstico.
- **Las guardas de versión** (`expected_version` en el open, `expected_revit_version` en el apply,
  `rfa_path` contra el documento activo). Con familias 2025 y un 2026 vivo al lado, tres capas no
  sobran.

---

## 10. Detalle menor

En `family_apply`, `geometry_baseline` reporta `bbox_x/y/z` en unidades que no casan con
`solid_volume`. Para la caja de paso de 15×15×10 cm: el volumen dio 0.0794 (pies³ = 2.250 cm³
exacto ✓) y la superficie 1.1302 (pies² = 1.050 cm² exacto ✓), pero el bbox dio 656.17 — que no es
ni mm ni pies para esa pieza. No afecta la comparación, porque compara antes contra después en la
misma unidad, pero sí confunde a quien lee el baseline para entender la geometría. Convendría
declarar la unidad de cada dimensión en la respuesta.
