using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class JoinGeometryCommand
    {
        /// <summary>
        /// Diagnostic write-ahead record, not a model-save or transaction receipt.
        /// Subtransactions do not contain a native access violation. This record survives
        /// it and identifies the last pending API and all candidate pairs for re-reading.
        /// A caller must never skip pairs solely because a subtransaction committed.
        /// </summary>
        private sealed class PairProgress
        {
            public string Path { get; }

            public PairProgress(Document document, string operation, IEnumerable<Pair> pairs)
            {
                string directory = System.IO.Path.Combine(HorizunPaths.LogsDir(), "join-pairs");
                Directory.CreateDirectory(directory);
                Path = System.IO.Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
                var header = new JObject
                {
                    ["schema"] = "horizun.join-pair-progress/1", ["utc"] = DateTime.UtcNow.ToString("O"),
                    ["pid"] = Process.GetCurrentProcess().Id, ["operation"] = operation,
                    ["model_path"] = document.PathName, ["project_uid"] = document.ProjectInformation?.UniqueId,
                    ["pairs"] = new JArray(pairs.Select(p => new JObject
                    {
                        ["a"] = p.A.Id, ["b"] = p.B.Id, ["action"] = p.Action,
                        ["wanted_cutter"] = p.WantedCutter
                    })),
                    ["resume"] = "Reopen the last saved model, compare model identity, re-read every pair and rehearse again. Exclude the last pending native pair for diagnosis. No line proves model persistence."
                };
                Append(header);
            }

            public void Record(Pair pair, string phase, string reason = null) => Append(new JObject
            {
                ["utc"] = DateTime.UtcNow.ToString("O"), ["phase"] = phase,
                ["a"] = pair == null ? JValue.CreateNull() : (JToken)pair.A.Id,
                ["b"] = pair == null ? JValue.CreateNull() : (JToken)pair.B.Id,
                ["reason"] = reason
            });

            private void Append(JObject row)
            {
                // Fail closed when diagnosis cannot be persisted, before the next native call.
                FileJobSink.Instance.Append(Path, row.ToString(Formatting.None));
            }
        }
    }
}
