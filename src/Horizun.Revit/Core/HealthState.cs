// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_health ALWAYS ANSWERS. It is the first call of every session, and field
// data (five teams, fourteen days) showed it failing in 23 % of session starts -
// when Revit was still loading, sitting on a dialog, or had no document yet - after
// which the agent retried up to three times, blind, because a thrown error says
// "something failed" and nothing about WHEN to try again.
//
// So health never reports an exception. It reports a STATE:
//
//   listo               a Revit answered and has an active document
//   sin_documento       a Revit answered and has no document open
//   arrancando          a Revit process exists but its bridge is not up yet
//   host_cerrado        no Revit process is running
//   ocupado             the bridge exists but Revit did not answer in time, or is on a dialog
//   puente_inaccesible  there is no usable bridge (no Revit, stale discovery, contract mismatch)
//
// plus retry_after_ms when waiting is the right move. The success fields stay exactly
// as they were; `estado` is added beside them. Pure: the server decides what it saw,
// this decides what to call it.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class HealthState
    {
        public const string Listo = "listo";
        public const string SinDocumento = "sin_documento";
        public const string Arrancando = "arrancando";
        public const string HostCerrado = "host_cerrado";
        public const string Ocupado = "ocupado";
        public const string PuenteInaccesible = "puente_inaccesible";
        /// <summary>More than one Revit has the bridge loaded and nothing chose one: the call is answered with the list.</summary>
        public const string VariasInstancias = "varias_instancias";

        public static readonly string[] All = { Listo, SinDocumento, Arrancando, HostCerrado, Ocupado, PuenteInaccesible, VariasInstancias };

        public const int RetryArrancandoMs = 5000;
        public const int RetryOcupadoMs = 10000;
        public const int RetryInaccesibleMs = 15000;

        /// <summary>Adds `estado` (listo or sin_documento) to a health payload that DID arrive. Nothing else changes.</summary>
        public static void StampSuccess(JObject health)
        {
            if (health == null) return;
            bool noDocument = health["no_active_document"] != null &&
                              health["no_active_document"].Type == JTokenType.Boolean &&
                              (bool)health["no_active_document"];
            health["estado"] = noDocument ? SinDocumento : Listo;
            if (noDocument)
                health["que_hacer"] = "Abre el modelo en Revit; horizun_health responde de nuevo en cuanto haya un documento activo.";
        }

        /// <summary>
        /// The health body for a call that could not reach, or got no answer from, a Revit.
        /// `errorText` is the failure's own text, `detail` its structured detail (either may be
        /// null), `revitProcessRunning` whether a Revit.exe exists on this machine at all.
        /// </summary>
        public static JObject Unavailable(string errorText, JObject detail, bool? revitProcessRunning)
        {
            ErrorCodes.Classification c = ErrorCodes.Classify(errorText, detail);
            string estado;
            int? retry;
            string queHacer;

            switch (c.Code)
            {
                case ErrorCodes.TiempoAgotado:
                case ErrorCodes.RevitOcupado:
                    estado = Ocupado; retry = RetryOcupadoMs;
                    queHacer = "Revit esta ocupado (un comando o un cuadro de dialogo abierto). Espera retry_after_ms y " +
                               "reintenta UNA vez; si sigue igual, pide a la persona que cierre el dialogo en Revit.";
                    break;
                case ErrorCodes.RevitSinDocumento:
                    estado = SinDocumento; retry = null;
                    queHacer = "Abre el modelo en Revit y vuelve a llamar a horizun_health.";
                    break;
                case ErrorCodes.RevitNoDisponible:
                    // A running process does not prove that its add-in is starting.
                    if (revitProcessRunning == true)
                    {
                        estado = PuenteInaccesible; retry = RetryInaccesibleMs;
                        queHacer = "Hay un proceso Revit, pero no se pudo contactar el puente; su estado es desconocido. " +
                                   "Reintenta una vez tras retry_after_ms; no reinicies Revit.";
                    }
                    else if (revitProcessRunning == false)
                    {
                        estado = HostCerrado; retry = RetryInaccesibleMs;
                        queHacer = "No hay un Revit con el complemento de Horizun. Pide a la persona que abra Revit " +
                                   "(con el complemento cargado); no reintentes en bucle.";
                    }
                    else
                    {
                        estado = PuenteInaccesible; retry = RetryInaccesibleMs;
                        queHacer = "El puente no responde y no se pudo comprobar si Revit esta abierto. " +
                                   "Reintenta una vez tras retry_after_ms; si persiste, avisa a la persona.";
                    }
                    break;
                default:
                    estado = PuenteInaccesible; retry = RetryInaccesibleMs;
                    queHacer = "El puente con Revit no es accesible. Reintenta una vez tras retry_after_ms; si persiste, " +
                               "avisa a la persona con el error_code.";
                    break;
            }

            var body = new JObject
            {
                ["estado"] = estado,
                ["ready"] = false,
                ["error_code"] = c.Code,
                ["que_hacer"] = queHacer
            };
            if (c.ExceptionType != null) body["exception_type"] = c.ExceptionType;
            if (c.LimiteMs.HasValue) body["limite_ms"] = c.LimiteMs.Value;
            if (retry.HasValue) body["retry_after_ms"] = retry.Value;
            body["mensaje"] = Truncate(errorText, 400);

            // The dialog the dispatcher read is carried WHOLE (title, every text, buttons,
            // module): the message above is cut, the dialog's own words are not. Its kind
            // replaces the generic que_hacer with what to ask the person for that dialog.
            if (detail?["modal_dialog"] is JObject modal)
            {
                body["modal_dialog"] = modal.DeepClone();
                ModalDialogKinds.Verdict v = ModalDialogKinds.Classify(modal);
                ModalDialogKinds.Stamp(body, v);
                if (v != null && estado == Ocupado) body["que_hacer"] = v.QueHacer;
            }
            return body;
        }

        /// <summary>
        /// The answer when several Revit instances have the bridge loaded and no target was
        /// chosen. Field data: health failed in 20 % of calls across five teams, and the cause
        /// reproduced was exactly this - two Revits open. It is not a failure of the bridge: it
        /// is a question only the caller can answer, so health ANSWERS it with every instance
        /// (pid, year, build, active document) and target_required, instead of an error.
        /// `instances` rows are built by the server; this decides the wording.
        /// </summary>
        public static JObject MultipleInstances(JArray instances, string refusalText)
        {
            var body = new JObject
            {
                ["estado"] = VariasInstancias,
                ["ok"] = true,
                ["ready"] = false,
                ["target_required"] = true,
                ["error_code"] = ErrorCodes.DocumentoObjetivoAmbiguo,
                ["instances"] = instances ?? new JArray(),
                // The same rule every other tool refuses by (server InstanceRouting.cs);
                // health is the one that answers, so the agent can choose.
                ["rule"] = "several_instances_refuse_uniformly",
                ["example"] = new JObject
                {
                    ["select_instance"] = instances?.FirstOrDefault()?["select_instance"]?.DeepClone(),
                    ["repeat_original_tool_arguments"] = new JObject
                    {
                        ["target_document"] = instances?.FirstOrDefault()?["target_document"]?.DeepClone() ?? JValue.CreateNull()
                    }
                },
                ["que_hacer"] = "Hay varias instancias Revit y no se eligió una. Escoge una fila de instances (pid, year, title, " +
                                "path, active); llama horizun_target con su select_instance.arguments y repite la lectura o escritura. " +
                                "En la llamada original, pasa target_document con el título o ruta de esa fila. La operación pedida " +
                                "no se ejecutó; solo se consultó health en modo lectura."
            };
            body["mensaje"] = Truncate(refusalText, 400);
            return body;
        }

        /// <summary>Whether a refusal text is the "several instances, none chosen" one (PipeClient.Resolve).</summary>
        public static bool IsMultipleInstancesRefusal(string text)
            => !string.IsNullOrEmpty(text) && text.IndexOf("Revit instances are running with the bridge loaded", StringComparison.Ordinal) >= 0;

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "...";
        }
    }
}
