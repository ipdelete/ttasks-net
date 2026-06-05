using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase4TaskCollectionConformanceTests
{
    [Fact]
    public void R_STORE_01_Store_Exposes_Task_And_Graph_Collections_Independently()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");
        var graph = new TaskGraph("g");

        store.Tasks.Save(task);
        store.Graphs.Save(graph);

        Assert.True(store.Tasks.Has(task.Id));
        Assert.True(store.Graphs.Has(graph.Id));
        Assert.False(store.Tasks.Has(graph.Id));
        Assert.False(store.Graphs.Has(task.Id));
    }

    [Fact]
    public void R_STORE_02_Task_Collection_Satisfies_Structural_Shape()
    {
        var collection = new InMemoryStore().Tasks;

        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Save)));
        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Get)));
        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Set)));
        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Delete)));
        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Has)));
        Assert.NotNull(collection.GetType().GetProperty("Item"));
    }

    [Fact]
    public void R_STORE_03_Task_Save_Writes_Under_Task_Id_And_Upserts()
    {
        var store = new InMemoryStore();
        var first = CoreTask.Bash("first");
        var replacement = new CoreTask(first.Id, TaskType.Bash, "replacement");

        store.Tasks.Save(first);
        store.Tasks.Save(replacement);

        Assert.Equal(1, store.Tasks.Count);
        Assert.Same(replacement, store.Tasks.Get(first.Id));
    }

    [Fact]
    public void R_STORE_04_Task_Set_Rejects_Id_Mismatch_Without_Mutation()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");
        store.Tasks.Save(task);

        Assert.Throws<ArgumentException>(() => store.Tasks.Set("other", task));

        Assert.Single(store.Tasks.Keys);
        Assert.Same(task, store.Tasks.Get(task.Id));
    }

    [Fact]
    public void R_STORE_05_Task_Set_Rejects_Non_Task_Without_Mutation()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");
        store.Tasks.Save(task);

        Assert.Throws<ArgumentException>(() => store.Tasks.Set(task.Id, "bad"));

        Assert.Single(store.Tasks.Keys);
        Assert.Same(task, store.Tasks.Get(task.Id));
    }

    [Fact]
    public void R_STORE_05_Task_Save_Rejects_Null()
    {
        var store = new InMemoryStore();

        Assert.Throws<ArgumentNullException>(() => store.Tasks.Save(null!));
    }

    [Fact]
    public void R_STORE_06_07_Missing_Task_Get_And_Indexer_Throw_KeyNotFound()
    {
        var store = new InMemoryStore();

        Assert.Throws<KeyNotFoundException>(() => store.Tasks.Get("missing"));
        Assert.Throws<KeyNotFoundException>(() => store.Tasks["missing"]);
    }

    [Fact]
    public void R_STORE_08_Task_Has_Accepts_Id_And_Object_And_Never_Throws_For_Other_Values()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");
        store.Tasks.Save(task);

        Assert.True(store.Tasks.Has(task.Id));
        Assert.True(store.Tasks.Has(task));
        Assert.False(store.Tasks.Has("missing"));
        Assert.False(store.Tasks.Has(123));
        Assert.False(store.Tasks.Has(new object()));
    }

    [Fact]
    public void R_STORE_09_Task_Delete_Removes_Record_And_Missing_Delete_Throws()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");
        store.Tasks.Save(task);

        store.Tasks.Delete(task.Id);

        Assert.False(store.Tasks.Has(task.Id));
        Assert.Throws<KeyNotFoundException>(() => store.Tasks.Delete(task.Id));
    }

    [Fact]
    public void R_STORE_10_Task_Keys_Are_Insertion_Ordered_And_Count_Matches()
    {
        var store = new InMemoryStore();
        var first = CoreTask.Bash("first");
        var second = CoreTask.Bash("second");
        var third = CoreTask.Bash("third");

        store.Tasks.Save(first);
        store.Tasks.Save(second);
        store.Tasks.Save(third);

        Assert.Equal(new[] { first.Id, second.Id, third.Id }, store.Tasks.Keys.ToArray());
        Assert.Equal(store.Tasks.Count, store.Tasks.Keys.Count());
    }

    [Fact]
    public void R_STORE_11_Task_Reads_Return_Live_References()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");
        store.Tasks.Save(task);

        task.Title = "mutated";
        var loaded = store.Tasks.Get(task.Id);

        Assert.Same(task, loaded);
        Assert.Equal("mutated", loaded.Title);
    }

    [Fact]
    public void R_STORE_12_Task_Cancel_Helper_Cancels_Held_Task_In_Place()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");
        store.Tasks.Save(task);

        store.Tasks.Cancel(task.Id);

        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.Same(task, store.Tasks.Get(task.Id));
    }
}
