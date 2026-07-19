using System.Diagnostics;
using Velopack.Locators;

namespace Lumi.Services;

internal interface IUpdateShutdownTerminator
{
    void Terminate();
}

internal sealed class VelopackUpdateShutdownTerminator : IUpdateShutdownTerminator
{
    private static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(5);

    public void Terminate()
    {
        using var forcedExitTimer = new Timer(
            static _ => Environment.Exit(0),
            null,
            ForcedExitTimeout,
            Timeout.InfiniteTimeSpan);
        try
        {
            var locator = VelopackLocator.Current;
            if (string.IsNullOrWhiteSpace(locator.UpdateExePath))
                throw new InvalidOperationException("Velopack did not provide its updater executable path.");

            var failedProcessIds = UpdateBlockerService.ForceCloseManagedChildProcessesForRestart(
                VelopackUpdatePathResolver.GetUpdateResourceRoots(),
                locator.UpdateExePath);
            if (failedProcessIds.Count > 0)
            {
                Trace.TraceWarning(
                    $"[UpdateService] Managed child processes still running during forced update shutdown: {string.Join(", ", failedProcessIds)}.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"[UpdateService] Failed to stop managed child processes during forced shutdown: {ex.Message}");
        }
        finally
        {
            Environment.Exit(0);
        }
    }
}

internal sealed class UpdateShutdownCoordinator
{
    internal static readonly TimeSpan VelopackExitTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DefaultGracefulCleanupTimeout = TimeSpan.FromSeconds(45);

    private readonly TimeSpan _gracefulCleanupTimeout;
    private readonly IUpdateShutdownTerminator _terminator;

    public UpdateShutdownCoordinator(
        TimeSpan? gracefulCleanupTimeout = null,
        IUpdateShutdownTerminator? terminator = null)
    {
        _gracefulCleanupTimeout = gracefulCleanupTimeout ?? DefaultGracefulCleanupTimeout;
        if (_gracefulCleanupTimeout <= TimeSpan.Zero
            || _gracefulCleanupTimeout >= VelopackExitTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gracefulCleanupTimeout),
                $"Update cleanup must finish before Velopack's {VelopackExitTimeout.TotalSeconds:0}-second exit timeout.");
        }

        _terminator = terminator ?? new VelopackUpdateShutdownTerminator();
    }

    public bool Run(Action synchronousCleanup, Func<Task> asynchronousCleanup)
    {
        ArgumentNullException.ThrowIfNull(synchronousCleanup);
        ArgumentNullException.ThrowIfNull(asynchronousCleanup);

        const int running = 0;
        const int terminating = 1;
        const int completed = 2;
        var state = running;
        using var terminationCompleted = new ManualResetEventSlim();

        void RequestTermination(string message)
        {
            if (Interlocked.CompareExchange(ref state, terminating, running) != running)
                return;

            Trace.TraceWarning(
                $"[UpdateService] {message} Forcing Lumi to exit so Velopack can continue.");
            try
            {
                _terminator.Terminate();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"[UpdateService] Forced update shutdown failed: {ex.Message}");
            }
            finally
            {
                terminationCompleted.Set();
            }
        }

        using var watchdog = new Timer(
            _ => RequestTermination("Update cleanup exceeded its deadline."),
            null,
            _gracefulCleanupTimeout,
            Timeout.InfiniteTimeSpan);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            synchronousCleanup();
            var remaining = _gracefulCleanupTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                RequestTermination("Synchronous update cleanup exceeded its deadline.");
                terminationCompleted.Wait();
                return false;
            }

            Task.Run(asynchronousCleanup).WaitAsync(remaining).GetAwaiter().GetResult();
            if (Interlocked.CompareExchange(ref state, completed, running) == running)
                return true;

            terminationCompleted.Wait();
            return false;
        }
        catch (TimeoutException)
        {
            RequestTermination("Update cleanup exceeded its deadline.");
        }
        catch (Exception ex)
        {
            RequestTermination($"Update cleanup failed: {ex.Message}");
        }

        terminationCompleted.Wait();
        return false;
    }
}
