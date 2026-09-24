# Horizun BIM Standards Pack

This folder holds optional, generic starting profiles for BIM QA/QC. It is not a
universal standard and it does not encode any client's naming rules, catalogs or
delivery requirements. Review every threshold, parameter name and remediation
before use on a project.

## Included profiles

- [`model-health-baseline.json`](model-health-baseline.json): a conservative
  input for `horizun_audit_model`. It avoids arbitrary naming and parameter
  requirements; its values are examples to calibrate per project.
- [`planimetry-baseline.json`](planimetry-baseline.json): a minimal inline
  requirement-set skeleton for `horizun_audit_planimetry`. Populate the empty
  policy fields only after the project team approves them.

- `co-ntc6047-accesibilidad.json`, `co-nsr10-titulo-k-evacuacion.json`,
  `co-retilap-iluminancia.json`: example requirement sets for
  `horizun_code_check`, transcribing public Colombian technical norms (NTC 6047,
  NSR-10 Título K, RETILAP 2024) with the numeral of every rule. A threshold that
  could not be verified against the norm's text is marked `unverified_value` and
  carries no number. See [docs/TOOLS-EXTENDED.md](../docs/TOOLS-EXTENDED.md).

## Safe use

1. Copy a profile into the project repository.
2. Give it an owner, identifier and semantic version.
3. Review its values with the BIM lead and record the project decision.
4. Supply it explicitly to the relevant audit tool.
5. Treat `unknown` and incomplete coverage as work to resolve, not a pass.

The bridge reports the profile hash with its findings. That provenance is why a
result can be reviewed later without guessing which rules were used.
