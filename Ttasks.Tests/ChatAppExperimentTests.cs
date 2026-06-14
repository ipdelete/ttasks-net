using System.Runtime.InteropServices;
using GitHub.Copilot;
using Microsoft.Extensions.Options;
using Ttasks.ChatApp.Services;
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.Tests;

public sealed class ChatAppExperimentTests
{
    [Fact]
    public void Validator_Accepts_Process_Plan_When_Command_Matches_Allowed_Prefix()
    {
        var validator = CreateValidator("mail");
        var plan = MakeProcessPlan(
            new ProcessSpec("mail", ["search", "--top", "10"]));

        validator.Validate(plan, MakeCapabilities("mail"));
    }

    [Fact]
    public void Validator_Rejects_Process_Plan_When_Command_Does_Not_Match_Allowed_Prefix()
    {
        var validator = CreateValidator("teams read");
        var plan = MakeProcessPlan(
            new ProcessSpec("teams", ["post", "--message", "hi"]));

        Assert.Throws<ArgumentException>(() => validator.Validate(plan, MakeCapabilities("teams read")));
    }

    [Fact]
    public void Validator_Honors_Subcommand_Prefixes()
    {
        var validator = CreateValidator("az account list");
        validator.Validate(
            MakeProcessPlan(new ProcessSpec("az", ["account", "list", "--output", "json"])),
            MakeCapabilities("az account list"));

        Assert.Throws<ArgumentException>(() =>
            validator.Validate(
                MakeProcessPlan(new ProcessSpec("az", ["vm", "delete", "--name", "x"])),
                MakeCapabilities("az account list")));
    }

    [Fact]
    public void Validator_Rejects_Library_Suggestion_That_Leaves_Tool_Boundary()
    {
        var validator = CreateValidator("mail");
        var task = new GraphPlanTask(
            "read",
            "process",
            Process: new ProcessSpec("mail", ["search", "--top", "10"]),
            LibrarySuggestion: new LibrarySuggestion(
                "leak",
                "Leak",
                "tries to wrap mail in pwsh",
                "mail",
                ["search", "--top", "10"]));
        var plan = new GraphPlan(
            new GraphPlanInfo("plan"),
            [task, new GraphPlanTask("summary", "prompt", Prompt: "ok")],
            [new GraphPlanEdge("read", "summary")]);

        validator.Validate(plan, MakeCapabilities("mail"));
    }

    [Fact]
    public void Builder_Creates_Process_And_Prompt_Tasks_From_Plan()
    {
        var builder = new GraphPlanBuilder();
        var plan = new GraphPlan(
            new GraphPlanInfo("plan"),
            [
                new GraphPlanTask("read", "process", Process: new ProcessSpec("mail", ["search", "--top", "10"])),
                new GraphPlanTask("summary", "prompt", Prompt: "summarize")
            ],
            [new GraphPlanEdge("read", "summary")]);

        var graph = builder.Build(plan);
        var read = graph.Members.Single(task => task.Type == TaskType.Process);
        var summary = graph.Members.Single(task => task.Type == TaskType.Prompt);

        Assert.Equal("mail", read.GetProcessCommand().FileName);
        Assert.Equal(["search", "--top", "10"], read.GetProcessCommand().Args);
        Assert.Equal("summarize", summary.Payload);
    }

    [Fact]
    public void Template_Renderer_Substitutes_Clock_And_Default_Parameters()
    {
        var store = new InMemoryStore();
        var library = new StoreBackedTaskLibrary(store);
        var item = library.GetOrAdd(new TaskLibraryDefinition(
            "mail.range",
            "Search mail by date range",
            "Search mail by date range.",
            "mail",
            ["search", "--filter", "receivedDateTime ge {start:yyyy-MM-dd}T00:00:00Z and receivedDateTime lt {end:yyyy-MM-dd}T00:00:00Z", "--top", "{top}"],
            [
                new TemplateParameter("start", "clock.yesterday"),
                new TemplateParameter("end", "clock.now"),
                new TemplateParameter("top", "default", DefaultValue: 100)
            ]));
        var renderer = new TaskLibraryTemplateRenderer(new ManualTimeProvider(DateTimeOffset.Parse("2026-06-06T00:30:00-04:00")));

        var command = renderer.Render(item);

        Assert.Equal("mail", command.FileName);
        Assert.Equal("receivedDateTime ge 2026-06-05T00:00:00Z and receivedDateTime lt 2026-06-06T00:00:00Z", command.Args[2]);
        Assert.Equal("100", command.Args[4]);
    }

    [Fact]
    public void Library_Seeder_Adds_Configured_Items()
    {
        var store = new InMemoryStore();
        var library = new StoreBackedTaskLibrary(store);
        var seeds = new List<TaskLibrarySeed>
        {
            new()
            {
                Key = "calendar.today",
                DisplayName = "Today's calendar",
                Description = "Today",
                FileName = "calendar",
                ArgsTemplate = ["list", "-s", "{today:yyyy-MM-dd}T00:00:00", "--json"],
                Parameters = [new TemplateParameterConfig { Name = "today", Source = "clock.now" }]
            }
        };

        TaskLibrarySeeder.Seed(library, seeds);
        var items = library.All();

        Assert.Single(items);
        Assert.Equal("calendar.today", items[0].Key);
        Assert.Equal("calendar", items[0].FileName);
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
    }

    [Fact]
    public void Chat_App_Returns_Empty_Message_When_No_Allowed_Tools_Configured()
    {
        var provider = new RecordingLlmProvider("""{"mode":"graph","answer":null,"planIntent":"read"}""");
        var service = CreateChatTurnService(provider);

        var response = service.Handle("page-1", "read teams chat");

        Assert.Equal("answer", response.Mode);
        Assert.Contains("No tools", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Chat_App_Reauthors_Process_Commands_On_Failure_And_Promotes_Successful_Suggestion()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), $"ttasks-fake-mail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDir);
        WriteFakeTool(toolDir, "mail",
            "@echo off\r\nif \"%1\"==\"fail\" exit /b 9\r\necho Count: 99\r\n",
            "#!/bin/sh\nif [ \"$1\" = \"fail\" ]; then exit 9; fi\necho 'Count: 99'\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Text("""{"mode":"graph","answer":null,"planIntent":"count mail"}"""));
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "graph": { "title": "Count mail" },
              "tasks": [
                { "id": "read", "type": "process", "process": { "fileName": "mail", "args": ["fail"] }, "librarySuggestion": { "key": "mail.bad", "displayName": "Bad", "description": "bad", "fileName": "mail", "argsTemplate": ["fail"] } },
                { "id": "summary", "type": "prompt", "prompt": "summarize" }
              ],
              "edges": [{ "from": "read", "to": "summary" }]
            }
            """));
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "graph": { "title": "Count mail repaired" },
              "tasks": [
                { "id": "read", "type": "process", "process": { "fileName": "mail", "args": ["ok"] }, "librarySuggestion": { "key": "mail.good", "displayName": "Good", "description": "good", "fileName": "mail", "argsTemplate": ["ok"] } },
                { "id": "summary", "type": "prompt", "prompt": "summarize" }
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
            var options = Options.Create(new ChatAppOptions
            {
                MaxGraphRepairAttempts = 1,
                MaxContinuationBatches = 0,
                AllowedTools = [new AllowedToolConfig { Prefix = "mail" }]
            });
            var service = CreateChatTurnService(provider, store, library, options);

            var response = service.Handle("page-1", "count my mail");

            Assert.Equal("Count: 99", response.Answer.Trim());
            Assert.Equal(4, provider.Requests.Count);
            Assert.Contains("Failure observations", provider.Requests[2].Prompt, StringComparison.OrdinalIgnoreCase);
            var items = library.All();
            var good = Assert.Single(items, item => item.Key == "mail.good");
            Assert.Equal("mail", good.FileName);
            Assert.DoesNotContain(items, item => item.Key == "mail.bad");

            var allowedToolRegistry = BuildAllowedToolRegistry(options);
            var admin = new AdminService(store, library, new StoreBackedGraphLibrary(store), allowedToolRegistry);
            var turns = admin.RecentTurns();
            var turnSummary = Assert.Single(turns);
            Assert.Equal("page-1", turnSummary.SessionId);
            Assert.Equal(1, turnSummary.RouterCount);
            Assert.Equal(1, turnSummary.PlannerCount);
            Assert.Equal(1, turnSummary.RepairCount);
            Assert.Equal(2, turnSummary.ProcessCount);
            Assert.Equal(2, turnSummary.SummaryCount);

            var turn = admin.GetTurn(turnSummary.TurnId);
            Assert.Equal(new[] { "router", "planner", "process", "summary", "repair", "process", "summary" },
                turn.Tasks.Select(task => task.Kind));
            Assert.All(turn.Tasks.Where(task => task.Kind != "router"), task => Assert.NotNull(task.Attempt));
            Assert.Null(turn.Tasks.Single(task => task.Kind == "router").Attempt);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(toolDir, recursive: true);
        }
    }

    [Fact]
    public void Chat_App_Continuation_Loop_Runs_Multiple_Batches_With_Upstream_Data()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), $"ttasks-fake-teams-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDir);
        WriteFakeTool(toolDir, "teams",
            "@echo off\r\n" +
            "if \"%1\"==\"chat-list\" ( echo {\"chats\":[{\"id\":\"19:abc@thread.v2\",\"topic\":\"aet swe\"}]} & exit /b 0 )\r\n" +
            "if \"%1\"==\"read\" ( echo {\"messages\":[{\"from\":\"alice\",\"text\":\"hello\"}]} & exit /b 0 )\r\n" +
            "exit /b 1\r\n",
            "#!/bin/sh\n" +
            "if [ \"$1\" = \"chat-list\" ]; then echo '{\"chats\":[{\"id\":\"19:abc@thread.v2\",\"topic\":\"aet swe\"}]}'; exit 0; fi\n" +
            "if [ \"$1\" = \"read\" ]; then echo '{\"messages\":[{\"from\":\"alice\",\"text\":\"hello\"}]}'; exit 0; fi\n" +
            "exit 1\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var provider = new RecordingLlmProvider();
        // 1. router
        provider.QueueResult(LlmTurnResult.Text("""{"mode":"graph","answer":null,"planIntent":"discover aet swe chat then read"}"""));
        // 2. planner batch 1: discovery
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "graph": { "title": "Discover" },
              "tasks": [
                { "id": "find", "type": "process", "process": { "fileName": "teams", "args": ["chat-list", "--topic", "aet swe"] } },
                { "id": "summary", "type": "prompt", "prompt": "summarize discovery" }
              ],
              "edges": [{ "from": "find", "to": "summary" }]
            }
            """));
        // 3. summary prompt output (graph executor)
        provider.QueueResult(LlmTurnResult.Text("discovered chat 19:abc@thread.v2"));
        // 4. continuation decision: run another batch
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "mode": "graph",
              "plan": {
                "graph": { "title": "Read" },
                "tasks": [
                  { "id": "read", "type": "process", "process": { "fileName": "teams", "args": ["read", "19:abc@thread.v2"] } },
                  { "id": "summary", "type": "prompt", "prompt": "summarize messages" }
                ],
                "edges": [{ "from": "read", "to": "summary" }]
              }
            }
            """));
        // 5. summary prompt output (graph executor for batch 2)
        provider.QueueResult(LlmTurnResult.Text("alice said hello"));
        // 6. continuation decision: done
        provider.QueueResult(LlmTurnResult.Text("""{"mode":"answer","answer":"AET SWE chat: alice said hello"}"""));

        try
        {
            Environment.SetEnvironmentVariable("PATH", toolDir + Path.PathSeparator + originalPath);
            var store = new InMemoryStore();
            var library = new StoreBackedTaskLibrary(store);
            var options = Options.Create(new ChatAppOptions
            {
                MaxGraphRepairAttempts = 0,
                MaxContinuationBatches = 2,
                AllowedTools = [new AllowedToolConfig { Prefix = "teams chat-list" }, new AllowedToolConfig { Prefix = "teams read" }]
            });
            var service = CreateChatTurnService(provider, store, library, options);

            var response = service.Handle("page-1", "what's latest on the aet swe chat");

            Assert.Equal("AET SWE chat: alice said hello", response.Answer);
            Assert.Equal(6, provider.Requests.Count);

            var allowedToolRegistry = BuildAllowedToolRegistry(options);
            var admin = new AdminService(store, library, new StoreBackedGraphLibrary(store), allowedToolRegistry);
            var turnSummary = Assert.Single(admin.RecentTurns());
            var turn = admin.GetTurn(turnSummary.TurnId);
            var continuationTasks = turn.Tasks.Where(task => task.Kind == "continuation").ToList();
            Assert.Equal(2, continuationTasks.Count);
            Assert.Contains(continuationTasks, task => task.Title == "Continuation decision for batch 2");
            Assert.Contains(continuationTasks, task => task.Title == "Continuation decision for batch 3");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(toolDir, recursive: true);
        }
    }

    [Fact]
    public void OutputReferenceResolver_Gives_Friendly_Error_When_Upstream_Is_Not_Json()
    {
        var upstream = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plain"] = "This is just plain text output."
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OutputReferenceResolver.Resolve("${{ tasks.plain.output.field }}", upstream));

        Assert.Contains("is not JSON", ex.Message);
        Assert.Contains("${{ tasks.<id>.output }}", ex.Message);
        Assert.Contains("prompt task", ex.Message);
    }

    [Fact]
    public void OutputReferenceResolver_Empty_Array_Index_Error_Explains_Discovery_Returned_No_Match()
    {
        var upstream = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["find"] = """{"chats":[]}"""
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OutputReferenceResolver.Resolve("${{ tasks.find.output.chats[0].id }}", upstream));

        Assert.Contains("had 0 item(s)", ex.Message);
        Assert.Contains("discovery", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Chat_App_Carries_Forward_Graph_Suggestion_From_Failed_Original_Plan_Into_Repair()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), $"ttasks-fake-repairsugg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDir);
        WriteFakeTool(toolDir, "echo",
            "@echo off\r\nif \"%1\"==\"bad\" exit /b 1\r\necho OK=%1\r\nexit /b 0\r\n",
            "#!/bin/sh\nif [ \"$1\" = \"bad\" ]; then exit 1; fi\necho \"OK=$1\"\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", toolDir + Path.PathSeparator + originalPath);
            var store = new InMemoryStore();
            var library = new StoreBackedTaskLibrary(store);
            var graphLibrary = new StoreBackedGraphLibrary(store);

            var provider = new RecordingLlmProvider();
            provider.QueueResult(LlmTurnResult.Text("""{"mode":"graph","answer":null,"planIntent":"echo something"}"""));
            // First attempt: includes graphSuggestion but the process fails (arg="bad").
            provider.QueueResult(LlmTurnResult.Text("""
                {
                  "graph": { "title": "Echo workflow" },
                  "tasks": [
                    { "id": "echo", "type": "process", "process": { "fileName": "echo", "args": ["bad"] } },
                    { "id": "summary", "type": "prompt", "prompt": "report" }
                  ],
                  "edges": [{ "from": "echo", "to": "summary" }],
                  "graphParameters": { "value": "bad" },
                  "graphSuggestion": {
                    "key": "echo.value",
                    "displayName": "Echo a value",
                    "description": "Run echo with a parameterized value.",
                    "parameters": [{ "name": "value", "source": "user" }]
                  }
                }
                """));
            // Repair attempt: fixes the process but OMITS graphSuggestion (planner forgot).
            provider.QueueResult(LlmTurnResult.Text("""
                {
                  "graph": { "title": "Echo workflow repaired" },
                  "tasks": [
                    { "id": "echo", "type": "process", "process": { "fileName": "echo", "args": ["good"] } },
                    { "id": "summary", "type": "prompt", "prompt": "report" }
                  ],
                  "edges": [{ "from": "echo", "to": "summary" }]
                }
                """));
            provider.QueueResult(LlmTurnResult.Text("done"));

            var options = Options.Create(new ChatAppOptions
            {
                MaxGraphRepairAttempts = 1,
                MaxContinuationBatches = 0,
                AllowedTools = [new AllowedToolConfig { Prefix = "echo" }]
            });
            var service = CreateChatTurnService(provider, store, library, options, graphLibrary: graphLibrary);

            var response = service.Handle("page-1", "echo good");

            Assert.Equal("graph", response.Mode);
            var promoted = Assert.Single(graphLibrary.All(), item => item.Key == "echo.value");
            Assert.Equal("Echo a value", promoted.DisplayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(toolDir, recursive: true);
        }
    }

    [Fact]
    public void OutputReferenceResolver_Resolves_Json_Path_From_Upstream_Output()
    {
        var upstream = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["find"] = """{"chats":[{"id":"19:abc@thread.v2","topic":"AET SWE"},{"id":"19:def@thread.v2","topic":"Other"}]}"""
        };

        var resolved = OutputReferenceResolver.Resolve("${{ tasks.find.output.chats[0].id }}", upstream);

        Assert.Equal("19:abc@thread.v2", resolved);
    }

    [Fact]
    public void OutputReferenceResolver_Resolves_Raw_Output_When_No_Path()
    {
        var upstream = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["echo"] = "hello world"
        };

        var resolved = OutputReferenceResolver.Resolve("${{ tasks.echo.output }}", upstream);

        Assert.Equal("hello world", resolved);
    }

    [Fact]
    public void OutputReferenceResolver_Supports_FromJson_For_String_Encoded_Json_Fields()
    {
        var upstream = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mail"] = """{"rawResponse":"{\"value\":[{\"id\":\"abc-123\"}]}"}"""
        };

        var resolved = OutputReferenceResolver.Resolve("${{ tasks.mail.output.rawResponse|fromjson|value[0].id }}", upstream);

        Assert.Equal("abc-123", resolved);
    }

    [Fact]
    public void Validator_Rejects_Reference_To_Task_Without_Edge()
    {
        var validator = CreateValidator("teams chat-list", "teams read");
        var plan = new GraphPlan(
            new GraphPlanInfo("plan"),
            [
                new GraphPlanTask("find", "process", Process: new ProcessSpec("teams", ["chat-list", "--topic", "aet swe"])),
                new GraphPlanTask("read", "process", Process: new ProcessSpec("teams", ["read", "${{ tasks.find.output.chats[0].id }}"])),
                new GraphPlanTask("summary", "prompt", Prompt: "summarize")
            ],
            [new GraphPlanEdge("read", "summary")]);

        var ex = Assert.Throws<ArgumentException>(() => validator.Validate(plan, MakeCapabilities("teams chat-list", "teams read")));
        Assert.Contains("does not declare it as a dependency", ex.Message);
    }

    [Fact]
    public void Validator_Rejects_Reference_To_Unknown_Task()
    {
        var validator = CreateValidator("teams read");
        var plan = new GraphPlan(
            new GraphPlanInfo("plan"),
            [
                new GraphPlanTask("read", "process", Process: new ProcessSpec("teams", ["read", "${{ tasks.missing.output.chats[0].id }}"])),
                new GraphPlanTask("summary", "prompt", Prompt: "summarize")
            ],
            [new GraphPlanEdge("read", "summary")]);

        var ex = Assert.Throws<ArgumentException>(() => validator.Validate(plan, MakeCapabilities("teams read")));
        Assert.Contains("unknown task 'missing'", ex.Message);
    }

    [Fact]
    public void GraphLibrary_Roundtrips_Plan_Template_Through_Store()
    {
        var store = new InMemoryStore();
        var library = new StoreBackedGraphLibrary(store);
        var template = new GraphPlan(
            new GraphPlanInfo("Discover then read", new Dictionary<string, object?> { ["kind"] = "workflow" }),
            [
                new GraphPlanTask("find", "process", Process: new ProcessSpec("teams", ["chat-list", "--topic", "{topic}", "--json"])),
                new GraphPlanTask("read", "process", Process: new ProcessSpec("teams", ["read", "${{ tasks.find.output.chats[0].id }}"])),
                new GraphPlanTask("summary", "prompt", Prompt: "summarize {topic} messages")
            ],
            [new GraphPlanEdge("find", "read"), new GraphPlanEdge("read", "summary")]);

        var stored = library.GetOrAdd(new GraphLibraryDefinition(
            "teams.chat.read-by-topic",
            "Discover Teams chat by topic, read, summarize",
            "Two-step workflow with parameterized topic.",
            template,
            [new TemplateParameter("topic", "user")]));

        var all = library.All();
        var item = Assert.Single(all, i => i.Key == "teams.chat.read-by-topic");
        Assert.Equal(template.Tasks.Count, item.PlanTemplate.Tasks.Count);
        Assert.Equal("teams", item.PlanTemplate.Tasks[0].Process!.FileName);
        Assert.Equal("{topic}", item.PlanTemplate.Tasks[0].Process!.Args[2]);
        Assert.Equal("summarize {topic} messages", item.PlanTemplate.Tasks[2].Prompt);
        Assert.Equal("topic", Assert.Single(item.Parameters).Name);
    }

    [Fact]
    public void Chat_App_Reuses_Graph_Library_Template_By_Key()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), $"ttasks-fake-graphlib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDir);
        WriteFakeTool(toolDir, "teams",
            "@echo off\r\n" +
            "if \"%1\"==\"chat-list\" ( echo {\"chats\":[{\"id\":\"19:xyz@thread.v2\",\"topic\":\"AET SWE\"}]} & exit /b 0 )\r\n" +
            "if \"%1\"==\"read\" ( echo READ_CHAT=%2 & exit /b 0 )\r\n" +
            "exit /b 1\r\n",
            "#!/bin/sh\n" +
            "if [ \"$1\" = \"chat-list\" ]; then echo '{\"chats\":[{\"id\":\"19:xyz@thread.v2\",\"topic\":\"AET SWE\"}]}'; exit 0; fi\n" +
            "if [ \"$1\" = \"read\" ]; then echo \"READ_CHAT=$2\"; exit 0; fi\n" +
            "exit 1\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", toolDir + Path.PathSeparator + originalPath);
            var store = new InMemoryStore();
            var library = new StoreBackedTaskLibrary(store);
            var graphLibrary = new StoreBackedGraphLibrary(store);

            // Seed a graph library item with {topic} placeholder.
            graphLibrary.GetOrAdd(new GraphLibraryDefinition(
                "teams.chat.read-by-topic",
                "Discover then read",
                "Discover Teams chat by topic and read it.",
                new GraphPlan(
                    new GraphPlanInfo("Discover then read"),
                    [
                        new GraphPlanTask("find", "process", Process: new ProcessSpec("teams", ["chat-list", "--topic", "{topic}", "--json"])),
                        new GraphPlanTask("read", "process", Process: new ProcessSpec("teams", ["read", "${{ tasks.find.output.chats[0].id }}"])),
                        new GraphPlanTask("summary", "prompt", Prompt: "summarize")
                    ],
                    [new GraphPlanEdge("find", "read"), new GraphPlanEdge("read", "summary")]),
                [new TemplateParameter("topic", "user")]));

            var provider = new RecordingLlmProvider();
            provider.QueueResult(LlmTurnResult.Text("""{"mode":"graph","answer":null,"planIntent":"find aet swe chat and read"}"""));
            // Planner returns ONLY a graphLibraryKey + graphParameters; no tasks/edges.
            provider.QueueResult(LlmTurnResult.Text("""
                {
                  "graph": { "title": "Reuse template" },
                  "tasks": [],
                  "edges": [],
                  "graphLibraryKey": "teams.chat.read-by-topic",
                  "graphParameters": { "topic": "aet swe" }
                }
                """));
            provider.QueueResult(LlmTurnResult.Text("done"));

            var options = Options.Create(new ChatAppOptions
            {
                MaxGraphRepairAttempts = 0,
                MaxContinuationBatches = 0,
                AllowedTools = [new AllowedToolConfig { Prefix = "teams chat-list" }, new AllowedToolConfig { Prefix = "teams read" }]
            });
            var service = CreateChatTurnService(provider, store, library, options, graphLibrary: graphLibrary);

            var response = service.Handle("page-1", "read aet swe");

            Assert.Equal("graph", response.Mode);
            var readTask = Assert.Single(response.Tasks!, task => task.Output.Contains("READ_CHAT="));
            Assert.Contains("READ_CHAT=19:xyz@thread.v2", readTask.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(toolDir, recursive: true);
        }
    }

    [Fact]
    public void Chat_App_Promotes_Graph_Suggestion_After_Successful_Authored_Graph()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), $"ttasks-fake-promote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDir);
        WriteFakeTool(toolDir, "echo",
            "@echo off\r\necho OK=%1\r\nexit /b 0\r\n",
            "#!/bin/sh\necho \"OK=$1\"\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", toolDir + Path.PathSeparator + originalPath);
            var store = new InMemoryStore();
            var library = new StoreBackedTaskLibrary(store);
            var graphLibrary = new StoreBackedGraphLibrary(store);

            var provider = new RecordingLlmProvider();
            provider.QueueResult(LlmTurnResult.Text("""{"mode":"graph","answer":null,"planIntent":"echo something"}"""));
            provider.QueueResult(LlmTurnResult.Text("""
                {
                  "graph": { "title": "Echo workflow" },
                  "tasks": [
                    { "id": "echo", "type": "process", "process": { "fileName": "echo", "args": ["{value}"] } },
                    { "id": "summary", "type": "prompt", "prompt": "report" }
                  ],
                  "edges": [{ "from": "echo", "to": "summary" }],
                  "graphParameters": { "value": "hello" },
                  "graphSuggestion": {
                    "key": "echo.value",
                    "displayName": "Echo a value",
                    "description": "Run echo with a parameterized value.",
                    "parameters": [{ "name": "value", "source": "user" }]
                  }
                }
                """));
            provider.QueueResult(LlmTurnResult.Text("done"));

            var options = Options.Create(new ChatAppOptions
            {
                MaxGraphRepairAttempts = 0,
                MaxContinuationBatches = 0,
                AllowedTools = [new AllowedToolConfig { Prefix = "echo" }]
            });
            var service = CreateChatTurnService(provider, store, library, options, graphLibrary: graphLibrary);

            var response = service.Handle("page-1", "echo hello");

            Assert.Equal("graph", response.Mode);
            var promoted = Assert.Single(graphLibrary.All(), item => item.Key == "echo.value");
            Assert.Equal("Echo a value", promoted.DisplayName);
            Assert.Equal("{value}", promoted.PlanTemplate.Tasks[0].Process!.Args[0]);
            Assert.Equal("value", Assert.Single(promoted.Parameters).Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(toolDir, recursive: true);
        }
    }

    [Fact]
    public void Chat_App_Dynamic_Binding_Resolves_Prompt_Output_Into_Downstream_Process_Task()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), $"ttasks-fake-echo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDir);
        WriteFakeTool(toolDir, "produce",
            "@echo off\r\necho line:initial-value\r\nexit /b 0\r\n",
            "#!/bin/sh\necho 'line:initial-value'\n");
        WriteFakeTool(toolDir, "echo",
            "@echo off\r\necho ECHOED=%1\r\nexit /b 0\r\n",
            "#!/bin/sh\necho \"ECHOED=$1\"\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Text("""{"mode":"graph","answer":null,"planIntent":"produce then echo"}"""));
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "graph": { "title": "Produce then echo" },
              "tasks": [
                { "id": "produce", "type": "process", "process": { "fileName": "produce", "args": [] } },
                { "id": "extract", "type": "prompt", "prompt": "extract the value" },
                { "id": "echo", "type": "process", "process": { "fileName": "echo", "args": ["${{ tasks.extract.output }}"] } },
                { "id": "summary", "type": "prompt", "prompt": "report" }
              ],
              "edges": [
                { "from": "produce", "to": "extract" },
                { "from": "extract", "to": "echo" },
                { "from": "echo", "to": "summary" }
              ]
            }
            """));
        // extract prompt returns just the value after "line:"
        provider.QueueResult(LlmTurnResult.Text("initial-value"));
        // summary
        provider.QueueResult(LlmTurnResult.Text("done"));

        try
        {
            Environment.SetEnvironmentVariable("PATH", toolDir + Path.PathSeparator + originalPath);
            var store = new InMemoryStore();
            var library = new StoreBackedTaskLibrary(store);
            var options = Options.Create(new ChatAppOptions
            {
                MaxGraphRepairAttempts = 0,
                MaxContinuationBatches = 0,
                AllowedTools =
                [
                    new AllowedToolConfig { Prefix = "produce" },
                    new AllowedToolConfig { Prefix = "echo" }
                ]
            });
            var service = CreateChatTurnService(provider, store, library, options);

            var response = service.Handle("page-1", "produce then echo");

            Assert.Equal("graph", response.Mode);
            Assert.NotNull(response.Tasks);
            var echoTask = response.Tasks!.Single(task => task.Type == "process" && task.Output.Contains("ECHOED="));
            Assert.Contains("ECHOED=initial-value", echoTask.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(toolDir, recursive: true);
        }
    }

    [Fact]
    public void Chat_App_Dynamic_Binding_Resolves_Upstream_Output_Inside_One_Graph()
    {
        var toolDir = Path.Combine(Path.GetTempPath(), $"ttasks-fake-teams-bind-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolDir);
        WriteFakeTool(toolDir, "teams",
            "@echo off\r\n" +
            "if \"%1\"==\"chat-list\" ( echo {\"chats\":[{\"id\":\"19:abc@thread.v2\",\"topic\":\"AET SWE\"}]} & exit /b 0 )\r\n" +
            "if \"%1\"==\"read\" ( echo READ_CHAT=%2 & exit /b 0 )\r\n" +
            "exit /b 1\r\n",
            "#!/bin/sh\n" +
            "if [ \"$1\" = \"chat-list\" ]; then echo '{\"chats\":[{\"id\":\"19:abc@thread.v2\",\"topic\":\"AET SWE\"}]}'; exit 0; fi\n" +
            "if [ \"$1\" = \"read\" ]; then echo \"READ_CHAT=$2\"; exit 0; fi\n" +
            "exit 1\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Text("""{"mode":"graph","answer":null,"planIntent":"discover then read"}"""));
        provider.QueueResult(LlmTurnResult.Text("""
            {
              "graph": { "title": "Discover then read" },
              "tasks": [
                { "id": "find", "type": "process", "process": { "fileName": "teams", "args": ["chat-list", "--topic", "aet swe", "--json"] } },
                { "id": "read", "type": "process", "process": { "fileName": "teams", "args": ["read", "${{ tasks.find.output.chats[0].id }}"] } },
                { "id": "summary", "type": "prompt", "prompt": "report what was read" }
              ],
              "edges": [
                { "from": "find", "to": "read" },
                { "from": "read", "to": "summary" }
              ]
            }
            """));
        provider.QueueResult(LlmTurnResult.Text("done"));

        try
        {
            Environment.SetEnvironmentVariable("PATH", toolDir + Path.PathSeparator + originalPath);
            var store = new InMemoryStore();
            var library = new StoreBackedTaskLibrary(store);
            var options = Options.Create(new ChatAppOptions
            {
                MaxGraphRepairAttempts = 0,
                MaxContinuationBatches = 0,
                AllowedTools = [new AllowedToolConfig { Prefix = "teams chat-list" }, new AllowedToolConfig { Prefix = "teams read" }]
            });
            var service = CreateChatTurnService(provider, store, library, options);

            var response = service.Handle("page-1", "read aet swe");

            Assert.Equal("graph", response.Mode);
            Assert.NotNull(response.Tasks);
            var readTask = response.Tasks!.Single(task => task.Type == "process" && task.Output.Contains("READ_CHAT="));
            Assert.Contains("READ_CHAT=19:abc@thread.v2", readTask.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(toolDir, recursive: true);
        }
    }

    private static GraphPlanValidator CreateValidator(params string[] prefixes) =>
        new(Options.Create(new ChatAppOptions
        {
            MaxTasks = 8,
            AllowedTools = prefixes.Select(p => new AllowedToolConfig { Prefix = p }).ToList()
        }));

    private static GraphPlan MakeProcessPlan(ProcessSpec process) =>
        new(
            new GraphPlanInfo("plan"),
            [
                new GraphPlanTask("read", "process", Process: process),
                new GraphPlanTask("summary", "prompt", Prompt: "summarize")
            ],
            [new GraphPlanEdge("read", "summary")]);

    private static CapabilitySet MakeCapabilities(params string[] prefixes) =>
        new(
            prefixes.Select(p => new AllowedTool(p)).ToList(),
            [],
            [],
            "empty");

    private static ChatTurnService CreateChatTurnService(
        RecordingLlmProvider provider,
        ITaskStore? store = null,
        ITaskLibrary? library = null,
        IOptions<ChatAppOptions>? options = null,
        ChatSessionRegistry? registry = null,
        IGraphLibrary? graphLibrary = null)
    {
        store ??= new InMemoryStore();
        library ??= new StoreBackedTaskLibrary(store);
        graphLibrary ??= new StoreBackedGraphLibrary(store);
        options ??= Options.Create(new ChatAppOptions
        {
            AllowedTools = []
        });
        var allowedToolRegistry = BuildAllowedToolRegistry(options);
        return new ChatTurnService(
            provider,
            new GraphPlanValidator(options),
            new GraphPlanBuilder(),
            registry ?? new ChatSessionRegistry(provider, options),
            store,
            new StoreBackedCapabilityProvider(allowedToolRegistry, library, graphLibrary),
            library,
            graphLibrary,
            new TaskLibraryTemplateRenderer(),
            options);
    }

    private static AllowedToolRegistry BuildAllowedToolRegistry(IOptions<ChatAppOptions> options)
    {
        var store = new InMemoryAllowedToolStore();
        var registry = new AllowedToolRegistry(store);
        AllowedToolSeeder.Seed(registry, options.Value.AllowedTools);
        return registry;
    }

    private static void WriteFakeTool(string dir, string name, string windowsScript, string shellScript)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.WriteAllText(Path.Combine(dir, name + ".cmd"), windowsScript);
        }
        else
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, shellScript);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                       UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

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
}
