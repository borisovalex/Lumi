using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatWorkspaceViewTests
{
    [Fact]
    public async Task WorkspaceHostsChatAndPreviewIslands()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, new CopilotService());
            var workspace = new ChatWorkspaceView
            {
                DataContext = chatVm,
                DataStore = dataStore,
                ShowInternalTitle = false,
                UseChatIslandChrome = false,
                PreviewIslandMargin = new Thickness(0, 8, 8, 8),
            };
            var window = new Window
            {
                Width = 1000,
                Height = 720,
                Content = workspace,
            };

            window.Show();
            try
            {
                await Task.Delay(50);

                Assert.Same(chatVm, workspace.DataContext);
                Assert.NotNull(workspace.ChatView);
                Assert.False(workspace.ChatView!.ShowInternalTitle);
                Assert.Equal(new Thickness(0, 8, 8, 8), workspace.PreviewIslandMargin);
                Assert.NotNull(workspace.FindControl<Grid>("WorkspaceGrid"));
                Assert.NotNull(workspace.FindControl<Border>("BrowserIsland"));
                Assert.NotNull(workspace.FindControl<Border>("DiffIsland"));
                Assert.NotNull(workspace.FindControl<Border>("PlanIsland"));
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WorkspacePreviewMethodsAreSafeAcrossRetargets()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, new CopilotService());
            using var nextChatVm = new ChatViewModel(dataStore, new CopilotService());
            var workspace = new ChatWorkspaceView
            {
                DataContext = chatVm,
                DataStore = dataStore,
            };
            var window = new Window
            {
                Width = 1100,
                Height = 760,
                Content = workspace,
            };

            window.Show();
            try
            {
                workspace.HideBrowserPanel();
                workspace.HideDiffPanel();
                workspace.HidePlanPanel();
                Assert.False(workspace.IsBrowserOpen);
                Assert.False(workspace.IsDiffOpen);
                Assert.False(workspace.IsPlanOpen);

                workspace.DataContext = nextChatVm;
                workspace.HideBrowserPanel();
                workspace.HideDiffPanel();
                workspace.HidePlanPanel();
                Assert.Same(nextChatVm, workspace.DataContext);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void MainAndDetachedHostXamlUseSameWorkspaceComponent()
    {
        var root = FindRepoRoot();
        var mainWindowXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "MainWindow.axaml"));
        var chatWindowXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatWindow.axaml"));

        Assert.Contains("<views:ChatWorkspaceView x:Name=\"ChatContentGrid\"", mainWindowXaml);
        Assert.Contains("UseChatIslandChrome=\"True\"", mainWindowXaml);
        Assert.Contains("ShowInternalTitle=\"True\"", mainWindowXaml);
        Assert.DoesNotContain("x:Name=\"BrowserIsland\"", mainWindowXaml);
        Assert.DoesNotContain("x:Name=\"DiffIsland\"", mainWindowXaml);
        Assert.DoesNotContain("x:Name=\"PlanIsland\"", mainWindowXaml);

        Assert.Contains("<views:ChatWorkspaceView x:Name=\"DetachedChatView\"", chatWindowXaml);
        Assert.Contains("UseChatIslandChrome=\"False\"", chatWindowXaml);
        Assert.Contains("ShowInternalTitle=\"False\"", chatWindowXaml);
        Assert.Contains("PreviewIslandMargin=\"0,8,8,8\"", chatWindowXaml);
        Assert.DoesNotContain("x:Name=\"BrowserIsland\"", chatWindowXaml);
        Assert.DoesNotContain("x:Name=\"DiffIsland\"", chatWindowXaml);
        Assert.DoesNotContain("x:Name=\"PlanIsland\"", chatWindowXaml);
    }

    private static AppData CreateAppData() => new()
    {
        Settings = new UserSettings
        {
            AutoSaveChats = false,
            EnableMemoryAutoSave = false,
        },
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Lumi repository root.");
    }
}
