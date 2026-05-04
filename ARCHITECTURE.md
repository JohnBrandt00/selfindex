# CodeIndexer — Architecture & Porting Guide

## What It Does

CodeIndexer crawls a directory of source code, chunks it into meaningful units (functions, classes, file-level blocks), stores those chunks in two indexes:

1. **Lucene.NET** — BM25 full-text search with stored metadata fields
2. **HNSW in-memory** — approximate nearest-neighbor vector search, persisted to `.bin` files

At query time it runs **hybrid search**: combines BM25 keyword ranks and vector similarity ranks via Reciprocal Rank Fusion (RRF), returning the best of both.

Additional features: incremental re-indexing (md5 skip), metadata enrichers, 3D embedding explorer (PCA), grounded chat (RAG), MCP tool exposure, and a file health dashboard.

---

## Project Layout

```
CodeIndexer/
├── Components/
│   ├── Layout/
│   │   └── MainLayout.razor          # nav bar
│   └── Pages/
│       ├── Home.razor                # dashboard — start/stop indexer, live log
│       ├── Search.razor              # keyword + vector search UI, filters, re-index
│       ├── Explore.razor             # 3D embedding explorer (Three.js + PCA)
│       ├── Chat.razor                # RAG chat grounded on indexed code
│       ├── Health.razor              # file health table — stale/missing detection
│       └── AiEndpoint.razor          # MCP endpoint info page
├── Models/
│   ├── SearchResult.cs               # unified result DTO
│   ├── CodeChunk.cs                  # unit of indexable content
│   └── IndexingState.cs              # shared mutable state (progress, logs)
├── Services/
│   ├── LuceneIndexService.cs         # Lucene.NET BM25 index
│   ├── VectorIndexService.cs         # HNSW + PCA + cosine similarity
│   ├── EmbeddingService.cs           # Ollama embedding API client
│   ├── IndexingOrchestrator.cs       # top-level crawl/chunk/index pipeline
│   ├── CodeChunker.cs                # splits files into CodeChunks
│   ├── DocumentCracker.cs            # PDF/Office/HTML/image → text
│   ├── VisionService.cs              # Ollama vision API (image OCR/description)
│   ├── CodeSearchTools.cs            # MCP tool definitions
│   └── Enrichers/
│       ├── IMetadataEnricher.cs      # enricher interface
│       ├── Md5Enricher.cs
│       ├── LastModifiedEnricher.cs
│       ├── FileSizeEnricher.cs
│       └── LanguageEnricher.cs
├── wwwroot/
│   ├── app.css                       # all custom CSS
│   ├── viz.js                        # Three.js 3D explorer logic
│   └── libs/
│       ├── three.min.js              # Three.js r128 UMD (local, no CDN)
│       └── OrbitControls.js
├── Program.cs                        # DI wiring, MCP server, HTTP client
└── appsettings.json
```

---

## Services

### LuceneIndexService

Single-responsibility: owns the Lucene FSDirectory and exposes CRUD + search.

**Lucene fields stored per chunk:**

| Field constant | Lucene type | Purpose |
|---|---|---|
| `F_KEY = "key"` | StringField (stored) | unique `{filePath}::{startLine}` |
| `F_PATH = "path"` | StringField (stored+indexed) | absolute file path |
| `F_RELPATH = "relpath"` | StoredField | relative path from watch root |
| `F_CONTENT = "content"` | TextField (analyzed, not stored) | searchable text |
| `F_CONTENT_STORED = "content_s"` | StoredField | raw text for retrieval |
| `F_TYPE = "chunk_type"` | StringField (stored) | `function`, `class`, `file`, etc. |
| `F_SYMBOL = "symbol"` | StoredField | function/class name if extracted |
| `F_START = "start_line"` | StoredField | int as string |
| `F_END = "end_line"` | StoredField | int as string |
| `F_MD5 = "md5"` | StoredField | file md5 for incremental check |
| `F_MODIFIED = "last_modified"` | StoredField | ISO 8601 datetime |
| `F_SIZE = "file_size"` | StoredField | bytes as string |
| `F_LANG = "language"` | StringField (stored) | e.g. `"C#"`, `"TypeScript"` |

**Key methods:**

```csharp
void Upsert(CodeChunk chunk, Dictionary<string, object>? meta = null)
void DeleteByPath(string filePath)
void Commit()
List<SearchResult> Search(string query, int top = 20)
List<SearchResult> FetchByKeys(IEnumerable<string> keys)
string? GetStoredMd5(string filePath)        // for incremental indexing
List<string> GetDistinctLanguages()
List<FileStats> GetFileStats()
```

**Upsert flow:** delete existing docs for that file path → write new docs with metadata fields.

---

### VectorIndexService

In-memory HNSW graph with binary persistence. No external vector DB required.

**Persistence files** (written to `IndexPath` from config):

| File | Contents |
|---|---|
| `vectors.bin` | float32 embedding arrays, length-prefixed |
| `graph.bin` | HNSW adjacency lists, binary |
| `meta.json` | `[{key, filePath, startLine}]` |

**Key methods:**

```csharp
void Add(string key, string filePath, int startLine, float[] vector)
void DeleteByFilePath(string filePath)
List<string> Search(float[] query, int top)   // returns Lucene keys
void Save()
void Load()
int LiveEntries { get; }

// PCA projection for 3D explorer
PcaSpace ProjectTo3D()
float[] ProjectPoint(PcaSpace space, float[] vector)
```

**PCA projection:** power iteration for top-3 principal components computed over all stored vectors. Query vectors are projected into the same space via `ProjectPoint()`. Used by the Explore page to render a navigable 3D point cloud.

---

### IndexingOrchestrator

Coordinates the full pipeline: scan → crack → chunk → enrich → index.

**Pipeline per file:**

```
1. Check md5 via LuceneIndexService.GetStoredMd5()
2. If unchanged → skip (incremental)
3. DocumentCracker.CrackAsync(filePath) → raw text
4. CodeChunker.Chunk(text, ext) → List<CodeChunk>
5. EmbeddingService.EmbedAsync(content) → float[] per chunk
6. Run all IMetadataEnricher.EnrichAsync() → merge into enrichedMeta dict
7. LuceneIndexService.Upsert(chunk, enrichedMeta) per chunk
8. VectorIndexService.Add(key, path, line, vector) per chunk
```

**Incremental indexing:** `IsUnchanged()` computes md5 of the file on disk and compares to what's stored in Lucene. If equal, the file is skipped entirely. Forces re-index by calling `ReindexFileAsync(filePath)`.

**`ReindexFileAsync`:**
```csharp
public async Task ReindexFileAsync(string filePath, CancellationToken ct = default)
{
    _lucene.DeleteByPath(filePath);
    _vectors.DeleteByFilePath(filePath);
    await IndexFileAsync(filePath, _state.WatchPath, ct);
    _lucene.Commit();
    _vectors.Save();
}
```

---

### EmbeddingService

Calls Ollama embedding API. Auto-detects `/api/embed` (new) vs `/api/embeddings` (legacy) by trying the new endpoint first.

```csharp
Task<float[]> EmbedAsync(string text)
```

Config: `Ollama:BaseUrl`, `Ollama:EmbeddingModel`

---

### CodeChunker

Splits file content into `CodeChunk` records. Strategy varies by file type:

- **Code files (.cs, .ts, .py, etc.):** regex-based detection of function/class/method boundaries → one chunk per symbol, remainder as file-level chunk
- **Markdown/text:** paragraph or heading-delimited chunks
- **JSON/YAML/XML:** whole-file chunk (structured data doesn't chunk well by line)
- **Fallback:** fixed sliding window (300 lines, 50-line overlap)

`CodeChunk` record:
```csharp
record CodeChunk(
    string FilePath,
    string RelativePath,
    string Content,
    string ChunkType,    // "function", "class", "file", etc.
    string Symbol,       // name if extractable
    int StartLine,
    int EndLine
);
```

---

### DocumentCracker

Converts non-text files to plaintext before chunking:

| File type | Library |
|---|---|
| `.pdf` | PdfPig |
| `.docx` | OpenXml SDK |
| `.xlsx` | OpenXml SDK |
| `.pptx` | OpenXml SDK |
| `.html`/`.htm` | HtmlAgilityPack |
| `.png`/`.jpg`/`.jpeg`/`.gif`/`.bmp`/`.webp` | ImageSharp + Ollama vision |

Images are described by `VisionService` which sends the image to `Ollama:VisionModel` and returns the text description.

---

### Enrichers

Each enricher implements:

```csharp
public interface IMetadataEnricher
{
    string Name { get; }
    Task<Dictionary<string, object>> EnrichAsync(
        string filePath,
        string content,
        CancellationToken ct = default);
}
```

| Enricher | Key(s) returned | Notes |
|---|---|---|
| `Md5Enricher` | `md5` | hex string, used for incremental skip |
| `LastModifiedEnricher` | `last_modified`, `created` | ISO 8601 |
| `FileSizeEnricher` | `file_size_bytes` | long |
| `LanguageEnricher` | `language`, `extension` | 50+ extension→name mappings |

Enrichers are registered conditionally in `Program.cs` based on `appsettings.json`:

```json
"Enrichers": {
  "Md5": true,
  "LastModified": true,
  "FileSize": true,
  "Language": true
}
```

---

### CodeSearchTools (MCP)

Exposes search via the Model Context Protocol so AI agents can call it:

```csharp
[McpServerToolType]
public class CodeSearchTools
{
    [McpServerTool(Name = "search_code")]
    public async Task<string> SearchCode(string query, int top = 10) { ... }
}
```

Registered in `Program.cs` via `builder.Services.AddMcpServer().WithTools<CodeSearchTools>()`.

---

## Search: BM25 + Vector Hybrid (RRF)

### BM25 (Lucene)

Uses `MultiFieldQueryParser` across `content` and `symbol` fields with `StandardAnalyzer`. Returns ranked `SearchResult` list.

### Vector (HNSW cosine)

Embeds the query, runs `VectorIndexService.Search()` returning Lucene keys in similarity order.

### Hybrid fusion (RRF)

```csharp
// Reciprocal Rank Fusion
score(doc) = Σ 1 / (k + rank_i)   where k = 60
```

Both ranked lists are merged: each document gets a combined score from its BM25 rank and vector rank. Documents appearing in only one list still get a score from that list alone. Final list sorted descending by fused score.

---

## Configuration Reference

```json
{
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "VisionModel": "llava",
    "ChatModel": "llama3"
  },
  "Lucene": {
    "IndexPath": "lucene-index"
  },
  "Enrichers": {
    "Md5": true,
    "LastModified": true,
    "FileSize": true,
    "Language": true
  },
  "Indexer": {
    "Extensions": ".cs,.ts,.py,...",
    "ExcludeFolders": "obj,bin,node_modules,.git,...",
    "ExcludeFiles": "package-lock.json,yarn.lock,..."
  }
}
```

---

## Data Flow Diagram

```
Filesystem (WatchPath)
    │
    ▼
IndexingOrchestrator.IndexAllAsync()
    │
    ├─► DocumentCracker (PDF/Office/HTML/images → text)
    │
    ├─► CodeChunker → [CodeChunk, ...]
    │
    ├─► EmbeddingService.EmbedAsync() → float[]
    │
    ├─► IMetadataEnricher[] → enrichedMeta dict
    │
    ├─► LuceneIndexService.Upsert(chunk, enrichedMeta)
    │       └─ stored: key, path, relpath, content, type, symbol,
    │                  lines, md5, last_modified, file_size, language
    │
    └─► VectorIndexService.Add(key, path, line, vector)
            └─ stored: vectors.bin, graph.bin, meta.json


Search query
    │
    ├─► EmbeddingService.EmbedAsync(query) → float[]
    ├─► VectorIndexService.Search(emb, top) → [key, ...]
    ├─► LuceneIndexService.Search(query, top) → [SearchResult, ...]
    └─► RRF merge → [SearchResult, ...] ordered by combined score
```

---

## Porting Checklist

To use this pattern in another project:

1. **Copy services:** `LuceneIndexService`, `VectorIndexService`, `EmbeddingService`, `CodeChunker`, `IndexingOrchestrator`, enrichers
2. **NuGet packages needed:**
   - `Lucene.Net` 4.8.x
   - `Lucene.Net.Analysis.Common`
   - `Lucene.Net.QueryParser`
   - `PdfPig` (if PDF cracking needed)
   - `DocumentFormat.OpenXml` (if Office cracking needed)
   - `HtmlAgilityPack` (if HTML cracking needed)
   - `SixLabors.ImageSharp` (if image cracking needed)
3. **Register in DI** (see `Program.cs`):
   ```csharp
   builder.Services.AddSingleton<LuceneIndexService>();
   builder.Services.AddSingleton<VectorIndexService>();
   builder.Services.AddSingleton<EmbeddingService>();
   builder.Services.AddSingleton<IndexingOrchestrator>();
   // enrichers conditionally
   ```
4. **Ollama** must be running with your chosen embedding model pulled
5. **Index path** must be writable — Lucene creates its own files there
6. **Vector files** (`vectors.bin`, `graph.bin`, `meta.json`) are written to the same `IndexPath`
7. Call `orchestrator.IndexAllAsync(rootPath, ct)` to build the index
8. Call `lucene.Search(query)` + `vectors.Search(emb, top)` + RRF to query
