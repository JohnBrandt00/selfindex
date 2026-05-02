using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeIndexer.Services;

/// <summary>
/// Persistent HNSW (Hierarchical Navigable Small World) vector index.
///
/// The full HNSW algorithm is implemented here — no external package needed since
/// every available .NET HNSW NuGet package targets older frameworks.
///
/// Disk layout (all files live in Lucene:IndexPath/hnsw/):
///   vectors.bin  — flat float32: [int32 count][int32 dims][float32 * count * dims]
///   graph.bin    — graph adjacency per node per layer
///   meta.json    — string keys list + tombstone set
///
/// Deletion is tombstone-based; compaction happens on next full re-index.
/// </summary>
public sealed class VectorIndexService : IDisposable
{
    // ── HNSW tuning constants ──────────────────────────────────────────────────
    private const int M              = 16;   // max bidirectional connections per layer (1+)
    private const int M0             = 32;   // max connections at layer 0 (= 2*M per paper)
    private const int EfConstruction = 200;  // candidate-list size during index build
    private const int EfSearch       = 100;  // candidate-list size during query
    private readonly double _mL = 1.0 / Math.Log(M); // level generation normaliser

    // ── State ──────────────────────────────────────────────────────────────────
    private readonly List<float[]>           _vectors   = [];
    private readonly List<string>            _keys      = [];
    // _graph[nodeId][layer] = list of neighbor IDs at that layer
    private readonly List<List<List<int>>>   _graph     = [];
    private readonly HashSet<int>            _tombstones = [];
    private int _entryPoint = -1;
    private int _maxLayer   = -1;

    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly Random               _rng    = new(42);

    // ── Paths ──────────────────────────────────────────────────────────────────
    private readonly string _dir;
    private readonly ILogger<VectorIndexService> _logger;

    public int Dimensions   { get; private set; }
    public int LiveEntries  => _vectors.Count - _tombstones.Count;
    public int TotalEntries => _vectors.Count;

    public VectorIndexService(IConfiguration config, ILogger<VectorIndexService> logger)
    {
        _logger = logger;
        var basePath = config["Lucene:IndexPath"]
            ?? Path.Combine(Path.GetTempPath(), "code-indexer-lucene");
        _dir = Path.Combine(basePath, "hnsw");
        Directory.CreateDirectory(_dir);
        TryLoad();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void Upsert(string key, float[] vector)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (Dimensions == 0) Dimensions = vector.Length;

            // Tombstone any prior entries for this key
            for (int i = 0; i < _keys.Count; i++)
                if (_keys[i] == key) _tombstones.Add(i);

            HnswInsert(key, vector);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public void DeleteByFilePath(string filePath)
    {
        _rwLock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _keys.Count; i++)
                if (_keys[i].StartsWith(filePath, StringComparison.OrdinalIgnoreCase))
                    _tombstones.Add(i);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public List<(string key, float score)> Search(float[] query, int topN)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_entryPoint < 0 || _vectors.Count == 0) return [];

            int ep = _entryPoint;

            // Greedy descent through upper layers to find a good layer-0 entry point
            for (int l = _maxLayer; l > 0; l--)
            {
                var w = SearchLayer(query, ep, ef: 1, layer: l);
                ep = w[0].id;
            }

            // Wide search at layer 0
            var results = SearchLayer(query, ep, ef: Math.Max(EfSearch, topN), layer: 0);

            return results
                .Where(r => !_tombstones.Contains(r.id))
                .Take(topN)
                .Select(r => (_keys[r.id], 1f - r.dist))
                .ToList();
        }
        finally { _rwLock.ExitReadLock(); }
    }

    // ── HNSW core ──────────────────────────────────────────────────────────────

    private void HnswInsert(string key, float[] vector)
    {
        int id = _vectors.Count;
        _vectors.Add(vector);
        _keys.Add(key);

        // Assign random level: P(level >= l) = e^(-l / mL) — log-uniform
        int level = (int)(-Math.Log(_rng.NextDouble()) * _mL);
        var nodeLayers = new List<List<int>>(level + 1);
        for (int l = 0; l <= level; l++) nodeLayers.Add([]);
        _graph.Add(nodeLayers);

        if (_entryPoint < 0)
        {
            _entryPoint = id;
            _maxLayer   = level;
            return;
        }

        int ep = _entryPoint;

        // Phase 1: descend from _maxLayer to level+1 — just find a good entry
        for (int l = _maxLayer; l > level; l--)
        {
            var w = SearchLayer(vector, ep, ef: 1, layer: l);
            ep = w[0].id;
        }

        // Phase 2: descend from min(level, _maxLayer) to 0, building connections
        for (int l = Math.Min(level, _maxLayer); l >= 0; l--)
        {
            var w      = SearchLayer(vector, ep, ef: EfConstruction, layer: l);
            int maxConn = l == 0 ? M0 : M;

            // Select the M best neighbours for the new node
            var chosen = w.Take(maxConn).ToList();

            foreach (var (nid, _) in chosen)
            {
                // q → neighbour
                if (!_graph[id][l].Contains(nid))
                    _graph[id][l].Add(nid);

                // neighbour → q  (only if the neighbour exists at layer l)
                if (l < _graph[nid].Count && !_graph[nid][l].Contains(id))
                {
                    _graph[nid][l].Add(id);
                    // Prune neighbour if over capacity
                    if (_graph[nid][l].Count > maxConn)
                        _graph[nid][l] = _graph[nid][l]
                            .OrderBy(x => CosineDistance(_vectors[nid], _vectors[x]))
                            .Take(maxConn)
                            .ToList();
                }
            }

            ep = w[0].id;
        }

        if (level > _maxLayer)
        {
            _entryPoint = id;
            _maxLayer   = level;
        }
    }

    /// <summary>Greedy layer search. Returns up to <paramref name="ef"/> nearest
    /// neighbours sorted closest-first.</summary>
    private List<(int id, float dist)> SearchLayer(float[] query, int entryPoint, int ef, int layer)
    {
        var visited    = new HashSet<int> { entryPoint };
        var candidates = new PriorityQueue<int, float>();          // min-heap by distance
        var results    = new List<(int id, float dist)>();
        float worst    = float.MaxValue;

        float epDist = CosineDistance(query, _vectors[entryPoint]);
        candidates.Enqueue(entryPoint, epDist);
        results.Add((entryPoint, epDist));
        worst = epDist;

        while (candidates.Count > 0)
        {
            candidates.TryDequeue(out int cId, out float cDist);

            // Candidate is further than our worst kept result — no improvement possible
            if (cDist > worst) break;

            if (layer < _graph[cId].Count)
            {
                foreach (int nid in _graph[cId][layer])
                {
                    if (!visited.Add(nid)) continue;

                    float d = CosineDistance(query, _vectors[nid]);
                    if (results.Count < ef || d < worst)
                    {
                        candidates.Enqueue(nid, d);
                        results.Add((nid, d));

                        if (results.Count > ef)
                        {
                            // Evict the furthest entry
                            int wi = 0;
                            for (int i = 1; i < results.Count; i++)
                                if (results[i].dist > results[wi].dist) wi = i;
                            results.RemoveAt(wi);
                        }

                        // Recompute worst
                        worst = 0;
                        foreach (var r in results)
                            if (r.dist > worst) worst = r.dist;
                    }
                }
            }
        }

        results.Sort((a, b) => a.dist.CompareTo(b.dist));
        return results;
    }

    private static float CosineDistance(float[] a, float[] b)
    {
        int   len  = Math.Min(a.Length, b.Length);
        float dot  = 0, magA = 0, magB = 0;
        for (int i = 0; i < len; i++) { dot += a[i]*b[i]; magA += a[i]*a[i]; magB += b[i]*b[i]; }
        float denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom == 0f ? 1f : 1f - dot / denom;
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    public void Save()
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_vectors.Count == 0) return;

            // vectors.bin
            using (var bw = new BinaryWriter(File.Create(Path.Combine(_dir, "vectors.bin"))))
            {
                bw.Write(_vectors.Count);
                bw.Write(Dimensions);
                foreach (var v in _vectors) foreach (var f in v) bw.Write(f);
            }

            // graph.bin  — [nodeCount][entryPoint][maxLayer] then per node:
            //              [layerCount] then per layer: [neighborCount][nid...]
            using (var bw = new BinaryWriter(File.Create(Path.Combine(_dir, "graph.bin"))))
            {
                bw.Write(_graph.Count);
                bw.Write(_entryPoint);
                bw.Write(_maxLayer);
                foreach (var node in _graph)
                {
                    bw.Write(node.Count);
                    foreach (var layer in node)
                    {
                        bw.Write(layer.Count);
                        foreach (var n in layer) bw.Write(n);
                    }
                }
            }

            // meta.json
            var meta = new HnswMeta
            {
                Dimensions  = Dimensions,
                Keys        = _keys,
                Tombstones  = [.. _tombstones]
            };
            File.WriteAllText(Path.Combine(_dir, "meta.json"), JsonSerializer.Serialize(meta));
        }
        finally { _rwLock.ExitReadLock(); }
    }

    private void TryLoad()
    {
        if (!File.Exists(Path.Combine(_dir, "vectors.bin"))) return;
        try
        {
            using (var br = new BinaryReader(File.OpenRead(Path.Combine(_dir, "vectors.bin"))))
            {
                int count = br.ReadInt32();
                Dimensions  = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var v = new float[Dimensions];
                    for (int j = 0; j < Dimensions; j++) v[j] = br.ReadSingle();
                    _vectors.Add(v);
                }
            }

            using (var br = new BinaryReader(File.OpenRead(Path.Combine(_dir, "graph.bin"))))
            {
                int count = br.ReadInt32();
                _entryPoint = br.ReadInt32();
                _maxLayer   = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int lc = br.ReadInt32();
                    var node = new List<List<int>>(lc);
                    for (int l = 0; l < lc; l++)
                    {
                        int nc = br.ReadInt32();
                        var neighbors = new List<int>(nc);
                        for (int n = 0; n < nc; n++) neighbors.Add(br.ReadInt32());
                        node.Add(neighbors);
                    }
                    _graph.Add(node);
                }
            }

            var meta = JsonSerializer.Deserialize<HnswMeta>(
                File.ReadAllText(Path.Combine(_dir, "meta.json")))!;
            _keys.AddRange(meta.Keys);
            foreach (var t in meta.Tombstones) _tombstones.Add(t);

            _logger.LogInformation(
                "HNSW loaded: {Live} live / {Total} total, {Dims}d, maxLayer={L}",
                LiveEntries, TotalEntries, Dimensions, _maxLayer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HNSW load failed — starting fresh");
            _vectors.Clear(); _keys.Clear(); _graph.Clear(); _tombstones.Clear();
            _entryPoint = -1; _maxLayer = -1; Dimensions = 0;
        }
    }

    public void Dispose() => _rwLock.Dispose();

    private sealed class HnswMeta
    {
        [JsonPropertyName("dimensions")]  public int          Dimensions { get; set; }
        [JsonPropertyName("keys")]        public List<string> Keys       { get; set; } = [];
        [JsonPropertyName("tombstones")]  public List<int>    Tombstones { get; set; } = [];
    }
}
