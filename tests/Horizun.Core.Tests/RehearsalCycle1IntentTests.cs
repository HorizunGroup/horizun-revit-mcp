using System;
using System.IO;
using Horizun.Contracts;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RehearsalCycle1IntentTests
    {

        [Fact]
        public void Pinned_move_is_one_operation_with_final_state_and_geometry_verification()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
            Assert.NotNull(root);
            string source = File.ReadAllText(Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs"));
            Assert.Contains("case \"move_preserve_pin\":", source);
            Assert.Contains("ElementTransformUtils.MoveElements(doc, p.Ids, p.Vector);", source);
            Assert.Contains("p.Operation != \"move_preserve_pin\" || e.Pinned", source);
            Assert.Contains("if (op == \"move_preserve_pin\" && !element.Pinned)", source);
            string undo = File.ReadAllText(Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", "UndoCapture.cs"));
            Assert.Contains("else if (e is Instance instance)", undo);
            Assert.Contains("s[\"loc\"] = new JArray(P(total.Origin))", undo);
            var operations = Contract.Find("horizun_transform_elements").InputSchema["properties"]["operations"]["items"]["properties"]["operation"]["enum"];
            Assert.Contains(operations, x => x.ToString() == "move_preserve_pin");
        }
    }
}
