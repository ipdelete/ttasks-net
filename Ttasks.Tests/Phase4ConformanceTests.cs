using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;

public class Phase4ConformanceTests
{
    [Fact]
    public void R_STORE_01_02_Store_Exposes_Independent_Collections_With_Structural_Shape()
    {
        var store = new InMemoryStore();

        Assert.NotNull(store.Tasks);
        Assert.NotNull(store.Graphs);

        Assert.True(store.Tasks.GetType().GetMethod("Save") is not null);
        Assert.True(store.Tasks.GetType().GetMethod("Get") is not null);
        Assert.True(store.Tasks.GetType().GetMethod("Delete") is not null);
        Assert.True(store.Tasks.GetType().GetMethod("Has") is not null);

        Assert.True(store.Graphs.GetType().GetMethod("Save") is not null);
        Assert.True(store.Graphs.GetType().GetMethod("Get") is not null);
        Assert.True(store.Graphs.GetType().GetMethod("Delete") is not null);
        Assert.True(store.Graphs.GetType().GetMethod("Has") is not null);

        var task = TaskModel.Bash("task");
        store.Tasks.Save(task);
        Assert.True(store.Tasks.Has(task.Id));
        Assert.False(store.Graphs.Has(task.Id));
    }

    [Fact]
    public void R_STORE_03_04_05_Save_Uses_Id_And_Rejects_Bad_Input()
    {
        var store = new InMemoryStore();
        var task = TaskModel.Bash("x");

        store.Tasks.Save(task);
        Assert.Same(task, store.Tasks.Get(task.Id));

        Assert.Throws<ArgumentException>(() => store.Tasks.Set("other", task));
        Assert.Throws<ArgumentException>(() => store.Tasks.Set(task.Id, "bad"));

        var graph = new TaskGraph("g");
        store.Graphs.Save(graph);
        Assert.Same(graph, store.Graphs.Get(graph.Id));

        Assert.Throws<ArgumentException>(() => store.Graphs.Set("other", graph));
        Assert.Throws<ArgumentException>(() => store.Graphs.Set(graph.Id, "bad"));
    }

    [Fact]
    public void R_STORE_06_07_08_09_Missing_Key_Delete_And_Has_Behavior()
    {
        var store = new InMemoryStore();
        var task = TaskModel.Bash("x");
        store.Tasks.Save(task);

        Assert.Throws<KeyNotFoundException>(() => store.Tasks.Get("missing"));
        Assert.False(store.Tasks.Has("missing"));
        Assert.True(store.Tasks.Has(task));

        store.Tasks.Delete(task.Id);
        Assert.Throws<KeyNotFoundException>(() => store.Tasks.Get(task.Id));

        var graph = new TaskGraph("g");
        store.Graphs.Save(graph);
        store.Graphs.Delete(graph.Id);
        Assert.Throws<KeyNotFoundException>(() => store.Graphs.Get(graph.Id));
    }

    [Fact]
    public void R_STORE_10_11_Iteration_Order_And_Live_References()
    {
        var store = new InMemoryStore();
        var first = TaskModel.Bash("first");
        var second = TaskModel.Bash("second");
        store.Tasks.Save(first);
        store.Tasks.Save(second);

        Assert.Equal(new[] { first.Id, second.Id }, store.Tasks.Keys.ToArray());
        Assert.Equal(2, store.Tasks.Keys.Count());

        var loaded = store.Tasks.Get(first.Id);
        loaded.Title = "mutated";
        Assert.Equal("mutated", store.Tasks.Get(first.Id).Title);
        Assert.Same(loaded, store.Tasks.Get(first.Id));

        var graph = new TaskGraph("g");
        store.Graphs.Save(graph);
        Assert.Same(graph, store.Graphs.Get(graph.Id));
    }

    [Fact]
    public void R_STORE_12_InMemory_Cancel_Helper_Updates_Task_Status()
    {
        var store = new InMemoryStore();
        var task = TaskModel.Bash("x");
        store.Tasks.Save(task);

        store.Tasks.Cancel(task.Id);

        Assert.Equal(Ttasks.Core.TaskStatus.Cancelled, task.Status);
    }
}
