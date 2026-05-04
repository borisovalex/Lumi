using StrataSearch;

namespace Lumi.Services;

internal static class SearchPipeline
{
    public static IReadOnlyList<T> Rank<T>(
        IEnumerable<T> items,
        string? query,
        Func<T, IEnumerable<SearchField>> fields,
        Func<T, SearchSortMetadata>? sort = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fields);

        var trimmedQuery = query?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
            return items.ToArray();

        var index = 0;
        var documents = items.Select(item =>
        {
            var metadata = sort?.Invoke(item) ?? new SearchSortMetadata(StableIndex: index);
            if (metadata.StableIndex is null)
                metadata = metadata with { StableIndex = index };

            index++;
            return new SearchDocument<T>(item, fields(item), sort: metadata);
        });

        return SearchEngine
            .Search(documents, trimmedQuery)
            .Select(static result => result.Item)
            .ToArray();
    }
}
