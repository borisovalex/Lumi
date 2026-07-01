using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;
using Lumi.Views;
using Lumi.Views.Controls;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class TranscriptTextContentHeadlessTests
{
    [Fact]
    public async Task StreamingMarkdown_UsesPlainTextUntilStreamingEnds()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var control = new TranscriptTextContent
            {
                Text = "## Heading\n\n- item",
                PreferPlainText = true
            };

            Assert.IsType<TextBlock>(control.Content);

            control.PreferPlainText = false;

            Assert.IsType<StrataMarkdown>(control.Content);

            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StreamingReasoningMarkdown_CanRenderMarkdownWhenEnabled()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var control = new TranscriptTextContent
            {
                Text = "## Heading\n\n- item",
                PreferPlainText = true,
                RenderMarkdownWhileStreaming = true
            };

            Assert.IsType<StrataMarkdown>(control.Content);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StreamingMarkdown_HeadingFollowedByBody_RendersSeparateHeadingAndParagraph()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            StrataMarkdown.ResetDiagnostics();

            var markdown = new StrataMarkdown
            {
                Markdown = "## Heading",
                IsInline = true
            };

            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = markdown
            };

            window.Show();
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            markdown.Markdown = "## Heading\n\nBody text";

            await Task.Delay(100);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var textBlocks = markdown.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
            var heading = Assert.Single(textBlocks, tb => tb.Classes.Contains("strata-md-heading"));
            var body = Assert.Single(textBlocks, tb => tb.Text == "Body text");

            Assert.Equal("Heading", heading.Text);
            Assert.Contains("strata-md-paragraph", body.Classes);
            Assert.DoesNotContain("strata-md-heading", body.Classes);
            Assert.True(heading.FontSize > body.FontSize);
            Assert.True(StrataMarkdown.CaptureDiagnostics().IncrementalParseCount > 0);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssistantTemplate_RendersMarkdownWhileStreaming()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chatView = new ChatView();
            var assistantMessage = new ChatMessage
            {
                Role = "assistant",
                Content = "## Heading\n\nBody text",
                IsStreaming = true
            };

            var assistantItem = new AssistantMessageItem(new ChatMessageViewModel(assistantMessage), showTimestamps: false);
            var template = chatView.DataTemplates.FirstOrDefault(candidate => candidate.Match(assistantItem));

            Assert.NotNull(template);

            var control = Assert.IsAssignableFrom<Control>(template!.Build(assistantItem));
            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = control
            };

            window.Show();
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var textContent = control.GetVisualDescendants().OfType<TranscriptTextContent>().Single();

            Assert.True(textContent.PreferPlainText);
            Assert.True(textContent.RenderMarkdownWhileStreaming);
            Assert.IsType<StrataMarkdown>(textContent.Content);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReasoningTemplate_RendersMarkdownWhileStreaming()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var chatView = new ChatView();
            var reasoningMessage = new ChatMessage
            {
                Role = "reasoning",
                Content = "**Reasoning**\n\n- first\n- second",
                IsStreaming = true
            };

            var reasoningItem = new ReasoningItem(new ChatMessageViewModel(reasoningMessage), expandWhileStreaming: true);
            var template = chatView.DataTemplates.FirstOrDefault(candidate => candidate.Match(reasoningItem));

            Assert.NotNull(template);

            var control = Assert.IsAssignableFrom<Control>(template!.Build(reasoningItem));
            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = control
            };

            window.Show();
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var textContent = control.GetVisualDescendants().OfType<TranscriptTextContent>().Single();

            Assert.True(textContent.PreferPlainText);
            Assert.True(textContent.RenderMarkdownWhileStreaming);
            Assert.IsType<StrataMarkdown>(textContent.Content);

            window.Close();
        }, CancellationToken.None);
    }
}
