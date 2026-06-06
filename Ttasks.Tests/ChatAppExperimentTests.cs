using GitHub.Copilot;
using Microsoft.Extensions.Options;
using Ttasks.ChatApp.Services;
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.Tests;

public sealed class ChatAppExperimentTests
{
    [Fact]
    public void Chat_App_Validator_Accepts_Allowed_Teams_FanIn_Plan()
    {
        var validator = CreateValidator();
        var plan = ValidPlan();

        validator.Validate(plan, ValidCapabilities());
    }

    [Fact]
    public void Chat_App_Validator_Rejects_Model_Payloads_But_Allows_Available_Capabilities()
    {
        var validator = CreateValidator();
        var unsafePayload = ValidPlan() with
        {
            Tasks =
            [
                new GraphPlanTask("bad", "powershell", "Remove-Item C:\\temp\\x"),
                new GraphPlanTask("summary", "prompt", "summarize")
            ],
            Edges = [new GraphPlanEdge("bad", "summary")]
        };
        var hostProvidedCapability = ValidPlan() with
        {
            Tasks =
            [
                new GraphPlanTask("chat", "powershell", CapabilityId: "cap-1"),
                new GraphPlanTask("summary", "prompt", "summarize")
            ],
            Edges = [new GraphPlanEdge("chat", "summary")]
        };

        Assert.Throws<ArgumentException>(() => validator.Validate(unsafePayload, ValidCapabilities()));
        validator.Validate(hostProvidedCapability, ValidCapabilities());
    }

    [Fact]
    public void Chat_App_Validator_Rejects_Teams_Reads_Not_Extracted_From_User_Message()
    {
        var validator = CreateValidator();
        var plan = ValidPlan();
        var invented = plan with
        {
            Tasks =
            [
                new GraphPlanTask("read-b", "powershell", CapabilityId: "cap-1"),
                new GraphPlanTask("read-a", "powershell", CapabilityId: "cap-invented"),
                new GraphPlanTask("summary", "prompt", "Summarize upstream reads.", Title: "Summary")
            ]
        };

        Assert.Throws<ArgumentException>(() => validator.Validate(invented, ValidCapabilities()));
    }

    [Fact]
    public void Chat_App_Teams_Provider_Extracts_Only_User_Provided_Targets()
    {
        var store = new InMemoryStore();
        var provider = new CompositeCapabilityProvider(
        [
            new TeamsCapabilityProvider(new StoreBackedTaskLibrary(store), new TaskLibraryTemplateRenderer(), new FakeTeamsChatMetadataResolver(), CreateChatOptions())
        ]);
        var capabilities = provider.GetCapabilities(new CapabilityRequest(
            "page-1",
            "read https://teams.microsoft.com/l/chat/19:abc%40thread.v2/conversations?context=x and 48:notes, not 19:invented@thread.v2x"));

        Assert.Equal(
            new[]
            {
                "teams read 19:abc@thread.v2 -n 20 --json",
                "teams read 48:notes -n 20 --json"
            },
            capabilities.Capabilities.Select(capability => capability.Payload));
        Assert.All(capabilities.Capabilities, capability => Assert.StartsWith("cap-", capability.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void Chat_App_Teams_Provider_Stores_Chat_Topic_Aliases()
    {
        var store = new InMemoryStore();
        var library = new StoreBackedTaskLibrary(store);
        var provider = new TeamsCapabilityProvider(
            library,
            new TaskLibraryTemplateRenderer(),
            new FakeTeamsChatMetadataResolver(("19:aet@thread.v2", "🔒AET SWE Chat")),
            CreateChatOptions());

        var capabilities = provider.GetCapabilities(new CapabilityRequest("page-1", "read 19:aet@thread.v2"));

        var capability = Assert.Single(capabilities.Capabilities);
        Assert.Equal("Read Teams chat 🔒AET SWE Chat", capability.DisplayName);
        var item = Assert.Single(library.All());
        Assert.Equal("🔒AET SWE Chat", item.Metadata["teamsChatTopic"]);
        Assert.Contains("aet swe chat", Assert.IsAssignableFrom<IEnumerable<object?>>(item.Metadata["teamsChatAliases"]).OfType<string>());
    }

    [Fact]
    public void Chat_App_Teams_Provider_Resolves_Previously_Stored_Chat_By_Alias()
    {
        var store = new InMemoryStore();
        var library = new StoreBackedTaskLibrary(store);
        var resolver = new FakeTeamsChatMetadataResolver(("19:aet@thread.v2", "🔒AET SWE Chat"));
        var provider = new TeamsCapabilityProvider(library, new TaskLibraryTemplateRenderer(), resolver, CreateChatOptions());
        provider.GetCapabilities(new CapabilityRequest("page-1", "read 19:aet@thread.v2"));

        var aliasCapabilities = provider.GetCapabilities(new CapabilityRequest("page-1", "read teams chat aet swe chat"));

        var capability = Assert.Single(aliasCapabilities.Capabilities);
        Assert.Equal("teams read 19:aet@thread.v2 -n 20 --json", capability.Payload);
        Assert.Equal("Read Teams chat 🔒AET SWE Chat", capability.DisplayName);
    }

    [Fact]
    public void Chat_App_Mail_Today_Capability_Renders_Date_From_Clock_Template()
    {
        var store = new InMemoryStore();
        var now = DateTimeOffset.Parse("2026-06-05T22:00:00-04:00");
        var time = new ManualTimeProvider(now);
        var provider = new MailTodayCapabilityProvider(new StoreBackedTaskLibrary(store), new TaskLibraryTemplateRenderer(time));

        var capabilities = provider.GetCapabilities(new CapabilityRequest("page-1", "look at today's mail"));

        var capability = Assert.Single(capabilities.Capabilities);
        Assert.Equal("mail.today", capability.Metadata["capabilityKind"]);
        Assert.Contains("2026-06-05T00:00:00Z", capability.Payload, StringComparison.Ordinal);
        Assert.Contains("2026-06-06T00:00:00Z", capability.Payload, StringComparison.Ordinal);
        Assert.Contains("$filter", capability.Payload, StringComparison.Ordinal);
        var item = Assert.Single(new StoreBackedTaskLibrary(store).All());
        Assert.Equal("mail.today", item.Key);
        Assert.Contains("{today:yyyy-MM-dd}", item.PayloadTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_App_Direct_Answer_Uses_One_Shared_Llm_Session()
    {
        var provider = new RecordingLlmProvider("""{"mode":"answer","answer":"Paris","planIntent":null}""");
        var service = CreateChatTurnService(provider);

        var response = service.Handle("page-1", "What is the capital of France?");

        Assert.Equal("answer", response.Mode);
        Assert.Equal("Paris", response.Answer);
        Assert.Equal("page-1", response.SessionId);
        Assert.Equal(1, provider.CreatedSessions);
        Assert.Single(provider.Requests);
        Assert.False(provider.Requests.Single().ToolsEnabled);
    }

    [Fact]
    public void Chat_App_Session_Uses_Replacement_System_Message_File_And_Skips_Custom_Instructions()
    {
        var provider = new RecordingLlmProvider("""{"mode":"answer","answer":"ok","planIntent":null}""");
        var systemMessagePath = WriteTempSystemMessage("ttasks harness system");
        using var registry = new ChatSessionRegistry(provider, Options.Create(new ChatAppOptions
        {
            SystemMessagePath = systemMessagePath
        }));
        var service = CreateChatTurnService(provider, registry);

        service.Handle("page-1", "hello");

        var options = provider.Sessions.Single().Options;
        Assert.Equal(SystemMessageMode.Replace, options.SystemMessage?.Mode);
        Assert.Equal("ttasks harness system", options.SystemMessage?.Content);
        Assert.True(options.SkipCustomInstructions);
    }

    [Fact]
    public void Chat_App_Reuses_Llm_Session_For_Same_Page_Session_Id()
    {
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Text("""{"mode":"answer","answer":"one","planIntent":null}"""));
        provider.QueueResult(LlmTurnResult.Text("""{"mode":"answer","answer":"two","planIntent":null}"""));
        using var registry = new ChatSessionRegistry(provider, CreateChatOptions());
        var service = CreateChatTurnService(provider, registry);

        var first = service.Handle("page-1", "first");
        var second = service.Handle("page-1", "second");

        Assert.Equal("one", first.Answer);
        Assert.Equal("two", second.Answer);
        Assert.Equal(1, provider.CreatedSessions);
        Assert.Equal(new[] { "page-1", "page-1" }, new[] { first.SessionId, second.SessionId });
    }

    [Fact]
    public void Chat_App_Builder_Preserves_Edge_Order_For_Final_Summary_Task()
    {
        var builder = new GraphPlanBuilder();
        var plan = ValidPlan();

        var graph = builder.Build(plan, ValidCapabilities());
        var summary = graph.Members.Single(task => task.Title == "Summary");

        Assert.Equal("chat-ui", graph.Metadata["source"]);
        Assert.Equal(new[] { "read-notes-b", "read-notes-a" }, graph.Dependencies(summary).Select(task => task.Metadata["plannerId"]));
        Assert.All(graph.Dependencies(summary), task => Assert.Equal("teams.read", task.Metadata["capabilityKind"]));
    }

    [Fact]
    public void Chat_App_Admin_Service_Reads_Persisted_Graphs_And_Tasks()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("Admin graph");
        var read = CoreTask.Powershell("teams read 48:notes -n 1 --json", title: "Read notes");
        var summarize = CoreTask.Prompt("Summarize upstream.", title: "Summarize");
        executor.Register(TaskType.Powershell, _ => "messages");
        executor.Register(TaskType.Prompt, _ => "summary");
        graph.Add(read);
        graph.Add(summarize, after: [read]);

        graph.Run(executor);

        var admin = new AdminService(store, new StoreBackedTaskLibrary(store));
        var summary = Assert.Single(admin.RecentGraphs());
        var detail = admin.GetGraph(graph.Id);
        var task = admin.GetTask(summarize.Id);

        Assert.Equal(graph.Id, summary.Id);
        Assert.Equal("Succeeded", summary.Status);
        Assert.Equal(2, summary.TaskCount);
        Assert.Equal(new[] { read.Id, summarize.Id }, detail.Tasks.Select(item => item.Id));
        var edge = Assert.Single(detail.Edges);
        Assert.Equal(read.Id, edge.From);
        Assert.Equal(summarize.Id, edge.To);
        Assert.Equal("summary", task.Result?.Output);
    }

    [Fact]
    public void Chat_App_Returns_Unsupported_Message_When_Action_Has_No_Capabilities()
    {
        var provider = new RecordingLlmProvider("""{"mode":"graph","answer":null,"planIntent":"read azure"}""");
        var service = CreateChatTurnService(provider);

        var response = service.Handle("page-1", "read azure resources");

        Assert.Equal("answer", response.Mode);
        Assert.Contains("host-approved capabilities", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Single(provider.Requests);
    }

    private static GraphPlanValidator CreateValidator() =>
        new(Options.Create(new ChatAppOptions
        {
            MaxTasks = 8
        }));

    private static ChatTurnService CreateChatTurnService(RecordingLlmProvider? provider = null, ChatSessionRegistry? registry = null)
    {
        provider ??= new RecordingLlmProvider("unused");
        var store = new InMemoryStore();
        var library = new StoreBackedTaskLibrary(store);
        return new ChatTurnService(
            provider,
            CreateValidator(),
            new GraphPlanBuilder(),
            registry ?? new ChatSessionRegistry(provider, CreateChatOptions()),
            store,
            new CompositeCapabilityProvider([new TeamsCapabilityProvider(library, new TaskLibraryTemplateRenderer(), new FakeTeamsChatMetadataResolver(), CreateChatOptions())]),
            CreateChatOptions());
    }

    private static IOptions<ChatAppOptions> CreateChatOptions() =>
        Options.Create(new ChatAppOptions
        {
            SystemMessagePath = WriteTempSystemMessage("test system message")
        });

    private static string WriteTempSystemMessage(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ttasks-chat-system-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, content);
        return path;
    }

    private static CapabilitySet ValidCapabilities() =>
        new(
            [
                new CommandCapability(
                    "cap-1",
                    "Read notes B",
                    "Read messages from 48:notes.",
                    TaskType.Powershell,
                    "teams read 48:notes -n 20 --json",
                    new Dictionary<string, object?>
                    {
                        ["capabilityKind"] = "teams.read",
                        [StoreBackedTaskLibrary.LibraryKeyKey] = "teams.chat.read:48:notes",
                        ["libraryTaskId"] = "library-notes"
                    }),
                new CommandCapability(
                    "cap-2",
                    "Read notes A",
                    "Read messages from 48:notes.",
                    TaskType.Powershell,
                    "teams read 48:notes -n 20 --json",
                    new Dictionary<string, object?>
                    {
                        ["capabilityKind"] = "teams.read",
                        [StoreBackedTaskLibrary.LibraryKeyKey] = "teams.chat.read:48:notes",
                        ["libraryTaskId"] = "library-notes"
                    })
            ],
            "unsupported");

    private static GraphPlan ValidPlan() =>
        new(
            new GraphPlanInfo("Read and summarize", new Dictionary<string, object?> { ["source"] = "chat-ui" }),
            [
                new GraphPlanTask("read-b", "powershell", CapabilityId: "cap-1", Metadata: new Dictionary<string, object?> { ["plannerId"] = "read-notes-b" }),
                new GraphPlanTask("read-a", "powershell", CapabilityId: "cap-2", Metadata: new Dictionary<string, object?> { ["plannerId"] = "read-notes-a" }),
                new GraphPlanTask("summary", "prompt", "Summarize upstream reads.", Title: "Summary")
            ],
            [
                new GraphPlanEdge("read-b", "summary"),
                new GraphPlanEdge("read-a", "summary")
            ]);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    }

    private sealed class FakeTeamsChatMetadataResolver : ITeamsChatMetadataResolver
    {
        private readonly IReadOnlyDictionary<string, string> _topics;

        public FakeTeamsChatMetadataResolver(params (string ChatId, string Topic)[] topics)
        {
            _topics = topics.ToDictionary(topic => topic.ChatId, topic => topic.Topic, StringComparer.Ordinal);
        }

        public TeamsChatMetadata Resolve(string chatId) =>
            _topics.TryGetValue(chatId, out var topic)
                ? new TeamsChatMetadata(chatId, topic)
                : new TeamsChatMetadata(chatId);
    }
}
