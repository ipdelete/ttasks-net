# Task

## Concept

A **task** is the atomic unit of work in `ttasks`. It carries everything an
executor needs to dispatch the work (`type`, `payload`, `timeout`) and
everything the ledger needs to track the work over its lifetime (`id`,
`status`, `result`, `blockedBy`, `error`, `createdAt`).

Tasks are *value-like for identity* (two references with the same id are the
same task) and *carefully mutable for state* (most fields can change, but only
through the rules in this document and `state-machine.md`).

A **task result** is the normalized record of a single execution attempt. It
is attached to a task by the executor on every non-`BLOCKED` terminal
transition. A task that never ran (still `PENDING`, or ended in `BLOCKED`)
has no result.

## Data shape

Every conforming implementation MUST expose at least these fields on a task,
under names of its choosing (the names below are the canonical ones used in
the docs and in scenarios):

| Field         | Sense                                                              | Mutability                                       |
| ------------- | ------------------------------------------------------------------ | ------------------------------------------------ |
| `id`          | Stable identifier assigned at creation.                            | Immutable (R-SM-10).                             |
| `type`        | The kind of work (`BASH`, `POWERSHELL`, `PROMPT`, `AGENT`, …).     | Set at creation. SHOULD remain immutable.        |
| `payload`     | The work itself (script, prompt, …). Implementation-specific text. | Mutable until `SUCCEEDED` (R-SM-09).             |
| `title`       | Short human label. May be empty.                                   | Mutable until `SUCCEEDED`.                       |
| `description` | Long human description. May be empty.                              | Mutable until `SUCCEEDED`.                       |
| `timeout`     | Optional positive wall-clock budget in seconds. `null` = unbounded.| Mutable until `SUCCEEDED`.                       |
| `status`      | Current lifecycle state (see `state-machine.md`).                  | Read-only externally; changes via transitions.   |
| `error`       | Latest error message, if any.                                      | Managed by the state machine.                    |
| `result`      | Latest `TaskResult`, or `null` if no run has produced one.         | Read-only externally; set by the executor.       |
| `blockedBy`   | Id of the direct upstream parent that caused a block, or `null`.   | Read-only externally; set by the executor/graph. |
| `createdAt`   | Timestamp at which the task was constructed.                       | Immutable.                                       |

A **task result** MUST expose at least:

| Field                | Sense                                                              |
| -------------------- | ------------------------------------------------------------------ |
| `taskId`             | The id of the task this result belongs to.                         |
| `status`             | The terminal status this result records.                           |
| `startedAt`          | Timestamp when the handler began executing.                        |
| `finishedAt`         | Timestamp when the handler finished, was cancelled, or timed out.  |
| `duration`           | `finishedAt - startedAt`, in seconds (or implementation's unit).   |
| `output`             | Normalized text output, or empty string if none.                   |
| `error`              | Error message, or `null`.                                          |
| `returncode`         | Subprocess exit code, or `null` for non-subprocess handlers.       |
| `raw`                | The unmodified value the handler returned, or `null`.              |
| `terminationReason`  | One of `null`, `"exit_code"`, `"timeout"`, `"cancelled"`, `"handler"`. |

`terminationReason` semantics:

- `null` — task ended in `SUCCEEDED`.
- `"exit_code"` — a subprocess exited non-zero.
- `"timeout"` — wall-clock budget exceeded; the process was force-stopped.
- `"cancelled"` — cooperative cancel was honored.
- `"handler"` — handler threw / raised an unstructured error.

## Rules

### R-TASK-01 — Task type is required and constrained

**Level:** MUST

A task MUST be constructed with a `type` from the implementation's set of
recognized task types (at minimum `BASH`, `POWERSHELL`, `PROMPT`, `AGENT`).
Passing a value outside that set MUST be rejected at construction.

**Reference tests:**
- `tests/test_task.py::test_type_must_be_task_type`

### R-TASK-02 — Timeout, if present, is positive

**Level:** MUST

If a `timeout` is supplied, it MUST be strictly greater than zero. Zero and
negative values MUST be rejected at construction (and on any later
assignment that the implementation allows).

**Reference tests:**
- `tests/test_task.py::test_timeout_must_be_positive`
- `tests/test_task_factories.py::test_factory_timeout_validation_still_applies`

### R-TASK-03 — Timeout defaults to unbounded

**Level:** MUST

If no `timeout` is supplied at construction, the task's `timeout` MUST be
the sentinel for "no automatic timeout" (`null` / `undefined` / `None`).
Implementations MUST NOT silently substitute a default duration.

**Reference test:**
- `tests/test_task.py::test_timeout_defaults_to_no_automatic_timeout`

### R-TASK-04 — Title and description default to empty

**Level:** MUST

If `title` is not supplied, it MUST default to the empty string.
If `description` is not supplied, it MUST default to the empty string.
Implementations MUST NOT substitute placeholder text like the task id or
the payload.

**Reference test:**
- `tests/test_task_factories.py::test_bash_factory_sets_type_and_payload`

### R-TASK-05 — Each task gets a fresh identity

**Level:** MUST

Every constructed task MUST be assigned an identifier that is unique within
a single process invocation with overwhelming probability (e.g. a UUIDv4 or
equivalent). Two constructions in sequence MUST NOT collide on id.

**Reference test:**
- `tests/test_task_factories.py::test_factory_tasks_have_distinct_ids`

### R-TASK-06 — Identity equality and membership

**Level:** MUST

Two task references representing the same id MUST be treated as equal for
the purposes of membership in collections keyed by task (sets, maps,
graph node lookups). Identity equality MUST NOT consider `status`, `title`,
`result`, or any other field.

A task MUST NOT compare equal to a non-task value.

**Reference tests:**
- `tests/test_task.py::TestTaskHashingAndEquality::test_task_equality_is_by_id`
- `tests/test_task.py::TestTaskHashingAndEquality::test_task_set_and_dict_membership`
- `tests/test_task.py::TestTaskHashingAndEquality::test_distinct_ids_are_not_equal`
- `tests/test_task.py::TestTaskHashingAndEquality::test_task_not_equal_to_non_task`
- `tests/test_task.py::TestTaskHashingAndEquality::test_same_id_different_status_still_equal`

> Note: some runtimes do not provide a direct `__hash__` equivalent for arbitrary objects.
> Implementations satisfy this rule by keying collections on `task.id` rather
> than by object identity. Reference equality (`===`) is not required to imply
> task equality.

### R-TASK-07 — Status is read-only externally

**Level:** MUST

The current `status` MUST NOT be writable through the normal public field
assignment path. External callers must use the state-machine operations
(`transitionTo`, `cancel`, or equivalents) to change status.

**Reference test:**
- `tests/test_task.py::test_status_is_read_only`

### R-TASK-08 — Result and blockedBy are read-only externally

**Level:** MUST

The `result` and `blockedBy` fields MUST NOT be writable through normal
public field assignment. They are managed by the executor and the graph
on the task's behalf.

Implementations MAY expose internal seams (private setters, friend
helpers) that the executor uses to attach values.

**Reference tests:**
- `tests/test_task.py::TestBlockedBy::test_public_write_rejected`
- `tests/test_task.py::TestBlockedBy::test_result_public_write_rejected`

### R-TASK-09 — Non-SUCCEEDED tasks remain editable for retry

**Level:** MUST

A task whose status is not `SUCCEEDED` MUST allow assignment to the
mutable fields listed in the data shape table (`payload`, `title`,
`description`, `timeout`, `error`). This is what makes a `FAILED` task
repairable before a retry.

This rule applies in conjunction with R-SM-09: once a task reaches
`SUCCEEDED`, all those same assignments MUST be rejected.

**Reference test:**
- `tests/test_task.py::test_failed_tasks_remain_mutable_for_retry`

### R-TASK-10 — Built-in task type set

**Level:** MUST

The built-in task types `BASH`, `POWERSHELL`, `PROMPT`, and `AGENT` MUST
be defined and recognizable to handlers. Implementations MAY define
additional types.

The string values associated with these types (`"bash"`, `"powershell"`,
`"prompt"`, `"agent"`) SHOULD be stable across implementations so
serialized payloads round-trip.

**Reference tests:**
- `tests/test_task_factories.py::test_bash_factory_sets_type_and_payload`
- `tests/test_task_factories.py::test_powershell_factory_sets_type_and_payload`
- `tests/test_task_factories.py::test_prompt_factory_sets_type_and_payload`
- `tests/test_task_factories.py::test_agent_factory_sets_type_and_payload`

### R-TASK-11 — Type-specific factory constructors

**Level:** SHOULD

Implementations SHOULD provide a type-specific factory for each built-in
task type so callers can construct common tasks without importing the
task-type enum (e.g. `Task.bash(payload, …)`). Factories SHOULD forward
optional `title`, `description`, and `timeout` arguments and SHOULD apply
the same validation as direct construction.

**Reference tests:**
- `tests/test_task_factories.py::test_factories_accept_title_description_and_timeout`
- `tests/test_task_factories.py::test_factory_timeout_validation_still_applies`

### R-TASK-12 — Result is attached on every non-BLOCKED terminal transition

**Level:** MUST

When a task reaches `SUCCEEDED`, `FAILED`, or `CANCELLED`, the executor
MUST attach a `TaskResult` to the task **before** emitting the
corresponding terminal event. A `BLOCKED` task MUST NOT have a result
attached, because no handler ran.

The interaction between result attachment and terminal event ordering is
specified in detail in `events.md`. This rule pins the necessary
condition: by the time any observer sees a terminal event, `task.result`
is already populated (for non-`BLOCKED` terminals).

**Reference tests:**
- `tests/test_executor.py::test_succeeded_event_has_result_attached_before_emit`
- `tests/test_executor.py::test_failed_event_has_result_attached_before_emit`
- `tests/test_executor.py::test_cancelled_event_has_result_attached_before_emit`

### R-TASK-13 — TaskResult is immutable

**Level:** MUST

Once constructed, a `TaskResult` MUST NOT be mutated. Subsequent runs of
the same task produce *new* `TaskResult` values that replace the prior
attachment (with the prior result cleared on entry to `RUNNING`, per
R-SM-03).

**Reference test:**
- (Enforced by the dataclass `frozen=True` in the reference implementation.
  No direct test; implementations satisfy this structurally.)

### R-TASK-14 — TaskResult normalization

**Level:** MUST

The implementation MUST provide a normalization step that turns a handler's
return value into a `TaskResult`. The required cases:

- If the handler returned a **string**, the result's `output` MUST be that
  string and `raw` MUST also reference the string.
- If the handler returned a **subprocess-completion-like** value (an object
  carrying `stdout`, `stderr`, and `returncode`), the result MUST copy
  those fields into `output`, `error` (with empty stderr normalized to
  `null`), and `returncode`, and MUST retain the original object as `raw`.
- For any other value, the result's `output` MUST default to the empty
  string, `error` to `null`, `returncode` to `null`, and `raw` MUST be
  the unmodified handler return value.

Implementations MAY extend normalization to recognize additional types,
provided the rules above still hold for the cases they cover.

**Reference test:**
- (Exercised indirectly through executor tests that assert `result.output`
  and `result.raw` for handlers returning strings, subprocess completions,
  and other values.)

### R-TASK-15 — Repr / display is identity-first

**Level:** SHOULD

When an implementation provides a default human display for a task
(`repr`, `toString`, `inspect`, …), it SHOULD include at minimum the id,
the title, and the current status. The payload SHOULD NOT be included by
default because it may be large or sensitive.

**Reference test:**
- `tests/test_task.py::test_repr_includes_identity_title_and_status`

### R-TASK-16 — TaskResult identity across execute / submit / task.result

**Level:** MUST

For a given task attempt that produces a `TaskResult`, the following
MUST all refer to the **same** object (`===` / `is`):

- the value returned synchronously by `executor.execute(task)`,
- the value resolved by the future-like returned from
  `executor.submit(task)`,
- the value attached at `task.result` once the terminal event has been
  emitted.

Implementations MUST NOT silently copy, freeze-clone, or re-wrap the
`TaskResult` between attachment and surfacing. Callers rely on this
identity to correlate the result delivered to them with the result
observable on the task and through any persistence path.

A subsequent attempt (retry, or another `execute` after the task has
been reset to `PENDING`) produces a *new* `TaskResult` object that
replaces the prior attachment (per R-SM-03). Identity holds within an
attempt, not across attempts.

**Reference test:**
- `tests/test_e2e.py::test_live_bash_submit_executes_real_subprocess`
  (`task.result is future.result(...)`)

## Scenarios

### S-TASK-01 — Constructing a bash task

**Given** no existing tasks
**When** a caller constructs a task with type `BASH`, payload `"echo hi"`,
and no other fields
**Then** the task's `type` is `BASH`
**And** `payload` is `"echo hi"`
**And** `title` and `description` are empty strings
**And** `timeout` is the "no automatic timeout" sentinel
**And** `status` is `PENDING`
**And** `result` is empty
**And** `blockedBy` is empty
**And** `id` is a fresh unique identifier

Rules exercised: R-TASK-01, R-TASK-03, R-TASK-04, R-TASK-05.

### S-TASK-02 — Two constructions, two identities

**Given** the same arguments
**When** the caller constructs two tasks
**Then** the two tasks compare unequal
**And** placing both into an id-keyed collection yields two entries

Rules exercised: R-TASK-05, R-TASK-06.

### S-TASK-03 — Sharing identity across references

**Given** a task with id `X`
**When** a second reference is constructed against the same id `X`
**Then** the two references compare equal
**And** mutating the second reference's status to `RUNNING` does not affect
the equality

Rules exercised: R-TASK-06.

### S-TASK-04 — Repairing a failed task before retry

**Given** a task in `FAILED` with `error = "boom"`
**When** the caller assigns a new `payload` and clears `error`
**Then** both assignments succeed
**And** subsequent transition to `RUNNING` is permitted

Rules exercised: R-TASK-09, R-SM-08.

### S-TASK-05 — Succeeded task rejects mutation

**Given** a task that has reached `SUCCEEDED`
**When** the caller attempts to assign `title`, `description`, `payload`,
`timeout`, or `error`
**Then** every assignment is rejected
**And** the task's fields are unchanged

Rules exercised: R-SM-09, R-TASK-09.

### S-TASK-06 — Result attached before terminal event

**Given** a registered handler that returns the string `"ok"`
**When** the executor runs a task with that handler
**Then** by the time the `SUCCEEDED` event reaches any subscriber,
`task.result` is populated
**And** `task.result.output` equals `"ok"`
**And** `task.result.terminationReason` is the success sentinel (`null`)

Rules exercised: R-TASK-12, R-TASK-14.

### S-TASK-07 — Subprocess-like return normalization

**Given** a handler that returns a value with `stdout="hello\n"`,
`stderr=""`, and `returncode=0`
**When** the executor runs the task
**Then** `task.result.output` equals `"hello\n"`
**And** `task.result.error` is the empty / null sentinel
**And** `task.result.returncode` equals `0`
**And** `task.result.raw` references the original returned value

Rules exercised: R-TASK-14.

### S-TASK-08 — Blocked task has no result

**Given** a task whose upstream parent ended in `FAILED`
**When** the graph marks the task `BLOCKED`
**Then** the task's `result` remains empty
**And** the task's `blockedBy` equals the direct upstream parent's id

Rules exercised: R-TASK-12, R-SM-03 (carryover semantics), `blockedBy`
definition in `glossary.md`.

## Out of scope

The task contract intentionally does not specify:

- the wire format for `id` (UUID, ULID, snowflake, etc.)
- whether `createdAt` is wall-clock or monotonic
- which storage backend, if any, owns the task at any given moment
- whether `payload` is constrained to a specific syntax per `type`
- how `result.raw` is represented when the handler returns a non-string,
  non-process value (it just has to round-trip)
