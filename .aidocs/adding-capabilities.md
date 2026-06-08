# Adding capabilities

Capabilities are the host-approved tool surface for the chat planner.

## Adding a new allowed tool

1. Edit `Ttasks.ChatApp\appsettings.json` and add to `ChatApp:AllowedTools`:

   ```json
   { "prefix": "your-tool subcommand", "description": "...", "helpCommand": "your-tool subcommand --help" }
   ```

2. Pick prefix granularity carefully:
   - Use the bare tool name (`mail`) to expose the whole surface.
   - Use a subcommand (`teams read`) to expose only that subcommand.
   - Use a fully-qualified command (`az account list`) to expose exactly that command.

3. Optionally pre-seed reusable templates with `ChatApp:LibrarySeed` entries (`fileName`, `argsTemplate`, `parameters`). Use seeds when there is a canonical default the planner should always start with (e.g. `calendar.today`).

That is the whole code path. There are no per-tool provider classes, regex intent matchers, or metadata adapters to write.

## Behaviors that come for free

- The planner can compose any documented command for the tool that fits the prefix.
- The planner can suggest new templates via `librarySuggestion`; successful ones get promoted to the library automatically.
- The repair loop will retry with failure observations within the configured budget.
- Process commands run with structured argv (no shell), avoiding quoting/expansion issues.

## Removing a capability

Delete the matching entry from `ChatApp:AllowedTools`. Existing library items keyed to that tool stay in the store but will no longer pass validation, so the planner will repair away from them.

## Anti-patterns

- Do not add per-tool provider classes for intent matching. That is the planner's job.
- Do not bake strategy constraints (e.g. specific OData shapes, `$top` caps) into the validator. The allowed-tool prefix is the boundary; strategy belongs to the planner.
- Do not promote candidates eagerly. Only successful graphs should write to the library.
