using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ColumnConstraintOrderTests
    {
        [Fact]
        public void A_base_raised_to_the_default_top_sets_the_top_first()
        {
            // MEASURED, Revit 2024 (acero L8, Q1): a column on Level 1 asked for base +3000 and a
            // 3000 mm height. Its default top was +3000; setting the base first made it 0 mm tall,
            // Revit posted "Column height is not 0.0" and kept it to the commit, which rolled back.
            Assert.True(ColumnConstraintOrder.TopFirst(0, 3000, 3000, 6000));
        }

        [Fact]
        public void A_base_lowered_below_the_current_base_sets_the_base_first()
        {
            // Wanted base -4000, top -1000: a top set first would sit below the current base (0).
            Assert.False(ColumnConstraintOrder.TopFirst(0, 3000, -4000, -1000));
        }

        [Fact]
        public void An_ordinary_column_keeps_the_top_first()
        {
            Assert.True(ColumnConstraintOrder.TopFirst(0, 3000, 0, 3000));
        }
    }
}
