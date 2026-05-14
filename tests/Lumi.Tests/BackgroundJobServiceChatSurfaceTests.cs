using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class BackgroundJobServiceChatSurfaceTests
{
    [Fact]
    public void ResolveChatExecutorForTest_UsesVisibleDetachedOwnerBeforeFallback()
    {
        var chat = new Chat { Title = "Detached job target" };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [chat]
        };
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var fallback = new ChatViewModel(store, new CopilotService());
        using var detached = new ChatViewModel(store, new CopilotService())
        {
            CurrentChat = chat
        };
        registry.Attach(detached);
        using var service = new BackgroundJobService(store, registry, fallback);

        var executor = service.ResolveChatExecutorForTest(chat.Id);

        Assert.Same(detached, executor);
    }

    [Fact]
    public void ResolveChatExecutorForTest_UsesFallbackWhenChatHasNoVisibleOwner()
    {
        var chat = new Chat { Title = "Hidden job target" };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [chat]
        };
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var fallback = new ChatViewModel(store, new CopilotService());
        using var service = new BackgroundJobService(store, registry, fallback);

        var executor = service.ResolveChatExecutorForTest(chat.Id);

        Assert.Same(fallback, executor);
    }
}
