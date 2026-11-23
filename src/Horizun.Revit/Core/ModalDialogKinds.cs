// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH dialog is holding Revit, in words an agent can act on. field cycle (2026-10-24):
// a second Revit 2023 sat on "External Tools - External Tool Failure" and health said
// only "ocupado" with its message cut at 400 characters ("The dialog persisted across
// 3 pr..."), so nobody could tell whether the Horizun add-in or a third party's had
// failed without looking at the screen; "Project Not Saved Recently" held another
// Revit through a whole first attempt while the agent waited for a dialog that only a
// person answers. The probe already READ the dialog (title, body, buttons, module);
// this names the kind, the add-in the body names, and what to ask the person.
//
// Pure (no Revit, no Win32): it judges the strings ModalProbe read. Nothing here is
// clicked or answered - a modal dialog belongs to the person at the keyboard.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ModalDialogKinds
    {
        public const string ExternalToolFailure = "external_tool_failure";
        public const string SaveReminder = "save_reminder";
        public const string SecurityAddinLoad = "security_addin_load";
        public const string Other = "other";

        public sealed class Verdict
        {
            public string Kind;
            /// <summary>The add-in or tool the dialog's own text names (null when it names none).</summary>
            public string FailingTool;
            /// <summary>True/false when the text names a tool; null when it does not.</summary>
            public bool? FailingToolIsHorizun;
            public string QueHacer;
        }

        // A name in quotes after the words Revit uses for an add-in, or a .dll/.addin path.
        private static readonly Regex QuotedName = new Regex(
            "(?:add-?in|application|external tool|command|tool|assembly)\\s*(?:name)?\\s*[:=]?\\s*[\"'“‘]([^\"'”’\\r\\n]{2,120})[\"'”’]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ModulePath = new Regex(
            "([A-Za-z]:\\\\[^\"'\\r\\n<>|]{1,240}?\\.(?:dll|addin))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Classifies the dialog from what the probe read. Null fields are tolerated.</summary>
        public static Verdict Classify(string title, IEnumerable<string> texts, IEnumerable<string> buttons = null)
        {
            string t = title ?? "";
            var bodyParts = (texts ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            string body = string.Join("\n", bodyParts);
            string all = t + "\n" + body;
            var btns = (buttons ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            if (Has(all, "External Tool Failure") || Has(t, "External Tools") ||
                Has(body, "external application") && (Has(body, "fail") || Has(body, "could not")))
            {
                string tool = NamedTool(body);
                bool? isHz = tool == null ? (bool?)null : tool.IndexOf("Horizun", StringComparison.OrdinalIgnoreCase) >= 0;
                return new Verdict
                {
                    Kind = ExternalToolFailure, FailingTool = tool, FailingToolIsHorizun = isHz,
                    QueHacer = isHz == true
                        ? "El complemento que fallo es el de Horizun (" + tool + "). Pide a la persona que cierre el dialogo; " +
                          "despues horizun_health dira si el puente cargo. Si no carga, reinstala con install.ps1 con Revit cerrado."
                        : isHz == false
                            ? "Fallo un complemento de terceros (" + tool + "), no el de Horizun. Pide a la persona que cierre el " +
                              "dialogo; el puente sigue cargado. Si se repite en cada arranque, que desactive ese complemento."
                            : "Fallo un complemento externo y el texto del dialogo no lo nombra (lee modal_dialog.all_text). " +
                              "Pide a la persona que cierre el dialogo y reintenta horizun_health."
                };
            }
            if (Has(all, "Not Saved Recently") || Has(all, "not been saved recently") || Has(all, "has not been saved"))
                return new Verdict
                {
                    Kind = SaveReminder,
                    QueHacer = "Es el recordatorio de guardado de Revit: no cambia el modelo. Pide a la persona que elija " +
                               "'Do not save the project' (o que guarde, si quiere guardar ya) y reintenta. Guardar despues va " +
                               "con horizun_save_document."
                };
            if (Has(t, "Security") && (Has(all, "Unsigned") || Has(all, "add-in") || btns.Any(b => Has(b, "Always Load"))))
                return new Verdict
                {
                    Kind = SecurityAddinLoad, FailingTool = NamedTool(body),
                    QueHacer = "Revit pregunta si carga un complemento sin firma. La persona elige 'Always Load' solo despues de " +
                               "verificar el build; el dialogo puede estar en otro monitor."
                };
            return new Verdict
            {
                Kind = Other,
                QueHacer = "Hay un dialogo abierto en Revit (titulo y texto en modal_dialog). Pide a la persona que lo conteste " +
                           "o lo cierre - revisa todos los monitores - y reintenta una vez."
            };
        }

        /// <summary>Classifies a ModalDialogInfo.ToJson() block (title, all_text, main_text, buttons). Null for no block.</summary>
        public static Verdict Classify(JObject modalDialog)
        {
            if (modalDialog == null) return null;
            var texts = new List<string>();
            if (modalDialog["all_text"] is JArray a) texts.AddRange(a.Select(x => x?.Type == JTokenType.String ? (string)x : null));
            string main = modalDialog["main_text"]?.Type == JTokenType.String ? (string)modalDialog["main_text"] : null;
            if (main != null && !texts.Contains(main)) texts.Add(main);
            var buttons = (modalDialog["buttons"] as JArray)?.Select(x => x?.Type == JTokenType.String ? (string)x : null);
            string title = modalDialog["title"]?.Type == JTokenType.String ? (string)modalDialog["title"] : null;
            return Classify(title, texts, buttons);
        }

        /// <summary>Writes dialog_kind, failing_tool, failing_tool_is_horizun into a reply body.</summary>
        public static void Stamp(JObject body, Verdict v)
        {
            if (body == null || v == null) return;
            body["dialog_kind"] = v.Kind;
            if (v.FailingTool != null) body["failing_tool"] = v.FailingTool;
            if (v.FailingToolIsHorizun.HasValue) body["failing_tool_is_horizun"] = v.FailingToolIsHorizun.Value;
        }

        internal static string NamedTool(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            Match q = QuotedName.Match(body);
            if (q.Success) return q.Groups[1].Value.Trim();
            Match p = ModulePath.Match(body);
            if (p.Success) return p.Groups[1].Value.Trim();
            return null;
        }

        private static bool Has(string s, string needle) =>
            !string.IsNullOrEmpty(s) && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
