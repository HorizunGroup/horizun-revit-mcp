using Xunit;

namespace Horizun.Server.Tests
{
    public class TextFoldTests
    {
        [Theory]
        [InlineData("Tablero Eléctrico", "tablero electrico")]
        [InlineData("DISEÑO", "diseno")]
        [InlineData("A\u0301rbol", "arbol")]
        public void Search_fold_does_not_depend_on_runtime_globalization(string input, string expected)
        {
            Assert.Equal(expected, TextFold.Fold(input));
        }
    }
}
