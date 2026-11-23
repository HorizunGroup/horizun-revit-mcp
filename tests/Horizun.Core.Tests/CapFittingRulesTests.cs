using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CapFittingRulesTests
    {
        private static ConnectorFact End(int id, string domain = "piping", bool connected = false) =>
            new ConnectorFact { Id = id, Domain = domain, IsConnected = connected };
        [Fact] public void One_open_end_is_selected_without_guessing() => Assert.Equal(2, MepRules.SelectCapEnd(new[] { End(1, connected:true), End(2) }, null).Id);
        [Fact] public void Two_open_ends_need_an_explicit_id() => Assert.Contains("ambiguous", Assert.Throws<ArgumentException>(() => MepRules.SelectCapEnd(new[] { End(1), End(2) }, null)).Message);
        [Fact] public void Named_duct_end_is_selected() => Assert.Equal(2, MepRules.SelectCapEnd(new[] { End(1,"hvac"), End(2,"hvac") }, 2).Id);
        [Fact] public void Connected_named_end_is_refused() => Assert.Throws<ArgumentException>(() => MepRules.SelectCapEnd(new[] { End(1,connected:true), End(2) }, 1));
        [Theory] [InlineData("electrical")] [InlineData("undefined")] [InlineData("conduit")]
        public void Other_domains_cannot_be_capped(string domain) => Assert.Throws<ArgumentException>(() => MepRules.SelectCapEnd(new[] { End(1,domain) }, null));

        // Live on Revit 2025: setting Connector.Radius/Width on the template's pipe cap and rectangular
        // endcap threw ArgumentOutOfRangeException; the connector is associated with an instance parameter.
        [Theory]
        [InlineData(false, false, false, false, "connector")]
        [InlineData(true, true, true, true, "parameter")]
        [InlineData(true, false, false, false, null)]   // associated with a type parameter: choose a sized type
        [InlineData(true, true, false, true, null)]     // formula-driven or read-only
        [InlineData(true, true, true, false, null)]     // not a length
        public void Cap_size_goes_through_the_associated_parameter_or_refuses(bool associated, bool found, bool writable, bool length, string expected)
            => Assert.Equal(expected, MepRules.CapSizeRoute(associated, found, writable, length));
    }
}
