using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;

public class InMemoryStoreTests
{
    [Fact]
    public void Tasks_Save_And_Get_Return_Live_References()
    {
        var store = new InMemoryStore();
        var task = TaskModel.Bash("echo hi");

        store.Tasks.Save(task);

        Assert.Same(task, store.Tasks.Get(task.Id));
        Assert.True(store.Tasks.Has(task.Id));
        Assert.True(store.Tasks.Has(task));
        Assert.False(store.Tasks.Has("missing"));
        Assert.Single(store.Tasks.Keys);
    }

    [Fact]
    public void Tasks_Set_Rejects_Mismatch_And_Non_Task_Values()
    {
        var store = new InMemoryStore();
        var task = TaskModel.Bash("x");

        Assert.Throws<ArgumentException>(() => store.Tasks.Set("other", task));
        Assert.Throws<ArgumentException>(() => store.Tasks.Set(task.Id, "bad"));
    }

    [Fact]
    public void Tasks_Delete_And_Cancel_Work()
    {
        var store = new InMemoryStore();
        var task = TaskModel.Bash("x");
        store.Tasks.Save(task);

        store.Tasks.Delete(task.Id);

        Assert.Throws<KeyNotFoundException>(() => store.Tasks.Get(task.Id));

        task = TaskModel.Bash("y");
        store.Tasks.Save(task);
        store.Tasks.Cancel(task.Id);

        Assert.Equal(Ttasks.Core.TaskStatus.Cancelled, task.Status);
    }

    [Fact]
    public void Graphs_Save_Persists_Member_Tasks_And_Rejects_Bad_Input()
    {
        var store = new InMemoryStore();
        var root = TaskModel.Bash("root");
        var leaf = TaskModel.Bash("leaf");
        var graph = new TaskGraph("demo");
        graph.Add(root);
        graph.Add(leaf, after: new[] { root });

        store.Graphs.Save(graph);

        Assert.Same(root, store.Tasks.Get(root.Id));
        Assert.Same(leaf, store.Tasks.Get(leaf.Id));
        Assert.Same(graph, store.Graphs.Get(graph.Id));
        Assert.True(store.Graphs.Has(graph));
        Assert.False(store.Graphs.Has("missing"));

        Assert.Throws<ArgumentException>(() => store.Graphs.Set(graph.Id, "bad"));
        Assert.Throws<ArgumentException>(() => store.Graphs.Set("other", graph));
    }

    [Fact]
    public void Collections_Keep_Insertion_Order_And_Are_Independent()
    {
        var store = new InMemoryStore();
        var first = TaskModel.Bash("first");
        var second = TaskModel.Bash("second");
        var graph = new TaskGraph("g");

        store.Tasks.Save(first);
        store.Tasks.Save(second);
        store.Graphs.Save(graph);

        Assert.Equal(new[] { first.Id, second.Id }, store.Tasks.Keys.ToArray());
        Assert.Single(store.Graphs.Keys);

        Assert.False(store.Tasks.Has(graph));
        Assert.False(store.Graphs.Has(first));
    }
}
