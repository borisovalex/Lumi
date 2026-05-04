using StrataSearch;

namespace Lumi.Services;

internal static class FuzzySearch
{
    public static bool IsMatch(string? query, params string?[] fields)
        => SearchEngine.IsMatch(query, fields);

    public static bool IsMatch(string? query, IEnumerable<string?> fields)
        => SearchEngine.Score(query, fields) > 0;

    public static double Score(string? query, params string?[] fields)
        => SearchEngine.Score(query, fields);

    public static double Score(string? query, IEnumerable<string?> fields)
        => SearchEngine.Score(query, fields);
}
