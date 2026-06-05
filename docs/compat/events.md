# Events

## Concept

The executor reports observable lifecycle changes through an **event bus**.
Every `Task` mutation that originates from the executor (state transitions,
streamed output, in-flight progress, persistence failures) is announced as a
single **event** to all subscribers.

Events are the only sanctioned way for code outside the executor to observe
what a task is doing in real time. The state machine in `state-machine.md`
tells you *what statuses exist*; this document tells you *when subscribers
hear about them*.

The event bus is intentionally narrow:

- it only carries `TaskEvent` values
- it makes no scheduling decisions
- it does not own task state — it announces, never authorizes
- a failing subscriber MUST NOT change a task outcome

## Data shape

A **`TaskEvent`** MUST expose at least:

| Field             | Sense                                                                       |
| ----------------- | --------------------------------------------------------------------------- |
| `type`            | One of the event types listed below.                                        |
| `taskId`          | The id of the task this event refers to.                                    |
| `task`            | A reference to the task itself (live, not a snapshot).                      |
| `timestamp`       | When the event was emitted.                                                 |
| `previousStatus`  | The task's status immediately before this event, or `null` (see below).     |
| `status`          | The task's status as observers should see it now.                           |
| `error`           | Error message attached to the event, or `null`.                             |
| `progressPercent` | Percent in `[0, 100]`, or `null`. Set only on `PROGRESS`.                   |
| `progressMessage` | Free-form message, or `null`. Set only on `PROGRESS`.                       |
| `outputStream`    | `"stdout"` or `"stderr"`, or `null`. Set only on `OUTPUT`.                  |
| `outputChunk`     | The streamed text fragment, or `null`. Set only on `OUTPUT`.                |

Events MUST be immutable once emitted.

### Event types

| Type                  | When emitted                                                                 |
| --------------------- | ---------------------------------------------------------------------------- |
| `STARTED`             | A task enters `RUNNING`.                                                     |
| `PROGRESS`            | A handler reports in-flight progress through the task context.               |
| `OUTPUT`              | A subprocess handler streams a chunk to stdout or stderr.                    |
| `SUCCEEDED`           | A task transitions into `SUCCEEDED`.                                         |
| `FAILED`              | A task transitions into `FAILED`.                                            |
| `CANCELLED`           | A task transitions into `CANCELLED`.                                         |
| `BLOCKED`             | A task transitions into `BLOCKED` because of upstream state.                 |
| `PERSISTENCE_FAILED`  | A store call raised during an executor-driven save. Task outcome unaffected. |

### `previousStatus` semantics

- For `STARTED`: the status the task moved *from* (typically `PENDING`,
  sometimes `FAILED` or `BLOCKED` on retries).
- For `SUCCEEDED`, `FAILED`, `CANCELLED`, `BLOCKED`: the status the task
  moved *from*. Usually `RUNNING`, but `PENDING -> FAILED` (no handler),
  `PENDING -> CANCELLED` (cancelled before start), `FAILED -> CANCELLED`,
  and `PENDING -> BLOCKED` are all valid (see the table in
  `state-machine.md`).
- For `PROGRESS` and `OUTPUT`: `null`. These events do not represent a
  status transition.
- For `PERSISTENCE_FAILED`: `null`.

## Rules

### R-EVT-01 — Event types are exhaustive

**Level:** MUST

The implementation MUST define exactly these event types:
`STARTED`, `PROGRESS`, `OUTPUT`, `SUCCEEDED`, `FAILED`, `CANCELLED`,
`BLOCKED`, `PERSISTENCE_FAILED`.

Implementations MAY define additional event types provided existing
subscribers that ignore unknown types continue to function (i.e. additions
do not break the contract for documented types).

**Reference test:**
- `tests/test_executor.py` exercises every type listed above.

### R-EVT-02 — Status-changing events fire after the transition is applied

**Level:** MUST

When the executor emits a status-changing event (`STARTED`, `SUCCEEDED`,
`FAILED`, `CANCELLED`, `BLOCKED`), the corresponding task transition MUST
already be applied to the task by the time subscribers see the event.

Concretely: a subscriber that reads `event.task.status` MUST see a value
equal to `event.status`.

**Reference test:**
- Exercised by every executor test that asserts `task.status` inside an
  event subscriber.

### R-EVT-03 — Terminal result attached before terminal event

**Level:** MUST

For every terminal status-changing event that is not `BLOCKED`
(`SUCCEEDED`, `FAILED`, `CANCELLED`), the corresponding `TaskResult` MUST
already be attached to the task by the time the event is delivered to any
subscriber.

A subscriber reading `event.task.result` from one of these events MUST
NOT see `null` (assuming no later code clears it).

`BLOCKED` events MUST NOT carry a result (per R-TASK-12).

**Reference tests:**
- `tests/test_executor.py::test_succeeded_event_subscriber_sees_attached_result`
- `tests/test_executor.py::test_failed_event_subscriber_sees_attached_result`
- `tests/test_executor.py::test_cancelled_event_subscriber_sees_attached_result`

(Each test name above describes the asserted behavior; the actual test
identifiers in the reference may differ slightly. R-EVT-03 is the
authority.)

### R-EVT-04 — STARTED precedes streaming events precedes terminal

**Level:** MUST

For any single execution attempt of a task:

1. Exactly one `STARTED` event MUST be emitted, before any other event for
   that attempt.
2. Zero or more `PROGRESS` and `OUTPUT` events MAY be emitted, all between
   `STARTED` and the terminal event.
3. Exactly one terminal status-changing event (`SUCCEEDED`, `FAILED`, or
   `CANCELLED`) MUST be emitted to close the attempt.

No `PROGRESS` or `OUTPUT` event MAY be emitted before the attempt's
`STARTED` or after the attempt's terminal event.

**Reference tests:**
- `tests/test_executor.py::test_execute_success_emits_started_and_succeeded_events`
- `tests/test_executor.py::test_execute_handler_can_emit_progress_event`
- `tests/test_executor.py::test_execute_failure_emits_started_and_failed_events`
- `tests/test_executor.py::test_execute_cancellation_emits_started_and_cancelled_events`

### R-EVT-05 — Never-ran terminals skip STARTED

**Level:** MUST

Some transitions terminalize a task *without* ever entering `RUNNING`:

- handler not registered → `PENDING -> FAILED`
- cancelled before start → `PENDING -> CANCELLED`
- upstream-bad in a graph → `PENDING -> BLOCKED`

In these cases the executor MUST emit exactly the terminal event for the
relevant transition (`FAILED`, `CANCELLED`, or `BLOCKED`) and MUST NOT
emit a `STARTED` event for the attempt.

**Reference tests:**
- `tests/test_executor.py::test_execute_without_handler_emits_failed_event_only`
- `tests/test_executor.py::test_cancel_blocked_task_emits_cancelled_only`
- (BLOCKED-only-no-STARTED is exercised in graph tests.)

### R-EVT-06 — Retries are independent attempts

**Level:** MUST

A retry is a *new attempt*. The ordering guarantees in R-EVT-04 apply
independently to each attempt. Across `N` attempts of a single task,
observers MUST see exactly `N` `STARTED` events and exactly `N`
terminal events (in whatever success/failure mix actually occurred).

The `previousStatus` of the second-and-later `STARTED` events MUST
reflect the task's status at the moment retry began (e.g. `FAILED` for
a retry-after-failure).

**Reference tests:**
- `tests/test_executor.py::test_retry_after_failure_emits_started_event_from_failed_status`
- `tests/test_executor.py` exhaustion test: `N` `STARTED` and `N` `FAILED`
  events when all attempts fail.

### R-EVT-07 — Subscriber isolation

**Level:** MUST

A subscriber that raises while handling an event MUST NOT:

1. prevent other subscribers from receiving the same event,
2. prevent the executor from emitting subsequent events,
3. change the task's status, result, or any other observable state.

The error MUST be captured by the bus (e.g. in an `errors` collection) so
diagnostics are available, but it MUST NOT propagate to the executor's
caller.

**Reference tests:**
- `tests/test_executor.py::test_progress_subscriber_errors_do_not_fail_task_execution`
- Plus subscriber-isolation tests across success / failure / cancel paths.

### R-EVT-08 — Subscription returns idempotent unsubscribe

**Level:** MUST

`subscribe(handler)` MUST return an unsubscribe callable. Invoking the
unsubscribe callable:

- removes the subscriber if currently registered, and
- is a silent no-op on any subsequent invocation.

Subscribers MUST NOT receive events emitted after their unsubscribe
returns.

**Reference test:**
- Exercised broadly throughout `tests/test_executor.py` via the standard
  subscribe/append idiom.

### R-EVT-09 — Scoped subscription helper

**Level:** SHOULD

Implementations SHOULD offer a scoped-subscription helper that subscribes
for the duration of a block and unsubscribes automatically on exit,
including on exceptional exit.

In Python this is a context manager (`with bus.subscribed(handler): ...`).
In other runtimes a comparable shape would be a disposal-based pattern or
an explicit scoped helper such as `subscribeScoped(handler, () => { ... })`.

**Reference test:**
- `tests/test_executor.py` uses the context-manager form in places where a
  short-lived observer is wanted.

### R-EVT-10 — Subscriber rejection of non-callables

**Level:** SHOULD

`subscribe` SHOULD reject obvious non-handler arguments at registration
time, before any event is emitted, with a clear type error. This protects
callers from silent failures at emit time.

**Reference test:**
- `tests/test_executor.py` exercises the equivalent via the Python
  "subscriber must be callable" guard.

### R-EVT-11 — Events are immutable

**Level:** MUST

A `TaskEvent` value MUST NOT be mutated after emission. Subscribers MAY
hold references to events indefinitely, and those references MUST remain
faithful to the values present at emit time.

Note: the `task` field is a *live* reference to the task, which itself
continues to mutate as the lifecycle progresses. Immutability applies to
the event's own fields (`type`, `taskId`, `timestamp`, `previousStatus`,
`status`, `error`, the streaming fields), not transitively through the
task reference.

### R-EVT-12 — `previousStatus` is correct

**Level:** MUST

For status-changing events (`STARTED`, `SUCCEEDED`, `FAILED`,
`CANCELLED`, `BLOCKED`), `previousStatus` MUST equal the task's status
immediately before the transition that triggered the event.

For non-status events (`PROGRESS`, `OUTPUT`, `PERSISTENCE_FAILED`),
`previousStatus` MUST be `null` (or the language's equivalent absent
value).

**Reference tests:**
- `tests/test_executor.py` asserts `previous_status` across success,
  failure, cancel, retry-after-failure, cancelled-from-PENDING,
  cancelled-from-FAILED, and never-ran paths.

### R-EVT-13 — Persistence failures surface as events, not exceptions

**Level:** MUST

When the executor invokes a store and the store raises, the executor MUST:

1. capture the error,
2. emit a `PERSISTENCE_FAILED` event referencing the affected task, and
3. continue task execution as if persistence had succeeded.

A persistence failure MUST NOT change the task's status, result, or the
ordering of subsequent lifecycle events.

**Reference test:**
- `tests/test_executor.py` "store that raises on save records an error
  and emits `PERSISTENCE_FAILED`".

### R-EVT-14 — OUTPUT carries stream and chunk

**Level:** MUST

When emitted, an `OUTPUT` event MUST set `outputStream` to either
`"stdout"` or `"stderr"` and MUST set `outputChunk` to the streamed text
fragment. Empty chunks MAY be coalesced or suppressed.

Stdout and stderr chunks MUST be emitted as separate events; they MUST
NOT be merged into a single event.

**Reference test:**
- `tests/test_executor.py` output-streaming tests assert stream / chunk
  separation across stdout and stderr.

### R-EVT-15 — PROGRESS carries percent and/or message

**Level:** MUST

A `PROGRESS` event MUST carry at least one of `progressPercent` (a
number in `[0, 100]`) and `progressMessage` (a non-empty string).
Implementations MUST reject empty progress emissions at the handler-side
entry point (e.g. `context.emitProgress()` with no arguments).

If `progressPercent` is supplied, it MUST be a finite number in
`[0, 100]`. Values outside that range MUST be rejected at emit time.

**Reference tests:**
- `tests/test_executor.py::test_task_context_emit_progress_rejects_empty_event`
- `tests/test_executor.py::test_task_context_emit_progress_rejects_out_of_range_percent`
- `tests/test_executor.py::test_task_context_emit_progress_rejects_non_numeric_percent`
- `tests/test_executor.py::test_task_context_emit_progress_rejects_non_string_message`

### R-EVT-16 — Bus collects subscriber errors

**Level:** MUST

The event bus MUST expose a read view of errors raised by subscribers
during emission. Reading this view MUST NOT mutate it. The collection
MUST preserve order in which the errors occurred.

This is what makes R-EVT-07's "captured, not silent" guarantee
observable.

**Reference test:**
- Exercised by every subscriber-isolation test that later asserts the
  bus's `errors` view is non-empty.

### R-EVT-17 — Dependency-order exception for BLOCKED children

**Level:** MUST

For any dependency edge `u -> v` where both `u` and `v` reach a
terminal status during the same run:

- If `v` is *not* `BLOCKED`, the terminal event for `u` MUST be emitted
  before the terminal event for `v`. This is the normal
  parent-before-child ordering guarantee that callers rely on for
  cascading observers.
- If `v` *is* `BLOCKED`, the terminal event for `v` MAY be emitted
  *before* the terminal event for some of `v`'s other parents. The
  scheduler emits `BLOCKED` as soon as **any one** parent has become
  bad and unrecoverable; siblings of that parent that were running
  concurrently MAY still complete (succeed or fail) after `v` is
  already terminal.

This is the single deliberate relaxation of dependency-order. It
follows from R-GRAPH-16: blocking propagates eagerly, not lazily.
Observers that want to count "all parents have terminated" MUST NOT
assume that a child's terminal event implies anything about siblings
of the triggering parent.

The ordering guarantee between the *triggering* parent and the
`BLOCKED` child is preserved: the parent whose terminal status caused
the block (the one recorded as `task.blockedBy`) MUST have its
terminal event emitted before the child's `BLOCKED` event.

**Reference test:**
- `tests/test_e2e.py::test_transition_zoo` (the dependency-ordering
  loop explicitly skips `BLOCKED` children)

## Scenarios

### S-EVT-01 — Happy path event sequence

**Given** a registered handler that returns the string `"ok"`
**When** the executor runs a task
**Then** subscribers observe exactly the sequence `STARTED, SUCCEEDED`
**And** `STARTED.previousStatus == PENDING`, `STARTED.status == RUNNING`
**And** `SUCCEEDED.previousStatus == RUNNING`, `SUCCEEDED.status == SUCCEEDED`
**And** by the `SUCCEEDED` event, `task.result.output == "ok"`

Rules: R-EVT-02, R-EVT-03, R-EVT-04, R-EVT-12.

### S-EVT-02 — Progress between started and terminal

**Given** a handler that emits a progress event with `percent=25,
message="warming up"` then returns successfully
**When** the executor runs the task
**Then** subscribers observe exactly `STARTED, PROGRESS, SUCCEEDED`
**And** the `PROGRESS` event has `previousStatus == null`,
`progressPercent == 25`, `progressMessage == "warming up"`

Rules: R-EVT-04, R-EVT-12, R-EVT-15.

### S-EVT-03 — Retry produces independent attempts

**Given** a retry policy `maxAttempts = 3` and a handler that fails on
attempts 1 and 2 and succeeds on attempt 3
**When** the executor runs the task
**Then** subscribers see exactly `STARTED, FAILED, STARTED, FAILED,
STARTED, SUCCEEDED`
**And** the second `STARTED` has `previousStatus == FAILED`
**And** the third `STARTED` has `previousStatus == FAILED`

Rules: R-EVT-04, R-EVT-06, R-EVT-12.

### S-EVT-04 — Handler missing skips STARTED

**Given** no handler registered for the task's type
**When** the executor runs the task
**Then** subscribers see exactly `FAILED`
**And** the `FAILED` event has `previousStatus == PENDING`,
`status == FAILED`

Rules: R-EVT-05, R-EVT-12.

### S-EVT-05 — Cancellation before start

**Given** a task that is cancelled before the executor ever schedules it
**When** the cancel is observed by the executor
**Then** subscribers see exactly `CANCELLED`
**And** the `CANCELLED` event has `previousStatus == PENDING`

Rules: R-EVT-05, R-EVT-12.

### S-EVT-06 — Throwing subscriber does not break others

**Given** two subscribers, the first of which raises on every event
**When** a task runs to completion
**Then** the second subscriber observes the full event sequence
unaffected
**And** the bus's error view contains the errors raised by the first
subscriber
**And** the task's terminal status reflects the handler's outcome, not
the subscriber's error

Rules: R-EVT-07, R-EVT-16.

### S-EVT-07 — Unsubscribe is idempotent

**Given** a subscriber and the unsubscribe callable returned by
`subscribe`
**When** the unsubscribe is invoked twice
**Then** the second invocation is a silent no-op
**And** the subscriber receives no further events

Rules: R-EVT-08.

### S-EVT-08 — Persistence failure does not derail execution

**Given** a store whose `save` raises
**When** the executor runs a task and tries to persist a transition
**Then** subscribers see the normal lifecycle events for the task
**And** subscribers also see a `PERSISTENCE_FAILED` event for the
affected save
**And** the task's terminal status matches what would have happened
without persistence

Rules: R-EVT-13.

### S-EVT-09 — Output streams stay separate

**Given** a subprocess handler that writes to both stdout and stderr
**When** the executor runs the task
**Then** subscribers see one or more `OUTPUT` events with
`outputStream == "stdout"` and one or more with `outputStream ==
"stderr"`
**And** stdout chunks are never merged with stderr chunks in a single
event

Rules: R-EVT-14.

## Out of scope

The event contract intentionally does not specify:

- whether emission is synchronous or asynchronous from the executor's
  perspective
- whether subscribers run on the executor's worker, on a dedicated
  thread, or via the event loop
- the exact format of `timestamp` (wall clock vs monotonic, UTC vs local)
- delivery semantics in the face of process crashes (this is store
  territory)
- how the bus de-duplicates or buffers events under load
- whether there is a single global bus or one per executor (the latter
  is the reference model, the former is acceptable if R-EVT-07 still
  holds)
