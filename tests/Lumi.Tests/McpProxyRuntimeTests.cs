using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using GitHub.Copilot.SDK;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class McpProxyRuntimeTests
{
    [SkippableFact]
    public async Task Proxy_StartsStdioServerLazilyAndReusesSingletonProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "proxy-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "emit_marker"; description = "Emit marker"; inputSchema = @{ type = "object"; properties = @{ value = @{ type = "string" } }; required = @("value") } }) } }
                    } elseif ($msg.method -eq "tools/call") {
                        $value = [string]$msg.params.arguments.value
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "PROXY_MARKER:$value" }) } }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var remote = runtime.Register(new McpProxyServerDefinition(
                "test:proxy",
                "proxy-test",
                new McpStdioServerConfig
                {
                    Command = GetPowerShellPath(),
                    Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    Cwd = root,
                    Env = new Dictionary<string, string>
                    {
                        ["MCP_TEST_LOG"] = logPath
                    },
                    Tools = ["*"]
                }));

            Assert.StartsWith("http://127.0.0.1:", remote.Url, StringComparison.Ordinal);
            Assert.False(File.Exists(logPath));

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.Equal("init", initialize.RootElement.GetProperty("id").GetString());
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            using var list = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}
                """);
            Assert.Equal("list", list.RootElement.GetProperty("id").GetString());
            Assert.Equal("emit_marker", list.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());

            using var call = await PostJsonAsync(http, remote.Url, """
                {"jsonrpc":"2.0","id":"call","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"OK"}}}
                """);
            Assert.Equal("call", call.RootElement.GetProperty("id").GetString());
            var text = call.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal("PROXY_MARKER:OK", text);

            var starts = await File.ReadAllLinesAsync(logPath);
            Assert.Single(starts);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task Proxy_ReplacesChangedRegistrationAfterInFlightRequestCompletes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PowerShell fake MCP server is Windows-only.");

        var root = Path.Combine(Path.GetTempPath(), "lumi-mcp-proxy-replace-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "fake-mcp.ps1");
            var logPath = Path.Combine(root, "starts.log");
            var callStartedPath = Path.Combine(root, "call-started.log");
            await File.WriteAllTextAsync(scriptPath, """
                [System.IO.File]::AppendAllText($env:MCP_TEST_LOG, "$($env:MCP_MARKER)|$PID`n")
                function Write-Json($obj) {
                    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 30))
                    [Console]::Out.Flush()
                }
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    $msg = $line | ConvertFrom-Json
                    if ($msg.method -eq "initialize") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ protocolVersion = "2025-06-18"; capabilities = @{ tools = @{ listChanged = $false } }; serverInfo = @{ name = "replace-test-mcp"; version = "1" } } }
                    } elseif ($msg.method -eq "tools/list") {
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ tools = @(@{ name = "emit_marker"; description = "Emit marker"; inputSchema = @{ type = "object"; properties = @{ value = @{ type = "string" } }; required = @("value") } }) } }
                    } elseif ($msg.method -eq "tools/call") {
                        $value = [string]$msg.params.arguments.value
                        [System.IO.File]::AppendAllText($env:MCP_CALL_STARTED, "$($env:MCP_MARKER)|$value`n")
                        if ($value -eq "SLOW") { Start-Sleep -Milliseconds 800 }
                        Write-Json @{ jsonrpc = "2.0"; id = $msg.id; result = @{ content = @(@{ type = "text"; text = "$($env:MCP_MARKER):$value" }) } }
                    }
                }
                """);

            await using var runtime = new McpProxyRuntime();
            var first = runtime.Register(CreateReplaceDefinition("test:replace", "replace-test", scriptPath, root, logPath, callStartedPath, "FIRST"));

            using var http = new HttpClient();
            using var initialize = await PostJsonAsync(http, first.Url, """
                {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            var inFlightCall = PostJsonAsync(http, first.Url, """
                {"jsonrpc":"2.0","id":"slow","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"SLOW"}}}
                """);
            await WaitForFileAsync(callStartedPath);

            var second = runtime.Register(CreateReplaceDefinition("test:replace", "replace-test", scriptPath, root, logPath, callStartedPath, "SECOND"));
            Assert.Equal(first.Url, second.Url);

            using var firstCall = await inFlightCall;
            var firstText = firstCall.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal("FIRST:SLOW", firstText);

            using var secondInitialize = await PostJsonAsync(http, second.Url, """
                {"jsonrpc":"2.0","id":"second-init","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
                """);
            Assert.True(secondInitialize.RootElement.TryGetProperty("result", out _));

            using var secondCall = await PostJsonAsync(http, second.Url, """
                {"jsonrpc":"2.0","id":"second-call","method":"tools/call","params":{"name":"emit_marker","arguments":{"value":"OK"}}}
                """);
            var secondText = secondCall.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal("SECOND:OK", secondText);

            var starts = await File.ReadAllLinesAsync(logPath);
            Assert.Contains(starts, line => line.StartsWith("FIRST|", StringComparison.Ordinal));
            Assert.Contains(starts, line => line.StartsWith("SECOND|", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient http, string url, string json)
    {
        using var response = await http.PostAsync(
            url,
            new StringContent(json, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private static McpProxyServerDefinition CreateReplaceDefinition(
        string key,
        string name,
        string scriptPath,
        string root,
        string logPath,
        string callStartedPath,
        string marker)
        => new(
            key,
            name,
            new McpStdioServerConfig
            {
                Command = GetPowerShellPath(),
                Args = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                Cwd = root,
                Env = new Dictionary<string, string>
                {
                    ["MCP_TEST_LOG"] = logPath,
                    ["MCP_CALL_STARTED"] = callStartedPath,
                    ["MCP_MARKER"] = marker
                },
                Tools = ["*"]
            });

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
                return;

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting for {path}.");
    }

    private static string GetPowerShellPath()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(path), $"PowerShell not found at {path}");
        return path;
    }
}
