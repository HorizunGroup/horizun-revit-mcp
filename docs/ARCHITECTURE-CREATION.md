# Creación arquitectónica y supuestos

Cambios de 5.2.1 comprobados solo en código y pruebas. Falta medirlos en Revit.

`horizun_create_elements` acepta tres recetas adicionales. Se expanden antes de
planificar y usan las mismas transacciones, ensayo, token, marca Horizun y
verificación posterior que `wall` y `family_instance`.

```json
{
  "target_document": "Mi modelo",
  "units": "mm",
  "dry_run": true,
  "elements": [
    {"kind":"wall_chain", "level_id":123,
     "points":[[0,0,0],[5000,0,0],[5000,4000,0]], "closed":false}
  ]
}
```

Una cadena crea un muro por par consecutivo. `closed:true` agrega el último
segmento hacia el primer punto, sin duplicarlo si ya estaba cerrado. Cada muro
tiene su propia fila, id, marca y relectura. La respuesta `architectural_expansion`
vincula el índice expandido con `elements_index` y `segment_index` de la receta.
Si dos tramos colineales se solapan más allá de la tolerancia del modelo, el
ensayo rechaza la cadena y nombra ambos segmentos. Los tramos que solo comparten
un extremo se conservan.
No se mezcla esta expansión con referencias `batch_index` ni con `all_enclosed`;
se usan lotes separados para conservar sus índices. El máximo sigue siendo 2.000
elementos después de expandir.

Puertas y ventanas se colocan sobre un muro existente:

```json
{"kind":"door", "host_id":456, "point":[2000,0,0], "type_name":"Puerta 900"}
```

```json
{"kind":"window", "host_id":456, "point":[3500,0,1000], "type_name":"Ventana 1200"}
```

Se admiten solo familias de la categoría pedida y hospedadas en muro. Si falta
`level_id`, se usa el nivel base del muro; si falta `coordinate_mode`, se usa
`level_offset`: Z sobre el nivel base del muro. La identidad del muro se protege con los campos comunes `identity`
o `unique_id`, como las demás escrituras sobre elementos existentes. Estos ids
de ejemplo se deben reemplazar por ids e identidades de una lectura del modelo.

`type_name` también está disponible en `wall`, `floor`, `ceiling` y `roof`.
Un `type_id` válido tiene precedencia. Un id inexistente sin `type_name` se rechaza.
Un nombre exacto acepta también «Familia: Tipo»; si varias familias coinciden,
se rechaza con los candidatos. Nombres exactos y ids aceptan los mismos tipos,
incluidos muros cortina/apilados y losas de cimentación. Sin tipo pedido, se usa
el predeterminado del documento. Si falta ese predeterminado o el nombre no
coincide, se elige un tipo compatible de forma determinista y se nombra lo pedido
y lo elegido como SUPUESTO. Las alternativas se ordenan por distancia de nombre,
nombre ordinal e id; sin nombre se prioriza el nombre ordinal, nunca su longitud.
Para muros, el respaldo prioriza tipos básicos. Un id de otra clase se rechaza.

Si se omite `height` en un muro, se toma la diferencia entre su Z base absoluta
y el nivel superior explícito (incluido `top_offset`) o el siguiente nivel por
encima, al cual se vincula. Si el siguiente nivel está a menos de 2.000 mm o no existe, se usan 3.000 mm sin vínculo superior. Un nivel superior explícito no es supuesto. Una altura explícita inválida
o contradictoria sigue siendo un error. La altura efectiva entra en el plan
resuelto: mover un nivel entre ensayo y aplicación invalida el token cuando
cambia esa altura. La posición y los desfases continúan teniendo que coincidir.

Las decisiones asumidas se devuelven arriba con `has_assumptions:true` y
`assumptions:[{flag:"SUPUESTO",what,why,override}]`. Sin supuestos se omiten ambos
campos. `override` indica cómo dar el dato explícito. Se muestran en ensayo,
aplicación y rechazo de una construcción que usó esos valores. Una misma decisión
se agrupa una sola vez por cadena. Un tipo exacto no es supuesto.

Las cadenas detectan el cierre por tolerancia y rechazan un cierre de dos
vértices que superpondría muros. Omiten los tramos bajo la tolerancia de curvas
de Revit y los nombran en un SUPUESTO, conservando los demás tramos. Si todos los
tramos de una cadena quedan por debajo de la tolerancia, se omite esa cadena y
las otras filas del lote siguen. Si no queda ninguna fila, se devuelve un rechazo
sin token que identifica los tramos omitidos.

Una habitación sin cerrar puede conservar su colocación, con
`room_status:"not_enclosed"`, `boundary_closed:false` y SUPUESTO.
Si Revit avisa que la región del nivel y la fase de colocación ya está ocupada,
se revierte el lote y se devuelve `redundant`. El ensayo de habitaciones por
punto comprueba este aviso mediante una transacción interna que luego revierte.
La respuesta distingue el área que midió Revit del cierre que se pudo verificar.
`placement:"all_enclosed"` conserva las comprobaciones estrictas.

Para aplicar, se repite la solicitud original con `dry_run:false`, el
`confirmation_token` del ensayo y una nueva `idempotency_key`. No se envían las
filas expandidas como reemplazo de la solicitud que produjo ese token.

## Pisos y grupos

Con `horizun_slab_shape operation:read`, un piso plano con `enabled:false`
devuelve los vértices de la cara superior en `boundary_loops` y `vertices`.
Los huecos conservan su propio contorno. Los arcos se devuelven como puntos
muestreados. `add_split_line` toma coordenadas XY sobre la cara superior. Revit
2023–2025 admite insertar un vértice de borde con `DrawPoint`. Revit 2026–2027
usa una línea de apoyo a la cota de la cara inferior para el corte de borde a
borde en un piso plano con edición de forma inicialmente desactivada. La
relectura comprueba la cobertura del segmento y, si se usó la línea de apoyo,
que la cara superior conserve su cota. Un extremo nuevo en el borde y otro
interior requiere un vértice de borde existente en Revit 2026–2027. El centro
de un arco de borde en un piso circular se comprueba contra la cara para
clasificarlo como interior.

Con `horizun_manage_groups operation:add_members scope:type` o
`scope:all_instances`, envía una instancia
de referencia en `group_ids` y los elementos sueltos en `element_ids`. El grupo
de referencia se reconstruye y las otras instancias reciben el tipo redefinido.
La respuesta devuelve los ids vigentes en `result.affected_group_ids`, la
identidad del grupo reconstruido en `result.group_unique_id` y las altas
adicionales con su identidad en `result.also_created`.
Si corresponde, `accept_member_regeneration:true` autoriza editar grupos, tipos o
miembros sin marca y acepta la pérdida de valores por instancia como marca y
comentarios. El ensayo muestra las etiquetas y cotas de las instancias
afectadas. Si sus referencias no se pueden leer o cambian durante la
redefinición, la operación se detiene sin consolidar esos cambios. En cada copia
se comprueban el tipo y los límites geométricos de los miembros agregados y de
sus dependientes, tomando como referencia la colocación anterior a la
regeneración. Se emparejan los miembros conservados en el orden que devuelve
Revit. Para propagar el alta, envía los giros conocidos de copias con la misma
orientación de espejo en `relative_rotations_degrees`, en orden ascendente de
id de copia. Se releen los ejes y el estado de espejo de familias, y el trazado
de rectas y arcos circulares acotados, incluso si sus extremos se invierten.
Para los arcos, el centro, el barrido y las muestras interiores permiten acotar
la separación entre trazados a lo largo de toda la curva.
Si el trazado cambió o no se puede medir, el ensayo revierte sin emitir token.
Un cambio de esas lecturas entre el ensayo y la aplicación invalida el token.
El ensayo verifica las altas después del commit interno y muestra sus roles
en `rehearsal.addition_roles`. El token vincula esos roles y las identidades
de `rehearsal.deleted_elements`. La aplicación exige los mismos roles y bajas
antes de consolidar. Un alta fuera de los grupos afectados revierte la
operación y aparece en `out_of_scope_additions` dentro de las comprobaciones.
Todo borrado fuera del alcance previsto de regeneración requiere propiedad comprobada.
Si falla la lectura final después de confirmar el cambio, la respuesta conserva
`transaction_status: "Committed"` y `changes_applied: true`, con el motivo en
`verification_error` cuando la API lanza una excepción.
`check_variation` compara la cantidad, los tipos, los límites geométricos y los
trazados legibles de los miembros. La API pública de Revit no entrega el giro
de un grupo de modelo.
Si conoces los giros y las instancias tienen la misma orientación de espejo,
envía `relative_rotations_degrees` con un ángulo antihorario por cada id después
del primero en `group_ids`, en el mismo orden. Por ejemplo, para dos instancias
paralelas envía `[0]`. La respuesta identifica la fuente como `declared`. Con
una comparación completa y coincidente devuelve `variation_count:0`.
Si las muestras de un arco coinciden dentro de la tolerancia pero no se puede
confirmar el trazado completo, `curve_path_matches` queda en `null` y la
comparación conserva las diferencias comprobadas en los otros miembros.
Si omites los giros, se informan las diferencias medibles de tipo, cantidad y
altura, con `coverage_complete:false` y `variation_count:null`. Una diferencia
comprobada produce `has_variation:true` aun con cobertura parcial.
