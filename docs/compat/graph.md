# Graph

## Concept

A **graph** is a directed acyclic set of tasks with declared dependencies.
It owns:

- a stable identity (`id`),
- the task references it contains,
- the dependency edges between them,
- the policy for "finally" and "optional" tasks,
- a parallel scheduler that runs ready tasks through a `TaskExecutor`,
- post-run status views (`succeeded`, `failed`, `cancelled`, `blocked`),
- a single boolean verdict (`ok`).

The graph does *not* own task storage in the persistence sense — that's
the store's job (see `store.md`). The graph owns the *in-process* edges and
scheduler state.

The scheduler in `graph.run(executor)` is the single component allowed to
call `executor.markBlocked` on graph members. It is the place blocking
propagation actually happens.

## Data shape

A conforming graph MUST expose at least:

| Member                | Sense                                                                |
| --------------------- | -------------------------------------------------------------------- |
| `id`                  | Immutable graph identity.                                            |
| `title`               | Display string. May be empty.                                        |
| `createdAt`           | Construction timestamp.                                              |
| `add(task, ...)`      | Register a task with optional dependencies and finally/required flags. |
| `dependencies(task)`  | Return direct upstream tasks of `task`.                              |
| `isFinally(task)`     | Whether `task` was registered with `finally_=true`.                  |
| `isOptional(task)`    | Whether `task` is a finally task with `required=false`.              |
| `finallyTasks`        | All tasks registered as finally, in insertion order.                 |
| `optionalTasks`       | All tasks registered as optional finally.                            |
| `requiredTasks`       | All tasks whose failure contributes to `ok`.                         |
| `roots()`             | Tasks with no upstream dependencies.                                 |
| `leaves()`            | Tasks no other task depends on.                                      |
| `succeeded` / `failed` / `cancelled` / `blocked` | Status views over graph members.          |
| `optionalFailed` / `requiredFailed` / `requiredBlocked` | Subset views for reporting.          |
| `errors`              | Map of `taskId -> error` from the most recent run.                   |
| `ok`                  | `true` iff every required task succeeded and no run errors.          |
| `run(executor, ...)`  | Execute the DAG. Blocks until finished. Returns the graph for chaining. |

Collection protocols (`__iter__`, `__len__`, `__contains__`, `items`,
mapping-style `g[task] = deps`) are SHOULD, not MUST — they are
ergonomics.

### Task classifications inside a graph

For every task `t` in a graph:

- **normal** — registered without `finally_`. Becomes ready when every
  direct upstream task is `SUCCEEDED`.
- **finally** — registered with `finally_=true`. Becomes ready when every
  direct upstream task is *inactive* (terminal or otherwise non-progressing).
- **required** — failure or block contributes to `ok=false`. All normal
  tasks are required. Finally tasks are required unless explicitly opted
  out.
- **optional** — finally tasks marked `required=false`. Their failure or
  block does NOT contribute to `ok=false`. They are still scheduled and
  still emit events normally.

## Rules

### Graph identity and construction

#### R-GRAPH-01 — Graph has a stable identity

**Level:** MUST

Every graph MUST have a stable `id`, assigned at construction and
unchanged thereafter. `id` MUST be unique within a process invocation
with overwhelming probability.

#### R-GRAPH-02 — Title is an optional string

**Level:** MUST

`title` MUST default to the empty string when not supplied. Non-string
titles MUST be rejected at construction.

**Reference tests:**
- `tests/test_workflow.py::test_graph_accepts_title`
- `tests/test_workflow.py::test_graph_rejects_non_string_title`

#### R-GRAPH-03 — `createdAt` is set at construction

**Level:** MUST

`createdAt` MUST be populated at construction with the current time.

**Reference test:**
- `tests/test_workflow.py::test_graph_created_at_defaults_to_now`

### Adding tasks

#### R-GRAPH-04 — `add` requires a Task

**Level:** MUST

`add(task, ...)` MUST reject any `task` that is not a `Task` value.
`add` MUST reject any dependency in `after` that is not a `Task`. Both
rejections happen synchronously, before mutating graph state.

**Reference tests:**
- `tests/test_workflow.py::test_add_rejects_non_task_and_non_bool_finally`
- `tests/test_workflow.py::test_add_rejects_non_task_dependency`

#### R-GRAPH-05 — `add` deduplicates dependencies

**Level:** MUST

If `after` contains the same upstream task more than once, the graph
MUST store it exactly once. Iteration order MUST match first
appearance.

**Reference test:**
- `tests/test_workflow.py::test_add_deduplicates_repeated_dependencies`

#### R-GRAPH-06 — `finally_` and `required` flags are validated

**Level:** MUST

- `finally_` MUST be a boolean.
- `required` MUST be a boolean.
- `required=false` without `finally_=true` MUST be rejected with a
  structured error.

Re-adding an existing task MUST update its finally / optional
classification according to the latest call.

**Reference tests:**
- `tests/test_workflow.py::test_add_required_false_without_finally_raises`
- `tests/test_workflow.py::test_add_rejects_non_bool_required`
- `tests/test_workflow.py::test_finally_optional_and_required_views_follow_readded_task_metadata`

#### R-GRAPH-07 — `__setitem__` is a sugar form of `add`

**Level:** SHOULD

Implementations SHOULD support a mapping-style registration
`graph[task] = [upstream...]` that is equivalent to
`graph.add(task, after=upstream)`. Implementations MAY omit
this if it doesn't fit the language idioms.

**Reference test:**
- `tests/test_workflow.py::test_setitem_registers_task_and_dependencies`

### Topology and views

#### R-GRAPH-08 — `dependencies` returns direct upstream tasks

**Level:** MUST

`dependencies(task)` MUST return the direct upstream tasks of `task`
in the order they were declared (after deduplication per R-GRAPH-05).
It MUST NOT include transitive ancestors.

**Reference test:**
- `tests/test_workflow.py::test_dependencies_returns_direct_upstream_tasks`

#### R-GRAPH-09 — `roots` and `leaves`

**Level:** MUST

- `roots()` MUST return graph tasks with no declared dependencies.
- `leaves()` MUST return graph tasks that no other registered task
  depends on.

Both MUST return tasks in insertion order. An empty graph MUST yield
empty lists.

**Reference tests:**
- `tests/test_workflow.py::test_roots_returns_tasks_with_no_deps`
- `tests/test_workflow.py::test_leaves_returns_tasks_with_no_dependents`
- `tests/test_workflow.py::test_diamond_roots_and_leaves`

### Validation

#### R-GRAPH-10 — `run` validates max workers

**Level:** MUST

`run(executor, maxWorkers)` MUST reject `maxWorkers <= 0` with a
structured error before doing anything else.

**Reference test:**
- `tests/test_workflow.py::test_run_rejects_non_positive_max_workers`

#### R-GRAPH-11 — `run` rejects unregistered dependencies

**Level:** MUST

If any task declares a dependency on a task that is not in the graph,
`run` MUST raise a structured error before scheduling. No tasks are
executed.

**Reference test:**
- `tests/test_workflow.py::test_run_raises_on_unregistered_dep`

#### R-GRAPH-12 — `run` rejects cycles

**Level:** MUST

If the graph contains a cycle (including a self-loop), `run` MUST
raise a structured error before scheduling. No tasks are executed.

**Reference tests:**
- `tests/test_workflow.py::test_run_raises_on_self_loop`
- `tests/test_workflow.py::test_run_raises_on_two_node_cycle`
- `tests/test_workflow.py::test_run_raises_on_larger_cycle`

#### R-GRAPH-13 — `run` rejects stale RUNNING tasks

**Level:** MUST

If any graph task is in `RUNNING` at the moment `run` is called, `run`
MUST raise a structured error before scheduling. The state machine
prohibits self-transitions on `RUNNING` and the scheduler would
otherwise deadlock.

**Reference test:**
- `tests/test_workflow.py::test_run_rejects_stale_running_task`

### Scheduling

#### R-GRAPH-14 — Normal tasks ready when all parents SUCCEEDED

**Level:** MUST

A normal (non-finally) task becomes ready for submission when every
direct upstream task is in `SUCCEEDED`. Ready tasks MUST be submitted
through the configured `executor.execute` path (so all events,
persistence, and retry semantics apply).

**Reference tests:**
- `tests/test_workflow.py::test_linear_chain_runs_in_order`
- `tests/test_workflow.py::test_diamond_runs_with_parallelism`
- `tests/test_workflow.py::test_single_node_runs`

#### R-GRAPH-15 — Finally tasks ready when all parents inactive

**Level:** MUST

A finally task becomes ready for submission when every direct upstream
task is *inactive* — terminal (`SUCCEEDED`, `FAILED`, `CANCELLED`,
`BLOCKED`) or otherwise recorded in the run's error set.

Finally tasks MUST be submitted even if some parents failed, were
cancelled, or were blocked. This is the entire point of "finally."

**Reference test:**
- `tests/test_workflow.py::test_add_finally_runs_after_failed_and_blocked_tasks`

#### R-GRAPH-16 — Bad parent blocks normal descendants

**Level:** MUST

If a normal task has a direct upstream parent whose status is bad
(`FAILED`, `CANCELLED`, `BLOCKED`) and that parent cannot still recover
in the current run, the scheduler MUST mark the task `BLOCKED` (via
`executor.markBlocked`) with `blockedBy` set to that parent's id.

The scheduler MUST NOT submit a normal task whose parent is bad.

**Reference tests:**
- `tests/test_workflow.py::test_failure_blocks_descendants`
- `tests/test_workflow.py::test_failure_in_diamond_blocks_only_downstream`
- `tests/test_workflow.py::test_executor_error_blocks_descendants`
- `tests/test_workflow.py::test_executor_setup_error_blocks_descendants_without_deadlock`

#### R-GRAPH-17 — Blocking does not propagate through finally tasks

**Level:** MUST

A finally task is never marked `BLOCKED` by its parents' badness; it
runs once its parents become inactive (R-GRAPH-15). Whether the
scheduler then submits the finally task is governed by R-GRAPH-15
alone, regardless of how its parents ended.

**Reference test:**
- `tests/test_workflow.py::test_add_finally_runs_after_failed_and_blocked_tasks`

#### R-GRAPH-18 — Independent branches are unaffected

**Level:** MUST

Failure of a task in one branch MUST NOT block tasks in a different
branch that does not depend on it (directly or transitively).

**Reference test:**
- `tests/test_workflow.py::test_failure_does_not_affect_independent_branch`

#### R-GRAPH-19 — Failure terminates the run promptly

**Level:** MUST

When a failure or block leaves no remaining live work and not every
task has reached a terminal state, the scheduler MUST detect the
"no progress" condition and surface it as a `RuntimeError`-equivalent
rather than hang.

**Reference tests:**
- `tests/test_workflow.py::test_run_no_progress_guard_raises_runtime_error`
- `tests/test_workflow.py::test_failure_terminates_run_without_hanging`

#### R-GRAPH-20 — Parallelism is bounded by `maxWorkers`

**Level:** MUST

The scheduler MUST NOT have more than `maxWorkers` task executions
in flight at any moment. Implementations MAY default `maxWorkers` to a
small number (Python's reference is `4`). The default is `IMPL-DEFINED`
but SHOULD be small and documented.

**Reference test:**
- `tests/test_workflow.py::test_diamond_runs_with_parallelism`

### Retry and re-run

#### R-GRAPH-21 — Already-SUCCEEDED tasks count as satisfied dependencies

**Level:** MUST

Tasks that are already in `SUCCEEDED` at the start of a run MUST be
treated as satisfied dependencies. The scheduler MUST NOT re-execute
them, and the graph MUST still allow descendants to run.

This is what makes "re-run a partially completed graph" work.

**Reference tests:**
- `tests/test_workflow.py::test_clean_graph_can_be_run_again_without_blocking_done_dependencies`
- `tests/test_workflow.py::test_done_dependency_allows_pending_descendant_to_run`

#### R-GRAPH-22 — Carryover-blocked tasks are retry-eligible

**Level:** MUST

A task that enters the run already in `BLOCKED` MUST be eligible for
retry within the current run if its upstream parents become satisfied.

A task that becomes `BLOCKED` *during* the current run MUST stay
`BLOCKED` until that run finishes (no in-run retry). This keeps finally
readiness and "no progress" detection unambiguous.

**Reference tests:**
- `tests/test_workflow.py::test_carryover_blocked_with_succeeded_parent_recovers`
- `tests/test_workflow.py::test_failed_parent_added_after_child_retries_before_child_is_blocked`

#### R-GRAPH-23 — Blocked view resets at start of run

**Level:** MUST

At the start of `run`, the scheduler MUST clear any cross-run state
that would otherwise persist incorrectly into the new run. In
particular, the per-run `errors` map MUST be empty when the run begins.

**Reference tests:**
- `tests/test_workflow.py::test_blocked_resets_at_start_of_run`
- `tests/test_workflow.py::test_succeeded_empty_before_run`

### Upstream context

#### R-GRAPH-24 — Graph passes direct upstream task refs to handlers

**Level:** MUST

When the graph submits a task to the executor, the executor's
`TaskContext.upstream` for that task MUST contain exactly the task's
direct upstream parents (the values stored by `add`'s `after`
argument), keyed by task id. It MUST NOT include transitive ancestors,
sibling tasks, or finally tasks.

**Reference tests:**
- `tests/test_workflow.py::test_graph_passes_direct_upstream_task_refs`
- `tests/test_workflow.py::test_graph_passes_only_direct_upstream_task_refs`

### Status views and verdict

#### R-GRAPH-25 — Status views reflect graph members only

**Level:** MUST

`succeeded`, `failed`, `cancelled`, and `blocked` MUST each return
graph tasks currently in the corresponding status. They MUST NOT
include tasks from other graphs that happen to share an executor or
store.

**Reference tests:**
- `tests/test_workflow.py::test_succeeded_only_lists_graph_tasks_not_other_graphs`
- `tests/test_workflow.py::test_failed_lists_failed_tasks`
- `tests/test_workflow.py::test_cancelled_lists_cancelled_tasks`

#### R-GRAPH-26 — `errors` records executor-thrown errors per task

**Level:** MUST

The `errors` view MUST map a task id to the error its execution raised
during the most recent run, when execution raised. Tasks that succeed
or are blocked without executing MUST NOT appear in `errors`.

**Reference test:**
- `tests/test_workflow.py::test_graph_records_executor_errors`

#### R-GRAPH-27 — `ok` is the authoritative verdict

**Level:** MUST

`ok` MUST be `true` iff every required task is in `SUCCEEDED` and no
required task appears in `errors`. Specifically:

- An optional task in `FAILED` MUST NOT make `ok` false.
- A required task in `FAILED`, `CANCELLED`, or `BLOCKED` MUST make `ok` false.
- A required task that succeeded but raised through the executor
  (recorded in `errors`) MUST make `ok` false. This is the "setup
  error" case where the task reached `SUCCEEDED` before a later
  executor-level failure rolled in.
- An empty graph MUST report `ok = true`.
- An un-run graph MUST report `ok = false`.

**Reference tests:**
- `tests/test_workflow.py::test_ok_true_after_clean_run`
- `tests/test_workflow.py::test_ok_false_after_failure`
- `tests/test_workflow.py::test_ok_false_when_tasks_blocked`
- `tests/test_workflow.py::test_ok_false_before_run`
- `tests/test_workflow.py::test_ok_true_for_empty_graph`
- `tests/test_workflow.py::test_optional_finally_failure_does_not_make_graph_not_ok`
- `tests/test_workflow.py::test_required_finally_failure_makes_graph_not_ok`
- `tests/test_workflow.py::test_required_executor_error_makes_graph_not_ok_even_if_status_succeeded`

### Persistence

#### R-GRAPH-28 — Graph is persisted before and after run

**Level:** MUST

If the configured executor has a store, the graph MUST be persisted to
that store:

1. once after validation succeeds and before any task is scheduled,
2. once after the run finishes (success, failure, or exception).

Graph persistence failures MUST be captured (`graphPersistenceErrors`
on the executor) and MUST NOT propagate to the caller. There is no
dedicated graph-level event type; the executor's error list is the
discovery channel.

**Reference test:**
- Exercised in tests that use a store-backed executor and read
  `graph_persistence_errors` after the run.

### Result and chaining

#### R-GRAPH-29 — `run` returns the graph for chaining

**Level:** MUST

`run` MUST return the graph itself so callers can chain status reads
(e.g. `graph.run(executor).ok`).

**Reference tests:**
- `tests/test_workflow.py::test_run_returns_self`
- `tests/test_workflow.py::test_run_returns_self_for_empty_graph`

#### R-GRAPH-30 — Empty graph runs cleanly

**Level:** MUST

`run` on a graph with no tasks MUST return promptly without hanging,
without raising, and MUST report `ok = true` and empty status views.

**Reference tests:**
- `tests/test_workflow.py::test_empty_graph_runs_without_hanging`
- `tests/test_workflow.py::test_ok_true_for_empty_graph`

## Scenarios

### S-GRAPH-01 — Linear chain in order

**Given** three tasks `A -> B -> C` declared in a graph
**When** the graph is run on a default executor
**Then** subscribers see `A`'s lifecycle complete before `B`'s `STARTED`
**And** `B`'s lifecycle completes before `C`'s `STARTED`
**And** all three tasks end in `SUCCEEDED`
**And** `graph.ok == true`

Rules: R-GRAPH-14, R-GRAPH-29.

### S-GRAPH-02 — Diamond with parallelism

**Given** a diamond `root -> {left, right} -> tail` and
`maxWorkers >= 2`
**When** the graph is run
**Then** `left` and `right` may run concurrently after `root` succeeds
**And** `tail` does not start until both `left` and `right` succeed
**And** `graph.ok == true`

Rules: R-GRAPH-14, R-GRAPH-20.

### S-GRAPH-03 — Failure blocks descendants

**Given** a chain `A -> B -> C` where `A`'s handler fails
**When** the graph is run
**Then** `A` ends in `FAILED`
**And** `B` and `C` end in `BLOCKED`
**And** `B.blockedBy == A.id`
**And** `C.blockedBy == B.id`
**And** `graph.ok == false`

Rules: R-GRAPH-16, R-GRAPH-27.

### S-GRAPH-04 — Failure does not affect independent branch

**Given** two independent chains `A -> B` and `X -> Y` where `A` fails
**When** the graph is run
**Then** `X` and `Y` both end in `SUCCEEDED`
**And** `B` ends in `BLOCKED`
**And** `graph.failed == [A]`, `graph.blocked == [B]`
**And** `graph.succeeded` contains both `X` and `Y`

Rules: R-GRAPH-16, R-GRAPH-18, R-GRAPH-25.

### S-GRAPH-05 — Finally runs after failed parents

**Given** `A -> B`, plus a finally task `cleanup` with parents
`{A, B}`, where `A` fails (and `B` is consequently blocked)
**When** the graph is run
**Then** `cleanup` runs after both `A` and `B` reach terminal status
**And** `cleanup` runs regardless of `A`'s failure and `B`'s block
**And** if `cleanup` is required, `graph.ok` reflects `cleanup`'s
outcome
**And** if `cleanup` is optional, `graph.ok` ignores `cleanup`'s
outcome

Rules: R-GRAPH-15, R-GRAPH-17, R-GRAPH-27.

### S-GRAPH-06 — Optional finally failure does not break `ok`

**Given** a graph where every required task succeeds, and an optional
finally task fails
**When** the graph is run
**Then** `graph.ok == true`
**And** `graph.failed` contains the optional task
**And** `graph.optionalFailed` contains the optional task
**And** `graph.requiredFailed` is empty

Rules: R-GRAPH-27.

### S-GRAPH-07 — Re-run skips already-SUCCEEDED tasks

**Given** a chain `A -> B` after a first run in which `A` succeeded
and `B` failed, and the caller has repaired `B`'s payload
**When** the graph is run again
**Then** `A` is not re-executed
**And** `B` is re-executed and (if its repair worked) ends in `SUCCEEDED`
**And** `graph.ok == true`

Rules: R-GRAPH-21, R-SM-08.

### S-GRAPH-08 — Carryover-blocked task recovers

**Given** a task `child` that entered the run in `BLOCKED`, whose
parent `root` succeeds during this run
**When** the scheduler advances after `root` succeeds
**Then** `child` is submitted for execution
**And** `child` ends in `SUCCEEDED` (assuming its handler succeeds)

Rules: R-GRAPH-22, R-SM-08.

### S-GRAPH-09 — Cycle detection

**Given** a graph containing `A -> B -> A`
**When** the caller invokes `run`
**Then** `run` raises a structured "cycle" error before any task is
submitted
**And** no events are emitted for graph members

Rules: R-GRAPH-12.

### S-GRAPH-10 — No-progress guard

**Given** a graph where scheduling has reached a state with no live
work and not every task is finished (an executor bug or external
mutation)
**When** the scheduler loops with no progress
**Then** `run` raises a `RuntimeError`-equivalent describing which
tasks are stuck
**And** `run` returns control to the caller rather than hanging

Rules: R-GRAPH-19.

## Out of scope

The graph contract intentionally does not specify:

- whether the scheduler uses threads, an event loop, workers, or a
  combination,
- the exact default `maxWorkers` value,
- whether finally tasks may themselves have finally children (they may;
  this falls out of the readiness rules),
- the wire format of `errors` beyond `taskId -> error`,
- whether `ok` is cached or recomputed on each read,
- how the scheduler chooses among multiple simultaneously-ready tasks
  beyond respecting `maxWorkers`.
