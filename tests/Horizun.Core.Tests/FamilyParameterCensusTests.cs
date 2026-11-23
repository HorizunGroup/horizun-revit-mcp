using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FamilyParameterCensusTests
    {




        [Fact]
        public void Python_schema_points_family_parameter_reads_to_typed_census()
        {
            string schema = Horizun.Contracts.Contract.Find("horizun_execute_python").InputSchema.ToString();
            Assert.Contains("parameter_census", schema);
        }
    }
}
