using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class SplitSidePanelTests
{
    [Fact]
    public async Task SecondarySplitDiffRequest_OpensSingleSharedSidePanel()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var viewModel = await CreateSplitMainViewModel(first, second);

            var window = new MainWindow
            {
                Width = 1200,
                Height = 820,
                DataContext = viewModel,
            };

            window.Show();
            try
            {
                await WaitUntilAsync(() => window.FindControl<Grid>("SplitWorkspaceHost")?.IsVisible == true);

                var secondary = viewModel.SplitWorkspace.Panes[1].ChatViewModel;
                var fileChange = new FileChangeItem("src\\Lumi\\Views\\MainWindow.axaml.cs", isCreate: false);
                fileChange.SetSnapshots("old", "new");

                secondary.ShowDiff(fileChange);

                var diffIsland = Assert.IsType<Border>(window.FindControl<Border>("DiffIsland"));
                var browserIsland = Assert.IsType<Border>(window.FindControl<Border>("BrowserIsland"));
                var planIsland = Assert.IsType<Border>(window.FindControl<Border>("PlanIsland"));
                var splitHost = Assert.IsType<Grid>(window.FindControl<Grid>("SplitWorkspaceHost"));
                var chatGrid = Assert.IsType<Grid>(window.FindControl<Grid>("ChatContentGrid"));

                await WaitUntilAsync(() => diffIsland.IsVisible && secondary.IsDiffOpen);

                Assert.False(viewModel.ChatVM.IsDiffOpen);
                Assert.False(browserIsland.IsVisible);
                Assert.False(planIsland.IsVisible);
                Assert.Equal(0, Grid.GetColumn(splitHost));
                Assert.Equal(2, Grid.GetColumn(diffIsland));
                Assert.Equal(GridUnitType.Star, chatGrid.ColumnDefinitions[0].Width.GridUnitType);
                Assert.Equal(GridUnitType.Star, chatGrid.ColumnDefinitions[2].Width.GridUnitType);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SecondarySplitPlanRequest_UsesRequestingPaneContentInSharedSidePanel()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var viewModel = await CreateSplitMainViewModel(first, second);

            var window = new MainWindow
            {
                Width = 1200,
                Height = 820,
                DataContext = viewModel,
            };

            window.Show();
            try
            {
                await WaitUntilAsync(() => window.FindControl<Grid>("SplitWorkspaceHost")?.IsVisible == true);

                var secondary = viewModel.SplitWorkspace.Panes[1].ChatViewModel;
                viewModel.ChatVM.PlanContent = "## Primary plan";
                secondary.PlanContent = "## Secondary split plan";

                secondary.RaisePlanShowRequestedForTest();

                var planIsland = Assert.IsType<Border>(window.FindControl<Border>("PlanIsland"));
                var browserIsland = Assert.IsType<Border>(window.FindControl<Border>("BrowserIsland"));
                var diffIsland = Assert.IsType<Border>(window.FindControl<Border>("DiffIsland"));
                var planMarkdown = Assert.IsType<StrataMarkdown>(window.FindControl<StrataMarkdown>("PlanMarkdown"));
                var splitHost = Assert.IsType<Grid>(window.FindControl<Grid>("SplitWorkspaceHost"));
                var chatGrid = Assert.IsType<Grid>(window.FindControl<Grid>("ChatContentGrid"));

                await WaitUntilAsync(() => planIsland.IsVisible && secondary.IsPlanOpen);

                Assert.Equal("## Secondary split plan", planMarkdown.Markdown);
                Assert.False(viewModel.ChatVM.IsPlanOpen);
                Assert.False(browserIsland.IsVisible);
                Assert.False(diffIsland.IsVisible);
                Assert.Equal(0, Grid.GetColumn(splitHost));
                Assert.Equal(2, Grid.GetColumn(planIsland));
                Assert.Equal(GridUnitType.Star, chatGrid.ColumnDefinitions[0].Width.GridUnitType);
                Assert.Equal(GridUnitType.Star, chatGrid.ColumnDefinitions[2].Width.GridUnitType);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SplitFocusChange_KeepsVisibleOwnerPlanPanelOpen()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var viewModel = await CreateSplitMainViewModel(first, second);

            var window = new MainWindow
            {
                Width = 1200,
                Height = 820,
                DataContext = viewModel,
            };

            window.Show();
            try
            {
                await WaitUntilAsync(() => window.FindControl<Grid>("SplitWorkspaceHost")?.IsVisible == true);

                var primaryPane = viewModel.SplitWorkspace.Panes[0];
                var secondary = viewModel.SplitWorkspace.Panes[1].ChatViewModel;
                secondary.PlanContent = "## Secondary split plan";
                secondary.RaisePlanShowRequestedForTest();

                var planIsland = Assert.IsType<Border>(window.FindControl<Border>("PlanIsland"));
                var planMarkdown = Assert.IsType<StrataMarkdown>(window.FindControl<StrataMarkdown>("PlanMarkdown"));

                await WaitUntilAsync(() => planIsland.IsVisible && secondary.IsPlanOpen);

                viewModel.SplitWorkspace.FocusPane(primaryPane);

                await WaitUntilAsync(() => viewModel.ActiveChatId == first.Id);

                Assert.True(planIsland.IsVisible);
                Assert.True(secondary.IsPlanOpen);
                Assert.Equal("## Secondary split plan", planMarkdown.Markdown);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReplacingSplitPaneChat_ClosesPlanPanelOwnedByThatPane()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var third = CreateChat("Third");
            var viewModel = await CreateSplitMainViewModel(first, second, third);

            var window = new MainWindow
            {
                Width = 1200,
                Height = 820,
                DataContext = viewModel,
            };

            window.Show();
            try
            {
                await WaitUntilAsync(() => window.FindControl<Grid>("SplitWorkspaceHost")?.IsVisible == true);

                var secondary = viewModel.SplitWorkspace.Panes[1].ChatViewModel;
                secondary.PlanContent = "## Secondary split plan";
                secondary.RaisePlanShowRequestedForTest();

                var planIsland = Assert.IsType<Border>(window.FindControl<Border>("PlanIsland"));
                await WaitUntilAsync(() => planIsland.IsVisible && secondary.IsPlanOpen);

                await viewModel.SplitWorkspace.ReplaceFocusedChatAsync(third);

                await WaitUntilAsync(() => !planIsland.IsVisible && !secondary.IsPlanOpen);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static async Task<MainViewModel> CreateSplitMainViewModel(Chat first, Chat second, params Chat[] additionalChats)
    {
        var store = CreateDataStore(new[] { first, second }.Concat(additionalChats).ToArray());
        var viewModel = new MainViewModel(store, new CopilotService(), new UpdateService());
        await viewModel.ChatVM.LoadChatAsync(first);
        await viewModel.SplitWorkspace.OpenChatInSplitViewAsync(second);
        return viewModel;
    }

    private static DataStore CreateDataStore(params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                IsOnboarded = true,
                AutoSaveChats = false,
                EnableMemoryAutoSave = false,
            }
        };

        data.Chats.AddRange(chats);
        return new DataStore(data);
    }

    private static Chat CreateChat(string title)
    {
        var chat = new Chat
        {
            Title = title,
            UpdatedAt = DateTimeOffset.Now,
        };
        chat.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = title,
            Author = "Tester",
        });
        return chat;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.Now < deadline)
        {
            if (condition())
                return;

            await PumpAsync();
        }

        Assert.True(condition(), "Timed out waiting for UI condition.");
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(20);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }
}
