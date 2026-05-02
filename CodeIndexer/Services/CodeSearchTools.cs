using CodeIndexer.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace CodeIndexer.Services;

/// <summary>
/// MCP tools exposed to AI agents for searching the indexed C# codebase.
/// Registered as both MCP tools (via [McpServerTool]) and usable as a
/// Semantic Kernel plugin — the same class works for both.
/// </summary>
[McpServerToolType]
public class CodeSearchTools(
    LuceneIndexService lucene,
    VectorIndexService vectors,
    EmbeddingService embeddings,
    IndexingState state)
{
    /// <summary>
    /// Semantically search the indexed C# codebase. Returns matching code chunks
    /// with file path, line numbers, and relevance scores.
    ///
    /// Use mode="hybrid" (default) for best results — combines keyword (BM25) and
    /// semantic (HNSW vector) search using Reciprocal Rank Fusion, matching
    /// Azure AI Search hybrid behaviour.
    /// </summary>
    [McpServerTool]
    [Description(
        "Search the indexed codebase or document set. Returns relevant chunks (methods, classes, " +
        "files, paragraphs) matching the query. Use this to find implementations, usages, patterns, " +
        "or answer questions about the content. mode can be 'hybrid' (default), 'vector' (semantic " +
        "only), or 'text' (keyword only).")]
    public async Task<string> SearchCode(
        [Description("Natural language or keyword query, e.g. 'authentication middleware' or 'how are embeddings stored'")] string query,
        [Description("Search mode: 'hybrid' (BM25 + HNSW vector, recommended), 'vector' (semantic), 'text' (keyword)")] string mode = "hybrid",
        [Description("Maximum number of results to return (1-20)")] int topN = 5)
    {
        topN = Math.Clamp(topN, 1, 20);

        if (lucene.DocumentCount == 0)
            return "Index is empty. No repo has been indexed yet.";

        List<SearchResult> results;
        try
        {
            results = mode switch
            {
                "text"   => lucene.TextSearch(query, topN).Select(t => t.result).ToList(),
                "vector" => await VectorSearchAsync(query, topN),
                _        => await HybridSearchAsync(query, topN)
            };
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}";
        }

        if (results.Count == 0)
            return $"No results found for: {query}";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} results for \"{query}\" (mode={mode}):");
        sb.AppendLine();

        foreach (var (r, i) in results.Select((r, i) => (r, i + 1)))
        {
            sb.AppendLine($"### {i}. {r.Symbol} ({r.ChunkType})");
            sb.AppendLine($"File: {r.RelativePath}  Lines: {r.StartLine}–{r.EndLine}");
            if (r.HybridScore > 0) sb.AppendLine($"Score: RRF={r.HybridScore:F4}  bm25={r.BM25Score:F3}  cos={r.VectorScore:F3}");
            else if (r.VectorScore > 0) sb.AppendLine($"Score: cos={r.VectorScore:F3}");
            else sb.AppendLine($"Score: bm25={r.BM25Score:F3}");
            sb.AppendLine("```csharp");
            sb.AppendLine(TrimContent(r.Content, 40));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get a summary of what has been indexed — repo path, file count, chunk count.
    /// Call this before searching to understand what's available.
    /// </summary>
    [McpServerTool]
    [Description("Returns the current state of the code index: what repo is indexed, " +
                 "how many files and code chunks are available, and whether indexing is active.")]
    public Task<string> GetIndexStats()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Code Index Stats");
        sb.AppendLine($"- Repo path: {(string.IsNullOrEmpty(state.WatchPath) ? "(none indexed yet)" : state.WatchPath)}");
        sb.AppendLine($"- Watching for changes: {state.IsWatching}");
        sb.AppendLine($"- Lucene chunks (BM25): {lucene.DocumentCount}");
        sb.AppendLine($"- HNSW vector entries: {vectors.LiveEntries} live / {vectors.TotalEntries} total");
        sb.AppendLine($"- Embedding model: {embeddings.Model} ({(embeddings.Dimensions > 0 ? embeddings.Dimensions + "d" : "not yet used")})");
        if (state.IsIndexing)
            sb.AppendLine($"- Currently indexing: {state.ProcessedFiles}/{state.TotalFiles} files, {state.IndexedChunks} chunks done");
        return Task.FromResult(sb.ToString());
    }

    /// <summary>
    /// Get the full content of a specific file from the repo.
    /// Use the relative path returned by SearchCode results.
    /// </summary>
    [McpServerTool]
    [Description("Read the full source of a specific file in the indexed repo. " +
                 "Pass the relative path from a SearchCode result.")]
    public Task<string> GetFileContent(
        [Description("Relative file path as returned by SearchCode, e.g. 'src/Services/AuthService.cs'")] string relativePath)
    {
        if (string.IsNullOrEmpty(state.WatchPath))
            return Task.FromResult("No repo indexed yet.");

        var fullPath = Path.Combine(state.WatchPath, relativePath);
        if (!File.Exists(fullPath))
            return Task.FromResult($"File not found: {relativePath}");

        try
        {
            var content = File.ReadAllText(fullPath);
            var ext = Path.GetExtension(relativePath).TrimStart('.').ToLowerInvariant();
            return Task.FromResult($"```{ext}\n// {relativePath}\n{content}\n```");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error reading file: {ex.Message}");
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<List<SearchResult>> VectorSearchAsync(string query, int topN)
    {
        var embedding = await embeddings.EmbedAsync(query);
        var hits = vectors.Search(embedding, topN);
        return lucene.FetchByKeys(hits);
    }

    private async Task<List<SearchResult>> HybridSearchAsync(string query, int topN, int k = 60)
    {
        var embedding  = await embeddings.EmbedAsync(query);
        var textHits   = lucene.TextSearch(query, topN * 3);
        var vectorHits = vectors.Search(embedding, topN * 3);

        var rrf = new Dictionary<string, (float score, float bm25, float vec)>(StringComparer.Ordinal);

        for (int rank = 0; rank < textHits.Count; rank++)
        {
            var (key, bm25, _) = textHits[rank];
            if (key == "") continue;
            rrf[key] = (1f / (k + rank + 1), bm25, 0);
        }
        for (int rank = 0; rank < vectorHits.Count; rank++)
        {
            var (key, cos) = vectorHits[rank];
            float contrib = 1f / (k + rank + 1);
            rrf[key] = rrf.TryGetValue(key, out var e)
                ? (e.score + contrib, e.bm25, cos)
                : (contrib, 0, cos);
        }

        var ranked = rrf.OrderByDescending(kv => kv.Value.score).Take(topN).ToList();
        var docs   = lucene.FetchByKeys(ranked.Select(kv => (kv.Key, kv.Value.vec)));

        foreach (var doc in docs)
        {
            var docKey = $"{doc.FilePath}:{doc.StartLine}";
            if (rrf.TryGetValue(docKey, out var s))
            {
                doc.BM25Score   = s.bm25;
                doc.VectorScore = s.vec;
                doc.HybridScore = s.score;
            }
        }

        return docs.OrderByDescending(d => d.HybridScore).ToList();
    }

    private static string TrimContent(string content, int maxLines)
    {
        var lines = content.Split('\n');
        return lines.Length > maxLines
            ? string.Join('\n', lines.Take(maxLines)) + $"\n// ... ({lines.Length - maxLines} more lines)"
            : content;
    }
}
