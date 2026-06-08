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
builder.Services.AddSingleton<TaskLibraryTemplateRenderer>();
builder.Services.AddSingleton<ICapabilityProvider, ConfigCapabilityProvider>();
builder.Services.AddSingleton<GraphPlanValidator>();
builder.Services.AddSingleton<GraphPlanBuilder>();
builder.Services.AddSingleton<ChatSessionRegistry>();
builder.Services.AddSingleton<ChatTurnService>();
builder.Services.AddSingleton<AdminService>();

var app = builder.Build();

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
      <style>
        body { font-family: system-ui, sans-serif; max-width: 900px; margin: 2rem auto; padding: 0 1rem; }
        #log { display: grid; gap: .75rem; margin-bottom: 1rem; }
        .msg { border: 1px solid #ddd; border-radius: .5rem; padding: .75rem; white-space: pre-wrap; }
        .user { background: #f6f8fa; }
        .assistant { background: #f0fff4; }
        form { display: flex; gap: .5rem; }
        input { flex: 1; padding: .75rem; }
        button { padding: .75rem 1rem; }
      </style>
    </head>
    <body>
      <h1>ttasks chat experiment</h1>
      <p>Plain questions are answered by one PROMPT task. Action requests are routed to a validated ttasks graph.</p>
      <div id="log"></div>
      <form id="form">
        <input id="message" autocomplete="off" placeholder="Ask a question, or ask to read 48:notes and summarize..." />
        <button>Send</button>
      </form>
      <script>
        const log = document.getElementById('log');
        const form = document.getElementById('form');
        const input = document.getElementById('message');
        const sessionId = crypto.randomUUID();
        function add(role, text) {
          const div = document.createElement('div');
          div.className = `msg ${role}`;
          div.textContent = `${role}: ${text}`;
          log.appendChild(div);
        }
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

app.MapGet("/api/admin/graphs", (AdminService admin, int? limit) =>
{
    var cappedLimit = Math.Clamp(limit ?? 50, 1, 200);
    return Results.Ok(admin.RecentGraphs(cappedLimit));
});

app.MapGet("/api/admin/library", (AdminService admin) => Results.Ok(admin.TaskLibrary()));

app.MapGet("/api/admin/capabilities", (AdminService admin) => Results.Ok(admin.AllowedTools()));

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

static string AdminPage() =>
    """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>ttasks admin</title>
      <style>
        :root { color-scheme: light dark; --border: #d0d7de; --muted: #57606a; --bg: #f6f8fa; --code-bg: #f6f8fa; --code-fg: #24292f; }
        @media (prefers-color-scheme: dark) {
          :root { --border: #8b949e; --muted: #8b949e; --bg: #161b22; --code-bg: #161b22; --code-fg: #e6edf3; }
        }
        body { font-family: system-ui, sans-serif; margin: 0; }
        header { border-bottom: 1px solid var(--border); padding: 1rem; display: flex; justify-content: space-between; align-items: center; }
        nav { display: flex; gap: .75rem; margin-top: .5rem; }
        nav a { color: inherit; }
        main { display: grid; grid-template-columns: 320px minmax(360px, 1fr) 420px; min-height: calc(100vh - 65px); }
        section { border-right: 1px solid var(--border); padding: 1rem; overflow: auto; }
        section:last-child { border-right: 0; }
        h1, h2, h3 { margin: 0 0 .75rem; }
        button { border: 1px solid var(--border); border-radius: .4rem; background: canvas; padding: .4rem .6rem; cursor: pointer; }
        .list { display: grid; gap: .5rem; }
        .card { border: 1px solid var(--border); border-radius: .5rem; padding: .7rem; background: canvas; cursor: pointer; }
        .card:hover, .card.selected { outline: 2px solid #0969da; }
        .muted { color: var(--muted); font-size: .85rem; }
        .row { display: flex; gap: .5rem; align-items: center; justify-content: space-between; }
        .badge { border-radius: 999px; padding: .15rem .45rem; font-size: .75rem; font-weight: 700; }
        .Succeeded { background: #dafbe1; color: #116329; }
        .Failed, .Cancelled, .Blocked { background: #ffebe9; color: #82071e; }
        .Running { background: #fff8c5; color: #7d4e00; }
        .Pending, .Empty { background: var(--bg); color: var(--muted); }
        .graph { display: grid; gap: .7rem; }
        .node { border: 1px solid var(--border); border-left-width: .45rem; border-radius: .5rem; padding: .65rem; cursor: pointer; }
        .node.Succeeded { border-left-color: #2da44e; background: canvas; color: inherit; }
        .node.Failed, .node.Cancelled, .node.Blocked { border-left-color: #cf222e; background: canvas; color: inherit; }
        .node.Pending, .node.Running { border-left-color: #bf8700; background: canvas; color: inherit; }
        .edge { margin: -.25rem 0 -.15rem 1.25rem; color: var(--muted); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
        pre { white-space: pre-wrap; overflow-wrap: anywhere; background: var(--code-bg); color: var(--code-fg); padding: .75rem; border-radius: .5rem; max-height: 45vh; overflow: auto; }
        dl { display: grid; grid-template-columns: 7rem 1fr; gap: .35rem .75rem; }
        dt { color: var(--muted); }
        dd { margin: 0; overflow-wrap: anywhere; }
      </style>
    </head>
    <body>
      <header>
        <div>
          <h1>ttasks admin</h1>
          <div class="muted">Persisted graph and task runs</div>
          <nav>
            <strong>Graphs and tasks</strong>
            <a href="/admin/capabilities">Capabilities</a>
            <a href="/admin/library">Task library</a>
          </nav>
        </div>
        <button id="refresh">Refresh</button>
      </header>
      <main>
        <section>
          <h2>Graphs</h2>
          <div id="graphs" class="list"></div>
        </section>
        <section>
          <h2 id="graph-title">Graph detail</h2>
          <div id="graph-meta" class="muted"></div>
          <div id="graph" class="graph"></div>
        </section>
        <section>
          <h2>Task inspector</h2>
          <div id="task">Select a task.</div>
        </section>
      </main>
      <script>
        const graphsEl = document.getElementById('graphs');
        const graphEl = document.getElementById('graph');
        const graphTitleEl = document.getElementById('graph-title');
        const graphMetaEl = document.getElementById('graph-meta');
        const taskEl = document.getElementById('task');
        let selectedGraphId = null;
        let selectedTaskId = null;

        document.getElementById('refresh').addEventListener('click', loadGraphs);

        function badge(status) {
          return `<span class="badge ${status}">${status}</span>`;
        }

        function labelTask(task) {
          return task.title || `${task.type} ${task.id.slice(0, 8)}`;
        }

        function fmtTime(value) {
          return new Date(value).toLocaleString();
        }

        async function loadGraphs() {
          graphsEl.textContent = 'Loading...';
          const graphs = await fetch('/api/admin/graphs?limit=100').then(r => r.json());
          graphsEl.innerHTML = '';
          if (graphs.length === 0) {
            graphsEl.textContent = 'No graphs persisted yet.';
            return;
          }
          for (const graph of graphs) {
            const card = document.createElement('div');
            card.className = `card ${graph.id === selectedGraphId ? 'selected' : ''}`;
            card.innerHTML = `
              <div class="row"><strong>${escapeHtml(graph.title || graph.id)}</strong>${badge(graph.status)}</div>
              <div class="muted">${fmtTime(graph.createdAt)}</div>
              <div class="muted">${graph.taskCount} tasks: ${graph.succeeded} ok, ${graph.failed + graph.cancelled + graph.blocked} bad</div>`;
            card.addEventListener('click', () => loadGraph(graph.id));
            graphsEl.appendChild(card);
          }
          if (!selectedGraphId) {
            await loadGraph(graphs[0].id);
          }
        }

        async function loadGraph(id) {
          selectedGraphId = id;
          selectedTaskId = null;
          const graph = await fetch(`/api/admin/graphs/${encodeURIComponent(id)}`).then(r => r.json());
          graphTitleEl.textContent = graph.title || graph.id;
          graphMetaEl.textContent = `${graph.id} - ${fmtTime(graph.createdAt)} - ${graph.status}`;
          renderGraph(graph);
          taskEl.textContent = 'Select a task.';
          await loadGraphsListOnly();
        }

        async function loadGraphsListOnly() {
          const graphs = await fetch('/api/admin/graphs?limit=100').then(r => r.json());
          for (const [i, card] of [...graphsEl.children].entries()) {
            card.classList.toggle('selected', graphs[i]?.id === selectedGraphId);
          }
        }

        function renderGraph(graph) {
          graphEl.innerHTML = '';
          const incoming = new Map(graph.tasks.map(task => [task.id, []]));
          for (const edge of graph.edges) incoming.get(edge.to)?.push(edge.from);
          const taskById = new Map(graph.tasks.map(task => [task.id, task]));

          for (const task of graph.tasks) {
            const parents = incoming.get(task.id) || [];
            if (parents.length > 0) {
              const edge = document.createElement('div');
              edge.className = 'edge';
              edge.textContent = `depends on ${parents.map(id => labelTask(taskById.get(id))).join(', ')}`;
              graphEl.appendChild(edge);
            }
            const node = document.createElement('div');
            node.className = `node ${task.status} ${task.id === selectedTaskId ? 'selected' : ''}`;
            node.innerHTML = `
              <div class="row"><strong>${escapeHtml(labelTask(task))}</strong>${badge(task.status)}</div>
              <div class="muted">${task.type} - ${task.id}</div>
              ${task.error ? `<div class="muted">error: ${escapeHtml(task.error)}</div>` : ''}`;
            node.addEventListener('click', () => loadTask(task.id));
            graphEl.appendChild(node);
          }
        }

        async function loadTask(id) {
          selectedTaskId = id;
          const task = await fetch(`/api/admin/tasks/${encodeURIComponent(id)}`).then(r => r.json());
          taskEl.innerHTML = `
            <h3>${escapeHtml(task.title || task.id)}</h3>
            <dl>
              <dt>id</dt><dd>${escapeHtml(task.id)}</dd>
              <dt>type</dt><dd>${escapeHtml(task.type)}</dd>
              <dt>status</dt><dd>${badge(task.status)}</dd>
              <dt>created</dt><dd>${fmtTime(task.createdAt)}</dd>
              <dt>timeout</dt><dd>${task.timeout ?? ''}</dd>
              <dt>blocked by</dt><dd>${escapeHtml(task.blockedBy || '')}</dd>
              <dt>error</dt><dd>${escapeHtml(task.error || task.result?.error || '')}</dd>
            </dl>
            <h3>Metadata</h3>
            <pre>${escapeHtml(JSON.stringify(task.metadata || {}, null, 2))}</pre>
            <h3>Payload</h3>
            <pre>${escapeHtml(task.payload)}</pre>
            <h3>Output</h3>
            <pre>${escapeHtml(task.result?.output || '')}</pre>`;
        }

        function escapeHtml(value) {
          return String(value ?? '').replace(/[&<>"']/g, ch => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
          }[ch]));
        }

        loadGraphs();
      </script>
    </body>
    </html>
    """;

static string CapabilitiesPage() =>
    """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>ttasks capabilities</title>
      <style>
        :root { color-scheme: light dark; --border: #d0d7de; --muted: #57606a; --bg: #f6f8fa; --code-bg: #f6f8fa; --code-fg: #24292f; }
        @media (prefers-color-scheme: dark) {
          :root { --border: #8b949e; --muted: #8b949e; --bg: #161b22; --code-bg: #161b22; --code-fg: #e6edf3; }
        }
        body { font-family: system-ui, sans-serif; margin: 0; }
        header { border-bottom: 1px solid var(--border); padding: 1rem; display: flex; justify-content: space-between; align-items: center; }
        nav { display: flex; gap: .75rem; margin-top: .5rem; }
        nav a { color: inherit; }
        main { padding: 1rem; }
        h1, h2, h3 { margin: 0 0 .75rem; }
        button { border: 1px solid var(--border); border-radius: .4rem; background: canvas; padding: .4rem .6rem; cursor: pointer; }
        .list { display: grid; gap: .75rem; max-width: 1100px; }
        .card { border: 1px solid var(--border); border-radius: .5rem; padding: .8rem; background: canvas; }
        .muted { color: var(--muted); font-size: .85rem; }
        .row { display: flex; gap: .5rem; align-items: center; justify-content: space-between; }
        .badge { border-radius: 999px; padding: .15rem .45rem; font-size: .75rem; font-weight: 700; background: var(--bg); color: var(--muted); }
        code { background: var(--code-bg); color: var(--code-fg); border-radius: .25rem; padding: .1rem .25rem; }
        ul { margin-bottom: 0; }
      </style>
    </head>
    <body>
      <header>
        <div>
          <h1>ttasks capabilities</h1>
          <div class="muted">Host-approved actions the chat planner can use</div>
          <nav>
            <a href="/admin">Graphs and tasks</a>
            <strong>Capabilities</strong>
            <a href="/admin/library">Task library</a>
          </nav>
        </div>
        <button id="refresh">Refresh</button>
      </header>
      <main>
        <div id="capabilities" class="list">Loading...</div>
      </main>
      <script>
        const capabilitiesEl = document.getElementById('capabilities');
        document.getElementById('refresh').addEventListener('click', loadCapabilities);

        async function loadCapabilities() {
          capabilitiesEl.textContent = 'Loading...';
          const capabilities = await fetch('/api/admin/capabilities').then(r => r.json());
          capabilitiesEl.innerHTML = '';
          if (capabilities.length === 0) {
            capabilitiesEl.textContent = 'No capabilities registered.';
            return;
          }
          for (const capability of capabilities) {
            const card = document.createElement('div');
            card.className = 'card';
            card.innerHTML = `
              <div class="row"><h2><code>${escapeHtml(capability.prefix)}</code></h2></div>
              <p>${escapeHtml(capability.description || '(no description)')}</p>
              ${capability.helpCommand ? `<p><strong>Help:</strong> <code>${escapeHtml(capability.helpCommand)}</code></p>` : ''}`;
            capabilitiesEl.appendChild(card);
          }
        }

        function escapeHtml(value) {
          return String(value ?? '').replace(/[&<>"']/g, ch => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
          }[ch]));
        }

        loadCapabilities();
      </script>
    </body>
    </html>
    """;

static string TaskLibraryPage() =>
    """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>ttasks task library</title>
      <style>
        :root { color-scheme: light dark; --border: #d0d7de; --muted: #57606a; --bg: #f6f8fa; --code-bg: #f6f8fa; --code-fg: #24292f; }
        @media (prefers-color-scheme: dark) {
          :root { --border: #8b949e; --muted: #8b949e; --bg: #161b22; --code-bg: #161b22; --code-fg: #e6edf3; }
        }
        body { font-family: system-ui, sans-serif; margin: 0; }
        header { border-bottom: 1px solid var(--border); padding: 1rem; display: flex; justify-content: space-between; align-items: center; }
        nav { display: flex; gap: .75rem; margin-top: .5rem; }
        nav a { color: inherit; }
        main { display: grid; grid-template-columns: 360px minmax(360px, 1fr); min-height: calc(100vh - 65px); }
        section { border-right: 1px solid var(--border); padding: 1rem; overflow: auto; }
        section:last-child { border-right: 0; }
        h1, h2, h3 { margin: 0 0 .75rem; }
        button { border: 1px solid var(--border); border-radius: .4rem; background: canvas; padding: .4rem .6rem; cursor: pointer; }
        .list { display: grid; gap: .5rem; }
        .card { border: 1px solid var(--border); border-radius: .5rem; padding: .7rem; background: canvas; cursor: pointer; }
        .card:hover, .card.selected { outline: 2px solid #0969da; }
        .muted { color: var(--muted); font-size: .85rem; }
        .row { display: flex; gap: .5rem; align-items: center; justify-content: space-between; }
        .badge { border-radius: 999px; padding: .15rem .45rem; font-size: .75rem; font-weight: 700; background: var(--bg); color: var(--muted); }
        pre { white-space: pre-wrap; overflow-wrap: anywhere; background: var(--code-bg); color: var(--code-fg); padding: .75rem; border-radius: .5rem; max-height: 45vh; overflow: auto; }
        dl { display: grid; grid-template-columns: 8rem 1fr; gap: .35rem .75rem; }
        dt { color: var(--muted); }
        dd { margin: 0; overflow-wrap: anywhere; }
      </style>
    </head>
    <body>
      <header>
        <div>
          <h1>ttasks task library</h1>
          <div class="muted">Saved reusable capability task templates</div>
          <nav>
            <a href="/admin">Graphs and tasks</a>
            <a href="/admin/capabilities">Capabilities</a>
            <strong>Task library</strong>
          </nav>
        </div>
        <button id="refresh">Refresh</button>
      </header>
      <main>
        <section>
          <h2>Library items</h2>
          <div id="library" class="list">Loading...</div>
        </section>
        <section>
          <h2>Library item detail</h2>
          <div id="detail">Select a library item.</div>
        </section>
      </main>
      <script>
        const libraryEl = document.getElementById('library');
        const detailEl = document.getElementById('detail');
        let items = [];
        let selectedKey = null;
        document.getElementById('refresh').addEventListener('click', loadLibrary);

        async function loadLibrary() {
          libraryEl.textContent = 'Loading...';
          items = await fetch('/api/admin/library').then(r => r.json());
          libraryEl.innerHTML = '';
          if (items.length === 0) {
            libraryEl.textContent = 'No reusable tasks yet.';
            detailEl.textContent = 'No library items are available.';
            return;
          }
          for (const item of items) {
            const card = document.createElement('div');
            card.className = `card ${item.key === selectedKey ? 'selected' : ''}`;
            card.innerHTML = `
              <div class="row"><strong>${escapeHtml(item.displayName || item.key)}</strong></div>
              <div class="muted">${escapeHtml(item.key)}</div>
              <div class="muted">${fmtTime(item.createdAt)}</div>`;
            card.addEventListener('click', () => selectItem(item.key));
            libraryEl.appendChild(card);
          }
          if (!selectedKey) {
            selectItem(items[0].key);
          }
        }

        function selectItem(key) {
          selectedKey = key;
          const item = items.find(candidate => candidate.key === key);
          if (!item) return;
          for (const card of libraryEl.children) {
            card.classList.toggle('selected', card.querySelector('.muted')?.textContent === key);
          }
          detailEl.innerHTML = `
            <h3>${escapeHtml(item.displayName || item.key)}</h3>
            <dl>
              <dt>id</dt><dd>${escapeHtml(item.id)}</dd>
              <dt>key</dt><dd>${escapeHtml(item.key)}</dd>
              <dt>fileName</dt><dd><code>${escapeHtml(item.fileName)}</code></dd>
              <dt>created</dt><dd>${fmtTime(item.createdAt)}</dd>
              <dt>description</dt><dd>${escapeHtml(item.description)}</dd>
            </dl>
            <h3>Args template</h3>
            <pre>${escapeHtml(JSON.stringify(item.argsTemplate || [], null, 2))}</pre>
            <h3>Metadata</h3>
            <pre>${escapeHtml(JSON.stringify(item.metadata || {}, null, 2))}</pre>`;
        }

        function fmtTime(value) {
          return new Date(value).toLocaleString();
        }

        function escapeHtml(value) {
          return String(value ?? '').replace(/[&<>"']/g, ch => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
          }[ch]));
        }

        loadLibrary();
      </script>
    </body>
    </html>
    """;
