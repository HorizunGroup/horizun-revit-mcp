using System;
using System.IO;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

public class R10APlanTests
{
    [Theory]
    [InlineData("layers", "Floor\nConcrete 100 mm\nFinish 20 mm")]
    [InlineData("schedule", "Mark\tDescription\n1\tWall")]
    public void Note_proposal_is_a_create_in_resolved_plan_and_preview(string source, string text)
    {
        var plan = new ResolvedPlan();
        var inputs = new Dictionary<string, string> { ["source"] = source, ["text"] = text, ["source_unique_id"] = "source-a", ["view_unique_id"] = "view-a" };
        plan.Elements.Add(NoteLeaderRules.PlannedCreation("text_note", 0, inputs));
        Assert.Equal(1, plan.CreateCount);
        Assert.Equal(1, (int)PlanPreview.Describe(plan)["total"]);
        Assert.Equal("create", (string)PlanPreview.Describe(plan)["rows"][0]["action"]);
        var fingerprint = plan.Fingerprint();
        inputs["text"] += " changed";
        plan.Elements[0] = NoteLeaderRules.PlannedCreation("text_note", 0, inputs);
        Assert.NotEqual(fingerprint, plan.Fingerprint());
    }

    [Fact]
    public void Existing_stale_note_is_not_applicable_with_reason()
    {
        var row = NoteLeaderRules.ExistingNoteOutcome(123, false);
        Assert.Equal("not_applicable", (string)row["status"]);
        Assert.Contains("stale", (string)row["reason"]);
    }

    [Fact]
    public void Cloned_tags_are_creates_per_target()
    {
        var p = new ResolvedPlan();
        p.Elements.Add(NoteLeaderRules.PlannedCreation("tag", 0, new Dictionary<string, string> { ["target_unique_id"] = "a" }));
        p.Elements.Add(NoteLeaderRules.PlannedCreation("tag", 1, new Dictionary<string, string> { ["target_unique_id"] = "b" }));
        Assert.Equal(2, p.CreateCount);
        Assert.Equal(2, (int)PlanPreview.Describe(p)["total"]);
    }
    [Fact]
    public void Creation_paths_materialize_their_proposals()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
        Assert.NotNull(d);
        var notes = File.ReadAllText(Path.Combine(d.FullName, "src/Horizun.Revit/Commands/PlanimetryOpsCommand.Notes.cs"));
        var legend = File.ReadAllText(Path.Combine(d.FullName, "src/Horizun.Revit/Commands/PlanimetryOpsCommand.cs"));
        Assert.Contains("NoteLeaderRules.PlannedCreation", notes);
        Assert.Contains("NoteLeaderRules.PlannedCreation", legend);
    }
}
