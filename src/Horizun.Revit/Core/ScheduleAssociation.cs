using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ScheduleAssociation
    {
        public sealed class Record
        {
            public string NoteUniqueId, ScheduleUniqueId, SourceUniqueId, SourceHash, RecordedUtc;
        }

        public static string Serialize(Record r)
        {
            if (r == null || string.IsNullOrEmpty(r.NoteUniqueId) || string.IsNullOrEmpty(r.ScheduleUniqueId) ||
                string.IsNullOrEmpty(r.SourceUniqueId) || string.IsNullOrEmpty(r.SourceHash) || string.IsNullOrEmpty(r.RecordedUtc))
                throw new ArgumentException("A complete schedule association is required.");
            return new JObject { ["note_unique_id"] = r.NoteUniqueId, ["schedule_unique_id"] = r.ScheduleUniqueId,
                ["source_unique_id"] = r.SourceUniqueId, ["source_hash"] = r.SourceHash, ["recorded_utc"] = r.RecordedUtc }.ToString(Formatting.None);
        }

        public static Record Parse(string json, string noteUniqueId)
        {
            try
            {
                using (var reader = new JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = DateParseHandling.None })
                {
                JObject j = JObject.Load(reader);
                var r = new Record { NoteUniqueId = j.Value<string>("note_unique_id"), ScheduleUniqueId = j.Value<string>("schedule_unique_id"),
                    SourceUniqueId = j.Value<string>("source_unique_id"), SourceHash = j.Value<string>("source_hash"), RecordedUtc = j.Value<string>("recorded_utc") };
                Serialize(r);
                return r.NoteUniqueId == noteUniqueId ? r : null;
                }
            }
            catch { return null; }
        }
    }
}
