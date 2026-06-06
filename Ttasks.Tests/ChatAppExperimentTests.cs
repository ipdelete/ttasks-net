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
    public void Chat_App_Mail_Capability_Exposes_Full_Tool_Documentation()
    {
        var provider = new MailToolCapabilityProvider(new FakeToolDocumentationProvider(("mail", "Mail CLI help")));

        var capabilities = provider.GetCapabilities(new CapabilityRequest("page-1", "how many emails total did I get today?"));

        Assert.Empty(capabilities.Capabilities);
        var tool = Assert.Single(capabilities.Tools);
        Assert.Equal("mail", tool.Kind);
        Assert.Equal("full", tool.Policy);
        Assert.Equal("Mail CLI help", tool.Documentation);
        Assert.Equal("mail", tool.Metadata["toolName"]);
    }

    [Fact]
    public void Chat_App_Template_Renderer_Supports_Yesterday_Clock_Source()
    {
        var store = new InMemoryStore();
        var library = new StoreBackedTaskLibrary(store);
        var item = library.GetOrAdd(new TaskLibraryDefinition(
            "mail.yesterday",
            "Read yesterday's mail",
            "Search mail received yesterday.",
            TaskType.Powershell,
            "mail search --query '?$filter=receivedDateTime ge {yesterday:yyyy-MM-dd}T00:00:00Z and receivedDateTime lt {today:yyyy-MM-dd}T00:00:00Z&$top={top}' --json",
            [
                new TemplateParameter("yesterday", "clock.yesterday"),
                new TemplateParameter("today", "clock.now"),
                new TemplateParameter("top", "default", DefaultValue: 10)
            ],
            new Dictionary<string, object?> { ["capabilityKind"] = "mail" }));
        var renderer = new TaskLibraryTemplateRenderer(new ManualTimeProvider(DateTimeOffset.Parse("2026-06-06T00:30:00-04:00")));

        var payload = renderer.Render(item);

        Assert.Contains("2026-06-05T00:00:00Z", payload, StringComparison.Ordinal);
        Assert.Contains("2026-06-06T00:00:00Z", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_App_Template_Renderer_Supports_Process_Args_With_Clock_Tokens()
    {
        var store = new InMemoryStore();
        var library = new StoreBackedTaskLibrary(store);
        var item = library.GetOrAdd(new TaskLibraryDefinition(
            "mail.yesterday.process",
            "Read yesterday's mail",
            "Search mail received yesterday.",
            TaskType.Process,
            "",
            [
                new TemplateParameter("yesterday", "clock.yesterday"),
                new TemplateParameter("today", "clock.now"),
                new TemplateParameter("top", "default", DefaultValue: 10)
            ],
            new Dictionary<string, object?> { ["capabilityKind"] = "mail" },
            "mail",
            [
                "search",
                "--query",
                "?$filter=receivedDateTime ge {yesterday:yyyy-MM-dd}T00:00:00Z and receivedDateTime lt {today:yyyy-MM-dd}T00:00:00Z&$select=id,subject,from,receivedDateTime&$top={top}",
                "--json"
            ]));
        var renderer = new TaskLibraryTemplateRenderer(new ManualTimeProvider(DateTimeOffset.Parse("2026-06-06T00:30:00-04:00")));

        var command = renderer.RenderProcess(item);

        Assert.Equal("mail", command.FileName);
        Assert.Equal("?$filter=receivedDateTime ge 2026-06-05T00:00:00Z and receivedDateTime lt 2026-06-06T00:00:00Z&$select=id,subject,from,receivedDateTime&$top=10", command.Args[2]);
    }

    [Fact]
    public void Chat_App_Calendar_Today_Capability_Renders_Date_From_Clock_Template()
    {
        var store = new InMemoryStore();
        var now = DateTimeOffset.Parse("2026-06-05T22:00:00-04:00");
        var time = new ManualTimeProvider(now);
        var provider = new CalendarTodayCapabilityProvider(new StoreBackedTaskLibrary(store), new TaskLibraryTemplateRenderer(time));

        var capabilities = provider.GetCapabilities(new CapabilityRequest("page-1", "summarize today's calendar"));

        var capability = Assert.Single(capabilities.Capabilities);
        Assert.Equal("calendar.today", capability.Metadata["capabilityKind"]);
        Assert.Equal("calendar list -s 2026-06-05T00:00:00 -e 2026-06-06T00:00:00 -n 10 --json", capability.Payload);
        var item = Assert.Single(new StoreBackedTaskLibrary(store).All());
        Assert.Equal("calendar.today", item.Key);
        Assert.Contains("{today:yyyy-MM-dd}", item.PayloadTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_App_Azure_Provider_Exposes_Read_Only_Inventory_Capabilities()
    {
        var store = new InMemoryStore();
        var provider = new AzureInventoryCapabilityProvider(new StoreBackedTaskLibrary(store), new TaskLibraryTemplateRenderer());

        var groups = provider.GetCapabilities(new CapabilityRequest("page-1", "list azure resource groups"));
        var resources = provider.GetCapabilities(new CapabilityRequest("page-1", "list azure resources"));
        var subscriptions = provider.GetCapabilities(new CapabilityRequest("page-1", "list azure subscriptions"));

        var groupCapability = Assert.Single(groups.Capabilities);
        Assert.Equal("az.group.list", groupCapability.Metadata["capabilityKind"]);
        Assert.Equal("az group list --only-show-errors --output json", groupCapability.Payload);
        var resourceCapability = Assert.Single(resources.Capabilities);
        Assert.Equal("az.resource.list", resourceCapability.Metadata["capabilityKind"]);
        Assert.Equal("az resource list --only-show-errors --output json", resourceCapability.Payload);
        var subscriptionCapability = Assert.Single(subscriptions.Capabilities);
        Assert.Equal("az.account.list", subscriptionCapability.Metadata["capabilityKind"]);
        Assert.Equal("az account list --only-show-errors --output json", subscriptionCapability.Payload);
    }

    [Fact]
    public void Chat_App_Validator_Rejects_Malformed_Host_Capability_Payloads()
    {
        var validator = CreateValidator();
        var plan = new GraphPlan(
            new GraphPlanInfo("Bad capability"),
            [
                new GraphPlanTask("read", "powershell", CapabilityId: "cap-1"),
                new GraphPlanTask("summary", "prompt", "summarize")
            ],
            [new GraphPlanEdge("read", "summary")]);

        var malformedMail = new CapabilitySet(
            [
                new CommandCapability(
                    "cap-1",
                    "Bad mail",
                    "Bad mail command",
                    TaskType.Powershell,
                    "mail search --json; Remove-Item C:\\temp\\x",
                    new Dictionary<string, object?> { ["capabilityKind"] = "mail" })
            ],
            "unsupported");
        var doubleQuotedMailQuery = malformedMail with
        {
            Capabilities =
            [
                new CommandCapability(
                    "cap-1",
                    "Double quoted mail query",
                    "Double quoted mail query command",
                    TaskType.Powershell,
                    "mail search --query \"?$filter=isRead eq false&$select=id,subject,from,receivedDateTime&$top=10\" --json",
                    new Dictionary<string, object?> { ["capabilityKind"] = "mail" })
            ]
        };
        var malformedCalendar = new CapabilitySet(
            [
                new CommandCapability(
                    "cap-1",
                    "Bad calendar",
                    "Bad calendar command",
                    TaskType.Powershell,
                    "calendar delete --id abc",
                    new Dictionary<string, object?> { ["capabilityKind"] = "calendar.today" })
            ],
            "unsupported");
        var malformedAz = malformedCalendar with
        {
            Capabilities =
            [
                new CommandCapability(
                    "cap-1",
                    "Bad Azure",
                    "Bad Azure command",
                    TaskType.Powershell,
                    "az vm delete --name bad --yes",
                    new Dictionary<string, object?> { ["capabilityKind"] = "az.resource.list" })
            ]
        };
        var unknownKind = malformedCalendar with
        {
            Capabilities =
            [
                new CommandCapability(
                    "cap-1",
                    "Unknown",
                    "Unknown command",
                    TaskType.Powershell,
                    "echo ok",
                    new Dictionary<string, object?> { ["capabilityKind"] = "unknown.kind" })
            ]
        };

        Assert.Throws<ArgumentException>(() => validator.Validate(plan, malformedMail));
        Assert.Throws<ArgumentException>(() => validator.Validate(plan, doubleQuotedMailQuery));
        Assert.Throws<ArgumentException>(() => validator.Validate(plan, malformedCalendar));
        Assert.Throws<ArgumentException>(() => validator.Validate(plan, malformedAz));
        Assert.Throws<ArgumentException>(() => validator.Validate(plan, unknownKind));
    }

    [Fact]
    public void Chat_App_Validator_And_Builder_Accept_Process_Mail_Capabilities()
    {
        var validator = CreateValidator();
        var builder = new GraphPlanBuilder();
        var command = new ProcessCommand(
            "mail",
            "search",
            "--query",
            "?$filter=isRead eq false&$select=id,subject,from,receivedDateTime&$top=10",
            "--json");
        var capabilities = new CapabilitySet(
            [
                new CommandCapability(
                    "cap-mail",
                    "Unread mail",
                    "Read unread mail.",
                    TaskType.Process,
                    command.ToJson(),
                    new Dictionary<string, object?> { ["capabilityKind"] = "mail" })
            ],
            "unsupported");
        var plan = new GraphPlan(
            new GraphPlanInfo("Read mail"),
            [
                new GraphPlanTask("read", "process", CapabilityId: "cap-mail"),
                new GraphPlanTask("summary", "prompt", "summarize")
            ],
            [new GraphPlanEdge("read", "summary")]);

        validator.Validate(plan, capabilities);
        var graph = builder.Build(plan, capabilities);
        var read = graph.Members.Single(task => task.Type == TaskType.Process);
        var builtCommand = read.GetProcessCommand();

        Assert.Equal(command.FileName, builtCommand.FileName);
        Assert.Equal(command.Args, builtCommand.Args);
    }

    [Fact]
    public void Chat_App_Validator_Treats_Process_Mail_As_The_Full_Tool_Boundary()
    {
        var validator = CreateValidator();
        var broadMailCommand = new ProcessCommand(
            "mail",
            "search",
            "--query",
            "?$filter=receivedDateTime ge 2026-06-05T00:00:00Z and receivedDateTime lt 2026-06-06T00:00:00Z&$orderby=receivedDateTime desc&$select=id&$top=100",
            "--top",
            "100",
            "--json");
        var capabilities = new CapabilitySet(
            [
                new CommandCapability(
                    "cap-mail",
                    "Broad mail search",
                    "Let the planner choose mail strategy.",
                    TaskType.Process,
                    broadMailCommand.ToJson(),
                    new Dictionary<string, object?> { ["capabilityKind"] = "mail" })
            ],
            "unsupported");
        var plan = new GraphPlan(
            new GraphPlanInfo("Read mail"),
            [
                new GraphPlanTask("read", "process", CapabilityId: "cap-mail"),
                new GraphPlanTask("summary", "prompt", "summarize")
            ],
            [new GraphPlanEdge("read", "summary")]);

        validator.Validate(plan, capabilities);

        var wrongTool = new CapabilitySet(
            [
                new CommandCapability(
                    "cap-mail",
                    "Wrong tool",
                    "Attempts to leave the mail tool boundary.",
                    TaskType.Process,
                    new ProcessCommand("pwsh", broadMailCommand.Args).ToJson(),
                    new Dictionary<string, object?> { ["capabilityKind"] = "mail" })
            ],
            "unsupported");
        Assert.Throws<ArgumentException>(() => validator.Validate(plan, wrongTool));
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

    [Fact]
    public void Chat_App_Reauthors_Full_Tool_Candidates_After_Failure_And_Promotes_Winner()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), $"ttasks-fake-mail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "mail.cmd"), "@echo off\r\nif \"%1\"==\"fail\" exit /b 9\r\necho Count: 99\r\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Text("""{"mode":"graph","answer":null,"planIntent":"count yesterday mail"}"""));
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "tasks": [
                {
                  "key": "mail.bad-count",
                  "displayName": "Bad mail count",
                  "description": "Broken count attempt.",
                  "type": "process",
                  "fileName": "mail",
                  "argsTemplate": ["fail"],
                  "toolCapabilityKind": "mail"
                }
              ]
            }
            """));
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "graph": { "title": "Count mail" },
              "tasks": [
                { "id": "read", "type": "process", "capabilityId": "cap-1" },
                { "id": "summary", "type": "prompt", "payload": "summarize" }
              ],
              "edges": [{ "from": "read", "to": "summary" }]
            }
            """));
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "tasks": [
                {
                  "key": "mail.good-count",
                  "displayName": "Good mail count",
                  "description": "Working count attempt.",
                  "type": "process",
                  "fileName": "mail",
                  "argsTemplate": ["ok"],
                  "toolCapabilityKind": "mail"
                }
              ]
            }
            """));
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "graph": { "title": "Count mail repaired" },
              "tasks": [
                { "id": "read", "type": "process", "capabilityId": "cap-1" },
                { "id": "summary", "type": "prompt", "payload": "summarize" }
              ],
              "edges": [{ "from": "read", "to": "summary" }]
            }
            """));
        provider.QueueResult(LlmTurnResult.Text("Count: 99"));
        try
        {
            Environment.SetEnvironmentVariable("PATH", toolDir + Path.PathSeparator + originalPath);
            var store = new InMemoryStore();
            var library = new StoreBackedTaskLibrary(store);
            var service = CreateChatTurnService(
                provider,
                store,
                library,
                new CompositeCapabilityProvider([new MailToolCapabilityProvider(new FakeToolDocumentationProvider(("mail", "mail fake help")))]),
                Options.Create(new ChatAppOptions
                {
                    SystemMessagePath = WriteTempSystemMessage("test system message"),
                    MaxGraphRepairAttempts = 1
                }));

            var response = service.Handle("page-1", "How many emails did I receive yesterday?");

            Assert.Equal("Count: 99", response.Answer.Trim());
            Assert.Equal(6, provider.Requests.Count);
            Assert.Contains("Complete-result strategy", provider.Requests[1].Prompt);
            Assert.Contains("Previous attempt failed", provider.Requests[3].Prompt);
            var item = Assert.Single(library.All());
            Assert.Equal("mail.good-count", item.Key);
            Assert.Equal(["ok"], item.ProcessArgsTemplate);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(toolDir, recursive: true);
        }
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
        return CreateChatTurnService(
            provider,
            store,
            library,
            new CompositeCapabilityProvider([new TeamsCapabilityProvider(library, new TaskLibraryTemplateRenderer(), new FakeTeamsChatMetadataResolver(), CreateChatOptions())]),
            CreateChatOptions(),
            registry);
    }

    private static ChatTurnService CreateChatTurnService(
        RecordingLlmProvider provider,
        ITaskStore store,
        ITaskLibrary library,
        ICapabilityProvider capabilityProvider,
        IOptions<ChatAppOptions> options,
        ChatSessionRegistry? registry = null)
    {
        return new ChatTurnService(
            provider,
            new GraphPlanValidator(options),
            new GraphPlanBuilder(),
            registry ?? new ChatSessionRegistry(provider, options),
            store,
            capabilityProvider,
            library,
            new TaskLibraryTemplateRenderer(),
            options);
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

    private sealed class FakeToolDocumentationProvider : IToolDocumentationProvider
    {
        private readonly IReadOnlyDictionary<string, string> _help;

        public FakeToolDocumentationProvider(params (string ToolName, string Help)[] help)
        {
            _help = help.ToDictionary(item => item.ToolName, item => item.Help, StringComparer.Ordinal);
        }

        public string GetHelp(string toolName) =>
            _help.TryGetValue(toolName, out var help)
                ? help
                : string.Empty;
    }
}
