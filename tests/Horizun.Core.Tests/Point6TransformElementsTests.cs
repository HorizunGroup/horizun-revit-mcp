using System;
using System.IO;
using System.Linq;
using Xunit;

public sealed class Point6TransformElementsTests
{
    private static string Source(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Move_plan_reports_the_sampling_method_for_view_owned_elements()
    {
        string s = Source("src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs");
        Assert.Contains("FilledRegion", s);
        Assert.Contains("TextNote", s);
        Assert.Contains("verification_method", s);
        Assert.Contains("owner_view_bounding_box", s);
        Assert.Contains("text_note_coordinate", s);
        int sample = s.IndexOf("private static List<XYZ> Samples(Element e, bool boundingBox = false)", StringComparison.Ordinal);
        Assert.True(sample >= 0);
        string sampler = s.Substring(sample, Math.Min(1800, s.Length - sample));
        Assert.Contains("e is FilledRegion", sampler);
        Assert.Contains("e.OwnerViewId", sampler);
        Assert.Contains("e is TextNote note", sampler);
    }

    [Fact]
    public void Slab_shape_apply_surfaces_revit_failure_details_as_actionable_preconditions()
    {
        string s = Source("src", "Horizun.Revit", "Commands", "ModelEditRunner.cs");
        Assert.Contains("precondition", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failure_details", s);
        Assert.Contains("Said()", s);
    }

    [Fact]
    public void Group_preflight_and_rollback_replies_name_the_failed_requirement_and_next_step()
    {
        string s = Source("src", "Horizun.Revit", "Commands", "ManageGroupsCommand.Execution.cs");
        Assert.Contains("ErrorCodes.PrecondicionNoCumplida", s);
        Assert.Contains("[\"how_to_satisfy\"]", s);
        Assert.Contains("ErrorCodes.TransaccionRevertida", s);
        Assert.Contains("[\"failure_details\"]", s);
    }
}
