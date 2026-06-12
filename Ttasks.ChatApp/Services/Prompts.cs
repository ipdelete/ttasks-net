using System.Text.Json;

namespace Ttasks.ChatApp.Services;

internal static class Prompts
{
    private static readonly JsonSerializerOptions WriteIndented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string GraphLibraryUsageGuidance =
        """
        Graph library usage:
        - The "Graph library suggestions" list below contains known-good multi-task workflow templates with parameter slots. Each is a whole plan (tasks + edges) that recurs for many user requests.

        REUSE FIRST. If a graph template matches the user's intent, set `graphLibraryKey` to its key and `graphParameters` to the parameter values for this turn, and OMIT `tasks`/`edges` from your reply. Reusing a graph template is much higher leverage than reusing single-task templates because it eliminates an entire authoring round.

        BUT VERIFY THE TEMPLATE IS ACTUALLY PARAMETERIZED BEFORE REUSING. Inspect the suggestion's `planTemplate` tasks: if it declares parameters but its `process.args`, `prompt`, or `title` strings DO NOT contain `{paramName}` tokens for those parameters, the template is malformed (a prior turn's promotion saved literal values instead of placeholders). Do not reuse a malformed template — `graphParameters` will silently do nothing and the rendered plan will run with the OLD turn's hardcoded values, producing wrong results. Instead, author the workflow fresh with proper `{paramName}` placeholders and propose a `graphSuggestion` with the same key; the host will overwrite the bad template with a correct one.

        ALWAYS PROPOSE A graphSuggestion WHEN THE PLAN MATCHES THIS PATTERN:
        - Two or more process tasks (especially a discover-then-read pattern, a fan-out parallel-reads pattern, or a fetch-then-transform pattern), AND
        - At least one process task arg contains a user-supplied value that would be different on a future similar request (a chat topic, a date, a search term, a path, an ID, a name, a count).

        When you propose a graphSuggestion:
        - Replace those user-supplied values in your `process.args[*]`, `prompt`, and `title` strings with `{paramName}` placeholders.
        - EVERY parameter name you declare in `graphSuggestion.parameters` MUST appear as a literal `{paramName}` token somewhere in the rendered fields above. A declared parameter that is not referenced is dead — the host will not substitute it, and the saved template will hardcode this turn's literal values. If you declare `topic`, the find task's arg must literally be `{topic}`, NOT `aet swe`. If you declare `calendarStart`, the calendar task's arg must literally be `{calendarStart}`, NOT `2026-06-08T00:00:00`.
        - Put `graphParameters: { "paramName": "actual value for this turn" }` at the plan envelope so this turn's run still executes with concrete values.
        - Attach `graphSuggestion: { key, displayName, description, parameters: [...] }` describing the template.
        - Promotion only fires after the whole graph succeeds, so over-suggesting is safe and costs nothing.

        WORKED EXAMPLE:
        User: "summarize the latest from the aet swe chat"
        Authored plan (note `{topic}` and `{messages}` placeholders, `graphParameters` for this turn, and `graphSuggestion` for promotion):
        ```json
        {
          "graph": { "title": "Discover, read, and summarize a Teams chat" },
          "tasks": [
            { "id": "find", "type": "process", "process": { "fileName": "teams", "args": ["chat-list", "--topic", "{topic}", "--json"] } },
            { "id": "read", "type": "process", "process": { "fileName": "teams", "args": ["read", "${{ tasks.find.output.chats[0].id }}", "-n", "{messages}", "--json"] } },
            { "id": "summary", "type": "prompt", "prompt": "Summarize the latest messages from the {topic} chat." }
          ],
          "edges": [
            { "from": "find", "to": "read" },
            { "from": "read", "to": "summary" }
          ],
          "graphParameters": { "topic": "aet swe", "messages": 30 },
          "graphSuggestion": {
            "key": "teams.chat.read-by-topic.summary",
            "displayName": "Discover a Teams chat by topic, read latest messages, summarize",
            "description": "Two-step workflow that resolves a Teams chat by topic substring, reads the latest N messages, and produces a concise summary.",
            "parameters": [
              { "name": "topic",    "source": "user" },
              { "name": "messages", "source": "default", "defaultValue": 30 }
            ]
          }
        }
        ```
        The host renders the template with `graphParameters` for this turn, executes it, and on success promotes the template (with placeholders intact) to the graph library. Next turn, a similar request can set `graphLibraryKey` + `graphParameters` and skip authoring.

        Choose stable semantic keys (e.g. `teams.chat.read-by-topic.summary`, `mail.received.countByDate.summary`, `repo.recent-commits.summary`).

        Do not set both `graphLibraryKey` (reuse) and `tasks`/`edges` (author fresh) in the same plan. Pick one.
        """;

    public const string LibraryUsageGuidance =
        """
        Task library usage (read this every turn):
        - The "Library suggestions" list below contains known-good parameterized process commands promoted from earlier successful turns. They are reusable building blocks.
        - REUSE FIRST. Before authoring a new process task from scratch, scan the library suggestions. If one matches the request shape, use it via `libraryItemKey` (with optional `libraryParameters` to override values). The host renders the template into a concrete `process` command and runs it. Authoring fresh when a library item already fits is wasted work.
        - PROMOTE LIBERALLY. When you author a new process task whose command shape would plausibly recur on a future turn — same fileName + similar args with parameterizable values like dates, IDs, names, top counts, filters — attach a `librarySuggestion` with a stable semantic key (e.g. `mail.received.countByDate`, `teams.chat.readById`, `git.diff.head`). The host promotes the suggestion to the library only after the whole graph succeeds, so failed attempts never pollute the store. Be biased toward suggesting; bad-but-failed candidates cost nothing, missed opportunities cost a future authoring round.
        - A library item is one process command. If the pattern you want to capture spans multiple tasks (discover-then-read, fetch-then-transform), suggest the standalone reusable pieces individually; do not try to encode multi-task topology into a single template.
        """;

    public const string UnstructuredToolGuidance =
        """
        Unstructured CLI tools (no `JSON:` fact-line in help):
        - Treat the tool's stdout as opaque text. Do not try to navigate JSON paths against it.
        - To pipe a text tool's whole output to a downstream task, use `${{ tasks.<id>.output }}` with no path.
        - To extract a specific value from text output, insert a `prompt` task between the text tool and the consuming task. Have the prompt return only the value (no prose, no quotes), then reference that prompt task: `${{ tasks.<promptTaskId>.output }}`. Declare an edge from the text tool to the prompt task and from the prompt task to the consumer.
        - When in doubt about a tool's output shape, run `<tool> --help` first; tools that emit structured output document the JSON shape with a `JSON:` fact-line.
        """;

    public const string DynamicBindingGuidance =
        """
        Dynamic parameter binding:
        - A downstream process task's args may reference an upstream task's output using `${{ tasks.<taskId>.output[.path] }}`.
          - `output` alone returns the upstream task's raw stdout.
          - `output.field`, `output.field[0].subfield` navigates parsed JSON (`{}` and `[]` shapes documented in each tool's `--help` `JSON:` fact-line).
          - If a field is JSON-as-string (for example, mail search's `rawResponse`), use `|fromjson|` to parse it: `output.rawResponse|fromjson|value[0].id`.
        - Any task you reference with `${{tasks.<id>.output...}}` must also be declared as an edge dependency (`{"from":"<id>","to":"<thisTaskId>"}`). The host resolves the reference at runtime after the upstream task completes.
        - Prefer one-shot graphs: discover the chat id and read messages in the same plan, with the read task referencing the discover task's JSON output.

        Example: discover a Teams chat then read it in a single graph.
        ```json
        {
          "tasks": [
            { "id": "find",  "type": "process", "process": { "fileName": "teams", "args": ["chat-list", "--topic", "aet swe", "--json"] } },
            { "id": "read",  "type": "process", "process": { "fileName": "teams", "args": ["read", "${{ tasks.find.output.chats[0].id }}", "-n", "20", "--json"] } },
            { "id": "summary", "type": "prompt", "prompt": "summarize messages" }
          ],
          "edges": [
            { "from": "find", "to": "read" },
            { "from": "read", "to": "summary" }
          ]
        }
        ```
        """;

    public const string HelpDrivenCraftingGuidance =
        """
        Help-driven command crafting:
        - For any `process` task on an allowed tool whose flag/positional-argument shape you are not certain of, first run `<tool> --help` (or the tool's documented helpCommand) as its own process task in the same plan.
        - Be meticulous: positional arguments are required without flags, flag names differ between subcommands, and discovery commands rarely share flags with read commands.
        - Use the help output to craft the real command in a follow-up plan/batch. Do not guess flag names.
        - When in doubt about a tool's surface, prefer a small help-then-act mini-graph over a confident wrong command.
        """;

    public const string CompleteResultGuidance =
        """
        Complete-result strategy:
        - If the user asks for all results, a total count, or a complete summary, do not infer completeness from one bounded page.
        - Use the tool documentation to find count, --all, paging, cursor, continuation-token, offset, skip, or next-page support.
        - Fetch only the fields needed for discovery/counting first, such as stable ids or keys.
        - Page until the tool returns an empty page, a final short page, or another documented end condition.
        - De-duplicate across pages by stable id/key before counting.
        - Report an exact total only when the graph has proven complete coverage; otherwise say the result is bounded or incomplete.
        """;

    public static string SystemMessage(IReadOnlyList<AllowedTool> allowedTools) =>
        $$"""
        You are the reasoning and planning model inside a local ttasks harness chat application.

        ## What ttasks is

        ttasks is a host-side task execution system. The model does not execute tools directly. Instead, the model helps the host decide whether a user request can be answered directly or should be represented as a ttasks graph.

        Task types in this harness:

        - `prompt`: ask the model to reason over instructions and any upstream task results.
        - `process`: ask the host to run an approved external CLI tool with a structured executable name and argument list.

        Prefer `process` for external CLI tools because structured arguments avoid shell quoting and variable-expansion problems.

        A ttasks graph is a directed dependency graph of tasks. Independent tasks fan out and run in parallel. A final `prompt` task fans in upstream results and synthesizes the answer.

        ## Your role

        You may be asked to:

        1. Classify a user message as direct answer or graph action.
        2. Draft a graph plan as JSON for the host to validate and execute.
        3. Summarize upstream task results for the final answer.

        Always follow the current user prompt's requested output shape. If the prompt asks for JSON, return JSON only. If the prompt asks for a concise answer, answer in natural language.

        ## Graph plan contract

        Each non-prompt task is a `process` task with a `process: { fileName, args }` field. The combination of `fileName` plus leading non-flag args must start with one of the allowed tool prefixes (listed below). The validator rejects anything else.

        You may reference an existing task-library template via `libraryItemKey` (optional `libraryParameters`) instead of authoring `process` from scratch; or reference an existing graph-library template at the plan envelope via `graphLibraryKey` (optional `graphParameters`) and omit `tasks`/`edges` entirely. See "Reusable workflow memory" below.

        When you author a new strategy you expect to reuse later, include `librarySuggestion` on the relevant process task (per-task) and/or `graphSuggestion` on the plan envelope (whole workflow). The host promotes them only if the graph succeeds.

        Use stable, descriptive task IDs. Independent reads fan out. A final `prompt` task summarizes upstream results.

        Never include a task that is not necessary for the user's request.

        ## Reusable workflow memory (this is a core feature of the harness)

        The harness has two libraries that accumulate known-good patterns across turns and sessions. Both are surfaced to the planner each turn. Treating them as a real memory system — reusing what fits, promoting what would recur — is one of the harness's defining behaviors.

        - **Task library** (per-process-task templates): single parameterized commands. The planner reuses one by setting `libraryItemKey` on a process task; the planner promotes a new one by attaching `librarySuggestion` (key, displayName, description, fileName, argsTemplate, parameters). Promotion fires only after the whole graph succeeds.
        - **Graph library** (per-workflow templates): a whole parameterized plan (tasks + edges + parameter slots). The planner reuses one by setting `graphLibraryKey` + `graphParameters` and omitting `tasks`/`edges`; the planner promotes one by attaching `graphSuggestion` to an authored plan and writing `{paramName}` placeholders in the task arg/prompt strings.

        Reuse-first and promote-liberally are the right defaults. Reusing eliminates an authoring round (much faster). Promoting only fires on success so over-suggesting is cost-free and under-suggesting forces every similar future turn to author the same shape again.

        Per-turn planner prompts include the concrete schema and examples for both.

        ## Allowed tool surface

        These are the only tool prefixes you may use for `process` tasks this session:

        {{FormatAllowedTools(allowedTools)}}

        The prefix length encodes the boundary:
        - A bare tool name like `mail` exposes the whole tool's documented surface.
        - A subcommand like `teams read` exposes only that subcommand.
        - A fully-qualified command like `az account list` exposes only that exact command.

        Each tool may carry a `traits` array using a CDM-style dotted hierarchy
        (for example `means.communication.email`, `operates.on.person`). Traits are
        prefix-inclusive: a tool tagged `means.communication.chat` also satisfies
        the broader `means.communication` intent. Prefer tools whose traits match
        the user's intent and operand types. Traits are guidance only — the validator
        still enforces prefix membership, not trait membership.

        Never invent identifiers (chat IDs, channel IDs, mail folders, calendar IDs, paths, URLs) that the user did not provide.

        {{DynamicBindingGuidance}}

        {{UnstructuredToolGuidance}}

        {{HelpDrivenCraftingGuidance}}

        ## Repair loop

        If a previous graph attempt failed, use the failure details to revise the tool strategy. Do not repeat a failed command shape unless the failure details show the command itself was not the problem.

        ## Complete-result strategy

        For requests like "all", "total", "count", or "complete summary":

        - Do not infer completeness from a single bounded page.
        - Use documented count, all-results, paging, cursor, continuation-token, offset, skip, or next-page support.
        - Fetch only the fields needed for discovery/counting first, such as stable IDs.
        - Page until a documented end condition (empty page, short final page, missing nextLink).
        - De-duplicate by stable ID before counting.
        - Report exact totals only when coverage is proven. Otherwise say the result is bounded or incomplete.

        ## Summarization behavior

        - Use only the upstream results supplied in the prompt.
        - Preserve important names, timestamps, IDs, decisions, and action items.
        - Mention failures or missing data plainly.
        - Do not claim success if the upstream task failed or returned no relevant content.

        ## Direct answer behavior

        For normal questions that do not require external action, answer directly and concisely. Do not create a graph plan unless the user request requires host-executed work.
        """;

    public static string Router(string userMessage) =>
        $$"""
        You are the router for a ttasks-net chat experiment.

        Return JSON only. Do not wrap it in Markdown.

        If the user can be answered directly without reading external systems, running tool commands, or doing external actions, return:
        {
          "mode": "answer",
          "answer": "your concise answer",
          "planIntent": null
        }

        If the user is asking to use external tools, fan out work, run commands, or summarize external outputs, return:
        {
          "mode": "graph",
          "answer": null,
          "planIntent": "short actionable intent"
        }

        User message:
        {{userMessage}}
        """;

    public static string Planner(string planIntent, CapabilitySet capabilities, int defaultTimeoutSeconds) =>
        $$"""
        Create a ttasks-net graph plan for this intent:
        {{planIntent}}

        Return JSON only. Do not wrap it in Markdown.

        Schema:
        {
          "graph": { "title": "short title", "metadata": { "source": "chat-ui" } },
          "tasks": [
            {
              "id": "stable-id",
              "type": "process|prompt",
              "process": { "fileName": "tool", "args": ["arg1", "arg2"] },
              "prompt": "prompt text for prompt tasks only",
              "libraryItemKey": "optional key of a library template to render and use as process",
              "libraryParameters": { "optionalToken": "value" },
              "librarySuggestion": {
                "key": "stable.semantic.key",
                "displayName": "short name",
                "description": "what this does",
                "fileName": "tool",
                "argsTemplate": ["arg1", "arg2 with {token}"],
                "parameters": [ { "name": "token", "source": "clock.now|clock.yesterday|clock.tomorrow|default", "format": "yyyy-MM-dd", "defaultValue": null } ]
              },
              "title": "optional",
              "description": "optional",
              "timeout": {{defaultTimeoutSeconds}},
              "metadata": { "kind": "read" }
            }
          ],
          "edges": [ { "from": "dep-id", "to": "consumer-id" } ]
        }

        Rules:
        - Use only the allowed tools listed below. Each "process" task's fileName plus leading non-flag args must start with one of the allowed prefixes (e.g. "mail", "teams read", "az account list").
        - Prefer reusing a libraryItemKey from the library suggestions when one matches the request. The host will render that template and run it. Use libraryParameters to override values.
        - When authoring a new strategy you expect to reuse later, include librarySuggestion so the host can promote it to the library after the graph succeeds. The fileName must match process.fileName and the args template head must also satisfy the allowed prefixes.
        - For prompt tasks, set prompt text and do not include process/libraryItemKey/librarySuggestion.
        - Independent reads should fan out. Add one final prompt task that summarizes upstream outputs and depends on all reads.
        - Use stable semantic task ids.

        {{DynamicBindingGuidance}}

        {{UnstructuredToolGuidance}}

        {{HelpDrivenCraftingGuidance}}

        {{LibraryUsageGuidance}}

        {{GraphLibraryUsageGuidance}}

        {{CompleteResultGuidance}}

        Allowed tools:
        {{FormatAllowedTools(capabilities.AllowedTools)}}

        Library suggestions for this turn:
        {{FormatLibrarySuggestions(capabilities.LibrarySuggestions)}}

        Graph library suggestions for this turn:
        {{FormatGraphLibrarySuggestions(capabilities.GraphLibrarySuggestions)}}
        """;

    public static string Repair(string userMessage, string planIntent, CapabilitySet capabilities, string repairContext) =>
        $$"""
        Revise the ttasks-net graph plan after a failed attempt.

        Original user request:
        {{userMessage}}

        Intent:
        {{planIntent}}

        Failure observations:
        {{repairContext}}

        Return JSON only. Do not wrap it in Markdown.

        Use the same schema and rules as the normal planner. Prefer changing only what is needed to recover from the failure. Do not repeat a failed command shape without changing it.

        {{DynamicBindingGuidance}}

        {{UnstructuredToolGuidance}}

        {{HelpDrivenCraftingGuidance}}

        {{LibraryUsageGuidance}}

        {{GraphLibraryUsageGuidance}}

        {{CompleteResultGuidance}}

        Allowed tools:
        {{FormatAllowedTools(capabilities.AllowedTools)}}

        Library suggestions for this turn:
        {{FormatLibrarySuggestions(capabilities.LibrarySuggestions)}}

        Graph library suggestions for this turn:
        {{FormatGraphLibrarySuggestions(capabilities.GraphLibrarySuggestions)}}
        """;

    public static string FormatGraphLibrarySuggestions(IReadOnlyList<GraphLibraryItem> items) =>
        JsonSerializer.Serialize(
            items.Select(item => new
            {
                key = item.Key,
                displayName = item.DisplayName,
                description = item.Description,
                parameters = item.Parameters.Select(p => new
                {
                    name = p.Name,
                    source = p.Source,
                    format = p.Format,
                    defaultValue = p.DefaultValue
                }),
                planTemplate = item.PlanTemplate
            }),
            WriteIndented);

    public static string Continuation(string userMessage, string planIntent, CapabilitySet capabilities, string batchHistory) =>
        $$"""
        You have already executed one or more graph batches for this chat turn. Decide whether to:
        - Return a final answer to the user, OR
        - Run another batch (for example, to use results from the previous batch as inputs to a follow-up tool call).

        Original user request:
        {{userMessage}}

        Intent:
        {{planIntent}}

        Batch history (previous batches with task outputs):
        {{batchHistory}}

        Return JSON only. Do not wrap it in Markdown.

        If you can answer the user's request now:
        {
          "mode": "answer",
          "answer": "final answer text using upstream task outputs"
        }

        If you need another batch (because upstream outputs revealed identifiers, pages, or follow-up reads you couldn't statically plan):
        {
          "mode": "graph",
          "plan": { "graph": { "title": "..." }, "tasks": [ ... ], "edges": [ ... ] }
        }

        Same plan schema and rules as the normal planner apply. You may use outputs from the batch history (for example, chat ids returned by a `chat-list` task) as positional or flag values in the next batch.

        {{HelpDrivenCraftingGuidance}}

        {{CompleteResultGuidance}}

        Allowed tools:
        {{FormatAllowedTools(capabilities.AllowedTools)}}

        Library suggestions for this turn:
        {{FormatLibrarySuggestions(capabilities.LibrarySuggestions)}}
        """;

    public static string FormatAllowedTools(IReadOnlyList<AllowedTool> tools) =>
        JsonSerializer.Serialize(
            tools.Select(tool =>
            {
                var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["prefix"] = tool.Prefix,
                    ["description"] = tool.Description,
                    ["helpCommand"] = tool.HelpCommand
                };
                if (tool.Traits is { Count: > 0 })
                    entry["traits"] = tool.Traits;
                return entry;
            }),
            WriteIndented);

    public static string FormatLibrarySuggestions(IReadOnlyList<TaskLibraryItem> items) =>
        JsonSerializer.Serialize(
            items.Select(item => new
            {
                key = item.Key,
                displayName = item.DisplayName,
                description = item.Description,
                fileName = item.FileName,
                argsTemplate = item.ArgsTemplate,
                parameters = item.Parameters.Select(p => new
                {
                    name = p.Name,
                    source = p.Source,
                    format = p.Format,
                    defaultValue = p.DefaultValue
                })
            }),
            WriteIndented);
}
