# Safety model

The chat app allows the LLM to plan and summarize, but not to directly execute arbitrary commands.

## Boundaries

The LLM can:

- route a turn as direct answer or graph work
- propose graph topology
- choose host-provided capability IDs
- write prompt task payloads
- propose task-library templates for full-tool capabilities

The LLM cannot:

- directly execute tools
- add executable payloads to non-prompt graph tasks
- invent capability IDs
- bypass host-side payload resolution
- execute PowerShell payloads outside the current approved capability set

## Validation boundary

`GraphPlanValidator` enforces:

- valid unique task IDs
- known task types
- at least one prompt task
- valid edge references
- no self edges
- acyclic graph
- no model-authored executable payloads for non-prompt tasks
- every non-prompt task references an available capability
- capability task type matches the plan task type
- capability payload matches its policy/kind

Unknown `capabilityKind` values are rejected.

## Execution boundary

`ChatTurnService.RegisterPowerShellCapabilities` registers a PowerShell handler that only executes payloads present in the current host-approved capability set.

If the graph contains any other PowerShell payload, execution fails.

## Policy-specific safety

### Full-tool

Full-tool capabilities allow flexible task authoring, but accepted templates must:

- invoke only the approved tool
- use one CLI command
- avoid pipes
- avoid command chaining
- avoid shell metacharacters
- become task-library items before graph planning

Current full-tool example: `mail`.

### Limited-tool

Limited-tool capabilities allow only approved commands or templates.

Current limited-tool example: Azure inventory. The LLM cannot use arbitrary `az` commands.

### Fixed-template

Fixed-template capabilities are deterministic. The host creates or reuses a specific template and validates its rendered command shape.

Current fixed-template examples: Teams reads and today's calendar.

## Design principle

Capability providers define what is possible. The task library records what is reusable. The graph planner decides how approved tasks connect. The executor runs only host-approved payloads.
