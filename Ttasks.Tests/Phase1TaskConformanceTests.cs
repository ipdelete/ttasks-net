using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase1TaskConformanceTests
{
    public static IEnumerable<object[]> BuiltInTypes()
    {
        yield return new object[] { TaskType.Bash, "bash" };
        yield return new object[] { TaskType.Powershell, "powershell" };
        yield return new object[] { TaskType.Prompt, "prompt" };
        yield return new object[] { TaskType.Agent, "agent" };
        yield return new object[] { TaskType.Process, "process" };
    }

    [Fact]
    public void R_TASK_01_Rejects_Unknown_Task_Type_At_Construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTask((TaskType)999, "x"));
    }

    [Theory]
    [MemberData(nameof(BuiltInTypes))]
    public void R_TASK_01_Accepts_Every_Built_In_Task_Type(TaskType type, string typeName)
    {
        var task = new CoreTask(type, "payload");

        Assert.Equal(type, task.Type);
        Assert.Equal(typeName, task.TypeName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void R_TASK_02_Rejects_Non_Positive_Timeouts_At_Construction(int timeout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreTask(TaskType.Bash, "x", timeout: timeout));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void R_TASK_02_Rejects_Non_Positive_Timeouts_On_Assignment(int timeout)
    {
        var task = CoreTask.Bash("x");

        Assert.Throws<ArgumentOutOfRangeException>(() => task.Timeout = timeout);
    }

    [Fact]
    public void R_TASK_03_Timeout_Defaults_To_Unbounded()
    {
        var task = CoreTask.Bash("x");

        Assert.Null(task.Timeout);
    }

    [Fact]
    public void R_TASK_04_Title_And_Description_Default_To_Empty_Strings()
    {
        var task = CoreTask.Bash("x");

        Assert.Equal(string.Empty, task.Title);
        Assert.Equal(string.Empty, task.Description);
    }

    [Fact]
    public void R_TASK_05_Constructed_Tasks_Get_Fresh_Ids()
    {
        var ids = Enumerable.Range(0, 100)
            .Select(_ => new CoreTask(TaskType.Bash, "x").Id)
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public void R_TASK_05_Factory_Built_Tasks_Get_Fresh_Ids()
    {
        var ids = new[]
        {
            CoreTask.Bash("x").Id,
            CoreTask.Bash("x").Id,
            CoreTask.Powershell("x").Id,
            CoreTask.Prompt("x").Id,
            CoreTask.Agent("x").Id
        };

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void R_TASK_06_Equality_And_Hash_Membership_Are_By_Id()
    {
        var first = new CoreTask("same", TaskType.Bash, "a");
        var second = new CoreTask("same", TaskType.Prompt, "b");
        var other = new CoreTask("other", TaskType.Bash, "a");
        var set = new HashSet<CoreTask> { first };

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Contains(second, set);
        Assert.DoesNotContain(other, set);
    }

    [Fact]
    public void R_TASK_07_Status_Is_Read_Only_Externally()
    {
        var property = typeof(CoreTask).GetProperty(nameof(CoreTask.Status))!;

        Assert.NotNull(property.GetMethod);
        Assert.False(property.SetMethod?.IsPublic == true);
    }

    [Fact]
    public void R_TASK_08_Result_And_BlockedBy_Are_Read_Only_Externally()
    {
        var result = typeof(CoreTask).GetProperty(nameof(CoreTask.Result))!;
        var blockedBy = typeof(CoreTask).GetProperty(nameof(CoreTask.BlockedBy))!;

        Assert.NotNull(result.GetMethod);
        Assert.NotNull(blockedBy.GetMethod);
        Assert.False(result.SetMethod?.IsPublic == true);
        Assert.False(blockedBy.SetMethod?.IsPublic == true);
    }

    [Theory]
    [InlineData(TaskState.Pending)]
    [InlineData(TaskState.Running)]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Blocked)]
    public void R_TASK_09_Non_Succeeded_Tasks_Remain_Editable_For_Retry(TaskState state)
    {
        var task = CreateTaskInState(state);

        task.Payload = "new payload";
        task.Title = "new title";
        task.Description = "new description";
        task.Timeout = 10;

        Assert.Equal("new payload", task.Payload);
        Assert.Equal("new title", task.Title);
        Assert.Equal("new description", task.Description);
        Assert.Equal(10, task.Timeout);
    }

    [Theory]
    [MemberData(nameof(BuiltInTypes))]
    public void R_TASK_10_Built_In_Task_Type_Names_Are_Stable(TaskType type, string typeName)
    {
        var task = new CoreTask(type, "payload");

        Assert.Equal(typeName, task.TypeName);
    }

    [Fact]
    public void R_TASK_11_Bash_Factory_Sets_Type_And_Payload()
    {
        var task = CoreTask.Bash("echo hi");

        Assert.Equal(TaskType.Bash, task.Type);
        Assert.Equal("echo hi", task.Payload);
    }

    [Fact]
    public void R_TASK_11_Powershell_Factory_Sets_Type_And_Payload()
    {
        var task = CoreTask.Powershell("Write-Host hi");

        Assert.Equal(TaskType.Powershell, task.Type);
        Assert.Equal("Write-Host hi", task.Payload);
    }

    [Fact]
    public void R_TASK_11_Prompt_Factory_Sets_Type_And_Payload()
    {
        var task = CoreTask.Prompt("Summarize this");

        Assert.Equal(TaskType.Prompt, task.Type);
        Assert.Equal("Summarize this", task.Payload);
    }

    [Fact]
    public void R_TASK_11_Agent_Factory_Sets_Type_And_Payload()
    {
        var task = CoreTask.Agent("Plan work");

        Assert.Equal(TaskType.Agent, task.Type);
        Assert.Equal("Plan work", task.Payload);
    }

    [Fact]
    public void R_TASK_11_Factories_Accept_Metadata_And_Timeout()
    {
        var task = CoreTask.Bash("x", title: "Build", description: "Compile", timeout: 30);

        Assert.Equal("Build", task.Title);
        Assert.Equal("Compile", task.Description);
        Assert.Equal(30, task.Timeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void R_TASK_02_AND_R_TASK_11_Factory_Timeout_Validation_Still_Applies(int timeout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoreTask.Bash("x", timeout: timeout));
    }

    [Fact]
    public void R_TASK_15_ToString_Includes_Id_Title_And_Status_But_Not_Payload()
    {
        var task = CoreTask.Bash("secret payload", title: "Visible title");

        var text = task.ToString();

        Assert.Contains(task.Id, text, StringComparison.Ordinal);
        Assert.Contains("Visible title", text, StringComparison.Ordinal);
        Assert.Contains(TaskState.Pending.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret payload", text, StringComparison.Ordinal);
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
            case TaskState.Failed:
                task.TransitionTo(TaskState.Failed, "boom");
                return task;
            case TaskState.Blocked:
                task.TransitionTo(TaskState.Blocked, blockedBy: "parent");
                return task;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }
}
