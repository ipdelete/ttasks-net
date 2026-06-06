# Chat turn lifecycle

This document explains when each part of the chat pipeline runs.

## Direct answer turn

Use this path when the user asks something the LLM can answer without external tool output.

1. Browser posts to `/api/chat`.
2. `ChatTurnService` gets or creates a page session.
3. Router prompt returns `mode = "answer"`.
4. The answer returns immediately.
5. No graph is built and no external command runs.

## Graph/action turn

Use this path when the user asks for external data, fan-out/fan-in work, shell-backed work, or summarization of tool output.

1. Browser posts to `/api/chat`.
2. Router prompt returns `mode = "graph"`.
3. `ICapabilityProvider` derives host-approved capabilities for the turn.
4. If no capabilities exist, the app returns the capability empty message.
5. Full-tool capabilities run a tool-authoring prompt to create candidate task templates.
6. Planner prompt receives only concrete capability IDs and descriptions.
7. `GraphPlanValidator` validates the plan, topology, task types, capability IDs, and payload safety.
8. `GraphPlanBuilder` turns capability IDs into `TaskGraph` tasks with host-resolved payloads.
9. `TaskExecutor` runs the graph.
10. If validation or execution fails, the app re-authors full-tool candidates and replans with failure observations until the repair budget is exhausted.
11. A final prompt task summarizes upstream outputs.
12. Graph/task state and results persist to SQLite.
13. Successful full-tool candidates used by the winning plan are promoted to the task library.

## Chat session setup

`ChatSessionRegistry` creates and caches one `LlmAgentSession` per browser page/session ID.

Sessions use:

- `SystemMessageMode.Replace`
- `SkipCustomInstructions = true`
- system prompt loaded from `Ttasks.ChatApp\system-message.md`

This makes the chat harness behavior reproducible and independent of repository or user custom instructions.

## Router flow

`ChatTurnService` first runs a small router prompt.

The router returns JSON:

```json
{
  "mode": "answer",
  "answer": "direct response",
  "planIntent": null
}
```

or:

```json
{
  "mode": "graph",
  "answer": null,
  "planIntent": "short actionable intent"
}
```

Only graph turns ask capability providers for external work.

## Graph planning

The planner receives a concrete capability catalog:

```json
[
  {
    "id": "cap-1",
    "taskType": "powershell",
    "displayName": "Read Teams chat AET SWE Chat",
    "description": "Read messages from a user-provided Teams chat."
  }
]
```

Planner rules:

- Non-prompt tasks must reference a listed capability ID.
- Non-prompt tasks must not include payload.
- Prompt tasks contain prompt payloads.
- A final prompt task summarizes upstream outputs.

## Host-side payload resolution

`GraphPlanBuilder` resolves executable payloads from selected capabilities.

It preserves provenance metadata:

- `capabilityId`
- `capabilityDisplayName`
- `capabilityKind`
- `capabilityPolicy`
- `toolName`
- `libraryKey`
- `libraryTaskId`

## Repair loop

Graph/action turns run with a bounded repair budget. A failed attempt records the previous plan, selected capability payloads, task errors, blocked tasks, and truncated output. The next attempt feeds those observations back into full-tool authoring and graph planning.

For full-tool capabilities, candidates are not saved to the task library before execution. The task library only receives candidates selected by a successful graph.

## Complete-result planning

Planner and full-tool authoring prompts include tool-neutral guidance for requests like "all", "total count", and "complete summary". The planner should use documented count/all/paging mechanisms, fetch minimal stable IDs first, page until a documented end condition, de-duplicate by stable ID, and avoid exact totals when only a bounded page was observed.
