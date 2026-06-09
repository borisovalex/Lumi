using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services;

/// <summary>
/// Lightweight git operations helper. All methods are static and shell out to git CLI.
/// </summary>
public static class GitService
{
    private static readonly TimeSpan DefaultGitCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorktreeGitCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TimedOutGitCleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Returns true if the directory is inside a git repository.</summary>
    public static bool IsGitRepo(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        // Quick check: .git folder exists at root or any parent
        var d = new DirectoryInfo(dir);
        while (d is not null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                return true;
            d = d.Parent;
        }
        return false;
    }

    /// <summary>Gets the current branch name, or null if not a git repo.</summary>
    public static async Task<string?> GetCurrentBranchAsync(string dir)
    {
        var result = NormalizeBranchName(await RunGitAsync(dir, "branch --show-current").ConfigureAwait(false));
        if (result is not null)
            return result;

        result = NormalizeBranchName(await RunGitAsync(dir, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false));
        if (result is not null)
            return result;

        result = ParseStatusBranch(await RunGitAsync(dir, "status --short --branch").ConfigureAwait(false));
        if (result is not null)
            return result;

        var shortSha = (await RunGitAsync(dir, "rev-parse --short HEAD").ConfigureAwait(false))?.Trim();
        return string.IsNullOrWhiteSpace(shortSha) ? null : $"Detached {shortSha}";
    }

    /// <summary>Returns the list of changed files (staged + unstaged + untracked) with line stats.</summary>
    public static async Task<List<GitFileChange>> GetChangedFilesAsync(string dir)
    {
        var output = await RunGitAsync(dir, "status --porcelain -uall").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output)) return [];

        var changes = new List<GitFileChange>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var status = line[..2];
            var path = line[3..].Trim().Trim('"');

            var kind = status.Trim() switch
            {
                "M" or "MM" => GitChangeKind.Modified,
                "A" or "AM" => GitChangeKind.Added,
                "D" => GitChangeKind.Deleted,
                "R" or "RM" => GitChangeKind.Renamed,
                "??" => GitChangeKind.Untracked,
                _ => GitChangeKind.Modified
            };

            var fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));

            // Skip worktree sibling directories (they appear as untracked in some configs)
            if (kind == GitChangeKind.Untracked && path.Contains("-wt-"))
                continue;

            changes.Add(new GitFileChange
            {
                RelativePath = path,
                FullPath = fullPath,
                Kind = kind,
                StatusCode = status
            });
        }

        // Enrich with line stats from numstat
        var numstat = await RunGitAsync(dir, "diff --numstat").ConfigureAwait(false);
        var cachedNumstat = await RunGitAsync(dir, "diff --cached --numstat").ConfigureAwait(false);
        var statsMap = new Dictionary<string, (int added, int removed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in new[] { numstat, cachedNumstat })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var sline in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = sline.Split('\t');
                if (parts.Length < 3) continue;
                if (int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var r))
                {
                    var fpath = parts[2];
                    if (statsMap.TryGetValue(fpath, out var existing))
                        statsMap[fpath] = (existing.added + a, existing.removed + r);
                    else
                        statsMap[fpath] = (a, r);
                }
            }
        }
        foreach (var c in changes)
        {
            if (statsMap.TryGetValue(c.RelativePath, out var stats))
            {
                c.LinesAdded = stats.added;
                c.LinesRemoved = stats.removed;
            }
            else if (c.Kind is GitChangeKind.Untracked or GitChangeKind.Added)
            {
                // Untracked/new files don't appear in numstat — count lines directly
                try
                {
                    if (File.Exists(c.FullPath))
                        c.LinesAdded = File.ReadLines(c.FullPath).Count();
                }
                catch { /* ignore */ }
            }
        }

        return changes;
    }

    /// <summary>Gets the unified diff for a specific file.</summary>
    public static async Task<string?> GetFileDiffAsync(string dir, string relativePath)
    {
        // Try staged first, then unstaged, then for untracked show the whole file
        var diff = await RunGitAsync(dir, $"diff -- \"{relativePath}\"").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(diff))
            diff = await RunGitAsync(dir, $"diff --cached -- \"{relativePath}\"").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(diff) ? null : diff;
    }

    /// <summary>Gets the short stat summary (e.g. "3 files changed, 12 insertions(+), 5 deletions(-)").</summary>
    public static async Task<string?> GetDiffStatAsync(string dir)
    {
        return await RunGitAsync(dir, "diff --stat --stat-width=60").ConfigureAwait(false);
    }

    /// <summary>Creates a git worktree as a sibling directory to the repo. Returns the worktree path.</summary>
    public static async Task<string?> CreateWorktreeAsync(string repoDir, string branchName)
    {
        // Place worktree as sibling: E:\Git\Lumi → E:\Git\Lumi-worktree-abc123
        var trimmedRepoDir = repoDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoName = Path.GetFileName(trimmedRepoDir);
        var safeBranch = branchName.Replace('/', '-').Replace('\\', '-');
        var parentDir = Path.GetDirectoryName(trimmedRepoDir);
        if (parentDir is null) return null;

        var worktreePath = Path.Combine(parentDir, $"{repoName}-wt-{safeBranch}");
        if (Directory.Exists(worktreePath))
            return worktreePath; // Already exists

        // Try creating with a new branch first
        var result = await RunGitAsync(repoDir, $"worktree add \"{worktreePath}\" -b \"{branchName}\"", WorktreeGitCommandTimeout).ConfigureAwait(false);
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        // Branch may already exist — try attaching to it
        result = await RunGitAsync(repoDir, $"worktree add \"{worktreePath}\" \"{branchName}\"", WorktreeGitCommandTimeout).ConfigureAwait(false);
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        // Last resort — create with detached HEAD
        result = await RunGitAsync(repoDir, $"worktree add --detach \"{worktreePath}\"", WorktreeGitCommandTimeout).ConfigureAwait(false);
        if (result is not null && Directory.Exists(worktreePath))
            return worktreePath;

        return null;
    }

    /// <summary>Removes a git worktree and its associated branch.</summary>
    public static async Task<bool> RemoveWorktreeAsync(string dir, string worktreePath)
    {
        if (!Directory.Exists(worktreePath)) return true;

        // Get the branch name before removing the worktree
        var branch = await RunGitAsync(worktreePath, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
        branch = branch?.Trim();

        var result = await RunGitAsync(dir, $"worktree remove \"{worktreePath}\" --force").ConfigureAwait(false);
        if (result is null) return false;

        // Delete the orphaned branch if it was a lumi/ branch
        if (branch is { Length: > 0 } && branch.StartsWith("lumi/"))
            await RunGitAsync(dir, $"branch -D \"{branch}\"").ConfigureAwait(false);

        return true;
    }

    /// <summary>Lists existing worktrees.</summary>
    public static async Task<List<string>> ListWorktreesAsync(string dir)
    {
        var output = await RunGitAsync(dir, "worktree list --porcelain").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output)) return [];

        return output.Split('\n')
            .Where(l => l.StartsWith("worktree "))
            .Select(l => l[9..].Trim())
            .ToList();
    }

    /// <summary>Lists existing worktrees with their branch names.</summary>
    public static async Task<List<WorktreeInfo>> ListWorktreeInfoAsync(string dir)
    {
        var output = await RunGitAsync(dir, "worktree list --porcelain").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output)) return [];

        var results = new List<WorktreeInfo>();
        string? currentPath = null;
        string? currentBranch = null;
        bool isBare = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("worktree "))
            {
                // Save previous entry
                if (currentPath is not null && !isBare)
                    results.Add(new WorktreeInfo(currentPath, currentBranch));

                currentPath = line[9..].Trim();
                currentBranch = null;
                isBare = false;
            }
            else if (line.StartsWith("branch "))
            {
                // branch refs/heads/main → main
                var refName = line[7..].Trim();
                currentBranch = refName.StartsWith("refs/heads/")
                    ? refName["refs/heads/".Length..]
                    : refName;
            }
            else if (line == "bare")
            {
                isBare = true;
            }
        }

        // Save last entry
        if (currentPath is not null && !isBare)
            results.Add(new WorktreeInfo(currentPath, currentBranch));

        return results;
    }

    private static async Task<string?> RunGitAsync(string workDir, string args, TimeSpan? timeout = null)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultGitCommandTimeout);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(proc);
                await WaitForExitQuietlyAsync(proc, TimedOutGitCleanupTimeout).ConfigureAwait(false);
                await DrainOutputQuietlyAsync(stdoutTask, stderrTask, TimedOutGitCleanupTimeout).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[Lumi] Git command timed out after {(timeout ?? DefaultGitCommandTimeout).TotalSeconds:N0}s: git {args}");
                return null;
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var output = stdoutTask.Result;
            return proc.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Git command failed: git {args} ({ex.Message})");
            return null;
        }
    }

    private static void TryKillProcessTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Failed to kill timed-out git process {proc.Id}: {ex.Message}");
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process proc, TimeSpan timeout)
    {
        try
        {
            var waitTask = proc.WaitForExitAsync();
            if (await Task.WhenAny(waitTask, Task.Delay(timeout)).ConfigureAwait(false) == waitTask)
                await waitTask.ConfigureAwait(false);
            else
                System.Diagnostics.Debug.WriteLine($"[Lumi] Timed out waiting for killed git process {proc.Id} to exit.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Failed waiting for timed-out git process {proc.Id}: {ex.Message}");
        }
    }

    private static async Task DrainOutputQuietlyAsync(Task<string> stdoutTask, Task<string> stderrTask, TimeSpan timeout)
    {
        var drainTask = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            if (await Task.WhenAny(drainTask, Task.Delay(timeout)).ConfigureAwait(false) == drainTask)
                await drainTask.ConfigureAwait(false);
            else
            {
                ObserveFault(stdoutTask);
                ObserveFault(stderrTask);
                System.Diagnostics.Debug.WriteLine("[Lumi] Timed out draining killed git process output.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lumi] Failed draining timed-out git output: {ex.Message}");
        }
    }

    private static void ObserveFault(Task<string> task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string? NormalizeBranchName(string? value)
    {
        var branch = value?.Trim();
        return string.IsNullOrWhiteSpace(branch) || string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase)
            ? null
            : branch;
    }

    private static string? ParseStatusBranch(string? statusOutput)
    {
        if (string.IsNullOrWhiteSpace(statusOutput))
            return null;

        var firstLine = statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim();
        if (firstLine is null || !firstLine.StartsWith("## ", StringComparison.Ordinal))
            return null;

        var branch = firstLine[3..].Trim();
        const string noCommitsPrefix = "No commits yet on ";
        if (branch.StartsWith(noCommitsPrefix, StringComparison.OrdinalIgnoreCase))
            return NormalizeBranchName(branch[noCommitsPrefix.Length..]);

        var upstreamIndex = branch.IndexOf("...", StringComparison.Ordinal);
        if (upstreamIndex >= 0)
            branch = branch[..upstreamIndex];
        var detailIndex = branch.IndexOf(' ');
        if (detailIndex >= 0)
            branch = branch[..detailIndex];

        return NormalizeBranchName(branch);
    }
}

public enum GitChangeKind { Modified, Added, Deleted, Renamed, Untracked }

/// <summary>Represents a git worktree with its path and branch name.</summary>
public record WorktreeInfo(string Path, string? Branch)
{
    /// <summary>Display name: branch name if available, otherwise the directory name.</summary>
    public string DisplayName => Branch ?? System.IO.Path.GetFileName(Path);
}

public class GitFileChange
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required GitChangeKind Kind { get; init; }
    public required string StatusCode { get; init; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }

    public string FileName => Path.GetFileName(RelativePath);
    public string? Directory => Path.GetDirectoryName(RelativePath)?.Replace('\\', '/');

    public string KindIcon => Kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Untracked => "U",
        _ => "?"
    };

    public string KindLabel => Kind switch
    {
        GitChangeKind.Modified => "Modified",
        GitChangeKind.Added => "Added",
        GitChangeKind.Deleted => "Deleted",
        GitChangeKind.Renamed => "Renamed",
        GitChangeKind.Untracked => "Untracked",
        _ => "Unknown"
    };
}
