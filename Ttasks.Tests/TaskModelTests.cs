using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class TaskModelTests
{
    [Fact]
    public void Constructor_DefaultsAndValidates()
    {
        var task = new TaskModel(TaskType.Bash, "echo hello", title: "Say hello");

        Assert.Equal(TaskState.Pending, task.Status);
        Assert.Equal("Say hello", task.Title);
        Assert.Equal(string.Empty, task.Description);
        Assert.Null(task.Timeout);
        Assert.False(string.IsNullOrWhiteSpace(task.Id));

        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskModel((TaskType)99, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskModel(TaskType.Bash, "x", timeout: 0));
    }

    [Fact]
    public void TransitionRules_RejectInvalidMoves()
    {
        var task = new TaskModel(TaskType.Prompt, "hello");

        task.TransitionTo(TaskState.Failed, "boom");
        Assert.Equal(TaskState.Failed, task.Status);
        Assert.Equal("boom", task.Error);

        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(TaskState.Blocked));
        Assert.Equal(TaskState.Failed, task.Status);
    }

    [Fact]
    public void RunningEntry_ClearsCarryoverAndSucceededClearsError()
    {
        var task = new TaskModel(TaskType.Bash, "echo x");
        task.TransitionTo(TaskState.Failed, "boom");
        task.AttachResult(new TaskResult { TaskId = task.Id, Status = TaskState.Failed, Error = "old" });
        task.AttachBlockedBy("upstream");

        task.TransitionTo(TaskState.Running);

        Assert.Null(task.Result);
        Assert.Null(task.BlockedBy);
        Assert.Null(task.Error);

        task.TransitionTo(TaskState.Succeeded);
        Assert.Null(task.Error);
    }

    [Fact]
    public void Cancel_IsIdempotentForNonTerminalTasks()
    {
        var task = new TaskModel(TaskType.Bash, "echo x");

        task.TransitionTo(TaskState.Failed, "boom");
        task.Cancel();

        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.Equal("boom", task.Error);

        task.Cancel();
        Assert.Equal(TaskState.Cancelled, task.Status);
    }

    [Fact]
    public void Equality_IsById_NotObjectIdentity()
    {
        const string sharedId = "shared-id";
        var first = new TaskModel(sharedId, TaskType.Bash, "a");
        var sameId = new TaskModel(sharedId, TaskType.Bash, "b");

        Assert.Equal(first, sameId);
        Assert.Equal(first.GetHashCode(), sameId.GetHashCode());

        Assert.NotEqual(first, new TaskModel("other", TaskType.Bash, "c"));
    }
}
