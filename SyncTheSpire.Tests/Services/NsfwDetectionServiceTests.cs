using SyncTheSpire.Services;
using Xunit;

namespace SyncTheSpire.Tests.Services;

public class NsfwDetectionServiceTests
{
    [Fact]
    public void MatchNsfwKeyword_Empty_ReturnsNull()
    {
        Assert.Null(NsfwDetectionService.MatchNsfwKeyword(""));
    }

    [Theory]
    [InlineData("R18", "R18")]
    [InlineData("nsfw", "NSFW")]
    [InlineData("R18G", "R18G")]
    public void MatchNsfwKeyword_PlainKeyword_ReturnsUppercased(string input, string expected)
    {
        Assert.Equal(expected, NsfwDetectionService.MatchNsfwKeyword(input));
    }

    [Fact]
    public void MatchNsfwKeyword_R18Dash_PrefersLongestMatch()
    {
        // keywords ordered ["r18-g", "r18g", "nsfw", "r18"] — longest first
        // "r18-g" should win over "r18"
        var result = NsfwDetectionService.MatchNsfwKeyword("某Mod-r18-g版");
        Assert.Equal("R18-G", result);
    }

    [Fact]
    public void MatchNsfwKeyword_CaseInsensitive_StillMatches()
    {
        Assert.Equal("R18", NsfwDetectionService.MatchNsfwKeyword("Some r18 Mod"));
        Assert.Equal("NSFW", NsfwDetectionService.MatchNsfwKeyword("NSFW package"));
    }

    [Fact]
    public void MatchNsfwKeyword_NoMatch_ReturnsNull()
    {
        Assert.Null(NsfwDetectionService.MatchNsfwKeyword("普通的Mod名称"));
    }

    [Fact]
    public void MatchNsfwKeyword_PartialKeywordEmbedded_StillMatches()
    {
        // substring search, not word boundary — "playerR18thing" still hits R18
        Assert.Equal("R18", NsfwDetectionService.MatchNsfwKeyword("playerR18thing"));
    }
}
