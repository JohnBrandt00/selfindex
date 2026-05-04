using CodeIndexer.Models;
using CodeIndexer.Services.Enrichers;

namespace CodeIndexer.Services;

public class IndexingOrchestrator : BackgroundService
{
    private readonly EmbeddingService _embeddings;
    private readonly LuceneIndexService _lucene;
    private readonly VectorIndexService _vectors;
    private readonly DocumentCracker _cracker;
    private readonly IEnumerable<IMetadataEnricher> _enrichers;
    private readonly IndexingState _state;
    private readonly ILogger<IndexingOrchestrator> _logger;
    private readonly HashSet<string> _extensions;
    private readonly HashSet<string> _excludeFolders;
    private readonly HashSet<string> _excludeFiles;

    private FileSystemWatcher? _watcher;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string path, bool deleted)> _pendingFiles = new();

    public IndexingOrchestrator(
        EmbeddingService embeddings,
        LuceneIndexService lucene,
        VectorIndexService vectors,
        DocumentCracker cracker,
        IEnumerable<IMetadataEnricher> enrichers,
        IndexingState state,
        IConfiguration config,
        ILogger<IndexingOrchestrator> logger)
    {
        _embeddings = embeddings;
        _lucene = lucene;
        _vectors = vectors;
        _cracker = cracker;
        _enrichers = enrichers;
        _state = state;
        _logger = logger;

        var exts = config["Indexer:Extensions"] ?? ".cs";
        _extensions = exts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();

        var excl = config["Indexer:ExcludeFolders"] ?? "obj,bin,node_modules,.git";
        _excludeFolders = excl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant())
            .ToHashSet();

        var exclFiles = config["Indexer:ExcludeFiles"] ?? "package-lock.json";
        _excludeFiles = exclFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant())
            .ToHashSet();
    }

    private static readonly HashSet<string> BinaryExts = [
        ".pdf", ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif"
    ];

    private bool ShouldIndex(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!_extensions.Contains(ext)) return false;

        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        if (_excludeFiles.Contains(fileName)) return false;

        var parts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => _excludeFolders.Contains(p.ToLowerInvariant()))) return false;

        if (BinaryExts.Contains(ext)) return true;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return false;
            var buf = new byte[Math.Min(8192, fs.Length)];
            int read = fs.Read(buf, 0, buf.Length);
            if (Array.IndexOf(buf, (byte)0, 0, read) >= 0) return false;
        }
        catch { return false; }

        return true;
    }

    public async Task StartWatchingAsync(string repoPath, CancellationToken ct = default)
    {
        _watcher?.Dispose();
        _state.SetWatchPath(repoPath);
        _state.AddLog($"Starting watch on {repoPath}");
        _state.AddLog($"Extensions: {string.Join(", ", _extensions.Order())}");

        await IndexAllAsync(repoPath, ct);

        _watcher = new FileSystemWatcher(repoPath)
        {
            Filter = "*.*",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, e) => { if (ShouldIndex(e.FullPath)) Enqueue(e.FullPath, false); };
        _watcher.Created += (_, e) => { if (ShouldIndex(e.FullPath)) Enqueue(e.FullPath, false); };
        _watcher.Deleted += (_, e) => Enqueue(e.FullPath, true);
        _watcher.Renamed += (_, e) =>
        {
            Enqueue(e.OldFullPath, true);
            if (ShouldIndex(e.FullPath)) Enqueue(e.FullPath, false);
        };

        _state.SetWatching(true);
        _state.AddLog("File watcher active.");
    }

    private void Enqueue(string path, bool deleted) =>
        _pendingFiles.Enqueue((path, deleted));

    private async Task IndexAllAsync(string repoPath, CancellationToken ct)
    {
        var files = Directory.GetFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(ShouldIndex)
            .ToList();

        _state.StartIndexing(files.Count);
        _state.AddLog($"Found {files.Count} files to index.");

        int skipped = 0;
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            // Skip files whose md5 hasn't changed since last index
            if (IsUnchanged(file))
            {
                skipped++;
                _state.FileStart(file, 0);
                _state.FileComplete();
                continue;
            }

            await IndexFileAsync(file, repoPath, ct);
        }

        if (skipped > 0)
            _state.AddLog($"Skipped {skipped} unchanged files.");

        _lucene.Commit();
        _vectors.Save();
        _state.FinishIndexing();
    }

    private bool IsUnchanged(string filePath)
    {
        try
        {
            var storedMd5 = _lucene.GetStoredMd5(filePath);
            if (storedMd5 == null) return false;
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = Convert.ToHexString(md5.ComputeHash(File.ReadAllBytes(filePath))).ToLowerInvariant();
            return hash == storedMd5;
        }
        catch { return false; }
    }

    private async Task IndexFileAsync(string filePath, string repoRoot, CancellationToken ct)
    {
        var rel = Path.GetRelativePath(repoRoot, filePath);
        try
        {
            _logger.LogInformation("Cracking {File}", rel);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var chunks = await _cracker.CrackAsync(filePath, repoRoot, ct);
            sw.Stop();

            if (chunks.Count == 0)
            {
                _state.AddLog($"Skip (no chunks): {rel}");
                _state.FileStart(filePath, 0);
                _state.FileComplete();
                return;
            }

            _logger.LogInformation("  → {Count} chunks in {Ms}ms, embedding...", chunks.Count, sw.ElapsedMilliseconds);
            _state.FileStart(filePath, chunks.Count);

            // Run enrichers once per file — results stored in Lucene document fields
            var enrichedMeta = new Dictionary<string, object>
            {
                ["file_path"]     = filePath,
                ["relative_path"] = rel,
                ["file_name"]     = Path.GetFileName(filePath),
            };
            foreach (var enricher in _enrichers)
            {
                try
                {
                    var extra = await enricher.EnrichAsync(filePath, "", ct);
                    foreach (var (k, v) in extra) enrichedMeta[k] = v;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Enricher {Name} failed for {File}: {Msg}", enricher.Name, rel, ex.Message);
                }
            }

            var contents = chunks.Select(c => c.Content).ToList();
            float[][] embeddings;
            try
            {
                embeddings = await _embeddings.EmbedBatchAsync(contents, ct);
            }
            catch (Exception ex)
            {
                var hint = ex.Message.Contains("404")
                    ? $"Model not found — run: ollama pull {_embeddings.Model}"
                    : ex.Message;
                _state.AddLog($"Embedding error ({rel}): {hint}");
                _logger.LogWarning("Embedding failed for {File}: {Hint}", rel, hint);
                _state.FileComplete();
                return;
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i].Embedding = i < embeddings.Length ? embeddings[i] : null;
                _lucene.Upsert(chunks[i], enrichedMeta);

                if (chunks[i].Embedding is { Length: > 0 } vec)
                {
                    var docKey = $"{chunks[i].FilePath}:{chunks[i].StartLine}";
                    _vectors.Upsert(docKey, vec);
                }

                _state.ChunkIndexed();
            }

            _state.FileComplete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error indexing {File}", filePath);
            _state.AddLog($"Error: {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    public async Task ReindexFileAsync(string filePath, CancellationToken ct = default)
    {
        _lucene.DeleteByPath(filePath);
        _vectors.DeleteByFilePath(filePath);
        await IndexFileAsync(filePath, _state.WatchPath, ct);
        _lucene.Commit();
        _vectors.Save();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state.AddLog("Indexer ready. Select a path to begin.");
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(2000, stoppingToken);

            if (_pendingFiles.IsEmpty || !_state.IsWatching) continue;

            var toProcess = new HashSet<(string, bool)>();
            while (_pendingFiles.TryDequeue(out var item))
                toProcess.Add(item);

            foreach (var (path, deleted) in toProcess)
            {
                if (deleted)
                {
                    _lucene.DeleteByPath(path);
                    _vectors.DeleteByFilePath(path);
                    _state.AddLog($"Removed: {Path.GetFileName(path)}");
                }
                else
                {
                    await IndexFileAsync(path, _state.WatchPath, stoppingToken);
                    _lucene.Commit();
                    _vectors.Save();
                }
            }
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
