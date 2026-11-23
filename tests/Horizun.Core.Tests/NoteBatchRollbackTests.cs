using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NoteBatchRollbackTests
    {
        [Fact]
        public void mid_batch_exception_restores_all_changes_through_runner()
        {
            var doc = new Document();
            doc.Elements[1] = new Element { Id = new ElementId(1) };
            doc.Elements[2] = new Element { Id = new ElementId(2) };
            var original1 = doc.Elements[1]; var original2 = doc.Elements[2];
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            ModelEditRunner.Run(app, "{\"operation\":\"move_note\",\"dry_run\":false}", "test", "test",
                _ => null, (d, r, scale) => new ArchModelEdit {
                    Apply = x => { x.Elements[1] = new Element { Id = new ElementId(10) };
                        x.Elements[2] = new Element { Id = new ElementId(20) };
                        throw new InvalidOperationException("induced mid-batch failure"); },
                    Verify = x => throw new InvalidOperationException("should not verify") });
            Assert.Same(original1, doc.Elements[1]);
            Assert.Same(original2, doc.Elements[2]);
            Assert.False(doc.Assimilated);
        }
    }
}
