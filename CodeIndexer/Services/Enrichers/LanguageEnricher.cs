namespace CodeIndexer.Services.Enrichers;

public sealed class LanguageEnricher : IMetadataEnricher
{
    public string Name => "Language";

    private static readonly Dictionary<string, string> ExtToLang = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"]     = "csharp",
        [".ts"]     = "typescript",
        [".tsx"]    = "typescript",
        [".js"]     = "javascript",
        [".jsx"]    = "javascript",
        [".py"]     = "python",
        [".go"]     = "go",
        [".rs"]     = "rust",
        [".java"]   = "java",
        [".kt"]     = "kotlin",
        [".cpp"]    = "cpp",
        [".c"]      = "c",
        [".h"]      = "c",
        [".hpp"]    = "cpp",
        [".rb"]     = "ruby",
        [".php"]    = "php",
        [".swift"]  = "swift",
        [".fs"]     = "fsharp",
        [".fsx"]    = "fsharp",
        [".vue"]    = "vue",
        [".svelte"] = "svelte",
        [".razor"]  = "razor",
        [".html"]   = "html",
        [".htm"]    = "html",
        [".css"]    = "css",
        [".scss"]   = "scss",
        [".sql"]    = "sql",
        [".sh"]     = "shell",
        [".ps1"]    = "powershell",
        [".bat"]    = "batch",
        [".md"]     = "markdown",
        [".json"]   = "json",
        [".yaml"]   = "yaml",
        [".yml"]    = "yaml",
        [".xml"]    = "xml",
        [".toml"]   = "toml",
        [".pdf"]    = "pdf",
        [".docx"]   = "word",
        [".xlsx"]   = "excel",
        [".pptx"]   = "powerpoint",
        [".png"]    = "image",
        [".jpg"]    = "image",
        [".jpeg"]   = "image",
        [".gif"]    = "image",
        [".webp"]   = "image",
    };

    public Task<Dictionary<string, object>> EnrichAsync(string filePath, string content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath);
        var lang = ExtToLang.TryGetValue(ext, out var l) ? l : "unknown";
        return Task.FromResult(new Dictionary<string, object>
        {
            ["language"]  = lang,
            ["extension"] = ext.TrimStart('.').ToLowerInvariant()
        });
    }
}
