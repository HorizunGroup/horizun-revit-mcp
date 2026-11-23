using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FamilyReloadPlanTests
    {
        private sealed class FamilyHandle
        {
            public bool Valid = true;
            public string UniqueId => Valid ? "family-uid" : throw new System.InvalidOperationException("The referenced object is not valid");
        }


        [Fact]
        public void ReloadFailuresKeepTheirExplicitCodesDespiteMisleadingMessages()
        {
            const string open = "Close the family in Revit, saving it first if it has changes, or pass a copy. Nothing was written.";
            const string invalid = "Family reload failed: The referenced object is not valid. The transaction rolled back (Revit reported RolledBack), so nothing was changed";
            Assert.Equal("family_document_open", (string)ErrorCodes.Ensure(open, new Newtonsoft.Json.Linq.JObject { ["error_code"] = ErrorCodes.FamilyDocumentOpen })["error_code"]);
            Assert.Equal("api_revit_excepcion", (string)ErrorCodes.Ensure(invalid, new Newtonsoft.Json.Linq.JObject { ["error_code"] = ErrorCodes.ApiRevitExcepcion })["error_code"]);
        }































    }
}
