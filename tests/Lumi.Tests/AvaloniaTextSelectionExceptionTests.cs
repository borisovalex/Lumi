using System;
using Xunit;

namespace Lumi.Tests;

public sealed class AvaloniaTextSelectionExceptionTests
{
    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_MatchesSelectionHandleCrash()
    {
        var exception = CaptureSelectionHandleCrash();

        Assert.True(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_MatchesSelectableTextBlockRenderCrash()
    {
        var exception = CaptureSelectableTextBlockRenderCrash();

        Assert.True(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    [Fact]
    public void IsAvaloniaTextSelectionBoundsFailure_IgnoresUnrelatedInvalidOperation()
    {
        var exception = new InvalidOperationException("Covered length must be greater than zero.");

        Assert.False(Program.IsAvaloniaTextSelectionBoundsFailure(exception));
    }

    private static Exception CaptureSelectionHandleCrash()
    {
        try
        {
            TextSelectionHandleCanvas.Render();
            throw new InvalidOperationException("Expected simulated crash.");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static Exception CaptureSelectableTextBlockRenderCrash()
    {
        try
        {
            SelectableTextBlock.RenderTextLayout();
            throw new InvalidOperationException("Expected simulated crash.");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static class TextSelectionHandleCanvas
    {
        public static void Render() => TextLayout.HitTestTextRange();
    }

    private static class SelectableTextBlock
    {
        public static void RenderTextLayout() => TextLayout.HitTestTextRange();
    }

    private static class TextLayout
    {
        public static void HitTestTextRange() =>
            throw new InvalidOperationException("Covered length must be greater than zero.");
    }
}
