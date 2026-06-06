# Admin and persistence

The chat app persists graph runs, task results, and task-library items to SQLite. Admin pages expose read-only views for debugging and inspection.

## Persistence

The chat app uses `SqliteStore` as the app `ITaskStore`.

Runtime data:

- SQLite DB: `Ttasks.ChatApp\data\ttasks-chat.db`
- ignored path: `Ttasks.ChatApp\data\`

Persisted data includes:

- graph records
- task records
- dependencies/topology
- task status
- payloads
- outputs
- errors
- task-library items
- metadata

## Admin pages

| Page | Purpose |
| --- | --- |
| `/admin` | Persisted graph and task runs, graph detail, task inspector. |
| `/admin/capabilities` | Capability inventory and policy modes. |
| `/admin/library` | Saved reusable task-library templates and metadata. |

## Admin APIs

| Endpoint | Purpose |
| --- | --- |
| `GET /api/admin/graphs` | Recent graph summaries. |
| `GET /api/admin/graphs/{id}` | Graph detail, tasks, and edges. |
| `GET /api/admin/tasks/{id}` | Task payload, metadata, output, and errors. |
| `GET /api/admin/capabilities` | Capability inventory. |
| `GET /api/admin/library` | Task-library items. |

## Debugging

Useful places to inspect:

- `/admin` for graph/task execution failures
- `/admin/library` for saved task-library templates and metadata
- `/admin/capabilities` for capability policies
- `Ttasks.ChatApp\data\ttasks-chat.db` for persisted runtime state
- `Ttasks.Tests\ChatAppExperimentTests.cs` for expected behavior examples

Common issues:

- **No capability**: provider did not match the user's intent.
- **Planner validation failure**: graph JSON referenced unknown capability IDs or included non-prompt payloads.
- **Execution rejection**: payload was not in the host-approved capability set.
- **Bad Teams alias**: metadata resolver could not resolve or store the chat topic.
- **Apphost lock during tests**: running chat app process must be stopped by PID.

## Running and testing

Run the app:

```powershell
dotnet run --project C:\src\ttasks-net\Ttasks.ChatApp\Ttasks.ChatApp.csproj --urls http://127.0.0.1:5123
```

Run tests:

```powershell
dotnet test C:\src\ttasks-net\TtasksNet.slnx
```

If tests fail on Windows with `Ttasks.ChatApp.exe` locked, stop the specific app process ID and rerun tests.
