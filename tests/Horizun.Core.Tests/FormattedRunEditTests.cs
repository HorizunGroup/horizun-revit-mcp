using System;
using Horizun.Revit.Core;
using Xunit;

public class FormattedRunEditTests
{
    private static FormattedRunEdit.Run[] Runs() => new[] { new FormattedRunEdit.Run(0, 3, "bold"), new FormattedRunEdit.Run(3, 3, "plain") };
    [Theory]
    [InlineData("bc", "LONG", "aLONGdef", 5)]
    [InlineData("abc", "x", "xdef", 1)]
    [InlineData("de", "", "abcf", 3)]
    public void EditsWithinAndAtBoundaryShiftRuns(string old, string replacement, string expected, int secondStart)
    {
        var p = FormattedRunEdit.Create("abcdef", Runs(), old, replacement);
        Assert.Equal(expected, p.Text); Assert.Equal(secondStart, p.Runs[1].Start); Assert.Equal("bold", p.Runs[0].Format);
    }
    [Fact] public void DifferentFormatsRejectCrossing()
    {
        var ex = Assert.Throws<ArgumentException>(() => FormattedRunEdit.Create("abcdef", Runs(), "cd", "x"));
        Assert.Contains("cruza tramos con formato distinto", ex.Message);
    }
    [Fact] public void MultipleMatchesUseUpdatedPositions()
    {
        var p = FormattedRunEdit.Create("abab", new[] { new FormattedRunEdit.Run(0, 4, "b") }, "ab", "xyz");
        Assert.Equal("xyzxyz", p.Text); Assert.Equal(new[] { 0, 3 }, new[] { p.Edits[0].Start, p.Edits[1].Start }); Assert.Equal(6, p.Runs[0].Length);
    }
    [Fact] public void ComparisonFindsFormatAndLengthDifferences()
    {
        var expected = new[] { new FormattedRunEdit.Run(0, 3, "b") };
        var split = new[] { new FormattedRunEdit.Run(0, 1, "b"), new FormattedRunEdit.Run(1, 2, "b") };
        Assert.Empty(FormattedRunEdit.Compare(expected, split));
        var actual = new[] { new FormattedRunEdit.Run(0, 1, "b"), new FormattedRunEdit.Run(1, 1, "i") };
        var differences = FormattedRunEdit.Compare(expected, actual);
        Assert.Contains(differences, d => d.Contains("length"));
        Assert.Contains(differences, d => d.Contains("position 1"));
    }
}
