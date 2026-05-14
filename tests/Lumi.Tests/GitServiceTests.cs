using System;
using System.IO;
using System.Threading.Tasks;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class GitServiceTests
{
    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsRepositoryHeadLabel()
    {
        var branch = await GitService.GetCurrentBranchAsync(FindRepoRoot());

        Assert.False(string.IsNullOrWhiteSpace(branch));
        Assert.NotEqual("Git", branch);
    }

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
