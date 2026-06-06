# Adding chat app capabilities

Capabilities are host-approved ways the chat app can use external tools. A capability can grant broad access to a whole tool, limited access to selected commands, or a fixed task template. The LLM can interpret intent and build graph structure, but the host owns capability policy, task-library persistence, and final execution.

Keep capabilities in `Ttasks.ChatApp`, not `Ttasks.Core`. `Ttasks.Core` remains the generic runtime for tasks, graphs, executors, stores, and LLM sessions.

## Capability policy modes

| Policy | Meaning | Current examples |
| --- | --- | --- |
| `full-tool` | The LLM may use the whole CLI/tool surface from tool docs, then propose task-library templates. | `mail` |
| `limited-tool` | The tool exists, but only approved subcommands/templates are exposed for now. | `az.account.list`, `az.group.list`, `az.resource.list` |
| `fixed-template` | The host exposes one deterministic reusable template. | `teams.chat.read:<chat-id>`, `calendar.today` |

The important distinction is that **capability does not always mean executable command**. A full-tool capability is permission plus documentation plus policy. The task library stores the executable templates that are created or reused under that policy.

## Core flow

1. A capability provider inspects the user's message and decides whether a host-approved capability is relevant.
2. For fixed or limited capabilities, the provider creates or reuses deterministic task-library templates immediately.
3. For full-tool capabilities, the provider exposes tool documentation such as `mail --help`.
4. The tool-authoring prompt asks the LLM to propose task-library templates using only the exposed tool docs.
5. The host validates the proposed template against the capability policy and exposes it as an in-memory candidate.
6. The host promotes successful candidates selected by the winning graph to the task library.
6. The graph planner receives concrete capability IDs for task-library-backed commands.
7. `GraphPlanValidator` rejects model-authored executable graph payloads and validates selected capabilities.
8. `GraphPlanBuilder` resolves capability IDs to executable task payloads host-side.
9. The graph runs and persisted task metadata records capability/library provenance.

## Files to update

### `Ttasks.ChatApp\Services\Capabilities.cs`

Add a provider that implements `ICapabilityProvider`.

For fixed-template or limited-tool capabilities, the provider should:

- Match user intent with narrow deterministic patterns.
- Create or reuse a `TaskLibraryDefinition`.
- Store stable metadata such as `capabilityKind`, `capabilityPolicy`, and `toolName`.
- Use `TaskLibraryTemplateRenderer` for runtime parameters.
- Return `CommandCapability` values with `"pending"` IDs; `CompositeCapabilityProvider` renumbers them.

For full-tool capabilities, the provider should:

- Match broad tool-level intent, such as mail/email/inbox for `mail`.
- Return a `ToolCapability` with:
  - `Kind`
  - `ToolName`
  - `Policy = "full"`
  - tool documentation from `IToolDocumentationProvider`
  - metadata identifying the tool and policy
- Let `ChatTurnService` run the tool-authoring step to create task-library-backed `CommandCapability` values.

Use task-library templates for anything executable. Prefer host-derived parameters such as `clock.now`, `clock.yesterday`, `clock.tomorrow`, `default`, or `metadata:<key>` when templates need runtime values.

### `Ttasks.ChatApp\Program.cs`

Register the provider and include it in the composite provider:

```csharp
builder.Services.AddSingleton<ExampleCapabilityProvider>();
builder.Services.AddSingleton<ICapabilityProvider>(services =>
    new CompositeCapabilityProvider(
    [
        services.GetRequiredService<TeamsCapabilityProvider>(),
        services.GetRequiredService<MailToolCapabilityProvider>(),
        services.GetRequiredService<ExampleCapabilityProvider>()
    ]));
```

Also update `CompositeCapabilityProvider.EmptyMessage` in `Capabilities.cs` if the new capability changes the unsupported-action guidance users see.

### `Ttasks.ChatApp\Services\ChatTurnService.cs`

Full-tool capabilities require a tool-authoring phase before graph planning.

The tool-authoring prompt should:

- Include only host-exposed tool capabilities and their docs.
- Ask for structured `ToolTaskProposal` output.
- Require `process` templates with `fileName` plus `argsTemplate` for external CLI tools.
- Validate that the process `fileName` stays inside the approved tool boundary.
- Expose accepted proposals as candidates; promote only candidates selected by a successful graph run.
- Convert saved templates into normal `CommandCapability` values for graph planning.

### `Ttasks.ChatApp\Services\GraphPlanValidator.cs`

Add a payload validation branch for the new `capabilityKind`.

Validation should match the policy:

- `full-tool`: allow structured process templates for the named tool. The tool executable is the boundary; do not hard-code strategy limits such as specific OData shapes or result caps in ttasks validation.
- `limited-tool`: allow only the specific approved subcommands/templates.
- `fixed-template`: match the exact rendered command shape.

Guidelines:

- Reject unknown `capabilityKind` values.
- Prefer exact string equality for fixed commands.
- Prefer strict anchored regexes for date or ID templates.
- Add negative tests proving malformed payloads are rejected.

### `Ttasks.ChatApp\Services\AdminService.cs`

Add an `AdminCapabilityItem` so `/admin/capabilities` explains:

- Capability kind
- Display name
- Description
- Task type
- Policy mode
- Availability rule
- Task-library behavior
- Example user requests

### `Ttasks.Tests\ChatAppExperimentTests.cs`

Add tests for:

- Provider intent matching.
- Full-tool documentation exposure.
- Template rendering for runtime parameters.
- Task-library item creation or reuse.
- Validator acceptance for real rendered payloads.
- Validator rejection for malformed payloads and unknown kinds.

## Safety rules

- Do not let the LLM author executable graph payloads directly.
- Full-tool capabilities are allowed, but they must become validated in-memory candidates before graph planning and are promoted to the task library only after success.
- Full-tool process templates must invoke only the approved tool executable.
- Limited-tool capabilities must stay narrow until explicitly expanded.
- Fixed-template capabilities should remain deterministic.
- For full-tool capabilities, let the planner choose the documented command strategy needed for the user request. If a command returns only a bounded page, downstream prompts should say that plainly instead of treating the page as a total.
- Complete-result requests should prove coverage using documented count, all-results, paging, cursor, continuation-token, offset, skip, or next-page support before reporting exact totals.
- Add timeouts when a command can hang or trigger auth/device-login flows.
- Preserve provenance metadata: `capabilityKind`, `capabilityPolicy`, `toolName`, `taskLibraryKey`, and `libraryTaskId`.

## Naming conventions

- Use stable, domain-scoped capability kinds, such as `mail`, `calendar.today`, or `az.resource.list`.
- Use matching task-library keys when the capability maps one-to-one with a reusable template.
- Use more specific keys for resource-specific items, such as `teams.chat.read:<chat-id>`.
- Keep display names user-facing and descriptions planner-facing.
- Use `full-tool`, `limited-tool`, and `fixed-template` consistently in admin-facing policy descriptions.

## Checklist

1. Choose the capability policy mode.
2. Add provider in `Capabilities.cs`.
3. Add or reuse task-library template metadata for executable commands.
4. Register provider in `Program.cs`.
5. Update composite unsupported-action message if needed.
6. Add or update tool-authoring support for full-tool capabilities.
7. Add strict validator branch for the new `capabilityKind`.
8. Add admin capability inventory entry.
9. Add provider, authoring, and validator tests.
10. Run `dotnet test C:\src\ttasks-net\TtasksNet.slnx`.
