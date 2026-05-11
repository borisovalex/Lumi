using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class SplitDragCancelTests
{
    [Fact]
    public async Task CancelBand_ActivatesBeforeOverlayWasVisible()
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

                var payload = CreateSplitDragPayload(third.Id);
                InvokeUpdateSplitDropOverlay(window, viewModel, payload, new Point(32, 24));

                var overlay = Assert.IsType<Grid>(window.FindControl<Grid>("SplitDropOverlay"));
                var cancelTarget = Assert.IsType<Border>(window.FindControl<Border>("SplitDropCancelTarget"));

                await WaitUntilAsync(() => overlay.IsVisible && cancelTarget.Classes.Contains("active"));
                await WaitUntilAsync(() => cancelTarget.BorderThickness.Left > 1.5);

                Assert.True(overlay.IsVisible);
                Assert.Contains("active", cancelTarget.Classes);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CancelBandDrop_DoesNotReplacePane_WhenOverlayIsAlreadyHidden()
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

                var initialPaneIds = viewModel.SplitWorkspace.Panes.Select(pane => pane.ChatId).ToArray();
                var initialFocusedChatId = viewModel.SplitWorkspace.FocusedChatId;
                var payload = CreateSplitDragPayload(third.Id);

                await InvokeHandleSplitDropAsync(window, viewModel, payload, new Point(32, 24));

                Assert.Equal(initialPaneIds, viewModel.SplitWorkspace.Panes.Select(pane => pane.ChatId).ToArray());
                Assert.Equal(initialFocusedChatId, viewModel.SplitWorkspace.FocusedChatId);
                Assert.DoesNotContain(viewModel.SplitWorkspace.Panes, pane => pane.ChatId == third.Id);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static async Task<MainViewModel> CreateSplitMainViewModel(params Chat[] chats)
    {
        var store = CreateDataStore(chats);
        var viewModel = new MainViewModel(store, new CopilotService(), new UpdateService());
        await viewModel.ChatVM.LoadChatAsync(chats[0]);
        await viewModel.SplitWorkspace.OpenChatInSplitViewAsync(chats[1]);
        return viewModel;
    }

    private static object CreateSplitDragPayload(Guid chatId)
    {
        var payloadType = typeof(MainWindow).GetNestedType("SplitChatDragPayload", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SplitChatDragPayload type not found.");

        return Activator.CreateInstance(
                   payloadType,
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   args: [chatId, SplitChatDragSource.Sidebar, null],
                   culture: null)
               ?? throw new InvalidOperationException("Failed to create SplitChatDragPayload.");
    }

    private static void InvokeUpdateSplitDropOverlay(
        MainWindow window,
        MainViewModel viewModel,
        object payload,
        Point point)
    {
        var method = GetPrivateMainWindowMethod("UpdateSplitDropOverlay");
        method.Invoke(window, [viewModel, payload, point]);
    }

    private static async Task InvokeHandleSplitDropAsync(
        MainWindow window,
        MainViewModel viewModel,
        object payload,
        Point point)
    {
        var method = GetPrivateMainWindowMethod("HandleSplitDropAsync");
        var result = method.Invoke(window, [viewModel, payload, point]);
        await Assert.IsAssignableFrom<Task>(result);
    }

    private static MethodInfo GetPrivateMainWindowMethod(string name)
        => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException($"{name} method not found.");

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
