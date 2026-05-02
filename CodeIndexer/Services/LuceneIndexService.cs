using CodeIndexer.Models;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Lucene.Net.QueryParsers.Classic;

namespace CodeIndexer.Services;

public class LuceneIndexService : IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;
    private const string F_ID       = "id";
    private const string F_PATH     = "path";
    private const string F_REL      = "rel";
    private const string F_FILE     = "file";
    private const string F_TYPE     = "type";
    private const string F_SYMBOL   = "symbol";
    private const string F_CONTENT  = "content";
    private const string F_START    = "start";
    private const string F_END      = "end";

    private readonly FSDirectory _directory;
    private readonly StandardAnalyzer _analyzer;
    private readonly IndexWriter _writer;
    private readonly object _searchLock = new();

    public string IndexPath { get; }
    public int DocumentCount => _writer.NumDocs;

    public LuceneIndexService(IConfiguration config)
    {
        IndexPath = config["Lucene:IndexPath"]
            ?? Path.Combine(Path.GetTempPath(), "code-indexer-lucene");
        System.IO.Directory.CreateDirectory(IndexPath);

        _directory = FSDirectory.Open(IndexPath);
        _analyzer  = new StandardAnalyzer(Version);
        _writer    = new IndexWriter(_directory, new IndexWriterConfig(Version, _analyzer)
        {
            OpenMode = OpenMode.CREATE_OR_APPEND
        });
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    public void Upsert(CodeChunk chunk)
    {
        var doc = new Document
        {
            new StringField(F_ID,      chunk.Id,                              Field.Store.YES),
            new StoredField(F_PATH,    chunk.FilePath),
            new TextField  (F_REL,     chunk.RelativePath,                    Field.Store.YES),
            new TextField  (F_FILE,    Path.GetFileName(chunk.FilePath),       Field.Store.YES),
            new StringField(F_TYPE,    chunk.ChunkType,                       Field.Store.YES),
            new StoredField(F_SYMBOL,  chunk.Symbol),
            new TextField  (F_CONTENT, chunk.Content,                         Field.Store.YES),
            new StoredField(F_START,   chunk.StartLine),
            new StoredField(F_END,     chunk.EndLine),
        };

        var docKey = $"{chunk.FilePath}:{chunk.StartLine}";
        doc.Add(new StringField("doc_key", docKey, Field.Store.YES));
        _writer.UpdateDocument(new Term("doc_key", docKey), doc);
    }

    public void Commit() => _writer.Commit();

    public void DeleteByPath(string filePath)
    {
        lock (_searchLock)
        {
            using var reader = _writer.GetReader(applyAllDeletes: true);
            var searcher = new IndexSearcher(reader);
            var hits = searcher.Search(new TermQuery(new Term(F_PATH, filePath)), int.MaxValue);
            foreach (var hit in hits.ScoreDocs)
            {
                var key = searcher.Doc(hit.Doc).Get("doc_key");
                if (key != null) _writer.DeleteDocuments(new Term("doc_key", key));
            }
        }
        _writer.Commit();
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    /// <summary>BM25 full-text search. Returns (docKey, bm25Score, SearchResult).</summary>
    public List<(string docKey, float score, SearchResult result)> TextSearch(string query, int topN = 50, bool rawLucene = false)
    {
        lock (_searchLock)
        {
            using var reader = _writer.GetReader(applyAllDeletes: true);
            var searcher = new IndexSearcher(reader);
            var parser   = new MultiFieldQueryParser(Version, [F_CONTENT, F_SYMBOL], _analyzer);
            Query q;
            try   { q = parser.Parse(rawLucene ? query : QueryParserBase.Escape(query)); }
            catch { return []; }

            var hits = searcher.Search(q, topN);
            return hits.ScoreDocs
                .Select(h =>
                {
                    var doc = searcher.Doc(h.Doc);
                    return (doc.Get("doc_key") ?? "", h.Score, DocToResult(doc, h.Score, 0, 0));
                })
                .ToList();
        }
    }

    /// <summary>Filter a set of doc_keys against a raw Lucene query, returning only those that pass.</summary>
    public HashSet<string> FilterKeysByQuery(IEnumerable<string> docKeys, string luceneQuery)
    {
        var keys = docKeys.ToList();
        if (keys.Count == 0) return [];
        lock (_searchLock)
        {
            using var reader  = _writer.GetReader(applyAllDeletes: true);
            var searcher = new IndexSearcher(reader);
            var parser   = new MultiFieldQueryParser(Version, [F_CONTENT, F_SYMBOL, F_FILE, F_REL], _analyzer);
            Query userQ;
            try   { userQ = parser.Parse(luceneQuery); }
            catch { return keys.ToHashSet(); }

            // Restrict to just our candidate keys, then apply the user filter
            var keyFilter = new BooleanQuery();
            foreach (var k in keys)
                keyFilter.Add(new TermQuery(new Term("doc_key", k)), Occur.SHOULD);

            var combined = new BooleanQuery();
            combined.Add(keyFilter, Occur.MUST);
            combined.Add(userQ,    Occur.MUST);

            var hits = searcher.Search(combined, keys.Count);
            return hits.ScoreDocs
                .Select(h => searcher.Doc(h.Doc).Get("doc_key") ?? "")
                .Where(k => k.Length > 0)
                .ToHashSet();
        }
    }

    /// <summary>Fetch full documents by their doc_keys (used after HNSW vector search).</summary>
    public List<SearchResult> FetchByKeys(IEnumerable<(string key, float vectorScore)> keysWithScores)
    {
        lock (_searchLock)
        {
            using var reader  = _writer.GetReader(applyAllDeletes: true);
            var searcher = new IndexSearcher(reader);
            var results  = new List<SearchResult>();

            foreach (var (key, score) in keysWithScores)
            {
                var hits = searcher.Search(new TermQuery(new Term("doc_key", key)), 1);
                if (hits.ScoreDocs.Length == 0) continue;
                var doc = searcher.Doc(hits.ScoreDocs[0].Doc);
                results.Add(DocToResult(doc, 0, score, 0));
            }
            return results;
        }
    }

    public List<SearchResult> GetRecent(int n = 20)
    {
        lock (_searchLock)
        {
            using var reader  = _writer.GetReader(applyAllDeletes: true);
            var searcher = new IndexSearcher(reader);
            if (reader.NumDocs == 0) return [];

            var allHits = searcher.Search(new MatchAllDocsQuery(), reader.NumDocs);
            return allHits.ScoreDocs
                .TakeLast(n)
                .Select(h => DocToResult(searcher.Doc(h.Doc), 0, 0, 0))
                .Reverse()
                .ToList();
        }
    }

    private static SearchResult DocToResult(Document doc, float bm25, float vec, float hybrid) =>
        new()
        {
            Id           = doc.Get(F_ID)      ?? "",
            FilePath     = doc.Get(F_PATH)    ?? "",
            RelativePath = doc.Get(F_REL)     ?? "",
            ChunkType    = doc.Get(F_TYPE)    ?? "",
            Symbol       = doc.Get(F_SYMBOL)  ?? "",
            Content      = doc.Get(F_CONTENT) ?? "",
            StartLine    = int.TryParse(doc.Get(F_START), out var s) ? s : 0,
            EndLine      = int.TryParse(doc.Get(F_END),   out var e) ? e : 0,
            BM25Score    = bm25,
            VectorScore  = vec,
            HybridScore  = hybrid,
        };

    public void Dispose()
    {
        _writer.Dispose();
        _analyzer.Dispose();
        _directory.Dispose();
    }
}
