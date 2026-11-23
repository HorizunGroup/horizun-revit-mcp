// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_api_help carries the ElementType.Duplicate trap (feedback §9): IronPython resolves the
// overload badly when called bound; call it unbound, ElementType.Duplicate(base, "name"). The note
// is on the type ElementType, so every subclass (WallType, FloorType...) shows it through the base chain.
// -----------------------------------------------------------------------------
using System;
using System.Collections;
using System.Reflection;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ApiHelpDuplicateNoteTests
    {
        [Fact]
        public void ElementType_has_the_unbound_duplicate_note()
        {
            FieldInfo f = typeof(ApiHelpRules).GetField("FieldTrapsByType", BindingFlags.NonPublic | BindingFlags.Static);
            var map = (IDictionary)f.GetValue(null);
            Assert.True(map.Contains("Autodesk.Revit.DB.ElementType"));
            string text = string.Join(" ", (string[])map["Autodesk.Revit.DB.ElementType"]);
            Assert.Contains("ElementType.Duplicate(base, \"name\")", text);
            Assert.Contains("unbound", text);
            Assert.DoesNotContain("WallType", text);   // only what was measured: no claim about subclasses or casts
            Assert.Contains("IronPython", text);
        }
    }
}
