using System;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using StrataTheme.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// Objective shape/placement assessor for the resting (Idle) aura, using the same Skia headless
/// real-pixel capture the motion tests rely on (composition <c>Offset</c>/<c>Scale</c> ARE applied,
/// unlike MCP/OS screenshots). Measures WHERE the field pools and HOW WIDE vs tall it sits when aimed
/// at the composer — the two qualities behind "the idle aura should spread along the composer".
/// </summary>
[Collection("Headless UI")]
public sealed class PresenceAuraShapeTests
{
    private readonly ITestOutputHelper _out;

    public PresenceAuraShapeTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public void IdleAura_PoolsLowAndSpreadsHorizontally()
    {
        HeadlessUnitTestSession? session = null;
        string? skipReason = null;
        try
        {
            session = HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);
        }
        catch (Exception ex)
        {
            skipReason = $"Skia headless session unavailable: {ex.Message}";
        }

        Skip.If(session is null, skipReason ?? "Skia headless session unavailable.");

        Box full = default, core = default;
        var rendered = false;
        int width = 0, height = 0;

        try
        {
            session!.Dispatch(() =>
            {
                var res = Application.Current!.Resources;
                res["Color.AccentDefault"] = Color.FromRgb(120, 110, 245);
                res["Color.AccentViolet"] = Color.FromRgb(160, 100, 230);
                res["Color.AccentRose"] = Color.FromRgb(230, 110, 170);
                res["Palette.Warning400"] = Color.FromRgb(235, 175, 90);
                res["Palette.Success400"] = Color.FromRgb(90, 210, 140);
                res["Palette.Accent400"] = Color.FromRgb(110, 160, 240);
                res["Palette.Danger400"] = Color.FromRgb(235, 90, 90);

                var presence = new StrataPresence
                {
                    State = PresenceState.Idle, // existing-chat resting state (Calm motion => WidthBias)
                    Intensity = 3.0,            // lift the (dim) idle ambient so the centroid has signal
                    FocusReach = 1.0,
                    FocusPoint = new Point(0.5, 0.86), // the composer anchor the controller uses when idle
                };

                // A realistic chat-canvas aspect (taller-than-square, like the live 1347x931 workspace).
                var window = new Window
                {
                    Width = 1200,
                    Height = 840,
                    Background = Brushes.White,
                    Content = presence,
                };
                window.Show();

                for (int i = 0; i < 8; i++)
                    Tick(40); // attach + InitComposition + initial focus snap

                // Let the spring settle at the composer anchor.
                for (int i = 0; i < 40; i++)
                    Tick(24);

                var frame = window.CaptureRenderedFrame();
                rendered = frame is not null;
                if (!rendered)
                    return;

                width = frame!.PixelSize.Width;
                height = frame.PixelSize.Height;
                full = Measure(frame, darkThreshold: 24);
                core = Measure(frame, darkThreshold: 70);
                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            session?.Dispose();
        }

        Skip.IfNot(rendered, "Skia headless capture returned no rendered glow (drawing-free platform).");
        Skip.If(full.Weight <= 0, "No aura pixels measured.");

        string Pct(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture);
        _out.WriteLine($"canvas={width}x{height}");
        _out.WriteLine($"FULL  centroid=({Pct(full.Cx / width)}%, {Pct(full.Cy / height)}%) " +
                       $"bbox W={Pct(full.W / width)}% H={Pct(full.H / height)}% aspect={(full.W / full.H):F2}");
        _out.WriteLine($"CORE  centroid=({Pct(core.Cx / width)}%, {Pct(core.Cy / height)}%) " +
                       $"bbox W={Pct(core.W / width)}% H={Pct(core.H / height)}% aspect={(core.W / core.H):F2}");

        // Assertions encode Adir's two asks for the idle aura:
        // 1) it pools LOW (toward the composer) rather than mid-canvas, and
        // 2) it reads WIDER than tall (light spreading ALONG the composer).
        Assert.True(core.Cy / height >= 0.55,
            $"Idle aura core pools too high ({Pct(core.Cy / height)}% of canvas) — it should settle low toward the composer.");
        Assert.True(full.W / full.H >= 1.6,
            $"Idle aura is not horizontally spread (aspect {(full.W / full.H):F2}) — it should read as a broad wash wider than tall.");
    }

    [SkippableFact]
    public void EmitLean_IsGentle_AndSettlesWithoutBouncingBack()
    {
        HeadlessUnitTestSession? session = null;
        string? skipReason = null;
        try
        {
            session = HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);
        }
        catch (Exception ex)
        {
            skipReason = $"Skia headless session unavailable: {ex.Message}";
        }

        Skip.If(session is null, skipReason ?? "Skia headless session unavailable.");

        var samples = new System.Collections.Generic.List<(double Ms, double Cx)>();
        var rendered = false;
        int width = 0;

        try
        {
            session!.Dispatch(() =>
            {
                var res = Application.Current!.Resources;
                res["Color.AccentDefault"] = Color.FromRgb(120, 110, 245);
                res["Color.AccentViolet"] = Color.FromRgb(160, 100, 230);
                res["Color.AccentRose"] = Color.FromRgb(230, 110, 170);
                res["Palette.Warning400"] = Color.FromRgb(235, 175, 90);
                res["Palette.Success400"] = Color.FromRgb(90, 210, 140);
                res["Palette.Accent400"] = Color.FromRgb(110, 160, 240);
                res["Palette.Danger400"] = Color.FromRgb(235, 90, 90);

                var presence = new StrataPresence
                {
                    State = PresenceState.Idle,
                    Intensity = 3.0,
                    FocusReach = 1.0,
                    FocusPoint = new Point(0.5, 0.74),
                };
                var window = new Window { Width = 1200, Height = 840, Background = Brushes.White, Content = presence };
                window.Show();

                for (int i = 0; i < 8; i++)
                    Tick(40);
                for (int i = 0; i < 24; i++)
                    Tick(24); // settle at rest

                var rest = Measure(window.CaptureRenderedFrame(), 24);
                width = rest.Cx > 0 ? 1200 : 0;
                rendered = rest.Weight > 0;
                if (!rendered)
                    return;
                samples.Add((0, rest.Cx));

                presence.Emit(PresenceEdge.Right); // the island-open lean

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 22; i++) // ~1.4s of the 1.28s Emit, sampled by the (slow) capture itself
                {
                    Tick(8);
                    var b = Measure(window.CaptureRenderedFrame(), 24);
                    if (b.Weight > 0)
                        samples.Add((sw.Elapsed.TotalMilliseconds, b.Cx));
                }
                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            session?.Dispose();
        }

        Skip.IfNot(rendered, "Skia headless capture returned no rendered glow (drawing-free platform).");
        Skip.If(samples.Count < 6, "Too few Emit samples captured.");

        double rest0 = samples[0].Cx;
        double peak = double.MinValue;
        int peakIdx = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Cx > peak)
            {
                peak = samples[i].Cx;
                peakIdx = i;
            }
        }
        double peakShift = peak - rest0;
        // Lowest position reached AFTER the peak — a bounce would swing left, past rest.
        double postPeakMin = double.MaxValue;
        for (int i = peakIdx; i < samples.Count; i++)
            postPeakMin = Math.Min(postPeakMin, samples[i].Cx);
        double overshoot = rest0 - postPeakMin; // >0 means it bounced left of rest
        double endShift = samples[^1].Cx - rest0;

        foreach (var (ms, cx) in samples)
            _out.WriteLine($"t={ms,7:F0}ms  Cx={cx:F1}  d={cx - rest0,7:F1}");
        _out.WriteLine($"rest={rest0:F1}  peakShift={peakShift:F1}px  overshoot(left of rest)={overshoot:F1}px  endShift={endShift:F1}px");

        // (1) The field DID lean toward the seam (the gesture is visible).
        Assert.True(peakShift > 3, $"Emit produced no visible lean (peakShift {peakShift:F1}px).");
        // (2) It SETTLES back home — does not bounce past rest to the opposite side. This is the polish fix:
        //     a decaying lean+settle, not an out-and-back swing that overshoots.
        Assert.True(overshoot <= Math.Max(6.0, 0.18 * peakShift),
            $"Emit bounced back past rest by {overshoot:F1}px (peakShift {peakShift:F1}px) — it should settle, not swing.");
        // (3) By the end it has returned close to rest (no residual displacement).
        Assert.True(Math.Abs(endShift) <= Math.Max(10.0, 0.30 * peakShift),
            $"Emit did not settle back to rest (endShift {endShift:F1}px).");
    }

    private readonly record struct Box(double Cx, double Cy, double W, double H, double Weight);

    /// <summary>Darkness-weighted centroid + bounding box of the glow (coloured lobes are darker than the
    /// white background), at a given darkness threshold. Higher threshold isolates the bright core.</summary>
    private static unsafe Box Measure(Bitmap bmp, double darkThreshold)
    {
        if (bmp is not WriteableBitmap wb)
        {
            // CaptureRenderedFrame returns a WriteableBitmap; guard defensively.
            return default;
        }

        using var fb = wb.Lock();
        int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
        double sx = 0, sy = 0, sw = 0;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        byte* p = (byte*)fb.Address;
        for (int y = 0; y < h; y++)
        {
            byte* row = p + y * stride;
            for (int x = 0; x < w; x++)
            {
                byte* px = row + x * 4;
                double dark = 255.0 - (px[0] + px[1] + px[2]) / 3.0;
                if (dark > darkThreshold)
                {
                    sx += x * dark;
                    sy += y * dark;
                    sw += dark;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (sw <= 0)
            return default;
        return new Box(sx / sw, sy / sw, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY), sw);
    }

    private static void Tick(int realMs)
    {
        if (realMs > 0)
            Thread.Sleep(realMs);
        try
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        catch
        {
            // Render timer not available on this platform variant; the dispatcher pump still advances.
        }
        Dispatcher.UIThread.RunJobs();
    }
}
