using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class FuzzySearchTests
{
    [Fact]
    public void IsMatch_MatchesCompactQueryAcrossSeparators()
    {
        Assert.True(FuzzySearch.IsMatch("filesearch", "File Search Service"));
    }

    [Fact]
    public void IsMatch_MatchesAcronymAcrossSeparators()
    {
        Assert.True(FuzzySearch.IsMatch("law", "Log Analytics Workspace"));
    }

    [Fact]
    public void IsMatch_MatchesMidTermTypoAcrossSeparators()
    {
        Assert.True(FuzzySearch.IsMatch("analyticworkspce", "Log Analytics Workspace"));
    }

    [Fact]
    public void Score_RanksExactShortNameAboveAcronymExpansion()
    {
        var exact = FuzzySearch.Score("law", "Law");
        var acronym = FuzzySearch.Score("law", "Log Analytics Workspace");

        Assert.True(exact > acronym, $"Exact short match ({exact}) should beat acronym expansion ({acronym})");
    }
}
