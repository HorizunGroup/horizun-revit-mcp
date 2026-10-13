# Reinforcement from a detail schedule / Refuerzo desde una cartilla

**[English](#english) · [Español](#español)**

## English

A complete, synthetic flow from a detail schedule to verified reinforcement:

1. `01-read-schedule.json` — read `cartilla-sintetica.xlsx` (sheet *Cartilla*: one row per member; sheet *Despiece*: the bar list).
2. `02-plan-reinforcement.json` — one **section rule** per member mark (`V-101`, `V-102`, `C-1`): faces, counts, bar types and a perimeter stirrup. Read-only; it shows the rules each member expands into.
3. `03-apply-reinforcement.json` — rehearse, then apply with the token.
4. `04-audit-reinforcement.json` — the model against the set.
5. `05-reconcile-quantities.json` — the model against the schedule's bar list, mark by mark.
6. `06-plan-bastones-and-laps.json` — bastones from each end over a quarter of the measured length, and a bottom row spliced at mid-length by a lap of 40 bar diameters (the multiplier is the example's, never a code's). Apply and audit it as in steps 3 and 4: the audit lists each lap in `laps` with the overlap measured off the model.

Everything here is synthetic: marks, sections, bars and weights are placeholders. Turning a real schedule's notation (`3#5`, `E#3 c/0.10`) into section rules is a reading step — by a person or a deterministic interpreter, with review — not something this bridge guesses. Bar types and hooks must exist in your model under the names the set uses. See `docs/STRUCTURAL-REQUIREMENT-SET.md`, section *Section rules*.

Measured live (Revit 2026, 2026-10-08): two beams of one mark, one straight and one turned 30° in plan, planned, applied and re-read, longitudinal rows exactly where the rule put them; a column cage and a footing mat planned, applied, audited and reconciled. Open: hooked closed stirrups are reported by the audit as an open bar (the apply verifies them), and bars Revit numbers alike share a mark.

## Español

Un flujo completo y sintético, de una cartilla de despiece a un refuerzo verificado:

1. `01-read-schedule.json` — leer `cartilla-sintetica.xlsx` (hoja *Cartilla*: una fila por elemento; hoja *Despiece*: la lista de barras).
2. `02-plan-reinforcement.json` — una **section rule** por marca de elemento (`V-101`, `V-102`, `C-1`): caras, cantidades, tipos de barra y un estribo perimetral. Solo lectura; muestra las reglas en que se expande cada elemento.
3. `03-apply-reinforcement.json` — ensayo y luego aplicación con el token.
4. `04-audit-reinforcement.json` — el modelo contra el set.
5. `05-reconcile-quantities.json` — el modelo contra el despiece, marca por marca.
6. `06-plan-bastones-and-laps.json` — bastones desde cada extremo sobre un cuarto de la longitud medida y una fila inferior empalmada a media luz con un traslapo de 40 diámetros (el multiplicador es del ejemplo, nunca de una norma). Se aplica y audita como en los pasos 3 y 4: la auditoría lista cada traslapo en `laps` con el solape medido sobre el modelo.

Todo es sintético: marcas, secciones, barras y pesos son marcadores. Convertir la notación de una cartilla real (`3#5`, `E#3 c/0.10`) en section rules es un paso de lectura —de una persona o de un intérprete determinista, con revisión—, no algo que este puente adivine. Los tipos de barra y ganchos deben existir en su modelo con los nombres que usa el set. Ver `docs/STRUCTURAL-REQUIREMENT-SET.md`, sección *Section rules*.
