# ttasks-net

`ttasks-net` is an experimental .NET port of the `ttasks` task runtime. It provides typed tasks, dependency graphs, executors, stores, and an ASP.NET chat harness that lets an LLM plan work while the host controls capabilities, validation, execution, and persistence.

## Why this exists

The project explores a safer pattern for LLM-assisted task execution:

- model plans graph shape and summarizes results
- host owns tool/capability policy
- executable payloads are resolved host-side
- reusable task templates accumulate in a task library
- graph runs, task payloads, outputs, errors, and metadata are inspectable

## Projects

| Project | Purpose |
| --- | --- |
| `Ttasks.Core` | Core task model, graph scheduler, executor, events, in-memory store, SQLite store, and LLM session integration. |
| `Ttasks.ChatApp` | ASP.NET chat experiment with routing, graph planning, capability policies, task library, admin pages, and SQLite persistence. |
| `Ttasks.Tests` | xUnit coverage for the core runtime, conformance behavior, stores, LLM integration, and chat app experiment. |

## Features

- task types for Bash, PowerShell, structured process argv, prompt, and agent work
- dependency-aware `TaskGraph` execution with parallel workers
- retry, timeout, cancellation, blocking, and result capture
- in-memory and SQLite-backed stores
- LLM prompt handlers through GitHub Copilot SDK integration
- browser chat app with per-page shared sessions and replacement system prompt
- config-driven allowed-tool capability boundary (prefix is the policy)
- task library with parameterized process templates and success-only promotion of planner-authored suggestions
- bounded repair loop that retries with failure observations before giving up
- complete-result planning guidance for "all/total/count/complete" requests
- admin pages for graphs/tasks, allowed tools, and task-library items

## Requirements

- .NET 8 SDK
- PowerShell for built-in PowerShell task execution and scripts
- GitHub Copilot access for LLM-backed prompt/chat flows
- Optional CLIs for chat app capabilities:
  - `teams`
  - `mail`
  - `calendar`
  - `az`

## Quick start

Clone and test:

```powershell
git clone https://github.com/ipdelete/ttasks-net.git
cd ttasks-net
dotnet test .\TtasksNet.slnx
```

Run the chat app:

```powershell
dotnet run --project C:\src\ttasks-net\Ttasks.ChatApp\Ttasks.ChatApp.csproj --urls http://127.0.0.1:5123
```

Open:

- chat: <http://127.0.0.1:5123>
- graphs/tasks admin: <http://127.0.0.1:5123/admin>
- capability inventory: <http://127.0.0.1:5123/admin/capabilities>
- task library: <http://127.0.0.1:5123/admin/library>

If `dotnet test` fails on Windows because `Ttasks.ChatApp.exe` is locked, stop the running app process by PID and rerun the command.

## Core usage example

```csharp
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

var executor = TaskExecutor.WithBuiltInHandlers();
var graph = new TaskGraph("Example graph");

var read = CoreTask.Powershell("Get-Date", title: "Read date");
var summarize = CoreTask.Prompt("Summarize the upstream result.", title: "Summarize");

graph.Add(read);
graph.Add(summarize, after: [read]);
graph.Run(executor);
```

Prompt tasks require a prompt handler registration, such as the LLM session handler used by the chat app.

Use `CoreTask.Process(new ProcessCommand("tool", "arg1", "arg2"))` for external CLI tools when arguments should bypass shell quoting and variable expansion. Keep `CoreTask.Powershell(...)` for actual PowerShell scripts.

## Chat app architecture

The chat app separates planning from execution:

1. The router decides whether a turn is a direct answer or graph work.
2. Capability providers expose host-approved work for the turn.
3. Full-tool capabilities can use tool docs to create in-memory candidate task templates.
4. The planner receives only concrete capability IDs and descriptions.
5. The validator rejects unsafe graph plans and malformed capability payloads.
6. The builder resolves capability IDs to executable tasks.
7. The executor runs only payloads from the current host-approved capability set.
8. Failed validation/execution can loop back through bounded candidate repair and replanning.
9. Successful full-tool candidates selected by the winning graph are promoted to the task library.
10. Complete-result requests are guided to prove coverage with documented count/all/paging mechanisms before reporting exact totals.
11. SQLite persistence makes graph and task runs inspectable through admin pages.

See [`.aidocs`](.aidocs/README.md) for developer documentation.

## Documentation

- [Chat app overview](.aidocs/chat-app-overview.md)
- [Chat turn lifecycle](.aidocs/chat-turn-lifecycle.md)
- [Capability system](.aidocs/capability-system.md)
- [Task library](.aidocs/task-library.md)
- [Admin and persistence](.aidocs/admin-and-persistence.md)
- [Safety model](.aidocs/safety-model.md)
- [Adding capabilities](.aidocs/adding-capabilities.md)

## Development

Run all tests:

```powershell
dotnet test C:\src\ttasks-net\TtasksNet.slnx
```

Runtime chat app data is written under `Ttasks.ChatApp\data\` and is ignored by git.

## Project status

This repository is experimental. APIs and chat app architecture may change as the .NET port and capability model evolve.

## License

Licensed under the [Apache License 2.0](LICENSE).
