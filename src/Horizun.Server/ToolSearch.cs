// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// TOOL SEARCH for the schema facade (horizun_list_tools { query }). A small model that
// sees only four tools needs to find the right one by DESCRIBING the task ("cuantos muros
// hay", "delete element", "capturar vista"), not by guessing a name. This file turns a
// free-text query into a ranked list of tools. It is deterministic, offline and has no
// model in it:
//
//   1. NORMALISE   lower case, accents removed (a, n with tilde -> plain), punctuation out,
//                  stopwords (ES + EN), numbers and tokens shorter than 3 letters out.
//   2. STEM        a deliberately crude plural/infinitive trimmer (muros -> muro, borrar ->
//                  borra, habitaciones -> habitacion). It is applied to the query, to the
//                  tool texts AND to the synonym table, so all three always agree.
//   3. EXPAND      every query word is widened by the SYNONYMS table below (bilingual).
//                  The word the user typed weighs more than its synonyms.
//   4. SCORE       per query word, the best hit counts: in the tool NAME (split on '_') the
//                  most, then in the tool's toolsets, then in its description weighed by how
//                  rare the word is across all tools (a word in every description says
//                  nothing). Words add up, so a tool matching both the verb and the object
//                  of the task beats one matching only the object.
//
// HOW TO EXTEND THE VOCABULARY. Add (or widen) one line in Synonyms: a group is a set of
// words that mean the same thing for a Revit task, Spanish and English mixed, any case,
// accents optional. One word may sit in several groups (a "view" is also a "plan"). A new
// tool needs NO entry here: its own name, toolsets and description are already searched;
// add a group only when users will call it by a word that appears in none of those. Then
// add a case to ToolSearchTests so the word stays covered.
//
// WHAT THIS DOES NOT DO. It never decides what may be called. The caller hands it two
// lists it built from the same gates tools/list uses (callable now; permitted but outside
// the active toolsets); this only ranks them.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Horizun.Server
{
    internal static class ToolSearch
    {
        /// <summary>At most this many callable matches are returned.</summary>
        internal const int MaxMatches = 8;

        /// <summary>At most this many "exists but is switched off" matches are returned.</summary>
        internal const int MaxNotActive = 5;

        // Weights. The name is what the model will type, so it dominates.
        private const double NameWeight = 6.0;
        private const double ToolsetWeight = 1.5;
        private const double DescriptionWeight = 2.0;
        private const double SynonymFactor = 0.9;
        private const double MinScore = 1.0;

        // ---- vocabulary ---------------------------------------------------------------------
        // One group per line. Words are stemmed when the table is built.
        private static readonly string[] Synonyms =
        {
            // --- objects of a Revit model
            "muro muros wall walls pared paredes tabique",
            "puerta puertas door doors",
            "ventana ventanas window windows",
            "nivel niveles level levels cota planta_nivel storey story",
            "habitacion habitaciones room rooms espacio espacios space spaces ambiente recinto",
            "suelo piso losa losas floor floors slab slabs entrepiso placa",
            "techo cubierta roof ceiling cielorraso",
            "tuberia tuberias tubo tubos pipe pipes piping plomeria plumbing hidraulica sanitaria",
            "ducto ductos duct ducts conducto hvac climatizacion ventilacion",
            "cable cables bandeja conduit electrical electrico electrica electricidad luminaria",
            "viga vigas columna columnas beam beams column columns estructura estructural structure structural cimentacion foundation",
            "armadura refuerzo reinforcement rebar acero barra barras",
            "escalera escaleras stair stairs baranda barandilla railing",
            "familia familias family families tipo tipos type types simbolo",
            "grupo grupos group groups",
            "material materiales material materials acabado acabados finish finishes",
            "vinculo vinculos link links enlace enlaces rvt_link",
            "fase fases phase phases",
            "taller worksets workset subproyecto",
            "elemento elementos element elements objeto objetos entidad id ids",
            "modelo model documento document proyecto project archivo",
            "seleccion seleccionado seleccionados selection selected",
            "tabla tablas schedule schedules planilla listado",
            "hoja hojas sheet sheets lamina laminas plano_hoja",
            "revision revisiones revision revisions",
            "cad dwg dxf autocad",
            "ifc intercambio exchange interoperabilidad",
            "excel xlsx hoja_calculo",
            "toposolido toposolid topografia topography terreno terrain",
            "cortina curtain fachada",
            "ensamblaje ensamblajes assembly assemblies parte partes parts",
            "dimension dimensiones cota_dim dimensions acotar cotas",
            "etiqueta etiquetas tag tags anotacion anotaciones annotation annotations texto",
            "detalle detalles detail details linea_detalle",
            "union unir join joined geometria geometry",

            // --- verbs: what the user wants to do
            "*borrar borra borre eliminar elimina elimine eliminado suprimir quitar quita remover delete remove erase supr",
            "*crear crea cree creado nuevo nueva nuevos nuevas anadir agregar agrega anade add create new make hacer generar genera generate colocar coloca place insert insertar modelar modela",
            "*mover mueve desplazar desplaza move translate transform transformar transforma rotar rota rotate espejo mirror",
            "*copiar copia copy duplicar duplica duplicate",
            "*cambiar cambia cambie modificar modifica editar edita actualizar actualiza escribir escribe asignar asigna poner pon fijar set change update edit modify write assign rename renombrar",
            "parametro parametros parameter parameters param params propiedad propiedades property properties atributo atributos comentario comentarios comment comments valor valores value values campo campos field fields marca mark",
            "*contar cuenta cuantos cuantas cuanto count total how many listar lista lista list listado enumerar numero",
            "*consultar consulta query buscar busca find search leer lee read obtener obtiene get ver mostrar muestra show filtrar filtro filter inspeccionar inspect saber info informacion",
            "vista vistas view views plano planos plan plans seccion secciones section sections alzado elevacion elevation 3d camara",
            "*capturar captura capture imagen imagenes image images screenshot pantallazo foto render snapshot",
            "*exportar exporta export exportacion descargar guardar_como pdf",
            "*importar importa import cargar carga load",
            "*deshacer deshaz undo revertir revierte revert rollback restaurar",
            "*salud estado health status diagnostico diagnostic conexion connection ping",
            "*interferencia interferencias clash clashes choque choques colision colisiones conflicto conflictos interference",
            "*cantidad cantidades quantity quantities takeoff medicion mediciones metrado computo cubicar",
            "presupuesto presupuestos budget costo costos cost",
            "concreto hormigon concrete cemento",
            "*auditar audita audit auditoria revisar revisa review verificar verifica verify validar valida validate comprobar check calidad quality",
            "guardar guarda save sincronizar sync",
            "ejecutar ejecuta run execute script python codigo code",
            "aplicar aplica apply plan planificar planificar ensayo dry_run simular simulate",
            "*medir mide measure longitud length area volumen volume distancia distance",
            "conectar conecta connect conexion enrutar ruta routing route unir_mep",
            "*dividir divide split separar separa partir capa capas layer layers multicapa",
            "reunion fusionar merge combinar combine agrupar agrupa regroup",
            "comparar compara compare diferencia diferencias diff",
            "abrir abre open navegar navega navigate activar activa activate",
            "nombre nombres name names clasificacion clasificar classification classify codigo_clasif keynote omniclass",
            "visibilidad visible ocultar oculta hide mostrar_ocultar visibility isolate aislar",
            "recortar recorte crop region",
            "paquete lote batch trabajo job tarea task estado_trabajo",
            "ayuda help api documentacion_api docs",
            "familia_editar internals interior interno",
        };

        private static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.Ordinal)
        {
            // Spanish
            "para", "por", "con", "sin", "los", "las", "del", "una", "uno", "unos", "unas", "que", "como", "esto",
            "esta", "estos", "estas", "este", "hay", "son", "mis", "sus", "todo", "todos", "todas", "pero", "mas",
            "muy", "desde", "hasta", "entre", "sobre", "ese", "esa", "esos", "esas", "cada", "ser", "tiene",
            "tener", "quiero", "necesito", "favor", "puedes", "puede", "donde", "cual", "cuales", "algun",
            "alguna", "algunos", "algunas", "tambien", "estan", "esta", "estoy", "tengo", "nos", "les", "pues",
            "mientras", "ahora", "aqui", "alli", "cuando", "porque", "solo", "yo", "tus",
            // English
            "the", "and", "for", "with", "from", "that", "this", "these", "those", "are", "was", "were", "what",
            "into", "onto", "all", "any", "each", "can", "you", "your", "have", "has", "not", "but", "use",
            "using", "want", "need", "please", "which", "there", "their", "them", "then", "than", "also", "how",
            "does", "did", "would", "could", "should", "will", "about", "over", "out", "our", "its", "who", "when",
            "where", "some", "one", "just", "like",
        };

        // ---- built once ---------------------------------------------------------------------

        // word -> (other word of one of its groups -> strength of the strongest such group).
        // A leading star on a group marks a STRONG one: an intent (delete, create, count, health,
        // clash...) that outweighs the object nouns around it ("crea un muro" is about creating).
        private const double StrongBoost = 1.6;

        private static readonly Lazy<Dictionary<string, Dictionary<string, double>>> _expansion =
            new Lazy<Dictionary<string, Dictionary<string, double>>>(BuildExpansion);

        private static Dictionary<string, Dictionary<string, double>> BuildExpansion()
        {
            var map = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach (string raw in Synonyms)
            {
                bool strong = raw.StartsWith("*", StringComparison.Ordinal);
                double boost = strong ? StrongBoost : 1.0;
                string[] words = raw.TrimStart('*').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                     .SelectMany(w => Words(w.Replace('_', ' ')))
                                     .Select(Stem).Where(w => w.Length > 0).Distinct().ToArray();
                foreach (string w in words)
                {
                    if (!map.TryGetValue(w, out Dictionary<string, double> set))
                        map[w] = set = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (string other in words)
                        if (!set.TryGetValue(other, out double old) || old < boost) set[other] = boost;
                }
            }
            return map;
        }

        // ---- normalisation ------------------------------------------------------------------

        /// <summary>Lower case, accents removed.</summary>
        internal static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string d = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        /// <summary>Letters and digits runs of the folded text; everything else separates.</summary>
        private static IEnumerable<string> Words(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in Fold(text))
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        /// <summary>
        /// Crude trimmer, the same on both sides of every comparison: plurals (muros,
        /// habitaciones, niveles, paredes, walls) and Spanish infinitives (borrar -> borra).
        /// </summary>
        internal static string Stem(string w)
        {
            if (w.Length <= 3) return w;
            if (w.EndsWith("es", StringComparison.Ordinal) && w.Length > 5 &&
                (w.EndsWith("iones", StringComparison.Ordinal) || w.EndsWith("ores", StringComparison.Ordinal) ||
                 w.EndsWith("ades", StringComparison.Ordinal) || w.EndsWith("udes", StringComparison.Ordinal) ||
                 w.EndsWith("ides", StringComparison.Ordinal) || w.EndsWith("edes", StringComparison.Ordinal) ||
                 w.EndsWith("eles", StringComparison.Ordinal) || w.EndsWith("ales", StringComparison.Ordinal) ||
                 w.EndsWith("oles", StringComparison.Ordinal)))
                return w.Substring(0, w.Length - 2);
            if (w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal) && w.Length > 3)
                w = w.Substring(0, w.Length - 1);
            if (w.Length >= 5 && (w.EndsWith("ar", StringComparison.Ordinal) || w.EndsWith("er", StringComparison.Ordinal) ||
                                  w.EndsWith("ir", StringComparison.Ordinal)))
                w = w.Substring(0, w.Length - 1);
            return w;
        }

        /// <summary>The meaningful, stemmed words of free text: no stopwords, numbers or tokens under 3 letters.</summary>
        internal static List<string> Terms(string text)
        {
            var result = new List<string>();
            foreach (string w in Words(text))
            {
                if (w.Length < 3 || Stopwords.Contains(w) || w.All(char.IsDigit)) continue;
                string s = Stem(w);
                if (!result.Contains(s)) result.Add(s);
            }
            return result;
        }

        // ---- ranking --------------------------------------------------------------------------

        internal sealed class Hit
        {
            public ToolDef Tool;
            public double Score;
        }

        /// <summary>
        /// Rank <paramref name="candidates"/> against <paramref name="terms"/> (from
        /// <see cref="Terms"/>). <paramref name="corpus"/> is every tool (callable or not):
        /// it only decides how rare a description word is.
        /// </summary>
        internal static List<Hit> Rank(IReadOnlyList<string> terms, IEnumerable<ToolDef> candidates,
                                       IReadOnlyCollection<ToolDef> corpus, int max)
        {
            // Per tool, the three bags of stemmed words.
            var bags = new Dictionary<ToolDef, (HashSet<string> name, HashSet<string> sets, HashSet<string> desc)>();
            var docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ToolDef t in corpus.Concat(candidates).Distinct())
            {
                var name = new HashSet<string>(Words((t.Name ?? "").Replace("horizun_", "").Replace('_', ' ')).Select(Stem));
                var sets = new HashSet<string>((t.Toolsets ?? new string[0]).SelectMany(s => Words(s.Replace('_', ' '))).Select(Stem));
                var desc = new HashSet<string>(Words(t.Description ?? "").Select(Stem));
                bags[t] = (name, sets, desc);
            }
            foreach (ToolDef t in corpus)
                foreach (string w in bags[t].desc.Concat(bags[t].name))
                    docFreq[w] = (docFreq.TryGetValue(w, out int n) ? n : 0) + 1;
            double total = Math.Max(1, corpus.Count);

            double Idf(string w)
            {
                docFreq.TryGetValue(w, out int df);
                // 1.0 for a word in one tool, towards 0 for a word in all of them.
                return Math.Max(0.05, Math.Log((total + 1) / (df + 1)) / Math.Log(total + 1) * 1.6);
            }

            // Each query word, with the words it may be found as and how much each is worth.
            var expanded = new List<List<(string word, double w)>>();
            foreach (string term in terms)
            {
                double self = 1.0;
                var forms = new List<(string, double)>();
                if (_expansion.Value.TryGetValue(term, out Dictionary<string, double> syn))
                {
                    if (syn.TryGetValue(term, out double b0)) self = b0;
                    foreach (var kv in syn) if (kv.Key != term) forms.Add((kv.Key, SynonymFactor * kv.Value));
                }
                forms.Add((term, self));
                expanded.Add(forms);
            }

            var hits = new List<Hit>();
            foreach (ToolDef t in candidates)
            {
                var (name, sets, desc) = bags[t];
                // Every word of the tool is credited ONCE, at the best value any query word
                // gives it: "estado" and "salud" are the same idea and must not add up twice.
                var credit = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var forms in expanded)
                {
                    string bestKey = null; double best = 0;
                    foreach (var (word, w) in forms)
                    {
                        if (name.Contains(word) && NameWeight * w > best) { best = NameWeight * w; bestKey = "n:" + word; }
                        if (sets.Contains(word) && ToolsetWeight * w > best) { best = ToolsetWeight * w; bestKey = "s:" + word; }
                        if (desc.Contains(word))
                        {
                            double v = DescriptionWeight * w * Math.Min(1.0, Idf(word) + 0.2);
                            if (v > best) { best = v; bestKey = "d:" + word; }
                        }
                    }
                    if (bestKey != null && (!credit.TryGetValue(bestKey, out double have) || have < best)) credit[bestKey] = best;
                }
                double score = credit.Values.Sum();
                if (score > 0)
                {
                    // Tie-breaker: the tool whose name is MOSTLY what was asked (health for
                    // "salud", not acc_upload_status) beats one that merely contains the word.
                    int namedHits = credit.Keys.Count(k => k.StartsWith("n:", StringComparison.Ordinal));
                    score += 2.0 * Math.Min(namedHits, name.Count) / Math.Max(1, name.Count);
                }
                if (score >= MinScore) hits.Add(new Hit { Tool = t, Score = Math.Round(score, 2) });
            }
            return hits.OrderByDescending(h => h.Score)
                       .ThenBy(h => h.Tool.Name, StringComparer.Ordinal)
                       .Take(max).ToList();
        }
    }
}
