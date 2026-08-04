# The requirement set — a standard as data

A **requirement set** is a document that says what a model must satisfy. The bridge
reads one, measures the model against it, and reports per element what it found. The
bridge does not know what ISO 19650 is, and it never will.

That is the whole design, and the acceptance test for this schema is blunt:

> **Three requirement sets — ISO 19650, IFC/buildingSMART, COBie — one command, no
> standard-specific code.** If `horizun_check_requirements` needs an
> `if (standard == …)` anywhere, this document is not finished.

## Why data and not code

A standard compiled into the add-in ships as a new binary whenever a clause changes,
cannot differ between two projects, and cannot be diffed. As a document it is
versioned, per-project, reviewable in a pull request, and the same command serves a
Colombian NSR job, a UK ISO 19650 job with its national annex, and a client EIR that
overrides both.

`AGENTS.md` already requires this: *"no company's standards or catalogues are
compiled in. Where a command needs one, it is passed as an argument."* This is that
argument, written down.

## The three layers, and the line between them

- **The bridge MEASURES and never judges.** It reports facts per element and keeps
  *"does not conform"* separate from *"could not be read"* — a distinction it already
  draws everywhere else.
- **The requirement set DECLARES** what is wanted. It carries no prose for humans and
  no severity philosophy; it carries selectors and assertions.
- **The judgement and the writing-up live above**, in the agent and in Horizun Hub. A
  finding names the typed command that would fix it, so a proposal is composed out of
  measurements rather than written from a guess about the model.

## Shape

YAML or JSON — the same document either way. Every key below is either required or
has a stated default; nothing is inferred from a name.

```yaml
requirement_set:
  id: iso-19650-stage-3        # stable, used in receipts and findings
  version: 2.1.0              # SemVer; a finding always cites the version it came from
  title: ISO 19650-2 stage 3 delivery
  # What this set is ABOUT, so a report can say what was and was not examined.
  scope:
    categories: [Walls, Doors, Pipes]     # omit for "everything the rules select"
    stage: 3                              # free-form; only rules interpret it

rules:
  - id: wall-fire-rating
    # WHICH elements. Every field is an AND; a rule with no selector selects nothing
    # and is refused at load, because a rule that silently matches everything is how a
    # requirement set deletes a project.
    selector:
      category: Walls
      type_name_matches: '^EXT-'          # regex, optional
      parameter_exists: Fire Rating       # optional pre-filter

    # WHAT must be true. Exactly one assertion per rule - a rule that checks two
    # things cannot report which one failed.
    assertion:
      parameter: Fire Rating
      operator: not_empty                 # see the operator table below
      # value: '120'                      # required by the comparing operators

    # WHAT TO DO about a failure. Optional: a set may be read-only by design.
    remediation:
      tool: horizun_write_params_verified
      arguments:
        parameter: Fire Rating
        value: '120'

    severity: blocking                    # blocking | advisory. Default: advisory.
```

### Operators

| Operator | Passes when | Needs `value` |
|---|---|---|
| `exists` / `not_exists` | the parameter is present / absent | no |
| `not_empty` | present and not blank | no |
| `equals` / `not_equals` | exact match, case-insensitive | yes |
| `matches` | regex over the stored text | yes |
| `in_list` | one of the listed values | yes (list) |
| `is_leaf_of` | the value is a last-level leaf of the named table | yes (table id) |
| `gt` / `gte` / `lt` / `lte` | numeric comparison against the raw stored value | yes |

`is_leaf_of` is what makes classification checkable without teaching the bridge any
classification system: the set carries the table, the bridge only asks whether a
value is a leaf of it.

### Tables

Classification systems arrive as data too, so OmniClass, Uniclass and a client
catalogue are the same shape:

```yaml
tables:
  - id: omniclass-22
    title: OmniClass Table 22
    # Either inline entries, or a path resolved relative to the set.
    source: ./omniclass-22.csv          # columns: code,title,parent
```

## What a finding must carry

Not a sentence. A finding is evidence, and the prose is composed above it:

```json
{
  "rule": "wall-fire-rating",
  "requirement_set": "iso-19650-stage-3",
  "version": "2.1.0",
  "outcome": "fails",
  "element": { "unique_id": "…", "category": "Walls", "type_name": "EXT-200" },
  "measured": { "parameter": "Fire Rating", "value": null },
  "severity": "blocking",
  "remediation": { "tool": "horizun_write_params_verified", "arguments": { "…": "…" } }
}
```

`outcome` is one of **`passes`**, **`fails`**, or **`unreadable`**. The third is not a
nicety: an element whose parameter could not be read has not passed and has not
failed, and collapsing it into either is the substitution this repository exists to
refuse. Coverage is reported alongside — how many elements the rule examined, and how
many it could not — so a clean report over half a model cannot read like a clean
report.

## Rules the loader enforces, and refuses on

A requirement set is input from outside, so it is validated like any other argument:

- **A rule with no selector is refused.** Not "warned about" — a rule that matches
  everything can drive a remediation across an entire model.
- **A rule with more than one assertion is refused**, because a failure could not say
  which half failed.
- **An unknown operator is refused**, naming the operator and listing the known ones.
  Silently skipping it would report a clean model.
- **A comparing operator with no `value` is refused.**
- **An unresolvable `tables` source is refused** at load, not at first use: a
  classification check that quietly passes because its table is missing is worse than
  no check.
- **A duplicate rule `id` is refused.** Findings are keyed by it.
- **An unknown top-level key is refused** (`additionalProperties: false`, as
  everywhere else here), so a typo is a refusal and not a rule nobody notices is
  missing.

## Three standards, one command — the acceptance test

Story 4.0 is done when these three exist as documents in `docs/requirement-sets/` and
`horizun_check_requirements` runs all three with no branch on which one it is:

- **`iso-19650.yaml`** — naming grammar (`matches`), stage-gated required properties
  (`not_empty` scoped by `scope.stage`), information-container fields.
- **`ifc-buildingsmart.yaml`** — class mapping completeness: every selected category
  asserts `not_equals IfcBuildingElementProxy` on its mapped class, plus `exists` on
  the mapping itself. This is the one an outside party can verify without opening
  Revit.
- **`cobie.yaml`** — handover completeness over Spaces, Types, Components and
  Systems: `not_empty` on each required field, reporting what is missing instead of
  emitting blank cells.

They ask different questions — naming, geometry mapping, data completeness — which is
exactly the point: the schema has to carry all three shapes without the C# knowing
which is which.

## Deliberately NOT in this schema

- **Scoring.** No weights, no percentages, no grades. A score hides which rule
  failed, and the delivery workflows that want one can compute it from the findings.
- **Prose for humans.** A finding carries evidence; the sentence is written above,
  where it can be written in the reader's language.
- **Severity philosophy.** `blocking` and `advisory` and nothing else. Which is which
  is the project's decision, declared in the set.
- **Fixes that are not typed commands.** A remediation names an existing verified
  command. A requirement set cannot ask the bridge to do something it has no verified
  command for — that would be a standard smuggling in behaviour.
