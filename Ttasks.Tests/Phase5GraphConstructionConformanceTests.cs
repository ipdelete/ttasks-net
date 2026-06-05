using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;

public class Phase5GraphConstructionConformanceTests
{
    [Fact]
    public void R_GRAPH_01_Graph_Has_Stable_Unique_Id()
    {
        var first = new TaskGraph();
        var second = new TaskGraph();
        var id = first.Id;

        first.Add(CoreTask.Bash("x"));

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(id, first.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void R_GRAPH_02_Title_Defaults_To_Empty_And_Preserves_String()
    {
        Assert.Equal(string.Empty, new TaskGraph().Title);
        Assert.Equal("Build", new TaskGraph("Build").Title);
    }

    [Fact]
    public void R_GRAPH_03_CreatedAt_Is_Set_At_Construction()
    {
        var before = DateTimeOffset.UtcNow;
        var graph = new TaskGraph();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(graph.CreatedAt, before, after);
    }

    [Fact]
    public void R_GRAPH_04_Add_Rejects_Null_Task()
    {
        var graph = new TaskGraph();

        Assert.Throws<ArgumentNullException>(() => graph.Add(null!));
    }

    [Fact]
    public void R_GRAPH_04_Add_Rejects_Null_Dependency_Before_Mutating()
    {
        var graph = new TaskGraph();
        var task = CoreTask.Bash("x");
        var dependencies = new List<CoreTask> { null! };

        Assert.Throws<ArgumentException>(() => graph.Add(task, after: dependencies));

        Assert.Empty(graph.Roots());
    }

    [Fact]
    public void R_GRAPH_05_Add_Deduplicates_Dependencies_In_First_Appearance_Order()
    {
        var graph = new TaskGraph();
        var first = CoreTask.Bash("first");
        var second = CoreTask.Bash("second");
        var leaf = CoreTask.Bash("leaf");

        graph.Add(first);
        graph.Add(second);
        graph.Add(leaf, after: new[] { first, second, first, second });

        Assert.Equal(new[] { first, second }, graph.Dependencies(leaf));
    }

    [Fact]
    public void R_GRAPH_06_Required_False_Without_Finally_Is_Rejected()
    {
        var graph = new TaskGraph();

        Assert.Throws<ArgumentException>(() => graph.Add(CoreTask.Bash("x"), required: false));
    }

    [Fact]
    public void R_GRAPH_06_ReAdd_Updates_Finally_And_Optional_Metadata()
    {
        var graph = new TaskGraph();
        var task = CoreTask.Bash("x");

        graph.Add(task);
        Assert.False(graph.IsFinally(task));
        Assert.False(graph.IsOptional(task));

        graph.Add(task, finally_: true, required: false);

        Assert.True(graph.IsFinally(task));
        Assert.True(graph.IsOptional(task));
        Assert.Equal(new[] { task }, graph.FinallyTasks);
        Assert.Equal(new[] { task }, graph.OptionalTasks);
        Assert.Empty(graph.RequiredTasks);
    }

    [Fact]
    public void R_GRAPH_08_Dependencies_Return_Direct_Upstream_Only()
    {
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var mid = CoreTask.Bash("mid");
        var leaf = CoreTask.Bash("leaf");

        graph.Add(root);
        graph.Add(mid, after: new[] { root });
        graph.Add(leaf, after: new[] { mid });

        Assert.Equal(new[] { mid }, graph.Dependencies(leaf));
    }

    [Fact]
    public void R_GRAPH_09_Roots_And_Leaves_Are_In_Insertion_Order()
    {
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var left = CoreTask.Bash("left");
        var right = CoreTask.Bash("right");
        var tail = CoreTask.Bash("tail");

        graph.Add(root);
        graph.Add(left, after: new[] { root });
        graph.Add(right, after: new[] { root });
        graph.Add(tail, after: new[] { left, right });

        Assert.Equal(new[] { root }, graph.Roots());
        Assert.Equal(new[] { tail }, graph.Leaves());
    }

    [Fact]
    public void R_GRAPH_09_Empty_Graph_Yields_Empty_Roots_And_Leaves()
    {
        var graph = new TaskGraph();

        Assert.Empty(graph.Roots());
        Assert.Empty(graph.Leaves());
    }
}
