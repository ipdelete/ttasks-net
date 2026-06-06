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
   - must be `process`
   - must target a full-tool capability
   - must set `fileName` to the approved tool
   - must provide `argsTemplate` as structured argv entries
5. Host exposes accepted proposals as in-memory candidate command capabilities.
6. Candidate capabilities become normal command capabilities for graph planning.
7. If the graph succeeds, candidates selected by the winning plan are promoted to task-library items.

This makes `mail` flexible without making it direct arbitrary shell access.

The chat app prefers `process` tasks for external CLI tools and reserves `powershell` tasks for scripts. This prevents shell-specific expansion problems such as PowerShell treating OData `$filter`, `$top`, and `$select` as variables.

For full-tool capabilities, ttasks validates the tool boundary rather than the tool strategy. For `mail`, the process executable must be `mail`, but the planner may choose any documented mail command, flags, or OData query shape needed for the user's request.

If a candidate fails, failure observations are fed back into the next authoring/planning attempt within a bounded repair loop.

Complete-result requests use tool-neutral planning guidance: the planner should look for documented count, all-results, paging, cursor, continuation-token, offset, skip, or next-page support; fetch minimal stable identifiers for counting; page until a documented end condition; de-duplicate by stable ID; and report exact totals only after coverage is proven.

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
