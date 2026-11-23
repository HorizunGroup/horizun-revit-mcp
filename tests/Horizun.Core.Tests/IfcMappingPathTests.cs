using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public class IfcMappingPathTests
{
    [Fact]
    public void MissingRelativeMappingReportsResolvedPathAsStructuredData()
    {
        var method = typeof(IfcDeliveryRules).GetMethod("ResolveMappingPathWithEvidence");
        Assert.NotNull(method);
        string context = Path.GetFullPath(Path.Combine("sample", "project-context.json"));
        object[] args = { "missing.txt", context, (System.Func<string, bool>)(_ => false), null, null };
        Assert.Null(method.Invoke(null, args));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(context), "missing.txt"), args[4]);
        Assert.Contains((string)args[4], (string)args[3]);
        var fields = new JObject { ["pset_mapping_resolved"] = (string)args[4], ["project_context_resolved"] = context };
        var failed = CommandResult.Fail((string)args[3]).CarryDetails(fields);
        Assert.Equal((string)args[4], (string)failed.Detail["pset_mapping_resolved"]);
        var successful = CommandResult.Ok(new JObject { ["dry_run"] = true }).CarryDetails(fields);
        Assert.Equal(context, (string)((JObject)successful.Data)["project_context_resolved"]);
    }
    [Fact]
    public void AbsolutePathNeedsNoContext()
    {
        string path = Path.GetFullPath(Path.Combine("sample", "mapping.txt"));
        Assert.Equal(path, IfcDeliveryRules.ResolveMappingPath(path, null, _ => true, out string error));
        Assert.Null(error);
    }

    [Fact]
    public void RelativePathUsesContextAndNormalizes()
    {
        string dir = Path.GetFullPath("sample");
        string context = Path.Combine(dir, "project-context.json");
        string path = IfcDeliveryRules.ResolveMappingPath(Path.Combine("nested", "..", "mapping.txt"), context, _ => true, out string error);
        Assert.Null(error);
        Assert.Equal(Path.Combine(dir, "mapping.txt"), path);
    }

    [Fact]
    public void RelativePathWithoutContextExplainsHowToFixC6Failure()
    {
        Assert.Null(IfcDeliveryRules.ResolveMappingPath("mapping.txt", null, _ => true, out string error));
        Assert.Equal("relative pset_mapping_path needs a project-context.json; send an absolute path", error);
        Assert.DoesNotContain("must be absolute", error);
    }

    [Fact]
    public void EscapingAndMissingPathsAreRejected()
    {
        string context = Path.GetFullPath(Path.Combine("sample", "project-context.json"));
        Assert.Null(IfcDeliveryRules.ResolveMappingPath(Path.Combine("..", "mapping.txt"), context, _ => true, out string escape));
        Assert.Contains("escapes", escape);
        Assert.Null(IfcDeliveryRules.ResolveMappingPath("missing.txt", context, _ => false, out string missing));
        Assert.Contains("does not exist", missing);
    }
}
