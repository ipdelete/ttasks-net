# Chat app overview

`Ttasks.ChatApp` is an ASP.NET chat harness over `Ttasks.Core`. It demonstrates LLM-assisted task execution where the LLM plans and summarizes, while the host owns capabilities, validation, execution, and persistence.

## Who

1. **User**
   - Uses the browser chat page at `/`.
   - Asks direct questions or requests external work such as reading Teams, mail, calendar, or Azure data.

2. **LLM**
   - Routes each turn as either direct answer or graph work.
   - Creates graph plans from host-provided capability catalogs.
   - For full-tool capabilities, proposes reusable task-library templates from exposed tool documentation.
   - Does not get direct tool access and does not execute commands itself.

3. **Host app**
   - Owns capability policy.
   - Resolves executable payloads from approved capabilities.
   - Validates graph plans and command shapes.
   - Executes tasks through `ttasks` runtime primitives.
   - Persists graphs, tasks, results, and reusable task-library items.

## What

The app includes:

- browser chat page
- replacement system prompt from `Ttasks.ChatApp\system-message.md`
- per-page shared LLM sessions through `ChatSessionRegistry`
- router prompt for direct answer vs graph action
- graph planner prompt that emits validated graph JSON
- host-owned capability providers with policy modes
- task library backed by `ITaskStore`
- template rendering for reusable command payloads
- SQLite persistence through `SqliteStore`
- admin pages for graphs/tasks, capabilities, and task-library items

## Why

The app proves a safer and more reusable pattern for LLM-assisted task execution:

- The LLM can reason about intent and graph structure.
- The host controls what can execute.
- Tool work becomes reusable task-library templates.
- Successful patterns accumulate over time.
- Graphs, tasks, payloads, results, and errors remain inspectable.

The design keeps `Ttasks.Core` generic while `Ttasks.ChatApp` owns product policy and capability decisions.

## Where

Important files:

| File | Purpose |
| --- | --- |
| `Ttasks.ChatApp\Program.cs` | DI registration, HTTP endpoints, inline chat/admin pages. |
| `Ttasks.ChatApp\system-message.md` | Replacement system prompt for chat sessions. |
| `Ttasks.ChatApp\Services\ChatTurnService.cs` | Main router, capability, tool-authoring, planner, validation, execution flow. |
| `Ttasks.ChatApp\Services\Capabilities.cs` | Capability providers, policy models, task library, template rendering, Teams metadata resolution. |
| `Ttasks.ChatApp\Services\GraphPlanValidator.cs` | Plan and capability payload safety boundary. |
| `Ttasks.ChatApp\Services\GraphPlanBuilder.cs` | Converts validated plans and capabilities into `TaskGraph` instances. |
| `Ttasks.ChatApp\Services\AdminService.cs` | Read-only admin DTO assembly. |
| `Ttasks.ChatApp\Models\ChatModels.cs` | Chat, graph plan, tool proposal, and admin DTOs. |
| `Ttasks.Tests\ChatAppExperimentTests.cs` | Main behavior tests. |

Runtime data:

- SQLite DB: `Ttasks.ChatApp\data\ttasks-chat.db`
- `Ttasks.ChatApp\data\` is ignored by git.

## Related docs

- [`chat-turn-lifecycle.md`](chat-turn-lifecycle.md)
- [`capability-system.md`](capability-system.md)
- [`task-library.md`](task-library.md)
- [`admin-and-persistence.md`](admin-and-persistence.md)
- [`safety-model.md`](safety-model.md)
- [`adding-capabilities.md`](adding-capabilities.md)
