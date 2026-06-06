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
            var session = new LlmAgentSession(_provider, new LlmSessionOptions
            {
                Model = _options.Model,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = ReadSystemMessage()
                },
                SkipCustomInstructions = true
            });
            session.Enter();
            return session;
        }));
    }

    private string ReadSystemMessage()
    {
        var configuredPath = _options.SystemMessagePath;
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("ChatApp:SystemMessagePath must be configured.");

        var candidates = Path.IsPathRooted(configuredPath)
            ? new[] { configuredPath }
            : new[]
            {
                Path.GetFullPath(configuredPath, Directory.GetCurrentDirectory()),
                Path.GetFullPath(configuredPath, AppContext.BaseDirectory)
            };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
            throw new FileNotFoundException($"Chat app system message file was not found. Configure ChatApp:SystemMessagePath or create '{configuredPath}'.", configuredPath);

        var content = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException($"Chat app system message file '{path}' must not be empty.");

        return content;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
