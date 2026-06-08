using System.Collections.Concurrent;
using GitHub.Copilot;
using Microsoft.Extensions.Options;
using Ttasks.Core;

namespace Ttasks.ChatApp.Services;

public sealed class ChatSessionRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, LlmAgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly ILlmProvider _provider;
    private readonly ChatAppOptions _options;

    public ChatSessionRegistry(ILlmProvider provider, IOptions<ChatAppOptions> options)
    {
        _provider = provider;
        _options = options.Value;
    }

    public (string SessionId, LlmAgentSession Session) GetOrCreate(string? sessionId)
    {
        var id = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        return (id, _sessions.GetOrAdd(id, _ =>
        {
            var allowedTools = _options.AllowedTools
                .Where(tool => !string.IsNullOrWhiteSpace(tool.Prefix))
                .Select(tool => new AllowedTool(tool.Prefix.Trim(), tool.Description, tool.HelpCommand))
                .ToList();

            var session = new LlmAgentSession(_provider, new LlmSessionOptions
            {
                Model = _options.Model,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = Prompts.SystemMessage(allowedTools)
                },
                SkipCustomInstructions = true
            });
            session.Enter();
            return session;
        }));
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
