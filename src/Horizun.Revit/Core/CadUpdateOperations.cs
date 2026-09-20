// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// AN UPDATE THAT CAN BE CONTINUED, AND SAY WHAT IT IS CONTINUING.
//
// Three things get called "retry" and they are not the same:
//
//   REPEAT        the same work, already finished. The answer is the reply that
//                 run produced, and nothing runs again.
//   CONTINUE      the same work, half done. The answer is to carry out what is
//                 still pending - and only that - under the decisions that were
//                 authorised for it, against the same world they were authorised
//                 against.
//   A NEW PLAN    the world moved. The old decisions do not carry: they were
//                 answers to a question that has changed.
//
// The in-session ledger covered the first and remembered the third existed. This
// covers the second, and it is durable on purpose: "save, close, open a new
// session and continue" is the case a caller actually has, and a record that lives
// in one Revit process cannot serve it.
//
// What a record holds, and why each part:
//
//   binding      the world the decisions were authorised against. A continuation
//                re-measures it through the same guard as a first apply: if
//                anything moved, the continuation is refused, because a decision
//                is an answer to a question and the question has changed.
//   decisions    what the caller authorised, and over which elements. A
//                continuation never invents them, and never widens them.
//   actions      one row each: confirmed, pending or failed, with the elements it
//                created or removed. That is what makes "carry out the rest"
//                mean something precise rather than "run it again and hope".
//
// The record is on this machine, beside the bridge's other state. It does not
// travel with the model - a continuation on another machine has no record and is
// told so, which is the safe direction.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadUpdateOperations
    {
        public const string Confirmed = "confirmed";
        public const string Pending = "pending";
        public const string Failed = "failed";

        public static string Root
        {
            get
            {
                string dir = Path.Combine(HorizunPaths.DataRoot(), "cad-update-operations");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        public static string PathFor(string operationId) =>
            Path.Combine(Root, "op-" + CadIdentity.Sha256Hex(operationId ?? "(none)").Substring(0, 32) + ".json");

        /// <summary>
        /// Open a record for this operation, with every action pending. An id already recorded is
        /// returned as it stands - beginning twice is what a retry looks like from here.
        /// </summary>
        public static JObject Begin(string operationId, string document, string placementId,
                                    JObject binding, JObject decisions, JArray actions)
        {
            JObject existing = Read(operationId);
            if (existing != null) return existing;
            var rows = new JArray();
            foreach (JObject a in (actions ?? new JArray()).OfType<JObject>())
                rows.Add(new JObject
                {
                    ["key"] = a["key"], ["tool"] = a["tool"], ["state"] = Pending,
                    ["created"] = new JArray(), ["removed"] = new JArray()
                });
            var o = new JObject
            {
                ["schema"] = "horizun.cad-update-operation/1",
                ["operation_id"] = operationId,
                ["document"] = document,
                ["placement_id"] = placementId,
                ["opened_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["state"] = "open",
                ["binding"] = binding == null ? null : (JObject)binding.DeepClone(),
                ["decisions"] = decisions == null ? null : (JObject)decisions.DeepClone(),
                ["actions"] = rows
            };
            Write(operationId, o);
            return o;
        }

        /// <summary>Record what one action did, the moment it did it: a crash after this leaves the truth.</summary>
        public static void MarkAction(string operationId, string key, string state,
                                      IEnumerable<long> created, IEnumerable<long> removed, string error)
        {
            JObject o = Read(operationId);
            if (o == null) return;
            foreach (JObject row in (o["actions"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (!string.Equals(row.Value<string>("key"), key, StringComparison.Ordinal)) continue;
                row["state"] = state;
                row["at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                if (created != null) row["created"] = new JArray(created.Select(x => (JToken)x));
                if (removed != null) row["removed"] = new JArray(removed.Select(x => (JToken)x));
                if (error != null) row["error"] = error;
            }
            Write(operationId, o);
        }

        public static void Finish(string operationId, string state)
        {
            JObject o = Read(operationId);
            if (o == null) return;
            o["state"] = state;
            o["closed_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Write(operationId, o);
        }

        public static JObject Read(string operationId)
        {
            try
            {
                string p = PathFor(operationId);
                return File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The binding's touched_elements MINUS the elements this operation's own confirmed actions
        /// created, removed or re-shaped.
        ///
        /// WITHOUT THIS A CONTINUATION IS IMPOSSIBLE. The binding records what the elements looked like
        /// when the plan was made; a confirmed action has since moved one on purpose, so the guard would
        /// re-measure it, find it different, and refuse the rest of the work as a stale plan - the
        /// operation held against itself. What the guard still has to catch is somebody ELSE's change,
        /// and every element this operation did not touch is still measured exactly as before.
        ///
        /// A null record (a fresh apply) changes nothing: everything is held to its print.
        /// </summary>
        public static JArray ExceptWhatItDidItself(JArray touched, JObject record)
        {
            if (touched == null || record == null) return touched;
            var mine = new HashSet<long>();
            foreach (JObject row in (record["actions"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (row.Value<string>("state") != Confirmed) continue;
                foreach (JToken id in (row["created"] as JArray ?? new JArray())) mine.Add((long)id);
                foreach (JToken id in (row["removed"] as JArray ?? new JArray())) mine.Add((long)id);
            }
            if (mine.Count == 0) return touched;
            var kept = new JArray();
            foreach (JObject e in touched.OfType<JObject>())
            {
                long id = e.Value<long?>("element_id") ?? -1;
                if (!mine.Contains(id)) kept.Add(e.DeepClone());
            }
            return kept;
        }

        /// <summary>The keys still to do, in the order the plan emitted them.</summary>
        public static List<string> PendingKeys(JObject record) =>
            (record?["actions"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(r => r.Value<string>("state") != Confirmed)
                .Select(r => r.Value<string>("key")).ToList();

        /// <summary>What a reply says about an operation, whatever shape the call took.</summary>
        public static JObject Describe(JObject record, string shape)
        {
            if (record == null) return new JObject { ["shape"] = shape };
            var rows = (record["actions"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            return new JObject
            {
                ["operation_id"] = record["operation_id"],
                ["shape"] = shape,
                ["opened_utc"] = record["opened_utc"],
                ["state"] = record["state"],
                ["confirmed"] = new JArray(rows.Where(r => r.Value<string>("state") == Confirmed).Select(r => r["key"])),
                ["pending"] = new JArray(rows.Where(r => r.Value<string>("state") == Pending).Select(r => r["key"])),
                ["failed"] = new JArray(rows.Where(r => r.Value<string>("state") == Failed).Select(r => r["key"])),
                // MEASURED THE FIRST TIME THIS RAN: an action that FAILED is not pending and not confirmed,
                // so a reply that listed only "pending" said nothing was left while a whole action was.
                // This is the list a continuation actually runs, failures included.
                ["still_to_do"] = new JArray(PendingKeys(record).Select(k => (JToken)k)),
                ["created"] = new JArray(rows.SelectMany(r => (r["created"] as JArray ?? new JArray()))),
                ["removed"] = new JArray(rows.SelectMany(r => (r["removed"] as JArray ?? new JArray()))),
                ["decisions_authorised"] = record["decisions"],
                ["means"] = shape == "continued"
                    ? "this call carried out what an earlier one left pending, under the decisions that call " +
                      "was given and against the same world they were authorised against. Nothing already " +
                      "confirmed ran again."
                    : shape == "replayed"
                    ? "this operation was already finished. Nothing ran; this is what it produced."
                    : "a fresh operation. Its id is how a later call continues it if this one does not finish."
            };
        }

        private static void Write(string operationId, JObject o)
        {
            try { File.WriteAllText(PathFor(operationId), o.ToString(Formatting.Indented)); }
            catch { /* a record that cannot be written leaves continuation unavailable, which is safe */ }
        }
    }
}
