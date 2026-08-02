# Family authoring

Revit uses the word *family* for three different mechanisms. Horizun keeps them
separate because they have different APIs, files and failure modes.

| Revit mechanism | Horizun operation | What is created |
| --- | --- | --- |
| Loadable family | `horizun_create_family` | A new `.rfa` compiled from an installed `.rft`, optionally loaded into the guarded project. |
| System-family type | `horizun_manage_system_types` | A project-resident type duplicated from an explicit source type. There is no RFA. |
| In-place family | Not automated | Revit's public API has no general in-place-family creation operation. Horizun does not drive the modal editor by screen coordinates. |

## Loadable RFA compiler

The RFT determines the Revit category, host behavior and placement rules. The
typed specification can add:

- family or type parameters for length, area, volume, angle, number, integer,
  yes/no, text and material;
- formulas and named family types with values in `mm`, `m` or `feet`;
- solid or void extrusions, blends, revolutions, planar-path sweeps and
  straight-path swept blends;
- named reference planes, multi-reference linear dimensions optionally labeled
  by declared length parameters, and symbolic or model lines;
- point-placed nested `.rfa` types in model or active-view mode, optional rotation,
  and propagation from unambiguous nested instance parameters to declared outer
  family parameters;
- parameter associations for extrusion/blend bounds, revolution angles,
  material and visibility;
- pipe, duct, electrical, conduit and cable-tray connectors hosted on a planar
  form face selected by its normal, including size-parameter associations and a
  primary connector;
- verified SaveAs to an absolute `.rfa` path and optional verified load into the
  target project. Verification re-reads parameter data types/formulas, every
  requested type value in internal units, exact form class and solid/void state,
  parameter associations, connector primary state and loaded symbol names.

Length/area/volume values follow the request's `units`; angle values are degrees.
Parameter references are type-checked before any family document is opened: for
example, a material association must target a declared `material` parameter and
a connector size must target `length`.

The template still decides what Revit permits. For example, a title-block or
annotation template is not a 3D component template, and an adaptive-component
template has a different point/curve workflow. The current compiler targets
loadable 3D component and MEP templates; it does not yet claim adaptive points,
filled/masking regions, hosted/work-plane nested placement or arbitrary spline/arc profile
segments. Symbolic/model lines and labeled dimensions are available, but the
selected RFT still determines whether a specific 2D primitive is legal.

### Example: typed parametric MEP body

Call once with `dry_run: true`. The response contains a single-use
`confirmation_token`; no family document, transaction or file is created.

```json
{
  "target_document": "My guarded project",
  "template_path": "C:\\ProgramData\\Autodesk\\RVT 2026\\Family Templates\\...\\Metric Generic Model.rft",
  "output_path": "C:\\BIM\\Families\\HZ_Valve.rfa",
  "units": "mm",
  "parameters": [
    { "name": "Depth", "data_type": "length", "group": "geometry" },
    { "name": "Diameter", "data_type": "length", "group": "geometry" },
    { "name": "Material", "data_type": "material", "group": "materials" },
    { "name": "Visible", "data_type": "yesno", "group": "general" }
  ],
  "types": [
    { "name": "DN100", "values": { "Depth": 600, "Diameter": 100, "Visible": true } }
  ],
  "forms": [
    {
      "key": "body",
      "kind": "extrusion",
      "plane": "xy",
      "profile": [[[-250,-250,0],[250,-250,0],[250,250,0],[-250,250,0]]],
      "depth": 600,
      "end_parameter": "Depth",
      "material_parameter": "Material",
      "visibility_parameter": "Visible"
    }
  ],
  "connectors": [
    {
      "key": "pipe_out",
      "host_form_key": "body",
      "kind": "pipe",
      "face_normal": [0,0,1],
      "system_type": "SupplyHydronic",
      "diameter_parameter": "Diameter",
      "primary": true
    }
  ],
  "load_into_project": true,
  "dry_run": true
}
```

Apply the identical semantic request with `dry_run: false`, the returned
`confirmation_token` and a new `idempotency_key`. Changing the template,
destination, units, parameter/type/form graph, connectors or load policy
invalidates the token.

## System-family authoring

`horizun_manage_system_types` accepts only project-resident non-`FamilySymbol`
types. It can write normal type parameters by BuiltInParameter token, shared GUID
or one unambiguous exact parameter name.

For `HostObjAttributes` such as wall, floor, roof and ceiling types, it can also
replace the complete vertically homogeneous compound structure. Layers are
ordered exterior to interior and can specify:

- material function and material ElementId;
- width in explicit units;
- participation in wrapping;
- exterior/interior shell counts and therefore core boundaries;
- structural and variable layer indices;
- end-cap and opening wrapping;
- structural-deck profile and embedding.

```json
{
  "target_document": "My guarded project",
  "units": "mm",
  "actions": [
    {
      "source_type_id": 123456,
      "new_name": "HZ_EXT_250",
      "compound_structure": {
        "layers": [
          { "function": "Finish1", "width": 15, "material_id": 201, "wraps": true },
          { "function": "Insulation", "width": 50, "material_id": 202 },
          { "function": "Structure", "width": 170, "material_id": 203 },
          { "function": "Finish2", "width": 15, "material_id": 204, "wraps": true }
        ],
        "exterior_shell_layers": 2,
        "interior_shell_layers": 1,
        "structural_layer_index": 2,
        "opening_wrapping": "ExteriorAndInterior"
      }
    }
  ],
  "dry_run": true
}
```

The dry run resolves the real source and every material before issuing a token.
Apply uses one atomic project transaction. After commit, Horizun re-reads the new
runtime type, name, parameters, every layer and all compound-structure settings;
it does not report the submitted JSON as proof.

## Safety and file behavior

- Both operations require an explicit active `target_document`, dry-run and
  confirmation-token flow, plus durable idempotency on apply.
- `horizun_create_family` requires `full_write` or `unsafe_code` because it writes
  an external file. Its output directory must already exist.
- A failed project load may leave the newly saved RFA on disk; the error names
  that path instead of deleting user-visible output automatically.
- `horizun_manage_system_types` is atomic inside the project. If one duplicate,
  parameter or compound structure fails, the complete batch rolls back.
