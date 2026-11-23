using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Horizun.Server
{
    internal static class ToolSearch
    {
        // Task vocabulary complements the English contract, without changing tool access.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            ["horizun_list_elements"] = "count contar cuantos cuantas cantidad listar elementos modelo model muros muro walls wall puertas puerta doors door ventanas windows",
            ["horizun_query_model"] = "buscar filtrar consultar consulta modelo niveles familias tipos cantidades parametros parametro comentarios comentario count filter query model level family type parameters parameter comments comment",
            ["horizun_health"] = "salud conexion estado activo connection status health",
            ["get_document_info"] = "informacion documento archivo proyecto contexto niveles nivel vistas vista plantas planta context levels level views view plans plan ids nombres elevacion elevation",
            ["horizun_capture_view"] = "capturar captura imagen vista vistas planta plantas corte cortes foto screenshot image view views plan plans section capture",
            ["horizun_manage_views"] = "create_sheet place_view crear hojas hoja planchas plancha laminas lamina colocar vistas vista plantas planta cortes corte secciones alzados sheet sheets viewport place view views floor plan section elevation",
            ["horizun_sheet_layout"] = "hojas hoja planchas plancha laminas lamina organizar distribuir alinear vistas sheet sheets layout viewport align arrange",
            ["horizun_query_planimetry"] = "listar consultar vistas vista plantas planta cortes corte hojas planchas laminas anotaciones etiquetas cotas list views view plans plan sections sheets annotations tags dimensions",
            ["horizun_navigate"] = "abrir activar navegar mostrar vistas vista plantas planta cortes corte open activate navigate show view views plan plans sections",
            ["horizun_annotate"] = "tag etiquetar etiquetas etiqueta puertas visibles anotar anotaciones anotacion acotar cotas cota texto notas tag doors visible annotate annotations dimensions dimension text notes",
            ["horizun_plan_annotations"] = "planificar etiquetas etiquetar anotaciones cotas puertas visibles plan tags tag annotations dimensions visible doors",
            ["horizun_quantities"] = "cantidades cantidad areas area metrados metrado medir computo computos quantities quantity areas area takeoff measurement volumes volumenes",
            ["horizun_create_elements"] = "crear modelar dibujar muro muros puertas elementos create model draw wall walls doors elements",
            ["horizun_write_params_verified"] = "editar cambiar escribir parametros parametro valores comentarios comentario observaciones write edit change parameters parameter values comments comment remarks",
            ["horizun_delete_verified"] = "borrar eliminar suprimir purgar elementos delete remove erase purge elements",
            ["horizun_export"] = "exportar pdf planos export sheets",
            ["horizun_list_schedules"] = "tablas planificacion cantidades areas metrados schedules quantities areas takeoff",
            ["horizun_target"] = "seleccionar revit version instancia select target instance"
        };

        private static string Normalize(string s) => new string((s ?? "").ToLowerInvariant()
            .Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

        public static List<ToolDef> Find(List<ToolDef> tools, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return tools;
            var stop = new HashSet<string> { "de", "del", "el", "la", "los", "las", "en", "un", "una", "y", "hay", "tiene", "the", "of", "in", "a", "and", "how", "many", "are", "there" };
            var words = Regex.Matches(Normalize(query), @"[a-z0-9]+")
                .Cast<Match>().Select(m => m.Value).Where(w => !stop.Contains(w)).Distinct().ToArray();
            if (words.Length == 0) return new List<ToolDef>();
            return tools.Select(t => new
                {
                    Tool = t,
                    // Curated tasks and identifiers outrank incidental words in long descriptions.
                    Score = Score(t, words)
                })
                .Where(r => r.Score > 0)
                .OrderByDescending(r => string.Equals(r.Tool.Name, query.Trim(), StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => r.Score)
                .ThenBy(r => r.Tool.Name, StringComparer.Ordinal)
                .Take(3).Select(r => r.Tool).ToList();
        }

        private static int Score(ToolDef tool, string[] words)
        {
            var tasks = Tokens(tool.Name + " " + (Aliases.TryGetValue(tool.Name, out string aliases) ? aliases : ""));
            var description = Tokens(tool.Description);
            int score = words.Sum(w => tasks.Contains(w) ? 10 :
                tasks.Any(t => w.Length >= 4 && t.StartsWith(w, StringComparison.Ordinal)) ? 5 :
                description.Contains(w) ? 1 : 0);
            // A bare drawing noun is a request to discover existing views, not to create them.
            if (words.Length == 1 && new[] { "planta", "plantas", "vista", "vistas", "corte", "cortes", "plan", "plans", "view", "views", "section", "sections" }.Contains(words[0]) &&
                new[] { "horizun_query_planimetry", "horizun_navigate", "horizun_capture_view" }.Contains(tool.Name)) score += 3;
            return score;
        }

        private static HashSet<string> Tokens(string value) => new HashSet<string>(
            Regex.Matches(Normalize(value), @"[a-z0-9]+").Cast<Match>().Select(m => m.Value));
    }
}
