// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHETHER THE VALUES A RULE DECLARED ACTUALLY LANDED.
//
// This is arithmetic over the parameter writer's own answer, and it lives here
// rather than in the command because it got the answer wrong twice, in opposite
// directions, and neither mistake could be reproduced without a Revit.
//
// FIRST IT READ KEYS THE WRITER DOES NOT EMIT - data["verified"], data["written"]
// - so null became zero and every successful write was reported as a failure.
//
// THEN THE CORRECTION OVERSHOT. It demanded that every row be confirmed against
// the exact value passed, which sounds like the stricter reading and is not: a
// STRING on a numeric parameter - "900 mm" on a sill height, "60" on a Double
// fire rating, which is the example in this bridge's own documentation - is
// applied by the writer through SetValueString and lands under
// writes_confirmed_by_parse_read_back_only. It is written. It is re-read. It is
// in the model. And it was counted as not written, which drove the stage to
// applied_without_parameters, stopped every later stage, and told somebody their
// correctly annotated elements carry none of the values they can see on screen.
//
// So the rule is stated once, here, where every case is a test:
//
//   LANDED     = confirmed-against-your-value + confirmed-by-parse
//   NOT LANDED = failed + unknown
//
// all_written is about LANDING. How strong the evidence for each landing was is a
// separate fact, reported beside it and never folded into it - because "written,
// and re-read as a formatted string rather than as your exact bytes" and "not
// written" are different things to tell somebody, and only one of them is a
// reason to stop.
//
// UNKNOWN COUNTS AS NOT LANDED, deliberately. The writer says unknown when it
// could not re-read what it set, and a conversion that treated that as success
// would be claiming exactly what this bridge exists not to claim.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadParameterOutcome
    {
        /// <summary>
        /// Fold the writer's reply into the fields a conversion stage reports.
        ///
        /// <paramref name="writerResult"/> is horizun_write_params_verified's own
        /// data object, or null when the call failed outright. <paramref
        /// name="requested"/> is how many writes were handed to it.
        /// </summary>
        public static JObject Summarise(JObject writerResult, int requested)
        {
            int exact = Count(writerResult, "writes_confirmed_against_your_value");
            int byParse = Count(writerResult, "writes_confirmed_by_parse_read_back_only");
            int failed = Count(writerResult, "failed");
            int unknown = Count(writerResult, "unknown");
            int unresolved = Count(writerResult, "unresolved");

            int landed = exact + byParse;
            bool all = writerResult != null &&
                       failed == 0 && unknown == 0 && unresolved == 0 &&
                       landed == requested && requested >= 0;

            return new JObject
            {
                ["all_written"] = all,
                ["landed"] = landed,
                ["confirmed_against_your_value"] = exact,
                ["confirmed_by_parse_read_back_only"] = byParse,
                ["failed"] = failed,
                ["unknown"] = unknown,
                ["unresolved"] = unresolved,
                ["evidence_means"] =
                    "all_written is about LANDING: every row the writer re-read from the model after its own " +
                    "commit, whether it matched the exact value passed or the formatted value Revit parsed " +
                    "from it. A unit string on a numeric parameter - '900 mm' on a sill height - can only be " +
                    "confirmed the second way, and it IS written. failed and unknown are the ones that are " +
                    "not: unknown means the writer could not re-read what it set, which is not success.",
                ["strength_means"] =
                    "confirmed_against_your_value is the stronger evidence and " +
                    "confirmed_by_parse_read_back_only the weaker. The difference is reported and never " +
                    "folded into all_written, because 'written, and read back as Revit formats it' and 'not " +
                    "written' are different things to tell somebody and only one of them is a reason to stop."
            };
        }

        /// <summary>
        /// What a REHEARSAL may say about parameters, which is not "all_written".
        ///
        /// A dry run of the conversion delegates its creates to create_elements
        /// with dry_run=true, and that reply carries no rows - the elements do not
        /// exist, so no instance id exists to write against. The old code turned
        /// that into "requested: 0, all_written: true": a rehearsal reporting
        /// success over something it never looked at, which is the one claim this
        /// bridge may never make.
        ///
        /// So it reports what it CAN: the values that would be written, and the
        /// plain fact that none of them was rehearsed and why.
        /// </summary>
        public static JObject NotRehearsed(JArray declared, int rows)
        {
            return new JObject
            {
                ["rehearsed"] = false,
                ["all_written"] = null,
                ["declared"] = declared ?? new JArray(),
                ["declared_count"] = declared == null ? 0 : declared.Count,
                ["rows_awaiting_creation"] = rows,
                ["why"] =
                    "NOTHING WAS REHEARSED. A parameter is written against an element id, and in a rehearsal " +
                    "the elements do not exist yet - create_elements rehearses them and returns no ids, " +
                    "because there are none to return. So the values below are what WOULD be written and " +
                    "nothing here has checked that any of them can be: a misspelt parameter name, a " +
                    "read-only parameter or a value Revit will not take is discovered when the apply runs, " +
                    "after the elements are committed.",
                ["not_all_written"] =
                    "all_written is null on purpose. It is neither true nor false here, and reporting true " +
                    "would be a rehearsal claiming success over work it never measured."
            };
        }

        private static int Count(JObject o, string key)
        {
            if (o == null) return 0;
            int? v = o.Value<int?>(key);
            return v ?? 0;
        }
    }
}
