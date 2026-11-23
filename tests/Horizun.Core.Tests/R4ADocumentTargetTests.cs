using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;
using Newtonsoft.Json.Linq;
public class R4ADocumentTargetTests
{
    [Theory]
    [InlineData("Tower")]
    [InlineData("tower.RVT")]
    [InlineData(@"C:\models\tower.rvt")]
    public void FindsOpenTitleOrFile(string target)
    {
        var open = new List<DocIdentity> { new DocIdentity { Title = "Tower", Path = @"C:\models\Tower.rvt" } };
        Assert.Equal(DocMatchOutcome.Matched, DocumentMatcher.FindTarget(open, target).Outcome);
    }
    [Fact]
    public void DuplicateNamesAreAmbiguousButPathsDistinguishThem()
    {
        var open = new List<DocIdentity> { new DocIdentity { Title = "Tower", Path = @"C:\a\Tower.rvt" }, new DocIdentity { Title = "Tower", Path = @"C:\b\Tower.rvt" } };
        Assert.Equal(DocMatchOutcome.Ambiguous, DocumentMatcher.FindTarget(open, "tower.rvt").Outcome);
        Assert.Equal(1, DocumentMatcher.FindTarget(open, @"c:\b\tower.rvt").Index);
    }
    [Fact]
    public void ReportedPythonFailuresResolveFullPathAndFilenameEvenWhenTitleDiffers()
    {
        var open = new List<DocIdentity> {
            new DocIdentity { Title = "Active", Path = @"C:\fixtures\active.rvt" },
            new DocIdentity { Title = "Different displayed title", Path = @"C:\fixtures\target.rvt" }
        };
        Assert.Equal(1, DocumentMatcher.FindTarget(open, @"c:/fixtures/TARGET.rvt").Index);
        Assert.Equal(1, DocumentMatcher.FindTarget(open, "target").Index);
        Assert.Equal(1, DocumentMatcher.FindTarget(open, "Different displayed title").Index);
        Assert.Equal(DocMatchOutcome.None, DocumentMatcher.FindTarget(open, @"C:\other\target.rvt").Outcome);
    }

    [Horizun.Core.Tests.WindowsTheory]
    [InlineData("horizun_execute_python")]
    [InlineData("horizun_query_model")]
    [InlineData("horizun_write_params_verified")]
    public void InactiveTargetOffersActivationThenExactOriginalCall(string tool)
    {
        var args = JObject.Parse("{target_document:'target', code:'print(1)', idempotency_key:'original'}");
        var detail = DocumentActivationSuggestion.Build(new DocIdentity { Path = @"C:\fixtures\target.rvt" }, tool, args, true);
        var call = detail["suggested_call"];
        Assert.Equal("horizun_open_document", (string)call["tool"]);
        Assert.Equal(@"C:\fixtures\target.rvt", (string)call["arguments"]["path"]);
        Assert.True(System.Guid.TryParse((string)call["arguments"]["idempotency_key"], out _));
        Assert.Equal(tool, (string)call["then"]["tool"]);
        Assert.True(JToken.DeepEquals(args, call["then"]["arguments"]));
        args["code"] = "changed";
        Assert.Equal("print(1)", (string)call["then"]["arguments"]["code"]);
    }

    [Theory]
    [InlineData("Unsaved", null, false)]
    [InlineData("Detached", @"C:\fixtures\detached.rvt", false)]
    [InlineData("Cloud", @"C:\fixtures\cloud.rvt", true)]
    public void InactiveDocumentSuggestsTypedActivation(string title, string path, bool cloud)
    {
        var identity = new DocIdentity { Title = title, Path = path, ModelGuid = cloud ? "cloud" : null };
        var call = DocumentActivationSuggestion.Build(identity, "tool", new JObject(), false)["suggested_call"];
        Assert.Equal("horizun_document_session", (string)call["tool"]);
        Assert.Equal("activate", (string)call["arguments"]["operation"]);
        Assert.Equal(title, (string)call["arguments"]["target_document"]);
        if (!cloud) Assert.Contains("save_as_path", (string)call["condition"]);
    }

    [Theory]
    [InlineData(false, false, true, "local")]
    [InlineData(true, false, false, "cloud")]
    [InlineData(false, false, false, "save_as")]
    [InlineData(false, true, true, "save_as")]
    [InlineData(true, true, true, "save_as")]
    public void SelectsActivationByDocumentFacts(bool cloud, bool detached, bool exists, string expected)
    {
        Assert.Equal(expected, ActivationChoice.ForDocument(cloud, detached, exists));
    }

    [Theory]
    [InlineData(@"C:\sandbox\ACCDocs\project\new.rvt")]
    [InlineData(@"C:\sandbox\Autodesk Docs\project\new.rvt")]
    [InlineData(@"C:\sandbox\BIM 360\project\new.rvt")]
    [InlineData(@"C:\sandbox\DC\ACC\project\new.rvt")]
    public void RefusesConnectorDestinations(string path)
    {
        Assert.NotNull(ActivationChoice.DestinationProblem(path, false, true));
    }

    [Fact]
    public void RefusesRegisteredConnectorRootWithoutRecognizableName()
    {
        var roots = new[] { @"C:\custom\workspace" };
        Assert.NotNull(ActivationChoice.RegisteredRootProblem(@"c:\CUSTOM\workspace\project\new.rvt", roots));
        Assert.Null(ActivationChoice.RegisteredRootProblem(@"C:\custom\workspace-other\new.rvt", roots));
    }

    [Fact]
    public void RefusesExistingOrUnknownDestination()
    {
        Assert.NotNull(ActivationChoice.DestinationProblem(@"C:\fixtures\new.rvt", true, true));
        Assert.NotNull(ActivationChoice.DestinationProblem(@"C:\fixtures\new.rvt", null, true));
        Assert.Null(ActivationChoice.DestinationProblem(@"C:\fixtures\new.rvt", false, true));
    }

    [Fact]
    public void UnsavedFamilyRequiresANewFamilyFile()
    {
        Assert.Null(ActivationChoice.DestinationProblem(@"C:\fixtures\new.rfa", false, true, true));
        Assert.NotNull(ActivationChoice.DestinationProblem(@"C:\fixtures\new.rvt", false, true, true));
        Assert.NotNull(ActivationChoice.DestinationProblem(@"C:\fixtures\new.rfa", true, true, true));
    }
    [Fact]
    public void PythonAndTypedReadsAndWritesUseTheSharedRefusal()
    {
        string root = System.AppContext.BaseDirectory;
        while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root, "AGENTS.md")))
            root = System.IO.Directory.GetParent(root)?.FullName;
        Assert.NotNull(root);
        string gate = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "Horizun.Revit", "Core", "DocumentGate.cs"));
        string python = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "Horizun.Revit", "Commands", "ExecutePythonCommand.cs"));
        Assert.Contains("DocumentGate.ForReadOnlyScript(app, request, Name)", python);
        Assert.Contains("DocumentGate.ForMutation(app, request, Name)", python);
        Assert.Contains("DocumentActivationSuggestion.Build(openIdentities[match.Index], commandName, request,", gate);
        Assert.Contains("DocumentMatcher.FindTarget(openIdentities, wanted)", gate);
        Assert.Contains("DocumentMatcher.FindTarget(openIdentities, target)", gate);
        Assert.Contains("will NOT switch for you", gate);
    }
}
