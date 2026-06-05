# Executor

## Concept

The **executor** is the component that actually runs tasks. It:

- holds the registry of handlers, one per task type,
- drives every status transition through the state machine,
- attaches a `TaskResult` to each terminal task,
- emits lifecycle events,
- optionally persists every transition through a configured store,
- offers both synchronous (`execute`) and asynchronous (`submit`) entry points,
- owns the cancellation protocol and the subprocess lifecycle for built-in
  shell handlers,
- ships built-in handlers for `BASH`, `POWERSHELL`, `PROMPT`, and `AGENT`.

The executor is the **single source of terminal events** for any task it
runs. Other components (graph, store, user code) can request cancellation or
inspect state, but only the executor decides when to emit `STARTED`,
`SUCCEEDED`, `FAILED`, `CANCELLED`, `BLOCKED`, `PROGRESS`, `OUTPUT`, and
`PERSISTENCE_FAILED`.

## Data shape

### Executor

A conforming executor MUST expose at least:

| Member               | Sense                                                                              |
| -------------------- | ---------------------------------------------------------------------------------- |
| `events`             | The `EventBus` for this executor (see `events.md`).                                |
| `store`              | The optional store providing auto-persistence, or `null`.                          |
| `persistenceErrors`  | Read-accessible collection of `(taskId, error)` for failed task saves.             |
| `graphPersistenceErrors` | Read-accessible collection of `(graphId, error)` for failed graph saves.       |
| `isShutdown`         | Whether asynchronous submission has been disabled.                                 |
| `register`           | Method to register a handler for a task type.                                      |
| `isRegistered`       | Predicate: does this executor have a handler for a given task type.                |
| `execute`            | Synchronous entry point.                                                           |
| `submit`             | Asynchronous entry point (returns a future / promise of `TaskResult`).             |
| `cancel`             | Cancellation entry point.                                                          |
| `markBlocked`        | Graph-facing seam to record a task as `BLOCKED`.                                   |
| `isRunning`          | Predicate: does this task currently have a live subprocess.                        |
| `shutdown` / `close` | Drain in-flight async work and disable further submission.                         |

Implementations MAY rename these members (e.g. camelCase vs snake_case) but
SHOULD preserve the shape and semantics.

### TaskContext

The value passed to handlers. Read-only with respect to lifecycle state:

| Field                    | Sense                                                          |
| ------------------------ | -------------------------------------------------------------- |
| `id`                     | Task id.                                                       |
| `title`, `description`   | Task metadata.                                                 |
| `payload`                | Task payload.                                                  |
| `type`                   | Task type.                                                     |
| `timeout`                | Task timeout, or `null`.                                       |
| `status`                 | The task's live status.                                        |
| `cancelled`              | `true` iff cancellation has been requested.                    |
| `upstream`               | Read-only map of direct upstream tasks keyed by id.            |
| `raiseIfCancelled()`     | Throw the cancellation signal if `cancelled`.                  |
| `emitProgress(percent?, message?)` | Emit a `PROGRESS` event for this task.               |

The context MUST NOT expose any way to transition the task's status
directly. Handlers signal cancellation by throwing the cancellation signal;
they signal failure by throwing any other error; they signal success by
returning.

### RetryPolicy

| Field         | Sense                                                                |
| ------------- | -------------------------------------------------------------------- |
| `maxAttempts` | Total number of attempts for a single task. Integer >= 1.            |
| `backoff`     | Wait between attempts, in seconds. Finite, non-negative number.      |

### Exception categories

The executor MUST surface three distinguishable failure categories:

| Category               | Meaning                                                            |
| ---------------------- | ------------------------------------------------------------------ |
| **cancellation**       | Cooperative cancel signal. Handlers throw this to abort cleanly.   |
| **execution error**    | Subprocess exited non-zero, or other structured handler failure.   |
| **timeout**            | Subprocess exceeded its wall-clock budget and was force-stopped.   |

In Python these are `TaskCancelled`, `TaskExecutionError`,
`TaskTimeoutError`. In other runtimes they MAY be exception classes,
MAY be a discriminated union surfaced through a single error type,
or MAY be both. The names above are the canonical *categories*;
the exact representation is `IMPL-DEFINED`.

Execution-error and timeout categories MUST carry the subprocess
completion record (stdout, stderr, returncode) so the executor can
attach structured failure details to `task.result`.

## Rules

### Handler registration

#### R-EXEC-01 — Handler registration is type-keyed

**Level:** MUST

`register(type, handler)` MUST:

- reject a `type` that is not a recognized task type,
- reject a `handler` that is not callable / invokable,
- replace any prior handler for the same type without error.

Subsequent calls to `execute` / `submit` with a task of that type MUST
dispatch to the most recently registered handler.

**Reference tests:**
- `tests/test_executor.py::test_register_rejects_non_task_type`
- `tests/test_executor.py::test_register_rejects_non_callable_handler`

#### R-EXEC-02 — `isRegistered` reflects registration state

**Level:** MUST

`isRegistered(type)` MUST return `true` iff a handler is currently
registered for `type`, and MUST reject non-task-type inputs the same
way as `register`.

#### R-EXEC-03 — Default handlers for built-in types

**Level:** SHOULD

A default executor SHOULD ship with handlers registered for each
built-in task type (`BASH`, `POWERSHELL`, `PROMPT`, `AGENT`).
Implementations SHOULD provide a constructor variant (`empty()` or
equivalent) that yields an executor with no handlers, so test code
and embedded uses can opt out cleanly.

**Reference tests:**
- `tests/test_executor.py::test_default_executor_can_execute_bash`

### Synchronous execution

#### R-EXEC-04 — `execute` requires a Task

**Level:** MUST

`execute(task, ...)` MUST reject any `task` argument that is not a
`Task` value, before mutating any state or emitting any event.

**Reference test:**
- `tests/test_executor.py::test_execute_rejects_non_task`

#### R-EXEC-05 — `execute` drives the canonical lifecycle

**Level:** MUST

For a task with a registered handler, `execute` MUST:

1. Transition `task` to `RUNNING` and emit `STARTED`.
2. Invoke the handler with a fresh `TaskContext`.
3. On normal handler return, normalize the return value into a
   `TaskResult`, attach it, transition to `SUCCEEDED`, and emit
   `SUCCEEDED`.
4. On a cancellation signal from the handler (or from `raiseIfCancelled`
   after handler return), attach a cancelled `TaskResult`, transition to
   `CANCELLED`, emit `CANCELLED`, and rethrow the cancellation signal.
5. On any other handler error, attach a failed `TaskResult`, transition
   to `FAILED`, emit `FAILED`, and rethrow the error.

The ordering and event guarantees in `events.md` (R-EVT-02, R-EVT-03,
R-EVT-04) apply to each step.

**Reference tests:**
- `tests/test_executor.py::test_execute_success_emits_started_and_succeeded_events`
- `tests/test_executor.py::test_execute_failure_emits_started_and_failed_events`
- `tests/test_executor.py::test_execute_cancellation_emits_started_and_cancelled_events`

#### R-EXEC-06 — Missing handler terminalizes without RUNNING

**Level:** MUST

If no handler is registered for the task's type, `execute` MUST:

1. Attach a failed `TaskResult` with `terminationReason = "handler"`.
2. Transition the task to `FAILED`.
3. Emit exactly one `FAILED` event (no `STARTED`).
4. Throw a structured error indicating the missing handler.

The task's `previousStatus` on the `FAILED` event MUST reflect the
status the task was in before `execute` was called (typically
`PENDING`, possibly `BLOCKED` on a retry).

**Reference tests:**
- `tests/test_executor.py::test_execute_terminalizes_task_without_registered_handler`
- `tests/test_executor.py::test_execute_without_handler_terminalizes_blocked_retry_cleanly`

#### R-EXEC-07 — `execute` refuses already-CANCELLED tasks

**Level:** MUST

If a task is already in `CANCELLED` when `execute` is called, `execute`
MUST NOT invoke any handler and MUST surface the cancellation through
the cancellation category. No `STARTED` event is emitted.

**Reference test:**
- `tests/test_executor.py::test_execute_rejects_cancelled_task_without_calling_handler`

#### R-EXEC-08 — `execute` refuses non-runnable status

**Level:** MUST

If a task's status does not permit transition to `RUNNING` (per the
state machine), `execute` MUST reject the call with a structured error
and MUST NOT mutate task state. Tasks in `SUCCEEDED` are an example.

### TaskContext

#### R-EXEC-09 — Context exposes read-only task view

**Level:** MUST

The `TaskContext` passed to a handler MUST expose the task's id, title,
description, payload, type, timeout, and live status as read-only
properties. Handlers MUST NOT be able to mutate the task's lifecycle
state through the context.

**Reference test:**
- `tests/test_executor.py::test_task_context_exposes_read_only_task_view`

#### R-EXEC-10 — Context exposes upstream tasks read-only

**Level:** MUST

The context MUST expose direct upstream tasks (the values populated by
the graph or by the caller) as a read-only map keyed by task id.
Mutations to the underlying mapping after submission MUST NOT change
what the handler sees.

**Reference tests:**
- `tests/test_executor.py::test_task_context_exposes_read_only_upstream_task_refs`
- `tests/test_executor.py::test_execute_passes_upstream_task_refs_to_handler`
- `tests/test_executor.py::test_submit_copies_upstream_mapping_before_worker_runs`

#### R-EXEC-11 — Progress requires an executor-bound emitter

**Level:** MUST

`context.emitProgress` MUST be backed by an emitter wired to the
executor's event bus. A context constructed without a bound emitter
(e.g. in tests) MUST raise a structured error if a handler tries to
emit progress.

**Reference test:**
- `tests/test_executor.py::test_task_context_emit_progress_requires_executor_emitter`

#### R-EXEC-12 — Progress is rejected after cancellation

**Level:** MUST

`context.emitProgress` MUST raise the cancellation signal if the task
is already cancelled at the time of the emit attempt.

**Reference test:**
- `tests/test_executor.py::test_task_context_emit_progress_raises_when_cancelled`

### Cancellation

#### R-EXEC-13 — `cancel` is the cancellation entry point

**Level:** MUST

`executor.cancel(task)` MUST:

- be a silent no-op if the task is already `SUCCEEDED`,
- be a silent no-op if the task is already `CANCELLED`, *except* it MUST
  still reap any lingering subprocess associated with the task,
- transition `PENDING`, `FAILED`, or `BLOCKED` tasks directly to
  `CANCELLED`, attach a cancelled `TaskResult`, and emit exactly one
  `CANCELLED` event,
- for a `RUNNING` task: terminate the live subprocess (if any) and
  request cooperative cancellation; the `CANCELLED` event is emitted by
  the in-flight `execute` call, not by `cancel`.

This makes `execute` the single source of terminal events for tasks it
is actively running, and `cancel` the single source of terminal events
for tasks it is not.

**Reference tests:**
- `tests/test_executor.py::test_cancel_pending_task_emits_cancelled_event`
- `tests/test_executor.py::test_cancel_pending_task_attaches_result`
- `tests/test_executor.py::test_cancel_succeeded_task_is_silent_noop`
- `tests/test_executor.py::test_cancel_idempotent_does_not_double_emit`
- `tests/test_executor.py::test_cancel_failed_task_emits_cancelled_event`
- `tests/test_executor.py::test_cancel_already_cancelled_with_live_process_still_terminates`
- `tests/test_executor.py::test_externally_cancelled_running_task_emits_one_cancelled_event`

#### R-EXEC-14 — Cancelled-pending result fields

**Level:** MUST

When `cancel` terminalizes a task that was in `PENDING`, `FAILED`, or
`BLOCKED`, the attached `TaskResult` MUST have:

- `status = CANCELLED`,
- `error = "cancelled"` (or the implementation's canonical equivalent),
- `terminationReason = "cancelled"`,
- `duration = 0` (no handler ran),
- `startedAt == finishedAt`.

**Reference test:**
- `tests/test_executor.py::test_cancel_pending_task_attaches_result`

#### R-EXEC-15 — Cancel persists to the store

**Level:** MUST

When `cancel` terminalizes a task and a store is configured, the
cancellation MUST be persisted before the `CANCELLED` event is
emitted, subject to R-EVT-13 (persistence failures surface as events,
not exceptions).

**Reference test:**
- `tests/test_executor.py::test_cancel_pending_task_persists_to_store`

### Retry policy

#### R-EXEC-16 — RetryPolicy validation

**Level:** MUST

A `RetryPolicy` MUST reject:

- `maxAttempts` that is not an integer,
- `maxAttempts < 1`,
- `backoff` that is not a finite number,
- `backoff < 0`.

`execute` and `submit` MUST reject a `retryPolicy` argument that is
not a `RetryPolicy` (or the implementation's equivalent value type).

**Reference tests:**
- `tests/test_executor.py::test_retry_policy_rejects_invalid_max_attempts`
- `tests/test_executor.py::test_retry_policy_rejects_invalid_backoff`
- `tests/test_executor.py::test_execute_rejects_invalid_retry_policy`
- `tests/test_executor.py::test_submit_rejects_invalid_retry_policy_synchronously`

#### R-EXEC-17 — Retries re-run until success or exhaustion

**Level:** MUST

With `maxAttempts = N > 1`, `execute` MUST:

- run the handler up to `N` times,
- return on the first successful attempt,
- on each failure that leaves the task in `FAILED`, re-attempt unless
  attempts are exhausted,
- on exhaustion, rethrow the final attempt's error.

Each attempt is a full lifecycle: `STARTED` + terminal, per R-EVT-06.

**Reference tests:**
- `tests/test_executor.py::test_execute_retry_policy_recovers_after_handler_failure`
- `tests/test_executor.py::test_execute_retry_policy_exhaustion_reraises_final_error`

#### R-EXEC-18 — Cancellation is never retried

**Level:** MUST

If an attempt ends in `CANCELLED` (either through a handler-thrown
cancellation signal or through an external `cancel` observed during
the attempt), `execute` MUST NOT initiate further attempts, regardless
of remaining retry budget.

**Reference test:**
- `tests/test_executor.py::test_execute_retry_policy_does_not_retry_cancellation`

#### R-EXEC-19 — Missing handler does not retry

**Level:** MUST

Termination through the "no handler" path (R-EXEC-06) MUST NOT trigger
retries; the structured error is thrown immediately after the single
`FAILED` event.

**Reference test:**
- `tests/test_executor.py::test_execute_retry_policy_does_not_retry_missing_handler`

#### R-EXEC-20 — Backoff is observed between attempts

**Level:** MUST

If `backoff > 0`, the executor MUST wait approximately `backoff`
seconds between attempts. Implementations MAY sleep in small chunks to
remain responsive to cancellation.

Backoff applies only between attempts, not before the first attempt
and not after the final attempt.

**Reference tests:**
- `tests/test_executor.py::test_execute_retry_policy_applies_backoff_between_attempts`
- `tests/test_executor.py::test_retry_backoff_long_sleep_returns_at_deadline`

#### R-EXEC-21 — Cancellation during backoff is honored promptly

**Level:** MUST

If a task is cancelled while the executor is waiting between attempts,
the executor MUST stop waiting promptly and surface the cancellation
category without invoking a further attempt.

"Promptly" means within a small bounded number of milliseconds; the
exact tick interval is `IMPL-DEFINED`.

**Reference tests:**
- `tests/test_executor.py::test_execute_retry_policy_honors_cancellation_during_backoff`
- `tests/test_executor.py::test_retry_backoff_observes_external_cancellation_promptly`
- `tests/test_executor.py::test_execute_retry_policy_honors_zero_backoff_cancellation`

### Asynchronous submission

#### R-EXEC-22 — `submit` returns a future-like

**Level:** MUST

`submit(task, ...)` MUST return a future-like value (Promise, Future,
Deferred, etc.) that resolves with the task's `TaskResult` on success
and rejects with the appropriate error category on failure or
cancellation.

`submit` MUST apply the same validation as `execute` (R-EXEC-04,
R-EXEC-16) synchronously, before returning the future-like.

**Reference tests:**
- `tests/test_executor.py::test_submit_rejects_non_task_synchronously`
- `tests/test_executor.py::test_submit_rejects_invalid_retry_policy_synchronously`
- `tests/test_executor.py::test_submit_returns_future_that_completes_with_task_result`

#### R-EXEC-23 — Submitted execution is consistent with synchronous

**Level:** MUST

A task run through `submit` MUST emit the same events in the same
order, produce the same `TaskResult` shape, honor the same retry
policy, and persist the same way as a task run through `execute` with
identical inputs.

**Reference tests:**
- `tests/test_executor.py::test_submit_auto_persists_task_lifecycle`
- `tests/test_executor.py::test_submit_accepts_retry_policy`
- `tests/test_executor.py::test_submit_runs_multiple_tasks_concurrently`

#### R-EXEC-24 — Queued cancel before run

**Level:** MUST

If a task is cancelled before its submitted attempt begins (via
`executor.cancel` or the future's own cancel hook), the executor MUST
NOT invoke the handler and MUST surface the cancellation category
through the future-like.

If the future-like provides its own cancel operation, invoking it on a
not-yet-started attempt MUST mark the task `CANCELLED` (via the
executor's cancel machinery) and resolve the future as cancelled.

A running task MUST NOT be cancelled by a future-side cancel call;
running tasks are cancelled only through `executor.cancel`.

**Reference tests:**
- `tests/test_executor.py::test_executor_cancelled_queued_submit_raises_task_cancelled`
- `tests/test_executor.py::test_future_cancel_marks_queued_task_cancelled`
- `tests/test_executor.py::test_future_cancel_does_not_cancel_running_task`

### Shutdown

#### R-EXEC-25 — Shutdown is idempotent and drains submitted work

**Level:** MUST

`shutdown()` MUST:

- be safe to call multiple times,
- set `isShutdown` to `true`,
- reject any further `submit` calls with a structured error,
- wait for already-submitted work to finish (including not-yet-started
  queued work).

`shutdown` MUST NOT cancel running or queued tasks. Callers that want
cancellation MUST request it explicitly through `cancel`.

`close()` MUST be an alias for `shutdown()`.

**Reference tests:**
- `tests/test_executor.py::test_shutdown_is_idempotent_and_rejects_later_submit`
- `tests/test_executor.py::test_shutdown_waits_for_submitted_work_to_finish`
- `tests/test_executor.py::test_shutdown_allows_queued_submissions_to_finish`
- `tests/test_executor.py::test_close_aliases_shutdown`

#### R-EXEC-26 — Shutdown from a worker is non-deadlocking

**Level:** MUST

`shutdown` invoked from a task currently running on the executor MUST
NOT deadlock. It MUST wait for *other* in-flight workers but MUST NOT
wait for the calling worker to finish itself.

**Reference tests:**
- `tests/test_executor.py::test_shutdown_from_worker_waits_for_other_running_workers`
- `tests/test_executor.py::test_submitted_task_can_shutdown_its_executor`

#### R-EXEC-27 — Resource-cleanup integration

**Level:** SHOULD

Implementations SHOULD integrate with the host language's
resource-cleanup idiom — `with` in Python, disposal syntax in other runtimes, etc.
— so that exiting the scope closes the executor.

**Reference test:**
- `tests/test_executor.py::test_context_manager_closes_executor`

### Subprocess handlers

#### R-EXEC-28 — Built-in shell handlers stream output

**Level:** MUST

The built-in `BASH` and `POWERSHELL` handlers MUST:

- spawn the configured shell with the task's `payload`,
- stream stdout and stderr as separate `OUTPUT` events while the
  process runs (per R-EVT-14),
- attach a result whose `output`, `error`, `returncode`, and `raw`
  reflect the completed process,
- treat a non-zero exit as the execution-error category and emit a
  `FAILED` event,
- treat exceeding `task.timeout` as the timeout category and emit a
  `FAILED` event (after force-stopping the process).

Stdout-only and stderr-only outputs MUST each appear in the live
event stream and MUST also be retained in the final result.

**Reference tests:**
- `tests/test_executor.py::test_bash_task_emits_output_events_and_retains_result_output`
- `tests/test_executor.py::test_failed_bash_task_emits_output_events_before_failed_event`
- `tests/test_executor.py::test_bash_nonzero_exit_marks_task_failed`
- `tests/test_executor.py::test_bash_failure_uses_stderr_as_error`
- `tests/test_executor.py::test_failed_subprocess_result_preserves_output_error_and_returncode`
- `tests/test_executor.py::test_powershell_task_executes`

#### R-EXEC-29 — Timeout terminates the process and yields partial output

**Level:** MUST

When a subprocess exceeds `task.timeout`, the executor MUST:

- stop the process within a bounded wall-clock budget (escalating
  signals as needed; exact signals are `IMPL-DEFINED`),
- preserve any output collected up to the moment of termination on the
  failed `TaskResult`,
- set `terminationReason = "timeout"` on the result,
- raise the timeout exception category.

**Reference tests:**
- `tests/test_executor.py::test_bash_task_times_out`
- `tests/test_executor.py::test_real_subprocess_timeout_kills_within_wall_budget`
- `tests/test_executor.py::test_timeout_applies_while_draining_output_from_background_children`
- `tests/test_executor.py::test_timed_out_subprocess_result_preserves_partial_output`
- `tests/test_executor.py::test_timed_out_bash_task_emits_partial_output_events`

#### R-EXEC-30 — Cancellation terminates the process

**Level:** MUST

When `cancel` is invoked on a task whose handler is currently running a
subprocess (`isRunning(task.id) === true`), the executor MUST:

- terminate the subprocess promptly,
- escalate the signal if the process does not exit within a bounded
  budget,
- be safe to call on a process that has already exited (no-op),
- be safe to call on a process whose process group has been reaped
  out-from-under the executor (no-op).

**Reference tests:**
- `tests/test_executor.py::test_cancel_without_running_process_only_cancels_task`
- `tests/test_executor.py::test_run_command_terminates_if_task_cancelled_during_process_start`
- `tests/test_executor.py::test_run_command_reports_cancelled_nonzero_process_as_task_cancelled`
- `tests/test_executor.py::test_terminate_process_ignores_already_exited_process`
- `tests/test_executor.py::test_terminate_process_escalates_to_sigkill`
- `tests/test_executor.py::test_terminate_process_ignores_missing_group_during_sigkill`

#### R-EXEC-31 — Output decode is lossy-tolerant

**Level:** SHOULD

Subprocess handlers SHOULD decode output as UTF-8 with replacement for
invalid sequences so that non-text or mixed-encoding output does not
crash the executor or lose the surrounding lines.

**Reference test:**
- `tests/test_executor.py::test_bash_task_with_non_utf8_output_succeeds_with_replacement_text`

### Mark blocked

#### R-EXEC-32 — `markBlocked` is the graph seam

**Level:** MUST

`markBlocked(task, parentId)` MUST:

- transition the task to `BLOCKED`,
- record `parentId` as the task's `blockedBy` (the id of the direct
  upstream parent that caused the block),
- emit exactly one `BLOCKED` event with the correct `previousStatus`,
- persist the change through the store, subject to R-EVT-13.

`markBlocked` is the only sanctioned way to enter `BLOCKED` from
outside the executor's own execute / submit paths.

**Reference test:**
- Exercised by graph tests (see `graph.md`).

### Persistence integration

#### R-EXEC-33 — Auto-persistence on every transition

**Level:** MUST

If a store is configured, the executor MUST persist the task before
emitting every status-changing event for that task (`STARTED`,
`SUCCEEDED`, `FAILED`, `CANCELLED`, `BLOCKED`).

Per R-EVT-13, store errors MUST be captured (`persistenceErrors`) and
emitted as `PERSISTENCE_FAILED` events; they MUST NOT propagate to the
caller, MUST NOT change task outcome, and MUST NOT interrupt the
lifecycle event sequence.

**Reference test:**
- `tests/test_executor.py::test_submit_auto_persists_task_lifecycle`

#### R-EXEC-34 — Progress and output are not persisted

**Level:** MUST

`PROGRESS` and `OUTPUT` events MUST NOT trigger persistence. They are
ephemeral observations of an in-flight handler.

## Scenarios

### S-EXEC-01 — Synchronous happy path

**Given** an executor with a registered handler returning the string `"ok"`
**When** the caller invokes `execute(task)`
**Then** subscribers see `STARTED, SUCCEEDED`
**And** the call returns a `TaskResult` with `status == SUCCEEDED`,
`output == "ok"`, `terminationReason == null`
**And** `task.status == SUCCEEDED`

Rules: R-EXEC-05, plus R-EVT-04, R-EVT-12.

### S-EXEC-02 — Handler missing

**Given** an executor with no handler for the task's type
**When** `execute(task)` is invoked
**Then** subscribers see exactly `FAILED`
**And** `task.result.terminationReason == "handler"`
**And** the call throws the structured "no handler" error
**And** `task.status == FAILED`

Rules: R-EXEC-06, R-EVT-05.

### S-EXEC-03 — Cancelling a pending task

**Given** a task in `PENDING` and a configured store
**When** `executor.cancel(task)` is invoked
**Then** subscribers see exactly `CANCELLED`
**And** `task.result.status == CANCELLED`,
`task.result.error == "cancelled"`,
`task.result.terminationReason == "cancelled"`,
`task.result.duration == 0`
**And** the store recorded the cancelled task
**And** the call returns without throwing

Rules: R-EXEC-13, R-EXEC-14, R-EXEC-15.

### S-EXEC-04 — Cancelling a running subprocess

**Given** a `BASH` task whose handler is running a long-lived process
**When** `executor.cancel(task)` is invoked
**Then** the subprocess is terminated within the cancellation budget
**And** the in-flight `execute` call emits exactly one `CANCELLED` event
**And** `cancel` itself does not emit `CANCELLED`
**And** the resulting `TaskResult` has
`terminationReason == "cancelled"` and retains any partial output
collected up to termination

Rules: R-EXEC-13, R-EXEC-30, R-EVT-04.

### S-EXEC-05 — Retry recovers

**Given** a retry policy `maxAttempts = 3` and a handler that fails on
attempts 1 and 2 and succeeds on attempt 3
**When** `execute(task, retryPolicy=...)` is invoked
**Then** subscribers see
`STARTED, FAILED, STARTED, FAILED, STARTED, SUCCEEDED`
**And** the call returns the successful `TaskResult`
**And** `task.status == SUCCEEDED`

Rules: R-EXEC-17, R-EVT-06.

### S-EXEC-06 — Retry exhaustion

**Given** a retry policy `maxAttempts = 3` and a handler that always
fails
**When** `execute` is invoked
**Then** subscribers see exactly three `STARTED` events and three
`FAILED` events
**And** the call throws the final attempt's error
**And** `task.status == FAILED` with the final error preserved

Rules: R-EXEC-17, R-EVT-06.

### S-EXEC-07 — Cancellation during backoff

**Given** a retry policy with `backoff > 0` and a failing handler
**When** the first attempt fails and the caller cancels the task
during the backoff wait
**Then** the executor stops waiting promptly
**And** no further attempt is invoked
**And** the call surfaces the cancellation category
**And** `task.status == CANCELLED`

Rules: R-EXEC-18, R-EXEC-21.

### S-EXEC-08 — Future-cancel on queued task

**Given** a task submitted to a busy executor and not yet started
**When** the caller cancels the returned future
**Then** the executor marks the task `CANCELLED` (via its cancel machinery)
**And** subscribers see exactly `CANCELLED` for that task
**And** the handler is never invoked
**And** the future resolves as cancelled

Rules: R-EXEC-22, R-EXEC-24.

### S-EXEC-09 — Future-cancel on running task is ignored

**Given** a task submitted to the executor and currently `RUNNING`
**When** the caller cancels the returned future
**Then** the task continues to run
**And** the caller MUST use `executor.cancel(task)` to actually cancel
running work

Rules: R-EXEC-24.

### S-EXEC-10 — Shutdown drains

**Given** several submitted tasks, some running, some queued
**When** the caller invokes `shutdown()`
**Then** the call blocks until every submitted task has produced a
terminal event
**And** no submitted task is cancelled by shutdown alone
**And** subsequent `submit` calls reject with the structured "shut down"
error
**And** a second `shutdown` is a silent no-op

Rules: R-EXEC-25.

### S-EXEC-11 — Persistence failure does not derail

**Given** a store whose `tasks.save` raises on every call, and a
registered handler returning `"ok"`
**When** `execute(task)` is invoked
**Then** subscribers see `STARTED, PERSISTENCE_FAILED, SUCCEEDED,
PERSISTENCE_FAILED` (one persistence-failure event per attempted save)
**And** `executor.persistenceErrors` records each `(task.id, error)`
**And** `task.status == SUCCEEDED`
**And** the call returns the success result

Rules: R-EXEC-33, R-EVT-13.

### S-EXEC-12 — Subprocess timeout

**Given** a `BASH` task with `timeout = 0.5` whose payload sleeps for
several seconds
**When** `execute(task)` is invoked
**Then** the process is terminated within a small wall-clock budget
beyond the timeout
**And** subscribers see `STARTED`, any `OUTPUT` produced before
termination, then `FAILED`
**And** `task.result.terminationReason == "timeout"`
**And** the call throws the timeout category

Rules: R-EXEC-29.

## Out of scope

The executor contract intentionally does not specify:

- whether asynchronous execution uses threads, an event loop, workers, or
  a combination,
- whether `submit` resolves on the same execution context the caller
  awaits on,
- exact signal names or signal escalation timing for process termination
  (only that termination happens within a bounded budget),
- the process group / session model used to isolate child processes,
- the wire format of persistence errors beyond `(taskId, error)`,
- whether `PROGRESS` percent is monotonic (it is not required to be),
- whether handlers may spawn their own subprocesses outside `runCommand`
  (they may; the executor's subprocess tracking only follows handlers
  that funnel through the built-in command helper).
