using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using Velopack.Locators;

namespace Lumi.Services;

internal static class InstalledAppWorkingDirectory
{
    public static void Configure()
    {
        IVelopackLocator locator;
        try
        {
            locator = VelopackLocator.Current;
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"[Startup] Could not inspect the Velopack installation: {ex.Message}");
            return;
        }

        if (locator.CurrentlyInstalledVersion is null)
            return;

        var safeDirectory = GetSafeWorkingDirectory();
        try
        {
            Directory.CreateDirectory(safeDirectory);
            Directory.SetCurrentDirectory(safeDirectory);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or SecurityException)
        {
            Trace.TraceWarning(
                $"[Startup] Could not move the process working directory to '{safeDirectory}': {ex.Message}");
        }
    }

    internal static string GetSafeWorkingDirectory()
    {
        var appDataRoot = Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR");
        if (string.IsNullOrWhiteSpace(appDataRoot))
            appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appDataRoot))
            appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.GetFullPath(Path.Combine(
            Environment.ExpandEnvironmentVariables(appDataRoot),
            "Lumi",
            "copilot",
            "workspace"));
    }

    internal static bool IsPathInsideRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;

        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(normalizedPath, normalizedRoot, comparison)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
            || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
