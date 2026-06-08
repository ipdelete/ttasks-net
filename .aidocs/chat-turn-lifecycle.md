# Chat turn lifecycle

This document explains when each part of the chat pipeline runs.

## Direct answer turn

Use this path when the user asks something the LLM can answer without external tool output.

1. Browser posts to `/api/chat`.
2. `ChatTurnService` gets or creates a per-page LLM session.
3. Router prompt returns `mode = "answer"`.
4. The answer returns immediately.

## Graph/action turn

1. Browser posts to `/api/chat`.
2. Router prompt returns `mode = "graph"`.
3. `ConfigCapabilityProvider` returns the allowed tool list plus all task-library items as suggestions.
4. If no allowed tools are configured, the chat returns the empty message.
5. Planner prompt receives the user message, intent, allowed tool list, library suggestions, and complete-result guidance.
6. The planner emits a graph plan with `process` and `prompt` tasks. Process tasks can either inline a `process: { fileName, args }`, reference a `libraryItemKey`, or include a `librarySuggestion` for promotion.
7. The host resolves any `libraryItemKey` references by rendering the template into a `ProcessCommand`.
8. `GraphPlanValidator` enforces topology, task ID shape, prompt presence, and the tool-prefix boundary for every process command (and library suggestion).
9. `GraphPlanBuilder` builds the `TaskGraph` and tags each process task with its `planTaskId`.
10. `TaskExecutor` runs the graph (prompt handler is the LLM session; process handler resolves the executable via PATH/PATHEXT and runs it with structured argv).
11. If validation or execution fails, the host re-plans with failure observations (previous plan, failed task errors, blocked downstream tasks). The repair budget is `ChatApp:MaxGraphRepairAttempts`.
12. On success, `librarySuggestion`s attached to successful process tasks are promoted to the task library.
13. Graph/task state and results persist to SQLite.

## Repair loop

Each attempt is bounded. Failure observations include the previous plan JSON, the process specs that ran, and the failing task errors. The planner is told to change only what is needed to recover.

## Complete-result strategy

Planner and repair prompts include tool-neutral guidance: for "all/total/count/complete" requests, prove coverage with documented count/all/paging/cursor/offset/skip/next-page support, fetch minimal stable IDs, page until a documented end condition, de-duplicate by ID, and avoid exact totals from a single bounded page.
