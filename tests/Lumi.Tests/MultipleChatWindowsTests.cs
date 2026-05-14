using System;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class MultipleChatWindowsTests
{
    [Fact]
    public void OpenChatInNewWindowCommand_RaisesRequestedChatId()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Detached chat" };
        var viewModel = CreateViewModel(chat);
        DetachedChatWindowRequest? request = null;
        viewModel.OpenChatWindowRequested += requested => request = requested;

        viewModel.OpenChatInNewWindowCommand.Execute(chat);

        Assert.Same(chat, request?.Chat);
        Assert.NotNull(request?.WindowVM);
        viewModel.Dispose();
        request?.WindowVM.Dispose();
        request?.WindowVM.ChatVM.Dispose();
    }

    [Fact]
    public void OpenChatInNewWindowCommand_WithNoActiveChat_RequestsDraftWindow()
    {
        Loc.Load("en");
        var viewModel = CreateViewModel();
        DetachedChatWindowRequest? request = null;
        var requested = false;
        viewModel.OpenChatWindowRequested += requestedWindow =>
        {
            request = requestedWindow;
            Assert.Null(requestedWindow.Chat);
            Assert.NotNull(requestedWindow.WindowVM);
            requested = true;
        };

        viewModel.OpenChatInNewWindowCommand.Execute(null);

        Assert.True(requested);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        viewModel.Dispose();
        request?.WindowVM.Dispose();
        request?.WindowVM.ChatVM.Dispose();
    }

    [Fact]
    public void OpenNewChatInNewWindowCommand_RequestsDraftWindowWithoutMovingActiveChat()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Still active in main" };
        var viewModel = CreateViewModel(chat);
        viewModel.ChatVM.CurrentChat = chat;
        DetachedChatWindowRequest? request = null;

        viewModel.OpenChatWindowRequested += requested => request = requested;

        viewModel.OpenNewChatInNewWindowCommand.Execute(null);

        Assert.Null(request?.Chat);
        Assert.NotNull(request?.WindowVM);
        Assert.Same(chat, viewModel.ChatVM.CurrentChat);
        viewModel.Dispose();
        request?.WindowVM.Dispose();
        request?.WindowVM.ChatVM.Dispose();
    }

    [Fact]
    public void OpenChatInNewWindowCommand_TransfersActiveChatViewModelWhenDetachingActiveChat()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Active detached chat" };
        var viewModel = CreateViewModel(chat);
        var originalChatVm = viewModel.ChatVM;
        originalChatVm.CurrentChat = chat;
        originalChatVm.IsBusy = true;
        originalChatVm.IsStreaming = true;
        DetachedChatWindowRequest? request = null;
        viewModel.OpenChatWindowRequested += requested => request = requested;

        viewModel.OpenChatInNewWindowCommand.Execute(chat);

        Assert.Same(chat, request?.Chat);
        Assert.Same(originalChatVm, request?.WindowVM.ChatVM);
        Assert.NotSame(originalChatVm, viewModel.ChatVM);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        Assert.True(originalChatVm.IsBusy);
        Assert.True(originalChatVm.IsStreaming);
        viewModel.Dispose();
        request?.WindowVM.Dispose();
        originalChatVm.Dispose();
    }

    [Fact]
    public void OpenChatInNewWindowCommand_DetachingActiveBusyChat_KeepsTransferredSurfaceAsChatOwner()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Busy owner" };
        var viewModel = CreateViewModel(chat);
        var originalChatVm = viewModel.ChatVM;
        originalChatVm.CurrentChat = chat;
        originalChatVm.IsBusy = true;
        DetachedChatWindowRequest? request = null;
        viewModel.OpenChatWindowRequested += requested => request = requested;

        viewModel.OpenChatInNewWindowCommand.Execute(chat);

        Assert.True(viewModel.ChatSurfaceRegistry.TryGetOwner(chat.Id, out var owner));
        Assert.Same(originalChatVm, owner);
        Assert.Same(originalChatVm, request?.WindowVM.ChatVM);
        viewModel.Dispose();
        request?.WindowVM.Dispose();
        originalChatVm.Dispose();
    }

    [Fact]
    public void OpenChatInNewWindowCommand_FocusingAlreadyDetachedChat_ClearsDuplicateMainSurfaceWithoutOpeningWindow()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Already detached" };
        var viewModel = CreateViewModel(chat);
        viewModel.ChatVM.CurrentChat = chat;
        var openRequested = false;
        viewModel.OpenChatWindowRequested += _ => openRequested = true;
        viewModel.DetachedChatFocusRequested += _ => true;

        viewModel.OpenChatInNewWindowCommand.Execute(chat);

        Assert.False(openRequested);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        Assert.Null(viewModel.ActiveChatId);
        viewModel.Dispose();
    }

    [Fact]
    public void OpenChatInNewWindowCommand_DoesNotTransferMainViewModelForInactiveChat()
    {
        Loc.Load("en");
        var mainChat = new Chat { Title = "Main chat" };
        var inactiveChat = new Chat { Title = "Inactive detached chat" };
        var viewModel = CreateViewModel(mainChat, inactiveChat);
        var originalChatVm = viewModel.ChatVM;
        viewModel.ChatVM.CurrentChat = mainChat;
        DetachedChatWindowRequest? request = null;
        viewModel.OpenChatWindowRequested += requested => request = requested;

        viewModel.OpenChatInNewWindowCommand.Execute(inactiveChat);

        Assert.Same(inactiveChat, request?.Chat);
        Assert.NotSame(originalChatVm, request?.WindowVM.ChatVM);
        Assert.Same(originalChatVm, viewModel.ChatVM);
        Assert.Same(mainChat, viewModel.ChatVM.CurrentChat);
        viewModel.Dispose();
        request?.WindowVM.Dispose();
        request?.WindowVM.ChatVM.Dispose();
    }

    [Fact]
    public async Task OpenChatByIdAsync_FocusesDetachedWindowInsteadOfLoadingInMainSurface()
    {
        Loc.Load("en");
        var mainChat = new Chat { Title = "Main chat" };
        var detachedChat = new Chat { Title = "Already detached chat" };
        var viewModel = CreateViewModel(mainChat, detachedChat);
        viewModel.ChatVM.CurrentChat = mainChat;
        Chat? focusedChat = null;
        Guid? resyncedActiveChatId = null;
        viewModel.DetachedChatFocusRequested += chat =>
        {
            focusedChat = chat;
            return true;
        };
        viewModel.ChatSelectionSyncRequested += activeChatId => resyncedActiveChatId = activeChatId;

        var opened = await viewModel.OpenChatByIdAsync(detachedChat.Id);

        Assert.True(opened);
        Assert.Same(detachedChat, focusedChat);
        Assert.Same(mainChat, viewModel.ChatVM.CurrentChat);
        Assert.Equal(mainChat.Id, resyncedActiveChatId);
        viewModel.Dispose();
    }

    [Fact]
    public void DeleteChatCommand_RaisesDeletedChatId()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Deleted chat" };
        var viewModel = CreateViewModel(chat);
        Guid? deletedChatId = null;
        viewModel.ChatDeleted += chatId => deletedChatId = chatId;

        viewModel.DeleteChatCommand.Execute(chat);

        Assert.Equal(chat.Id, deletedChatId);
        viewModel.Dispose();
    }

    [Fact]
    public void ChatWindowViewModel_WindowTitleTracksChatTitle()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Initial title" };
        var chatVm = new ChatViewModel(new DataStore(new AppData { Chats = [chat] }), new CopilotService())
        {
            CurrentChat = chat
        };
        using var windowVm = new ChatWindowViewModel(chatVm);
        var changed = false;
        windowVm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatWindowViewModel.WindowTitle))
                changed = true;
        };

        chat.Title = "Updated title";

        Assert.True(changed);
        Assert.Equal("Updated title", windowVm.WindowTitle);
        chatVm.Dispose();
    }

    private static MainViewModel CreateViewModel(params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [.. chats]
        };

        return new MainViewModel(new DataStore(data), new CopilotService(), new UpdateService());
    }
}
