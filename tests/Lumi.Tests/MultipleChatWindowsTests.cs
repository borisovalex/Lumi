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
        Chat? requestedChat = null;
        viewModel.OpenChatWindowRequested += requested => requestedChat = requested;

        viewModel.OpenChatInNewWindowCommand.Execute(chat);

        Assert.Same(chat, requestedChat);
        viewModel.Dispose();
    }

    [Fact]
    public void OpenChatInNewWindowCommand_WithNoActiveChat_RequestsDraftWindow()
    {
        Loc.Load("en");
        var viewModel = CreateViewModel();
        var requested = false;
        viewModel.OpenChatWindowRequested += requestedChat =>
        {
            Assert.Null(requestedChat);
            requested = true;
        };

        viewModel.OpenChatInNewWindowCommand.Execute(null);

        Assert.True(requested);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        viewModel.Dispose();
    }

    [Fact]
    public void OpenNewChatInNewWindowCommand_RequestsDraftWindowWithoutMovingActiveChat()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Still active in main" };
        var viewModel = CreateViewModel(chat);
        viewModel.ChatVM.CurrentChat = chat;
        Chat? requestedChat = chat;

        viewModel.OpenChatWindowRequested += requested => requestedChat = requested;

        viewModel.OpenNewChatInNewWindowCommand.Execute(null);

        Assert.Null(requestedChat);
        Assert.Same(chat, viewModel.ChatVM.CurrentChat);
        viewModel.Dispose();
    }

    [Fact]
    public void OpenChatInNewWindowCommand_ClearsMainSurfaceWhenDetachingActiveChat()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Active detached chat" };
        var viewModel = CreateViewModel(chat);
        viewModel.ChatVM.CurrentChat = chat;
        Chat? requestedChat = null;
        viewModel.OpenChatWindowRequested += requested => requestedChat = requested;

        viewModel.OpenChatInNewWindowCommand.Execute(chat);

        Assert.Same(chat, requestedChat);
        Assert.Null(viewModel.ChatVM.CurrentChat);
        viewModel.Dispose();
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
