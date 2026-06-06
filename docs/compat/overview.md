# ttasks compat overview

## Purpose

This directory describes the **observable behavior** of `ttasks` in
implementation-neutral terms.

Any implementation — the Python reference (`ttasks`), the earlier reference
port (`ttasks-ts`), or a future .NET port — is *conforming* if it satisfies
the rules
defined here. Two conforming implementations should be **indistinguishable**
from the outside for every documented scenario.

These docs are **not**:

- API reference (how to call the library)
- design docs (how a given implementation is built)
- tutorials
- a changelog

These docs **are**:

- a list of named, testable rules
- a catalog of behavior scenarios
- the contract every implementation owes its users

## Source of truth

When the compat docs and an implementation disagree:

1. If the disagreement is a documented `MUST` rule, the **implementation is wrong**.
2. If the disagreement is on something the docs don't yet cover, the **docs are incomplete** — fix the docs first, then decide which implementation needs to change.

The Python implementation is the *reference implementation*, not the spec.
It can be wrong relative to these docs.

## Conformance levels

Every rule and scenario is tagged with one of:

| Level             | Meaning                                                                 |
| ----------------- | ----------------------------------------------------------------------- |
| **MUST**          | Required. A non-conforming implementation cannot claim compatibility.   |
| **SHOULD**        | Strongly recommended. Deviation requires explicit justification.        |
| **MAY**           | Optional. Implementations may provide this without breaking conformance.|
| **IMPL-DEFINED**  | Must exist in some form. The specific shape is up to the implementation.|

These map to the IETF RFC 2119 senses, narrowed for a library context.

## Document layout

| File                  | Topic                                                       |
| --------------------- | ----------------------------------------------------------- |
| `overview.md`         | This file.                                                  |
| `glossary.md`         | Defined terms used throughout the docs.                     |
| `state-machine.md`    | The canonical `Task` status transitions and entry effects.  |
| `task.md`             | Task identity, mutability, factories, result attachment.    |
| `events.md`           | Event taxonomy, ordering, observer isolation.               |
| `executor.md`         | Execution, retries, cancel, shutdown, timeout, streaming.   |
| `graph.md`            | DAG rules, blocking, finally / optional / required tasks.   |
| `store.md`            | In-memory + SQLite persistence contracts.                   |
| `copilot.md`          | Shared Copilot session lifecycle (later).                   |
| `agent-graphs.md`     | LLM-authored graph plans, metadata, upstream fan-in prompts.|
| `conformance.md`      | Index of every rule with level and reference test.          |

Each topic file follows a four-section pattern:

1. **Concept** — one paragraph.
2. **Data shape** — fields and their meanings, language-agnostic.
3. **Rules** — numbered, testable statements, each at a conformance level.
4. **Scenarios** — Given / When / Then behaviors that exercise the rules.

## Cross-references

Rules are referenced by their stable identifier, e.g. `R-EXEC-07`.

Reference tests are linked using their location in the Python tree, e.g.
`tests/test_executor.py::test_execute_retry_policy_exhaustion_reraises_final_error`.

Reference tests are *evidence* that the Python implementation satisfies a rule.
They are not the spec. A rule with no reference test is still authoritative —
it just isn't yet exercised in CI.

## Out of scope

The compat docs intentionally do not constrain:

- threading model vs event loop vs other concurrency primitives
- how subprocesses are spawned or terminated
- how persistence is implemented underneath the documented protocols
- the exact text of error messages
- the exact class hierarchy of error types (only the distinguishable categories)
- the exact module / namespace layout
- equality and hashing details beyond the identity-by-id rule

When in doubt, ask: "would two implementations both look correct to a user
running the same scenario?" If yes, the docs don't need to constrain it.
