using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Views.Controls;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class MarkdownTableLayoutHeadlessTests
{
    private const double BroadMeasureWidth = 900;

    [Theory]
    [InlineData("| First | Second | Third |\n| --- | --- | --- |", 345, 360)]
    [InlineData("| First | Second |\n| --- | --- |", 236, 240)]
    public async Task BroadlyMeasuredNarrowHeaderOnlyTable_LayoutConvergesWithHorizontalOverflow(
        string markdownText,
        double narrowArrangeWidth,
        double expectedGridWidth)
    {
        using var session = HeadlessTestSession.Start();

        const int runawayLayoutPassLimit = 100;
        var layoutPassCount = 0;
        var runawayLayoutDetected = false;
        var dispatcherResponded = false;
        var extentWidth = 0d;
        var viewportWidth = 0d;
        var gridWidth = 0d;
        var gridDesiredWidth = 0d;
        var tableWidth = 0d;
        var tableDesiredWidth = 0d;
        var textContentWidth = 0d;
        var textContentDesiredWidth = 0d;
        var layoutIsValid = false;
        var horizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        await session.Dispatch(() =>
        {
            var textContent = new TranscriptTextContent
            {
                Text = markdownText,
                PreferPlainText = false,
            };
            var markdown = Assert.IsType<StrataMarkdown>(textContent.Content);
            markdown.StreamingRebuildThrottleMs = 0;
            var window = new Window
            {
                Width = narrowArrangeWidth,
                Height = 200,
                Content = new BroadMeasureNarrowArrangeHost
                {
                    Child = textContent,
                    ArrangeWidth = narrowArrangeWidth,
                },
            };
            var layoutManager = GetLayoutManager(window);

            layoutManager.LayoutUpdated += OnLayoutUpdated;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var scrollViewer = markdown
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .Single(control => control.Classes.Contains("strata-md-table-scroll"));
            var grid = Assert.IsType<Grid>(scrollViewer.Content);
            var presenter = scrollViewer
                .GetVisualDescendants()
                .OfType<ScrollContentPresenter>()
                .Single();
            var table = markdown
                .GetVisualDescendants()
                .OfType<Border>()
                .Single(control => control.Classes.Contains("strata-md-table"));

            // Exercise the post-mode-change remeasure that remained invalid in the dump.
            grid.InvalidateMeasure();
            Dispatcher.UIThread.RunJobs();

            extentWidth = scrollViewer.Extent.Width;
            viewportWidth = scrollViewer.Viewport.Width;
            gridWidth = grid.Bounds.Width;
            gridDesiredWidth = grid.DesiredSize.Width;
            tableWidth = table.Bounds.Width;
            tableDesiredWidth = table.DesiredSize.Width;
            textContentWidth = textContent.Bounds.Width;
            textContentDesiredWidth = textContent.DesiredSize.Width;
            layoutIsValid =
                grid.IsMeasureValid &&
                grid.IsArrangeValid &&
                presenter.IsMeasureValid &&
                presenter.IsArrangeValid &&
                scrollViewer.IsMeasureValid &&
                scrollViewer.IsArrangeValid &&
                table.IsMeasureValid &&
                table.IsArrangeValid;
            horizontalScrollBarVisibility = scrollViewer.HorizontalScrollBarVisibility;

            Dispatcher.UIThread.Post(
                () => dispatcherResponded = true,
                DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();

            layoutManager.LayoutUpdated -= OnLayoutUpdated;
            window.Close();

            void OnLayoutUpdated(object? sender, EventArgs e)
            {
                layoutPassCount++;
                if (layoutPassCount < runawayLayoutPassLimit)
                    return;

                runawayLayoutDetected = true;
                window.Content = null;
            }
        }, CancellationToken.None);

        Assert.False(
            runawayLayoutDetected,
            $"The markdown table exceeded {runawayLayoutPassLimit} layout passes without converging.");
        Assert.True(dispatcherResponded, "The UI dispatcher did not process background work after table layout.");
        Assert.True(
            layoutIsValid,
            "The responsive table left part of its ScrollViewer layout subtree invalid.");
        Assert.Equal(ScrollBarVisibility.Auto, horizontalScrollBarVisibility);
        Assert.True(
            tableDesiredWidth <= narrowArrangeWidth,
            $"The {BroadMeasureWidth}px measure constraint expanded the table's desired width to {tableDesiredWidth}px " +
            $"after it was allocated only {narrowArrangeWidth}px.");
        Assert.InRange(gridWidth, expectedGridWidth - 0.01, expectedGridWidth + 0.01);
        Assert.InRange(gridDesiredWidth, expectedGridWidth - 0.01, expectedGridWidth + 0.01);
        Assert.InRange(extentWidth, expectedGridWidth - 0.01, expectedGridWidth + 0.01);
        Assert.True(
            extentWidth > viewportWidth,
            $"Expected horizontal overflow, but extent {extentWidth} did not exceed viewport {viewportWidth}. " +
            $"Passes={layoutPassCount}, grid={gridWidth}/{gridDesiredWidth}, " +
            $"table={tableWidth}/{tableDesiredWidth}, text={textContentWidth}/{textContentDesiredWidth}.");
        Assert.True(
            gridWidth > viewportWidth,
            $"Expected the fixed-width table grid to exceed the viewport, but grid {gridWidth} did not exceed viewport {viewportWidth}.");
    }

    private static ILayoutManager GetLayoutManager(Window window)
    {
        var property = typeof(TopLevel).GetProperty(
            "LayoutManager",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        return Assert.IsAssignableFrom<ILayoutManager>(property?.GetValue(window));
    }

    private sealed class BroadMeasureNarrowArrangeHost : Decorator
    {
        public double ArrangeWidth { get; init; }

        protected override Size MeasureOverride(Size availableSize)
        {
            Child?.Measure(new Size(BroadMeasureWidth, availableSize.Height));
            return Child is null
                ? default
                : new Size(ArrangeWidth, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Child?.Arrange(new Rect(0, 0, ArrangeWidth, finalSize.Height));
            return finalSize;
        }
    }
}
