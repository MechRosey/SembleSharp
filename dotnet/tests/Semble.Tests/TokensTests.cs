using Xunit;

namespace Semble.Tests;

public class SplitIdentifierTests
{
    [Theory]
    [InlineData("HandlerStack", new[] { "handlerstack", "handler", "stack" })]
    [InlineData("my_func", new[] { "my_func", "my", "func" })]
    [InlineData("simple", new[] { "simple" })]
    [InlineData("getHTTPResponse", new[] { "gethttpresponse", "get", "http", "response" })]
    [InlineData("XMLParser", new[] { "xmlparser", "xml", "parser" })]
    [InlineData("ABC", new[] { "abc" })]
    [InlineData("abc123", new[] { "abc123", "abc", "123" })]
    public void Splits_Compound_Identifiers(string input, string[] expected)
    {
        Assert.Equal(expected, Tokens.SplitIdentifier(input));
    }

    [Fact]
    public void Snake_Case_Single_Leading_Underscore_Returns_Lowered_Original()
    {
        // "_foo".split("_") -> ["", "foo"] -> filter empty -> ["foo"] -> single part
        Assert.Equal(new[] { "_foo" }, Tokens.SplitIdentifier("_foo"));
    }

    [Fact]
    public void Snake_Case_Multi_Underscore_Includes_Lowered_Original_Plus_Parts()
    {
        Assert.Equal(
            new[] { "_foo_bar", "foo", "bar" },
            Tokens.SplitIdentifier("_foo_bar"));
    }
}

public class TokenizeTests
{
    [Fact]
    public void Empty_Text_Returns_Empty_List()
    {
        Assert.Empty(Tokens.Tokenize(""));
    }

    [Fact]
    public void Punctuation_Is_Stripped()
    {
        Assert.Equal(
            new[] { "foo", "bar", "baz" },
            Tokens.Tokenize("foo bar.baz()"));
    }

    [Fact]
    public void Compound_Identifiers_Expand_Into_Subtokens()
    {
        Assert.Equal(
            new[] { "dosomething", "do", "something", "x" },
            Tokens.Tokenize("doSomething(x)"));
    }

    [Fact]
    public void Multiple_Identifiers_Concat_Their_Subtokens()
    {
        var result = Tokens.Tokenize("class HandlerStack: my_func()");
        // "class" -> ["class"]
        // "HandlerStack" -> ["handlerstack", "handler", "stack"]
        // "my_func" -> ["my_func", "my", "func"]
        Assert.Equal(
            new[] { "class", "handlerstack", "handler", "stack", "my_func", "my", "func" },
            result);
    }

    [Fact]
    public void Numeric_Only_Sequences_Are_Skipped()
    {
        // _TOKEN_RE requires the token to start with a letter or underscore,
        // so a bare "123" is not extracted.
        Assert.Empty(Tokens.Tokenize("123"));
    }
}
