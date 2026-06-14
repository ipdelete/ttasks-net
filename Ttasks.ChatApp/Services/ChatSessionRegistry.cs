using System.Collections.Concurrent;
using GitHub.Copilot;
using Microsoft.Extensions.Options;
using Ttasks.Core;

namespace Ttasks.ChatApp.Services;

public sealed class ChatSessionRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly ILlmProvider _provider;
    private readonly ChatAppOptions _options;

    public ChatSessionRegistry(ILlmProvider provider, IOptions<ChatAppOptions> options)
    {
        _provider = provider;
        _options = options.Value;
    }

    public static string ResolveSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;

    public LlmAgentSession GetOrCreate(string sessionId, IReadOnlyList<AllowedTool> allowedTools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var systemMessage = Prompts.SystemMessage(allowedTools);
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var existing)
                && string.Equals(existing.SystemMessage, systemMessage, StringComparison.Ordinal))
            {
                return existing.Session;
            }

            if (existing is not null)
            {
                existing.Session.Dispose();
                _sessions.TryRemove(sessionId, out _);
            }

            var session = new LlmAgentSession(_provider, new LlmSessionOptions
            {
                Model = _options.Model,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = systemMessage
                },
                SkipCustomInstructions = true
            });
            session.Enter();
            _sessions[sessionId] = new SessionEntry(session, systemMessage);
            return session;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _sessions.Values)
            entry.Session.Dispose();
        _sessions.Clear();
    }

    private sealed record SessionEntry(LlmAgentSession Session, string SystemMessage);
}
