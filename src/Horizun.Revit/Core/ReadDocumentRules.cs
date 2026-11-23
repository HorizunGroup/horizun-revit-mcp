using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Revit-free half of DocumentGate.ReadGuard; every read uses the same aliases.
    public static class ReadDocumentRules
    {
        public static JObject MissingTarget(string wanted, IEnumerable<DocIdentity> open, bool isWrite = false)
        {
            var titles = (open ?? Enumerable.Empty<DocIdentity>()).Select(d => d?.Title ?? "(title unreadable)").ToArray();
            return new JObject {
                ["error_code"] = ErrorCodes.DocumentoObjetivoAmbiguo,
                ["message"] = "No open document matches target_document '" + wanted + "'. Open documents: " +
                    (titles.Length == 0 ? "(unavailable or none)" : string.Join(", ", titles)) + (isWrite ? ". Nothing was changed." : ". Nothing was read."),
                ["open_documents"] = new JArray(titles)
            };
        }
        public static string Target(JObject request) =>
            request?.Value<string>("target_document") ?? request?.Value<string>("expected_document") ??
            request?.Value<string>("target_document_title");

        public static bool Allows(string wanted, string actual)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return true;
            if (actual == null) return false;
            return string.Equals(StripExtension(wanted), StripExtension(actual), StringComparison.OrdinalIgnoreCase);
        }

        public static bool Allows(string wanted, string actual, string actualPath)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return true;
            // A path is a stronger identity than the title; never degrade a failed
            // path match to a same-named document.
            if (wanted.IndexOf('/') >= 0 || wanted.IndexOf('\\') >= 0)
                return DocIdentity.SamePath(wanted, actualPath);
            return Allows(wanted, actual);
        }

        private static string StripExtension(string value)
        {
            value = (value ?? "").Trim();
            return value.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? value.Substring(0, value.Length - 4) : value;
        }
    }
}
