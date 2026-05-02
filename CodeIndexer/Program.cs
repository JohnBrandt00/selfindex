using CodeIndexer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient("ollama", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");
    c.Timeout = TimeSpan.FromMinutes(5);
});

// Core indexing services
builder.Services.AddSingleton<LuceneIndexService>();
builder.Services.AddSingleton<VectorIndexService>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<VisionService>();
builder.Services.AddSingleton<DocumentCracker>();
builder.Services.AddSingleton<IndexingState>();
builder.Services.AddSingleton<IndexingOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexingOrchestrator>());

// CodeSearchTools is both an MCP tool class and injectable in Blazor pages
builder.Services.AddSingleton<CodeSearchTools>();

// MCP server — exposes code search tools to AI agents via SSE
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<CodeSearchTools>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseAntiforgery();

// MCP endpoint — AI agents connect here
app.MapMcp("/mcp");

app.MapRazorComponents<CodeIndexer.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
