using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ttasks.LiveE2ETests;

[Collection(LiveE2ECollection.Name)]
public sealed class AzureCapabilityLiveE2ETests : IClassFixture<LiveChatAppFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LiveChatAppFixture _app;

    public AzureCapabilityLiveE2ETests(LiveChatAppFixture app)
    {
        _app = app;
    }

    [LiveE2EFact]
    public async Task Azure_Audit_Prompt_Runs_MultiStep_Graph_Through_Real_App()
    {
        EnsureAzCliReady();
        var sessionId = $"live-az-audit-{Guid.NewGuid():N}";

        var chat = await PostChat(
            sessionId,
            """
            Using only Azure CLI capabilities, run a validated graph that:
            1) lists Azure subscriptions,
            2) lists resource groups in the active subscription,
            3) lists resources in the active subscription,
            then summarizes subscription count, resource group count, total resource count,
            and top resource types by count. Use the host-approved az account list,
            az group list, and az resource list capabilities.
            """);

        Assert.Equal("graph", chat.RootElement.GetProperty("mode").GetString());
        var graphId = chat.RootElement.GetProperty("graphId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(graphId));
        Assert.False(string.IsNullOrWhiteSpace(chat.RootElement.GetProperty("answer").GetString()));

        var commands = await GetGraphProcessCommands(graphId!);

        Assert.Contains(commands, command => StartsWithCommand(command, "az account list"));
        Assert.Contains(commands, command => StartsWithCommand(command, "az group list"));
        Assert.Contains(commands, command => StartsWithCommand(command, "az resource list"));
    }

    [LiveE2EFact]
    public async Task Disabled_Az_Resource_List_Is_Not_Executed_For_Same_Session()
    {
        EnsureAzCliReady();
        var sessionId = $"live-az-disable-{Guid.NewGuid():N}";
        var resourceCapability = await FindCapabilityByPrefix("az resource list");

        await PostChat(sessionId, "Answer directly with exactly: ready");

        try
        {
            await PostAdmin($"/api/admin/capabilities/{Uri.EscapeDataString(resourceCapability.Id)}/disable");

            var chat = await PostChat(
                sessionId,
                """
                Using Azure CLI capabilities, list Azure resources in the active subscription,
                then summarize total resource count. This requires az resource list if it is available.
                """);

            var graphId = chat.RootElement.TryGetProperty("graphId", out var graphIdElement)
                ? graphIdElement.GetString()
                : null;
            var commands = string.IsNullOrWhiteSpace(graphId)
                ? []
                : await GetGraphProcessCommands(graphId!);

            Assert.DoesNotContain(commands, command => StartsWithCommand(command, "az resource list"));
        }
        finally
        {
            await PostAdmin($"/api/admin/capabilities/{Uri.EscapeDataString(resourceCapability.Id)}/enable");
        }
    }

    private async Task<JsonDocument> PostChat(string sessionId, string message)
    {
        using var response = await _app.Client.PostAsJsonAsync("/api/chat", new { sessionId, message }, JsonOptions);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Chat request failed with HTTP {(int)response.StatusCode}.\nBody:\n{text}\nApp log:\n{_app.ReadLog()}");
        return JsonDocument.Parse(text);
    }

    private async Task PostAdmin(string path)
    {
        using var response = await _app.Client.PostAsync(path, content: null);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Admin request '{path}' failed with HTTP {(int)response.StatusCode}.\nBody:\n{text}\nApp log:\n{_app.ReadLog()}");
    }

    private async Task<CapabilityDto> FindCapabilityByPrefix(string prefix)
    {
        using var response = await _app.Client.GetAsync("/api/admin/capabilities");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Capabilities request failed with HTTP {(int)response.StatusCode}.\nBody:\n{text}");

        using var doc = JsonDocument.Parse(text);
        foreach (var capability in doc.RootElement.EnumerateArray())
        {
            if (string.Equals(capability.GetProperty("prefix").GetString(), prefix, StringComparison.Ordinal))
            {
                Assert.True(capability.GetProperty("enabled").GetBoolean(), $"Capability '{prefix}' should start enabled.");
                return new CapabilityDto(capability.GetProperty("id").GetString() ?? string.Empty, prefix);
            }
        }

        throw new InvalidOperationException($"Capability '{prefix}' was not found.\nCapabilities:\n{text}");
    }

    private async Task<IReadOnlyList<string>> GetGraphProcessCommands(string graphId)
    {
        using var graphResponse = await _app.Client.GetAsync($"/api/admin/graphs/{Uri.EscapeDataString(graphId)}");
        var graphText = await graphResponse.Content.ReadAsStringAsync();
        Assert.True(graphResponse.IsSuccessStatusCode, $"Graph request failed with HTTP {(int)graphResponse.StatusCode}.\nBody:\n{graphText}");

        using var graph = JsonDocument.Parse(graphText);
        var taskIds = graph.RootElement
            .GetProperty("tasks")
            .EnumerateArray()
            .Where(task => string.Equals(task.GetProperty("type").GetString(), "process", StringComparison.OrdinalIgnoreCase))
            .Select(task => task.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();

        var commands = new List<string>();
        foreach (var taskId in taskIds)
        {
            using var taskResponse = await _app.Client.GetAsync($"/api/admin/tasks/{Uri.EscapeDataString(taskId)}");
            var taskText = await taskResponse.Content.ReadAsStringAsync();
            Assert.True(taskResponse.IsSuccessStatusCode, $"Task request failed with HTTP {(int)taskResponse.StatusCode}.\nBody:\n{taskText}");

            using var task = JsonDocument.Parse(taskText);
            var payload = task.RootElement.GetProperty("payload").GetString() ?? string.Empty;
            using var command = JsonDocument.Parse(payload);
            var fileName = command.RootElement.GetProperty("fileName").GetString() ?? string.Empty;
            var args = command.RootElement.GetProperty("args").EnumerateArray()
                .Select(arg => arg.GetString() ?? string.Empty);
            commands.Add(string.Join(' ', new[] { fileName }.Concat(args)));
        }

        return commands;
    }

    private static bool StartsWithCommand(string command, string expectedPrefix) =>
        string.Equals(command, expectedPrefix, StringComparison.Ordinal)
        || command.StartsWith(expectedPrefix + " ", StringComparison.Ordinal);

    private static void EnsureAzCliReady()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "az",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("account");
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("--only-show-errors");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add("json");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'az'. Install Azure CLI before running live E2E tests.");
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("'az account list' timed out. Ensure Azure CLI is logged in and responsive.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "'az account list' failed. Run 'az login' and select a subscription before live E2E tests.\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");
        }
    }

    private sealed record CapabilityDto(string Id, string Prefix);
}
