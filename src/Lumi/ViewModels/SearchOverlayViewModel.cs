using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Services;

namespace Lumi.ViewModels;

public class SearchResultItem
{
    public string Category { get; init; } = "";
    public string CategoryIcon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public int NavIndex { get; init; }
    public object? Item { get; init; }
    public double Score { get; init; }
    public bool IsContentMatch { get; init; }
    public int SettingsPageIndex { get; init; } = -1;

    private Geometry? _categoryGeometry;
    public Geometry? CategoryGeometry => _categoryGeometry ??=
        !string.IsNullOrEmpty(CategoryIcon) ? StreamGeometry.Parse(CategoryIcon) : null;
}

public class SearchResultGroup
{
    public string Category { get; init; } = "";
    public string CategoryIcon { get; init; } = "";
    public bool IsCurrentTab { get; init; }
    public List<SearchResultItem> Items { get; init; } = [];

    private Geometry? _categoryGeometry;
    public Geometry? CategoryGeometry => _categoryGeometry ??=
        !string.IsNullOrEmpty(CategoryIcon) ? StreamGeometry.Parse(CategoryIcon) : null;
}

public partial class SearchOverlayViewModel : ObservableObject
{
    private readonly GlobalSearchService _searchService;
    private readonly Func<int> _getCurrentNavIndex;
    private CancellationTokenSource? _searchCts;
    private long _searchRequestId;
    private long _lastAppliedRequestId = -1;
    private int _lastAppliedPhaseRank = -1;
    private int _activeFinalPhaseRank = -1;

    private const int FastSearchDelayMs = 32;
    private const int InteractiveSearchDelayMs = 120;

    private const string IconChat = "M4 4h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H7l-4 3V6a2 2 0 0 1 2-2z";
    private const string IconClock = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm1 5h-2v6l5 3 .9-1.6-3.9-2.3V7z";
    private const string IconFolder = "M2 6a2 2 0 0 1 2-2h5l2 2h9a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6z";
    private const string IconBolt = "M21.64 3.64l-1.28-1.28a1.21 1.21 0 0 0-1.72 0L2.36 18.64a1.21 1.21 0 0 0 0 1.72l1.28 1.28a1.2 1.2 0 0 0 1.72 0L21.64 5.36a1.2 1.2 0 0 0 0-1.72z M14 7l3 3 M5 6v4 M19 14v4 M10 2v2 M7 8H3 M21 16h-4 M11 3H9";
    private const string IconSparkle = "M11.017 2.814a1 1 0 0 1 1.966 0l1.051 5.558a2 2 0 0 0 1.594 1.594l5.558 1.051a1 1 0 0 1 0 1.966l-5.558 1.051a2 2 0 0 0-1.594 1.594l-1.051 5.558a1 1 0 0 1-1.966 0l-1.051-5.558a2 2 0 0 0-1.594-1.594l-5.558-1.051a1 1 0 0 1 0-1.966l5.558-1.051a2 2 0 0 0 1.594-1.594z M20 2v4 M22 4h-4 M4 20a2 2 0 1 0 0-4 2 2 0 0 0 0 4z";
    private const string IconMemory = "M9.5 2A1.5 1.5 0 0 0 8 3.5V4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2h-4v-.5A1.5 1.5 0 0 0 14.5 2h-5zM10 4V3.5a.5.5 0 0 1 .5-.5h3a.5.5 0 0 1 .5.5V4h-4zm-2 5a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm5 0a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm-5 4a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm5 0a1 1 0 1 1 2 0 1 1 0 0 1-2 0z";
    private const string IconPlug = "M17 6.1h3V8h-3v2.1a5 5 0 0 1-4 4.9V18h2v2H9v-2h2v-3a5 5 0 0 1-4-4.9V8H4V6.1h3V4h2v2.1h4V4h2v2.1zM9 8v2.1a3 3 0 0 0 6 0V8H9z";
    private const string IconGear = "M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915z M12 12a3 3 0 1 0 0-6 3 3 0 0 0 0 6z";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private bool _isSearching;

    public ObservableCollection<SearchResultGroup> ResultGroups { get; } = [];

    /// <summary>Flat list of all results for keyboard navigation.</summary>
    public List<SearchResultItem> FlatResults { get; private set; } = [];

    /// <summary>Raised when a result is selected (navigate to it).</summary>
    public event Action<SearchResultItem>? ResultSelected;

    /// <summary>Raises the ResultSelected event for the given item.</summary>
    public void RaiseResultSelected(SearchResultItem item) => ResultSelected?.Invoke(item);

    public SearchOverlayViewModel(GlobalSearchService searchService, Func<int> getCurrentNavIndex)
    {
        _searchService = searchService;
        _getCurrentNavIndex = getCurrentNavIndex;
    }

    public void Open()
    {
        SearchQuery = "";
        SelectedIndex = 0;
        IsOpen = true;
        QueueSearch(immediate: true);
    }

    [RelayCommand]
    public void Close()
    {
        CancelPendingSearch();
        IsOpen = false;
        SearchQuery = "";
        ResultGroups.Clear();
        FlatResults.Clear();
        SelectedIndex = -1;
        StopSearchActivity();
    }

    public void SelectCurrent()
    {
        if (SelectedIndex >= 0 && SelectedIndex < FlatResults.Count)
        {
            var result = FlatResults[SelectedIndex];
            Close();
            RaiseResultSelected(result);
        }
    }

    public void MoveSelection(int delta)
    {
        if (FlatResults.Count == 0) return;

        var newIndex = SelectedIndex + delta;
        if (newIndex < 0) newIndex = FlatResults.Count - 1;
        else if (newIndex >= FlatResults.Count) newIndex = 0;

        SelectedIndex = newIndex;
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (!IsOpen)
            return;

        QueueSearch();
    }

    private void QueueSearch(bool immediate = false)
    {
        CancelPendingSearch(disposeOnly: true);

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var requestId = Interlocked.Increment(ref _searchRequestId);
        var query = SearchQuery?.Trim() ?? "";
        _lastAppliedRequestId = -1;
        _lastAppliedPhaseRank = -1;

        if (string.IsNullOrEmpty(query))
        {
            _activeFinalPhaseRank = 1;
            StartSearchActivity();
            _ = RunSearchAsync(
                query,
                requestId,
                token,
                GlobalSearchExecutionMode.Full,
                delayMs: 0,
                phaseRank: 1,
                DispatcherPriority.Input);
            return;
        }

        _activeFinalPhaseRank = 2;
        StartSearchActivity();
        _ = RunSearchAsync(
            query,
            requestId,
            token,
            GlobalSearchExecutionMode.Preview,
            delayMs: 0,
            phaseRank: 0,
            DispatcherPriority.Input);

        _ = RunSearchAsync(
            query,
            requestId,
            token,
            GlobalSearchExecutionMode.Fast,
            delayMs: immediate ? 0 : FastSearchDelayMs,
            phaseRank: 1,
            DispatcherPriority.Input);

        _ = RunSearchAsync(
            query,
            requestId,
            token,
            GlobalSearchExecutionMode.Interactive,
            delayMs: InteractiveSearchDelayMs,
            phaseRank: 2,
            DispatcherPriority.Background);
    }

    private async Task RunSearchAsync(
        string query,
        long requestId,
        CancellationToken cancellationToken,
        GlobalSearchExecutionMode executionMode,
        int delayMs,
        int phaseRank,
        DispatcherPriority applyPriority)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);

            // SearchAsync captures a snapshot before it offloads scoring, so keep
            // the call on the UI thread and only move off-thread for the heavy work.
            var matches = await _searchService
                .SearchAsync(query, executionMode, cancellationToken)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(
                () => ApplyResults(query, requestId, phaseRank, matches),
                applyPriority);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer query superseded this one.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Search phase '{executionMode}' failed for query '{query}': {ex}");
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsOpen
                    || requestId != _searchRequestId
                    || !string.Equals(query, SearchQuery?.Trim() ?? "", StringComparison.Ordinal))
                {
                    return;
                }

                UpdateSearchStatusAfterApplied(phaseRank);
            });
        }
    }

    private void ApplyResults(string query, long requestId, int phaseRank, IReadOnlyList<GlobalSearchMatch> matches)
    {
        if (!IsOpen
            || requestId != _searchRequestId
            || !string.Equals(query, SearchQuery?.Trim() ?? "", StringComparison.Ordinal))
        {
            return;
        }

        if (_lastAppliedRequestId == requestId && phaseRank < _lastAppliedPhaseRank)
            return;

        var currentNavIndex = _getCurrentNavIndex();
        var groups = matches
            .GroupBy(static match => match.Category)
            .Select(group =>
            {
                var items = group
                    .Take(20)
                    .Select(ToResultItem)
                    .ToList();

                return new
                {
                    Group = new SearchResultGroup
                    {
                        Category = GetCategoryLabel(group.Key),
                        CategoryIcon = GetCategoryIcon(group.Key),
                        IsCurrentTab = items.Count > 0 && items[0].NavIndex == currentNavIndex,
                        Items = items
                    },
                    TopScore = items.Count > 0 ? items[0].Score : 0
                };
            })
            .OrderByDescending(entry => entry.TopScore + (entry.Group.IsCurrentTab ? 12 : 0))
            .ThenBy(entry => entry.Group.Category)
            .Select(entry => entry.Group)
            .ToList();

        var previousSelectedItem = SelectedIndex >= 0 && SelectedIndex < FlatResults.Count
            ? FlatResults[SelectedIndex]
            : null;
        var flatResults = groups.SelectMany(static group => group.Items).ToList();
        var selectedIndex = ResolveSelectedIndex(previousSelectedItem, flatResults);

        if (HasEquivalentResults(groups, flatResults))
        {
            SelectedIndex = selectedIndex;
            _lastAppliedRequestId = requestId;
            _lastAppliedPhaseRank = phaseRank;
            UpdateSearchStatusAfterApplied(phaseRank);
            return;
        }

        FlatResults = flatResults;
        SelectedIndex = selectedIndex;

        ResultGroups.Clear();
        foreach (var group in groups)
            ResultGroups.Add(group);

        _lastAppliedRequestId = requestId;
        _lastAppliedPhaseRank = phaseRank;
        UpdateSearchStatusAfterApplied(phaseRank);
    }

    private void CancelPendingSearch(bool disposeOnly = false)
    {
        if (_searchCts is null)
            return;

        _searchCts.Cancel();
        _searchCts.Dispose();
        _searchCts = null;

        _lastAppliedRequestId = -1;
        _lastAppliedPhaseRank = -1;
        _activeFinalPhaseRank = -1;

        if (!disposeOnly)
            Interlocked.Increment(ref _searchRequestId);
    }

    private void StartSearchActivity()
    {
        IsSearching = true;
    }

    private void StopSearchActivity()
    {
        IsSearching = false;
    }

    private void UpdateSearchStatusAfterApplied(int phaseRank)
    {
        if (phaseRank >= _activeFinalPhaseRank)
        {
            StopSearchActivity();
            return;
        }

        StartSearchActivity();
    }

    private int ResolveSelectedIndex(SearchResultItem? previousSelectedItem, IReadOnlyList<SearchResultItem> flatResults)
    {
        if (flatResults.Count == 0)
            return -1;

        if (previousSelectedItem is not null)
        {
            for (var index = 0; index < flatResults.Count; index++)
            {
                if (AreEquivalent(previousSelectedItem, flatResults[index]))
                    return index;
            }
        }

        return SelectedIndex >= 0
            ? Math.Min(SelectedIndex, flatResults.Count - 1)
            : 0;
    }

    private bool HasEquivalentResults(
        IReadOnlyList<SearchResultGroup> groups,
        IReadOnlyList<SearchResultItem> flatResults)
    {
        if (FlatResults.Count != flatResults.Count || ResultGroups.Count != groups.Count)
            return false;

        for (var index = 0; index < flatResults.Count; index++)
        {
            var current = FlatResults[index];
            var next = flatResults[index];
            if (!AreEquivalent(current, next))
                return false;
        }

        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var currentGroup = ResultGroups[groupIndex];
            var nextGroup = groups[groupIndex];

            if (!string.Equals(currentGroup.Category, nextGroup.Category, StringComparison.Ordinal)
                || !string.Equals(currentGroup.CategoryIcon, nextGroup.CategoryIcon, StringComparison.Ordinal)
                || currentGroup.IsCurrentTab != nextGroup.IsCurrentTab
                || currentGroup.Items.Count != nextGroup.Items.Count)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreEquivalent(SearchResultItem current, SearchResultItem next)
    {
        return string.Equals(current.Category, next.Category, StringComparison.Ordinal)
               && string.Equals(current.CategoryIcon, next.CategoryIcon, StringComparison.Ordinal)
               && string.Equals(current.Title, next.Title, StringComparison.Ordinal)
               && string.Equals(current.Subtitle, next.Subtitle, StringComparison.Ordinal)
               && current.NavIndex == next.NavIndex
               && Equals(current.Item, next.Item)
               && current.IsContentMatch == next.IsContentMatch
               && current.SettingsPageIndex == next.SettingsPageIndex;
    }

    private static SearchResultItem ToResultItem(GlobalSearchMatch match)
    {
        return new SearchResultItem
        {
            Category = GetCategoryLabel(match.Category),
            CategoryIcon = GetCategoryIcon(match.Category),
            Title = match.Title,
            Subtitle = match.Subtitle,
            NavIndex = match.NavIndex,
            Item = match.Item,
            Score = match.Score,
            IsContentMatch = match.IsContentMatch,
            SettingsPageIndex = match.SettingsPageIndex
        };
    }

    private static string GetCategoryLabel(GlobalSearchCategory category) => category switch
    {
        GlobalSearchCategory.Chats => "Chats",
        GlobalSearchCategory.BackgroundJobs => "Jobs",
        GlobalSearchCategory.Projects => "Projects",
        GlobalSearchCategory.Skills => "Skills",
        GlobalSearchCategory.Lumis => "Lumis",
        GlobalSearchCategory.Memories => "Memories",
        GlobalSearchCategory.McpServers => "MCP Servers",
        GlobalSearchCategory.Settings => "Settings",
        _ => "Results"
    };

    private static string GetCategoryIcon(GlobalSearchCategory category) => category switch
    {
        GlobalSearchCategory.Chats => IconChat,
        GlobalSearchCategory.BackgroundJobs => IconClock,
        GlobalSearchCategory.Projects => IconFolder,
        GlobalSearchCategory.Skills => IconBolt,
        GlobalSearchCategory.Lumis => IconSparkle,
        GlobalSearchCategory.Memories => IconMemory,
        GlobalSearchCategory.McpServers => IconPlug,
        GlobalSearchCategory.Settings => IconGear,
        _ => IconChat
    };
}
