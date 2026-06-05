# Store

## Concept

A **store** is the durable (or in-memory) seam between the in-process
runtime objects (`Task`, `TaskGraph`) and any backing storage. It is
the single component a `TaskExecutor` writes to when auto-persisting
lifecycle transitions and graph runs.

A store MUST expose exactly two collections:

- `tasks` — a mapping of task id → `Task`,
- `graphs` — a mapping of graph id → `TaskGraph`.

Each collection MUST behave like a `MutableMapping` keyed by the object's
own immutable id, with a `save(...)` convenience that writes the object
under its own id.

Two reference backends are defined:

- **InMemoryStore** — holds live references; reads and writes return the
  same in-process object.
- **SQLiteStore** — durable; reads return *detached snapshots*
  reconstructed from rows, independent of any live runtime instance.

A conforming implementation MUST provide an in-memory backend. It SHOULD
provide at least one durable backend (SQLite is the canonical choice,
but an implementation MAY substitute any equivalent embedded database).

The store is *not* responsible for transitions, scheduling, or events —
that's the executor and the graph. The store only persists.

## Data shape

### Store

| Member     | Sense                                          |
| ---------- | ---------------------------------------------- |
| `tasks`    | The task collection.                           |
| `graphs`   | The graph collection.                          |

### TaskCollection

| Member          | Sense                                                   |
| --------------- | ------------------------------------------------------- |
| `save(task)`    | Persist `task` under `task.id`.                         |
| `get(id)` / `[id]` | Return `Task` for id, or raise the "missing" error. |
| `set(id, task)` / `[id] = task` | Persist with explicit id (must match). |
| `delete(id)` / `del [id]` | Remove the task (and its result).             |
| iteration       | Yield ids in a stable order.                            |
| `length` / `size` | Number of stored tasks.                               |
| `has(key)` / `in` | True if key (id or `Task`) is present.                |

### GraphCollection

| Member            | Sense                                                  |
| ----------------- | ------------------------------------------------------ |
| `save(graph)`     | Persist `graph` under `graph.id` (and its member tasks). |
| `get(id)` / `[id]`| Return a `TaskGraph` reconstruction for id.            |
| `set(id, graph)`  | Persist with explicit id (must match).                 |
| `delete(id)`      | Remove the graph (member tasks remain).                |
| iteration / length / has | As for tasks.                                   |

### Reference identity

- In **in-memory** stores, `tasks[id]` returns the same `Task` object
  that was last `save`d under that id. Mutations made to that object
  outside the store are visible on subsequent reads.
- In **durable** stores, `tasks[id]` returns a freshly reconstructed
  `Task` snapshot. Two reads of the same id return two distinct
  objects, each independent of any in-memory original.

Both semantics are valid; users select the backend appropriate to their
durability needs.

## Rules

### Store shape

#### R-STORE-01 — Store exposes `tasks` and `graphs`

**Level:** MUST

Every store MUST expose a `tasks` collection and a `graphs` collection
as documented above. The two collections MUST be addressable
independently. A store MUST NOT silently couple them (e.g. iterating
graphs MUST NOT mutate the task collection).

**Reference tests:**
- `tests/test_store.py::test_exposes_tasks_and_graphs`
- `tests/test_store.py::test_tasks_and_graphs_are_independent`

#### R-STORE-02 — Protocol conformance is structural

**Level:** SHOULD

The store contract is intentionally structural. Implementations SHOULD
be substitutable based on shape alone — any object satisfying the
documented members satisfies the contract. Implementations MAY use a
nominal interface or protocol for type ergonomics but MUST NOT require
nominal inheritance.

**Reference tests:**
- `tests/test_store.py::test_satisfies_store_protocol`
- `tests/test_store.py::test_collections_satisfy_protocols_structurally`

### Collection mapping semantics

#### R-STORE-03 — `save` writes under the object's id

**Level:** MUST

`tasks.save(task)` MUST behave as `tasks[task.id] = task`.
`graphs.save(graph)` MUST behave as `graphs[graph.id] = graph`. Both
MUST upsert: re-saving overwrites the prior record without raising.

**Reference tests:**
- `tests/test_store.py::test_save_persists_under_task_id`
- `tests/test_store.py::test_save_persists_under_graph_id`

#### R-STORE-04 — Explicit-id setitem requires id match

**Level:** MUST

`tasks[id] = task` MUST raise a structured "id mismatch" error if
`id != task.id`. The same rule applies to `graphs[id] = graph`. This
prevents accidentally storing an object under the wrong key.

**Reference tests:**
- `tests/test_store.py::test_setitem_rejects_id_mismatch`
- `tests/test_sqlite_store.py::test_setitem_rejects_id_mismatch`

#### R-STORE-05 — Setitem validates type

**Level:** MUST

`tasks[id] = value` MUST reject any `value` that is not a `Task`.
`graphs[id] = value` MUST reject any `value` that is not a `TaskGraph`.
Rejection is synchronous and MUST NOT mutate the collection.

**Reference tests:**
- `tests/test_store.py::test_setitem_rejects_non_task`
- `tests/test_store.py::test_setitem_rejects_non_graph`
- `tests/test_sqlite_store.py::test_setitem_rejects_non_task`

#### R-STORE-06 — Missing key raises the structured "missing" error

**Level:** MUST

`tasks[id]` MUST raise the language's structured "missing key" error
(Python `KeyError`, or an implementation-defined equivalent such as an
explicit "not found" error or `undefined` — see R-STORE-07) when `id` is
not present. The same rule applies to `graphs[id]`.

**Reference tests:**
- `tests/test_store.py::test_missing_key_raises_key_error`
- `tests/test_sqlite_store.py::test_missing_task_raises_key_error`
- `tests/test_sqlite_store.py::test_missing_graph_raises_key_error`

#### R-STORE-07 — Implementation-defined missing-key representation

**Level:** IMPL-DEFINED

The canonical "missing key" surface is `IMPL-DEFINED`:
implementations MAY throw a structured "not found" error from a
`get(id)` method, OR return `undefined`, OR both — but MUST be
consistent within a single store. Implementations SHOULD document the
chosen surface in their README.

#### R-STORE-08 — `has` accepts both id and object

**Level:** MUST

`tasks.has(key)` / `key in tasks` MUST return `true` when `key` is the
id of a present task OR a present `Task` object. It MUST return `false`
for unknown ids, non-string keys (other than the supported object
type), and unhashable values. It MUST NOT raise.

The same rule applies to graphs.

**Reference tests:**
- `tests/test_store.py::test_contains_supports_id_and_task`
- `tests/test_store.py::test_contains_returns_false_for_unhashable_non_keys` (both classes)

#### R-STORE-09 — `delete` removes the record

**Level:** MUST

Deleting a task MUST remove the task and its associated result from
the collection. Deleting a missing id MUST raise the structured
"missing key" error (or the implementation's documented equivalent
per R-STORE-07).

Deleting a graph MUST remove the graph's metadata, membership, and
edges but MUST NOT remove its member tasks from the task collection.

**Reference tests:**
- `tests/test_store.py::test_delitem_removes_task`
- `tests/test_store.py::test_delitem_removes_graph`
- `tests/test_sqlite_store.py::test_delitem_removes_task`
- `tests/test_sqlite_store.py::test_delete_graph_keeps_member_tasks`
- `tests/test_sqlite_store.py::test_delitem_missing_task_raises_key_error`
- `tests/test_sqlite_store.py::test_delitem_missing_graph_raises_key_error`

#### R-STORE-10 — Iteration order is stable

**Level:** MUST

Iterating a collection MUST yield ids in a stable, deterministic order
for a fixed collection state.

- In-memory backends MUST yield insertion order.
- Durable backends MUST yield `(createdAt, id)` order (i.e. by creation
  timestamp, breaking ties by id).

`length` / `size` MUST equal the number of yielded ids.

**Reference tests:**
- `tests/test_store.py::test_iter_yields_task_ids_in_insertion_order`
- `tests/test_sqlite_store.py::test_iter_yields_persisted_ids`
- `tests/test_sqlite_store.py::test_iter_len_and_contains_variants`

### In-memory semantics

#### R-STORE-11 — In-memory collections hold live references

**Level:** MUST

The in-memory backend MUST store the exact object passed to
`save` / setitem. Subsequent reads MUST return the same object
(by identity / `===`).

Mutations to the object outside the store MUST be visible on the next
read.

**Reference tests:**
- `tests/test_store.py::test_setitem_then_getitem_returns_same_object`
- `tests/test_store.py::test_setitem_then_getitem_returns_same_graph`

#### R-STORE-12 — In-memory cancel helper

**Level:** SHOULD

The in-memory task collection SHOULD expose a `cancel(id)` helper that
calls `cancel()` on the held task. This is a convenience for code that
holds only the store, not the live task reference.

**Reference test:**
- `tests/test_store.py::test_cancel_updates_task_status_in_place`

### Durable semantics

#### R-STORE-13 — Durable reads return detached snapshots

**Level:** MUST

A durable backend's `tasks[id]` MUST return a freshly reconstructed
`Task` snapshot. Two reads of the same id MUST return distinct
objects. Mutations to one snapshot MUST NOT affect the stored row or
other snapshots; the change is only persisted on a subsequent `save`.

The same applies to graph reads.

**Reference tests:**
- `tests/test_sqlite_store.py::test_load_returns_detached_snapshot`
- `tests/test_sqlite_store.py::test_loaded_graph_holds_detached_tasks`

#### R-STORE-14 — Full task roundtrip

**Level:** MUST

A `Task` saved and reloaded through a durable backend MUST recover:

- `id`, `title`, `description`, `payload`, `type`,
- `status`, `error`, `timeout`, `blockedBy`,
- `createdAt`,
- the associated `TaskResult` if any, including `status`, `startedAt`,
  `finishedAt`, `duration`, `output`, `error`, `returncode`, and
  `terminationReason`.

`TaskResult.raw` MAY be dropped on roundtrip (it is the raw subprocess
completion record, not part of the persisted contract). Implementations
SHOULD load it as `null` when reconstructing.

**Reference tests:**
- `tests/test_sqlite_store.py::test_save_and_load_roundtrip`
- `tests/test_sqlite_store.py::test_task_result_roundtrips`
- `tests/test_sqlite_store.py::test_termination_reason_roundtrips`

#### R-STORE-15 — Full graph roundtrip

**Level:** MUST

A `TaskGraph` saved and reloaded through a durable backend MUST
recover:

- `id`, `title`, `createdAt`,
- the membership set (every task added to the graph),
- task-insertion order,
- every dependency edge in declaration order (after dedup per
  R-GRAPH-05),
- the `isFinally` and `isOptional` classifications.

The reloaded graph MUST also contain detached snapshots of its member
tasks (with their full state per R-STORE-14).

**Reference tests:**
- `tests/test_sqlite_store.py::test_graph_topology_roundtrips`
- `tests/test_sqlite_store.py::test_finally_metadata_roundtrips`

#### R-STORE-16 — Graph save is atomic with member-task save

**Level:** MUST

`graphs.save(graph)` MUST upsert every member task (as if calling
`tasks.save(member)`) and the graph metadata, membership, and edges
within a single transactional boundary. A partial failure mid-save
MUST leave the store in the prior consistent state.

**Reference tests:**
- `tests/test_sqlite_store.py::test_save_persists_graph_and_member_tasks_atomically`
- `tests/test_sqlite_store.py::test_explicit_save_then_run_is_idempotent`

#### R-STORE-17 — Durable backends survive process restart

**Level:** MUST

Opening a fresh store instance against the same backing storage MUST
return the previously persisted tasks and graphs unchanged (modulo the
detached-snapshot rule R-STORE-13).

**Reference tests:**
- `tests/test_sqlite_store.py::test_persists_across_store_instances` (tasks)
- `tests/test_sqlite_store.py::test_persists_across_store_instances` (graphs)

### Schema management (durable backends)

#### R-STORE-18 — Schema is versioned

**Level:** MUST

A durable backend MUST embed a schema version into the backing
storage. Opening a store MUST verify the version before any read or
write.

**Reference test:**
- `tests/test_sqlite_store.py::test_schema_version_row_updated_after_rebuild`

#### R-STORE-19 — Fresh empty storage is accepted

**Level:** MUST

Opening a durable store against truly empty backing storage (no known
tables) MUST succeed and stamp the current schema version.

**Reference test:**
- `tests/test_sqlite_store.py::test_fresh_empty_database_is_accepted`

#### R-STORE-20 — Version mismatch refuses to touch data

**Level:** MUST

If the backing storage contains known tables but the embedded schema
version does not match the current version (or the version row is
missing entirely), the store MUST refuse to open unless the caller
opts into destructive migration.

The refusal MUST be a structured error explaining the mismatch and
how to opt in. No data is modified.

**Reference tests:**
- `tests/test_sqlite_store.py::test_schema_mismatch_raises_without_opt_in`
- `tests/test_sqlite_store.py::test_populated_database_without_metadata_row_raises`

#### R-STORE-21 — Destructive migration is explicit and noisy

**Level:** MUST

The opt-in flag for destructive migration MUST:

- be off by default,
- when set, drop and rebuild the known tables,
- emit a warning (in Python: `UserWarning`; in TS: a console warning or
  equivalent) before destruction,
- stamp the current schema version after rebuild,
- never silently discard data.

The exact flag name is `IMPL-DEFINED`; Python uses
`allow_destructive_migration`.

**Reference tests:**
- `tests/test_sqlite_store.py::test_schema_mismatch_rebuilds_with_opt_in_and_warns`
- `tests/test_sqlite_store.py::test_schema_match_preserves_data`

### Concurrency

#### R-STORE-22 — Concurrent writes from the executor are safe

**Level:** MUST

A store MUST be safe to write to from multiple concurrent executor
workers (`graph.run` with `maxWorkers > 1`). It MUST NOT corrupt data
under contention, and it MUST NOT deadlock under typical workloads.

Implementations MAY achieve this through any combination of locks,
per-call connections, write serialization, or transactional retries.
The exact mechanism is `IMPL-DEFINED`.

**Reference test:**
- `tests/test_sqlite_store.py::test_wide_dag_persists_all_tasks_via_executor_auto_save`

#### R-STORE-23 — Store errors surface through the executor, not the lifecycle

**Level:** MUST

A `save` that fails MUST raise a structured error to the caller. When
the caller is the executor's auto-persistence path, that error is
captured into `persistenceErrors` and surfaced as a
`PERSISTENCE_FAILED` event per R-EVT-13 / R-EXEC-33; it MUST NOT
propagate further. Direct `store.tasks.save(...)` calls from user
code SHOULD propagate errors normally.

**Reference test:**
- `tests/test_executor.py::test_persistence_failure_emits_event_and_records_error` (cross-area)

### Run-integration

#### R-STORE-24 — Graph run persists at start and end

**Level:** MUST

When a graph runs against an executor with a configured store, the
graph MUST be persisted twice:

1. Once after validation passes, before any task is scheduled. This
   records the topology so an external observer can see the planned
   work.
2. Once after the run finishes (success or failure or exception).
   This records the final statuses.

Invalid graphs (cycles, missing deps, stale `RUNNING`) MUST NOT be
persisted — validation runs first per R-GRAPH-11..13.

**Reference tests:**
- `tests/test_sqlite_store.py::test_run_persists_graph_at_start_before_handlers_execute`
- `tests/test_sqlite_store.py::test_run_persists_graph_at_end_reflects_final_statuses`
- `tests/test_sqlite_store.py::test_run_does_not_persist_invalid_graph`
- `tests/test_sqlite_store.py::test_run_without_store_is_noop_for_persistence`

## Scenarios

### S-STORE-01 — Save and read back

**Given** an in-memory store and a freshly created `Task`
**When** `store.tasks.save(task)` is called
**Then** `store.tasks.has(task.id) === true`
**And** `store.tasks[task.id] === task` (same object)
**And** `store.tasks.size === 1`

Rules: R-STORE-03, R-STORE-08, R-STORE-11.

### S-STORE-02 — Durable roundtrip preserves a finished task

**Given** a SQLite store and a task that ran to `SUCCEEDED` with
output, started/finished timestamps, and `terminationReason = null`
**When** the task is saved and a fresh store instance is opened
against the same database
**Then** reading `store.tasks[task.id]` returns a `Task` snapshot
**And** every persisted field of the task and its `TaskResult`
matches the original (R-STORE-14)
**And** the returned object is *not* the same reference as the
original

Rules: R-STORE-13, R-STORE-14, R-STORE-17.

### S-STORE-03 — Graph roundtrip preserves topology and finally metadata

**Given** a SQLite store and a diamond graph `root -> {a, b} -> tail`
plus a finally task `cleanup` (optional) whose parents are `{a, b}`
**When** the graph is saved and reloaded from a fresh store instance
**Then** the reloaded graph's `roots()` is `[root]`,
`leaves()` is `[tail, cleanup]` (or insertion-ordered equivalent)
**And** `dependencies(tail)` is `[a, b]` in that order
**And** `isFinally(cleanup) === true`, `isOptional(cleanup) === true`
**And** every member task is a detached snapshot

Rules: R-STORE-13, R-STORE-15.

### S-STORE-04 — Save graph atomically updates member tasks

**Given** a SQLite store and a graph whose member task `A` has had
its status changed since the last save
**When** `store.graphs.save(graph)` is called
**Then** `store.tasks[A.id]` reflects the new status of `A`
**And** the graph metadata, membership, and edges also reflect the
current graph
**And** if the save fails mid-way, neither the graph nor `A` shows
partial changes

Rules: R-STORE-16.

### S-STORE-05 — Delete graph keeps member tasks

**Given** a stored graph `G` containing tasks `A, B, C`
**When** `del store.graphs[G.id]`
**Then** `G.id` is no longer in `store.graphs`
**And** `A`, `B`, `C` are still in `store.tasks` and still loadable

Rules: R-STORE-09.

### S-STORE-06 — Schema mismatch refuses to open

**Given** a SQLite file populated under an older schema version
**When** the caller opens a new `SQLiteStore` against that path
without `allowDestructiveMigration`
**Then** opening raises a structured error describing the mismatch
**And** the file is not modified

**And given** the caller retries with `allowDestructiveMigration = true`
**When** the store opens
**Then** a warning is emitted
**And** the known tables are dropped and rebuilt
**And** the schema version row reflects the new version

Rules: R-STORE-18, R-STORE-20, R-STORE-21.

### S-STORE-07 — Concurrent writes during graph run

**Given** a wide DAG (e.g. 32 independent tasks) and a SQLite store
configured on the executor with `maxWorkers = 8`
**When** the graph runs
**Then** every task reaches a terminal status without persistence
errors
**And** every task is persisted exactly once per terminal status
**And** `len(store.tasks) === 32` (plus any pre-existing tasks)

Rules: R-STORE-22, R-EXEC-33.

### S-STORE-08 — Save failure surfaces through executor events, not propagation

**Given** a store whose `tasks.save` raises every time and a task with
a registered handler
**When** the executor runs the task
**Then** the task still reaches `SUCCEEDED` (or its true terminal
status)
**And** `PERSISTENCE_FAILED` events are emitted alongside the
lifecycle events
**And** the executor's `persistenceErrors` records the failures
**And** the executor's caller observes the task's terminal `TaskResult`
without seeing the save error

Rules: R-STORE-23, R-EXEC-33, R-EVT-13.

## Out of scope

The store contract intentionally does not specify:

- the wire format of persisted rows beyond the field set in R-STORE-14
  and R-STORE-15,
- the specific embedded database used for durable storage (SQLite is
  reference, not requirement),
- the connection / pooling model (per-call, persistent pool, etc.),
- whether `TaskResult.raw` is preserved on durable roundtrip (it MAY
  be dropped),
- the wall-clock semantics of `createdAt` ordering when two records
  share the same timestamp (ties are broken by id; finer tie-breaking
  is not specified),
- migration paths between schema versions (the contract only requires
  refusal-or-destructive-rebuild, not in-place upgrade).
