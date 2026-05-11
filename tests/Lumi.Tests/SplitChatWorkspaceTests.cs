using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class SplitChatWorkspaceLayoutTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(6, 2, 3)]
    [InlineData(7, 2, 4)]
    [InlineData(8, 2, 4)]
    public void GetGridDimensions_BalancesUpToEightPanes(int panes, int expectedRows, int expectedColumns)
    {
        var (rows, columns) = SplitChatWorkspaceViewModel.GetGridDimensions(panes);

        Assert.Equal(expectedRows, rows);
        Assert.Equal(expectedColumns, columns);
    }
}

[Collection("Headless UI")]
public sealed class SplitChatWorkspaceTests
{
    [Fact]
    public async Task OpenChatInSplitView_WithCurrentChat_AddsNewPaneAndFocusesIt()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var current = CreateChat("Current");
            var next = CreateChat("Next");
            var store = CreateDataStore(current, next);
            var primary = new ChatViewModel(store, new CopilotService());
            await primary.LoadChatAsync(current);
            var workspace = new SplitChatWorkspaceViewModel(store, new CopilotService(), primary);

            await workspace.OpenChatInSplitViewAsync(next);

            Assert.True(workspace.IsActive);
            Assert.Equal(2, workspace.Panes.Count);
            Assert.Equal((1, 2), (workspace.Rows, workspace.Columns));
            Assert.Equal(current.Id, workspace.Panes[0].ChatId);
            Assert.Equal(next.Id, workspace.Panes[1].ChatId);
            Assert.Equal(next.Id, workspace.FocusedChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReplaceFocusedChatAsync_ReplacesFocusedPaneWithoutAddingPane()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var replacement = CreateChat("Replacement");
            var store = CreateDataStore(first, second, replacement);
            var primary = new ChatViewModel(store, new CopilotService());
            await primary.LoadChatAsync(first);
            var workspace = new SplitChatWorkspaceViewModel(store, new CopilotService(), primary);
            await workspace.OpenChatInSplitViewAsync(second);
            workspace.FocusPane(workspace.Panes[0]);

            await workspace.ReplaceFocusedChatAsync(replacement);

            Assert.Equal(2, workspace.Panes.Count);
            Assert.Equal(replacement.Id, workspace.Panes[0].ChatId);
            Assert.Equal(replacement.Id, workspace.FocusedChatId);
            Assert.Equal(second.Id, workspace.Panes[1].ChatId);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task DropSidebarChatAsync_WhenInactive_CreatesSplitWithDroppedChatInRequestedSlot(
        int targetIndex,
        int expectedDroppedIndex)
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var current = CreateChat("Current");
            var dropped = CreateChat("Dropped");
            var store = CreateDataStore(current, dropped);
            var primary = new ChatViewModel(store, new CopilotService());
            await primary.LoadChatAsync(current);
            var workspace = new SplitChatWorkspaceViewModel(store, new CopilotService(), primary);

            await workspace.DropSidebarChatAsync(dropped, targetIndex);

            Assert.True(workspace.IsActive);
            Assert.Equal(2, workspace.Panes.Count);
            Assert.Equal((1, 2), (workspace.Rows, workspace.Columns));
            Assert.Equal(dropped.Id, workspace.Panes[expectedDroppedIndex].ChatId);
            Assert.Equal(current.Id, workspace.Panes[1 - expectedDroppedIndex].ChatId);
            Assert.Equal(dropped.Id, workspace.FocusedChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MovePane_ReordersPaneAndKeepsMovedPaneFocused()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var third = CreateChat("Third");
            var store = CreateDataStore(first, second, third);
            var primary = new ChatViewModel(store, new CopilotService());
            await primary.LoadChatAsync(first);
            var workspace = new SplitChatWorkspaceViewModel(store, new CopilotService(), primary);
            await workspace.OpenChatInSplitViewAsync(second);
            await workspace.OpenChatInSplitViewAsync(third);
            var movedPane = workspace.Panes[2];

            workspace.MovePane(movedPane.PaneId, 0);

            Assert.Equal(third.Id, workspace.Panes[0].ChatId);
            Assert.Equal(third.Id, workspace.FocusedChatId);
            Assert.Equal([third.Id, first.Id, second.Id], workspace.Panes.Select(pane => pane.ChatId!.Value));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClosePaneAsync_ExitsSplitViewWhenOnePaneRemains()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var store = CreateDataStore(first, second);
            var primary = new ChatViewModel(store, new CopilotService());
            await primary.LoadChatAsync(first);
            var workspace = new SplitChatWorkspaceViewModel(store, new CopilotService(), primary);
            await workspace.OpenChatInSplitViewAsync(second);

            await workspace.ClosePaneAsync(workspace.Panes[1]);

            Assert.False(workspace.IsActive);
            Assert.Empty(workspace.Panes);
            Assert.Equal(first.Id, primary.CurrentChat?.Id);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClosePaneAsync_WhenFocusedPaneCloses_FocusesNearestRemainingPane()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var third = CreateChat("Third");
            var store = CreateDataStore(first, second, third);
            var primary = new ChatViewModel(store, new CopilotService());
            await primary.LoadChatAsync(first);
            var workspace = new SplitChatWorkspaceViewModel(store, new CopilotService(), primary);
            await workspace.OpenChatInSplitViewAsync(second);
            await workspace.OpenChatInSplitViewAsync(third);
            workspace.FocusPane(workspace.Panes[1]);

            await workspace.ClosePaneAsync(workspace.Panes[1]);

            Assert.True(workspace.IsActive);
            Assert.Equal(2, workspace.Panes.Count);
            Assert.Equal(third.Id, workspace.FocusedChatId);
            Assert.Equal([first.Id, third.Id], workspace.Panes.Select(pane => pane.ChatId!.Value));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ExitSplitViewAsync_ReturnsPrimaryViewModelToFocusedChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var store = CreateDataStore(first, second);
            var primary = new ChatViewModel(store, new CopilotService());
            await primary.LoadChatAsync(first);
            var workspace = new SplitChatWorkspaceViewModel(store, new CopilotService(), primary);
            await workspace.OpenChatInSplitViewAsync(second);

            await workspace.ExitSplitViewAsync();

            Assert.False(workspace.IsActive);
            Assert.Empty(workspace.Panes);
            Assert.Equal(second.Id, primary.CurrentChat?.Id);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelOpenChatCommand_ReplacesFocusedPaneWhenSplitViewIsActive()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var replacement = CreateChat("Replacement");
            var store = CreateDataStore(first, second, replacement);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(first);
            await vm.SplitWorkspace.OpenChatInSplitViewAsync(second);
            vm.SplitWorkspace.FocusPane(vm.SplitWorkspace.Panes[0]);

            await vm.OpenChatCommand.ExecuteAsync(replacement);

            Assert.True(vm.SplitWorkspace.IsActive);
            Assert.Equal(2, vm.SplitWorkspace.Panes.Count);
            Assert.Equal(replacement.Id, vm.SplitWorkspace.Panes[0].ChatId);
            Assert.Equal(replacement.Id, vm.ActiveChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelOpenChatCommand_WhenSplitViewIsInactive_LoadsSingleChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var store = CreateDataStore(first, second);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(first);

            await vm.OpenChatCommand.ExecuteAsync(second);

            Assert.False(vm.SplitWorkspace.IsActive);
            Assert.Empty(vm.SplitWorkspace.Panes);
            Assert.Equal(second.Id, vm.ChatVM.CurrentChat?.Id);
            Assert.Equal(second.Id, vm.ActiveChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelOpenChatCommand_FocusesExistingSplitPane()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var store = CreateDataStore(first, second);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(first);
            await vm.SplitWorkspace.OpenChatInSplitViewAsync(second);
            vm.SplitWorkspace.FocusPane(vm.SplitWorkspace.Panes[0]);

            await vm.OpenChatCommand.ExecuteAsync(second);

            Assert.True(vm.SplitWorkspace.IsActive);
            Assert.Equal(2, vm.SplitWorkspace.Panes.Count);
            Assert.Equal(second.Id, vm.SplitWorkspace.FocusedChatId);
            Assert.Equal(second.Id, vm.ActiveChatId);
            Assert.Equal([first.Id, second.Id], vm.SplitWorkspace.Panes.Select(pane => pane.ChatId!.Value));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelNewChatCommand_ClearsFocusedSplitPaneOnly()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var store = CreateDataStore(first, second);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(first);
            await vm.SplitWorkspace.OpenChatInSplitViewAsync(second);
            var focusedPane = vm.SplitWorkspace.Panes[1];
            vm.SplitWorkspace.FocusPane(focusedPane);

            vm.NewChatCommand.Execute(null);

            Assert.True(vm.SplitWorkspace.IsActive);
            Assert.Equal(first.Id, vm.SplitWorkspace.Panes[0].ChatViewModel.CurrentChat?.Id);
            Assert.Null(focusedPane.ChatViewModel.CurrentChat);
            Assert.Null(focusedPane.Chat);
            Assert.Null(focusedPane.ChatId);
            Assert.Null(vm.SplitWorkspace.FocusedChatId);
            Assert.True(focusedPane.IsFocused);
            Assert.Null(vm.ActiveChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelNewChatCommand_WhenSplitViewIsInactive_ClearsSingleChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = CreateChat("Current");
            var store = CreateDataStore(chat);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(chat);

            vm.NewChatCommand.Execute(null);

            Assert.False(vm.SplitWorkspace.IsActive);
            Assert.Empty(vm.SplitWorkspace.Panes);
            Assert.Null(vm.ChatVM.CurrentChat);
            Assert.Null(vm.ActiveChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelOpenChatCommand_WhenFocusedPaneIsDraftAndChatAlreadyOpen_MovesOpenChatIntoDraftSlot()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var third = CreateChat("Third");
            var store = CreateDataStore(first, second, third);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(first);
            await vm.SplitWorkspace.OpenChatInSplitViewAsync(second);
            await vm.SplitWorkspace.OpenChatInSplitViewAsync(third);
            vm.SplitWorkspace.FocusPane(vm.SplitWorkspace.Panes[1]);
            vm.NewChatCommand.Execute(null);

            Assert.Null(vm.SplitWorkspace.Panes[1].ChatId);

            await vm.OpenChatCommand.ExecuteAsync(third);

            Assert.True(vm.SplitWorkspace.IsActive);
            Assert.Equal(2, vm.SplitWorkspace.Panes.Count);
            Assert.Equal([first.Id, third.Id], vm.SplitWorkspace.Panes.Select(pane => pane.ChatId!.Value));
            Assert.DoesNotContain(vm.SplitWorkspace.Panes, pane => pane.ChatId is null);
            Assert.Equal(third.Id, vm.SplitWorkspace.FocusedChatId);
            Assert.Equal(third.Id, vm.ActiveChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OpenChatInSplitViewAsync_WhenFocusedPaneIsDraft_ReusesDraftSlot()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var third = CreateChat("Third");
            var store = CreateDataStore(first, second, third);
            var primary = new ChatViewModel(store, new CopilotService());
            await primary.LoadChatAsync(first);
            var workspace = new SplitChatWorkspaceViewModel(store, new CopilotService(), primary);
            await workspace.OpenChatInSplitViewAsync(second);
            workspace.FocusPane(workspace.Panes[0]);
            workspace.StartNewChatInFocusedPane(projectId: null);

            await workspace.OpenChatInSplitViewAsync(third);

            Assert.True(workspace.IsActive);
            Assert.Equal(2, workspace.Panes.Count);
            Assert.Equal([third.Id, second.Id], workspace.Panes.Select(pane => pane.ChatId!.Value));
            Assert.Equal(third.Id, workspace.FocusedChatId);
            Assert.DoesNotContain(workspace.Panes, pane => pane.ChatId is null);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelDeleteChatCommand_WhenFocusedSplitPaneClosesToSinglePane_KeepsRemainingChatOpen()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var first = CreateChat("First");
            var second = CreateChat("Second");
            var store = CreateDataStore(first, second);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(first);
            await vm.SplitWorkspace.OpenChatInSplitViewAsync(second);
            vm.SplitWorkspace.FocusPane(vm.SplitWorkspace.Panes[1]);

            await vm.DeleteChatCommand.ExecuteAsync(second);

            Assert.DoesNotContain(store.Data.Chats, chat => chat.Id == second.Id);
            Assert.False(vm.SplitWorkspace.IsActive);
            Assert.Empty(vm.SplitWorkspace.Panes);
            Assert.Equal(first.Id, vm.ChatVM.CurrentChat?.Id);
            Assert.Equal(first.Id, vm.ActiveChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelDeleteChatCommand_WhenSingleInactiveChatDeleted_KeepsCurrentChatOpen()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var current = CreateChat("Current");
            var other = CreateChat("Other");
            var store = CreateDataStore(current, other);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(current);

            await vm.DeleteChatCommand.ExecuteAsync(other);

            Assert.DoesNotContain(store.Data.Chats, item => item.Id == other.Id);
            Assert.False(vm.SplitWorkspace.IsActive);
            Assert.Empty(vm.SplitWorkspace.Panes);
            Assert.Equal(current.Id, vm.ChatVM.CurrentChat?.Id);
            Assert.Equal(current.Id, vm.ActiveChatId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainViewModelDeleteChatCommand_WhenSingleActiveChatDeleted_ClearsChat()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chat = CreateChat("Only");
            var store = CreateDataStore(chat);
            var vm = new MainViewModel(store, new CopilotService(), new UpdateService());
            await vm.ChatVM.LoadChatAsync(chat);

            await vm.DeleteChatCommand.ExecuteAsync(chat);

            Assert.DoesNotContain(store.Data.Chats, item => item.Id == chat.Id);
            Assert.Null(vm.ChatVM.CurrentChat);
            Assert.Null(vm.ActiveChatId);
        }, CancellationToken.None);
    }

    private static DataStore CreateDataStore(params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
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
            UpdatedAt = DateTimeOffset.Now
        };
        chat.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = title,
            Author = "Tester"
        });
        return chat;
    }
}
