using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression guard for the "clicking a transcript collapsible randomly stops working" bug.
///
/// The transcript retains each item's built view and re-parents the SAME control instance across
/// the visual tree on chat switches / virtualization (see TranscriptTurnControl.CreateItemHost,
/// which builds the view once and holds it). The collapsible controls subscribe their PART_Header
/// pointer handler only in OnApplyTemplate, which does NOT run again when an already-templated
/// control is detached and reattached. StrataThink additionally unsubscribed that handler in
/// OnDetachedFromVisualTree, so after the first detach/reattach its header click was dead — the
/// control could no longer be expanded or collapsed. These tests detach and reattach each control
/// (mirroring the retain/re-parent cycle) and assert a real header click still toggles it.
/// </summary>
[Collection("Headless UI")]
public sealed class StrataCollapsibleReparentClickTests
{
    [Fact]
    public Task StrataThink_HeaderClickTogglesAfterDetachReattach() =>
        AssertHeaderClickTogglesAcrossReparent(
            () => new StrataThink { Label = "7 sources", Content = "trace" },
            view => ((StrataThink)view).IsExpanded);

    [Fact]
    public Task StrataAiToolCall_HeaderClickTogglesAfterDetachReattach() =>
        AssertHeaderClickTogglesAcrossReparent(
            () => new StrataAiToolCall { ToolName = "search_code", InputParameters = "{ \"q\": \"x\" }" },
            view => ((StrataAiToolCall)view).IsExpanded);

    [Fact]
    public Task StrataTerminalPreview_HeaderClickTogglesAfterDetachReattach() =>
        AssertHeaderClickTogglesAcrossReparent(
            () => new StrataTerminalPreview { Command = "ls -la", Output = "file.txt" },
            view => ((StrataTerminalPreview)view).IsExpanded);

    [Fact]
    public Task StrataTurnSummary_HeaderClickTogglesAfterDetachReattach() =>
        AssertHeaderClickTogglesAcrossReparent(
            () => new StrataTurnSummary { Label = "3 steps", Content = "details" },
            view => ((StrataTurnSummary)view).IsExpanded);

    private static async Task AssertHeaderClickTogglesAcrossReparent(
        Func<Control> buildView,
        Func<Control, bool> getIsExpanded)
    {
        using var session = HeadlessTestSession.Start();

        // IMPORTANT: capture observations inside the dispatched body and assert OUTSIDE it. Avalonia's
        // HeadlessUnitTestSession.Dispatch(Func<Task>) swallows exceptions thrown inside an async body,
        // so an Assert.* inside the lambda would NOT fail the test (it would pass regardless of the
        // bug). See HeadlessTestSession.Dispatch for details.
        bool baselineExpanded = false;
        bool baselineCollapsed = false;
        bool reparentExpanded = false;
        bool reparentCollapsed = false;

        await session.Dispatch(async () =>
        {
            var view = buildView();

            // A StackPanel host mirrors how TranscriptTurnControl holds the built item views, and an
            // outer container mirrors the transcript re-parenting the whole host (AdoptHost /
            // ReleaseAdoptedHost) rather than removing individual items. Detaching via the ANCESTOR
            // keeps `view` in its own parent, so Avalonia preserves its template and does NOT re-run
            // OnApplyTemplate on reattach — exactly the condition that exposed the dead click handler.
            var host = new StackPanel();
            host.Children.Add(view);
            var outer = new Border { Child = host };

            var window = new Window { Width = 520, Height = 360, Content = outer };
            window.Show();
            await PumpAsync();

            // Baseline: a real header click toggles expansion before any re-parenting.
            var initial = getIsExpanded(view);
            ClickHeader(window, view);
            await PumpAsync();
            baselineExpanded = getIsExpanded(view) != initial;

            ClickHeader(window, view);
            await PumpAsync();
            baselineCollapsed = getIsExpanded(view) == initial;

            // Detach then reattach the SAME control instance by re-parenting its host, as the
            // retained transcript does on a chat switch. The template is not rebuilt, so
            // OnApplyTemplate does not re-run.
            outer.Child = null;
            await PumpAsync();
            outer.Child = host;
            await PumpAsync();

            // Regression: the header click handler must survive the detach/reattach.
            var afterReparent = getIsExpanded(view);
            ClickHeader(window, view);
            await PumpAsync();
            reparentExpanded = getIsExpanded(view) != afterReparent;

            // And it must keep working for a full toggle cycle, not just a single click.
            ClickHeader(window, view);
            await PumpAsync();
            reparentCollapsed = getIsExpanded(view) == afterReparent;

            window.Close();
        }, CancellationToken.None);

        Assert.True(baselineExpanded, "Baseline header click should toggle expansion before re-parenting.");
        Assert.True(baselineCollapsed, "Second baseline header click should toggle back before re-parenting.");
        Assert.True(reparentExpanded, "Header click after detach/reattach should still toggle expansion.");
        Assert.True(reparentCollapsed, "Header click after detach/reattach should still toggle back.");
    }

    private static void ClickHeader(Window window, Control view)
    {
        var header = view.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Name == "PART_Header");

        var topLeft = header.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("PART_Header is not attached to the test window.");
        var point = topLeft + new Point(header.Bounds.Width / 2, header.Bounds.Height / 2);

        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
