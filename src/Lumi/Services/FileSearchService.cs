using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using StrataSearch;

namespace Lumi.Services;

/// <summary>
/// Searches for files in a directory, respecting .gitignore rules and hardcoded ignore patterns.
/// Designed to be used from the # file autocomplete in the chat composer.
///
/// Performance strategy:
/// 1. On first search, walks the filesystem once and caches all eligible file paths.
/// 2. Subsequent searches filter the in-memory cache — zero filesystem IO.
/// 3. Incremental narrowing: if the user types "ch" → "cha" → "chat", each keystroke
///    filters the previous result set instead of re-scanning.
/// 4. Cache auto-expires after <see cref="CacheTtl"/> to pick up new files.
/// </summary>
public sealed class FileSearchService
{
    // ── Cache state ─────────────────────────────────────────────────

    private readonly object _sync = new();
    private string? _cachedDir;
    private int _cachedMaxDepth;
    private List<PreparedSearchDocument<CachedFile>>? _fileIndex;
    private long _fileIndexTimestamp; // Stopwatch ticks when index was built

    private string? _cachedGitIgnoreDir;
    private List<GitIgnoreRule>? _cachedGitIgnoreRules;

    // Previous search state for incremental narrowing
    private string? _prevQuery;
    private List<SearchResult<CachedFile>>? _prevResults;

    private static readonly long CacheTtl = Stopwatch.Frequency * 300; // 5 minutes
    private static readonly SearchOptions AllFileSearchResultsOptions = new()
    {
        ParallelThreshold = 2_000,
        MaxResults = int.MaxValue
    };

    /// <summary>
    /// Searches for files matching <paramref name="query"/> in <paramref name="workDir"/>.
    /// Returns scored, ranked (relativePath, fullPath) tuples.
    /// </summary>
    public List<(string RelativePath, string FullPath)> Search(
        string workDir,
        string query,
        int maxResults = 20,
        int maxDepth = 10,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return SearchCore(workDir, query, maxResults, maxDepth, cancellationToken);
    }

    private List<(string RelativePath, string FullPath)> SearchCore(
        string workDir,
        string query,
        int maxResults,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workDir)) return [];

        // Ensure file index is populated and fresh
        cancellationToken.ThrowIfCancellationRequested();
        var index = GetOrBuildIndex(workDir, maxDepth, cancellationToken);
        var compiledQuery = SearchQuery.Create(query);

        var hasQuery = !compiledQuery.IsEmpty;

        if (!hasQuery)
        {
            _prevQuery = null;
            _prevResults = null;
            var count = Math.Min(index.Count, maxResults);
            var list = new List<(string, string)>(count);
            for (var i = 0; i < count; i++)
                list.Add((index[i].Item.RelativePath, index[i].Item.FullPath));
            return list;
        }

        // ── Incremental narrowing ───────────────────────────────────
        // If the new query is a refinement of the previous one (user typed more),
        // filter the previous scored results instead of rescanning the full index.
        if (_prevResults is not null &&
            _prevQuery is not null &&
            query.StartsWith(_prevQuery, StringComparison.OrdinalIgnoreCase) &&
            _prevResults.Count > 0)
        {
            var narrowed = SearchEngine.SearchPrepared(
                _prevResults.Select(static result => result.Document).ToArray(),
                compiledQuery,
                AllFileSearchResultsOptions,
                cancellationToken);

            if (narrowed.Count > 0 || !MayUseNonMonotonicFuzzy(compiledQuery))
            {
                _prevQuery = query;
                _prevResults = narrowed;

                return TakeTopResults(narrowed, maxResults);
            }

            _prevQuery = null;
            _prevResults = null;
        }

        // ── Full index scan ─────────────────────────────────────────
        // No candidate limit — the index is in-memory so scanning all entries is fast.
        // A limit would cause different results depending on which intermediate queries
        // were processed (timing-dependent), breaking determinism.
        var candidates = SearchEngine.SearchPrepared(
            index,
            compiledQuery,
            AllFileSearchResultsOptions,
            cancellationToken);

        _prevQuery = query;
        _prevResults = candidates;

        return TakeTopResults(candidates, maxResults);
    }

    private static bool MayUseNonMonotonicFuzzy(SearchQuery query)
    {
        // Edit-distance and approximate matching are intentionally non-monotonic:
        // adding a character can make a previously missed typo match. If narrowing
        // goes empty in that mode, rescan the full index before showing no results.
        foreach (var term in query.Terms)
        {
            if (term.Compact.Length >= 3 || term.Normalized.Length >= 3)
                return true;
        }

        return false;
    }

    /// <summary>Invalidates the file index cache, forcing a fresh filesystem walk on next search.</summary>
    public void InvalidateCache()
    {
        lock (_sync)
        {
            _fileIndex = null;
            _cachedDir = null;
            _prevQuery = null;
            _prevResults = null;
        }
    }

    private static List<(string, string)> TakeTopResults(List<SearchResult<CachedFile>> scored, int maxResults)
    {
        if (maxResults <= 0 || scored.Count == 0)
            return [];

        scored.Sort(SearchEngine.CompareResults);

        var count = Math.Min(scored.Count, maxResults);
        var results = new List<(string, string)>(count);
        for (var i = 0; i < count; i++)
            results.Add((scored[i].Item.RelativePath, scored[i].Item.FullPath));
        return results;
    }

    // ── File index ──────────────────────────────────────────────────

    private List<PreparedSearchDocument<CachedFile>> GetOrBuildIndex(string workDir, int maxDepth, CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();

        // Return cached index if same directory, same depth, and not expired
        if (_fileIndex is not null &&
            _cachedDir == workDir &&
            _cachedMaxDepth == maxDepth &&
            (now - _fileIndexTimestamp) < CacheTtl)
        {
            return _fileIndex;
        }

        // Build new index
        var gitIgnoreRules = GetGitIgnoreRules(workDir);
        var index = new List<PreparedSearchDocument<CachedFile>>();

        EnumerateNonIgnoredFiles(workDir, workDir, gitIgnoreRules, maxDepth, 0, cancellationToken, (rel, full) =>
        {
            var file = new CachedFile(rel, full);
            index.Add(SearchEngine.Prepare(CreateFileDocument(file), index.Count));
            return true; // collect all files
        });

        _fileIndex = index;
        _cachedDir = workDir;
        _cachedMaxDepth = maxDepth;
        _fileIndexTimestamp = now;
        _prevQuery = null;
        _prevResults = null;

        return index;
    }

    // ── Directory walking (skips ignored subtrees) ──────────────────

    /// <summary>
    /// Manually walks the directory tree, skipping ignored directories at the directory level
    /// so we never enter bin/, obj/, node_modules/, .git/ etc.
    /// </summary>
    private static void EnumerateNonIgnoredFiles(
        string rootDir,
        string currentDir,
        List<GitIgnoreRule> gitIgnoreRules,
        int maxDepth,
        int currentDepth,
        CancellationToken cancellationToken,
        Func<string, string, bool> onFile)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Enumerate files in current directory first
        try
        {
            foreach (var fullPath in Directory.EnumerateFiles(currentDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(rootDir, fullPath);
                if (IsIgnoredFile(relativePath, gitIgnoreRules))
                    continue;
                if (!onFile(relativePath, fullPath))
                    return;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* access denied, etc */ }

        if (currentDepth >= maxDepth) return;

        // Then recurse into non-ignored subdirectories
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(subDir);

                // Fast skip: hardcoded ignored directories
                if (ShouldSkipDirectory(rootDir, currentDepth, dirName))
                    continue;

                // Check gitignore directory rules
                var relativeDir = Path.GetRelativePath(rootDir, subDir);
                if (IsIgnoredDirectory(relativeDir, gitIgnoreRules))
                    continue;

                EnumerateNonIgnoredFiles(
                    rootDir,
                    subDir,
                    gitIgnoreRules,
                    maxDepth,
                    currentDepth + 1,
                    cancellationToken,
                    onFile);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* access denied, etc */ }
    }

    // ── Scoring ─────────────────────────────────────────────────────

    /// <summary>
    /// Scores a file path against query parts. Returns 0 if no match.
    /// Higher scores mean better matches.
    ///
    /// Scoring tiers (base score before bonuses/penalties):
    ///   100 — exact filename match (query == filename)
    ///    95 — exact filename-without-extension match
    ///    80 — filename starts with query
    ///    60 — filename contains query as substring
    ///    30 — path-only match (query appears in directory, not filename)
    ///
    /// Bonuses:
    ///   +20 max — coverage ratio: how much of the filename the query covers
    ///   +10     — multi-term: all query terms appear in the filename itself
    ///   + 5     — source file bonus (.cs, .ts, .py, etc.) over non-source files
    ///
    /// Penalties:
    ///   -1 per  — directory depth (shallower files win ties)
    ///
    /// Fuzzy matching (when substring match fails):
    ///    45 — fuzzy match in filename (query chars appear in order)
    ///    15 — fuzzy match in path only
    ///   +15 max — consecutive-char bonus (rewards tighter matches)
    /// </summary>
    internal static int ScoreMatch(string relativePath, string[] queryParts)
    {
        var query = SearchQuery.Create(string.Join(" ", queryParts));
        var result = SearchEngine.SearchPrepared(
            [SearchEngine.Prepare(CreateFileDocument(new CachedFile(relativePath, relativePath)))],
            query,
            new SearchOptions { MaxResults = 1 });

        return result.Count == 0 ? 0 : (int)Math.Max(Math.Round(result[0].Score), 1);
    }

    private static SearchDocument<CachedFile> CreateFileDocument(CachedFile file)
    {
        var relativePath = file.RelativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(fileName);
        var fileNameNoExtension = string.IsNullOrEmpty(extension)
            ? fileName
            : fileName[..^extension.Length];
        var directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";
        var depth = relativePath.Count(static character => character == '/');
        var sourceBoost = IsSourceFileExtension(extension) ? 180 : 0;

        var fields = new List<SearchField>
        {
            SearchField.Primary(fileNameNoExtension, 4.8),
            SearchField.Primary(fileName, 4.4),
            new(relativePath, 1.35),
            new(directory, 0.8)
        };

        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            fields.Add(new SearchField(segment, 4.1));

        return new SearchDocument<CachedFile>(
            file,
            fields,
            baseScore: sourceBoost - (depth * 28),
            sort: new SearchSortMetadata(
                Length: relativePath.Length,
                Text: relativePath));
    }

    internal static string NormalizePathText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (character == '\\' || character == '/')
            {
                if (builder.Length > 0 && builder[^1] != '/')
                    builder.Append('/');
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString().Trim('/');
    }

    internal static string ToCompact(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSourceFileExtension(string ext)
    {
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".java", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".go", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".rs", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".swift", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".rb", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".axaml", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase);
    }

    // ── Ignore logic ────────────────────────────────────────────────

    /// <summary>Checks whether a relative file path should be excluded from search results.</summary>
    internal static bool IsIgnoredPath(string relativePath, List<GitIgnoreRule> gitIgnoreRules)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (IsHardcodedIgnoredPath(normalized))
            return true;
        return IsGitIgnored(normalized, gitIgnoreRules);
    }

    /// <summary>Checks if a file is ignored (called with relativePath already computed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIgnoredFile(string relativePath, List<GitIgnoreRule> gitIgnoreRules)
    {
        // The file-level check only needs gitignore file rules (hardcoded dirs already skipped in walk)
        if (gitIgnoreRules.Count == 0) return false;
        var normalized = relativePath.Replace('\\', '/');
        return IsGitIgnored(normalized, gitIgnoreRules);
    }

    /// <summary>Checks if a directory should be skipped entirely during walk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIgnoredDirectory(string relativeDirPath, List<GitIgnoreRule> gitIgnoreRules)
    {
        if (gitIgnoreRules.Count == 0) return false;
        var normalized = relativeDirPath.Replace('\\', '/');

        // Check directory-applicable gitignore rules
        var ignored = false;
        foreach (var rule in gitIgnoreRules)
        {
            if (GitIgnoreMatchesDir(normalized, rule))
                ignored = !rule.IsNegation;
        }
        return ignored;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGitIgnored(string normalizedPath, List<GitIgnoreRule> gitIgnoreRules)
    {
        var ignored = false;
        foreach (var rule in gitIgnoreRules)
        {
            if (GitIgnoreMatches(normalizedPath, rule))
                ignored = !rule.IsNegation;
        }
        return ignored;
    }

    /// <summary>Fast check for directory names that are always ignored.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldSkipDirectory(string rootDir, int currentDepth, string dirName)
    {
        return IsHardcodedIgnoredDirName(dirName)
               || (currentDepth == 0
                   && IsUserProfileRoot(rootDir)
                   && IsUserProfileInfrastructureDirName(dirName));
    }

    private static bool IsUserProfileRoot(string rootDir)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.Equals(
            Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(userProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUserProfileInfrastructureDirName(string dirName)
    {
        return dirName.Equals(".copilot", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".dotnet", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".vscode", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".vscode-insiders", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".azure", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".codex", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".AzureToolsForIntelliJ", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("AppData", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fast check for directory names that are always ignored in every search root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHardcodedIgnoredDirName(string dirName)
    {
        return dirName.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".next", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals(".nuget", StringComparison.OrdinalIgnoreCase) ||
               dirName.Equals("packages", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHardcodedIgnoredPath(string normalizedPath)
    {
        var span = normalizedPath.AsSpan();
        while (span.Length > 0)
        {
            var sepIndex = span.IndexOf('/');
            var segment = sepIndex >= 0 ? span.Slice(0, sepIndex) : span;

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".next", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".nuget", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("packages", StringComparison.OrdinalIgnoreCase))
                return true;

            if (sepIndex < 0) break;
            span = span.Slice(sepIndex + 1);
        }
        return false;
    }

    // ── .gitignore parsing ──────────────────────────────────────────

    internal List<GitIgnoreRule> GetGitIgnoreRules(string workDir)
    {
        if (_cachedGitIgnoreDir == workDir && _cachedGitIgnoreRules is not null)
            return _cachedGitIgnoreRules;

        _cachedGitIgnoreDir = workDir;
        _cachedGitIgnoreRules = ParseGitIgnoreFile(Path.Combine(workDir, ".gitignore"));
        return _cachedGitIgnoreRules;
    }

    /// <summary>Parses a .gitignore file into a list of rules.</summary>
    internal static List<GitIgnoreRule> ParseGitIgnoreFile(string gitIgnorePath)
    {
        if (!File.Exists(gitIgnorePath))
            return [];

        try
        {
            return ParseGitIgnoreLines(File.ReadLines(gitIgnorePath));
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Parses .gitignore lines into rules. Separated for testability.</summary>
    internal static List<GitIgnoreRule> ParseGitIgnoreLines(IEnumerable<string> lines)
    {
        var rules = new List<GitIgnoreRule>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var isNegation = false;
            if (line[0] == '!')
            {
                isNegation = true;
                line = line[1..];
            }

            var isDirectoryOnly = line.EndsWith('/');
            if (isDirectoryOnly)
                line = line.TrimEnd('/');

            // Remove leading slash (anchored to root)
            line = line.TrimStart('/');

            if (line.Length > 0)
                rules.Add(new GitIgnoreRule(line, isNegation, isDirectoryOnly));
        }
        return rules;
    }

    // ── Gitignore matching ──────────────────────────────────────────

    internal static bool GitIgnoreMatches(string normalizedPath, GitIgnoreRule rule)
    {
        var pattern = rule.Pattern;

        // If pattern contains '/', match against the full path
        if (pattern.Contains('/'))
            return GlobMatch(normalizedPath, pattern);

        // Match pattern against each path segment
        var pathSpan = normalizedPath.AsSpan();
        while (pathSpan.Length > 0)
        {
            var sep = pathSpan.IndexOf('/');
            var segment = sep >= 0 ? pathSpan.Slice(0, sep) : pathSpan;

            // Directory-only rules only match segments followed by '/'
            if (!rule.IsDirectoryOnly || sep >= 0)
            {
                if (GlobMatch(segment, pattern))
                    return true;
            }

            if (sep < 0) break;
            pathSpan = pathSpan.Slice(sep + 1);
        }

        // For non-directory-only patterns, also try against the full path (e.g. "*.log")
        if (!rule.IsDirectoryOnly)
            return GlobMatch(normalizedPath, pattern);

        return false;
    }

    /// <summary>Matches a directory path against a gitignore rule.</summary>
    private static bool GitIgnoreMatchesDir(string normalizedDirPath, GitIgnoreRule rule)
    {
        var pattern = rule.Pattern;

        // If pattern contains '/', match against the full dir path
        if (pattern.Contains('/'))
            return GlobMatch(normalizedDirPath, pattern);

        // Match pattern against each segment of the dir path
        var pathSpan = normalizedDirPath.AsSpan();
        while (pathSpan.Length > 0)
        {
            var sep = pathSpan.IndexOf('/');
            var segment = sep >= 0 ? pathSpan.Slice(0, sep) : pathSpan;

            if (GlobMatch(segment, pattern))
                return true;

            if (sep < 0) break;
            pathSpan = pathSpan.Slice(sep + 1);
        }

        return false;
    }

    // ── Glob matching ───────────────────────────────────────────────

    /// <summary>
    /// Glob pattern matching supporting *, ?, and [...] character classes.
    /// Case-insensitive. Used for .gitignore pattern evaluation.
    /// </summary>
    internal static bool GlobMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int ti = 0, pi = 0;
        int starTi = -1, starPi = -1;

        while (ti < text.Length)
        {
            if (pi < pattern.Length && pattern[pi] == '*')
            {
                while (pi < pattern.Length && pattern[pi] == '*') pi++;
                starPi = pi;
                starTi = ti;
                continue;
            }

            if (pi < pattern.Length && pattern[pi] == '?')
            {
                ti++;
                pi++;
                continue;
            }

            if (pi < pattern.Length && pattern[pi] == '[')
            {
                if (MatchCharClass(text[ti], pattern, ref pi))
                {
                    ti++;
                    continue;
                }
                if (starPi >= 0)
                {
                    pi = starPi;
                    ti = ++starTi;
                    continue;
                }
                return false;
            }

            if (pi < pattern.Length && char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(text[ti]))
            {
                ti++;
                pi++;
                continue;
            }

            if (starPi >= 0)
            {
                pi = starPi;
                ti = ++starTi;
                continue;
            }

            return false;
        }

        while (pi < pattern.Length && pattern[pi] == '*')
            pi++;

        return pi == pattern.Length;
    }

    /// <summary>
    /// Matches a character against a [...] character class in the pattern.
    /// Advances <paramref name="pi"/> past the closing ']'.
    /// Supports ranges like [a-z], negation [!...] / [^...], and literal characters.
    /// </summary>
    private static bool MatchCharClass(char ch, ReadOnlySpan<char> pattern, ref int pi)
    {
        // pi points to '['
        pi++; // skip '['
        if (pi >= pattern.Length) return false;

        var negate = false;
        if (pattern[pi] == '!' || pattern[pi] == '^')
        {
            negate = true;
            pi++;
        }

        var matched = false;
        var first = true;

        while (pi < pattern.Length && (first || pattern[pi] != ']'))
        {
            first = false;
            var lo = pattern[pi];
            pi++;

            // Check for range: [a-z]
            if (pi + 1 < pattern.Length && pattern[pi] == '-' && pattern[pi + 1] != ']')
            {
                var hi = pattern[pi + 1];
                pi += 2;
                if (char.ToLowerInvariant(ch) >= char.ToLowerInvariant(lo) &&
                    char.ToLowerInvariant(ch) <= char.ToLowerInvariant(hi))
                    matched = true;
            }
            else
            {
                if (char.ToLowerInvariant(ch) == char.ToLowerInvariant(lo))
                    matched = true;
            }
        }

        // Skip past ']'
        if (pi < pattern.Length && pattern[pi] == ']')
            pi++;

        return negate ? !matched : matched;
    }
}

/// <summary>A single parsed .gitignore rule.</summary>
public readonly record struct GitIgnoreRule(string Pattern, bool IsNegation, bool IsDirectoryOnly);

/// <summary>A cached file entry in the index.</summary>
internal readonly record struct CachedFile(string RelativePath, string FullPath);
