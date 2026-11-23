using System;
using System.IO;
using System.Linq;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class RevitPythonCompatibilityTests
    {
        private static ScriptScope LoadCompatibilityModule()
        {
            string recipes = Path.Combine(AppContext.BaseDirectory, "Recipes");
            Assert.True(File.Exists(Path.Combine(recipes, "hz_compat.py")));

            ScriptEngine engine = Python.CreateEngine();
            var paths = engine.GetSearchPaths().ToList();
            paths.Add(recipes);
            engine.SetSearchPaths(paths);
            ScriptScope scope = engine.CreateScope();
            engine.Execute(@"
import sys, types
autodesk = types.ModuleType('Autodesk')
revit = types.ModuleType('Autodesk.Revit')
db = types.ModuleType('Autodesk.Revit.DB')
autodesk.Revit = revit
revit.DB = db
sys.modules['Autodesk'] = autodesk
sys.modules['Autodesk.Revit'] = revit
sys.modules['Autodesk.Revit.DB'] = db
class UnitTypeId(object): Millimeters = 'forge:mm'
class DisplayUnitType(object): DUT_MILLIMETERS = 'legacy:mm'
class GroupTypeId(object): Data = 'forge:group:data'
class BuiltInParameterGroup(object): PG_DATA = 'legacy:group:data'
class UnitUtils(object):
    @staticmethod
    def ConvertFromInternalUnits(value, unit): return (value, unit, 'from')
    @staticmethod
    def ConvertToInternalUnits(value, unit): return (value, unit, 'to')
db.UnitTypeId = UnitTypeId
db.DisplayUnitType = DisplayUnitType
db.GroupTypeId = GroupTypeId
db.BuiltInParameterGroup = BuiltInParameterGroup
db.UnitUtils = UnitUtils
class FakeElementId(object):
    def __init__(self, value): self.value = value
db.ElementId = FakeElementId
class NewId(object):
    Value = 9223372036854770000
class OldId(object):
    IntegerValue = 2147483647
class NewDefinition(object):
    def GetDataType(self): return 'forge:spec'
class OldDefinition(object):
    ParameterType = 'legacy:parameter-type'
class NewParameter(object):
    def GetUnitTypeId(self): return 'forge:unit'
class OldParameter(object):
    DisplayUnitType = 'legacy:unit'
", scope);
            engine.Execute("import hz_compat", scope);
            return scope;
        }

        [Fact]
        public void Provides_python3_aliases_and_handles_modern_and_legacy_element_ids()
        {
            ScriptScope scope = LoadCompatibilityModule();
            scope.Engine.Execute(@"
new_id_value = hz_compat.eid_int(NewId())
old_id_value = hz_compat.eid_int(OldId())
created = hz_compat.element_id(9223372036854770000, FakeElementId).value
none_id = hz_compat.eid_int(None)
aliases = (hz_compat.unicode is str and hz_compat.long is int and
           isinstance('text', hz_compat.basestring) and
           isinstance(3, hz_compat.integer_types))
", scope);

            Assert.Equal(9223372036854770000L, long.Parse(scope.GetVariable("new_id_value").ToString(), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(2147483647, Convert.ToInt32(scope.GetVariable("old_id_value")));
            Assert.Equal(9223372036854770000L, long.Parse(scope.GetVariable("created").ToString(), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(scope.GetVariable("none_id"));
            Assert.True((bool)scope.GetVariable("aliases"));
        }

        [Fact]
        public void Resolves_forge_and_legacy_units_parameter_types_and_groups()
        {
            ScriptScope scope = LoadCompatibilityModule();
            scope.Engine.Execute(@"
modern_unit = hz_compat.unit_type_id('Millimeters', 'DUT_MILLIMETERS')
legacy_unit = hz_compat.unit_type_id('Missing', 'DUT_MILLIMETERS')
modern_spec = hz_compat.parameter_data_type(NewDefinition())
legacy_spec = hz_compat.parameter_data_type(OldDefinition())
modern_parameter_unit = hz_compat.parameter_unit_type(NewParameter())
legacy_parameter_unit = hz_compat.parameter_unit_type(OldParameter())
modern_group = hz_compat.parameter_group('Data')
legacy_named_as_modern = hz_compat.parameter_group('PG_DATA')
db.GroupTypeId = type('NoModernGroups', (), {})
legacy_group = hz_compat.parameter_group('Data')
converted_from = hz_compat.convert_from_internal(2, modern_unit)
converted_to = hz_compat.convert_to_internal(3, legacy_unit)
", scope);

            Assert.Equal("forge:mm", scope.GetVariable("modern_unit").ToString());
            Assert.Equal("legacy:mm", scope.GetVariable("legacy_unit").ToString());
            Assert.Equal("forge:spec", scope.GetVariable("modern_spec").ToString());
            Assert.Equal("legacy:parameter-type", scope.GetVariable("legacy_spec").ToString());
            Assert.Equal("forge:unit", scope.GetVariable("modern_parameter_unit").ToString());
            Assert.Equal("legacy:unit", scope.GetVariable("legacy_parameter_unit").ToString());
            Assert.Equal("forge:group:data", scope.GetVariable("modern_group").ToString());
            Assert.Equal("forge:group:data", scope.GetVariable("legacy_named_as_modern").ToString());
            Assert.Equal("legacy:group:data", scope.GetVariable("legacy_group").ToString());
            Assert.Contains("from", scope.GetVariable("converted_from").ToString());
            Assert.Contains("to", scope.GetVariable("converted_to").ToString());
        }
    }
}
