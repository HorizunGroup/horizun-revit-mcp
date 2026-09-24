# Information management

## Verified IFC delivery

`horizun_deliver_ifc` turns "export an IFC and hope" into one call whose verdict
is the **file's**, not the model's. It runs, in order:

1. **precheck** *(optional, advisory)* — the `horizun_validate_ids` pre-check over
   the live model. It catches expensive problems before exporting, but a Revit
   parameter is not evidence of an IFC property set, so it **never** decides
   readiness.
2. **export** — the IFC is written with every option explicit: `ifc_version`
   (required, closed list), `ifc_filter_view_id`, `export_base_quantities`,
   `split_walls_and_columns`, `space_boundary_level`, the exporter's own
   user-defined property-set file (`pset_mapping_path`), the coordinate basis
   (`site_placement`: `shared`, `survey_point`, `project_base_point`,
   `internal`) and, optionally, `export_ifc_common_property_sets` /
   `export_internal_revit_property_sets`. The gate passes only when a new,
   non-empty file is measured at the exact planned path; it is hashed (SHA-256)
   from disk.
3. **schema_header** — the head of the file must be ISO-10303-21 with a
   `FILE_SCHEMA` of the family the requested version must produce (IFC2X3, IFC4
   or IFC4X3 — an IFC4X3 file is *not* accepted as IFC4), and the file must end
   with `END-ISO-10303-21` (a truncated write fails).
4. **ids_validate** — the IDS evaluated on the **exported file** (the same
   evaluator as `horizun_validate_ids operation=validate`). Any failing
   specification fails the gate; anything undecided makes it `not_decidable`,
   never `passed`.
5. **pset_mapping** — every property the mapping declares is looked for in the
   exported file on the IFC classes the mapping names (an occurrence is credited
   with its type's sets; a `...Type` class is checked on its own sets). The
   report gives coverage *n of m* per property and example GlobalIds of the
   entities that lack it. `pset_min_coverage` (default `1`) sets the share that
   must carry each property.
6. **bcf** *(optional, needs `ids_path`)* — one BCF 2.1 topic per failed
   specification, with the failing GlobalIds as the selection, written as
   `<name>.ids-issues.bcf` and re-read structurally (every markup and viewpoint
   re-parsed, topics and GlobalIds counted). No failure, no file: the gate is
   then `skipped`.

Every gate reports `passed`, `failed`, `skipped` or `not_decidable`.
`deliverable_ready` is `true` only when `export` and `schema_header` passed and
every other requested, non-advisory gate passed (or was skipped because there
was nothing to do). A file that is not ready stays where it was written, and
`blocking` names why.

`dry_run` defaults to `true`: the rehearsal returns the plan (effective options
and how each one reaches the exporter, the model's current georeference, the
IDS and mapping to be used with their SHA-256, output paths, the advisory
precheck) and writes nothing. The confirmation token binds the destination,
every option, the filter view, the **content** of the mapping and the IDS, and
which targets already exist.

### Georeference

The reply always carries `georeference_in_model` — survey point and project base
point (internal and shared, mm), the active location's east/west, north/south,
elevation and angle to true north, and the site location (latitude, longitude,
place, time zone, coordinate system id). After export it adds
`georeference_in_file`: the `IfcSite` placement and reference
latitude/longitude/elevation, any `IfcMapConversion` with its target CRS, and the
true-north direction the file declares. These are **observed, not judged** —
compare them with the model before sending the file.

### How each option reaches the exporter

| Option | Channel | How the delivery knows it held |
|---|---|---|
| `FileVersion`, `FilterViewId`, `ExportBaseQuantities`, `WallAndColumnSplitting`, `SpaceBoundaryLevel` | typed `IFCExportOptions` properties, compiled against Revit 2023–2027 | version: `schema_header`; the rest: requested, not provable from the file |
| `ExportUserDefinedPsets`, `ExportUserDefinedPsetsFileName` | `IFCExportOptions.AddOption`, read by name by the Revit IFC exporter | `pset_mapping` gate |
| `SitePlacement` (`Shared`, `Site`, `Project`, `Internal`) | `AddOption` | observed in `georeference_in_file` |
| `ExportIFCCommonPropertySets`, `ExportInternalRevitPropertySets` | `AddOption`, only when given | requested, not provable |

The named options follow the documented behaviour of the open-source Revit IFC
exporter; they are not readable back from the option object, which is why each
one is either checked in the file or labelled `requested_unverifiable`.

### The mapping file

The mapping **is** the exporter's user-defined property-set file — the same file
is handed to the exporter and then read back as the list of what the IFC must
carry. Fields are separated by **TAB**; a line written with spaces would export
no set at all, so it is refused by line number before anything is exported.

```text
# Generic delivery mapping - fields separated by TAB
#PropertySet:<TAB><Pset name><TAB>I|T<TAB><IFC classes, comma separated>
#<TAB><Property name><TAB><Data type><TAB>[Revit parameter, if different]
PropertySet:	Org_Identity	I	IfcWall,IfcSlab,IfcColumn,IfcBeam
	AssetCode	Text	Asset Code
	Zone	Label	Zone
PropertySet:	Org_TypeData	T	IfcWall,IfcSlab
	Manufacturer	Label
	FireRating	Label	Fire Rating
```

The exporter writes a property only when its Revit parameter has a value. A
property missing from an element therefore means an empty parameter **or** a
mapping the exporter did not apply; the file cannot tell those apart, and the
GlobalIds in the report are where to look.

### Naming

Give `output_name`, or an `information_container` (`fields`, `field_order`,
`separator`, `field_patterns`); the name is the fields joined in `field_order`.
If both are given they must agree. Only the name is used here.

### Resumen en español

`horizun_deliver_ifc` es una **entrega IFC verificada** en una sola llamada. El
veredicto es el del **archivo**: se exporta con opciones explícitas (versión,
vista de filtro, cantidades base, división de muros/columnas, límites de
espacio, archivo de property sets definido por el usuario y base de
coordenadas), se relee la cabecera (`FILE_SCHEMA` y cierre `END-ISO-10303-21`),
se valida el IDS **sobre el IFC exportado**, se busca en el archivo cada
propiedad del mapeo con cobertura *n de m* y GlobalIds de los faltantes, y
opcionalmente se escribe un BCF con un tema por especificación fallada, releído
estructuralmente. El pre-chequeo del modelo es solo orientativo y nunca decide.
`deliverable_ready` es `true` solo si pasan todos los gates solicitados. El
ensayo (`dry_run`, por defecto) devuelve el plan, la georreferencia actual del
modelo y no escribe nada. El archivo de mapeo es el mismo formato del
exportador de Revit, separado por **tabuladores**.
