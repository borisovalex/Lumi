using System;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class SearchTimeQueryTests
{
    // Friday, 12 June 2026, 19:32, +03:00
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 19, 32, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("budget report")]
    [InlineData("last")]
    [InlineData("this")]
    [InlineData("1234")]
    [InlineData("error 4040")]
    public void Parse_NoTemporalSignal_LeavesQueryIntact(string query)
    {
        var result = SearchTimeQuery.Parse(query, Now);

        Assert.False(result.HasAnyTimeSignal);
        Assert.False(result.HasTimeFilter);
        Assert.False(result.HasRecencyIntent);
        Assert.Equal(query.Trim(), result.ResidualText);
    }

    [Fact]
    public void Parse_Today_ReturnsTodayWindow()
    {
        var result = SearchTimeQuery.Parse("today", Now);

        Assert.True(result.HasTimeFilter);
        Assert.True(result.IsTextEmpty);
        var range = result.Range!.Value;
        Assert.Equal(new DateTimeOffset(2026, 6, 12, 0, 0, 0, Now.Offset), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 13, 0, 0, 0, Now.Offset), range.End);
        Assert.True(range.Contains(Now));
    }

    [Fact]
    public void Parse_Yesterday_ReturnsPreviousDay()
    {
        var result = SearchTimeQuery.Parse("yesterday", Now);

        var range = result.Range!.Value;
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 0, 0, 0, Now.Offset), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 12, 0, 0, 0, Now.Offset), range.End);
        Assert.False(range.Contains(Now));
    }

    [Theory]
    [InlineData("budget yesterday")]
    [InlineData("yesterday budget")]
    [InlineData("budget from yesterday")]
    public void Parse_StripsTemporalAndKeepsResidual(string query)
    {
        var result = SearchTimeQuery.Parse(query, Now);

        Assert.True(result.HasTimeFilter);
        Assert.Equal("budget", result.ResidualText);
    }

    [Fact]
    public void Parse_ThisWeek_IsSevenDayWindowContainingNow()
    {
        var result = SearchTimeQuery.Parse("this week", Now);

        var range = result.Range!.Value;
        Assert.True(range.Contains(Now));
        Assert.Equal(7, (range.End - range.Start).TotalDays, 3);
        Assert.True(result.IsTextEmpty);
    }

    [Fact]
    public void Parse_LastWeek_IsSevenDayWindowEndingAtThisWeekStart()
    {
        var thisWeek = SearchTimeQuery.Parse("this week", Now).Range!.Value;
        var lastWeek = SearchTimeQuery.Parse("last week", Now).Range!.Value;

        Assert.Equal(thisWeek.Start, lastWeek.End);
        Assert.Equal(7, (lastWeek.End - lastWeek.Start).TotalDays, 3);
        Assert.False(lastWeek.Contains(Now));
    }

    [Fact]
    public void Parse_PastWeek_WithoutNumber_BehavesLikeLastWeek()
    {
        var lastWeek = SearchTimeQuery.Parse("last week", Now).Range!.Value;
        var pastWeek = SearchTimeQuery.Parse("past week", Now).Range!.Value;

        Assert.Equal(lastWeek.Start, pastWeek.Start);
        Assert.Equal(lastWeek.End, pastWeek.End);
    }

    [Fact]
    public void Parse_ThisMonth_CoversJune()
    {
        var result = SearchTimeQuery.Parse("this month", Now);

        var range = result.Range!.Value;
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, Now.Offset), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, Now.Offset), range.End);
    }

    [Fact]
    public void Parse_LastMonth_CoversMay()
    {
        var range = SearchTimeQuery.Parse("last month", Now).Range!.Value;

        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, Now.Offset), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, Now.Offset), range.End);
    }

    [Fact]
    public void Parse_ThisYear_And_LastYear()
    {
        var thisYear = SearchTimeQuery.Parse("this year", Now).Range!.Value;
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, Now.Offset), thisYear.Start);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, Now.Offset), thisYear.End);

        var lastYear = SearchTimeQuery.Parse("last year", Now).Range!.Value;
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, Now.Offset), lastYear.Start);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, Now.Offset), lastYear.End);
    }

    [Theory]
    [InlineData("last 7 days", 7)]
    [InlineData("past 30 days", 30)]
    [InlineData("last 2 weeks", 14)]
    public void Parse_RollingWindow_EndsAtNow(string query, int days)
    {
        var result = SearchTimeQuery.Parse(query, Now);

        var range = result.Range!.Value;
        Assert.Equal(Now, range.End);
        Assert.Equal(days, (range.End - range.Start).TotalDays, 3);
    }

    [Fact]
    public void Parse_DaysAgo_IsSingleDayWindow()
    {
        var result = SearchTimeQuery.Parse("3 days ago", Now);

        var range = result.Range!.Value;
        Assert.Equal(new DateTimeOffset(2026, 6, 9, 0, 0, 0, Now.Offset), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 0, 0, 0, Now.Offset), range.End);
        Assert.True(result.IsTextEmpty);
    }

    [Fact]
    public void Parse_MonthsAgo_IsSingleMonthWindow()
    {
        var range = SearchTimeQuery.Parse("2 months ago", Now).Range!.Value;

        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, Now.Offset), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, Now.Offset), range.End);
    }

    [Fact]
    public void Parse_MonthName_UsesMostRecentOccurrence()
    {
        // June (current month) -> this year.
        var june = SearchTimeQuery.Parse("june", Now).Range!.Value;
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, Now.Offset), june.Start);

        // December (future this year) -> previous year.
        var december = SearchTimeQuery.Parse("december", Now).Range!.Value;
        Assert.Equal(new DateTimeOffset(2025, 12, 1, 0, 0, 0, Now.Offset), december.Start);

        // January (past this year) -> this year.
        var january = SearchTimeQuery.Parse("january", Now).Range!.Value;
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, Now.Offset), january.Start);
    }

    [Theory]
    [InlineData("june 2024", 2024, 6)]
    [InlineData("jan 2025", 2025, 1)]
    [InlineData("dec 2023", 2023, 12)]
    public void Parse_MonthWithYear(string query, int year, int month)
    {
        var range = SearchTimeQuery.Parse(query, Now).Range!.Value;

        Assert.Equal(new DateTimeOffset(year, month, 1, 0, 0, 0, Now.Offset), range.Start);
        Assert.Equal(new DateTimeOffset(year, month, 1, 0, 0, 0, Now.Offset).AddMonths(1), range.End);
    }

    [Fact]
    public void Parse_StandaloneYear_ScopesWholeYear_AndKeepsResidual()
    {
        var result = SearchTimeQuery.Parse("2024 roadmap", Now);

        Assert.Equal("roadmap", result.ResidualText);
        var range = result.Range!.Value;
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, Now.Offset), range.Start);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, Now.Offset), range.End);
    }

    [Theory]
    [InlineData("recent")]
    [InlineData("latest")]
    [InlineData("newest")]
    public void Parse_RecencyWords_SetRecencyIntentWithoutRange(string query)
    {
        var result = SearchTimeQuery.Parse(query, Now);

        Assert.True(result.HasRecencyIntent);
        Assert.False(result.HasTimeFilter);
        Assert.True(result.IsTextEmpty);
    }

    [Fact]
    public void Parse_RecencyWithResidual_KeepsText()
    {
        var result = SearchTimeQuery.Parse("latest invoices", Now);

        Assert.True(result.HasRecencyIntent);
        Assert.Equal("invoices", result.ResidualText);
    }

    [Fact]
    public void Parse_PrepositionStripping_ProducesCleanResidual()
    {
        Assert.Equal("notes", SearchTimeQuery.Parse("notes from last week", Now).ResidualText);
        Assert.Equal("meeting", SearchTimeQuery.Parse("meeting in june", Now).ResidualText);
        Assert.Equal("deploy", SearchTimeQuery.Parse("deploy on yesterday", Now).ResidualText);
    }

    [Fact]
    public void Parse_CombinedRecencyAndRange()
    {
        var result = SearchTimeQuery.Parse("recent budget last month", Now);

        Assert.True(result.HasRecencyIntent);
        Assert.True(result.HasTimeFilter);
        Assert.Equal("budget", result.ResidualText);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, Now.Offset), result.Range!.Value.Start);
    }

    [Fact]
    public void Parse_NaturalLanguagePhrase_ExtractsTopicAndWindow()
    {
        var result = SearchTimeQuery.Parse("what did we discuss about pricing yesterday", Now);

        Assert.True(result.HasTimeFilter);
        Assert.Equal("what did we discuss about pricing", result.ResidualText);
    }

    [Theory]
    [InlineData("last 9999999 years")]
    [InlineData("past 5000000 months")]
    [InlineData("last 99999999 weeks")]
    [InlineData("9999999 years ago")]
    [InlineData("3000000 months ago")]
    [InlineData("123456 weeks ago")]
    // Counts engineered to overflow the int32 "7 * count" week multiply (must be computed in long
    // so the date math still throws and degrades to text rather than wrapping to a tiny window).
    [InlineData("last 613566757 weeks")]
    [InlineData("613566753 weeks ago")]
    public void Parse_AbsurdCounts_DoNotThrow_AndDegradeToText(string query)
    {
        // Counts large enough to overflow DateTimeOffset must not throw; the phrase is left as
        // plain text (no time filter) so the search still runs instead of failing silently.
        var result = SearchTimeQuery.Parse(query, Now);

        Assert.False(result.HasAnyTimeSignal);
        Assert.False(result.HasTimeFilter);
        Assert.Equal(query, result.ResidualText);
    }
}
