# User-supplied product family routes

Add product entries to `product_families` in a `horizun.template-kit/1` manifest,
or add `product` and `source_path` to an existing `catalog` entry:

```json
{
  "product_families": [{
    "key": "ProductSink|One",
    "family": "ProductSink",
    "type": "One",
    "name_en": "Sink",
    "category": "Plumbing Fixtures",
    "source_path": "D:\\Library\\ProductSink.rfa",
    "product": { "manufacturer": "Example", "model": "S1" }
  }]
}
```

The user supplies the RFA and metadata. The resolver never downloads or authors
files and does not infer product geometry from the product metadata.

Use `horizun_template_kit` with `operation=find`, `text=sink` and
`product_only=true`, or `horizun_catalog_lookup` with `operation=products`,
`catalog_path` naming the kit JSON, and `query=sink`. `category`, `code` and
`max_results` filter the catalog route. Ordinary kit search includes both sources
and prefers a product entry when relevance and dimensions tie. Kit `type` resolves
these entries by key. Results identify the kit SHA-256.

Every product hit reports local RFA availability (`status`: `available`,
`file_missing`, `invalid_source_path` or `family_or_type_missing`); `load` is null. Loading a family through the bridge (`horizun_create_family`) is in the Pro edition: in Community, load the RFA in Revit, then re-read the loaded family/type with the returned `resolve_after_load` query.
The resolver itself reports `geometry_quality=not_measured`.

Read the loaded type using the returned query, checking exact family/type names
among substring matches. Inspect geometry with `geometry_quality`, and check
hosting and connector compatibility before placing or replacing instances.
Availability tests use dummy local files; live Revit loading, geometry and host
compatibility remain pending in vivo.
