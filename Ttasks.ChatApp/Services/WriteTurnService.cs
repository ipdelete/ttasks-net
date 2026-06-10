using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.ChatApp.Services;

public sealed class WriteTurnService
{
    private const string TurnIdKey = "turnId";
    private const string SessionIdKey = "sessionId";
    private const string TaskKindKey = "kind";
    private const string BatchKey = "batch";

    private readonly ChatSessionRegistry _sessions;
    private readonly ITaskStore _store;
    private readonly ChatTurnService _chat;

    public WriteTurnService(ChatSessionRegistry sessions, ITaskStore store, ChatTurnService chat)
    {
        _sessions = sessions;
        _store = store;
        _chat = chat;
    }

    public WriteResponse RunAi(string? sessionId, string? turnId, int index, int total, string prompt)
    {
        var (activeSessionId, llmSession) = _sessions.GetOrCreate(sessionId);
        var activeTurnId = string.IsNullOrWhiteSpace(turnId)
            ? Guid.NewGuid().ToString("N")
            : turnId!;

        var executor = new TaskExecutor(_store);
        executor.Register(TaskType.Prompt, llmSession.PromptHandler(new LlmHandlerOptions
        {
            IncludeUpstreamResults = false
        }));

        var safeIndex = Math.Max(1, index);
        var safeTotal = Math.Max(safeIndex, total);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [TurnIdKey] = activeTurnId,
            [SessionIdKey] = activeSessionId,
            [TaskKindKey] = "write",
            [BatchKey] = $"{safeIndex}/{safeTotal}"
        };

        var task = CoreTask.Prompt(
            prompt,
            title: $"Write directive {safeIndex}/{safeTotal}",
            metadata: metadata);

        var result = executor.Execute(task);
        return new WriteResponse(
            Reply: result.Output ?? string.Empty,
            SessionId: activeSessionId,
            TurnId: activeTurnId,
            GraphId: null,
            TaskCount: 1,
            Succeeded: true);
    }

    public WriteResponse RunTtasks(string? sessionId, string? turnId, int index, int total, string intent)
    {
        var planned = _chat.RunPlannedTurn(sessionId, turnId, intent);

        if (!planned.Succeeded)
        {
            return new WriteResponse(
                Reply: planned.Answer,
                SessionId: planned.SessionId,
                TurnId: planned.TurnId,
                GraphId: planned.GraphId,
                TaskCount: planned.Tasks.Count,
                Succeeded: false);
        }

        var safeIndex = Math.Max(1, index);
        var safeTotal = Math.Max(safeIndex, total);
        var taskCount = planned.Tasks.Count;
        var callout = $"> 🛠 **ttasks** {safeIndex}/{safeTotal} · {taskCount} task{(taskCount == 1 ? "" : "s")} · [view turn](/admin/turns#turn-{planned.TurnId})";
        var body = $"{callout}\n\n{planned.Answer.TrimEnd()}";

        return new WriteResponse(
            Reply: body,
            SessionId: planned.SessionId,
            TurnId: planned.TurnId,
            GraphId: planned.GraphId,
            TaskCount: taskCount,
            Succeeded: true);
    }
}

public sealed record WriteResponse(
    string Reply,
    string SessionId,
    string TurnId,
    string? GraphId,
    int TaskCount,
    bool Succeeded);

