// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A HOOK LENGTH IS DECLARED, NEVER CHOSEN. "Estribo #3 con ganchos de 135 de .10"
// is a straight extension of 100 mm somebody is answerable for, and Revit stores
// it on the BAR TYPE, per hook type - so writing it changes every bar of that
// type with that hook, not only the bars this set builds. These tests hold the
// parser to the declaration (refusals name their path) and hold the plan, the
// apply's re-read and the audit to one comparison, made on the row the Revit
// reader emits (HookLengthRules.ReadRow) and nothing else.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HookLengthTests
    {
        private const string Head = @"{
  'schema': 'horizun.structural-requirements/1',
  'requirement_set': { 'id': 'stirrups', 'version': '1.0.0' },
  'units': 'millimeter',
  'bar_types': [ { 'id': 'N3', 'type_name': '#3' }, { 'id': 'N5', 'type_name': '#5' } ],";

        private const string OneRule = @",
  'reinforcement_rules': [ {
      'id': 'bottom', 'host': { 'category': 'OST_StructuralFraming' }, 'bar_type': 'N5', 'style': 'standard',
      'curve_mm': [[0,0,0],[4000,0,0]], 'normal': [0,1,0],
      'layout': { 'rule': 'fixed_number', 'number': 4, 'array_length_mm': 300 }
  } ]";























































        [Fact]
        public void The_finding_code_is_published()
        {
            Assert.Equal("hook_length_differs", RebarFinding.HookLengthDiffers);
            Assert.Contains(RebarFinding.HookLengthDiffers, RebarFinding.All);
        }
    }
}
