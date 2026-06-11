using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotAuthenticationTests
{
    [Fact]
    public void ParseStoredCopilotIdentity_SupportsCliConfigCommentsAndCamelCaseKey()
    {
        var identity = CopilotService.ParseStoredCopilotIdentity("""
            // User settings belong in settings.json.
            {
              "lastLoggedInUser": {
                "host": "https://github.com",
                "login": "octocat"
              },
            }
            """);

        Assert.Equal("octocat", identity.Login);
        Assert.Equal("https://github.com", identity.Host);
    }

    [Theory]
    // Verbatim mid-session 401s observed in ~/.copilot/logs/process-*.log (CLI 1.0.60):
    [InlineData("401 unauthorized: authenticating token: twirp error internal: twirp error internal: failed to do request: Post \"http://usersd.usersd-production.svc.cluster.local:8080\"")]
    [InlineData("401 unauthorized: unauthorized: AuthenticateToken authentication failed")]
    [InlineData("CAPIError: 401 401 401 unauthorized: authenticating token: twirp error internal")]
    [InlineData("503 service unavailable: 401 unauthorized")]
    [InlineData("401 Unauthorized — backend temporarily unavailable")]
    public void IsTransientServerAuthError_TreatsServerSide401sAsRetryable(string error)
        => Assert.True(CopilotService.IsTransientServerAuthError(error));

    [Theory]
    // Genuine credential failures and unrelated errors must NOT be retried in a loop:
    [InlineData("401 unauthorized: Bad credentials")]
    [InlineData("403 Forbidden: token does not have the required scopes")]
    [InlineData("Session not found")]
    [InlineData("The operation was canceled.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsTransientServerAuthError_DoesNotRetryGenuineOrUnrelatedFailures(string? error)
        => Assert.False(CopilotService.IsTransientServerAuthError(error));
}
