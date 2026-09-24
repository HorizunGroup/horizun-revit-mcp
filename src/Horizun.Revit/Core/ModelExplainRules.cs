// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A MODEL, EXPLAINED FROM WHAT WAS MEASURED. horizun_model_diff operation=explain
// gathers facts (counts by category, discipline and level, links, worksets,
// phases, the last recorded quality run) and this file turns them into sentences.
// Every sentence is built from a fact that is present; a fact that is absent
// produces a sentence saying it is absent, never a guess. Revit-free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ModelExplainRules
    {
        /// <summary>The top N entries of a {name: count} object, largest first, ties by name.</summary>
        public static List<KeyValuePair<string, long>> Top(JObject counts, int n)
        {
            var list = new List<KeyValuePair<string, long>>();
            if (counts == null) return list;
            foreach (JProperty p in counts.Properties())
                if (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float)
                    list.Add(new KeyValuePair<string, long>(p.Name, p.Value.Value<long>()));
            return list.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(n).ToList();
        }

        private static string Join(List<KeyValuePair<string, long>> items)
            => string.Join(", ", items.Select(x => x.Key + " (" + x.Value.ToString(CultureInfo.InvariantCulture) + ")"));

        public static JObject Narrate(JObject f)
        {
            var en = new List<string>();
            var es = new List<string>();
            string title = f.Value<string>("title") ?? "(untitled)";
            long elements = f.Value<long?>("model_elements") ?? 0;
            en.Add("'" + title + "' holds " + elements + " model elements.");
            es.Add("'" + title + "' contiene " + elements + " elementos de modelo.");

            var disciplines = Top(f["by_discipline"] as JObject, 8);
            if (disciplines.Count > 0)
            {
                en.Add("By discipline (inferred from category): " + Join(disciplines) + ".");
                es.Add("Por disciplina (inferida de la categoría): " + Join(disciplines) + ".");
            }
            var cats = Top(f["by_category"] as JObject, 5);
            if (cats.Count > 0)
            {
                en.Add("Largest categories: " + Join(cats) + ".");
                es.Add("Categorías principales: " + Join(cats) + ".");
            }
            var levels = f["by_level"] as JObject;
            if (levels != null && levels.Count > 0)
            {
                en.Add(levels.Count + " level group(s) carry elements; most populated: " + Join(Top(levels, 3)) + ".");
                es.Add(levels.Count + " nivel(es) con elementos; los más poblados: " + Join(Top(levels, 3)) + ".");
            }

            JObject links = f["links"] as JObject;
            if (links != null)
            {
                long total = links.Value<long?>("total") ?? 0, loaded = links.Value<long?>("loaded") ?? 0;
                en.Add(total == 0 ? "No Revit links." : total + " Revit link(s), " + loaded + " loaded.");
                es.Add(total == 0 ? "Sin vínculos de Revit." : total + " vínculo(s) de Revit, " + loaded + " cargado(s).");
            }

            JObject ws = f["worksets"] as JObject;
            if (ws != null)
            {
                if (ws.Value<bool?>("workshared") != true)
                { en.Add("Not workshared."); es.Add("No es un modelo colaborativo (sin worksets)."); }
                else
                {
                    long user = ws.Value<long?>("user") ?? 0, open = ws.Value<long?>("open") ?? 0;
                    en.Add("Workshared: " + user + " user workset(s), " + open + " open" +
                           (open < user ? " - elements on closed worksets were NOT counted." : "."));
                    es.Add("Colaborativo: " + user + " workset(s) de usuario, " + open + " abierto(s)" +
                           (open < user ? " - los elementos en worksets cerrados NO se contaron." : "."));
                }
            }

            JArray phases = f["phases"] as JArray;
            if (phases != null)
            {
                string names = string.Join(", ", phases.Select(p => (string)p));
                en.Add(phases.Count + " phase(s)" + (phases.Count > 0 ? ": " + names + "." : "."));
                es.Add(phases.Count + " fase(s)" + (phases.Count > 0 ? ": " + names + "." : "."));
            }

            JObject q = f["last_quality"] as JObject;
            if (q == null || q.Value<string>("status") != "recorded")
            {
                en.Add("No quality run is recorded for this project; run record_quality to start the history.");
                es.Add("No hay ninguna corrida de calidad registrada para este proyecto; usa record_quality para iniciar el historial.");
            }
            else
            {
                string when = q.Value<string>("recorded_utc");
                bool complete = q.Value<bool?>("complete") == true;
                en.Add("Last quality run (" + q.Value<string>("source") + ") on " + when + ", " +
                       (complete ? "complete coverage" : "INCOMPLETE coverage - its counts are lower bounds") + ".");
                es.Add("Última corrida de calidad (" + q.Value<string>("source") + ") el " + when + ", " +
                       (complete ? "cobertura completa" : "cobertura INCOMPLETA - sus conteos son cotas inferiores") + ".");
            }
            return new JObject { ["en"] = string.Join(" ", en), ["es"] = string.Join(" ", es) };
        }

        /// <summary>The most recent record in a history, as the explain block shows it.</summary>
        public static JObject LastQuality(QualityReadResult history)
        {
            if (history == null || !history.FileExists || history.Records.Count == 0)
                return new JObject { ["status"] = "none_recorded", ["malformed_lines"] = history?.MalformedLines ?? 0 };
            JObject last = history.Records.OrderBy(r => r.Value<string>("recorded_utc"), StringComparer.Ordinal).Last();
            return new JObject
            {
                ["status"] = "recorded",
                ["recorded_utc"] = last["recorded_utc"],
                ["source"] = last["source"],
                ["complete"] = last["complete"],
                ["failed"] = last["failed"],
                ["metrics"] = last["metrics"],
                ["records_in_history"] = history.Records.Count,
                ["malformed_lines"] = history.MalformedLines
            };
        }
    }
}
