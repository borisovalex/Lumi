using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
                Assert.False(workspace.ChatView!.UseShellChrome);
                Assert.Contains("flat-window", workspace.ChatView!.FindControl<StrataTheme.Controls.StrataChatShell>("ChatShell")!.Classes);
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
        Assert.DoesNotContain("RowDefinitions=\"38,*\"", chatWindowXaml);
        Assert.DoesNotContain("Grid.Row=\"1\"", chatWindowXaml);
        Assert.Contains("<Grid Height=\"10\"", chatWindowXaml);
        Assert.DoesNotContain("<Grid Height=\"38\"", chatWindowXaml);
        Assert.Contains("Panel.ZIndex=\"10\"", chatWindowXaml);
        Assert.True(chatWindowXaml.IndexOf("x:Name=\"DetachedChatView\"", StringComparison.Ordinal) <
            chatWindowXaml.IndexOf("x:Name=\"TitleDragRegion\"", StringComparison.Ordinal));
        Assert.Contains("UseShellChrome=\"{Binding UseChatIslandChrome", File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatWorkspaceView.axaml")));
        var chatViewXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatView.axaml"));
        Assert.Contains("StrataChatShell.flat-window /template/ Border#PART_Root", chatViewXaml);
        Assert.Contains("StrataChatShell.flat-window /template/ Border#PART_HeaderChrome", chatViewXaml);
        Assert.DoesNotContain("x:Name=\"BrowserIsland\"", chatWindowXaml);
        Assert.DoesNotContain("x:Name=\"DiffIsland\"", chatWindowXaml);
        Assert.DoesNotContain("x:Name=\"PlanIsland\"", chatWindowXaml);
    }

    [Fact]
    public void ChatViewKeepsCodingBranchSlotVisibleForCodingProjects()
    {
        var root = FindRepoRoot();
        var chatViewXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatView.axaml"));

        Assert.Contains("IsVisible=\"{Binding IsCodingProject}\"", chatViewXaml);
        Assert.Contains("Text=\"{Binding GitBranchLabel}\"", chatViewXaml);
    }

    [Fact]
    public void MainWindowChatListDoubleClickUsesDetachedWindowCommand()
    {
        var root = FindRepoRoot();
        var mainWindowCode = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("OnChatListPointerPressed", mainWindowCode);
        Assert.Contains("IsChatListDoubleClick(chat, point.Position, e)", mainWindowCode);
        Assert.Contains("ChatListHandlersAttachedProperty", mainWindowCode);
        Assert.Contains("OpenChatInNewWindowCommand.Execute(chat)", mainWindowCode);
    }

    [Fact]
    public async Task MainWindowChatListDoubleClickRequestsDetachedWindow()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat
            {
                Title = "Open by double click",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var data = CreateAppData();
            data.Settings.IsOnboarded = true;
            data.Chats.Add(chat);
            var dataStore = new DataStore(data);
            var viewModel = new MainViewModel(
                dataStore,
                new CopilotService(),
                new UpdateService(),
                startBackgroundJobs: false);
            DetachedChatWindowRequest? request = null;
            viewModel.OpenChatWindowRequested += requested => request = requested;

            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = 1100,
                Height = 820,
            };

            window.Show();
            try
            {
                await PumpAsync();

                var listItem = window.GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .First(item => ReferenceEquals(item.DataContext, chat));
                var topLeft = listItem.TranslatePoint(new Point(0, 0), window)
                    ?? throw new InvalidOperationException("Chat list item is not attached.");
                var point = topLeft + new Point(listItem.Bounds.Width / 2, listItem.Bounds.Height / 2);

                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                await Task.Delay(80);
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();

                Assert.Same(chat, request?.Chat);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
                request?.WindowVM.Dispose();
                request?.WindowVM.ChatVM.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void BrowserViewReparentsNativeWebViewBeforeBoundsUpdates()
    {
        var root = FindRepoRoot();
        var browserViewCode = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "BrowserView.axaml.cs"));
        var browserServiceCode = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Services", "BrowserService.cs"));

        Assert.Contains("_browserService.SetParentHwnd(platformHandle.Handle)", browserViewCode);
        Assert.Contains("_controller.ParentWindow = hwnd", browserServiceCode);
        Assert.Contains("_controller.NotifyParentWindowPositionChanged()", browserServiceCode);
        Assert.Contains("_webViewHwnd = IntPtr.Zero", browserServiceCode);
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

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
