using RensaioBackend.Services.Import;
using Xunit;

namespace RensaioBackend.Tests;

public class ImportLanguageHintsTests
{
    [Theory]
    [InlineData("隣のステラ", new[] { "en", "ja" })]            // kana present -> Japanese only
    [InlineData("気になってる人が男じゃなかった", new[] { "en", "ja" })]
    [InlineData("スタジオカバナ", new[] { "en", "ja" })]
    [InlineData("花野井くんと恋の病", new[] { "en", "ja" })]
    [InlineData("鬼滅", new[] { "en", "ja", "zh" })]             // Han only -> ambiguous
    [InlineData("나 혼자만 레벨업", new[] { "en", "ko" })]
    [InlineData("Medalist", new[] { "en" })]
    [InlineData("Gazing at the Star Next Door (2022)", new[] { "en" })]
    [InlineData("", new[] { "en" })]
    public void AddsLanguagesFromScript(string title, string[] expected)
    {
        Assert.Equal(expected, ImportLanguageHints.WithScriptLanguages(new[] { "en" }, title));
    }

    [Fact]
    public void NullTitleKeepsInput()
    {
        Assert.Equal(new[] { "en" }, ImportLanguageHints.WithScriptLanguages(new[] { "en" }, null));
    }

    [Fact]
    public void NeverRemovesOrDuplicates()
    {
        Assert.Equal(new[] { "JA", "en" }, ImportLanguageHints.WithScriptLanguages(new[] { "JA", "en" }, "隣のステラ"));
    }
}
