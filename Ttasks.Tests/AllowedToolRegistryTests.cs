using Microsoft.Extensions.Options;
using Ttasks.ChatApp.Services;

namespace Ttasks.Tests;

public sealed class AllowedToolRegistryTests
{
    [Fact]
    public void SeedDefaults_Does_Not_Resurrect_Disabled_Default()
    {
        var store = new InMemoryAllowedToolStore();
        var registry = new AllowedToolRegistry(store);
        var seeds = new[]
        {
            new AllowedToolConfig
            {
                Prefix = "gh issue list",
                Description = "List issues."
            }
        };

        registry.SeedDefaults(seeds);
        var seeded = Assert.Single(registry.All());
        registry.SetEnabled(seeded.Id, enabled: false, actor: "test");

        registry.SeedDefaults(seeds);

        var afterRestartSeed = Assert.Single(registry.All());
        Assert.False(afterRestartSeed.Enabled);
        Assert.Equal(seeded.Id, afterRestartSeed.Id);
    }

    [Fact]
    public void Create_Rejects_Strict_Prefix_Overlap()
    {
        var store = new InMemoryAllowedToolStore();
        var registry = new AllowedToolRegistry(store);
        registry.Create(new AllowedToolDraft("gh"), "test");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Create(new AllowedToolDraft("gh issue list"), "test"));

        Assert.Contains("overlaps", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_Registry_Recreates_Session_When_Capability_Prompt_Changes()
    {
        var provider = new RecordingLlmProvider("ok");
        var options = Options.Create(new ChatAppOptions { Model = "m" });
        using var registry = new ChatSessionRegistry(provider, options);

        var first = registry.GetOrCreate("session-1", [new AllowedTool("gh issue list")]);
        var second = registry.GetOrCreate("session-1", [new AllowedTool("gh issue list")]);
        var third = registry.GetOrCreate("session-1", [new AllowedTool("az account list")]);

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(2, provider.CreatedSessions);
        Assert.True(provider.Sessions[0].Disposed);
        var currentSystemMessage = provider.Sessions[1].Options.SystemMessage?.Content ?? string.Empty;
        Assert.Contains("az account list", currentSystemMessage, StringComparison.Ordinal);
    }
}
