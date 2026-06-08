# Capability system

Capabilities are the host-approved tool surface the chat planner can use. There are no per-tool provider classes anymore — the capability layer is a config-driven prefix list plus the task library.

## Allowed tools

Configured under `ChatApp:AllowedTools` in `appsettings.json`:

```json
"AllowedTools": [
  { "prefix": "mail",            "description": "Microsoft 365 mail CLI.",  "helpCommand": "mail --help" },
  { "prefix": "teams read",      "description": "Read a Teams chat." },
  { "prefix": "teams chat-get",  "description": "Read Teams chat metadata." },
  { "prefix": "calendar list",   "description": "List calendar events." },
  { "prefix": "az account list", "description": "List Azure subscriptions." }
]
```

The string granularity *is* the policy:

| Prefix | What it allows |
| --- | --- |
| `mail` | Whole mail CLI surface |
| `teams read` | Only `teams read ...` |
| `az account list` | Only that specific command |

The validator builds a "head" from `process.fileName` plus leading non-flag args and approves the task if any allowed prefix matches.

## Task library

The task library holds parameterized templates the planner can reuse. Library items are added in three ways:

1. **Seed**: `ChatApp:LibrarySeed` entries pre-populate the library on app startup. Today these include `calendar.today` and the `az.*.list` items.
2. **Successful suggestion promotion**: when the planner emits a `process` task with a `librarySuggestion`, the host promotes that suggestion to the library only after the graph succeeds. Failed candidates stay transient.
3. **Reuse**: the planner can set `libraryItemKey` (and optional `libraryParameters`) on a process task; the host renders the template into a `ProcessCommand` before validation.

Library item shape:

- `key` (stable semantic id)
- `displayName`, `description`
- `fileName`, `argsTemplate`
- `parameters` (clock.now / clock.yesterday / clock.tomorrow / default / metadata:&lt;key&gt;)

## Capability resolution

`ConfigCapabilityProvider` returns a `CapabilitySet` containing:

- `AllowedTools`: from config
- `LibrarySuggestions`: every library item (the planner picks which ones apply)
- `EmptyMessage`: shown when no tools are configured

There is no per-turn regex intent matching. The planner (LLM) does intent matching, parameter extraction, and command composition based on the allowed tools, library suggestions, and tool docs (`mail --help`-style help commands fetched on demand).

## Repair loop

When a graph attempt fails (validation or execution), the host feeds the failure observations to the planner and replans within a bounded retry budget (`ChatApp:MaxGraphRepairAttempts`). Only candidates selected by a successful repair attempt are promoted to the library.

## What this collapses

The previous design had `TeamsCapabilityProvider`, `MailToolCapabilityProvider`, `CalendarTodayCapabilityProvider`, `AzureInventoryCapabilityProvider`, `CompositeCapabilityProvider`, plus `IToolDocumentationProvider`, `ITeamsChatMetadataResolver`, alias matching, policy mode enums, and capability ID round-tripping. All of that is gone. The chat app is now: allowed-tool list + library + planner + validator + executor + repair loop.
