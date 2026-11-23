using Horizun.Revit.Core;
using Xunit;
using Newtonsoft.Json.Linq;

namespace Horizun.Core.Tests;
public sealed class ScheduleAssociationTests
{
    [Fact]
    public void ScheduleSourceRequiresItsScheduleId()
    {
        var request = JObject.Parse("{\"view_id\":1,\"source\":\"schedule\",\"notes\":[{\"element_id\":2,\"end_mm\":[0,0,0],\"note_mm\":[1,1,0]}]}");
        Assert.Contains("schedule_id", NoteLeaderRules.ValidateWrite("note_from_model", request));
        request["schedule_id"] = 3;
        Assert.Null(NoteLeaderRules.ValidateWrite("note_from_model", request));
        request["source"] = "layers";
        Assert.Contains("schedule_id", NoteLeaderRules.ValidateWrite("note_from_model", request));
    }
    [Fact]
    public void RecordRoundTripsAndRejectsDamagedOrWrongNote()
    {
        var record = new ScheduleAssociation.Record { NoteUniqueId = "note", ScheduleUniqueId = "schedule", SourceUniqueId = "source", SourceHash = "hash", RecordedUtc = "2026-11-03T00:00:00Z" };
        string json = ScheduleAssociation.Serialize(record);
        var read = ScheduleAssociation.Parse(json, "note");
        Assert.Equal("schedule", read.ScheduleUniqueId);
        Assert.Equal("source", read.SourceUniqueId);
        Assert.Equal("hash", read.SourceHash);
        Assert.Equal(record.RecordedUtc, read.RecordedUtc);
        Assert.Null(ScheduleAssociation.Parse(json, "other"));
        Assert.Null(ScheduleAssociation.Parse("{broken", "note"));
        Assert.Null(ScheduleAssociation.Parse("{}", "note"));
    }
}
