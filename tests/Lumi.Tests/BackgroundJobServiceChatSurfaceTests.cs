using System;
using System.Collections.Generic;
using System.Reflection;
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

    [Fact]
    public void ResolveChatExecutorForTest_UsesLiveOwnerBeforeFallback()
    {
        var running = new Chat { Title = "Running hidden target" };
        var visible = new Chat { Title = "Visible chat" };
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            },
            Chats = [running, visible]
        };
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var fallback = new ChatViewModel(store, new CopilotService());
        using var liveOwner = new ChatViewModel(store, new CopilotService())
        {
            CurrentChat = running
        };
        registry.Attach(liveOwner);
        var runtimeStates = GetField<Dictionary<Guid, ChatRuntimeState>>(liveOwner, "_runtimeStates");
        var runtime = new ChatRuntimeState { Chat = running };
        runtime.IsBusy = true;
        runtimeStates[running.Id] = runtime;
        liveOwner.CurrentChat = visible;
        using var service = new BackgroundJobService(store, registry, fallback);

        var executor = service.ResolveChatExecutorForTest(running.Id);

        Assert.Same(liveOwner, executor);
    }

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));
}
