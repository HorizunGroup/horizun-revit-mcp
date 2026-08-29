// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The Revit API types cannot be instantiated in the offline test process, so this
// pins the three pieces of the inline-accessory safety contract in source.  The
// live release gate supplies the behavioural proof in every Revit year.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class InlineAccessoryWiringTests
    {
        private static string Source()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string path = Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands",
                                           "CreateElementsCommand.cs");
                if (File.Exists(path)) return File.ReadAllText(path);
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        [Fact]
        public void Inline_accessory_cuts_the_real_connector_gap_and_removes_the_middle()
        {
            string src = Source();

            Assert.Contains("orderedEnds[0].Along", src);
            Assert.Contains("orderedEnds[1].Along", src);
            Assert.Contains("connectorMidpoint", src);
            Assert.Contains("ElementTransformUtils.MoveElement(doc, placed.Id, seatingMove)", src);
            Assert.Contains("PlumbingUtils.BreakCurve", src);
            Assert.Contains("doc.Delete(middle.Id)", src);
        }

        [Fact]
        public void Inline_accessory_proves_both_distinct_pipe_connections_after_commit()
        {
            string src = Source();

            Assert.Contains("ExpectedInlineConnections = plan.Kind == \"accessory_inline\"", src);
            Assert.Contains("connectedPipingConnectors == 2", src);
            Assert.Contains("connectedPipeIds.Count == 2", src);
            Assert.Contains("[\"inline_connections\"]", src);
        }

        [Fact]
        public void Inline_accessory_refuses_a_transient_or_wrong_owner_connection_inside_the_transaction()
        {
            string src = Source();

            Assert.Contains("other?.Owner is Pipe pipe", src);
            Assert.Contains("reachesExpectedPipe", src);
            Assert.Contains("batch rolls back rather than keep a half-", src);
        }
    }
}
