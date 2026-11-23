// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_model_diff origin split: which changed elements this MCP session wrote
// (its own session record) and which it did not. "Not in the session record" is
// never claimed to be someone else's work: it also holds hand edits in this Revit
// and writes older than the record.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ModelDiffOriginTests
    {
        private static DiffRow Row(string state, long id) => new DiffRow { State = state, ElementId = id, UniqueId = "u" + id };
        private static DiffResult Result(params DiffRow[] rows) { var r = new DiffResult(); r.Rows.AddRange(rows); return r; }
        private static readonly DateTime T0 = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Truncated_record_cannot_prove_zero_session_changes()
        {
            DiffResult r = Result(Row("modified", 42));
            JObject o = ModelDiffOrigin.Classify(r, new ModelDiffOrigin.SessionWrite[0], T0, true);
            Assert.Equal("unknown", r.Rows[0].Origin);
            Assert.Equal(1, (int)o["unknown"]["total"]);
            Assert.Equal(JTokenType.Null, o["horizun_session"]["total"].Type);
        }

        [Fact]
        public void Rows_are_split_by_the_session_record_and_counted_by_state()
        {
            DiffResult r = Result(Row("added", 1), Row("modified", 2), Row("deleted", 3), Row("modified", 4));
            var writes = new[] { new ModelDiffOrigin.SessionWrite { AtUtc = T0.AddMinutes(1), Ids = new long[] { 1, 3 } } };
            JObject o = ModelDiffOrigin.Classify(r, writes, T0, false);
            Assert.Equal("horizun_session", r.Rows[0].Origin);
            Assert.Equal("not_in_session_record", r.Rows[1].Origin);
            Assert.Equal("horizun_session", r.Rows[2].Origin);
            Assert.Equal(2, (int)o["horizun_session"]["total"]);
            Assert.Equal(1, (int)o["horizun_session"]["added"]);
            Assert.Equal(1, (int)o["horizun_session"]["deleted"]);
            Assert.Equal(2, (int)o["not_in_session_record"]["modified"]);
            Assert.True((bool)o["assessed"]);
        }

        [Fact]
        public void Writes_before_the_baseline_snapshot_do_not_count_as_changes_since_it()
        {
            DiffResult r = Result(Row("modified", 7));
            var writes = new[] { new ModelDiffOrigin.SessionWrite { AtUtc = T0.AddMinutes(-5), Ids = new long[] { 7 } } };
            JObject o = ModelDiffOrigin.Classify(r, writes, T0, false);
            Assert.Equal("not_in_session_record", r.Rows[0].Origin);
            Assert.Equal(0, (int)o["horizun_session"]["total"]);
        }

        [Fact]
        public void A_full_session_record_is_not_called_complete_and_unknown_is_never_zero()
        {
            DiffResult r = Result(Row("added", 9));
            JObject o = ModelDiffOrigin.Classify(r, new ModelDiffOrigin.SessionWrite[0], T0, true);
            Assert.False((bool)o["session_record_complete"]);
            Assert.Equal(1, (int)o["not_in_session_record"]["total"]);
            Assert.Contains("hand", (string)o["note"]);
        }

        [Fact]
        public void Without_a_live_side_nothing_is_assessed_and_rows_stay_unmarked()
        {
            DiffResult r = Result(Row("added", 1));
            JObject o = ModelDiffOrigin.NotAssessed("both sides are stored snapshots");
            Assert.False((bool)o["assessed"]);
            Assert.Null(r.Rows[0].Origin);
            Assert.Null(o["horizun_session"]);
        }
    }
}
