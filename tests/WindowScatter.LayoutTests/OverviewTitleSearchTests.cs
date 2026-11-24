using WindowScatter;
using Xunit;

public class OverviewTitleSearchTests
{
    [Theory]
    [InlineData("Résumé - COOPERACIÓN", "me - cooperacion", true)]
    [InlineData("PINGUINO", "ping\u00fcino", true)]
    [InlineData("cafe\u0301", "CAFÉ", true)]
    [InlineData("MAN\u0303ANA", "mañana", true)]
    [InlineData("MAN\u0303ANA", "manana", false)]
    [InlineData("MANANA", "mañana", false)]
    [InlineData("資料 - 日本語", "日本", true)]
    [InlineData("Quarterly report", "calculator", false)]
    public void FindsTitleSubstringsWithoutFoldingEnyeIntoN(string title, string query, bool expected) =>
        Assert.Equal(expected, OverviewTitleSearch.Matches(OverviewTitleSearch.Fold(title), OverviewTitleSearch.Fold(query)));

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDFFF)]
    public void IsolatedSurrogateDoesNotPreventSearchOfTheRemainingTitle(int codeUnit)
    {
        string title = $"{(char)codeUnit} Cafe\u0301, man\u0303ana \U0001F4C4";
        Assert.Equal("\uFFFD Cafe, mañana \U0001F4C4", OverviewTitleSearch.Fold(title));
        Assert.Equal("\uFFFD", OverviewTitleSearch.Fold(((char)codeUnit).ToString()));
    }
}
