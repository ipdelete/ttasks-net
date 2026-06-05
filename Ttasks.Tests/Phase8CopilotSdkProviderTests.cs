using Ttasks.Core;

namespace Ttasks.Tests;

public sealed class Phase8CopilotSdkProviderTests
{
    [Fact]
    public void R_COP_23_CopilotSdkProvider_Implements_Generic_LlmProvider_Interface()
    {
        ILlmProvider provider = new CopilotSdkProvider();

        using var session = provider.CreateSession(new LlmSessionOptions { Model = "gpt-5.5" });

        Assert.IsAssignableFrom<ILlmSession>(session);
    }

    [Fact]
    public void R_COP_24_Agent_Permission_Policy_Is_Passed_Through_Request()
    {
        var provider = new RecordingLlmProvider("ok");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Agent, CopilotHandlers.MakeAgentHandler(provider, new LlmHandlerOptions
        {
            PermissionPolicy = "custom-policy"
        }));

        executor.Execute(Ttasks.Core.Task.Agent("permission check"));

        Assert.Equal("custom-policy", provider.Requests.Single().PermissionPolicy);
    }
}
