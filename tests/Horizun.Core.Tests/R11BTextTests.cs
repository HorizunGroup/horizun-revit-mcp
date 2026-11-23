using Xunit;
using Horizun.Revit.Core;

public class R11BTextTests
{
    [Fact]
    public void Normalizes_only_requested_whitespace()
    {
        Assert.True(NoteLeaderRules.TextsMatch("A\r\nB ", "A\nB"));
        Assert.True(NoteLeaderRules.TextsMatch("1\u00a0200 m²", "1 200 m²"));
        Assert.True(NoteLeaderRules.TextsMatch("A\u2009B\r", "A B"));
        Assert.False(NoteLeaderRules.TextsMatch("A\nB", "A\nC"));
        Assert.False(NoteLeaderRules.TextsMatch("1 m²", "1 m³"));
        Assert.False(NoteLeaderRules.TextsMatch("A\tB", "A B"));
    }

    [Fact]
    public void Failure_preserves_escaped_text_and_first_normalized_difference()
    {
        var diff = NoteLeaderRules.TextDifference("A\r\n1\u00a0200\tB", "A\n1 200\tC");
        Assert.Equal("A\\r\\n1\\u00a0200\\tB", (string)diff["expected_text"]);
        Assert.Equal("A\\n1 200\\tC", (string)diff["reread_text"]);
        Assert.Equal(8, (int)diff["first_difference"]);
        Assert.Equal(1, (int)NoteLeaderRules.TextDifference("AB", "A")["first_difference"]);
    }
}
