using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class TaskFactoryTests
{
    [Fact]
    public void Factories_PreserveTypeAndPayload()
    {
        var bash = TaskModel.Bash("echo hi", title: "Build", description: "desc", timeout: 30);
        var powershell = TaskModel.Powershell("Write-Host hi");
        var prompt = TaskModel.Prompt("Hello");
        var agent = TaskModel.Agent("Plan");

        Assert.Equal(TaskType.Bash, bash.Type);
        Assert.Equal("echo hi", bash.Payload);
        Assert.Equal("Build", bash.Title);
        Assert.Equal("desc", bash.Description);
        Assert.Equal(30, bash.Timeout);
        Assert.Equal("bash", bash.TypeName);

        Assert.Equal(TaskType.Powershell, powershell.Type);
        Assert.Equal(TaskType.Prompt, prompt.Type);
        Assert.Equal(TaskType.Agent, agent.Type);
    }

    [Fact]
    public void CanTransitionTo_ReflectsAllowedMoves()
    {
        var task = TaskModel.Bash("x");

        Assert.True(task.CanTransitionTo(TaskState.Running));
        Assert.True(task.CanTransitionTo(TaskState.Failed));
        Assert.False(task.CanTransitionTo(TaskState.Succeeded));
    }
}
