# Glossary

Defined terms used throughout the compat docs. Terms are listed alphabetically.
When a doc uses a term in its defined sense, it links here.

A term defined in this glossary always takes precedence over a colloquial
reading. If a doc needs a term that isn't here yet, the doc is incomplete —
add the term first.

---

### active status

A task status from which the task may still progress without external
intervention. The active statuses are `PENDING` and `RUNNING`. See
`state-machine.md`.

### bad status

A task status which, when held by a direct upstream parent, prevents
ready descendants from running. The bad statuses are `FAILED`,
`CANCELLED`, and `BLOCKED`. See `state-machine.md`.

### blocked-by

The id of the direct upstream parent whose status caused a task to be
marked `BLOCKED`. Set at the moment a task enters `BLOCKED`; cleared at
the moment a task enters `RUNNING` (see rule R-SM-03). Never references
a transitive ancestor.

### compat docs

This directory. The implementation-neutral contract every conforming
implementation must satisfy. See `overview.md`.

### conformance level

One of `MUST`, `SHOULD`, `MAY`, `IMPL-DEFINED`. Applied to every rule
in the compat docs. See `overview.md`.

### conforming implementation

An implementation that satisfies every `MUST` rule in the compat docs.
A conforming implementation MAY decline `SHOULD` rules with documented
justification and MAY ignore `MAY` rules entirely.

### direct upstream parent

A task that appears as an immediate dependency in a graph edge. Contrast
with **transitive ancestor**, which is any task reachable by following
edges upstream more than one step.

### event

A timestamped record of something that happened to a task. The defined
event types are `STARTED`, `SUCCEEDED`, `FAILED`, `CANCELLED`, `BLOCKED`,
`PROGRESS`, and `OUTPUT`. Events are delivered through an **event bus**.
See `events.md`.

### event bus

The pub/sub channel through which an executor publishes events and
subscribers observe them. The bus guarantees observer isolation: a
failing subscriber does not affect other subscribers or task outcomes.
See `events.md`.

### executor

The component that runs tasks. An executor accepts handler registrations
per task type, performs lifecycle transitions, emits events, and
optionally persists state through a store. See `executor.md`.

### finally task

A task added to a graph with the "finally" marker. Finally tasks run
after their dependencies have reached any terminal status (not only
`SUCCEEDED`). Used for cleanup, reporting, and post-run hooks. See
`graph.md`.

### graph

A directed acyclic set of tasks with declared dependencies. Tasks within
a graph are scheduled according to their dependencies, blocked when
their upstream is bad, and reported on collectively through status
views like `succeeded`, `failed`, `blocked`. See `graph.md`.

### handler

A function registered with an executor for a given task type. When the
executor runs a task of that type, it invokes the handler with a task
context and treats the handler's return value (or thrown error) as the
task outcome.

### identity

A task's stable id, assigned at creation and unchanging for the lifetime
of the task. Equality and any membership semantics are by id. See
rule R-SM-10.

### IMPL-DEFINED

A conformance level meaning the behavior must exist in some form but
the specific shape is up to the implementation. Implementations should
document their chosen shape.

### MAY

A conformance level. Optional behavior. An implementation can provide
or omit a `MAY` rule without affecting conformance.

### MUST

A conformance level. Required for conformance. A non-conforming
implementation cannot claim compatibility.

### optional task

A task added to a graph with the "optional" flag (only valid alongside
the "finally" marker). An optional task's failure does not make the
graph not-ok. Used for best-effort cleanup or non-critical reporting.
See `graph.md`.

### reference implementation

The Python implementation (`ttasks`). The reference implementation is
*not* the spec — the compat docs are. The reference implementation may
itself be wrong relative to a `MUST` rule.

### reference test

A test in the Python reference implementation cited as evidence that
the reference satisfies a given rule. Reference tests are evidence,
not specification. A rule with no reference test is still authoritative.

### required task

A task that participates in the graph's overall ok / not-ok verdict.
All non-finally tasks are required by default. A finally task is required
unless explicitly marked optional. See `graph.md`.

### result

The normalized record of a single task execution. Attached to a task by
the executor on every non-`BLOCKED` terminal path. `BLOCKED` tasks have
no result because no handler ran. See `task.md`.

### retry policy

A configuration value that tells an executor how many times to re-run a
single task whose handler failed, and how long to wait between attempts.
A retry policy never applies to cancellation or to graph-level re-runs.
See `executor.md`.

### rule

A numbered, testable statement in the compat docs. Each rule has a
stable id of the form `R-AREA-NN` (see `conformance.md`) and a
conformance level.

### scenario

A Given / When / Then behavior in the compat docs that exercises one or
more rules. Scenarios are deliberately implementation-free.

### sink status

A task status with no outgoing transitions. The sink statuses are
`SUCCEEDED` and `CANCELLED`. Once entered, a task in a sink status
never moves again. See `state-machine.md`.

### SHOULD

A conformance level. Strongly recommended. Deviation requires explicit
justification.

### state machine

The complete set of allowed task status transitions and their entry
effects. The authoritative definition lives in `state-machine.md`.

### store

A persistence backend for tasks and graphs. Defined by two collections,
`tasks` and `graphs`, each behaving as an id-keyed mapping. Built-in
backends include an in-memory store and a SQLite-backed store. See
`store.md`.

### task

A single unit of work tracked by the ledger. Has identity, a type, a
payload, a status, optional metadata, and at most one current result.
See `task.md`.

### task context

The read-only view of a task and its upstream dependencies passed to a
handler when the executor runs that task. Also exposes the cooperative
cancellation signal and the progress emitter. See `executor.md`.

### task type

The kind of work a task represents. The built-in types are `BASH`,
`POWERSHELL`, `PROMPT`, and `AGENT`. An executor dispatches tasks to
handlers by type. Implementations MAY support additional types.

### terminal status

Any status from which the current run cannot continue. The terminal
statuses are `SUCCEEDED`, `FAILED`, `CANCELLED`, and `BLOCKED`. Note
that some terminal statuses (`FAILED`, `BLOCKED`) are **not** sinks —
they can re-enter `RUNNING` on a retry or re-run.

### transition

A status change applied to a task. Allowed transitions are listed in
the table in `state-machine.md`. Disallowed transitions MUST be
rejected without mutating task state (rule R-SM-01).

### transitive ancestor

Any task reachable by following dependency edges upstream more than one
step. Contrast with **direct upstream parent**. Implementations MUST
NOT report a transitive ancestor as a task's `blockedBy`.

### upstream

A direct dependency of a task in a graph. The upstream of a task with
no declared dependencies is empty. The upstream is what is exposed to
the handler through the task context.
