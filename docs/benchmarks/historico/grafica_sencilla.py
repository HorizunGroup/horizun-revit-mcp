"""Regenera grafica-sencilla.png desde operaciones.csv: operaciones completas en Revit por versión publicada.

Solo entran las versiones que salieron al público (VERSIONES). Se resaltan la Community v4.0.1 y la PRO 6.0.0.
Uso: python grafica_sencilla.py   (desde esta carpeta)
"""
import csv
import os

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

AQUI = os.path.dirname(os.path.abspath(__file__))
MESES = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"]

# Publicadas: v1.0.0 a v4.0.1 como releases de GitHub; 5.0.5 y 6.0.0 por la Suite.
# Fuera: 5.0.0 (la primera de la Suite fue la 5.0.1), 5.1.0 (solo un ZIP para un socio)
# y 0587ab68 / 8b83c2ec (commits, no versiones).
VERSIONES = ["v1.0.0", "v2.0.0", "v3.0.0", "v3.0.6", "v4.0.0", "v4.0.1", "5.0.5", "6.0.0"]
RESALTADAS = {
    "v4.0.1": ("Community v4.0.1", "#0072B2"),
    "6.0.0": ("PRO 6.0.0 oficial", "#C3461C"),
}
GRIS = "#D3D3D3"

plt.rcParams["font.family"] = "DejaVu Sans"

with open(os.path.join(AQUI, "operaciones.csv"), encoding="utf-8-sig") as f:
    filas = {r["version"]: r for r in csv.DictReader(f)}
faltan = [v for v in VERSIONES if v not in filas]
if faltan:
    raise SystemExit(f"faltan en operaciones.csv: {faltan}")
filas = [filas[v] for v in VERSIONES]

def pct(v):
    return f"{v:.1f} %".replace(".", ",")

x = list(range(len(filas)))
totales = [float(r["operaciones_pct"]) for r in filas]
colores = [RESALTADAS.get(r["version"], (None, GRIS))[1] for r in filas]
etiquetas = []
for r in filas:
    _, m, d = r["fecha"].split("-")
    etiquetas.append(f"{r['version']}\n{int(d)} {MESES[int(m) - 1]}")

fig, ax = plt.subplots(figsize=(16, 9), dpi=100)
ax.bar(x, totales, color=colores, width=0.62)

for i, (r, v) in enumerate(zip(filas, totales)):
    if r["version"] in RESALTADAS:
        nombre, color = RESALTADAS[r["version"]]
        ax.text(i, v + 2, nombre + "\n" + pct(v), ha="center", va="bottom", linespacing=1.2,
                fontsize=13, fontweight="bold", color=color)
    else:
        ax.text(i, v + 2, pct(v), ha="center", va="bottom", fontsize=11, color="#555555")

ax.set_ylim(0, 112)
ax.set_yticks(range(0, 101, 20))
ax.set_yticklabels([f"{t} %" for t in range(0, 101, 20)])
ax.set_xticks(x)
ax.set_xticklabels(etiquetas)
ax.set_xlabel("Versión publicada, en orden de versión, y fecha (2026)", labelpad=14)
ax.set_ylabel("Operaciones completas en Revit (%)")
ax.grid(axis="y", color="#eeeeee")
ax.set_axisbelow(True)
for s in ("top", "right", "left"):
    ax.spines[s].set_visible(False)
ax.spines["bottom"].set_color("#bbbbbb")
ax.tick_params(length=0)
fig.suptitle("Operaciones completas en Revit por versión publicada", x=0.085, ha="left", fontsize=24, fontweight="bold")
fig.text(0.085, 0.885, "Completa = todas las tareas de un encargo de cliente (16 encargos, ponderados por frecuencia) aprobadas en vivo en Revit 2025, sin ayuda humana ni Python,"
         + chr(10) + "cada una verificada releyendo el modelo o el archivo. Si falla un paso, no cuenta. Armado con tareas del banco corridas por separado, no como una sola corrida encadenada.",
         ha="left", va="top", fontsize=11, color="#444444")
fig.subplots_adjust(left=0.085, right=0.97, top=0.80, bottom=0.12)
fig.savefig(os.path.join(AQUI, "grafica-sencilla.png"))
