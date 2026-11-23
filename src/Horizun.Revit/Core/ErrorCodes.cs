// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// STABLE ERROR CODES. Every failure the bridge or the server reports carries a
// machine-readable `error_code` beside the prose, so a client (or a telemetry
// job counting failures by cause) branches on a field instead of reading
// English. Field data from five teams over fourteen days: 169 errors in 1,588
// calls, and 79 % of them had no identifiable cause because the only evidence
// was free text.
//
// ONE SEAT. This file is linked into the add-in AND the server (and the tests),
// and the two places that assemble failure replies - PipeEnvelope on the add-in
// side, McpResult on the server side - call it. No tool stamps its own errors:
// a tool may NAME a code where it knows better than the text does (a timeout, a
// syntax error), and everything else is classified here from the structured
// detail first and the message second. `error_no_clasificado` is the honest
// default and is meant to be the exception.
//
// What a code never carries: project data. Codes are fixed snake_case words;
// `exception_type` is the bare type NAME (InvalidOperationException), never a
// message, never a namespace-qualified path; `limite_ms` is a number.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ErrorCodes
    {
        // ---- the catalog --------------------------------------------------------
        public const string RevitNoDisponible = "revit_no_disponible";
        public const string RevitSinDocumento = "revit_sin_documento";
        public const string RevitOcupado = "revit_ocupado";
        public const string Cancelado = "cancelado";
        public const string SinVista = "sin_vista";
        public const string DocumentoNoActivo = "documento_no_activo";
        public const string DocumentoObjetivoAmbiguo = "documento_objetivo_ambiguo";
        public const string PythonErrorSintaxis = "python_error_sintaxis";
        public const string PythonExcepcion = "python_excepcion";
        public const string ApiRevitExcepcion = "api_revit_excepcion";
        public const string FamilyDocumentOpen = "family_document_open";
        public const string TransaccionRevertida = "transaccion_revertida";
        public const string ElementoNoEncontrado = "elemento_no_encontrado";
        public const string EsquemaInvalido = "esquema_invalido";
        /// <summary>Well-formed input whose value cannot be resolved or is invalid for the requested operation.</summary>
        public const string EntradaInvalida = "entrada_invalida";
        public const string ConfirmacionRequerida = "confirmacion_requerida";
        public const string TokenInvalidoOVencido = "token_invalido_o_vencido";
        public const string TiempoAgotado = "tiempo_agotado";
        public const string AnnotationBudgetExhausted = "annotation_budget_exhausted";
        public const string PermisoPythonRequerido = "permiso_python_requerido";

        // Beyond the minimum catalog: causes the bridge can name with certainty and that
        // a caller must act on differently from every code above.
        public const string ContratoIncompatible = "contrato_incompatible";
        public const string HerramientaDesactivada = "herramienta_desactivada";
        public const string PermisoInsuficiente = "permiso_insuficiente";
        public const string LlamadaEnCuarentena = "llamada_en_cuarentena";
        public const string AdjuntoImagenFallido = "adjunto_imagen_fallido";
        public const string RespuestaDemasiadoGrande = "respuesta_demasiado_grande";
        public const string TransporteFallido = "transporte_fallido";
        public const string ErrorInterno = "error_interno";
        public const string CapturaSinResultado = "captura_sin_resultado";
        public const string PrecondicionNoCumplida = "precondicion_no_cumplida";
        public const string VerificacionFallida = "verificacion_fallida";
        public const string ArchivoNoAccesible = "archivo_no_accesible";
        /// <summary>A family (.rfa) saved in a newer Revit than this one: Revit cannot load it and nothing downgrades it.</summary>
        public const string FamiliaVersionMasNueva = "familia_version_mas_nueva";
        /// <summary>An element asked to move is a member of a model group: Revit leaves it where it is without saying so.</summary>
        public const string MiembroDeGrupo = "miembro_de_grupo";
        /// <summary>horizun_execute_python documentation_only=true: the script changed something outside views, sheets, annotation and view-owned elements and the run was rolled back.</summary>
        public const string DocumentacionViolada = "documentacion_violada";
        /// <summary>The generic code: nothing above matched. It must be the exception.</summary>
        public const string NoClasificado = "error_no_clasificado";

        /// <summary>Every code a reply may carry. Documented in README.md; a test keeps them in step.</summary>
        public static readonly string[] Catalog =
        {
            RevitNoDisponible, RevitSinDocumento, RevitOcupado, Cancelado, SinVista, DocumentoNoActivo, DocumentoObjetivoAmbiguo,
            PythonErrorSintaxis, PythonExcepcion, ApiRevitExcepcion, FamilyDocumentOpen, TransaccionRevertida,
            ElementoNoEncontrado, EsquemaInvalido, EntradaInvalida, ConfirmacionRequerida, TokenInvalidoOVencido,
            TiempoAgotado, AnnotationBudgetExhausted, PermisoPythonRequerido,
            ContratoIncompatible, HerramientaDesactivada, PermisoInsuficiente, LlamadaEnCuarentena,
            AdjuntoImagenFallido, RespuestaDemasiadoGrande, TransporteFallido, ErrorInterno,
            CapturaSinResultado, PrecondicionNoCumplida, VerificacionFallida,
            ArchivoNoAccesible, FamiliaVersionMasNueva, MiembroDeGrupo, DocumentacionViolada, NoClasificado
        };

        public const string CodeField = "error_code";
        public const string ExceptionTypeField = "exception_type";
        public const string LimitField = "limite_ms";

        private static readonly Regex SnakeCase = new Regex("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex TypeNameShape = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,79}$", RegexOptions.Compiled);

        public static bool IsStable(string code)
        {
            if (string.IsNullOrEmpty(code) || !SnakeCase.IsMatch(code)) return false;
            return Array.IndexOf(Catalog, code) >= 0;
        }

        // ---- building a detail ---------------------------------------------------

        /// <summary>
        /// A detail object carrying the code (and, when given, the exception type name and
        /// the limit). `extra` is copied first so a caller's own diagnostics survive.
        /// </summary>
        public static JObject Detail(string code, string exceptionType = null, long? limiteMs = null,
                                     JObject extra = null)
        {
            JObject d = extra != null ? (JObject)extra.DeepClone() : new JObject();
            d[CodeField] = code;
            string type = ShortTypeName(exceptionType);
            if (type != null) d[ExceptionTypeField] = type;
            if (limiteMs.HasValue) d[LimitField] = limiteMs.Value;
            return d;
        }

        /// <summary>
        /// The bare type NAME: no namespace, no generic arity, no nesting path. Null when what
        /// was given is not a plausible identifier - a message that wandered into the field
        /// must not be republished as if it were a type.
        /// </summary>
        public static string ShortTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            string s = typeName.Trim();
            // Only identifier characters may make a type name: a path or a sentence that wandered
            // into the field is not one, even if its last dot-separated piece looks like one.
            if (!Regex.IsMatch(s, @"^[A-Za-z0-9_\.`+]+$")) return null;
            int tick = s.IndexOf('`');
            if (tick >= 0) s = s.Substring(0, tick);
            int dot = s.LastIndexOf('.');
            if (dot >= 0) s = s.Substring(dot + 1);
            int plus = s.LastIndexOf('+');
            if (plus >= 0) s = s.Substring(plus + 1);
            return TypeNameShape.IsMatch(s) ? s : null;
        }

        public static string TypeNameOf(Exception ex) => ex == null ? null : ShortTypeName(ex.GetType().Name);

        /// <summary>True for the exception types the Revit API itself raises.</summary>
        public static bool IsRevitApiType(string fullTypeName)
            => fullTypeName != null && fullTypeName.StartsWith("Autodesk.Revit.", StringComparison.Ordinal);

        // ---- classification ------------------------------------------------------

        public sealed class Classification
        {
            public string Code;
            public string ExceptionType;
            public long? LimiteMs;
        }

        private static readonly Regex WithinMs = new Regex(
            @"(?:within|after|its)\s+(\d{3,9})\s*ms", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ExceptionName = new Regex(
            @"\b([A-Z][A-Za-z0-9]{2,60}Exception)\b", RegexOptions.Compiled);

        private static bool Has(string text, string needle)
            => text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool HasAny(string text, params string[] needles)
        {
            foreach (string n in needles) if (Has(text, n)) return true;
            return false;
        }

        /// <summary>
        /// The cause of a failure from what it already says: the structured detail first
        /// (it is data), then the message. Never throws. A detail that already carries a
        /// stable `error_code` wins outright.
        /// </summary>
        public static Classification Classify(string message, JObject detail)
        {
            var result = new Classification();
            try { ClassifyCore(message ?? "", detail, result); }
            catch { result.Code = NoClasificado; }
            if (result.Code == null) result.Code = NoClasificado;
            return result;
        }

        private static void ClassifyCore(string msg, JObject detail, Classification r)
        {
            string present = detail?[CodeField]?.Type == JTokenType.String ? (string)detail[CodeField] : null;
            if (IsStable(present))
            {
                r.Code = present;
                r.ExceptionType = ShortTypeName(detail?[ExceptionTypeField]?.Type == JTokenType.String
                    ? (string)detail[ExceptionTypeField] : null);
                if (detail?[LimitField] != null && detail[LimitField].Type == JTokenType.Integer)
                    r.LimiteMs = (long)detail[LimitField];
                return;
            }

            string legacy = detail?["code"]?.Type == JTokenType.String ? (string)detail["code"] : null;
            string detailType = detail?[ExceptionTypeField]?.Type == JTokenType.String
                ? (string)detail[ExceptionTypeField] : null;
            string detailTypeShort = ShortTypeName(detailType);
            string rollback = detail?["rollback_status"]?.Type == JTokenType.String
                ? (string)detail["rollback_status"] : null;

            // Transport identity is stronger than incidental words in its message.
            // In particular, "no longer" must not hide a cancelled request as a precondition.
            if (legacy == "revit_transport_failed")
            {
                r.Code = detailTypeShort == "TimeoutException" ? TiempoAgotado
                       : detailTypeShort == "OperationCanceledException" || detailTypeShort == "TaskCanceledException" ? Cancelado
                       : Has(msg, "byte limit") ? RespuestaDemasiadoGrande : TransporteFallido;
                r.ExceptionType = detailTypeShort;
                return;
            }

            // ---- by structured detail ------------------------------------------
            switch (legacy)
            {
                case "python_execution_failed":
                    r.Code = PythonExcepcion; r.ExceptionType = detailTypeShort; return;
                case "invalid_spec":
                case "invalid_operation_arguments":
                case "request_state_rejected":
                    r.Code = EsquemaInvalido; return;
                case "type_not_found":
                case "sleeve_type_not_found":
                case "unknown_finding_set":
                    r.Code = ElementoNoEncontrado; return;
                case "result_serialization_failed":
                case "unhandled_command_exception":
                    r.Code = IsRevitApiType(detailType) ? ApiRevitExcepcion : ErrorInterno;
                    r.ExceptionType = detailTypeShort; return;
                case "quarantined_call":
                case "call_quarantined":
                    r.Code = LlamadaEnCuarentena; return;
            }
            if (string.Equals(rollback, "rolled_back", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rollback, "rolled_back_verified", StringComparison.OrdinalIgnoreCase))
            {
                r.Code = TransaccionRevertida; return;
            }
            if (detail?["modal_dialog"] != null && detail["modal_dialog"].Type != JTokenType.Null)
            {
                r.Code = RevitOcupado; return;
            }

            // ---- by message ----------------------------------------------------
            ClassifyMessage(msg, legacy, detailType, detailTypeShort, r);
        }

        private sealed class Rule
        {
            public readonly string Code;
            public readonly Regex Pattern;
            public Rule(string code, string pattern)
            {
                Code = code;
                Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            }
        }

        // ORDER IS THE POLICY: the most specific cause first. A timeout that names a modal
        // dialog is busy, not merely late; a stale plan is a token problem before it is a
        // generic refusal. The last rules are the broad "refused before acting" families.
        private static readonly Rule[] Rules =
        {
            new Rule(EsquemaInvalido, @"point placement takes level_id\+point without phase_id"),
            new Rule(PrecondicionNoCumplida, @"Category '[^']*' does not accept bound parameters"),
            new Rule(RevitOcupado, @"MODAL DIALOG open|Revit is busy|command or dialog is open"),
            new Rule(TiempoAgotado, @"timed out after|No reply from Revit within|did not read the request within|ms deadline"),
            new Rule(Cancelado, @"\b(?:operation|request|opening|script|command) (?:was |is )?cancel(?:led|ed)\b|\b(?:OperationCanceledException|TaskCanceledException)\b"),
            new Rule(SinVista, @"\bno active view\b|\bactive view is null\b"),
            new Rule(DocumentoNoActivo, @"\bdocument is not (?:the )?active\b|\bdocument must be (?:the )?active\b"),
            new Rule(PythonExcepcion, @"(?:^|\n)\s*(?:TypeError|AttributeError|NameError|ValueError|KeyError|IndexError|SystemError|ImportError|ModuleNotFoundError|StopIteration|ZeroDivisionError):"),
            new Rule(PermisoPythonRequerido, @"DISABLED ON THIS MACHINE|request_python_access|unsafe_code and|python access|python is not authorized"),
            new Rule(FamiliaVersionMasNueva, @"family_saved_in_newer_version|was saved in a NEWER Revit"),
            new Rule(TokenInvalidoOVencido,
                @"No such confirmation token|confirmation was already used|confirmation expired|That confirmation was issued|THE MODEL MOVED AFTER THE DRY RUN|NOT THE ONE THAT WAS REHEARSED|confirmation_token is invalid|Auth failed|^\s*(stale_plan|plan_not_kept|plan_id_mismatch|placement_moved)\b|THE DELETE PLAN MOVED|plan moved|\bstale plan\b"),
            new Rule(ConfirmacionRequerida,
                @"confirmation_token[^.]{0,80}(is required|required|needed)|requires? (a )?confirmation|run this command with dry_run=true first"),
            new Rule(ContratoIncompatible, @"different command contracts|wire protocol|does not have the '|contract hash mismatch|older build than this server|Unknown command:"),
            new Rule(HerramientaDesactivada, @"disabled by settings|is switched off|is not enabled in this toolset"),
            new Rule(PermisoInsuficiente, @"permission_profile=|does not authorize|requires explicit permission"),
            new Rule(DocumentoObjetivoAmbiguo,
                @"target_document' is required|matches MORE THAN ONE|No open document matches|target_document '|target_document is '|ACTIVE document changed|was asked for document|more than one Revit|nothing says which|Refusing to pick|target_document does not match"),
            new Rule(RevitSinDocumento,
                @"No document is active|No document is open|No active Revit document|no active document|has nothing to act on|There is no active document|document closed before|no document is open"),
            new Rule(RevitNoDisponible,
                @"no Revit is reachable|is no longer running|Is Revit running|no bridge to route|is reachable\. That|Revit is shutting down|Pipe closed before|discovery file|Could not connect to Revit's bridge|has published a bridge"),
            new Rule(TransaccionRevertida,
                @"was rolled back|were rolled back|rolled back\.|rolled back and|transaction was reverted|Nothing was committed|rehearsal could not verify|A rollback was attempted|group rehearsal|rehearsed copy failed|rehearsal exists to catch|did not commit"),
            new Rule(VerificacionFallida,
                @"committed (but|and then)|re-read|re-reads|read back|post-?commit|postcondition|would not assimilate|assimilated but|state is uncertain|Success is not claimed|could not verify|not verified|did not verify|verification (failed|contradicted)|cannot be read back|no longer re-reads|does not re-read|unverified|not all verified|verified, but|and verified|Expected exactly [0-9]+ produced"),
            new Rule(PythonErrorSintaxis, @"SyntaxError|invalid syntax|IndentationError"),
            new Rule(ElementoNoEncontrado,
                @"not found|does not exist|do not exist|no such element|No element|No view named|no type named|No type named|names no|could not be found|matches no |no element with|No level named|No family|does not identify|does not resolve|no wall in scope|is not an? [A-Za-z]+ in this document|Category '[^']*' does not|no audit with|no operation|nothing is recorded under"),
            new Rule(EsquemaInvalido,
                @"Parameters must be a JSON object|not valid JSON|Invalid params|schema_help|Unknown capture argument|Unknown argument|unknown field|Malformed request|No command given|^Invalid [a-z0-9 -]*(plan|proposals?|profile|spec|request)|\bmust (be|hold|contain|name|not|identify|select|carry|take|have|include|resolve|set|list|exceed)\b|\bshould be\b|\bis required\b|\bare required\b|\brequired:|\bcannot be (combined|mixed|used|named|applied|set)\b|\bcannot (name|run|include|list)\b|Specify |not both|is not one of|expected one of|Unknown (operation|kind|format)|\bneeds (a|an|at least|exactly|the|one|to|[a-z_]+\.[a-z_]+|[a-z_]+:)|\bapplies to\b|only\. |takes [a-z ]+ only|holds a (value|blank)|carries a value|arrived as|\bexceeds\b|the bound is|limited to|repeats '|is not a (valid|Level|pipe|view|3D)|\bnot a (view|level|number|string|integer|boolean|ElementId|supported)|must be unique|requires? (one or more|exactly|a |an |at least)|is not (read|supported|an installed)|Too many|None of the|Nothing to check|Use [a-z_]+ to|only basic|names [0-9{}]+ value\(s\) that are not|only [a-z ]+ can be|unsupported|not allowed|are not valid|is not valid|invalid (value|argument|combination)|is mutually exclusive|mutually exclusive|Give exactly one|Pass [a-z_]+|Name one|\bomit [a-z_]|at most [0-9{}]+|Unknown [a-z_ ]+(argument|field|section|operation)|Arguments are not|^Invalid [a-z ]+:|\bneeds\b|\brequires\b|-only options|\blimit: [0-9]|must differ|is ambiguous|not a JSON|\bchoose a different\b|distinct non-empty|\bnot a (JSON|number)|should not carry|may not|takes exactly|accepts only|belongs to another|\bis a [{}]+ \(|identifies a [{}]+|\bof a level|outside the source|is outside"),
            new Rule(ArchivoNoAccesible,
                @"could not be (read|opened|loaded|found on disk)|Could not open|cannot be opened|unreadable|not readable|file (is )?(missing|locked|not)|DWG source is unavailable|path .* (not|does not)|Cannot read|cannot read|could not be (listed|parsed|inspected|decoded|collected|written)|this machine cannot read"),
            new Rule(ApiRevitExcepcion, @"Autodesk\.Revit\.Exceptions|Revit refused|Revit rejected|Revit raised|Revit threw|Revit returned no|\bfailed: |failed:|\bfailed\b.{0,60}\b(Revit|API)\b|Could not start the|would not start|could not start|transaction did not start|did not start|raised no exception|returned FALSE without throwing|declined to"),
            new Rule(ErrorInterno, @"produced no result|could not be recorded|not registered|not installed|is not available in this build|internal"),
            // The broad families last: the call was well-formed and the model or the plan did
            // not allow it, and the command declined BEFORE acting.
            new Rule(PrecondicionNoCumplida,
                @"^\s*[a-z]+(_[a-z0-9]+)+\s*:|\bNothing (was|is|ran|has been|will be|more|to)\b|\bnothing (was|is|ran|will be|to)\b|\bRefused\b|\brefused\b|\bREFUSED\b|\bis READ-ONLY\b|\bread-only\b|\bwould (be|produce|break|duplicate|collide|leave|not)\b|\bnot allowed\b|\bcannot (be|proceed|apply|run|continue)\b|\bmust not\b|\bis (already|not)\b|\bno longer\b|\bhas no\b|\bhave no\b|\bdoes not (match|have|apply|support)\b|\bdo not\b|\bcould not (be )?(determine|resolve|identify|find|place|create|measure|compute|build|derive)\b|\bis unavailable\b|\bunavailable\b|\bcontradict|already exists|overwrite=false|Dry run failed|\bcursor\b|not schedulable|, not an? [A-Za-z]+\.|incomplete|truncated|coverage|did not produce|\bcorrupt|does not belong|cannot become|edits ONE element|not rolled back|Rolled back|did not run|not_started|was not created|no correction was applied|failed to run|\bwas not\b|\bwere not\b|\bnot viable\b|\bnot reported as\b|nothing could be|cannot cross|did not hold|does not hold|\bwill NOT\b|\bnot (all|every)\b|is not proven|\bmeasured [0-9{}]+|\bdoes not take\b|has an active|hides [{}]|writes nothing|nothing kept|lacks |\bmissing\b|not found among|Fewer than|fewer than|too few|too many|instead of|nothing to |No [a-z ]+ (found|matched|selected|was)|rows? (is|are) (blank|empty)|empty")
        };

        private static void ClassifyMessage(string msg, string legacy, string detailType, string detailTypeShort,
                                            Classification r)
        {
            // These are the argument diagnostics emitted by PdfNamePattern before export.
            if (msg.StartsWith("pdf_name_pattern ", StringComparison.OrdinalIgnoreCase))
            {
                r.Code = EntradaInvalida;
                return;
            }
            // A "timed out" message that also names a modal dialog stays a timeout: the limit
            // that was hit is the fact; the Rules table orders busy before late only for
            // messages that are not already a timeout.
            if (msg.IndexOf("timed out after", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.Code = TiempoAgotado;
                Match t = WithinMs.Match(msg);
                long tms;
                if (t.Success && long.TryParse(t.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out tms))
                    r.LimiteMs = tms;
                return;
            }

            foreach (Rule rule in Rules)
            {
                if (!rule.Pattern.IsMatch(msg)) continue;
                r.Code = rule.Code;
                if (rule.Code == TiempoAgotado)
                {
                    Match m = WithinMs.Match(msg);
                    long ms;
                    if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ms))
                        r.LimiteMs = ms;
                }
                else if (rule.Code == ApiRevitExcepcion)
                {
                    Match m = ExceptionName.Match(msg);
                    if (m.Success) r.ExceptionType = ShortTypeName(m.Groups[1].Value);
                }
                else if (rule.Code == PythonExcepcion)
                {
                    Match m = Regex.Match(msg, @"(?:^|\n)\s*([A-Za-z]+(?:Error|Iteration)):");
                    if (m.Success) r.ExceptionType = ShortTypeName(m.Groups[1].Value);
                }
                return;
            }

            if (detailTypeShort != null)
            {
                r.Code = IsRevitApiType(detailType) ? ApiRevitExcepcion : ErrorInterno;
                r.ExceptionType = detailTypeShort;
                return;
            }

            r.Code = NoClasificado;
        }

        // ---- python scripts ----------------------------------------------------------

        private static readonly Regex TypeBeforeColon = new Regex(
            @"^\s*([A-Za-z_][A-Za-z0-9_\.]*)\s*(?::|$)", RegexOptions.Compiled);

        /// <summary>
        /// The exception type NAME from the last line of a formatted Python traceback
        /// ("ValueError: bad value" gives ValueError). Null when the line does not start with one.
        /// </summary>
        public static string PythonTypeFromTraceback(string formatted)
        {
            if (string.IsNullOrWhiteSpace(formatted)) return null;
            string[] lines = formatted.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                Match m = TypeBeforeColon.Match(lines[i]);
                return m.Success ? ShortTypeName(m.Groups[1].Value) : null;
            }
            return null;
        }

        /// <summary>
        /// The detail of a script that did not finish, telling the causes apart: a script
        /// that never compiled (python_error_sintaxis), a transaction Revit threw away
        /// (transaccion_revertida), an exception the Revit API raised (api_revit_excepcion)
        /// and an exception of the script's own (python_excepcion). `revitApiType` is the
        /// bare name of the Autodesk.Revit.* exception that surfaced (null when none did);
        /// `scriptType` is the Python or CLR type name of whatever ended the script.
        /// </summary>
        public static JObject ScriptFailureDetail(JObject baseDetail, bool syntax, string revitApiType,
                                                  string scriptType, bool transactionLost, bool bridgeFailure = false)
        {
            string cause = bridgeFailure ? ErrorInterno
                         : syntax ? PythonErrorSintaxis
                         : revitApiType != null ? ApiRevitExcepcion
                         : PythonExcepcion;
            string type = syntax ? (ShortTypeName(scriptType) ?? "SyntaxError")
                        : (ShortTypeName(revitApiType) ?? ShortTypeName(scriptType));
            JObject d = baseDetail != null ? (JObject)baseDetail.DeepClone() : new JObject();
            if (transactionLost && !syntax)
            {
                d[CodeField] = TransaccionRevertida;
                d["causa_error_code"] = cause;
            }
            else d[CodeField] = cause;
            if (type != null) d[ExceptionTypeField] = type;
            return d;
        }

        // ---- stamping --------------------------------------------------------------

        /// <summary>
        /// The detail of a failure with its `error_code` guaranteed. Returns the SAME object
        /// when it already carries a stable code (nothing to do), otherwise a copy with the
        /// code, the exception type NAME and the limit added. A pre-existing `exception_type`
        /// that was a qualified name is reduced to its bare name.
        /// </summary>
        public static JObject Ensure(string message, JObject detail)
        {
            bool coded = IsStable(detail?[CodeField]?.Type == JTokenType.String ? (string)detail[CodeField] : null);
            Classification c = Classify(message, detail);
            JObject d = detail != null ? (JObject)detail.DeepClone() : new JObject();
            if (!coded) d[CodeField] = c.Code;
            string type = c.ExceptionType ?? ShortTypeName(d[ExceptionTypeField]?.Type == JTokenType.String
                ? (string)d[ExceptionTypeField] : null);
            if (type != null) d[ExceptionTypeField] = type;
            else if (d[ExceptionTypeField] != null) d.Remove(ExceptionTypeField);
            if (c.LimiteMs.HasValue && d[LimitField] == null) d[LimitField] = c.LimiteMs.Value;
            return d;
        }

        /// <summary>The one-line rendering appended to an error's text, for clients that only read prose.</summary>
        public static string Line(JObject detail)
        {
            string code = detail?[CodeField]?.Type == JTokenType.String ? (string)detail[CodeField] : null;
            if (code == null) return null;
            string line = "error_code: " + code;
            string type = detail[ExceptionTypeField]?.Type == JTokenType.String ? (string)detail[ExceptionTypeField] : null;
            if (type != null) line += " (" + type + ")";
            if (detail[LimitField] != null && detail[LimitField].Type == JTokenType.Integer)
                line += " limite_ms=" + (long)detail[LimitField];
            // Its own line, code identifiers only (Core/ErrorSignature.cs), so team telemetry can keep it whole.
            if (detail[ErrorSignature.Field]?.Type == JTokenType.String)
                line += "\nerror_signature: " + (string)detail[ErrorSignature.Field];
            return line;
        }
    }
}
