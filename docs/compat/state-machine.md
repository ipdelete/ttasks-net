# Task state machine

This document defines the canonical `Task` lifecycle. It is the single most
important file in the compat docs: most observable behavior follows from
these rules.

## Status values

| Status      | Meaning                                                            |
| ----------- | ------------------------------------------------------------------ |
| `PENDING`   | Task has been created but has not yet started.                     |
| `RUNNING`   | A handler is currently executing for this task.                    |
| `SUCCEEDED` | Handler completed normally. Terminal.                              |
| `FAILED`    | Handler raised or exited non-zero. May be retried.                 |
| `CANCELLED` | Task was cancelled before or during execution. Terminal.           |
| `BLOCKED`   | An upstream dependency prevented this task from running. May retry.|

Each status falls into exactly one of three classifications:

- **active** — task may still progress without intervention. `PENDING`, `RUNNING`.
- **sink** — task is terminal and never moves again. `SUCCEEDED`, `CANCELLED`.
- **bad** — task is in a state that blocks descendants from running. `FAILED`, `CANCELLED`, `BLOCKED`.

Notes:
- `SUCCEEDED` is a sink but not bad.
- `CANCELLED` is both a sink and bad.
- `FAILED` and `BLOCKED` are bad but **not** sinks — they can re-enter `RUNNING`.

## Transition table

The complete set of allowed transitions:

| From \ To     | PENDING | RUNNING | SUCCEEDED | FAILED | CANCELLED | BLOCKED |
| ------------- | :-----: | :-----: | :-------: | :----: | :-------: | :-----: |
| **PENDING**   |    —    |   ✅    |     —     |   ✅   |    ✅     |   ✅    |
| **RUNNING**   |    —    |   —     |    ✅     |   ✅   |    ✅     |   —     |
| **SUCCEEDED** |    —    |   —     |     —     |   —    |     —     |   —     |
| **FAILED**    |    —    |   ✅    |     —     |   —    |    ✅     |   —     |
| **CANCELLED** |    —    |   —     |     —     |   —    |     —     |   —     |
| **BLOCKED**   |    —    |   ✅    |     —     |   —    |    ✅     |   —     |

Equivalent edge list (the canonical form implementations should mirror):

```text
PENDING   -> RUNNING | FAILED | CANCELLED | BLOCKED
RUNNING   -> SUCCEEDED | FAILED | CANCELLED
FAILED    -> RUNNING | CANCELLED
BLOCKED   -> RUNNING | CANCELLED
SUCCEEDED -> (none)
CANCELLED -> (none)
```

## Rules

### R-SM-01 — Allowed transitions are exhaustive

**Level:** MUST

A task MUST only move between statuses along edges listed in the transition
table. Any other request MUST be rejected without mutating status, error, or
result.

**Reference tests:**
- `tests/test_task.py::test_allowed_transitions`
- `tests/test_task.py::test_disallowed_transitions_are_rejected`
- `tests/test_task.py::test_invalid_transition_preserves_error`

### R-SM-02 — Sink states are terminal

**Level:** MUST

`SUCCEEDED` and `CANCELLED` MUST have no outgoing transitions. Once entered,
a task in a sink state never moves again, regardless of API calls.

**Reference tests:**
- `tests/test_task.py::TestTaskStatusPredicates::test_is_sink_truth_table`
- `tests/test_task.py::TestTaskStatusPredicates::test_is_sink_matches_allowed_transitions`

### R-SM-03 — Entering RUNNING clears prior-run carryover

**Level:** MUST

When a task transitions into `RUNNING`, the implementation MUST clear:

1. Any attached `result` from a prior run.
2. Any `blockedBy` value from a prior run.
3. Any `error` carried from a prior run.

This guarantees a retry starts from a clean slate.

**Reference tests:**
- `tests/test_task.py::TestRunningEntryResetsCarryover::test_failed_to_running_clears_result`
- `tests/test_task.py::TestRunningEntryResetsCarryover::test_blocked_to_running_clears_blocked_by`

### R-SM-04 — Entering SUCCEEDED clears error

**Level:** MUST

A successful transition MUST result in `error` being empty / null, even if
the caller passes an explicit error value alongside the transition request.

**Reference test:**
- `tests/test_task.py::test_success_transition_clears_even_explicit_error_text`

### R-SM-05 — FAILED preserves error for inspection

**Level:** MUST

When a task transitions into `FAILED`, the supplied error message MUST be
retained on the task and remain readable until the next entry into `RUNNING`
(which clears it per R-SM-03).

**Reference test:**
- `tests/test_task.py::test_failed_tasks_remain_mutable_for_retry`

### R-SM-06 — Cancellation preserves prior error

**Level:** MUST

Transitioning `FAILED -> CANCELLED` MUST NOT erase the failure reason. A
cancelled-after-failure task retains the failure error for post-mortem.

**Reference test:**
- `tests/test_task.py::test_cancel_preserves_previous_error`

### R-SM-07 — Cancellation is idempotent

**Level:** MUST

A high-level `cancel()` operation invoked on a task that is already in a
sink state MUST be a silent no-op:

- no error raised
- no event emitted
- no persistence side effect
- status unchanged

This is *separate* from the strict `transitionTo(CANCELLED)` API, which MAY
raise on disallowed transitions per R-SM-01.

**Reference tests:**
- `tests/test_task.py::test_cancel_is_idempotent`
- `tests/test_task.py::test_cancel_after_success_is_no_op`
- `tests/test_executor.py::test_cancel_succeeded_task_is_silent_noop`
- `tests/test_executor.py::test_cancel_idempotent_does_not_double_emit`

### R-SM-08 — FAILED and BLOCKED are retryable

**Level:** MUST

`FAILED -> RUNNING` and `BLOCKED -> RUNNING` MUST be valid transitions.
This is what makes retries and graph re-runs possible.

**Reference tests:**
- `tests/test_task.py::TestBlockedStatus::test_blocked_can_transition_back_to_running`
- `tests/test_executor.py::test_retry_after_failure_emits_started_event_from_failed_status`

### R-SM-09 — SUCCEEDED tasks are immutable

**Level:** MUST

Once a task is `SUCCEEDED`, normal public mutation of its fields
(`title`, `description`, `payload`, `timeout`, `error`) MUST be rejected.
This guarantees completed upstream tasks can be safely shared by reference
to handlers of downstream tasks.

Implementations MAY allow internal updates required to attach the result.

**Reference test:**
- `tests/test_task.py::test_done_tasks_reject_public_field_mutation`

### R-SM-10 — Identity is stable

**Level:** MUST

A task's `id` is assigned at creation and MUST NOT change for the lifetime
of the task. Equality and any membership semantics MUST be by id.

**Reference tests:**
- `tests/test_task.py::test_id_is_read_only`
- `tests/test_task.py::TestTaskHashingAndEquality`

### R-SM-11 — Status predicates agree with the table

**Level:** SHOULD

If an implementation exposes convenience predicates (`isPending`,
`isRunning`, `isSucceeded`, `isFailed`, `isCancelled`, `isBlocked`,
`isTerminal`, `isSink`, `isActive`, `isBad`), they SHOULD agree exactly
with the classifications above.

**Reference tests:**
- `tests/test_task.py::test_status_predicates`
- `tests/test_task.py::TestTaskStatusPredicates`

## Entry-effect summary

For quick reference, the effects an implementation MUST apply on each entry:

| Entering    | Effects                                                                  |
| ----------- | ------------------------------------------------------------------------ |
| `PENDING`   | (initial state only)                                                     |
| `RUNNING`   | clear `result`, clear `blockedBy`, clear `error`                         |
| `SUCCEEDED` | clear `error`; attach successful result before emitting terminal event   |
| `FAILED`    | retain supplied `error`; attach failure result before terminal event     |
| `CANCELLED` | retain prior `error` if any; attach cancelled result before terminal evt |
| `BLOCKED`   | set `blockedBy` to direct upstream parent that caused the block          |

The "attach result before terminal event" obligation is detailed in
`executor.md` under the events ordering rules.

## Out of scope

The state machine deliberately does **not** specify:

- which API calls cause each transition
- which thread / task / coroutine performs the transition
- the exact error type raised on a disallowed transition (only that one is)
- how `blockedBy` is propagated to descendants beyond the direct parent
