# Copilot Integration: `PROMPT`, `AGENT`, and `CopilotAgentSession`

## Concept

`ttasks` supports two LLM-driven task types in addition to the shell
types (`BASH`, `POWERSHELL`):

- **`PROMPT`** — one single-turn LLM completion. Tools are disabled.
  The payload is the prompt text. The output is the assistant message
  text.
- **`AGENT`** — one tool-capable agent turn. Tools are enabled. The
  payload is the agent instruction. The output is the assistant
  message text.

Both task types are driven by *handlers* the executor invokes (per
`executor.md`). Two handler styles are defined:

1. **One-shot handlers** — created by factory functions
   (`makeCopilotPromptHandler`, `makeCopilotAgentHandler`). Each task
   execution opens a fresh LLM client+session, sends one turn, then
   closes both. Best for stateless prompts.
2. **Shared session** — `CopilotAgentSession` opens one long-lived
   client+session, multiplexes many `AGENT` task executions over it,
   preserving conversation state across calls. Best for conversational
   workflows where each task is a follow-up turn.

The Python reference implementation backs both styles with the GitHub
Copilot Python SDK. The *contract* in this document treats the SDK as
an `IMPL-DEFINED` provider: an implementation MAY back these task
types with the Copilot CLI, OpenAI SDK, Anthropic SDK, an MCP-bridged
agent, or any equivalent provider, as long as the rules below hold.

What the contract guarantees is the *task-level* surface: how
`PROMPT` and `AGENT` tasks behave from the executor's point of view,
how cancellation flows, how multi-call sessions serialize and abort,
and how the session integrates with the rest of the system.

## Important

Copilot-SDK source can be found at one of these locations depending on machine:
~/src/copilot-sdk
c:\src\copilot-sdk

Use the sdk source for reference

## Data shape

### Task type semantics

| Task type | Tools | Multi-turn state | Default timeout                |
| --------- | ----- | ---------------- | ------------------------------ |
| `PROMPT`  | None  | No               | Implementation default, finite |
| `AGENT`   | All   | Only via shared session | None (unlimited unless task-level timeout set) |

### One-shot handler factories

| Factory                              | Returns                                       |
| ------------------------------------ | --------------------------------------------- |
| `makeCopilotPromptHandler({model?, timeout?})` | A `TaskHandler` for `PROMPT` tasks. |
| `makeCopilotAgentHandler({model?})`            | A `TaskHandler` for `AGENT` tasks.  |

### CopilotAgentSession

A reusable agent session with the following members:

| Member               | Sense                                                          |
| -------------------- | -------------------------------------------------------------- |
| `model`              | LLM model identifier in use for this session.                  |
| `reasoningEffort`    | Optional model reasoning effort tier, or `null`.               |
| `workingDirectory`   | Optional working-directory hint passed to the SDK.             |
| `timeout`            | Default per-turn timeout, or `null` for "no default".          |
| `eventErrors`        | Read-only list of errors raised by event subscribers.          |
| `enter()` / `exit()` | Sync resource-management lifecycle.                            |
| `enterAsync()` / `exitAsync()` | Async resource-management lifecycle.                 |
| `sendAndWait(prompt, opts?)` | Async send-one-turn-and-await-text.                    |
| `on(handler)`        | Subscribe to session events; returns an `unsubscribe()` closure. |
| `handler()`          | Return a synchronous `TaskHandler` bound to this session.      |

### Session lifecycle states

A `CopilotAgentSession` MUST be in exactly one of:

- **closed** — no SDK resources held; not usable for sends.
- **async-active** — opened via async enter; `sendAndWait` valid;
  `handler()` invalid.
- **sync-active** — opened via sync enter; `sendAndWait` valid;
  `handler()` valid.

Transitions: `closed → async-active → closed`, or
`closed → sync-active → closed`. No other transitions are defined.

## Rules

### Task type semantics

#### R-COP-01 — `PROMPT` is single-turn and tool-less

**Level:** MUST

A handler registered for `PROMPT` MUST:

- treat `context.payload` as the user prompt text,
- send exactly one LLM turn,
- run with tools disabled (no tool-call resolution),
- return the assistant message text as the task's success output.

A handler MUST NOT make the task multi-turn, MUST NOT execute tool
calls, and MUST NOT prompt the user.

**Reference tests:**
- `tests/test_executor.py::test_default_prompt_handler_uses_copilot_sdk`
- `tests/test_executor.py::test_copilot_prompt_handler_none_response_returns_empty_string`

#### R-COP-02 — `AGENT` is single-turn and tool-capable

**Level:** MUST

A handler registered for `AGENT` MUST:

- treat `context.payload` as the agent instruction text,
- send exactly one agent turn,
- run with tools enabled (tool calls within the turn are resolved by
  the provider, not by `ttasks`),
- return the assistant message text as the task's success output.

For one-shot agent handlers, each task execution MUST use a fresh
session and MUST NOT preserve conversation state from prior runs. For
shared sessions (R-COP-10..), conversation state is preserved.

**Reference tests:**
- `tests/test_executor.py::test_default_agent_handler_uses_copilot_sdk_with_tools_enabled`

#### R-COP-03 — Empty / non-text responses normalize to empty string

**Level:** MUST

If the provider returns no assistant message, a `null`/`undefined`
response, or a response whose payload is not an assistant text
message, the handler MUST return the empty string `""` as the task
output. It MUST NOT throw.

This keeps "the model declined to answer" a normal `SUCCEEDED`
outcome distinguishable from "the model errored" (which becomes
`FAILED`).

**Reference tests:**
- `tests/test_executor.py::test_copilot_prompt_handler_none_response_returns_empty_string`
- `tests/test_executor.py::test_copilot_prompt_handler_unknown_response_data_returns_empty_string`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_async_validation_and_empty_response`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_unknown_response_data_returns_empty`

#### R-COP-04 — Provider errors map to task failure

**Level:** MUST

If the LLM provider raises an error during the turn (network failure,
authentication failure, content-filter rejection, SDK internal error),
the handler MUST let that error propagate. The executor turns it into
a `FAILED` task per R-EXEC-05.

Handlers MUST NOT swallow provider errors into empty-string returns;
the empty-string path is reserved for "no assistant message" (R-COP-03).

**Reference tests:**
- `tests/test_executor.py::test_copilot_prompt_handler_sdk_error_marks_task_failed`
- `tests/test_executor.py::test_copilot_agent_handler_sdk_error_marks_task_failed`

### One-shot handler factories

#### R-COP-05 — Factory validation

**Level:** MUST

- `makeCopilotPromptHandler({model})` MUST reject an empty `model`.
- `makeCopilotPromptHandler({timeout})` MUST reject a non-positive
  `timeout`.
- `makeCopilotAgentHandler({model})` MUST reject an empty `model`.

Validation runs at factory-call time, before the handler is returned.

**Reference tests:**
- `tests/test_executor.py::test_make_copilot_prompt_handler_rejects_empty_model`
- `tests/test_executor.py::test_make_copilot_prompt_handler_rejects_non_positive_timeout`
- `tests/test_executor.py::test_make_copilot_agent_handler_rejects_empty_model`

#### R-COP-06 — Default models and timeouts

**Level:** IMPL-DEFINED

The default values for `model` (PROMPT and AGENT) and `timeout`
(PROMPT) are `IMPL-DEFINED`. Implementations MUST document their
chosen defaults. The Python reference uses:

- `DEFAULT_COPILOT_PROMPT_MODEL = "gpt-5.4-mini"`
- `DEFAULT_COPILOT_PROMPT_TIMEOUT = 60.0`
- `DEFAULT_COPILOT_AGENT_MODEL = "gpt-5.5"`

An implementation MAY choose different defaults that match its chosen
provider, and MAY change them across releases — but SHOULD treat such
changes as breaking and version them appropriately.

#### R-COP-07 — Task timeout overrides factory default

**Level:** MUST

A `PROMPT` task with a non-null `timeout` MUST cause the handler to
use the task's timeout for the LLM turn, not the factory's default
timeout. A task with `timeout == null` MUST fall back to the factory
default (if any). A handler with no default and a task with no timeout
MUST run without a `ttasks`-imposed wall-clock budget (the provider
may still impose its own).

**Reference tests:**
- `tests/test_executor.py::test_copilot_prompt_handler_uses_task_timeout`
- `tests/test_executor.py::test_copilot_agent_handler_uses_task_timeout`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_handler_uses_task_timeout`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_handler_uses_no_default_timeout`

#### R-COP-08 — Model is overridable per handler

**Level:** MUST

Callers MUST be able to construct a handler that uses a non-default
model by passing `model: "..."` to the factory. The registered handler
MUST send turns with that model.

**Reference tests:**
- `tests/test_executor.py::test_copilot_prompt_handler_allows_model_override`
- `tests/test_executor.py::test_copilot_agent_handler_allows_model_override`

#### R-COP-09 — Cancellation checked before, during, and after

**Level:** MUST

A one-shot Copilot handler MUST:

- check `context.cancelled` (via `raiseIfCancelled` or equivalent)
  before opening the LLM client,
- check again before sending the turn,
- check again after the turn returns,
- if the underlying send is cancellable, terminate it promptly when
  the task is cancelled mid-turn.

A cancelled task MUST surface through the cancellation category per
R-EXEC-13 / R-EXEC-30.

Tool-driven side effects in the AGENT case that completed before
cancellation observe MAY remain (the contract does not require
rollback of side effects).

### Shared session — lifecycle

#### R-COP-10 — Session construction validation

**Level:** MUST

`new CopilotAgentSession({model, timeout, ...})` MUST:

- reject empty `model`,
- reject non-positive `timeout`,
- accept any additional provider-specific session options through a
  pass-through mechanism (Python uses `**session_options`; an
  implementation MAY use an options object or rest parameters).

**Reference test:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_rejects_invalid_configuration`

#### R-COP-11 — Single-active lifecycle

**Level:** MUST

A `CopilotAgentSession` MUST NOT be entered twice. Calling sync
`enter()` while already active MUST raise; calling async
`enterAsync()` while already active MUST raise. The error MUST
mention that the session is already active.

A session that exits (sync or async) MUST be re-enterable. Successful
exit returns the session to **closed**.

**Reference tests:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_rejects_double_enter`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_rejects_double_async_enter`

#### R-COP-12 — Sync entry runs an isolated event loop

**Level:** MUST

For language runtimes where the underlying SDK is async-only
(Python; in many modern SDKs this is common), sync `enter()` MUST:

- spin up a dedicated event-loop / runtime context for this session,
- on `exit()`, shut that context down cleanly and join any helper
  thread.

The runtime context MUST NOT be the caller's main loop (since the
caller's code is sync). The exact mechanism is `IMPL-DEFINED`:
Python uses a daemon `Thread` running `asyncio.new_event_loop`; an
implementation MAY use a worker thread, a microtask queue, or — if the
underlying SDK is naturally sync — no loop at all.

**Reference test:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_async_context`

#### R-COP-13 — Enter failure cleans up

**Level:** MUST

If opening the underlying client succeeds but creating the session
fails, the client MUST be closed before the enter call rethrows. If
the background runtime context was started for sync entry, it MUST be
stopped before the enter call rethrows. A failed enter MUST leave the
`CopilotAgentSession` in **closed**.

**Reference tests:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_enter_failure_cleans_up_thread`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_session_enter_failure_closes_client`

#### R-COP-14 — Exit closes session then client

**Level:** MUST

Exit MUST close the underlying SDK session first, then the SDK client,
in that order (reverse of creation). If both close calls raise, the
session's error MUST be the one surfaced (the client error MAY be
preserved for diagnostics but the session error wins). If only one
raises, that error MUST be raised.

**Reference tests:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_close_raises_session_error`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_close_raises_client_error`

### Shared session — turns

#### R-COP-15 — `sendAndWait` validation and dispatch

**Level:** MUST

`sendAndWait(prompt, {timeout?})` MUST:

- reject calls when the session is not active,
- reject a `prompt` that is not a string,
- reject a non-positive `timeout`,
- use the per-call `timeout` when provided, otherwise the session's
  default `timeout` (which MAY be `null` for "no default"),
- send exactly one turn,
- return the assistant message text (empty string per R-COP-03 if
  there is none).

**Reference tests:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_passes_session_options`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_handler_uses_task_timeout`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_handler_uses_no_default_timeout`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_async_validation_and_empty_response`

#### R-COP-16 — Turns are serialized

**Level:** MUST

A shared session MUST serialize concurrent calls to `sendAndWait`
(and equivalently, concurrent invocations of the bound `handler()`):
turns execute one at a time in submission order. Concurrent callers
MUST observe their turns in a consistent order rather than racing
into the SDK.

This is what preserves conversation state: parallel agent tasks that
share a session line up rather than interleave.

**Reference test:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_serializes_concurrent_handler_calls`

#### R-COP-17 — Session preserves conversation state across turns

**Level:** MUST

Successive `sendAndWait` calls on the same active session MUST go
through the same underlying SDK session and MUST therefore observe
the provider's conversation state from prior turns. Closing and
re-opening the `CopilotAgentSession` MUST start a fresh conversation
(no leakage across enter/exit cycles).

**Reference test:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_reuses_one_sdk_session`

### Shared session — handler integration

#### R-COP-18 — `handler()` requires sync-active context

**Level:** MUST

The handler returned by `session.handler()` MUST raise a structured
error if invoked while the session is not sync-active. Specifically:

- before any `enter()`,
- after `exit()` returns,
- if the session is async-active rather than sync-active.

This is because the handler is synchronous (it returns a string, not
a promise) and bridges to the async session via the background
event loop established by sync entry (R-COP-12).

**Reference test:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_rejects_handler_outside_sync_context`

#### R-COP-19 — Handler is a normal AGENT handler

**Level:** MUST

The handler returned by `session.handler()` MUST be a valid
`TaskHandler` per `executor.md`. Specifically:

- it MUST accept a `TaskContext` and return the assistant text on
  success,
- it MUST honor `context.payload` as the agent instruction,
- it MUST honor `context.timeout` per R-COP-07,
- it MUST honor `context.raiseIfCancelled()` per R-COP-09 and R-COP-20.

It MUST be registerable for `AGENT` (or any user-chosen task type
that allows tool use) via the standard `executor.register` API.

#### R-COP-20 — Handler cancellation aborts the active turn

**Level:** MUST

If a task running on the session-bound handler is cancelled while a
turn is in flight, the handler MUST:

- attempt to abort the active turn through the SDK if the SDK
  exposes an abort/cancel operation (Python: `session.abort()`),
- handle the case where no abort operation exists by simply waiting
  for the underlying future to surface the cancel,
- raise the cancellation category from the handler,
- not corrupt the session for subsequent turns (the session MUST
  remain usable after a cancelled turn, as long as the abort
  succeeded).

The exact poll interval used while waiting for cancellation
visibility is `IMPL-DEFINED` and SHOULD be small (Python uses 50 ms).

**Reference tests:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_cancels_in_flight_handler`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_cancels_without_abort_method`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_wraps_cancelled_future`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_abort_without_loop_returns`

### Shared session — events

#### R-COP-21 — `on(handler)` subscribes and returns unsubscribe

**Level:** MUST

`session.on(handler)` MUST:

- reject a non-callable `handler`,
- add `handler` to the session's event subscribers,
- return a callable that, when invoked, removes `handler` from the
  subscribers (idempotent — calling it twice MUST NOT throw),
- be safe to call before, during, and after the session is active.

Subscribed handlers receive provider events as they arrive. The
exact event shape is `IMPL-DEFINED` (provider-specific).

**Reference tests:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_on_subscribes_to_events`
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_on_rejects_non_callable`

#### R-COP-22 — Subscriber errors are isolated

**Level:** MUST

If an event subscriber throws while handling an event, the session
MUST:

- catch the error,
- append it to `session.eventErrors`,
- continue dispatching the event to remaining subscribers,
- not propagate the error to the SDK or to the task lifecycle.

This mirrors the executor `EventBus` isolation rule (R-EVT-09) but
scoped to the session's own event stream.

**Reference test:**
- `tests/test_copilot_session.py::test_shared_copilot_agent_session_event_errors_are_isolated`

### Cross-cutting

#### R-COP-23 — Copilot integration is `IMPL-DEFINED` in provider

**Level:** IMPL-DEFINED

The specific LLM provider, SDK, transport, authentication model, and
event schema backing PROMPT, AGENT, and `CopilotAgentSession` are
`IMPL-DEFINED`. A conforming implementation MAY use:

- the GitHub Copilot Python SDK (Python reference),
- the GitHub Copilot CLI bridged over a child process,
- an MCP-bridged agent runtime,
- the OpenAI / Anthropic / etc. SDK directly,
- a stub that always returns an empty string (useful for tests and
  unconfigured environments).

Implementations MUST satisfy R-COP-01..22 regardless of provider
choice.

#### R-COP-24 — Permission handling is `IMPL-DEFINED` but documented

**Level:** IMPL-DEFINED

For `AGENT` tasks (one-shot and shared session) where the provider
issues permission requests (file write, network access, command
execution, etc.), the implementation's default permission policy is
`IMPL-DEFINED`. The Python reference uses *approve-all* to match the
existing one-shot AGENT behavior.

Implementations MUST document their default policy and SHOULD allow
callers to override it through the same option mechanism used for
other session options.

#### R-COP-25 — Copilot integration is optional

**Level:** MAY

An implementation MAY ship without PROMPT or AGENT handlers
pre-registered, and MAY ship a build flavor that omits the Copilot
integration entirely. In that case:

- `Task.prompt(...)` and `Task.agent(...)` MUST still construct
  valid tasks,
- registering them on a default executor MUST fail at execute time
  with the R-EXEC-06 "no handler" path, not at construction time,
- the package MUST document the omission and the workaround
  (registering a user-supplied handler).

This makes Copilot integration a compositional extension, not a hard
dependency.

## Scenarios

### S-COP-01 — One-shot PROMPT roundtrip

**Given** an executor with the default PROMPT handler registered and
a configured provider stub returning `"hello"`
**When** `Task.prompt("greet me")` is run through `execute`
**Then** subscribers see `STARTED, SUCCEEDED`
**And** `task.result.output == "hello"`
**And** the provider stub was called exactly once with prompt
`"greet me"` and tools disabled

Rules: R-COP-01, R-EXEC-05.

### S-COP-02 — Empty-response normalization

**Given** a configured provider stub that returns `null` for the
assistant message
**When** a PROMPT task is run
**Then** `task.result.output == ""`
**And** `task.status == SUCCEEDED`
**And** no error is surfaced

Rules: R-COP-03.

### S-COP-03 — Provider error becomes task failure

**Given** a configured provider stub that raises mid-turn
**When** a PROMPT task is run
**Then** subscribers see `STARTED, FAILED`
**And** `task.result.status == FAILED`
**And** the original provider error is preserved on `task.result.error`

Rules: R-COP-04.

### S-COP-04 — Task timeout overrides factory default

**Given** a PROMPT handler created with `timeout = 60`
**And** a task with `timeout = 2.5`
**When** the task runs
**Then** the underlying provider call is invoked with timeout `2.5`,
not `60`

Rules: R-COP-07.

### S-COP-05 — Shared session preserves conversation

**Given** a `CopilotAgentSession` entered in sync mode
**And** two AGENT tasks each sent through `session.handler()`
**When** both tasks run sequentially on a thread pool
**Then** both turns went through the *same* underlying SDK session
**And** the second turn observed the conversation state from the
first
**And** `session.eventErrors` is empty

Rules: R-COP-16, R-COP-17.

### S-COP-06 — Cancel mid-turn aborts and recovers

**Given** an active sync `CopilotAgentSession` running a long agent
turn
**When** `executor.cancel(task)` is invoked
**Then** the session attempts `abort()` on the SDK session
**And** the handler raises the cancellation category
**And** the task ends in `CANCELLED` with `terminationReason = "cancelled"`
**And** the session is still active and accepts a subsequent
`sendAndWait` without error

Rules: R-COP-20.

### S-COP-07 — Event subscriber error doesn't break the session

**Given** an active session with one subscriber that throws on every
event, and a second well-behaved subscriber
**When** the session emits N events during a turn
**Then** the well-behaved subscriber sees all N events
**And** `session.eventErrors` contains N errors
**And** the SDK turn completes normally and returns assistant text

Rules: R-COP-22.

### S-COP-08 — Double-enter rejected

**Given** a `CopilotAgentSession` already in sync-active
**When** the caller invokes `enter()` again
**Then** the call raises a structured error
**And** the existing active session is undisturbed
**And** subsequent `sendAndWait` still works on the original entry

Rules: R-COP-11.

### S-COP-09 — Session-bound handler refuses outside sync entry

**Given** a `CopilotAgentSession` that has never been entered
**When** the caller invokes `session.handler()` and then calls the
returned handler with a real `TaskContext`
**Then** the handler raises a structured error indicating the
session is not sync-active
**And** the task ends in `FAILED` via R-EXEC-05

Rules: R-COP-18.

### S-COP-10 — Optional Copilot build still constructs tasks

**Given** a build that omits the Copilot integration
**When** the caller writes `Task.prompt("hi")` and submits it to a
default executor with no PROMPT handler registered
**Then** `Task.prompt("hi")` returns a valid `Task`
**And** `executor.execute(task)` fails via R-EXEC-06 (no handler),
emitting exactly `FAILED` with `terminationReason = "handler"`

Rules: R-COP-25, R-EXEC-06.

## Implementation guidance

This section is non-normative. It documents how an implementation
could satisfy this contract.

### Recommended provider choice

The Python reference uses the GitHub Copilot Python SDK. A port has
several reasonable options:

1. **Copilot CLI bridge** — spawn `npx @microsoft/copilot-cli`
   (or `gh copilot`) as a subprocess, communicate over stdio. Fits
   naturally with Node's `child_process` and the existing
   subprocess-based handler patterns. Each turn is one CLI invocation
   for the one-shot case; for the shared-session case, keep the CLI
   alive and stream prompts/responses over its protocol.
2. **MCP runtime** — speak Model Context Protocol to a running agent.
   Lets the same TS package work with any MCP-compliant provider
   (Anthropic Claude, GitHub Copilot, local models).
3. **Direct SDK** — depend on `openai`, `@anthropic-ai/sdk`, or
   similar. Simplest implementation but ties the package to one
   provider.
4. **Stub** — ship a default handler that returns the empty string
   and require users to register a real handler for PROMPT/AGENT.
   Lets the package compile and pass tests without an external
   dependency, with R-COP-25 making it a documented choice.

### Async vs sync handler

In async-first runtimes the natural handler return type is often a
promise-like value rather than a direct string. The executor contract
(`executor.md`) does not require synchronous handlers — `R-EXEC-05` is
framed around "returns" and "throws" rather than sync/async. An
implementation SHOULD make all handlers async and drop the sync/async
distinction where practical.

This simplifies R-COP-12 (no background event loop needed),
R-COP-18 (no sync-active vs async-active distinction; just
"active"), and removes the entire sync-side machinery from
`CopilotAgentSession`.

The trade-off: callers using `executor.execute(task)` get an async
result rather than a direct synchronous result. That falls out of
implementation-specific async idioms (`await executor.execute(task)` in
C# or equivalent in other ports).

If an implementation collapses sync and async into one path, it MAY mark
R-COP-12, R-COP-18, and the "sync-active vs async-active" distinction
in R-COP-11 as not applicable (`N/A`) in its conformance index,
provided the underlying capability — abort, cancel mid-turn,
clean teardown — is preserved.

### Cancellation integration

An implementation SHOULD accept a cancellation handle on
`sendAndWait` (in addition to `timeout`), and SHOULD wire
`executor.cancel(task)` to a per-task cancellation mechanism whose
signal/token is exposed on `TaskContext`. This is the idiomatic
equivalent of Python's `context.raiseIfCancelled()` + `session.abort()`.

A handler then becomes an async wrapper around the session API that
uses the task's timeout and cancellation token/signal. The exact
surface is implementation-defined but the behavior must preserve
abort/cancel semantics.

Cancellation flows through the implementation's cancellation handle
and the provider-specific abort support.

## Out of scope

The Copilot contract intentionally does not specify:

- which provider, SDK, or transport backs the integration,
- the exact wire format of provider events (subscriber-visible
  events are passed through unmodified),
- authentication, token refresh, or quota handling,
- streaming vs non-streaming response delivery (the contract surfaces
  the *final* assistant text; streaming MAY occur internally and MAY
  drive `OUTPUT` events in a future revision),
- multi-turn conversations within a single `sendAndWait` call (the
  contract is one user turn → one assistant text),
- tool-call semantics, tool registration, or tool sandboxing for
  AGENT tasks,
- session migration, persistence, or replay (sessions are
  in-process only),
- default permission policy (`IMPL-DEFINED`, R-COP-24).
