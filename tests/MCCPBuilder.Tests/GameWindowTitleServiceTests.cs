using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class GameWindowTitleServiceTests
{
    [Fact]
    public void Validate_AcceptsChineseAndSpaces()
    {
        GameWindowTitleService.Validate("最后防线 2.2 - POTATO LIGHT STUDIO");
    }

    [Theory]
    [InlineData("")]
    [InlineData("标题\r换行")]
    public void Validate_RejectsEmptyAndControlCharacters(string title)
    {
        Assert.Throws<InvalidDataException>(() =>
            GameWindowTitleService.Validate(title));
    }

    [Fact]
    public void Validate_RejectsTitleLongerThanLimit()
    {
        Assert.Throws<InvalidDataException>(() =>
            GameWindowTitleService.Validate(
                new string('A', GameWindowTitleService.MaximumTitleLength + 1)));
    }
}
