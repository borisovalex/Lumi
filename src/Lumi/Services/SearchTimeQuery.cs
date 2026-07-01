using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lumi.Services;

/// <summary>A half-open time window [Start, End) used to scope search results by time.</summary>
public readonly record struct SearchTimeRange(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Contains(DateTimeOffset value) => value >= Start && value < End;
}

/// <summary>
/// Result of parsing temporal intent out of a free-text search query.
/// Splits a raw query such as "budget last week" into a residual text query ("budget")
/// and a time window (the previous calendar week), so search can be time-aware.
/// </summary>
public sealed class SearchTimeQuery
{
    private SearchTimeQuery(string original, string residualText, SearchTimeRange? range, bool hasRecencyIntent)
    {
        Original = original;
        ResidualText = residualText;
        Range = range;
        HasRecencyIntent = hasRecencyIntent;
    }

    /// <summary>The original, untrimmed query.</summary>
    public string Original { get; }

    /// <summary>The query with recognised temporal tokens removed.</summary>
    public string ResidualText { get; }

    /// <summary>Explicit time window detected in the query, if any.</summary>
    public SearchTimeRange? Range { get; }

    /// <summary>True when the query expressed a "recent/latest/newest" preference without a hard window.</summary>
    public bool HasRecencyIntent { get; }

    /// <summary>True when a concrete time window was detected.</summary>
    public bool HasTimeFilter => Range.HasValue;

    /// <summary>True when the query carried any temporal signal (window or recency preference).</summary>
    public bool HasAnyTimeSignal => HasTimeFilter || HasRecencyIntent;

    /// <summary>True when no searchable text remains after removing temporal tokens.</summary>
    public bool IsTextEmpty => string.IsNullOrWhiteSpace(ResidualText);

    private static readonly string[] MonthNames =
    [
        "january", "february", "march", "april", "may", "june",
        "july", "august", "september", "october", "november", "december"
    ];

    private static readonly string[] MonthAbbreviations =
    [
        "jan", "feb", "mar", "apr", "may", "jun",
        "jul", "aug", "sep", "oct", "nov", "dec"
    ];

    // Filler/preposition words stripped from the residual when adjacent to a temporal phrase.
    private static readonly HashSet<string> AdjacentFillers = new(StringComparer.Ordinal)
    {
        "from", "in", "on", "during", "of", "the", "at", "for", "within", "over", "this", "past", "last"
    };

    public static SearchTimeQuery Parse(string? query) => Parse(query, DateTimeOffset.Now);

    public static SearchTimeQuery Parse(string? query, DateTimeOffset now)
    {
        var original = query ?? "";
        if (string.IsNullOrWhiteSpace(original))
            return new SearchTimeQuery(original, "", null, false);

        var tokens = Tokenize(original);
        if (tokens.Count == 0)
            return new SearchTimeQuery(original, original.Trim(), null, false);

        var consumed = new bool[tokens.Count];
        SearchTimeRange? range = null;
        var hasRecencyIntent = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed[i])
                continue;

            var match = MatchTemporalPhrase(tokens, i, now);
            if (match is { } phrase)
            {
                range ??= phrase.Range;

                for (var c = phrase.StartIndex; c <= phrase.EndIndex; c++)
                    consumed[c] = true;

                i = phrase.EndIndex;
                continue;
            }

            if (IsRecencyWord(tokens[i].Lower))
            {
                consumed[i] = true;
                hasRecencyIntent = true;
            }
        }

        if (range is null && !hasRecencyIntent)
            return new SearchTimeQuery(original, original.Trim(), null, false);

        StripAdjacentFillers(tokens, consumed);
        var residual = BuildResidual(tokens, consumed);

        return new SearchTimeQuery(original, residual, range, hasRecencyIntent);
    }

    private readonly record struct TemporalMatch(int StartIndex, int EndIndex, SearchTimeRange Range);

    /// <summary>
    /// Builds a temporal match, returning null when the date arithmetic overflows
    /// <see cref="DateTimeOffset"/>'s supported range (e.g. counts like "9999999 years"),
    /// so an absurd query degrades to a plain-text search instead of failing silently.
    /// </summary>
    private static TemporalMatch? SafeMatch(int startIndex, int endIndex, Func<SearchTimeRange> build)
    {
        try
        {
            return new TemporalMatch(startIndex, endIndex, build());
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static TemporalMatch? MatchTemporalPhrase(IReadOnlyList<Token> tokens, int index, DateTimeOffset now)
    {
        var token = tokens[index].Lower;

        // Single-word relative days.
        switch (token)
        {
            case "today":
                return new TemporalMatch(index, index, DayRange(StartOfDay(now)));
            case "yesterday":
                return new TemporalMatch(index, index, DayRange(StartOfDay(now).AddDays(-1)));
            case "tomorrow":
                return new TemporalMatch(index, index, DayRange(StartOfDay(now).AddDays(1)));
        }

        // "this/last/past/next <unit>" (e.g., "last week", "this month", "past year").
        if (token is "this" or "last" or "past" or "next" && index + 1 < tokens.Count)
        {
            var unit = NormalizeUnit(tokens[index + 1].Lower);
            if (unit != TimeUnit.None)
            {
                var range = RelativePeriod(token, unit, now);
                if (range is { } r)
                    return new TemporalMatch(index, index + 1, r);
            }
        }

        // "last/past N <unit>" rolling window (e.g., "last 7 days", "past 2 weeks").
        if (token is "last" or "past" && index + 2 < tokens.Count
            && TryParseCount(tokens[index + 1].Lower, out var rollingCount))
        {
            var unit = NormalizeUnit(tokens[index + 2].Lower);
            if (unit != TimeUnit.None)
            {
                // An absurd count (e.g. "last 9999999 years") would overflow the date math; when
                // that happens we leave the tokens as plain text rather than failing the search.
                var match = SafeMatch(index, index + 2, () => new SearchTimeRange(ShiftBack(now, unit, rollingCount), now));
                if (match is { } rolling)
                    return rolling;
            }
        }

        // "N <unit> ago" single period (e.g., "3 days ago", "2 months ago").
        if (TryParseCount(token, out var agoCount) && index + 2 < tokens.Count)
        {
            var unit = NormalizeUnit(tokens[index + 1].Lower);
            if (unit != TimeUnit.None && tokens[index + 2].Lower == "ago")
            {
                var match = SafeMatch(index, index + 2, () => UnitPeriodBack(now, unit, agoCount));
                if (match is { } ago)
                    return ago;
            }
        }

        // "N <unit>" rolling window without "ago" (e.g., "7 days"), only for plausible counts.
        if (TryParseCount(token, out var bareCount) && bareCount is > 0 and <= 366 && index + 1 < tokens.Count)
        {
            var unit = NormalizeUnit(tokens[index + 1].Lower);
            if (unit != TimeUnit.None)
                return new TemporalMatch(index, index + 1, new SearchTimeRange(ShiftBack(now, unit, bareCount), now));
        }

        // Month name, optionally followed by a year (e.g., "june", "june 2024").
        var monthIndex = MatchMonth(token);
        if (monthIndex >= 0)
        {
            var endIndex = index;
            int year;
            if (index + 1 < tokens.Count && TryParseYear(tokens[index + 1].Lower, out var explicitYear))
            {
                year = explicitYear;
                endIndex = index + 1;
            }
            else
            {
                // Most recent occurrence of this month.
                year = (monthIndex + 1) <= now.Month ? now.Year : now.Year - 1;
            }

            return new TemporalMatch(index, endIndex, MonthRange(year, monthIndex + 1, now.Offset));
        }

        // Standalone calendar year (e.g., "2024").
        if (TryParseYear(token, out var standaloneYear))
            return new TemporalMatch(index, index, YearRange(standaloneYear, now.Offset));

        return null;
    }

    private enum TimeUnit { None, Day, Week, Month, Year }

    private static TimeUnit NormalizeUnit(string word) => word switch
    {
        "day" or "days" => TimeUnit.Day,
        "week" or "weeks" => TimeUnit.Week,
        "month" or "months" => TimeUnit.Month,
        "year" or "years" => TimeUnit.Year,
        _ => TimeUnit.None
    };

    private static SearchTimeRange? RelativePeriod(string qualifier, TimeUnit unit, DateTimeOffset now)
    {
        var direction = qualifier switch { "last" or "past" => -1, "next" => 1, _ => 0 };

        return unit switch
        {
            TimeUnit.Day => DayRange(StartOfDay(now).AddDays(direction)),
            TimeUnit.Week => WeekRange(StartOfWeek(now).AddDays(direction * 7)),
            TimeUnit.Month => MonthRange(AddMonthsToMonthStart(now, direction)),
            TimeUnit.Year => YearRange(now.Year + direction, now.Offset),
            _ => null
        };
    }

    private static SearchTimeRange UnitPeriodBack(DateTimeOffset now, TimeUnit unit, int count) => unit switch
    {
        TimeUnit.Day => DayRange(StartOfDay(now).AddDays(-count)),
        TimeUnit.Week => WeekRange(StartOfWeek(now).AddDays(-7L * count)),
        TimeUnit.Month => MonthRange(StartOfMonth(now).AddMonths(-count)),
        TimeUnit.Year => YearRange(now.Year - count, now.Offset),
        _ => DayRange(StartOfDay(now))
    };

    private static DateTimeOffset ShiftBack(DateTimeOffset now, TimeUnit unit, int count) => unit switch
    {
        TimeUnit.Day => now.AddDays(-count),
        TimeUnit.Week => now.AddDays(-7L * count),
        TimeUnit.Month => now.AddMonths(-count),
        TimeUnit.Year => now.AddYears(-count),
        _ => now
    };

    private static DateTimeOffset StartOfDay(DateTimeOffset now)
        => new(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

    private static DateTimeOffset StartOfWeek(DateTimeOffset now)
    {
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var start = StartOfDay(now);
        var delta = ((int)start.DayOfWeek - (int)firstDay + 7) % 7;
        return start.AddDays(-delta);
    }

    private static DateTimeOffset StartOfMonth(DateTimeOffset now)
        => new(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

    private static DateTimeOffset AddMonthsToMonthStart(DateTimeOffset now, int months)
        => StartOfMonth(now).AddMonths(months);

    private static SearchTimeRange DayRange(DateTimeOffset dayStart) => new(dayStart, dayStart.AddDays(1));

    private static SearchTimeRange WeekRange(DateTimeOffset weekStart) => new(weekStart, weekStart.AddDays(7));

    private static SearchTimeRange MonthRange(DateTimeOffset monthStart) => new(monthStart, monthStart.AddMonths(1));

    private static SearchTimeRange MonthRange(int year, int month, TimeSpan offset)
    {
        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, offset);
        return new SearchTimeRange(start, start.AddMonths(1));
    }

    private static SearchTimeRange YearRange(int year, TimeSpan offset)
    {
        var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, offset);
        return new SearchTimeRange(start, start.AddYears(1));
    }

    private static int MatchMonth(string word)
    {
        if (word.Length < 3)
            return -1;

        for (var i = 0; i < MonthNames.Length; i++)
        {
            if (string.Equals(word, MonthNames[i], StringComparison.Ordinal)
                || string.Equals(word, MonthAbbreviations[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseCount(string word, out int count)
        => int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out count);

    private static bool TryParseYear(string word, out int year)
    {
        year = 0;
        if (word.Length != 4 || !int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return false;

        if (value is < 1990 or > 2100)
            return false;

        year = value;
        return true;
    }

    private static bool IsRecencyWord(string word) => word switch
    {
        "recent" or "recently" or "latest" or "newest" => true,
        _ => false
    };

    private static void StripAdjacentFillers(IReadOnlyList<Token> tokens, bool[] consumed)
    {
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (consumed[i] || !AdjacentFillers.Contains(tokens[i].Lower))
                    continue;

                var nextConsumed = i + 1 < tokens.Count && consumed[i + 1];
                var prevConsumed = i - 1 >= 0 && consumed[i - 1];
                if (nextConsumed || prevConsumed)
                {
                    consumed[i] = true;
                    changed = true;
                }
            }
        } while (changed);
    }

    private static string BuildResidual(IReadOnlyList<Token> tokens, bool[] consumed)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed[i])
                continue;

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(tokens[i].Text);
        }

        return builder.ToString().Trim();
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var isBoundary = i == text.Length || char.IsWhiteSpace(text[i]);
            if (isBoundary)
            {
                if (start >= 0)
                {
                    var slice = text.Substring(start, i - start);
                    tokens.Add(new Token(slice, slice.ToLowerInvariant()));
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        return tokens;
    }

    private readonly record struct Token(string Text, string Lower);
}
