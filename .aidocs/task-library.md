# Task library

The task library stores reusable process-task templates so the chat app can remember strategies that worked.

## What gets stored

Each library item includes:

- stable key
- display name and description
- process `fileName` and `argsTemplate` (with `{token}` placeholders)
- template parameters
- metadata
- created timestamp

The library is backed by `ITaskStore`. Library items are normal tasks with a `taskLibraryItem` metadata flag, not a separate schema.

## When items are created

1. **Seeded**: `ChatApp:LibrarySeed` entries pre-populate deterministic templates on app startup (`calendar.today`, `az.account.list`, etc.).
2. **Promoted from a successful graph**: when the planner emits a `process` task with a `librarySuggestion`, the host promotes that suggestion to the library only after the graph succeeds. Failed candidates stay transient.
3. **Updated**: re-adding an existing key replaces the template if any field differs.

## Template parameters

Supported parameter sources:

- `clock.now`
- `clock.yesterday`
- `clock.tomorrow`
- `default`
- `metadata:<key>`

Example process template:

```text
fileName: mail
argsTemplate: [
  "search",
  "--filter",
  "receivedDateTime ge {start:yyyy-MM-dd}T00:00:00Z and receivedDateTime lt {end:yyyy-MM-dd}T00:00:00Z",
  "--top", "{top}",
  "--count", "--all-pages", "--json"
]
parameters: [
  { name: "start", source: "clock.yesterday" },
  { name: "end",   source: "clock.now" },
  { name: "top",   source: "default", defaultValue: 100 }
]
```

## Promotion vs reuse

- The planner reuses a library item by setting `libraryItemKey` on a process task. The host renders it (with optional `libraryParameters` overrides) into a concrete `ProcessCommand` before validation and execution.
- The planner suggests a new item by attaching `librarySuggestion` to a process task. The host stores it only after the graph succeeds.

This separates "things that worked" (library) from "things the model guessed" (transient).
