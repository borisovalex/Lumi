using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatSurfaceRegistryTests
{
    [Fact]
    public void Attach_TracksCurrentChatOwner()
    {
        var chat = new Chat { Title = "Tracked" };
        using var surface = CreateSurface(chat);
        using var registry = new ChatSurfaceRegistry();

        registry.Attach(surface);

        Assert.True(registry.TryGetOwner(chat.Id, out var owner));
        Assert.Same(surface, owner);
    }

    [Fact]
    public void CurrentChatChange_MovesOwnerToNewChat()
    {
        var first = new Chat { Title = "First" };
        var second = new Chat { Title = "Second" };
        using var surface = CreateSurface(first, second);
        using var registry = new ChatSurfaceRegistry();
        registry.Attach(surface);

        surface.CurrentChat = second;

        Assert.False(registry.TryGetOwner(first.Id, out _));
        Assert.True(registry.TryGetOwner(second.Id, out var owner));
        Assert.Same(surface, owner);
    }

    [Fact]
    public void Detach_RemovesTrackedOwner()
    {
        var chat = new Chat { Title = "Detached" };
        using var surface = CreateSurface(chat);
        using var registry = new ChatSurfaceRegistry();
        registry.Attach(surface);

        registry.Detach(surface);

        Assert.False(registry.TryGetOwner(chat.Id, out _));
    }

    private static ChatViewModel CreateSurface(params Chat[] chats)
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

        return new ChatViewModel(new DataStore(data), new CopilotService())
        {
            CurrentChat = chats.FirstOrDefault()
        };
    }
}
