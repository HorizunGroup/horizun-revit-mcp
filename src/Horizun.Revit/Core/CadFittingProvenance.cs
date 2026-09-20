// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// WHERE A FITTING CAME FROM.
//
// Nothing stamped a fitting. A run carries provenance and an elbow did not, so
// when a revision needed a fitting released the bridge could not tell one it had
// placed itself from one somebody put there and tuned - and the only safe answer
// was to hold every one of them and make a person name it by id. That is right and
// it is also friction on every revision of every connected network.
//
// So a fitting this bridge places now remembers:
//
//   the OPERATION that placed it and the drawing it was placed from
//   the JUNCTION it serves and the MEMBERS it joins
//   its TYPE and the point it was placed at, as re-read after the commit
//   a PRINT of all of that, so "has somebody touched it since" is a comparison
//
// The print is what makes the record useful rather than merely present. Provenance
// of our own is not permission to delete: a fitting we placed and a person then
// moved, resized or retyped is THEIR work now, and it is protected exactly like one
// we never placed. Three answers, never two:
//
//   made_here            ours, and as we left it
//   made_here_modified   ours, and somebody has changed it since
//   origin_unknown       no record: linked in Revit, placed by hand, made by an
//                        older build, or copied in. Unknown stays unknown - a model
//                        from before this record existed is not retrospectively
//                        claimed by the bridge.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadFittingProvenance
    {
        public const string MadeHere = "made_here";
        public const string MadeHereModified = "made_here_modified";
        public const string Unknown = "origin_unknown";

        /// <summary>A junction id in the candidate field is what marks a record as a fitting's.</summary>
        public const string JunctionPrefix = "cadjun:";

        /// <summary>
        /// Stamp one fitting with the junction it serves. <paramref name="from"/> is a member's own
        /// provenance: the drawing, the rules and the placement are the same, and copying them keeps a
        /// fitting answerable to the same scope question as the runs it joins.
        /// </summary>
        public static bool Stamp(Document doc, Element fitting, string junctionId, string kind,
                                 IList<long> members, CadProvenance from, out string problem)
        {
            problem = null;
            if (fitting == null) { problem = "no fitting"; return false; }
            var p = new CadProvenance
            {
                SchemaVersion = from?.SchemaVersion ?? 4,
                CandidateId = JunctionPrefix + (junctionId ?? "junction"),
                GeometryId = from?.GeometryId,
                SemanticId = from?.SemanticId,
                RuleId = "connect:" + (kind ?? "fitting"),
                RequirementSetId = from?.RequirementSetId,
                RequirementSetVersion = from?.RequirementSetVersion,
                RequirementSetSha256 = from?.RequirementSetSha256,
                SourceFingerprint = from?.SourceFingerprint,
                SourceFileSha256 = from?.SourceFileSha256,
                SourceSetSha256 = from?.SourceSetSha256,
                Layer = from?.Layer,
                PlacementId = from?.PlacementId,
                PlacementTransform = from?.PlacementTransform,
                InterpretationVersion = from?.InterpretationVersion,
                BuiltGeometry = PointOf(fitting),
                WrittenUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Confidence = 1.0
            };
            p.SourceEntities = CadProvenanceStore.Capped(
                "fitting:" + (kind ?? "?") +
                ";members:" + string.Join(",", (members ?? new List<long>()).Select(x => x.ToString(CultureInfo.InvariantCulture))) +
                ";print:" + (CadElementPrint.Of(doc, Rid.Value(fitting.Id)) ?? "?"));
            return CadProvenanceStore.Write(fitting, p, out problem);
        }

        /// <summary>What this fitting is, to this bridge, right now.</summary>
        public static JObject Describe(Document doc, Element fitting)
        {
            var o = new JObject { ["element_id"] = fitting == null ? (JToken)JValue.CreateNull() : Rid.Value(fitting.Id) };
            if (fitting == null) { o["origin"] = Unknown; return o; }
            string problem;
            CadProvenance p = null;
            try { p = CadProvenanceStore.Read(fitting, out problem); } catch { }
            string candidate = p?.CandidateId;
            if (p == null || candidate == null || !candidate.StartsWith(JunctionPrefix, StringComparison.Ordinal))
            {
                o["origin"] = Unknown;
                o["means"] = "no record of this bridge placing it: linked in Revit, placed by hand, made by a " +
                             "build from before fittings were stamped, or copied in. Unknown stays unknown - a " +
                             "model from before this record existed is not claimed retrospectively.";
                return o;
            }

            o["junction"] = candidate.Substring(JunctionPrefix.Length);
            o["placed_by"] = p.RuleId;
            o["placed_utc"] = p.WrittenUtc;
            o["from_drawing"] = new JObject
            {
                ["source_file_sha256"] = p.SourceFileSha256,
                ["source_set_sha256"] = p.SourceSetSha256,
                ["requirement_set_sha256"] = p.RequirementSetSha256
            };
            string entities = p.SourceEntities ?? "";
            o["members"] = new JArray(Between(entities, "members:").Split(',')
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => (JToken)long.Parse(x, CultureInfo.InvariantCulture)));
            string print = Between(entities, "print:");
            string now = CadElementPrint.Of(doc, Rid.Value(fitting.Id));
            o["print"] = new JObject { ["when_placed"] = print, ["now"] = now };
            bool same = !string.IsNullOrWhiteSpace(print) && string.Equals(print, now, StringComparison.Ordinal);
            o["origin"] = same ? MadeHere : MadeHereModified;
            o["means"] = same
                ? "this bridge placed it, from this drawing, and it is as it was left. It may be released by " +
                  "a decision that names it - having placed it is not permission on its own."
                : "this bridge placed it AND somebody has changed it since: its point, its type or its size " +
                  "is not what was recorded. That is their work now, and it is protected exactly like a " +
                  "fitting this bridge never placed.";
            return o;
        }

        private static string Between(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            int j = s.IndexOf(';', i);
            return j < 0 ? s.Substring(i + key.Length) : s.Substring(i + key.Length, j - i - key.Length);
        }

        private static string PointOf(Element e)
        {
            try
            {
                var lp = e.Location as LocationPoint;
                if (lp?.Point == null) return null;
                return CadUnits.FeetToMm(lp.Point.X).ToString("0.###", CultureInfo.InvariantCulture) + "," +
                       CadUnits.FeetToMm(lp.Point.Y).ToString("0.###", CultureInfo.InvariantCulture) + "," +
                       CadUnits.FeetToMm(lp.Point.Z).ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }
    }
}
