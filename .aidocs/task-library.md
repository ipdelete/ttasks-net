# Task library

The task library stores reusable task templates. It lets the chat app remember useful operations and resource-specific commands over time.

## What gets stored

Task-library items include:

- stable key
- display name
- description
- task type
- payload template
- process file name and argument template, for `process` task items
- template parameters
- metadata
- created timestamp

The library is backed by the existing `ITaskStore`. Library items are normal tasks with metadata, not a separate database schema.

## When items are created

Task-library items are created when:

- a provider sees a reusable resource, such as a Teams chat ID
- a fixed-template capability is first needed, such as `calendar.today`
- a full-tool candidate template is selected by a graph that succeeds, such as a mail search

Existing task-library items are reused on later turns.

## Template parameters

Templates support host-rendered parameters:

- `clock.now`
- `clock.yesterday`
- `clock.tomorrow`
- `default`
- `metadata:<key>`

Examples:

```text
calendar list -s {today:yyyy-MM-dd}T00:00:00 -e {tomorrow:yyyy-MM-dd}T00:00:00 -n {top} --json
```

```text
teams read {chatId} -n {maxMessages} --json
```

Process templates store the executable separately from argv templates:

```text
fileName: mail
argsTemplate: ["search", "--query", "?$filter=receivedDateTime ge {yesterday:yyyy-MM-dd}T00:00:00Z&$orderby=receivedDateTime desc&$top={top}", "--json"]
```

Use process templates for external CLI tools. Use string payload templates for shell/script task types such as PowerShell. For full-tool capabilities, the app first executes in-memory candidates during the repair loop, then stores useful strategies selected by a successful graph instead of forcing every request through one fixed query shape.

## Teams alias enrichment

When a Teams chat URL or ID is first seen:

1. `TeamsCapabilityProvider` extracts the chat ID.
2. `ShellTeamsChatMetadataResolver` calls `teams chat-get <chat-id> --json`.
3. The provider stores the chat topic and normalized aliases in task-library metadata.
4. Later requests like `read teams chat aet swe chat` can resolve the saved library item by alias.

Relevant metadata:

- `teamsChatId`
- `teamsChatTopic`
- `teamsChatAliases`
- `teamsChatMetadataError`

## Provenance metadata

Graph tasks created from library-backed capabilities preserve:

- `capabilityId`
- `capabilityDisplayName`
- `capabilityKind`
- `capabilityPolicy`
- `toolName`
- `libraryKey`
- `libraryTaskId`
