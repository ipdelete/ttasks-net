using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;

public class Phase4GraphCollectionConformanceTests
{
    [Fact]
    public void R_STORE_02_Graph_Collection_Satisfies_Structural_Shape()
    {
        var collection = new InMemoryStore().Graphs;

        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Save)));
        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Get)));
        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Set)));
        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Delete)));
        Assert.NotNull(collection.GetType().GetMethod(nameof(collection.Has)));
        Assert.NotNull(collection.GetType().GetProperty("Item"));
    }

    [Fact]
    public void R_STORE_03_Graph_Save_Writes_Under_Graph_Id_And_Upserts()
    {
        var store = new InMemoryStore();
        var first = new TaskGraph("first");
        var replacement = new TaskGraph("replacement");
        typeof(TaskGraph).GetField("<Id>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(replacement, first.Id);

        store.Graphs.Save(first);
        store.Graphs.Save(replacement);

        Assert.Equal(1, store.Graphs.Count);
        Assert.Same(replacement, store.Graphs.Get(first.Id));
    }

    [Fact]
    public void R_STORE_03_Graph_Save_Also_Saves_Member_Tasks()
    {
        var store = new InMemoryStore();
        var root = CoreTask.Bash("root");
        var leaf = CoreTask.Bash("leaf");
        var graph = new TaskGraph("g");
        graph.Add(root);
        graph.Add(leaf, after: new[] { root });

        store.Graphs.Save(graph);

        Assert.Same(root, store.Tasks.Get(root.Id));
        Assert.Same(leaf, store.Tasks.Get(leaf.Id));
    }

    [Fact]
    public void R_STORE_04_Graph_Set_Rejects_Id_Mismatch_Without_Mutation()
    {
        var store = new InMemoryStore();
        var graph = new TaskGraph("g");
        store.Graphs.Save(graph);

        Assert.Throws<ArgumentException>(() => store.Graphs.Set("other", graph));

        Assert.Single(store.Graphs.Keys);
        Assert.Same(graph, store.Graphs.Get(graph.Id));
    }

    [Fact]
    public void R_STORE_05_Graph_Set_Rejects_Non_Graph_Without_Mutation()
    {
        var store = new InMemoryStore();
        var graph = new TaskGraph("g");
        store.Graphs.Save(graph);

        Assert.Throws<ArgumentException>(() => store.Graphs.Set(graph.Id, "bad"));

        Assert.Single(store.Graphs.Keys);
        Assert.Same(graph, store.Graphs.Get(graph.Id));
    }

    [Fact]
    public void R_STORE_05_Graph_Save_Rejects_Null()
    {
        var store = new InMemoryStore();

        Assert.Throws<ArgumentNullException>(() => store.Graphs.Save(null!));
    }

    [Fact]
    public void R_STORE_06_07_Missing_Graph_Get_And_Indexer_Throw_KeyNotFound()
    {
        var store = new InMemoryStore();

        Assert.Throws<KeyNotFoundException>(() => store.Graphs.Get("missing"));
        Assert.Throws<KeyNotFoundException>(() => store.Graphs["missing"]);
    }

    [Fact]
    public void R_STORE_08_Graph_Has_Accepts_Id_And_Graph_And_Never_Throws_For_Other_Values()
    {
        var store = new InMemoryStore();
        var graph = new TaskGraph("g");
        store.Graphs.Save(graph);

        Assert.True(store.Graphs.Has(graph.Id));
        Assert.True(store.Graphs.Has(graph));
        Assert.False(store.Graphs.Has("missing"));
        Assert.False(store.Graphs.Has(123));
        Assert.False(store.Graphs.Has(new object()));
    }

    [Fact]
    public void R_STORE_09_Graph_Delete_Removes_Graph_But_Keeps_Member_Tasks()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");
        var graph = new TaskGraph("g");
        graph.Add(task);
        store.Graphs.Save(graph);

        store.Graphs.Delete(graph.Id);

        Assert.False(store.Graphs.Has(graph.Id));
        Assert.True(store.Tasks.Has(task.Id));
        Assert.Same(task, store.Tasks.Get(task.Id));
        Assert.Throws<KeyNotFoundException>(() => store.Graphs.Delete(graph.Id));
    }

    [Fact]
    public void R_STORE_10_Graph_Keys_Are_Insertion_Ordered_And_Count_Matches()
    {
        var store = new InMemoryStore();
        var first = new TaskGraph("first");
        var second = new TaskGraph("second");

        store.Graphs.Save(first);
        store.Graphs.Save(second);

        Assert.Equal(new[] { first.Id, second.Id }, store.Graphs.Keys.ToArray());
        Assert.Equal(store.Graphs.Count, store.Graphs.Keys.Count());
    }

    [Fact]
    public void R_STORE_11_Graph_Reads_Return_Live_References()
    {
        var store = new InMemoryStore();
        var graph = new TaskGraph("g");
        store.Graphs.Save(graph);

        graph.Title = "mutated";
        var loaded = store.Graphs.Get(graph.Id);

        Assert.Same(graph, loaded);
        Assert.Equal("mutated", loaded.Title);
    }
}
