using CodeIndexer.Services;
using CodeIndexer.Services.Enrichers;

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

// Metadata enrichers — register only those enabled in config
var cfg = builder.Configuration;
if (cfg.GetValue<bool>("Enrichers:Md5",         true)) builder.Services.AddSingleton<IMetadataEnricher, Md5Enricher>();
if (cfg.GetValue<bool>("Enrichers:LastModified", true)) builder.Services.AddSingleton<IMetadataEnricher, LastModifiedEnricher>();
if (cfg.GetValue<bool>("Enrichers:FileSize",     true)) builder.Services.AddSingleton<IMetadataEnricher, FileSizeEnricher>();
if (cfg.GetValue<bool>("Enrichers:Language",     true)) builder.Services.AddSingleton<IMetadataEnricher, LanguageEnricher>();

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

app.MapMcp("/mcp");

app.MapRazorComponents<CodeIndexer.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
