using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase1StateMachineConformanceTests
{
    public static IEnumerable<object[]> AllowedTransitions()
    {
        yield return new object[] { TaskState.Pending, TaskState.Running };
        yield return new object[] { TaskState.Pending, TaskState.Failed };
        yield return new object[] { TaskState.Pending, TaskState.Cancelled };
        yield return new object[] { TaskState.Pending, TaskState.Blocked };
        yield return new object[] { TaskState.Running, TaskState.Succeeded };
        yield return new object[] { TaskState.Running, TaskState.Failed };
        yield return new object[] { TaskState.Running, TaskState.Cancelled };
        yield return new object[] { TaskState.Running, TaskState.Blocked };
        yield return new object[] { TaskState.Failed, TaskState.Running };
        yield return new object[] { TaskState.Failed, TaskState.Cancelled };
        yield return new object[] { TaskState.Blocked, TaskState.Running };
        yield return new object[] { TaskState.Blocked, TaskState.Cancelled };
    }

    public static IEnumerable<object[]> DisallowedTransitions()
    {
        var allowed = AllowedTransitions()
            .Select(row => ((TaskState)row[0], (TaskState)row[1]))
            .ToHashSet();

        foreach (var from in Enum.GetValues<TaskState>())
        {
            foreach (var to in Enum.GetValues<TaskState>())
            {
                if (!allowed.Contains((from, to)))
                    yield return new object[] { from, to };
            }
        }
    }

    public static IEnumerable<object[]> PredicateClassifications()
    {
        yield return new object[] { TaskState.Pending, true, false, false, false, false, false, true, false, false, false };
        yield return new object[] { TaskState.Running, false, true, false, false, false, false, true, false, false, false };
        yield return new object[] { TaskState.Succeeded, false, false, true, false, false, false, false, true, true, false };
        yield return new object[] { TaskState.Failed, false, false, false, true, false, false, false, false, false, true };
        yield return new object[] { TaskState.Cancelled, false, false, false, false, true, false, false, true, true, true };
        yield return new object[] { TaskState.Blocked, false, false, false, false, false, true, false, false, false, true };
    }

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void R_SM_01_Every_Allowed_Edge_Is_Accepted(TaskState from, TaskState to)
    {
        var task = CreateTaskInState(from);

        Assert.True(task.CanTransitionTo(to));
        task.TransitionTo(to, error: "error", blockedBy: "parent");

        Assert.Equal(to, task.Status);
    }

    [Theory]
    [MemberData(nameof(DisallowedTransitions))]
    public void R_SM_01_Disallowed_Edges_Are_Rejected_Without_Mutation(TaskState from, TaskState to)
    {
        var task = CreateTaskInState(from);
        var originalStatus = task.Status;
        var originalError = task.Error;
        var originalResult = task.Result;
        var originalBlockedBy = task.BlockedBy;

        Assert.False(task.CanTransitionTo(to));
        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(to, error: "new", blockedBy: "new-parent"));

        Assert.Equal(originalStatus, task.Status);
        Assert.Equal(originalError, task.Error);
        Assert.Same(originalResult, task.Result);
        Assert.Equal(originalBlockedBy, task.BlockedBy);
    }

    [Theory]
    [InlineData(TaskState.Succeeded)]
    [InlineData(TaskState.Cancelled)]
    public void R_SM_02_Sink_States_Have_No_Outgoing_Transitions(TaskState sink)
    {
        var task = CreateTaskInState(sink);

        foreach (var next in Enum.GetValues<TaskState>())
        {
            Assert.False(task.CanTransitionTo(next));
            Assert.Throws<InvalidOperationException>(() => task.TransitionTo(next));
        }

        Assert.True(task.IsSink);
        Assert.True(task.IsTerminal);
    }

    [Fact]
    public void R_SM_03_Entering_Running_From_Failed_Clears_Result_Error_And_BlockedBy()
    {
        var task = CoreTask.Bash("x");
        var result = new TaskResult { TaskId = task.Id, Status = TaskState.Failed, Error = "old" };
        task.TransitionTo(TaskState.Failed, "boom");
        task.AttachResult(result);
        task.AttachBlockedBy("parent");

        task.TransitionTo(TaskState.Running);

        Assert.Null(task.Result);
        Assert.Null(task.Error);
        Assert.Null(task.BlockedBy);
    }

    [Fact]
    public void R_SM_03_Entering_Running_From_Blocked_Clears_BlockedBy()
    {
        var task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Blocked, blockedBy: "parent");

        task.TransitionTo(TaskState.Running);

        Assert.Null(task.BlockedBy);
        Assert.Null(task.Result);
        Assert.Null(task.Error);
    }

    [Fact]
    public void R_SM_04_Entering_Succeeded_Clears_Even_Explicit_Error()
    {
        var task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Running);

        task.TransitionTo(TaskState.Succeeded, error: "ignored");

        Assert.Null(task.Error);
    }

    [Fact]
    public void R_SM_05_Failed_Preserves_Error_Until_Next_Run()
    {
        var task = CoreTask.Bash("x");

        task.TransitionTo(TaskState.Failed, "boom");

        Assert.Equal("boom", task.Error);

        task.TransitionTo(TaskState.Running);
        Assert.Null(task.Error);
    }

    [Fact]
    public void R_SM_06_Failed_To_Cancelled_Preserves_Prior_Error()
    {
        var task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Failed, "boom");

        task.TransitionTo(TaskState.Cancelled);

        Assert.Equal("boom", task.Error);
    }

    [Theory]
    [InlineData(TaskState.Succeeded)]
    [InlineData(TaskState.Cancelled)]
    public void R_SM_07_Cancel_Is_Idempotent_On_Sink_States(TaskState sink)
    {
        var task = CreateTaskInState(sink);
        var result = task.Result;
        var error = task.Error;

        task.Cancel();
        task.Cancel();

        Assert.Equal(sink, task.Status);
        Assert.Same(result, task.Result);
        Assert.Equal(error, task.Error);
    }

    [Theory]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Blocked)]
    public void R_SM_08_Failed_And_Blocked_Are_Retryable(TaskState retryable)
    {
        var task = CreateTaskInState(retryable);

        Assert.True(task.CanTransitionTo(TaskState.Running));
        task.TransitionTo(TaskState.Running);

        Assert.Equal(TaskState.Running, task.Status);
    }

    [Fact]
    public void R_SM_09_Succeeded_Tasks_Reject_Public_Field_Mutation()
    {
        var task = CreateTaskInState(TaskState.Succeeded);

        Assert.Throws<InvalidOperationException>(() => task.Payload = "new");
        Assert.Throws<InvalidOperationException>(() => task.Title = "new");
        Assert.Throws<InvalidOperationException>(() => task.Description = "new");
        Assert.Throws<InvalidOperationException>(() => task.Timeout = 10);
    }

    [Fact]
    public void R_SM_10_Id_Is_Assigned_Once_And_Read_Only()
    {
        var task = CoreTask.Bash("x");
        var id = task.Id;

        task.TransitionTo(TaskState.Running);
        task.TransitionTo(TaskState.Succeeded);

        Assert.Equal(id, task.Id);
        Assert.Null(typeof(CoreTask).GetProperty(nameof(CoreTask.Id))!.SetMethod);
    }

    [Theory]
    [MemberData(nameof(PredicateClassifications))]
    public void R_SM_11_Status_Predicates_Agree_With_Classification_Table(
        TaskState state,
        bool pending,
        bool running,
        bool succeeded,
        bool failed,
        bool cancelled,
        bool blocked,
        bool active,
        bool terminal,
        bool sink,
        bool bad)
    {
        var task = CreateTaskInState(state);

        Assert.Equal(pending, task.IsPending);
        Assert.Equal(running, task.IsRunning);
        Assert.Equal(succeeded, task.IsSucceeded);
        Assert.Equal(failed, task.IsFailed);
        Assert.Equal(cancelled, task.IsCancelled);
        Assert.Equal(blocked, task.IsBlocked);
        Assert.Equal(active, task.IsActive);
        Assert.Equal(terminal, task.IsTerminal);
        Assert.Equal(sink, task.IsSink);
        Assert.Equal(bad, task.IsBad);
    }

    private static CoreTask CreateTaskInState(TaskState state)
    {
        var task = CoreTask.Bash("x");
        switch (state)
        {
            case TaskState.Pending:
                return task;
            case TaskState.Running:
                task.TransitionTo(TaskState.Running);
                return task;
            case TaskState.Succeeded:
                task.TransitionTo(TaskState.Running);
                task.TransitionTo(TaskState.Succeeded);
                return task;
            case TaskState.Failed:
                task.TransitionTo(TaskState.Failed, "boom");
                return task;
            case TaskState.Cancelled:
                task.TransitionTo(TaskState.Cancelled);
                return task;
            case TaskState.Blocked:
                task.TransitionTo(TaskState.Blocked, blockedBy: "parent");
                return task;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }
}
