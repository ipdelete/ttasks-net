# Capability system

Capabilities are host-approved ways the chat app can use external tools. A capability can grant broad access to a whole tool, limited access to selected commands, or a fixed task template.

## Policy modes

| Policy | Meaning | Current examples |
| --- | --- | --- |
| `full-tool` | The LLM can use exposed tool docs to propose reusable task templates. | `mail` |
| `limited-tool` | The tool exists, but only approved commands/templates are exposed. | `az.account.list`, `az.group.list`, `az.resource.list` |
| `fixed-template` | The host exposes a deterministic reusable template. | `teams.read`, `calendar.today` |

Capability does not always mean executable command. A full-tool capability is permission plus documentation plus policy. The task library stores executable templates created or reused under that policy.

## Capability flow

All providers implement `ICapabilityProvider`.

Providers return a `CapabilitySet` with:

- `Capabilities`: concrete command-backed capabilities ready for graph planning.
- `Tools`: full-tool capabilities that still need task-library authoring.
- `EmptyMessage`: user-facing message when no capability is available.

`CompositeCapabilityProvider` combines provider results and renumbers command capabilities as `cap-1`, `cap-2`, and so on.

## Full-tool authoring

Full-tool capabilities, currently `mail`, expose tool docs instead of immediate commands.

Flow:

1. Provider matches broad tool intent.
2. Provider loads docs with `IToolDocumentationProvider`, such as `mail --help`.
3. `ChatTurnService` asks the LLM for `ToolTaskProposal` JSON.
4. Host validates each proposal:
   - must be `powershell`
   - must target a full-tool capability
   - must invoke only the approved tool
   - must not use command chaining, pipes, or shell metacharacters
5. Host saves accepted proposals as task-library items.
6. Saved items become normal command capabilities for graph planning.

This makes `mail` flexible without making it direct arbitrary shell access.

## Limited-tool capabilities

Limited-tool capabilities expose only approved commands.

Current Azure examples:

- `az account list --only-show-errors --output json`
- `az group list --only-show-errors --output json`
- `az resource list --only-show-errors --output json`

The LLM cannot use arbitrary `az` commands yet. Expanding Azure means deliberately adding new allowed templates and validator checks.

## Fixed-template capabilities

Fixed-template capabilities create deterministic task-library items.

Examples:

- `calendar.today` renders today's date range with `clock.now` and `clock.tomorrow`.
- `teams.read` creates a resource-scoped template for a specific Teams chat ID.

## Current providers

| Provider | Policy | Purpose |
| --- | --- | --- |
| `TeamsCapabilityProvider` | fixed-template | Read known Teams chats and reuse aliases. |
| `MailToolCapabilityProvider` | full-tool | Expose the full `mail` CLI through `mail --help` and task authoring. |
| `CalendarTodayCapabilityProvider` | fixed-template | Read today's calendar events. |
| `AzureInventoryCapabilityProvider` | limited-tool | Expose selected read-only Azure inventory commands. |
