using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Ttasks.LiveE2ETests;

public sealed class LiveChatAppFixture : IAsyncLifetime
{
    private readonly string _repoRoot = FindRepoRoot();
    private Process? _process;

    public HttpClient Client { get; private set; } = null!;
    public string BaseUrl { get; private set; } = string.Empty;
    public string StorePath { get; } = Path.Combine(Path.GetTempPath(), $"ttasks-live-e2e-{Guid.NewGuid():N}.db");
    public string LogPath { get; } = Path.Combine(Path.GetTempPath(), $"ttasks-live-e2e-{Guid.NewGuid():N}.log");

    public async Task InitializeAsync()
    {
        var port = ReservePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        Client = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_repoRoot, "Ttasks.ChatApp", "Ttasks.ChatApp.csproj"));
        startInfo.ArgumentList.Add("--no-launch-profile");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(BaseUrl);
        startInfo.Environment["ChatApp__StorePath"] = StorePath;
        startInfo.Environment["ChatApp__AdminWriteLocalOnly"] = "true";

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Ttasks.ChatApp.");

        _ = Task.Run(() => PumpOutput(_process.StandardOutput));
        _ = Task.Run(() => PumpOutput(_process.StandardError));

        await WaitForAppAsync();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        foreach (var path in new[] { StorePath, $"{StorePath}-wal", $"{StorePath}-shm", LogPath })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public string ReadLog() => File.Exists(LogPath) ? File.ReadAllText(LogPath) : string.Empty;

    private async Task WaitForAppAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"Ttasks.ChatApp exited before startup. Log:\n{ReadLog()}");

            try
            {
                using var response = await Client.GetAsync("/api/admin/capabilities");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Ttasks.ChatApp did not become ready. Last error: {lastException?.Message}\nLog:\n{ReadLog()}");
    }

    private async Task PumpOutput(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                await File.AppendAllTextAsync(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // Test diagnostics only; process readiness/exit is checked elsewhere.
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TtasksNet.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
