"""Operaciones completas en Revit: cada operación es un encargo de punta a punta armado con tareas del banco.

Las tareas de una operación se ejecutaron cada una por separado sobre copias nuevas (no es una corrida encadenada).
Una operación cuenta como completa solo si TODAS sus tareas se aprobaron en vivo en Revit 2025, sin Python
(necesito_python falso en cada una) y con el resultado verificado releyendo el modelo o el archivo, que es lo
que exige cada tarea del banco. Uso: python operaciones.py <resultado.json>...  (imprime una fila CSV por archivo)
"""
import json
import sys

# (clave, nombre, peso por lo que hace un cliente: 3 = cada semana, 2 = cada proyecto, 1 = ocasional, tareas)
OPERACIONES = [
    ("arq_basica", "Modelar arquitectura: muros, puertas, ventanas, pisos, cielos, cubierta y recintos", 3,
     ["ARQ-01", "ARQ-02", "ARQ-03", "ARQ-04", "ARQ-05", "ARQ-06", "ARQ-12", "ARQ-13"]),
    ("arq_edicion", "Corregir arquitectura existente: mover, rotar, cambiar tipo, alargar y borrar verificado", 2,
     ["ARQ-10", "ARQ-11", "ARQ-16", "ARQ-17", "ARQ-18", "ARQ-19", "ARQ-27", "ARQ-28"]),
    ("acabados", "Acabados de muros y pisos por recinto", 1,
     ["ARQ-07", "ARQ-08", "ARQ-09", "ARQ-14", "CAN-14"]),
    ("planos_pdf", "Set de planos: plantas y cortes en hojas, etiquetas y cotas, exportado a PDF y revisado", 3,
     ["DOC-01", "DOC-02", "DOC-03", "DOC-04", "DOC-06", "DOC-08", "DOC-09", "DOC-10", "DOC-11"]),
    ("anotacion", "Anotar y emitir revisiones: notas, nubes, revisiones y cotas verificadas en el PDF", 2,
     ["DOC-07", "DOC-12", "DOC-13", "DOC-18", "DOC-21", "DOC-25", "DOC-26"]),
    ("cantidades", "Cantidades del modelo exportadas a tabla (CSV) y cuadradas con el modelo", 3,
     ["CAN-01", "CAN-03", "CAN-05", "CAN-06", "CAN-10", "CAN-13"]),
    ("tablas", "Tablas de planificación: leer las existentes y crear puertas, ventanas y materiales", 1,
     ["CAN-07", "CAN-08", "CAN-09", "CAN-11", "CAN-12"]),
    ("mep_dwg", "Red sanitaria desde DWG: tubos con pendiente y todas las uniones conectadas, también las tees oblicuas, y guardado", 2,
     ["MEP-01", "MEP-02", "MEP-03", "MEP-04", "MEP-05", "MEP-06", "MEP-07", "MEP-08", "MEP-09", "MEP-10", "MEP-12"]),
    ("familia_param", "Familias: crear paramétricas y cargarlas al proyecto, y colocar una familia cargada desde RFA", 2,
     ["FAM-01", "FAM-02", "FAM-05", "FAM-08", "FAM-16"]),
    ("homologar", "Homologar una familia: parámetro compartido, guardar el RFA y recargarla", 1,
     ["FAM-10", "FAM-12", "FAM-13"]),
    ("estructura", "Estructura por ejes: columnas, vigas y refuerzo auditado", 2,
     ["EST-01", "EST-03", "EST-04", "EST-05c", "EST-06c", "EST-08c", "EST-09c", "EST-10c"]),
    ("ifc", "Entrega IFC4 verificada con su contenido", 2,
     ["DIN-01", "DIN-02"]),
    ("federacion", "Federar vínculos y encontrar interferencias", 2,
     ["VIN-01", "VIN-02", "VIN-03", "VIN-06", "VIN-07", "MEP-11"]),
    ("export_dwg", "Exportar planos a DWG e imágenes", 1,
     ["DIN-08", "DIN-13", "DIN-15", "DOC-15"]),
    ("proyecto_nuevo", "Proyecto nuevo desde plantilla verificado, y cambio con guardado verificado en el modelo de trabajo", 1,
     ["INF-14", "INF-06", "INF-18"]),
    ("nube", "Nube de puntos E57 a muros", 1,
     ["DIN-09", "DIN-10"]),
]


def medir(path):
    d = json.load(open(path, encoding="utf-8"))
    tareas = {t["id"]: t for a in d["ambitos"].values() for t in a["tareas"]}
    res = {}
    for clave, _, _, ids in OPERACIONES:
        faltan = [i for i in ids if i not in tareas]
        if faltan:
            raise SystemExit(f"{path}: la operación {clave} pide tareas que no están: {faltan}")
        res[clave] = all(tareas[i]["estado"] == "aprobada" and not tareas[i].get("necesito_python") for i in ids)
    peso_total = sum(o[2] for o in OPERACIONES)
    pct = 100.0 * sum(o[2] for o in OPERACIONES if res[o[0]]) / peso_total
    return res, pct


if __name__ == "__main__":
    for p in sys.argv[1:]:
        res, pct = medir(p)
        print(p, f"{pct:.1f}", sum(res.values()), len(OPERACIONES),
              " ".join(k for k, v in res.items() if not v))
