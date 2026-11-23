using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StreamingDistinctTests
    {
        [Fact]
        public void Large_input_yields_first_occurrences_without_enumerating_the_tail()
        {
            int examined = 0;
            IEnumerable<int> Input()
            {
                for (int i = 0; i < 100000; i++) { examined++; yield return i / 2; }
            }
            Assert.Equal(new[] { 0, 1, 2 }, StreamingDistinct.ByKey(Input(), x => x).Take(3).ToArray());
            Assert.Equal(5, examined);
        }

        [Fact]
        public void Preserves_first_occurrence_and_source_order()
        {
            var items = new[] { (1, "first"), (2, "second"), (1, "duplicate") };
            Assert.Equal(new[] { "first", "second" }, StreamingDistinct.ByKey(items, x => x.Item1).Select(x => x.Item2).ToArray());
        }
    }
}
