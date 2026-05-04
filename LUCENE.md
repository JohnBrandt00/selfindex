# Lucene.NET in CodeIndexer

## Why Lucene

Lucene.NET 4.8 gives you BM25 full-text search with zero infrastructure — just files on disk. It handles tokenization, stemming, inverted index construction, and ranked retrieval. In this project it plays two roles:

1. **Search index** — keyword queries return ranked results from code content
2. **Metadata store** — each document stores structured fields (md5, language, file size, timestamps) that the vector index doesn't carry

---

## Setup

```csharp
// Program.cs
builder.Services.AddSingleton<LuceneIndexService>();
```

```csharp
// LuceneIndexService constructor
var indexPath = config["Lucene:IndexPath"] ?? "lucene-index";
var dir = FSDirectory.Open(indexPath);
var analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);
var writerConfig = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer);
_writer = new IndexWriter(dir, writerConfig);
_reader = DirectoryReader.Open(_writer, applyAllDeletes: true);
_searcher = new IndexSearcher(_reader);
```

The `IndexWriter` is held open for the lifetime of the app (singleton). `DirectoryReader.Open(_writer, true)` creates a near-real-time reader — after each `Commit()` call it sees new documents without reopening the directory.

---

## Document Schema

Every `CodeChunk` becomes one Lucene `Document`. Fields:

```csharp
// Field type reference:
// StringField  — indexed as-is (no tokenization), optionally stored
// TextField    — analyzed/tokenized, optionally stored
// StoredField  — stored only, not searchable
// Int32Field   — numeric, not used here (stored as string instead for simplicity)

doc.Add(new StringField(F_KEY,          chunk.Key,          Field.Store.YES));
doc.Add(new StringField(F_PATH,         chunk.FilePath,     Field.Store.YES));
doc.Add(new StoredField(F_RELPATH,      chunk.RelativePath));
doc.Add(new TextField  (F_CONTENT,      chunk.Content,      Field.Store.NO));   // analyzed, not stored
doc.Add(new StoredField(F_CONTENT_STORED, chunk.Content));                      // stored verbatim
doc.Add(new StringField(F_TYPE,         chunk.ChunkType,    Field.Store.YES));
doc.Add(new StoredField(F_SYMBOL,       chunk.Symbol ?? ""));
doc.Add(new StoredField(F_START,        chunk.StartLine.ToString()));
doc.Add(new StoredField(F_END,          chunk.EndLine.ToString()));

// Enricher metadata (stored only — not searchable as free text)
if (meta.TryGetValue("md5",            out var md5))  doc.Add(new StoredField(F_MD5,      md5.ToString()!));
if (meta.TryGetValue("last_modified",  out var lm))   doc.Add(new StoredField(F_MODIFIED, lm.ToString()!));
if (meta.TryGetValue("file_size_bytes",out var fs))   doc.Add(new StoredField(F_SIZE,     fs.ToString()!));
if (meta.TryGetValue("language",       out var lang)) doc.Add(new StringField(F_LANG,     lang.ToString()!, Field.Store.YES));
```

**Why `F_CONTENT` is not stored:** The analyzed field is what Lucene tokenizes and scores — you don't need it back. The raw text lives in `F_CONTENT_STORED` so you can display it. This halves the index size for large codebases.

**Why enricher fields are `StoredField` (not `StringField`):** md5, file size, timestamps are not searchable — they're just metadata you want to retrieve. `StringField` would add them to the inverted index unnecessarily.

**Exception: `language`** uses `StringField` (stored+indexed) because `GetDistinctLanguages()` scans the index using a `TermEnum` — it needs to be in the term dictionary.

---

## Upsert Pattern

Lucene has no native upsert. The pattern used here: delete-then-add.

```csharp
public void Upsert(CodeChunk chunk, Dictionary<string, object>? meta = null)
{
    // Delete all existing docs for this file path
    _writer.DeleteDocuments(new Term(F_PATH, chunk.FilePath));

    // Add fresh document
    var doc = BuildDocument(chunk, meta);
    _writer.AddDocument(doc);
    // Don't commit here — caller batches and calls Commit() after all chunks
}

public void Commit()
{
    _writer.Commit();
    // Refresh near-real-time reader
    var newReader = DirectoryReader.OpenIfChanged(_reader);
    if (newReader != null)
    {
        _reader.Dispose();
        _reader = newReader;
        _searcher = new IndexSearcher(_reader);
    }
}
```

**Important:** `DeleteDocuments` deletes by file path, which removes ALL chunks for that file. This is intentional — when re-indexing a file, the number of chunks may have changed, so you can't update in place.

---

## BM25 Search

```csharp
public List<SearchResult> Search(string query, int top = 20)
{
    var fields = new[] { F_CONTENT, F_SYMBOL };
    var parser = new MultiFieldQueryParser(LuceneVersion.LUCENE_48, fields, _analyzer);
    var q = parser.Parse(query);

    var hits = _searcher.Search(q, top);
    return hits.ScoreDocs
        .Select(h => DocToResult(_searcher.Doc(h.Doc), h.Score))
        .ToList();
}
```

**BM25 is the default scorer in Lucene.NET 4.8.** No configuration needed — it replaced TF-IDF as the default. BM25 saturates term frequency (repeated terms give diminishing returns) and normalizes for document length, which works well for code where function bodies vary wildly in size.

**`MultiFieldQueryParser`** searches both `content` (the full code text) and `symbol` (the function/class name). Symbol matches are boosted slightly because a query like "IndexFile" that exactly matches a function name is almost certainly what you want.

---

## Fetch by Key (for vector search results)

After vector search returns a list of `key` strings, they need to be hydrated into full `SearchResult` objects:

```csharp
public List<SearchResult> FetchByKeys(IEnumerable<string> keys)
{
    var results = new List<SearchResult>();
    foreach (var key in keys)
    {
        var q = new TermQuery(new Term(F_KEY, key));
        var hits = _searcher.Search(q, 1);
        if (hits.TotalHits > 0)
            results.Add(DocToResult(_searcher.Doc(hits.ScoreDocs[0].Doc), 1.0f));
    }
    return results;
}
```

`TermQuery` on a `StringField` is an exact match — no tokenization, O(log N) lookup via the inverted index.

---

## Incremental Indexing: GetStoredMd5

The orchestrator checks whether a file has changed before re-indexing:

```csharp
public string? GetStoredMd5(string filePath)
{
    var q = new TermQuery(new Term(F_PATH, filePath));
    var hits = _searcher.Search(q, 1);
    if (hits.TotalHits == 0) return null;

    var doc = _searcher.Doc(hits.ScoreDocs[0].Doc);
    return doc.Get(F_MD5);  // null if field not present
}
```

Only the first matching document is checked. Since all chunks of a file share the same md5 (set at file level by the enricher), any chunk's md5 is representative.

**In IndexingOrchestrator:**

```csharp
private bool IsUnchanged(string filePath)
{
    var stored = _lucene.GetStoredMd5(filePath);
    if (stored == null) return false;  // not indexed yet

    using var md5 = MD5.Create();
    using var stream = File.OpenRead(filePath);
    var hash = Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    return hash == stored;
}
```

---

## GetDistinctLanguages

Used to populate filter dropdowns in the Search and Health pages:

```csharp
public List<string> GetDistinctLanguages()
{
    var langs = new HashSet<string>();
    var terms = MultiFields.GetTerms(_reader, F_LANG);
    if (terms == null) return [];

    var tenum = terms.GetEnumerator();
    while (tenum.MoveNext())
        langs.Add(tenum.Term.Utf8ToString());

    return langs.Order().ToList();
}
```

`MultiFields.GetTerms` returns the term dictionary for a field across all segments. For a `StringField`, each distinct value is a term — iterating gives you all unique languages without scanning every document.

---

## GetFileStats

Returns one `FileStats` record per unique file path. Used by the Health page:

```csharp
public record FileStats(
    string FilePath, string RelativePath,
    int ChunkCount, string Language,
    string Md5, string LastModified, string FileSize);

public List<FileStats> GetFileStats()
{
    // Group all docs by file path
    var grouped = new Dictionary<string, FileStats>();
    var q = new MatchAllDocsQuery();
    var hits = _searcher.Search(q, _reader.NumDocs);

    foreach (var hit in hits.ScoreDocs)
    {
        var doc = _searcher.Doc(hit.Doc);
        var path = doc.Get(F_PATH);
        if (grouped.TryGetValue(path, out var existing))
        {
            grouped[path] = existing with { ChunkCount = existing.ChunkCount + 1 };
        }
        else
        {
            grouped[path] = new FileStats(
                path,
                doc.Get(F_RELPATH),
                1,
                doc.Get(F_LANG) ?? "",
                doc.Get(F_MD5) ?? "",
                doc.Get(F_MODIFIED) ?? "",
                doc.Get(F_SIZE) ?? "");
        }
    }

    return grouped.Values.ToList();
}
```

**Note:** `MatchAllDocsQuery` with `_reader.NumDocs` as the limit fetches every document. For very large indexes (100k+ chunks) this would be slow — consider caching or a separate file-level index.

---

## DeleteByPath

```csharp
public void DeleteByPath(string filePath)
{
    _writer.DeleteDocuments(new Term(F_PATH, filePath));
    // Caller must call Commit() after
}
```

Deletion in Lucene is lazy: documents are marked deleted in the segment but not physically removed until a merge. `DirectoryReader.Open(_writer, applyAllDeletes: true)` ensures the near-real-time reader respects pending deletions before the next commit.

---

## Lucene Concepts Quick Reference

| Concept | What it means |
|---|---|
| **Segment** | Immutable mini-index. Lucene writes new segments and merges them in background |
| **IndexWriter** | Writes/deletes documents, manages segments |
| **DirectoryReader** | Read view of the index; must be refreshed after commits |
| **IndexSearcher** | Executes queries against a reader |
| **Analyzer** | Tokenizes text: lowercasing, stop words, stemming. StandardAnalyzer does basic English |
| **StringField** | Indexed verbatim (no tokenization) — good for IDs, paths, enum values |
| **TextField** | Tokenized and analyzed — good for natural language / code content |
| **StoredField** | Stored but not indexed — retrieved but not searchable |
| **TermQuery** | Exact term lookup — O(log N) |
| **MultiFieldQueryParser** | Parses human query string against multiple fields |
| **BM25** | Default scorer. Better than TF-IDF for variable-length documents |
| **NRT (near-real-time)** | Reader opened from writer sees uncommitted changes within milliseconds |

---

## Common Gotchas

**`content` field not stored:** If you call `doc.Get(F_CONTENT)` you'll get null. Use `doc.Get(F_CONTENT_STORED)` instead.

**Deletes need Commit to propagate to NRT reader:** `DeleteDocuments()` is buffered. Call `Commit()` and refresh the reader or the deletes won't show in search.

**Analyzer must match at write and read time:** If you write with `StandardAnalyzer` but query with a `TermQuery` using an un-lowercased term, you'll miss hits. `MultiFieldQueryParser` uses the same analyzer, so queries are normalized consistently.

**StringField is case-sensitive:** A `StringField` with value `"C#"` won't match a `TermQuery` for `"c#"`. Keep values consistent (lowercase or controlled casing).

**`_reader.NumDocs` in `MatchAllDocsQuery`:** This is the count including deleted-but-not-merged docs. Pass it as the top-N limit to ensure all live docs are returned, but be aware it may slightly overcount.

**Thread safety:** `IndexSearcher` is thread-safe for concurrent reads. `IndexWriter` is thread-safe for concurrent writes. Do not share `IndexReader`/`IndexSearcher` instances that you dispose mid-request.
