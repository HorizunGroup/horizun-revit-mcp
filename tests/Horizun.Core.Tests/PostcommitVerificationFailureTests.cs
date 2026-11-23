using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PostcommitVerificationFailureTests
    {
        [Fact]
        public void A_diagnostic_failure_after_assimilation_reports_the_already_committed_write()
        {
            var doc = new Document { TestConfirmationStore = new ConfirmationStore() };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var request = JObject.Parse("{'operation':'add_split_line','element_id':41}");
            CommandResult Run() => ModelEditRunner.Run(app, request.ToString(), "horizun_slab_shape", "Split", _ => null,
                (_, _, _) => new ArchModelEdit
                {
                    Apply = _ => { },
                    Verify = d =>
                    {
                        if (d.Assimilated) throw new InvalidOperationException("diagnostic sink unavailable");
                        var check = new PostconditionCheck("crease");
                        check.Compare("crease", 1, 1);
                        return check;
                    }
                });
            var preview = Run();
            Assert.True(preview.Success, preview.Error);
            request["confirmation_token"] = Assert.IsType<JObject>(preview.Data)["confirmation_token"];
            request["idempotency_key"] = "postcommit-failure";
            request["dry_run"] = false;

            var applied = Run();

            Assert.False(applied.Success);
            Assert.True(doc.Assimilated);
            Assert.True(applied.Detail.Value<bool>("changes_applied"));
            Assert.Equal("diagnostic sink unavailable", applied.Detail.Value<string>("verification_error"));
            Assert.Equal(ErrorCodes.VerificacionFallida, ErrorCodes.Classify(applied.Error, applied.Detail).Code);
        }
    }
}
