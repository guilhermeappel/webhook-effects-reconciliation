namespace WebhookInbox.Tests;

public class LogTextTests
{
    [Theory]
    [InlineData("evt_123", "evt_123")]
    [InlineData("PAYMENT_UPDATED", "PAYMENT_UPDATED")]
    [InlineData("ação é ok", "ação é ok")]   // letters from other languages are not control characters
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Safe_OrdinaryText_IsLoggedAsItIs(string? value, string expected)
    {
        Assert.Equal(expected, LogText.Safe(value));
    }

    [Theory]
    [InlineData("evt\nfail: Forged.Category[0]", "evt?fail: Forged.Category[0]")]
    [InlineData("evt\r\nnext", "evt??next")]
    [InlineData("red\u001b[31m", "red?[31m")]
    [InlineData("a\0b", "a?b")]
    [InlineData("a\u2028b\u2029c", "a?b?c")]
    [InlineData("tab\there", "tab?here")]
    public void Safe_ControlCharacters_AreReplacedSoTheyCannotForgeALogLine(string value, string expected)
    {
        Assert.Equal(expected, LogText.Safe(value));
    }

    [Theory]
    [InlineData("a\u202Eb", "a?b")]   // right-to-left override: reorders what follows when the line is displayed
    [InlineData("a\u200Bb", "a?b")]   // zero-width space: hides that two ids differ
    [InlineData("a\uFEFFb", "a?b")]   // zero-width no-break space, also the byte order mark
    [InlineData("a\u00ADb", "a?b")]   // soft hyphen
    public void Safe_FormatCharacters_AreReplacedBecauseTheyCanReorderOrHideText(string value, string expected)
    {
        Assert.Equal(expected, LogText.Safe(value));
    }

    [Fact]
    public void Safe_AnEmoji_IsKept()
    {
        Assert.Equal("ok 😀", LogText.Safe("ok 😀"));
    }

    [Fact]
    public void Safe_LongText_IsCutAtTheLimitAndSaysSo()
    {
        var safe = LogText.Safe(new string('a', LogText.MaxLength + 50));

        Assert.Equal(new string('a', LogText.MaxLength) + "...", safe);
    }

    [Fact]
    public void Safe_TextExactlyAtTheLimit_IsNotCut()
    {
        var text = new string('a', LogText.MaxLength);

        Assert.Equal(text, LogText.Safe(text));
    }

    [Theory]
    [InlineData(200, 200)]
    [InlineData(201, 203)]   // cut to 200 characters, plus "..."
    public void Safe_TheLimitIsTwoHundredCharacters(int length, int expectedLength)
    {
        // The other tests use the constant, so they would follow it if it changed; this one pins the number.
        Assert.Equal(expectedLength, LogText.Safe(new string('a', length)).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Cut_ALimitThatLeavesNothing_IsRefused(int maxLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LogText.Cut("abcdef", maxLength));
    }

    [Fact]
    public void Cut_ALimitOfOneThatFallsInsideASurrogatePair_KeepsNothingOfIt()
    {
        Assert.Equal("...", LogText.Cut("😀tail", 1));
    }

    [Fact]
    public void Safe_CutThroughASurrogatePair_DoesNotLeaveHalfOfIt()
    {
        // 199 letters and then an emoji (two UTF-16 units): the cut would fall between its two halves.
        var safe = LogText.Safe(new string('a', LogText.MaxLength - 1) + "😀" + "tail");

        Assert.Equal(new string('a', LogText.MaxLength - 1) + "...", safe);
        Assert.DoesNotContain(safe, c => char.IsSurrogate(c));
    }
}
