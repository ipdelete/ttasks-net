using Ttasks.ChatApp.Services;
using Ttasks.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ChatAppOptions>(builder.Configuration.GetSection("ChatApp"));
builder.Services.AddSingleton<ILlmProvider, CopilotSdkProvider>();
builder.Services.AddSingleton<ITaskStore>(services =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChatAppOptions>>().Value;
    var environment = services.GetRequiredService<IHostEnvironment>();
    var path = ResolvePath(options.StorePath, environment.ContentRootPath);
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);
    return new SqliteStore(path);
});
builder.Services.AddSingleton<ITaskLibrary, StoreBackedTaskLibrary>();
builder.Services.AddSingleton<IGraphLibrary, StoreBackedGraphLibrary>();
builder.Services.AddSingleton<TaskLibraryTemplateRenderer>();
builder.Services.AddSingleton<ICapabilityProvider, ConfigCapabilityProvider>();
builder.Services.AddSingleton<GraphPlanValidator>();
builder.Services.AddSingleton<GraphPlanBuilder>();
builder.Services.AddSingleton<ChatSessionRegistry>();
builder.Services.AddSingleton<ChatTurnService>();
builder.Services.AddSingleton<AdminService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var library = scope.ServiceProvider.GetRequiredService<ITaskLibrary>();
    var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChatAppOptions>>().Value;
    TaskLibrarySeeder.Seed(library, options.LibrarySeed);
}

app.MapGet("/", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>ttasks chat experiment</title>
      <script src="https://cdn.tailwindcss.com"></script>
      <link rel="stylesheet" href="/admin.css" />
    </head>
    <body>
      <div class="app-shell">
        <aside class="sidebar">
          <div class="sidebar-brand">
            <div class="logo">tt</div>
            <div>
              <div class="name">ttasks</div>
              <div class="tag">control plane</div>
            </div>
          </div>
          <div class="sidebar-section">Workspace</div>
          <nav class="sidebar-nav">
            <a class="sidebar-link sidebar-link-active" href="/">Chat</a>
            <a class="sidebar-link" href="/admin">Dashboard</a>
            <a class="sidebar-link" href="/admin/turns">Turns</a>
            <a class="sidebar-link" href="/admin/capabilities">Capabilities</a>
            <a class="sidebar-link" href="/admin/library">Task library</a>
            <a class="sidebar-link" href="/admin/graph-library">Graph library</a>
          </nav>
          <div class="sidebar-footer">ttasks-net &middot; experimental</div>
        </aside>
        <div class="app-main">
          <header class="topbar">
            <div class="topbar-title">
              <h1>Chat</h1>
              <p>Plain questions are answered directly. Action requests route through a validated ttasks graph.</p>
            </div>
            <div class="topbar-actions">
              <button id="clear" class="btn btn-ghost" type="button">Clear</button>
            </div>
          </header>
          <main class="content">
            <div class="chat-shell">
              <div id="log" class="chat-log"></div>
              <form id="form" class="chat-form">
                <input id="message" class="chat-input" autocomplete="off"
                  placeholder="Ask a question, or ask to read 48:notes and summarize..." />
                <button class="btn btn-primary" type="submit">Send</button>
              </form>
            </div>
          </main>
        </div>
      </div>
      <script src="/admin.js"></script>
      <script>
        const log = document.getElementById('log');
        const form = document.getElementById('form');
        const input = document.getElementById('message');
        const sessionId = crypto.randomUUID();

        function add(role, text) {
          const div = document.createElement('div');
          div.className = `chat-msg ${role}`;
          div.innerHTML = `<div class="role">${role}</div>` + tt.escapeHtml(text);
          log.appendChild(div);
          div.scrollIntoView({ behavior: 'smooth', block: 'end' });
        }

        document.getElementById('clear').addEventListener('click', () => { log.innerHTML = ''; });

        form.addEventListener('submit', async event => {
          event.preventDefault();
          const message = input.value.trim();
          if (!message) return;
          input.value = '';
          add('user', message);
          try {
            const response = await fetch('/api/chat', {
              method: 'POST',
              headers: { 'content-type': 'application/json' },
              body: JSON.stringify({ sessionId, message })
            });
            const text = await response.text();
            const body = text ? JSON.parse(text) : {};
            if (!response.ok) {
              add('assistant', body.error ?? `Request failed with HTTP ${response.status}.`);
              return;
            }
            add('assistant', body.answer ?? body.error ?? JSON.stringify(body));
          } catch (error) {
            add('assistant', `Request failed: ${error.message}`);
          }
        });
      </script>
    </body>
    </html>
    """,
    "text/html"));

app.MapPost("/api/chat", (ChatRequest request, ChatTurnService chat) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "Message is required." });

    var response = chat.Handle(request.SessionId, request.Message);
    return Results.Ok(response);
});

app.MapGet("/admin", () => Results.Content(AdminPage(), "text/html"));
app.MapGet("/admin/capabilities", () => Results.Content(CapabilitiesPage(), "text/html"));
app.MapGet("/admin/library", () => Results.Content(TaskLibraryPage(), "text/html"));
app.MapGet("/admin/graph-library", () => Results.Content(GraphLibraryPage(), "text/html"));
app.MapGet("/admin/turns", () => Results.Content(TurnsPage(), "text/html"));

app.MapGet("/api/admin/graphs", (AdminService admin, int? limit) =>
{
    var cappedLimit = Math.Clamp(limit ?? 50, 1, 200);
    return Results.Ok(admin.RecentGraphs(cappedLimit));
});

app.MapGet("/api/admin/library", (AdminService admin) => Results.Ok(admin.TaskLibrary()));
app.MapGet("/api/admin/graph-library", (AdminService admin) => Results.Ok(admin.GraphLibrary()));

app.MapDelete("/api/admin/graph-library/{key}", (string key, AdminService admin) =>
{
    return admin.RemoveGraphLibraryItem(key) ? Results.NoContent() : Results.NotFound(new { error = $"Graph library item '{key}' was not found." });
});

app.MapGet("/api/admin/capabilities", (AdminService admin) => Results.Ok(admin.AllowedTools()));

app.MapGet("/api/admin/turns", (AdminService admin, int? limit) =>
{
    var cappedLimit = Math.Clamp(limit ?? 50, 1, 200);
    return Results.Ok(admin.RecentTurns(cappedLimit));
});

app.MapGet("/api/admin/turns/{id}", (string id, AdminService admin) =>
{
    try
    {
        return Results.Ok(admin.GetTurn(id));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Turn '{id}' was not found." });
    }
});

app.MapGet("/api/admin/graphs/{id}", (string id, AdminService admin) =>
{
    try
    {
        return Results.Ok(admin.GetGraph(id));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Graph '{id}' was not found." });
    }
});

app.MapGet("/api/admin/tasks/{id}", (string id, AdminService admin) =>
{
    try
    {
        return Results.Ok(admin.GetTask(id));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Task '{id}' was not found." });
    }
});

app.Run();

static string ResolvePath(string configuredPath, string contentRootPath)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
        throw new InvalidOperationException("ChatApp:StorePath must be configured.");

    return Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.GetFullPath(configuredPath, contentRootPath);
}

static string Sidebar(string active)
{
    string Link(string href, string key, string label)
    {
        var cls = key == active ? "sidebar-link sidebar-link-active" : "sidebar-link";
        return $$"""<a class="{{cls}}" href="{{href}}">{{label}}</a>""";
    }

    return $$"""
    <aside class="sidebar">
      <div class="sidebar-brand">
        <div class="logo">tt</div>
        <div>
          <div class="name">ttasks</div>
          <div class="tag">control plane</div>
        </div>
      </div>
      <div class="sidebar-section">Workspace</div>
      <nav class="sidebar-nav">
        {{Link("/", "chat", "Chat")}}
        {{Link("/admin", "dashboard", "Dashboard")}}
        {{Link("/admin/turns", "turns", "Turns")}}
        {{Link("/admin/capabilities", "capabilities", "Capabilities")}}
        {{Link("/admin/library", "library", "Task library")}}
        {{Link("/admin/graph-library", "graph-library", "Graph library")}}
      </nav>
      <div class="sidebar-footer">ttasks-net &middot; experimental</div>
    </aside>
    """;
}

static string AdminLayout(string title, string subtitle, string active, string body, string pageScript) =>
    $$"""
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>ttasks · {{title}}</title>
      <script src="https://cdn.tailwindcss.com"></script>
      <link rel="stylesheet" href="/admin.css" />
    </head>
    <body>
      <div class="app-shell">
        {{Sidebar(active)}}
        <div class="app-main">
          <header class="topbar">
            <div class="topbar-title">
              <h1>{{title}}</h1>
              <p>{{subtitle}}</p>
            </div>
            <div class="topbar-actions">
              <button id="refresh" class="btn btn-primary" type="button">Refresh</button>
            </div>
          </header>
          <main class="content">
            {{body}}
          </main>
        </div>
      </div>
      <script src="/admin.js"></script>
      <script>
      {{pageScript}}
      </script>
    </body>
    </html>
    """;

static string AdminPage()
{
    var body = """
    <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-5">
      <div class="card"><div class="row"><div><div class="kpi-label">Total graphs</div><div id="kpi-total" class="kpi-value">—</div><div class="kpi-sub">persisted in store</div></div><div class="kpi-accent kpi-accent-indigo">G</div></div></div>
      <div class="card"><div class="row"><div><div class="kpi-label">Succeeded</div><div id="kpi-ok" class="kpi-value">—</div><div class="kpi-sub">all tasks ok</div></div><div class="kpi-accent kpi-accent-emerald">✓</div></div></div>
      <div class="card"><div class="row"><div><div class="kpi-label">Failed / cancelled</div><div id="kpi-fail" class="kpi-value">—</div><div class="kpi-sub">needs attention</div></div><div class="kpi-accent kpi-accent-rose">!</div></div></div>
      <div class="card"><div class="row"><div><div class="kpi-label">Running / pending</div><div id="kpi-run" class="kpi-value">—</div><div class="kpi-sub">in flight</div></div><div class="kpi-accent kpi-accent-amber">↻</div></div></div>
    </div>
    <div class="grid gap-4" style="grid-template-columns: 280px 320px minmax(480px, 1fr);">
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem - 130px); min-height: 480px;">
        <div class="panel-header"><h2 class="panel-title">Recent graphs</h2><span id="graphs-count" class="muted"></span></div>
        <div class="panel-body"><div id="graphs" class="flex flex-col gap-2"></div></div>
      </div>
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem - 130px); min-height: 480px;">
        <div class="panel-header"><div><h2 class="panel-title" id="graph-title">Graph detail</h2><p class="panel-subtitle" id="graph-meta">Select a graph from the list.</p></div></div>
        <div class="panel-body"><div id="graph"></div></div>
      </div>
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem - 130px); min-height: 480px;">
        <div class="panel-header"><h2 class="panel-title">Task inspector</h2></div>
        <div class="panel-body"><div id="task" class="muted">Select a task to inspect.</div></div>
      </div>
    </div>
    """;

    var script = """
    const graphsEl = document.getElementById('graphs');
    const graphEl = document.getElementById('graph');
    const graphTitleEl = document.getElementById('graph-title');
    const graphMetaEl = document.getElementById('graph-meta');
    const graphsCountEl = document.getElementById('graphs-count');
    const taskEl = document.getElementById('task');
    const kpi = {
      total: document.getElementById('kpi-total'),
      ok: document.getElementById('kpi-ok'),
      fail: document.getElementById('kpi-fail'),
      run: document.getElementById('kpi-run'),
    };
    let selectedGraphId = null;

    document.getElementById('refresh').addEventListener('click', loadGraphs);

    function labelTask(task) { return task.title || `${task.type} ${tt.shortId(task.id)}`; }

    function updateKpis(graphs) {
      kpi.total.textContent = graphs.length;
      kpi.ok.textContent = graphs.filter(g => g.status === 'Succeeded').length;
      kpi.fail.textContent = graphs.filter(g => ['Failed','Cancelled','Blocked'].includes(g.status)).length;
      kpi.run.textContent = graphs.filter(g => ['Running','Pending'].includes(g.status)).length;
    }

    function highlightGraphs() {
      for (const card of graphsEl.children) {
        if (!card.dataset || !card.dataset.graphId) continue;
        card.classList.toggle('card-selected', card.dataset.graphId === selectedGraphId);
      }
    }

    async function loadGraphs() {
      graphsEl.innerHTML = '<div class="muted">Loading…</div>';
      const graphs = await fetch('/api/admin/graphs?limit=100').then(r => r.json());
      updateKpis(graphs);
      graphsCountEl.textContent = graphs.length ? `${graphs.length} total` : '';
      graphsEl.innerHTML = '';
      if (graphs.length === 0) {
        graphsEl.innerHTML = '<div class="muted">No graphs persisted yet.</div>';
        return;
      }
      for (const graph of graphs) {
        const card = document.createElement('div');
        card.className = 'card card-hover';
        card.dataset.graphId = graph.id;
        const bad = graph.failed + graph.cancelled + graph.blocked;
        card.innerHTML = `
          <div class="row"><strong>${tt.escapeHtml(graph.title || tt.shortId(graph.id, 12))}</strong>${tt.pill(graph.status)}</div>
          <div class="muted mt-1">${tt.fmtTime(graph.createdAt)}</div>
          <div class="muted">${graph.taskCount} tasks · ${graph.succeeded} ok · ${bad} bad</div>`;
        card.addEventListener('click', () => loadGraph(graph.id));
        graphsEl.appendChild(card);
      }
      if (!selectedGraphId) await loadGraph(graphs[0].id);
      else highlightGraphs();
    }

    async function loadGraph(id) {
      selectedGraphId = id;
      highlightGraphs();
      const graph = await fetch(`/api/admin/graphs/${encodeURIComponent(id)}`).then(r => r.json());
      graphTitleEl.textContent = graph.title || graph.id;
      graphMetaEl.textContent = `${graph.id} · ${tt.fmtTime(graph.createdAt)} · ${graph.status}`;
      renderGraph(graph);
      taskEl.innerHTML = '<div class="muted">Select a task to inspect.</div>';
    }

    function renderGraph(graph) {
      graphEl.innerHTML = '';
      const incoming = new Map(graph.tasks.map(t => [t.id, []]));
      for (const e of graph.edges) incoming.get(e.to)?.push(e.from);
      const taskById = new Map(graph.tasks.map(t => [t.id, t]));
      for (const task of graph.tasks) {
        const parents = incoming.get(task.id) || [];
        if (parents.length > 0) {
          const edge = document.createElement('div');
          edge.className = 'edge';
          edge.textContent = `depends on ${parents.map(id => labelTask(taskById.get(id))).join(', ')}`;
          graphEl.appendChild(edge);
        }
        const node = document.createElement('div');
        node.className = `node ${task.status}`;
        node.innerHTML = `
          <div class="row"><strong>${tt.escapeHtml(labelTask(task))}</strong>${tt.pill(task.status)}</div>
          <div class="muted">${tt.escapeHtml(task.type)} · <code class="inline">${tt.escapeHtml(tt.shortId(task.id, 12))}</code></div>
          ${task.error ? `<div class="muted">error: ${tt.escapeHtml(task.error)}</div>` : ''}`;
        node.addEventListener('click', () => loadTask(task.id));
        graphEl.appendChild(node);
      }
    }

    async function loadTask(id) {
      const task = await fetch(`/api/admin/tasks/${encodeURIComponent(id)}`).then(r => r.json());
      taskEl.innerHTML = `
        <div class="row mb-2"><strong>${tt.escapeHtml(task.title || task.id)}</strong>${tt.pill(task.status)}</div>
        <dl class="props">
          <dt>id</dt><dd><code class="inline">${tt.escapeHtml(task.id)}</code></dd>
          <dt>type</dt><dd>${tt.escapeHtml(task.type)}</dd>
          <dt>created</dt><dd>${tt.fmtTime(task.createdAt)}</dd>
          <dt>timeout</dt><dd>${task.timeout ?? '—'}</dd>
          <dt>blocked by</dt><dd>${tt.escapeHtml(task.blockedBy || '—')}</dd>
          <dt>error</dt><dd>${tt.escapeHtml(task.error || task.result?.error || '—')}</dd>
        </dl>
        <div class="detail-section-title">Metadata</div>
        <pre class="code">${tt.escapeHtml(JSON.stringify(task.metadata || {}, null, 2))}</pre>
        <div class="detail-section-title">Payload</div>
        <pre class="code">${tt.escapeHtml(task.payload)}</pre>
        <div class="detail-section-title">Output</div>
        <pre class="code">${tt.escapeHtml(task.result?.output || '')}</pre>`;
    }

    loadGraphs();
    """;

    return AdminLayout("Dashboard", "Graph runs and tasks persisted across chat turns.", "dashboard", body, script);
}

static string CapabilitiesPage()
{
    var body = """
    <div id="capabilities" class="grid grid-cols-1 lg:grid-cols-2 gap-4">
      <div class="muted">Loading…</div>
    </div>
    """;

    var script = """
    const capabilitiesEl = document.getElementById('capabilities');
    document.getElementById('refresh').addEventListener('click', loadCapabilities);

    async function loadCapabilities() {
      capabilitiesEl.innerHTML = '<div class="muted">Loading…</div>';
      const capabilities = await fetch('/api/admin/capabilities').then(r => r.json());
      capabilitiesEl.innerHTML = '';
      if (capabilities.length === 0) {
        capabilitiesEl.innerHTML = '<div class="muted">No capabilities registered.</div>';
        return;
      }
      for (const capability of capabilities) {
        const card = document.createElement('div');
        card.className = 'card';
        card.innerHTML = `
          <div class="row"><h2 class="panel-title"><code class="inline">${tt.escapeHtml(capability.prefix)}</code></h2><span class="pill pill-muted">prefix</span></div>
          <p class="mt-2 text-sm text-slate-700">${tt.escapeHtml(capability.description || '(no description)')}</p>
          ${capability.helpCommand ? `<p class="muted mt-2"><strong>Help:</strong> <code class="inline">${tt.escapeHtml(capability.helpCommand)}</code></p>` : ''}`;
        capabilitiesEl.appendChild(card);
      }
    }
    loadCapabilities();
    """;

    return AdminLayout("Capabilities", "Host-approved actions the chat planner can use.", "capabilities", body, script);
}

static string TaskLibraryPage()
{
    var body = """
    <div class="grid gap-4" style="grid-template-columns: 360px minmax(360px, 1fr);">
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem); min-height: 480px;">
        <div class="panel-header"><h2 class="panel-title">Library items</h2></div>
        <div class="panel-body"><div id="library" class="flex flex-col gap-2"><div class="muted">Loading…</div></div></div>
      </div>
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem); min-height: 480px;">
        <div class="panel-header"><h2 class="panel-title">Library item detail</h2></div>
        <div class="panel-body"><div id="detail" class="muted">Select a library item.</div></div>
      </div>
    </div>
    """;

    var script = """
    const libraryEl = document.getElementById('library');
    const detailEl = document.getElementById('detail');
    let items = [];
    let selectedKey = null;
    document.getElementById('refresh').addEventListener('click', loadLibrary);

    function highlight() {
      for (const card of libraryEl.children) {
        if (!card.dataset || !card.dataset.key) continue;
        card.classList.toggle('card-selected', card.dataset.key === selectedKey);
      }
    }

    async function loadLibrary() {
      libraryEl.innerHTML = '<div class="muted">Loading…</div>';
      items = await fetch('/api/admin/library').then(r => r.json());
      libraryEl.innerHTML = '';
      if (items.length === 0) {
        libraryEl.innerHTML = '<div class="muted">No reusable tasks yet.</div>';
        detailEl.innerHTML = '<div class="muted">No library items are available.</div>';
        return;
      }
      for (const item of items) {
        const card = document.createElement('div');
        card.className = 'card card-hover';
        card.dataset.key = item.key;
        card.innerHTML = `
          <div class="row"><strong>${tt.escapeHtml(item.displayName || item.key)}</strong></div>
          <div class="muted mt-1"><code class="inline">${tt.escapeHtml(item.key)}</code></div>
          <div class="muted">${tt.fmtTime(item.createdAt)}</div>`;
        card.addEventListener('click', () => selectItem(item.key));
        libraryEl.appendChild(card);
      }
      if (!selectedKey) selectItem(items[0].key);
      else highlight();
    }

    function selectItem(key) {
      selectedKey = key;
      const item = items.find(c => c.key === key);
      if (!item) return;
      highlight();
      detailEl.innerHTML = `
        <div class="row mb-2"><strong>${tt.escapeHtml(item.displayName || item.key)}</strong></div>
        <dl class="props">
          <dt>id</dt><dd><code class="inline">${tt.escapeHtml(item.id)}</code></dd>
          <dt>key</dt><dd><code class="inline">${tt.escapeHtml(item.key)}</code></dd>
          <dt>fileName</dt><dd><code class="inline">${tt.escapeHtml(item.fileName)}</code></dd>
          <dt>created</dt><dd>${tt.fmtTime(item.createdAt)}</dd>
          <dt>description</dt><dd>${tt.escapeHtml(item.description)}</dd>
        </dl>
        <div class="detail-section-title">Args template</div>
        <pre class="code">${tt.escapeHtml(JSON.stringify(item.argsTemplate || [], null, 2))}</pre>
        <div class="detail-section-title">Metadata</div>
        <pre class="code">${tt.escapeHtml(JSON.stringify(item.metadata || {}, null, 2))}</pre>`;
    }

    loadLibrary();
    """;

    return AdminLayout("Task library", "Saved reusable capability task templates.", "library", body, script);
}

static string TurnsPage()
{
    var body = """
    <div class="grid gap-4" style="grid-template-columns: 300px 340px minmax(480px, 1fr);">
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem); min-height: 480px;">
        <div class="panel-header"><h2 class="panel-title">Recent turns</h2></div>
        <div class="panel-body"><div id="turns" class="flex flex-col gap-2"><div class="muted">Loading…</div></div></div>
      </div>
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem); min-height: 480px;">
        <div class="panel-header"><div><h2 class="panel-title" id="turn-title">Turn detail</h2><p class="panel-subtitle" id="turn-meta">Select a turn from the list.</p></div></div>
        <div class="panel-body"><div id="turn-tasks"></div></div>
      </div>
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem); min-height: 480px;">
        <div class="panel-header"><h2 class="panel-title">Task inspector</h2></div>
        <div class="panel-body"><div id="task" class="muted">Select a task to inspect.</div></div>
      </div>
    </div>
    """;

    var script = """
    const turnsEl = document.getElementById('turns');
    const turnTasksEl = document.getElementById('turn-tasks');
    const turnTitleEl = document.getElementById('turn-title');
    const turnMetaEl = document.getElementById('turn-meta');
    const taskEl = document.getElementById('task');
    let selectedTurnId = null;

    document.getElementById('refresh').addEventListener('click', loadTurns);

    function highlight() {
      for (const card of turnsEl.children) {
        if (!card.dataset || !card.dataset.turnId) continue;
        card.classList.toggle('card-selected', card.dataset.turnId === selectedTurnId);
      }
    }

    async function loadTurns() {
      turnsEl.innerHTML = '<div class="muted">Loading…</div>';
      const turns = await fetch('/api/admin/turns?limit=100').then(r => r.json());
      turnsEl.innerHTML = '';
      if (turns.length === 0) {
        turnsEl.innerHTML = '<div class="muted">No turns persisted yet. Send a chat message to create one.</div>';
        return;
      }
      for (const turn of turns) {
        const card = document.createElement('div');
        card.className = 'card card-hover';
        card.dataset.turnId = turn.turnId;
        const counts = [];
        if (turn.routerCount) counts.push(`${turn.routerCount} router`);
        if (turn.plannerCount) counts.push(`${turn.plannerCount} planner`);
        if (turn.repairCount) counts.push(`${turn.repairCount} repair`);
        if (turn.processCount) counts.push(`${turn.processCount} process`);
        if (turn.summaryCount) counts.push(`${turn.summaryCount} summary`);
        card.innerHTML = `
          <div class="row"><strong><code class="inline">${tt.escapeHtml(tt.shortId(turn.turnId))}</code></strong>${tt.pill(turn.status)}</div>
          <div class="muted mt-1">${tt.fmtTime(turn.createdAt)}</div>
          <div class="muted">${turn.taskCount} tasks · ${counts.join(' · ') || 'none'}</div>
          <div class="muted">session ${tt.escapeHtml(tt.shortId(turn.sessionId, 16))}</div>`;
        card.addEventListener('click', () => loadTurn(turn.turnId));
        turnsEl.appendChild(card);
      }
      if (!selectedTurnId) await loadTurn(turns[0].turnId);
      else highlight();
    }

    async function loadTurn(turnId) {
      selectedTurnId = turnId;
      highlight();
      const turn = await fetch(`/api/admin/turns/${encodeURIComponent(turnId)}`).then(r => r.json());
      turnTitleEl.textContent = `Turn ${tt.shortId(turn.turnId)}`;
      turnMetaEl.textContent = `${turn.turnId} · session ${turn.sessionId || ''} · ${tt.fmtTime(turn.createdAt)} · ${turn.status}`;
      renderTurn(turn);
      taskEl.innerHTML = '<div class="muted">Select a task to inspect.</div>';
    }

    function renderTurn(turn) {
      turnTasksEl.innerHTML = '';
      for (const task of turn.tasks) {
        const node = document.createElement('div');
        node.className = `node ${task.status}`;
        const attempt = task.attempt ? ` (attempt ${task.attempt})` : '';
        node.innerHTML = `
          <div class="row">
            <strong>${tt.kindBadge(task.kind)}${tt.escapeHtml(task.title || tt.shortId(task.id))}${attempt}</strong>
            ${tt.pill(task.status)}
          </div>
          <div class="muted">${tt.escapeHtml(task.type)} · <code class="inline">${tt.escapeHtml(task.id)}</code> · ${tt.fmtTime(task.createdAt)}</div>
          ${task.error ? `<div class="muted">error: ${tt.escapeHtml(task.error)}</div>` : ''}`;
        node.addEventListener('click', () => loadTask(task.id));
        turnTasksEl.appendChild(node);
      }
    }

    async function loadTask(id) {
      const task = await fetch(`/api/admin/tasks/${encodeURIComponent(id)}`).then(r => r.json());
      taskEl.innerHTML = `
        <div class="row mb-2"><strong>${tt.escapeHtml(task.title || task.id)}</strong>${tt.pill(task.status)}</div>
        <dl class="props">
          <dt>id</dt><dd><code class="inline">${tt.escapeHtml(task.id)}</code></dd>
          <dt>type</dt><dd>${tt.escapeHtml(task.type)}</dd>
          <dt>created</dt><dd>${tt.fmtTime(task.createdAt)}</dd>
          <dt>timeout</dt><dd>${task.timeout ?? '—'}</dd>
          <dt>error</dt><dd>${tt.escapeHtml(task.error || task.result?.error || '—')}</dd>
        </dl>
        <div class="detail-section-title">Metadata</div>
        <pre class="code">${tt.escapeHtml(JSON.stringify(task.metadata || {}, null, 2))}</pre>
        <div class="detail-section-title">Payload</div>
        <pre class="code">${tt.escapeHtml(task.payload)}</pre>
        <div class="detail-section-title">Output</div>
        <pre class="code">${tt.escapeHtml(task.result?.output || '')}</pre>`;
    }

    loadTurns();
    """;

    return AdminLayout("Turns", "Per-turn reasoning and execution trail.", "turns", body, script);
}

static string GraphLibraryPage()
{
    var body = """
    <div class="grid gap-4" style="grid-template-columns: 360px minmax(360px, 1fr);">
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem); min-height: 480px;">
        <div class="panel-header"><h2 class="panel-title">Graph templates</h2></div>
        <div class="panel-body"><div id="items" class="flex flex-col gap-2"><div class="muted">Loading…</div></div></div>
      </div>
      <div class="panel" style="max-height: calc(100vh - 56px - 2.5rem); min-height: 480px;">
        <div class="panel-header"><h2 class="panel-title">Template detail</h2></div>
        <div class="panel-body"><div id="detail" class="muted">Select a template.</div></div>
      </div>
    </div>
    """;

    var script = """
    const itemsEl = document.getElementById('items');
    const detailEl = document.getElementById('detail');
    let items = [];
    let selectedKey = null;
    document.getElementById('refresh').addEventListener('click', loadItems);

    function highlight() {
      for (const card of itemsEl.children) {
        if (!card.dataset || !card.dataset.key) continue;
        card.classList.toggle('card-selected', card.dataset.key === selectedKey);
      }
    }

    async function loadItems() {
      itemsEl.innerHTML = '<div class="muted">Loading…</div>';
      items = await fetch('/api/admin/graph-library').then(r => r.json());
      itemsEl.innerHTML = '';
      if (items.length === 0) {
        itemsEl.innerHTML = '<div class="muted">No graph templates yet. The planner promotes graph templates after a successful authored graph that included a graphSuggestion.</div>';
        detailEl.innerHTML = '<div class="muted">No templates are available.</div>';
        return;
      }
      for (const item of items) {
        const card = document.createElement('div');
        card.className = 'card card-hover';
        card.dataset.key = item.key;
        card.innerHTML = `
          <div class="row"><strong>${tt.escapeHtml(item.displayName || item.key)}</strong></div>
          <div class="muted mt-1"><code class="inline">${tt.escapeHtml(item.key)}</code></div>
          <div class="muted">${item.planTemplate.tasks.length} tasks · ${tt.fmtTime(item.createdAt)}</div>`;
        card.addEventListener('click', () => selectItem(item.key));
        itemsEl.appendChild(card);
      }
      if (!selectedKey) selectItem(items[0].key);
      else highlight();
    }

    function selectItem(key) {
      selectedKey = key;
      const item = items.find(i => i.key === key);
      if (!item) return;
      highlight();
      const params = (item.parameters || []).map(p => `${tt.escapeHtml(p.name)} (${tt.escapeHtml(p.source)}${p.defaultValue !== null && p.defaultValue !== undefined ? ', default=' + tt.escapeHtml(String(p.defaultValue)) : ''})`).join(', ');
      detailEl.innerHTML = `
        <div class="row mb-2"><strong>${tt.escapeHtml(item.displayName || item.key)}</strong></div>
        <dl class="props">
          <dt>key</dt><dd><code class="inline">${tt.escapeHtml(item.key)}</code></dd>
          <dt>created</dt><dd>${tt.fmtTime(item.createdAt)}</dd>
          <dt>description</dt><dd>${tt.escapeHtml(item.description)}</dd>
          <dt>parameters</dt><dd>${params || '(none)'}</dd>
        </dl>
        <div class="detail-section-title">Plan template</div>
        <pre class="code">${tt.escapeHtml(JSON.stringify(item.planTemplate, null, 2))}</pre>`;
    }

    loadItems();
    """;

    return AdminLayout("Graph library", "Reusable multi-task workflow templates.", "graph-library", body, script);
}
