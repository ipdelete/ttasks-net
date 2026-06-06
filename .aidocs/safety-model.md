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
- execute PowerShell or process payloads outside the current approved capability set

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

`ChatTurnService.RegisterExecutableCapabilities` registers PowerShell and process handlers that only execute payloads present in the current host-approved capability set.

If the graph contains any other executable payload, execution fails.

## Policy-specific safety

### Full-tool

Full-tool capabilities allow flexible task authoring, but candidate templates must:

- invoke only the approved tool
- use structured process argv for CLI tools
- pass host validation before graph planning

The approved executable is the boundary for full-tool process capabilities. Strategy details such as filters, result size, count attempts, or paging approaches belong to the planner and tool documentation, not deterministic ttasks validation.

Candidates are promoted to task-library items only after a successful graph selects them. Failed candidates remain transient observations for the repair loop.

Current full-tool example: `mail`.

### Limited-tool

Limited-tool capabilities allow only approved commands or templates.

Current limited-tool example: Azure inventory. The LLM cannot use arbitrary `az` commands.

### Fixed-template

Fixed-template capabilities are deterministic. The host creates or reuses a specific template and validates its rendered command shape.

Current fixed-template examples: Teams reads and today's calendar.

## Design principle

Capability providers define what is possible. The task library records what is reusable. The graph planner decides how approved tasks connect. The executor runs only host-approved payloads.
