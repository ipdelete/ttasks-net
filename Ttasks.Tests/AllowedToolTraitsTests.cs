using System.Text.Json;
using Ttasks.ChatApp.Services;
using Ttasks.Core;

namespace Ttasks.Tests;

public sealed class AllowedToolTraitsTests
{
    [Fact]
    public void Normalization_Lowercases_Trims_Dedupes_And_Drops_Empties()
    {
        var store = new InMemoryAllowedToolStore();
        var registry = new AllowedToolRegistry(store);
        registry.Create(new AllowedToolDraft(
            Prefix: "mail",
            Traits:
            [
                "  Means.Communication.Email  ",
                "operates.on.person",
                "OPERATES.ON.PERSON",
                " ",
                "operates.on.message"
            ]), actor: "test");

        var provider = new StoreBackedCapabilityProvider(
            registry,
            new InMemoryTaskLibrary(),
            new InMemoryGraphLibrary());

        var capabilities = provider.GetCapabilities(new CapabilityRequest("s1", "u"));
        var tool = Assert.Single(capabilities.AllowedTools);
        Assert.NotNull(tool.Traits);
        Assert.Equal(
            new[] { "means.communication.email", "operates.on.person", "operates.on.message" },
            tool.Traits);
    }

    [Fact]
    public void Tool_Without_Traits_Stays_Backward_Compatible()
    {
        var store = new InMemoryAllowedToolStore();
        var registry = new AllowedToolRegistry(store);
        registry.Create(new AllowedToolDraft(Prefix: "echo", Description: "test"), actor: "test");

        var provider = new StoreBackedCapabilityProvider(
            registry,
            new InMemoryTaskLibrary(),
            new InMemoryGraphLibrary());

        var capabilities = provider.GetCapabilities(new CapabilityRequest("s1", "u"));
        var tool = Assert.Single(capabilities.AllowedTools);
        Assert.Null(tool.Traits);
    }

    [Fact]
    public void FormatAllowedTools_Includes_Traits_When_Present()
    {
        var tools = new List<AllowedTool>
        {
            new("mail", "Mail CLI", "mail --help", new[] { "means.communication.email" })
        };

        var json = Prompts.FormatAllowedTools(tools);
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement[0];
        Assert.Equal("mail", entry.GetProperty("prefix").GetString());
        var traits = entry.GetProperty("traits");
        Assert.Equal(JsonValueKind.Array, traits.ValueKind);
        Assert.Equal("means.communication.email", traits[0].GetString());
    }

    [Fact]
    public void FormatAllowedTools_Omits_Traits_When_Empty_Or_Null()
    {
        var tools = new List<AllowedTool>
        {
            new("echo", "Echo", null, null),
            new("noop", "Noop", null, Array.Empty<string>())
        };

        var json = Prompts.FormatAllowedTools(tools);
        using var doc = JsonDocument.Parse(json);

        foreach (var entry in doc.RootElement.EnumerateArray())
            Assert.False(entry.TryGetProperty("traits", out _));
    }

    [Fact]
    public void NormalizeTraits_Returns_Null_For_Empty_List()
    {
        Assert.Null(AllowedToolRegistry.NormalizeTraits([]));
    }

    private sealed class InMemoryTaskLibrary : ITaskLibrary
    {
        public TaskLibraryItem GetOrAdd(TaskLibraryDefinition definition) =>
            new(
                Guid.NewGuid().ToString(),
                definition.Key,
                definition.DisplayName,
                definition.Description,
                definition.FileName,
                definition.ArgsTemplate,
                definition.Parameters,
                definition.Metadata ?? new Dictionary<string, object?>(),
                DateTimeOffset.UtcNow);

        public IReadOnlyList<TaskLibraryItem> All() => [];
    }

    private sealed class InMemoryGraphLibrary : IGraphLibrary
    {
        public GraphLibraryItem GetOrAdd(GraphLibraryDefinition definition) =>
            throw new NotImplementedException();

        public IReadOnlyList<GraphLibraryItem> All() => [];

        public bool Remove(string key) => false;
    }
}
