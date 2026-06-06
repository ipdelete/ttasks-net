# Agent-authored graphs

## Concept

An **agent-authored graph** is a `TaskGraph` produced from a structured plan
created by an LLM or other planner. The planner does not receive direct shell,
Teams, filesystem, or provider tools. Instead, it proposes tasks, dependencies,
and metadata. The host application validates that plan, builds a normal
`TaskGraph`, and executes it through `TaskExecutor`.

This keeps the safety boundary clear:

- the planner describes work as data,
- the host validates and constrains that data,
- `ttasks` executes only registered task handlers,
- the store preserves enough metadata to inspect, resume, audit, or explain the
  graph later.

This document also defines the standard way for fan-in `PROMPT`/`AGENT` tasks
to include direct upstream task outputs in their provider request.

## Data shape

### Graph plan

A graph plan is an implementation-defined serializable object that contains at
least:

| Field      | Sense                                                    |
| ---------- | -------------------------------------------------------- |
| `graph`    | Graph-level title and metadata.                          |
| `tasks`    | Task declarations keyed by stable ids.                   |
| `edges`    | Dependency edges, equivalent to `graph.add(... after ...)`. |

Implementations MAY support additional host-specific fields, but unknown fields
MUST NOT silently grant new capabilities. Unknown fields are either ignored
under an explicit extension policy or rejected during host validation.

### Metadata

Tasks and graphs MAY carry user metadata. Metadata is intended for planning,
display, provenance, audit, and resume workflows. It MUST NOT affect task state
machine behavior or graph scheduling semantics.

Metadata is a mapping:

| Shape | Sense |
| ----- | ----- |
| key   | Non-empty string. |
| value | JSON-compatible scalar, array, object, or `null`. |

Examples:

```json
{
  "source": "chat-ui",
  "intent": "read Teams channels and summarize",
  "chatId": "48:notes",
  "kind": "teams-read",
  "fanout": 3
}
```

### Upstream result envelope

When a prompt/agent handler is configured to include upstream results, it MUST
compose a deterministic envelope from `TaskContext.upstream`. The envelope order
is part of the provider-facing request shape, so repeated executions of the same
graph MUST produce upstream entries in the same order.

Each direct upstream task entry contains:

| Field               | Sense                                           |
| ------------------- | ----------------------------------------------- |
| `id`                | Upstream task id.                               |
| `title`             | Upstream task title.                            |
| `description`       | Upstream task description.                      |
| `type`              | Upstream task type.                             |
| `status`            | Upstream task status.                           |
| `output`            | Upstream `TaskResult.output`, or empty string.  |
| `error`             | Upstream `TaskResult.error`, or `null`.         |
| `returnCode`        | Upstream `TaskResult.returnCode`, or `null`.    |
| `terminationReason` | Upstream result termination reason, or `null`.  |
| `metadata`          | Upstream task metadata, if supported.           |

The raw provider/subprocess result (`TaskResult.raw`) MUST NOT be included in
the default envelope.

## Rules

### Planning boundary

#### R-AGENTGRAPH-01 — Planner output is data, not authority

**Level:** MUST

An implementation that accepts an LLM-authored plan MUST treat that plan as
untrusted data. The plan MUST NOT execute directly. It MUST be validated and
translated into normal `Task` and `TaskGraph` objects before execution.

#### R-AGENTGRAPH-02 — Host validation gates capabilities

**Level:** MUST

The host MUST validate at least:

- task type is allowed,
- handler for that task type is registered or intentionally deferred,
- payload satisfies host policy for that task type,
- referenced ids are unique and well-formed,
- edges reference declared tasks,
- graph is acyclic under normal graph validation,
- timeouts and max worker counts are within host limits.

For example, a chat UI MAY allow `teams read 48:notes -n 20 --json` but reject
arbitrary PowerShell payloads or unapproved chat ids.

#### R-AGENTGRAPH-03 — Planner ids are stable semantic handles

**Level:** MUST

If a plan supplies task ids, those ids MUST be used as stable semantic handles
when constructing tasks and dependencies. Implementations MAY generate ids for
tasks that omit them, but generated ids MUST be stable within the constructed
graph and exposed to the store.

### Metadata

#### R-AGENTGRAPH-04 — Metadata is lifecycle-neutral

**Level:** MUST

Metadata MUST NOT alter task status transitions, graph readiness, retry
behavior, cancellation behavior, event ordering, or result normalization.

#### R-AGENTGRAPH-05 — Metadata keys and values are constrained

**Level:** MUST

Metadata keys MUST be non-empty strings. Metadata values MUST be
JSON-compatible. Implementations MUST reject metadata values that cannot be
serialized by their durable store representation.

#### R-AGENTGRAPH-06 — Stores roundtrip metadata

**Level:** MUST

If an implementation supports metadata on tasks or graphs, every conforming
store backend MUST preserve that metadata across `save` / `get` roundtrips.
Durable stores MUST return detached metadata snapshots just as they return
detached task and graph snapshots.

#### R-AGENTGRAPH-07 — Graph metadata is persisted with graph topology

**Level:** MUST

Graph metadata MUST be saved atomically with graph identity, membership, and
edges. A failure mid-save MUST leave the prior graph metadata and topology in a
consistent state.

#### R-AGENTGRAPH-08 — Metadata deletion is explicit

**Level:** SHOULD

Implementations SHOULD distinguish between omitting a metadata key on update and
explicitly deleting a metadata key. The exact API is `IMPL-DEFINED`, but silent
loss of metadata during unrelated task/graph updates SHOULD be avoided.

### Upstream fan-in composition

#### R-AGENTGRAPH-09 — Graphs pass direct upstream tasks to handlers

**Level:** MUST

When a graph executes a task, the handler receives the direct dependency tasks
through `TaskContext.upstream`. This restates `R-GRAPH-24` for agent-authored
graph scenarios: fan-in composition operates over direct dependencies only.

#### R-AGENTGRAPH-10 — Upstream inclusion is opt-in per handler or task

**Level:** MUST

`PROMPT` and `AGENT` handlers MUST NOT implicitly include upstream outputs in
every request. Upstream inclusion MUST be enabled explicitly by handler options,
task options, or another host-visible configuration surface.

This prevents accidental leakage of parent task output into unrelated LLM turns.

#### R-AGENTGRAPH-11 — Upstream envelopes are deterministic

**Level:** MUST

When upstream inclusion is enabled, the envelope order MUST be deterministic for
a fixed graph. Implementations MUST use the graph dependency declaration order
when it is available. Durable stores MUST preserve enough edge ordering metadata
to reconstruct the same order after save/load roundtrips.

If dependency declaration order is unavailable because an implementation exposes
only an unordered upstream map, the implementation MUST apply a documented stable
tie-breaker, such as lexical task id order. It MUST NOT rely on hash map,
database row, or scheduler completion order.

#### R-AGENTGRAPH-12 — Upstream composition preserves task payload separation

**Level:** MUST

The child task's own payload MUST remain distinguishable from upstream outputs.
An implementation MUST NOT concatenate upstream outputs into the prompt in a way
that makes it impossible for the provider to distinguish user instructions from
dependency results.

For example, a conforming composed prompt can use separate sections:

```text
Instruction:
Summarize the upstream Teams reads.

Upstream results:
[
  { "id": "read-notes", "output": "..." }
]
```

#### R-AGENTGRAPH-13 — Upstream composition excludes raw results by default

**Level:** MUST

The default upstream envelope MUST NOT include `TaskResult.raw`. Implementations
MAY expose an opt-in extension to include raw records, but it MUST be explicit
because raw records can contain provider-specific or sensitive data.

#### R-AGENTGRAPH-14 — Missing upstream results remain visible

**Level:** MUST

If an upstream task has no result, the envelope MUST still include that task's
id, type, title, status, and metadata. Its result fields MUST be represented as
empty or `null` values rather than causing prompt composition to fail.

#### R-AGENTGRAPH-15 — Upstream composition is available to PROMPT and AGENT

**Level:** SHOULD

Implementations SHOULD provide the same upstream inclusion behavior for both
one-shot `PROMPT` handlers and one-shot/shared-session `AGENT` handlers.

## Scenarios

### S-AGENTGRAPH-01 — Chat UI builds a fan-out/fan-in graph

**Given** a chat UI receives the user instruction "read three Teams channels and
summarize"
**And** the planner emits three `POWERSHELL` read tasks and one `PROMPT`
summary task
**When** the host validates allowed chat ids, command shapes, task ids, edges,
and max workers
**Then** the host constructs a normal `TaskGraph`
**And** the read tasks fan out
**And** the summary task runs after all three reads succeed.

Rules: R-AGENTGRAPH-01, R-AGENTGRAPH-02, R-AGENTGRAPH-03, R-GRAPH-14.

### S-AGENTGRAPH-02 — Metadata survives durable storage

**Given** an agent-authored graph with graph metadata
`{ "source": "chat-ui" }`
**And** a task with metadata `{ "chatId": "48:notes", "kind": "teams-read" }`
**When** the graph is saved to a durable store and reloaded
**Then** the graph metadata is present on the loaded graph
**And** the task metadata is present on the loaded task
**And** both metadata objects are detached snapshots.

Rules: R-AGENTGRAPH-04, R-AGENTGRAPH-05, R-AGENTGRAPH-06, R-AGENTGRAPH-07.

### S-AGENTGRAPH-03 — Summary prompt receives direct upstream outputs

**Given** a graph with three Teams read tasks as direct dependencies of a
summary `PROMPT` task
**And** the prompt handler has upstream inclusion enabled
**When** the summary task runs
**Then** the provider request contains the summary task's instruction
**And** contains a deterministic envelope for the three read task results
**And** does not include `TaskResult.raw`.

Rules: R-AGENTGRAPH-09, R-AGENTGRAPH-10, R-AGENTGRAPH-11,
R-AGENTGRAPH-12, R-AGENTGRAPH-13.

### S-AGENTGRAPH-04 — Missing result is summarized as status, not failure

**Given** a direct upstream task that has no `TaskResult`
**When** upstream inclusion is enabled for a prompt/agent task
**Then** the envelope includes the upstream task id, metadata, and status
**And** result fields are represented as empty or `null`
**And** prompt composition itself does not fail.

Rules: R-AGENTGRAPH-14.

## Out of scope

This contract intentionally does not specify:

- the exact JSON schema emitted by a particular chat UI or planner,
- the policy language used by the host to allow or reject commands,
- how metadata is rendered in a UI,
- whether metadata values are indexed for search,
- the exact prompt text used to introduce upstream results to an LLM provider,
- whether non-direct ancestors are included by convenience helpers.

Non-direct ancestors are out of scope by default: if a fan-in task needs an
earlier ancestor's output, the graph should declare that ancestor as a direct
dependency.
