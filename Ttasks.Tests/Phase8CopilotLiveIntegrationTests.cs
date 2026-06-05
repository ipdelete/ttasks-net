using Ttasks.Core;

namespace Ttasks.Tests;

public sealed class Phase8CopilotLiveIntegrationTests
{
    [Fact]
    public void Live_CopilotSdk_Prompt_With_Gpt55_Returns_Pong()
    {
        if (Environment.GetEnvironmentVariable("TTASKS_RUN_COPILOT_LIVE_TESTS") != "1")
            return;

        var provider = new CopilotSdkProvider();
        var executor = new TaskExecutor();
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider, new LlmHandlerOptions
        {
            Model = "gpt-5.5",
            Timeout = TimeSpan.FromSeconds(120)
        }));

        var result = executor.Execute(Ttasks.Core.Task.Prompt(
            "Reply with exactly the lowercase word pong and no punctuation.",
            timeout: 120));

        var normalized = result.Output.Trim().Trim('"', '\'', '`').Trim();
        Assert.Equal("pong", normalized, ignoreCase: true);
    }
}
