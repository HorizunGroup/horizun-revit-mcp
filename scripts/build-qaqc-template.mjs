import fs from "node:fs/promises";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const outputDir = "examples";
await fs.mkdir(outputDir, { recursive: true });

const wb = Workbook.create();
const summary = wb.worksheets.add("Resumen QAQC");
const findings = wb.worksheets.add("Hallazgos");
const readme = wb.worksheets.add("Uso");

for (const sheet of [summary, findings, readme]) {
  sheet.showGridLines = false;
  sheet.getRange("A1:K60").format.font = { name: "Arial", size: 10, color: "#1B2A38" };
}

summary.getRange("A2:H2").merge();
summary.getRange("A2").values = [["Informe QA/QC BIM — plantilla de muestra"]];
summary.getRange("A2:H2").format = { font: { name: "Arial", size: 16, bold: true, color: "#12324A" } };
summary.getRange("A4:B8").values = [
  ["Modelo", "Completar con el documento auditado"],
  ["Fecha", "Completar con la fecha de auditoría"],
  ["Workflow", "Model Health Audit"],
  ["Perfil", "Completar con el perfil o estándar"],
  ["Cobertura", "Completar con la cobertura medida"]
];
summary.getRange("A4:A8").format = { fill: "#E8F1F5", font: { bold: true, color: "#12324A" } };
summary.getRange("B4:B8").format = { fill: "#FFF4CC", font: { color: "#6B5300" } };
summary.getRange("A4:B8").format.borders = { preset: "all", style: "thin", color: "#C9D8E0" };
summary.getRange("A11:D11").values = [["Estado", "Cantidad", "Significado", "Acción requerida"]];
summary.getRange("A12:D14").values = [
  ["Bloqueante", 0, "Impide declarar el alcance conforme", "Resolver o aceptar con evidencia"],
  ["Asesoría", 0, "Revisión recomendada", "Asignar responsable"],
  ["Desconocido", 0, "No se pudo medir todo el alcance", "Corregir cobertura y volver a auditar"]
];
summary.getRange("A11:D11").format = { fill: "#12324A", font: { bold: true, color: "#FFFFFF" }, horizontalAlignment: "center" };
summary.getRange("A11:D14").format.borders = { preset: "all", style: "thin", color: "#C9D8E0" };
summary.getRange("B12:B14").format.numberFormat = "#,##0";
summary.getRange("A16:H16").merge();
summary.getRange("A16").values = [["Esta plantilla no contiene resultados de un modelo. Copie únicamente hallazgos, cobertura y evidencia emitidos por Horizun."]];
summary.getRange("A16:H16").format = { fill: "#F4F7F9", font: { italic: true, color: "#526775" } };
summary.getRange("A1:H20").format.wrapText = true;
summary.getRange("A:D").format.autofitColumns();
summary.getRange("A").format.columnWidth = 18;
summary.getRange("B").format.columnWidth = 28;
summary.getRange("C").format.columnWidth = 30;
summary.getRange("D").format.columnWidth = 34;

findings.getRange("A2:H2").merge();
findings.getRange("A2").values = [["Hallazgos QA/QC"]];
findings.getRange("A2:H2").format = { font: { name: "Arial", size: 14, bold: true, color: "#12324A" } };
findings.getRange("A4:H4").values = [["ID", "Workflow", "Severidad", "Elemento / vista", "Hallazgo", "Cobertura", "Evidencia", "Responsable"]];
findings.getRange("A5:H5").values = [["Ejemplo-001", "Model Health Audit", "Desconocido", "—", "Reemplace esta fila con un hallazgo medido", "Incompleta", "Respuesta MCP / recibo", "Sin asignar"]];
findings.getRange("A4:H4").format = { fill: "#12324A", font: { bold: true, color: "#FFFFFF" }, horizontalAlignment: "center" };
findings.getRange("A4:H5").format.borders = { preset: "all", style: "thin", color: "#C9D8E0" };
findings.getRange("A5:H5").format = { fill: "#FFF4CC", font: { color: "#6B5300" } };
findings.getRange("A:H").format.autofitColumns();
findings.getRange("E").format.columnWidth = 42;
findings.getRange("G").format.columnWidth = 28;
findings.freezePanes.freezeRows(4);

readme.getRange("A2:F2").merge();
readme.getRange("A2").values = [["Cómo usar esta plantilla"]];
readme.getRange("A2:F2").format = { font: { name: "Arial", size: 14, bold: true, color: "#12324A" } };
readme.getRange("A4:B8").values = [
  ["1", "Ejecute un workflow read-only o un dry run en Horizun."],
  ["2", "Copie el modelo, perfil, cobertura y hallazgos verificables."],
  ["3", "Mantenga 'Desconocido' como estado, no como una aprobación."],
  ["4", "Asigne responsables y vuelva a medir tras las correcciones."],
  ["5", "Conserve el recibo o respuesta MCP como evidencia de origen."]
];
readme.getRange("A4:A8").format = { fill: "#E8F1F5", font: { bold: true, color: "#12324A" }, horizontalAlignment: "center" };
readme.getRange("A4:B8").format.borders = { preset: "all", style: "thin", color: "#C9D8E0" };
readme.getRange("A:B").format.autofitColumns();
readme.getRange("B").format.columnWidth = 75;

wb.recalculate();
const check = await wb.inspect({ kind: "table", range: "Resumen QAQC!A2:D16", include: "values,formulas", tableMaxRows: 20, tableMaxCols: 8 });
if (!check.ndjson.includes("Informe QA/QC BIM")) throw new Error("Template title was not written.");
const errors = await wb.inspect({ kind: "match", searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A|#NUM!", options: { useRegex: true, maxResults: 20 } });
if (errors.ndjson.includes("#REF!") || errors.ndjson.includes("#DIV/0!")) throw new Error("Formula error found.");
const preview = await wb.render({ sheetName: "Resumen QAQC", range: "A1:H20", scale: 1 });
await fs.writeFile(`${outputDir}/qaqc-report-template-preview.png`, new Uint8Array(await preview.arrayBuffer()));
const file = await SpreadsheetFile.exportXlsx(wb);
await file.save(`${outputDir}/qaqc-report-template.xlsx`);
