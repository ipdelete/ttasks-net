# Safety model

The chat app lets the LLM plan and summarize, but the host owns execution.

## Boundaries

The LLM can:

- route a turn as direct answer or graph work
- propose graph topology
- compose `process` commands (within the allowed-tool boundary)
- reuse task-library templates via `libraryItemKey`
- suggest new templates via `librarySuggestion`
- write prompt task instructions

The LLM cannot:

- execute tools directly
- run process commands whose `fileName + leading non-flag args` head does not match an allowed tool prefix
- invent identifiers the user did not provide

## Validation

`GraphPlanValidator` enforces:

- valid unique task IDs
- known task types (`process`, `prompt`)
- at least one prompt task
- valid edge references, no self edges, acyclic graph
- process tasks include a `process` spec
- the process command head matches an allowed tool prefix from `ChatApp:AllowedTools`
- any `librarySuggestion` matches the process fileName and also stays inside the allowed-tool boundary

## Execution

The graph executor delegates `TaskType.Process` to the built-in process handler, which resolves the executable via PATH/PATHEXT and runs it with structured argv (no shell). Prompts run through the per-page LLM session.

## Allowed-tool model

The string in `AllowedTools:Prefix` is the boundary:

- `mail` — whole tool surface
- `teams read` — only this subcommand
- `az account list` — only this specific command

Less data, fewer abstractions, same guarantees. The previous policy-mode enum (`full-tool`/`limited-tool`/`fixed-template`) is gone; the prefix length encodes the same intent.

## Repair loop

Failed candidates are not promoted. Only library suggestions attached to successful process tasks of a successful graph become durable library items.
