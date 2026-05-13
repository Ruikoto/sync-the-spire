using SyncTheSpire.Helpers;
using Xunit;

namespace SyncTheSpire.Tests.Helpers;

public class VdfParserTests
{
    [Fact]
    public void Parse_Empty_ReturnsEmptyDictionary()
    {
        var result = VdfParser.Parse("");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_SimpleKeyValue_ParsesEntries()
    {
        var input = """
            "appid"  "646570"
            "name"   "Slay the Spire"
            """;

        var result = VdfParser.Parse(input);

        Assert.Equal("646570", result["appid"]);
        Assert.Equal("Slay the Spire", result["name"]);
    }

    [Fact]
    public void Parse_NestedSection_ProducesNestedDictionary()
    {
        var input = """
            "AppState"
            {
                "appid"  "646570"
                "UserConfig"
                {
                    "language"  "schinese"
                }
            }
            """;

        var result = VdfParser.Parse(input);

        var app = Assert.IsAssignableFrom<Dictionary<string, object>>(result["AppState"]);
        Assert.Equal("646570", app["appid"]);
        var userCfg = Assert.IsAssignableFrom<Dictionary<string, object>>(app["UserConfig"]);
        Assert.Equal("schinese", userCfg["language"]);
    }

    [Fact]
    public void Parse_LineComment_SkipsCommentedTokens()
    {
        var input = """
            // this whole line is a comment "fake" "value"
            "real"  "ok"
            """;

        var result = VdfParser.Parse(input);

        Assert.Single(result);
        Assert.Equal("ok", result["real"]);
    }

    [Fact]
    public void Parse_EscapedQuote_KeepsLiteralQuoteInValue()
    {
        // VDF escapes are rare but the tokenizer handles odd-count backslash before quote
        var input = "\"key\"  \"a\\\"b\"\n";

        var result = VdfParser.Parse(input);

        Assert.True(result.ContainsKey("key"));
        Assert.Contains("\\\"", (string)result["key"]);
    }

    [Fact]
    public void Parse_KeyIsCaseInsensitive_LookupBothCases()
    {
        var input = "\"AppID\"  \"123\"\n";

        var result = VdfParser.Parse(input);

        Assert.Equal("123", result["appid"]);
        Assert.Equal("123", result["APPID"]);
    }
}
